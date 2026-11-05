namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.App.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.DeadCode;
    using HelixPythonNamespace.Git;
    using HelixPythonNamespace.Lint;
    using HelixPythonNamespace.Linker;
    using HelixPythonNamespace.Metrics;
    using HelixPythonNamespace.Profiler;
    using HelixPythonNamespace.Testing;
    using HelixPythonNamespace.VirtualCompiler;
    using LinkerCompileFileResult = HelixPythonNamespace.Linker.CompileFileResult;
    using VcAst = HelixPythonNamespace.VirtualCompiler.Ast;
    using VcCompileFileResult = HelixPythonNamespace.VirtualCompiler.CompileFileResult;

    /// <summary>What body of work a code review submits to the engine.</summary>
    public enum CodeReviewScope { Solution, ActiveFile, UncommittedChanges }

    /// <summary>
    /// Wires the analysis/run engine libraries (Lint, Dead Code, Metrics, Profiler, Tests,
    /// Git) into the GUI. Blocking engines run through <see cref="MainForm._jobRunner"/> on a
    /// background thread with cancellation; the event-driven engines (Profiler, TestRunner)
    /// are registered with the runner so the shared Cancel button calls their <c>Stop()</c>.
    /// Results land in the dedicated panels created by <see cref="MainForm"/>.
    /// </summary>
    public sealed class EngineCoordinator : ICoordinator
    {
        private MainFormContext _context;
        private MainForm host;

        // Handles to the currently-running event-driven engines so the shared Cancel
        // button (and Detach) can Stop() them. _lastTestRunner is retained after a run
        // finishes so "Run Failed Tests" can query its FailedIds().
        private TestRunner _activeTestRunner;
        private TestRunner _lastTestRunner;
        private PythonProfiler _activeProfiler;

        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        // On teardown, stop any in-flight test run / profiler so their background
        // processes don't outlive the form. Swallow errors — we're shutting down.
        public void Detach()
        {
            try { if (_activeTestRunner != null) _activeTestRunner.Stop(); } catch { }
            try { if (_activeProfiler != null) _activeProfiler.Stop(); } catch { }
            _context = null;
            host = null;
        }

        // Marshal an action to the UI thread, but safely: if the host form is gone or its window
        // handle no longer exists (the app is closing while a background engine is still raising
        // events), drop the callback instead of throwing. A raw BeginInvoke here would crash the
        // process from the engine's worker thread during shutdown.
        private void UiPost(Action action)
        {
            Form h = host;
            if (h == null || h.IsDisposed || !h.IsHandleCreated) return;
            // ObjectDisposedException derives from InvalidOperationException, so one catch covers
            // both: the handle can be destroyed between the guard above and the BeginInvoke call.
            try { h.BeginInvoke(action); }
            catch (System.InvalidOperationException) { }
        }

        // ------------------------------------------------------------------ Lint

        // Analysis ▸ Lint. Reads every solution file and runs the linter on a background
        // job; the completion callback (marshalled to the UI thread by _jobRunner)
        // populates the Lint panel. Guarded so it no-ops when a job is already running.
        /// <summary>
        /// Languages the Analysis ▸ Lint command can actually lint. Each has a complete native
        /// linter that was already CLI-reachable (`&lt;lang&gt;-lint`) and drives the editor's squiggles —
        /// only the menu wiring was missing. Plain C++ is absent on purpose: there is no C++ linter,
        /// and offering the command would hand the user something that can only report nothing.
        /// </summary>
        internal static bool CanLintExtension(string ext)
        {
            switch (ext)
            {
                // The extensions HelixPython actually has a linter for: Python, plus the native
                // sources the Convert-to-C/C++/CUDA commands emit. .rs/.cs/.java/.jl linting ships
                // in those languages' own products, so claiming it here would grey-in a menu
                // command that then found no engine.
                case ".py": case ".cu": case ".cuh":
                case ".cpp": case ".cc": case ".cxx": case ".h": case ".hh": case ".hpp": case ".hxx":
                    return true;
                default: return false;
            }
        }

        public void OnRunLint(object sender, EventArgs e)
        {
            string wsExt = ActiveWorkspaceExt();
            if (wsExt != null && CanLintExtension(wsExt)) { RunWorkspaceLint(wsExt); return; }

            List<WorkspaceFile> files = GetSolutionFiles();
            if (files == null) return;
            if (host._jobRunner.IsBusy) return;

            // First delegate = background work (returns lint rows); second = UI callback.
            host._jobRunner.Run(
                "Linting " + files.Count + " file(s)",
                token =>
                {
                    var linter = new PythonLinter();
                    var rows = new List<LintResultsPanel.LintRow>();
                    foreach (WorkspaceFile wf in files)
                    {
                        // Check cancellation each iteration so Cancel is responsive on
                        // large solutions; skip files that can't be read rather than abort.
                        token.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(wf.FilePath)) continue;
                        string source;
                        try { source = File.ReadAllText(wf.FilePath); }
                        catch { continue; }
                        foreach (LintFinding f in linter.Run(source, wf.FilePath, token))
                            rows.Add(new LintResultsPanel.LintRow(wf.FilePath, f));
                    }
                    return rows;
                },
                rows =>
                {
                    host._lintPanel.Show(rows);
                    host.ShowTab("lint");
                    host._statusLabel.Text = string.Format("Lint: {0} finding(s) in {1} file(s)", rows.Count, files.Count);
                });
        }

        /// <summary>
        /// Analysis ▸ Lint for a non-Python language: run that language's own native linter over
        /// every matching source file in the solution, into the SAME Lint panel Python uses.
        /// </summary>
        private void RunWorkspaceLint(string ext)
        {
            if (host._jobRunner.IsBusy) return;
            List<string> paths = EnumerateWorkspaceSourceFiles(ext);
            if (paths.Count == 0)
            {
                host._statusLabel.Text = "Lint: no " + ext + " files found in the solution";
                return;
            }

            string label = LanguageLabel(ext);
            host._jobRunner.Run("Linting " + paths.Count + " " + label + " file(s)",
                token =>
                {
                    var rows = new List<LintResultsPanel.LintRow>();
                    foreach (string p in paths)
                    {
                        token.ThrowIfCancellationRequested();
                        string src;
                        try { src = File.ReadAllText(p); } catch { continue; }
                        foreach (LintFinding f in LintOne(p, src, ext))
                            rows.Add(new LintResultsPanel.LintRow(p, f));
                    }
                    return rows;
                },
                rows =>
                {
                    host._lintPanel.Show(rows);
                    host.ShowTab("lint");
                    host._statusLabel.Text = string.Format("{0} lint: {1} finding(s) in {2} file(s)",
                        label, rows.Count, paths.Count);
                });
        }

        // One file through its language's linter, adapted to the shared LintFinding shape.
        private static List<LintFinding> LintOne(string path, string src, string ext)
        {
            var findings = new List<LintFinding>();
            switch (ext)
            {
                case ".cu":
                case ".cuh":
                    // CUDA lint rides the virtual compiler with the lint pass switched on — the same
                    // CU60xx rules the editor shows, but solution-wide instead of per-buffer.
                    var opts = HelixPythonNamespace.CudaVirtualCompiler.CudaCompileOptions.Default();
                    opts.EnableLint = true;
                    var res = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler.CompileSource(src, path, opts);
                    foreach (var d in res.Diagnostics)
                        findings.Add(Adapt(d.Code, d.Message, d.Severity.ToString(),
                            d.Span.Start.Line, d.Span.Start.Column));
                    break;
                case ".cpp": case ".cc": case ".cxx":
                case ".h": case ".hh": case ".hpp": case ".hxx":
                    findings.AddRange(LintCpp(path, src));
                    break;
            }
            return findings;
        }

        /// <summary>
        /// C++ lint = the C++-specific rules (CppLinter, CPL0xx) PLUS the language-general body
        /// rules the shared C++/CUDA front end already computes for any translation unit
        /// (CU6020 unreachable code, CU6021 unused local, CU6030/CU6031 static array bounds).
        ///
        /// Only CU6xxx is kept from that pass: a .cpp routinely uses headers this parser was not
        /// given, so its parse/name-resolution diagnostics would be false positives in a lint report.
        /// Real syntax errors are the editor's job, and it already shows them.
        /// </summary>
        private static List<LintFinding> LintCpp(string path, string src)
        {
            var findings = new List<LintFinding>();
            foreach (var f in HelixPythonNamespace.Workspace.Cpp.CppLinter.Lint(src, path))
                findings.Add(Adapt(f.Code, f.Message, f.Severity.ToString(), f.Line, f.Column));

            try
            {
                var opts = new HelixPythonNamespace.CudaVirtualCompiler.CudaCompileOptions
                {
                    EnableLexParse = true,
                    EnableAstValidator = false,
                    EnableSymtablePass = true,
                    EnableQualifierSync = false,   // CUDA qualifiers — meaningless in plain C++
                    EnableLaunchConfig = false,    // no kernels to launch
                    EnableMemorySpace = false,     // no device memory spaces
                    EnableLint = true,             // the general body rules we want
                    EnableLanguageStandard = false,
                    EnableNameResolution = false,  // would need the full include graph
                    EnableIncludeResolution = false,
                };
                var res = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler.CompileSource(src, path, opts);
                foreach (var d in res.Diagnostics)
                {
                    if (d.Code == null || !d.Code.StartsWith("CU6", StringComparison.Ordinal)) continue;
                    findings.Add(Adapt(d.Code, d.Message, d.Severity.ToString(),
                        d.Span.Start.Line, d.Span.Start.Column));
                }
            }
            catch { /* a file this front end cannot parse still keeps its CPL0xx findings */ }

            return findings;
        }

        private static LintFinding Adapt(string code, string message, string severity, int line, int column)
        {
            int ln = line > 0 ? line : 1;
            int col = column > 0 ? column : 1;
            var pos = new HelixPythonNamespace.Python.Text.SourcePosition(0, ln, col);
            return new LintFinding
            {
                Code = code ?? string.Empty,
                Message = message ?? string.Empty,
                Severity = (severity ?? string.Empty).IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                    ? HelixPythonNamespace.Core.Model.Enums.AnalysisSeverity.Error
                    : HelixPythonNamespace.Core.Model.Enums.AnalysisSeverity.Warning,
                Span = new HelixPythonNamespace.Python.Text.SourceSpan(pos, pos),
            };
        }

        private static string LanguageLabel(string ext)
        {
            switch (ext)
            {
                case ".cu": case ".cuh": return "CUDA";
                case ".cpp": case ".cc": case ".cxx": case ".h": case ".hh": case ".hpp": case ".hxx": return "C++";
                default: return "Python";
            }
        }

        /// <summary>
        /// Every source file of one language in the open solution. GetSolutionFiles() is the PYTHON
        /// enumerator (it collects .py only), so the non-Python engines cannot use it — they walk the
        /// solution/CMake root instead, skipping build trees so a configured project does not lint
        /// its own generated and fetched dependencies.
        /// </summary>
        private List<string> EnumerateWorkspaceSourceFiles(string ext)
        {
            var found = new List<string>();
            string root = host._currentCMakeFolder;
            if (string.IsNullOrEmpty(root) && host._currentSolution != null)
                root = host._currentSolution.SolutionDirectory;

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                string active = host.ActiveEditorFilePath();
                if (!string.IsNullOrEmpty(active)) found.Add(active);
                return found;
            }

            // Languages whose code is split across source AND header files must sweep both, or the
            // header-only rules can never fire: CUDA headers carry kernels, and for C++ the include
            // guard rule (CPL001) and the header using-namespace rule (CPL002) apply to .hpp/.h
            // exclusively. Linting only ".cpp" made an injected faulty header report nothing.
            string[] wanted;
            switch (ext)
            {
                case ".cu": case ".cuh":
                    wanted = new[] { ".cu", ".cuh" };
                    break;
                case ".cpp": case ".cc": case ".cxx":
                case ".h": case ".hh": case ".hpp": case ".hxx":
                    wanted = new[] { ".cpp", ".cc", ".cxx", ".h", ".hh", ".hpp", ".hxx" };
                    break;
                default:
                    wanted = new[] { ext };
                    break;
            }
            foreach (string want in wanted)
            {
                string[] hits;
                try { hits = Directory.GetFiles(root, "*" + want, SearchOption.AllDirectories); }
                catch { continue; }
                foreach (string f in hits)
                    if (!IsGeneratedOrDependencyPath(f) && !found.Contains(f)) found.Add(f);
            }
            return found;
        }

        private static readonly string[] SkipPathSegments =
        {
            "cmake-build", "\\build\\", "\\target\\", "\\bin\\", "\\obj\\", "\\_deps\\",
            "\\CMakeFiles\\", "\\node_modules\\", "\\.git\\", "\\venv\\", "\\.venv\\",
        };

        private static bool IsGeneratedOrDependencyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string p = path.Replace('/', '\\');
            foreach (string seg in SkipPathSegments)
                if (p.IndexOf(seg, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // -------------------------------------------------------------- Dead code

        // Analysis ▸ Find Dead Code. Needs a full compile+link pass first: virtual-compile
        // every file to ASTs, feed them to the linker to resolve cross-module references,
        // then let the dead-code engine flag unreachable symbols.
        public void OnFindDeadCode(object sender, EventArgs e)
        {
            List<WorkspaceFile> files = GetSolutionFiles();
            if (files == null) return;
            if (host._jobRunner.IsBusy) return;

            host._jobRunner.Run(
                "Finding dead code",
                token =>
                {
                    // Compile -> adapt results to linker inputs -> link -> analyze.
                    IReadOnlyList<VcCompileFileResult> compiled = VirtualCompiler.CompileSolution(files, null, token);
                    List<LinkerCompileFileResult> linkerInputs = ToLinkerInputs(compiled);
                    token.ThrowIfCancellationRequested();
                    LinkResult link = PythonLinker.Link(linkerInputs, null, null, token);
                    return new DeadCodeEngine().Analyze(linkerInputs, link, token);
                },
                report =>
                {
                    host._deadCodePanel.Show(report);
                    host.ShowTab("deadcode");
                    host._statusLabel.Text = string.Format("Dead code: {0} finding(s) in {1} file(s)",
                        report.Findings.Count, report.FilesAnalyzed);
                });
        }

        // ---------------------------------------------------------------- Metrics

        // Metrics ▸ Compute Metrics. Reads each file's source into a MetricsSource and
        // runs the metrics engine on a background job, then shows the Metrics panel.
        // FR-METR/PROF/TEST in-app routing: when the active editor file is a non-Python workspace language
        // (.rs/.cs/.java/.jl/.cu), the Metrics/Profiler/Tests menus drive that language's native engine/runner
        // into the SAME panels — mirroring how the debugger routes by extension. Python keeps its existing path.
        /// <summary>
        /// The non-Python language whose engines this command should use, or null for Python (the
        /// callers' fall-through). Delegates to the same resolution the menus gate on so a command
        /// can never disagree with the menu that offered it.
        ///
        /// It must NOT be resolved by sniffing GetSolutionFiles(): that is the Python enumerator
        /// (.py only), so a C++/CUDA solution with no file open found nothing, returned null,
        /// and the command silently ran the Python engine over an empty file list — Lint reported
        /// 0 findings on a loaded C++ solution that a CLI sweep of the same files flagged 300+ times.
        /// </summary>
        private string ActiveWorkspaceExt()
        {
            string ext = CurrentLanguageExtension();
            return ext == ".py" ? null : ext;
        }

        // The extension, if it is one of the non-Python workspace languages this IDE analyses.
        // Plain C++ (.cpp/.cc/.cxx/.h/.hh/.hxx) belongs here as much as CUDA does:
        // LanguageMetricsEngine.ForExtension has always handled it, but this list didn't offer it,
        // so a C++ file fell through to the Python path.
        private static string ExtIfWorkspaceLanguage(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string ext;
            try { ext = (System.IO.Path.GetExtension(filePath) ?? string.Empty).ToLowerInvariant(); }
            catch { return null; }
            switch (ext)
            {
                case ".rs": case ".cs": case ".java": case ".jl":
                case ".cu": case ".cuh":
                case ".cpp": case ".cc": case ".cxx": case ".h": case ".hh": case ".hpp": case ".hxx":
                    return ext;
                default: return null;
            }
        }

        // The dominant non-Python extension among the open solution's files, or null for a Python
        // solution. Counted rather than first-found so one stray header can't decide the language.
        private string SolutionWorkspaceExt()
        {
            List<WorkspaceFile> files;
            try { files = GetSolutionFiles(); } catch { return null; }
            if (files == null || files.Count == 0) return null;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (WorkspaceFile wf in files)
            {
                string ext = ExtIfWorkspaceLanguage(wf.FilePath);
                if (ext == null) continue;
                int n; counts.TryGetValue(ext, out n); counts[ext] = n + 1;
            }
            string best = null; int bestCount = 0;
            foreach (var kv in counts)
                if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
            return best;
        }

        public void OnComputeMetrics(object sender, EventArgs e)
        {
            string wsExt = ActiveWorkspaceExt();
            if (wsExt != null) { ComputeWorkspaceMetrics(host.ActiveEditorFilePath(), wsExt); return; }

            List<WorkspaceFile> files = GetSolutionFiles();
            if (files == null) return;
            if (host._jobRunner.IsBusy) return;

            host._jobRunner.Run(
                "Computing metrics",
                token =>
                {
                    var sources = new List<MetricsSource>();
                    foreach (WorkspaceFile wf in files)
                    {
                        token.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(wf.FilePath)) continue;
                        try { sources.Add(new MetricsSource(wf.FilePath, File.ReadAllText(wf.FilePath))); }
                        catch { }
                    }
                    return new MetricsEngine().Analyze(sources, token);
                },
                report =>
                {
                    host._metricsPanel.Show(report);
                    host.ShowTab("metrics");
                    host._statusLabel.Text = string.Format("Metrics: {0} file(s), {1} class(es), {2} function(s)",
                        report.Files.Count, report.Classes.Count, report.Functions.Count);
                });
        }

        // Native (non-Python) metrics: run LanguageMetricsEngine over the active file + its same-extension
        // siblings in the solution, into the same Metrics panel.
        private void ComputeWorkspaceMetrics(string activeFile, string ext)
        {
            if (host._jobRunner.IsBusy) return;
            var profile = HelixPythonNamespace.Metrics.LanguageMetricsEngine.ForExtension(ext);
            if (profile == null) { host._statusLabel.Text = "Metrics: no engine for " + ext; return; }

            // Walk the solution for this language's files. This used to seed from the active editor
            // and then top up from GetSolutionFiles() — the PYTHON enumerator — so a C++/CUDA
            // solution with no file open measured nothing at all.
            var paths = new List<string>();
            if (!string.IsNullOrEmpty(activeFile) && File.Exists(activeFile)) paths.Add(activeFile);
            foreach (string p in EnumerateWorkspaceSourceFiles(ext))
                if (!paths.Contains(p)) paths.Add(p);
            if (paths.Count == 0)
            {
                host._statusLabel.Text = "Metrics: no " + ext + " files found in the solution";
                return;
            }
            host._jobRunner.Run(profile.Name + " metrics",
                token =>
                {
                    var overall = new MetricsReport();
                    foreach (string p in paths)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var rep = HelixPythonNamespace.Metrics.LanguageMetricsEngine.Analyze(new MetricsSource(p, File.ReadAllText(p)), profile);
                            overall.Files.AddRange(rep.Files); overall.Functions.AddRange(rep.Functions); overall.Classes.AddRange(rep.Classes);
                        }
                        catch { }
                    }
                    return overall;
                },
                report =>
                {
                    host._metricsPanel.Show(report);
                    host.ShowTab("metrics");
                    host._statusLabel.Text = string.Format("{0} metrics: {1} file(s), {2} function(s)", profile.Name, report.Files.Count, report.Functions.Count);
                });
        }

        // ------------------------------------------------------------- Complexity

        // Analyze the solution and report all supported complexity cases.
        public void OnAnalyzeComplexity()
        {
            List<WorkspaceFile> files = GetSolutionFiles();
            if (files == null) return;
            if (host._jobRunner.IsBusy) return;

            host._jobRunner.Run(
                "Analyzing asymptotic complexity",
                token =>
                {
                    var sources = new List<HelixPythonNamespace.Complexity.ComplexitySource>();
                    foreach (WorkspaceFile wf in files)
                    {
                        token.ThrowIfCancellationRequested();
                        if (string.IsNullOrEmpty(wf.FilePath)) continue;
                        try { sources.Add(new HelixPythonNamespace.Complexity.ComplexitySource(wf.FilePath, File.ReadAllText(wf.FilePath))); }
                        catch { }
                    }
                    return new HelixPythonNamespace.Complexity.ComplexityEngine().Analyze(sources, token);
                },
                report =>
                {
                    host._complexityPanel.Show(report);
                    host.ShowTab("complexity");
                    host._statusLabel.Text = string.Format("Asymptotic complexity: {0} function(s) analyzed.",
                        report.Findings.Count);
                });
        }

        // ------------------------------------------------------------ Code Review

        /// <summary>
        /// Analysis ▸ Code Review. Three doorways into one engine, differing only in WHAT is
        /// submitted — which is the whole distinction between a review and a lint pass: a reviewer
        /// judges a body of work, and "the whole solution", "this file" and "what I changed" are
        /// three different bodies of work with three different verdicts.
        /// </summary>
        public void OnRunCodeReview(CodeReviewScope scope)
        {
            if (host._jobRunner.IsBusy) return;

            string label;
            List<HelixPythonNamespace.CodeReview.ReviewSource> sources;
            var options = HelixPythonNamespace.CodeReview.ReviewOptions.Default;

            switch (scope)
            {
                case CodeReviewScope.ActiveFile:
                    sources = ActiveFileReviewSource(out label);
                    break;
                case CodeReviewScope.UncommittedChanges:
                    // Only here is DemoteUnchangedFindings on: the point of reviewing a change is
                    // to lead with what the author wrote, while still showing what surrounds it.
                    options.DemoteUnchangedFindings = true;
                    sources = ChangedFileReviewSources(out label);
                    break;
                default:
                    sources = SolutionReviewSources(out label);
                    break;
            }
            if (sources == null) return;   // the helper already explained why on the status bar

            host._jobRunner.Run(
                "Reviewing " + label,
                token => new HelixPythonNamespace.CodeReview.CodeReviewEngine().Review(sources, options, label, token),
                (HelixPythonNamespace.CodeReview.ReviewReport report) =>
                {
                    host._codeReviewPanel.Show(report);
                    host.ShowTab("codeReview");
                    host._statusLabel.Text = string.Format("Code review ({0}): {1} — {2} finding(s) in {3} file(s){4}",
                        label,
                        HelixPythonNamespace.CodeReview.ReviewMarkdownWriter.VerdictText(report.Verdict),
                        report.Findings.Count,
                        report.Summary.FilesReviewed,
                        report.IsCompleteReview ? string.Empty : "; review INCOMPLETE");
                });
        }

        private List<HelixPythonNamespace.CodeReview.ReviewSource> SolutionReviewSources(out string label)
        {
            label = "whole solution";
            List<WorkspaceFile> files = GetSolutionFiles();
            if (files == null) return null;

            var sources = new List<HelixPythonNamespace.CodeReview.ReviewSource>();
            foreach (WorkspaceFile file in files)
            {
                if (string.IsNullOrEmpty(file.FilePath)) continue;
                try { sources.Add(new HelixPythonNamespace.CodeReview.ReviewSource(file.FilePath, File.ReadAllText(file.FilePath))); }
                catch { /* an unreadable file is reported by the engine as unreviewed */ }
            }
            return sources;
        }

        private List<HelixPythonNamespace.CodeReview.ReviewSource> ActiveFileReviewSource(out string label)
        {
            label = "active file";
            string path = host.ActiveEditorFilePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                host._statusLabel.Text = "Code review: no file is open in the editor";
                return null;
            }
            if (!string.Equals(Path.GetExtension(path), ".py", StringComparison.OrdinalIgnoreCase))
            {
                host._statusLabel.Text = "Code review: the review engine reads Python (" +
                    Path.GetExtension(path) + " is not supported)";
                return null;
            }
            label = Path.GetFileName(path);
            try
            {
                return new List<HelixPythonNamespace.CodeReview.ReviewSource>
                {
                    new HelixPythonNamespace.CodeReview.ReviewSource(path, File.ReadAllText(path)),
                };
            }
            catch (Exception ex)
            {
                host._statusLabel.Text = "Code review: could not read " + Path.GetFileName(path) + " — " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// The Python files git reports as changed, each carrying the line ranges its diff touched.
        /// A file with no working-tree diff (staged-only, or renamed with no edit) still gets
        /// reviewed whole rather than being dropped: submitting a file and then attributing none of
        /// it would silently narrow the review.
        /// </summary>
        private List<HelixPythonNamespace.CodeReview.ReviewSource> ChangedFileReviewSources(out string label)
        {
            label = "uncommitted changes";
            string dir = SolutionDir();
            if (string.IsNullOrEmpty(dir)) { host._statusLabel.Text = "No solution loaded"; return null; }

            string repo = GitService.RepoWorkdir(dir);
            if (string.IsNullOrEmpty(repo))
            {
                host._statusLabel.Text = "Code review: this solution is not inside a git repository";
                return null;
            }

            List<GitStatusEntry> status;
            try { status = GitService.Status(repo); }
            catch (Exception ex)
            {
                host._statusLabel.Text = "Code review: git status failed — " + ex.Message;
                return null;
            }

            var sources = new List<HelixPythonNamespace.CodeReview.ReviewSource>();
            foreach (GitStatusEntry entry in status)
            {
                if (entry == null || string.IsNullOrEmpty(entry.FilePath)) continue;
                if (!entry.FilePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.WorkingStatus == GitFileStatus.Deleted || entry.IndexStatus == GitFileStatus.Deleted) continue;

                string absolute = Path.Combine(repo, entry.FilePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolute)) continue;

                string text;
                try { text = File.ReadAllText(absolute); }
                catch { continue; }

                // An untracked file is entirely new, so all of it is the change.
                IReadOnlyList<HelixPythonNamespace.CodeReview.LineRange> changed = null;
                if (entry.WorkingStatus != GitFileStatus.Untracked && entry.IndexStatus != GitFileStatus.Added)
                {
                    List<HelixPythonNamespace.CodeReview.LineRange> hunks = ChangedLineRanges(repo, entry.FilePath);
                    if (hunks != null && hunks.Count > 0) changed = hunks;
                }
                sources.Add(new HelixPythonNamespace.CodeReview.ReviewSource(absolute, text, changed));
            }

            if (sources.Count == 0)
            {
                host._statusLabel.Text = "Code review: no changed Python files in the working tree";
                return null;
            }
            label = "uncommitted changes (" + sources.Count + " file(s))";
            return sources;
        }

        /// <summary>
        /// The new-file line ranges a unified diff touches, read from its <c>@@</c> hunk headers.
        /// Returns null when the diff cannot be produced — which the caller must treat as "review
        /// the whole file", never as "nothing changed".
        /// </summary>
        internal static List<HelixPythonNamespace.CodeReview.LineRange> ChangedLineRanges(string repo, string relPath)
        {
            string diff;
            try { diff = GitService.DiffFile(repo, relPath); }
            catch { return null; }
            if (string.IsNullOrEmpty(diff)) return null;

            var ranges = new List<HelixPythonNamespace.CodeReview.LineRange>();
            foreach (string line in diff.Split('\n'))
            {
                if (!line.StartsWith("@@", StringComparison.Ordinal)) continue;
                int plus = line.IndexOf('+');
                if (plus < 0) continue;
                int end = line.IndexOf(' ', plus);
                if (end < 0) continue;

                string span = line.Substring(plus + 1, end - plus - 1);
                string[] parts = span.Split(',');
                int start, count = 1;
                if (!int.TryParse(parts[0], out start)) continue;
                if (parts.Length > 1 && !int.TryParse(parts[1], out count)) count = 1;
                if (count <= 0) continue;   // a pure deletion adds no reviewable lines
                ranges.Add(new HelixPythonNamespace.CodeReview.LineRange(start, start + count - 1));
            }
            return ranges;
        }

        /// <summary>
        /// Analysis ▸ Code Review ▸ Export — the last review as a Markdown write-up, which is the
        /// form that can be pasted into a pull request. Exports the REPORT rather than the grid:
        /// the rationale and the suggested change are the parts worth sending to someone.
        /// </summary>
        public void OnExportCodeReview()
        {
            HelixPythonNamespace.CodeReview.ReviewReport report =
                host._codeReviewPanel != null ? host._codeReviewPanel.LastReport : null;
            if (report == null)
            {
                host._statusLabel.Text = "Code review: nothing to export — run a review first";
                return;
            }

            using (var dialog = new SaveFileDialog
            {
                Title = "Export code review",
                Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
                FileName = "code-review.md",
                OverwritePrompt = true,
            })
            {
                if (dialog.ShowDialog(host) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dialog.FileName, HelixPythonNamespace.CodeReview.ReviewMarkdownWriter.Write(report));
                    host._statusLabel.Text = "Code review exported to " + Path.GetFileName(dialog.FileName);
                }
                catch (Exception ex)
                {
                    host._statusLabel.Text = "Code review export failed: " + ex.Message;
                }
            }
        }

        // Native (non-Python) profiler: run the real native sampling profiler for the active
        // file's language on a background job, into the same Profiler panel.
        private void ProfileWorkspaceFile(string file, string ext)
        {
            if (host._jobRunner.IsBusy) return;
            string dir = Path.GetDirectoryName(file);
            host._jobRunner.Run("Profiling " + Path.GetFileName(file),
                token =>
                {
                    // HelixPython's workspace-level profiler covers the native C/C++ the
                    // Convert-to-C/C++ commands emit; Python itself profiles through
                    // HelixPythonNamespace.Profiler (cProfile). The cargo/dotnet/jfr/julia samplers ship
                    // in their own Helix products.
                    HelixPythonNamespace.Workspace.Profiling.WorkspaceProfile wp =
                        HelixPythonNamespace.Workspace.Cpp.CppProfiler.Run(file);
                    return wp;
                },
                wp =>
                {
                    if (!wp.Succeeded) { host._statusLabel.Text = "Profiler: " + (wp.Reason ?? "no samples"); return; }
                    int total = wp.TotalSamples;
                    var records = new List<HelixPythonNamespace.Profiler.ProfileRecord>();
                    foreach (var s in wp.Samples)
                    {
                        double secs = total > 0 && wp.WallSeconds > 0 ? wp.WallSeconds * s.Samples / total : 0;
                        records.Add(new HelixPythonNamespace.Profiler.ProfileRecord { Name = s.Function, CallCount = s.Samples, TotalSeconds = secs, CumulativeSeconds = secs });
                    }
                    host._profilePanel.Show(records);
                    host.ShowTab("profile");
                    host._statusLabel.Text = string.Format("{0} profile: {1} hot function(s)", wp.Tool, records.Count);
                });
        }

        /// <summary>
        /// Profiler ▸ Profile Active File for a .cu/.cuh: the real nvcc <c>-Xptxas -v</c> pass giving
        /// per-kernel registers / shared memory / spills plus computed theoretical occupancy, into
        /// the Profiler panel's CUDA view. A .cu has no call-graph samples to show, so this is the
        /// meaningful "profile" for a kernel — it was implemented and CLI-reachable but never wired
        /// to the menu.
        /// </summary>
        private void ProfileCudaKernels(string cuFile)
        {
            if (host._jobRunner.IsBusy) return;
            host._jobRunner.Run("Profiling CUDA kernels in " + Path.GetFileName(cuFile),
                token => HelixPythonNamespace.Workspace.Cuda.CudaKernelProfiler.Run(cuFile),
                (HelixPythonNamespace.Workspace.Cuda.CudaProfileResult result) =>
                {
                    if (result == null || !result.Succeeded)
                    {
                        host._statusLabel.Text = "CUDA profiler: " +
                            (result != null && !string.IsNullOrEmpty(result.Reason) ? result.Reason : "no kernels found");
                        return;
                    }
                    var rows = new List<HelixPythonNamespace.App.Controls.ProfilePanel.CudaKernelRow>();
                    foreach (var k in result.Kernels)
                        rows.Add(new HelixPythonNamespace.App.Controls.ProfilePanel.CudaKernelRow
                        {
                            Kernel = k.Name, Arch = k.Arch, Registers = k.Registers,
                            SharedMemBytes = k.SharedMemBytes, Spills = k.SpillStores + k.SpillLoads,
                            OccupancyPercent = k.OccupancyPercent, ActiveWarps = k.ActiveWarpsPerSm,
                            MaxWarps = k.MaxWarpsPerSm, Limiter = k.Limiter,
                        });
                    host._profilePanel.ShowCudaKernels(rows);
                    host.ShowTab("profile");
                    host._statusLabel.Text = string.Format("CUDA profile: {0} kernel(s) on {1}",
                        rows.Count, result.Arch);
                });
        }

        /// <summary>
        /// The extension whose language's engines apply to the current context: the active document
        /// when it is a known source file, otherwise the open solution's dominant language, and
        /// ".py" as the last resort. This is what the Analysis/Metrics/Profiler menus gate on.
        /// </summary>
        internal string CurrentLanguageExtension()
        {
            if (host == null) return ".py";
            // 1. An open document is the strongest signal — the user is looking at it.
            string ext = ExtIfWorkspaceLanguage(host.ActiveEditorFilePath());
            if (ext != null) return ext;
            // 2. The solution's DECLARED language. This must come before sniffing files: the file
            //    sniffer walks GetSolutionFiles(), which is the Python enumerator (.py only), so a
            //    CUDA or C++ solution found nothing and fell through to ".py" — which then offered
            //    Dead Code and Complexity on a C++/CUDA workspace, the exact dead commands this
            //    gating exists to prevent.
            ext = SolutionLanguageExtension();
            if (ext != null) return ext;
            // 3. Last resort: whatever extension dominates on disk.
            try { ext = SolutionWorkspaceExt(); } catch { ext = null; }
            return ext ?? ".py";
        }

        /// <summary>
        /// The open solution's own language, from the model the loader populated (so it holds even
        /// before any file is opened, and without touching the disk). Null when nothing is open.
        /// </summary>
        private string SolutionLanguageExtension()
        {
            var solution = host._currentSolution;
            if (solution == null || solution.Projects == null || solution.Projects.Count == 0)
                return host.IsNativeWorkspaceActive() ? ".cu" : null;

            var counts = new Dictionary<HelixPythonNamespace.Core.Model.Enums.ProjectLanguage, int>();
            bool anyCuda = false;
            foreach (var p in solution.Projects)
            {
                if (p == null) continue;
                int n; counts.TryGetValue(p.Language, out n); counts[p.Language] = n + 1;
                if (p.HasCudaSources) anyCuda = true;
            }
            if (counts.Count == 0) return null;

            var best = HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Python;
            int bestCount = -1;
            foreach (var kv in counts)
                if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }

            switch (best)
            {
                // ProjectLanguage.Cpp covers BOTH plain C++ and CUDA (see its doc comment); which
                // one this solution is decides whether CUDA-only commands like Lint apply.
                case HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp: return anyCuda ? ".cu" : ".cpp";
                default: return ".py";
            }
        }

        /// <summary>
        /// Profiler ▸ Profile Active File for a plain C++ file: build the target the file belongs to
        /// with the real CMake toolchain, then sample the resulting executable. C++ is the one
        /// language whose profiler needs a build first and cannot name its own binary, so the
        /// orchestration lives here (where the CMake build and target lookup already are) and the
        /// engine — CppProfiler — only samples.
        /// </summary>
        private void ProfileCppExecutable(string sourceFile)
        {
            if (host._jobRunner.IsBusy) return;
            if (string.IsNullOrEmpty(host._currentCMakeFolder))
            {
                host._statusLabel.Text = "Profiler: open the C++ project's CMake folder first";
                return;
            }

            string target = NativeTargetForSource(sourceFile);
            if (target == null)
            {
                host._statusLabel.Text = "Profiler: no executable CMake target owns " + Path.GetFileName(sourceFile);
                return;
            }

            // Build first — the sampler needs a real binary with a .pdb beside it. This is the same
            // pumped build the Build menu runs, so it takes the same busy gate.
            if (!host.BeginNativeBuildJob("Building " + target + " to profile", "==> Profile " + target)) return;
            HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult build;
            try { build = host.BuildNativeCMakeTarget(target, clearOutput: false); }
            finally { host.EndNativeBuildJob(host._statusLabel != null ? host._statusLabel.Text : "Build finished"); }
            if (build == null || !build.Succeeded)
            {
                host._statusLabel.Text = "Profiler: the build failed — see Output.";
                return;
            }

            string exe = MainForm.FindCMakeExecutable(host.CMakeBuildDirectory(host._currentCMakeFolder), target);
            if (string.IsNullOrEmpty(exe))
            {
                host._statusLabel.Text = "Profiler: built executable for '" + target + "' not found";
                return;
            }

            host._jobRunner.Run("Profiling " + Path.GetFileName(exe),
                token => HelixPythonNamespace.Workspace.Cpp.CppProfiler.Run(exe),
                (HelixPythonNamespace.Workspace.Profiling.WorkspaceProfile wp) =>
                {
                    if (wp == null || !wp.Succeeded)
                    {
                        host._statusLabel.Text = "Profiler: " +
                            (wp != null && !string.IsNullOrEmpty(wp.Reason) ? wp.Reason : "no samples");
                        return;
                    }
                    int total = wp.TotalSamples;
                    var records = new List<HelixPythonNamespace.Profiler.ProfileRecord>();
                    foreach (var s in wp.Samples)
                    {
                        double secs = total > 0 && wp.WallSeconds > 0 ? wp.WallSeconds * s.Samples / total : 0;
                        records.Add(new HelixPythonNamespace.Profiler.ProfileRecord
                        { Name = s.Function, CallCount = s.Samples, TotalSeconds = secs, CumulativeSeconds = secs });
                    }
                    host._profilePanel.Show(records);
                    host.ShowTab("profile");
                    host._statusLabel.Text = string.Format("C++ profile: {0} hot function(s){1}",
                        records.Count, string.IsNullOrEmpty(wp.Reason) ? "" : " — " + wp.Reason);
                });
        }

        /// <summary>The executable CMake target whose sources include this file, or null.</summary>
        private string NativeTargetForSource(string sourceFile)
        {
            if (host._currentCMakeProject == null || host._currentCMakeProject.Targets == null) return null;
            string full;
            try { full = Path.GetFullPath(sourceFile); } catch { return null; }

            string fallback = null;
            foreach (var t in host._currentCMakeProject.Targets)
            {
                if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                if (t.Name == HelixPythonNamespace.Workspace.Cuda.CMakeConventions.UnassignedSourcesTargetName) continue;
                bool executable = t.Kind == HelixPythonNamespace.Workspace.Cuda.CMakeTargetKind.Executable;
                if (!executable) continue;
                if (fallback == null) fallback = t.Name;
                if (t.SourceFiles == null) continue;
                foreach (string s in t.SourceFiles)
                {
                    string sf; try { sf = Path.GetFullPath(s); } catch { continue; }
                    if (string.Equals(sf, full, StringComparison.OrdinalIgnoreCase)) return t.Name;
                }
            }
            // A header (or a source no target lists) still profiles usefully against the solution's
            // executable — that is the binary its code ends up in.
            return fallback;
        }

        /// <summary>Languages the Profiler menu can actually profile.</summary>
        internal static bool CanProfileExtension(string ext)
        {
            switch (ext)
            {
                case ".py": case ".java": case ".jl": case ".cs": case ".rs": case ".cu": case ".cuh":
                case ".cpp": case ".cc": case ".cxx": case ".h": case ".hh": case ".hpp": case ".hxx":
                    return true;
                default: return false;
            }
        }

        private static string FindProjectDir(string sourceFile, string pattern)
        {
            try
            {
                var d = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourceFile)));
                while (d != null) { if (d.GetFiles(pattern).Length > 0) return d.FullName; d = d.Parent; }
            }
            catch { }
            return Path.GetDirectoryName(Path.GetFullPath(sourceFile));
        }

        // --------------------------------------------------------------- Profiler

        // Profiler ▸ Profile Active File. Unlike the blocking engines above, the profiler
        // spawns an external Python process and reports asynchronously via its Finished
        // event, so it registers with _jobRunner.BeginExternal (not Run) and the shared
        // Cancel button is wired to profiler.Stop().
        public void OnProfileActiveFile(object sender, EventArgs e)
        {
            // Needs a real file open in the editor and a usable interpreter.
            string file = host.ActiveEditorFilePath();
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                host._statusLabel.Text = "Profiler: open a Python file in the editor first";
                return;
            }
            string wsExt = ExtIfWorkspaceLanguage(file);   // the OPEN file decides, not the solution
            if (wsExt == ".java" || wsExt == ".jl" || wsExt == ".cs" || wsExt == ".rs")
            { ProfileWorkspaceFile(file, wsExt); return; }
            if (wsExt == ".cu" || wsExt == ".cuh") { ProfileCudaKernels(file); return; }
            if (wsExt == ".cpp" || wsExt == ".cc" || wsExt == ".cxx" ||
                wsExt == ".h" || wsExt == ".hh" || wsExt == ".hpp" || wsExt == ".hxx")
            { ProfileCppExecutable(file); return; }
            string interpreter = ResolveInterpreter();
            if (interpreter == null) return;
            if (host._jobRunner.IsBusy) return;

            // Finished fires on a background thread — marshal to the UI via BeginInvoke.
            var profiler = new PythonProfiler();
            _activeProfiler = profiler;
            profiler.Finished += (s, args) => host.BeginInvoke(new Action(() =>
            {
                _activeProfiler = null;
                if (!string.IsNullOrEmpty(args.ErrorMessage))
                {
                    host._jobRunner.Finish("Profiler failed: " + args.ErrorMessage);
                    return;
                }
                // Always Finish the external job, even if populating the panel throws — otherwise a
                // render error would leave the job stuck "busy" and the UI frozen at "Profiling…".
                string done = string.Format("Profiler: {0} function(s)", args.Records.Count);
                try
                {
                    host._profilePanel.Show(args.Records);
                    host.ShowTab("profile");
                }
                catch (Exception ex)
                {
                    done = "Profiler: panel error: " + ex.Message;
                }
                finally
                {
                    host._jobRunner.Finish(done);
                }
            }));

            // Mark the runner busy with a cancel action, then launch the profiler process.
            // Order matters: register the external job before Start so an immediate
            // Finished still finds the job in a "busy" state to Finish.
            host._jobRunner.BeginExternal("Profiling " + Path.GetFileName(file), () => { try { profiler.Stop(); } catch { } });
            // Same PYTHONPATH Run/Debug use, so the profiled file's own top-level absolute imports
            // of sibling source roots resolve instead of raising ImportError (the file runs fine via
            // Run, which sets this env, but previously failed here because Start() never did).
            var env = host._currentSolution != null ? host.BuildPythonPathEnv(host._currentSolution) : null;
            // Resolve the file's owning project (if any) so it can run as a real package submodule
            // (`python -m pkg.chain`) instead of a bare script — needed for the file's own genuine
            // relative imports (`from . import x`) to resolve; a bare `python file.py` (or
            // runpy.run_path) never sets __package__ at all, no matter what PYTHONPATH is.
            string moduleName = null;
            if (host._currentSolution != null && host._currentSolution.Projects != null)
            {
                foreach (var proj in host._currentSolution.Projects)
                {
                    moduleName = MainForm.ResolveModuleName(proj, file);
                    if (moduleName != null) break;
                }
            }
            // Same working directory as Run and Debug — the file's PROJECT folder, not the file's
            // own folder, so a profiled run resolves relative paths exactly as a normal run does.
            string workingDir = host.ResolveWorkingDirectoryForScript(host._currentSolution, file)
                ?? Path.GetDirectoryName(file);
            profiler.Start(interpreter, file, workingDir, env, moduleName);
        }

        // ------------------------------------------------------------------ Tests

        // null targets = run the whole suite.
        public void OnRunAllTests(object sender, EventArgs e) { StartTests(null); }

        /// <summary>
        /// "Run Tests" on a single test-package Solution Explorer node — pytest scoped to just that
        /// project's directory, reusing the same TestRunner/panel/job-runner plumbing as "Run All"
        /// (which always scopes to the whole solution). This is the per-project verb VS2022's MSTest
        /// runner has that the old whole-solution-only Tests menu didn't.
        /// </summary>
        public void RunTestsForProject(string testProjectDir)
        {
            if (string.IsNullOrEmpty(testProjectDir) || !Directory.Exists(testProjectDir))
            {
                host._statusLabel.Text = "Test project directory not found";
                return;
            }
            StartTests(null, testProjectDir);
        }

        // Re-runs only the tests that failed last time, pulled from the retained runner.
        public void OnRunFailedTests(object sender, EventArgs e)
        {
            List<string> failed = _lastTestRunner != null ? _lastTestRunner.FailedIds() : null;
            if (failed == null || failed.Count == 0)
            {
                host._statusLabel.Text = "No failed tests from the last run";
                return;
            }
            StartTests(failed);
        }

        // Cancels the in-flight run; the runner's Stopped event finishes the job.
        public void OnStopTests(object sender, EventArgs e)
        {
            if (_activeTestRunner != null) { try { _activeTestRunner.Stop(); } catch { } }
        }

        // Clears the Test Runner panel's node tree/summary. Does not touch _lastTestRunner,
        // so "Run Failed Tests" still works after the display is cleared.
        public void OnClearTests(object sender, EventArgs e)
        {
            host._testRunnerPanel.Clear();
        }

        // Resolve the active file through the registered test adapter and walk up to that
        // language's manifest. Adding a runner no longer requires another extension switch here.
        private bool TryWorkspaceTestProject(out string dir, out HelixPythonNamespace.Workspace.Testing.IWorkspaceTestAdapter adapter)
        {
            dir = null; adapter = null;
            string f = host.ActiveEditorFilePath();
            if (string.IsNullOrEmpty(f)) return false;
            adapter = HelixPythonNamespace.Workspace.Testing.WorkspaceTestAdapterRegistry.ForFilePath(f);
            if (adapter == null) return false;
            var descriptor = HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(adapter.Language);
            try
            {
                var d = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(f)));
                while (d != null)
                {
                    bool has = false;
                    foreach (string name in descriptor.SolutionManifestFileNames)
                        if (File.Exists(Path.Combine(d.FullName, name))) { has = true; break; }
                    if (!has)
                        foreach (string ext in descriptor.SolutionFileExtensions)
                            if (d.GetFiles("*" + ext).Length > 0) { has = true; break; }
                    if (has) { dir = d.FullName; return true; }
                    d = d.Parent;
                }
            }
            catch { }
            adapter = null;
            return false;
        }

        /// <summary>
        /// The open solution's own language, when it has a workspace test runner. Used when no
        /// editor file identifies a project — the solution, not the editor, is the authority for
        /// a solution-wide "Run All Tests".
        /// </summary>
        private bool TrySolutionTestLanguage(out string dir, out HelixPythonNamespace.Workspace.Testing.IWorkspaceTestAdapter adapter)
        {
            dir = null; adapter = null;
            var sol = host._currentSolution;
            if (sol == null || string.IsNullOrEmpty(sol.FilePath) || sol.Projects == null || sol.Projects.Count == 0)
                return false;

            HelixPythonNamespace.Core.Model.Enums.ProjectLanguage language = sol.Projects[0].Language;
            foreach (var p in sol.Projects)
                if (p != null && p.Language != language) return false;   // mixed: no single runner

            adapter = HelixPythonNamespace.Workspace.Testing.WorkspaceTestAdapterRegistry.For(language);
            if (adapter == null) return false;
            dir = Path.GetDirectoryName(Path.GetFullPath(sol.FilePath));
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
        }

        // Run the language's real test runner on a background job and upsert its cases into the Test Runner panel.
        private void RunWorkspaceTests(string projectDir, HelixPythonNamespace.Workspace.Testing.IWorkspaceTestAdapter adapter, bool wholeWorkspace = false)
        {
            if (adapter == null) return;
            if (host._jobRunner.IsBusy) return;
            host._testRunnerPanel.Clear();
            host.ShowTab("tests");
            host._jobRunner.Run(adapter.Id + " tests",
                token => adapter.Run(projectDir, wholeWorkspace),
                (HelixPythonNamespace.Workspace.Testing.WorkspaceTestRun run) =>
                {
                    if (!run.Available) { host._statusLabel.Text = "Tests: " + run.Reason; return; }
                    var nodes = new List<TestNode>();
                    foreach (var t in run.Tests)
                        nodes.Add(new TestNode
                        {
                            Id = t.Name, Display = t.Name,
                            Status = t.Outcome == "passed" ? TestStatus.Passed : t.Outcome == "failed" ? TestStatus.Failed : TestStatus.Skipped,
                            DurationSeconds = t.DurationSeconds,
                        });
                    host._testRunnerPanel.UpsertNodes(nodes);
                    host._testRunnerPanel.MarkRunComplete();
                    host._statusLabel.Text = string.Format("{0} tests: {1} passed, {2} failed", run.Tool, run.Passed, run.Failed);
                });
        }

        /// <summary>
        /// "Run Tests"/"Run All Tests" for a native C++/CUDA CMake workspace: build first
        /// (gtest_discover_tests registers its cases at BUILD time, so ctest sees nothing until
        /// the test executables exist), then run the REAL ctest and map its cases into the same
        /// Test Runner panel every other language uses. scopedProjectDir (a source directory of
        /// one test project) narrows the run to that project's own build subdirectory.
        /// </summary>
        internal void RunNativeCMakeTests(string scopedProjectDir = null)
        {
            if (host._jobRunner.IsBusy) return;
            if (host.IsActiveCMakeProjectUsingEmulator())
            {
                host._statusLabel.Text = "Native tests need a real cmake/ctest toolchain (Emulator is active).";
                return;
            }

            // The build phase pumps the message loop, so it must run behind the same busy gate the
            // Build menu commands use — otherwise the whole menu bar stays clickable and a second
            // build can start on top of this one. Released before the ctest phase, which claims the
            // job runner again with its own Cancel.
            if (!host.BeginNativeBuildJob("Building tests", null)) return;
            HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult build;
            try { build = host.BuildActiveCMakeProject(clearOutput: false) as HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult; }
            finally { host.EndNativeBuildJob(host._statusLabel != null ? host._statusLabel.Text : "Build finished"); }

            if (build == null || !build.Succeeded)
            {
                host._statusLabel.Text = "Tests not run: the native build failed — see Output.";
                return;
            }

            string buildDir = host.CMakeBuildDirectory(host._currentCMakeFolder);
            string ctestDir = buildDir;
            if (!string.IsNullOrEmpty(scopedProjectDir))
            {
                // The build tree mirrors the source tree, so the project's tests live in the
                // same relative subdirectory of the build dir (its own CTestTestfile.cmake).
                string root = Path.GetFullPath(host._currentCMakeFolder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(scopedProjectDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    ctestDir = Path.Combine(buildDir, full.Substring(root.Length + 1));
            }

            host._testRunnerPanel.Clear();
            host.ShowTab("tests");
            string sourceRoot = host._currentCMakeFolder;

            // Streamed, like the pytest path: rows land as ctest reports them rather than all at
            // once when the suite finishes. Runs as an EXTERNAL job (Cancel = kill ctest) because
            // the result arrives through a callback, not a return value.
            var cts = new System.Threading.CancellationTokenSource();
            host._jobRunner.BeginExternal("ctest", () => { try { cts.Cancel(); } catch { } });
            host.SetCommandsEnabledDuringNativeBuild(false);   // Stop Execution stays live

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                HelixPythonNamespace.Workspace.Testing.WorkspaceTestRun run = null;
                try
                {
                    // ctest output carries no source locations — index the workspace's
                    // TEST_CASE/TEST macros so each row gets a Line and double-click can
                    // navigate, same as pytest rows. Built BEFORE the run so a streamed row can
                    // be placed the moment it arrives.
                    Dictionary<string, HelixPythonNamespace.Workspace.Cuda.NativeTestLocation> locations;
                    try { locations = HelixPythonNamespace.Workspace.Cuda.NativeTestLocator.BuildIndex(sourceRoot); }
                    catch { locations = new Dictionary<string, HelixPythonNamespace.Workspace.Cuda.NativeTestLocation>(); }

                    int completed = 0;
                    run = HelixPythonNamespace.Workspace.Cuda.CTestRunner.Run(ctestDir, 180000, null,
                        t =>
                        {
                            HelixPythonNamespace.Workspace.Cuda.NativeTestLocation loc;
                            locations.TryGetValue(t.Name, out loc);
                            var node = new TestNode
                            {
                                Id = t.Name, Display = t.Name,
                                Status = t.Outcome == "passed" ? TestStatus.Passed
                                       : t.Outcome == "failed" ? TestStatus.Failed : TestStatus.Skipped,
                                DurationSeconds = t.DurationSeconds,
                                FilePath = loc != null ? loc.FilePath : "",
                                Line = loc != null ? loc.Line : 0,
                            };
                            int done = System.Threading.Interlocked.Increment(ref completed);
                            // ctest does not announce a total up front, so report progress against
                            // what has landed so far rather than inventing a denominator.
                            UiPost(() =>
                            {
                                host._testRunnerPanel.UpsertNodes(new[] { node });
                                host._statusLabel.Text = "ctest: " + done + " test(s) so far…";
                            });
                        },
                        cts.Token);
                }
                catch (Exception ex)
                {
                    UiPost(() => host._statusLabel.Text = "Tests: " + ex.Message);
                }

                var finished = run;
                UiPost(() =>
                {
                    host.SetCommandsEnabledDuringNativeBuild(true);
                    host._testRunnerPanel.MarkRunComplete();
                    host._statusLabel.Text = finished == null ? "Tests: ctest did not run"
                        : !finished.Available ? "Tests: " + finished.Reason
                        : string.Format("{0} tests: {1} passed, {2} failed",
                            finished.Tool, finished.Passed, finished.Failed);
                    host._jobRunner.Finish(host._statusLabel.Text);
                    try { cts.Dispose(); } catch { }
                });
            });
        }

        // Shared entry for both "run all" and "run failed". Sets up a TestRunner, wires
        // its streaming events (batches/progress/finish) back to the UI thread, and
        // launches it as an external job whose Cancel calls runner.Stop().
        // workingDirectoryOverride scopes the run to one test project's directory (RunTestsForProject)
        // instead of the whole solution — pytest's own recursive discovery then naturally only sees
        // that project's test_*.py files.
        private void StartTests(List<string> targets, string workingDirectoryOverride = null)
        {
            // A native C++/CUDA workspace routes to the real ctest pipeline — pytest below would
            // just discover nothing (or worse, collect .cpp files). This is what makes the Tests
            // menu honest for native solutions instead of hidden (see UpdateMenuWorkflowVisibility).
            if (host.IsNativeWorkspaceActive())
            {
                RunNativeCMakeTests(workingDirectoryOverride);
                return;
            }
            // In-app routing: when the active editor file belongs to a non-Python project, run that language's
            // real test runner (ctest) into the same Test Runner panel. Python pytest below.
            // Skipped for a project-scoped run (workingDirectoryOverride set) — that always targets a
            // specific Python test package the user picked from Solution Explorer, not whatever file
            // happens to be open in the editor.
            if (workingDirectoryOverride == null && TryWorkspaceTestProject(out string wsDir, out HelixPythonNamespace.Workspace.Testing.IWorkspaceTestAdapter wsAdapter))
            {
                RunWorkspaceTests(wsDir, wsAdapter);
                return;
            }

            // No relevant editor file — ask the SOLUTION rather than falling through to pytest.
            // Routing off "whatever happens to be open" is why a native solution with nothing open
            // reported Python results (or none).
            if (workingDirectoryOverride == null && TrySolutionTestLanguage(out string solDir, out HelixPythonNamespace.Workspace.Testing.IWorkspaceTestAdapter solAdapter))
            {
                RunWorkspaceTests(solDir, solAdapter, wholeWorkspace: true);
                return;
            }

            string dir = workingDirectoryOverride ?? SolutionDir();
            if (dir == null) { host._statusLabel.Text = "No solution loaded"; return; }
            string interpreter = ResolveInterpreter();
            if (interpreter == null) return;
            if (host._jobRunner.IsBusy) return;

            host._testRunnerPanel.Clear();
            host.ShowTab("tests");

            // Track as both the active runner (for cancel) and last runner (for re-run failed).
            var runner = new TestRunner();
            _activeTestRunner = runner;
            _lastTestRunner = runner;

            // All runner callbacks arrive off-thread; each hops to the UI via UiPost, which no-ops
            // if the form is closing/closed. Without that guard a late pytest-output flush (the
            // process keeps streaming for a beat after the window is gone) calls BeginInvoke on a
            // destroyed handle and crashes the whole app. Terminal events (StartFailed/Finished/
            // Stopped) clear _activeTestRunner and Finish the external job so the toolbar idles.
            runner.TestsBatch += batch => UiPost(() => host._testRunnerPanel.UpsertNodes(batch));
            runner.Progress += (pct, done, total) => UiPost(() => host._testRunnerPanel.UpdateSummary(pct, done, total));
            runner.StartFailed += msg => UiPost(() =>
            {
                _activeTestRunner = null;
                host._jobRunner.Finish("Tests failed to start: " + msg);
            });
            runner.Finished += exit => UiPost(() =>
            {
                _activeTestRunner = null;
                // Finalize the panel summary so a trailing Progress event can't leave it
                // stuck on "Running… 100%" once the run has actually completed.
                host._testRunnerPanel.MarkRunComplete();
                host._jobRunner.Finish(exit == 0 ? "Tests passed" : "Tests finished (exit " + exit + ")");
            });
            runner.Stopped += () => UiPost(() =>
            {
                _activeTestRunner = null;
                // Cancelled mid-run: drop the "Running… %" line back to the partial totals
                // instead of leaving it frozen at whatever progress was last reported.
                host._testRunnerPanel.MarkRunComplete();
                host._jobRunner.Finish("Tests cancelled");
            });

            // Build the pytest run request; Targets is only set for "run failed".
            var request = new TestRunRequest
            {
                Framework = TestFramework.Pytest,
                InterpreterPath = interpreter,
                WorkingDirectory = dir,
            };
            if (targets != null) request.Targets = targets;

            // Register as an external job (cancel = Stop) before launching.
            host._jobRunner.BeginExternal("Running tests", () => { try { runner.Stop(); } catch { } });
            runner.Start(request);
        }

        // -------------------------------------------------------------------- Git

        // Git ▸ Manage .gitignore. Ensures the standard Python ignore rules exist in the
        // solution directory, reporting how many were newly added. Runs synchronously —
        // it's a quick file edit, not a background job.
        public void OnManageGitIgnore(object sender, EventArgs e)
        {
            string dir = SolutionDir();
            if (dir == null) { host._statusLabel.Text = "No solution loaded"; return; }
            string err;
            List<string> added = GitService.EnsurePythonGitIgnore(dir, out err);
            if (!string.IsNullOrEmpty(err))
            {
                MessageBox.Show(host, err, "Manage .gitignore", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            host._statusLabel.Text = added != null && added.Count > 0
                ? string.Format(".gitignore: added {0} rule(s)", added.Count)
                : ".gitignore is already up to date";
        }

        // Git ▸ Persist repo. One-shot init (if needed) + stage-all + commit, run on a
        // background job. Each step short-circuits with a message string that becomes the
        // status-bar text; the string return doubles as the result and the error report.
        public void OnPersistRepo(object sender, EventArgs e)
        {
            string dir = SolutionDir();
            if (dir == null) { host._statusLabel.Text = "No solution loaded"; return; }
            if (host._jobRunner.IsBusy) return;

            // Ask for a real commit message instead of hard-coding one — a professional
            // commit carries the author's own summary. The previous fixed text is offered as
            // the pre-selected default so the one-click flow still works with a single Enter.
            string message;
            using (var dlg = new HelixPythonNamespace.App.Dialogs.SimpleInputDialog(
                "Commit", "Commit message:", "Update from HelixStudio"))
            {
                if (dlg.ShowDialog(host) != DialogResult.OK)
                {
                    host._statusLabel.Text = "Commit cancelled";
                    return;
                }
                message = string.IsNullOrWhiteSpace(dlg.Value) ? "Update from HelixStudio" : dlg.Value.Trim();
            }

            host._jobRunner.Run(
                "Persisting repo",
                token =>
                {
                    string err;
                    // Initialize a repo here if the directory isn't one yet.
                    if (!GitService.IsRepo(dir))
                    {
                        if (!GitService.Init(dir, false, out err))
                            return "Git init failed: " + err;
                    }
                    token.ThrowIfCancellationRequested();
                    // Stage everything (LibGit2Sharp pathspec "*"), then commit.
                    if (!GitService.Add(dir, new[] { "*" }, out err))
                        return "Git stage failed: " + err;
                    token.ThrowIfCancellationRequested();
                    if (!GitService.Commit(dir, message, out err))
                        return string.IsNullOrEmpty(err) ? "Nothing to commit" : "Commit: " + err;
                    return "Committed working tree";
                },
                summary => { host._statusLabel.Text = summary; });
        }

        // -------------------------------------------------------------- helpers

        // Returns the solution's Python files, or null (with a status message) when there
        // is no solution or it has no files — lets callers bail with a single null check.
        private List<WorkspaceFile> GetSolutionFiles()
        {
            if (host._currentSolution == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return null;
            }
            List<WorkspaceFile> files = host.EnumerateSolutionWorkspaceFiles(host._currentSolution);
            if (files == null || files.Count == 0)
            {
                host._statusLabel.Text = "No Python files found in solution";
                return null;
            }
            return files;
        }

        // The directory containing the loaded solution file (the working dir for tests
        // and git ops), or null when nothing is loaded.
        private string SolutionDir()
        {
            if (host._currentSolution == null || string.IsNullOrEmpty(host._currentSolution.FilePath))
                return null;
            try { return Path.GetDirectoryName(host._currentSolution.FilePath); }
            catch { return null; }
        }

        // Resolves a Python interpreter path for run/profile/test: prefer the one the user
        // configured (if it still exists on disk), else auto-detect. Returns null with a
        // status hint when none is available so callers can abort.
        private string ResolveInterpreter()
        {
            string configured = host._settingsManager != null && host._settingsManager.Settings != null
                ? host._settingsManager.Settings.SelectedPythonExecutable
                : null;
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;
            string found = host.FindPythonInterpreter();
            if (string.IsNullOrEmpty(found))
            {
                host._statusLabel.Text = "No Python interpreter found — select one in Build ▸ Select Python Interpreter";
                return null;
            }
            return found;
        }

        // Adapts virtual-compiler results into the linker's input shape, keeping only files
        // that produced a real AST Module. The VcAst/Linker type aliases at the top of the
        // file disambiguate the two libraries' identically-named CompileFileResult types.
        private static List<LinkerCompileFileResult> ToLinkerInputs(IReadOnlyList<VcCompileFileResult> compiled)
        {
            var outList = new List<LinkerCompileFileResult>();
            foreach (VcCompileFileResult fr in compiled)
            {
                VcAst.Module mod = fr.Result.AstModule as VcAst.Module;
                if (mod == null) continue;
                outList.Add(new LinkerCompileFileResult(mod, fr.ModuleFqn, fr.FilePath));
            }
            return outList;
        }
    }
}
