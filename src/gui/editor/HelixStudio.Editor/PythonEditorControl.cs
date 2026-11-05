using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using HelixTextEditorNET;
using HelixPythonNamespace.Refactoring;

namespace HelixPythonNamespace.Editor
{
    /// <summary>
    /// Describes a context-menu item supplied by a subscriber of
    /// <see cref="PythonEditorControl.ResolveSymbolRequested"/>. Keeping this
    /// as a plain label+callback pair lets the host (e.g. HelixPythonNamespace.App) build
    /// navigation items without the editor project needing to reference the
    /// domain model.
    /// </summary>
    public sealed class EditorContextMenuItem
    {
        public string Label;
        public Action Callback;
    }

    /// <summary>
    /// Event args for <see cref="PythonEditorControl.ResolveSymbolRequested"/>.
    /// The editor fills in file/position/token before firing; subscribers append
    /// menu items. An empty <see cref="Items"/> list causes the menu to be suppressed.
    /// </summary>
    public sealed class EditorSymbolRequestArgs : EventArgs
    {
        public string FilePath;
        public int Line;
        public int Column;
        public string Token;
        public List<EditorContextMenuItem> Items = new List<EditorContextMenuItem>();
    }

    /// <summary>
    /// Event args for <see cref="PythonEditorControl.GoToDefinitionRequested"/> (F12).
    ///
    /// Carries the same file/position/token the context menu supplies, so F12 can resolve
    /// through the full symbol resolver rather than guessing from a bare word. A subscriber
    /// that navigates sets <see cref="Handled"/>; when it stays false the editor gives the
    /// user feedback instead of silently doing nothing.
    /// </summary>
    public sealed class EditorGoToDefinitionArgs : EventArgs
    {
        public string FilePath;
        public int Line;
        public int Column;
        public string Token;
        public bool Handled;
    }

    /// <summary>
    /// A Scintilla-based editor control configured for Python editing with
    /// syntax highlighting, line numbers, code folding, and bracket matching.
    /// </summary>
    public partial class PythonEditorControl : Scintilla
    {
        private readonly PythonAutoCompleteProvider _autoComplete;
        private readonly Timer _autoCompleteTimer;
        private int _maxLineNumberCharLength;

        // Hover tooltip support
        private PythonHoverProvider _hoverProvider;
        private BackgroundWorker _hoverWorker;
        private int _lastHoverPos = -1;
        private string _filePath;
        internal string CurrentFilePath { get { return _filePath; } }

        /// <summary>
        /// Extra import roots for type inference — the directories registered in a notebook's
        /// Modules pane. Set on the embedded cell editor, where <see cref="CurrentFilePath"/> is a
        /// SYNTHETIC path (<c>&lt;notebook&gt;#cell3.py</c>) that exists nowhere on disk, so the
        /// filesystem walk has nothing to walk and these roots are the only way an imported
        /// module's annotations can be found at all. Null for an ordinary document.
        /// </summary>
        internal IList<string> ModuleSearchPaths
        {
            get { return _moduleSearchPaths; }
            set
            {
                _moduleSearchPaths = value;
                if (_typeMarkers != null) _typeMarkers.RefreshFileContext();
            }
        }

        private IList<string> _moduleSearchPaths;

        // Marker indices for debugging
        private const int MARKER_ERROR = 1;
        private const int MARKER_BREAKPOINT = 3;
        private const int MARKER_CURRENT_LINE = 4;
        private const int MARKER_BREAKPOINT_BG = 5;
        private const int MARKER_CURRENT_LINE_BG = 6;
        private const int MARKER_BREAKPOINT_DISABLED = 7;

        /// <summary>Fired when user clicks the breakpoint margin to toggle a breakpoint (1-based line).</summary>
        public event EventHandler<int> BreakpointToggled;

        /// <summary>
        /// Fired for F12 (Go to Definition), carrying the caret position so the shell can resolve
        /// functions and methods — not just classes matched by bare name — and jump to the real
        /// definition in source.
        ///
        /// This replaced an identifier-text-only "go to type in diagram" event (iter 347): matching
        /// a bare word against class names meant F12 did nothing on a function, a method, or an
        /// ambiguous name, which is most of what a Python file contains. The diagram jump is still
        /// available as "Goto Designer" on the context menu.
        /// </summary>
        public event EventHandler<EditorGoToDefinitionArgs> GoToDefinitionRequested;

        /// <summary>
        /// Fired each time the right-click / app-key context menu is about to open.
        /// The editor supplies the file path, 1-based line/column of the caret
        /// (moved to the click point for right-click), and the word under the caret.
        /// Subscribers populate <see cref="EditorSymbolRequestArgs.Items"/>. When the
        /// list is empty after the event returns, the menu is suppressed.
        /// </summary>
        public event EventHandler<EditorSymbolRequestArgs> ResolveSymbolRequested;

        /// <summary>Fires when the caret moves; args are (1-based line, 1-based visual column).</summary>
        public event Action<int, int> CaretMoved;

        /// <summary>Caret line, 1-based (for a status-bar Ln/Col readout).</summary>
        public int CaretLine { get { return this.LineFromPosition(this.CurrentPosition) + 1; } }

        /// <summary>Caret column, 1-based and tab-aware (for a status-bar Ln/Col readout).</summary>
        public int CaretColumn { get { return this.GetColumn(this.CurrentPosition) + 1; } }

        // Python keywords for highlighting
        internal const string PythonHighlightKeywords =
            "False None True and as assert async await break class continue " +
            "def del elif else except finally for from global if import in " +
            "is lambda nonlocal not or pass raise return try while with yield";

        // Python builtins for secondary keyword highlighting
        internal const string PythonHighlightBuiltins =
            "abs all any ascii bin bool breakpoint bytearray bytes callable " +
            "chr classmethod compile complex delattr dict dir divmod enumerate " +
            "eval exec filter float format frozenset getattr globals hasattr " +
            "hash help hex id input int isinstance issubclass iter len list " +
            "locals map max memoryview min next object oct open ord pow print " +
            "property range repr reversed round set setattr slice sorted " +
            "staticmethod str sum super tuple type vars zip __import__ " +
            "ArithmeticError AssertionError AttributeError BaseException " +
            "BlockingIOError BrokenPipeError BufferError BytesWarning " +
            "ChildProcessError ConnectionAbortedError ConnectionError " +
            "ConnectionRefusedError ConnectionResetError DeprecationWarning " +
            "EOFError EnvironmentError Exception FileExistsError " +
            "FileNotFoundError FloatingPointError FutureWarning GeneratorExit " +
            "IOError ImportError ImportWarning IndentationError IndexError " +
            "InterruptedError IsADirectoryError KeyError KeyboardInterrupt " +
            "LookupError MemoryError ModuleNotFoundError NameError " +
            "NotADirectoryError NotImplemented NotImplementedError OSError " +
            "OverflowError PendingDeprecationWarning PermissionError " +
            "ProcessLookupError RecursionError ReferenceError ResourceWarning " +
            "RuntimeError RuntimeWarning StopAsyncIteration StopIteration " +
            "SyntaxError SyntaxWarning SystemError SystemExit TabError " +
            "TimeoutError TypeError UnboundLocalError UnicodeDecodeError " +
            "UnicodeEncodeError UnicodeError UnicodeTranslationError " +
            "UnicodeWarning UserWarning ValueError Warning WindowsError " +
            "ZeroDivisionError";

        // Decorators and special names
        private const string PYTHON_SPECIAL =
            "self cls __init__ __new__ __del__ __repr__ __str__ __bytes__ " +
            "__format__ __lt__ __le__ __eq__ __ne__ __gt__ __ge__ __hash__ " +
            "__bool__ __getattr__ __getattribute__ __setattr__ __delattr__ " +
            "__dir__ __get__ __set__ __delete__ __slots__ __init_subclass__ " +
            "__set_name__ __call__ __len__ __length_hint__ __getitem__ " +
            "__setitem__ __delitem__ __missing__ __iter__ __next__ " +
            "__reversed__ __contains__ __add__ __radd__ __iadd__ __mul__ " +
            "__rmul__ __imul__ __sub__ __rsub__ __isub__ __mod__ __rmod__ " +
            "__imod__ __pow__ __rpow__ __ipow__ __truediv__ __rtruediv__ " +
            "__itruediv__ __floordiv__ __rfloordiv__ __ifloordiv__ " +
            "__neg__ __pos__ __abs__ __invert__ __complex__ __int__ " +
            "__float__ __index__ __round__ __trunc__ __floor__ __ceil__ " +
            "__enter__ __exit__ __await__ __aiter__ __anext__ __aenter__ " +
            "__aexit__ __name__ __module__ __qualname__ __doc__ __dict__ " +
            "__class__ __bases__ __all__ __file__ __path__ __version__";

        private BlockScopeRenderer _blockScope;
        private Completion.CompletionController _completion;

        /// <summary>
        /// The solution the editor's content belongs to, or null for untitled / out-of-solution
        /// files. Hosts set this so completion can surface cross-file types and functions.
        /// </summary>
        public HelixPythonNamespace.Core.Model.SolutionModel Solution { get; set; }

        public PythonEditorControl()
        {
            _autoComplete = new PythonAutoCompleteProvider();
            _autoCompleteTimer = new Timer();
            _autoCompleteTimer.Interval = 300;
            _autoCompleteTimer.Tick += AutoCompleteTimer_Tick;

            ConfigureEditor();
            ConfigureSyntaxHighlighting();
            ConfigureAutoComplete();
            ConfigureFolding();
            ConfigureBraceMatching();

            // §3.2/§3.4: per-block scope overlays — virtual C#-style braces by
            // default, smooth Bézier connectors when braces are toggled off.
            // Rendered via GDI in our WM_PAINT hook; nothing touches the doc (§3.5).
            _blockScope = new BlockScopeRenderer(this);
            _blockScope.Attach();

            // §3.3: virtual inferred-type inlay markers (`: int`) after assignments.
            _typeMarkers = new VirtualTypeMarkerRenderer(this);
            _typeMarkers.Attach();

            // VS 2017-style completion: Ctrl+Space invokes, `.` re-opens in member mode,
            // typing 2+ id-chars auto-opens; icons per kind; side doc panel for the
            // selected item. Built on top of Scintilla's AutoC — the native popup is
            // shown; we only feed it content and watch selection changes.
            _completion = new Completion.CompletionController(
                this,
                solutionResolver: () => this.Solution,
                filePathResolver: () => this._filePath);
            // The notebook surface edits one CELL in this control while the document is the whole
            // notebook; this is how completion is told about the cells above it. Null for a file.
            _completion.ContextPrefix = () => CompletionContextPrefix == null ? null : CompletionContextPrefix();
        }

        /// <summary>
        /// Text that precedes this editor's buffer in the document it is part of — set by the
        /// notebook surface to the source of every code cell above the one being edited, so
        /// completion in cell 40 knows what cell 3 imported. Null for an ordinary file, where the
        /// buffer IS the document.
        /// </summary>
        public Func<string> CompletionContextPrefix { get; set; }

        /// <summary>Smallest / largest font size Ctrl+MouseWheel will zoom to.</summary>
        public const float MinEditorFontSize = 6f;
        public const float MaxEditorFontSize = 48f;

        // The font currently applied to this document, so Ctrl+MouseWheel can step relative to it
        // and the host can persist whatever the user zoomed to.
        private string _editorFontName = "Consolas";
        private float _editorFontSize = 11f;

        /// <summary>The font size currently applied to this document.</summary>
        public float EditorFontSize { get { return _editorFontSize; } }

        /// <summary>The font face currently applied to this document.</summary>
        public string EditorFontName { get { return _editorFontName; } }

        /// <summary>
        /// Raised when Ctrl+MouseWheel zoomed this document's font. The host persists the new size
        /// and mirrors it into the other open documents; the editor itself has no access to
        /// user settings.
        /// </summary>
        public event EventHandler<float> EditorFontSizeChanged;

        /// <summary>
        /// Reapply the Default style with the given font face and size. Downstream
        /// style slots (keywords, strings, etc.) inherit via <c>StyleClearAll</c>.
        /// </summary>
        public void ApplyFontSettings(string fontName, float fontSize)
        {
            if (string.IsNullOrWhiteSpace(fontName)) fontName = "Consolas";
            if (fontSize < MinEditorFontSize) fontSize = 11;
            if (fontSize > MaxEditorFontSize) fontSize = MaxEditorFontSize;

            // Record the choice BEFORE restyling: ConfigureSyntaxHighlighting re-applies the
            // Default style from these two fields, and it runs as the last step below. While it
            // hardcoded "Consolas"/11 instead, every Ctrl+MouseWheel zoom and every persisted
            // size was written into the style and then immediately overwritten again — the size
            // was tracked correctly in _editorFontSize but never actually rendered.
            _editorFontName = fontName;
            _editorFontSize = fontSize;
            try
            {
                this.Styles[HelixTextEditorNET.Style.Default].Font = fontName;
                this.Styles[HelixTextEditorNET.Style.Default].Size = (int)fontSize;
                this.StyleClearAll();
                ConfigureSyntaxHighlighting();
                UpdateLineNumberMarginWidth(force: true);
            }
            catch { /* editor may be disposed; ignore */ }
        }

        /// <summary>
        /// Whether the zoom modifier is held for the wheel event being handled.
        ///
        /// WinForms does not carry modifier state in <see cref="MouseEventArgs"/>, so this reads
        /// the live keyboard — which a synthetic <c>WM_MOUSEWHEEL</c> cannot influence. Virtual so
        /// an integration test can drive the real <see cref="OnMouseWheel"/> path without pressing
        /// a physical Ctrl key on the developer's desktop.
        /// </summary>
        protected virtual bool IsZoomModifierPressed()
        {
            return (Control.ModifierKeys & Keys.Control) == Keys.Control;
        }

        /// <summary>
        /// Step the font size by <paramref name="steps"/> points (Ctrl+MouseWheel, one point per
        /// wheel notch), clamped to <see cref="MinEditorFontSize"/>..<see cref="MaxEditorFontSize"/>.
        /// Raises <see cref="EditorFontSizeChanged"/> only when the size actually moved, so the
        /// host doesn't re-persist at the clamp boundaries.
        /// </summary>
        public void ZoomFontSize(int steps)
        {
            if (steps == 0) return;
            // ApplyFontSettings pushes an int into the style, so only whole points are reachable.
            float next = (float)Math.Round(_editorFontSize) + steps;
            if (next < MinEditorFontSize) next = MinEditorFontSize;
            if (next > MaxEditorFontSize) next = MaxEditorFontSize;
            if (Math.Abs(next - _editorFontSize) < 0.001f) return;

            ApplyFontSettings(_editorFontName, next);
            EventHandler<float> handler = EditorFontSizeChanged;
            if (handler != null) handler(this, _editorFontSize);
        }


