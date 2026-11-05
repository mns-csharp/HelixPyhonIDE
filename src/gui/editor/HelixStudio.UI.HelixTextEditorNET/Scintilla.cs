using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using HelixTextEditorNET.Core;
using HelixTextEditorNET.Rendering;

namespace HelixTextEditorNET
{
    // The Scintilla editor control: a WinForms port of the HelixStudioCpp
    // mini-Scintilla (editor engine + view renderer) exposing the IDE's editor
    // API. Its familiar control members follow ScintillaNET 3.6.3 where useful.
    // This implementation is fully managed and has its own HelixTextEditorNET identity.
    public partial class Scintilla : Control
    {
        public static readonly int InvalidPosition = -1;

        private readonly EditorEngine _engine = new EditorEngine();
        private readonly ViewStyleModel _viewModel = new ViewStyleModel();
        private readonly GdiSurface _surface = new GdiSurface();
        private readonly EditViewRenderer _renderer;

        private readonly LineCollection _lines;
        private readonly StyleCollection _styles;
        private readonly MarginCollection _margins;
        private readonly MarkerCollection _markers;
        private readonly IndicatorCollection _indicators;

        private Lexing.LexerEngine _lexerEngine = new Lexing.NullLexerEngine();
        private Lexer _lexer = Lexer.Container;

        private readonly VScrollBar _vScroll = new VScrollBar();
        private readonly HScrollBar _hScroll = new HScrollBar();

        private int _firstVisibleLine;   // display-line index of the top row
        private double _xOffset;         // horizontal scroll in pixels
        private bool _readOnly;
        private Eol _eolMode = Eol.CrLf;
        private bool _useTabs = true;
        private int _scrollWidth = 2000;
        private bool _scrollWidthTracking;
        private AutomaticFold _automaticFold = AutomaticFold.None;
        private bool _multipleSelection;
        private bool _additionalSelectionTyping;
        private bool _stylingValid;      // false -> full re-lex before next read/paint

        public Scintilla()
        {
            _renderer = new EditViewRenderer(_surface);
            _lines = new LineCollection(this);
            _styles = new StyleCollection(this);
            _margins = new MarginCollection(this);
            _markers = new MarkerCollection(this);
            _indicators = new IndicatorCollection(this);

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Cursor = Cursors.IBeam;
            TabStop = true;

            // Default like the original control: a monospace 10pt base style.
            StyleSpec def = _viewModel.Styles[Rendering.ViewStyleModel.StyleDefaultIndex];
            def.FontName = "Consolas";
            def.SizeF = 10f;
            _viewModel.StyleClearAll();

            _vScroll.Dock = DockStyle.None;
            _vScroll.Visible = true;
            _vScroll.Scroll += OnVScroll;
            _hScroll.Dock = DockStyle.None;
            _hScroll.Visible = true;
            _hScroll.Scroll += OnHScroll;
            Controls.Add(_vScroll);
            Controls.Add(_hScroll);

            _engine.Doc.TextModified += OnDocTextModified;

            InitDwell();
        }

        // ---- internal plumbing shared with the API objects ----

        internal DocumentModel Document
        {
            get { return _engine.Doc; }
        }

        internal ViewStyleModel ViewModel
        {
            get { return _viewModel; }
        }

        internal ContractionState Contraction
        {
            get { return _engine.Cont; }
        }

        internal EditorEngine Engine
        {
            get { return _engine; }
        }

        internal int LineHeightPixels
        {
            get { return (int)Math.Ceiling(_renderer.LineHeight(_viewModel)); }
        }

        internal void InvalidateView()
        {
            Invalidate();
        }

        internal void StylesChanged()
        {
            _surface.Reset();
            Invalidate();
        }

        internal void SyncFoldLines()
        {
            _engine.SyncFoldLines();
        }

        internal void RecalcFoldVisibility()
        {
            _engine.RecalcFoldVisibility();
            UpdateScrollBars();
            Invalidate();
        }

        internal int GetLastChild(int line, int level)
        {
            return _engine.GetLastChild(line, level);
        }

        internal int GetFoldParent(int line)
        {
            return _engine.GetFoldParent(line);
        }

        internal void EnsureLineVisible(int line)
        {
            _engine.EnsureLineVisible(line);
            UpdateScrollBars();
            Invalidate();
        }

        internal void ToggleFoldLine(int line)
        {
            _engine.ToggleFold(line);
            UpdateScrollBars();
            Invalidate();
        }

        // ---- collections ----

        public LineCollection Lines
        {
            get { return _lines; }
        }

        public StyleCollection Styles
        {
            get { return _styles; }
        }

        public MarginCollection Margins
        {
            get { return _margins; }
        }

        public MarkerCollection Markers
        {
            get { return _markers; }
        }

        public IndicatorCollection Indicators
        {
            get { return _indicators; }
        }

        // ---- events ----

        public event EventHandler<InsertCheckEventArgs> InsertCheck;

        public event EventHandler<CharAddedEventArgs> CharAdded;

        public event EventHandler<UpdateUIEventArgs> UpdateUI;

        public event EventHandler<MarginClickEventArgs> MarginClick;

