using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using HelixPythonNamespace.Editor.Markdown;   // the renderer: MarkdownStyle, MarkdownLayoutEngine, MarkdownVisual
using HelixPythonNamespace.Markdown;          // the parser engine: MarkdownParser, MarkdownDocument
using HelixPythonNamespace.Notebooks;

namespace HelixPythonNamespace.Editor.Notebook
{
    public sealed class NotebookCellEventArgs : EventArgs
    {
        public NotebookCellEventArgs(int index, NotebookCell cell) { Index = index; Cell = cell; }
        public int Index { get; private set; }
        public NotebookCell Cell { get; private set; }
    }

    /// <summary>
    /// The notebook surface: cells top to bottom, each with its source and whatever running it
    /// produced, on one scrolling page — shaped after Colab, because that is the interface the
    /// people who own these files already have in their fingers.
    ///
    /// <para>
    /// <b>One editor, not one per cell.</b> The fixture corpus has a 78-cell notebook; giving each
    /// cell its own <see cref="PythonEditorControl"/> would build 78 document models, 78 lexers and
    /// 78 completion controllers to show a page the user scrolls past. Instead every cell is
    /// PAINTED, and a single real editor is moved into whichever cell is being edited. That editor
    /// is the full one — the same syntax highlighting, IntelliSense, call tips and key handling a
    /// .py file gets — and it is fed the preceding cells as context, so completion in cell 40 knows
    /// what cell 3 imported.
    /// </para>
    ///
    /// <para>
    /// Layout is computed once per (document, width) into a flat list and painted per scroll tick,
    /// the same two-pass shape the Markdown preview uses. Output text is capped per output
    /// (<see cref="NotebookStyle.MaxOutputLines"/>) and SAYS it was capped: an 11 MB notebook whose
    /// one cell printed forty thousand lines must not cost a second of layout per scroll, and must
    /// not silently pretend the rest was never there.
    /// </para>
    /// </summary>
    public sealed class NotebookViewControl : Panel
    {
        private readonly NotebookStyle _style = new NotebookStyle();
        private readonly MarkdownStyle _markdownStyle = new MarkdownStyle();
        private readonly List<CellLayout> _layouts = new List<CellLayout>();
        private readonly Dictionary<NotebookOutput, Image> _images =
            new Dictionary<NotebookOutput, Image>();
        private readonly Timer _runningTicker;

        private NotebookDocument _document;
        private PythonEditorControl _editor;
        private int _selected = -1;
        private int _editing = -1;
        private int _laidOutWidth = -1;
        private int _contentLeft;
        private int _hoveredCell = HoverNone;
        private int _hoveredButton = HoverNone;
        private bool _suppressEditorEvents;
        private string _typeMarkerSource;
        private string _typeMarkerRoots;
        private Dictionary<int, string> _notebookTypeMarkers = new Dictionary<int, string>();
        private string _filePath;

        private const int HoverNone = -1;
        private const int HoverRun = -2;
        private const int HoverGrip = -3;

        // A resize in progress: which cell, where the drag started, and the height it started at.
        private int _resizingCell = -1;
        private int _resizeStartY;
        private int _resizeStartLines;

        public NotebookViewControl()
        {
            AutoScroll = true;
            BackColor = _style.Background;
            DoubleBuffered = true;
            TabStop = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            // Colab counts the seconds under a running cell. Only ticks while something is running.
            _runningTicker = new Timer { Interval = 500 };
            _runningTicker.Tick += delegate { InvalidateRunningCells(); };
        }

        /// <summary>Raised when the user asks to run one cell (Ctrl+Enter, or the ▶ in the gutter).</summary>
        public event EventHandler<NotebookCellEventArgs> RunCellRequested;

        /// <summary>Raised whenever the document changed in a way that should mark the tab dirty.</summary>
        public event EventHandler DocumentModified;

        /// <summary>Raised when the selected cell changes, so a host can update its commands.</summary>
        public event EventHandler SelectionChanged;

        /// <summary>
        /// Raised when the user asks for part of the selected cell on the clipboard. The view knows
        /// which cell and which part; it deliberately does not touch the clipboard itself, because
        /// the host is what owns the status line that has to report whether the copy worked.
        /// </summary>
        public event EventHandler<NotebookCopyPart> CopyRequested;

        public NotebookDocument Document
        {
            get { return _document; }
            set
            {
                CommitEdit();
                _document = value;
                _selected = _document != null && _document.Cells.Count > 0 ? 0 : -1;
                _editing = -1;
                _typeMarkerSource = null;
                _notebookTypeMarkers.Clear();
                DisposeImages();
                Relayout();
            }
        }

        /// <summary>The file the notebook came from — the editor uses it for completion context.</summary>
        public string FilePath
        {
            get { return _filePath; }
            set
            {
                if (string.Equals(_filePath, value, StringComparison.Ordinal)) return;
                _filePath = value;
                _typeMarkerSource = null;
                if (_document != null) Relayout();
            }
        }

