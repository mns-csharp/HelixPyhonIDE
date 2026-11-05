namespace HelixPythonNamespace.Launcher
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Testing;
    using HelixPythonNamespace.Workspace.Execution;

    /// <summary>
    /// The two verbs besides <c>run</c> that EXECUTE the user's code — <c>--profile</c> and
    /// <c>--run-test</c> — and are therefore gated by the four solution rules exactly as a run is.
    ///
    /// Both engines here are event-driven and were built for a message pump to marshal back to. A
    /// console has no pump, so each is driven to completion against a
    /// <see cref="ManualResetEventSlim"/> and its results printed on the way out.
    /// </summary>
    internal static class ExecutionVerbs
    {
        // ---- --profile -----------------------------------------------------------------

        /// <summary>
        /// Run the package's entry point under cProfile and print the hot functions.
        ///
        /// The target is the same entry point <c>run</c> would start, resolved by the same plan, so
        /// "profile what I run" is true by construction rather than by a second resolution that
        /// could pick a different file.
        /// </summary>
        internal static int Profile(SolutionModel solution, SolutionRunPlan plan, string interpreter,
                                    LauncherOptions cli, TextWriter outw, TextWriter errw)
        {
            IReadOnlyDictionary<string, string> env = SolutionRunPlanner.BuildPythonPathEnv(solution);

            if (cli.DryRun)
            {
                outw.WriteLine("profile: " + (plan.RunModule ?? plan.EntryFile));
                outw.WriteLine("  package: " + plan.StartupName);
                outw.WriteLine("  in:      " + (plan.WorkingDirectory ?? "(current directory)"));
                return 0;
            }

            var profiler = new HelixPythonNamespace.Profiler.PythonProfiler();
            var done = new ManualResetEventSlim(false);
            HelixPythonNamespace.Profiler.ProfileFinishedEventArgs result = null;
            profiler.Finished += delegate (object s, HelixPythonNamespace.Profiler.ProfileFinishedEventArgs e)
            {
                result = e;
                done.Set();
            };

            // Ctrl+C stops the profiled process rather than orphaning it. Without this the launcher
            // exits and leaves a python.exe writing to a temp file nobody will read.
            ConsoleCancelEventHandler onCancel = delegate (object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                try { profiler.Stop(); } catch { }
            };
            try { Console.CancelKeyPress += onCancel; } catch { onCancel = null; }

            try
            {
                profiler.Start(interpreter, plan.EntryFile, plan.WorkingDirectory, env, plan.RunModule);
                done.Wait();
            }
            finally
            {
                if (onCancel != null) try { Console.CancelKeyPress -= onCancel; } catch { }
            }

            if (result == null || !string.IsNullOrEmpty(result.ErrorMessage))
            {
                errw.WriteLine("helixstudio: profiler failed: "
                    + (result == null ? "no result" : result.ErrorMessage));
                return SolutionLauncher.ExitFindings;
            }

            List<HelixPythonNamespace.Profiler.ProfileRecord> records = result.Records;
            records.Sort(delegate (HelixPythonNamespace.Profiler.ProfileRecord a, HelixPythonNamespace.Profiler.ProfileRecord b)
            {
                return b.CumulativeSeconds.CompareTo(a.CumulativeSeconds);
            });

            outw.WriteLine(string.Format("{0,10}  {1,10}  {2,10}  {3}", "calls", "tottime", "cumtime", "function"));
            foreach (HelixPythonNamespace.Profiler.ProfileRecord r in records)
                outw.WriteLine(string.Format("{0,10}  {1,10:F6}  {2,10:F6}  {3}  ({4}:{5})",
                    r.CallCount, r.TotalSeconds, r.CumulativeSeconds, r.Name, r.FilePath, r.Line));

            outw.WriteLine(string.Format("Profile ({0}): {1} function(s).", plan.StartupName, records.Count));
            return 0;
        }

        // ---- --run-test ----------------------------------------------------------------

        /// <summary>
        /// Run pytest over the solution, or over one package's directory when scoped — the same two
        /// scopes the IDE offers as Tests ▸ Run All Tests and the Solution Explorer's per-project
        /// Run Tests. pytest's own recursive discovery then naturally sees only that project's
        /// <c>test_*.py</c> files.
        /// </summary>
        internal static int RunTests(SolutionModel solution, ProjectModel scope, string interpreter,
                                     LauncherOptions cli, TextWriter outw, TextWriter errw)
        {
            string directory = scope != null
                ? (scope.ProjectDirectory ?? Path.GetDirectoryName(scope.FilePath))
                : SolutionRunPlanner.SolutionRootDirectory(solution);

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                errw.WriteLine("helixstudio: no directory to run tests in for "
                    + AnalysisVerbs.ScopeLabel(scope) + ".");
                return SolutionLauncher.ExitBlocked;
            }

            var request = new TestRunRequest
            {
                Framework = TestFramework.Pytest,
                InterpreterPath = interpreter,
                WorkingDirectory = directory,
            };
            if (cli.ProgramArgumentsGiven) request.ExtraArgs = new List<string>(cli.ProgramArguments);

            if (cli.DryRun)
            {
                outw.WriteLine("run-test (" + AnalysisVerbs.ScopeLabel(scope) + "): pytest");
                outw.WriteLine("  in: " + directory);
                if (request.ExtraArgs.Count > 0)
                    outw.WriteLine("  extra args: "
                        + HelixPythonNamespace.Core.CommandLineArguments.Join(request.ExtraArgs));
                return 0;
            }

            var runner = new TestRunner();
            var done = new ManualResetEventSlim(false);
            int exitCode = -1;
            string startFailure = null;
            bool stopped = false;

            // The runner streams pytest's own output; a console is the one host that can simply
            // print it as it arrives instead of buffering it into a panel.
            runner.OutputLine += line => outw.WriteLine(line);
            runner.StartFailed += message => { startFailure = message; done.Set(); };
            runner.Finished += exit => { exitCode = exit; done.Set(); };
            runner.Stopped += () => { stopped = true; done.Set(); };

            ConsoleCancelEventHandler onCancel = delegate (object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                try { runner.Stop(); } catch { }
            };
            try { Console.CancelKeyPress += onCancel; } catch { onCancel = null; }

            try
            {
                runner.Start(request);
                done.Wait();
            }
            finally
            {
                if (onCancel != null) try { Console.CancelKeyPress -= onCancel; } catch { }
            }

            if (startFailure != null)
            {
                errw.WriteLine("helixstudio: tests failed to start: " + startFailure);
                return SolutionLauncher.ExitFindings;
            }

            int passed = 0, failed = 0, skipped = 0;
            foreach (TestNode node in runner.Results)
            {
                switch (node.Status)
                {
                    case TestStatus.Passed: passed++; break;
                    case TestStatus.Failed:
                    case TestStatus.Error: failed++; break;
                    case TestStatus.Skipped: skipped++; break;
                }
            }

            outw.WriteLine(string.Format("Tests ({0}): {1} passed, {2} failed, {3} skipped.",
                AnalysisVerbs.ScopeLabel(scope), passed, failed, skipped));
            if (stopped)
            {
                // A cancelled run is not a passing one, whatever the partial totals say.
                errw.WriteLine("  Cancelled - these totals are partial.");
                return SolutionLauncher.ExitFindings;
            }
            // pytest's own exit code passes through: 0 all passed, 1 tests failed, 5 none collected,
            // and so on. Inventing our own would hide the distinctions a CI script reads.
            return exitCode;
        }
    }
}