        private void ConfigureEditor()
        {
            // General settings
            this.WrapMode = WrapMode.None;
            this.IndentationGuides = IndentView.LookBoth;
            this.TabWidth = 4;
            this.UseTabs = false;
            this.EdgeMode = EdgeMode.Line;
            this.EdgeColumn = 120;
            this.EdgeColor = Color.FromArgb(230, 230, 230);

            // Line numbers
            this.Margins[0].Type = MarginType.Number;
            this.Margins[0].Width = 40;

            // Breakpoint + error margin (markers 1, 3, 4)
            this.Margins[1].Type = MarginType.Symbol;
            this.Margins[1].Width = 20;
            this.Margins[1].Sensitive = true;
            this.Margins[1].Mask = (1 << MARKER_ERROR) | (1 << MARKER_BREAKPOINT) | (1 << MARKER_CURRENT_LINE)
                | (1 << MARKER_BREAKPOINT_BG) | (1 << MARKER_CURRENT_LINE_BG) | (1 << MARKER_BREAKPOINT_DISABLED);

            // Folding margin
            this.Margins[2].Type = MarginType.Symbol;
            this.Margins[2].Width = 16;
            this.Margins[2].Sensitive = true;
            this.Margins[2].Mask = Marker.MaskFolders;

            // Current line highlighting
            this.CaretLineVisible = true;
            this.CaretLineBackColor = Color.FromArgb(245, 245, 255);
            // A 1px caret is easy to lose on a large monitor — 2px matches VS/VS Code.
            this.CaretWidth = 2;
            this.CaretForeColor = Color.Black;

            // Selection
            this.SetSelectionBackColor(true, Color.FromArgb(173, 214, 255));

            // Multi-selection support
            this.MultipleSelection = true;
            this.AdditionalSelectionTyping = true;

            // Scrolling
            this.ScrollWidth = 1;
            this.ScrollWidthTracking = true;

            // Events
            this.TextChanged += OnTextChanged;
            this.InsertCheck += OnInsertCheck;
            this.CharAdded += OnCharAdded;
            this.UpdateUI += OnUpdateUI;
            this.MarginClick += OnMarginClick;
            this.KeyDown += OnKeyDownForNavigation;
            this.MouseDown += OnMouseDownForContextMenu;

            // Accept drops from the project tree: dropping a class/function inserts
            // the corresponding `from X import Y` line into this editor.
            this.AllowDrop = true;
            this.DragEnter += OnDragEnterForImport;
            this.DragOver  += OnDragEnterForImport;
            this.DragDrop  += OnDragDropForImport;

            // Dynamic context menu populated on Opening via ResolveSymbolRequested.
            ContextMenuStrip ctx = new ContextMenuStrip();
            ctx.Opening += OnContextMenuOpening;
            this.ContextMenuStrip = ctx;

            // Hover tooltip – mouse must dwell 600ms to trigger
            this.MouseDwellTime = 600;
            this.DwellStart += OnDwellStart;
            this.DwellEnd += OnDwellEnd;

            // A genuine focus move to another control (tab switch, clicking another panel)
            // otherwise leaves the quick-info doc panel dangling on screen forever — once
            // focus is elsewhere, Escape goes to whatever control now has it and never
            // reaches this editor's own Escape handling. See Scintilla.OnLeave for why this
            // is Leave, not LostFocus (showing the panel itself transiently blurs the editor).
            this.Leave += (s, e) => { try { _completion?.HideDoc(); } catch { } };
        }

        /// <summary>
        /// Show the quick-info doc panel for the symbol at the caret.
        /// Called by the host on Ctrl+K, Ctrl+I — the chord itself is owned by
        /// MainForm.ProcessCmdKey because the second key (Ctrl+I) collides with a
        /// form-level menu shortcut and must be intercepted before menu dispatch.
        /// </summary>
        public void ShowQuickInfoAtCaret()
        {
            try { _completion?.ShowQuickInfoAtCaret(); } catch { }
        }

        public void ShowPythonSnippets()
        {
            try { _completion?.InvokePythonSnippets(); } catch { }
        }

