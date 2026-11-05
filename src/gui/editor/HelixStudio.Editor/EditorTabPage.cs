using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace HelixPythonNamespace.Editor
{
    /// <summary>
    /// Represents a tab page containing a Python editor for a single file.
    /// </summary>
    public class EditorTabPage : TabPage
    {
        private readonly PythonEditorControl _editor;

        /// <summary>Turn word wrap on or off for this tab's editor.</summary>
        public void SetWordWrap(bool enabled)
        {
            if (_editor != null) _editor.SetWordWrap(enabled);
        }
        private readonly FindReplaceBar _findBar;
        private readonly NavigationBar _navBar;
        private Panel _body;
        private Markdown.MarkdownViewControl _markdownView;
        private Panel _markdownBar;
        private Button _previewButton;
        private Button _sourceButton;
        private Timer _previewTimer;
        private bool _showingPreview;
        private string _filePath;
        private bool _isModified;
        private string _originalContent;
        private bool _isPinned;
        private bool _isReadOnly;
        private readonly Timer _autosaveTimer;

        // UTF-8 without BOM – Python files must not have a BOM
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public PythonEditorControl Editor { get { return _editor; } }

        /// <summary>
        /// Open for reading only: the buffer refuses every edit and the tab never writes to disk.
        ///
        /// <para>
        /// This exists for files the user is LOOKING at rather than working on — a package on the
        /// import path, which is somebody else's library sitting outside the notebook's own folder
        /// and often outside the solution entirely. This tab autosaves on a 400 ms debounce, so
        /// without a mode like this, resting a hand on the keyboard while reading somebody's
        /// library writes to it. The user is not asked whether they meant it, and there is no
        /// prompt to say no to.
        /// </para>
        ///
        /// <para>
        /// Enforced at the buffer (<c>Scintilla.ReadOnly</c>, which every mutating path checks),
        /// belt-and-braced at the autosave, and STATED in the tab title — a read-only tab that
        /// looks like an editable one just makes the user wonder why their typing does nothing.
        /// </para>
        /// </summary>
        public bool IsReadOnly
        {
            get { return _isReadOnly; }
            set
            {
                if (_isReadOnly == value) return;
                _isReadOnly = value;
                if (_editor != null) _editor.ReadOnly = value;
                if (value) _autosaveTimer.Stop();
                UpdateTitle();
            }
        }

        /// <summary>Reveal the Find bar (find-only mode) and focus it.</summary>
        public void ShowFind() { _findBar.ShowFind(); }

        /// <summary>Reveal the Find/Replace bar (replace mode) and focus it.</summary>
        public void ShowReplace() { _findBar.ShowReplace(); }

        /// <summary>Find next/previous match using the current search term.</summary>
        public void FindNext(bool forward) { _findBar.FindNextExternal(forward); }

        public string FilePath
        {
            get { return _filePath; }
            set
            {
                _filePath = value;
                if (_editor != null) _editor.SetFilePath(value);
                UpdateTitle();
                // A Save As from .py to .md (or the reverse) changes what this tab IS, so the mode
                // is re-decided here as well as on load.
                if (_body != null) ApplyMarkdownMode();
            }
        }

        public bool IsModified
        {
            get { return _isModified; }
            private set
            {
                if (_isModified != value)
                {
                    _isModified = value;
                    UpdateTitle();
                }
            }
        }

        public bool IsNewFile { get; set; }

        /// <summary>
        /// Session-local pin state. A pinned tab is marked with a leading
        /// <c>U+25CF</c> bullet in its title. Hosts consult this flag to skip
        /// the tab in "Close All Others" and to keep new tabs from inserting
        /// to its left.
        /// </summary>
        public bool IsPinned
        {
            get { return _isPinned; }
            set
            {
                if (_isPinned != value)
                {
                    _isPinned = value;
                    UpdateTitle();
                }
            }
        }

        public event EventHandler ModifiedChanged;

        /// <summary>Caret moved; args are (1-based line, 1-based column). For a status-bar readout.</summary>
        public event Action<int, int> CaretMoved;

        /// <summary>Current caret line (1-based).</summary>
        public int CaretLine { get { return _editor != null ? _editor.CaretLine : 1; } }

        /// <summary>Current caret column (1-based).</summary>
        public int CaretColumn { get { return _editor != null ? _editor.CaretColumn : 1; } }

        /// <summary>Number of characters currently selected (0 when there is no selection).</summary>
        public int SelectionLength
        {
            get
            {
                if (_editor == null) return 0;
                string sel = _editor.SelectedText;
                return sel != null ? sel.Length : 0;
            }
        }

        /// <summary>
        /// Fired after the file has been successfully saved to disk. Hosts use
        /// this to re-parse the file and refresh downstream state (e.g.
        /// HelixStudio's class diagram).
        /// </summary>
        public event EventHandler FileSaved;

        /// <summary>
        /// Fired immediately before <see cref="SaveFile"/> writes to disk. Host
        /// subscribers may set <see cref="CancelEventArgs.Cancel"/> to abort the
        /// save (no bytes written, no <see cref="FileSaved"/> raised).
        /// </summary>
        public event EventHandler<CancelEventArgs> BeforeSave;

        /// <summary>
        /// Host-supplied scope entered for the duration of every disk write this tab performs
        /// (autosave, explicit save, close-time flush) and held through <see cref="FileSaved"/>.
        /// The host returns its file-watcher suspension scope here so the IDE's own saves are
        /// never misreported as "External Change Detected". This assembly cannot reference the
        /// watcher directly, hence the callback. Null (or a null return) means "no guard".
        /// </summary>
        public Func<IDisposable> SaveGuard { get; set; }

        public EditorTabPage()
        {
            // TabPage (like Panel) isn't double-buffered by default; without this, the editor's own
            // buffered repaint can still show a flash of the page's raw background around it (nav
            // bar / find bar layout, tab-page edges) on every child Invalidate() — i.e. on typing
            // and clicking, since PythonEditorControl.OnMouseDown/caret-move both call Invalidate().
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);

            _originalContent = "";
            IsNewFile = true;

            _editor = new PythonEditorControl();
            _editor.Dock = DockStyle.Fill;
            // Forward caret movement so the host can show a status-bar Ln/Col readout, and keep the
            // navigation bar pointed at the symbol the caret is inside.
            _editor.CaretMoved += (line, col) =>
            {
                try { if (_navBar != null) _navBar.SyncToLine(line); } catch { }
                var h = CaretMoved; if (h != null) h(line, col);
            };

            // Find/Replace bar docks above the editor. Hidden until Ctrl+F / Ctrl+H.
            _findBar = new FindReplaceBar(_editor);

            // VS2017-style navigation bar (Classes / Functions / Variables dropdowns).
            _navBar = new NavigationBar();
            _navBar.NavigateToLine += OnNavigateToLine;

            // Nest the editor + find bar inside a Fill body panel, and dock the nav bar Top on the
            // page ABOVE that body. This guarantees the nav bar reserves its own strip and can never
            // overlap the editor's first line (a flat Top+Fill sibling layout let it cover line 1).
            _body = new Panel { Dock = DockStyle.Fill };
            _body.Controls.Add(_editor);    // Fill (added first → innermost)
            _body.Controls.Add(_findBar);   // Top
            this.Controls.Add(_body);       // Fill (added first → innermost)
            this.Controls.Add(_navBar);    // Top (added last → outermost, reserves the top strip)
            BuildMarkdownBar();            // Top, above the nav bar; hidden unless this is a .md file

            // Hook TextChanged AFTER _originalContent is set and the control is
            // added, so initialisation churn does not mark the tab as modified.
            _editor.TextChanged += Editor_TextChanged;

            // Autosave: every keystroke restarts this debounce; once typing pauses
            // for 400 ms the pending edits are flushed to disk. Hosts can also
            // force-flush via FlushPendingSave() (e.g. before closing a tab).
            _autosaveTimer = new Timer { Interval = 400 };
            _autosaveTimer.Tick += OnAutosaveTick;

            UpdateTitle();
        }

        public EditorTabPage(string filePath) : this()
        {
            LoadFile(filePath);
        }

        private void Editor_TextChanged(object sender, EventArgs e)
        {
            IsModified = _editor.Text != _originalContent;
            var handler = ModifiedChanged;
            if (handler != null) handler(this, EventArgs.Empty);

            // Keep the preview in step with edits made in Source mode, debounced.
            if (_previewTimer != null && IsMarkdown)
            {
                _previewTimer.Stop();
                _previewTimer.Start();
            }

            // Kick the autosave debounce. Untitled tabs (no path yet) don't
            // autosave — there's nowhere to write.
            if (_isModified && !string.IsNullOrEmpty(_filePath))
            {
                _autosaveTimer.Stop();
                _autosaveTimer.Start();
            }
        }

        private void OnAutosaveTick(object sender, EventArgs e)
        {
            _autosaveTimer.Stop();
            if (IsDisposed) return;
            // Belt and braces. The buffer already refuses edits, so this should be unreachable —
            // but autosave is the one path here that writes to somebody else's file without being
            // asked, and it is worth two lines to make that impossible rather than unlikely.
            if (_isReadOnly) return;
            // Refresh the navigation dropdowns once typing settles (cheap re-parse).
            try { _navBar.Populate(_editor.Text, _filePath); _navBar.SyncToLine(_editor.CaretLine); } catch { }
            if (!_isModified) return;
            if (string.IsNullOrEmpty(_filePath)) return;
            try { SaveFile(); } catch { /* best-effort; next keystroke will retry */ }
        }

        // Navigation-bar selection: jump the editor to the chosen symbol's (1-based) line.
        private void OnNavigateToLine(int line)
        {
            if (IsDisposed || line < 1) return;
            try
            {
                int idx = line - 1;
                if (idx >= _editor.Lines.Count) idx = _editor.Lines.Count - 1;
                if (idx < 0) return;
                _editor.Lines[idx].EnsureVisible();
                _editor.Lines[idx].Goto();
                _editor.Focus();
            }
            catch { /* best-effort navigation */ }
        }

        /// <summary>
        /// Immediately save any pending autosave without waiting for the debounce.
        /// Safe to call when nothing is pending. Hosts call this before closing
        /// the tab or the app so edits typed just before close are not lost.
        /// </summary>
        public void FlushPendingSave()
        {
            _autosaveTimer.Stop();
            if (_isModified && !string.IsNullOrEmpty(_filePath))
            {
                try { SaveFile(); } catch { /* best-effort */ }
            }
        }

        public void LoadFile(string filePath)
        {
            try
            {
                string content = File.ReadAllText(filePath, Utf8NoBom);

                // Strip BOM if present – files saved by other editors may have one,
                // and Python syntax checkers flag U+FEFF as an error.
                if (content.Length > 0 && content[0] == '\uFEFF')
                    content = content.Substring(1);

                _editor.Text = content;
                _originalContent = content;
                _filePath = filePath;
                // Propagate the path to the editor so file-path-aware features work: hover context
                // AND completion-engine selection by extension (.cu/.cuh -> CUDA, else Python).
                _editor.SetFilePath(filePath);
                _navBar.Populate(content, filePath);
                // Repopulating resets the dropdowns to their placeholders, so point them back at
                // wherever the caret is — including a jump that landed mid-file (F12, a references
                // hit), where the bar would otherwise read blank on arrival.
                try { _navBar.SyncToLine(_editor.CaretLine); } catch { }
                IsNewFile = false;
                IsModified = false;
                UpdateTitle();
                // After _filePath is set — IsMarkdown is decided by extension, so this cannot run
                // any earlier.
                ApplyMarkdownMode();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Error loading file: {0}", ex.Message),
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public bool SaveFile(string filePath = null)
        {
            string path = filePath ?? _filePath;
            if (string.IsNullOrEmpty(path))
                return false;

            // A read-only tab never writes, whoever asks. The buffer refuses edits so there should
            // be nothing to write, but "should be nothing" is not the same promise as "cannot
            // write": this is somebody else's library file, reached by browsing rather than by
            // opening, and the one outcome that must be impossible is the IDE modifying it.
            // Save-As is still allowed — that writes a copy somewhere the user chose.
            if (_isReadOnly && string.Equals(path, _filePath, StringComparison.OrdinalIgnoreCase))
                return false;

            // Give the host a chance to veto the save (used by the project-kind
            // main() constraint). If cancelled, do not touch disk and do not
            // raise FileSaved.
            EventHandler<CancelEventArgs> beforeHandler = BeforeSave;
            if (beforeHandler != null)
            {
                CancelEventArgs args = new CancelEventArgs(false);
                beforeHandler(this, args);
                if (args.Cancel) return false;
            }

            // Enter the host's guard (file-watcher suspension) before touching disk and hold it
            // through FileSaved: subscribers there may write further files (base-type rebinds).
            IDisposable guard = null;
            try { Func<IDisposable> g = SaveGuard; if (g != null) guard = g(); }
            catch { guard = null; }

            try
            {
                File.WriteAllText(path, _editor.Text, Utf8NoBom);
                _filePath = path;
                _originalContent = _editor.Text;
                IsNewFile = false;
                IsModified = false;
                UpdateTitle();
                EventHandler savedHandler = FileSaved;
                if (savedHandler != null) savedHandler(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Error saving file: {0}", ex.Message),
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                if (guard != null) { try { guard.Dispose(); } catch { } }
            }
        }

        // ---- Markdown preview -----------------------------------------------------------------

        /// <summary>
        /// Raised when the user clicks a link in the rendered preview. Unhandled by default: the tab
        /// deliberately does not launch a browser or open a file on its own — see
        /// <see cref="Markdown.MarkdownViewControl.LinkClicked"/>.
        /// </summary>
        public event EventHandler<Markdown.MarkdownLinkClickedEventArgs> MarkdownLinkClicked;

        /// <summary>True when this tab holds a Markdown document.</summary>
        public bool IsMarkdown
        {
            get { return PythonEditorControl.IsMarkdownFilePath(_filePath); }
        }

        /// <summary>True when the rendered preview is the visible half.</summary>
        public bool IsPreviewVisible
        {
            get { return _showingPreview; }
        }

        /// <summary>The rendered view, or null until a Markdown file is loaded into this tab.</summary>
        internal Markdown.MarkdownViewControl MarkdownView { get { return _markdownView; } }

        private void BuildMarkdownBar()
        {
            _markdownBar = new Panel { Dock = DockStyle.Top, Height = 26, Visible = false };

            _previewButton = new Button
            {
                Text = "Preview", Width = 74, Height = 22, Left = 4, Top = 2,
                FlatStyle = FlatStyle.System, TabStop = false,
            };
            _sourceButton = new Button
            {
                Text = "Source", Width = 74, Height = 22, Left = 82, Top = 2,
                FlatStyle = FlatStyle.System, TabStop = false,
            };
            _previewButton.Click += delegate { ShowMarkdownPreview(true); };
            _sourceButton.Click += delegate { ShowMarkdownPreview(false); };

            _markdownBar.Controls.Add(_previewButton);
            _markdownBar.Controls.Add(_sourceButton);
            this.Controls.Add(_markdownBar);   // added last → outermost, sits above the nav bar
        }

        /// <summary>
        /// Puts the tab into Markdown mode, or takes it out.
        ///
        /// <b>Markdown opens in Preview.</b> A .md file opened as source is the complaint this
        /// feature exists to answer — the user asked for a README and got a syntax-coloured text
        /// file. Source is one click away and the choice is per-tab, so anyone editing rather than
        /// reading pays a single click.
        /// </summary>
        private void ApplyMarkdownMode()
        {
            bool markdown = IsMarkdown;
            if (_markdownBar != null) _markdownBar.Visible = markdown;

            if (!markdown)
            {
                _showingPreview = false;
                if (_markdownView != null) _markdownView.Visible = false;
                _editor.Visible = true;
                _navBar.Visible = true;
                return;
            }

            EnsureMarkdownView();
            RenderMarkdownPreview();
            ShowMarkdownPreview(true);
        }

        private void EnsureMarkdownView()
        {
            if (_markdownView != null) return;

            _markdownView = new Markdown.MarkdownViewControl { Dock = DockStyle.Fill, Visible = false };
            _markdownView.LinkClicked += (s, e) =>
            {
                var handler = MarkdownLinkClicked;
                if (handler != null) handler(this, e);
            };
            _body.Controls.Add(_markdownView);

            // Re-render on a debounce rather than per keystroke: parsing a long README on every
            // character would make typing in Source mode stutter, and the preview only has to be
            // right once the user pauses. Same 400 ms budget the autosave debounce already uses.
            _previewTimer = new Timer { Interval = 400 };
            _previewTimer.Tick += delegate
            {
                _previewTimer.Stop();
                RenderMarkdownPreview();
            };
        }

        /// <summary>Switches between the rendered preview and the source editor.</summary>
        public void ShowMarkdownPreview(bool preview)
        {
            if (!IsMarkdown) return;
            EnsureMarkdownView();

            _showingPreview = preview;
            if (preview) RenderMarkdownPreview();

            // The nav bar's Classes / Functions / Variables dropdowns describe code. Over a rendered
            // document they are three permanently-empty combo boxes taking a strip of the page.
            _navBar.Visible = !preview;
            _editor.Visible = !preview;
            _findBar.Visible = _findBar.Visible && !preview;
            _markdownView.Visible = preview;

            if (_previewButton != null) _previewButton.Enabled = !preview;
            if (_sourceButton != null) _sourceButton.Enabled = preview;
            if (preview) _markdownView.BringToFront(); else _editor.BringToFront();
        }

        private void RenderMarkdownPreview()
        {
            if (_markdownView == null) return;
            try { _markdownView.SetMarkdown(_editor.Text); }
            catch (Exception ex)
            {
                // A parser fault must not take the editor down with it. Showing the message beats
                // an empty pane, because it tells the user the preview is broken rather than the
                // document being empty.
                System.Diagnostics.Debug.WriteLine("[markdown] render failed: " + ex.Message);
            }
        }

        private void UpdateTitle()
        {
            string name = string.IsNullOrEmpty(_filePath)
                ? "Untitled"
                : Path.GetFileName(_filePath);

            string modifiedSuffix = _isModified ? " *" : string.Empty;
            string pinnedPrefix = _isPinned ? "● " : string.Empty;
            string readOnlySuffix = _isReadOnly ? "  (read-only)" : string.Empty;
            this.Text = pinnedPrefix + name + modifiedSuffix + readOnlySuffix;
            this.ToolTipText = (_filePath ?? "Untitled")
                             + (_isReadOnly ? Environment.NewLine + "Open for reading only."
                                            : string.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (_autosaveTimer != null)
                    {
                        _autosaveTimer.Stop();
                        _autosaveTimer.Tick -= OnAutosaveTick;
                        _autosaveTimer.Dispose();
                    }
                }
                catch { }
                try
                {
                    // The preview debounce is a second Timer on this page; leaving it running past
                    // disposal fires a Tick against a disposed editor.
                    if (_previewTimer != null) { _previewTimer.Stop(); _previewTimer.Dispose(); }
                }
                catch { }
            }
            base.Dispose(disposing);
        }
    }
}