        public event EventHandler<DwellEventArgs> DwellStart;

        public event EventHandler<DwellEventArgs> DwellEnd;

        public event EventHandler<AutoCSelectionEventArgs> AutoCSelection;

        public event EventHandler AutoCCompleted;

        public event EventHandler AutoCCancelled;

        // ---- text ----

        public override string Text
        {
            get { return _engine.Doc.GetText(0, _engine.Doc.Length); }
            set
            {
                string v = value ?? string.Empty;
                _engine.Doc.SetText(v);
                _engine.SetEmptySelection(0);
                _firstVisibleLine = 0;
                _xOffset = 0;
                RestyleAll();
                UpdateScrollBars();
                Invalidate();
            }
        }

        public int TextLength
        {
            get { return _engine.Doc.Length; }
        }

        public string GetTextRange(int position, int length)
        {
            int docLen = _engine.Doc.Length;
            if (position < 0) position = 0;
            if (position > docLen) position = docLen;
            if (length < 0) length = 0;
            if (position + length > docLen) length = docLen - position;
            return _engine.Doc.GetText(position, length);
        }

        public int GetCharAt(int position)
        {
            if (position < 0 || position >= _engine.Doc.Length) return 0;
            return _engine.Doc.CharAt(position);
        }

        public int GetStyleAt(int position)
        {
            EnsureStyled();
            return _engine.Doc.StyleAt(position);
        }

        public void InsertText(int position, string text)
        {
            if (_readOnly || string.IsNullOrEmpty(text)) return;
            if (position < 0) position = CurrentPosition;
            _engine.Doc.InsertString(position, text);
        }

        public void DeleteRange(int position, int length)
        {
            if (_readOnly) return;
            _engine.Doc.DeleteChars(position, length);
        }

        public void ReplaceSelection(string text)
        {
            if (_readOnly) return;
            _engine.ReplaceSelectionWith(text ?? string.Empty);
            AfterEdit();
        }

        public void ClearAll()
        {
            if (_readOnly) return;
            _engine.Doc.SetText(string.Empty);
            _engine.SetEmptySelection(0);
        }

        // ---- caret / selection ----

        public int CurrentPosition
        {
            get { return _engine.MainCaret; }
            set
            {
                _engine.MoveCaret(value, false);
                CaretMoved();
            }
        }

        public int CurrentLine
        {
            get { return _engine.Doc.LineFromPosition(_engine.MainCaret); }
        }

        public int SelectionStart
        {
            get { return _engine.Sel.Main.Start; }
            set
            {
                Core.SelectionRange r = _engine.Sel.Main;
                int caret = Math.Max(r.Caret.Position, value);
                _engine.Sel.SetSelection(new Core.SelectionRange(caret, value));
                CaretMoved();
            }
        }

        public int SelectionEnd
        {
            get { return _engine.Sel.Main.End; }
            set
            {
                Core.SelectionRange r = _engine.Sel.Main;
                int anchor = Math.Min(r.Anchor.Position, value);
                _engine.Sel.SetSelection(new Core.SelectionRange(value, anchor));
                CaretMoved();
            }
        }

        public string SelectedText
        {
            get
            {
                Core.SelectionRange r = _engine.Sel.Main;
                return _engine.Doc.GetText(r.Start, r.End - r.Start);
            }
        }

        public void SetSelection(int caret, int anchor)
        {
            _engine.Sel.SetSelection(new Core.SelectionRange(caret, anchor));
            CaretMoved();
        }

        public void SetEmptySelection(int position)
        {
            _engine.SetEmptySelection(position);
            CaretMoved();
        }

        public void SelectAll()
        {
            _engine.SelectAll();
            CaretMoved();
        }

        public void GotoPosition(int position)
        {
            _engine.SetEmptySelection(Math.Max(0, Math.Min(position, _engine.Doc.Length)));
            ScrollCaret();
            CaretMoved();
        }

        // ---- position / line mapping ----

        public int LineFromPosition(int position)
        {
            return _engine.Doc.LineFromPosition(position);
        }

        // Column of a position, expanding tabs to the next tab stop.
        public int GetColumn(int position)
        {
            int line = _engine.Doc.LineFromPosition(position);
            int start = _engine.Doc.LineStart(line);
            int tabW = Math.Max(1, _viewModel.TabWidth);
            int col = 0;
            for (int p = start; p < position && p < _engine.Doc.Length; p++)
            {
                if (_engine.Doc.CharAt(p) == '\t') col += tabW - (col % tabW);
                else col++;
            }
            return col;
        }

        public int WordStartPosition(int position, bool onlyWordCharacters)
        {
            return _engine.Doc.WordStartPosition(position, onlyWordCharacters);
        }

        public int WordEndPosition(int position, bool onlyWordCharacters)
        {
            return _engine.Doc.WordEndPosition(position, onlyWordCharacters);
        }

        public int CharPositionFromPoint(int x, int y)
        {
            EnsureStyled();
            return _renderer.PositionFromPoint(_engine.Doc, _viewModel, MakeRenderState(),
                                               TextClientRect(), new Point(x, y));
        }

