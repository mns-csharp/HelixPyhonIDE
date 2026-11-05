using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HelixTextEditorNET.Core;

namespace HelixTextEditorNET
{
    // Input handling (keyboard, mouse, dwell) + autocompletion and call tips for
    // the managed Scintilla control. WinForms replacement for the Qt widget layer
    // (qt/scintilla_edit.cpp).
    public partial class Scintilla
    {
        // ---- autocompletion state ----
        private AutoCListWindow _acWindow;
        private readonly Dictionary<int, Bitmap> _acImages = new Dictionary<int, Bitmap>();
        private bool _acActive;
        private int _acPosStart;
        private int _acPosEnd;
        private string _acFillups = string.Empty;
        private int _acMaxHeight = 5;
        private int _acMaxWidth;
        private Order _acOrder = Order.Presorted;
        private bool _acIgnoreCase;
        private bool _acChooseSingle;

        /// <summary>
        /// Screen-space bounds of the visible autocomplete list. Companion popups use this to
        /// avoid covering the candidates the user is navigating.
        /// </summary>
        public Rectangle AutoCWindowBounds
        {
            get
            {
                return _acActive && _acWindow != null && _acWindow.Visible
                    ? _acWindow.Bounds
                    : Rectangle.Empty;
            }
        }

        // ---- call tip state ----
        private CallTipWindow _ctWindow;
        private bool _ctActive;

        // ---- dwell state ----
        private Timer _dwellTimer;
        private int _mouseDwellTime = 10000000;  // SC_TIME_FOREVER semantics: disabled
        private bool _dwelling;
        // Deliberately NOT (0,0): that is a real client point — the top-left character of the
        // document — so a dwell raised before the pointer ever entered the control would report a
        // hover over the file's first token.
        private Point _lastMousePos = new Point(int.MinValue, int.MinValue);
        // A dwell is "the pointer rested somewhere". With no pointer in the control there is
        // nowhere it could be resting, so the timer must not run.
        private bool _mouseInside;

        // ---- mouse selection state ----
        private bool _mouseSelecting;

        private void InitDwell()
        {
            _dwellTimer = new Timer();
            _dwellTimer.Tick += OnDwellTimer;
        }

        private void DisposePopups()
        {
            if (_acWindow != null)
            {
                _acWindow.Dispose();
                _acWindow = null;
            }
            if (_ctWindow != null)
            {
                _ctWindow.Dispose();
                _ctWindow = null;
            }
            if (_dwellTimer != null)
            {
                _dwellTimer.Dispose();
                _dwellTimer = null;
            }
            foreach (var kv in _acImages) kv.Value.Dispose();
            _acImages.Clear();
        }

        // =====================================================================
        // Keyboard
        // =====================================================================

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.End:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Tab:
                case Keys.Enter:
                case Keys.Back:
                case Keys.Delete:
                    return true;
                case Keys.Escape:
                    // Only claim Escape as an input key while a popup is actually up — otherwise
                    // WinForms' dialog-key processing may swallow it before OnKeyDown ever runs
                    // (Escape is not in this whitelist by default), so AutoCCancel/CallTipCancel
                    // below never fire even though _acActive/_ctActive are true. When nothing is
                    // showing, let Escape fall through to normal dialog-key handling as before.
                    if (_acActive || _ctActive) return true;
                    break;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;

