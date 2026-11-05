namespace HelixPythonNamespace.App.Coordinators
{
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.UI.Interaction.Commands;
    using HelixPythonNamespace.AddIns.Kit;

    /// <summary>
    /// Builds and owns the application's main menu strip and toolbar. The command
    /// handlers themselves remain on <see cref="MainForm"/> (internal) for now and
    /// are repointed to their owning coordinators as those are extracted.
    /// </summary>
    public sealed class MenuCoordinator : ICoordinator
    {
        private MainFormContext _context;

        public void Attach(MainFormContext context)
        {
            _context = context;
        }

        public void Detach()
        {
            _context = null;
        }

        /// <summary>
        /// Construct the menu strip and toolbar onto the host form's fields. Called
        /// from <c>MainForm.InitializeComponent</c> before the strips are added to
        /// the form's control collection.
        /// </summary>
        public void Build(MainForm host)
        {
            // If a designer-authored menu already populated host._menuStrip, don't rebuild
            // it — skip straight to the toolbar below. Otherwise build the whole menu tree
            // here in code (the current build path). The toolbar is always built regardless.
            bool designerMenuAvailable = host._menuStrip != null && host._menuStrip.Items.Count > 0;
            if (!designerMenuAvailable)
            {
                // Menu strip
                host._menuStrip = new MenuStrip();

                // File menu
                ToolStripMenuItem fileMenu = new ToolStripMenuItem("&File");
                // Mirrors the designer menu: ONE "New" and ONE "Open".
                //
                // These used to be four siblings — New Project, New Solution, New Jupyter Notebook,
                // Open Solution — which made the top of File a list of the program's internal
                // categories instead of the two things a person does when they arrive. WHAT gets
                // created still differs; picking it is a second step, not a first one. The children
                // drop the word "New": inside a menu called New, "New Project..." says it twice.
                var miNew = new ToolStripMenuItem("&New");
                var miNewNotebook = new ToolStripMenuItem("&Jupyter Notebook...", null, host.OnFileNewNotebook);
                host._keybindings.Register("file.newNotebook", miNewNotebook);
                var miFileNew = new ToolStripMenuItem("&Project...", null, host.OnFileNew);
                host._keybindings.Register("file.new", miFileNew);
                // The escape hatch: always creates a STANDALONE solution rather than joining the
                // open one (see MainForm.OnFileNewSolution).
                var miNewSolution = new ToolStripMenuItem("&Solution...", null, host.OnFileNewSolution);
                // A single Python file, with no solution around it. Listed first because it is the
                // smallest thing this IDE can make and the one a person reaches for when they have
                // not decided on a project yet. What it costs is written on the file it creates:
                // installed packages only, and a solution is how you get your own modules.
                var miNewFile = new ToolStripMenuItem("Python &File...", null, host.OnFileNewPythonFile);
                host._keybindings.Register("file.newPythonFile", miNewFile);
                miNew.DropDownItems.AddRange(new ToolStripItem[]
                    { miNewFile, miNewNotebook, miFileNew, miNewSolution });
                fileMenu.DropDownItems.Add(miNew);

                // One Open, for both things this IDE can open. A pyproject.toml and an .ipynb are
                // told apart by their names, and the Recent list already routes a mixed list
                // correctly — so asking the user to classify the file before the dialog will show
                // it was asking a question the program can answer.
                var miFileOpen = new ToolStripMenuItem("&Open...", null, host.OnFileOpenAnything);
                host._keybindings.Register("file.open", miFileOpen);
                fileMenu.DropDownItems.Add(miFileOpen);
                var miFileImport = new ToolStripMenuItem("&Import Python Directory...", null, host.OnFileImportDirectory);
                host._keybindings.Register("file.import", miFileImport);
                fileMenu.DropDownItems.Add(miFileImport);
                var miOpenInEditor = new ToolStripMenuItem("Open File in &Editor...", null, host.OnFileOpenInEditor);
                host._keybindings.Register("file.openEditor", miOpenInEditor);
                fileMenu.DropDownItems.Add(miOpenInEditor);
                fileMenu.DropDownItems.Add("&Close Solution", null, host.OnFileClose);
                fileMenu.DropDownItems.Add(new ToolStripSeparator());

                // Recent Files submenu
                host._recentFilesMenu = new ToolStripMenuItem("&Recent Files");
                fileMenu.DropDownItems.Add(host._recentFilesMenu);
                host.RefreshRecentFilesMenu();

                fileMenu.DropDownItems.Add(new ToolStripSeparator());
                var miSaveLayout = new ToolStripMenuItem("&Save Layout", null, host.OnFileSaveLayout);
                host._keybindings.Register("file.saveLayout", miSaveLayout);
                fileMenu.DropDownItems.Add(miSaveLayout);
                fileMenu.DropDownItems.Add(new ToolStripSeparator());
                // Export submenu (C#-unique — folded under File for C++ menu parity).
                ToolStripMenuItem exportMenu = new ToolStripMenuItem("E&xport");
                exportMenu.DropDownItems.Add("Export C# &Skeletons...", null, host.OnExportCSharp);
                exportMenu.DropDownItems.Add("Export &JSON Metadata...", null, host.OnExportJson);
                exportMenu.DropDownItems.Add("Export &PNG Image...", null, host.OnExportPng);
                exportMenu.DropDownItems.Add("Export S&VG Image...", null, host.OnExportSvg);
                exportMenu.DropDownItems.Add("Export P&DF...", null, host.OnExportPdf);
                fileMenu.DropDownItems.Add(exportMenu);
                fileMenu.DropDownItems.Add(new ToolStripSeparator());
                // ExitApplication, not Close(): Close() is what the title-bar [X] reaches, and that
                // now hides to the notification area. Exit means exit. (This whole branch is the
                // fallback for a host with no designer-authored menu; the shipped shell uses
                // MainForm.Designer.cs's exitMenuItem, which routes to the same method.)
                fileMenu.DropDownItems.Add("E&xit", null, delegate { host.ExitApplication(); });

                // Edit menu
                ToolStripMenuItem editMenu = new ToolStripMenuItem("&Edit");
                var miUndo = new ToolStripMenuItem("&Undo", null, host.OnEditUndo);
                miUndo.ShortcutKeys = Keys.Control | Keys.Z;
                editMenu.DropDownItems.Add(miUndo);
                var miRedo = new ToolStripMenuItem("&Redo", null, host.OnEditRedo);
                miRedo.ShortcutKeys = Keys.Control | Keys.Y;
                editMenu.DropDownItems.Add(miRedo);
                editMenu.DropDownItems.Add(new ToolStripSeparator());
                var miCut = new ToolStripMenuItem("Cu&t", null, host.OnEditCut);
                miCut.ShortcutKeys = Keys.Control | Keys.X;
                editMenu.DropDownItems.Add(miCut);
                var miCopy = new ToolStripMenuItem("&Copy", null, host.OnEditCopy);
                miCopy.ShortcutKeys = Keys.Control | Keys.C;
                editMenu.DropDownItems.Add(miCopy);
                var miPaste = new ToolStripMenuItem("&Paste", null, host.OnEditPaste);
                miPaste.ShortcutKeys = Keys.Control | Keys.V;
                editMenu.DropDownItems.Add(miPaste);
                editMenu.DropDownItems.Add(new ToolStripSeparator());
                var miDelete = new ToolStripMenuItem("&Delete", null, host.OnDeleteSelection);
                miDelete.ShortcutKeys = Keys.Delete;
                editMenu.DropDownItems.Add(miDelete);
                editMenu.DropDownItems.Add(new ToolStripSeparator());
                var findTypeItem = new ToolStripMenuItem("&Find Type...", null, host.OnFindType);
                findTypeItem.ShortcutKeys = Keys.Control | Keys.F;
                editMenu.DropDownItems.Add(findTypeItem);
                editMenu.DropDownItems.Add(new ToolStripSeparator());
                var hideItem = new ToolStripMenuItem("&Hide Selected", null, host.OnHideSelected);
                hideItem.ShortcutKeys = Keys.Control | Keys.H;
                editMenu.DropDownItems.Add(hideItem);
                var showAllItem = new ToolStripMenuItem("&Show All Hidden", null, host.OnShowAllHidden);
                showAllItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.H;
                editMenu.DropDownItems.Add(showAllItem);
                // Diagram menu
                ToolStripMenuItem diagramMenu = new ToolStripMenuItem("&Diagram");
                diagramMenu.DropDownItems.Add("Add &Interface", null, delegate { host.AddType(TypeKind.Interface); });
                diagramMenu.DropDownItems.Add("Add &Abstract Class", null, delegate { host.AddType(TypeKind.AbstractClass); });
                diagramMenu.DropDownItems.Add("Add &Concrete Class", null, delegate { host.AddType(TypeKind.ConcreteClass); });
                diagramMenu.DropDownItems.Add(new ToolStripSeparator());
                diagramMenu.DropDownItems.Add("Auto &Layout", null, host.OnAutoLayout);
                diagramMenu.DropDownItems.Add("&Zoom to Fit", null, host.OnZoomToFit);
                diagramMenu.DropDownItems.Add("&Grid Settings...", null, host.OnGridSettings);
                ToolStripMenuItem drillUpItem =
                    new ToolStripMenuItem("Drill &Up", ToolbarIcons.DrillUp(), host.OnDrillUp);
                drillUpItem.Name = "miDrillUp";
                drillUpItem.ShortcutKeys = Keys.Alt | Keys.Up;
                diagramMenu.DropDownItems.Add(drillUpItem);
                diagramMenu.DropDownOpening += delegate
                {
                    drillUpItem.Enabled = host.CanDrillUp;
                };
                diagramMenu.DropDownItems.Add(new ToolStripSeparator());

                ToolStripMenuItem alignMenu = new ToolStripMenuItem("A&lign");
                alignMenu.DropDownItems.Add("Align &Left", null, delegate { host.AlignSelected(AlignmentKind.Left); });
                alignMenu.DropDownItems.Add("Align &Right", null, delegate { host.AlignSelected(AlignmentKind.Right); });
                alignMenu.DropDownItems.Add("Align &Top", null, delegate { host.AlignSelected(AlignmentKind.Top); });
                alignMenu.DropDownItems.Add("Align &Bottom", null, delegate { host.AlignSelected(AlignmentKind.Bottom); });
                alignMenu.DropDownItems.Add(new ToolStripSeparator());
                alignMenu.DropDownItems.Add("Center &Horizontally", null, delegate { host.AlignSelected(AlignmentKind.CenterHorizontal); });
                alignMenu.DropDownItems.Add("Center &Vertically", null, delegate { host.AlignSelected(AlignmentKind.CenterVertical); });
                diagramMenu.DropDownItems.Add(alignMenu);

                ToolStripMenuItem distributeMenu = new ToolStripMenuItem("&Distribute");
                distributeMenu.DropDownItems.Add("Distribute &Horizontally", null, delegate { host.DistributeSelected(DistributeKind.Horizontal); });
                distributeMenu.DropDownItems.Add("Distribute &Vertically", null, delegate { host.DistributeSelected(DistributeKind.Vertical); });
                diagramMenu.DropDownItems.Add(distributeMenu);

                diagramMenu.DropDownItems.Add(new ToolStripSeparator());
                diagramMenu.DropDownItems.Add("&Link Selected...", ToolbarIcons.Link(), host.OnLinkSelected);

                ToolStripMenuItem refactorMenu = new ToolStripMenuItem("&Refactor");
                refactorMenu.DropDownItems.Add("&Extract Interface...", null, host.OnExtractInterface);
                refactorMenu.DropDownItems.Add("&Pull Up Member...", null, host.OnPullUpMember);
                refactorMenu.DropDownItems.Add("Push &Down Member...", null, host.OnPushDownMember);
                diagramMenu.DropDownItems.Add(refactorMenu);

                // Build menu (C++ "Build"): Run Startup App + Packager + Compile + Build via C.
                ToolStripMenuItem buildMenu = new ToolStripMenuItem("&Build");
                host._runStartupMenuItem = new ToolStripMenuItem("&Run Startup App", ToolbarIcons.PlayRun(), host.OnRunStartupApp);
                host._runStartupMenuItem.ShortcutKeys = Keys.Control | Keys.F5;
                buildMenu.DropDownItems.Add(host._runStartupMenuItem);
                host._buildMenuItem = new ToolStripMenuItem("&Packager", ToolbarIcons.Build(), host.OnBuildSolution);
                host._buildMenuItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.B;
                buildMenu.DropDownItems.Add(host._buildMenuItem);
                ToolStripMenuItem compileMenuItem = new ToolStripMenuItem("&Compile", ToolbarIcons.Compile(), host.OnCompile);
                compileMenuItem.ShortcutKeys = Keys.Control | Keys.F7;
                host._keybindings.Register("build.compile", compileMenuItem);
                buildMenu.DropDownItems.Add(compileMenuItem);
                ToolStripMenuItem buildViaCMenuItem = new ToolStripMenuItem("Build via C++", null, host.OnBuildViaCpp);
                buildViaCMenuItem.ToolTipText = "Translate the Python solution to C++ (strict typed subset) and build a native .exe with MSVC.";
                buildMenu.DropDownItems.Add(buildViaCMenuItem);
                buildMenu.DropDownItems.Add(new ToolStripSeparator());
                ToolStripMenuItem selectInterpreterItem = new ToolStripMenuItem("Select Python &Interpreter…", null, host.OnSelectPythonInterpreter);
                buildMenu.DropDownItems.Add(selectInterpreterItem);
                buildMenu.DropDownItems.Add(new ToolStripSeparator());
                // &G, not &S: Build already spends Alt+S on "&Stop Execution" (same fix as the designer menu).
                host._buildSettingsMenuItem = new ToolStripMenuItem("Packager Settin&gs...", null, host.OnBuildToolSettings);
                buildMenu.DropDownItems.Add(host._buildSettingsMenuItem);

                // Analysis menu (C++ "Analysis"): Run Analysis + Lint + Find Dead Code.
                ToolStripMenuItem analyzeMenu = new ToolStripMenuItem("&Analysis");
                analyzeMenu.DropDownItems.Add("&Run Analysis", ToolbarIcons.Analyze(), host.OnRunAnalysis);
                var miLint = new ToolStripMenuItem("&Lint Python Sources", ToolbarIcons.Lint(), host.OnRunLint);
                miLint.ShortcutKeys = Keys.Control | Keys.Shift | Keys.L;
                analyzeMenu.DropDownItems.Add(miLint);
                var miDeadCode = new ToolStripMenuItem("Find &Dead Code", ToolbarIcons.DeadCode(), host.OnFindDeadCode);
                miDeadCode.ShortcutKeys = Keys.Control | Keys.Shift | Keys.D;
                analyzeMenu.DropDownItems.Add(miDeadCode);
                // Protein Structure is not here: it is Add-in ▸ PDB Viewer now (spec 004).

                // Tests menu (C++ "Tests").
                ToolStripMenuItem testsMenu = new ToolStripMenuItem("&Tests");
                var miRunAllTests = new ToolStripMenuItem("Run &All Tests", ToolbarIcons.Tests(), host.OnRunAllTests);
                miRunAllTests.ShortcutKeys = Keys.Control | Keys.Shift | Keys.T;
                testsMenu.DropDownItems.Add(miRunAllTests);
                testsMenu.DropDownItems.Add("Run &Failed Tests Only", null, host.OnRunFailedTests);
                testsMenu.DropDownItems.Add(new ToolStripSeparator());
                testsMenu.DropDownItems.Add("&Stop", null, host.OnStopTests);
                testsMenu.DropDownItems.Add("&Clear Results", null, host.OnClearTests);

                // Metrics menu (C++ "Metrics").
                ToolStripMenuItem metricsMenu = new ToolStripMenuItem("Me&trics");
                var miMetrics = new ToolStripMenuItem("Compute &Metrics", ToolbarIcons.Metrics(), host.OnComputeMetrics);
                miMetrics.ShortcutKeys = Keys.Control | Keys.Shift | Keys.M;
                metricsMenu.DropDownItems.Add(miMetrics);

                // Profiler menu (C++ "Profiler").
                ToolStripMenuItem profilerMenu = new ToolStripMenuItem("Pro&filer");
                var miProfile = new ToolStripMenuItem("&Profile Active File", ToolbarIcons.Profiler(), host.OnProfileActiveFile);
                miProfile.ShortcutKeys = Keys.Control | Keys.Shift | Keys.P;
                profilerMenu.DropDownItems.Add(miProfile);

                // Git menu (C++ "Git").
                ToolStripMenuItem gitMenu = new ToolStripMenuItem("&Git");
                gitMenu.DropDownItems.Add("Manage &.gitignore...", ToolbarIcons.Git(), host.OnManageGitIgnore);
                gitMenu.DropDownItems.Add(new ToolStripSeparator());
                var miPersist = new ToolStripMenuItem("&Commit All...", null, host.OnPersistRepo);
                miPersist.ToolTipText = "Init / stage / commit the working tree in one shot.";
                gitMenu.DropDownItems.Add(miPersist);

                // View menu
                ToolStripMenuItem viewMenu = new ToolStripMenuItem("&View");
                viewMenu.DropDownItems.Add("Dock Explorer &Left", null, host.OnDockExplorerLeft);
                viewMenu.DropDownItems.Add("Dock Explorer &Right", null, host.OnDockExplorerRight);
                viewMenu.DropDownItems.Add("&Float Explorer", null, host.OnFloatExplorer);
                // The way back from any arrangement at all. Tool windows can be closed, collapsed,
                // floated and dragged between docks, and the combinations that leave nothing on
                // screen are reachable by accident — this puts every one of them back where it
                // started, in one click, without touching the user's documents.
                viewMenu.DropDownItems.Add("Reset &Panel Layout", null, host.OnResetPanelLayout);
                viewMenu.DropDownItems.Add(new ToolStripSeparator());
                viewMenu.DropDownItems.Add("Undock &Analysis Results", null, host.OnToggleAnalysisResultsDocking);
                viewMenu.DropDownItems.Add("Undock &Output", null, host.OnToggleBuildResultsDocking);
                viewMenu.DropDownItems.Add(new ToolStripSeparator());
                host._toolsPaletteMenuItem = new ToolStripMenuItem("&Tools Palette", null, host.OnToggleToolsPalette);
                host._toolsPaletteMenuItem.CheckOnClick = false;
                viewMenu.DropDownItems.Add(host._toolsPaletteMenuItem);
                host._minimapMenuItem = new ToolStripMenuItem("&Minimap", null, host.OnToggleMinimap);
                host._minimapMenuItem.CheckOnClick = false;
                viewMenu.DropDownItems.Add(host._minimapMenuItem);
                host._snapToGridMenuItem = new ToolStripMenuItem("Snap to &Grid", null, host.OnToggleSnapToGrid);
                host._snapToGridMenuItem.Checked = true;
                viewMenu.DropDownItems.Add(host._snapToGridMenuItem);
                viewMenu.DropDownItems.Add(new ToolStripSeparator());
                viewMenu.DropDownItems.Add("&Refresh Canvas", ToolbarIcons.Refresh(), host.OnRefreshCanvas);
                viewMenu.DropDownItems.Add("Copy Diagram as &Image", null, host.OnCopyDiagramImage);

                // Panels submenu — closable/reopenable top-level tabs.
                viewMenu.DropDownItems.Add(new ToolStripSeparator());
                ToolStripMenuItem panelsMenu = new ToolStripMenuItem("&Panels");
                host.AddPanelMenuItem(panelsMenu, "welcome", "&Welcome Page", "view.panel.welcome");
                host.AddPanelMenuItem(panelsMenu, "designer", "Class Desi&gner Canvas", "view.panel.designer");
                host.AddPanelMenuItem(panelsMenu, "editor", "&Editor", "view.panel.editor");
                host.AddPanelMenuItem(panelsMenu, "debug", "&Debug", "view.panel.debug");
                host.AddPanelMenuItem(panelsMenu, "analysis", "&Analysis Results", "view.panel.analysis");
                host.AddPanelMenuItem(panelsMenu, "buildResults", "&Output", "view.panel.buildResults");
                host.AddPanelMenuItem(panelsMenu, "problems", "&Compile", "view.panel.problems");
                host.AddPanelMenuItem(panelsMenu, "hiddenElements", "&Hidden Elements", "view.panel.hiddenElements");
                host.AddPanelMenuItem(panelsMenu, "lint", "&Lint", "view.panel.lint");
                host.AddPanelMenuItem(panelsMenu, "deadcode", "Dead &Code", "view.panel.deadcode");
                host.AddPanelMenuItem(panelsMenu, "metrics", "&Metrics", "view.panel.metrics");
                host.AddPanelMenuItem(panelsMenu, "profile", "Pro&filer", "view.panel.profile");
                host.AddPanelMenuItem(panelsMenu, "tests", "&Tests", "view.panel.tests");
                // The PDB Viewer is an add-in (spec 004): its "PDB Viewer" item is under Add-in.
                viewMenu.DropDownItems.Add(panelsMenu);
                viewMenu.DropDownItems.Add(new ToolStripSeparator());
                host._floatTreeMenuItem = new ToolStripMenuItem("&Float Navigation Tree", null, host.OnToggleFloatNavigationTree);
                host._floatTreeMenuItem.CheckOnClick = true;
                viewMenu.DropDownItems.Add(host._floatTreeMenuItem);

                // Tools menu
                ToolStripMenuItem toolsMenu = new ToolStripMenuItem("&Tools");
                toolsMenu.DropDownItems.Add("&Preferences...", null, host.OnShowPreferences);
                toolsMenu.DropDownItems.Add("&Keybindings...", null, host.OnShowKeybindings);
                toolsMenu.DropDownItems.Add(new ToolStripSeparator());
                toolsMenu.DropDownItems.Add("C&Make Designer...", null, delegate { CMakeDesignerForm.ShowSingleton(host); });
                toolsMenu.DropDownItems.Add("&TOML Config Canvas...", null, delegate { OopClassCanvasForm.ShowTomlSample(host); });
                toolsMenu.DropDownItems.Add("Open Config on &Canvas...", null, delegate { OopClassCanvasForm.OpenTomlConfigOnCanvas(host); });
                toolsMenu.DropDownItems.Add("Debug Sample CUDA &Kernel...", null, delegate { host.OpenSampleCudaKernelForDebug(); });
                toolsMenu.DropDownItems.Add(new ToolStripSeparator());
                toolsMenu.DropDownItems.Add("&Register TOML Files", null, host.OnRegisterTomlFiles);

                // Debug menu
                ToolStripMenuItem debugMenu = new ToolStripMenuItem("&Debug");
                var miDbgStart = new ToolStripMenuItem("&Start / Continue", null, host.OnDebugContinue);
                host._keybindings.Register("debug.start", miDbgStart);
                var miDbgPause = new ToolStripMenuItem("&Pause / Break All", null, host.OnDebugPause);
                host._keybindings.Register("debug.pause", miDbgPause);
                var miDbgRestart = new ToolStripMenuItem("&Restart", null, host.OnDebugRestart);
                host._keybindings.Register("debug.restart", miDbgRestart);
                var miDbgStop = new ToolStripMenuItem("S&top", null, host.OnDebugStop);
                host._keybindings.Register("debug.stop", miDbgStop);
                var miDbgStepOver = new ToolStripMenuItem("Step &Over", null, host.OnDebugStepOver);
                host._keybindings.Register("debug.stepOver", miDbgStepOver);
                var miDbgStepInto = new ToolStripMenuItem("Step &Into", null, host.OnDebugStepInto);
                host._keybindings.Register("debug.stepInto", miDbgStepInto);
                var miDbgStepOut = new ToolStripMenuItem("Step O&ut", null, host.OnDebugStepOut);
                host._keybindings.Register("debug.stepOut", miDbgStepOut);
                var miDbgToggleBp = new ToolStripMenuItem("Toggle &Breakpoint", null, host.OnDebugToggleBreakpoint);
                host._keybindings.Register("debug.toggleBreakpoint", miDbgToggleBp);
                var miDbgStopOnEntry = new ToolStripMenuItem("Break on &Entry", null, host.OnDebugToggleStopOnEntry)
                { CheckOnClick = false, ToolTipText = "Pause on the script's first executable line when a debug session starts (applies on the next run)." };
                var miDbgExRaised = new ToolStripMenuItem("Break on &Raised Exceptions", null, host.OnDebugToggleExceptionRaised)
                { CheckOnClick = false, ToolTipText = "Pause whenever any exception is raised (applies on the next run)." };
                var miDbgExUncaught = new ToolStripMenuItem("Break on &Uncaught Exceptions", null, host.OnDebugToggleExceptionUncaught)
                { CheckOnClick = false, ToolTipText = "Pause only on exceptions that propagate uncaught (applies on the next run)." };
                var miDbgKernelEntry = new ToolStripMenuItem("Break on CUDA &Kernel Entry", null,
                    host.OnDebugToggleStopOnKernelEntry)
                { CheckOnClick = false, ToolTipText = "Pause at the first statement of every CUDA kernel the program launches. "
                    + "The way in to a kernel written inline in the .py, which has no file to set a breakpoint in until it has stopped once "
                    + "(applies on the next run)." };
                var miDbgCudaLane = new ToolStripMenuItem("CUDA Debug &Lane...", null, host.OnDebugChooseCudaLane)
                { ToolTipText = "Choose which CUDA thread the debugger follows. Every other thread still runs, so the kernel's results are unchanged." };
                debugMenu.DropDownItems.Add(miDbgStart);
                debugMenu.DropDownItems.Add(miDbgPause);
                debugMenu.DropDownItems.Add(miDbgRestart);
                debugMenu.DropDownItems.Add(miDbgStop);
                debugMenu.DropDownItems.Add(new ToolStripSeparator());
                debugMenu.DropDownItems.Add(miDbgStepOver);
                debugMenu.DropDownItems.Add(miDbgStepInto);
                debugMenu.DropDownItems.Add(miDbgStepOut);
                debugMenu.DropDownItems.Add(new ToolStripSeparator());
                debugMenu.DropDownItems.Add(miDbgToggleBp);
                debugMenu.DropDownItems.Add(new ToolStripSeparator());
                debugMenu.DropDownItems.Add(miDbgStopOnEntry);
                debugMenu.DropDownItems.Add(miDbgExRaised);
                debugMenu.DropDownItems.Add(miDbgExUncaught);
                debugMenu.DropDownItems.Add(new ToolStripSeparator());
                debugMenu.DropDownItems.Add(miDbgKernelEntry);
                debugMenu.DropDownItems.Add(miDbgCudaLane);

                // Help menu
                ToolStripMenuItem helpMenu = new ToolStripMenuItem("&Help");
                helpMenu.DropDownItems.Add("&Welcome Page", null, delegate { host.ShowTab("welcome"); });
                helpMenu.DropDownItems.Add(new ToolStripSeparator());
                helpMenu.DropDownItems.Add("&About " + MainForm.ProductName + "...", null, host.OnShowAbout);

                // Add top-level menus in the exact C++ order:
                // File, Edit, View, Build, Analysis, Tests, Metrics, Profiler, Diagram, Debug, Tools, Git, Help.
                host._menuStrip.Items.Add(fileMenu);
                host._menuStrip.Items.Add(editMenu);
                host._menuStrip.Items.Add(viewMenu);
                host._menuStrip.Items.Add(buildMenu);
                host._menuStrip.Items.Add(analyzeMenu);
                host._menuStrip.Items.Add(testsMenu);
                host._menuStrip.Items.Add(metricsMenu);
                host._menuStrip.Items.Add(profilerMenu);
                host._menuStrip.Items.Add(diagramMenu);
                host._menuStrip.Items.Add(debugMenu);
                host._menuStrip.Items.Add(toolsMenu);
                host._menuStrip.Items.Add(gitMenu);
                host._menuStrip.Items.Add(helpMenu);

                host.MainMenuStrip = host._menuStrip;
            }

                // Tool strip with icons and tooltips. Built unconditionally (outside the
                // menu guard above) so the toolbar exists even when the menu came from the
                // designer. Buttons reuse the same host.On* handlers as the menu items, and
                // fields like _linkButton/_runButton/_dbg*Button are cached on the host so
                // other coordinators can toggle their enabled-state at runtime.
                host._toolStrip = new ToolStrip();
                host._toolStrip.ImageScalingSize = new Size(16, 16);

                var btnNew = new ToolStripButton(null, ToolbarIcons.NewFile(), host.OnFileNew);
                btnNew.Name = "tbNew";
                btnNew.ToolTipText = "New Project (Ctrl+N)";
                host._toolStrip.Items.Add(btnNew);

                var btnOpen = new ToolStripButton(null, ToolbarIcons.OpenFolder(), host.OnFileOpen);
                btnOpen.Name = "tbOpen";
                btnOpen.ToolTipText = "Open a notebook or a solution (Ctrl+O)";
                host._toolStrip.Items.Add(btnOpen);

                var btnImport = new ToolStripButton(null, ToolbarIcons.Import(), host.OnFileImportDirectory);
                btnImport.Name = "tbImport";
                btnImport.ToolTipText = "Import Python Directory (Ctrl+I) — scan a folder and generate .sln + .pyproj";
                host._toolStrip.Items.Add(btnImport);

                var btnSave = new ToolStripButton(null, ToolbarIcons.Save(), host.OnFileSaveLayout);
                btnSave.Name = "tbSaveLayout";
                btnSave.ToolTipText = "Save Layout";
                host._toolStrip.Items.Add(btnSave);

                var btnRefresh = new ToolStripButton(null, ToolbarIcons.Refresh(), host.OnRefreshCanvas);
                btnRefresh.Name = "tbRefreshCanvas";
                btnRefresh.ToolTipText = "Refresh Canvas — recompact all tabs";
                host._toolStrip.Items.Add(btnRefresh);

                var btnNavigateBackward = new ToolStripButton(
                    null, ToolbarIcons.NavigateBackward(), host.OnNavigateBackward);
                btnNavigateBackward.Name = "tbNavigateBackward";
                btnNavigateBackward.ToolTipText = "Go backward to the previous source location";
                btnNavigateBackward.Enabled = false;
                host._toolStrip.Items.Add(btnNavigateBackward);

                var btnNavigateForward = new ToolStripButton(
                    null, ToolbarIcons.NavigateForward(), host.OnNavigateForward);
                btnNavigateForward.Name = "tbNavigateForward";
                btnNavigateForward.ToolTipText = "Go forward to the next source location";
                btnNavigateForward.Enabled = false;
                host._toolStrip.Items.Add(btnNavigateForward);

                host._drillUpButton =
                    new ToolStripButton(null, ToolbarIcons.DrillUp(), host.OnDrillUp);
                host._drillUpButton.Name = "tbDrillUp";
                host._drillUpButton.Enabled = false;
                host._drillUpButton.ToolTipText = "Drill Up — already at solution level";
                host._toolStrip.Items.Add(host._drillUpButton);

                host._toolStrip.Items.Add(new ToolStripSeparator());

                var btnInterface = new ToolStripButton(null, ToolbarIcons.Interface(), delegate { host.AddType(TypeKind.Interface); });
                btnInterface.Name = "tbAddInterface";
                btnInterface.ToolTipText = "Add Interface";
                host._toolStrip.Items.Add(btnInterface);

                var btnAbstract = new ToolStripButton(null, ToolbarIcons.AbstractClass(), delegate { host.AddType(TypeKind.AbstractClass); });
                btnAbstract.Name = "tbAddAbstract";
                btnAbstract.ToolTipText = "Add Abstract Class";
                host._toolStrip.Items.Add(btnAbstract);

                var btnConcrete = new ToolStripButton(null, ToolbarIcons.ConcreteClass(), delegate { host.AddType(TypeKind.ConcreteClass); });
                btnConcrete.Name = "tbAddConcrete";
                btnConcrete.ToolTipText = "Add Concrete Class";
                host._toolStrip.Items.Add(btnConcrete);

                host._linkButton = new ToolStripButton(null, ToolbarIcons.Link(), host.OnLinkSelected);
                host._linkButton.ToolTipText = "Link Selected Types (inheritance / implementation)";
                host._linkButton.Enabled = false;
                host._toolStrip.Items.Add(host._linkButton);

                host._toolStrip.Items.Add(new ToolStripSeparator());

                host._runButton = new ToolStripButton(null, ToolbarIcons.PlayRun(), host.OnRunStartupApp);
                host._runButton.ToolTipText = "Run Startup App (Ctrl+F5)";
                host._toolStrip.Items.Add(host._runButton);

                host._stopExecutionButton = new ToolStripButton(null, ToolbarIcons.DebugStop(), null);
                host._stopExecutionButton.ToolTipText = "Stop Execution";
                host._stopExecutionButton.Enabled = false;
                host._toolStrip.Items.Add(host._stopExecutionButton);

                host._terminalButton = new ToolStripButton(null, ToolbarIcons.Terminal(), (s, e) => host.OnOpenTerminal(s, e));
                host._terminalButton.Name = "tbTerminal";
                host._terminalButton.ToolTipText = "Open HelixPython Terminal";
                host._toolStrip.Items.Add(host._terminalButton);

                host._buildButton = new ToolStripButton(null, ToolbarIcons.Build(), host.OnBuildSolution);
                host._buildButton.ToolTipText = "Packager (Ctrl+Shift+B)";
                host._buildButton.Enabled = host._buildToolDetector.HasDotNet();
                host._toolStrip.Items.Add(host._buildButton);

                var btnAnalyze = new ToolStripButton(null, ToolbarIcons.Analyze(), host.OnRunAnalysis);
                btnAnalyze.Name = "tbAnalyze";
                btnAnalyze.ToolTipText = "Run Analysis";
                host._toolStrip.Items.Add(btnAnalyze);

                var btnCompile = new ToolStripButton(null, ToolbarIcons.Compile(), host.OnCompile);
                btnCompile.Name = "tbCompile";
                btnCompile.ToolTipText = "Compile (virtual-compile all .py files) — F6";
                host._toolStrip.Items.Add(btnCompile);

                host._toolStrip.Items.Add(new ToolStripSeparator());

                // Debug controls — visible step/continue/stop buttons so a user paused at a
                // breakpoint isn't forced into the Debug menu. Enabled-state is driven by
                // DebugCoordinator.UpdateDebugToolbar (start enabled; steps only while paused).
                host._dbgContinueButton = new ToolStripButton(null, ToolbarIcons.DebugContinue(), host.OnDebugContinue);
                host._dbgContinueButton.ToolTipText = "Start / Continue debugging (F5)";
                host._toolStrip.Items.Add(host._dbgContinueButton);

                host._dbgPauseButton = new ToolStripButton(null, ToolbarIcons.DebugPause(), host.OnDebugPause);
                // No keyboard shortcut is wired for Break All (the Ctrl+Alt+Break chord isn't a
                // reliable WinForms ShortcutKeys), so don't advertise one in the tooltip.
                host._dbgPauseButton.ToolTipText = "Break All / Pause";
                host._toolStrip.Items.Add(host._dbgPauseButton);

                host._dbgRestartButton = new ToolStripButton(null, ToolbarIcons.DebugRestart(), host.OnDebugRestart);
                host._dbgRestartButton.ToolTipText = "Restart debugging (Ctrl+Shift+F5)";
                host._toolStrip.Items.Add(host._dbgRestartButton);

                host._dbgStopButton = new ToolStripButton(null, ToolbarIcons.DebugStop(), host.OnDebugStop);
                host._dbgStopButton.ToolTipText = "Stop debugging (Shift+F5)";
                host._toolStrip.Items.Add(host._dbgStopButton);

                host._dbgStepOverButton = new ToolStripButton(null, ToolbarIcons.DebugStepOver(), host.OnDebugStepOver);
                host._dbgStepOverButton.ToolTipText = "Step Over (F10)";
                host._toolStrip.Items.Add(host._dbgStepOverButton);

                host._dbgStepIntoButton = new ToolStripButton(null, ToolbarIcons.DebugStepInto(), host.OnDebugStepInto);
                host._dbgStepIntoButton.ToolTipText = "Step Into (F11)";
                host._toolStrip.Items.Add(host._dbgStepIntoButton);

                host._dbgStepOutButton = new ToolStripButton(null, ToolbarIcons.DebugStepOut(), host.OnDebugStepOut);
                host._dbgStepOutButton.ToolTipText = "Step Out (Shift+F11)";
                host._toolStrip.Items.Add(host._dbgStepOutButton);

                host._dbgToggleBpButton = new ToolStripButton(null, ToolbarIcons.Breakpoint(), host.OnDebugToggleBreakpoint);
                host._dbgToggleBpButton.ToolTipText = "Toggle Breakpoint (F9)";
                host._toolStrip.Items.Add(host._dbgToggleBpButton);

                host._toolStrip.Items.Add(new ToolStripSeparator());

                var btnZoomIn = new ToolStripButton(null, ToolbarIcons.ZoomIn(), host.OnZoomIn);
                btnZoomIn.Name = "tbZoomIn";
                btnZoomIn.ToolTipText = "Zoom In";
                host._toolStrip.Items.Add(btnZoomIn);

                var btnZoomOut = new ToolStripButton(null, ToolbarIcons.ZoomOut(), host.OnZoomOut);
                btnZoomOut.Name = "tbZoomOut";
                btnZoomOut.ToolTipText = "Zoom Out";
                host._toolStrip.Items.Add(btnZoomOut);

                var btnZoomFit = new ToolStripButton(null, ToolbarIcons.ZoomFit(), host.OnZoomToFit);
                btnZoomFit.Name = "tbZoomFit";
                btnZoomFit.ToolTipText = "Zoom to Fit";
                host._toolStrip.Items.Add(btnZoomFit);

                // Zoom readout doubles as a preset picker (click for 25..200% or Zoom to Fit).
                host._zoomLabel = new ToolStripDropDownButton("100 %");
                host._zoomLabel.AutoSize = false;
                host._zoomLabel.Width = 66;
                host._zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
                host._zoomLabel.ShowDropDownArrow = true;
                host._zoomLabel.ToolTipText = "Zoom level — click for presets";
                foreach (int pct in new[] { 25, 50, 75, 100, 150, 200 })
                {
                    int p = pct; // capture per iteration
                    host._zoomLabel.DropDownItems.Add(p + " %", null, delegate { host.SetZoomPercent(p); });
                }
                host._zoomLabel.DropDownItems.Add(new ToolStripSeparator());
                host._zoomLabel.DropDownItems.Add("Zoom to Fit", null, host.OnZoomToFit);
                host._toolStrip.Items.Add(host._zoomLabel);

                host._toolStrip.Items.Add(new ToolStripSeparator());
                host.AddPanelToolbarButton("welcome", "Welcome", "Show/hide the Welcome page", ToolbarIcons.Welcome());
                host.AddPanelToolbarButton("designer", "Designer", "Show/hide the Class Designer Canvas tab", ToolbarIcons.Designer());
                host.AddPanelToolbarButton("editor", "Editor", "Show/hide the Editor tab", ToolbarIcons.Editor());
                host.AddPanelToolbarButton("debug", "Debug", "Show/hide the Debug tab", ToolbarIcons.DebugPanel());
                host.AddPanelToolbarButton("analysis", "Analysis", "Show/hide the Analysis Results tab", ToolbarIcons.Analyze());
                host.AddPanelToolbarButton("buildResults", "Output", "Show/hide the Output tab", ToolbarIcons.Output());
                host.AddPanelToolbarButton("problems", "Compile", "Show/hide the Compile tab", ToolbarIcons.Compile());
                host.AddPanelToolbarButton("hiddenElements", "Hidden", "Show/hide the Hidden Elements tab", ToolbarIcons.Hidden());

                // The designer-authored menu (used when designerMenuAvailable) ships a partial Debug
                // submenu — no Pause/Restart and no exception-break toggles. Backfill any missing items
                // into whichever Debug menu is live so the menu matches the toolbar and the feature set.
                EnsureDebugMenuItems(host);
                EnsureToolsMenuItems(host);
                EnsureBuildMenuItems(host);
        }

        // Analysis ▸ Protein Structure moved to Add-in ▸ PDB Viewer (spec 004 §4.3, menus ruling).

        // Backfill the (designer-authored) Build menu with the native CMake actions (FR-PM-03/04):
        // "Open CMake Folder…" and "Build CMake Project". Added only when absent, so this is idempotent.
        private static void EnsureBuildMenuItems(MainForm host)
        {
            if (host._menuStrip == null) return;
            ToolStripMenuItem build = null;
            foreach (ToolStripItem it in host._menuStrip.Items)
                if (it is ToolStripMenuItem m
                    && (m.Text ?? "").Replace("&", "").TrimStart().StartsWith("Build", System.StringComparison.OrdinalIgnoreCase))
                { build = m; break; }
            if (build == null) return;

            foreach (ToolStripItem it in build.DropDownItems)
                if ((it.Text ?? "").IndexOf("CMake", System.StringComparison.OrdinalIgnoreCase) >= 0) return; // already present

            build.DropDownItems.Add(new ToolStripSeparator());
            build.DropDownItems.Add(new ToolStripMenuItem("Open C&Make Folder…", null, host.OnOpenCMakeFolder)
            {
                Name = "openCMakeFolderMenuItem",
                ToolTipText = "Open a folder containing a CMakeLists.txt as a native CUDA/C++ project.",
            });

            // VS2017-parity Build commands for a native workspace: Build/Rebuild/Clean at solution
            // scope, and the same three per project. Mnemonics (B, D, L, J, U, N) avoid every letter
            // the Build menu already spends — a repeat makes the Alt key cycle instead of activate
            // (MenuMnemonicTests). Named, because OnBuildMenuOpening finds them by Name.
            build.DropDownItems.Add(new ToolStripMenuItem("&Build Solution", null, host.OnBuildCMakeProject)
            {
                Name = "buildCMakeSolutionMenuItem",
                ToolTipText = "Configure and build every target with the real cmake toolchain.",
            });
            build.DropDownItems.Add(new ToolStripMenuItem("Rebuil&d Solution", null, host.OnRebuildCMakeSolution)
            {
                Name = "rebuildCMakeSolutionMenuItem",
                ToolTipText = "Clean the whole build tree, then configure and build it again.",
            });
            build.DropDownItems.Add(new ToolStripMenuItem("C&lean Solution", null, host.OnCleanCMakeSolution)
            {
                Name = "cleanCMakeSolutionMenuItem",
                ToolTipText = "cmake --build --target clean over the whole build tree.",
            });
            build.DropDownItems.Add(new ToolStripMenuItem("Build Pro&ject")
            { Name = "buildCMakeProjectMenu", ToolTipText = "Build one CMake target." });
            build.DropDownItems.Add(new ToolStripMenuItem("Reb&uild Project")
            { Name = "rebuildCMakeProjectMenu", ToolTipText = "Clean one CMake target's objects and binary, then build it." });
            build.DropDownItems.Add(new ToolStripMenuItem("Clea&n Project")
            { Name = "cleanCMakeProjectMenu", ToolTipText = "Remove one CMake target's objects and binary." });
        }

        // Backfill the (designer-authored) Tools menu with the nvcc CUDA-verify action.
        private static void EnsureToolsMenuItems(MainForm host)
        {
            if (host._menuStrip == null) return;
            ToolStripMenuItem tools = null;
            foreach (ToolStripItem it in host._menuStrip.Items)
                if (it is ToolStripMenuItem m
                    && (m.Text ?? "").Replace("&", "").TrimStart().StartsWith("Tools", System.StringComparison.OrdinalIgnoreCase))
                { tools = m; break; }
            if (tools == null) return;
            bool hasNvcc = false, hasCMakeDesigner = false, hasTomlDesigner = false, hasRegisterToml = false, hasDevelopmentEnv = false;
            foreach (ToolStripItem it in tools.DropDownItems)
            {
                // The accelerator is stripped first: "C&Make Designer..." does not CONTAIN
                // "CMake Designer", so comparing the raw caption would leave that guard false and
                // the menu free to grow a second copy. FindMenuItemByText has always stripped it;
                // this is the same rule, in the other place that reads menu captions.
                string t = (it.Text ?? "").Replace("&", string.Empty);
                if (t.IndexOf("nvcc", System.StringComparison.OrdinalIgnoreCase) >= 0) hasNvcc = true;
                if (t.IndexOf("Register TOML", System.StringComparison.OrdinalIgnoreCase) >= 0) hasRegisterToml = true;
                if (t.IndexOf("Development Env", System.StringComparison.OrdinalIgnoreCase) >= 0) hasDevelopmentEnv = true;
                if (t.IndexOf("CMake Designer", System.StringComparison.OrdinalIgnoreCase) >= 0) hasCMakeDesigner = true;
                if (t.IndexOf("TOML Config Canvas", System.StringComparison.OrdinalIgnoreCase) >= 0) hasTomlDesigner = true;
            }
            if (hasNvcc && hasCMakeDesigner && hasTomlDesigner && hasRegisterToml && hasDevelopmentEnv) return;
            if (!hasNvcc)
            {
                tools.DropDownItems.Add(new ToolStripSeparator());
                tools.DropDownItems.Add(new ToolStripMenuItem("Verify Active .cu with &nvcc", null, host.OnVerifyCudaWithNvcc)
                { ToolTipText = "Compile the active CUDA file with the real nvcc toolchain and mark its authoritative errors." });
            }
            // FR-DES-02, the Python GUI form-builder surface, is an add-in now: its item is under
            // Add-in ▸ GUI Designer Canvas (spec 004, Task B).
            // FR-DES-06: the visual CMake project designer surface.
            if (!hasCMakeDesigner)
                tools.DropDownItems.Add(new ToolStripMenuItem("C&Make Designer...", null,
                    delegate { CMakeDesignerForm.ShowSingleton(host); })
                { ToolTipText = "Design a CMake project's target graph visually and generate CMakeLists.txt." });
            // FR-DES-05: the TOML designer is the GDI+ OOP canvas, like every other language surface —
            // tables render as boxes with their keys as members and nested tables as linked children.
            // The tree+form editor it replaces was the odd one out; OpenTomlConfigOnCanvas already
            // existed (with the file picker and solution-file fast path) but nothing called it.
            if (!hasTomlDesigner)
                tools.DropDownItems.Add(new ToolStripMenuItem("&TOML Config Canvas...", null,
                    delegate
                    {
                        string preferred = host._currentSolution != null ? host._currentSolution.FilePath : null;
                        OopClassCanvasForm.OpenTomlConfigOnCanvas(host, preferred);
                    })
                { ToolTipText = "Render a TOML/pyproject document as a diagram on the GDI+ canvas." });
            // What language kits this machine actually has. Machine state, like Register TOML Files
            // below it, so it is never gated on a workspace or an active document — the question it
            // answers ("is the toolchain for this even installed?") comes up before there is one.
            // Mnemonic 'D' is otherwise unused in this menu (MenuMnemonicTests).
            if (!hasDevelopmentEnv)
            {
                tools.DropDownItems.Add(new ToolStripSeparator());
                tools.DropDownItems.Add(new ToolStripMenuItem("&Development Env", null, host.OnShowDevelopmentEnvironment)
                {
                    Name = "developmentEnvMenuItem",
                    ToolTipText = "Open the Development Environment tab: the programming-language kits "
                                + "installed on this machine — interpreters, compilers, SDKs and build "
                                + "tools, with their versions and paths.",
                });
            }
            // Shell integration: claim .toml for this build so Explorer double-click opens the
            // whole owning solution. Separated by a rule because it changes the MACHINE's state,
            // not the IDE's — everything above this line only opens a window. Mnemonic 'R' is
            // otherwise unused in this menu (MenuMnemonicTests).
            if (!hasRegisterToml)
            {
                tools.DropDownItems.Add(new ToolStripSeparator());
                tools.DropDownItems.Add(new ToolStripMenuItem("&Register TOML Files", null, host.OnRegisterTomlFiles)
                {
                    Name = "registerTomlFilesMenuItem",
                    ToolTipText = "Associate .toml with HelixPython (current user) so double-clicking a "
                                + "pyproject.toml in Explorer opens its whole solution here.",
                });
            }
        }

        // Idempotently ensure the live Debug menu has Pause, Restart, and the exception-break toggles.
        // Each item is added only when absent (matched by a text token), so this is a no-op for the
        // fully code-built menu and a backfill for the partial designer menu.
        private static void EnsureDebugMenuItems(MainForm host)
        {
            if (host._menuStrip == null) return;
            ToolStripMenuItem debug = null;
            foreach (ToolStripItem it in host._menuStrip.Items)
                if (it is ToolStripMenuItem m
                    && (m.Text ?? "").Replace("&", "").TrimStart().StartsWith("Debug", System.StringComparison.OrdinalIgnoreCase))
                { debug = m; break; }
            if (debug == null) return;

            System.Func<string, bool> has = token =>
            {
                foreach (ToolStripItem it in debug.DropDownItems)
                    if ((it.Text ?? "").Replace("&", "").IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            };

            if (!has("Pause"))
            {
                int idx = 1;
                for (int i = 0; i < debug.DropDownItems.Count; i++)
                    if ((debug.DropDownItems[i].Text ?? "").Replace("&", "").IndexOf("Continue", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    { idx = i + 1; break; }
                // Restart / Run-to-Cursor are global debug commands, so a real ShortcutKeys (not a
                // display-only hint) is correct — they dispatch regardless of focus. Chords match
                // Visual Studio: Restart = Ctrl+Shift+F5, Run to Cursor = Ctrl+F10.
                debug.DropDownItems.Insert(idx, new ToolStripMenuItem("&Restart", null, host.OnDebugRestart)
                    { ShortcutKeys = Keys.Control | Keys.Shift | Keys.F5 });
                debug.DropDownItems.Insert(idx, new ToolStripMenuItem("&Pause / Break All", null, host.OnDebugPause));
            }
            if (!has("Run to Cursor"))
                debug.DropDownItems.Add(new ToolStripMenuItem("Run to &Cursor", null, host.OnDebugRunToCursor)
                    { ShortcutKeys = Keys.Control | Keys.F10 });
            if (!has("Entry"))
            {
                debug.DropDownItems.Add(new ToolStripSeparator());
                debug.DropDownItems.Add(new ToolStripMenuItem("Break on &Entry", null, host.OnDebugToggleStopOnEntry));
            }
            if (!has("Raised"))
                debug.DropDownItems.Add(new ToolStripMenuItem("Break on &Raised Exceptions", null, host.OnDebugToggleExceptionRaised));
            if (!has("Uncaught"))
                debug.DropDownItems.Add(new ToolStripMenuItem("Break on &Uncaught Exceptions", null, host.OnDebugToggleExceptionUncaught));
        }
    }
}