        public int PointXFromPosition(int pos)
        {
            EnsureStyled();
            return (int)Math.Round(_renderer.XFromPosition(_engine.Doc, _viewModel, MakeRenderState(), pos));
        }

        public int PointYFromPosition(int pos)
        {
            int displayLine = _wrapMode != WrapMode.None
                ? _renderer.DisplayRowOfPosition(_engine.Doc, _viewModel, _engine.Cont, pos)
                : _engine.Cont.DisplayFromDoc(_engine.Doc.LineFromPosition(pos));
            return (displayLine - _firstVisibleLine) * LineHeightPixels;
        }

        // ---- annotations: extra blank display rows reserved right after a line ----
        //
        // Real Scintilla-style line annotations: reserving N rows after `line` shifts
        // every subsequent line, the scrollbar range, and hit-testing down by N rows —
        // callers that need genuinely separate visual rows (not just an inline paint
        // trick) register them here and draw into the reserved space themselves via
        // PointYFromAnnotationRow (e.g. BlockScopeRenderer's Allman-style virtual braces).
        // Scintilla itself never paints annotation content — it only reserves the space.

        public int AnnotationGetLines(int line)
        {
            return _engine.Cont.AnnotationGetLines(line);
        }

        public void AnnotationSetLines(int line, int numLines)
        {
            _engine.Cont.AnnotationSetLines(line, numLines);
            UpdateScrollBars();
            Invalidate();
        }

        public void AnnotationClearAll()
        {
            _engine.Cont.AnnotationClearAll();
            UpdateScrollBars();
            Invalidate();
        }

        // Y (client pixels) of the top of the `rowIndex`-th (0-based) annotation row
        // reserved after `line` — i.e. below the line's own row(s), plus `rowIndex` more when
        // several things share that line's annotation rows.
        //
        // "The line's own row(s)" is one row normally but SEVERAL when the line is word-wrapped,
        // and the rows of every line above are likewise wrapped — get either wrong and the virtual
        // braces / type markers drawn into these rows land against unrelated code.
        public int PointYFromAnnotationRow(int line, int rowIndex)
        {
            int lineTopDisplayRow, ownRows;
            if (_wrapMode != WrapMode.None)
            {
                lineTopDisplayRow = _renderer.DisplayRowOfLine(_engine.Doc, _viewModel, _engine.Cont, line);
                // null contraction state on purpose: we want the line's TEXT rows only. Its own
                // annotation rows are what `rowIndex` indexes into, so counting them here would
                // push the first one past the block it belongs to.
                ownRows = _renderer.RowsOccupiedBy(_engine.Doc, _viewModel, null, line);
            }
            else
            {
                lineTopDisplayRow = _engine.Cont.DisplayFromDoc(line);
                ownRows = 1;
            }
            int displayRow = lineTopDisplayRow + ownRows + Math.Max(0, rowIndex);
            return (displayRow - _firstVisibleLine) * LineHeightPixels;
        }

        // The document line shown at a display row. Overlay renderers cull to the viewport with
        // this, so it has to count wrapped rows too — otherwise, scrolled into a wrapped file, the
        // culled range drifts and whole blocks lose their virtual braces.
        public int DocLineFromVisible(int displayLine)
        {
            return _wrapMode != WrapMode.None
                ? _renderer.DocLineOfDisplayRow(_engine.Doc, _viewModel, _engine.Cont, displayLine)
                : _engine.Cont.DocFromDisplay(displayLine);
        }

        public int FirstVisibleLine
        {
            get { return _firstVisibleLine; }
            set
            {
                _engine.SyncFoldLines();
                int max = Math.Max(0, _engine.Cont.LinesDisplayed - 1);
                _firstVisibleLine = Math.Max(0, Math.Min(value, max));
                UpdateScrollBars();
                Invalidate();
            }
        }

        public int LinesOnScreen
        {
            get
            {
                int lh = LineHeightPixels;
                return Math.Max(1, TextClientRect().Height / Math.Max(1, lh));
            }
        }

        public void LineScroll(int lines, int columns)
        {
            FirstVisibleLine = _firstVisibleLine + lines;
            if (columns != 0)
            {
                double spaceWidth = 8;
                _xOffset = Math.Max(0, _xOffset + columns * spaceWidth);
                Invalidate();
            }
        }

        public void ScrollCaret()
        {
            int caretLine = _engine.Doc.LineFromPosition(_engine.MainCaret);
            _engine.SyncFoldLines();
            // FirstVisibleLine counts display ROWS, so the caret's row must be measured the same
            // way — a wrapped line above the caret occupies more than one of them.
            int display = _wrapMode != WrapMode.None
                ? _renderer.DisplayRowOfPosition(_engine.Doc, _viewModel, _engine.Cont, _engine.MainCaret)
                : _engine.Cont.DisplayFromDoc(caretLine);
            int onScreen = LinesOnScreen;
            if (display < _firstVisibleLine)
                FirstVisibleLine = display;
            else if (display >= _firstVisibleLine + onScreen)
                FirstVisibleLine = display - onScreen + 1;
            // Keep the caret horizontally visible too.
            int caretX = PointXFromPosition(_engine.MainCaret);
            Rectangle rc = TextClientRect();
            double marginW = _viewModel.MarginsWidth();
            if (caretX < marginW + 4)
            {
                _xOffset = Math.Max(0, _xOffset - (marginW + 40 - caretX));
                Invalidate();
            }
            else if (caretX > rc.Right - 8)
            {
                _xOffset += caretX - (rc.Right - 40);
                Invalidate();
            }
            UpdateScrollBars();
        }