        /// <summary>The solution, handed to the embedded editor so completion can see the workspace.</summary>
        public HelixPythonNamespace.Core.Model.SolutionModel Solution { get; set; }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (_document == null || _document.Cells.Count == 0) return;
                int clamped = Math.Max(0, Math.Min(value, _document.Cells.Count - 1));
                if (clamped == _selected) return;
                CommitEdit();
                _selected = clamped;
                EnsureVisible(clamped);
                Invalidate();
                RaiseSelectionChanged();
            }
        }

        public NotebookCell SelectedCell
        {
            get
            {
                if (_document == null || _selected < 0 || _selected >= _document.Cells.Count) return null;
                return _document.Cells[_selected];
            }
        }

        /// <summary>True while a cell's text is being edited in the embedded editor.</summary>
        public bool IsEditing { get { return _editing >= 0; } }

        /// <summary>The embedded editor, or null until a cell has been opened for editing.</summary>
        internal PythonEditorControl Editor { get { return _editor; } }

        internal IList<CellLayout> Layouts { get { return _layouts; } }

        /// <summary>
        /// The registered import roots changed. Re-run type inference over the notebook and hand
        /// the new set to the cell editor, if one is open.
        ///
        /// <para>
        /// This is the analyzer's half of what <c>ApplyModuleSearchPaths</c> does for the kernel.
        /// The two used to be one call to one consumer, which is how a notebook could run a cell
        /// perfectly while every inlay above it said "couldn't infer type": the kernel was told
        /// where the code lived and the engine that reads the code was not.
        /// </para>
        /// </summary>
        internal void RefreshModuleSearchPaths()
        {
            if (_editor != null && !_editor.IsDisposed && _document != null)
                _editor.ModuleSearchPaths = _document.ModuleSearchPaths;
            _typeMarkerSource = null;   // force the cached inference to be taken again
            if (_document != null) Relayout();
        }

        // ------------------------------------------------------------------ document edits

        /// <summary>Write the editor's text back into the cell it belongs to and close the editor.</summary>
        public void CommitEdit()
        {
            if (_editing < 0 || _editor == null || _document == null) return;
            int index = _editing;
            _editing = -1;

            if (index < _document.Cells.Count)
            {
                NotebookCell cell = _document.Cells[index];
                string text = _editor.Text ?? string.Empty;
                if (!string.Equals(text, cell.Source, StringComparison.Ordinal))
                {
                    cell.Source = text;
                    RaiseModified();
                }
            }

            _suppressEditorEvents = true;
            try { _editor.Visible = false; }
            finally { _suppressEditorEvents = false; }
            Relayout();
        }

        /// <summary>Open a cell for editing: the shared editor moves into it and takes focus.</summary>
        public void BeginEdit(int index)
        {
            if (_document == null || index < 0 || index >= _document.Cells.Count) return;
            if (_editing == index) { if (_editor != null) _editor.Focus(); return; }
            CommitEdit();

            _selected = index;
            EnsureEditor();
            NotebookCell cell = _document.Cells[index];

            _suppressEditorEvents = true;
            try
            {
                _editor.Text = cell.Source ?? string.Empty;
                // The path decides which completion engine the controller picks. A notebook's own
                // path ends in .ipynb, which no engine claims — so the editor is told it is looking
                // at Python, which is what a code cell is.
                _editor.SetFilePath(SyntheticCellPath(index));
                // That synthetic path exists nowhere on disk, so the inference engine's usual
                // walk-the-filesystem-around-the-file route resolves nothing for a cell being
                // edited. The registered roots are what it has instead — and they are the
                // user's own answer to "where does this import live", so they are better
                // evidence than the walk was.
                _editor.ModuleSearchPaths = _document.ModuleSearchPaths;
                _editor.Solution = Solution;
            }
            finally { _suppressEditorEvents = false; }

            _editing = index;
            LayoutEditorInto(index);
            _editor.Visible = true;
            _editor.BringToFront();
            _editor.Focus();
            Invalidate();
            RaiseSelectionChanged();
        }

        public void InsertCell(int index, NotebookCellKind kind)
        {
            if (_document == null) return;
            CommitEdit();
            _document.InsertCell(index, kind, string.Empty);
            _selected = Math.Max(0, Math.Min(index, _document.Cells.Count - 1));
            RaiseModified();
            Relayout();
            RaiseSelectionChanged();
        }

        public void DeleteSelectedCell() { DeleteCell(_selected); }

        public void DeleteCell(int index)
        {
            if (_document == null || index < 0) return;
            CommitEdit();
            if (!_document.RemoveCellAt(index)) return;
            if (_selected >= _document.Cells.Count) _selected = _document.Cells.Count - 1;
            RaiseModified();
            Relayout();
            RaiseSelectionChanged();
        }

        public void MoveSelectedCell(int delta) { MoveCell(_selected, delta); }

        public void MoveCell(int index, int delta)
        {
            if (_document == null || index < 0) return;
            CommitEdit();
            int moved = _document.MoveCell(index, delta);
            if (moved < 0) return;
            _selected = moved;
            RaiseModified();
            Relayout();
            RaiseSelectionChanged();
        }

        public void SetSelectedCellKind(NotebookCellKind kind)
        {
            NotebookCell cell = SelectedCell;
            if (cell == null || cell.Kind == kind) return;
            CommitEdit();
            cell.Kind = kind;
            if (kind != NotebookCellKind.Code) cell.ClearOutputs();
            RaiseModified();
            Relayout();
        }

        /// <summary>Drop one cell's outputs, or every cell's when <paramref name="index"/> is -1.</summary>
        public void ClearOutputs(int index)
        {
            if (_document == null) return;
            if (index < 0) _document.ClearAllOutputs();
            else if (index < _document.Cells.Count) _document.Cells[index].ClearOutputs();
            DisposeImages();
            RaiseModified();
            Relayout();
        }

        /// <summary>Re-measure after the model changed underneath us (a cell finished running).</summary>
        public void Refresh(int changedCellIndex)
        {
            Relayout();
            if (changedCellIndex >= 0) EnsureVisible(changedCellIndex);
            SyncRunningTicker();
        }

        // ------------------------------------------------------------------ layout

        /// <summary>One cell's measured geometry. Internal because the tests assert on it.</summary>
        internal sealed class CellLayout
        {
            public int Index;
            public NotebookCell Cell;
            public Rectangle Bounds;         // the whole cell card, content coordinates

            /// <summary>
            /// Everything this cell PAINTS, which is the card plus the gutter to its left — the
            /// <c>[*]</c>, the round run button and the live timer.
            ///
            /// <para>
            /// Separate from <see cref="Bounds"/> because they answer different questions and
            /// confusing them erased the run button twice a second: <see cref="Bounds"/> is the
            /// card (it starts at <c>_contentLeft + GutterWidth</c>), so a repaint clipped to the
            /// gutter did not intersect it, <c>OnPaint</c> skipped the cell, and the
            /// <c>g.Clear</c> that had already run left the gutter blank. Any intersection test
            /// deciding whether to paint a cell must use THIS.
            /// </para>
            /// </summary>
            public Rectangle PaintBounds;

            /// <summary>
            /// Text lines the source area is drawn at — its own line count, the height the user
            /// dragged it to, or <c>MinimumCodeLines</c>, whichever is largest. Code cells only.
            /// </summary>
            public int DisplayLines;

            /// <summary>The bottom-right drag handle, or empty for a cell that cannot be resized.</summary>
            public Rectangle ResizeGrip;
            public Rectangle SourceBounds;   // where the text sits (and where the editor goes)

            /// <summary>
            /// The timing string this cell's gutter was last painted with, so the running-cell
            /// ticker can skip a repaint that would draw the same thing — see
            /// <see cref="NotebookViewControl.InvalidateRunningCells"/>. Layout state, not model
            /// state: it is rebuilt with the layout and never saved.
            /// </summary>
            public string LastTimingPainted;
            public Rectangle OutputBounds;
            public Rectangle RunButton;      // the round ▶ in the gutter
            public Rectangle Toolbar;        // the hover strip at the cell's top right
            public Rectangle[] ToolbarButtons;   // move up, move down, delete
            public List<MarkdownVisual> MarkdownVisuals;
            public List<OutputVisual> Outputs;
            public string[] SourceLines;
            public Dictionary<int, string> TypeMarkers;
        }

        /// <summary>One drawable piece of one output.</summary>
        internal sealed class OutputVisual
        {
            public Rectangle Bounds;
            public string[] Lines;
            public Color Foreground;
            public Color Background;
            public Image Image;
            public string Note;              // "… 4 210 more lines", or why nothing is shown
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ClientSize.Width != _laidOutWidth) Relayout();
        }

        private void Relayout()
        {
            _layouts.Clear();
            if (_document == null || ClientSize.Width <= 0) { AutoScrollMinSize = Size.Empty; Invalidate(); return; }

            _laidOutWidth = ClientSize.Width;
            int available = ClientSize.Width - _style.PagePadding * 2 - SystemInformation.VerticalScrollBarWidth;
            // Colab keeps the page narrow and centred rather than letting a cell stretch across a
            // 4K monitor: a 300-character line of code is unreadable however much room it is given.
            int contentWidth = Math.Max(320, Math.Min(available, _style.MaxContentWidth));
            _contentLeft = _style.PagePadding + Math.Max(0, (available - contentWidth) / 2);
            int cellWidth = contentWidth - _style.GutterWidth;
            int y = _style.PagePadding;

            using (Graphics graphics = CreateGraphics())
            {
                NotebookCodeView codeView = NotebookCodeView.Build(_document);

                // The registered module roots are an input to inference, so they belong in the
                // cache key beside the source. Registering a root and watching the inlays keep
                // saying "couldn't infer type" until something else happened to edit a cell would
                // read as the feature not working.
                IList<string> moduleRoots = _document.ModuleSearchPaths;
                string rootKey = moduleRoots == null ? string.Empty : string.Join(";", moduleRoots);
                if (!string.Equals(_typeMarkerSource, codeView.Source, StringComparison.Ordinal) ||
                    !string.Equals(_typeMarkerRoots, rootKey, StringComparison.Ordinal))
                {
                    _notebookTypeMarkers =
                        VirtualTypeMarkerRenderer.ComputeMarkers(codeView.Source, FilePath, moduleRoots);
                    _typeMarkerSource = codeView.Source;
                    _typeMarkerRoots = rootKey;
                }
                for (int i = 0; i < _document.Cells.Count; i++)
                {
                    CellLayout layout = LayoutCell(graphics, _document.Cells[i], i, cellWidth, y);
                    layout.TypeMarkers = MarkersForCell(codeView, _notebookTypeMarkers, i);
                    _layouts.Add(layout);
                    y = layout.Bounds.Bottom + _style.CellSpacing;
                }
            }

            AutoScrollMinSize = new Size(contentWidth + _style.PagePadding * 2, y + _style.PagePadding);
            if (_editing >= 0) LayoutEditorInto(_editing);
            Invalidate();
        }

        private static Dictionary<int, string> MarkersForCell(NotebookCodeView codeView,
            IDictionary<int, string> notebookMarkers, int cellIndex)
        {
            var result = new Dictionary<int, string>();
            if (codeView == null || notebookMarkers == null) return result;
            foreach (NotebookCodeView.CellSpan span in codeView.Spans)
            {
                if (span.CellIndex != cellIndex) continue;
                foreach (KeyValuePair<int, string> marker in notebookMarkers)
                    if (marker.Key >= span.StartLine && marker.Key < span.StartLine + span.LineCount)
                        result[marker.Key - span.StartLine] = marker.Value;
                break;
            }
            return result;
        }

        private CellLayout LayoutCell(Graphics graphics, NotebookCell cell, int index, int width, int top)
        {
            var layout = new CellLayout { Index = index, Cell = cell, Outputs = new List<OutputVisual>() };
            int left = _contentLeft + _style.GutterWidth;
            bool code = cell.Kind == NotebookCellKind.Code;
            int textLeft = left + _style.CellPadding + (code ? _style.LineNumberWidth : 0);
            int innerWidth = width - _style.CellPadding * 2 - (code ? _style.LineNumberWidth : 0);
            int y = top + _style.CellPadding;

            if (cell.Kind == NotebookCellKind.Markdown)
            {
                Size size;
                MarkdownDocument parsed = MarkdownParser.Parse(cell.Source ?? string.Empty);
                layout.MarkdownVisuals = new MarkdownLayoutEngine(_markdownStyle)
                    .Layout(parsed, graphics, innerWidth, out size);
                layout.SourceBounds = new Rectangle(textLeft, y, innerWidth,
                    Math.Max(size.Height, _style.CodeLineHeight));
                y += layout.SourceBounds.Height;
            }
            else
            {
                layout.SourceLines = SplitLines(cell.Source);
                // Three claims on the height, and the tallest wins: the code must fit, the size the
                // user dragged this box to must be honoured, and a code cell is never shorter than
                // MinimumCodeLines. Content first means a remembered height can never hide a line —
                // dragging a box smaller than its code would be a way to lose track of it.
                layout.DisplayLines = Math.Max(
                    Math.Max(Math.Max(1, layout.SourceLines.Length), cell.HeightInLines),
                    _style.MinimumCodeLines);
                int height = layout.DisplayLines * _style.CodeLineHeight + _style.CellPadding;
                layout.SourceBounds = new Rectangle(textLeft, y, innerWidth, height);
                y += height;
            }

            int outputTop = y;
            if (code && cell.Outputs.Count > 0)
            {
                y += _style.OutputPadding;
                foreach (NotebookOutput output in cell.Outputs)
                {
                    OutputVisual visual = LayoutOutput(output, left + _style.CellPadding, y,
                                                       width - _style.CellPadding * 2);
                    layout.Outputs.Add(visual);
                    y = visual.Bounds.Bottom + 2;
                }
            }
            layout.OutputBounds = new Rectangle(left, outputTop, width, Math.Max(0, y - outputTop));

            y += _style.CellPadding;
            layout.Bounds = new Rectangle(left, top, width, y - top);
            if (code)
            {
                int grip = _style.ResizeGripSize;
                layout.ResizeGrip = new Rectangle(layout.Bounds.Right - grip - 2,
                                                  layout.Bounds.Bottom - grip - 2, grip, grip);
            }
            // The gutter is painted by PaintCell but lies outside the card, so the cell's real
            // painted extent starts one gutter to the left. See CellLayout.PaintBounds.
            layout.PaintBounds = Rectangle.FromLTRB(_contentLeft, layout.Bounds.Top,
                                                    layout.Bounds.Right, layout.Bounds.Bottom);

            // The gutter's round run button, aligned with the cell's first line — the most
            // recognisable control in the Colab interface, and the one a user reaches for before
            // they look for any menu.
            int runSize = _style.RunButtonSize;
            layout.RunButton = code
                ? new Rectangle(_contentLeft + _style.GutterWidth - runSize - 10,
                                top + _style.CellPadding, runSize, runSize)
                : Rectangle.Empty;

            // Hover toolbar, top-right inside the cell: move up, move down, delete.
            int buttonSize = _style.ToolbarButtonSize;
            const int buttons = 3;
            layout.Toolbar = new Rectangle(layout.Bounds.Right - buttons * buttonSize - 6,
                                           layout.Bounds.Top + 4, buttons * buttonSize, buttonSize);
            layout.ToolbarButtons = new Rectangle[buttons];
            for (int i = 0; i < buttons; i++)
                layout.ToolbarButtons[i] = new Rectangle(layout.Toolbar.Left + i * buttonSize,
                                                         layout.Toolbar.Top, buttonSize, buttonSize);
            return layout;
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        }

        private OutputVisual LayoutOutput(NotebookOutput output, int left, int top, int width)
        {
            var visual = new OutputVisual
            {
                Foreground = _style.Foreground,
                Background = _style.OutputBackground,
            };

            string mime;
            string base64 = output.ImageBase64(out mime);
            if (!string.IsNullOrEmpty(base64))
            {
                Image image = DecodeImage(output, base64);
                if (image != null)
                {
                    visual.Image = image;
                    int drawWidth = Math.Min(Math.Min(image.Width, width), _style.MaxImageWidth);
                    int drawHeight = image.Width <= 0
                        ? 0
                        : (int)Math.Round(image.Height * (drawWidth / (double)image.Width));
                    visual.Bounds = new Rectangle(left, top, drawWidth, drawHeight);
                    return visual;
                }
                visual.Note = "(an image output that could not be decoded)";
            }

            if (output.IsError)
            {
                visual.Foreground = _style.ErrorForeground;
                visual.Background = _style.ErrorBackground;
            }
            else if (output.Kind == NotebookOutputKind.Stream
                     && string.Equals(output.StreamName, "stderr", StringComparison.OrdinalIgnoreCase))
            {
                visual.Foreground = _style.StdErrForeground;
            }

            string text = AnsiText.Strip(output.PlainText());
            if (string.IsNullOrEmpty(text) && visual.Note == null && output.Data.Count > 0)
            {
                // An output with no text at all — a widget, a JavaScript bundle. Name what it is
                // rather than leaving a blank block the user cannot account for.
                var mimes = new List<string>(output.Data.Keys);
                text = "(" + string.Join(", ", mimes.ToArray()) + " output — not rendered here)";
                visual.Foreground = _style.Muted;
            }

            string[] lines = SplitLines((text ?? string.Empty).TrimEnd('\n'));
            if (lines.Length > _style.MaxOutputLines)
            {
                int hidden = lines.Length - _style.MaxOutputLines;
                var kept = new string[_style.MaxOutputLines];
                Array.Copy(lines, kept, _style.MaxOutputLines);
                lines = kept;
                visual.Note = "… " + hidden.ToString("N0") + " more lines not shown";
            }

            visual.Lines = lines;
            int height = lines.Length * _style.CodeLineHeight
                       + (visual.Note != null ? _style.CodeLineHeight : 0)
                       + _style.OutputPadding;
            visual.Bounds = new Rectangle(left, top, width, height);
            return visual;
        }

        private Image DecodeImage(NotebookOutput output, string base64)
        {
            Image image;
            if (_images.TryGetValue(output, out image)) return image;
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                using (var stream = new System.IO.MemoryStream(bytes))
                    image = Image.FromStream(stream);
            }
            catch (FormatException) { image = null; }         // not base64 after all
            catch (ArgumentException) { image = null; }        // not an image after all
            catch (OutOfMemoryException) { image = null; }     // GDI+ says this for a corrupt image
            _images[output] = image;
            return image;
        }

        private void DisposeImages()
        {
            foreach (Image image in _images.Values) if (image != null) image.Dispose();
            _images.Clear();
        }

        private void LayoutEditorInto(int index)
        {
            if (_editor == null || index < 0 || index >= _layouts.Count) return;
            CellLayout layout = _layouts[index];
            Point offset = AutoScrollPosition;
            _editor.Bounds = new Rectangle(
                layout.SourceBounds.Left + offset.X,
                layout.SourceBounds.Top + offset.Y,
                layout.SourceBounds.Width,
                Math.Max(_style.CodeLineHeight * 2, layout.SourceBounds.Height));
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            if (_editing >= 0) LayoutEditorInto(_editing);
        }

        private void EnsureVisible(int index)
        {
            if (index < 0 || index >= _layouts.Count) return;
            Rectangle bounds = _layouts[index].Bounds;
            int top = bounds.Top + AutoScrollPosition.Y;
            if (top >= 0 && bounds.Height + top <= ClientSize.Height) return;
            AutoScrollPosition = new Point(0, Math.Max(0, bounds.Top - _style.PagePadding));
            if (_editing >= 0) LayoutEditorInto(_editing);
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(_style.Background);
            if (_document == null) { PaintEmpty(g); return; }
            if (_layouts.Count != _document.Cells.Count) Relayout();

            g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            Rectangle clip = e.ClipRectangle;
            clip.Offset(-AutoScrollPosition.X, -AutoScrollPosition.Y);

            foreach (CellLayout layout in _layouts)
            {
                // PaintBounds, not Bounds: the gutter is outside the card and is painted here too,
                // so testing the card alone skipped exactly the repaints the running-cell ticker
                // asks for — after g.Clear had already wiped them.
                if (!layout.PaintBounds.IntersectsWith(clip)) continue;   // the whole point of the pass
                PaintCell(g, layout);
            }
        }

        private void PaintEmpty(Graphics g)
        {
            TextRenderer.DrawText(g, "No notebook loaded.", _style.Ui,
                new Point(_style.PagePadding, _style.PagePadding), _style.Muted);
        }

        private void PaintCell(Graphics g, CellLayout layout)
        {
            bool selected = layout.Index == _selected;
            bool code = layout.Cell.Kind == NotebookCellKind.Code;

            using (var background = new SolidBrush(code ? _style.CellBackground : _style.MarkdownBackground))
            using (GraphicsPath card = RoundedRect(layout.Bounds, _style.CellCornerRadius))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(background, card);
                Color borderColor = layout.Cell.IsRunning
                    ? _style.RunningCellBorder
                    : (selected ? _style.SelectedCellBorder : _style.CellBorder);
                using (var pen = new Pen(borderColor, selected || layout.Cell.IsRunning ? 2f : 1f))
                    g.DrawPath(pen, card);
                g.SmoothingMode = SmoothingMode.Default;
            }

            PaintGutter(g, layout);

            if (layout.Cell.Kind == NotebookCellKind.Markdown && layout.MarkdownVisuals != null)
            {
                PaintMarkdown(g, layout);
            }
            else if (layout.SourceLines != null)
            {
                // The line numbers keep being painted while the cell is being edited: they sit in
                // their own strip to the LEFT of SourceBounds, which is where the editor goes, so
                // the editor never covers them. Only the code itself is skipped — that IS covered.
                //
                // Both used to be skipped together, because the editor drew its own numbers in its
                // own margin. It no longer does (that margin was half of why a cell changed shape
                // when you clicked it), so skipping these left an edited cell with no numbers at all.
                PaintLineNumbers(g, layout);
                if (layout.Index != _editing) PaintCode(g, layout);
            }

            if (!layout.OutputBounds.IsEmpty)
            {
                Rectangle outputRegion = Rectangle.Inflate(layout.OutputBounds, -1, 0);
                using (var fill = new SolidBrush(_style.OutputBackground))
                    g.FillRectangle(fill, outputRegion);
                using (var separator = new Pen(_style.OutputBorder))
                    g.DrawLine(separator,
                        outputRegion.Left + _style.CellPadding, outputRegion.Top,
                        outputRegion.Right - _style.CellPadding, outputRegion.Top);
            }

            foreach (OutputVisual output in layout.Outputs) PaintOutput(g, output);

            if (_hoveredCell == layout.Index || selected) PaintCellToolbar(g, layout);
            if (_hoveredCell == layout.Index || _resizingCell == layout.Index) PaintResizeGrip(g, layout);
        }

        /// <summary>
        /// The gutter: Colab's round run button, the [n] execution label, and — once the cell has
        /// run in this session — a tick and how long it took.
        /// </summary>
        private void PaintGutter(Graphics g, CellLayout layout)
        {
            NotebookCell cell = layout.Cell;
            if (cell.Kind == NotebookCellKind.Code && !layout.RunButton.IsEmpty)
            {
                bool hovered = _hoveredCell == layout.Index && _hoveredButton == HoverRun;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (hovered)
                    using (var fill = new SolidBrush(_style.RunButtonHover)) g.FillEllipse(fill, layout.RunButton);
                using (var pen = new Pen(_style.RunButtonRing, 1.4f)) g.DrawEllipse(pen, layout.RunButton);

                if (cell.IsRunning) PaintStopGlyph(g, layout.RunButton);
                else PaintPlayGlyph(g, layout.RunButton);
                g.SmoothingMode = SmoothingMode.Default;
            }

            string label;
            if (cell.Kind != NotebookCellKind.Code) label = cell.Kind == NotebookCellKind.Raw ? "raw" : string.Empty;
            else if (cell.IsRunning) label = "[*]";
            else if (cell.ExecutionCount.HasValue) label = "[" + cell.ExecutionCount.Value + "]";
            else label = "[ ]";

            // Left of the run button, never under it: Colab puts [1] at the far left of the gutter
            // and the button beside it, and drawing both right-aligned overlaps them.
            int labelWidth = Math.Max(10, layout.RunButton.IsEmpty
                ? _style.GutterWidth - 8
                : layout.RunButton.Left - _contentLeft - 4);
            var labelBounds = new Rectangle(_contentLeft, layout.Bounds.Top + _style.CellPadding + 4,
                                            labelWidth, _style.CodeLineHeight);
            TextRenderer.DrawText(g, label, _style.Prompt, Scrolled(labelBounds), _style.Muted,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);

            string timing = TimingText(cell);
            if (timing == null) return;
            var timingBounds = new Rectangle(_contentLeft, layout.RunButton.Bottom + 2,
                                             _style.GutterWidth - 6, _style.CodeLineHeight);
            TextRenderer.DrawText(g, timing, _style.Ui, Scrolled(timingBounds),
                cell.LastRunFailed ? _style.ErrorForeground : _style.Ok,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        /// <summary>"✓ 7s" for a finished cell, a live counter for a running one, null otherwise.</summary>
        internal static string TimingText(NotebookCell cell)
        {
            if (cell == null) return null;
            if (cell.IsRunning && cell.RunStartedUtc.HasValue)
                return Describe(DateTime.UtcNow - cell.RunStartedUtc.Value);
            if (!cell.HasRunThisSession) return null;
            return (cell.LastRunFailed ? "✕ " : "✓ ") + Describe(cell.LastRunDuration.Value);
        }

        private static string Describe(TimeSpan elapsed)
        {
            if (elapsed.TotalSeconds < 1) return elapsed.TotalMilliseconds.ToString("0") + "ms";
            if (elapsed.TotalSeconds < 60) return elapsed.TotalSeconds.ToString("0") + "s";
            return ((int)elapsed.TotalMinutes) + "m " + elapsed.Seconds + "s";
        }

        /// <summary>
        /// Three short diagonals in the bottom-right corner — the shape every resizable thing on
        /// this platform uses, so it needs no label. Drawn on hover only: a permanent handle on
        /// every cell is visual noise on a page that is meant to read like a document.
        /// </summary>
        private void PaintResizeGrip(Graphics g, CellLayout layout)
        {
            if (layout.ResizeGrip.IsEmpty) return;
            Rectangle grip = layout.ResizeGrip;
            using (var pen = new Pen(_style.Muted, 1f))
            {
                for (int i = 1; i <= 3; i++)
                {
                    int inset = i * 4;
                    g.DrawLine(pen, grip.Right - inset, grip.Bottom, grip.Right, grip.Bottom - inset);
                }
            }
        }

        private void PaintPlayGlyph(Graphics g, Rectangle ring)
        {
            int size = ring.Width / 3;
            var centre = new Point(ring.Left + ring.Width / 2 + 1, ring.Top + ring.Height / 2);
            var triangle = new[]
            {
                new Point(centre.X - size / 2, centre.Y - size),
                new Point(centre.X - size / 2, centre.Y + size),
                new Point(centre.X + size, centre.Y),
            };
            using (var brush = new SolidBrush(_style.RunButtonRing)) g.FillPolygon(brush, triangle);
        }

        private void PaintStopGlyph(Graphics g, Rectangle ring)
        {
            int size = ring.Width / 3;
            var square = new Rectangle(ring.Left + ring.Width / 2 - size / 2,
                                       ring.Top + ring.Height / 2 - size / 2, size, size);
            using (var brush = new SolidBrush(_style.RunningCellBorder)) g.FillRectangle(brush, square);
        }

        private void PaintCellToolbar(Graphics g, CellLayout layout)
        {
            if (layout.ToolbarButtons == null) return;
            string[] glyphs = { "↑", "↓", "✕" };

            using (var background = new SolidBrush(_style.ToolbarBackground))
            using (GraphicsPath path = RoundedRect(layout.Toolbar, 4))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(background, path);
                g.SmoothingMode = SmoothingMode.Default;
            }

            for (int i = 0; i < layout.ToolbarButtons.Length; i++)
            {
                bool hovered = _hoveredCell == layout.Index && _hoveredButton == i;
                if (hovered)
                    using (var fill = new SolidBrush(_style.RunButtonHover))
                        g.FillRectangle(fill, layout.ToolbarButtons[i]);
                TextRenderer.DrawText(g, glyphs[i], _style.Ui, Scrolled(layout.ToolbarButtons[i]), _style.Foreground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void PaintLineNumbers(Graphics g, CellLayout layout)
        {
            int y = layout.SourceBounds.Top + 2;
            var bounds = new Rectangle(layout.SourceBounds.Left - _style.LineNumberWidth, y,
                                       _style.LineNumberWidth - 8, _style.CodeLineHeight);
            for (int i = 0; i < layout.SourceLines.Length; i++)
            {
                bounds.Y = y;
                TextRenderer.DrawText(g, (i + 1).ToString(), _style.Prompt, Scrolled(bounds), _style.LineNumber,
                    TextFormatFlags.Right | TextFormatFlags.NoPadding);
                y += _style.CodeLineHeight;
            }
        }

        private void PaintCode(Graphics g, CellLayout layout)
        {
            int y = layout.SourceBounds.Top + 2;
            int x = layout.SourceBounds.Left;
            char tripleQuote = '\0';

            foreach (string line in layout.SourceLines)
            {
                // Colourise every line even when it is off screen, or the triple-quote state drifts
                // and everything below a scrolled-past docstring is coloured wrongly.
                List<CodeToken> tokens = PythonPreviewColouriser.Colour(line, _style, ref tripleQuote);
                if (y + _style.CodeLineHeight >= 0)
                {
                    PaintColouredLine(g, line, tokens, x, y);
                    string marker;
                    int lineIndex = (y - layout.SourceBounds.Top - 2) / _style.CodeLineHeight;
                    if (layout.TypeMarkers != null && layout.TypeMarkers.TryGetValue(lineIndex, out marker))
                        PaintTypeMarker(g, line, marker, x, y);
                }
                y += _style.CodeLineHeight;
            }
        }

        private void PaintTypeMarker(Graphics g, string line, string marker, int x, int y)
        {
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            Size sourceWidth = TextRenderer.MeasureText(g, line ?? string.Empty, _style.Code,
                new Size(int.MaxValue, int.MaxValue), flags);
            TextRenderer.DrawText(g, ": " + marker, _style.CodeItalic,
                Scrolled(new Point(x + sourceWidth.Width + 14, y)), _style.TypeMarker, flags);
        }

        private void PaintColouredLine(Graphics g, string line, List<CodeToken> tokens, int x, int y)
        {
            if (tokens.Count == 0)
            {
                TextRenderer.DrawText(g, line, _style.Code, Scrolled(new Point(x, y)), _style.Foreground,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                return;
            }

            int cursor = 0;
            int penX = x;
            foreach (CodeToken token in tokens)
            {
                if (token.Start > cursor)
                    penX = DrawRun(g, line.Substring(cursor, token.Start - cursor), penX, y, _style.Foreground);
                int length = Math.Min(token.Length, line.Length - token.Start);
                if (length > 0) penX = DrawRun(g, line.Substring(token.Start, length), penX, y,
                    token.Color, token.FontStyle);
                cursor = token.Start + Math.Max(0, length);
            }
            if (cursor < line.Length) DrawRun(g, line.Substring(cursor), penX, y, _style.Foreground);
        }

        private int DrawRun(Graphics g, string text, int x, int y, Color color,
            FontStyle fontStyle = FontStyle.Regular)
        {
            if (string.IsNullOrEmpty(text)) return x;
            // Measure and draw with the SAME renderer: GDI and GDI+ disagree by a few pixels per
            // run, and mixing them makes a coloured line drift apart character by character.
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            Font font = fontStyle == FontStyle.Bold ? _style.CodeBold
                : (fontStyle == FontStyle.Italic ? _style.CodeItalic : _style.Code);
            TextRenderer.DrawText(g, text, font, Scrolled(new Point(x, y)), color, flags);
            Size size = TextRenderer.MeasureText(g, text, font,
                new Size(int.MaxValue, int.MaxValue), flags);
            return x + size.Width;
        }

        private void PaintMarkdown(Graphics g, CellLayout layout)
        {
            foreach (MarkdownVisual visual in layout.MarkdownVisuals)
            {
                Rectangle bounds = visual.Bounds;
                bounds.Offset(layout.SourceBounds.Left, layout.SourceBounds.Top);

                if (visual.Kind == MarkdownVisualKind.Rule)
                {
                    using (var pen = new Pen(visual.ForeColor))
                        g.DrawLine(pen, bounds.Left, bounds.Top, bounds.Right, bounds.Top);
                    continue;
                }
                if (visual.BackColor.A > 0)
                    using (var brush = new SolidBrush(visual.BackColor)) g.FillRectangle(brush, bounds);
                if (!string.IsNullOrEmpty(visual.Text))
                    TextRenderer.DrawText(g, visual.Text, visual.Font ?? _style.Ui, Scrolled(bounds.Location),
                        visual.ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        private void PaintOutput(Graphics g, OutputVisual output)
        {
            if (output.Image != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(output.Image, output.Bounds);
                return;
            }

            if (output.Background != _style.OutputBackground)
                using (var brush = new SolidBrush(output.Background)) g.FillRectangle(brush, output.Bounds);

            int y = output.Bounds.Top;
            if (output.Lines != null)
            {
                foreach (string line in output.Lines)
                {
                    TextRenderer.DrawText(g, line, _style.Code, Scrolled(new Point(output.Bounds.Left, y)),
                        output.Foreground, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    y += _style.CodeLineHeight;
                }
            }
            if (output.Note != null)
                TextRenderer.DrawText(g, output.Note, _style.Ui, Scrolled(new Point(output.Bounds.Left, y)),
                    _style.Muted, TextFormatFlags.NoPadding);
        }

        // TextRenderer uses GDI and does not honour Graphics.TranslateTransform consistently.
        // The cards, borders and images above are GDI+ and therefore follow the scroll transform;
        // without applying the same offset explicitly, text remains at its document coordinate
        // and drifts out of its cell as soon as the notebook is scrolled.
        private Point Scrolled(Point point)
        {
            Point offset = AutoScrollPosition;
            return new Point(point.X + offset.X, point.Y + offset.Y);
        }

        private Rectangle Scrolled(Rectangle bounds)
        {
            Point offset = AutoScrollPosition;
            bounds.Offset(offset.X, offset.Y);
            return bounds;
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ------------------------------------------------------------------ input

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_resizingCell >= 0) { DragResize(e.Location); return; }

            int cell = HitTest(e.Location);
            int button = HoverNone;
            if (cell >= 0)
            {
                Point content = ToContent(e.Location);
                CellLayout layout = _layouts[cell];
                if (layout.RunButton.Contains(content)) button = HoverRun;
                else if (!layout.ResizeGrip.IsEmpty && layout.ResizeGrip.Contains(content)) button = HoverGrip;
                else if (layout.ToolbarButtons != null)
                    for (int i = 0; i < layout.ToolbarButtons.Length; i++)
                        if (layout.ToolbarButtons[i].Contains(content)) { button = i; break; }
            }

            if (cell == _hoveredCell && button == _hoveredButton) return;
            _hoveredCell = cell;
            _hoveredButton = button;
            Cursor = button == HoverGrip ? Cursors.SizeNS
                   : button != HoverNone ? Cursors.Hand
                   : Cursors.Default;
            Invalidate();
        }

        /// <summary>
        /// Turn the pointer's travel since mouse-down into a new height, in whole text lines.
        ///
        /// <para>
        /// Lines rather than pixels because that is the unit the cell is laid out in and the unit
        /// that is saved: a height of "4.6 lines" would round differently on a machine with a
        /// different code font and reopen at a different size. Snapping also makes the drag feel
        /// like it is resizing a text box rather than a rectangle.
        /// </para>
        /// </summary>
        private void DragResize(Point clientPoint)
        {
            if (_resizingCell < 0 || _document == null || _resizingCell >= _document.Cells.Count) return;
            int deltaLines = (clientPoint.Y - _resizeStartY) / _style.CodeLineHeight;
            int wanted = Math.Max(_style.MinimumCodeLines, _resizeStartLines + deltaLines);

            NotebookCell cell = _document.Cells[_resizingCell];
            if (cell.HeightInLines == wanted) return;
            cell.HeightInLines = wanted;
            RaiseModified();
            Relayout();
        }

        private void EndResize()
        {
            if (_resizingCell < 0) return;
            _resizingCell = -1;
            Capture = false;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            EndResize();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_resizingCell >= 0) return;   // the capture keeps the drag alive off the control
            if (_hoveredCell == HoverNone && _hoveredButton == HoverNone) return;
            _hoveredCell = HoverNone;
            _hoveredButton = HoverNone;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int index = HitTest(e.Location);
            if (index < 0) { CommitEdit(); return; }

            // A right click selects the cell it landed on and stops there. The context menu that
            // follows is about THAT cell, so the selection has to move first — but a right click
            // that ALSO opened the editor, fired the run button or started a resize would be
            // performing an action the user never asked for on the way to a menu.
            if (e.Button != MouseButtons.Left)
            {
                if (index != _selected)
                {
                    CommitEdit();
                    _selected = index;
                    Invalidate();
                    RaiseSelectionChanged();
                }
                return;
            }

            Point content = ToContent(e.Location);
            CellLayout layout = _layouts[index];

            // The bottom-right grip: start a resize rather than entering the cell. Checked before
            // everything else because it overlaps the cell body, and a click that put the caret in
            // the code would make the handle unusable.
            if (!layout.ResizeGrip.IsEmpty && layout.ResizeGrip.Contains(content))
            {
                CommitEdit();
                _resizingCell = index;
                _resizeStartY = e.Y;
                _resizeStartLines = layout.DisplayLines;
                Capture = true;
                Cursor = Cursors.SizeNS;
                return;
            }

            // The gutter's run button.
            if (layout.RunButton.Contains(content))
            {
                if (index != _selected) { _selected = index; RaiseSelectionChanged(); }
                RaiseRun(index);
                Invalidate();
                return;
            }

            if (layout.ToolbarButtons != null)
            {
                for (int i = 0; i < layout.ToolbarButtons.Length; i++)
                {
                    if (!layout.ToolbarButtons[i].Contains(content)) continue;
                    if (i == 0) MoveCell(index, -1);
                    else if (i == 1) MoveCell(index, 1);
                    else DeleteCell(index);
                    return;
                }
            }

            if (index != _selected)
            {
                CommitEdit();
                _selected = index;
                Invalidate();
                RaiseSelectionChanged();
            }

            // A code cell opens for editing on a click in its source area; a markdown cell needs a
            // double click, because a single click on rendered prose is how you select the cell.
            if (layout.SourceBounds.Contains(content) && layout.Cell.Kind == NotebookCellKind.Code)
                BeginEdit(index);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int index = HitTest(e.Location);
            if (index < 0 || _document == null) return;
            if (_document.Cells[index].Kind != NotebookCellKind.Code) BeginEdit(index);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_document == null) return;

            // Colab's own keys, because they are the ones these users' fingers already know.
            if (e.KeyCode == Keys.Enter && (e.Control || e.Shift))
            {
                RaiseRun(_selected);
                if (e.Shift && _selected + 1 < _document.Cells.Count) SelectedIndex = _selected + 1;
                e.Handled = true;
                return;
            }
            // Copy, but only while the notebook itself has the keyboard: with a cell open for
            // editing the focus is inside the embedded editor, and Ctrl+C there must stay the
            // editor's own "copy what I selected".
            if (e.KeyCode == Keys.C && e.Control && !IsEditing)
            {
                RaiseCopy(e.Shift ? NotebookCopyPart.SourceAndOutput : NotebookCopyPart.Source);
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Enter && !IsEditing) { BeginEdit(_selected); e.Handled = true; return; }
            if (e.KeyCode == Keys.Up && !IsEditing) { SelectedIndex = _selected - 1; e.Handled = true; return; }
            if (e.KeyCode == Keys.Down && !IsEditing) { SelectedIndex = _selected + 1; e.Handled = true; return; }
        }

        /// <summary>The cell under a client point, or -1.</summary>
        internal int HitTest(Point clientPoint)
        {
            Point content = ToContent(clientPoint);
            for (int i = 0; i < _layouts.Count; i++)
            {
                CellLayout layout = _layouts[i];
                // The gutter belongs to the cell beside it, or its run button is unclickable.
                var withGutter = new Rectangle(_contentLeft, layout.Bounds.Top,
                                               layout.Bounds.Right - _contentLeft, layout.Bounds.Height);
                if (withGutter.Contains(content)) return i;
            }
            return -1;
        }

        private Point ToContent(Point clientPoint)
        {
            return new Point(clientPoint.X - AutoScrollPosition.X, clientPoint.Y - AutoScrollPosition.Y);
        }

        // ------------------------------------------------------------------ the shared editor

        private void EnsureEditor()
        {
            if (_editor != null && !_editor.IsDisposed) return;

            _editor = new PythonEditorControl { Visible = false };
            // A cell paints its own line numbers and sizes itself to its content, so the editor
            // that moves in on a click must bring no margins and no permanent scrollbars with it —
            // otherwise the box changes shape and its text jumps sideways under the caret.
            _editor.EmbeddedCellMode = true;
            // The card's own colour, so the editor is invisible as a rectangle: a white block inset
            // in a tinted card is still the box changing appearance under the click.
            _editor.PageBackColor = _style.CellBackground;
            // Notebook-wide IntelliSense: the engine is handed every code cell above this one, so
            // `df.` in cell 40 completes against the DataFrame cell 12 built rather than against
            // nothing. Evaluated per keystroke, which is why it is a delegate and not a string.
            _editor.CompletionContextPrefix = delegate
            {
                if (_document == null || _editing < 0) return null;
                return NotebookCodeView.PrecedingCode(_document, _editing);
            };
            _editor.TextChanged += delegate
            {
                if (_suppressEditorEvents || _editing < 0 || _document == null) return;
                // Grow the cell as the user adds lines, so the editor never covers the output below
                // it — but only when the line COUNT changed, or every keystroke relays out the whole
                // notebook.
                int lines = SplitLines(_editor.Text).Length;
                CellLayout layout = _editing < _layouts.Count ? _layouts[_editing] : null;
                if (layout != null && layout.SourceLines != null && lines != layout.SourceLines.Length)
                {
                    _document.Cells[_editing].Source = _editor.Text ?? string.Empty;
                    Relayout();
                }
            };
            _editor.LostFocus += delegate
            {
                if (_suppressEditorEvents) return;
                CommitEdit();
            };
            Controls.Add(_editor);
        }

        /// <summary>
        /// The path the embedded editor is told it is editing. It ends in `.py` because that is what
        /// selects the Python completion engine, and it names the cell so anything keyed on the path
        /// (diagnostics, the hover provider) does not collide between cells of one notebook.
        /// </summary>
        private string SyntheticCellPath(int index)
        {
            string notebook = string.IsNullOrEmpty(FilePath) ? "untitled.ipynb" : FilePath;
            return notebook + "#cell" + index + ".py";
        }

        // ------------------------------------------------------------------ plumbing

        private void SyncRunningTicker()
        {
            bool running = false;
            if (_document != null)
                foreach (NotebookCell cell in _document.Cells) if (cell.IsRunning) { running = true; break; }
            if (running == _runningTicker.Enabled) return;
            _runningTicker.Enabled = running;
        }

        /// <summary>
        /// Repaint a running cell's gutter only when what it SAYS has changed.
        ///
        /// <para>
        /// The ticker runs at 2 Hz so the counter cannot lag by most of a second, but
        /// <see cref="TimingText"/> rounds to whole seconds — so half of those repaints redrew a
        /// byte-identical gutter: the same antialiased ring, the same "[*]", the same "7s".
        /// Reported as the execution buttons flickering, and on a cell that never finishes it
        /// never stopped. Comparing the rendered text first makes the repaint rate follow the
        /// information rate, which is 1 Hz.
        /// </para>
        ///
        /// <para>
        /// The invalidated rectangle is the gutter down to the timing line rather than the whole
        /// cell: a 400-pixel-tall card was being invalidated to move one digit.
        /// </para>
        /// </summary>
        private void InvalidateRunningCells()
        {
            if (_document == null) return;
            foreach (CellLayout layout in _layouts)
            {
                if (!layout.Cell.IsRunning) continue;

                string timing = TimingText(layout.Cell) ?? string.Empty;
                if (string.Equals(layout.LastTimingPainted, timing, StringComparison.Ordinal)) continue;
                layout.LastTimingPainted = timing;

                int bottom = layout.RunButton.IsEmpty
                    ? layout.Bounds.Top + _style.CellPadding + 4 + _style.CodeLineHeight
                    : layout.RunButton.Bottom + 2 + _style.CodeLineHeight;
                int height = Math.Max(1, Math.Min(layout.Bounds.Bottom, bottom + 2) - layout.Bounds.Top);
                var bounds = new Rectangle(_contentLeft + AutoScrollPosition.X,
                                           layout.Bounds.Top + AutoScrollPosition.Y,
                                           _style.GutterWidth, height);
                Invalidate(bounds);
            }
        }

        private void RaiseRun(int index)
        {
            if (_document == null || index < 0 || index >= _document.Cells.Count) return;
            CommitEdit();
            EventHandler<NotebookCellEventArgs> handler = RunCellRequested;
            if (handler != null) handler(this, new NotebookCellEventArgs(index, _document.Cells[index]));
        }

        /// <summary>Run the selected cell — the host's Run command routes here.</summary>
        public void RunSelectedCell() { RaiseRun(_selected); }

        private void RaiseModified()
        {
            EventHandler handler = DocumentModified;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseSelectionChanged()
        {
            EventHandler handler = SelectionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>Ask the host to put part of the selected cell on the clipboard.</summary>
        internal void RaiseCopy(NotebookCopyPart part)
        {
            if (SelectedCell == null) return;
            EventHandler<NotebookCopyPart> handler = CopyRequested;
            if (handler != null) handler(this, part);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _runningTicker.Stop();
                _runningTicker.Dispose();
                DisposeImages();
                _style.Dispose();
                if (_markdownStyle != null) _markdownStyle.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
