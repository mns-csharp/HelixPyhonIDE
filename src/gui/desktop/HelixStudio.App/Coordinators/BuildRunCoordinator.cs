namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Workspace.Execution;
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// Owns build/run/compile orchestration: running the startup app, packaging the
    /// solution, virtual-compile, build-via-C, interpreter selection and build-tool
    /// settings, plus the Python subprocess helper. References the host via <c>host</c>.
    /// </summary>
    public sealed class BuildRunCoordinator : ICoordinator
    {
        private MainFormContext _context;
        private MainForm host;

        // ICoordinator lifecycle: cache the shared context + direct host reference.
        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        // Drop references on teardown so the coordinator doesn't pin the form.
        public void Detach()
        {
            _context = null;
            host = null;
        }

        // Bring the Build Results/Output tab into view: select its docked tab, re-inserting it
        // first if the user had closed it (a bare `_mainTabControl.SelectedTab = _buildResultsTab`
        // silently does nothing once the page is no longer in TabPages — host.ShowTab handles
        // that), or if it's floating, bring that window forward instead of re-docking it.
        internal void ShowBuildResultsTab()
        {
            if (host._buildResultsUndocked)
            {
                if (host._floatingBuildResults != null) host._floatingBuildResults.BringToFront();
            }
            else
            {
                host.ShowTab("buildResults");
            }
        }

        // Locate the default Python interpreter (PATH / registry discovery). Returns null when
        // none is found; callers surface a "install Python" message in that case.
        public string FindPythonInterpreter()
        {
            var mgr = new HelixPythonNamespace.Debugger.PythonInterpreterManager();
            var interp = mgr.DiscoverDefault();
            return interp?.ExecutablePath;
        }

        // Shared helper that runs a Python subprocess (run / build / package) against a
        // pyproject dir, streaming output to the Output pane and offering to pip-install a
        // missing module and retry. Returns true only on a clean (exit 0) completion.
        /// <param name="workingDirectory">
        /// Where to run. Defaults to the directory of <paramref name="pyprojectPath"/>, which is
        /// right for manifest-scoped operations (a solution-wide <c>-m build</c>); a program launch
        /// passes the startup PROJECT's folder instead, matching Visual Studio.
        /// </param>
        public async Task<bool> RunPythonCommandAsync(
            string pyprojectPath, string[] pyArgs, string verbForStatus,
            IReadOnlyDictionary<string, string> extraEnv = null, string workingDirectory = null)
        {
            // No interpreter → nothing to run; tell the user and abort.
            string python = FindPythonInterpreter();
            if (string.IsNullOrEmpty(python))
            {
                MessageBox.Show(host,
                    "No Python interpreter found on PATH. Install Python and retry.",
                    "Python " + verbForStatus, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            // Run in the project directory; reset the Output pane for this run.
            string projectDir = string.IsNullOrEmpty(workingDirectory)
                ? Path.GetDirectoryName(pyprojectPath)
                : workingDirectory;
            if (host._statusLabel != null) host._statusLabel.Text = "Running " + verbForStatus + " in " + projectDir + "...";
            if (host._buildOutputTextBox != null) host._buildOutputTextBox.Clear();

            // Make the run/build cancellable: register with the shared Cancel button so the
            // user can stop a long process (a running app, pip install, PyInstaller). The
            // token tree-kills the active child process in RunProcessAsync. Falls back to an
            // un-cancellable run only if another job already owns the Cancel button.
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                bool external = host._jobRunner.BeginExternal("Python " + verbForStatus,
                    delegate { try { cts.Cancel(); } catch { } });
                try
                {
                    // First attempt. Check cancellation before interpreting the exit code so a
                    // user-cancelled run isn't reported as a failure.
                    var firstResult = await host.RunProcessAsync(python, pyArgs, projectDir, extraEnv, cts.Token);
                    if (cts.IsCancellationRequested)
                    {
                        if (host._statusLabel != null) host._statusLabel.Text = "Python " + verbForStatus + " cancelled.";
                        return false;
                    }
                    // Terminal-style completion marker so a finished program reads clearly as done,
                    // not still-running — and the exit code is visible next to its output.
                    host.AppendBuildOutputLine("==> exited with code " + firstResult.ExitCode);
                    if (firstResult.ExitCode == 0)
                    {
                        if (host._statusLabel != null) host._statusLabel.Text = "Python " + verbForStatus + " succeeded.";
                        return true;
                    }

                    // Non-zero exit: if the failure was a ModuleNotFoundError, offer to pip-install
                    // the missing module into this interpreter and re-run the original command.
                    string missing = MainForm.ExtractMissingModule(firstResult.CapturedOutput);

                    // A module the SOLUTION defines is never a pip problem, and offering to install
                    // one is worse than saying nothing: PyPI has packages under short, ordinary
                    // names, so "pip install inference" happily downloads a stranger's library that
                    // shadows the user's own module and leaves the import just as broken. Look
                    // inside the workspace before assuming the outside world.
                    // Judge the WHOLE statement, not the one word Python could report. See
                    // MainForm.ExtractFailingImport.
                    string failingImport =
                        MainForm.ExtractFailingImport(firstResult.CapturedOutput, missing) ?? missing;
                    WorkspaceModuleMatch local = MatchModuleInWorkspace(failingImport);
                    if (local != null)
                    {
                        string headline, advice;
                        if (local.IsExact)
                        {
                            // The whole dotted name exists here, so the code is right and the path
                            // is not: the interpreter cannot see the package that owns it.
                            headline = "'" + failingImport + "' is part of this solution: " + local.FilePath;
                            advice = "It is not on the interpreter's import path for this project. Add a "
                                   + "reference to the package that owns it.";
                        }
                        else
                        {
                            // Only the FIRST segment matched. Saying "it's in your solution" here
                            // would be a confident wrong answer: `inference.export_onnx` cannot
                            // resolve to a MODULE called inference no matter what the path is, so
                            // the import itself is what does not exist.
                            headline = "this solution has no module '" + failingImport + "'";
                            advice = "The closest thing it defines is " + local.ImportPath
                                   + " (" + local.FilePath + "), which is a module, not a package — "
                                   + "so '" + failingImport + "' cannot resolve to it.";
                            if (local.Suggestion != null)
                                advice += " Did you mean " + local.Suggestion + "?";
                            else
                                advice += " Check the import.";
                        }

                        host.AppendBuildOutputLine("");
                        host.AppendBuildOutputLine("--- " + headline + " ---");
                        host.AppendBuildOutputLine("    " + advice);
                        MessageBox.Show(host,
                            char.ToUpper(headline[0]) + headline.Substring(1) + "."
                          + "\n\n" + advice
                          + "\n\nThis is not a package to install, so pip was not offered.",
                            "Unresolved local import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        if (host._statusLabel != null)
                            host._statusLabel.Text = "Python " + verbForStatus + " failed — '" + failingImport + "' did not resolve.";
                        return false;
                    }

                    if (!string.IsNullOrEmpty(missing))
                    {
                        var answer = MessageBox.Show(host,
                            "The Python module '" + missing + "' is not installed in " + python +
                            ".\n\nInstall it now with `pip install " + missing + "` and retry?",
                            "Missing Python module", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (answer == DialogResult.Yes)
                        {
                            // Run `python -m pip install <missing>`, then retry the original command.
                            host.AppendBuildOutputLine("");
                            host.AppendBuildOutputLine("--- Installing missing module: " + missing + " ---");
                            var installResult = await host.RunProcessAsync(python, new[] { "-m", "pip", "install", missing }, projectDir, null, cts.Token);
                            if (cts.IsCancellationRequested)
                            {
                                if (host._statusLabel != null) host._statusLabel.Text = "Python " + verbForStatus + " cancelled.";
                                return false;
                            }
                            if (installResult.ExitCode != 0)
                            {
                                if (host._statusLabel != null) host._statusLabel.Text = "pip install " + missing + " failed (exit " + installResult.ExitCode + ").";
                                return false;
                            }
                            host.AppendBuildOutputLine("");
                            host.AppendBuildOutputLine("--- Retrying " + verbForStatus + " ---");
                            var retryResult = await host.RunProcessAsync(python, pyArgs, projectDir, extraEnv, cts.Token);
                            if (cts.IsCancellationRequested)
                            {
                                if (host._statusLabel != null) host._statusLabel.Text = "Python " + verbForStatus + " cancelled.";
                                return false;
                            }
                            host.AppendBuildOutputLine("==> exited with code " + retryResult.ExitCode);
                            bool ok2 = retryResult.ExitCode == 0;
                            if (!ok2) PublishPythonErrors(retryResult.CapturedOutput);
                            if (host._statusLabel != null)
                                host._statusLabel.Text = "Python " + verbForStatus + (ok2 ? " succeeded." : " failed (exit " + retryResult.ExitCode + ").");
                            return ok2;
                        }
                    }

                    // Turn the interpreter's traceback into located rows in the Errors list, so the
                    // user can double-click through to the offending line instead of reading the
                    // path out of flat Output text. Everything past this point already existed:
                    // the list has File/Line columns and its activate handler already opens them.
                    PublishPythonErrors(firstResult.CapturedOutput);

                    if (host._statusLabel != null) host._statusLabel.Text = "Python " + verbForStatus + " failed (exit " + firstResult.ExitCode + ").";
                    return false;
                }
                finally
                {
                    // Release the Cancel button back to the job runner if we claimed it.
                    if (external) host._jobRunner.Finish();
                }
            }
        }

        /// <summary>
        /// The interpreter argv for a launch — see
        /// <see cref="HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.BuildPythonLaunchArgs"/>,
        /// which every Run path (Output pane, terminal, headless launcher) composes argv with, so
        /// they cannot drift on HOW the program is started.
        /// </summary>
        private static List<string> BuildPythonLaunchArgs(SolutionRunPlan pre)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.BuildPythonLaunchArgs(pre);
        }

        /// <summary>
        /// Runs ONE package — the one the user right-clicked in the Solution Explorer — without it
        /// having to be the startup package first. Same launcher, same Output pane, same blockers
        /// as <see cref="OnRunStartupApp"/>; only the choice of what to launch differs.
        /// </summary>
        internal async Task RunProjectAsync(HelixPythonNamespace.Core.Model.ProjectModel project)
        {
            if (project == null) return;
            if (!host.SaveAllModifiedEditorTabsBeforeExternalOperation("Run")) return;
            if (host._currentSolution == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return;
            }

            SolutionRunPlan pre = host.ComputeRunPreflightFor(host._currentSolution, project);
            if (!pre.CanRun)
            {
                host._buildOutputTextBox.Clear();
                host._buildInnerTabControl.SelectedTab = host._buildOutputSubTab;
                host.AppendBuildOutputLine("==> Run blocked");
                host.AppendBuildOutputLine(pre.BlockReason);
                if (pre.PolicyViolations != null && pre.PolicyViolations.Count > 0)
                    host.FinishCompile(new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>(pre.PolicyViolations));
                ShowBuildResultsTab();
                host._buildSummaryLabel.Text = "Run blocked";
                return;
            }

            try
            {
                if (host._runButton != null) host._runButton.Enabled = false;
                if (host._runStartupMenuItem != null) host._runStartupMenuItem.Enabled = false;

                // Reset the Output pane and bring it forward, so the user sees the run they just
                // asked for rather than the previous one's output.
                host._buildOutputTextBox.Clear();
                host._buildErrorsListView.Items.Clear();
                host._buildSummaryLabel.Text = "Running " + pre.StartupName + "...";
                host._buildInnerTabControl.SelectedTab = host._buildOutputSubTab;
                ShowBuildResultsTab();

                var pyArgs = BuildPythonLaunchArgs(pre);

                host.AppendBuildOutputLine("==> python "
                    + HelixPythonNamespace.Core.CommandLineArguments.Join(pyArgs));
                if (!string.IsNullOrEmpty(pre.WorkingDirectory))
                    host.AppendBuildOutputLine("    in " + pre.WorkingDirectory);

                var env = host.BuildPythonPathEnv(host._currentSolution);
                bool ok = await RunPythonCommandAsync(
                    host._currentSolution.FilePath,
                    pyArgs.ToArray(),
                    verbForStatus: "run",
                    extraEnv: env,
                    workingDirectory: pre.WorkingDirectory);

                host._buildSummaryLabel.Text = ok
                    ? "Run " + pre.StartupName + " succeeded"
                    : "Run " + pre.StartupName + " failed";
                host._statusLabel.Text = host._buildSummaryLabel.Text;
            }
            finally
            {
                if (host._runButton != null) host._runButton.Enabled = true;
                if (host._runStartupMenuItem != null) host._runStartupMenuItem.Enabled = true;
            }
        }

        internal async void OnRunStartupApp(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(host._currentCMakeFolder))
            {
                host.RunActiveCMakeProject();
                return;
            }

            // A loose file is the whole program, so it is also the whole run target — there is no
            // startup package to resolve and no solution preflight to satisfy. The single-file
            // path applies its own rules (PY8002/PY8003/PY8005) before it launches anything.
            if (host.IsSingleFileMode)
            {
                await host.RunSingleFileFromMenuAsync();
                return;
            }

            if (!host.SaveAllModifiedEditorTabsBeforeExternalOperation("Run")) return;

            if (host._currentSolution == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return;
            }

            // A startup package must be designated when the solution has more than one executable
            // package (PSM-08). The pre-flight is a pure decision so it can also be surfaced
            // non-modally in the Build Output pane, not only in the dialog.
            SolutionRunPlan pre = host.ComputeRunPreflight(host._currentSolution);
            if (!pre.CanRun)
            {
                host._buildOutputTextBox.Clear();
                host._buildInnerTabControl.SelectedTab = host._buildOutputSubTab;
                host.AppendBuildOutputLine("==> Run blocked");
                host.AppendBuildOutputLine(pre.BlockReason);
                // A dialog says "blocked"; the Problems panel says WHICH files and lines. Without
                // this the user is told a rule was broken but not where.
                if (pre.PolicyViolations != null && pre.PolicyViolations.Count > 0)
                    host.FinishCompile(new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>(pre.PolicyViolations));
                ShowBuildResultsTab();
                host._buildSummaryLabel.Text = "Run blocked — no startup package";
                MessageBox.Show(host, pre.BlockReason, "Run",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string startup = pre.StartupName;

            try
            {
                // Disable the Run affordances while a run is in flight (re-enabled in finally).
                if (host._runButton != null) host._runButton.Enabled = false;
                if (host._runStartupMenuItem != null) host._runStartupMenuItem.Enabled = false;

                // Reset the Output pane and focus its Output sub-tab for the new run.
                host._buildOutputTextBox.Clear();
                host._buildErrorsListView.Items.Clear();
                host._buildSummaryLabel.Text = "Running " + startup + "...";
                host._buildInnerTabControl.SelectedTab = host._buildOutputSubTab;

                // Bring the Build Results into view: select its docked tab, or if it's floating,
                // bring that window forward.
                ShowBuildResultsTab();

                var pyArgs = BuildPythonLaunchArgs(pre);

                // Beside the manifest, before the program starts: pyproject.toml is what the
                // project asked for, and this is what actually answered. Off the UI thread and
                // unable to fail the run - see CaptureEnvironmentAsync.
                CaptureEnvironmentAsync(host);

                host.AppendBuildOutputLine("==> python "
                    + HelixPythonNamespace.Core.CommandLineArguments.Join(pyArgs));
                if (!string.IsNullOrEmpty(pre.WorkingDirectory))
                    host.AppendBuildOutputLine("    in " + pre.WorkingDirectory);

                // PSM-18/19: launch the entry file natively while making source roots importable.
                var env = host.BuildPythonPathEnv(host._currentSolution);
                bool ok = await RunPythonCommandAsync(
                    host._currentSolution.FilePath,
                    pyArgs.ToArray(),
                    verbForStatus: "run",
                    extraEnv: env,
                    // VS parity: the program runs in its OWN project folder, not the solution root,
                    // so its relative paths mean what they do when run from that folder.
                    workingDirectory: pre.WorkingDirectory);

                host._buildSummaryLabel.Text = ok
                    ? "Run " + startup + " succeeded"
                    : "Run " + startup + " failed";
                host._statusLabel.Text = host._buildSummaryLabel.Text;
            }
            finally
            {
                // Always restore the Run affordances, even if the run threw.
                if (host._runButton != null) host._runButton.Enabled = true;
                if (host._runStartupMenuItem != null) host._runStartupMenuItem.Enabled = true;
            }
        }

        // Connects the Build command to the standalone HelixPython Terminal popup.
        internal void PopulateRunInTerminalMenus()
        {
            if (host._runInTerminalMenuItem != null)
                host._runInTerminalMenuItem.Click += OnRunInTerminalClick;
        }

        private void OnRunInTerminalClick(object sender, EventArgs e) => OnRunInTerminal();

        internal void OnOpenTerminal()
        {
            string directory = !string.IsNullOrEmpty(host._currentCMakeFolder)
                ? host._currentCMakeFolder
                : host._currentSolution != null
                    ? host._currentSolution.SolutionDirectory
                    : Environment.CurrentDirectory;
            StartTerminalWindow(directory, null);
        }

        // Runs the startup app in the standalone HelixPython Terminal popup (a real,
        // interactive shell instead of the captured, read-only Output pane) so the user
        // can watch it live and interact with it (stdin prompts, Ctrl+C, etc). Reuses the exact
        // same interpreter/entry-file/PYTHONPATH resolution as OnRunStartupApp.
        //
        // Routes the same way OnRunStartupApp does — CMake folder, then a non-Python
        // workspace project, then the Python solution preflight — instead of jumping straight to
        // the Python-only preflight the way this method used to, which produced the wrong
        // "needs a main.py" message for every non-Python project.
        internal void OnRunInTerminal()
        {
            if (!host.SaveAllModifiedEditorTabsBeforeExternalOperation("Run in Terminal")) return;

            if (!string.IsNullOrEmpty(host._currentCMakeFolder))
            {
                RunCMakeInTerminal();
                return;
            }

            if (host._currentSolution == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return;
            }
            SolutionRunPlan pre = host.ComputeRunPreflight(host._currentSolution);
            if (!pre.CanRun)
            {
                MessageBox.Show(host, pre.BlockReason, "Run in Terminal",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string python = FindPythonInterpreter();
            if (string.IsNullOrEmpty(python))
            {
                MessageBox.Show(host,
                    "No Python interpreter found on PATH. Install Python and retry.",
                    "Run in Terminal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // VS parity: the startup project's own folder, falling back to the solution root only
            // when the preflight could not attribute the entry file to a project.
            string projectDir = !string.IsNullOrEmpty(pre.WorkingDirectory)
                ? pre.WorkingDirectory
                : Path.GetDirectoryName(host._currentSolution.FilePath);
            var env = host.BuildPythonPathEnv(host._currentSolution);
            string pythonPath;
            env.TryGetValue("PYTHONPATH", out pythonPath);

            string cmd = ComposeTerminalRunCommand(
                python, pre.RunModule, pre.EntryFile, pythonPath, pre.RunArguments);
            if (StartTerminalWindow(projectDir, cmd))
                host._statusLabel.Text = "Running " + pre.StartupName + " in HelixPython Terminal";
        }

        // Builds one command line that sets PYTHONPATH (if any) and launches the interpreter on
        // the startup target, in the syntax the target shell expects. `runArgs` is the package's own
        // argv from [tool.helixstudio].run-args, passed through verbatim: the user typed it as a
        // command line, and this IS a command line, so re-quoting it would double-escape their
        // own quotes. Re-quoting is only correct on the Run path, which goes through argv.
        //
        // `module` wins over `entryFile` when set, for the reason BuildPythonLaunchArgs gives: an
        // entry module inside a package must be started as `-m pkg.mod` or its own relative imports
        // cannot resolve. A module name is a bare identifier chain — never quoted, unlike a path.
        private static string ComposeTerminalRunCommand(string python, string module,
            string entryFile, string pythonPath, string runArgs = null)
        {
            string quotedPython = QuoteTerminalWord(python);
            string target = string.IsNullOrEmpty(module) ? QuoteTerminalWord(entryFile) : "-m " + module;
            string tail = string.IsNullOrWhiteSpace(runArgs) ? string.Empty : " " + runArgs.Trim();
            string launch = quotedPython + " -u " + target + tail;
            return string.IsNullOrEmpty(pythonPath)
                ? launch
                : "PYTHONPATH=" + QuoteTerminalWord(pythonPath) + " " + launch;
        }

        private static string QuoteTerminalWord(string value) =>
            "'" + (value ?? string.Empty).Replace("'", "'\\''") + "'";

        // Runs the active native CMake project's built executable in the popup shell.
        // Emulator-backed projects have no standalone binary to hand to a shell — that case
        // explains the limitation instead of showing the Python "no main.py" dialog (the
        // originally-reported bug).
        private void RunCMakeInTerminal()
        {
            if (string.IsNullOrEmpty(host._currentCMakeFolder) || !Directory.Exists(host._currentCMakeFolder))
            {
                host._statusLabel.Text = "No native CMake project is open to run.";
                return;
            }
            if (host.IsActiveCMakeProjectUsingEmulator())
            {
                MessageBox.Show(host,
                    "Emulator projects run inside the IDE — the terminal requires a real cmake/compiler " +
                    "toolchain. Select MSVC or GCC in Build > Tool Settings, or use Run (Ctrl+F5) instead.",
                    "Run in Terminal", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var build = host.BuildActiveCMakeProject() as HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult;
            if (build == null || !build.Succeeded)
            {
                MessageBox.Show(host, "Build failed; see the Output tab for details.", "Run in Terminal",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string targetName = null;
            if (host._currentCMakeProject != null)
                foreach (HelixPythonNamespace.Workspace.Cuda.CMakeResolvedTarget target in host._currentCMakeProject.Targets)
                    if (target.Kind == HelixPythonNamespace.Workspace.Cuda.CMakeTargetKind.Executable)
                    {
                        targetName = target.Name;
                        break;
                    }

            string buildDir = host.CMakeBuildDirectory(host._currentCMakeFolder);
            string executable = MainForm.FindCMakeExecutable(buildDir, targetName);
            if (executable == null)
            {
                MessageBox.Show(host,
                    string.IsNullOrEmpty(targetName)
                        ? "No executable target is defined by this CMake project."
                        : "Built executable for target '" + targetName + "' was not found.",
                    "Run in Terminal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (StartTerminalWindow(Path.GetDirectoryName(executable), QuoteTerminalWord(executable)))
                host._statusLabel.Text = "Running " + Path.GetFileName(executable) + " in HelixPython Terminal";
        }

        private bool StartTerminalWindow(string workingDirectory, string command)
        {
            string executable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HelixPython.Terminal.exe");
            if (!File.Exists(executable))
            {
                MessageBox.Show(host,
                    "HelixPython Terminal is missing beside the IDE. Rebuild or reinstall HelixPython.",
                    "Terminal", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                workingDirectory = Environment.CurrentDirectory;

            var arguments = new List<string> { "--working-directory", workingDirectory };
            if (!string.IsNullOrWhiteSpace(command))
            {
                arguments.Add("--command");
                arguments.Add(command);
            }

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = HelixPythonNamespace.Core.CommandLineArguments.Join(arguments),
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                };
                Process.Start(start)?.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(host, "Could not open HelixPython Terminal:\n\n" + ex.Message,
                    "Terminal", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // Toolbar/menu "Build". Two very different back ends depending on the loaded project:
        // a Python pyproject builds via `python -m build`; anything else is packaged with the
        // .NET SDK (`dotnet build`).
        internal async void OnBuildSolution(object sender, EventArgs e)
        {
            if (host._currentSolution == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return;
            }

            if (!string.IsNullOrEmpty(host._currentCMakeFolder))
            {
                host.BuildActiveCMakeProject();
                return;
            }

            if (!host.SaveAllModifiedEditorTabsBeforeExternalOperation("Build")) return;

            // Python project → delegate to the shared Python runner and stop.
            if (MainForm.IsPythonProjectFile(host._currentSolution.FilePath))
            {
                await RunPythonCommandAsync(host._currentSolution.FilePath, new[] { "-m", "build" }, "build");
                return;
            }

            // Otherwise we need the .NET SDK on the host to package.
            if (!host._buildToolDetector.HasDotNet())
            {
                MessageBox.Show(
                    "No .NET SDK found on host system.\nInstall the .NET SDK from https://dotnet.microsoft.com",
                    "Packager Unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            // Single-job policy: don't stack a package on top of another running job (it would
            // also fight over the shared Cancel button / status label).
            if (host._jobRunner.IsBusy)
            {
                host._statusLabel.Text = "A job is already running";
                return;
            }

            var results = new List<BuildResultItem>();
            bool buildSucceeded = false;
            bool cancelled = false;
            try
            {
                // Disable Build affordances for the duration; re-enabled in the finally below.
                host._buildButton.Enabled = false;
                host._buildMenuItem.Enabled = false;
                host._statusLabel.Text = "Packaging solution...";

                // Clear embedded build output
                host._buildOutputTextBox.Clear();
                host._buildErrorsListView.Items.Clear();
                host._buildSummaryLabel.Text = "Packaging...";
                host._buildInnerTabControl.SelectedTab = host._buildOutputSubTab;

                // Navigate to Build Results tab
                ShowBuildResultsTab();

                // Run `dotnet build` off the UI thread via the shared process helper. Unlike the
                // previous hand-rolled Task.Run+Process (which had no Cancel wiring and a fixed 2-min
                // wait), RunProcessAsync registers the child with the shared Cancel button through
                // BeginExternal and TREE-KILLS dotnet/MSBuild on cancellation — net472's Process.Kill
                // only kills the immediate child, so a plain kill left MSBuild/VBCSCompiler running
                // and made Cancel appear to do nothing. This is what makes a package actually stoppable.
                using (var cts = new System.Threading.CancellationTokenSource())
                {
                    bool external = host._jobRunner.BeginExternal("Packaging solution",
                        delegate { try { cts.Cancel(); } catch { } });
                    try
                    {
                        var buildResult = await host.RunProcessAsync(
                            "dotnet",
                            // Raw, unquoted: RunProcessAsync now quotes every argument itself.
                            // The manual quotes that used to be here would be escaped as literals.
                            new[] { "build", host._currentSolution.FilePath },
                            Path.GetDirectoryName(host._currentSolution.FilePath),
                            null, cts.Token);

                        cancelled = cts.IsCancellationRequested;

                        // Parse the buffered MSBuild output into diagnostics rows. RunProcessAsync
                        // interleaves stdout+stderr into CapturedOutput, so one pass covers both.
                        if (!cancelled && buildResult.CapturedOutput != null)
                        {
                            foreach (var line in buildResult.CapturedOutput.Split(
                                         new[] { "\r\n", "\n" }, StringSplitOptions.None))
                            {
                                HelixPythonNamespace.App.Coordinators.BuildAnalysisCoordinator.ParseBuildLine(line, results);
                            }
                        }
                        buildSucceeded = !cancelled && buildResult.ExitCode == 0;
                    }
                    finally
                    {
                        // Release the shared Cancel button (whether we finished, failed, or cancelled).
                        if (external) host._jobRunner.Finish();
                    }
                }

                if (cancelled)
                {
                    host._statusLabel.Text = "Packaging cancelled";
                    return;
                }

                // Populate errors/warnings in embedded tab
                host.PopulateEmbeddedBuildResults(results, buildSucceeded);

                int errorCount = 0;
                int warningCount = 0;
                foreach (var r in results)
                {
                    if (string.Equals(r.Severity, "error", StringComparison.OrdinalIgnoreCase))
                        errorCount++;
                    else if (string.Equals(r.Severity, "warning", StringComparison.OrdinalIgnoreCase))
                        warningCount++;
                }

                host._statusLabel.Text = buildSucceeded
                    ? string.Format("Packager succeeded - {0} warning(s)", warningCount)
                    : string.Format("Packager failed - {0} error(s), {1} warning(s)", errorCount, warningCount);

                // A successful package may have regenerated files; reload the solution to pick
                // them up. Suspend the file watcher during reload so we don't re-trigger on our
                // own writes.
                if (buildSucceeded)
                {
                    using (host._fileWatcher?.SuspendWhileEditing())
                    {
                        host.ReloadSolutionAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                // On any failure, report the error (Build affordances restored in the finally).
                MessageBox.Show(
                    string.Format("Packager failed: {0}", ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                host._buildButton.Enabled = true;
                host._buildMenuItem.Enabled = true;
            }
        }

        // Build-tool settings dialog: lets the user pick the default SDK version. On OK, persist
        // the choice and refresh the Build button enablement (the detector may have re-scanned).
        internal void OnBuildToolSettings(object sender, EventArgs e)
        {
            var currentDefault = host._settingsManager?.Settings?.DefaultSdkVersion;
            string currentNative = host._settingsManager?.Settings?.NativeCMakeToolchain ?? "Auto";
            string currentGcc = host._settingsManager?.Settings?.NativeGccCppCompilerPath;
            string currentMsvc = host._settingsManager?.Settings?.NativeMsvcEnvironmentScriptPath;
            string currentCxx = host._settingsManager?.Settings?.NativeCxxStandard ?? "20";
            string currentCuda = host._settingsManager?.Settings?.NativeCudaStandard ?? "20";
            using (var dialog = new BuildToolSettingsDialog(host._buildToolDetector, currentDefault,
                currentNative, currentGcc, currentMsvc, currentCxx, currentCuda))
            {
                if (dialog.ShowDialog(host) == DialogResult.OK)
                {
                    if (host._settingsManager != null)
                    {
                        host._settingsManager.Settings.DefaultSdkVersion = dialog.SelectedSdkVersion;
                        host._settingsManager.Settings.NativeCMakeToolchain = dialog.SelectedNativeToolchain;
                        host._settingsManager.Settings.NativeGccCppCompilerPath = dialog.SelectedGccCompilerPath;
                        host._settingsManager.Settings.NativeMsvcEnvironmentScriptPath =
                            dialog.SelectedMsvcEnvironmentScriptPath;
                        host._settingsManager.Settings.NativeCxxStandard = dialog.SelectedCxxStandard;
                        host._settingsManager.Settings.NativeCudaStandard = dialog.SelectedCudaStandard;
                        host._settingsManager.Save();
                    }

                    // Update build button state after potential refresh
                    bool hasDotNet = host._buildToolDetector.HasDotNet();
                    host._buildButton.Enabled = hasDotNet;
                    host._buildMenuItem.Enabled = hasDotNet;
                }
            }
        }

        /// <summary>
        /// Write environment.lock.toml beside the solution's manifest, on a background thread.
        ///
        /// Fire-and-forget on purpose, and swallowing on purpose. Probing the interpreter and
        /// scanning the machine's toolchains takes seconds; doing that on the UI thread would freeze
        /// the IDE at the moment the user pressed Run, and failing a run because a note about the
        /// machine could not be written would be the wrong trade twice over.
        ///
        /// The interpreter passed is the one the IDE is CONFIGURED with. When none is selected the
        /// record says "no interpreter was resolved" rather than guessing at one - a capture naming
        /// a Python that did not run the program would be provenance in name only.
        /// </summary>
        private static void CaptureEnvironmentAsync(MainForm host)
        {
            SolutionModel solution = host._currentSolution;
            if (solution == null) return;

            string root = HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner
                .SolutionRootDirectory(solution);
            if (string.IsNullOrEmpty(root)) return;

            string interpreter = host._settingsManager != null && host._settingsManager.Settings != null
                ? host._settingsManager.Settings.SelectedPythonExecutable
                : null;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var scanner = new HelixPythonNamespace.Workspace.Toolchains.DevelopmentEnvironmentScanner(
                        new HelixPythonNamespace.Workspace.Toolchains.ProcessToolProbeRunner());
                    var toolchains = scanner.Scan(System.Threading.CancellationToken.None);

                    var record = HelixPythonNamespace.Workspace.Toolchains.EnvironmentCapture.Capture(
                        interpreter, toolchains,
                        new HelixPythonNamespace.Workspace.Toolchains.ProcessInterpreterProbe(),
                        System.Threading.CancellationToken.None);

                    HelixPythonNamespace.Workspace.Toolchains.EnvironmentCapture.WriteBeside(root, record);
                }
                catch (Exception)
                {
                    // Never surfaced and never fatal. The run is the point.
                }
            });
        }

        // Python interpreter picker. Persists whichever interpreter is selected — see the inline
        // note on why we check SelectedInterpreter rather than only DialogResult.OK.
        internal void OnSelectPythonInterpreter(object sender, EventArgs e)
        {
            string current = host._settingsManager?.Settings?.SelectedPythonExecutable;
            using (var dlg = new HelixPythonNamespace.App.Dialogs.SelectPythonInterpreterDialog(current))
            {
                var result = dlg.ShowDialog(host);
                // Apply may have been clicked standalone (sets SelectedInterpreter without
                // closing); Save && Close returns OK. Either way, persist whatever's set.
                if (dlg.SelectedInterpreter != null)
                {
                    string path = dlg.SelectedInterpreter.ExecutablePath;
                    if (!string.IsNullOrEmpty(path) && host._settingsManager != null)
                    {
                        host._settingsManager.Settings.SelectedPythonExecutable = path;
                        host._settingsManager.Save();
                        host._statusLabel.Text = "Python interpreter: " + path +
                            (string.IsNullOrEmpty(dlg.SelectedInterpreter.Version) ? "" : " (" + dlg.SelectedInterpreter.Version + ")");
                    }
                }
            }
        }

        // "Build via C++": Pass 1 (strict type check + typed-Python rewrite, materialized to
        // disk) -> Pass 2 (C++ emission) -> MSVC compile. async void event handler.
        internal async void OnBuildViaCpp(object sender, EventArgs e)
        {
            if (host._currentSolution == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return;
            }
            // Emit next to the solution when we have a path, else fall back to a temp dir.
            string solutionPath = host._currentSolution.FilePath;
            string outputRoot;
            if (!string.IsNullOrEmpty(solutionPath))
                outputRoot = Path.Combine(Path.GetDirectoryName(solutionPath), "out");
            else
                outputRoot = Path.Combine(Path.GetTempPath(), "HelixStudio_BuildViaCpp");

            host._statusLabel.Text = "Build via C++: translating + compiling…";
            if (host._buildOutputTextBox != null) host._buildOutputTextBox.Clear();
            ShowBuildResultsTab();

            var orchestrator = new HelixPythonNamespace.Workspace.PyToCpp.BuildViaCppOrchestrator();

            // Run the translate + MSVC invocation off the UI thread.
            HelixPythonNamespace.Workspace.PyToCpp.BuildViaCppResult r = await System.Threading.Tasks.Task.Run(
                () => orchestrator.Build(host._currentSolution, outputRoot));

            // Dump the orchestrator log to the Output pane and append a success/failure banner.
            if (host._buildOutputTextBox != null)
            {
                host._buildOutputTextBox.AppendText(r.Log ?? string.Empty);
                if (r.Success) host._buildOutputTextBox.AppendText("\r\n[build-via-cpp] SUCCESS → " + r.OutputExePath + "\r\n");
                else host._buildOutputTextBox.AppendText("\r\n[build-via-cpp] FAILED\r\n");
            }

            // Surface Pass 1 diagnostics (PYCPP1xxx unsupported-construct etc.) in the Problems tab.
            if (host._problemsPanel != null && r.Diagnostics != null && r.Diagnostics.Count > 0)
            {
                // Adapt the checker's diagnostic shape to the VirtualCompiler.CompileDiagnostic
                // the Problems panel understands. Diagnostics with a line get a 1-char source span
                // (columns clamped to >= 1); line-less ones get an empty span.
                var diags = new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>();
                foreach (var d in r.Diagnostics)
                {
                    var span = d.Line > 0
                        ? new HelixPythonNamespace.Python.Text.SourceSpan(
                            new HelixPythonNamespace.Python.Text.SourcePosition(0, d.Line, Math.Max(1, d.Column)),
                            new HelixPythonNamespace.Python.Text.SourcePosition(0, d.Line, Math.Max(1, d.Column) + 1))
                        : new HelixPythonNamespace.Python.Text.SourceSpan();
                    diags.Add(new HelixPythonNamespace.VirtualCompiler.CompileDiagnostic(
                        d.FilePath ?? string.Empty, d.Code,
                        HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error,
                        span, d.Message));
                }
                host._problemsPanel.ShowDiagnostics(diags);
            }

            host._statusLabel.Text = r.Success
                ? "Build via C++: ok → " + (r.OutputExePath ?? "")
                : "Build via C++: failed (see Output tab)";
        }

        /// <summary>
        /// Renders a failed run's traceback into the Build Errors list as double-clickable rows.
        ///
        /// Layout is chosen for what the user does next: the INNERMOST frame comes first, carrying
        /// the exception headline, because that is the line the error actually happened on and the
        /// one they want to open. The remaining frames follow innermost → outermost as the call
        /// chain that led there — the reading order of a stack, not the printing order of a
        /// traceback (CPython prints outermost first, ending with the culprit).
        ///
        /// Frames outside the solution (stdlib, site-packages) are kept — they are often the only
        /// clue for a library misuse — but marked <c>Source = "python"</c> and demoted to "info",
        /// so they never look like something the user broke and never inflate the error count.
        /// </summary>
        internal void PublishPythonErrors(string capturedOutput)
        {
            var report = HelixPythonNamespace.Core.Diagnostics.PythonTracebackParser.Parse(capturedOutput);
            if (report == null || !report.HasLocation) return;

            var rows = new List<BuildResultItem>();
            var ordered = new List<HelixPythonNamespace.Core.Diagnostics.PythonErrorLocation>(report.Frames);
            ordered.Reverse();   // innermost first

            for (int i = 0; i < ordered.Count; i++)
            {
                var f = ordered[i];
                bool primary = i == 0;
                bool inSolution = IsInsideSolution(f.File);
                rows.Add(new BuildResultItem
                {
                    // Only the primary row is an error: N stack frames are ONE failure, and
                    // counting each as an error would report "6 errors" for a single exception.
                    Severity = primary ? "error" : "info",
                    Source = inSolution ? "Python" : "python",
                    Code = primary
                        ? (report.ExceptionType ?? (report.IsSyntaxError ? "SyntaxError" : "Error"))
                        : "frame",
                    Message = primary
                        ? report.Headline
                        : "at " + (string.IsNullOrEmpty(f.Function) ? "<frame>" : f.Function)
                          + (string.IsNullOrEmpty(f.SourceLine) ? "" : "  —  " + f.SourceLine),
                    File = f.File,
                    Line = f.Line,
                    Column = f.Column,
                });
            }

            host.PopulateEmbeddedBuildResults(rows, false);
        }

        // Whether a traceback frame points at the user's own code rather than the interpreter's.
        private bool IsInsideSolution(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || host._currentSolution == null
                || string.IsNullOrEmpty(host._currentSolution.FilePath)) return false;
            try
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(host._currentSolution.FilePath));
                if (string.IsNullOrEmpty(root)) return false;
                string full = Path.GetFullPath(filePath);
                return full.StartsWith(
                    root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // "Compile" runs the in-process VirtualCompiler (static analysis / type checking) over
        // every Python file in the solution and shows the diagnostics in the Problems panel.
        // Requires an idle job runner and a loaded solution with at least one .py file.
        internal async void OnCompile(object sender, EventArgs e)
        {
            if (host._jobRunner.IsBusy) return;

            // In single-file mode the loose file IS the compilation unit. Same check Run and Debug
            // gate on, run on demand — so Compile answers "would this launch?" instead of "no
            // solution loaded", which is true and useless to someone who never asked for one.
            if (host.IsSingleFileMode)
            {
                host.CheckSingleFileFromMenu();
                return;
            }

            if (host._currentSolution == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return;
            }

            // Gather the compilable files up front; nothing to do if there are none.
            List<HelixPythonNamespace.VirtualCompiler.WorkspaceFile> workspaceFiles =
                EnumerateSolutionWorkspaceFiles(host._currentSolution);
            if (workspaceFiles.Count == 0)
            {
                host._problemsPanel.Clear();
                host._statusLabel.Text = "Compile: no Python files found in solution";
                return;
            }

            // Resolve imports against the SAME sys.path the launcher will hand python.exe, so a
            // packaging fault shows up in Problems instead of as an ImportError on the first Run.
            var compileOptions = new HelixPythonNamespace.VirtualCompiler.CompileOptions
            {
                SearchPath = host.BuildPythonSearchPath(host._currentSolution, null),
                // Same policy Run enforces, so Build > Compile surfaces the blockers up front
                // instead of the user discovering them only when a launch is refused.
                Policy = host.BuildSolutionPolicy(host._currentSolution),
            };

            // Compile on a background job; _compileBusy gates re-entrancy from other UI paths.
            host._compileBusy = true;
            host._jobRunner.Run(
                string.Format("Compiling {0} file(s)", workspaceFiles.Count),
                // Background work: compile all files and flatten their diagnostics into one list.
                // Cancellation propagates as-is; any other crash is turned into a single PY9999
                // diagnostic so the UI still gets a result instead of a silent failure.
                token =>
                {
                    List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> all =
                        new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>();
                    try
                    {
                        IReadOnlyList<HelixPythonNamespace.VirtualCompiler.CompileFileResult> perFile =
                            HelixPythonNamespace.VirtualCompiler.VirtualCompiler.CompileSolution(workspaceFiles, compileOptions, token);
                        foreach (HelixPythonNamespace.VirtualCompiler.CompileFileResult f in perFile)
                        {
                            if (f?.Result?.Diagnostics == null) continue;
                            all.AddRange(f.Result.Diagnostics);
                        }
                        // EnumerateSolutionWorkspaceFiles only walks MEMBER projects, so a
                        // non-member file could never report itself. Sweep the solution tree for
                        // the policy rules, which are precisely about files members do not cover.
                        all.AddRange(host.FindPolicyViolations(host._currentSolution));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception inner)
                    {
                        all.Add(new HelixPythonNamespace.VirtualCompiler.CompileDiagnostic(
                            "", "PY9999",
                            HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error,
                            default(HelixPythonNamespace.Python.Text.SourceSpan),
                            "Compiler crashed: " + inner.Message));
                    }
                    return all;
                },
                // Success: hand diagnostics to MainForm (which renders + clears _compileBusy).
                all => host.FinishCompile(all),
                // Failure: clear the busy gate (and let the default status message show).
                ex => { host._compileBusy = false; },
                // Cancel: clear the busy gate too, so a cancelled compile doesn't leave it stuck.
                onCancelled: () => { host._compileBusy = false; });
        }

        // Enumerate every compilable Python file across the solution's projects, de-duplicating
        // by path and skipping virtualenv/cache/build directories, and pair each with its derived
        // module FQN. Used to feed both Compile and other whole-solution operations.
        public List<HelixPythonNamespace.VirtualCompiler.WorkspaceFile> EnumerateSolutionWorkspaceFiles(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionSourceInventory.WorkspaceFiles(sol, null);
        }

        /// <summary>What the workspace has to say about an import that failed.</summary>
        private sealed class WorkspaceModuleMatch
        {
            public string FilePath;     // the .py the name resolved to
            public string ImportPath;   // the dotted name Python would actually accept for it
            public bool IsExact;        // the WHOLE dotted name resolved, not just its first part
            public string Suggestion;   // an importable name whose LAST segment is the one wanted
        }

        /// <summary>
        /// Resolves a failed import against the open solution, or null if the name is not ours.
        ///
        /// Resolution follows every segment, not just the first. `inference.export_onnx` failing
        /// while the solution happens to contain an `inference.py` is a coincidence of names: a
        /// module cannot carry a submodule, so no import path would ever make that work. Matching
        /// on the first segment alone and announcing "it's in your solution" turns a wrong guess
        /// into a confident one, which is worse than the pip offer it replaced.
        /// </summary>
        private WorkspaceModuleMatch MatchModuleInWorkspace(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName) || host == null || host._currentSolution == null) return null;

            string[] segments = moduleName.Split('.');
            var roots = new List<string>();
            string solutionDir = SafeDirectory(host._currentSolution.FilePath);
            if (solutionDir != null) roots.Add(solutionDir);
            foreach (ProjectModel project in host._currentSolution.Projects)
            {
                string dir = SafeDirectory(project.FilePath);
                if (dir != null) roots.Add(dir);
            }

            foreach (string root in roots)
            {
                string top = SearchForModule(root, segments[0], 0);
                if (top == null) continue;

                string resolved = ResolveRemainingSegments(top, segments);
                return new WorkspaceModuleMatch
                {
                    FilePath = resolved ?? top,
                    ImportPath = ImportPathFor(resolved ?? top),
                    IsExact = resolved != null,
                    // When the dotted name is wrong, the name of the LAST segment is usually right
                    // — it is the module the author actually wanted, under a path they misremembered.
                    // Naming where it really lives turns "that import is wrong" into a line to paste.
                    Suggestion = resolved != null ? null : SuggestImportFor(segments[segments.Length - 1], roots),
                };
            }
            return null;
        }

        /// <summary>The importable path of a module with this name, wherever it is in the workspace.</summary>
        private static string SuggestImportFor(string lastSegment, List<string> roots)
        {
            if (string.IsNullOrEmpty(lastSegment)) return null;
            foreach (string root in roots)
            {
                string found = SearchForModule(root, lastSegment, 0);
                if (found != null) return ImportPathFor(found);
            }
            return null;
        }

        // Walks the rest of a dotted name down from whatever the first segment resolved to.
        // Returns null the moment a segment cannot be followed — including when the chain is asked
        // to descend into a plain .py, which is exactly the case that used to be reported as a hit.
        private static string ResolveRemainingSegments(string topPath, string[] segments)
        {
            string current = topPath;
            for (int i = 1; i < segments.Length; i++)
            {
                string dir = Path.GetFileName(current) == "__init__.py"
                    ? Path.GetDirectoryName(current)
                    : null;
                if (dir == null) return null;   // a module has no submodules

                string asPackage = Path.Combine(dir, segments[i], "__init__.py");
                string asFile = Path.Combine(dir, segments[i] + ".py");
                if (File.Exists(asPackage)) current = asPackage;
                else if (File.Exists(asFile)) current = asFile;
                else return null;
            }
            return current;
        }

        /// <summary>
        /// The dotted name Python would accept for a file — found by walking up while each parent
        /// is still a package. Telling the user the file path alone leaves them to work out the
        /// import themselves; this is the line they can paste.
        /// </summary>
        private static string ImportPathFor(string filePath)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                string dir = Path.GetDirectoryName(filePath);
                var parts = new List<string>();
                if (name != "__init__") parts.Add(name);

                while (dir != null && File.Exists(Path.Combine(dir, "__init__.py")))
                {
                    parts.Insert(0, Path.GetFileName(dir));
                    dir = Path.GetDirectoryName(dir);
                }
                return parts.Count == 0 ? name : string.Join(".", parts.ToArray());
            }
            catch (ArgumentException) { return filePath; }
            catch (PathTooLongException) { return filePath; }
        }

        private static string SafeDirectory(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            try { return Path.GetDirectoryName(Path.GetFullPath(filePath)); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        // Bounded depth: a source tree is not a filesystem crawl, and this runs while the user is
        // waiting on a failed run.
        private static string SearchForModule(string dir, string moduleName, int depth)
        {
            if (depth > 4 || string.IsNullOrEmpty(dir)) return null;
            try
            {
                string asFile = Path.Combine(dir, moduleName + ".py");
                if (File.Exists(asFile)) return asFile;

                string asPackage = Path.Combine(dir, moduleName, "__init__.py");
                if (File.Exists(asPackage)) return asPackage;

                foreach (string child in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(child);
                    // Nothing importable lives in these, and .venv in particular is where a crawl
                    // goes to die.
                    if (name.Length == 0 || name[0] == '.') continue;
                    if (name == "__pycache__" || name == "node_modules" || name == "venv") continue;

                    string found = SearchForModule(child, moduleName, depth + 1);
                    if (found != null) return found;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

    }
}