        public int TextWidth(int style, string text)
        {
            return (int)Math.Ceiling(_surface.WidthText(_viewModel.StyleAt(style), text ?? string.Empty));
        }

        // ---- undo / redo ----

        public bool CanUndo
        {
            get { return _engine.Doc.CanUndo; }
        }

        public bool CanRedo
        {
            get { return _engine.Doc.CanRedo; }
        }

        public void Undo()
        {
            if (_readOnly) return;
            _engine.Undo();
            AfterEdit();
        }

        public void Redo()
        {
            if (_readOnly) return;
            _engine.Redo();
            AfterEdit();
        }

        public void BeginUndoAction()
        {
            _engine.Doc.BeginUndoAction();
        }

        public void EndUndoAction()
        {
            _engine.Doc.EndUndoAction();
        }

        public void EmptyUndoBuffer()
        {
            _engine.Doc.DeleteUndoHistory();
        }

        // ---- clipboard ----

        public bool CanPaste
        {
            get { return !_readOnly && Clipboard.ContainsText(); }
        }

        public void Cut()
        {
            if (_readOnly) return;
            string sel = SelectedText;
            if (sel.Length == 0) return;
            Clipboard.SetText(sel);
            _engine.ClearSelection();
            AfterEdit();
        }

        public void Copy()
        {
            string sel = SelectedText;
            if (sel.Length > 0) Clipboard.SetText(sel);
        }

        public void Paste()
        {
            if (_readOnly || !Clipboard.ContainsText()) return;
            string text = Clipboard.GetText().Replace("\r\n", "\n").Replace("\r", "\n");
            if (_eolMode == Eol.CrLf) text = text.Replace("\n", "\r\n");
            var ic = new InsertCheckEventArgs(CurrentPosition, text);
            OnInsertCheck(ic);
            _engine.ReplaceSelectionWith(ic.Text ?? string.Empty);
            AfterEdit();
            ScrollCaret();
        }

        // ---- search / target ----

        public SearchFlags SearchFlags
        {
            get { return (SearchFlags)_engine.SearchFlags; }
            set { _engine.SearchFlags = (int)value; }
        }

        public int TargetStart
        {
            get { return _engine.TargetStart; }
            set { _engine.TargetStart = value; }
        }

        public int TargetEnd
        {
            get { return _engine.TargetEnd; }
            set { _engine.TargetEnd = value; }
        }

        public void TargetWholeDocument()
        {
            _engine.TargetStart = 0;
            _engine.TargetEnd = _engine.Doc.Length;
        }

        public int SearchInTarget(string text)
        {
            return _engine.SearchInTarget(text);
        }

        public int ReplaceTarget(string text)
        {
            if (_readOnly) return 0;
            int result = _engine.ReplaceTarget(text);
            AfterEdit();
            return result;
        }

        // ---- lexing / styling ----

        public Lexer Lexer
        {
            get { return _lexer; }
            set
            {
                _lexer = value;
                _lexerEngine = Lexing.LexerEngine.Create((int)value);
                RestyleAll();
                Invalidate();
            }
        }

        public void SetKeywords(int set, string keywords)
        {
            _lexerEngine.SetKeywords(set, keywords);
            RestyleAll();
            Invalidate();
        }

        public void SetProperty(string name, string value)
        {
            _lexerEngine.SetProperty(name, value);
            RestyleAll();
        }

        public void Colorize(int startPos, int endPos)
        {
            RestyleAll();
            Invalidate();
        }

        public void StartStyling(int position)
        {
            _engine.Doc.StartStyling(position);
        }

        public void SetStyling(int length, int style)
        {
            _engine.Doc.SetStyles(length, (byte)style);
        }

        public void StyleClearAll()
        {
            _viewModel.StyleClearAll();
            StylesChanged();
        }

        public void StyleResetDefault()
        {
            _viewModel.StyleResetDefault();
            StylesChanged();
        }

        public void SetSelectionBackColor(bool use, Color color)
        {
            _viewModel.SelectionBackSet = use;
            if (use) _viewModel.SelectionBack = color;
            Invalidate();
        }

        // ---- folding ----

        public AutomaticFold AutomaticFold
        {
            get { return _automaticFold; }
            set { _automaticFold = value; }
        }

        public void FoldAll(FoldAction action)
        {
            EnsureStyled();
            _engine.FoldAll(action);
            UpdateScrollBars();
            Invalidate();
        }

        // ---- brace matching ----

        public int BraceMatch(int position)
        {
            EnsureStyled();
            return _engine.Doc.BraceMatch(position);
        }

        public void BraceHighlight(int position1, int position2)
        {
            _engine.SetBraceHighlight(position1, position2);
            Invalidate();
        }