            // The autocompletion list consumes navigation keys first.
            if (_acActive && (e.Modifiers == Keys.None) && _acWindow != null &&
                _acWindow.HandleKey(e.KeyCode))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (_acActive && e.KeyCode == Keys.Escape)
            {
                AutoCCancel();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (_ctActive && e.KeyCode == Keys.Escape)
            {
                CallTipCancel();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            bool shift = (e.Modifiers & Keys.Shift) != 0;
            bool ctrl = (e.Modifiers & Keys.Control) != 0;

            switch (e.KeyCode)
            {
                case Keys.Left:
                    MoveCaretBy(ctrl ? WordLeftDelta() : -1, shift);
                    e.Handled = true;
                    break;
                case Keys.Right:
                    MoveCaretBy(ctrl ? WordRightDelta() : 1, shift);
                    e.Handled = true;
                    break;
                case Keys.Up:
                    MoveCaretLines(-1, shift);
                    e.Handled = true;
                    break;
                case Keys.Down:
                    MoveCaretLines(1, shift);
                    e.Handled = true;
                    break;
                case Keys.Home:
                    if (ctrl) MoveCaretTo(0, shift);
                    else MoveCaretTo(_engine.Doc.LineStart(CurrentLine), shift);
                    e.Handled = true;
                    break;
                case Keys.End:
                    if (ctrl) MoveCaretTo(_engine.Doc.Length, shift);
                    else MoveCaretTo(_engine.Doc.LineEnd(CurrentLine), shift);
                    e.Handled = true;
                    break;
                case Keys.PageUp:
                    MoveCaretLines(-Math.Max(1, LinesOnScreen - 1), shift);
                    e.Handled = true;
                    break;
                case Keys.PageDown:
                    MoveCaretLines(Math.Max(1, LinesOnScreen - 1), shift);
                    e.Handled = true;
                    break;
                case Keys.Delete:
                    if (!_readOnly)
                    {
                        _engine.DeleteForward();
                        AfterEdit();
                    }
                    e.Handled = true;
                    break;
                case Keys.A:
                    if (ctrl)
                    {
                        SelectAll();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                    break;
                case Keys.C:
                    if (ctrl)
                    {
                        Copy();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                    break;
                case Keys.X:
                    if (ctrl)
                    {
                        Cut();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                    break;
                case Keys.V:
                    if (ctrl)
                    {
                        Paste();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                    break;
                case Keys.Z:
                    if (ctrl)
                    {
                        Undo();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                    break;
                case Keys.Y:
                    if (ctrl)
                    {
                        Redo();
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                    break;
            }
        }

        private int WordLeftDelta()
        {
            int pos = CurrentPosition;
            int target = _engine.Doc.WordStartPosition(pos, false);
            if (target == pos && pos > 0) target = _engine.Doc.WordStartPosition(pos - 1, false);
            return target - pos;
        }

        private int WordRightDelta()
        {
            int pos = CurrentPosition;
            int target = _engine.Doc.WordEndPosition(pos, false);
            if (target == pos && pos < _engine.Doc.Length)
                target = _engine.Doc.WordEndPosition(pos + 1, false);
            return target - pos;
        }

        private void MoveCaretBy(int delta, bool extend)
        {
            MoveCaretTo(CurrentPosition + delta, extend);
        }

        private void MoveCaretTo(int pos, bool extend)
        {
            _engine.MoveCaret(pos, extend);
            _engine.SetBraceHighlight(EditorEngine.InvalidPosition, EditorEngine.InvalidPosition);
            HighlightBracesAtCaret();
            ScrollCaret();
            CaretMoved();
        }

        private void MoveCaretLines(int deltaLines, bool extend)
        {
            int pos = CurrentPosition;
            int line = _engine.Doc.LineFromPosition(pos);
            int col = pos - _engine.Doc.LineStart(line);
            _engine.SyncFoldLines();

            int targetLine;
            if (deltaLines == 1) targetLine = _engine.Cont.NextVisibleLine(line);
            else if (deltaLines == -1) targetLine = _engine.Cont.PrevVisibleLine(line);
            else
            {
                // Multi-line jumps (Page Up/Down) travel by screen distance, where a line's
                // reserved annotation rows legitimately count as vertical space to cross.
                int display = _engine.Cont.DisplayFromDoc(line);
                int targetDisplay = Math.Max(0, display + deltaLines);
                targetLine = _engine.Cont.DocFromDisplay(targetDisplay);
            }
            int target = Math.Min(_engine.Doc.LineStart(targetLine) + col,
                                  _engine.Doc.LineEnd(targetLine));
            MoveCaretTo(target, extend);
        }

        private void HighlightBracesAtCaret()
        {
            int caret = CurrentPosition;
            for (int k = 0; k < 2; k++)
            {
                int bracePos = k == 0 ? caret - 1 : caret;
                if (bracePos < 0 || bracePos >= _engine.Doc.Length) continue;
                char c = _engine.Doc.CharAt(bracePos);
                if (c != '(' && c != ')' && c != '[' && c != ']' && c != '{' && c != '}') continue;
                int match = _engine.Doc.BraceMatch(bracePos);
                if (match != DocumentModel.InvalidPosition) _engine.SetBraceHighlight(bracePos, match);
                else _engine.SetBraceBad(bracePos);
                return;
            }
            _engine.SetBraceHighlight(EditorEngine.InvalidPosition, EditorEngine.InvalidPosition);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled) return;
            char c = e.KeyChar;

            if (c == '\b')
            {
                if (!_readOnly)
                {
                    _engine.DeleteBack();
                    AfterEdit();
                    if (_acActive) RefilterAutoC();
                }
                e.Handled = true;
                return;
            }
            if (c == '\r' || c == '\n')
            {
                if (!_readOnly)
                {
                    string eol = _eolMode == Eol.CrLf ? "\r\n" : (_eolMode == Eol.Cr ? "\r" : "\n");
                    var ic = new InsertCheckEventArgs(CurrentPosition, eol);
                    OnInsertCheck(ic);
                    _engine.ReplaceSelectionWith(ic.Text ?? string.Empty);
                    AfterEdit();
                    ScrollCaret();
                    RaiseCharAdded('\n');
                }
                if (_acActive) AutoCCancel();
                e.Handled = true;
                return;
            }
            if (c == '\t')
            {
                if (!_readOnly)
                {
                    string insert = _useTabs
                        ? "\t"
                        : new string(' ', Math.Max(1, _viewModel.TabWidth));
                    var ic = new InsertCheckEventArgs(CurrentPosition, insert);
                    OnInsertCheck(ic);
                    _engine.ReplaceSelectionWith(ic.Text ?? string.Empty);
                    AfterEdit();
                    ScrollCaret();
                    RaiseCharAdded('\t');
                }
                e.Handled = true;
                return;
            }
            if (c < ' ') return;  // other control chars

            if (_readOnly)
            {
                e.Handled = true;
                return;
            }

            // Fillup characters commit the active completion, then insert.
            if (_acActive && _acFillups.IndexOf(c) >= 0) AutoCComplete();

            var check = new InsertCheckEventArgs(CurrentPosition, c.ToString());
            OnInsertCheck(check);
            _engine.ReplaceSelectionWith(check.Text ?? string.Empty);
            HighlightBracesAtCaret();
            AfterEdit();
            ScrollCaret();
            RaiseCharAdded(c);
            if (_acActive) RefilterAutoC();
            e.Handled = true;
        }

        // =====================================================================
        // Mouse
        // =====================================================================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            Rectangle rc = TextClientRect();
            int margin = _renderer.MarginFromX(_viewModel, rc, e.X);
            if (margin >= 0)
            {
                HandleMarginClick(margin, e);
                return;
            }
            int pos = CharPositionFromPoint(e.X, e.Y);
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            _engine.MoveCaret(pos, shift);
            HighlightBracesAtCaret();
            _mouseSelecting = true;
            Capture = true;
            CaretMoved();
        }

        private void HandleMarginClick(int margin, MouseEventArgs e)
        {
            int pos = CharPositionFromPoint(e.X, e.Y);
            var m = _viewModel.Margins[margin];
            bool foldMargin = (m.Mask & Marker.MaskFolders) != 0 && m.Type == (int)MarginType.Symbol;
            if (foldMargin && (_automaticFold & AutomaticFold.Click) != 0)
            {
                int line = _engine.Doc.LineFromPosition(pos);
                if ((_engine.Doc.GetLevel(line) & Lexing.Folding.FoldLevelHeaderFlag) != 0)
                {
                    _engine.ToggleFold(line);
                    UpdateScrollBars();
                    Invalidate();
                }
                return;
            }
            if (m.Sensitive)
            {
                var handler = MarginClick;
                if (handler != null) handler(this, new MarginClickEventArgs(margin, pos, ModifierKeys));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            // A move proves the pointer is here even if MouseEnter was missed (it is, when the
            // control is created underneath a stationary cursor).
            _mouseInside = true;
            if (_mouseSelecting && e.Button == MouseButtons.Left)
            {
                int pos = CharPositionFromPoint(e.X, e.Y);
                _engine.MoveCaret(pos, true);
                Invalidate();
            }
            // Dwell tracking: reset only on a REAL movement. Windows regenerates
            // WM_MOUSEMOVE with unchanged coordinates whenever the window under the
            // cursor repaints — and this control repaints on a timer (caret blink), so
            // a phantom "move" fired every blink, ending the dwell and closing hover
            // tooltips half a second after they appeared.
            if (e.Location == _lastMousePos) return;
            if (_dwelling)
            {
                _dwelling = false;
                var handler = DwellEnd;
                if (handler != null)
                    handler(this, new DwellEventArgs(CharPositionFromPoint(e.X, e.Y), e.X, e.Y));
            }
            _lastMousePos = e.Location;
            RestartDwellTimer();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_mouseSelecting)
            {
                _mouseSelecting = false;
                Capture = false;
                RaiseUpdateUI(UpdateChange.Selection);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _mouseInside = true;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _mouseInside = false;
            if (_dwellTimer != null) _dwellTimer.Stop();
            if (_dwelling)
            {
                _dwelling = false;
                var handler = DwellEnd;
                if (handler != null) handler(this, new DwellEventArgs(InvalidPosition, 0, 0));
            }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            // Select the word under the caret.
            int pos = CurrentPosition;
            int ws = _engine.Doc.WordStartPosition(pos, true);
            int we = _engine.Doc.WordEndPosition(pos, true);
            if (we > ws)
            {
                _engine.Sel.SetSelection(new Core.SelectionRange(we, ws));
                CaretMoved();
            }
        }

        // ---- dwell ----

        public int MouseDwellTime
        {
            get { return _mouseDwellTime; }
            set
            {
                _mouseDwellTime = value;
                RestartDwellTimer();
            }
        }

        private void RestartDwellTimer()
        {
            if (_dwellTimer == null) return;
            _dwellTimer.Stop();
            // Setting MouseDwellTime (the IDE does it once while building each editor) must only
            // configure the dwell, never arm it: with no pointer in the control the tick would fire
            // for the default mouse position and pop a hover doc for the file's first token, with
            // no mouse movement left to dismiss it.
            if (!_mouseInside) return;
            if (_mouseDwellTime > 0 && _mouseDwellTime < 10000000)
            {
                _dwellTimer.Interval = Math.Max(1, _mouseDwellTime);
                _dwellTimer.Start();
            }
        }

        private void OnDwellTimer(object sender, EventArgs e)
        {
            _dwellTimer.Stop();
            if (_dwelling || !_mouseInside) return;
            _dwelling = true;
            var handler = DwellStart;
            if (handler != null)
            {
                int pos = CharPositionFromPoint(_lastMousePos.X, _lastMousePos.Y);
                handler(this, new DwellEventArgs(pos, _lastMousePos.X, _lastMousePos.Y));
            }
        }

        // =====================================================================
        // Autocompletion
        // =====================================================================

        public bool AutoCActive
        {
            get { return _acActive; }
        }

        public int AutoCMaxHeight
        {
            get { return _acMaxHeight; }
            set { _acMaxHeight = Math.Max(1, value); }
        }

        public int AutoCMaxWidth
        {
            get { return _acMaxWidth; }
            set { _acMaxWidth = Math.Max(0, value); }
        }

        public Order AutoCOrder
        {
            get { return _acOrder; }
            set { _acOrder = value; }
        }

        public bool AutoCIgnoreCase
        {
            get { return _acIgnoreCase; }
            set { _acIgnoreCase = value; }
        }

        public bool AutoCChooseSingle
        {
            get { return _acChooseSingle; }
            set { _acChooseSingle = value; }
        }

        public void AutoCSetFillUps(string chars)
        {
            _acFillups = chars ?? string.Empty;
        }

        public void RegisterRgbaImage(int type, Bitmap image)
        {
            Bitmap old;
            if (_acImages.TryGetValue(type, out old) && old != null) old.Dispose();
            _acImages[type] = image == null ? null : new Bitmap(image);
        }

        public void ClearRegisteredImages()
        {
            foreach (var kv in _acImages)
                if (kv.Value != null) kv.Value.Dispose();
            _acImages.Clear();
        }

        public void AutoCShow(int lenEntered, string list)
        {
            if (string.IsNullOrEmpty(list))
            {
                AutoCCancel();
                return;
            }
            if (_acWindow == null)
            {
                _acWindow = new AutoCListWindow(_acImages);
                _acWindow.CommitRequested += delegate { AutoCComplete(); };
                // Relay live navigation (arrow keys, etc.), not just commit, so a listener
                // (CompletionController's side doc panel) can track the highlight in real time —
                // AutoCComplete() below still separately raises this on commit, for callers that
                // want the final choice, so this doesn't change that contract, only adds coverage
                // for the in-between navigation the popup itself already visibly shows.
                _acWindow.SelectionChanged += delegate
                {
                    if (!_acActive) return;
                    string sel = _acWindow.SelectedItemText;
                    if (sel == null) return;
                    var selHandler = AutoCSelection;
                    if (selHandler != null) selHandler(this, new AutoCSelectionEventArgs(_acPosStart, sel));
                };
            }
            _acPosStart = CurrentPosition - Math.Max(0, lenEntered);
            _acPosEnd = CurrentPosition;
            _acWindow.SetItems(list, ' ', '?', _acOrder == Order.PerformSort);
            string prefix = GetTextRange(_acPosStart, CurrentPosition - _acPosStart);
            if (!_acWindow.Filter(prefix, _acIgnoreCase))
            {
                AutoCCancel();
                return;
            }
            if (_acChooseSingle && _acWindow.ItemCount == 1)
            {
                _acActive = true;
                AutoCComplete();
                return;
            }
            Font font = _surface.GetFont(_viewModel.StyleAt(Rendering.ViewStyleModel.StyleDefaultIndex));
            _acWindow.SizeToContent(_acMaxHeight, _acMaxWidth, font);
            PositionAutoCWindow();
            Form owner = FindForm();
            if (owner != null && _acWindow.Owner != owner) _acWindow.Owner = owner;
            _acWindow.Show();
            _acActive = true;
        }

        private void PositionAutoCWindow()
        {
            if (_acWindow == null) return;
            int x = PointXFromPosition(_acPosStart);
            int y = PointYFromPosition(_acPosStart) + LineHeightPixels;
            Point screen = PointToScreen(new Point(x, y));
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (screen.Y + _acWindow.Height > wa.Bottom)
                screen.Y = PointToScreen(new Point(x, PointYFromPosition(_acPosStart))).Y - _acWindow.Height;
            if (screen.X + _acWindow.Width > wa.Right)
                screen.X = wa.Right - _acWindow.Width;
            _acWindow.Location = screen;
        }

        private void RefilterAutoC()
        {
            if (!_acActive || _acWindow == null) return;
            int caret = CurrentPosition;
            if (caret < _acPosStart)
            {
                AutoCCancel();
                return;
            }
            string prefix = GetTextRange(_acPosStart, caret - _acPosStart);
            if (!_acWindow.Filter(prefix, _acIgnoreCase))
            {
                AutoCCancel();
                return;
            }
            _acPosEnd = caret;
            Font font = _surface.GetFont(_viewModel.StyleAt(Rendering.ViewStyleModel.StyleDefaultIndex));
            _acWindow.SizeToContent(_acMaxHeight, _acMaxWidth, font);
            PositionAutoCWindow();
        }

        public void AutoCComplete()
        {
            if (!_acActive || _acWindow == null) return;
            string chosen = _acWindow.SelectedItemText;
            HideAutoCWindow();
            if (chosen == null)
            {
                RaiseAutoCCancelled();
                return;
            }
            var selHandler = AutoCSelection;
            if (selHandler != null) selHandler(this, new AutoCSelectionEventArgs(_acPosStart, chosen));
            // Default behavior: replace the typed prefix with the chosen item.
            if (!_readOnly)
            {
                int caret = CurrentPosition;
                _engine.Doc.BeginUndoAction();
                if (caret > _acPosStart) _engine.Doc.DeleteChars(_acPosStart, caret - _acPosStart);
                _engine.Doc.InsertString(_acPosStart, chosen);
                _engine.Doc.EndUndoAction();
                _engine.SetEmptySelection(_acPosStart + chosen.Length);
                AfterEdit();
            }
            var doneHandler = AutoCCompleted;
            if (doneHandler != null) doneHandler(this, EventArgs.Empty);
        }

        public void AutoCCancel()
        {
            if (!_acActive) return;
            HideAutoCWindow();
            RaiseAutoCCancelled();
        }

        private void HideAutoCWindow()
        {
            _acActive = false;
            if (_acWindow != null && _acWindow.Visible) _acWindow.Hide();
        }

        private void RaiseAutoCCancelled()
        {
            var handler = AutoCCancelled;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // =====================================================================
        // Call tips
        // =====================================================================

        public bool CallTipActive
        {
            get { return _ctActive; }
        }

        public void CallTipShow(int posStart, string definition)
        {
            if (string.IsNullOrEmpty(definition))
            {
                CallTipCancel();
                return;
            }
            if (_ctWindow == null) _ctWindow = new CallTipWindow();
            Font font = _surface.GetFont(_viewModel.StyleAt(Rendering.ViewStyleModel.StyleDefaultIndex));
            _ctWindow.SetTip(definition, font);
            int x = PointXFromPosition(posStart);
            int y = PointYFromPosition(posStart) + LineHeightPixels;
            Point screen = PointToScreen(new Point(x, y));
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (screen.Y + _ctWindow.Height > wa.Bottom)
                screen.Y = PointToScreen(new Point(x, PointYFromPosition(posStart))).Y - _ctWindow.Height;
            _ctWindow.Location = screen;
            Form ctOwner = FindForm();
            if (ctOwner != null && _ctWindow.Owner != ctOwner) _ctWindow.Owner = ctOwner;
            _ctWindow.Show();
            _ctActive = true;
        }

        public void CallTipCancel()
        {
            _ctActive = false;
            if (_ctWindow != null && _ctWindow.Visible) _ctWindow.Hide();
        }

        public void CallTipSetHlt(int hlStart, int hlEnd)
        {
            if (_ctWindow != null) _ctWindow.SetHighlight(hlStart, hlEnd);
        }

        public void CallTipSetForeHlt(Color color)
        {
            if (_ctWindow == null) _ctWindow = new CallTipWindow();
            _ctWindow.SetHighlightColor(color);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            StopCaretBlink();   // no ticking (or repainting) while the caret isn't drawn anyway
            Invalidate();
        }

        // NOTE: deliberately NOT OnLostFocus. Showing AutoC/CallTip (real, if
        // WS_EX_NOACTIVATE-styled, top-level Forms) transiently blurs this control at the
        // WinForms bookkeeping level even though nothing the user did actually moved
        // focus — an OnLostFocus-driven cancel would self-destruct the popup the instant
        // it appears. OnLeave (ContainerControl's "ActiveControl changed to a sibling"
        // notification) does NOT fire for that same blip, only for a genuine focus move
        // to another control — e.g. switching editor tabs, clicking a different panel —
        // which is exactly when a dangling AutoC/CallTip popup becomes unreachable by any
        // further Escape (the keypress now goes to whatever control actually has focus,
        // never back to this one), so it must be dismissed proactively here instead.
        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            if (_acActive) AutoCCancel();
            if (_ctActive) CallTipCancel();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            StartCaretBlink();
            Invalidate();
        }
    }
}
