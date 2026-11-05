namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using HelixPythonNamespace.VirtualCompiler;
    using HelixPythonNamespace.VirtualCompiler.Compliance;

    /// <summary>
    /// Single-file mode: one Python file, open on its own, edited, run, debugged and saved without
    /// a solution anywhere in sight.
    ///
    /// <para>
    /// <b>It is a MODE, not a second kind of document.</b> A loose file exists only while no
    /// solution is open, and opening a solution ends it. That exclusivity is the whole reason the
    /// feature is safe to have: Run, Debug, Compile and the Problems panel each have exactly one
    /// target and one rule set at any moment, so no command has to ask which world it is acting in
    /// and no rule has to be written twice. The alternative — a scratch file open beside a solution
    /// — needs every one of those surfaces to carry a per-document answer, and needs the UI to keep
    /// telling the user which answer applies to the tab in front of them.
    /// </para>
    ///
    /// <para>
    /// <b>The rules do not relax.</b> A loose file is still the user's code, so PY8002 (object
    /// orientation) and PY8003 (test files belong in a test project) apply exactly as they do
    /// inside a solution. What changes is rule 1: PY8001 asks whether a file is a workspace member,
    /// and there is no workspace, so <see cref="SolutionPolicy.ForSingleFile"/> turns it off and
    /// turns on <see cref="SingleFileImportRule"/> (PY8005) — a loose file may import installed
    /// packages and nothing else. Wanting your own modules is exactly the point at which the answer
    /// is "make a solution", and that is what the message says.
    /// </para>
    ///
    /// <para>
    /// <b>The launch clears PYTHONPATH, and that is load-bearing.</b> PY8005 enumerates the three
    /// routes a lone script has into user-written code — a relative import, a module beside the
    /// script, a <c>sys.path</c> edit — and that enumeration is only complete if <c>sys.path</c> is
    /// the script's own directory plus the installation. An inherited PYTHONPATH (this IDE's own
    /// process may well have one, and so may the user's shell) would add a fourth route the rule
    /// cannot see, and every guarantee above would be false while still looking true.
    /// </para>
    /// </summary>
    public sealed class SingleFileCoordinator : ICoordinator
    {
        private MainFormContext _context;
        private MainForm host;

        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        public void Detach()
        {
            _context = null;
            host = null;
        }

        /// <summary>The loose file currently open, or null when not in single-file mode.</summary>
        public string ActiveFile { get; private set; }

        /// <summary>True while a file is open on its own, outside any solution.</summary>
        public bool IsActive { get { return !string.IsNullOrEmpty(ActiveFile); } }

        /// <summary>
        /// The starter file. It is object-oriented and has a <c>main()</c> because rule 2 applies
        /// here too — handing the user a procedural <c>print("hello")</c> would be handing them a
        /// file the IDE then refuses to run, which teaches the rule in the worst possible way.
        /// </summary>
        private static string StarterSource(string stem)
        {
            string className = ClassNameFor(stem);
            return "\"\"\"" + stem + " — a single Python file.\n"
                + "\n"
                + "Opened on its own, outside a solution: it may import installed packages\n"
                + "(the standard library, anything pip has installed) and nothing else. To use\n"
                + "modules of your own, create a solution — File > New > Solution.\n"
                + "\"\"\"\n"
                + "\n"
                + "\n"
                + "class " + className + ":\n"
                + "    \"\"\"Work lives in a class: module-level functions are not allowed (PY8002).\"\"\"\n"
                + "\n"
                + "    @staticmethod\n"
                + "    def run() -> None:\n"
                + "        print(\"Hello from " + stem + "\")\n"
                + "\n"
                + "\n"
                + "def main() -> None:\n"
                + "    " + className + ".run()\n"
                + "\n"
                + "\n"
                + "if __name__ == \"__main__\":\n"
                + "    main()\n";
        }

        // snake_case / kebab-case / spaced file names -> PascalCase class name. Falls back to a
        // generic name for a stem with nothing usable in it, so an odd file name cannot produce a
        // starter file that does not parse.
        private static string ClassNameFor(string stem)
        {
            var sb = new System.Text.StringBuilder();
            bool upper = true;
            foreach (char c in stem ?? string.Empty)
            {
                if (c == '_' || c == '-' || c == ' ' || c == '.') { upper = true; continue; }
                if (!char.IsLetterOrDigit(c)) continue;
                if (sb.Length == 0 && !char.IsLetter(c)) continue;   // an identifier cannot start with a digit
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            return sb.Length == 0 ? "Script" : sb.ToString();
        }

        // ---- Commands ----------------------------------------------------------

        /// <summary>File ▸ New ▸ Python File: create a loose .py on disk and open it.</summary>
        public void NewFile()
        {
            if (!EnsureNoSolutionOpen("Creating a Python file on its own")) return;

            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Python Files (*.py)|*.py|All Files (*.*)|*.*";
                dlg.Title = "New Python File";
                dlg.FileName = "script.py";
                dlg.DefaultExt = "py";
                dlg.AddExtension = true;
                dlg.OverwritePrompt = true;
                if (dlg.ShowDialog(host) != DialogResult.OK) return;

                string path = dlg.FileName;
                try
                {
                    // The dialog already asked about overwriting, and the user said yes; writing
                    // the starter over a file they picked deliberately is what they asked for.
                    File.WriteAllText(path, StarterSource(Path.GetFileNameWithoutExtension(path)));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(host, "Could not create the file: " + ex.Message,
                        "New Python File", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Open(path);
            }
        }

        /// <summary>
        /// Open <paramref name="path"/> as a loose file. Returns false when the user declined to
        /// close an open solution, so callers can leave the IDE exactly as it was.
        /// </summary>
        public bool Open(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (!EnsureNoSolutionOpen("Opening a Python file on its own")) return false;

            // Order matters: the mode has to be on before the document opens. The Editor tab is
            // solution-scoped, so with the mode still off the gate takes it straight back off the
            // strip and the file lands in a pad nobody can see.
            ActiveFile = Path.GetFullPath(path);
            ApplyModeToShell();
            host.OpenInEditor(ActiveFile);
            host.RememberRecentFile(ActiveFile);
            // Again, now that there is an active document: the title is "{file} - {product}", and
            // the first call had no file to put in it yet.
            host.UpdateTitle();
            Check(reportClean: true);
            return true;
        }

        /// <summary>Leave single-file mode. Safe to call when it was never entered.</summary>
        public void Close()
        {
            if (!IsActive) return;
            ActiveFile = null;
            ApplyModeToShell();
        }

        // A solution and a loose file are mutually exclusive. Asking beats refusing: the user's
        // intent is unambiguous (they picked a .py), so the only real question is whether they
        // meant to put the solution away, and answering no leaves everything untouched.
        private bool EnsureNoSolutionOpen(string action)
        {
            if (!host.HasOpenSolution()) return true;

            if (host.IsNonInteractive()) { host.CloseSolutionForSingleFile(); return true; }

            DialogResult answer = MessageBox.Show(host,
                action + " closes the solution that is open now — a file on its own and a solution "
                + "are never open at the same time, so that Run, Debug and the rules always have "
                + "one target.\n\nClose '" + host._currentSolution.Name + "' and continue?",
                "Single File", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return false;

            host.CloseSolutionForSingleFile();
            return true;
        }

        private void ApplyModeToShell()
        {
            host.UpdateTitle();
            host.UpdateSingleFileUi();
        }

        // ---- The rules ----------------------------------------------------------

        /// <summary>
        /// Compile the loose file under the single-file policy and publish the result to Problems.
        /// Returns the blocking (error-severity policy) diagnostics, empty when it is clean.
        /// </summary>
        public IReadOnlyList<CompileDiagnostic> Check(bool reportClean)
        {
            var blocking = new List<CompileDiagnostic>();
            if (!IsActive) return blocking;

            string source;
            try { source = File.ReadAllText(ActiveFile); }
            catch (Exception ex)
            {
                host._statusLabel.Text = "Could not read " + Path.GetFileName(ActiveFile) + ": " + ex.Message;
                return blocking;
            }

            var options = new CompileOptions
            {
                // The path the script will actually run under: its own directory and nothing else.
                // Same object the rule resolves against, so a clean check and a clean run agree.
                SearchPath = HelixPythonNamespace.VirtualCompiler.Imports.PythonSearchPath
                    .ForRun(ActiveFile, null),
                Policy = SolutionPolicy.ForSingleFile(ActiveFile),
            };

            CompileResult result;
            try { result = VirtualCompiler.CompileSource(source, ActiveFile, options); }
            catch (Exception ex)
            {
                host._statusLabel.Text = "Could not check " + Path.GetFileName(ActiveFile) + ": " + ex.Message;
                return blocking;
            }

            var all = new List<CompileDiagnostic>(result.Diagnostics);
            foreach (CompileDiagnostic d in all)
                if (IsBlocking(d)) blocking.Add(d);

            // Clean and nobody asked: publish, say so, and leave the user looking at their file.
            // Anything to report goes through the full path, which reveals a closed Problems tab.
            if (blocking.Count == 0 && reportClean)
            {
                host.PublishDiagnosticsQuietly(all);
                host._statusLabel.Text = Path.GetFileName(ActiveFile)
                    + " — single file, no policy violations (installed packages only)";
            }
            else
            {
                host.FinishCompile(all);
            }
            return blocking;
        }

        private static bool IsBlocking(CompileDiagnostic d)
        {
            return d != null
                && d.Severity == HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error
                && !string.IsNullOrEmpty(d.Code)
                && d.Code.StartsWith("PY8", StringComparison.Ordinal);
        }

        /// <summary>
        /// Save, check, and report. Shared by Run and Debug so the two can never disagree about
        /// what is allowed to launch — the failure mode where Run refuses and Debug does not is
        /// the one that makes a hard rule advisory.
        /// </summary>
        private bool ReadyToLaunch(string verb)
        {
            if (!IsActive) return false;
            if (!host.SaveAllModifiedEditorTabsBeforeExternalOperation(verb)) return false;

            IReadOnlyList<CompileDiagnostic> blocking = Check(reportClean: false);
            if (blocking.Count == 0) return true;

            // Check has already put every violation in the Problems panel, with its file and line.
            // What is left is to say that the launch did not happen and why — a refusal the user
            // has to infer from a grid that was already on screen is indistinguishable from a Run
            // button that does nothing.
            //
            // The Output pane is written to only if it exists. It is built with a solution, and in
            // single-file mode there may never have been one; reaching into it unconditionally
            // turned a blocked Run into a NullReferenceException inside an `async void` handler,
            // which is the quietest way this could possibly have failed.
            if (host._buildOutputTextBox != null)
            {
                host._buildOutputTextBox.Clear();
                if (host._buildInnerTabControl != null && host._buildOutputSubTab != null)
                    host._buildInnerTabControl.SelectedTab = host._buildOutputSubTab;
                host.AppendBuildOutputLine("==> " + verb + " blocked");
                foreach (CompileDiagnostic d in blocking)
                    host.AppendBuildOutputLine("    " + d.Code + ": " + d.Message);
                host.ShowBuildResultsTabFromSingleFile();
            }
            if (host._buildSummaryLabel != null) host._buildSummaryLabel.Text = verb + " blocked";
            host._statusLabel.Text = verb + " blocked — " + blocking.Count
                + " policy violation(s) in " + Path.GetFileName(ActiveFile);

            if (!host.IsNonInteractive())
            {
                var text = new System.Text.StringBuilder();
                text.AppendLine(verb + " is blocked: " + Path.GetFileName(ActiveFile)
                    + " breaks " + blocking.Count + " rule" + (blocking.Count == 1 ? string.Empty : "s")
                    + " the IDE will not launch past.");
                text.AppendLine();
                foreach (CompileDiagnostic d in blocking)
                {
                    // Already 1-based, as the Problems grid prints it — see ProblemsPanel, which
                    // writes Span.Start.Line straight into the Line column.
                    text.AppendLine(d.Code + "  (line " + d.Span.Start.Line + ")");
                    text.AppendLine("    " + d.Message);
                    text.AppendLine();
                }
                MessageBox.Show(host, text.ToString().TrimEnd(), verb + " blocked",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return false;
        }

        /// <summary>Run the loose file.</summary>
        public async Task RunAsync()
        {
            if (!ReadyToLaunch("Run")) return;

            string file = ActiveFile;
            string workingDirectory = Path.GetDirectoryName(file);

            host._buildOutputTextBox.Clear();
            host._buildErrorsListView.Items.Clear();
            host._buildSummaryLabel.Text = "Running " + Path.GetFileName(file) + "...";
            host._buildInnerTabControl.SelectedTab = host._buildOutputSubTab;
            host.ShowBuildResultsTabFromSingleFile();
            host.AppendBuildOutputLine("==> python " + HelixPythonNamespace.Core.CommandLineArguments.Join(
                new List<string> { file }));
            host.AppendBuildOutputLine("    in " + workingDirectory);

            bool ok = await host.RunSingleFileAsync(file, workingDirectory);

            host._buildSummaryLabel.Text = ok
                ? "Run " + Path.GetFileName(file) + " succeeded"
                : "Run " + Path.GetFileName(file) + " failed";
            host._statusLabel.Text = host._buildSummaryLabel.Text;
        }

        /// <summary>Debug the loose file, under the same gate Run uses.</summary>
        public void Debug()
        {
            if (!ReadyToLaunch("Debug")) return;
            host.DebugSingleFile(ActiveFile, Path.GetDirectoryName(ActiveFile));
        }
    }
}