        public void BraceBadLight(int position)
        {
            _engine.SetBraceBad(position);
            Invalidate();
        }

        // ---- indicators ----

        public int IndicatorCurrent
        {
            get { return _engine.IndicatorCurrent; }
            set { _engine.IndicatorCurrent = value; }
        }

        public int IndicatorValue
        {
            get { return _engine.IndicatorValue; }
            set { _engine.IndicatorValue = value; }
        }

        public void IndicatorFillRange(int position, int length)
        {
            _engine.Doc.IndicatorFillRange(_engine.IndicatorCurrent, position, length,
                                           _engine.IndicatorValue);
            Invalidate();
        }

        public void IndicatorClearRange(int position, int length)
        {
            _engine.Doc.IndicatorClearRange(_engine.IndicatorCurrent, position, length);
            Invalidate();
        }

        // ---- EOL ----

        public Eol EolMode
        {
            get { return _eolMode; }
            set { _eolMode = value; }
        }

        public void ConvertEols(Eol eolMode)
        {
            if (_readOnly) return;
            string text = Text;
            string unified = text.Replace("\r\n", "\n").Replace("\r", "\n");
            string converted;
            switch (eolMode)
            {
                case Eol.CrLf: converted = unified.Replace("\n", "\r\n"); break;
                case Eol.Cr: converted = unified.Replace('\n', '\r'); break;
                default: converted = unified; break;
            }
            if (converted == text) return;
            int caret = CurrentPosition;
            _engine.Doc.BeginUndoAction();
            _engine.Doc.SetText(converted);
            _engine.Doc.EndUndoAction();
            _engine.SetEmptySelection(Math.Min(caret, _engine.Doc.Length));
            AfterEdit();
        }

        // ---- misc appearance / behavior properties ----

        public bool ReadOnly
        {
            get { return _readOnly; }
            set { _readOnly = value; }
        }

        /// <summary>
        /// Word wrap. This used to be an API-compatibility stub that nothing read; it now drives
        /// the renderer's wrap layout, so setting it actually wraps text, hit-testing and the
        /// caret together.
        /// </summary>
        public WrapMode WrapMode
        {
            get { return _wrapMode; }
            set
            {
                if (_wrapMode == value) return;
                _wrapMode = value;
                _renderer.WordWrap = value != WrapMode.None;
                // Wrapped text never extends past the right edge, so the horizontal scrollbar
                // would be dead UI -- and leaving it visible is what makes wrap look broken.
                _xOffset = 0;
                UpdateScrollBars();
                Invalidate();
            }
        }
        private WrapMode _wrapMode = WrapMode.None;

        public IndentView IndentationGuides
        {
            get { return (IndentView)_viewModel.IndentationGuides; }
            set
            {
                _viewModel.IndentationGuides = (int)value;
                Invalidate();
            }
        }

        public int TabWidth
        {
            get { return _viewModel.TabWidth; }
            set
            {
                _viewModel.TabWidth = Math.Max(1, value);
                _viewModel.IndentWidth = _viewModel.TabWidth;
                Invalidate();
            }
        }

        public bool UseTabs
        {
            get { return _useTabs; }
            set { _useTabs = value; }
        }

        public EdgeMode EdgeMode
        {
            get { return (EdgeMode)_viewModel.EdgeMode; }
            set
            {
                _viewModel.EdgeMode = (int)value;
                Invalidate();
            }
        }

        public int EdgeColumn
        {
            get { return _viewModel.EdgeColumn; }
            set
            {
                _viewModel.EdgeColumn = value;
                Invalidate();
            }
        }

        public Color EdgeColor
        {
            get { return _viewModel.EdgeColour; }
            set
            {
                _viewModel.EdgeColour = value;
                Invalidate();
            }
        }

        public bool CaretLineVisible
        {
            get { return _viewModel.CaretLineVisible; }
            set
            {
                _viewModel.CaretLineVisible = value;
                Invalidate();
            }
        }

        public Color CaretLineBackColor
        {
            get { return _viewModel.CurrentLineBack; }
            set
            {
                _viewModel.CurrentLineBack = value;
                Invalidate();
            }
        }

        public Color CaretForeColor
        {
            get { return _viewModel.CaretColour; }
            set
            {
                _viewModel.CaretColour = value;
                Invalidate();
            }
        }

        /// <summary>Caret width in pixels (SCI_SETCARETWIDTH parity; clamped to 1..20).</summary>
        public int CaretWidth
        {
            get { return (int)_viewModel.CaretWidth; }
            set
            {
                _viewModel.CaretWidth = Math.Max(1, Math.Min(20, value));
                Invalidate();
            }
        }

        public bool MultipleSelection
        {
            get { return _multipleSelection; }
            set { _multipleSelection = value; }
        }

        public bool AdditionalSelectionTyping
        {
            get { return _additionalSelectionTyping; }
            set { _additionalSelectionTyping = value; }
        }

        public int ScrollWidth
        {
            get { return _scrollWidth; }
            set
            {
                _scrollWidth = Math.Max(1, value);
                UpdateScrollBars();
            }
        }

