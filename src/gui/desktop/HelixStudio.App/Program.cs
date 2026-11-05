namespace HelixPythonNamespace.App
{
    using System;
    using System.Windows.Forms;

    // WinForms entry point. Poor-man's composition root: it constructs every service
    // via ServiceLocator, hands them to MainForm, and runs the message loop.
    internal static class Program
    {
        // STAThread is required by WinForms (COM controls, clipboard, drag/drop).
        //
        // args carries the path Explorer passes when a registered file type is double-clicked
        // (Tools > Register TOML Files writes `"HelixPython.exe" "%1"` as the .toml open command),
        // and the same path a user can type at a prompt. It is NOT a flag surface: headless verbs
        // live in helixstudio.exe / helix_cli.exe, which are console-subsystem on purpose.
        [STAThread]
        static void Main(string[] args)
        {
            // ONE IDE PER SESSION, decided before anything else is built.
            //
            // Two shells share %APPDATA%\HelixPython — settings, tab layout, recent files,
            // breakpoints — and both write it on shutdown, so the one that quits last silently
            // discards the other's session. They also share the designers' per-solution sidecars.
            // A second launch therefore wakes the first and exits; see SingleInstance.
            if (!SingleInstance.Claim())
            {
                SingleInstance.HandOver(FirstPathArgument(args));
                return;
            }

            // Install the crash safety net first so failures during startup are also caught.
            GlobalExceptionHandler.Install();

            // Opt into XP/Luna visual styles and GDI text rendering before any UI exists.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Solution-native services
            var solutionService = ServiceLocator.CreateSolutionService();
            var typeService = ServiceLocator.CreateTypeService();
            var projectService = ServiceLocator.CreateProjectService();
            var layoutService = ServiceLocator.CreateLayoutService();

            // Analysis engine
            var analysisEngine = ServiceLocator.CreateAnalysisEngine();

            // Safeguard services
            var deletionAnalyzer = ServiceLocator.CreateDeletionImpactAnalyzer();

            // App services
            var recentFilesManager = ServiceLocator.CreateRecentFilesManager();
            var solutionGenerator = ServiceLocator.CreateSolutionGenerator();

            // Legacy services (kept for backward compatibility during transition)
            var codeExporter = ServiceLocator.CreateCodeExporter();
            var metadataExporter = ServiceLocator.CreateMetadataExporter();

            // User settings
            var settingsManager = new UserSettingsManager();

            // Inject the fully-built service graph into the shell form, then pump messages
            // until the form closes.
            var mainForm = new MainForm(
                solutionService,
                typeService,
                projectService,
                layoutService,
                analysisEngine,
                deletionAnalyzer,
                recentFilesManager,
                solutionGenerator,
                codeExporter,
                metadataExporter,
                settingsManager);

            // Let the crash handler flush unsaved editor edits before it shows its dialog.
            GlobalExceptionHandler.EmergencySave = mainForm.FlushAllEditorSavesSafe;

            // [X] SHUTS THE IDE DOWN. It used to hide to the notification area instead, which made
            // the close button mean something other than close: the only way to reach the tray was
            // to press [X], and the only way to actually quit was File ▸ Exit. The title bar now
            // carries four buttons -- [tray] [minimize] [maximize] [X] -- so hiding and quitting
            // are two gestures instead of one overloaded one, and each says what it does.
            //
            // Left assigned rather than deleted so the tray-on-close behaviour stays one flag away
            // if it is ever wanted back. See MainForm.Tray.cs and EnableTrayCaptionButton.
            mainForm.CloseToTrayEnabled = false;

            // Shell-launched path (double-click on an associated .toml, or a path on the command
            // line). Queued rather than opened here: the form has no handle until Application.Run
            // pumps, and the solution load marshals back onto the UI context.
            string shellPath = FirstPathArgument(args);
            if (shellPath != null) mainForm.QueueShellOpen(shellPath);

            Application.Run(mainForm);
        }

        /// <summary>
        /// The first argument that names something on disk. Anything option-shaped is ignored
        /// rather than treated as a path, so a mistyped flag opens the IDE empty instead of
        /// reporting "file not found" for a word the user never meant as a file.
        /// </summary>
        private static string FirstPathArgument(string[] args)
        {
            if (args == null) return null;
            foreach (string a in args)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                string candidate = a.Trim().Trim('"');
                if (candidate.Length == 0) continue;
                if (candidate[0] == '-' || candidate[0] == '/') continue;
                return candidate;
            }
            return null;
        }
    }
}
