namespace HelixPythonNamespace.App
{
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    public partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // The solution file watcher is owned by this form but was only ever torn down by
                // StopFileWatcher() on solution close/reopen — never on disposal. A disposed MainForm
                // therefore left a live FileSystemWatcher still subscribed to OnExternalChangeDetected
                // and OnConflictDetected, firing callbacks into a dead form. In the shipped app the
                // process usually exits straight after, which hid it; in a host that outlives the form
                // (the test runner) the stale watcher fires during LATER work and takes the process
                // down with no catchable managed exception. Idempotent and null-safe.
                StopFileWatcher();

                if (components != null)
                {
                    components.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        internal AnalysisFeedbackPanel _analysisPanel;
        internal DiagramCanvas _abstractClassesCanvas;
        internal DiagramCanvas _canvas;
        internal DiagramCanvas _generalClassesCanvas;
        internal DiagramCanvas _interfacesCanvas;
        internal DiagramCanvas _manageGlobalNsCanvas;
        internal DockablePanel _leftDockPanel;
        internal Form _floatingAnalysisResults;
        internal Form _floatingBuildResults;
        internal Form _floatingExplorerForm;
        internal Form _floatingTreeForm;
        internal Label _analysisSummaryLabel;
        internal Label _buildSummaryLabel;
        internal ListView _analysisResultsListView;
        internal ListView _buildErrorsListView;
        internal HelixPythonNamespace.App.Services.DiagnosticStore _diagnosticStore;
        internal string _diagnosticSourceFilter;
        internal string _diagnosticSeverityFilter;
        internal MenuStrip _menuStrip;
        internal ProjectTreePanel _projectTreePanel;
        internal HelixPythonNamespace.App.Controls.DeadCodePanel _deadCodePanel;
        internal HelixPythonNamespace.App.Controls.LintResultsPanel _lintPanel;
        internal HelixPythonNamespace.App.Controls.MetricsPanel _metricsPanel;
        internal HelixPythonNamespace.App.Controls.ComplexityPanel _complexityPanel;
        internal HelixPythonNamespace.App.Controls.CodeReviewPanel _codeReviewPanel;
        internal HelixPythonNamespace.App.Controls.ProblemsPanel _problemsPanel;
        internal HelixPythonNamespace.App.Controls.ReferencesPanel _referencesPanel;
        internal HelixPythonNamespace.App.Controls.CallHierarchyPanel _callHierarchyPanel;
        internal HelixPythonNamespace.App.Controls.ProfilePanel _profilePanel;
        internal HelixPythonNamespace.App.Controls.TestRunnerPanel _testRunnerPanel;

        /// <summary>Live 3D view of a running simulation. Internal so tests and harnesses can drive it.</summary>

        /// <summary>The AI agent panel. Internal so the GUI verification harness can drive it.</summary>
        internal HelixPythonNamespace.App.Controls.AiTerminalPanel _aiTerminalPanel;

        /// <summary>Chrome around the AI Terminal, giving it a title bar like the other docks.</summary>
        internal HelixPythonNamespace.UI.Controls.DockablePanel _aiTerminalDock;

        /// <summary>Editor above / AI Terminal below, so both are on screen together.</summary>
        internal SplitContainer _editorAiSplitter;

        /// <summary>View ▸ AI Terminal — the way back after the header's ✕ hides the panel.</summary>
        internal ToolStripMenuItem _aiTerminalMenuItem;

        /// <summary>
        /// The AI providers the panel can choose between. Keys are resolved lazily from
        /// UserSettings so a key pasted into Preferences takes effect without a restart.
        /// </summary>
        internal HelixPythonNamespace.Ai.Agent.AiProviderRegistry _aiProviders;
        internal SplitContainer _leftSplitter;
        internal TabControl _analysisInnerTabControl;
        internal TabControl _buildInnerTabControl;
        internal HelixPythonNamespace.UI.Controls.CompositedTabControl _editorInnerTabControl;
        internal TabPage _analysisResultsSubTab;
        internal TabPage _analysisResultsTab;
        internal TabPage _buildErrorsSubTab;
        internal TabPage _buildOutputSubTab;
        internal TabPage _buildResultsTab;
        internal TabPage _editorTab;
        internal TabPage _manageGlobalNsTab;
        internal TabPage _problemsTab;
        internal TextBox _analysisOutputTextBox;
        internal TextBox _buildOutputTextBox;
        internal ToolStrip _toolStrip;
        internal ToolStripButton _buildButton;
        internal ToolStripButton _cancelJobButton;
        internal ToolStripButton _dbgContinueButton;
        internal ToolStripButton _dbgPauseButton;
        internal ToolStripButton _dbgRestartButton;
        internal ToolStripButton _dbgStepIntoButton;
        internal ToolStripButton _dbgStepOutButton;
        internal ToolStripButton _dbgStepOverButton;
        internal ToolStripButton _dbgStopButton;
        internal ToolStripButton _dbgToggleBpButton;
        internal ToolStripButton _linkButton;
        internal ToolStripButton _runButton;
        internal ToolStripButton _stopExecutionButton;
        internal ToolStripButton _terminalButton;
        private ToolStripButton newButton;
        private ToolStripButton openButton;
        private ToolStripButton importButton;
        private ToolStripButton saveButton;
        private ToolStripButton refreshButton;
        private ToolStripButton interfaceButton;
        private ToolStripButton abstractButton;
        private ToolStripButton concreteButton;
        private ToolStripButton analyzeButton;
        private ToolStripButton compileButton;
        private ToolStripButton zoomInButton;
        private ToolStripButton zoomOutButton;
        private ToolStripButton zoomFitButton;
        private ToolStripButton welcomePanelButton;
        private ToolStripButton designerPanelButton;
        private ToolStripButton editorPanelButton;
        private ToolStripButton debugPanelButton;
        private ToolStripButton analysisPanelButton;
        private ToolStripButton outputPanelButton;
        private ToolStripButton problemsPanelButton;
        private ToolStripButton hiddenPanelButton;
        private ToolStripSeparator fileToolbarSeparator;
        private ToolStripSeparator diagramToolbarSeparator;
        private ToolStripSeparator buildToolbarSeparator;
        private ToolStripSeparator debugToolbarSeparator;
        private ToolStripSeparator zoomToolbarSeparator;
        internal ToolStripDropDownButton _zoomLabel;
        internal ToolStripMenuItem _buildMenuItem;
        internal ToolStripMenuItem _buildSettingsMenuItem;
        internal ToolStripMenuItem _floatTreeMenuItem;
        internal ToolStripMenuItem _minimapMenuItem;
        internal ToolStripMenuItem _recentFilesMenu;
        internal ToolStripMenuItem _runStartupMenuItem;
        internal ToolStripMenuItem _stopExecutionMenuItem;
        internal ToolStripMenuItem _runInTerminalMenuItem;
        internal ToolStripMenuItem _snapToGridMenuItem;
        internal ToolStripMenuItem _toolsPaletteMenuItem;
        internal ToolStripStatusLabel _statusLabel;
        private HelixPythonNamespace.App.Controls.HiddenElementsPanel _hiddenElementsPanel;
        private HelixPythonNamespace.App.Controls.WelcomePanel _welcomePanel;
        private HelixPythonNamespace.UI.Controls.MinimapForm _minimap;
        private HelixPythonNamespace.UI.Controls.ToolsPaletteForm _toolsPalette;
        private StatusStrip _statusStrip;
        internal TabControl _designerInnerTabControl;
        private TabPage _abstractClassesTab;
        private TabPage _analysisOutputSubTab;
        internal TabPage _designerTab;
        private Panel _designerFilterBar;
        private Panel _designerCanvasHost;
        private TabPage _generalClassesTab;
        internal TabPage _graphicalDesignerTab;
        private TabPage _hiddenElementsTab;
        private TabPage _interfacesTab;
        private TabPage _welcomeTab;
        private ToolStripStatusLabel _breadcrumbLabel;
        private ToolStripStatusLabel _caretLabel;
        private ToolStripStatusLabel _indentLabel;
        private ToolStripStatusLabel _eolLabel;
        private ToolStripStatusLabel _diagnosticsStatusLabel;
        private ToolStripMenuItem fileMenuItem;
        private ToolStripMenuItem newProjectWizardMenuItem;
        internal ToolStripMenuItem newSolutionMenuItem;
        private ToolStripMenuItem openSolutionMenuItem;
        private ToolStripMenuItem importDirectoryMenuItem;
        private ToolStripMenuItem openEditorMenuItem;
        internal ToolStripMenuItem newMenuItem;
        private ToolStripMenuItem newNotebookMenuItem;
        private ToolStripMenuItem closeSolutionMenuItem;
        private ToolStripMenuItem saveFileMenuItem;
        internal ToolStripMenuItem saveAllFilesMenuItem;
        internal ToolStripMenuItem closeFileMenuItem;
        private ToolStripMenuItem saveLayoutMenuItem;
        private ToolStripMenuItem exportMenuItem;
        private ToolStripMenuItem exportCSharpMenuItem;
        private ToolStripMenuItem exportJsonMenuItem;
        private ToolStripMenuItem exportPngMenuItem;
        private ToolStripMenuItem exportSvgMenuItem;
        private ToolStripMenuItem exportPdfMenuItem;
        private ToolStripMenuItem exitMenuItem;
        private ToolStripMenuItem editMenuItem;
        private ToolStripMenuItem undoMenuItem;
        private ToolStripMenuItem redoMenuItem;
        private ToolStripMenuItem cutMenuItem;
        private ToolStripMenuItem copyMenuItem;
        private ToolStripMenuItem pasteMenuItem;
        private ToolStripMenuItem deleteMenuItem;
        private ToolStripMenuItem findTypeMenuItem;
        private ToolStripMenuItem hideSelectedMenuItem;
        private ToolStripMenuItem showHiddenMenuItem;
        private ToolStripMenuItem viewMenuItem;
        private ToolStripMenuItem dockLeftMenuItem;
        private ToolStripMenuItem dockRightMenuItem;
        private ToolStripMenuItem floatExplorerMenuItem;
        private ToolStripMenuItem undockAnalysisMenuItem;
        private ToolStripMenuItem undockOutputMenuItem;
        private ToolStripMenuItem refreshCanvasMenuItem;
        private ToolStripMenuItem copyImageMenuItem;
        private ToolStripMenuItem panelsMenuItem;
        private ToolStripMenuItem welcomePanelMenuItem;
        private ToolStripMenuItem designerPanelMenuItem;
        private ToolStripMenuItem editorPanelMenuItem;
        private ToolStripMenuItem debugPanelMenuItem;
        private ToolStripMenuItem analysisPanelMenuItem;
        private ToolStripMenuItem outputPanelMenuItem;
        private ToolStripMenuItem problemsPanelMenuItem;
        private ToolStripMenuItem hiddenElementsPanelMenuItem;
        private ToolStripMenuItem lintPanelMenuItem;
        private ToolStripMenuItem deadCodePanelMenuItem;
        private ToolStripMenuItem metricsPanelMenuItem;
        private ToolStripMenuItem profilerPanelMenuItem;
        private ToolStripMenuItem testsPanelMenuItem;
        private ToolStripMenuItem referencesPanelMenuItem;
        private ToolStripMenuItem callHierarchyPanelMenuItem;
        private ToolStripMenuItem terminalMenuItem;
        internal ToolStripMenuItem analysisMenuItem;
        private ToolStripMenuItem buildTopMenuItem;
        private ToolStripMenuItem compileMenuItem;
        private ToolStripMenuItem buildViaCMenuItem;
        private ToolStripMenuItem selectInterpreterMenuItem;
        private ToolStripMenuItem runAnalysisMenuItem;
        private ToolStripMenuItem lintMenuItem;
        private ToolStripMenuItem deadCodeMenuItem;
        internal ToolStripMenuItem testsMenuItem;
        private ToolStripMenuItem runAllTestsMenuItem;
        private ToolStripMenuItem runFailedTestsMenuItem;
        private ToolStripMenuItem stopTestsMenuItem;
        private ToolStripMenuItem clearTestsMenuItem;
        private ToolStripMenuItem metricsMenuItem;
        private ToolStripMenuItem computeMetricsMenuItem;
        private ToolStripMenuItem profilerMenuItem;
        private ToolStripMenuItem profileFileMenuItem;
        private ToolStripMenuItem diagramMenuItem;
        private ToolStripMenuItem addInterfaceMenuItem;
        private ToolStripMenuItem addAbstractMenuItem;
        private ToolStripMenuItem addConcreteMenuItem;
        private ToolStripMenuItem autoLayoutMenuItem;
        private ToolStripMenuItem zoomFitMenuItem;
        private ToolStripMenuItem gridSettingsMenuItem;
        private ToolStripMenuItem linkSelectedMenuItem;
        private ToolStripMenuItem debugMenuItem;
        private ToolStripMenuItem debugContinueMenuItem;
        private ToolStripMenuItem debugStopMenuItem;
        private ToolStripMenuItem debugStepOverMenuItem;
        private ToolStripMenuItem debugStepIntoMenuItem;
        private ToolStripMenuItem debugStepOutMenuItem;
        private ToolStripMenuItem debugBreakpointMenuItem;
        private ToolStripMenuItem toolsMenuItem;
        private ToolStripMenuItem preferencesMenuItem;
        private ToolStripMenuItem keybindingsMenuItem;
        private ToolStripMenuItem gitMenuItem;
        private ToolStripMenuItem gitIgnoreMenuItem;
        private ToolStripMenuItem persistRepoMenuItem;
        private ToolStripMenuItem helpMenuItem;
        private ToolStripMenuItem welcomeMenuItem;
        private ToolStripMenuItem aboutMenuItem;

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this._menuStrip = new System.Windows.Forms.MenuStrip();
            this.fileMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.newProjectWizardMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.newSolutionMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.openSolutionMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.importDirectoryMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.openEditorMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.newMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.newNotebookMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.closeSolutionMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._recentFilesMenu = new System.Windows.Forms.ToolStripMenuItem();
            this.saveFileMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.saveAllFilesMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.closeFileMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.saveLayoutMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exportMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exportCSharpMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exportJsonMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exportPngMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exportSvgMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exportPdfMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.exitMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.editMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.undoMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.redoMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.cutMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.copyMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.pasteMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.deleteMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.findTypeMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.hideSelectedMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.showHiddenMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.viewMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.dockLeftMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.dockRightMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.floatExplorerMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.undockAnalysisMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.undockOutputMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._toolsPaletteMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._minimapMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._snapToGridMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.refreshCanvasMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.copyImageMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.panelsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.welcomePanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.designerPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.editorPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.debugPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.analysisPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.outputPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.problemsPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.hiddenElementsPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.lintPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.deadCodePanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.metricsPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.profilerPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.testsPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.referencesPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.callHierarchyPanelMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.terminalMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._floatTreeMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.buildTopMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._runStartupMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._stopExecutionMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._runInTerminalMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._buildMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.compileMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.buildViaCMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.selectInterpreterMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._buildSettingsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.analysisMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.runAnalysisMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.lintMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.deadCodeMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.testsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.runAllTestsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.runFailedTestsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.stopTestsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.clearTestsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.metricsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.computeMetricsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.profilerMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.profileFileMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.diagramMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.addInterfaceMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.addAbstractMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.addConcreteMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.autoLayoutMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.zoomFitMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.gridSettingsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.linkSelectedMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.debugMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.debugContinueMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.debugStopMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.debugStepOverMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.debugStepIntoMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.debugStepOutMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.debugBreakpointMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.toolsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.preferencesMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.keybindingsMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.gitMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.gitIgnoreMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.persistRepoMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.helpMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.welcomeMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.aboutMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this._toolStrip = new System.Windows.Forms.ToolStrip();
            this.newButton = new System.Windows.Forms.ToolStripButton();
            this.openButton = new System.Windows.Forms.ToolStripButton();
            this.importButton = new System.Windows.Forms.ToolStripButton();
            this.saveButton = new System.Windows.Forms.ToolStripButton();
            this.refreshButton = new System.Windows.Forms.ToolStripButton();
            this.fileToolbarSeparator = new System.Windows.Forms.ToolStripSeparator();
            this.interfaceButton = new System.Windows.Forms.ToolStripButton();
            this.abstractButton = new System.Windows.Forms.ToolStripButton();
            this.concreteButton = new System.Windows.Forms.ToolStripButton();
            this._linkButton = new System.Windows.Forms.ToolStripButton();
            this.diagramToolbarSeparator = new System.Windows.Forms.ToolStripSeparator();
            this._runButton = new System.Windows.Forms.ToolStripButton();
            this._buildButton = new System.Windows.Forms.ToolStripButton();
            this.analyzeButton = new System.Windows.Forms.ToolStripButton();
            this.compileButton = new System.Windows.Forms.ToolStripButton();
            this.buildToolbarSeparator = new System.Windows.Forms.ToolStripSeparator();
            this._dbgContinueButton = new System.Windows.Forms.ToolStripButton();
            this._dbgStopButton = new System.Windows.Forms.ToolStripButton();
            this._dbgStepOverButton = new System.Windows.Forms.ToolStripButton();
            this._dbgStepIntoButton = new System.Windows.Forms.ToolStripButton();
            this._dbgStepOutButton = new System.Windows.Forms.ToolStripButton();
            this._dbgToggleBpButton = new System.Windows.Forms.ToolStripButton();
            this.debugToolbarSeparator = new System.Windows.Forms.ToolStripSeparator();
            this.zoomInButton = new System.Windows.Forms.ToolStripButton();
            this.zoomOutButton = new System.Windows.Forms.ToolStripButton();
            this.zoomFitButton = new System.Windows.Forms.ToolStripButton();
            this._zoomLabel = new System.Windows.Forms.ToolStripDropDownButton();
            this.zoomToolbarSeparator = new System.Windows.Forms.ToolStripSeparator();
            this.welcomePanelButton = new System.Windows.Forms.ToolStripButton();
            this.designerPanelButton = new System.Windows.Forms.ToolStripButton();
            this.editorPanelButton = new System.Windows.Forms.ToolStripButton();
            this.debugPanelButton = new System.Windows.Forms.ToolStripButton();
            this.analysisPanelButton = new System.Windows.Forms.ToolStripButton();
            this.outputPanelButton = new System.Windows.Forms.ToolStripButton();
            this.problemsPanelButton = new System.Windows.Forms.ToolStripButton();
            this.hiddenPanelButton = new System.Windows.Forms.ToolStripButton();
            this._statusStrip = new System.Windows.Forms.StatusStrip();
            this._statusLabel = new System.Windows.Forms.ToolStripStatusLabel();
            this._mainTabControl = new HelixPythonNamespace.UI.Controls.HelixTabControl();
            this._welcomeTab = new System.Windows.Forms.TabPage();
            this._graphicalDesignerTab = new System.Windows.Forms.TabPage();
            this._designerInnerTabControl = new System.Windows.Forms.TabControl();
            this._designerTab = new System.Windows.Forms.TabPage();
            this._interfacesTab = new System.Windows.Forms.TabPage();
            this._abstractClassesTab = new System.Windows.Forms.TabPage();
            this._generalClassesTab = new System.Windows.Forms.TabPage();
            this._analysisResultsTab = new System.Windows.Forms.TabPage();
            this._buildResultsTab = new System.Windows.Forms.TabPage();
            this._problemsTab = new System.Windows.Forms.TabPage();
            this._hiddenElementsTab = new System.Windows.Forms.TabPage();
            this._menuStrip.SuspendLayout();
            this._toolStrip.SuspendLayout();
            this._statusStrip.SuspendLayout();
            this._mainTabControl.SuspendLayout();
            this._graphicalDesignerTab.SuspendLayout();
            this._designerInnerTabControl.SuspendLayout();
            this.SuspendLayout();
            // 
            // _menuStrip
            // 
            this._menuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.fileMenuItem,
            this.editMenuItem,
            this.viewMenuItem,
            this.buildTopMenuItem,
            this.analysisMenuItem,
            this.testsMenuItem,
            this.metricsMenuItem,
            this.profilerMenuItem,
            this.diagramMenuItem,
            this.debugMenuItem,
            this.toolsMenuItem,
            this.gitMenuItem,
            this.helpMenuItem});
            this._menuStrip.Location = new System.Drawing.Point(0, 0);
            this._menuStrip.Name = "_menuStrip";
            this._menuStrip.Size = new System.Drawing.Size(1280, 24);
            this._menuStrip.TabIndex = 3;
            // 
            // fileMenuItem
            // 
            // ONE "New" and ONE "Open". The menu used to carry New Project, New Solution and New
            // Jupyter Notebook as three siblings, and Open Solution beside a notebook route that
            // was somewhere else entirely — so the top of File read as a list of the program's
            // internal categories rather than of the two things a person does when they arrive.
            // What gets created still differs; choosing it is a second step, not a first one.
            this.newMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.newNotebookMenuItem,
            this.newProjectWizardMenuItem,
            this.newSolutionMenuItem});
            this.fileMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.newMenuItem,
            this.openSolutionMenuItem,
            this.importDirectoryMenuItem,
            this.openEditorMenuItem,
            this.closeFileMenuItem,
            this.closeSolutionMenuItem,
            this._recentFilesMenu,
            this.saveFileMenuItem,
            this.saveAllFilesMenuItem,
            this.saveLayoutMenuItem,
            this.exportMenuItem,
            this.exitMenuItem});
            this.fileMenuItem.Name = "fileMenuItem";
            this.fileMenuItem.Size = new System.Drawing.Size(37, 20);
            this.fileMenuItem.Text = "&File";
            //
            // newProjectWizardMenuItem
            //
            // The three children drop the word "New": inside a menu called New, "New Project..."
            // says it twice and buries the noun the eye is actually looking for.
            this.newProjectWizardMenuItem.Name = "newProjectWizardMenuItem";
            this.newProjectWizardMenuItem.Size = new System.Drawing.Size(211, 22);
            this.newProjectWizardMenuItem.Text = "&Project...";
            this.newProjectWizardMenuItem.Click += new System.EventHandler(this.OnNewProjectWizard);
            //
            // newSolutionMenuItem
            //
            this.newSolutionMenuItem.Name = "newSolutionMenuItem";
            this.newSolutionMenuItem.Size = new System.Drawing.Size(211, 22);
            this.newSolutionMenuItem.Text = "&Solution...";
            this.newSolutionMenuItem.Click += new System.EventHandler(this.OnFileNewSolution);
            //
            // newMenuItem — the unified parent
            //
            this.newMenuItem.Name = "newMenuItem";
            this.newMenuItem.Size = new System.Drawing.Size(211, 22);
            this.newMenuItem.Text = "&New";
            //
            // openSolutionMenuItem
            //
            // Field name is historical: this is now the ONE Open command, and it takes a
            // pyproject.toml or an .ipynb and works out which it was handed. Renaming the field
            // would touch tests and the keybinding id `file.open` for no user-visible gain.
            this.openSolutionMenuItem.Name = "openSolutionMenuItem";
            this.openSolutionMenuItem.Size = new System.Drawing.Size(211, 22);
            this.openSolutionMenuItem.Text = "&Open...";
            this.openSolutionMenuItem.Click += new System.EventHandler(this.OnFileOpenAnything);
            // 
            // importDirectoryMenuItem
            // 
            this.importDirectoryMenuItem.Name = "importDirectoryMenuItem";
            this.importDirectoryMenuItem.Size = new System.Drawing.Size(211, 22);
            this.importDirectoryMenuItem.Text = "&Import Python Directory...";
            this.importDirectoryMenuItem.Click += new System.EventHandler(this.OnFileImportDirectory);
            // 
            // openEditorMenuItem
            // 
            this.openEditorMenuItem.Name = "openEditorMenuItem";
            this.openEditorMenuItem.Size = new System.Drawing.Size(211, 22);
            this.openEditorMenuItem.Text = "Open File in &Editor...";
            this.openEditorMenuItem.Click += new System.EventHandler(this.OnFileOpenInEditor);
            //
            // newNotebookMenuItem
            //
            // Creating a notebook must not require a solution. Every other creation command in
            // this menu builds one first, and until this item existed the only route to an .ipynb
            // was the Solution Explorer verb on a project node — so a scratch notebook meant
            // inventing a pyproject.toml to hang it on. Sits next to "Open File in Editor...",
            // which is the matching solution-free way to open one.
            this.newNotebookMenuItem.Name = "newNotebookMenuItem";
            this.newNotebookMenuItem.Size = new System.Drawing.Size(211, 22);
            this.newNotebookMenuItem.Text = "&Jupyter Notebook...";
            this.newNotebookMenuItem.Click += new System.EventHandler(this.OnFileNewNotebook);
            // 
            // closeSolutionMenuItem
            // 
            this.closeSolutionMenuItem.Name = "closeSolutionMenuItem";
            this.closeSolutionMenuItem.Size = new System.Drawing.Size(211, 22);
            this.closeSolutionMenuItem.Text = "&Close Solution";
            this.closeSolutionMenuItem.Click += new System.EventHandler(this.OnFileClose);
            // 
            // _recentFilesMenu
            // 
            this._recentFilesMenu.Name = "_recentFilesMenu";
            this._recentFilesMenu.Size = new System.Drawing.Size(211, 22);
            this._recentFilesMenu.Text = "&Recent Files";
            // 
            // saveFileMenuItem
            //
            this.saveFileMenuItem.Name = "saveFileMenuItem";
            this.saveFileMenuItem.Size = new System.Drawing.Size(211, 22);
            this.saveFileMenuItem.Text = "&Save";
            this.saveFileMenuItem.Click += new System.EventHandler(this.OnFileSave);
            //
            // saveAllFilesMenuItem
            //
            this.saveAllFilesMenuItem.Name = "saveAllFilesMenuItem";
            this.saveAllFilesMenuItem.Size = new System.Drawing.Size(211, 22);
            // Mnemonic is &A, not &L — "Save &Layout" already owns Alt+L in this menu
            // (MenuMnemonicTests guards against the collision).
            this.saveAllFilesMenuItem.Text = "Save &All";
            this.saveAllFilesMenuItem.Click += new System.EventHandler(this.OnFileSaveAll);
            //
            // closeFileMenuItem
            //
            this.closeFileMenuItem.Name = "closeFileMenuItem";
            this.closeFileMenuItem.Size = new System.Drawing.Size(211, 22);
            this.closeFileMenuItem.Text = "Close &File";
            this.closeFileMenuItem.Click += new System.EventHandler(this.OnFileCloseDocument);
            //
            // saveLayoutMenuItem
            //
            this.saveLayoutMenuItem.Name = "saveLayoutMenuItem";
            this.saveLayoutMenuItem.Size = new System.Drawing.Size(211, 22);
            this.saveLayoutMenuItem.Text = "Save &Layout";
            this.saveLayoutMenuItem.Click += new System.EventHandler(this.OnFileSaveLayout);
            // 
            // exportMenuItem
            // 
            this.exportMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.exportCSharpMenuItem,
            this.exportJsonMenuItem,
            this.exportPngMenuItem,
            this.exportSvgMenuItem,
            this.exportPdfMenuItem});
            this.exportMenuItem.Name = "exportMenuItem";
            this.exportMenuItem.Size = new System.Drawing.Size(211, 22);
            this.exportMenuItem.Text = "Expor&t";
            // 
            // exportCSharpMenuItem
            // 
            this.exportCSharpMenuItem.Name = "exportCSharpMenuItem";
            this.exportCSharpMenuItem.Size = new System.Drawing.Size(201, 22);
            this.exportCSharpMenuItem.Text = "Export C# &Skeletons...";
            this.exportCSharpMenuItem.Click += new System.EventHandler(this.OnExportCSharp);
            // 
            // exportJsonMenuItem
            // 
            this.exportJsonMenuItem.Name = "exportJsonMenuItem";
            this.exportJsonMenuItem.Size = new System.Drawing.Size(201, 22);
            this.exportJsonMenuItem.Text = "Export &JSON Metadata...";
            this.exportJsonMenuItem.Click += new System.EventHandler(this.OnExportJson);
            // 
            // exportPngMenuItem
            // 
            this.exportPngMenuItem.Name = "exportPngMenuItem";
            this.exportPngMenuItem.Size = new System.Drawing.Size(201, 22);
            this.exportPngMenuItem.Text = "Export &PNG Image...";
            this.exportPngMenuItem.Click += new System.EventHandler(this.OnExportPng);
            // 
            // exportSvgMenuItem
            // 
            this.exportSvgMenuItem.Name = "exportSvgMenuItem";
            this.exportSvgMenuItem.Size = new System.Drawing.Size(201, 22);
            this.exportSvgMenuItem.Text = "Export S&VG Image...";
            this.exportSvgMenuItem.Click += new System.EventHandler(this.OnExportSvg);
            // 
            // exportPdfMenuItem
            // 
            this.exportPdfMenuItem.Name = "exportPdfMenuItem";
            this.exportPdfMenuItem.Size = new System.Drawing.Size(201, 22);
            this.exportPdfMenuItem.Text = "Export P&DF...";
            this.exportPdfMenuItem.Click += new System.EventHandler(this.OnExportPdf);
            // 
            // exitMenuItem
            // 
            this.exitMenuItem.Name = "exitMenuItem";
            this.exitMenuItem.Size = new System.Drawing.Size(211, 22);
            this.exitMenuItem.Text = "E&xit";
            // Alt+F4 is handled by Windows, not by a ShortcutKeys dispatch — advertise it so the
            // menu still tells the user the chord exists.
            this.exitMenuItem.ShortcutKeyDisplayString = "Alt+F4";
            this.exitMenuItem.Click += new System.EventHandler(this.OnExitClick);
            // 
            // editMenuItem
            // 
            this.editMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.undoMenuItem,
            this.redoMenuItem,
            this.cutMenuItem,
            this.copyMenuItem,
            this.pasteMenuItem,
            this.deleteMenuItem,
            this.findTypeMenuItem,
            this.hideSelectedMenuItem,
            this.showHiddenMenuItem});
            this.editMenuItem.Name = "editMenuItem";
            this.editMenuItem.Size = new System.Drawing.Size(39, 20);
            this.editMenuItem.Text = "&Edit";
            // 
            // undoMenuItem
            // 
            this.undoMenuItem.Name = "undoMenuItem";
            this.undoMenuItem.Size = new System.Drawing.Size(162, 22);
            this.undoMenuItem.Text = "&Undo";
            // Display-only hint (ShortcutKeyDisplayString, not ShortcutKeys): the keys are routed
            // by focus — canvas undo/redo/delete in IdleState, cut/copy/paste in ProcessCmdKey when
            // a designer tab is active — so the editor keeps Scintilla's native Ctrl+Z/X/C/V.
            this.undoMenuItem.ShortcutKeyDisplayString = "Ctrl+Z";
            this.undoMenuItem.Click += new System.EventHandler(this.OnEditUndo);
            // 
            // redoMenuItem
            // 
            this.redoMenuItem.Name = "redoMenuItem";
            this.redoMenuItem.Size = new System.Drawing.Size(162, 22);
            this.redoMenuItem.Text = "&Redo";
            this.redoMenuItem.ShortcutKeyDisplayString = "Ctrl+Y";
            this.redoMenuItem.Click += new System.EventHandler(this.OnEditRedo);
            // 
            // cutMenuItem
            // 
            this.cutMenuItem.Name = "cutMenuItem";
            this.cutMenuItem.Size = new System.Drawing.Size(162, 22);
            this.cutMenuItem.Text = "Cu&t";
            this.cutMenuItem.ShortcutKeyDisplayString = "Ctrl+X";
            this.cutMenuItem.Click += new System.EventHandler(this.OnEditCut);
            // 
            // copyMenuItem
            // 
            this.copyMenuItem.Name = "copyMenuItem";
            this.copyMenuItem.Size = new System.Drawing.Size(162, 22);
            this.copyMenuItem.Text = "&Copy";
            this.copyMenuItem.ShortcutKeyDisplayString = "Ctrl+C";
            this.copyMenuItem.Click += new System.EventHandler(this.OnEditCopy);
            // 
            // pasteMenuItem
            // 
            this.pasteMenuItem.Name = "pasteMenuItem";
            this.pasteMenuItem.Size = new System.Drawing.Size(162, 22);
            this.pasteMenuItem.Text = "&Paste";
            this.pasteMenuItem.ShortcutKeyDisplayString = "Ctrl+V";
            this.pasteMenuItem.Click += new System.EventHandler(this.OnEditPaste);
            // 
            // deleteMenuItem
            // 
            this.deleteMenuItem.Name = "deleteMenuItem";
            this.deleteMenuItem.Size = new System.Drawing.Size(162, 22);
            this.deleteMenuItem.Text = "&Delete";
            this.deleteMenuItem.ShortcutKeyDisplayString = "Del";
            this.deleteMenuItem.Click += new System.EventHandler(this.OnDeleteSelection);
            // 
            // findTypeMenuItem
            // 
            this.findTypeMenuItem.Name = "findTypeMenuItem";
            this.findTypeMenuItem.Size = new System.Drawing.Size(162, 22);
            this.findTypeMenuItem.Text = "&Find Type...";
            // Display-only hint like the other Edit items: Ctrl+F is routed by focus (editor find bar
            // vs. global type finder) via ProcessCmdKey, not dispatched from a stamped ShortcutKeys.
            this.findTypeMenuItem.ShortcutKeyDisplayString = "Ctrl+F";
            this.findTypeMenuItem.Click += new System.EventHandler(this.OnFindType);
            // 
            // hideSelectedMenuItem
            // 
            this.hideSelectedMenuItem.Name = "hideSelectedMenuItem";
            this.hideSelectedMenuItem.Size = new System.Drawing.Size(162, 22);
            this.hideSelectedMenuItem.Text = "&Hide Selected";
            this.hideSelectedMenuItem.Click += new System.EventHandler(this.OnHideSelected);
            // 
            // showHiddenMenuItem
            // 
            this.showHiddenMenuItem.Name = "showHiddenMenuItem";
            this.showHiddenMenuItem.Size = new System.Drawing.Size(162, 22);
            this.showHiddenMenuItem.Text = "&Show All Hidden";
            this.showHiddenMenuItem.Click += new System.EventHandler(this.OnShowAllHidden);
            // 
            // viewMenuItem
            // 
            this.viewMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.dockLeftMenuItem,
            this.dockRightMenuItem,
            this.floatExplorerMenuItem,
            this.undockAnalysisMenuItem,
            this.undockOutputMenuItem,
            this._toolsPaletteMenuItem,
            this._minimapMenuItem,
            this._snapToGridMenuItem,
            this.refreshCanvasMenuItem,
            this.copyImageMenuItem,
            this.panelsMenuItem,
            this._floatTreeMenuItem});
            this.viewMenuItem.Name = "viewMenuItem";
            this.viewMenuItem.Size = new System.Drawing.Size(44, 20);
            this.viewMenuItem.Text = "&View";
            // 
            // dockLeftMenuItem
            // 
            this.dockLeftMenuItem.Name = "dockLeftMenuItem";
            this.dockLeftMenuItem.Size = new System.Drawing.Size(201, 22);
            this.dockLeftMenuItem.Text = "Dock Explorer &Left";
            this.dockLeftMenuItem.Click += new System.EventHandler(this.OnDockExplorerLeft);
            // 
            // dockRightMenuItem
            // 
            this.dockRightMenuItem.Name = "dockRightMenuItem";
            this.dockRightMenuItem.Size = new System.Drawing.Size(201, 22);
            this.dockRightMenuItem.Text = "Dock Explorer &Right";
            this.dockRightMenuItem.Click += new System.EventHandler(this.OnDockExplorerRight);
            // 
            // floatExplorerMenuItem
            // 
            this.floatExplorerMenuItem.Name = "floatExplorerMenuItem";
            this.floatExplorerMenuItem.Size = new System.Drawing.Size(201, 22);
            this.floatExplorerMenuItem.Text = "&Float Explorer";
            this.floatExplorerMenuItem.Click += new System.EventHandler(this.OnFloatExplorer);
            // 
            // undockAnalysisMenuItem
            // 
            this.undockAnalysisMenuItem.Name = "undockAnalysisMenuItem";
            this.undockAnalysisMenuItem.Size = new System.Drawing.Size(201, 22);
            this.undockAnalysisMenuItem.Text = "Undock &Analysis Results";
            this.undockAnalysisMenuItem.Click += new System.EventHandler(this.OnToggleAnalysisResultsDocking);
            // 
            // undockOutputMenuItem
            // 
            this.undockOutputMenuItem.Name = "undockOutputMenuItem";
            this.undockOutputMenuItem.Size = new System.Drawing.Size(201, 22);
            this.undockOutputMenuItem.Text = "Undock &Output";
            this.undockOutputMenuItem.Click += new System.EventHandler(this.OnToggleBuildResultsDocking);
            // 
            // _toolsPaletteMenuItem
            // 
            this._toolsPaletteMenuItem.Name = "_toolsPaletteMenuItem";
            this._toolsPaletteMenuItem.Size = new System.Drawing.Size(201, 22);
            this._toolsPaletteMenuItem.Text = "&Tools Palette";
            this._toolsPaletteMenuItem.Click += new System.EventHandler(this.OnToggleToolsPalette);
            // 
            // _minimapMenuItem
            // 
            this._minimapMenuItem.Name = "_minimapMenuItem";
            this._minimapMenuItem.Size = new System.Drawing.Size(201, 22);
            this._minimapMenuItem.Text = "&Minimap";
            this._minimapMenuItem.Click += new System.EventHandler(this.OnToggleMinimap);
            // 
            // _snapToGridMenuItem
            // 
            this._snapToGridMenuItem.Checked = true;
            this._snapToGridMenuItem.CheckState = System.Windows.Forms.CheckState.Checked;
            this._snapToGridMenuItem.Name = "_snapToGridMenuItem";
            this._snapToGridMenuItem.Size = new System.Drawing.Size(201, 22);
            this._snapToGridMenuItem.Text = "Snap to &Grid";
            this._snapToGridMenuItem.Click += new System.EventHandler(this.OnToggleSnapToGrid);
            // 
            // refreshCanvasMenuItem
            // 
            this.refreshCanvasMenuItem.Name = "refreshCanvasMenuItem";
            this.refreshCanvasMenuItem.Size = new System.Drawing.Size(201, 22);
            this.refreshCanvasMenuItem.Text = "Refresh &Canvas";
            this.refreshCanvasMenuItem.Click += new System.EventHandler(this.OnRefreshCanvas);
            // 
            // copyImageMenuItem
            // 
            this.copyImageMenuItem.Name = "copyImageMenuItem";
            this.copyImageMenuItem.Size = new System.Drawing.Size(201, 22);
            this.copyImageMenuItem.Text = "Copy Diagram as &Image";
            this.copyImageMenuItem.Click += new System.EventHandler(this.OnCopyDiagramImage);
            // 
            // panelsMenuItem
            // 
            this.panelsMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.welcomePanelMenuItem,
            this.designerPanelMenuItem,
            this.editorPanelMenuItem,
            this.debugPanelMenuItem,
            this.analysisPanelMenuItem,
            this.outputPanelMenuItem,
            this.problemsPanelMenuItem,
            this.hiddenElementsPanelMenuItem,
            this.lintPanelMenuItem,
            this.deadCodePanelMenuItem,
            this.metricsPanelMenuItem,
            this.profilerPanelMenuItem,
            this.testsPanelMenuItem,
            this.referencesPanelMenuItem,
            this.callHierarchyPanelMenuItem,
            this.terminalMenuItem});
            this.panelsMenuItem.Name = "panelsMenuItem";
            this.panelsMenuItem.Size = new System.Drawing.Size(201, 22);
            this.panelsMenuItem.Text = "&Panels";
            // 
            // welcomePanelMenuItem
            // 
            this.welcomePanelMenuItem.Name = "welcomePanelMenuItem";
            this.welcomePanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.welcomePanelMenuItem.Tag = "welcome";
            this.welcomePanelMenuItem.Text = "&Welcome Page";
            this.welcomePanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // designerPanelMenuItem
            // 
            this.designerPanelMenuItem.Name = "designerPanelMenuItem";
            this.designerPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.designerPanelMenuItem.Tag = "designer";
            this.designerPanelMenuItem.Text = "Class Desi&gner Canvas";
            this.designerPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // editorPanelMenuItem
            // 
            this.editorPanelMenuItem.Name = "editorPanelMenuItem";
            this.editorPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.editorPanelMenuItem.Tag = "editor";
            this.editorPanelMenuItem.Text = "&Editor";
            this.editorPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // debugPanelMenuItem
            // 
            this.debugPanelMenuItem.Name = "debugPanelMenuItem";
            this.debugPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.debugPanelMenuItem.Tag = "debug";
            this.debugPanelMenuItem.Text = "&Debug";
            this.debugPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // analysisPanelMenuItem
            // 
            this.analysisPanelMenuItem.Name = "analysisPanelMenuItem";
            this.analysisPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.analysisPanelMenuItem.Tag = "analysis";
            this.analysisPanelMenuItem.Text = "&Analysis Results";
            this.analysisPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // outputPanelMenuItem
            // 
            this.outputPanelMenuItem.Name = "outputPanelMenuItem";
            this.outputPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.outputPanelMenuItem.Tag = "buildResults";
            this.outputPanelMenuItem.Text = "&Output";
            this.outputPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // problemsPanelMenuItem
            // 
            this.problemsPanelMenuItem.Name = "problemsPanelMenuItem";
            this.problemsPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.problemsPanelMenuItem.Tag = "problems";
            this.problemsPanelMenuItem.Text = "Com&pile";
            this.problemsPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // hiddenElementsPanelMenuItem
            // 
            this.hiddenElementsPanelMenuItem.Name = "hiddenElementsPanelMenuItem";
            this.hiddenElementsPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.hiddenElementsPanelMenuItem.Tag = "hiddenElements";
            this.hiddenElementsPanelMenuItem.Text = "&Hidden Elements";
            this.hiddenElementsPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // lintPanelMenuItem
            // 
            this.lintPanelMenuItem.Name = "lintPanelMenuItem";
            this.lintPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.lintPanelMenuItem.Tag = "lint";
            this.lintPanelMenuItem.Text = "&Lint";
            this.lintPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // deadCodePanelMenuItem
            // 
            this.deadCodePanelMenuItem.Name = "deadCodePanelMenuItem";
            this.deadCodePanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.deadCodePanelMenuItem.Tag = "deadcode";
            this.deadCodePanelMenuItem.Text = "Dead &Code";
            this.deadCodePanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // metricsPanelMenuItem
            // 
            this.metricsPanelMenuItem.Name = "metricsPanelMenuItem";
            this.metricsPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.metricsPanelMenuItem.Tag = "metrics";
            this.metricsPanelMenuItem.Text = "&Metrics";
            this.metricsPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // profilerPanelMenuItem
            // 
            this.profilerPanelMenuItem.Name = "profilerPanelMenuItem";
            this.profilerPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.profilerPanelMenuItem.Tag = "profile";
            this.profilerPanelMenuItem.Text = "Pro&filer";
            this.profilerPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // testsPanelMenuItem
            // 
            this.testsPanelMenuItem.Name = "testsPanelMenuItem";
            this.testsPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.testsPanelMenuItem.Tag = "tests";
            this.testsPanelMenuItem.Text = "&Tests";
            this.testsPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            //
            // terminalMenuItem
            //
            this.terminalMenuItem.Name = "terminalMenuItem";
            this.terminalMenuItem.Size = new System.Drawing.Size(214, 22);
            this.terminalMenuItem.Text = "Terminal";
            this.terminalMenuItem.Click += new System.EventHandler(this.OnOpenTerminal);
            //
            // referencesPanelMenuItem
            //
            this.referencesPanelMenuItem.Name = "referencesPanelMenuItem";
            this.referencesPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.referencesPanelMenuItem.Tag = "references";
            this.referencesPanelMenuItem.Text = "Reference&s";
            this.referencesPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            //
            // callHierarchyPanelMenuItem
            //
            this.callHierarchyPanelMenuItem.Name = "callHierarchyPanelMenuItem";
            this.callHierarchyPanelMenuItem.Size = new System.Drawing.Size(214, 22);
            this.callHierarchyPanelMenuItem.Tag = "callHierarchy";
            this.callHierarchyPanelMenuItem.Text = "Call Hierarch&y";
            this.callHierarchyPanelMenuItem.Click += new System.EventHandler(this.OnPanelMenuItemClick);
            // 
            // _floatTreeMenuItem
            // 
            this._floatTreeMenuItem.Name = "_floatTreeMenuItem";
            this._floatTreeMenuItem.Size = new System.Drawing.Size(201, 22);
            this._floatTreeMenuItem.Text = "Float &Navigation Tree";
            this._floatTreeMenuItem.Click += new System.EventHandler(this.OnToggleFloatNavigationTree);
            // 
            // buildTopMenuItem
            // 
            this.buildTopMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._runStartupMenuItem,
            this._stopExecutionMenuItem,
            this._runInTerminalMenuItem,
            this._buildMenuItem,
            this.compileMenuItem,
            this.buildViaCMenuItem,
            this.selectInterpreterMenuItem,
            this._buildSettingsMenuItem});
            this.buildTopMenuItem.Name = "buildTopMenuItem";
            this.buildTopMenuItem.Size = new System.Drawing.Size(46, 20);
            this.buildTopMenuItem.Text = "&Build";
            //
            // _runStartupMenuItem
            //
            this._runStartupMenuItem.Name = "_runStartupMenuItem";
            this._runStartupMenuItem.Size = new System.Drawing.Size(213, 22);
            this._runStartupMenuItem.Text = "&Run Startup App";
            this._runStartupMenuItem.Click += new System.EventHandler(this.OnRunStartupApp);
            //
            // _stopExecutionMenuItem
            //
            this._stopExecutionMenuItem.Name = "_stopExecutionMenuItem";
            this._stopExecutionMenuItem.Size = new System.Drawing.Size(213, 22);
            this._stopExecutionMenuItem.Text = "&Stop Execution";
            this._stopExecutionMenuItem.Enabled = false;
            //
            // _runInTerminalMenuItem
            //
            this._runInTerminalMenuItem.Name = "_runInTerminalMenuItem";
            this._runInTerminalMenuItem.Size = new System.Drawing.Size(213, 22);
            this._runInTerminalMenuItem.Text = "Run Startup App in &Terminal";
            //
            // _buildMenuItem
            // 
            this._buildMenuItem.Name = "_buildMenuItem";
            this._buildMenuItem.Size = new System.Drawing.Size(213, 22);
            this._buildMenuItem.Text = "&Packager";
            this._buildMenuItem.Click += new System.EventHandler(this.OnBuildSolution);
            // 
            // compileMenuItem
            // 
            this.compileMenuItem.Name = "compileMenuItem";
            this.compileMenuItem.Size = new System.Drawing.Size(213, 22);
            this.compileMenuItem.Text = "&Compile";
            this.compileMenuItem.Click += new System.EventHandler(this.OnCompile);
            // 
            // buildViaCMenuItem
            // 
            this.buildViaCMenuItem.Name = "buildViaCMenuItem";
            this.buildViaCMenuItem.Size = new System.Drawing.Size(213, 22);
            this.buildViaCMenuItem.Text = "Build via C++";
            this.buildViaCMenuItem.Click += new System.EventHandler(this.OnBuildViaCpp);
            // 
            // selectInterpreterMenuItem
            // 
            this.selectInterpreterMenuItem.Name = "selectInterpreterMenuItem";
            this.selectInterpreterMenuItem.Size = new System.Drawing.Size(213, 22);
            this.selectInterpreterMenuItem.Text = "Select Python &Interpreter...";
            this.selectInterpreterMenuItem.Click += new System.EventHandler(this.OnSelectPythonInterpreter);
            // 
            // _buildSettingsMenuItem
            // 
            this._buildSettingsMenuItem.Name = "_buildSettingsMenuItem";
            this._buildSettingsMenuItem.Size = new System.Drawing.Size(213, 22);
            this._buildSettingsMenuItem.Text = "Packager Settin&gs...";
            this._buildSettingsMenuItem.Click += new System.EventHandler(this.OnBuildToolSettings);
            // 
            // analysisMenuItem
            // 
            this.analysisMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.runAnalysisMenuItem,
            this.lintMenuItem,
            this.deadCodeMenuItem});
            this.analysisMenuItem.Name = "analysisMenuItem";
            this.analysisMenuItem.Size = new System.Drawing.Size(62, 20);
            this.analysisMenuItem.Text = "&Analysis";
            // 
            // runAnalysisMenuItem
            // 
            this.runAnalysisMenuItem.Name = "runAnalysisMenuItem";
            this.runAnalysisMenuItem.Size = new System.Drawing.Size(179, 22);
            this.runAnalysisMenuItem.Text = "&Run Analysis";
            this.runAnalysisMenuItem.Click += new System.EventHandler(this.OnRunAnalysis);
            // 
            // lintMenuItem
            // 
            this.lintMenuItem.Name = "lintMenuItem";
            this.lintMenuItem.Size = new System.Drawing.Size(179, 22);
            this.lintMenuItem.Text = "&Lint Python Sources";
            this.lintMenuItem.Click += new System.EventHandler(this.OnRunLint);
            // 
            // deadCodeMenuItem
            // 
            this.deadCodeMenuItem.Name = "deadCodeMenuItem";
            this.deadCodeMenuItem.Size = new System.Drawing.Size(179, 22);
            this.deadCodeMenuItem.Text = "Find &Dead Code";
            this.deadCodeMenuItem.Click += new System.EventHandler(this.OnFindDeadCode);
            // 
            // testsMenuItem
            // 
            this.testsMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.runAllTestsMenuItem,
            this.runFailedTestsMenuItem,
            this.stopTestsMenuItem,
            this.clearTestsMenuItem});
            this.testsMenuItem.Name = "testsMenuItem";
            this.testsMenuItem.Size = new System.Drawing.Size(44, 20);
            this.testsMenuItem.Text = "&Tests";
            // 
            // runAllTestsMenuItem
            // 
            this.runAllTestsMenuItem.Name = "runAllTestsMenuItem";
            this.runAllTestsMenuItem.Size = new System.Drawing.Size(185, 22);
            this.runAllTestsMenuItem.Text = "Run &All Tests";
            this.runAllTestsMenuItem.Click += new System.EventHandler(this.OnRunAllTests);
            // 
            // runFailedTestsMenuItem
            // 
            this.runFailedTestsMenuItem.Name = "runFailedTestsMenuItem";
            this.runFailedTestsMenuItem.Size = new System.Drawing.Size(185, 22);
            this.runFailedTestsMenuItem.Text = "Run &Failed Tests Only";
            this.runFailedTestsMenuItem.Click += new System.EventHandler(this.OnRunFailedTests);
            // 
            // stopTestsMenuItem
            // 
            this.stopTestsMenuItem.Name = "stopTestsMenuItem";
            this.stopTestsMenuItem.Size = new System.Drawing.Size(185, 22);
            this.stopTestsMenuItem.Text = "&Stop";
            this.stopTestsMenuItem.Click += new System.EventHandler(this.OnStopTests);
            // 
            // clearTestsMenuItem
            // 
            this.clearTestsMenuItem.Name = "clearTestsMenuItem";
            this.clearTestsMenuItem.Size = new System.Drawing.Size(185, 22);
            this.clearTestsMenuItem.Text = "&Clear Results";
            this.clearTestsMenuItem.Click += new System.EventHandler(this.OnClearTests);
            // 
            // metricsMenuItem
            // 
            this.metricsMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.computeMetricsMenuItem});
            this.metricsMenuItem.Name = "metricsMenuItem";
            this.metricsMenuItem.Size = new System.Drawing.Size(58, 20);
            this.metricsMenuItem.Text = "&Metrics";
            // 
            // computeMetricsMenuItem
            // 
            this.computeMetricsMenuItem.Name = "computeMetricsMenuItem";
            this.computeMetricsMenuItem.Size = new System.Drawing.Size(166, 22);
            this.computeMetricsMenuItem.Text = "Compute &Metrics";
            this.computeMetricsMenuItem.Click += new System.EventHandler(this.OnComputeMetrics);
            // 
            // profilerMenuItem
            // 
            this.profilerMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.profileFileMenuItem});
            this.profilerMenuItem.Name = "profilerMenuItem";
            this.profilerMenuItem.Size = new System.Drawing.Size(57, 20);
            this.profilerMenuItem.Text = "&Profiler";
            // 
            // profileFileMenuItem
            // 
            this.profileFileMenuItem.Name = "profileFileMenuItem";
            this.profileFileMenuItem.Size = new System.Drawing.Size(165, 22);
            this.profileFileMenuItem.Text = "&Profile Active File";
            this.profileFileMenuItem.Click += new System.EventHandler(this.OnProfileActiveFile);
            // 
            // diagramMenuItem
            // 
            this.diagramMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.addInterfaceMenuItem,
            this.addAbstractMenuItem,
            this.addConcreteMenuItem,
            this.autoLayoutMenuItem,
            this.zoomFitMenuItem,
            this.gridSettingsMenuItem,
            this.linkSelectedMenuItem});
            this.diagramMenuItem.Name = "diagramMenuItem";
            this.diagramMenuItem.Size = new System.Drawing.Size(64, 20);
            this.diagramMenuItem.Text = "&Diagram";
            // 
            // addInterfaceMenuItem
            // 
            this.addInterfaceMenuItem.Name = "addInterfaceMenuItem";
            this.addInterfaceMenuItem.Size = new System.Drawing.Size(177, 22);
            this.addInterfaceMenuItem.Text = "Add &Interface";
            this.addInterfaceMenuItem.Click += new System.EventHandler(this.OnAddInterfaceClick);
            // 
            // addAbstractMenuItem
            // 
            this.addAbstractMenuItem.Name = "addAbstractMenuItem";
            this.addAbstractMenuItem.Size = new System.Drawing.Size(177, 22);
            this.addAbstractMenuItem.Text = "Add &Abstract Class";
            this.addAbstractMenuItem.Click += new System.EventHandler(this.OnAddAbstractClassClick);
            // 
            // addConcreteMenuItem
            // 
            this.addConcreteMenuItem.Name = "addConcreteMenuItem";
            this.addConcreteMenuItem.Size = new System.Drawing.Size(177, 22);
            this.addConcreteMenuItem.Text = "Add &Concrete Class";
            this.addConcreteMenuItem.Click += new System.EventHandler(this.OnAddConcreteClassClick);
            // 
            // autoLayoutMenuItem
            // 
            this.autoLayoutMenuItem.Name = "autoLayoutMenuItem";
            this.autoLayoutMenuItem.Size = new System.Drawing.Size(177, 22);
            this.autoLayoutMenuItem.Text = "Auto &Layout";
            this.autoLayoutMenuItem.Click += new System.EventHandler(this.OnAutoLayout);
            // 
            // zoomFitMenuItem
            // 
            this.zoomFitMenuItem.Name = "zoomFitMenuItem";
            this.zoomFitMenuItem.Size = new System.Drawing.Size(177, 22);
            this.zoomFitMenuItem.Text = "&Zoom to Fit";
            this.zoomFitMenuItem.Click += new System.EventHandler(this.OnZoomToFit);
            // 
            // gridSettingsMenuItem
            // 
            this.gridSettingsMenuItem.Name = "gridSettingsMenuItem";
            this.gridSettingsMenuItem.Size = new System.Drawing.Size(177, 22);
            this.gridSettingsMenuItem.Text = "&Grid Settings...";
            this.gridSettingsMenuItem.Click += new System.EventHandler(this.OnGridSettings);
            // 
            // linkSelectedMenuItem
            // 
            this.linkSelectedMenuItem.Name = "linkSelectedMenuItem";
            this.linkSelectedMenuItem.Size = new System.Drawing.Size(177, 22);
            this.linkSelectedMenuItem.Text = "Link &Selected...";
            this.linkSelectedMenuItem.Click += new System.EventHandler(this.OnLinkSelected);
            // 
            // debugMenuItem
            // 
            this.debugMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.debugContinueMenuItem,
            this.debugStopMenuItem,
            this.debugStepOverMenuItem,
            this.debugStepIntoMenuItem,
            this.debugStepOutMenuItem,
            this.debugBreakpointMenuItem});
            this.debugMenuItem.Name = "debugMenuItem";
            this.debugMenuItem.Size = new System.Drawing.Size(54, 20);
            this.debugMenuItem.Text = "Deb&ug";
            // 
            // debugContinueMenuItem
            // 
            this.debugContinueMenuItem.Name = "debugContinueMenuItem";
            this.debugContinueMenuItem.Size = new System.Drawing.Size(169, 22);
            this.debugContinueMenuItem.Text = "&Start / Continue";
            this.debugContinueMenuItem.Click += new System.EventHandler(this.OnDebugContinue);
            // 
            // debugStopMenuItem
            // 
            this.debugStopMenuItem.Name = "debugStopMenuItem";
            this.debugStopMenuItem.Size = new System.Drawing.Size(169, 22);
            this.debugStopMenuItem.Text = "S&top";
            this.debugStopMenuItem.Click += new System.EventHandler(this.OnDebugStop);
            // 
            // debugStepOverMenuItem
            // 
            this.debugStepOverMenuItem.Name = "debugStepOverMenuItem";
            this.debugStepOverMenuItem.Size = new System.Drawing.Size(169, 22);
            this.debugStepOverMenuItem.Text = "Step &Over";
            this.debugStepOverMenuItem.Click += new System.EventHandler(this.OnDebugStepOver);
            // 
            // debugStepIntoMenuItem
            // 
            this.debugStepIntoMenuItem.Name = "debugStepIntoMenuItem";
            this.debugStepIntoMenuItem.Size = new System.Drawing.Size(169, 22);
            this.debugStepIntoMenuItem.Text = "Step &Into";
            this.debugStepIntoMenuItem.Click += new System.EventHandler(this.OnDebugStepInto);
            // 
            // debugStepOutMenuItem
            // 
            this.debugStepOutMenuItem.Name = "debugStepOutMenuItem";
            this.debugStepOutMenuItem.Size = new System.Drawing.Size(169, 22);
            this.debugStepOutMenuItem.Text = "Step O&ut";
            this.debugStepOutMenuItem.Click += new System.EventHandler(this.OnDebugStepOut);
            // 
            // debugBreakpointMenuItem
            // 
            this.debugBreakpointMenuItem.Name = "debugBreakpointMenuItem";
            this.debugBreakpointMenuItem.Size = new System.Drawing.Size(169, 22);
            this.debugBreakpointMenuItem.Text = "Toggle &Breakpoint";
            this.debugBreakpointMenuItem.Click += new System.EventHandler(this.OnDebugToggleBreakpoint);
            // 
            // toolsMenuItem
            // 
            this.toolsMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.preferencesMenuItem,
            this.keybindingsMenuItem});
            this.toolsMenuItem.Name = "toolsMenuItem";
            this.toolsMenuItem.Size = new System.Drawing.Size(46, 20);
            this.toolsMenuItem.Text = "T&ools";
            // 
            // preferencesMenuItem
            // 
            this.preferencesMenuItem.Name = "preferencesMenuItem";
            this.preferencesMenuItem.Size = new System.Drawing.Size(148, 22);
            this.preferencesMenuItem.Text = "&Preferences...";
            this.preferencesMenuItem.Click += new System.EventHandler(this.OnShowPreferences);
            // 
            // keybindingsMenuItem
            // 
            this.keybindingsMenuItem.Name = "keybindingsMenuItem";
            this.keybindingsMenuItem.Size = new System.Drawing.Size(148, 22);
            this.keybindingsMenuItem.Text = "&Keybindings...";
            this.keybindingsMenuItem.Click += new System.EventHandler(this.OnShowKeybindings);
            // 
            // gitMenuItem
            // 
            this.gitMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.gitIgnoreMenuItem,
            this.persistRepoMenuItem});
            this.gitMenuItem.Name = "gitMenuItem";
            this.gitMenuItem.Size = new System.Drawing.Size(34, 20);
            this.gitMenuItem.Text = "&Git";
            // 
            // gitIgnoreMenuItem
            // 
            this.gitIgnoreMenuItem.Name = "gitIgnoreMenuItem";
            this.gitIgnoreMenuItem.Size = new System.Drawing.Size(180, 22);
            this.gitIgnoreMenuItem.Text = "Manage &.gitignore...";
            this.gitIgnoreMenuItem.Click += new System.EventHandler(this.OnManageGitIgnore);
            // 
            // persistRepoMenuItem
            // 
            this.persistRepoMenuItem.Name = "persistRepoMenuItem";
            this.persistRepoMenuItem.Size = new System.Drawing.Size(180, 22);
            this.persistRepoMenuItem.Text = "&Commit All...";
            this.persistRepoMenuItem.Click += new System.EventHandler(this.OnPersistRepo);
            // 
            // helpMenuItem
            // 
            this.helpMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.welcomeMenuItem,
            this.aboutMenuItem});
            this.helpMenuItem.Name = "helpMenuItem";
            this.helpMenuItem.Size = new System.Drawing.Size(44, 20);
            this.helpMenuItem.Text = "&Help";
            // 
            // welcomeMenuItem
            // 
            this.welcomeMenuItem.Name = "welcomeMenuItem";
            this.welcomeMenuItem.Size = new System.Drawing.Size(177, 22);
            this.welcomeMenuItem.Text = "&Welcome Page";
            this.welcomeMenuItem.Click += new System.EventHandler(this.OnWelcomeMenuClick);
            // 
            // aboutMenuItem
            // 
            this.aboutMenuItem.Name = "aboutMenuItem";
            this.aboutMenuItem.Size = new System.Drawing.Size(177, 22);
            this.aboutMenuItem.Text = "&About " + MainForm.ProductName + "...";
            this.aboutMenuItem.Click += new System.EventHandler(this.OnShowAbout);
            // 
            // _toolStrip
            // 
            this._toolStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.newButton,
            this.openButton,
            this.importButton,
            this.saveButton,
            this.refreshButton,
            this.fileToolbarSeparator,
            this.interfaceButton,
            this.abstractButton,
            this.concreteButton,
            this._linkButton,
            this.diagramToolbarSeparator,
            this._runButton,
            this._buildButton,
            this.analyzeButton,
            this.compileButton,
            this.buildToolbarSeparator,
            this._dbgContinueButton,
            this._dbgStopButton,
            this._dbgStepOverButton,
            this._dbgStepIntoButton,
            this._dbgStepOutButton,
            this._dbgToggleBpButton,
            this.debugToolbarSeparator,
            this.zoomInButton,
            this.zoomOutButton,
            this.zoomFitButton,
            this._zoomLabel,
            this.zoomToolbarSeparator,
            this.welcomePanelButton,
            this.designerPanelButton,
            this.editorPanelButton,
            this.debugPanelButton,
            this.analysisPanelButton,
            this.outputPanelButton,
            this.problemsPanelButton,
            this.hiddenPanelButton});
            this._toolStrip.Location = new System.Drawing.Point(0, 24);
            this._toolStrip.Name = "_toolStrip";
            this._toolStrip.Size = new System.Drawing.Size(1280, 25);
            this._toolStrip.TabIndex = 2;
            // 
            // newButton
            // 
            this.newButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.newButton.Image = ((System.Drawing.Image)(resources.GetObject("newButton.Image")));
            this.newButton.Name = "newButton";
            this.newButton.Size = new System.Drawing.Size(23, 22);
            this.newButton.Text = "New Project";
            this.newButton.ToolTipText = "New Project (Ctrl+N)";
            this.newButton.Click += new System.EventHandler(this.OnFileNew);
            // 
            // openButton
            // 
            this.openButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.openButton.Image = ((System.Drawing.Image)(resources.GetObject("openButton.Image")));
            this.openButton.Name = "openButton";
            this.openButton.Size = new System.Drawing.Size(23, 22);
            this.openButton.Text = "Open Solution";
            this.openButton.ToolTipText = "Open a notebook or a solution (Ctrl+O)";
            this.openButton.Click += new System.EventHandler(this.OnFileOpen);
            // 
            // importButton
            // 
            this.importButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.importButton.Image = ((System.Drawing.Image)(resources.GetObject("importButton.Image")));
            this.importButton.Name = "importButton";
            this.importButton.Size = new System.Drawing.Size(23, 22);
            this.importButton.Text = "Import Directory";
            this.importButton.ToolTipText = "Import Python Directory (Ctrl+I)";
            this.importButton.Click += new System.EventHandler(this.OnFileImportDirectory);
            // 
            // saveButton
            // 
            this.saveButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.saveButton.Image = ((System.Drawing.Image)(resources.GetObject("saveButton.Image")));
            this.saveButton.Name = "saveButton";
            this.saveButton.Size = new System.Drawing.Size(23, 22);
            this.saveButton.Text = "Save Layout";
            this.saveButton.ToolTipText = "Save Layout (Ctrl+Shift+S)";
            this.saveButton.Click += new System.EventHandler(this.OnFileSaveLayout);
            // 
            // refreshButton
            // 
            this.refreshButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.refreshButton.Image = ((System.Drawing.Image)(resources.GetObject("refreshButton.Image")));
            this.refreshButton.Name = "refreshButton";
            this.refreshButton.Size = new System.Drawing.Size(23, 22);
            this.refreshButton.Text = "Refresh";
            this.refreshButton.ToolTipText = "Refresh Canvas";
            this.refreshButton.Click += new System.EventHandler(this.OnRefreshCanvas);
            // 
            // fileToolbarSeparator
            // 
            this.fileToolbarSeparator.Name = "fileToolbarSeparator";
            this.fileToolbarSeparator.Size = new System.Drawing.Size(6, 25);
            // 
            // interfaceButton
            // 
            this.interfaceButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.interfaceButton.Image = ((System.Drawing.Image)(resources.GetObject("interfaceButton.Image")));
            this.interfaceButton.Name = "interfaceButton";
            this.interfaceButton.Size = new System.Drawing.Size(23, 22);
            this.interfaceButton.Text = "Interface";
            this.interfaceButton.ToolTipText = "Add Interface";
            this.interfaceButton.Click += new System.EventHandler(this.OnAddInterfaceClick);
            // 
            // abstractButton
            // 
            this.abstractButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.abstractButton.Image = ((System.Drawing.Image)(resources.GetObject("abstractButton.Image")));
            this.abstractButton.Name = "abstractButton";
            this.abstractButton.Size = new System.Drawing.Size(23, 22);
            this.abstractButton.Text = "Abstract Class";
            this.abstractButton.ToolTipText = "Add Abstract Class";
            this.abstractButton.Click += new System.EventHandler(this.OnAddAbstractClassClick);
            // 
            // concreteButton
            // 
            this.concreteButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.concreteButton.Image = ((System.Drawing.Image)(resources.GetObject("concreteButton.Image")));
            this.concreteButton.Name = "concreteButton";
            this.concreteButton.Size = new System.Drawing.Size(23, 22);
            this.concreteButton.Text = "Concrete Class";
            this.concreteButton.ToolTipText = "Add Concrete Class";
            this.concreteButton.Click += new System.EventHandler(this.OnAddConcreteClassClick);
            // 
            // _linkButton
            // 
            this._linkButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this._linkButton.Image = ((System.Drawing.Image)(resources.GetObject("_linkButton.Image")));
            this._linkButton.Name = "_linkButton";
            this._linkButton.Size = new System.Drawing.Size(23, 22);
            this._linkButton.Text = "Link";
            this._linkButton.ToolTipText = "Link Selected Types";
            this._linkButton.Click += new System.EventHandler(this.OnLinkSelected);
            // 
            // diagramToolbarSeparator
            // 
            this.diagramToolbarSeparator.Name = "diagramToolbarSeparator";
            this.diagramToolbarSeparator.Size = new System.Drawing.Size(6, 25);
            // 
            // _runButton
            // 
            this._runButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this._runButton.Image = ((System.Drawing.Image)(resources.GetObject("_runButton.Image")));
            this._runButton.Name = "_runButton";
            this._runButton.Size = new System.Drawing.Size(23, 22);
            this._runButton.Text = "Run";
            this._runButton.ToolTipText = "Run Startup App (Ctrl+F5)";
            this._runButton.Click += new System.EventHandler(this.OnRunStartupApp);
            // 
            // _buildButton
            // 
            this._buildButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this._buildButton.Image = ((System.Drawing.Image)(resources.GetObject("_buildButton.Image")));
            this._buildButton.Name = "_buildButton";
            this._buildButton.Size = new System.Drawing.Size(23, 22);
            this._buildButton.Text = "Packager";
            this._buildButton.ToolTipText = "Packager (Ctrl+Shift+B)";
            this._buildButton.Click += new System.EventHandler(this.OnBuildSolution);
            // 
            // analyzeButton
            // 
            this.analyzeButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.analyzeButton.Image = ((System.Drawing.Image)(resources.GetObject("analyzeButton.Image")));
            this.analyzeButton.Name = "analyzeButton";
            this.analyzeButton.Size = new System.Drawing.Size(23, 22);
            this.analyzeButton.Text = "Analyze";
            this.analyzeButton.ToolTipText = "Run Analysis";
            this.analyzeButton.Click += new System.EventHandler(this.OnRunAnalysis);
            // 
            // compileButton
            // 
            this.compileButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.compileButton.Image = ((System.Drawing.Image)(resources.GetObject("compileButton.Image")));
            this.compileButton.Name = "compileButton";
            this.compileButton.Size = new System.Drawing.Size(23, 22);
            this.compileButton.Text = "Compile";
            this.compileButton.ToolTipText = "Compile (F6)";
            this.compileButton.Click += new System.EventHandler(this.OnCompile);
            // 
            // buildToolbarSeparator
            // 
            this.buildToolbarSeparator.Name = "buildToolbarSeparator";
            this.buildToolbarSeparator.Size = new System.Drawing.Size(6, 25);
            // 
            // _dbgContinueButton
            // 
            this._dbgContinueButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this._dbgContinueButton.Image = ((System.Drawing.Image)(resources.GetObject("_dbgContinueButton.Image")));
            this._dbgContinueButton.Name = "_dbgContinueButton";
            this._dbgContinueButton.Size = new System.Drawing.Size(23, 22);
            this._dbgContinueButton.Text = "Continue";
            this._dbgContinueButton.ToolTipText = "Start / Continue debugging (F5)";
            this._dbgContinueButton.Click += new System.EventHandler(this.OnDebugContinue);
            // 
            // _dbgStopButton
            // 
            this._dbgStopButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this._dbgStopButton.Image = ((System.Drawing.Image)(resources.GetObject("_dbgStopButton.Image")));
            this._dbgStopButton.Name = "_dbgStopButton";
            this._dbgStopButton.Size = new System.Drawing.Size(23, 22);
            this._dbgStopButton.Text = "Stop";
            this._dbgStopButton.ToolTipText = "Stop debugging (Shift+F5)";
            this._dbgStopButton.Click += new System.EventHandler(this.OnDebugStop);
            // 
            // _dbgStepOverButton
            // 
            this._dbgStepOverButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this._dbgStepOverButton.Image = ((System.Drawing.Image)(resources.GetObject("_dbgStepOverButton.Image")));
            this._dbgStepOverButton.Name = "_dbgStepOverButton";
            this._dbgStepOverButton.Size = new System.Drawing.Size(23, 22);
            this._dbgStepOverButton.Text = "Step Over";
            this._dbgStepOverButton.ToolTipText = "Step Over (F10)";
            this._dbgStepOverButton.Click += new System.EventHandler(this.OnDebugStepOver);
            // 
            // _dbgStepIntoButton
            // 
            this._dbgStepIntoButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this._dbgStepIntoButton.Image = ((System.Drawing.Image)(resources.GetObject("_dbgStepIntoButton.Image")));
            this._dbgStepIntoButton.Name = "_dbgStepIntoButton";
            this._dbgStepIntoButton.Size = new System.Drawing.Size(23, 22);
            this._dbgStepIntoButton.Text = "Step Into";
            this._dbgStepIntoButton.ToolTipText = "Step Into (F11)";
            this._dbgStepIntoButton.Click += new System.EventHandler(this.OnDebugStepInto);
            // 
            // _dbgStepOutButton
            // 
            this._dbgStepOutButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this._dbgStepOutButton.Image = ((System.Drawing.Image)(resources.GetObject("_dbgStepOutButton.Image")));
            this._dbgStepOutButton.Name = "_dbgStepOutButton";
            this._dbgStepOutButton.Size = new System.Drawing.Size(23, 22);
            this._dbgStepOutButton.Text = "Step Out";
            this._dbgStepOutButton.ToolTipText = "Step Out (Shift+F11)";
            this._dbgStepOutButton.Click += new System.EventHandler(this.OnDebugStepOut);
            // 
            // _dbgToggleBpButton
            // 
            this._dbgToggleBpButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this._dbgToggleBpButton.Image = ((System.Drawing.Image)(resources.GetObject("_dbgToggleBpButton.Image")));
            this._dbgToggleBpButton.Name = "_dbgToggleBpButton";
            this._dbgToggleBpButton.Size = new System.Drawing.Size(23, 22);
            this._dbgToggleBpButton.Text = "Breakpoint";
            this._dbgToggleBpButton.ToolTipText = "Toggle Breakpoint (F9)";
            this._dbgToggleBpButton.Click += new System.EventHandler(this.OnDebugToggleBreakpoint);
            // 
            // debugToolbarSeparator
            // 
            this.debugToolbarSeparator.Name = "debugToolbarSeparator";
            this.debugToolbarSeparator.Size = new System.Drawing.Size(6, 25);
            // 
            // zoomInButton
            // 
            this.zoomInButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.zoomInButton.Image = ((System.Drawing.Image)(resources.GetObject("zoomInButton.Image")));
            this.zoomInButton.Name = "zoomInButton";
            this.zoomInButton.Size = new System.Drawing.Size(23, 22);
            this.zoomInButton.Text = "Zoom In";
            this.zoomInButton.ToolTipText = "Zoom In";
            this.zoomInButton.Click += new System.EventHandler(this.OnZoomIn);
            // 
            // zoomOutButton
            // 
            this.zoomOutButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.zoomOutButton.Image = ((System.Drawing.Image)(resources.GetObject("zoomOutButton.Image")));
            this.zoomOutButton.Name = "zoomOutButton";
            this.zoomOutButton.Size = new System.Drawing.Size(23, 22);
            this.zoomOutButton.Text = "Zoom Out";
            this.zoomOutButton.ToolTipText = "Zoom Out";
            this.zoomOutButton.Click += new System.EventHandler(this.OnZoomOut);
            // 
            // zoomFitButton
            // 
            this.zoomFitButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.zoomFitButton.Image = ((System.Drawing.Image)(resources.GetObject("zoomFitButton.Image")));
            this.zoomFitButton.Name = "zoomFitButton";
            this.zoomFitButton.Size = new System.Drawing.Size(23, 22);
            this.zoomFitButton.Text = "Zoom to Fit";
            this.zoomFitButton.ToolTipText = "Zoom to Fit";
            this.zoomFitButton.Click += new System.EventHandler(this.OnZoomToFit);
            // 
            // _zoomLabel
            // 
            this._zoomLabel.AutoSize = false;
            this._zoomLabel.Name = "_zoomLabel";
            this._zoomLabel.Size = new System.Drawing.Size(60, 22);
            this._zoomLabel.Text = "100 %";
            // 
            // zoomToolbarSeparator
            // 
            this.zoomToolbarSeparator.Name = "zoomToolbarSeparator";
            this.zoomToolbarSeparator.Size = new System.Drawing.Size(6, 25);
            // 
            // welcomePanelButton
            // 
            this.welcomePanelButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.welcomePanelButton.Image = ((System.Drawing.Image)(resources.GetObject("welcomePanelButton.Image")));
            this.welcomePanelButton.Name = "welcomePanelButton";
            this.welcomePanelButton.Size = new System.Drawing.Size(23, 22);
            this.welcomePanelButton.Text = "Welcome";
            this.welcomePanelButton.ToolTipText = "Show/hide the Welcome page";
            // 
            // designerPanelButton
            // 
            this.designerPanelButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.designerPanelButton.Image = ((System.Drawing.Image)(resources.GetObject("designerPanelButton.Image")));
            this.designerPanelButton.Name = "designerPanelButton";
            this.designerPanelButton.Size = new System.Drawing.Size(23, 22);
            this.designerPanelButton.Text = "Designer";
            this.designerPanelButton.ToolTipText = "Show/hide the Class Designer Canvas tab";
            // 
            // editorPanelButton
            // 
            this.editorPanelButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.editorPanelButton.Image = ((System.Drawing.Image)(resources.GetObject("editorPanelButton.Image")));
            this.editorPanelButton.Name = "editorPanelButton";
            this.editorPanelButton.Size = new System.Drawing.Size(23, 22);
            this.editorPanelButton.Text = "Editor";
            this.editorPanelButton.ToolTipText = "Show/hide the Editor tab";
            // 
            // debugPanelButton
            // 
            this.debugPanelButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.debugPanelButton.Image = ((System.Drawing.Image)(resources.GetObject("debugPanelButton.Image")));
            this.debugPanelButton.Name = "debugPanelButton";
            this.debugPanelButton.Size = new System.Drawing.Size(23, 22);
            this.debugPanelButton.Text = "Debug";
            this.debugPanelButton.ToolTipText = "Show/hide the Debug tab";
            // 
            // analysisPanelButton
            // 
            this.analysisPanelButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.analysisPanelButton.Image = ((System.Drawing.Image)(resources.GetObject("analysisPanelButton.Image")));
            this.analysisPanelButton.Name = "analysisPanelButton";
            this.analysisPanelButton.Size = new System.Drawing.Size(23, 22);
            this.analysisPanelButton.Text = "Analysis";
            this.analysisPanelButton.ToolTipText = "Show/hide the Analysis Results tab";
            // 
            // outputPanelButton
            // 
            this.outputPanelButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.outputPanelButton.Name = "outputPanelButton";
            this.outputPanelButton.Size = new System.Drawing.Size(23, 22);
            this.outputPanelButton.Text = "Output";
            this.outputPanelButton.ToolTipText = "Show/hide the Output tab";
            // 
            // problemsPanelButton
            // 
            this.problemsPanelButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.problemsPanelButton.Name = "problemsPanelButton";
            this.problemsPanelButton.Size = new System.Drawing.Size(23, 22);
            this.problemsPanelButton.Text = "Compile";
            this.problemsPanelButton.ToolTipText = "Show/hide the Compile tab";
            // 
            // hiddenPanelButton
            // 
            this.hiddenPanelButton.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.hiddenPanelButton.Name = "hiddenPanelButton";
            this.hiddenPanelButton.Size = new System.Drawing.Size(23, 22);
            this.hiddenPanelButton.Text = "Hidden";
            this.hiddenPanelButton.ToolTipText = "Show/hide the Hidden Elements tab";
            // 
            // _statusStrip
            // 
            this._statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this._statusLabel});
            this._statusStrip.Location = new System.Drawing.Point(0, 778);
            this._statusStrip.Name = "_statusStrip";
            this._statusStrip.Size = new System.Drawing.Size(1280, 22);
            this._statusStrip.TabIndex = 1;
            // 
            // _statusLabel
            // 
            this._statusLabel.Name = "_statusLabel";
            this._statusLabel.Size = new System.Drawing.Size(151, 17);
            this._statusLabel.Text = "Ready - No solution loaded";
            // 
            // _mainTabControl
            // 
            this._mainTabControl.Controls.Add(this._welcomeTab);
            this._mainTabControl.Controls.Add(this._graphicalDesignerTab);
            this._mainTabControl.Controls.Add(this._analysisResultsTab);
            this._mainTabControl.Controls.Add(this._buildResultsTab);
            this._mainTabControl.Controls.Add(this._problemsTab);
            this._mainTabControl.Controls.Add(this._hiddenElementsTab);
            this._mainTabControl.Dock = System.Windows.Forms.DockStyle.Fill;
            this._mainTabControl.Location = new System.Drawing.Point(0, 49);
            this._mainTabControl.Name = "_mainTabControl";
            this._mainTabControl.SelectedIndex = 0;
            this._mainTabControl.Size = new System.Drawing.Size(1280, 729);
            this._mainTabControl.TabIndex = 0;
            // 
            // _welcomeTab
            // 
            this._welcomeTab.Location = new System.Drawing.Point(4, 29);
            this._welcomeTab.Name = "_welcomeTab";
            this._welcomeTab.Size = new System.Drawing.Size(1272, 696);
            this._welcomeTab.TabIndex = 0;
            this._welcomeTab.Text = "Welcome";
            // 
            // _graphicalDesignerTab
            // 
            this._graphicalDesignerTab.Controls.Add(this._designerInnerTabControl);
            this._graphicalDesignerTab.Location = new System.Drawing.Point(4, 29);
            this._graphicalDesignerTab.Name = "_graphicalDesignerTab";
            this._graphicalDesignerTab.Size = new System.Drawing.Size(1272, 696);
            this._graphicalDesignerTab.TabIndex = 1;
            this._graphicalDesignerTab.Text = "Class Designer Canvas";
            // 
            // _designerInnerTabControl
            // 
            this._designerInnerTabControl.Controls.Add(this._designerTab);
            this._designerInnerTabControl.Controls.Add(this._interfacesTab);
            this._designerInnerTabControl.Controls.Add(this._abstractClassesTab);
            this._designerInnerTabControl.Controls.Add(this._generalClassesTab);
            this._designerInnerTabControl.Dock = System.Windows.Forms.DockStyle.Fill;
            this._designerInnerTabControl.Location = new System.Drawing.Point(0, 0);
            this._designerInnerTabControl.Name = "_designerInnerTabControl";
            this._designerInnerTabControl.SelectedIndex = 0;
            this._designerInnerTabControl.Size = new System.Drawing.Size(1272, 696);
            this._designerInnerTabControl.TabIndex = 0;
            // 
            // _designerTab
            // 
            this._designerTab.Location = new System.Drawing.Point(4, 22);
            this._designerTab.Name = "_designerTab";
            this._designerTab.Size = new System.Drawing.Size(1264, 670);
            this._designerTab.TabIndex = 0;
            this._designerTab.Text = "All";
            // 
            // _interfacesTab
            // 
            this._interfacesTab.Location = new System.Drawing.Point(4, 22);
            this._interfacesTab.Name = "_interfacesTab";
            this._interfacesTab.Size = new System.Drawing.Size(1264, 670);
            this._interfacesTab.TabIndex = 1;
            this._interfacesTab.Text = "Interfaces";
            // 
            // _abstractClassesTab
            // 
            this._abstractClassesTab.Location = new System.Drawing.Point(4, 22);
            this._abstractClassesTab.Name = "_abstractClassesTab";
            this._abstractClassesTab.Size = new System.Drawing.Size(1264, 670);
            this._abstractClassesTab.TabIndex = 2;
            this._abstractClassesTab.Text = "Abstract Classes";
            // 
            // _generalClassesTab
            // 
            this._generalClassesTab.Location = new System.Drawing.Point(4, 22);
            this._generalClassesTab.Name = "_generalClassesTab";
            this._generalClassesTab.Size = new System.Drawing.Size(1264, 670);
            this._generalClassesTab.TabIndex = 3;
            this._generalClassesTab.Text = "General Classes";
            // 
            // _analysisResultsTab
            // 
            this._analysisResultsTab.Location = new System.Drawing.Point(4, 29);
            this._analysisResultsTab.Name = "_analysisResultsTab";
            this._analysisResultsTab.Size = new System.Drawing.Size(1272, 696);
            this._analysisResultsTab.TabIndex = 2;
            this._analysisResultsTab.Text = "Analysis Results";
            // 
            // _buildResultsTab
            // 
            this._buildResultsTab.Location = new System.Drawing.Point(4, 29);
            this._buildResultsTab.Name = "_buildResultsTab";
            this._buildResultsTab.Size = new System.Drawing.Size(1272, 696);
            this._buildResultsTab.TabIndex = 3;
            this._buildResultsTab.Text = "Output";
            // 
            // _problemsTab
            // 
            this._problemsTab.Location = new System.Drawing.Point(4, 29);
            this._problemsTab.Name = "_problemsTab";
            this._problemsTab.Size = new System.Drawing.Size(1272, 696);
            this._problemsTab.TabIndex = 4;
            this._problemsTab.Text = "Compile";
            // 
            // _hiddenElementsTab
            // 
            this._hiddenElementsTab.Location = new System.Drawing.Point(4, 29);
            this._hiddenElementsTab.Name = "_hiddenElementsTab";
            this._hiddenElementsTab.Size = new System.Drawing.Size(1272, 696);
            this._hiddenElementsTab.TabIndex = 5;
            this._hiddenElementsTab.Text = "Hidden Elements";
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1280, 800);
            this.Controls.Add(this._mainTabControl);
            this.Controls.Add(this._statusStrip);
            this.Controls.Add(this._toolStrip);
            this.Controls.Add(this._menuStrip);
            this.MainMenuStrip = this._menuStrip;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "HelixPython - Visual Python IDE";
            this._menuStrip.ResumeLayout(false);
            this._menuStrip.PerformLayout();
            this._toolStrip.ResumeLayout(false);
            this._toolStrip.PerformLayout();
            this._statusStrip.ResumeLayout(false);
            this._statusStrip.PerformLayout();
            this._mainTabControl.ResumeLayout(false);
            this._graphicalDesignerTab.ResumeLayout(false);
            this._designerInnerTabControl.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        internal HelixPythonNamespace.UI.Controls.HelixTabControl _mainTabControl;
    }
}