        public bool ScrollWidthTracking
        {
            get { return _scrollWidthTracking; }
            set { _scrollWidthTracking = value; }
        }

        /// <summary>
        /// When each scrollbar is shown. <see cref="ScrollBarVisibility.Always"/> is the default
        /// because it is what this control has always done and what a full editor window wants — a
        /// scrollbar that comes and goes reflows the text area under the caret.
        ///
        /// <para>
        /// <see cref="ScrollBarVisibility.WhenNeeded"/> exists for an editor embedded in something
        /// that is already the right size, where a permanent scrollbar is just a grey stripe: a
        /// notebook code cell showed one under every cell, including empty ones, and clicking into
        /// a cell visibly changed its shape.
        /// </para>
        /// </summary>
        public ScrollBarVisibility HScrollBarVisibility
        {
            get { return _hScrollVisibility; }
            set { if (_hScrollVisibility == value) return; _hScrollVisibility = value; PerformLayout(); UpdateScrollBars(); }
        }

        public ScrollBarVisibility VScrollBarVisibility
        {
            get { return _vScrollVisibility; }
            set { if (_vScrollVisibility == value) return; _vScrollVisibility = value; PerformLayout(); UpdateScrollBars(); }
        }

        private ScrollBarVisibility _hScrollVisibility = ScrollBarVisibility.Always;
        private ScrollBarVisibility _vScrollVisibility = ScrollBarVisibility.Always;

        /// <summary>
        /// Whether a scrollbar should be on screen right now.
        ///
        /// <para>
        /// Each answer changes the text area, which changes the other answer — a horizontal bar
        /// costs height, which can push the last line off and call for a vertical one. Resolved by
        /// iterating to a fixed point rather than by deciding once: two passes settle every real
        /// case, and the cap stops a pathological size oscillating forever.
        /// </para>
        /// </summary>
        private void ApplyScrollBarVisibility()
        {
            for (int pass = 0; pass < 3; pass++)
            {
                Rectangle rc = TextClientRect();
                double textWidth = Math.Max(0, rc.Width - _viewModel.MarginsWidth() - 4.0);

                bool needH = _wrapMode == WrapMode.None && _scrollWidth > textWidth;
                int displayLines = _wrapMode != WrapMode.None
                    ? _renderer.DisplayRowCount(_engine.Doc, _viewModel, _engine.Cont)
                    : _engine.Cont.LinesDisplayed;
                bool needV = displayLines > LinesOnScreen;

                bool h = _hScrollVisibility == ScrollBarVisibility.Always
                    || (_hScrollVisibility == ScrollBarVisibility.WhenNeeded && needH);
                bool v = _vScrollVisibility == ScrollBarVisibility.Always
                    || (_vScrollVisibility == ScrollBarVisibility.WhenNeeded && needV);

                if (h == _hScroll.Visible && v == _vScroll.Visible) return;
                _hScroll.Visible = h;
                _vScroll.Visible = v;
            }
        }

        // ---- commands ----

        public void ExecuteCmd(Command cmd)
        {
            switch (cmd)
            {
                case Command.MoveSelectedLinesUp:
                    if (!_readOnly)
                    {
                        _engine.MoveSelectedLines(-1);
                        AfterEdit();
                    }
                    break;
                case Command.MoveSelectedLinesDown:
                    if (!_readOnly)
                    {
                        _engine.MoveSelectedLines(1);
                        AfterEdit();
                    }
                    break;
                case Command.SelectionDuplicate:
                    if (!_readOnly)
                    {
                        _engine.SelectionDuplicate();
                        AfterEdit();
                    }
                    break;
                case Command.SelectAll:
                    SelectAll();
                    break;
                case Command.Undo:
                    Undo();
                    break;
                case Command.Redo:
                    Redo();
                    break;
                case Command.Cut:
                    Cut();
                    break;
                case Command.Copy:
                    Copy();
                    break;
                case Command.Paste:
                    Paste();
                    break;
                case Command.LineDelete:
                    if (!_readOnly)
                    {
                        int line = CurrentLine;
                        int start = _engine.Doc.LineStart(line);
                        int end = (line + 1 < _engine.Doc.LinesTotal)
                            ? _engine.Doc.LineStart(line + 1)
                            : _engine.Doc.Length;
                        _engine.Doc.DeleteChars(start, end - start);
                        _engine.SetEmptySelection(Math.Min(start, _engine.Doc.Length));
                        AfterEdit();
                    }
                    break;
            }
        }

        // ---- shared edit plumbing ----

        private void OnDocTextModified(int position, int length, bool isInsert)
        {
            // Keep every selection range consistent with the edit (mirrors the
            // native control's selection tracking).
            if (isInsert) _engine.Sel.InsertedAt(position, length);
            else _engine.Sel.DeletedAt(position, length);
            _stylingValid = false;
            _pendingTextChanged = true;
            // Coalesce per-edit work: many edits arrive in bursts (undo groups,
            // multi-range typing). Restyle lazily; notify synchronously.
            OnTextChangedCore();
        }

        private bool _pendingTextChanged;

