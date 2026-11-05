namespace HelixPythonNamespace.Cli
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Threading;
    using System.IO;
    using System.Text;
    using HelixPythonNamespace.Analysis;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.DeadCode;
    using HelixPythonNamespace.Lint;
    using HelixPythonNamespace.Linker;
    using HelixPythonNamespace.Metrics;
    using HelixPythonNamespace.Pdb.Dssp;
    using HelixPythonNamespace.Pdb.Io;
    using HelixPythonNamespace.Pdb.Model;
    using HelixPythonNamespace.Pdb.Query;
    using HelixPythonNamespace.Python.Analysis;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;
    using HelixPythonNamespace.Refactoring;
    using HelixPythonNamespace.VirtualCompiler;
    using Ast = HelixPythonNamespace.VirtualCompiler.Ast;
    using Kind = HelixPythonNamespace.Refactoring.StructuralCandidate.Kind;
    using LinkerCompileFileResult = HelixPythonNamespace.Linker.CompileFileResult;
    using VcCompileFileResult = HelixPythonNamespace.VirtualCompiler.CompileFileResult;
    // CUDA subsystem — aliased to avoid the WorkspaceFile / CompileResult name clashes
    // with the Python VirtualCompiler namespace already imported above.
    using CudaCompiler = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler;
    using CudaCompileResult = HelixPythonNamespace.CudaVirtualCompiler.CudaCompileResult;
    using CudaEmulator = HelixPythonNamespace.CudaEmulator.CudaEmulator;
    using CudaStringSink = HelixPythonNamespace.CudaEmulator.StringOutputSink;
    using CudaEmulationResult = HelixPythonNamespace.CudaEmulator.EmulationResult;
    using CudaCompileDiagnostic = HelixPythonNamespace.CudaVirtualCompiler.CudaCompileDiagnostic;

    /// <summary>
    /// Headless driver for the analysis engines (compile / link / lint / deadcode / analyze).
    /// Port of the C++ <c>helix_cli</c> <c>main.cpp</c>. All output is written to the supplied
    /// writers so the runner is unit-testable; exit code = total error-severity diagnostics.
    /// </summary>
    public static class CliRunner
    {
        public static int Run(IList<string> args, TextWriter outw, TextWriter errw)
        {
            if (args.Count < 1)
            {
                PrintUsage(outw);
                return 2;
            }
            string sub = args[0];
            if (sub == "-h" || sub == "--help" || sub == "help")
            {
                PrintUsage(outw);
                return 0;
            }

            CliOptions opt = new CliOptions();
            string target = null;
            List<string> breakSpecs = new List<string>();
            List<string> watches = new List<string>();
            List<string> exceptionFilters = new List<string>();
            bool stopOnEntry = false;
            int debugTimeoutMs = 30000;
            string stepMode = null;
            int maxStops = 200;
            for (int i = 1; i < args.Count; i++)
            {
                string a = args[i];
                if (a == "--json") opt.Json = true;
                else if (a == "--quiet") opt.Quiet = true;
                else if (a == "--include-warnings") opt.IncludeWarnings = true;
                else if (a == "--errors-only") opt.IncludeWarnings = false;
                else if (a == "--nvcc") opt.Nvcc = true;
                else if (a == "--stop-on-entry") stopOnEntry = true;
                else if (a.StartsWith("--break=", StringComparison.Ordinal)) breakSpecs.Add(a.Substring(8));
                else if (a.StartsWith("--watch=", StringComparison.Ordinal)) watches.Add(a.Substring(8));
                else if (a.StartsWith("--step=", StringComparison.Ordinal)) stepMode = a.Substring(7);
                else if (a.StartsWith("--exceptions=", StringComparison.Ordinal))
                {
                    foreach (string f in a.Substring(13).Split(','))
                    {
                        string filter = f.Trim();
                        if (filter.Length == 0) continue;
                        if (filter != "raised" && filter != "uncaught" && filter != "userUnhandled")
                        {
                            errw.Write("invalid --exceptions filter (want raised|uncaught|userUnhandled): " + filter + "\n");
                            return 2;
                        }
                        exceptionFilters.Add(filter);
                    }
                }
                else if (a.StartsWith("--max-stops=", StringComparison.Ordinal))
                {
                    int ms;
                    if (!int.TryParse(a.Substring(12), out ms) || ms <= 0)
                    {
                        errw.Write("invalid --max-stops value: " + a.Substring(12) + "\n");
                        return 2;
                    }
                    maxStops = ms;
                }
                else if (a.StartsWith("--timeout=", StringComparison.Ordinal))
                {
                    int t;
                    if (!int.TryParse(a.Substring(10), out t) || t <= 0)
                    {
                        errw.Write("invalid --timeout value: " + a.Substring(10) + "\n");
                        return 2;
                    }
                    debugTimeoutMs = t;
                }
                else if (a == "--apply") opt.Apply = true;
                else if (a == "--auto") opt.AutoHint = true;
                else if (a.StartsWith("--kind=", StringComparison.Ordinal)) opt.RefactorKind = a.Substring(7);
                else if (a.StartsWith("--roundtrip=", StringComparison.Ordinal)) opt.RoundtripKind = a.Substring(12);
                else if (a.StartsWith("--filter=", StringComparison.Ordinal)) opt.CodeFilter = a.Substring(9);
                else if (a.StartsWith("--framework=", StringComparison.Ordinal)) opt.Framework = a.Substring(12);
                else if (a == "--release") opt.Release = true;
                else if (a.StartsWith("--entry=", StringComparison.Ordinal)) opt.Entry = a.Substring(8);
                else if (a.StartsWith("--python=", StringComparison.Ordinal)) opt.AuxPath = a.Substring(9);
                else if (a.StartsWith("--pyproject=", StringComparison.Ordinal)) opt.AuxPath = a.Substring(12);
                else if (a.StartsWith("--venv=", StringComparison.Ordinal)) opt.AuxPath = a.Substring(7);
                else if (a.StartsWith("--bind=", StringComparison.Ordinal)) opt.AuxPath = a.Substring(7);
                else if (a.StartsWith("--topology=", StringComparison.Ordinal)) opt.AuxPath = a.Substring(11);
                else if (a.StartsWith("--sql=", StringComparison.Ordinal)) opt.Sql = a.Substring(6);
                else if (a == "--frames") opt.PerFrame = true;
                else if (a == "--reference") opt.DsspReference = true;
                else if (a.StartsWith("--reference=", StringComparison.Ordinal))
                {
                    opt.DsspReference = true;
                    int major;
                    if (!int.TryParse(a.Substring(12), out major) || (major != 2 && major != 4))
                    {
                        errw.Write("--reference takes 2 or 4 (the mkdssp major version): " + a + "\n");
                        return 2;
                    }
                    opt.DsspReferenceMajor = major;
                }
                else if (a == "--recursive") opt.Recursive = true;
                else if (a.StartsWith("--line=", StringComparison.Ordinal)) { int.TryParse(a.Substring(7), out int dl); opt.DebugLine = dl; }
                else if (a.StartsWith("--steps=", StringComparison.Ordinal)) { int.TryParse(a.Substring(8), out int ds); opt.DebugSteps = ds; }
                else if (a.StartsWith("--block=", StringComparison.Ordinal)) { int.TryParse(a.Substring(8), out int bs); if (bs > 0) opt.BlockSize = bs; }
                else if (a.StartsWith("--max-complexity=", StringComparison.Ordinal))
                {
                    int mc;
                    if (!int.TryParse(a.Substring(17), out mc) || mc < 0)
                    {
                        errw.Write("invalid --max-complexity value: " + a.Substring(17) + "\n");
                        return 2;
                    }
                    opt.MaxComplexity = mc;
                }
                else if (a.Length > 0 && a[0] == '-')
                {
                    errw.Write("unknown flag: " + a + "\n");
                    return 2;
                }
                else
                {
                    if (target == null) target = a;
                    else { errw.Write("unexpected extra arg: " + a + "\n"); return 2; }
                }
            }
            // gui-emit accepts an optional model .json; with no target it emits a built-in sample.
            bool targetOptional = sub == "gui-emit";
            if (string.IsNullOrEmpty(target) && !targetOptional)
            {
                errw.Write("missing target path\n");
                PrintUsage(outw);
                return 2;
            }

            Stopwatch sw = Stopwatch.StartNew();
            int rc;
            if (sub == "compile") rc = RunCompile(target, opt, outw, errw);
            else if (sub == "link") rc = RunLink(target, opt, outw, errw);
            else if (sub == "lint") rc = RunLint(target, opt, outw, errw);
            else if (sub == "deadcode") rc = RunDeadCode(target, opt, outw, errw);
            else if (sub == "metrics") rc = RunMetrics(target, opt, outw, errw);
            else if (sub == "todo") rc = RunTodo(target, opt, outw, errw);
            else if (sub == "debug") rc = DebugRunner.Run(target, breakSpecs, stopOnEntry, stepMode, watches, exceptionFilters, maxStops, opt.Json, debugTimeoutMs, outw, errw);
            else if (sub == "analyze") rc = RunAnalyze(target, opt, outw, errw);
            else if (sub == "refactor") rc = RunRefactor(target, opt, outw, errw);
            else if (sub == "cuda-compile") rc = RunCudaCompile(target, opt, outw, errw);
            else if (sub == "cuda-run") rc = RunCudaRun(target, opt, outw, errw);
            else if (sub == "gui-emit") rc = RunGuiEmit(target, opt, outw, errw);
            else if (sub == "toml-lint") rc = RunTomlLint(target, opt, outw, errw);
            else if (sub == "toml-analyze") rc = RunTomlAnalyze(target, opt, outw, errw);
            else if (sub == "code-metrics") rc = RunCodeMetrics(target, opt, outw, errw);
            else if (sub == "cpp-lint") rc = RunGreenfieldLintAnalyze(target, opt, "cpp", false, outw, errw);
            else if (sub == "cpp-profile") rc = RunWsProfile(target, opt, "cpp", outw, errw);
            else if (sub == "cuda-profile") rc = RunCudaProfile(target, opt, outw, errw);
            else if (sub == "ctest-run") rc = RunWsTest(target, opt, "ctest", outw, errw);
            else if (sub == "py-roundtrip") rc = RunPyRoundtrip(target, opt, outw, errw);
            else if (sub == "venv-create") rc = RunVenvCreate(target, opt, outw, errw);
            else if (sub == "venv-list") rc = RunVenvList(target, opt, outw, errw);
            else if (sub == "req-sync") rc = RunReqSync(target, opt, false, outw, errw);
            else if (sub == "req-diff") rc = RunReqSync(target, opt, true, outw, errw);
            else if (sub == "dssp") rc = RunDssp(target, opt, outw, errw);
            else if (sub == "query") rc = RunQuery(target, opt, outw, errw);
            else
            {
                errw.Write("unknown subcommand: " + sub + "\n");
                PrintUsage(outw);
                return 2;
            }
            sw.Stop();
            if (!opt.Quiet)
            {
                errw.Write("elapsed: " + sw.ElapsedMilliseconds + " ms\n");
            }
            return rc;
        }

        // ---- subcommands ----

        // ---- structure verbs ------------------------------------------------------------------
        //
        // These belong in helix_cli rather than the launcher, and the split is deliberate: the
        // launcher takes a SOLUTION and executes it, helix_cli takes a PATH and reports on it. A
        // DSSP run over a folder and a query over one structure are both the second kind. (The
        // submission plan wrote them as `helixstudio dssp`, which would have put an analysis verb
        // on the execution surface.)
        //
        // What they are for: every figure in a paper reproducible from one command line, which is
        // what a deposited archive needs.

        /// <summary>
        /// <c>dssp &lt;file|dir&gt; [--reference] [--recursive] [--json]</c> — assign secondary
        /// structure and write a <c>.dssp</c> beside each input.
        ///
        /// <c>--reference</c> is <see cref="DsspOptions.ReferenceCompatible"/>: the seven switches
        /// that separate the 1983 paper's semantics from DSSP 4's, set the way mkdssp sets them. It
        /// exists so a run here can be diffed against mkdssp's output line for line.
        /// </summary>
        private static int RunDssp(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                errw.Write("no such file or directory: " + target + "\n");
                return 2;
            }

            // --reference=2 diffs against a DSSP 2.x binary, which has neither polyproline-II nor
            // the pi-helix priority. Separating a version difference from a real disagreement is
            // the whole job of an agreement study.
            DsspOptions dssp;
            if (opt.DsspReferenceMajor == 2) dssp = DsspOptions.ReferenceCompatible2x();
            else if (opt.DsspReference) dssp = DsspOptions.ReferenceCompatible();
            else dssp = new DsspOptions();

            var options = new DsspBatchOptions
            {
                InputFolder = Directory.Exists(target) ? target : Path.GetDirectoryName(target),
                Recursive = opt.Recursive,
                Dssp = dssp,
            };

            List<string> inputs = Directory.Exists(target)
                ? DsspBatchRunner.FindStructures(options)
                : new List<string> { target };

            if (inputs.Count == 0)
            {
                errw.Write("no structures found under " + target + "\n");
                return 1;
            }

            int failed = 0;
            var rows = new List<string>();
            foreach (string input in inputs)
            {
                DsspBatchItem item = DsspBatchRunner.RunOne(input, options, CancellationToken.None);
                if (!item.Succeeded) failed++;

                if (opt.Json)
                {
                    rows.Add("  {\"file\":" + JsonString(Path.GetFileName(input))
                        + ",\"ok\":" + (item.Succeeded ? "true" : "false")
                        + ",\"residues\":" + item.ResidueCount
                        + ",\"chains\":" + item.ChainCount
                        + ",\"helix\":" + item.HelixResidues
                        + ",\"sheet\":" + item.SheetResidues
                        + ",\"turn\":" + item.TurnResidues
                        + ",\"output\":" + JsonString(item.OutputPath)
                        + ",\"message\":" + JsonString(item.Message) + "}");
                }
                else if (item.Succeeded)
                {
                    outw.Write(string.Format(CultureInfo.InvariantCulture,
                        "{0,-28} {1,5} residues  {2,3} chains  H {3,4}  E {4,4}  T {5,4}  -> {6}\n",
                        Path.GetFileName(input), item.ResidueCount, item.ChainCount,
                        item.HelixResidues, item.SheetResidues, item.TurnResidues,
                        Path.GetFileName(item.OutputPath)));
                }
                else
                {
                    errw.Write(Path.GetFileName(input) + ": " + item.Message + "\n");
                }
            }

            if (opt.Json)
            {
                outw.Write("{\"command\":\"dssp\",\"reference_mode\":"
                    + (opt.DsspReference ? "true" : "false")
                    + ",\"reference_major\":" + (opt.DsspReference ? opt.DsspReferenceMajor : 0)
                    + ",\"files\":" + inputs.Count + ",\"failed\":" + failed
                    + ",\"results\":[\n" + string.Join(",\n", rows.ToArray()) + "\n]}\n");
            }
            else if (inputs.Count > 1)
            {
                outw.Write(inputs.Count + " structures, " + (inputs.Count - failed)
                           + " assigned, " + failed + " failed\n");
            }

            return failed > 0 ? 1 : 0;
        }

        /// <summary>
        /// <c>query &lt;structure&gt; --sql=&lt;statement&gt; [--frames] [--topology=&lt;file&gt;]</c> —
        /// run the atoms query language over a structure and print the answer as CSV.
        ///
        /// Three answers are possible and they are different shapes, so the output says which it
        /// gave: a GROUP BY yields a table, an aggregate one number, and a bare predicate the atoms
        /// it matched. <c>--frames</c> asks the aggregate once per frame instead, which is the
        /// time-series figure from a shell.
        /// </summary>
        private static int RunQuery(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (!File.Exists(target)) { errw.Write("no such file: " + target + "\n"); return 2; }
            if (string.IsNullOrWhiteSpace(opt.Sql))
            {
                errw.Write("missing --sql=<statement>, for example "
                           + "--sql=\"select avg(rg) from atoms where chain='A'\"\n");
                return 2;
            }

            try
            {
                using (Trajectory trajectory = OpenForQuery(target, opt))
                {
                    CompiledQuery query = AtomQuery.Compile(Selection.ToStatement(opt.Sql));

                    if (opt.PerFrame)
                    {
                        TrajectoryTimeSeries series = TrajectoryQuery.Run(trajectory, opt.Sql);
                        outw.Write(series.ToCsv());
                        return series.DefinedCount == 0 ? 1 : 0;
                    }

                    // The `ss` column reads an assignment computed from the frame on screen; out
                    // here there is no screen, so frame 0 is assigned before anything reads it.
                    if (query.ReferencesColumn("ss"))
                        TrajectoryQuery.AssignForFrame(trajectory.Topology, trajectory.GetFrame(0), 0);

                    var ctx = new EvalContext(trajectory.Topology, trajectory.GetFrame(0),
                                              trajectory.Index.FirstFrameXyz);

                    if (query.IsGrouped) { outw.Write(query.EvaluateGrouped(ctx).ToCsv()); return 0; }

                    if (query.IsAggregate)
                    {
                        long matched;
                        double value = query.EvaluateScalar(ctx, out matched);
                        outw.Write("# query: " + opt.Sql + "\n");
                        outw.Write("value,matched_atoms\n");
                        outw.Write((double.IsNaN(value) ? string.Empty
                                    : value.ToString("R", CultureInfo.InvariantCulture))
                                   + "," + matched.ToString(CultureInfo.InvariantCulture) + "\n");
                        return matched == 0 ? 1 : 0;
                    }

                    bool[] mask = query.Evaluate(ctx);
                    var matchedAtoms = new List<int>();
                    for (int i = 0; i < mask.Length; i++) if (mask[i]) matchedAtoms.Add(i);
                    outw.Write(AtomTableCsv.Write(opt.Sql, trajectory.Topology,
                                                  trajectory.GetFrame(0), matchedAtoms));
                    return matchedAtoms.Count == 0 ? 1 : 0;
                }
            }
            catch (QueryException ex) { errw.Write("query error: " + ex.Message + "\n"); return 2; }
            catch (Exception ex) { errw.Write(ex.Message + "\n"); return 1; }
        }

        /// <summary>Open a structure, or a trajectory paired with the structure named by --topology.</summary>
        private static Trajectory OpenForQuery(string target, CliOptions opt)
        {
            if (TrajectoryFormat.IsCoordinateOnly(target))
                return Trajectory.OpenWithTopology(target, opt.AuxPath);
            return Trajectory.Open(target);
        }

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }


        // FR-GEN-03: headless GUI code generation. `gui-emit [form.json] --framework=<tkinter|pyqt6|pyside6|wxpython>`
        // — loads a saved GuiForm (or a built-in sample when no valid .json is given) and emits framework source.
        private static int RunGuiEmit(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            HelixPythonNamespace.GuiDesigner.Model.GuiForm form;
            if (!string.IsNullOrEmpty(target) && File.Exists(target) && target.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                form = new HelixPythonNamespace.GuiDesigner.Persistence.GuiFormRepository().Load(target);
                if (form == null) { errw.Write("could not load GUI form: " + target + "\n"); return 1; }
            }
            else form = SampleGuiForm();

            var fw = form.Framework;
            if (!string.IsNullOrEmpty(opt.Framework))
            {
                switch (opt.Framework.ToLowerInvariant())
                {
                    case "tkinter": fw = HelixPythonNamespace.GuiDesigner.Model.GuiFramework.Tkinter; break;
                    case "pyqt6": fw = HelixPythonNamespace.GuiDesigner.Model.GuiFramework.PyQt6; break;
                    case "pyside6": fw = HelixPythonNamespace.GuiDesigner.Model.GuiFramework.PySide6; break;
                    case "wxpython": case "wx": fw = HelixPythonNamespace.GuiDesigner.Model.GuiFramework.WxPython; break;
                    default: errw.Write("unknown --framework '" + opt.Framework + "' (tkinter|pyqt6|pyside6|wxpython)\n"); return 2;
                }
            }
            string code = HelixPythonNamespace.GuiDesigner.Emit.EmitterFactory.EmitterFor(fw).Emit(form, null);
            outw.Write(code);
            if (!code.EndsWith("\n")) outw.Write("\n");
            return 0;
        }

        // FR-PROJ-03: create a venv with REAL `python -m venv` and bind it to a project.
        // `venv-create <venvDir> [--python=<base>] [--bind=<projectDir>]`
        private static int RunVenvCreate(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing venv directory to create\n"); return 2; }
            string basePy = opt.AuxPath; // when set via --python=; --bind= also lands here, so disambiguate below
            // --bind= and --python= share AuxPath; if the aux path is a directory that isn't a python exe, treat as bind target.
            string bindDir = null, python = null;
            if (!string.IsNullOrEmpty(opt.AuxPath))
            {
                if (System.IO.Directory.Exists(opt.AuxPath)) bindDir = opt.AuxPath;
                else python = opt.AuxPath;
            }
            var r = HelixPythonNamespace.Workspace.Python.VenvService.Create(python, target);
            if (!r.Success) { errw.Write("venv-create failed: " + r.Reason + "\n"); return 1; }
            outw.Write("created venv: " + r.Venv.Directory + "\n");
            outw.Write("  interpreter: " + r.Venv.InterpreterPath + "\n");
            outw.Write("  python     : " + (string.IsNullOrEmpty(r.Venv.Version) ? "?" : r.Venv.Version) + "\n");
            if (bindDir != null)
            {
                bool bound = HelixPythonNamespace.Workspace.Python.VenvService.Bind(bindDir, r.Venv.Directory);
                outw.Write("  bound to   : " + bindDir + " -> " + (bound ? "OK" : "FAILED") + "\n");
            }
            return 0;
        }

        // FR-PROJ-03: list venvs discovered under a project + the currently bound interpreter.
        private static int RunVenvList(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing project directory\n"); return 2; }
            var venvs = HelixPythonNamespace.Workspace.Python.VenvService.Discover(target, true);
            string bound = HelixPythonNamespace.Workspace.Python.VenvService.BoundInterpreter(target);
            outw.Write("venvs under " + target + ": " + venvs.Count + "\n");
            foreach (var v in venvs) outw.Write("  - " + v + "\n");
            outw.Write("bound interpreter: " + (bound ?? "(none)") + "\n");
            return 0;
        }

        // FR-PROJ-04: sync/diff requirements.txt against pyproject.toml [project].dependencies.
        // `req-sync|req-diff <requirements.txt> --pyproject=<pyproject.toml>`
        private static int RunReqSync(string target, CliOptions opt, bool diffOnly, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing requirements.txt path\n"); return 2; }
            if (string.IsNullOrEmpty(opt.AuxPath)) { errw.Write("missing --pyproject=<path>\n"); return 2; }
            var diff = HelixPythonNamespace.Workspace.Python.RequirementsService.Diff(target, opt.AuxPath);
            outw.Write("requirements.txt vs pyproject.toml:\n");
            outw.Write("  in-sync            : " + diff.InSync + "\n");
            outw.Write("  only-in-reqs       : " + string.Join(", ", diff.OnlyInRequirements.ToArray()) + "\n");
            outw.Write("  only-in-pyproject  : " + string.Join(", ", diff.OnlyInPyproject.ToArray()) + "\n");
            outw.Write("  specifier-differs  : " + string.Join(", ", diff.SpecifierDiffers.ToArray()) + "\n");
            if (!diffOnly)
            {
                int added = HelixPythonNamespace.Workspace.Python.RequirementsService.SyncFromPyproject(target, opt.AuxPath);
                outw.Write("  synced (appended)  : " + added + " requirement(s) from pyproject -> requirements.txt\n");
            }
            return 0;
        }

        // FR-GEN-02: lossless trivia-preserving Python round-trip. `py-roundtrip <file-or-dir> [--json]`
        // — parses each .py and reproduces it from the CST spans; reports whether the output is byte-identical.
        // Exit code = number of files that did NOT round-trip exactly (0 = all lossless).
        private static int RunPyRoundtrip(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing .py file or directory\n"); return 2; }
            var files = new List<string>();
            if (File.Exists(target)) files.Add(target);
            else if (Directory.Exists(target))
                files.AddRange(Directory.GetFiles(target, "*.py", SearchOption.AllDirectories));
            else { errw.Write("not found: " + target + "\n"); return 2; }

            int mismatches = 0, ok = 0, parseErrors = 0;
            foreach (string f in files)
            {
                string src;
                try { src = File.ReadAllText(f); } catch { continue; }
                var pr = HelixPythonNamespace.Python.Python.ParseModule(src, f);
                if (pr.HasErrors) { parseErrors++; if (!opt.Quiet) outw.Write("  PARSE-ERR " + f + "\n"); continue; }
                string outp = HelixPythonNamespace.Python.Emit.LosslessPythonWriter.Write(src, pr.Module);
                bool identical = string.Equals(outp, src, StringComparison.Ordinal);
                if (identical) { ok++; }
                else { mismatches++; if (!opt.Quiet) outw.Write("  DIFF " + f + " (len " + src.Length + " -> " + outp.Length + ")\n"); }
            }
            if (opt.Json)
                outw.Write("{\"files\":" + files.Count + ",\"lossless\":" + ok + ",\"mismatch\":" + mismatches
                    + ",\"parseErrors\":" + parseErrors + "}\n");
            else
                outw.Write("py-roundtrip: " + ok + "/" + (ok + mismatches) + " files reproduced byte-identical"
                    + (parseErrors > 0 ? " (" + parseErrors + " skipped: parse errors)" : "") + "\n");
            return mismatches;
        }

        // FR-LINT-03: native TOML linter. `toml-lint <file-or-dir>` — TLN00x/TLN010 + folded parser diags.
        // Exit code = number of error-severity findings.
        private static int RunTomlLint(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing .toml file or directory\n"); return 2; }
            var files = new List<string>();
            if (File.Exists(target)) files.Add(target);
            else if (Directory.Exists(target)) files.AddRange(Directory.GetFiles(target, "*.toml", SearchOption.AllDirectories));
            else { errw.Write("not found: " + target + "\n"); return 2; }

            int totalFindings = 0, errorFindings = 0, filesWithFindings = 0;
            var codeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                string src;
                try { src = File.ReadAllText(f); } catch { continue; }
                var findings = HelixPythonNamespace.Workspace.Toml.TomlLinter.Lint(src, f);
                if (findings.Count > 0) filesWithFindings++;
                foreach (var d in findings)
                {
                    totalFindings++;
                    if (d.Severity == HelixPythonNamespace.Workspace.Toml.TomlLintSeverity.Error) errorFindings++;
                    codeCounts[d.Code] = codeCounts.TryGetValue(d.Code, out int c) ? c + 1 : 1;
                    if (!opt.Quiet && files.Count == 1)
                        outw.Write("  " + f + ":" + d.Line + ":" + d.Column + " " + d.Severity.ToString().ToLowerInvariant()
                            + " " + d.Code + ": " + d.Message + "\n");
                }
            }
            if (opt.Json)
            {
                var sb = new StringBuilder("{\"files\":" + files.Count + ",\"filesWithFindings\":" + filesWithFindings
                    + ",\"findings\":" + totalFindings + ",\"errors\":" + errorFindings + ",\"byCode\":{");
                bool first = true;
                foreach (var kv in codeCounts) { if (!first) sb.Append(','); sb.Append('"').Append(kv.Key).Append("\":").Append(kv.Value); first = false; }
                sb.Append("}}");
                outw.Write(sb.ToString()); outw.Write("\n");
            }
            else
            {
                var codeParts = new List<string>();
                foreach (var kv in codeCounts) codeParts.Add(kv.Key + ":" + kv.Value);
                outw.Write("toml-lint: " + files.Count + " file(s), " + totalFindings + " finding(s), " + errorFindings + " error(s)"
                    + (codeParts.Count > 0 ? " [" + string.Join(" ", codeParts.ToArray()) + "]" : "") + "\n");
            }
            return errorFindings;
        }

        // FR-PROF-05/06: real sampling profilers. `<tool>-profile <file>` shells the native profiler and ranks hot fns.
        private static int RunWsProfile(string target, CliOptions opt, string tool, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing source file\n"); return 2; }
            // HelixPython profiles Python (HelixPythonNamespace.Profiler / cProfile) and the native C++ the
            // Convert-to-C/C++ commands emit. The cargo/dotnet/julia/jfr profilers ship in their
            // own products.
            // C++ takes a BUILT executable, not a project dir: there is no one command that turns a
            // C++ source tree into a runnable binary, so the caller (the IDE's CMake build, or a
            // developer at the prompt) supplies the exe.
            if (tool != "cpp") { errw.Write(tool + "-profile is not available in HelixPython\n"); return 2; }
            HelixPythonNamespace.Workspace.Profiling.WorkspaceProfile r =
                HelixPythonNamespace.Workspace.Cpp.CppProfiler.Run(target);
            if (!r.Available) { errw.Write(tool + "-profile unavailable: " + r.Reason + "\n"); return 3; }
            if (!r.Succeeded) { errw.Write(tool + "-profile: no samples captured (workload too short?)\n"); return 1; }

            if (opt.Json)
            {
                var sb = new StringBuilder("{\"tool\":\"" + r.Tool + "\",\"totalSamples\":" + r.TotalSamples + ",\"wallSeconds\":" + r.WallSeconds.ToString("0.000") + ",\"hot\":[");
                for (int i = 0; i < r.Samples.Count; i++)
                { var s = r.Samples[i]; if (i > 0) sb.Append(','); sb.Append("{\"function\":").Append(JsonStr(s.Function)).Append(",\"samples\":").Append(s.Samples).Append(",\"percent\":").Append(s.Percent.ToString("0.0")).Append('}'); }
                sb.Append("]}");
                outw.Write(sb.ToString()); outw.Write("\n");
            }
            else
            {
                outw.Write(r.Tool + "-profile -> " + r.Samples.Count + " hot functions (" + r.TotalSamples + " samples, " + r.WallSeconds.ToString("0.0") + "s)\n");
                foreach (var s in r.Samples) outw.Write("  " + s.Percent.ToString("0.0").PadLeft(5) + "%  " + s.Samples + "  " + s.Function + "\n");
            }
            return 0;
        }

        // FR-PROF-02: real CUDA kernel profiler. `cuda-profile <file.cu> [--block=<n>]` shells nvcc -Xptxas -v for
        // per-kernel registers/shared-mem/spills, then computes theoretical occupancy natively (NFR-02).
        private static int RunCudaProfile(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing .cu file\n"); return 2; }
            var r = HelixPythonNamespace.Workspace.Cuda.CudaKernelProfiler.Run(target, opt.BlockSize);
            if (!r.Available) { errw.Write("cuda-profile unavailable: " + r.Reason + "\n"); return 3; }
            if (!r.Succeeded) { errw.Write("cuda-profile: " + r.Reason + "\n"); return 1; }

            if (opt.Json)
            {
                var sb = new StringBuilder("{\"tool\":\"nvcc-ptxas\",\"arch\":" + JsonStr(r.Arch) + ",\"block\":" + opt.BlockSize + ",\"kernels\":[");
                for (int i = 0; i < r.Kernels.Count; i++)
                {
                    var k = r.Kernels[i]; if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(JsonStr(k.Name))
                      .Append(",\"registers\":").Append(k.Registers)
                      .Append(",\"sharedMemBytes\":").Append(k.SharedMemBytes)
                      .Append(",\"spillStores\":").Append(k.SpillStores)
                      .Append(",\"spillLoads\":").Append(k.SpillLoads)
                      .Append(",\"constMemBytes\":").Append(k.ConstMemBytes)
                      .Append(",\"occupancyPercent\":").Append(k.OccupancyPercent)
                      .Append(",\"activeWarpsPerSm\":").Append(k.ActiveWarpsPerSm)
                      .Append(",\"maxWarpsPerSm\":").Append(k.MaxWarpsPerSm)
                      .Append(",\"limiter\":").Append(JsonStr(k.Limiter)).Append('}');
                }
                sb.Append("]}");
                outw.Write(sb.ToString()); outw.Write("\n");
            }
            else
            {
                outw.Write("cuda-profile -> " + r.Kernels.Count + " kernel(s) on " + r.Arch + " @ block=" + opt.BlockSize + " (nvcc " + r.NvccVersion + ")\n");
                foreach (var k in r.Kernels)
                    outw.Write("  " + k.Name + ": regs=" + k.Registers + " smem=" + k.SharedMemBytes + "B spills=" + (k.SpillStores + k.SpillLoads) +
                               "B cmem=" + k.ConstMemBytes + "B  occ=" + k.OccupancyPercent + "% (" + k.ActiveWarpsPerSm + "/" + k.MaxWarpsPerSm + " warps, " + k.Limiter + "-limited)\n");
            }
            return 0;
        }

        // FR-X-04 / FR-LINT + FR-ANA: headless native linter/analyzer for the greenfield/shipped languages —
        // `<lang>-lint` / `<lang>-analyze <file>` (rust/csharp/java/julia). No external tool (NFR-02); makes the
        // native linters+analyzers CI-reachable for all 8 languages (Python/TOML already had verbs).
        private static int RunGreenfieldLintAnalyze(string target, CliOptions opt, string lang, bool analyze, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target) || !File.Exists(target)) { errw.Write("file not found: " + target + "\n"); return 2; }
            string src = ReadAll(target);
            var lines = new System.Collections.Generic.List<string>();
            switch (lang)
            {
                case "cpp":
                    // C++ has no separate analyzer yet — only the linter.
                    if (analyze) { errw.Write("cpp-analyze is not implemented (no C++ analyzer exists)\n"); return 2; }
                    foreach (var f in HelixPythonNamespace.Workspace.Cpp.CppLinter.Lint(src, target)) lines.Add(f.ToString());
                    break;
                default: errw.Write("unknown language: " + lang + "\n"); return 2;
            }

            string verb = lang + "-" + (analyze ? "analyze" : "lint");
            if (opt.Json)
            {
                var sb = new StringBuilder("{\"verb\":").Append(JsonStr(verb)).Append(",\"file\":").Append(JsonStr(Path.GetFileName(target)))
                    .Append(",\"count\":").Append(lines.Count).Append(",\"findings\":[");
                for (int i = 0; i < lines.Count; i++) { if (i > 0) sb.Append(','); sb.Append(JsonStr(lines[i])); }
                sb.Append("]}");
                outw.Write(sb.ToString()); outw.Write("\n");
            }
            else
            {
                outw.Write(verb + " -> " + lines.Count + " finding(s) in " + Path.GetFileName(target) + "\n");
                foreach (string l in lines) outw.Write("  " + l + "\n");
            }
            return 0;
        }

        // FR-METR-02..07: native metrics for the non-Python languages. `code-metrics <file-or-dir>` —
        // LOC + per-function complexity/nesting/params via LanguageMetricsEngine (dispatch by extension).
        private static int RunCodeMetrics(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing source file or directory\n"); return 2; }
            var files = new List<string>();
            if (File.Exists(target)) files.Add(target);
            else if (Directory.Exists(target))
                foreach (string ext in new[] { "*.rs", "*.cs", "*.java", "*.jl", "*.cu", "*.cuh" })
                    files.AddRange(Directory.GetFiles(target, ext, SearchOption.AllDirectories));
            else { errw.Write("not found: " + target + "\n"); return 2; }

            var overall = new MetricsReport();
            int analyzed = 0;
            foreach (string f in files)
            {
                var profile = HelixPythonNamespace.Metrics.LanguageMetricsEngine.ForExtension(Path.GetExtension(f));
                if (profile == null) continue;
                var rep = HelixPythonNamespace.Metrics.LanguageMetricsEngine.Analyze(new MetricsSource(f, ReadAll(f)), profile);
                overall.Files.AddRange(rep.Files);
                overall.Functions.AddRange(rep.Functions);
                overall.Classes.AddRange(rep.Classes);
                analyzed++;
            }
            if (analyzed == 0) { errw.Write("no supported source files (.rs/.cs/.java/.jl/.cu) at: " + target + "\n"); return 1; }

            int over = 0;
            if (opt.Json)
            {
                var sb = new StringBuilder("{\"files\":" + overall.Files.Count + ",\"functions\":" + overall.Functions.Count + ",\"fn\":[");
                for (int i = 0; i < overall.Functions.Count; i++)
                {
                    var fn = overall.Functions[i]; if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(JsonStr(fn.Name)).Append(",\"loc\":").Append(fn.Loc)
                      .Append(",\"params\":").Append(fn.ParameterCount).Append(",\"complexity\":").Append(fn.CyclomaticComplexity)
                      .Append(",\"maxNesting\":").Append(fn.MaxNestingDepth).Append('}');
                    if (opt.MaxComplexity > 0 && fn.CyclomaticComplexity > opt.MaxComplexity) over++;
                }
                sb.Append("]}");
                outw.Write(sb.ToString()); outw.Write("\n");
            }
            else
            {
                foreach (var fl in overall.Files)
                    outw.Write(fl.FilePath + ": " + fl.CodeLines + " code, " + fl.CommentLines + " comment, " + fl.BlankLines
                        + " blank; " + fl.FunctionCount + " fn, " + fl.ClassCount + " class\n");
                foreach (var fn in overall.Functions)
                {
                    bool o = opt.MaxComplexity > 0 && fn.CyclomaticComplexity > opt.MaxComplexity; if (o) over++;
                    if (!opt.Quiet)
                        outw.Write("  " + fn.Name + " (line " + fn.Line + "): loc=" + fn.Loc + " params=" + fn.ParameterCount
                            + " cc=" + fn.CyclomaticComplexity + " nest=" + fn.MaxNestingDepth + (o ? "  OVER" : "") + "\n");
                }
            }
            return over;
        }

        // FR-TEST-02..07: per-language test runners. `<tool>-test <dir>` shells the REAL runner and lists cases.
        // Exit code = number of failed tests (0 = all passed / none).
        private static int RunWsTest(string target, CliOptions opt, string tool, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing project/build directory\n"); return 2; }
            HelixPythonNamespace.Workspace.Testing.WorkspaceTestRun r;
            switch (tool)
            {
                // ctest is the only workspace-level runner HelixPython ships: Python tests run
                // through HelixPythonNamespace.Testing (pytest/unittest), not this registry.
                case "ctest": r = HelixPythonNamespace.Workspace.Cuda.CTestRunner.Run(target); break;
                default: errw.Write(tool + "-test is not available in HelixPython\n"); return 2;
            }
            if (!r.Available) { errw.Write(tool + "-test unavailable: " + r.Reason + "\n"); return 3; }

            if (opt.Json)
            {
                var sb = new StringBuilder("{\"tool\":\"" + r.Tool + "\",\"passed\":" + r.Passed + ",\"failed\":" + r.Failed
                    + ",\"ignored\":" + r.Ignored + ",\"tests\":[");
                for (int i = 0; i < r.Tests.Count; i++)
                {
                    var t = r.Tests[i]; if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":").Append(JsonStr(t.Name)).Append(",\"outcome\":\"").Append(t.Outcome).Append("\"}");
                }
                sb.Append("]}");
                outw.Write(sb.ToString()); outw.Write("\n");
            }
            else
            {
                outw.Write(r.Tool + "-test -> " + r.Passed + " passed, " + r.Failed + " failed, " + r.Ignored + " ignored ("
                    + r.Tests.Count + " total)\n");
                foreach (var t in r.Tests) outw.Write("  [" + t.Outcome + "] " + t.Name + "\n");
            }
            return r.Failed;
        }

        // FR-ANA-04: native TOML analyzer. `toml-analyze <file-or-dir>` — TMA001-004 PEP-621/508 + workspace.
        // Exit code = number of error-severity findings.
        private static int RunTomlAnalyze(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (string.IsNullOrEmpty(target)) { errw.Write("missing .toml file or directory\n"); return 2; }
            var files = new List<string>();
            if (File.Exists(target)) files.Add(target);
            else if (Directory.Exists(target)) files.AddRange(Directory.GetFiles(target, "*.toml", SearchOption.AllDirectories));
            else { errw.Write("not found: " + target + "\n"); return 2; }

            int totalFindings = 0, errorFindings = 0, filesWithFindings = 0;
            var codeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string f in files)
            {
                string src;
                try { src = File.ReadAllText(f); } catch { continue; }
                // Only pyproject-style files carry a [project] schema; analyze all .toml but the analyzer
                // no-ops cleanly when there's no [project] table. basePath = the file's dir for member resolution.
                var findings = HelixPythonNamespace.Workspace.Toml.TomlAnalyzer.Analyze(
                    HelixPythonNamespace.Workspace.Toml.TomlParser.Parse(src), Path.GetDirectoryName(Path.GetFullPath(f)));
                if (findings.Count > 0) filesWithFindings++;
                foreach (var dgn in findings)
                {
                    totalFindings++;
                    if (dgn.Severity == HelixPythonNamespace.Workspace.Toml.TomlLintSeverity.Error) errorFindings++;
                    codeCounts[dgn.Code] = codeCounts.TryGetValue(dgn.Code, out int c) ? c + 1 : 1;
                    if (!opt.Quiet && files.Count == 1)
                        outw.Write("  " + f + ":" + dgn.Line + ":" + dgn.Column + " " + dgn.Severity.ToString().ToLowerInvariant()
                            + " " + dgn.Code + ": " + dgn.Message + "\n");
                }
            }
            if (opt.Json)
            {
                var sb = new StringBuilder("{\"files\":" + files.Count + ",\"filesWithFindings\":" + filesWithFindings
                    + ",\"findings\":" + totalFindings + ",\"errors\":" + errorFindings + ",\"byCode\":{");
                bool first = true;
                foreach (var kv in codeCounts) { if (!first) sb.Append(','); sb.Append('"').Append(kv.Key).Append("\":").Append(kv.Value); first = false; }
                sb.Append("}}");
                outw.Write(sb.ToString()); outw.Write("\n");
            }
            else
            {
                var codeParts = new List<string>();
                foreach (var kv in codeCounts) codeParts.Add(kv.Key + ":" + kv.Value);
                outw.Write("toml-analyze: " + files.Count + " file(s), " + totalFindings + " finding(s), " + errorFindings + " error(s)"
                    + (codeParts.Count > 0 ? " [" + string.Join(" ", codeParts.ToArray()) + "]" : "") + "\n");
            }
            return errorFindings;
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static HelixPythonNamespace.GuiDesigner.Model.GuiForm SampleGuiForm()
        {
            var f = new HelixPythonNamespace.GuiDesigner.Model.GuiForm { FormClassName = "LoginForm", Title = "Login", Width = 420, Height = 260, Framework = HelixPythonNamespace.GuiDesigner.Model.GuiFramework.Tkinter };
            f.Controls.Add(new HelixPythonNamespace.GuiDesigner.Model.GuiControl { Id = "l1", Kind = HelixPythonNamespace.GuiDesigner.Model.GuiControlKind.Label, Name = "userLabel", X = 30, Y = 30, Width = 90, Height = 24, Props = { ["Text"] = "Username" } });
            f.Controls.Add(new HelixPythonNamespace.GuiDesigner.Model.GuiControl { Id = "t1", Kind = HelixPythonNamespace.GuiDesigner.Model.GuiControlKind.TextBox, Name = "userText", X = 140, Y = 28, Width = 220, Height = 26 });
            f.Controls.Add(new HelixPythonNamespace.GuiDesigner.Model.GuiControl { Id = "b1", Kind = HelixPythonNamespace.GuiDesigner.Model.GuiControlKind.Button, Name = "loginButton", X = 140, Y = 156, Width = 100, Height = 30, Props = { ["Text"] = "Log In" }, Events = { new HelixPythonNamespace.GuiDesigner.Model.GuiEvent { EventName = "Click", HandlerName = "on_login_click" } } });
            return f;
        }

        private static int RunCompile(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            List<string> files = CollectPyFiles(target);
            if (files.Count == 0) { errw.Write("no .py files at: " + target + "\n"); return 1; }
            IReadOnlyList<VcCompileFileResult> results = VirtualCompiler.CompileSolution(BuildWorkspaceFiles(files));

            int errors = 0, total = 0;
            foreach (VcCompileFileResult f in results)
            {
                foreach (CompileDiagnostic d in f.Result.Diagnostics)
                {
                    if (!opt.IncludeWarnings && d.Severity < ParseSeverity.Warning) continue;
                    total++;
                    if (d.Severity >= ParseSeverity.Error) errors++;
                    PrintDiag(outw, opt, d.FilePath, d.Code, SeverityLabel(d.Severity), d.Span, d.Message);
                }
            }
            if (!opt.Quiet)
                errw.Write("compile: " + results.Count + " file(s), " + total + " diagnostic(s), " + errors + " error(s)\n");
            return errors;
        }

        private static int RunLink(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            List<string> files = CollectPyFiles(target);
            if (files.Count == 0) { errw.Write("no .py files at: " + target + "\n"); return 1; }
            IReadOnlyList<VcCompileFileResult> compileResults = VirtualCompiler.CompileSolution(BuildWorkspaceFiles(files));
            LinkResult linkResult = PythonLinker.Link(ToLinkerInputs(compileResults));

            int errors = 0, total = 0;
            foreach (VcCompileFileResult f in compileResults)
            {
                foreach (CompileDiagnostic d in f.Result.Diagnostics)
                {
                    if (!opt.IncludeWarnings && d.Severity < ParseSeverity.Warning) continue;
                    total++;
                    if (d.Severity >= ParseSeverity.Error) errors++;
                    PrintDiag(outw, opt, d.FilePath, d.Code, SeverityLabel(d.Severity), d.Span, d.Message);
                }
            }
            foreach (LinkDiagnostic d in linkResult.Diagnostics)
            {
                if (!opt.IncludeWarnings && d.Severity < ParseSeverity.Warning) continue;
                total++;
                if (d.Severity >= ParseSeverity.Error) errors++;
                PrintDiag(outw, opt, d.FilePath, d.Code, SeverityLabel(d.Severity), d.Span, d.Message);
            }
            if (!opt.Quiet)
                errw.Write("link: " + compileResults.Count + " file(s), " + linkResult.Diagnostics.Count + " link diag(s), " + errors + " error(s)\n");
            return errors;
        }

        private static int RunLint(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            List<string> files = CollectPyFiles(target);
            if (files.Count == 0) { errw.Write("no .py files at: " + target + "\n"); return 1; }
            PythonLinter linter = new PythonLinter();
            int errors = 0, total = 0;
            foreach (string path in files)
            {
                string source = ReadAll(path);
                List<LintFinding> findings = linter.Run(source, path);
                foreach (LintFinding f in findings)
                {
                    if (!string.IsNullOrEmpty(opt.CodeFilter) && !f.Code.StartsWith(opt.CodeFilter, StringComparison.Ordinal)) continue;
                    total++;
                    if (f.Severity == AnalysisSeverity.Error) errors++;
                    PrintDiag(outw, opt, path, f.Code, AnalysisSeverityLabel(f.Severity), f.Span, f.Message);
                }
            }
            if (!opt.Quiet)
                errw.Write("lint: " + files.Count + " file(s), " + total + " finding(s), " + errors + " error(s)\n");
            return errors;
        }

        private static int RunDeadCode(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            List<string> files = CollectPyFiles(target);
            if (files.Count == 0) { errw.Write("no .py files at: " + target + "\n"); return 1; }
            IReadOnlyList<VcCompileFileResult> compileResults = VirtualCompiler.CompileSolution(BuildWorkspaceFiles(files));
            List<LinkerCompileFileResult> linkerInputs = ToLinkerInputs(compileResults);
            LinkResult linkResult = PythonLinker.Link(linkerInputs);
            DeadCodeReport report = new DeadCodeEngine().Analyze(linkerInputs, linkResult);

            foreach (DeadCodeFinding f in report.Findings)
            {
                string kind = DeadCodeKindLabels.Label(f.Kind);
                if (!string.IsNullOrEmpty(opt.CodeFilter) && !kind.StartsWith(opt.CodeFilter, StringComparison.Ordinal)) continue;
                if (opt.Json)
                {
                    outw.Write("{\"file\":\"" + EscapeJson(f.FilePath)
                        + "\",\"kind\":\"" + kind
                        + "\",\"line\":" + f.Line
                        + ",\"column\":" + f.Column
                        + ",\"symbol\":\"" + EscapeJson(f.SymbolName)
                        + "\",\"message\":\"" + EscapeJson(f.Message) + "\"}\n");
                }
                else
                {
                    outw.Write(f.FilePath + ":" + f.Line + ":" + f.Column + ": " + kind + " '" + f.SymbolName + "': " + f.Message + "\n");
                }
            }
            if (!opt.Quiet)
                errw.Write("deadcode: " + report.FilesAnalyzed + " file(s) analyzed, " + report.Findings.Count + " finding(s)"
                    + (report.LinkSkipped ? " (link skipped)" : "") + "\n");
            return 0; // dead-code is informational; never fails.
        }

        private static int RunTodo(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            List<string> files = CollectPyFiles(target);
            if (files.Count == 0) { errw.Write("no .py files at: " + target + "\n"); return 1; }

            int total = 0;
            foreach (string path in files)
            {
                foreach (TodoItem t in TodoScanner.ScanText(ReadAll(path), path))
                {
                    // --filter=<tag> restricts to a single tag (e.g. --filter=FIXME).
                    if (!string.IsNullOrEmpty(opt.CodeFilter)
                        && !t.Tag.StartsWith(opt.CodeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    total++;
                    if (opt.Json)
                    {
                        outw.Write("{\"file\":\"" + EscapeJson(t.FilePath)
                            + "\",\"line\":" + t.Line
                            + ",\"column\":" + t.Column
                            + ",\"tag\":\"" + t.Tag
                            + "\",\"message\":\"" + EscapeJson(t.Message) + "\"}\n");
                    }
                    else
                    {
                        outw.Write(t.FilePath + ":" + t.Line + ":" + t.Column
                            + ": " + t.Tag + " " + t.Message + "\n");
                    }
                }
            }
            if (!opt.Quiet)
                errw.Write("todo: " + files.Count + " file(s), " + total + " tag(s)\n");
            return 0; // informational; never fails the build.
        }

        private static int RunMetrics(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            List<string> files = CollectPyFiles(target);
            if (files.Count == 0) { errw.Write("no .py files at: " + target + "\n"); return 1; }

            List<MetricsSource> sources = new List<MetricsSource>();
            foreach (string path in files)
                sources.Add(new MetricsSource(path, ReadAll(path)));
            MetricsReport report = new MetricsEngine().Analyze(sources);

            int overThreshold = 0;
            foreach (FunctionMetric fn in report.Functions)
            {
                bool over = opt.MaxComplexity > 0 && fn.CyclomaticComplexity > opt.MaxComplexity;
                if (over) overThreshold++;
                if (opt.Json)
                {
                    outw.Write("{\"file\":\"" + EscapeJson(fn.FilePath)
                        + "\",\"function\":\"" + EscapeJson(fn.Name)
                        + "\",\"line\":" + fn.Line
                        + ",\"loc\":" + fn.Loc
                        + ",\"statements\":" + fn.StatementCount
                        + ",\"params\":" + fn.ParameterCount
                        + ",\"complexity\":" + fn.CyclomaticComplexity
                        + ",\"maxNesting\":" + fn.MaxNestingDepth
                        + (over ? ",\"overThreshold\":true" : "")
                        + "}\n");
                }
                else
                {
                    outw.Write(fn.FilePath + ":" + fn.Line + ": " + fn.Name
                        + " — complexity " + fn.CyclomaticComplexity
                        + ", loc " + fn.Loc
                        + ", stmts " + fn.StatementCount
                        + ", params " + fn.ParameterCount
                        + ", maxNest " + fn.MaxNestingDepth
                        + (over ? "  [OVER --max-complexity=" + opt.MaxComplexity + "]" : "")
                        + "\n");
                }
            }
            if (!opt.Quiet)
                errw.Write("metrics: " + report.Files.Count + " file(s), " + report.Classes.Count
                    + " class(es), " + report.Functions.Count + " function(s)"
                    + (opt.MaxComplexity > 0 ? ", " + overThreshold + " over complexity " + opt.MaxComplexity : "") + "\n");
            // Exit code = functions over the complexity gate (0 when no gate set).
            return overThreshold;
        }

        private static int RunAnalyze(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            int nodeCount;
            AnalysisReport report = AnalyzeTargetReport(target, out nodeCount);
            if (report == null) { errw.Write("no .py files at: " + target + "\n"); return 1; }

            int errors = 0;
            foreach (AnalysisResult v in report.Violations)
            {
                if (!string.IsNullOrEmpty(opt.CodeFilter) && !v.RuleId.StartsWith(opt.CodeFilter, StringComparison.Ordinal)) continue;
                if (opt.Json)
                {
                    outw.Write("{\"rule\":\"" + v.RuleId
                        + "\",\"severity\":\"" + AnalysisSeverityLabel(v.Severity)
                        + "\",\"message\":\"" + EscapeJson(v.Message) + "\"}\n");
                }
                else
                {
                    outw.Write(v.RuleId + " " + AnalysisSeverityLabel(v.Severity) + ": " + v.Message + "\n");
                }
            }
            foreach (AnalysisResult v in report.Violations)
                if (v.Severity == AnalysisSeverity.Error) errors++;
            if (!opt.Quiet)
                errw.Write("analyze: " + nodeCount + " type(s), " + report.Violations.Count + " violation(s)\n");
            return errors;
        }

        /// <summary>
        /// Build the class diagram for <paramref name="target"/> and run the SOLID rules over
        /// it. Shared by the <c>analyze</c> verb and by <c>refactor --auto</c>, which consumes
        /// only the fired rule IDs as targeting hints. Returns null when there are no .py files.
        /// </summary>
        internal static AnalysisReport AnalyzeTargetReport(string target, out int nodeCount)
        {
            nodeCount = 0;
            List<string> files = CollectPyFiles(target);
            if (files.Count == 0) return null;
            IReadOnlyList<VcCompileFileResult> compileResults = VirtualCompiler.CompileSolution(BuildWorkspaceFiles(files));

            SolutionModel solution = new SolutionModel(target, "cli");
            ProjectModel project = new ProjectModel(target, "cli", solution);

            // Pass 1: short-name -> FQN, plus the set of Protocol-derived classes.
            Dictionary<string, string> shortNameToFqn = new Dictionary<string, string>(StringComparer.Ordinal);
            HashSet<string> protocolClassNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (VcCompileFileResult fr in compileResults)
            {
                Ast.Module mod = fr.Result.AstModule as Ast.Module;
                if (mod == null) continue;
                foreach (Ast.Stmt s in mod.Body)
                {
                    Ast.ClassDef c = s as Ast.ClassDef;
                    if (c == null) continue;
                    string fqn = string.IsNullOrEmpty(fr.ModuleFqn) ? c.Name : fr.ModuleFqn + "." + c.Name;
                    if (!shortNameToFqn.ContainsKey(c.Name)) shortNameToFqn.Add(c.Name, fqn);
                    foreach (Ast.Expr b in c.Bases)
                    {
                        string baseShort = null;
                        Ast.Name bn = b as Ast.Name;
                        if (bn != null) baseShort = bn.Id;
                        else { Ast.Subscript sub = b as Ast.Subscript; if (sub != null) { Ast.Name sn = sub.Value as Ast.Name; if (sn != null) baseShort = sn.Id; } }
                        if (baseShort == "Protocol") { protocolClassNames.Add(c.Name); break; }
                    }
                }
            }

            // Pass 2: build TypeModels.
            foreach (VcCompileFileResult fr in compileResults)
            {
                Ast.Module mod = fr.Result.AstModule as Ast.Module;
                if (mod == null) continue;
                foreach (Ast.Stmt s in mod.Body)
                {
                    Ast.ClassDef cls = s as Ast.ClassDef;
                    if (cls == null) continue;

                    TypeKind kind = TypeKind.ConcreteClass;
                    foreach (Ast.Expr b in cls.Bases)
                    {
                        string baseName = BareBaseName(b);
                        if (baseName == "Protocol") { kind = TypeKind.Interface; break; }
                        if (baseName == "ABC") kind = TypeKind.AbstractClass;
                    }

                    TypeModel tm = new TypeModel(cls.Name, fr.ModuleFqn ?? "", kind, project);
                    tm.SourceFilePath = fr.FilePath;

                    // Generic params from Generic[T] / Protocol[T] bases.
                    foreach (Ast.Expr b in cls.Bases)
                    {
                        Ast.Subscript sub = b as Ast.Subscript;
                        if (sub == null) continue;
                        string bn = BareBaseName(sub.Value);
                        if (bn == "Generic" || bn == "Protocol") CollectGenericParams(tm, sub.Slice);
                    }

                    foreach (Ast.Expr b in cls.Bases)
                    {
                        string bn = BareBaseName(b);
                        if (string.IsNullOrEmpty(bn) || bn == "Generic" || bn == "Protocol") continue;
                        string fqn = shortNameToFqn.TryGetValue(bn, out string mapped) ? mapped : bn;
                        RelationshipKind rk = protocolClassNames.Contains(bn) ? RelationshipKind.Implements : RelationshipKind.Inherits;
                        tm.BaseTypes.Add(new TypeReference(fqn, rk));
                    }

                    foreach (Ast.Stmt bodyStmt in cls.Body)
                    {
                        MemberModel mm = ExtractMember(bodyStmt);
                        if (mm != null) tm.Members.Add(mm);
                    }

                    project.Types.Add(tm);
                }
            }
            solution.Projects.Add(project);

            Diagram diagram = new DiagramProjection(solution, new LayoutModel(target)).Build();
            nodeCount = diagram.Nodes.Count;
            return new AnalysisEngine().Analyze(diagram);
        }

        // ---- refactor ----

        /// <summary>
        /// Treats the whole file as one selection, runs the structural matchers, and either
        /// lists candidates, applies one, or performs a forward+reverse round-trip
        /// exact-equality check. Port of the C++ <c>RunRefactor</c>.
        /// </summary>
        private static int RunRefactor(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            if (!File.Exists(target))
            {
                errw.Write("refactor: target must be a single .py file: " + target + "\n");
                return 2;
            }
            string source = ReadAll(target);
            Kind kind;

            // Round-trip mode: apply forward kind, then its paired reverse, and compare to
            // the original source.
            if (!string.IsNullOrEmpty(opt.RoundtripKind))
            {
                if (!StructuralRefactor.KindFromToken(opt.RoundtripKind, out kind))
                {
                    errw.Write("refactor: unknown kind: " + opt.RoundtripKind + "\n");
                    return 2;
                }
                string forwardApplied = StructuralRefactor.Apply(source, kind);
                if (forwardApplied == null)
                {
                    errw.Write("refactor: no candidate of kind " + opt.RoundtripKind
                        + " found in " + target + "\n");
                    return 1;
                }
                Kind rev = StructuralRefactor.PairedKind(kind);
                string reverseApplied = StructuralRefactor.Apply(forwardApplied, rev);
                if (reverseApplied == null)
                {
                    errw.Write("refactor: reverse (" + StructuralRefactor.TokenForKind(rev)
                        + ") did not match the forward output\n");
                    return 1;
                }
                bool exact = reverseApplied == source;
                outw.Write("forward (" + opt.RoundtripKind + ") ->\n"
                    + "-----------------------------------\n"
                    + forwardApplied + "\n"
                    + "-----------------------------------\n"
                    + "reverse (" + StructuralRefactor.TokenForKind(rev) + ") ->\n"
                    + "-----------------------------------\n"
                    + reverseApplied + "\n"
                    + "-----------------------------------\n"
                    + "round-trip exact-equality: " + (exact ? "PASS" : "FAIL") + "\n");
                return exact ? 0 : 1;
            }

            // Apply mode: print the transformed source for the requested kind.
            if (opt.Apply || !string.IsNullOrEmpty(opt.RefactorKind))
            {
                if (!StructuralRefactor.KindFromToken(opt.RefactorKind, out kind))
                {
                    errw.Write("refactor: --kind=<K> required for --apply\n");
                    return 2;
                }
                string applied = StructuralRefactor.Apply(source, kind);
                if (applied == null)
                {
                    errw.Write("refactor: no candidate of kind " + opt.RefactorKind
                        + " found in " + target + "\n");
                    return 1;
                }
                outw.Write(applied);
                return 0;
            }

            // List mode.
            List<StructuralCandidate> candidates = StructuralRefactor.Match(source);

            // --auto: gate the candidate list by analysis findings. The analyzer
            // (SRP001/DIP001/ISP001/PAT001-006) reports smells/opportunities with no source
            // spans, so they are used only as targeting HINTS via the pure-logic
            // DetectionBridge; the structural matchers still own all span derivation.
            if (opt.AutoHint)
            {
                int nodeCount;
                AnalysisReport report = AnalyzeTargetReport(target, out nodeCount);
                List<string> firedRules = new List<string>();
                if (report != null)
                {
                    foreach (AnalysisResult v in report.Violations) firedRules.Add(v.RuleId);
                }
                candidates = DetectionBridge.Gate(firedRules, candidates);
                if (!opt.Quiet)
                {
                    errw.Write("refactor --auto: " + firedRules.Count + " analysis hint(s) gated to "
                        + candidates.Count + " candidate(s)\n");
                }
            }

            foreach (StructuralCandidate c in candidates)
            {
                if (opt.Json)
                {
                    outw.Write("{\"kind\":\"" + StructuralRefactor.TokenForKind(c.PatternKind)
                        + "\",\"label\":\"" + EscapeJson(c.Label)
                        + "\",\"startLine\":" + c.StartLine
                        + ",\"endLine\":" + c.EndLine
                        + ",\"confidence\":" + c.Confidence
                        + ",\"lossy\":" + (c.Lossy ? "true" : "false") + "}\n");
                }
                else
                {
                    outw.Write(StructuralRefactor.TokenForKind(c.PatternKind) + "  [lines "
                        + c.StartLine + "-" + c.EndLine + "]  " + c.Label + "\n");
                }
            }
            if (!opt.Quiet) errw.Write("refactor: " + candidates.Count + " candidate(s)\n");
            return 0;
        }

        // ---- CUDA (headless verification path for the CUDA subsystem) ----

        /// <summary>
        /// <c>cuda-compile &lt;dir-or-file&gt;</c> — statically compile every .cu with the CUDA
        /// virtual compiler and print its CU#### diagnostics. Exit code = error count.
        /// </summary>
        private static int RunCudaCompile(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            List<string> files = CollectCuFiles(target);
            if (files.Count == 0) { errw.Write("no .cu files at: " + target + "\n"); return 1; }

            int errors = 0, total = 0;
            int nvccErrors = 0; bool nvccRan = false; string nvccVersion = null;
            foreach (string path in files)
            {
                CudaCompileResult r = CudaCompiler.CompileFile(path);
                foreach (CudaCompileDiagnostic d in r.Diagnostics)
                {
                    if (!opt.IncludeWarnings && d.Severity < ParseSeverity.Warning) continue;
                    total++;
                    if (d.Severity >= ParseSeverity.Error) errors++;
                    PrintDiag(outw, opt, d.FilePath, d.Code, SeverityLabel(d.Severity), d.Span, d.Message);
                }

                // Authoritative parity pass: shell out to the real nvcc when --nvcc is set.
                if (opt.Nvcc)
                {
                    HelixPythonNamespace.CudaVirtualCompiler.NvccVerifyResult nv =
                        HelixPythonNamespace.CudaVirtualCompiler.NvccVerifier.Verify(path);
                    if (!nv.Available)
                    {
                        if (!opt.Quiet) errw.Write("cuda-compile: nvcc unavailable (" + (nv.Reason ?? "unknown") + ")\n");
                    }
                    else
                    {
                        nvccRan = true; nvccVersion = nv.NvccVersion;
                        foreach (var d in nv.Diagnostics)
                        {
                            if (!opt.IncludeWarnings && !d.IsError) continue;
                            outw.Write(string.Format("{0}({1}): nvcc {2}: {3}\n",
                                d.FilePath, d.Line, d.IsError ? "error" : "warning", d.Message));
                        }
                        nvccErrors += nv.ErrorCount;
                    }
                }
            }
            if (!opt.Quiet)
            {
                errw.Write("cuda-compile: " + files.Count + " file(s), " + total + " diagnostic(s), " + errors + " error(s)\n");
                if (nvccRan)
                    errw.Write("cuda-compile[nvcc " + (nvccVersion ?? "?") + "]: " + nvccErrors + " authoritative error(s)"
                        + (nvccErrors != errors ? "  <-- differs from virtual compiler (" + errors + ")" : "  (matches virtual compiler)") + "\n");
            }
            // When nvcc ran, its error count is authoritative for the exit code.
            return nvccRan ? nvccErrors : errors;
        }

        /// <summary>
        /// <c>cuda-run &lt;file.cu&gt;</c> — compile the .cu and execute its <c>main()</c> on the
        /// CPU CUDA emulator, streaming stdout. Exit code = the program's exit code, or 1 on a
        /// runtime fault.
        ///
        /// <para>
        /// The counterpart to <c>cuda-compile</c>, which shells out to a real <c>nvcc</c> when one
        /// is installed. Having both is the point: the same kernel can be checked against the
        /// authoritative compiler and RUN with no GPU and no CUDA toolkit present, which is what
        /// makes a kernel testable on a machine that cannot execute one.
        /// </para>
        /// </summary>
        private static int RunCudaRun(string target, CliOptions opt, TextWriter outw, TextWriter errw)
        {
            List<string> files = CollectCuFiles(target);
            if (files.Count == 0) { errw.Write("no .cu files at: " + target + "\n"); return 1; }

            string path = files[0];
            CudaCompileResult compiled = CudaCompiler.CompileFile(path);
            CudaStringSink sink = new CudaStringSink();
            CudaEmulator emu = new CudaEmulator(compiled, sink);
            CudaEmulationResult run = emu.RunProgram("main");
            outw.Write(sink.Text);
            if (!opt.Quiet)
                errw.Write("cuda-run: " + path + (run.Ok ? " ok, exit " + run.ExitCode : " runtime error") + "\n");
            return run.Ok ? run.ExitCode : 1;
        }

        internal static List<string> CollectCuFiles(string path)
        {
            List<string> outList = new List<string>();
            if (File.Exists(path))
            {
                string ext = Path.GetExtension(path);
                if (string.Equals(ext, ".cu", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".cuh", StringComparison.OrdinalIgnoreCase))
                    outList.Add(path);
                return outList;
            }
            if (Directory.Exists(path))
                CollectCuFilesRecursive(path, outList);
            return outList;
        }

        private static void CollectCuFilesRecursive(string dir, List<string> outList)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.cu")) outList.Add(f);
                foreach (string f in Directory.GetFiles(dir, "*.cuh")) outList.Add(f);
            }
            catch { }
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (string sub in subDirs)
            {
                if (SkipDirs.Contains(Path.GetFileName(sub))) continue;
                CollectCuFilesRecursive(sub, outList);
            }
        }

        // ---- analyze helpers ----

        private static string BareBaseName(Ast.Expr e)
        {
            if (e == null) return "";
            Ast.Name n = e as Ast.Name;
            if (n != null) return n.Id;
            Ast.AttributeExpr a = e as Ast.AttributeExpr;
            if (a != null) return a.Attr;
            Ast.Subscript s = e as Ast.Subscript;
            if (s != null) { Ast.Name sn = s.Value as Ast.Name; if (sn != null) return sn.Id; }
            return "";
        }

        private static void CollectGenericParams(TypeModel tm, Ast.Expr slice)
        {
            if (slice == null) return;
            Ast.TupleExpr tup = slice as Ast.TupleExpr;
            if (tup != null) { foreach (Ast.Expr elt in tup.Elts) PushGenericParam(tm, elt); }
            else PushGenericParam(tm, slice);
        }

        private static void PushGenericParam(TypeModel tm, Ast.Expr p)
        {
            string paramName = null;
            Ast.Name n = p as Ast.Name;
            if (n != null) paramName = n.Id;
            else { Ast.AttributeExpr a = p as Ast.AttributeExpr; if (a != null) paramName = a.Attr; }
            if (string.IsNullOrEmpty(paramName)) return;
            foreach (GenericParameterModel existing in tm.GenericParameters)
                if (existing.Name == paramName) return;
            tm.GenericParameters.Add(new GenericParameterModel(paramName));
        }

        private static MemberModel ExtractMember(Ast.Stmt s)
        {
            Ast.FunctionDef fn = s as Ast.FunctionDef;
            if (fn != null) return BuildFromFn(fn.Args, fn.Name, fn.Returns, fn.Span.Start.Line, false, fn.DecoratorList);
            Ast.AsyncFunctionDef afn = s as Ast.AsyncFunctionDef;
            if (afn != null) return BuildFromFn(afn.Args, afn.Name, afn.Returns, afn.Span.Start.Line, true, afn.DecoratorList);
            Ast.AnnAssign an = s as Ast.AnnAssign;
            if (an != null)
            {
                Ast.Name nm = an.Target as Ast.Name;
                if (nm != null)
                {
                    MemberModel m = new MemberModel(nm.Id, MemberKind.Field) { SourceLineNumber = an.Span.Start.Line };
                    Ast.Name tn = an.Annotation as Ast.Name;
                    if (tn != null) m.ReturnType = tn.Id;
                    return m;
                }
            }
            Ast.Assign asn = s as Ast.Assign;
            if (asn != null && asn.Targets.Count == 1)
            {
                Ast.Name nm = asn.Targets[0] as Ast.Name;
                if (nm != null)
                    return new MemberModel(nm.Id, MemberKind.Field) { SourceLineNumber = asn.Span.Start.Line };
            }
            return null;
        }

        private static MemberModel BuildFromFn(Ast.Arguments args, string name, Ast.Expr returns, int line, bool isAsync, IReadOnlyList<Ast.Expr> decs)
        {
            MemberModel m = new MemberModel(name, name == "__init__" ? MemberKind.Constructor : MemberKind.Method)
            {
                SourceLineNumber = line,
                IsAsync = isAsync,
                IsAbstract = DecoratorIs(decs, "abstractmethod"),
                IsStatic = DecoratorIs(decs, "staticmethod"),
                AccessModifier = (!string.IsNullOrEmpty(name) && name[0] == '_' && (name.Length < 2 || name[1] != '_'))
                    ? AccessModifier.Private : AccessModifier.Public,
            };
            if (returns != null) m.ReturnType = AnnotationToString(returns);
            if (args != null)
            {
                foreach (Ast.Arg a in args.PosOnlyArgs) PushArg(m, a);
                foreach (Ast.Arg a in args.Args) PushArg(m, a);
                foreach (Ast.Arg a in args.KwOnlyArgs) PushArg(m, a);
            }
            return m;
        }

        private static void PushArg(MemberModel m, Ast.Arg a)
        {
            string typeName = a.Annotation != null ? AnnotationToString(a.Annotation) : null;
            m.Parameters.Add(new MethodParameterModel(a.Name, typeName));
        }

        private static bool DecoratorIs(IReadOnlyList<Ast.Expr> decs, string want)
        {
            foreach (Ast.Expr d in decs)
            {
                Ast.Name nm = d as Ast.Name;
                if (nm != null && nm.Id == want) return true;
                Ast.AttributeExpr attr = d as Ast.AttributeExpr;
                if (attr != null && attr.Attr == want) return true;
            }
            return false;
        }

        private static string AnnotationToString(Ast.Expr e)
        {
            if (e == null) return "";
            Ast.Name n = e as Ast.Name;
            if (n != null) return n.Id;
            Ast.AttributeExpr a = e as Ast.AttributeExpr;
            if (a != null) return a.Attr;
            Ast.Subscript sub = e as Ast.Subscript;
            if (sub != null)
            {
                Ast.Name sn = sub.Value as Ast.Name;
                if (sn != null) return sn.Id;
                Ast.AttributeExpr sa = sub.Value as Ast.AttributeExpr;
                if (sa != null) return sa.Attr;
                return "<subscript>";
            }
            return "<annotated>";
        }

        // ---- shared helpers ----

        // Directories that hold third-party / generated / cache code, never the user's
        // own sources. Mirrors the App's BuildRunCoordinator skip set. Walking into these
        // on a real project (a populated .venv alone is tens of thousands of .py files)
        // turned a sub-second run into a multi-minute one and produced bogus diagnostics
        // from vendored code.
        private static readonly HashSet<string> SkipDirs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "__pycache__", ".venv", "venv", "env", ".env",
                "build", "dist", ".tox", ".pytest_cache", ".mypy_cache",
                ".git", "node_modules", ".idea", ".vs",
            };

        internal static List<string> CollectPyFiles(string path)
        {
            List<string> outList = new List<string>();
            if (File.Exists(path))
            {
                if (string.Equals(Path.GetExtension(path), ".py", StringComparison.OrdinalIgnoreCase))
                    outList.Add(path);
                return outList;
            }
            if (Directory.Exists(path))
                CollectPyFilesRecursive(path, outList);
            return outList;
        }

        private static void CollectPyFilesRecursive(string dir, List<string> outList)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.py"); }
            catch { files = null; }
            if (files != null)
            {
                foreach (string f in files) outList.Add(f);
            }

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (string sub in subDirs)
            {
                string name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name)) continue;
                CollectPyFilesRecursive(sub, outList);
            }
        }

        private static string ReadAll(string path)
        {
            try { return File.ReadAllText(path); }
            catch { return ""; }
        }

        internal static List<WorkspaceFile> BuildWorkspaceFiles(IEnumerable<string> paths)
        {
            List<WorkspaceFile> outList = new List<WorkspaceFile>();
            foreach (string p in paths)
                outList.Add(new WorkspaceFile(p, PythonModuleFqn.Derive(p)));
            return outList;
        }

        private static List<LinkerCompileFileResult> ToLinkerInputs(IReadOnlyList<VcCompileFileResult> compileResults)
        {
            List<LinkerCompileFileResult> outList = new List<LinkerCompileFileResult>();
            foreach (VcCompileFileResult fr in compileResults)
            {
                Ast.Module mod = fr.Result.AstModule as Ast.Module;
                if (mod == null) continue;
                outList.Add(new LinkerCompileFileResult(mod, fr.ModuleFqn, fr.FilePath));
            }
            return outList;
        }

        private static void PrintDiag(TextWriter outw, CliOptions opt, string filePath, string code, string severity, SourceSpan span, string message)
        {
            if (!string.IsNullOrEmpty(opt.CodeFilter) && !code.StartsWith(opt.CodeFilter, StringComparison.Ordinal)) return;
            if (opt.Json)
            {
                outw.Write("{\"file\":\"" + EscapeJson(filePath)
                    + "\",\"code\":\"" + code
                    + "\",\"severity\":\"" + severity
                    + "\",\"line\":" + span.Start.Line
                    + ",\"column\":" + span.Start.Column
                    + ",\"message\":\"" + EscapeJson(message) + "\"}\n");
            }
            else
            {
                outw.Write(filePath + ":" + span.Start.Line + ":" + span.Start.Column
                    + ": " + severity + " " + code + ": " + message + "\n");
            }
        }

        internal static string EscapeJson(string s)
        {
            if (s == null) return "";
            StringBuilder outSb = new StringBuilder(s.Length + 2);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': outSb.Append("\\\""); break;
                    case '\\': outSb.Append("\\\\"); break;
                    case '\n': outSb.Append("\\n"); break;
                    case '\r': outSb.Append("\\r"); break;
                    case '\t': outSb.Append("\\t"); break;
                    default:
                        if (c < 0x20) outSb.Append("\\u").Append(((int)c).ToString("x4"));
                        else outSb.Append(c);
                        break;
                }
            }
            return outSb.ToString();
        }

        internal static string SeverityLabel(ParseSeverity s)
        {
            switch (s)
            {
                case ParseSeverity.Hidden: return "hidden";
                case ParseSeverity.Info: return "info";
                case ParseSeverity.Warning: return "warning";
                case ParseSeverity.Error: return "error";
                case ParseSeverity.Fatal: return "fatal";
            }
            return "?";
        }

        internal static string AnalysisSeverityLabel(AnalysisSeverity s)
        {
            switch (s)
            {
                case AnalysisSeverity.Info: return "info";
                case AnalysisSeverity.Warning: return "warning";
                case AnalysisSeverity.Error: return "error";
            }
            return "?";
        }

        public static void PrintUsage(TextWriter outw)
        {
            outw.Write(
                "helix_cli — drive the analysis engines\n" +
                "\n" +
                "Usage:\n" +
                "  helix_cli compile  <dir-or-file>\n" +
                "  helix_cli link     <dir-or-file>\n" +
                "  helix_cli lint     <dir-or-file>\n" +
                "  helix_cli deadcode <dir-or-file>\n" +
                "  helix_cli metrics  <dir-or-file>   (LOC / complexity / nesting per function)\n" +
                "  helix_cli todo     <dir-or-file>   (TODO/FIXME/HACK/XXX/NOTE comment tags)\n" +
                "  helix_cli analyze  <dir>           (SOLID rules over class hierarchy)\n" +
                "  helix_cli debug    <script.py>     (live debugpy/DAP session, headless)\n" +
                "  helix_cli refactor <file>          (structural transforms; see flags)\n" +
                "  helix_cli cuda-compile <dir-or-file>  (static CUDA .cu analysis, CU#### diags)\n" +
                "  helix_cli cuda-run     <file.cu>      (run main() on the CPU CUDA emulator)\n" +
                "  helix_cli gui-emit     [form.json]    (emit GUI source; --framework=tkinter|pyqt6|pyside6|wxpython)\n" +
                "  helix_cli toml-lint    <file-or-dir>  (native TOML linter: TLN00x/TLN010 + parser diags)\n" +
                "  helix_cli toml-analyze <file-or-dir>  (native TOML analyzer: TMA001-004 PEP-621/508 + workspace)\n" +
                "  helix_cli py-roundtrip <file-or-dir>  (lossless Python source<->AST round-trip; byte-identical check)\n" +
                "  helix_cli venv-create  <venvDir>      (REAL python -m venv; --python=<base> --bind=<projectDir>)\n" +
                "  helix_cli venv-list    <project-dir>  (discover venvs + show the bound interpreter)\n" +
                "  helix_cli req-sync     <requirements> (append pyproject deps; --pyproject=<path>)\n" +
                "  helix_cli req-diff     <requirements> (diff vs pyproject deps; --pyproject=<path>)\n" +
                "  helix_cli dssp         <file|dir> (assign secondary structure, write .dssp beside each;\n" +
                "                                     --reference for mkdssp-comparable output, --recursive, --json)\n" +
                "  helix_cli query        <structure> --sql=<statement> (atoms query -> CSV;\n" +
                "                                     --frames for one answer per frame;\n" +
                "                                     --topology=<file> when the target is a .dcd)\n" +
                "\n" +
                "IDE services (test / profile / metrics — headless, per-language):\n" +
                "  helix_cli ctest-run    <build-dir>    (REAL ctest; CUDA/CMake test results)\n" +
                "  helix_cli cpp-profile  <target-dir>   (REAL native sampling profiler -> WorkspaceProfile)\n" +
                "  helix_cli cuda-profile <file.cu>      (REAL nvcc -Xptxas -v; regs/smem/spills + theoretical occupancy; --block=<n>)\n" +
                "  helix_cli code-metrics <dir-or-file>  (native lexical metrics for CUDA/C++)\n" +
                "  helix_cli cpp-lint     <file>         (native C++ linter — no external tool)\n" +
                "\n" +
                "Flags:\n" +
                "  --json                 emit JSON (machine-readable)\n" +
                "  --quiet                suppress per-file headers and summary\n" +
                "  --kind=<K>             (refactor) transform kind, e.g. P1_FactoryIntroduce\n" +
                "  --apply                (refactor) print the transformed source\n" +
                "  --roundtrip=<K>        (refactor) apply forward K then its reverse; exact check\n" +
                "  --auto                 (refactor) list only candidates hinted by analysis findings\n" +
                "  --filter=<code>        show only diagnostics whose code starts with this\n" +
                "  --errors-only          hide warning/info diagnostics (compile/link)\n" +
                "  --max-complexity=<n>   metrics: exit non-zero per function over this complexity\n" +
                "  --break=<[file:]line>  debug: set a breakpoint (repeatable; bare line = script)\n" +
                "  --stop-on-entry        debug: break on the first line\n" +
                "  --step=<over|into|out> debug: single-step at each stop instead of continuing\n" +
                "  --watch=<expr>         debug: evaluate expr at each stop (repeatable)\n" +
                "  --exceptions=<f[,f]>   debug: break on exceptions (raised|uncaught|userUnhandled)\n" +
                "  --max-stops=<n>        debug: cap total stops (default 200; guards step loops)\n" +
                "  --timeout=<ms>         debug: session timeout (default 30000)\n" +
                "\n" +
                "Exit code = total number of error-severity diagnostics (0 on clean);\n" +
                "for 'metrics', the number of functions over --max-complexity.\n");
        }
    }
}
