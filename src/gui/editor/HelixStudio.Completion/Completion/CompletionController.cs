using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using HelixPythonNamespace.Catalog;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Extensibility;
using HelixTextEditorNET;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// Orchestrates the completion popup for a <see cref="Scintilla"/> editor:
    /// classifies context, runs the engine, filters + ranks, pushes to AutoC,
    /// and drives the side doc panel. Owned by <c>PythonEditorControl</c>.
    /// </summary>
    public sealed class CompletionController : IDisposable
    {
        private readonly Scintilla _editor;
        private readonly Func<SolutionModel> _solutionResolver;
        private readonly Func<string> _filePathResolver;
        private readonly ContributionRegistry<CompletionEngineContribution> _engines =
            new ContributionRegistry<CompletionEngineContribution>();
        private readonly PythonCompletionEngine _pyEngine = new PythonCompletionEngine();
        private readonly CudaCompletionEngine _cudaEngine = new CudaCompletionEngine();
        private readonly CppCompletionEngine _cppEngine = new CppCompletionEngine();
        private readonly CMakeCompletionEngine _cmakeEngine = new CMakeCompletionEngine();
        private readonly TomlCompletionEngine _tomlEngine = new TomlCompletionEngine();
        private readonly CompletionDocPanel _doc = new CompletionDocPanel();
        private const string DefaultCommitCharacters = ".([,";

        // Pick the completion engine by the active file so .cu/.cuh get CUDA, CMakeLists.txt/*.cmake get
        // CMake, and everything else gets Python — same editor UI, language-appropriate suggestions.
        private ICompletionEngine _engine
        {
            get
            {
                string fp = _filePathResolver != null ? _filePathResolver() : null;
                CompletionEngineContribution match = _engines.FirstOrDefault(c => c.Supports(fp));
                return match != null ? match.Engine : _pyEngine;
            }
        }

        private static bool IsCppFile(string ext)
        {
            return string.Equals(ext, ".c", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".cc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".cpp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".cxx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".hh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".hpp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".hxx", StringComparison.OrdinalIgnoreCase);
        }

        // CMakeLists.txt (any case) or a *.cmake module file.
        private static bool IsCMakeFile(string filePath, string ext)
        {
            if (string.Equals(ext, ".cmake", StringComparison.OrdinalIgnoreCase)) return true;
            string name = string.IsNullOrEmpty(filePath) ? null : System.IO.Path.GetFileName(filePath);
            return string.Equals(name, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase);
        }
        private List<CompletionItem> _lastItems = new List<CompletionItem>();
        private CompletionItem _pendingCommittedItem;
        private int _pendingCommitPosition = -1;
        private bool _disposed;

        // Third-party/stdlib member completion (e.g. `np.` after `import numpy as np`):
        // PythonCompletionEngine.AddMemberCompletions only ever resolves local symbols on its
        // own — this is what feeds it real, live-interpreter-introspected module members on
        // demand. _pyCatalog grows over the session (CatalogScanner.Add()s into it in place,
        // never cleared) and is shared by reference with _pyEngine, so newly-scanned modules
        // become visible on the NEXT completion request with no extra wiring. One scan process
        // per not-yet-seen module FQN — _scanAttempted prevents spawning a duplicate `python.exe`
        // on every keystroke while a scan for the same module is still in flight or already failed.
        private readonly PythonCatalog _pyCatalog = new PythonCatalog();
        // One CatalogScanner PER scan (not shared) — StartScan mutates instance state
        // (_proc, _finishedRaised) that a second concurrent call on a shared instance would
        // clobber if the user triggers completion for two different not-yet-seen modules close
        // together. Tracked here only so Dispose() can Cancel() whatever's still in flight.
        private readonly List<CatalogScanner> _activeScanners = new List<CatalogScanner>();
        private readonly HashSet<string> _scanAttempted = new HashSet<string>(StringComparer.Ordinal);
        private string _pythonInterpreterPath;   // resolved lazily; null = "looked, not found"
        private bool _pythonInterpreterResolved;
        private string _introspectScriptPath;    // resolved lazily; null = "looked, not found"
        private bool _introspectScriptResolved;

        /// <summary>
        /// Text that comes BEFORE this editor's buffer in the document it is really part of, or
        /// null when the buffer is the whole document.
        ///
        /// <para>
        /// This exists for notebooks. A notebook cell is edited in an ordinary editor whose buffer
        /// holds that cell and nothing else, so without this the completion engine sees a file
        /// that starts at `df.` and knows nothing about the twelve cells above it that imported
        /// pandas and built `df`. The host supplies the preceding cells here; the engine is handed
        /// the joined text with the caret shifted to match, so it answers about the notebook rather
        /// than about a fragment.
        /// </para>
        ///
        /// <para>
        /// Every read of the buffer for COMPLETION purposes goes through <see cref="SourceAndCaret"/>
        /// so the two can never drift apart — a source that includes the prefix with a caret that
        /// does not is an offset into the wrong statement, which is worse than no completion.
        /// </para>
        /// </summary>
        public Func<string> ContextPrefix { get; set; }

        /// <summary>The buffer as the engine should see it, and where the caret is in THAT text.</summary>
        private string SourceAndCaret(out int caret)
        {
            string source = _editor.Text ?? string.Empty;
            caret = _editor.CurrentPosition;

            Func<string> prefixResolver = ContextPrefix;
            string prefix = prefixResolver == null ? null : prefixResolver();
            if (string.IsNullOrEmpty(prefix)) return source;

            caret += prefix.Length;
            return prefix + source;
        }

        public CompletionController(Scintilla editor,
            Func<SolutionModel> solutionResolver, Func<string> filePathResolver)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _solutionResolver = solutionResolver ?? (() => null);
            _filePathResolver = filePathResolver ?? (() => null);
            _pyEngine.Catalog = _pyCatalog;
            RegisterDefaultEngines();

            CompletionIcons.RegisterAll(_editor);
            _editor.AutoCIgnoreCase = false;
            _editor.AutoCChooseSingle = false;
            _editor.AutoCOrder = Order.PerformSort;
            _editor.AutoCMaxHeight = 12;
            _editor.AutoCSetFillUps(DefaultCommitCharacters);

            _editor.AutoCSelection += OnAutoCSelection;
            _editor.AutoCCompleted += OnAutoCCompleted;
            _editor.AutoCCancelled += OnAutoCDismiss;
            // NOTE: we deliberately don't hook LostFocus. Showing the doc-panel form can
            // briefly blink focus away from Scintilla; a LostFocus → HideDoc wire would
            // then dismiss the panel we just showed. Dismissal is handled by Escape,
            // caret-move (UpdateUI Selection), and AutoC cancel/completed events.
        }

        /// <summary>
        /// Adds a completion provider without changing the controller's language switch. Higher
        /// priority providers can deliberately claim a path before built-in providers.
        /// </summary>
        public void RegisterCompletionEngine(string id, ICompletionEngine engine,
            Func<string, bool> supportsFile, int priority = 0)
        {
            _engines.Register(id, new CompletionEngineContribution(engine, supportsFile), priority);
        }

        private void RegisterDefaultEngines()
        {
            RegisterCompletionEngine("cuda", _cudaEngine, fp => HasExtension(fp, ".cu", ".cuh"), 200);
            RegisterCompletionEngine("cpp", _cppEngine, fp => IsCppFile(ExtensionOf(fp)), 100);
            RegisterCompletionEngine("cmake", _cmakeEngine, fp => IsCMakeFile(fp, ExtensionOf(fp)), 100);
            RegisterCompletionEngine("toml", _tomlEngine, fp => HasExtension(fp, ".toml"), 50);
            // .rs/.cs/.java/.jl completion ships in HelixRust/HelixCSharp/HelixJava/HelixJulia.
            // The registry is still open: RegisterCompletionEngine is public for host contributions.
            RegisterCompletionEngine("python", _pyEngine, fp => true, -1000);
        }

        private static string ExtensionOf(string filePath)
        {
            return string.IsNullOrEmpty(filePath) ? null : System.IO.Path.GetExtension(filePath);
        }

        private static bool HasExtension(string filePath, params string[] extensions)
        {
            string ext = ExtensionOf(filePath);
            foreach (string candidate in extensions)
                if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Show a read-only doc panel for the symbol at the current caret (Ctrl+K, Ctrl+I in VS).
        /// Looks up the word at the caret in the completion engine; if we have a matching
        /// item with Detail/Documentation, the side doc panel appears anchored to the caret.
        /// </summary>
        public void ShowQuickInfoAtCaret()
        {
            ShowQuickInfoAt(QuickInfoPosition(_editor == null ? 0 : _editor.CurrentPosition), allowCatalogScan: true);
        }

        public void ShowQuickInfoAtPosition(int position)
        {
            ShowQuickInfoAt(QuickInfoPosition(position), allowCatalogScan: true);
        }

        private int QuickInfoPosition(int position)
        {
            if (_editor == null) return position;
            string text = _editor.Text ?? string.Empty;
            position = Math.Max(0, Math.Min(position, text.Length));
            if (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_')) return position;
            int p = position - 1;
            while (p >= 0 && char.IsWhiteSpace(text[p])) p--;
            if (p >= 0 && text[p] == '(') p--;
            while (p >= 0 && char.IsWhiteSpace(text[p])) p--;
            return p >= 0 ? p : position;
        }

        /// <summary>
        /// The Quick Info body: resolve the symbol at <paramref name="caret"/> and show the doc
        /// panel for it.
        ///
        /// <paramref name="allowCatalogScan"/> is false on the re-show that a finished catalog
        /// scan triggers, so a module that still fails to resolve can't loop.
        /// </summary>
        private void ShowQuickInfoAt(int caret, bool allowCatalogScan)
        {
            if (_disposed || _editor == null) return;
            // Quick Info is asked about a caret the CALLER chose (a hover position, not necessarily
            // the current one), so it is shifted by the prefix length rather than replaced.
            string source = SourceAndCaret(out int ignoredCaret);
            caret += source.Length - (_editor.Text ?? string.Empty).Length;

            CompletionItem best = null;
            string unresolvedModule = null;

            // Python resolves the symbol directly (module names, dotted members, locally-declared
            // classes/methods) rather than fishing through a completion list — see
            // PythonCompletionEngine.GetQuickInfo for why the list-based lookup answered nothing
            // for `import asyncio` or `asyncio.Semaphore`.
            if (ReferenceEquals(_engine, _pyEngine))
            {
                try
                {
                    var quick = _pyEngine.GetQuickInfo(source, caret,
                        _filePathResolver?.Invoke(), _solutionResolver?.Invoke());
                    if (quick != null)
                    {
                        best = quick.Item;
                        unresolvedModule = quick.UnresolvedModuleFqn;
                    }
                }
                catch { }
            }

            if (best == null) best = MatchCompletionItemAt(source, caret);

            if (best != null)
            {
                // Fallback Detail for bare variables / parameters / plain names: show "kind name"
                // rather than silently hiding the panel (VS always shows at least the kind).
                if (string.IsNullOrEmpty(best.Detail) && string.IsNullOrEmpty(best.Documentation))
                {
                    best = new CompletionItem(best.Label, best.Kind,
                        insertText: best.InsertText,
                        detail: best.Kind + " " + best.Label);
                }
                ShowDocPanelAt(best, caret);
            }

            // The symbol lives in a module the catalog hasn't introspected yet. Kick the scan and
            // re-show once it lands, so the first F1 on `asyncio.Semaphore` ends with the real
            // signature and docstring instead of nothing at all.
            if (allowCatalogScan && !string.IsNullOrEmpty(unresolvedModule))
            {
                int requestedAt = caret;
                TryStartCatalogScan(unresolvedModule, () =>
                {
                    if (_editor.CurrentPosition == requestedAt) ShowQuickInfoAt(requestedAt, allowCatalogScan: false);
                });
            }
        }

        // The legacy lookup: ask for the completion list at the caret and take the richest entry
        // whose label matches the word. Still the whole story for the non-Python engines.
        private CompletionItem MatchCompletionItemAt(string source, int caret)
        {
            int start = _editor.WordStartPosition(caret, true);
            int end = _editor.WordEndPosition(caret, true);
            if (end <= start) return null;
            string word = _editor.GetTextRange(start, end - start);
            if (string.IsNullOrEmpty(word)) return null;

            List<CompletionItem> all;
            try
            {
                all = _engine.GetCompletions(source, end, _filePathResolver?.Invoke(),
                    _solutionResolver?.Invoke());
            }
            catch { return null; }
            if (all == null || all.Count == 0) return null;

            // A single identifier can land in the completion list multiple times — e.g.
            // `total_area` appears both as a `from X import total_area` alias (Kind=Module,
            // no Detail) and as a solution-wide function (Kind=Function, Detail=FQN).
            // Pick the richest match so the doc panel has something to render.
            CompletionItem best = null;
            foreach (var it in all)
            {
                if (!string.Equals(it.Label, word, StringComparison.Ordinal)) continue;
                if (best == null) { best = it; continue; }
                bool bestHas = !string.IsNullOrEmpty(best.Detail) || !string.IsNullOrEmpty(best.Documentation);
                bool itHas = !string.IsNullOrEmpty(it.Detail) || !string.IsNullOrEmpty(it.Documentation);
                if (itHas && !bestHas) best = it;
            }
            return best;
        }

        private void ShowDocPanelAt(CompletionItem best, int caret)
        {
            Point anchor;
            try
            {
                int x = _editor.PointXFromPosition(caret);
                int y = _editor.PointYFromPosition(caret);
                // Below the FULL line, not a fixed guess — a fixed +20 overlapped the typed line
                // whenever the actual line height (font size, DPI) exceeded 20px.
                int lineHeight = _editor.Lines[_editor.LineFromPosition(caret)].Height;
                anchor = _editor.PointToScreen(new Point(x, y + lineHeight));
            }
            catch { anchor = _editor.PointToScreen(new Point(100, 100)); }
            // Hide first to force a visible re-render even when the content and anchor
            // haven't changed — otherwise a repeat Ctrl+K,Ctrl+I leaves the panel in
            // the same state and reads as "nothing happened".
            _doc.Hide();
            _doc.Show(best, anchor, _editor.FindForm());
        }

        /// <summary>Hide the quick-info / selection doc panel.</summary>
        public void HideDoc()
        {
            _doc.Hide();
        }

        /// <summary>
        /// The one-line signature for a function name, from the active engine's completion items
        /// (CUDA runtime/intrinsics for <c>.cu</c>, Python builtins/solution symbols otherwise).
        /// Returns null when unknown — the caller falls back to its built-in table.
        /// </summary>
        /// <summary>
        /// Hover text (FR-KB-05): the signature + documentation for <paramref name="word"/>, matching
        /// Quick Info. Takes explicit source/caret so it is safe to call off the UI thread (the dwell
        /// handler runs on a background worker). Returns null when the word isn't a known symbol.
        /// </summary>
        public string GetHoverText(string source, int caret, string word)
        {
            if (_disposed || string.IsNullOrEmpty(word)) return null;

            // Same resolution as Quick Info, so hovering a function, method, class or imported
            // module shows the doctip F1 would. The list-based match below only ever answered for
            // symbols that are also completion candidates at that exact caret — which excluded
            // every method (the general list carries module-level definitions only) and every
            // member of an imported module.
            if (ReferenceEquals(_engine, _pyEngine))
            {
                try
                {
                    var quick = _pyEngine.GetQuickInfo(source ?? string.Empty, caret,
                        _filePathResolver != null ? _filePathResolver() : null,
                        _solutionResolver != null ? _solutionResolver() : null);
                    if (quick != null && quick.Item != null)
                    {
                        // A module whose members aren't catalogued yet: scan now so the NEXT hover
                        // (or an F1) has the real docs. Deliberately no re-show — by the time a
                        // real interpreter has finished importing the module the mouse has moved.
                        if (!string.IsNullOrEmpty(quick.UnresolvedModuleFqn))
                            TryStartCatalogScan(quick.UnresolvedModuleFqn, null);
                        string composed = Compose(quick.Item.Detail, quick.Item.Documentation);
                        if (!string.IsNullOrEmpty(composed)) return composed;
                    }
                }
                catch { }
            }

            try
            {
                var items = _engine.GetCompletions(source ?? string.Empty, caret,
                    _filePathResolver != null ? _filePathResolver() : null,
                    _solutionResolver != null ? _solutionResolver() : null);
                if (items == null) return null;
                foreach (var it in items)
                {
                    if (!string.Equals(it.Label, word, System.StringComparison.Ordinal)) continue;
                    return Compose(it.Detail, it.Documentation);
                }
            }
            catch { }
            return null;
        }

        private static string Compose(string detail, string doc)
        {
            if (string.IsNullOrEmpty(detail) && string.IsNullOrEmpty(doc)) return null;
            if (string.IsNullOrEmpty(doc)) return detail;
            if (string.IsNullOrEmpty(detail)) return doc;
            return detail + "\n\n" + doc;
        }

        public string GetCallTip(string funcName)
        {
            if (_disposed || string.IsNullOrEmpty(funcName)) return null;
            try
            {
                int caret;
                string source = SourceAndCaret(out caret);
                if (ReferenceEquals(_engine, _pyEngine))
                {
                    int nameEnd = Math.Min(caret, source.Length);
                    int nameStart = nameEnd;
                    while (nameStart > 0 && char.IsWhiteSpace(source[nameStart - 1])) nameStart--;
                    if (nameStart > 0 && source[nameStart - 1] == '(') nameStart--;
                    while (nameStart > 0 && (char.IsLetterOrDigit(source[nameStart - 1]) || source[nameStart - 1] == '_')) nameStart--;
                    int occurrence = source.LastIndexOf(funcName, Math.Max(0, nameEnd - 1), StringComparison.Ordinal);
                    if (occurrence >= 0)
                    {
                        var quick = _pyEngine.GetQuickInfo(source, occurrence + Math.Min(1, funcName.Length),
                            _filePathResolver != null ? _filePathResolver() : null,
                            _solutionResolver != null ? _solutionResolver() : null);
                        if (quick != null && quick.Item != null && !string.IsNullOrEmpty(quick.Item.Detail))
                            return quick.Item.Detail;
                    }
                }
                var items = _engine.GetCompletions(source, caret,
                    _filePathResolver != null ? _filePathResolver() : null,
                    _solutionResolver != null ? _solutionResolver() : null);
                if (items == null) return null;
                foreach (var it in items)
                    if (string.Equals(it.Label, funcName, StringComparison.Ordinal) && !string.IsNullOrEmpty(it.Detail))
                        return it.Detail;
            }
            catch { }
            return null;
        }

        /// <summary>Explicit Ctrl+Space invocation: show popup with every visible item.</summary>
        public void Invoke(bool force)
        {
            if (_disposed) return;
            int caret;
            string source = SourceAndCaret(out caret);
            var ctx = _engine.Classify(source, caret);
            var items = _engine.GetCompletions(source, caret,
                _filePathResolver?.Invoke(), _solutionResolver?.Invoke(), ctx);

            if (ReferenceEquals(_engine, _pyEngine) && !string.IsNullOrEmpty(_pyEngine.LastUnresolvedModuleFqn))
            {
                TryStartCatalogScan(_pyEngine.LastUnresolvedModuleFqn, () =>
                {
                    // Only refreshes when the AutoC popup is CURRENTLY open — never pops one up
                    // unprompted for a user who has since moved on to something else.
                    if (_editor.AutoCActive) Invoke(force: false);
                });
            }

            Show(items, ctx);
        }

        public void InvokePythonSnippets()
        {
            if (_disposed || !ReferenceEquals(_engine, _pyEngine)) return;
            int caret;
            string source = SourceAndCaret(out caret);
            CompletionContext context = _pyEngine.Classify(source, caret);
            Show(new List<CompletionItem>(PythonSnippetCatalog.Items()), context);
        }

        // -------------------------------------------------------------- catalog (np. etc.)

        // Fire-and-forget: spawns `<interpreter> introspect.py <moduleFqn>` once per module FQN
        // per controller lifetime. CatalogScanner streams results straight into _pyCatalog as
        // they arrive; there's no explicit "done, re-show the popup" step in this first pass —
        // the next keystroke/re-trigger (another char typed, backspace+retype, Ctrl+Space) picks
        // up whatever's landed in the catalog by then. A slow/failed scan degrades to exactly
        // today's behavior (no completions for that module), never worse.
        private void TryStartCatalogScan(string moduleFqn, Action onScanned)
        {
            // One scan per module per controller lifetime. Also called from GetHoverText, which
            // runs on the hover worker thread, so the guard set needs its own lock.
            if (string.IsNullOrEmpty(moduleFqn)) return;
            lock (_scanAttempted) { if (!_scanAttempted.Add(moduleFqn)) return; }

            string interpreter = ResolvePythonInterpreter();
            string script = ResolveIntrospectScript();
            if (interpreter == null || script == null) return;

            // The scan (a real `python.exe` importing the module) can take a few real seconds —
            // long enough that the popup or doc panel a user is looking at has usually already
            // given up and shown nothing by the time it finishes. `onScanned` is the caller's
            // "now show the real answer" step, run once scanning completes.
            // HandleEvent/Finished fire on the scanner's background process-output thread, so this
            // must marshal back to the UI thread before touching _editor/_pyEngine.
            var scanner = new CatalogScanner();
            lock (_activeScanners) _activeScanners.Add(scanner);
            EventHandler<ScannerFinishedEventArgs> onFinished = null;
            onFinished = (s, e) =>
            {
                scanner.Finished -= onFinished;
                lock (_activeScanners) _activeScanners.Remove(scanner);
                if (onScanned == null || _disposed || !_editor.IsHandleCreated) return;
                try
                {
                    _editor.BeginInvoke((Action)(() =>
                    {
                        if (_disposed) return;
                        onScanned();
                    }));
                }
                catch { }
            };
            scanner.Finished += onFinished;

            scanner.StartScan(interpreter, script, _pyCatalog, new[] { moduleFqn });
        }

        // Minimal PATH-only discovery (python.exe / python3.exe) — deliberately not the fuller
        // registry/well-known-paths/py-launcher scan HelixPythonNamespace.Debugger.PythonInterpreterManager
        // does, to avoid a wrong-direction project reference from Completion to Debugger. A
        // project's actually-configured interpreter (venv, etc.) isn't consulted here yet —
        // known follow-up, tracked as a limitation, not silently pretended to be handled.
        private string ResolvePythonInterpreter()
        {
            if (_pythonInterpreterResolved) return _pythonInterpreterPath;
            _pythonInterpreterResolved = true;
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                string[] candidates = { "python.exe", "python3.exe" };
                foreach (string dir in pathEnv.Split(System.IO.Path.PathSeparator))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    foreach (string exe in candidates)
                    {
                        string full;
                        try { full = System.IO.Path.Combine(dir, exe); }
                        catch { continue; }
                        if (System.IO.File.Exists(full)) { _pythonInterpreterPath = full; return full; }
                    }
                }
            }
            catch { }
            return null;
        }

        private string ResolveIntrospectScript()
        {
            if (_introspectScriptResolved) return _introspectScriptPath;
            _introspectScriptResolved = true;
            try
            {
                string candidate = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "catalog", "introspect.py");
                if (System.IO.File.Exists(candidate)) { _introspectScriptPath = candidate; return candidate; }
            }
            catch { }
            return null;
        }

        /// <summary>Route typing events: update filter when user types an identifier char.</summary>
        public void OnCharAdded(char ch)
        {
            if (_disposed) return;
            if ((ch == '"' || ch == '<') && IsNativeEngine())
            {
                CompletionContext includeContext;
                if (NativePreprocessorCompletion.TryClassifyInclude(
                    _editor.Text ?? string.Empty, _editor.CurrentPosition, out includeContext))
                {
                    Invoke(force: false);
                    return;
                }
            }
            if (ch == '#' && IsNativeEngine() &&
                NativePreprocessorCompletion.IsDirectiveContext(_editor.Text ?? string.Empty, _editor.CurrentPosition))
            {
                Invoke(force: false);
                return;
            }
            if (ch == '@' && _engine is PythonCompletionEngine)
            {
                // Python decorators have a dedicated completion surface; open it immediately
                // rather than making the user type two identifier characters or press Ctrl+Space.
                Invoke(force: false);
                return;
            }
            if (ch == '.' )
            {
                // Re-invoke to switch into member-access mode immediately after the `.`.
                Invoke(force: false);
                return;
            }
            if (ch == ' ' && _engine is PythonCompletionEngine)
            {
                int caret = _editor.CurrentPosition;
                string source = _editor.Text ?? string.Empty;
                int lineStart = source.LastIndexOf('\n', Math.Max(0, caret - 1));
                lineStart = lineStart < 0 ? 0 : lineStart + 1;
                string line = source.Substring(lineStart, caret - lineStart).Trim();
                CompletionContext context = _engine.Classify(source, caret);

                // Spaces complete the import grammar: `from `, `import `, and
                // `from module import ` (or the next name after a comma).
                bool importBoundary = line == "from" || line == "import" ||
                    (line.StartsWith("import ", StringComparison.Ordinal) && line.EndsWith(",", StringComparison.Ordinal)) ||
                    (line.StartsWith("from ", StringComparison.Ordinal) &&
                     (line.EndsWith(" import", StringComparison.Ordinal) || line.EndsWith(",", StringComparison.Ordinal)));
                if (importBoundary &&
                    (context.Kind == CompletionContextKind.Import ||
                     context.Kind == CompletionContextKind.FromImportMember))
                    Invoke(force: false);
                return;
            }
            // For identifier characters: only auto-open when enough prefix is typed
            // and the popup isn't already active (prevents spam on every keystroke).
            if (!_editor.AutoCActive && IsIdChar(ch))
            {
                int caret = _editor.CurrentPosition;
                int ws = _editor.WordStartPosition(caret, true);
                int prefixLen = caret - ws;
                if (prefixLen >= 2) Invoke(force: false);
            }
            // Active popup: Scintilla filters the list itself against what's typed.
        }

        // -------------------------------------------------------------- internals

        private void Show(List<CompletionItem> items, CompletionContext ctx)
        {
            // Commit characters belong to the completion SESSION, not globally to the editor.
            // In ordinary/member contexts punctuation such as '.' commits the highlighted symbol
            // before the character is inserted. Inside #include, however, dots are literal header
            // filename characters (events.cuh); committing there corrupts the path by replacing
            // the typed prefix with whichever row happens to be selected. Enter/Tab still commit.
            _editor.AutoCSetFillUps(ctx.Kind == CompletionContextKind.PreprocessorInclude
                ? string.Empty
                : DefaultCommitCharacters);

            if (items == null || items.Count == 0) { _editor.AutoCCancel(); HideDoc(); return; }

            var ranked = CompletionRanker.FilterAndRank(items, ctx.Prefix ?? string.Empty);
            if (ranked.Count == 0) { _editor.AutoCCancel(); HideDoc(); return; }

            _lastItems = ranked;
            string list = BuildAutoCList(ranked);
            int lenEntered = (ctx.Prefix ?? string.Empty).Length;
            try { _editor.AutoCShow(lenEntered, list); }
            catch { /* Scintilla occasionally throws if called during an internal event */ }

            // Show the brief doc-on-the-right for the item Scintilla will auto-select immediately —
            // it only raises AutoCSelection on subsequent navigation. Scintilla sorts case-sensitively
            // (AutoCIgnoreCase=false), so its initial selection is the ordinally-first label that
            // matches the typed prefix — NOT ranked[0]. Match that so the doc tracks the highlight.
            CompletionItem initiallySelected = InitiallySelected(ranked, ctx.Prefix ?? string.Empty);
            _pendingCommittedItem = initiallySelected;
            _pendingCommitPosition = Math.Max(0, _editor.CurrentPosition - lenEntered);
            ShowDocForItem(initiallySelected);
        }

        // The item Scintilla's AutoCShow auto-selects: ordinally-first label matching the prefix.
        private static CompletionItem InitiallySelected(List<CompletionItem> ranked, string prefix)
        {
            CompletionItem best = null;
            foreach (CompletionItem it in ranked)
            {
                if (prefix.Length > 0 && !it.Label.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (best == null || string.CompareOrdinal(it.Label, best.Label) < 0) best = it;
            }
            return best ?? (ranked.Count > 0 ? ranked[0] : null);
        }

        private static string BuildAutoCList(List<CompletionItem> items)
        {
            // Scintilla format: label?icon\n (or space-separated, but newline is safer
            // when labels might contain spaces — which they don't for Python identifiers).
            // We use space separator with the default AutoCSetSeparator, so append "?N"
            // for the icon index per item.
            var sb = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(items[i].Label);
                int icon = CompletionIcons.TypeFor(items[i].Kind);
                if (icon > 0) { sb.Append('?').Append(icon); }
            }
            return sb.ToString();
        }

        private void OnAutoCSelection(object sender, AutoCSelectionEventArgs e)
        {
            if (_disposed) return;
            string label = e.Text;
            if (string.IsNullOrEmpty(label)) { HideDoc(); return; }

            CompletionItem item = null;
            foreach (var it in _lastItems)
                if (string.Equals(it.Label, label, StringComparison.Ordinal)) { item = it; break; }
            _pendingCommittedItem = item;
            _pendingCommitPosition = e.Position;
            ShowDocForItem(item);
        }

        private void OnAutoCCompleted(object sender, EventArgs e)
        {
            CompletionItem item = _pendingCommittedItem;
            int start = _pendingCommitPosition;
            _pendingCommittedItem = null;
            _pendingCommitPosition = -1;

            if (item != null && item.Kind == CompletionKind.Snippet &&
                !string.Equals(item.InsertText, item.Label, StringComparison.Ordinal) && start >= 0)
            {
                try
                {
                    string source = _editor.Text ?? string.Empty;
                    string expansion = FormatSnippetExpansion(source, start, item.InsertText);
                    _editor.TargetStart = start;
                    _editor.TargetEnd = Math.Min(_editor.TextLength, start + item.Label.Length);
                    _editor.ReplaceTarget(expansion);
                    _editor.GotoPosition(start + expansion.Length);
                }
                catch { /* completion remains the shortcut label if expansion cannot be applied */ }
            }
            else if (item != null && start >= 0)
            {
                ApplyCallableInsertion(_editor, item, start);
            }
            HideDoc();
        }

        public static bool ApplyCallableInsertion(Scintilla editor, CompletionItem item, int start)
        {
            if (editor == null || item == null || start < 0) return false;
            string call = item.Label + "()";
            if (!string.Equals(item.InsertText, call, StringComparison.Ordinal)) return false;

            editor.TargetStart = start;
            editor.TargetEnd = Math.Min(editor.TextLength, start + item.Label.Length);
            editor.ReplaceTarget(call);
            editor.GotoPosition(start + item.Label.Length + 1);
            return true;
        }

        public static string FormatSnippetExpansion(string source, int insertionPosition, string expansion)
        {
            source = source ?? string.Empty;
            expansion = expansion ?? string.Empty;
            insertionPosition = Math.Max(0, Math.Min(insertionPosition, source.Length));

            int lineStart = insertionPosition == 0 ? -1 : source.LastIndexOf('\n', insertionPosition - 1);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int p = lineStart;
            while (p < insertionPosition && (source[p] == ' ' || source[p] == '\t')) p++;
            string indent = source.Substring(lineStart, p - lineStart);
            string eol = source.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string normalized = expansion.Replace("\r\n", "\n").Replace('\r', '\n');
            return normalized.Replace("\n", eol + indent);
        }

        // Show the doc-on-the-right panel for a completion item, anchored just right of the list.
        private void ShowDocForItem(CompletionItem item)
        {
            if (_disposed || item == null) { HideDoc(); return; }
            Point anchor;
            try
            {
                int p = _editor.CurrentPosition;
                int x = _editor.PointXFromPosition(p);
                int y = _editor.PointYFromPosition(p);
                int lineHeight = _editor.Lines[_editor.LineFromPosition(p)].Height;
                anchor = _editor.PointToScreen(new Point(x + 300, y + lineHeight));
            }
            catch { anchor = _editor.PointToScreen(new Point(100, 100)); }
            _doc.Show(item, anchor, _editor.FindForm(), _editor.AutoCWindowBounds);
        }

        private void OnAutoCDismiss(object sender, EventArgs e)
        {
            _pendingCommittedItem = null;
            _pendingCommitPosition = -1;
            HideDoc();
        }

        private static bool IsIdChar(char c) => c == '_' || char.IsLetterOrDigit(c);

        private bool IsNativeEngine()
        {
            ICompletionEngine engine = _engine;
            return ReferenceEquals(engine, _cudaEngine) || ReferenceEquals(engine, _cppEngine);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _editor.AutoCSelection -= OnAutoCSelection; } catch { }
            try { _editor.AutoCCompleted -= OnAutoCCompleted; } catch { }
            try { _editor.AutoCCancelled -= OnAutoCDismiss; } catch { }
            lock (_activeScanners)
            {
                foreach (var s in _activeScanners) { try { s.Cancel(); } catch { } }
                _activeScanners.Clear();
            }
            _doc.Dispose();
        }
    }
}