        private void OnTextChangedCore()
        {
            if (!_pendingTextChanged) return;
            _pendingTextChanged = false;
            UpdateScrollBars();
            Invalidate();
            OnTextChanged(EventArgs.Empty);
        }

        private void AfterEdit()
        {
            EnsureStyled();
            UpdateScrollBars();
            ScrollCaret();
            ResetCaretBlink();
            Invalidate();
            RaiseUpdateUI(UpdateChange.Content | UpdateChange.Selection);
        }

        private void CaretMoved()
        {
            // Completion is valid only while the caret stays inside the prefix range owned by
            // the active session. Typing/backspace extends or shrinks _acPosEnd through
            // RefilterAutoC; explicit navigation, mouse clicks, GotoPosition, or a non-empty
            // selection outside that range closes the stale popup. This mirrors the automatic
            // StartOffset/EndOffset lifetime of SharpDevelop completion windows.
            if (_acActive)
            {
                Core.SelectionRange selection = _engine.Sel.Main;
                int caret = _engine.MainCaret;
                if (selection.Start != selection.End || caret < _acPosStart || caret > _acPosEnd)
                    AutoCCancel();
            }
            ResetCaretBlink();
            Invalidate();
            RaiseUpdateUI(UpdateChange.Selection);
        }

        // ---- caret blink ----
        // The native control blinks its caret through the OS caret; this managed port draws
        // the caret itself in OnPaint, so without a timer it stood permanently solid. The
        // phase resets to VISIBLE on every caret move / edit / focus gain — standard editor
        // behavior, so the caret never happens to be mid-blink (invisible) right as it lands
        // somewhere new (e.g. after jumping to a failed test's line).
        private System.Windows.Forms.Timer _caretBlinkTimer;
        private bool _caretBlinkVisible = true;

        private void EnsureCaretBlinkTimer()
        {
            if (_caretBlinkTimer != null) return;
            int interval = SystemInformation.CaretBlinkTime;
            if (interval < 100) interval = 530;   // OS "no blink"/bogus value: classic rate
            _caretBlinkTimer = new System.Windows.Forms.Timer { Interval = interval };
            _caretBlinkTimer.Tick += delegate
            {
                _caretBlinkVisible = !_caretBlinkVisible;
                Invalidate();
            };
        }

        // Snap the caret to visible and restart the interval, so it stays solid for one
        // full blink period after any movement.
        private void ResetCaretBlink()
        {
            _caretBlinkVisible = true;
            if (_caretBlinkTimer != null && _caretBlinkTimer.Enabled)
            {
                _caretBlinkTimer.Stop();
                _caretBlinkTimer.Start();
            }
        }

        internal void StartCaretBlink()
        {
            EnsureCaretBlinkTimer();
            _caretBlinkVisible = true;
            _caretBlinkTimer.Start();
        }

        internal void StopCaretBlink()
        {
            if (_caretBlinkTimer != null) _caretBlinkTimer.Stop();
            _caretBlinkVisible = true;
        }

        private void RaiseUpdateUI(UpdateChange change)
        {
            var handler = UpdateUI;
            if (handler != null) handler(this, new UpdateUIEventArgs(change));
        }

        internal void OnInsertCheck(InsertCheckEventArgs e)
        {
            var handler = InsertCheck;
            if (handler != null) handler(this, e);
        }

        private void RaiseCharAdded(int ch)
        {
            var handler = CharAdded;
            if (handler != null) handler(this, new CharAddedEventArgs(ch));
        }

        // Re-lex + re-fold the whole document now.
        private void RestyleAll()
        {
            _lexerEngine.StyleDocument(_engine.Doc);
            _lexerEngine.FoldDocument(_engine.Doc);
            _engine.RecalcFoldVisibility();
            _engine.Doc.ResetDirty();
            _stylingValid = true;
        }

        private void EnsureStyled()
        {
            if (!_stylingValid) RestyleAll();
        }

        // ---- layout / scrollbars ----

        private Rectangle TextClientRect()
        {
            Rectangle rc = ClientRectangle;
            if (_vScroll.Visible) rc.Width = Math.Max(0, rc.Width - _vScroll.Width);
            if (_hScroll.Visible) rc.Height = Math.Max(0, rc.Height - _hScroll.Height);
            return rc;
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int vw = SystemInformation.VerticalScrollBarWidth;
            int hh = SystemInformation.HorizontalScrollBarHeight;
            _vScroll.SetBounds(ClientSize.Width - vw, 0, vw, Math.Max(0, ClientSize.Height - hh));
            _hScroll.SetBounds(0, ClientSize.Height - hh, Math.Max(0, ClientSize.Width - vw), hh);
            UpdateScrollBars();
        }

        /// <summary>Vertical scroll range in display rows, for tests asserting wrap layout.</summary>
        public int VScrollMaximumForTests { get { return _vScroll.Maximum; } }

        /// <summary>Horizontal scroll range; zero while wrapping.</summary>
        public int HScrollMaximumForTests { get { return _hScroll.Maximum; } }

