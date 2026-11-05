namespace HelixPythonNamespace.Launcher
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Workspace.Execution;
    using HelixPythonNamespace.Workspace.Services;
    using HelixPythonNamespace.Workspace.Structure;
    using HelixPythonNamespace.Workspace.Toolchains;
    using System.Threading;

    /// <summary>
    /// The headless equivalent of the IDE's action buttons: open a solution, apply the four
    /// solution-wide rules, and then run, compile, lint, review, profile or test it.
    ///
    /// It decides nothing itself. The load goes through the production
    /// <see cref="SolutionService"/>, the PY8004 verdict through
    /// <see cref="StructureParityGate"/>, what a launch consists of through
    /// <see cref="SolutionRunPlanner"/>, and which files an engine sees through
    /// <see cref="SolutionSourceInventory"/> — the same four the IDE uses, so a solution that runs
    /// from the toolbar runs here, and one the IDE refuses is refused here for the same stated
    /// reason.
    /// </summary>
    public static class SolutionLauncher
    {
        /// <summary>
        /// Write the environment lock beside the solution's manifest.
        ///
        /// pyproject.toml says what the project ASKED for; this says what answered — the exact
        /// interpreter, the resolved package versions, and the machine's toolchains. That pairing is
        /// what turns "we use pyproject.toml" into a reproducibility claim.
        ///
        /// Best-effort by construction. Every failure is recorded IN the file rather than raised:
        /// an unwritable directory, a Python without pip, a toolchain scan that timed out. The one
        /// thing it must never do is prevent the user's program from running.
        /// </summary>
        private static void CaptureEnvironment(SolutionModel solution, string python,
                                               LauncherOptions options, TextWriter outw)
        {
            try
            {
                var scanner = new DevelopmentEnvironmentScanner(new ProcessToolProbeRunner());
                DevelopmentEnvironmentReport toolchains = scanner.Scan(CancellationToken.None);

                EnvironmentRecord record = EnvironmentCapture.Capture(
                    python, toolchains, new ProcessInterpreterProbe(), CancellationToken.None);

                string written = EnvironmentCapture.WriteBeside(
                    SolutionRunPlanner.SolutionRootDirectory(solution), record);

                if (written != null && !options.Quiet)
                {
                    outw.WriteLine("helixstudio: environment captured to " + Path.GetFileName(written)
                                   + (record.IsComplete ? string.Empty : " (incomplete: " + record.Incompleteness + ")"));
                }
            }
            catch (Exception)
            {
                // Deliberately swallowed. A note about the machine is not worth a failed run.
            }
        }

        /// <summary>Something was reported: compile errors, an unclean review, failing tests.</summary>
        public const int ExitFindings = 1;
        /// <summary>Bad command line.</summary>
        public const int ExitUsage = 2;
        /// <summary>The solution could not be opened.</summary>
        public const int ExitSolutionNotLoaded = 3;
        /// <summary>A rule blocked the action, or there was nothing runnable to start.</summary>
        public const int ExitBlocked = 4;
        /// <summary>No Python interpreter was found.</summary>
        public const int ExitNoInterpreter = 5;

        public static int Run(IList<string> args, TextWriter outw, TextWriter errw, IProgramLauncher launcher)
        {
            LauncherOptions options = LauncherOptions.Parse(args);
            if (options.ShowHelp) { LauncherOptions.PrintUsage(outw); return 0; }
            if (options.Error != null) return UsageError(options.Error, errw);

            // The profiler's wrapper takes the target and nothing else, so argv would be accepted
            // and silently dropped. Saying so beats running a different program than was asked for.
            if (options.ProgramArgumentsGiven && options.Verb == LauncherVerb.Profile)
                return UsageError("--profile cannot pass arguments to the program: the profiler "
                    + "wrapper runs the entry point with no argv. Use --run to pass arguments.", errw);

            SolutionLoadResult load;
            try { load = new SolutionService().LoadAsync(options.SolutionPath).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                errw.WriteLine("helixstudio: could not open " + options.SolutionPath + ": " + ex.Message);
                return ExitSolutionNotLoaded;
            }
            if (load == null || !load.Success || load.Solution == null)
            {
                errw.WriteLine("helixstudio: could not open " + options.SolutionPath
                    + (load != null && !string.IsNullOrEmpty(load.ErrorMessage) ? ": " + load.ErrorMessage : "."));
                return ExitSolutionNotLoaded;
            }
            SolutionModel solution = load.Solution;

            if (options.ListPackages) { ListPackages(solution, outw); return 0; }

            // The scope every verb shares. Null means "the whole solution" to the reading verbs and
            // "the startup package" to the executing ones — which is what the equivalent IDE
            // command does with nothing selected.
            ProjectModel scope = null;
            if (!string.IsNullOrEmpty(options.PackageName))
            {
                scope = FindPackage(solution, options.PackageName);
                if (scope == null)
                {
                    errw.WriteLine("helixstudio: '" + options.PackageName + "' is not a package of this solution.");
                    errw.WriteLine("Its packages are:");
                    ListPackages(solution, errw);
                    return ExitUsage;
                }
            }

            SolutionRunOptions runOptions = BuildRunOptions(load, options);

            switch (options.Verb)
            {
                case LauncherVerb.Compile:
                    return AnalysisVerbs.Compile(solution, scope, runOptions, options, outw, errw);
                case LauncherVerb.Lint:
                    return AnalysisVerbs.Lint(solution, scope, options, outw, errw);
                case LauncherVerb.CodeReview:
                    return AnalysisVerbs.CodeReview(solution, scope, options, outw, errw);
                case LauncherVerb.RunTest:
                    return ExecuteTests(solution, scope, runOptions, options, outw, errw);
                case LauncherVerb.Profile:
                case LauncherVerb.Run:
                default:
                    return ExecuteProgram(solution, scope, runOptions, options, outw, errw, launcher);
            }
        }

        // ---- the verbs that execute the entry point -------------------------------------

        private static int ExecuteProgram(SolutionModel solution, ProjectModel scope,
                                          SolutionRunOptions runOptions, LauncherOptions options,
                                          TextWriter outw, TextWriter errw, IProgramLauncher launcher)
        {
            SolutionRunPlan plan = scope == null
                ? SolutionRunPlanner.ComputeRunPlan(solution, runOptions)
                : SolutionRunPlanner.ComputeRunPlanFor(solution, scope, runOptions);

            if (!plan.CanRun) return ReportBlocked(plan, options.Verb, errw);

            string python = FindPythonInterpreter();
            if (python == null) return NoInterpreter(errw);

            if (options.Verb == LauncherVerb.Profile)
                return ExecutionVerbs.Profile(solution, plan, python, options, outw, errw);

            List<string> pyArgs = ComposeLaunchArguments(plan, options);
            IReadOnlyDictionary<string, string> env = SolutionRunPlanner.BuildPythonPathEnv(solution);

            if (options.DryRun)
            {
                outw.WriteLine(python + " " + HelixPythonNamespace.Core.CommandLineArguments.Join(pyArgs));
                outw.WriteLine("  package: " + plan.StartupName);
                outw.WriteLine("  in:      " + (plan.WorkingDirectory ?? "(current directory)"));
                string pythonPath;
                outw.WriteLine("  PYTHONPATH: " +
                    (env != null && env.TryGetValue("PYTHONPATH", out pythonPath) ? pythonPath : "(unchanged)"));
                return 0;
            }

            // Written BEFORE the program starts, and never allowed to stop it: this describes the
            // environment the result was produced in, and a run that failed still has a provenance
            // worth having. --no-capture is for a read-only checkout or a hot loop.
            if (!options.NoEnvironmentCapture) CaptureEnvironment(solution, python, options, outw);

            try
            {
                return (launcher ?? new ProcessProgramLauncher())
                    .Launch(python, pyArgs, plan.WorkingDirectory, env);
            }
            catch (Exception ex)
            {
                errw.WriteLine("helixstudio: could not start " + python + ": " + ex.Message);
                return ExitNoInterpreter;
            }
        }

        /// <summary>
        /// <c>--run-test</c> executes the user's code, so it is gated by the same four rules — but
        /// it does not need an entry point, so it asks the planner only for the blockers rather than
        /// for a full run plan. A test project has no <c>def main()</c>, and demanding one would
        /// refuse every solution whose tests are correctly laid out.
        /// </summary>
        private static int ExecuteTests(SolutionModel solution, ProjectModel scope,
                                        SolutionRunOptions runOptions, LauncherOptions options,
                                        TextWriter outw, TextWriter errw)
        {
            SolutionRunPlan blocked = SolutionRunPlanner.ComputeBlockers(solution, runOptions);
            if (blocked != null) return ReportBlocked(blocked, options.Verb, errw);

            string python = FindPythonInterpreter();
            if (python == null) return NoInterpreter(errw);

            return ExecutionVerbs.RunTests(solution, scope, python, options, outw, errw);
        }

        private static int ReportBlocked(SolutionRunPlan plan, LauncherVerb verb, TextWriter errw)
        {
            errw.WriteLine("helixstudio: " + LauncherVerbs.Flag(verb) + " blocked.");
            errw.WriteLine(plan.BlockReason);
            // The summary says a rule was broken; without this the user is not told WHERE.
            if (plan.PolicyViolations != null && plan.PolicyViolations.Count > 0)
            {
                errw.WriteLine();
                foreach (var d in plan.PolicyViolations) errw.WriteLine("  " + d);
            }
            return ExitBlocked;
        }

        private static int NoInterpreter(TextWriter errw)
        {
            errw.WriteLine("helixstudio: no Python interpreter found on PATH. Install Python and retry.");
            return ExitNoInterpreter;
        }

        private static int UsageError(string message, TextWriter errw)
        {
            errw.WriteLine("helixstudio: " + message);
            errw.WriteLine("Try 'helixstudio --help'.");
            return ExitUsage;
        }

        // ---- shared --------------------------------------------------------------------

        /// <summary>
        /// What this host contributes to the shared planner. Two of the three differ from the IDE's
        /// answers, and deliberately: there are no preferences to read (so rule 2 is enforced unless
        /// the caller says otherwise), and there is nobody to confirm a restructure (so a PY8004
        /// mismatch is reported rather than repaired).
        /// </summary>
        private static SolutionRunOptions BuildRunOptions(SolutionLoadResult load, LauncherOptions options)
        {
            StructureParityVerdict verdict =
                StructureParityGate.Evaluate(load.StructureParity, new DirectoryStructureCorrectionEngine());

            string structureBlock = verdict.IsVerified
                ? null
                : StructureParityGate.ComposeBlockReason(verdict.Reason,
                    verdict.RepairAvailable
                        // Naming the action a console cannot take would be a dead end; name the one
                        // host that can.
                        ? verdict.Remedy + " Open the solution in HelixStudio and accept the restructure when prompted."
                        : verdict.Remedy);

            return new SolutionRunOptions
            {
                EnforceObjectOrientation = !options.AllowNonObjectOriented,
                StructureBlockReason = structureBlock,
                MessageStyle = new PolicyMessageStyle
                {
                    // No context menu out here, and no Problems panel — the violations are printed
                    // in full instead, so pointing at either would be an instruction to nowhere.
                    QuickFixCaption = null,
                    FullListHint = null,
                    // The default "•" prints as a blank on the default Windows console code page.
                    Bullet = "* ",
                },
            };
        }

        /// <summary>
        /// The interpreter argv. <c>--</c> on the command line REPLACES the package's stored
        /// <c>run-args</c>: a caller who spells argv out means that argv, and silently appending it
        /// to a run-args line they cannot see would produce a command neither of them wrote.
        /// </summary>
        private static List<string> ComposeLaunchArguments(SolutionRunPlan plan, LauncherOptions options)
        {
            if (!options.ProgramArgumentsGiven) return SolutionRunPlanner.BuildPythonLaunchArgs(plan);

            string stored = plan.RunArguments;
            try
            {
                plan.RunArguments = null;
                List<string> composed = SolutionRunPlanner.BuildPythonLaunchArgs(plan);
                composed.AddRange(options.ProgramArguments);
                return composed;
            }
            finally { plan.RunArguments = stored; }
        }

        /// <summary>
        /// The member whose <c>[project] name</c> this is. Exact match first, then case-insensitive:
        /// a package name is case-sensitive in the manifest, but a user typing one at a Windows
        /// prompt should not be refused over its casing when there is exactly one candidate.
        /// </summary>
        internal static ProjectModel FindPackage(SolutionModel solution, string name)
        {
            if (solution == null || solution.Projects == null || string.IsNullOrEmpty(name)) return null;
            foreach (ProjectModel p in solution.Projects)
                if (p != null && string.Equals(p.Name, name, StringComparison.Ordinal)) return p;

            ProjectModel loose = null;
            foreach (ProjectModel p in solution.Projects)
            {
                if (p == null || !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (loose != null) return null;   // ambiguous only by case — refuse to guess
                loose = p;
            }
            return loose;
        }

        private static void ListPackages(SolutionModel solution, TextWriter w)
        {
            if (solution.Projects == null || solution.Projects.Count == 0)
            {
                w.WriteLine("  (this solution has no member packages)");
                return;
            }
            foreach (ProjectModel p in solution.Projects)
            {
                if (p == null) continue;
                string entry;
                bool runnable = SolutionRunPlanner.TryFindEntryFile(p, out entry);
                w.WriteLine("  " + p.Name
                    + "  [" + ProjectKinds.ToToken(p.Kind) + "]"
                    + (runnable ? "  runnable" : "  library")
                    + (p.IsStartup ? "  (startup)" : ""));
            }
        }

        /// <summary>
        /// The default interpreter, found the same way the IDE's Run button finds it (PATH,
        /// registry, well-known locations, the py launcher).
        /// </summary>
        private static string FindPythonInterpreter()
        {
            try
            {
                var interpreter = new HelixPythonNamespace.Debugger.PythonInterpreterManager().DiscoverDefault();
                return interpreter == null ? null : interpreter.ExecutablePath;
            }
            catch { return null; }
        }
    }
}