        private void OnKeyDownForNavigation(object sender, KeyEventArgs e)
        {
            // Ctrl+Shift+Space → Parameter Info (re-show the signature with the current-arg highlight, FR-KB-03).
            if (e.KeyCode == Keys.Space && e.Control && e.Shift && !e.Alt)
            {
                e.SuppressKeyPress = true;
                try { ShowCallTip(); } catch { }
                return;
            }
            // Ctrl+Space (Complete Word) and Ctrl+J (List Members) → force-show the completion popup.
            // Both are VS shortcuts; Ctrl+J lists members even mid-identifier (FR-KB-01 / FR-KB-02).
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.J) && e.Control && !e.Shift && !e.Alt)
            {
                e.SuppressKeyPress = true;
                try { _completion?.Invoke(force: true); } catch { }
                return;
            }
            // Escape closes the quick-info panel (but only when no popup / selection is active —
            // let those consume Escape first).
            if (e.KeyCode == Keys.Escape && !this.AutoCActive && this.SelectionStart == this.SelectionEnd)
            {
                try { _completion?.HideDoc(); } catch { }
            }
            if (e.KeyCode == Keys.F12 && e.Modifiers == Keys.None)
            {
                e.SuppressKeyPress = true;
                GoToDefinitionAtCaret();
            }
            // F1 → detailed, scrollable documentation for the symbol at the caret (VS2017 style).
            if (e.KeyCode == Keys.F1 && e.Modifiers == Keys.None)
            {
                e.SuppressKeyPress = true;
                try { _completion?.ShowQuickInfoAtCaret(); } catch { }
            }
        }

        /// <summary>
        /// F12. Sends the caret's file/line/column/token to the shell so the definition is
        /// resolved the same way the context menu resolves it. Returns the args so callers
        /// (and tests) can see whether anything was navigated to.
        ///
        /// Identifiers inside strings and comments are skipped: a word in a docstring is prose,
        /// and jumping away from it would be surprising rather than helpful.
        /// </summary>
        public EditorGoToDefinitionArgs GoToDefinitionAtCaret()
        {
            int pos = this.CurrentPosition;
            if (pos < 0 || pos > this.TextLength) return null;
            if (IsInStringOrComment(pos)) return null;
            int start = this.WordStartPosition(pos, true);
            int end = this.WordEndPosition(pos, true);
            if (end <= start) return null;
            string word = this.GetTextRange(start, end - start);
            if (string.IsNullOrEmpty(word)) return null;

            int lineIndex = this.LineFromPosition(pos);
            int lineStart = this.Lines[lineIndex].Position;
            EditorGoToDefinitionArgs args = new EditorGoToDefinitionArgs
            {
                FilePath = _filePath,
                Line = lineIndex + 1,
                Column = pos - lineStart + 1,
                Token = word,
            };

            EventHandler<EditorGoToDefinitionArgs> h = GoToDefinitionRequested;
            if (h != null) h(this, args);
            return args;
        }

        private void OnMouseDownForContextMenu(object sender, MouseEventArgs e)
        {
            // Scintilla does not move the caret on right-click by default; move it
            // to the click location so context-menu handlers read the right position.
            if (e.Button != MouseButtons.Right) return;
            int pos = this.CharPositionFromPoint(e.X, e.Y);
            if (pos < 0) return;
            // Keep an existing selection when the click lands inside it, so Cut/Copy act on the
            // selection the user made; only move (and clear) when clicking outside it.
            int selStart = System.Math.Min(this.SelectionStart, this.SelectionEnd);
            int selEnd = System.Math.Max(this.SelectionStart, this.SelectionEnd);
            if (selStart != selEnd && pos >= selStart && pos <= selEnd) return;
            this.GotoPosition(pos);
        }

        private void OnContextMenuOpening(object sender, CancelEventArgs e)
        {
            ContextMenuStrip ctx = (ContextMenuStrip)sender;
            ctx.Items.Clear();

            // Standard editing commands are ALWAYS present, so right-clicking anywhere — whitespace,
            // a string, a comment — offers a menu (VS / VS Code parity), not nothing.
            bool hasSelection = this.SelectionStart != this.SelectionEnd;
            AddEditItem(ctx, "Cut", hasSelection, () => this.Cut());
            AddEditItem(ctx, "Copy", hasSelection, () => this.Copy());
            AddEditItem(ctx, "Paste", this.CanPaste, () => this.Paste());
            ctx.Items.Add(new ToolStripSeparator());
            AddEditItem(ctx, "Select All", true, () => this.SelectAll());

            // Editor commands, with their shortcuts shown so they're discoverable (not just muscle memory).
            ctx.Items.Add(new ToolStripSeparator());
            AddEditItem(ctx, "Toggle Comment", true, () => this.ToggleComment(), "Ctrl+/");
            AddEditItem(ctx, "Move Line Up", true, () => this.MoveLinesUp(), "Alt+Up");
            AddEditItem(ctx, "Move Line Down", true, () => this.MoveLinesDown(), "Alt+Down");
            AddEditItem(ctx, "Duplicate Line", true, () => this.DuplicateSelection(), "Ctrl+D");
            if (IsPythonFile(_filePath))
                AddEditItem(ctx, "Insert Snippet...", true, ShowPythonSnippets, "Ctrl+K, Ctrl+X");
            ctx.Items.Add(new ToolStripSeparator());
            AddEditItem(ctx, "Fold All", true, () => this.FoldAll(), "Ctrl+K, Ctrl+0");
            AddEditItem(ctx, "Unfold All", true, () => this.UnfoldAll(), "Ctrl+K, Ctrl+J");

            if (IsCppOrCudaFile(_filePath))
            {
                ctx.Items.Add(new ToolStripSeparator());

                // Forward direction: a selected loop nest in a C or C++ file becomes a kernel.
                // Two forms are offered because they suit different users — the explicit kernel
                // for someone who wants to see the launch geometry, and the helix_gpu form for
                // someone who wants the speed-up without learning CUDA at all.
                if (CudaConversionCommands.AppliesTo(CudaConversionKind.ToCudaKernel, _filePath))
                {
                    AddEditItem(ctx, "Convert to CUDA Kernel", hasSelection,
                        () => ApplyConversion(CudaConversionKind.ToCudaKernel));
                    AddEditItem(ctx, "Convert to CUDA (helix_gpu, no CUDA knowledge needed)", hasSelection,
                        () => ApplyConversion(CudaConversionKind.ToCudaFramework));
                }

                // Reverse direction: a selected kernel becomes the sequential oracle you check it against.
                if (CudaConversionCommands.AppliesTo(CudaConversionKind.ToC, _filePath))
                {
                    AddEditItem(ctx, "Convert to C", hasSelection, () => ApplyConversion(CudaConversionKind.ToC));
                    AddEditItem(ctx, "Convert to C++", hasSelection, () => ApplyConversion(CudaConversionKind.ToCpp));
                }

                // The legacy marker-based refactoring stays available only where it can actually
                // act: it restores the exact original text a previous conversion stored inline.
                if (this.Text.Contains("// PYOPTOOL-CUDA-BEGIN:"))
                    AddEditItem(ctx, "Restore Original CPU Code", true, ConvertCudaToCpu);
            }

            if (IsPythonFile(_filePath))
            {
                ctx.Items.Add(new ToolStripSeparator());
                AddEditItem(ctx, "Vectorize", hasSelection, () => ApplyVectorization(forward: true));
                AddEditItem(ctx, "Un-vectorize", hasSelection, () => ApplyVectorization(forward: false));
            }

            // Symbol-specific actions (Go to Definition, Find References, …) appended below a separator
            // when the caret sits on a resolvable token outside strings/comments.
            int pos = this.CurrentPosition;
            if (pos >= 0 && pos <= this.TextLength && !IsInStringOrComment(pos))
            {
                int start = this.WordStartPosition(pos, true);
                int end = this.WordEndPosition(pos, true);
                string token = end > start ? this.GetTextRange(start, end - start) : string.Empty;
                if (!string.IsNullOrEmpty(token))
                {
                    // CurrentPosition may sit just after a selected word (for example on the
                    // '(' in `.run()`), even though WordStart/WordEnd correctly recovered
                    // `run`. Resolve the AST at a position inside that recovered token.
                    int symbolPos = start + ((end - start) / 2);
                    int lineIndex = this.LineFromPosition(symbolPos);
                    int lineStart = this.Lines[lineIndex].Position;
                    EditorSymbolRequestArgs args = new EditorSymbolRequestArgs
                    {
                        FilePath = _filePath,
                        Line = lineIndex + 1,
                        Column = symbolPos - lineStart + 1,
                        Token = token,
                    };

                    EventHandler<EditorSymbolRequestArgs> h = ResolveSymbolRequested;
                    if (h != null) h(this, args);

                    if (args.Items != null && args.Items.Count > 0)
                    {
                        ctx.Items.Add(new ToolStripSeparator());
                        foreach (EditorContextMenuItem item in args.Items)
                        {
                            if (item == null || string.IsNullOrEmpty(item.Label)) continue;
                            ToolStripMenuItem tsi = new ToolStripMenuItem(item.Label);
                            Action cb = item.Callback;
                            tsi.Click += delegate { if (cb != null) cb(); };
                            ctx.Items.Add(tsi);
                        }
                    }
                }
            }
            // Never cancel — the standard commands are always available.
        }

        private static bool IsCppOrCudaFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext == ".c" || ext == ".cc" || ext == ".cpp" || ext == ".cxx" ||
                   ext == ".h" || ext == ".hh" || ext == ".hpp" || ext == ".hxx" ||
                   ext == ".cu" || ext == ".cuh";
        }

        /// <summary>
        /// Runs one of the four CPU↔CUDA conversions on the selection and replaces it, or reports
        /// the concrete dependence that made the conversion unsafe. The analysis is the engine's
        /// (<see cref="CudaConversionCommands"/>); this method only edits the buffer and presents.
        /// </summary>
        private void ApplyConversion(CudaConversionKind kind)
        {
            CudaConversionOutcome outcome = ApplyConversionCore(kind);
            if (outcome == null) return;
            MessageBox.Show(outcome.Message, ConversionTitle(kind), MessageBoxButtons.OK,
                outcome.Succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        /// <summary>The transform without the modal dialog, so tests can drive it.</summary>
        internal CudaConversionOutcome ApplyConversionCore(CudaConversionKind kind)
        {
            int start = Math.Min(this.SelectionStart, this.SelectionEnd);
            int length = Math.Abs(this.SelectionEnd - this.SelectionStart);

            CudaConversionOutcome outcome;
            try
            {
                outcome = CudaConversionCommands.Convert(kind, this.Text, start, length);
            }
            catch (Exception ex)
            {
                return new CudaConversionOutcome { Succeeded = false, Message = "Could not analyze the selection: " + ex.Message };
            }

            if (!outcome.Succeeded) return outcome;

            this.BeginUndoAction();
            try
            {
                if (length > 0) this.ReplaceSelection(outcome.Replacement);
                else this.Text = outcome.Replacement;
                // The framework form only compiles with the header in scope, and a user who chose
                // "no CUDA knowledge needed" should not have to know that either.
                if (kind == CudaConversionKind.ToCudaFramework) EnsureIncludePresent("helix_gpu.hpp");
            }
            finally { this.EndUndoAction(); }
            return outcome;
        }

        /// <summary>Inserts `#include "name"` above the first non-directive line if it is absent.</summary>
        private void EnsureIncludePresent(string header)
        {
            if (string.IsNullOrEmpty(header) || this.Text.Contains(header)) return;
            this.InsertText(0, "#include \"" + header + "\"\n");
        }

        private static string ConversionTitle(CudaConversionKind kind)
        {
            switch (kind)
            {
                case CudaConversionKind.ToC: return "Convert to C";
                case CudaConversionKind.ToCpp: return "Convert to C++";
                case CudaConversionKind.ToCudaFramework: return "Convert to CUDA (helix_gpu)";
                default: return "Convert to CUDA";
            }
        }

        private void ConvertCudaToCpu()
        {
            ApplyCudaRefactoring(new CudaKernelRefactoringService().ConvertToCpu(this.Text));
        }

        private void ApplyCudaRefactoring(CudaRefactoringResult result)
        {
            if (!result.Succeeded)
            {
                MessageBox.Show(result.Message, "CUDA Refactoring", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            this.BeginUndoAction();
            try { this.Text = result.Source; }
            finally { this.EndUndoAction(); }
            MessageBox.Show(result.Message, "CUDA Refactoring", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        internal static bool IsPythonFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext == ".py" || ext == ".pyi";
        }

        /// <summary>Outcome of <see cref="ApplyVectorizationCore"/> — kept separate from the
        /// MessageBox presentation so the actual transform logic is testable without popping a
        /// modal dialog (which would hang an automated test waiting to be dismissed).</summary>
        internal sealed class VectorizationApplyResult
        {
            public bool Applied;
            public string Message;
        }

        // "Vectorize" (forward=true, F1-F16: loop -> numpy/comprehension) and "Un-vectorize"
        // (forward=false, R1/R2/R5/R10/R14: numpy/idiomatic -> explicit loop) share this handler.
        private void ApplyVectorization(bool forward)
        {
            VectorizationApplyResult result = ApplyVectorizationCore(forward);
            if (result == null) return; // nothing selected; menu item is disabled in this case anyway
            string verb = forward ? "Vectorize" : "Un-vectorize";
            MessageBox.Show(result.Message, verb, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // VectorizationRefactor.Match doesn't take a direction — it returns whatever candidates
        // matched in EITHER direction — so this filters to the requested direction and, per the
        // "engine decides the right candidate" ask, auto-applies the highest-confidence match
        // rather than showing a picker. Returns null only when there was no selection to act on.
        internal VectorizationApplyResult ApplyVectorizationCore(bool forward)
        {
            int start = Math.Min(this.SelectionStart, this.SelectionEnd);
            int end = Math.Max(this.SelectionStart, this.SelectionEnd);
            if (start == end) return null;

            string verb = forward ? "Vectorize" : "Un-vectorize";
            string selectedSource = this.GetTextRange(start, end - start);

            List<VectorizationCandidate> candidates;
            try
            {
                candidates = VectorizationRefactor.Match(selectedSource, this.Text);
            }
            catch (Exception ex)
            {
                return new VectorizationApplyResult { Applied = false, Message = "Could not analyze the selection: " + ex.Message };
            }

            VectorizationCandidate best = null;
            foreach (VectorizationCandidate c in candidates)
            {
                // F* kinds are loop->numpy (Vectorize); R* kinds are numpy->loop (Un-vectorize).
                bool isForwardKind = c.PatternKind.ToString().StartsWith("F", StringComparison.Ordinal);
                if (isForwardKind != forward) continue;
                if (best == null || c.Confidence > best.Confidence) best = c;
            }

            if (best == null)
            {
                return new VectorizationApplyResult
                {
                    Applied = false,
                    Message = "No recognizable pattern to " + verb.ToLowerInvariant() + " in the current selection.",
                };
            }

            this.BeginUndoAction();
            try
            {
                // Replace the selection first, while start/end still refer to the live selection —
                // any import insertion below happens at the top of the file, which Scintilla's own
                // position tracking accounts for regardless of order, but doing the replacement
                // first keeps this method's own reasoning simple: nothing here depends on
                // recomputing offsets after an earlier edit.
                this.ReplaceSelection(best.ReplacementText);
                foreach (string import in best.RequiredImports)
                    EnsureImportPresent(import);
            }
            finally
            {
                this.EndUndoAction();
            }

            string message = best.Label;
            if (best.Caveats != null && best.Caveats.Count > 0)
            {
                var sb = new StringBuilder(message);
                foreach (CandidateIssue issue in best.Caveats)
                    sb.Append("\n• ").Append(issue.Description);
                message = sb.ToString();
            }
            return new VectorizationApplyResult { Applied = true, Message = message };
        }

        // Inserts `import` as its own line if it isn't already present anywhere in the file, right
        // after the last existing top-level import (or at the very top if there are none).
        private void EnsureImportPresent(string import)
        {
            if (string.IsNullOrEmpty(import) || this.Text.Contains(import)) return;
            int insertAt = FindImportInsertionPoint(this.Text);
            this.InsertText(insertAt, import + "\n");
        }

        internal static int FindImportInsertionPoint(string fullText)
        {
            int insertAt = 0;
            bool seenImport = false;
            int pos = 0;
            while (pos < fullText.Length)
            {
                int lineEnd = fullText.IndexOf('\n', pos);
                if (lineEnd < 0) lineEnd = fullText.Length;
                string line = fullText.Substring(pos, lineEnd - pos).TrimEnd('\r');
                string trimmed = line.TrimStart();
                int nextPos = lineEnd < fullText.Length ? lineEnd + 1 : fullText.Length;

                if (trimmed.StartsWith("import ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("from ", StringComparison.Ordinal))
                {
                    seenImport = true;
                    insertAt = nextPos;
                }
                else if (seenImport && trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    // First real statement after the import block — stop, so a later `import` deep
                    // inside a function/class body doesn't get mistaken for part of the top block.
                    break;
                }

                pos = nextPos;
            }
            return insertAt;
        }

        private static void AddEditItem(ContextMenuStrip ctx, string label, bool enabled, Action onClick, string shortcut = null)
        {
            ToolStripMenuItem tsi = new ToolStripMenuItem(label) { Enabled = enabled };
            if (!string.IsNullOrEmpty(shortcut)) tsi.ShortcutKeyDisplayString = shortcut;
            tsi.Click += delegate { onClick(); };
            ctx.Items.Add(tsi);
        }

        // ------------------------------------------------------------------
        // Drop-to-import: dropping a type/function from the project tree
        // inserts the corresponding `from <ns> import <name>` line into this
        // editor. Format strings mirror ProjectTreePanel's drag formats —
        // keep them in sync with that file.
        // ------------------------------------------------------------------

        private const string TypeDragFormat = "HelixPythonNamespace.TypeDrag.v1";
        private const string FunctionDragFormat = "HelixPythonNamespace.FunctionDrag.v1";

        private void OnDragEnterForImport(object sender, DragEventArgs e)
        {
            if (e.Data == null) { e.Effect = DragDropEffects.None; return; }
            bool accepted = e.Data.GetDataPresent(TypeDragFormat)
                         || e.Data.GetDataPresent(FunctionDragFormat);
            e.Effect = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDropForImport(object sender, DragEventArgs e)
        {
            if (e.Data == null) return;

            string ns = null;
            string name = null;

            if (e.Data.GetDataPresent(TypeDragFormat))
            {
                var t = e.Data.GetData(TypeDragFormat) as HelixPythonNamespace.Core.Model.TypeModel;
                if (t != null) { ns = t.Namespace; name = t.Name; }
            }
            else if (e.Data.GetDataPresent(FunctionDragFormat))
            {
                var fn = e.Data.GetData(FunctionDragFormat) as HelixPythonNamespace.Core.Model.FunctionModel;
                if (fn != null) { ns = fn.Namespace; name = fn.Name; }
            }

            if (string.IsNullOrEmpty(name)) return;

            string line = string.IsNullOrEmpty(ns)
                ? "import " + name
                : "from " + ns + " import " + name;
            InsertImportLine(line, ns, name);
        }

        /// <summary>
        /// Insert <paramref name="newLine"/> into the document after the last
        /// existing import line (or at the top if there are none). No-op when
        /// the target symbol is already imported in this file.
        /// </summary>
        private void InsertImportLine(string newLine, string ns, string name)
        {
            string text = this.Text ?? string.Empty;
            string[] lines = text.Split('\n');

            if (AlreadyImports(lines, ns, name)) return;

            int insertAt = FindImportInsertionIndex(lines);

            int charOffset = 0;
            for (int i = 0; i < insertAt && i < lines.Length; i++)
                charOffset += lines[i].Length + 1; // +1 for the consumed '\n'

            string toInsert = newLine + "\n";
            this.InsertText(charOffset, toInsert);
        }

        /// <summary>
        /// Return true if the file already imports <paramref name="name"/> from
        /// <paramref name="ns"/> (or as a bare <c>import name</c> when ns is empty).
        /// Line-oriented string matching — we deliberately don't reparse the file.
        /// </summary>
        private static bool AlreadyImports(string[] lines, string ns, string name)
        {
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;

                if (!string.IsNullOrEmpty(ns))
                {
                    // from <ns> import a, b, name, c
                    string prefix = "from " + ns + " import ";
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        string rest = line.Substring(prefix.Length);
                        foreach (string part in rest.Split(','))
                        {
                            string token = part.Trim();
                            // strip "as alias"
                            int asIdx = token.IndexOf(" as ", StringComparison.Ordinal);
                            if (asIdx > 0) token = token.Substring(0, asIdx).Trim();
                            if (string.Equals(token, name, StringComparison.Ordinal)) return true;
                        }
                    }
                }
                else
                {
                    // import <name>   or   import <name> as X
                    if (line.StartsWith("import ", StringComparison.Ordinal))
                    {
                        string rest = line.Substring("import ".Length);
                        foreach (string part in rest.Split(','))
                        {
                            string token = part.Trim();
                            int asIdx = token.IndexOf(" as ", StringComparison.Ordinal);
                            if (asIdx > 0) token = token.Substring(0, asIdx).Trim();
                            if (string.Equals(token, name, StringComparison.Ordinal)) return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Find the 0-based line index at which a new import should be inserted:
        /// after any shebang + leading docstring + existing import block.
        /// </summary>
        private static int FindImportInsertionIndex(string[] lines)
        {
            int i = 0;

            // Shebang.
            if (i < lines.Length && lines[i].StartsWith("#!", StringComparison.Ordinal)) i++;

            // Module docstring (triple-single or triple-double). Handle both "whole
            // docstring on one line" and "multi-line" cases.
            i = SkipBlankLines(lines, i);
            if (i < lines.Length)
            {
                string trimmed = lines[i].TrimStart();
                string q3 = null;
                if (trimmed.StartsWith("\"\"\"", StringComparison.Ordinal)) q3 = "\"\"\"";
                else if (trimmed.StartsWith("'''", StringComparison.Ordinal)) q3 = "'''";
                if (q3 != null)
                {
                    // If the opening line also contains a closing triple-quote, it's a
                    // single-line docstring; otherwise scan forward for the closer.
                    string body = trimmed.Substring(3);
                    int closeOnSameLine = body.IndexOf(q3, StringComparison.Ordinal);
                    if (closeOnSameLine >= 0)
                    {
                        i++;
                    }
                    else
                    {
                        i++;
                        while (i < lines.Length && lines[i].IndexOf(q3, StringComparison.Ordinal) < 0) i++;
                        if (i < lines.Length) i++; // consume the closing-line
                    }
                }
            }

            // Walk past any existing import block (contiguous `import`/`from` lines, allowing blanks between).
            int lastImportEnd = i;
            while (i < lines.Length)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0) { i++; continue; }
                if (trimmed.StartsWith("import ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("from ", StringComparison.Ordinal))
                {
                    i++;
                    lastImportEnd = i;
                    continue;
                }
                break;
            }
            return lastImportEnd;
        }

        private static int SkipBlankLines(string[] lines, int i)
        {
            while (i < lines.Length && lines[i].Trim().Length == 0) i++;
            return i;
        }

        // C/C++ keyword set (Scintilla Cpp lexer, keyword list 0).
        private const string CPP_KEYWORDS =
            "alignas alignof and asm auto bool break case catch char char16_t char32_t class const " +
            "constexpr const_cast continue decltype default delete do double dynamic_cast else enum " +
            "explicit export extern false float for friend goto if inline int long mutable namespace " +
            "new noexcept nullptr operator or private protected public register reinterpret_cast return " +
            "short signed sizeof static static_assert static_cast struct switch template this throw true " +
            "try typedef typeid typename union unsigned using virtual void volatile wchar_t while size_t";

        // CUDA qualifiers, built-in vector/dim types, and coordinate built-ins (keyword list 1 — styled
        // distinctly from plain C/C++ keywords).
        private const string CUDA_KEYWORDS =
            "__global__ __device__ __host__ __constant__ __shared__ __managed__ __restrict__ __forceinline__ " +
            "__noinline__ __launch_bounds__ dim3 uint3 int1 int2 int3 int4 uint1 uint2 uint4 float1 float2 " +
            "float3 float4 double1 double2 double3 double4 char1 char2 char3 char4 uchar1 uchar2 uchar4 " +
            "cudaError_t cudaStream_t cudaEvent_t cudaMemcpyKind size_t threadIdx blockIdx blockDim gridDim " +
            "warpSize __syncthreads __syncwarp __threadfence atomicAdd atomicSub atomicExch atomicCAS";

        private void ConfigureSyntaxHighlighting()
        {
            this.StyleResetDefault();

            // Default style. The face and size come from the fields ApplyFontSettings maintains —
            // NOT a literal — because this method re-runs on every font change and on every
            // re-lex, and a literal here silently reverts the user's Preferences font and every
            // Ctrl+MouseWheel zoom.
            this.Styles[Style.Default].Font = _editorFontName;
            this.Styles[Style.Default].Size = (int)_editorFontSize;
            this.Styles[Style.Default].BackColor = _pageBackColor;
            this.Styles[Style.Default].ForeColor = Color.Black;
            this.StyleClearAll();

            // CUDA-C files use the C/C++ lexer with CUDA keyword sets (FR-IC-01); everything else Python.
            string ext = string.IsNullOrEmpty(_filePath) ? null : System.IO.Path.GetExtension(_filePath);
            if (string.Equals(ext, ".cu", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".cuh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".cpp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".hpp", StringComparison.OrdinalIgnoreCase))
            {
                ConfigureCudaHighlighting();
                return;
            }

            // CMakeLists.txt / *.cmake use the native Scintilla CMake lexer (FR-CORE-09).
            if (IsCMakeFilePath(_filePath))
            {
                ConfigureCMakeHighlighting();
                return;
            }

            // *.toml / pyproject.toml use the Properties lexer (sections/keys/comments) (FR-CORE-06).
            if (IsTomlFilePath(_filePath))
            {
                ConfigureTomlHighlighting();
                return;
            }

            // *.md / *.markdown. Before this branch existed they fell through to Lexer.Python
            // below, so a README's prose was coloured as source: `is`, `not`, `and`, `class` and
            // `import` all rendered as keywords, and `#` headings as comments. The rendered preview
            // (MarkdownViewControl) is the primary view; this is what the Source toggle shows.
            if (IsMarkdownFilePath(_filePath))
            {
                ConfigureMarkdownHighlighting();
                return;
            }





            this.Lexer = Lexer.Python;

            // Comment styles
            this.Styles[Style.Python.CommentLine].ForeColor = Color.FromArgb(0, 128, 0);
            this.Styles[Style.Python.CommentLine].Italic = true;
            this.Styles[Style.Python.CommentBlock].ForeColor = Color.FromArgb(0, 128, 0);
            this.Styles[Style.Python.CommentBlock].Italic = true;

            // String styles
            this.Styles[Style.Python.String].ForeColor = Color.FromArgb(163, 21, 21);
            this.Styles[Style.Python.StringEol].ForeColor = Color.FromArgb(163, 21, 21);
            this.Styles[Style.Python.StringEol].BackColor = Color.FromArgb(255, 230, 230);
            this.Styles[Style.Python.Character].ForeColor = Color.FromArgb(163, 21, 21);
            this.Styles[Style.Python.Triple].ForeColor = Color.FromArgb(128, 0, 0);
            this.Styles[Style.Python.TripleDouble].ForeColor = Color.FromArgb(128, 0, 0);

            // Number style
            this.Styles[Style.Python.Number].ForeColor = Color.FromArgb(9, 134, 88);

            // Keyword styles
            this.Styles[Style.Python.Word].ForeColor = Color.FromArgb(0, 0, 255);
            this.Styles[Style.Python.Word].Bold = true;
            this.Styles[Style.Python.Word2].ForeColor = Color.FromArgb(43, 145, 175);

            // Decorator
            this.Styles[Style.Python.Decorator].ForeColor = Color.FromArgb(128, 0, 128);

            // Operator
            this.Styles[Style.Python.Operator].ForeColor = Color.FromArgb(0, 0, 0);
            this.Styles[Style.Python.Operator].Bold = true;

            // Identifier
            this.Styles[Style.Python.Identifier].ForeColor = Color.Black;

            // ClassName (via word2)
            this.Styles[Style.Python.ClassName].ForeColor = Color.FromArgb(43, 145, 175);
            this.Styles[Style.Python.ClassName].Bold = true;

            // DefName
            this.Styles[Style.Python.DefName].ForeColor = Color.FromArgb(116, 83, 31);
            this.Styles[Style.Python.DefName].Bold = true;

            // Set keyword lists
            this.SetKeywords(0, PythonHighlightKeywords);
            this.SetKeywords(1, PythonHighlightBuiltins);
        }

        // FR-IC-01: C/CUDA colorization for .cu/.cuh (and host C++). Keywords blue, CUDA qualifiers/types
        // teal, comments green, strings dark-red, numbers green, preprocessor purple. The C++ lexer styles
        // the kernel-launch chevrons <<< / >>> as operators, so they read distinctly from identifiers.
        private void ConfigureCudaHighlighting()
        {
            this.Lexer = Lexer.Cpp;

            this.Styles[Style.Cpp.Comment].ForeColor = Color.FromArgb(0, 128, 0);
            this.Styles[Style.Cpp.Comment].Italic = true;
            this.Styles[Style.Cpp.CommentLine].ForeColor = Color.FromArgb(0, 128, 0);
            this.Styles[Style.Cpp.CommentLine].Italic = true;
            this.Styles[Style.Cpp.CommentDoc].ForeColor = Color.FromArgb(0, 128, 0);
            this.Styles[Style.Cpp.CommentLineDoc].ForeColor = Color.FromArgb(0, 128, 0);

            this.Styles[Style.Cpp.Number].ForeColor = Color.FromArgb(9, 134, 88);

            this.Styles[Style.Cpp.String].ForeColor = Color.FromArgb(163, 21, 21);
            this.Styles[Style.Cpp.Character].ForeColor = Color.FromArgb(163, 21, 21);
            this.Styles[Style.Cpp.StringEol].BackColor = Color.FromArgb(255, 230, 230);
            this.Styles[Style.Cpp.Verbatim].ForeColor = Color.FromArgb(163, 21, 21);

            // Keyword list 0 (C/C++ keywords) — blue bold.
            this.Styles[Style.Cpp.Word].ForeColor = Color.FromArgb(0, 0, 255);
            this.Styles[Style.Cpp.Word].Bold = true;
            // Keyword list 1 (CUDA qualifiers/types/built-ins) — teal, distinct from plain keywords.
            this.Styles[Style.Cpp.Word2].ForeColor = Color.FromArgb(43, 145, 175);
            this.Styles[Style.Cpp.Word2].Bold = true;

            this.Styles[Style.Cpp.Preprocessor].ForeColor = Color.FromArgb(128, 0, 128);
            this.Styles[Style.Cpp.Operator].ForeColor = Color.FromArgb(0, 0, 0);
            this.Styles[Style.Cpp.Operator].Bold = true;
            this.Styles[Style.Cpp.Identifier].ForeColor = Color.Black;
            this.Styles[Style.Cpp.GlobalClass].ForeColor = Color.FromArgb(43, 145, 175);

            this.SetKeywords(0, CPP_KEYWORDS);
            this.SetKeywords(1, CUDA_KEYWORDS);
        }

        /// <summary>True for <c>*.toml</c> (incl. <c>pyproject.toml</c>) files.</summary>
        internal static bool IsTomlFilePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return string.Equals(System.IO.Path.GetExtension(path), ".toml", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for <c>*.md</c> / <c>*.markdown</c>.
        ///
        /// The single authority for "is this a Markdown document?" across the editor: the
        /// highlighting dispatch above and <c>EditorTabPage</c>'s Preview/Source toggle both ask
        /// here, so a tab can never render a preview for a file the lexer is treating as Python, or
        /// the reverse.
        /// </summary>
        internal static bool IsMarkdownFilePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = System.IO.Path.GetExtension(path);
            return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".mdown", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".mkd", StringComparison.OrdinalIgnoreCase);
        }

        // Native Scintilla Markdown lexer id + its style numbers (SciLexer.h SCE_MARKDOWN_*),
        // implemented by HelixTextEditorNET.Lexing.MarkdownLexerEngine in the in-house port.
        private const int SCLEX_MARKDOWN = 98;
        private const int SCE_MARKDOWN_LINE_BEGIN = 1, SCE_MARKDOWN_STRONG1 = 2, SCE_MARKDOWN_EM1 = 4,
            SCE_MARKDOWN_HEADER1 = 6, SCE_MARKDOWN_PRECHAR = 12, SCE_MARKDOWN_ULIST_ITEM = 13,
            SCE_MARKDOWN_OLIST_ITEM = 14, SCE_MARKDOWN_BLOCKQUOTE = 15, SCE_MARKDOWN_HRULE = 17,
            SCE_MARKDOWN_LINK = 18, SCE_MARKDOWN_CODE = 19, SCE_MARKDOWN_CODEBK = 21;

        private void ConfigureMarkdownHighlighting()
        {
            this.Lexer = (Lexer)SCLEX_MARKDOWN;

            // Headings share one blue and get smaller-to-plainer by level, mirroring what the
            // preview does with size — the source view cannot change font size per line, so weight
            // carries the hierarchy instead.
            for (int level = 0; level < 6; level++)
            {
                this.Styles[SCE_MARKDOWN_HEADER1 + level].ForeColor = Color.FromArgb(0, 0, 192);
                this.Styles[SCE_MARKDOWN_HEADER1 + level].Bold = level < 3;
            }

            this.Styles[SCE_MARKDOWN_STRONG1].Bold = true;
            this.Styles[SCE_MARKDOWN_EM1].Italic = true;

            // Code reuses the editor's own string colour so a fenced block in a README reads the
            // same way a string literal does in a .py file.
            this.Styles[SCE_MARKDOWN_CODE].ForeColor = Color.FromArgb(163, 21, 21);
            this.Styles[SCE_MARKDOWN_CODEBK].ForeColor = Color.FromArgb(163, 21, 21);

            this.Styles[SCE_MARKDOWN_LINK].ForeColor = Color.FromArgb(0, 102, 204);
            this.Styles[SCE_MARKDOWN_BLOCKQUOTE].ForeColor = Color.FromArgb(0, 128, 0);
            this.Styles[SCE_MARKDOWN_BLOCKQUOTE].Italic = true;
            this.Styles[SCE_MARKDOWN_ULIST_ITEM].ForeColor = Color.FromArgb(128, 0, 128);
            this.Styles[SCE_MARKDOWN_ULIST_ITEM].Bold = true;
            this.Styles[SCE_MARKDOWN_OLIST_ITEM].ForeColor = Color.FromArgb(128, 0, 128);
            this.Styles[SCE_MARKDOWN_OLIST_ITEM].Bold = true;
            this.Styles[SCE_MARKDOWN_HRULE].ForeColor = Color.FromArgb(128, 128, 128);
            this.Styles[SCE_MARKDOWN_PRECHAR].ForeColor = Color.FromArgb(128, 128, 128);
            this.Styles[SCE_MARKDOWN_LINE_BEGIN].ForeColor = Color.FromArgb(32, 32, 32);
        }

        // Native Scintilla Properties lexer id + its style numbers (SciLexer.h SCE_PROPS_*).
        private const int SCLEX_PROPS = 9;
        private const int SCE_PROPS_COMMENT = 1, SCE_PROPS_SECTION = 2, SCE_PROPS_ASSIGNMENT = 3,
            SCE_PROPS_DEFVAL = 4, SCE_PROPS_KEY = 5;

        /// <summary>
        /// FR-CORE-06: colorize <c>*.toml</c> with the Properties lexer — <c>[tables]</c>, keys, <c>=</c>,
        /// values, and <c># comments</c>. (Scintilla 3.x has no dedicated TOML lexer; Properties is a close,
        /// robust approximation — a clear upgrade over the Python-lexer fallback.)
        /// </summary>
        private void ConfigureTomlHighlighting()
        {
            this.Lexer = (Lexer)SCLEX_PROPS;
            this.SetProperty("lexer.props.allow.initial.spaces", "1");

            this.Styles[SCE_PROPS_COMMENT].ForeColor = Color.FromArgb(0, 128, 0);
            this.Styles[SCE_PROPS_COMMENT].Italic = true;
            this.Styles[SCE_PROPS_SECTION].ForeColor = Color.FromArgb(0, 0, 255);   // [table] headers
            this.Styles[SCE_PROPS_SECTION].Bold = true;
            this.Styles[SCE_PROPS_KEY].ForeColor = Color.FromArgb(128, 0, 128);      // keys
            this.Styles[SCE_PROPS_ASSIGNMENT].ForeColor = Color.FromArgb(0, 0, 0);
            this.Styles[SCE_PROPS_ASSIGNMENT].Bold = true;
            this.Styles[SCE_PROPS_DEFVAL].ForeColor = Color.FromArgb(163, 21, 21);   // values
        }

        /// <summary>True for <c>CMakeLists.txt</c> and <c>*.cmake</c> files.</summary>
        internal static bool IsCMakeFilePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = System.IO.Path.GetFileName(path);
            if (string.Equals(name, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) return true;
            string ext = System.IO.Path.GetExtension(path);
            return string.Equals(ext, ".cmake", StringComparison.OrdinalIgnoreCase);
        }

        // Native Scintilla CMake lexer id + its style numbers (SciLexer.h SCE_CMAKE_*).
        private const int SCLEX_CMAKE = 80;
        private const int SCE_CMAKE_COMMENT = 1, SCE_CMAKE_STRINGDQ = 2, SCE_CMAKE_STRINGLQ = 3, SCE_CMAKE_STRINGRQ = 4,
            SCE_CMAKE_COMMANDS = 5, SCE_CMAKE_PARAMETERS = 6, SCE_CMAKE_VARIABLE = 7, SCE_CMAKE_USERDEFINED = 8,
            SCE_CMAKE_WHILEDEF = 9, SCE_CMAKE_FOREACHDEF = 10, SCE_CMAKE_IFDEFINEDEF = 11, SCE_CMAKE_MACRODEF = 12,
            SCE_CMAKE_STRINGVAR = 13, SCE_CMAKE_NUMBER = 14;

        /// <summary>
        /// FR-CORE-09: colorize <c>CMakeLists.txt</c>/<c>*.cmake</c> with the native Scintilla CMake lexer —
        /// comments, strings, commands, variables (<c>${...}</c>), and the control-flow keyword styles
        /// (while/foreach/if/macro). Replaces the previous Python-lexer fallback.
        /// </summary>
        private void ConfigureCMakeHighlighting()
        {
            this.Lexer = (Lexer)SCLEX_CMAKE;

            Color green = Color.FromArgb(0, 128, 0);
            Color str = Color.FromArgb(163, 21, 21);
            Color blue = Color.FromArgb(0, 0, 255);
            Color varColor = Color.FromArgb(128, 0, 128);

            this.Styles[SCE_CMAKE_COMMENT].ForeColor = green;
            this.Styles[SCE_CMAKE_COMMENT].Italic = true;
            this.Styles[SCE_CMAKE_STRINGDQ].ForeColor = str;
            this.Styles[SCE_CMAKE_STRINGLQ].ForeColor = str;
            this.Styles[SCE_CMAKE_STRINGRQ].ForeColor = str;
            this.Styles[SCE_CMAKE_STRINGVAR].ForeColor = varColor;
            this.Styles[SCE_CMAKE_COMMANDS].ForeColor = blue;
            this.Styles[SCE_CMAKE_COMMANDS].Bold = true;
            this.Styles[SCE_CMAKE_PARAMETERS].ForeColor = Color.FromArgb(43, 145, 175);
            this.Styles[SCE_CMAKE_VARIABLE].ForeColor = varColor;
            this.Styles[SCE_CMAKE_USERDEFINED].ForeColor = Color.FromArgb(116, 83, 31);
            foreach (int s in new[] { SCE_CMAKE_WHILEDEF, SCE_CMAKE_FOREACHDEF, SCE_CMAKE_IFDEFINEDEF, SCE_CMAKE_MACRODEF })
            {
                this.Styles[s].ForeColor = blue;
                this.Styles[s].Bold = true;
            }
            this.Styles[SCE_CMAKE_NUMBER].ForeColor = Color.FromArgb(9, 134, 88);

            // Keyword list 0 = commands (colorized with the COMMANDS style).
            this.SetKeywords(0, CMAKE_COMMAND_KEYWORDS);
            // Keyword list 1 = parameters (PUBLIC/PRIVATE/... and common arg keywords).
            this.SetKeywords(1, CMAKE_PARAM_KEYWORDS);
        }

        private const string CMAKE_COMMAND_KEYWORDS =
            "cmake_minimum_required project add_executable add_library add_subdirectory add_custom_command " +
            "add_custom_target add_dependencies add_test enable_testing enable_language include include_guard " +
            "set unset list string math option message cmake_policy set_property get_property set_target_properties " +
            "get_target_property target_link_libraries target_include_directories target_compile_definitions " +
            "target_compile_options target_compile_features target_sources target_link_directories target_link_options " +
            "include_directories link_directories link_libraries add_compile_options add_compile_definitions " +
            "add_definitions remove_definitions find_package find_library find_path find_program find_file " +
            "file configure_file install export execute_process try_compile try_run mark_as_advanced " +
            "separate_arguments get_filename_component source_group aux_source_directory " +
            "if elseif else endif foreach endforeach while endwhile function endfunction macro endmacro block endblock " +
            "return break continue fetchcontent_declare fetchcontent_makeavailable";

        private const string CMAKE_PARAM_KEYWORDS =
            "PUBLIC PRIVATE INTERFACE STATIC SHARED MODULE OBJECT REQUIRED QUIET COMPONENTS VERSION LANGUAGES " +
            "CACHE FORCE PARENT_SCOPE PROPERTIES DESTINATION TARGETS FILES DIRECTORY GLOB GLOB_RECURSE " +
            "AND OR NOT STREQUAL MATCHES EQUAL LESS GREATER EXISTS DEFINED ON OFF TRUE FALSE " +
            "NAME COMMAND WORKING_DIRECTORY OUTPUT DEPENDS APPEND";

        private void ConfigureAutoComplete()
        {
            this.AutoCMaxHeight = 12;
            this.AutoCMaxWidth = 0;
            this.AutoCOrder = Order.PerformSort;

            // CallTip style
            this.CallTipSetForeHlt(Color.FromArgb(0, 0, 200));
        }

        private void ConfigureFolding()
        {
            this.SetProperty("fold", "1");
            this.SetProperty("fold.compact", "1");

            // Folding markers
            this.Markers[Marker.Folder].Symbol = MarkerSymbol.BoxPlus;
            this.Markers[Marker.Folder].SetBackColor(Color.White);
            this.Markers[Marker.Folder].SetForeColor(Color.Gray);

            this.Markers[Marker.FolderOpen].Symbol = MarkerSymbol.BoxMinus;
            this.Markers[Marker.FolderOpen].SetBackColor(Color.White);
            this.Markers[Marker.FolderOpen].SetForeColor(Color.Gray);

            this.Markers[Marker.FolderEnd].Symbol = MarkerSymbol.BoxPlusConnected;
            this.Markers[Marker.FolderEnd].SetBackColor(Color.White);
            this.Markers[Marker.FolderEnd].SetForeColor(Color.Gray);

            this.Markers[Marker.FolderMidTail].Symbol = MarkerSymbol.TCorner;
            this.Markers[Marker.FolderMidTail].SetBackColor(Color.White);
            this.Markers[Marker.FolderMidTail].SetForeColor(Color.Gray);

            this.Markers[Marker.FolderOpenMid].Symbol = MarkerSymbol.BoxMinusConnected;
            this.Markers[Marker.FolderOpenMid].SetBackColor(Color.White);
            this.Markers[Marker.FolderOpenMid].SetForeColor(Color.Gray);

            this.Markers[Marker.FolderSub].Symbol = MarkerSymbol.VLine;
            this.Markers[Marker.FolderSub].SetBackColor(Color.White);
            this.Markers[Marker.FolderSub].SetForeColor(Color.Gray);

            this.Markers[Marker.FolderTail].Symbol = MarkerSymbol.LCorner;
            this.Markers[Marker.FolderTail].SetBackColor(Color.White);
            this.Markers[Marker.FolderTail].SetForeColor(Color.Gray);

            // Error marker — a bold red '✕' (SC_MARK_CHARACTER), deliberately DISTINCT from
            // the breakpoint's solid disc below: both used to be identical red circles, so a
            // diagnostic mark and a stray persisted breakpoint were indistinguishable in the
            // margin (and got misdiagnosed for each other more than once).
            this.Markers[MARKER_ERROR].Symbol = (MarkerSymbol)((int)MarkerSymbol.Character + '✕');
            this.Markers[MARKER_ERROR].SetBackColor(Color.FromArgb(205, 30, 30));
            this.Markers[MARKER_ERROR].SetForeColor(Color.FromArgb(205, 30, 30));

            // Breakpoint marker — solid red circle with dark outline
            this.Markers[MARKER_BREAKPOINT].Symbol = MarkerSymbol.Circle;
            this.Markers[MARKER_BREAKPOINT].SetBackColor(Color.FromArgb(220, 40, 40));
            this.Markers[MARKER_BREAKPOINT].SetForeColor(Color.FromArgb(120, 10, 10));

            // Disabled breakpoint marker — greyed circle, no line highlight (VS-style "hollow"/inactive).
            this.Markers[MARKER_BREAKPOINT_DISABLED].Symbol = MarkerSymbol.Circle;
            this.Markers[MARKER_BREAKPOINT_DISABLED].SetBackColor(Color.FromArgb(210, 210, 210));
            this.Markers[MARKER_BREAKPOINT_DISABLED].SetForeColor(Color.FromArgb(130, 130, 130));

            // Current execution line marker (yellow arrow)
            this.Markers[MARKER_CURRENT_LINE].Symbol = MarkerSymbol.ShortArrow;
            this.Markers[MARKER_CURRENT_LINE].SetBackColor(Color.Yellow);
            this.Markers[MARKER_CURRENT_LINE].SetForeColor(Color.Black);

            // Breakpoint line background highlight (yellow)
            this.Markers[MARKER_BREAKPOINT_BG].Symbol = MarkerSymbol.Background;
            this.Markers[MARKER_BREAKPOINT_BG].SetBackColor(Color.FromArgb(255, 255, 200));

            // Current execution line background highlight (light red)
            this.Markers[MARKER_CURRENT_LINE_BG].Symbol = MarkerSymbol.Background;
            this.Markers[MARKER_CURRENT_LINE_BG].SetBackColor(Color.FromArgb(255, 210, 210));

            this.AutomaticFold = AutomaticFold.Show | AutomaticFold.Click | AutomaticFold.Change;
        }

        private void ConfigureBraceMatching()
        {
            // Brace matching style
            this.Styles[Style.BraceLight].BackColor = Color.FromArgb(220, 255, 220);
            this.Styles[Style.BraceLight].ForeColor = Color.Blue;
            this.Styles[Style.BraceLight].Bold = true;

            this.Styles[Style.BraceBad].BackColor = Color.FromArgb(255, 220, 220);
            this.Styles[Style.BraceBad].ForeColor = Color.Red;
            this.Styles[Style.BraceBad].Bold = true;
        }

        /// <summary>
        /// Sets the hover provider for type/signature tooltips on mouse dwell.
        /// </summary>
        public void SetHoverProvider(PythonHoverProvider provider)
        {
            _hoverProvider = provider;
        }

        /// <summary>
        /// Sets the file path used for hover context. Call after loading/saving.
        /// </summary>
        public void SetFilePath(string path)
        {
            _filePath = path;
            if (_typeMarkers != null) _typeMarkers.RefreshFileContext();
            // Re-apply syntax highlighting now that the language (by extension) is known — .cu/.cuh
            // switch to the C/CUDA lexer (FR-IC-01).
            try { ConfigureSyntaxHighlighting(); } catch { }
            // Re-run diagnostics now that the language (by extension) is known — the load-time
            // TextChanged fired before the path was set.
            KickDiagnostics();
        }

        /// <summary>
        /// Turn word wrap on or off for this editor (View &gt; Word Wrap).
        ///
        /// <see cref="HelixTextEditorNET.Scintilla.WrapMode"/> used to be an API-compatibility stub that
        /// nothing read; it now drives the port's wrap layout, so this really does re-flow the text,
        /// the scroll range, hit-testing and the caret.
        /// </summary>
        public void SetWordWrap(bool enabled)
        {
            this.WrapMode = enabled ? HelixTextEditorNET.WrapMode.Word : HelixTextEditorNET.WrapMode.None;
            Invalidate();
        }

        private void OnDwellStart(object sender, DwellEventArgs e)
        {
            if (e.Position < 0 || e.Position >= this.TextLength) return;

            // Don't show hover if autocomplete is active or a calltip from '(' is showing
            if (this.AutoCActive) return;

            // A live debug session wins every other tip: while the program is paused, what the
            // expression IS right now is the thing the user stopped the mouse to find out. Asked
            // before the diagnostic and doc hovers so a squiggle on the same word cannot hide it.
            if (!IsInStringOrComment(e.Position) && TryRaiseDebugDataTip(e.Position))
            {
                _lastHoverPos = e.Position;
                return;
            }

            // Diagnostic hover first: dwelling on a squiggled line shows the issue text.
            // Checked before the symbol-doc hover and independent of a word being under the
            // mouse — a squiggle often sits on punctuation or spans a whole statement.
            string diagTip = DiagnosticMessageAt(e.Position);
            if (diagTip != null)
            {
                if (e.Position == _lastHoverPos && this.CallTipActive) return;
                _lastHoverPos = e.Position;
                this.CallTipShow(e.Position, diagTip);
                return;
            }

            if (_hoverProvider == null && _completion == null) return;

            // Skip if inside a string or comment
            if (IsInStringOrComment(e.Position)) return;

            // Get the word under the mouse
            int wordStart = this.WordStartPosition(e.Position, true);
            int wordEnd = this.WordEndPosition(e.Position, true);
            if (wordEnd <= wordStart) return;

            string word = this.GetTextRange(wordStart, wordEnd - wordStart);
            if (string.IsNullOrEmpty(word) || word.Length < 2) return;

            // Don't re-query the same position
            if (e.Position == _lastHoverPos && this.CallTipActive) return;
            _lastHoverPos = e.Position;

            // Calculate 1-based line and 0-based column for jedi
            int line = this.LineFromPosition(e.Position) + 1;
            int lineStart = this.Lines[line - 1].Position;
            int column = e.Position - lineStart;

            string sourceCode = this.Text;
            string filePath = _filePath;

            // Cancel any previous hover query
            if (_hoverWorker != null && _hoverWorker.IsBusy)
            {
                _hoverWorker.CancelAsync();
            }

            int hoverOffset = e.Position;
            _hoverWorker = new BackgroundWorker();
            _hoverWorker.WorkerSupportsCancellation = true;
            BackgroundWorker worker = _hoverWorker;
            _hoverWorker.DoWork += (s2, e2) =>
            {
                // CancelAsync only raises a flag; without this check the work always runs to
                // completion and e2.Cancelled is never true, so the cancel was inert.
                if (worker.CancellationPending) { e2.Cancel = true; return; }
                // FR-KB-05: hover shows the same signature+doc as Quick Info (CUDA runtime/intrinsics with
                // catalog docs, Python solution symbols); fall back to the legacy provider.
                string info = null;
                try { if (_completion != null) info = _completion.GetHoverText(sourceCode, hoverOffset, word); } catch { }
                if (string.IsNullOrEmpty(info))
                    info = _hoverProvider != null ? _hoverProvider.GetHoverInfo(sourceCode, line, column, word, filePath) : null;
                e2.Result = info;
            };
            _hoverWorker.RunWorkerCompleted += (s2, e2) =>
            {
                if (e2.Cancelled || e2.Error != null) return;
                // The hover query is asynchronous, so by the time it returns the mouse may have
                // moved on and OnDwellEnd may already have run. Showing the tip now would put a
                // tooltip on screen that NOTHING will ever hide: DwellEnd has been and gone, and
                // the next one only fires after a new dwell begins. OnDwellEnd resets
                // _lastHoverPos to -1 (and a newer dwell overwrites it), so this equality is
                // exactly the test for "the dwell that asked for this text is still current".
                if (_lastHoverPos != hoverOffset) return;
                string info = e2.Result as string;
                if (!string.IsNullOrEmpty(info) && !this.AutoCActive)
                {
                    try { _completion?.ShowQuickInfoAtPosition(e.Position); } catch { }
                }
            };
            _hoverWorker.RunWorkerAsync();
        }

        /// <summary>
        /// Dismiss a hover tooltip whose context has gone away.
        ///
        /// DwellEnd only fires on real mouse movement inside the control (or MouseLeave), so a tip
        /// that is up when the user Alt-Tabs, scrolls with the wheel, or starts typing has no
        /// path to hide and stays painted over the code. These overrides are that path.
        /// The autocomplete popup is deliberately left alone: it is dismissed by its own key
        /// handling, and cancelling it here would break committing a completion.
        /// </summary>
        private void DismissHoverTip()
        {
            if (this.CallTipActive) this.CallTipCancel();
            try { _completion?.HideDoc(); } catch { }
            RaiseDataTipDismiss();
            _lastHoverPos = -1;
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            DismissHoverTip();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // The tip is anchored to a document position; once the view scrolls it points at
            // whatever text happens to be under it now.
            DismissHoverTip();

            // Ctrl+MouseWheel zooms the font instead of scrolling — the gesture every editor
            // shares. Handled before the base call so the view does not also scroll; marking a
            // HandledMouseEventArgs additionally stops the container from re-dispatching it.
            if (IsZoomModifierPressed())
            {
                int notch = SystemInformation.MouseWheelScrollDelta;
                if (notch <= 0) notch = 120;
                int steps = e.Delta / notch;
                // A high-resolution wheel can report a delta smaller than one notch; still move a
                // point rather than silently ignoring the gesture.
                if (steps == 0 && e.Delta != 0) steps = e.Delta > 0 ? 1 : -1;
                ZoomFontSize(steps);
                var handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
                return;
            }

            base.OnMouseWheel(e);
        }

        private void OnDwellEnd(object sender, DwellEventArgs e)
        {
            if (this.CallTipActive)
            {
                this.CallTipCancel();
            }
            RaiseDataTipDismiss();
            _lastHoverPos = -1;
        }

        /// <summary>
        /// Show the hover tooltip (signature + doc) for the word at <paramref name="position"/>, the same
        /// content the mouse-dwell path shows. Synchronous entry point (used by tests/harness and any
        /// caller that wants to trigger hover programmatically). FR-KB-05.
        /// </summary>
        public void ShowHoverAt(int position)
        {
            if (position < 0 || position >= this.TextLength) return;
            if (IsInStringOrComment(position)) return;
            int wordStart = this.WordStartPosition(position, true);
            int wordEnd = this.WordEndPosition(position, true);
            if (wordEnd <= wordStart) return;
            string word = this.GetTextRange(wordStart, wordEnd - wordStart);
            if (string.IsNullOrEmpty(word) || word.Length < 2) return;

            string info = null;
            try { if (_completion != null) info = _completion.GetHoverText(this.Text, position, word); } catch { }
            if (string.IsNullOrEmpty(info) && _hoverProvider != null)
            {
                int line = this.LineFromPosition(position) + 1;
                int col = position - this.Lines[line - 1].Position;
                info = _hoverProvider.GetHoverInfo(this.Text, line, col, word, _filePath);
            }
            if (!string.IsNullOrEmpty(info) && !this.AutoCActive)
                try { _completion?.ShowQuickInfoAtPosition(position); } catch { }
        }

        // The line-number margin is sized from the rendered width of the widest line number, so it
        // has to be recomputed both when the line count grows a digit and when the font changes
        // size under it (Ctrl+MouseWheel, Preferences) — otherwise a zoomed-in document clips its
        // own line numbers. `force` covers the second case, where the digit count is unchanged.
        /// <summary>
        /// Strip everything that makes this look like an editor WINDOW, for an instance embedded in
        /// a surface that draws its own chrome — today, a notebook code cell.
        ///
        /// <para>
        /// A cell paints its own line numbers in its own gutter and sizes itself to its content.
        /// The editor moving in on a click brought three margins (numbers, breakpoints, folding —
        /// 76px of grey) and two permanent scrollbars with it, so the box visibly changed shape and
        /// its text jumped sideways the moment the caret entered it. None of that chrome has a job
        /// in a cell: there is no folding to do in six lines and nowhere to put a breakpoint.
        /// </para>
        ///
        /// <para>
        /// Set BEFORE the control is sized. It also pins the line-number margin at zero, which is
        /// why <see cref="UpdateLineNumberMarginWidth"/> checks it — that method re-sizes margin 0
        /// on every text change and would put the grey block back on the first keystroke.
        /// </para>
        /// </summary>
        /// <summary>
        /// The paper colour behind the text. White for a document window; a host that draws its own
        /// card sets its card colour so the editor does not show up as a white rectangle inside it.
        ///
        /// <para>
        /// A property rather than a call, because <c>ConfigureSyntaxHighlighting</c> re-applies the
        /// default style on every re-lex and on every font change — anything set once from outside
        /// would be reverted to white the first time the user typed.
        /// </para>
        /// </summary>
        public Color PageBackColor
        {
            get { return _pageBackColor; }
            set
            {
                if (_pageBackColor == value) return;
                _pageBackColor = value;
                // The caret-line band follows it: a near-white highlight on a tinted card reads as
                // a stripe across the cell, which is exactly the shape change this is avoiding.
                this.CaretLineBackColor = value;
                ConfigureSyntaxHighlighting();
            }
        }

        private Color _pageBackColor = Color.White;

        public bool EmbeddedCellMode
        {
            get { return _embeddedCellMode; }
            set
            {
                if (_embeddedCellMode == value) return;
                _embeddedCellMode = value;
                if (!value) return;
                this.Margins[0].Width = 0;
                this.Margins[1].Width = 0;
                this.Margins[2].Width = 0;
                this.HScrollBarVisibility = HelixTextEditorNET.ScrollBarVisibility.WhenNeeded;
                this.VScrollBarVisibility = HelixTextEditorNET.ScrollBarVisibility.WhenNeeded;
                // The 120-column long-line guide is a ruler for a document window. In a cell it is
                // a vertical hairline that appears the moment you click and is not there when you
                // click away — the same "the box changed" report, one pixel wide.
                this.EdgeMode = HelixTextEditorNET.EdgeMode.None;
            }
        }

        private bool _embeddedCellMode;

        private void UpdateLineNumberMarginWidth(bool force)
        {
            if (_embeddedCellMode) { this.Margins[0].Width = 0; return; }
            int maxLength = this.Lines.Count.ToString().Length;
            if (!force && maxLength == _maxLineNumberCharLength) return;
            _maxLineNumberCharLength = maxLength;
            this.Margins[0].Width = this.TextWidth(Style.LineNumber, new string('9', maxLength + 1)) + 2;
        }

        private void OnTextChanged(object sender, EventArgs e)
        {
            UpdateLineNumberMarginWidth(force: false);

            // Real-time syntax diagnostics (FR-15): debounce a re-parse that draws squiggles.
            KickDiagnostics();
        }

        // ---------- Real-time syntax diagnostics (FR-15) ----------
        // A debounced background-free re-parse of the buffer that underlines syntax errors with red
        // squiggles, exactly matching what the actual Python parser reports (no compile required).
        private System.Windows.Forms.Timer _diagTimer;

        private void KickDiagnostics()
        {
            // §8.2: only the parser-only Python re-parse may run unconditionally.
            // The other languages' edit-time diagnostics invoke the CUDA virtual
            // compiler or full linter/analyzer passes, so they run only when the
            // user explicitly enabled background analysis.
            if (!ShouldRunEditTimeDiagnostics(_filePath)) return;
            if (_diagTimer == null)
            {
                _diagTimer = new System.Windows.Forms.Timer { Interval = 400 };
                _diagTimer.Tick += (s, e) => { _diagTimer.Stop(); RunDiagnostics(); };
            }
            _diagTimer.Stop();
            _diagTimer.Start();
        }

        // True when a typing pause in `filePath` may run RunDiagnostics: always for
        // Python (parse-only, no compiler) and for CUDA-C (whose run is downgraded to a
        // parse-only syntax check unless the user opted into background analysis — see
        // RunDiagnostics); everything else only with the explicit opt-in (SRS §8.2).
        // CUDA-C joined the always-on set because typing garbage into a .cu produced NO
        // squiggle at all by default — and the opt-in toggle lives on the Analysis menu,
        // which is hidden for native workspaces, so a C++/CUDA user couldn't even reach it.
        // Public + static for tests.
        public static bool ShouldRunEditTimeDiagnostics(string filePath)
        {
            if (HelixPythonNamespace.Editor.Completion.AnalysisGate.BackgroundAnalysisEnabled) return true;
            string ext = string.IsNullOrEmpty(filePath)
                ? string.Empty
                : (System.IO.Path.GetExtension(filePath) ?? string.Empty).ToLowerInvariant();
            return ext == string.Empty || ext == ".py" || ext == ".pyi" || ext == ".pyw"
                || ext == ".cu" || ext == ".cuh";
        }

        // The always-on edit-time check for .cu/.cuh: syntax (lex/parse) plus
        // include-aware undefined-name analysis — the two classes nvcc always rejects.
        // The CUDA-specific analyses (qualifier/launch/memory/lint) stay behind the
        // §8.2 background-analysis opt-in.
        private static readonly global::HelixPythonNamespace.CudaVirtualCompiler.CudaCompileOptions CudaEditTimeOptions =
            new global::HelixPythonNamespace.CudaVirtualCompiler.CudaCompileOptions
            {
                EnableLexParse = true,
                EnableAstValidator = false,
                EnableSymtablePass = true,
                EnableQualifierSync = false,
                EnableLaunchConfig = false,
                EnableMemorySpace = false,
                EnableLint = false,
                EnableLanguageStandard = false,
                EnableNameResolution = true,
                EnableIncludeResolution = true,
            };

        // The full pass set for the §8.2 opt-in — like the default options, plus the
        // include resolution the edit-time path uses (cross-file symbols).
        private static readonly global::HelixPythonNamespace.CudaVirtualCompiler.CudaCompileOptions CudaFullAnalysisOptions =
            new global::HelixPythonNamespace.CudaVirtualCompiler.CudaCompileOptions
            {
                EnableIncludeResolution = true,
            };

        private void RunDiagnostics()
        {
            try
            {
                string ext = string.IsNullOrEmpty(_filePath) ? null : System.IO.Path.GetExtension(_filePath);
                string source = this.Text ?? string.Empty;

                // CUDA-C files: re-analyze with the CUDA virtual compiler (lex/parse + structural +
                // symtable + qualifier/launch/memory/lint passes) and underline its Error diagnostics.
                if (string.Equals(ext, ".cu", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".cuh", StringComparison.OrdinalIgnoreCase))
                {
                    // Full pass set only with the §8.2 opt-in; otherwise syntax +
                    // include-aware undefined names — what nvcc always rejects —
                    // squiggle out of the box.
                    var cudaOpts = HelixPythonNamespace.Editor.Completion.AnalysisGate.BackgroundAnalysisEnabled
                        ? CudaFullAnalysisOptions : CudaEditTimeOptions;
                    var cuda = global::HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler.CompileSource(source, _filePath, cudaOpts);
                    ClearErrorMarkers();
                    if (cuda == null || cuda.Diagnostics == null) { ApplyBuildErrors(); return; }
                    int cn = 0;
                    foreach (var d in cuda.Diagnostics)
                    {
                        if (d.Severity != global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error) continue;
                        MarkError(d.Span.Start.Line, d.Span.Start.Column, d.Message);
                        if (++cn >= 200) break;
                    }
                    ApplyBuildErrors();
                    return;
                }

                // CMakeLists.txt / *.cmake: run the native CMake linter + analyzer (FR-LINT-04/FR-ANA-05)
                // and underline their Error-severity findings live (FR-CORE-09 gives the colorization).
                if (IsCMakeFilePath(_filePath))
                {
                    ClearErrorMarkers();
                    int cm = 0;
                    var lint = global::HelixPythonNamespace.Workspace.Cuda.CMakeLinter.Lint(source);
                    foreach (var f in lint)
                    {
                        if (f.Severity != global::HelixPythonNamespace.Workspace.Cuda.CMakeLintSeverity.Error) continue;
                        MarkError(f.Line, f.Column, f.Code + ": " + f.Message);
                        if (++cm >= 200) break;
                    }
                    var ana = global::HelixPythonNamespace.Workspace.Cuda.CMakeAnalyzer.Analyze(source);
                    foreach (var f in ana.Findings)
                    {
                        if (f.Severity != global::HelixPythonNamespace.Workspace.Cuda.CMakeLintSeverity.Error) continue;
                        MarkError(f.Line, f.Column, f.Code + ": " + f.Message);
                        if (++cm >= 200) break;
                    }
                    ApplyBuildErrors();
                    return;
                }

                // *.toml / pyproject.toml: run the native TOML linter + analyzer (FR-LINT-03/FR-ANA-04)
                // and underline their Error-severity findings live.
                if (IsTomlFilePath(_filePath))
                {
                    ClearErrorMarkers();
                    int tn = 0;
                    var doc = global::HelixPythonNamespace.Workspace.Toml.TomlParser.Parse(source);
                    var lint = global::HelixPythonNamespace.Workspace.Toml.TomlLinter.Lint(doc, source, _filePath);
                    string baseDir = null; try { baseDir = System.IO.Path.GetDirectoryName(_filePath); } catch { }
                    var ana = global::HelixPythonNamespace.Workspace.Toml.TomlAnalyzer.Analyze(doc, baseDir);
                    foreach (var f in lint)
                    {
                        if (f.Severity != global::HelixPythonNamespace.Workspace.Toml.TomlLintSeverity.Error) continue;
                        MarkError(f.Line, f.Column, f.Code + ": " + f.Message);
                        if (++tn >= 200) break;
                    }
                    foreach (var f in ana)
                    {
                        if (f.Severity != global::HelixPythonNamespace.Workspace.Toml.TomlLintSeverity.Error) continue;
                        MarkError(f.Line, f.Column, f.Code + ": " + f.Message);
                        if (++tn >= 200) break;
                    }
                    ApplyBuildErrors();
                    return;
                }





                // Python-family files: re-parse with the real Python parser.
                bool isPython = ext == null
                    || string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".pyi", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".pyw", StringComparison.OrdinalIgnoreCase);
                if (!isPython) { ClearErrorMarkers(); ApplyBuildErrors(); return; }

                var result = global::HelixPythonNamespace.Python.Python.ParseModule(source, _filePath);
                ClearErrorMarkers();
                if (result == null || result.Diagnostics == null) { ApplyBuildErrors(); return; }

                int shown = 0;
                foreach (var d in result.Diagnostics)
                {
                    if (d.Severity != global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error) continue;
                    MarkError(d.Span.Start.Line, d.Span.Start.Column, d.Message);
                    if (++shown >= 200) break;   // guard against pathological error storms
                }
                ApplyBuildErrors();
            }
            catch { /* diagnostics are best-effort; never disrupt editing */ }
        }

        private void OnInsertCheck(object sender, InsertCheckEventArgs e)
        {
            // Auto-indent on Enter
            if (e.Text.EndsWith("\n") || e.Text.EndsWith("\r\n"))
            {
                int lineIndex = this.LineFromPosition(e.Position);
                string lineText = this.Lines[lineIndex].Text.TrimEnd('\r', '\n');
                int caretColumn = e.Position - this.Lines[lineIndex].Position;
                e.Text += ComputeAutoIndent(lineText, caretColumn);
            }
        }

        /// <summary>
        /// The indentation to append after a newline inserted at <paramref name="caretColumn"/>
        /// of <paramref name="lineText"/>. Derived from the text BEFORE the caret only: pressing
        /// Enter at the start (or inside the leading whitespace) of an existing line must move
        /// that line down UNCHANGED — the old whole-line version saw the trailing ':' of
        /// "class X:" even with the caret at column 0 and pushed the class 4 columns right per
        /// press, progressively mangling the file.
        /// </summary>
        internal static string ComputeAutoIndent(string lineText, int caretColumn)
        {
            if (lineText == null) return string.Empty;
            if (caretColumn < 0) caretColumn = 0;
            if (caretColumn > lineText.Length) caretColumn = lineText.Length;
            string prefix = lineText.Substring(0, caretColumn);

            string indent = "";
            foreach (char c in prefix)
            {
                if (c == ' ' || c == '\t') indent += c;
                else break;
            }

            // Extra level only when the caret actually sits after a block-opening colon.
            if (prefix.TrimEnd().EndsWith(":")) indent += "    ";
            else if (lineText.Substring(caretColumn).Trim().Length == 0 && IsTerminalStatement(prefix))
                indent = DedentOneLevel(indent);
            return indent;
        }

        private static bool IsTerminalStatement(string text)
        {
            string statement = (text ?? string.Empty).TrimStart();
            foreach (string keyword in new[] { "return", "raise", "break", "continue", "pass" })
            {
                if (string.Equals(statement, keyword, StringComparison.Ordinal) ||
                    statement.StartsWith(keyword + " ", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static string DedentOneLevel(string indent)
        {
            if (string.IsNullOrEmpty(indent)) return string.Empty;
            if (indent[indent.Length - 1] == '\t') return indent.Substring(0, indent.Length - 1);
            int remove = Math.Min(4, indent.Length);
            while (remove > 0 && indent[indent.Length - remove] != ' ') remove--;
            return remove > 0 ? indent.Substring(0, indent.Length - remove) : indent;
        }

        private void OnCharAdded(object sender, CharAddedEventArgs e)
        {
            char c = (char)e.Char;

            // Manually typed opening delimiters remain exactly as entered. Snippets and
            // callable completion may still insert balanced syntax deliberately.

            // Route identifier / dot characters through the new completion controller.
            // (The legacy debounce timer is kept disabled — left in place to avoid churn
            // in downstream callers that still reference _autoComplete.GetCallTip.)
            // A hover tip describes what the MOUSE was over; once the user types, it is stale.
            if (this.CallTipActive && _lastHoverPos >= 0) DismissHoverTip();

            try { _completion?.OnCharAdded(c); } catch { }

            // Show call tip on '(' and re-emphasize the current argument on ',' (FR-KB-03).
            if (c == '(')
            {
                ShowCallTip();
            }
            else if (c == ',')
            {
                ShowCallTip();
            }
            else if (c == ')')
            {
                if (this.CallTipActive)
                    this.CallTipCancel();
            }
        }

        private void InsertTextAtCaret(string text)
        {
            int pos = this.CurrentPosition;
            this.InsertText(pos, text);
            this.GotoPosition(pos); // Keep caret before the inserted text
        }

        private void OnUpdateUI(object sender, UpdateUIEventArgs e)
        {
            if (e.Change.HasFlag(UpdateChange.Selection))
            {
                // Brace matching
                int pos = this.CurrentPosition;
                if (pos > 0 && IsBrace(this.GetCharAt(pos - 1)))
                {
                    int match = this.BraceMatch(pos - 1);
                    if (match != Scintilla.InvalidPosition)
                        this.BraceHighlight(pos - 1, match);
                    else
                        this.BraceBadLight(pos - 1);
                }
                else if (IsBrace(this.GetCharAt(pos)))
                {
                    int match = this.BraceMatch(pos);
                    if (match != Scintilla.InvalidPosition)
                        this.BraceHighlight(pos, match);
                    else
                        this.BraceBadLight(pos);
                }
                else
                {
                    this.BraceHighlight(Scintilla.InvalidPosition, Scintilla.InvalidPosition);
                }

                // Any caret movement dismisses the quick-info doc panel.
                try { _completion?.HideDoc(); } catch { }

                // Notify listeners (status-bar Ln/Col readout) of the new caret position.
                Action<int, int> cm = CaretMoved;
                if (cm != null)
                {
                    try { cm(this.LineFromPosition(pos) + 1, this.GetColumn(pos) + 1); } catch { }
                }
            }
        }

        private void OnMarginClick(object sender, MarginClickEventArgs e)
        {
            if (e.Margin == 1)
            {
                // Toggle breakpoint
                int lineIndex = this.LineFromPosition(e.Position);
                int lineNumber = lineIndex + 1; // 1-based for DAP
                var mask = this.Lines[lineIndex].MarkerGet();
                if ((mask & (1 << MARKER_BREAKPOINT)) != 0)
                {
                    this.Lines[lineIndex].MarkerDelete(MARKER_BREAKPOINT);
                    this.Lines[lineIndex].MarkerDelete(MARKER_BREAKPOINT_BG);
                }
                else
                {
                    this.Lines[lineIndex].MarkerAdd(MARKER_BREAKPOINT);
                    this.Lines[lineIndex].MarkerAdd(MARKER_BREAKPOINT_BG);
                }

                var handler = BreakpointToggled;
                if (handler != null) handler(this, lineNumber);
            }
            else if (e.Margin == 2)
            {
                // Toggle fold
                int line = this.LineFromPosition(e.Position);
                this.Lines[line].ToggleFold();
            }
        }

        /// <summary>
        /// Whether the given line (1-based) currently shows the red breakpoint margin marker.
        /// Lets callers/tests confirm the margin reflects the BreakpointManager (e.g. after a
        /// restored breakpoint is synced on file open).
        /// </summary>
        public bool HasBreakpointMarker(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > this.Lines.Count) return false;
            return (this.Lines[lineNumber - 1].MarkerGet() & (1 << MARKER_BREAKPOINT)) != 0;
        }

        /// <summary>
        /// Sets a breakpoint marker on the given line (1-based).
        /// </summary>
        public void SetBreakpointMarker(int lineNumber, bool enabled)
        {
            if (lineNumber < 1 || lineNumber > this.Lines.Count) return;
            int lineIndex = lineNumber - 1;
            if (enabled)
            {
                this.Lines[lineIndex].MarkerAdd(MARKER_BREAKPOINT);
                this.Lines[lineIndex].MarkerAdd(MARKER_BREAKPOINT_BG);
            }
            else
            {
                this.Lines[lineIndex].MarkerDelete(MARKER_BREAKPOINT);
                this.Lines[lineIndex].MarkerDelete(MARKER_BREAKPOINT_BG);
            }
        }

        /// <summary>
        /// Renders a breakpoint on the given line (1-based) reflecting its ENABLED state: a solid red
        /// dot + line highlight when enabled, or a greyed circle (no highlight) when disabled — the VS
        /// convention. Clears any prior breakpoint markers on the line first.
        /// </summary>
        public void ShowBreakpoint(int lineNumber, bool isEnabled)
        {
            if (lineNumber < 1 || lineNumber > this.Lines.Count) return;
            var line = this.Lines[lineNumber - 1];
            line.MarkerDelete(MARKER_BREAKPOINT);
            line.MarkerDelete(MARKER_BREAKPOINT_BG);
            line.MarkerDelete(MARKER_BREAKPOINT_DISABLED);
            if (isEnabled)
            {
                line.MarkerAdd(MARKER_BREAKPOINT);
                line.MarkerAdd(MARKER_BREAKPOINT_BG);
            }
            else
            {
                line.MarkerAdd(MARKER_BREAKPOINT_DISABLED);
            }
        }

        /// <summary>
        /// Toggles the breakpoint marker on the given line (1-based) and fires the BreakpointToggled event.
        /// Used when toggling via keyboard (F9) rather than margin click.
        /// </summary>
        public void ToggleBreakpointAtLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > this.Lines.Count) return;
            int lineIndex = lineNumber - 1;
            var mask = this.Lines[lineIndex].MarkerGet();
            if ((mask & (1 << MARKER_BREAKPOINT)) != 0)
            {
                this.Lines[lineIndex].MarkerDelete(MARKER_BREAKPOINT);
                this.Lines[lineIndex].MarkerDelete(MARKER_BREAKPOINT_BG);
            }
            else
            {
                this.Lines[lineIndex].MarkerAdd(MARKER_BREAKPOINT);
                this.Lines[lineIndex].MarkerAdd(MARKER_BREAKPOINT_BG);
            }

            var handler = BreakpointToggled;
            if (handler != null) handler(this, lineNumber);
        }

        /// <summary>
        /// Sets the current execution line marker (yellow arrow). Pass 0 to clear.
        /// </summary>
        public void SetCurrentLineMarker(int lineNumber)
        {
            // Clear all current line markers first
            for (int i = 0; i < this.Lines.Count; i++)
            {
                this.Lines[i].MarkerDelete(MARKER_CURRENT_LINE);
                this.Lines[i].MarkerDelete(MARKER_CURRENT_LINE_BG);
            }

            if (lineNumber >= 1 && lineNumber <= this.Lines.Count)
            {
                int lineIndex = lineNumber - 1;
                this.Lines[lineIndex].MarkerAdd(MARKER_CURRENT_LINE);
                this.Lines[lineIndex].MarkerAdd(MARKER_CURRENT_LINE_BG);
                this.Lines[lineIndex].EnsureVisible();
                this.Lines[lineIndex].Goto();
                this.ScrollCaret();
            }
        }

        /// <summary>
        /// Clears all current-line (debug) markers.
        /// </summary>
        public void ClearCurrentLineMarker()
        {
            for (int i = 0; i < this.Lines.Count; i++)
            {
                this.Lines[i].MarkerDelete(MARKER_CURRENT_LINE);
                this.Lines[i].MarkerDelete(MARKER_CURRENT_LINE_BG);
            }
        }

        private void AutoCompleteTimer_Tick(object sender, EventArgs e)
        {
            _autoCompleteTimer.Stop();
            ShowAutoComplete();
        }

        /// <summary>
        /// Triggers the auto-complete list to show.
        /// </summary>
        public void ShowAutoComplete()
        {
            int currentPos = this.CurrentPosition;
            int wordStart = this.WordStartPosition(currentPos, true);
            string currentWord = this.GetTextRange(wordStart, currentPos - wordStart);

            if (currentWord.Length < 1)
                return;

            var suggestions = _autoComplete.GetSuggestions(this.Text, currentPos, currentWord);
            if (suggestions.Count > 0)
            {
                string list = string.Join(" ", suggestions);
                this.AutoCShow(currentWord.Length, list);
            }
        }

        /// <summary>
        /// Shows a call tip for the function at the current position.
        /// </summary>
        public void ShowCallTip()
        {
            // Find the open '(' of the innermost call enclosing the caret, and the function name before it.
            int caret = this.CurrentPosition;
            int open = FindEnclosingCallOpenParen(caret);
            if (open < 0) return;

            int funcEnd = open;
            while (funcEnd > 0 && this.GetCharAt(funcEnd - 1) == ' ') funcEnd--;
            int funcStart = this.WordStartPosition(funcEnd, true);
            string funcName = this.GetTextRange(funcStart, funcEnd - funcStart);
            if (string.IsNullOrEmpty(funcName)) return;

            // Prefer the active completion engine's signature (CUDA runtime/intrinsics + Python symbols);
            // fall back to the small built-in lookup.
            string tip = null;
            try { if (_completion != null) tip = _completion.GetCallTip(funcName); } catch { }
            if (string.IsNullOrEmpty(tip)) tip = _autoComplete.GetCallTip(funcName);
            if (string.IsNullOrEmpty(tip)) return;

            this.CallTipShow(open, tip);

            // FR-KB-03 / Parameter Info: emphasize the current argument by counting the top-level commas
            // between the '(' and the caret, then highlighting that parameter's span in the signature.
            int argIndex = CountTopLevelCommas(open + 1, caret);
            int hlStart, hlEnd;
            if (CallTipHighlighter.TryGetParamSpan(tip, argIndex, out hlStart, out hlEnd))
            {
                try
                {
                    this.CallTipSetForeHlt(Color.FromArgb(0, 0, 255)); // current-arg emphasis (bold blue)
                    this.CallTipSetHlt(hlStart, hlEnd);
                }
                catch { }
            }
        }

        // Position of the '(' that opens the innermost still-open call at or before `caret`, or -1.
        private int FindEnclosingCallOpenParen(int caret)
        {
            int depth = 0;
            for (int p = caret - 1; p >= 0; p--)
            {
                char c = (char)this.GetCharAt(p);
                if (c == ')') depth++;
                else if (c == '(')
                {
                    if (depth == 0) return p;
                    depth--;
                }
                else if (c == ';' || c == '{' || c == '}') break; // don't cross statement boundaries
            }
            return -1;
        }

        // Count top-level (depth-0) commas in [from, to) — the current argument index.
        private int CountTopLevelCommas(int from, int to)
        {
            int depth = 0, count = 0;
            for (int p = from; p < to && p < this.TextLength; p++)
            {
                char c = (char)this.GetCharAt(p);
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0) count++;
            }
            return count;
        }

        /// <summary>
        /// Clears all error markers and indicators.
        /// </summary>
        /// <summary>A build diagnostic to keep pinned on the editor across debounced re-parses.</summary>
        public sealed class BuildErrorMark
        {
            public int Line;
            public int Column;
            public string Message;
        }

        private readonly System.Collections.Generic.List<BuildErrorMark> _buildErrors =
            new System.Collections.Generic.List<BuildErrorMark>();

        /// <summary>
        /// FR-PM-04: pin external build errors (from the real cmake/nvcc build) onto the editor as gutter
        /// markers + squiggles. Unlike <see cref="MarkError"/>, these survive the debounced real-time
        /// re-parse — which would otherwise clear all markers for a non-Python/non-CUDA file (e.g. `.cpp`).
        /// Cleared by <see cref="ClearBuildErrors"/> (call on a successful rebuild or when the user edits).
        /// </summary>
        public void SetBuildErrors(System.Collections.Generic.IEnumerable<BuildErrorMark> errors)
        {
            _buildErrors.Clear();
            if (errors != null) _buildErrors.AddRange(errors);
            ApplyBuildErrors();
        }

        public void ClearBuildErrors()
        {
            _buildErrors.Clear();
            ClearErrorMarkers();
        }

        private void ApplyBuildErrors()
        {
            for (int i = 0; i < _buildErrors.Count; i++)
            {
                var b = _buildErrors[i];
                MarkError(b.Line, b.Column, b.Message);
            }
        }

        public void ClearErrorMarkers()
        {
            _errorTips.Clear();

            // Clear marker margin
            for (int i = 0; i < this.Lines.Count; i++)
            {
                this.Lines[i].MarkerDelete(1);
            }

            // Clear squiggly indicators
            this.IndicatorCurrent = 0;
            this.IndicatorClearRange(0, this.TextLength);
        }

        /// <summary>
        /// Marks a line with an error indicator.
        /// </summary>
        // Messages behind the marks currently on screen, so hovering a squiggled line can
        // show WHAT the problem is (the marks themselves carry no text). Rebuilt on every
        // diagnostics pass: ClearErrorMarkers empties it, each MarkError appends.
        private readonly System.Collections.Generic.List<BuildErrorMark> _errorTips =
            new System.Collections.Generic.List<BuildErrorMark>();

        /// <summary>The diagnostic message(s) on the line containing <paramref name="position"/>,
        /// newline-joined, or null when the line has no error mark. Public for tests and the
        /// dwell-hover path.</summary>
        public string DiagnosticMessageAt(int position)
        {
            if (_errorTips.Count == 0 || position < 0 || position > this.TextLength) return null;
            int line = this.LineFromPosition(position) + 1;
            System.Text.StringBuilder sb = null;
            for (int i = 0; i < _errorTips.Count; i++)
            {
                if (_errorTips[i].Line != line) continue;
                string msg = _errorTips[i].Message ?? string.Empty;
                if (sb == null) sb = new System.Text.StringBuilder(msg);
                else if (sb.ToString().IndexOf(msg, StringComparison.Ordinal) < 0)
                    sb.Append('\n').Append(msg);
            }
            return sb != null ? sb.ToString() : null;
        }

        public void MarkError(int lineNumber, int column, string message)
        {
            if (lineNumber < 1 || lineNumber > this.Lines.Count) return;

            _errorTips.Add(new BuildErrorMark { Line = lineNumber, Column = column, Message = message ?? string.Empty });

            int lineIndex = lineNumber - 1;

            // Red error marker in the gutter (distinct from the breakpoint marker).
            this.Lines[lineIndex].MarkerAdd(MARKER_ERROR);

            // Red squiggle indicator.
            this.IndicatorCurrent = 0;
            this.Indicators[0].Style = IndicatorStyle.Squiggle;
            this.Indicators[0].ForeColor = Color.Red;
            this.Indicators[0].Under = true;

            int startPos = this.Lines[lineIndex].Position;
            int endPos = this.Lines[lineIndex].EndPosition;
            string lineText = this.Lines[lineIndex].Text;

            // Underline the line's non-whitespace CONTENT with the red squiggle. Underlining just a
            // 1-char token (as a precise column would give) is an invisible sliver; the exact line is
            // already flagged by the gutter marker, so a full-content underline reads clearly as an
            // error the way VS's does.
            int indent = 0;
            foreach (char c in lineText)
            {
                if (c == ' ' || c == '\t') indent++;
                else break;
            }
            int contentEnd = endPos;
            while (contentEnd > startPos + indent && char.IsWhiteSpace((char)this.GetCharAt(contentEnd - 1)))
                contentEnd--;
            int len = Math.Max(1, contentEnd - (startPos + indent));
            this.IndicatorFillRange(startPos + indent, len);
        }

        /// <summary>
        /// Runs the REAL nvcc toolchain over this <c>.cu</c> file (via <c>NvccVerifier</c>) and marks its
        /// authoritative errors in the gutter + squiggle — the CUDA analogue of running real CPython.
        /// Blocks for the duration of the nvcc compile (seconds), so callers should show a wait cursor.
        /// Returns the nvcc error count, or -1 (not a .cu / unsaved) / -2 (nvcc unavailable).
        /// </summary>
        public int VerifyWithNvcc(out string summary)
        {
            summary = null;
            if (string.IsNullOrEmpty(_filePath) || !System.IO.File.Exists(_filePath)) { summary = "save the .cu file first"; return -1; }
            string ext = System.IO.Path.GetExtension(_filePath);
            if (!string.Equals(ext, ".cu", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ext, ".cuh", StringComparison.OrdinalIgnoreCase)) { summary = "not a CUDA (.cu) file"; return -1; }

            global::HelixPythonNamespace.CudaVirtualCompiler.NvccVerifyResult r =
                global::HelixPythonNamespace.CudaVirtualCompiler.NvccVerifier.Verify(_filePath);
            if (!r.Available) { summary = "nvcc unavailable: " + (r.Reason ?? "unknown"); return -2; }

            ClearErrorMarkers();
            foreach (global::HelixPythonNamespace.CudaVirtualCompiler.NvccDiagnostic d in r.Diagnostics)
                if (d.IsError) MarkError(d.Line, d.Column, "nvcc: " + d.Message);

            summary = string.Format("nvcc {0}: {1} error(s), {2} warning(s)",
                r.NvccVersion ?? "?", r.ErrorCount, r.WarningCount);
            return r.ErrorCount;
        }

        private bool IsBrace(int charCode)
        {
            char c = (char)charCode;
            return c == '(' || c == ')' || c == '[' || c == ']' || c == '{' || c == '}';
        }

        private bool IsInStringOrComment(int position)
        {
            int style = this.GetStyleAt(position);
            return style == Style.Python.String ||
                   style == Style.Python.Character ||
                   style == Style.Python.Triple ||
                   style == Style.Python.TripleDouble ||
                   style == Style.Python.CommentLine ||
                   style == Style.Python.CommentBlock ||
                   style == Style.Python.StringEol;
        }

        /// <summary>
        /// Goes to a specific line number (1-based).
        /// </summary>
        public void GoToLine(int lineNumber)
        {
            if (lineNumber < 1) lineNumber = 1;
            if (lineNumber > this.Lines.Count) lineNumber = this.Lines.Count;

            _blockScope?.FlushPendingRecompute();
            int lineIndex = lineNumber - 1;
            this.Lines[lineIndex].Goto();
            this.Lines[lineIndex].EnsureVisible();
            this.ScrollCaret();
            CentreCaretLine();
        }

        /// <summary>
        /// Like <see cref="GoToLine"/>, but places the caret at a specific 1-based <paramref name="column"/>
        /// on that line instead of the line start. Used when jumping to a diagnostic, a Find hit, or a
        /// reference that carries a column — a professional IDE lands the caret on the exact token, not
        /// at column 1. A column past the line's end is clamped to the end of that line so the caret can
        /// never spill onto the next line.
        /// </summary>
        public void GoToLineColumn(int lineNumber, int column)
        {
            if (column <= 1) { GoToLine(lineNumber); return; }
            if (lineNumber < 1) lineNumber = 1;
            if (lineNumber > this.Lines.Count) lineNumber = this.Lines.Count;

            _blockScope?.FlushPendingRecompute();
            int lineIndex = lineNumber - 1;
            int lineStart = this.Lines[lineIndex].Position;
            // Clamp to the last character position ON this line (before its EOL), so an over-long
            // column can never spill the caret onto the next line. Lines[i].Text includes the line
            // ending, so trim it before measuring.
            int lineTextLen = (this.Lines[lineIndex].Text ?? string.Empty).TrimEnd('\r', '\n').Length;
            int maxOnLine = lineStart + lineTextLen;
            int target = lineStart + (column - 1);
            if (target > maxOnLine) target = maxOnLine;
            if (target < lineStart) target = lineStart;

            this.GotoPosition(target);
            this.Lines[lineIndex].EnsureVisible();
            this.ScrollCaret();
            CentreCaretLine();
        }

        /// <summary>
        /// Puts the caret's line near the top of the viewport (a few lines of leading context, not
        /// dead center).
        ///
        /// ScrollCaret alone scrolls the bare minimum, so a jump forward — Go to Definition, a Find
        /// All References hit, a Problems-panel entry — lands the target on the LAST visible row,
        /// with the code it belongs to entirely off-screen below it (iter 347 screenshot: a `def`
        /// half-clipped by the bottom edge). This lands the target near the top instead, so the
        /// rest of the file below it is what fills the viewport — the user asked for this over the
        /// earlier "a third of the way down" placement.
        ///
        /// Works in display lines, so folded and wrapped regions above the target are accounted for.
        /// </summary>
        private void CentreCaretLine()
        {
            try
            {
                int onScreen = this.LinesOnScreen;
                if (onScreen <= 2) return;   // too small a viewport to reposition meaningfully

                const int topPadding = 3;   // a couple of lines of context above the target
                int caretDisplayLine = this.Lines[this.CurrentLine].DisplayIndex;
                int desiredFirst = caretDisplayLine - topPadding;
                if (desiredFirst < 0) desiredFirst = 0;   // near the top of the file, show the top
                this.FirstVisibleLine = desiredFirst;
            }
            catch { /* positioning is a nicety — never let it break navigation */ }
        }

        // The line-comment token for the file's language: "//" for C/C++/CUDA/JS/TS,
        // "#" for Python/CMake/TOML/shell (the default).
        internal string LineCommentPrefix()
        {
            string ext = string.IsNullOrEmpty(_filePath)
                ? string.Empty
                : System.IO.Path.GetExtension(_filePath).ToLowerInvariant();
            switch (ext)
            {
                case ".c": case ".h":
                case ".cpp": case ".hpp": case ".cc": case ".cxx": case ".hxx":
                case ".cu": case ".cuh":
                case ".js": case ".ts":
                    return "//";
                default:
                    return "#";
            }
        }

        /// <summary>Move the current line (or all selected lines) up one line (Alt+Up).</summary>
        public void MoveLinesUp() { this.ExecuteCmd(HelixTextEditorNET.Command.MoveSelectedLinesUp); }

        /// <summary>Move the current line (or all selected lines) down one line (Alt+Down).</summary>
        public void MoveLinesDown() { this.ExecuteCmd(HelixTextEditorNET.Command.MoveSelectedLinesDown); }

        /// <summary>Duplicate the selection, or the current line when nothing is selected (Ctrl+D).</summary>
        public void DuplicateSelection() { this.ExecuteCmd(HelixTextEditorNET.Command.SelectionDuplicate); }

        /// <summary>Collapse every foldable region (classes, functions, blocks) — VS Code's Fold All.</summary>
        public void FoldAll() { try { this.FoldAll(FoldAction.Contract); } catch { } }

        /// <summary>Expand every folded region — VS Code's Unfold All.</summary>
        public void UnfoldAll() { try { this.FoldAll(FoldAction.Expand); } catch { } }

        /// <summary>
        /// Comments or uncomments the selected lines using the file's line-comment token. If every
        /// non-blank line is already commented the block is uncommented; otherwise it is commented.
        /// </summary>
        public void ToggleComment()
        {
            string prefix = LineCommentPrefix();
            int startLine = this.LineFromPosition(this.SelectionStart);
            int endLine = this.LineFromPosition(this.SelectionEnd);

            // Uncomment only when every non-blank line already starts with the comment token.
            bool allCommented = true;
            for (int i = startLine; i <= endLine; i++)
            {
                string text = this.Lines[i].Text.TrimStart();
                if (!string.IsNullOrEmpty(text) && !text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    allCommented = false;
                    break;
                }
            }

            this.BeginUndoAction();
            try
            {
                // Positions are re-read from Lines[i] each pass, so they stay correct as edits shift text.
                for (int i = startLine; i <= endLine; i++)
                {
                    string lineText = this.Lines[i].Text;
                    if (allCommented)
                    {
                        int pos = lineText.IndexOf(prefix, StringComparison.Ordinal);
                        if (pos >= 0)
                        {
                            this.DeleteRange(this.Lines[i].Position + pos, prefix.Length);
                            // Remove a single following space if present (mirrors what commenting adds).
                            if (pos + prefix.Length < lineText.Length && lineText[pos + prefix.Length] == ' ')
                                this.DeleteRange(this.Lines[i].Position + pos, 1);
                        }
                    }
                    else
                    {
                        // Don't comment blank lines.
                        if (!string.IsNullOrWhiteSpace(lineText.TrimEnd('\r', '\n')))
                            this.InsertText(this.Lines[i].Position, prefix + " ");
                    }
                }
            }
            finally
            {
                this.EndUndoAction();
            }
        }

        /// <summary>
        /// Increases the indentation of selected lines.
        /// </summary>
        public void IndentLines()
        {
            int startLine = this.LineFromPosition(this.SelectionStart);
            int endLine = this.LineFromPosition(this.SelectionEnd);

            this.BeginUndoAction();
            try
            {
                for (int i = startLine; i <= endLine; i++)
                {
                    this.InsertText(this.Lines[i].Position, "    ");
                }
            }
            finally
            {
                this.EndUndoAction();
            }
        }

        /// <summary>
        /// Decreases the indentation of selected lines.
        /// </summary>
        public void UnindentLines()
        {
            int startLine = this.LineFromPosition(this.SelectionStart);
            int endLine = this.LineFromPosition(this.SelectionEnd);

            this.BeginUndoAction();
            try
            {
                for (int i = startLine; i <= endLine; i++)
                {
                    string text = this.Lines[i].Text;
                    int pos = this.Lines[i].Position;
                    int toRemove = 0;
                    for (int j = 0; j < Math.Min(4, text.Length); j++)
                    {
                        if (text[j] == ' ') toRemove++;
                        else if (text[j] == '\t') { toRemove++; break; }
                        else break;
                    }
                    if (toRemove > 0)
                        this.DeleteRange(pos, toRemove);
                }
            }
            finally
            {
                this.EndUndoAction();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _autoCompleteTimer.Dispose();
                // §9: the diagnostics debounce timer's Tick closure roots this
                // control (and through it the SolutionModel); left undisposed it
                // strands one solution per closed tab.
                if (_diagTimer != null)
                {
                    try { _diagTimer.Stop(); _diagTimer.Dispose(); } catch { }
                    _diagTimer = null;
                }
                try { _completion?.Dispose(); } catch { }
                _completion = null;
                try { _blockScope?.Dispose(); } catch { }
                _blockScope = null;
                try { _typeMarkers?.Dispose(); } catch { }
                _typeMarkers = null;
            }
            base.Dispose(disposing);
        }

        private VirtualTypeMarkerRenderer _typeMarkers;

        /// <summary>
        /// §3.2/§3.4 toggle: true renders virtual C#-style braces; false renders the
        /// Bézier scope connectors instead (the SRS pairs the two states).
        /// </summary>
        public bool VirtualBracesEnabled
        {
            get { return _blockScope != null && _blockScope.Mode == BlockOverlayMode.VirtualBraces; }
            set
            {
                if (_blockScope == null) return;
                _blockScope.Mode = value ? BlockOverlayMode.VirtualBraces : BlockOverlayMode.BezierConnectors;
                Invalidate();
            }
        }

        /// <summary>§3.3 toggle: virtual inferred-type markers on assignments.</summary>
        public bool TypeMarkersEnabled
        {
            get { return _typeMarkersEnabled; }
            set
            {
                _typeMarkersEnabled = value;
                Invalidate();
            }
        }

        private bool _typeMarkersEnabled = true;

        // Paint overlays into Scintilla's own OptimizedDoubleBuffer frame. This avoids
        // exposing an undecorated frame before a second Graphics.FromHwnd pass.
        // Caret blinks, selections, and scrolling are rendered atomically with overlays;
        // repeated paints therefore cannot alternate between two visual states.
        // didn't actually repaint — without the clip, the tints compound on every
        // small paint and darken within seconds.
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_blockScope == null || e == null || e.Graphics == null) return;
            try
            {
                // Must read the update rect BEFORE base.WndProc — Scintilla's
                // BeginPaint validates (clears) the region as part of its paint.
                _blockScope.DrawOverlay(e.Graphics);
                if (_typeMarkersEnabled && _typeMarkers != null) _typeMarkers.DrawOverlay(e.Graphics);
            }
            catch { /* tear-down window or transient paint failure */ }
        }
    }
}