        private void UpdateScrollBars()
        {
            if (!IsHandleCreated) return;
            _engine.SyncFoldLines();
            ApplyScrollBarVisibility();
            // The renderer needs the wrap width before it can report display rows, and this can
            // run before the first paint.
            Rectangle rcText = TextClientRect();
            _renderer.WrapWidth = Math.Max(0, rcText.Width - _viewModel.MarginsWidth() - 4.0);
            // When wrapping, one document line can occupy several rows, so the vertical range is
            // display ROWS -- using document lines would stop scrolling before the end of a file.
            int displayLines = _wrapMode != WrapMode.None
                ? _renderer.DisplayRowCount(_engine.Doc, _viewModel, _engine.Cont)
                : _engine.Cont.LinesDisplayed;
            int onScreen = LinesOnScreen;
            _vScroll.Minimum = 0;
            _vScroll.Maximum = Math.Max(0, displayLines - 1);
            _vScroll.LargeChange = Math.Max(1, onScreen);
            _vScroll.SmallChange = 1;
            if (_firstVisibleLine > _vScroll.Maximum) _firstVisibleLine = _vScroll.Maximum;
            _vScroll.Value = Math.Max(0, Math.Min(_firstVisibleLine,
                                                  Math.Max(0, _vScroll.Maximum - _vScroll.LargeChange + 1)));

            Rectangle rc = TextClientRect();
            _hScroll.Minimum = 0;
            // Wrapped text has no horizontal extent to scroll.
            _hScroll.Maximum = _wrapMode != WrapMode.None ? 0 : Math.Max(0, _scrollWidth);
            _hScroll.LargeChange = Math.Max(1, rc.Width);
            _hScroll.SmallChange = 16;
            int hMax = Math.Max(0, _hScroll.Maximum - _hScroll.LargeChange + 1);
            if (_xOffset > hMax) _xOffset = hMax;
            _hScroll.Value = Math.Max(0, Math.Min((int)_xOffset, hMax));
        }

        private void OnVScroll(object sender, ScrollEventArgs e)
        {
            _firstVisibleLine = e.NewValue;
            Invalidate();
            RaiseUpdateUI(UpdateChange.VScroll);
        }

        private void OnHScroll(object sender, ScrollEventArgs e)
        {
            _xOffset = e.NewValue;
            Invalidate();
            RaiseUpdateUI(UpdateChange.HScroll);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int lines = -Math.Sign(e.Delta) * 3;
            FirstVisibleLine = _firstVisibleLine + lines;
        }

        // ---- painting ----

        private RenderState MakeRenderState()
        {
            return new RenderState
            {
                FirstVisibleLine = _firstVisibleLine,
                XOffset = _xOffset,
                Sel = _engine.Sel,
                Cont = _engine.Cont,
                BracePos0 = _engine.BraceA,
                BracePos1 = _engine.BraceB,
                BraceBad = _engine.BraceBad,
                TabWidthColumns = _viewModel.TabWidth,
                ShowCaret = Focused && _caretBlinkVisible
            };
        }

        // OnPaint below repaints every pixel of the client area unconditionally (the text view
        // fills `rc`, and the dead corner between scrollbars is filled explicitly) — so the
        // default OnPaintBackground fill is pure redundant work. Left in place, .NET WinForms can
        // still invoke it as a separate erase pass in some code paths (notably around focus
        // changes and mouse-down, both of which call Invalidate() — OnMouseDown always calls
        // Focus(), and Focus/click-driven repaints are exactly what was reported as visible
        // flicker while typing/clicking), producing a visible erase-then-redraw flash even with
        // ControlStyles.AllPaintingInWmPaint/OptimizedDoubleBuffer already set.
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            EnsureStyled();
            _engine.SyncFoldLines();
            Rectangle rc = TextClientRect();
            _renderer.Paint(e.Graphics, rc, _engine.Doc, _viewModel, MakeRenderState());
            // Fill the dead corner between the two scrollbars.
            if (_vScroll.Visible && _hScroll.Visible)
            {
                using (var brush = new SolidBrush(SystemColors.Control))
                    e.Graphics.FillRectangle(brush, rc.Right, rc.Bottom,
                                             ClientSize.Width - rc.Right, ClientSize.Height - rc.Bottom);
            }
            if (_scrollWidthTracking) TrackScrollWidth();
        }

        private void TrackScrollWidth()
        {
            // Grow the horizontal extent to the widest visible line.
            int onScreen = LinesOnScreen;
            int line = _engine.Cont.DocFromDisplay(_firstVisibleLine);
            int shown = 0;
            double widest = 0;
            for (; line < _engine.Doc.LinesTotal && shown < onScreen; line++)
            {
                if (!_engine.Cont.GetVisible(line)) continue;
                shown++;
                int start = _engine.Doc.LineStart(line);
                int end = _engine.Doc.LineEnd(line);
                double w = _renderer.XAdvance(_engine.Doc, _viewModel, start, 0, end);
                if (w > widest) widest = w;
            }
            int needed = (int)Math.Ceiling(widest) + 32;
            if (needed > _scrollWidth)
            {
                _scrollWidth = needed;
                UpdateScrollBars();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposePopups();
                if (_caretBlinkTimer != null) { _caretBlinkTimer.Dispose(); _caretBlinkTimer = null; }
                _surface.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
