namespace HelixPythonNamespace.App
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.Notebooks;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.UI.Projection;
    using HelixPythonNamespace.UI.Rendering;
    using HelixPythonNamespace.UI.Interaction;
    using HelixPythonNamespace.UI.Interaction.Commands;
    using HelixPythonNamespace.UI.Interaction.States;
    using HelixPythonNamespace.UI.Services;
    using HelixPythonNamespace.App.Services;
    using HelixPythonNamespace.Workspace.Services;
    using CategoryFilterMode = HelixPythonNamespace.UI.Rendering.CategoryFilterMode;
    // The run pre-flight moved out of this form so the headless launcher enforces the same four
    // hard blockers from the same code. See HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.
    using SolutionRunPlan = HelixPythonNamespace.Workspace.Execution.SolutionRunPlan;
    using HelixPythonNamespace.AddIns.Kit;

    [System.ComponentModel.DesignerCategory("Form")]

    public partial class MainForm : LunaForm, Connectors.ISolutionExplorerHostServices
    {
        /// <summary>The product this fork ships as. Every window-title site goes through these two
        /// constants: the designer's Text is overwritten at runtime by <see cref="UpdateWindowTitle"/>
        /// and by the composition code below, so renaming MainForm.Designer.cs alone is not enough.</summary>
        internal new const string ProductName = "HelixPython";
        internal const string ProductTitle = ProductName + " - Visual Python IDE";

        // Parameterless ctor required by the WinForms designer; the DI ctor below chains to it
        // (: this()) so the designer-generated shell exists before runtime composition begins.
        public MainForm()
        {
            InitializeComponent();
            EnsurePanelToolbarIcons();
        }

        // The Output / Compile / Hidden panel toolbar buttons are declared in the designer with
        // DisplayStyle=Image but no icon: the .resx doesn't carry these three, and .Designer.cs may
        // not reference ToolbarIcons (enforced by DesignerSources_DoNotDependOnRuntimeFactories).
        // Assign them here — runs for both the bare designer/test form and the app (the DI ctor
        // chains ": this()") — so they render an icon instead of a blank button.
        private void EnsurePanelToolbarIcons()
        {
            if (outputPanelButton != null && outputPanelButton.Image == null)
                outputPanelButton.Image = ToolbarIcons.Output();
            if (problemsPanelButton != null && problemsPanelButton.Image == null)
                problemsPanelButton.Image = ToolbarIcons.Compile();
            if (hiddenPanelButton != null && hiddenPanelButton.Image == null)
                hiddenPanelButton.Image = ToolbarIcons.Hidden();

            // The designer's embedded Step-Over image is a stale "?" placeholder (the other debug
            // glyphs are fine). The correct icon ships as a disk PNG, so override it here at runtime —
            // this is unconditional because the button already carries the bad bitmap, not null.
            if (_dbgStepOverButton != null)
                _dbgStepOverButton.Image = ToolbarIcons.DebugStepOver();
        }



        // Thin menu/command handlers that forward straight to a form operation. Kept as
        // named methods (rather than lambdas) so the designer/menu wiring can reference them.
        // File ▸ Exit is the deliberate quit — NOT Close(), which the title-bar [X] also reaches and
        // which now hides to the notification area. See MainForm.Tray.cs.
        private void OnExitClick(object sender, EventArgs e) { ExitApplication(); }
        private void OnAddInterfaceClick(object sender, EventArgs e) { AddType(TypeKind.Interface); }
        private void OnAddAbstractClassClick(object sender, EventArgs e) { AddType(TypeKind.AbstractClass); }
        private void OnAddConcreteClassClick(object sender, EventArgs e) { AddType(TypeKind.ConcreteClass); }
        private void OnWelcomeMenuClick(object sender, EventArgs e) { ShowTab("welcome"); }

        // MainForm themes its own descendants via ApplyThemeRecursive (which deliberately
        // leaves custom-painted buttons alone), so opt out of LunaForm's auto-styler.
        protected override bool AutoStyleChildControls { get { return false; } }

        // MainForm's controls are all docked/anchored via the coordinators and already sit below the
        // caption; it must NOT be reparented into LunaForm's client host (that is for absolutely-
        // positioned designer dialogs).
        protected override bool UseLunaClientHost { get { return false; } }

        // Solution-native services
        internal readonly ISolutionService _solutionService;
        internal readonly ITypeService _typeService;
        // C++ counterpart to _typeService (Add Class/Interface/Abstract Class scaffolding for
        // CMake-backed ProjectLanguage.Cpp projects); no host-wiring needed, stateless service.
        internal readonly HelixPythonNamespace.Workspace.Cuda.CppTypeService _cppTypeService =
            new HelixPythonNamespace.Workspace.Cuda.CppTypeService();
        private readonly IProjectService _projectService;
        private readonly ILayoutService _layoutService;
        internal readonly IAnalysisEngine _analysisEngine;
        private readonly DeletionImpactAnalyzer _deletionAnalyzer;
        internal readonly RecentFilesManager _recentFilesManager;

        /// <summary>
        /// Legacy notebook MRU, retained only to migrate existing entries into Recent Solutions.
        /// </summary>
        internal readonly RecentFilesManager _recentNotebooksManager =
            new RecentFilesManager(RecentFilesManager.NotebooksFileName);

        private readonly SolutionGenerator _solutionGenerator;

        // Legacy services (for export compatibility)
        private readonly ICodeExporter _codeExporter;
        private readonly IMetadataExporter _metadataExporter;

        // User settings
        internal readonly UserSettingsManager _settingsManager;

        // Shell infrastructure (Phase 0): the event bus and shared context that
        // coordinators are progressively migrated onto. Created in the constructor;
        // logic is moved out of this form into coordinators incrementally.
        private readonly HelixPythonNamespace.App.Events.IAppEventBus _eventBus;
        private readonly MainFormContext _context;
        private HelixPythonNamespace.App.Coordinators.MenuCoordinator _menuCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.PanelCoordinator _panelCoordinator;
        internal readonly HelixPythonNamespace.App.Coordinators.DebugCoordinator _debugCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.ExplorerCoordinator _explorerCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.BuildAnalysisCoordinator _buildAnalysisCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.EditorCoordinator _editorCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.NavigationCoordinator _navigationCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.CanvasCoordinator _canvasCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.SolutionCoordinator _solutionCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.BuildRunCoordinator _buildRunCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.EngineCoordinator _engineCoordinator;
        private readonly HelixPythonNamespace.App.Coordinators.SingleFileCoordinator _singleFileCoordinator;

        // Engine result panels (Analysis ▸ Lint / Dead Code, Metrics, Profiler, Tests)





        // Current state
        internal SolutionModel _currentSolution;
        // One-shot snapshot used when a rename changes a TreeNode.FullPath segment before reload.
        internal List<string> _expandedTreePathsAfterRename;
        internal ILayoutMetadataStore _layoutStore;
        internal SolutionProjectionBuilder _projectionBuilder;
        internal SolutionFileWatcher _fileWatcher;

        // Hierarchical navigation state
        internal NavigationViewState _navigationState;
        internal ToolStripButton _drillUpButton;

        // UI components





        internal HelixPythonNamespace.App.Services.JobRunner _jobRunner;






        internal HelixPythonNamespace.App.Services.KeybindingManager _keybindings;





        private HelixPythonNamespace.UI.Interaction.SharedInteractionPreferences _sharedPreferences;

        // User intent ("I want the palette") vs. actual visibility. The palette is a canvas tool, so
        // it is only shown when intent is on AND the Class Designer Canvas tab is active — otherwise it
        // would float (TopMost) over the Editor/Debug/other tabs. See UpdateToolsPaletteVisibility.
        private bool _toolsPaletteEnabled;





        // Popup dialogs that should close with solution (kept for backward compat)
        internal AnalysisResultsDialog _analysisResultsDialog;
        internal BuildResultsDialog _buildResultsDialog;

        // R1: Embedded analysis/build panels











        // R1: Top-level tabs for docking






        // R2/R3: Docking state
        internal bool _analysisResultsUndocked;
        internal bool _buildResultsUndocked;


        internal bool _isPromotedMode;

        // Build tools
        internal readonly BuildToolDetector _buildToolDetector;


        // Debug toolbar buttons — enabled/disabled by DebugCoordinator.UpdateDebugToolbar per state.









        // Panel state for collapse/restore
        internal int _savedLeftSplitterDistance = 200;

        // R1/R2: Dock position and floating state
        internal bool _explorerDockedRight;



        internal bool _treeFloating;
        internal int _savedSplitterDistance;

        // R4: Tabbed main area (Designer + Editor + Debug + Analysis + Output + Problems)


        private bool _solutionDirty;

        // Category designer tabs






        // Designer Canvas category filter — one shared canvas/builder now serves the All/
        // Interfaces/ABC/Classes views (previously four separate sub-tabs); the combo switches
        // SolutionProjectionBuilder.CategoryFilter on _projectionBuilder and rebuilds in place.
        internal ComboBox _designerCategoryCombo;
        internal readonly HashSet<TabPage> _permanentTabs = new HashSet<TabPage>();
        internal readonly Dictionary<string, TabPage> _knownTabs = new Dictionary<string, TabPage>(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> _tabDefaultOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly Dictionary<TabPage, string> _tabKeysByPage = new Dictionary<TabPage, string>();
        // The close-button hit rects, the hover highlight and the whole drag-reorder state used to
        // live here, mirrored against the tab strip's geometry. HelixTabControl owns that geometry
        // now and hit-tests against the same layout it paints from, so keeping a second copy in the
        // form could only ever be a copy that disagrees.
        internal readonly Dictionary<string, ToolStripMenuItem> _panelMenuItemsByKey = new Dictionary<string, ToolStripMenuItem>(StringComparer.Ordinal);
        internal readonly Dictionary<string, ToolStripButton> _panelToolbarButtonsByKey = new Dictionary<string, ToolStripButton>(StringComparer.Ordinal);

        // Real (dependency-injected) constructor. Chains to the parameterless ctor first so the
        // designer shell is up, then stores services, builds the event bus + shared context,
        // attaches every coordinator, composes the runtime UI, applies settings, and opens an
        // empty workspace. Ordering matters: context must exist before coordinators attach, and
        // coordinators must be attached before InitializeRuntimeComponent wires their events.
        public MainForm(
            ISolutionService solutionService,
            ITypeService typeService,
            IProjectService projectService,
            ILayoutService layoutService,
            IAnalysisEngine analysisEngine,
            DeletionImpactAnalyzer deletionAnalyzer,
            RecentFilesManager recentFilesManager,
            SolutionGenerator solutionGenerator,
            ICodeExporter codeExporter,
            IMetadataExporter metadataExporter,
            UserSettingsManager settingsManager) : this()
        {
            _solutionService = solutionService;
            _typeService = typeService;
            // Install the era-specific codegen confirmation hook now that a Form is alive.
            if (_typeService != null)
            {
                _typeService.DowngradeNotifier =
                    new HelixPythonNamespace.App.Services.CodegenDowngradeNotifier(this);
            }
            _projectService = projectService;
            _layoutService = layoutService;
            _analysisEngine = analysisEngine;
            _deletionAnalyzer = deletionAnalyzer;
            _recentFilesManager = recentFilesManager;
            _solutionGenerator = solutionGenerator;
            _codeExporter = codeExporter;
            _metadataExporter = metadataExporter;
            _settingsManager = settingsManager;
            // Apply the saved Luna accent theme (Blue / Green / Crimson) before any control captures an
            // accent color (status strip, tabs, renderer) so the whole app opens in the chosen theme.
            HelixPythonNamespace.UI.Controls.LunaThemes.Apply(
                HelixPythonNamespace.UI.Controls.LunaThemes.FromName(_settingsManager.Settings.Theme));
            _buildToolDetector = new BuildToolDetector();

            // Keybinding registry, seeded with defaults before any menu item registers against it.
            _keybindings = new HelixPythonNamespace.App.Services.KeybindingManager(_settingsManager);
            SeedDefaultKeybindings();

            // Event bus + shared context: the plumbing every coordinator is handed so it can reach
            // services and publish/subscribe without this form widening its own surface.
            _eventBus = new HelixPythonNamespace.App.Events.AppEventBus();
            _context = new MainFormContext(
                this,
                _eventBus,
                _solutionService,
                _typeService,
                _projectService,
                _layoutService,
                _analysisEngine,
                _deletionAnalyzer,
                _recentFilesManager,
                _solutionGenerator,
                _codeExporter,
                _metadataExporter,
                _settingsManager,
                _buildToolDetector,
                _keybindings);

            // Instantiate and attach every coordinator against the shared context. Each Attach
            // hands the coordinator the same _context so they all share services + the event bus.
            // (MenuCoordinator is created later in InitializeRuntimeComponent, after the shell.)
            _panelCoordinator = new HelixPythonNamespace.App.Coordinators.PanelCoordinator();
            _panelCoordinator.Attach(_context);
            _debugCoordinator = new HelixPythonNamespace.App.Coordinators.DebugCoordinator();
            _debugCoordinator.Attach(_context);
            _explorerCoordinator = new HelixPythonNamespace.App.Coordinators.ExplorerCoordinator();
            _explorerCoordinator.Attach(_context);
            _buildAnalysisCoordinator = new HelixPythonNamespace.App.Coordinators.BuildAnalysisCoordinator();
            _buildAnalysisCoordinator.Attach(_context);
            _editorCoordinator = new HelixPythonNamespace.App.Coordinators.EditorCoordinator();
            _editorCoordinator.Attach(_context);
            _navigationCoordinator = new HelixPythonNamespace.App.Coordinators.NavigationCoordinator();
            _navigationCoordinator.Attach(_context);
            _canvasCoordinator = new HelixPythonNamespace.App.Coordinators.CanvasCoordinator();
            _canvasCoordinator.Attach(_context);
            _solutionCoordinator = new HelixPythonNamespace.App.Coordinators.SolutionCoordinator();
            _solutionCoordinator.Attach(_context);
            _buildRunCoordinator = new HelixPythonNamespace.App.Coordinators.BuildRunCoordinator();
            _buildRunCoordinator.Attach(_context);
            _engineCoordinator = new HelixPythonNamespace.App.Coordinators.EngineCoordinator();
            _engineCoordinator.Attach(_context);
            _singleFileCoordinator = new HelixPythonNamespace.App.Coordinators.SingleFileCoordinator();
            _singleFileCoordinator.Attach(_context);

            // Compose the real runtime UI (menus, toolbar, tabs, canvases, panels), apply the
            // user's persisted settings, then start on a blank in-memory solution.
            InitializeRuntimeComponent();
            // Add-ins register their tabs here, before the Load event replays the saved tab
            // visibility (ApplySettingsOnLoad), so a tab the user had open comes back.
            LoadAddIns();
            ApplySettings();
            NewEmptyWorkspace();
            _shell = this;
            FormClosed += delegate { if (ReferenceEquals(_shell, this)) _shell = null; };
        }

        private static MainForm _shell;

        /// <summary>
        /// The composed shell, for code that must reach the IDE without being handed it.
        ///
        /// A designer decides between a document tab and a floating window, and that decision is
        /// about whether an IDE is running — not about what the caller happened to pass as an
        /// owner. <c>Application.OpenForms</c> answers it only once the form has been SHOWN, which
        /// is true of the product and not of a headless host, so the shell records itself. Only the
        /// composed constructor registers: a bare <c>new MainForm()</c> is the designer ctor, whose
        /// coordinators are all null and which must never be mistaken for a running IDE.
        /// </summary>
        internal static MainForm Shell
        {
            get { return _shell != null && !_shell.IsDisposed ? _shell : null; }
        }

        /// <summary>
        /// Shared context and event bus used by the shell coordinators. Exposed so
        /// coordinators (and code being migrated out of this form) can resolve
        /// services and publish/subscribe without widening their constructors.
        /// </summary>
        internal MainFormContext CoordinatorContext
        {
            get { return _context; }
        }

        internal HelixPythonNamespace.App.Events.IAppEventBus EventBus
        {
            get { return _eventBus; }
        }

        // Builds the entire runtime window: tears down the placeholder designer shell, then
        // constructs the menu/toolbar (via MenuCoordinator), status strip, canvases, the top-level
        // tab control and all docked panels, and wires their events. This is the heart of the
        // form's composition — everything the user sees is assembled here.
        private void InitializeRuntimeComponent()
        {
            // Remove the lightweight design-surface shell before composing runtime-only panels.
            ToolStrip designToolStrip = _toolStrip;
            Controls.Remove(_mainTabControl);
            Controls.Remove(_statusStrip);
            Controls.Remove(designToolStrip);
            designToolStrip.Dispose();

            // Form settings
            Text = ProductTitle;
            Size = new Size(1280, 800);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            // Accept .sln drops from Explorer
            AllowDrop = true;
            DragEnter += OnFormDragEnter;
            DragDrop += OnFormDragDrop;

            // Apply Luna (XP) theme globally
            ToolStripManager.Renderer = new LunaRenderer();
            LunaColors.ApplyTo(this);

            // Menu + toolbar construction lives in MenuCoordinator (Coordinators/MenuCoordinator.cs).
            _menuCoordinator = new HelixPythonNamespace.App.Coordinators.MenuCoordinator();
            _menuCoordinator.Attach(_context);
            _menuCoordinator.Build(this);
            RegisterDesignerMenuRuntimeState();
            RefreshRecentFilesMenu();
            MigrateRecentNotebooks();

            // Status strip
            _statusStrip = new StatusStrip();
            _statusStrip.BackColor = LunaColors.StatusBarBackground;
            _statusStrip.ForeColor = Color.White;
            _statusLabel = new ToolStripStatusLabel("Ready - No solution loaded");
            _statusLabel.ForeColor = Color.White;
            _statusStrip.Items.Add(_statusLabel);

            // The design surfaces' own status line, which used to be painted on each designer's
            // top bar. See MainForm.DesignerStatus.cs.
            BuildDesignerStatusSlot();

            // Shared Cancel button for long-running jobs (compile/analyze/lint/etc.).
            // Hidden until a job is running; the JobRunner toggles its visibility.
            _cancelJobButton = new ToolStripButton("Cancel");
            _cancelJobButton.ForeColor = Color.White;
            _cancelJobButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _cancelJobButton.Alignment = ToolStripItemAlignment.Right;
            _cancelJobButton.ToolTipText = "Cancel the running operation";
            _cancelJobButton.Visible = false;
            _statusStrip.Items.Add(_cancelJobButton);

            // Marquee busy indicator — shown only while a job runs (the JobRunner toggles it), so the
            // user can see the app is working during compile/analyze/load rather than frozen.
            var busyBar = new ToolStripProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 40,
                Alignment = ToolStripItemAlignment.Right,
                AutoSize = false,
                Width = 90,
                Visible = false,
            };
            _statusStrip.Items.Add(busyBar);

            _breadcrumbLabel = new ToolStripStatusLabel("");
            _breadcrumbLabel.ForeColor = Color.FromArgb(200, 200, 200);
            _breadcrumbLabel.Alignment = ToolStripItemAlignment.Right;
            _breadcrumbLabel.Spring = false;
            _breadcrumbLabel.IsLink = true;
            _breadcrumbLabel.LinkBehavior = LinkBehavior.HoverUnderline;
            _breadcrumbLabel.ToolTipText = "Click to drill up one level";
            _breadcrumbLabel.Click += delegate { NavigateBack(); };
            _statusStrip.Items.Add(_breadcrumbLabel);

            // Diagnostics summary (errors/warnings from the last compile), right-aligned so it sits
            // beside the caret readout. Mirrors the Compile panel counts, visible from any tab.
            _diagnosticsStatusLabel = new ToolStripStatusLabel("");
            _diagnosticsStatusLabel.ForeColor = Color.White;
            _diagnosticsStatusLabel.Alignment = ToolStripItemAlignment.Right;
            // Clicking the "N errors, M warnings" summary jumps to the Compile panel (VS Code parity).
            // Rendered as a link so it reads as clickable; becomes a link only when there is a count.
            _diagnosticsStatusLabel.LinkColor = Color.FromArgb(255, 150, 150);
            _diagnosticsStatusLabel.ActiveLinkColor = Color.White;
            _diagnosticsStatusLabel.VisitedLinkColor = Color.FromArgb(255, 150, 150);
            _diagnosticsStatusLabel.LinkBehavior = LinkBehavior.HoverUnderline;
            _diagnosticsStatusLabel.ToolTipText = "Show the Compile panel";
            _diagnosticsStatusLabel.Click += delegate { ShowTab("problems"); };
            _statusStrip.Items.Add(_diagnosticsStatusLabel);

            // Ln/Col caret readout at the far right — populated only while an editor tab is active.
            _caretLabel = new ToolStripStatusLabel("");
            _caretLabel.ForeColor = Color.White;
            _caretLabel.Alignment = ToolStripItemAlignment.Right;
            _caretLabel.AutoSize = false;
            _caretLabel.Width = 110;
            _caretLabel.TextAlign = ContentAlignment.MiddleRight;
            // Clicking the Ln/Col readout opens Go to Line — the VS Code affordance. Styled as a link
            // (white, hover-underline) so it reads as clickable against the dark status strip.
            _caretLabel.IsLink = false;   // becomes a link once an editor is active (UpdateCaretStatus)
            _caretLabel.LinkColor = Color.White;
            _caretLabel.ActiveLinkColor = Color.White;
            _caretLabel.VisitedLinkColor = Color.White;
            _caretLabel.LinkBehavior = LinkBehavior.HoverUnderline;
            _caretLabel.ToolTipText = "Go to Line… (Ctrl+G)";
            _caretLabel.Click += OnCaretLabelClick;
            _statusStrip.Items.Add(_caretLabel);

            // Indentation indicator ("Spaces: 4" / "Tabs"), detected from the file content the way VS
            // Code shows it. Updated on tab-switch / open alongside the EOL indicator.
            _indentLabel = new ToolStripStatusLabel("");
            _indentLabel.ForeColor = Color.White;
            _indentLabel.Alignment = ToolStripItemAlignment.Right;
            _indentLabel.AutoSize = false;
            _indentLabel.Width = 78;
            _indentLabel.TextAlign = ContentAlignment.MiddleCenter;
            _indentLabel.ToolTipText = "Indentation — click to convert (Spaces / Tabs)";
            // Clicking the indicator converts the document's indentation (VS Code parity). Link-styled;
            // becomes a link only while an editor is active (set in UpdateEolStatus).
            _indentLabel.LinkColor = Color.White;
            _indentLabel.ActiveLinkColor = Color.White;
            _indentLabel.VisitedLinkColor = Color.White;
            _indentLabel.LinkBehavior = LinkBehavior.HoverUnderline;
            _indentLabel.Click += OnIndentLabelClick;
            _statusStrip.Items.Add(_indentLabel);

            // Line-ending indicator (CRLF / LF), the way VS Code shows it — populated only while an
            // editor tab is active. Updated on tab-switch / open (not every caret move), so it is cheap.
            _eolLabel = new ToolStripStatusLabel("");
            _eolLabel.ForeColor = Color.White;
            _eolLabel.Alignment = ToolStripItemAlignment.Right;
            _eolLabel.AutoSize = false;
            _eolLabel.Width = 52;
            _eolLabel.TextAlign = ContentAlignment.MiddleCenter;
            _eolLabel.ToolTipText = "Line ending — click to convert (CRLF / LF)";
            // Clicking the indicator converts the document's line endings (VS Code parity). Link-styled
            // and clickable only while an editor is active (set in UpdateEolStatus).
            _eolLabel.LinkColor = Color.White;
            _eolLabel.ActiveLinkColor = Color.White;
            _eolLabel.VisitedLinkColor = Color.White;
            _eolLabel.LinkBehavior = LinkBehavior.HoverUnderline;
            _eolLabel.Click += OnEolLabelClick;
            _statusStrip.Items.Add(_eolLabel);

            // Background-job runner that drives the status label + Cancel button.
            _jobRunner = new HelixPythonNamespace.App.Services.JobRunner(this, _statusLabel, _cancelJobButton);
            _jobRunner.BusyIndicator = busyBar;
            _jobRunner.JobStarted += OnForegroundJobStarted;
            _jobRunner.JobFinished += OnForegroundJobFinished;
            // Always-visible red Stop toolbar button + Build-menu item: enabled only while a job
            // (Run, Profiler, Analysis, Build, ...) is busy; clicking either cancels it the same
            // way the status-strip Cancel button already does.
            _jobRunner.RegisterStopItem(_stopExecutionButton);
            _jobRunner.RegisterStopItem(_stopExecutionMenuItem);
            _buildRunCoordinator.PopulateRunInTerminalMenus();

            // Shared interaction preferences — one instance drives all canvases + the palette
            _sharedPreferences = new HelixPythonNamespace.UI.Interaction.SharedInteractionPreferences();
            if (_settingsManager != null)
            {
                string saved = _settingsManager.Settings.ActiveTool;
                if (string.Equals(saved, "Select", StringComparison.OrdinalIgnoreCase))
                {
                    _sharedPreferences.ActiveTool = HelixPythonNamespace.UI.Interaction.CanvasTool.Select;
                }
                else
                {
                    _sharedPreferences.ActiveTool = HelixPythonNamespace.UI.Interaction.CanvasTool.Pan;
                }
            }

            // Canvas
            _canvas = new DiagramCanvas();
            _canvas.SetSharedPreferences(_sharedPreferences);
            // The designer toolbar lives beside the Show combo in the filter strip, so the canvas
            // keeps its full drawing area and Drill Up can use its normal left inset.
            _canvas.DrillUpButtonLeft = 8;
            _canvas.Dock = DockStyle.Fill;
            _canvas.NodeDoubleClicked += OnNodeDoubleClicked;
            _canvas.TypeDroppedFromTree += OnTypeDroppedFromTree;
            _canvas.NodeContextRequested += OnCanvasNodeContextRequested;
            _canvas.DrillUpRequested += OnDrillUp;
            _canvas.ContextMenuStrip = CreateCanvasContextMenu();

            // R1: Main tab control (top-level tabs). HelixTabControl owns the strip outright — head
            // layout, scrolling, hit-testing, the ✕ and drag-reorder all live in the control now.
            // It derives from TabControl only because TabPage refuses any other parent; the native
            // tab row is shrunk to nothing so the common control never creates its own scroller,
            // whose two arrows were locked together at the right-hand end.
            _mainTabControl = new HelixPythonNamespace.UI.Controls.HelixTabControl();
            _mainTabControl.Dock = DockStyle.Fill;
            _mainTabControl.ShowToolTips = true;
            _mainTabControl.Font = LunaFonts.Ui;
            _mainTabControl.StripHeight = 26;
            _mainTabControl.BackColor = LunaColors.TabStripBackground;
            _mainTabControl.TabCloseRequested += delegate (object s, HelixPythonNamespace.UI.Controls.TabEventArgs te)
            {
                CloseTab(te.Page);
                // Closing the LAST tab leaves nothing selected, so SelectedIndexChanged is not a
                // reliable signal here — and this is the gesture requirement 4.1 is about: the
                // explorer must come back the moment the structure tab (PDB Viewer add-in) goes away.
                DeactivateLeftAddInTab();
            };
            // The move is already done by the time this fires; all that is left is remembering it.
            _mainTabControl.TabReordered += delegate
            {
                RebuildTabDefaultOrderFromCurrentStrip();
                PersistTopLevelTabOrder();
            };
            _mainTabControl.SelectedIndexChanged += delegate
            {
                // Refresh recent-files list each time the user navigates to Welcome.
                if (_mainTabControl.SelectedTab == _welcomeTab && _welcomePanel != null)
                    _welcomePanel.RefreshRecentFiles();
                // The Tools palette is a canvas tool — show it only on the Designer tab so it
                // doesn't float over the Editor/Debug/other tabs.
                UpdateToolsPaletteVisibility();
                // Show the Ln/Col readout only while the Editor tab is the active view.
                UpdateCaretStatus();
                // Canvas commands are live only while the canvas is the view on screen.
                UpdateCanvasScopedToolbarEnabled();
                // Autosave keeps disk current while typing, but model/canvas reconstruction is
                // deferred until the canvas is visible so it cannot stall the editor UI thread.
                if (_editorCoordinator != null &&
                    (_mainTabControl.SelectedTab == _graphicalDesignerTab ||
                     _mainTabControl.SelectedTab == _designerTab))
                    _editorCoordinator.RefreshPendingCanvasChanges();
                // BEFORE ApplyNotebookMode, and the order is load-bearing: notebook mode captures
                // the explorer's visibility on entry, so an add-in tab that put the explorer aside
                // (the PDB Viewer's structure mode) has to have put it back before that snapshot is
                // taken. The other way round, selecting a notebook straight from the PDB viewer
                // would record "explorer hidden" and never bring it back.
                DeactivateLeftAddInTab();
                _editorCoordinator?.ApplyNotebookMode();
            };

            // --- R1.1/R1.2: Class Designer Canvas with inner sub-tabs ---
            _designerInnerTabControl = new TabControl();
            _designerInnerTabControl.Dock = DockStyle.Fill;
            _designerInnerTabControl.Font = LunaFonts.Sized(-0.5f);
            _designerInnerTabControl.SelectedIndexChanged += OnDesignerInnerTabChanged;

            _designerTab = new TabPage("Class Designer Canvas");
            _designerTab.ToolTipText = "Class Designer Canvas — use the Show filter to switch between All / Interfaces / ABC / Classes";

            // Filter bar: a "Show:" combo that switches _projectionBuilder.CategoryFilter on the
            // single shared canvas, replacing what used to be four separate sub-tabs.
            _designerCategoryCombo = new ComboBox();
            _designerCategoryCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _designerCategoryCombo.Font = LunaFonts.Sized(-0.5f);
            _designerCategoryCombo.Items.AddRange(new object[] { "All", "Interfaces", "ABC", "Classes" });
            _designerCategoryCombo.SelectedIndex = 0;
            _designerCategoryCombo.Width = 110;
            _designerCategoryCombo.Location = new Point(44, 3);
            _designerCategoryCombo.SelectedIndexChanged += OnDesignerCategoryFilterChanged;

            Label designerFilterLabel = new Label();
            designerFilterLabel.Text = "Show:";
            designerFilterLabel.AutoSize = true;
            designerFilterLabel.Location = new Point(4, 7);
            designerFilterLabel.ForeColor = LunaColors.TextPrimary;

            _designerFilterBar = new Panel();
            _designerFilterBar.Name = "designerFilterBar";
            _designerFilterBar.Dock = DockStyle.Top;
            _designerFilterBar.Height = 26;
            _designerFilterBar.Controls.Add(designerFilterLabel);
            _designerFilterBar.Controls.Add(_designerCategoryCombo);
            LunaColors.ApplyTo(_designerFilterBar);

            // Fill control added before the Top-docked bar so it reserves the leftover area
            // (same ordering idiom used by ProjectTreePanel's search/filter header).
            // Host the class-diagram canvas separately so the Tools palette can remain
            // permanently docked beside it instead of floating over workbench content.
            _designerCanvasHost = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = Color.FromArgb(246, 246, 246),
            };
            _designerCanvasHost.Controls.Add(_canvas);
            _designerTab.Controls.Add(_designerCanvasHost);
            _designerTab.Controls.Add(_designerFilterBar);

            // Welcome lives in its own top-level TabPage (_welcomeTab, created further
            // below). Here we just construct the panel and wire its quick-action events;
            // the parenting to a dedicated tab happens alongside the other top-level tabs.
            _welcomePanel = new HelixPythonNamespace.App.Controls.WelcomePanel(_recentFilesManager, _settingsManager);
            _welcomePanel.Dock = DockStyle.Fill;
            _welcomePanel.NewSolutionClicked += delegate { OnFileNew(this, EventArgs.Empty); };
            _welcomePanel.ImportDirectoryClicked += delegate { OnFileImportDirectory(this, EventArgs.Empty); };
            _welcomePanel.NewNotebookClicked += delegate { OnFileNewNotebook(this, EventArgs.Empty); };
            _welcomePanel.NewStandaloneSolutionClicked += delegate { OnFileNewSolution(this, EventArgs.Empty); };
            _welcomePanel.OpenAnythingClicked += delegate { OnFileOpenAnything(this, EventArgs.Empty); };
            _welcomePanel.OpenRecentClicked += (s, path) => OpenRecentPath(path);
            // "Clear list" on the Welcome page and File ▸ Recent Files ▸ Clear Recent Files are the
            // same history; whichever one is used, the other surface must not keep showing it.
            _welcomePanel.RecentsCleared += delegate { RefreshRecentFilesMenu(); };
            // A solution deleted from disk via the Welcome page must not linger open in the IDE.
            _welcomePanel.SolutionDeletedFromDisk += (s, deletedPath) =>
            {
                if (_currentSolution != null &&
                    string.Equals(_currentSolution.FilePath, deletedPath, StringComparison.OrdinalIgnoreCase))
                {
                    NewEmptyWorkspace();
                }
            };
            _designerInnerTabControl.TabPages.Add(_designerTab);

            _permanentTabs.Add(_designerTab);

            // Welcome is its own top-level tab (standalone from Designer) so that
            // Panels menu / toolbar toggles work uniformly via the shared ShowTab /
            // CloseTab infrastructure. Registered first so its default slot is the
            // left-most "Home" position.
            _welcomeTab = new TabPage("Welcome");
            _welcomeTab.ToolTipText = "Welcome page — quick-start + recent solutions";
            _welcomeTab.Controls.Add(_welcomePanel);
            _mainTabControl.TabPages.Add(_welcomeTab);
            _permanentTabs.Add(_welcomeTab);
            RegisterTopLevelTab("welcome", _welcomeTab);

            _graphicalDesignerTab = new TabPage("Class Designer Canvas");
            _graphicalDesignerTab.ToolTipText = "Class Designer Canvas";
            _graphicalDesignerTab.Controls.Add(_designerInnerTabControl);
            _mainTabControl.TabPages.Add(_graphicalDesignerTab);
            _permanentTabs.Add(_graphicalDesignerTab);
            RegisterTopLevelTab("designer", _graphicalDesignerTab);

            // --- Editor tab (HelixTextEditorNET-based, from HelixPythonNamespace.Editor) ---
            InitializeEditorTab();

            // --- Debug tab (Output + Breakpoints, backed by HelixPythonNamespace.Debugger) ---
            _debugCoordinator.Initialize();

            // --- R1.4: Analysis Results with inner sub-tabs ---
            _analysisInnerTabControl = new TabControl();
            _analysisInnerTabControl.Dock = DockStyle.Fill;
            _analysisInnerTabControl.Font = LunaFonts.Sized(-0.5f);

            _analysisResultsSubTab = new TabPage("Results");
            _analysisResultsListView = new ListView();
            _analysisResultsListView.View = View.Details;
            _analysisResultsListView.FullRowSelect = true;
            _analysisResultsListView.GridLines = true;
            _analysisResultsListView.Dock = DockStyle.Fill;
            _analysisResultsListView.Columns.Add("Severity", 80);
            _analysisResultsListView.Columns.Add("Rule", 120);
            _analysisResultsListView.Columns.Add("Type", 150);
            _analysisResultsListView.Columns.Add("Description", 400);
            ListViewClipboard.AttachCopyMenu(_analysisResultsListView);
            _analysisResultsSubTab.Controls.Add(_analysisResultsListView);
            _analysisInnerTabControl.TabPages.Add(_analysisResultsSubTab);

            _analysisOutputSubTab = new TabPage("Output");
            _analysisOutputTextBox = new TextBox();
            _analysisOutputTextBox.Multiline = true;
            _analysisOutputTextBox.ReadOnly = true;
            _analysisOutputTextBox.ScrollBars = ScrollBars.Both;
            _analysisOutputTextBox.Dock = DockStyle.Fill;
            _analysisOutputTextBox.Font = new Font("Consolas", 9f);
            _analysisOutputTextBox.WordWrap = false;
            _analysisOutputSubTab.Controls.Add(_analysisOutputTextBox);
            _analysisInnerTabControl.TabPages.Add(_analysisOutputSubTab);

            _analysisSummaryLabel = new Label();
            _analysisSummaryLabel.Dock = DockStyle.Top;
            _analysisSummaryLabel.Height = 26;
            _analysisSummaryLabel.TextAlign = ContentAlignment.MiddleLeft;
            _analysisSummaryLabel.Padding = new Padding(6, 0, 0, 0);
            _analysisSummaryLabel.Font = LunaFonts.UiBold;
            _analysisSummaryLabel.Text = "No analysis results yet — run Analysis ▸ Run Analysis to check your code.";

            Panel analysisContainer = new Panel();
            analysisContainer.Dock = DockStyle.Fill;
            analysisContainer.Controls.Add(_analysisInnerTabControl);
            analysisContainer.Controls.Add(_analysisSummaryLabel);
            // Clear / Copy / Save, same strip the Terminal and every other results panel carries.
            // Text follows the selected sub-tab so Copy gives what the user is actually looking at.
            HelixPythonNamespace.App.Controls.ResultsToolbar.Attach(analysisContainer, "analysis results",
                () => _analysisInnerTabControl.SelectedTab == _analysisOutputSubTab
                    ? _analysisOutputTextBox.Text
                    : HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_analysisResultsListView, false),
                () =>
                {
                    _analysisResultsListView.Items.Clear();
                    _analysisOutputTextBox.Clear();
                    _analysisSummaryLabel.Text = "No analysis results yet — run Analysis ▸ Run Analysis to check your code.";
                });

            _analysisResultsTab = new TabPage("Analysis Results");
            _analysisResultsTab.ToolTipText = "Analysis Results";
            _analysisResultsTab.Controls.Add(analysisContainer);
            _mainTabControl.TabPages.Add(_analysisResultsTab);
            _permanentTabs.Add(_analysisResultsTab);
            RegisterTopLevelTab("analysis", _analysisResultsTab);

            // --- R1.5: Build Results with inner sub-tabs ---
            _buildInnerTabControl = new TabControl();
            _buildInnerTabControl.Dock = DockStyle.Fill;
            _buildInnerTabControl.Font = LunaFonts.Sized(-0.5f);

            _buildOutputSubTab = new TabPage("Output");
            _buildOutputTextBox = new TextBox();
            _buildOutputTextBox.Multiline = true;
            _buildOutputTextBox.ReadOnly = true;
            _buildOutputTextBox.ScrollBars = ScrollBars.Both;
            _buildOutputTextBox.Dock = DockStyle.Fill;
            _buildOutputTextBox.Font = new Font("Consolas", 9f);
            _buildOutputTextBox.WordWrap = false;
            _buildOutputSubTab.Controls.Add(_buildOutputTextBox);
            _buildInnerTabControl.TabPages.Add(_buildOutputSubTab);

            _buildErrorsSubTab = new TabPage("Errors & Warnings");
            _buildErrorsListView = new ListView();
            _buildErrorsListView.View = View.Details;
            _buildErrorsListView.FullRowSelect = true;
            _buildErrorsListView.GridLines = true;
            _buildErrorsListView.Dock = DockStyle.Fill;
            _diagnosticStore = new HelixPythonNamespace.App.Services.DiagnosticStore();
            _buildErrorsListView.Columns.Add("Severity", 80);
            _buildErrorsListView.Columns.Add("Source", 90);
            _buildErrorsListView.Columns.Add("Code", 80);
            _buildErrorsListView.Columns.Add("File", 200);
            _buildErrorsListView.Columns.Add("Line", 50);
            _buildErrorsListView.Columns.Add("Message", 400);
            ListViewClipboard.AttachCopyMenu(_buildErrorsListView);
            _diagnosticStore.Changed += delegate { _buildAnalysisCoordinator?.RenderDiagnosticStore(); };
            ContextMenuStrip diagnosticMenu = _buildErrorsListView.ContextMenuStrip;
            diagnosticMenu.Items.Add(new ToolStripSeparator());
            diagnosticMenu.Items.Add("Show All Diagnostics", null, delegate { _diagnosticSourceFilter = null; _diagnosticSeverityFilter = null; _buildAnalysisCoordinator.RenderDiagnosticStore(); });
            diagnosticMenu.Items.Add("Build Diagnostics", null, delegate { _diagnosticSourceFilter = "Build"; _buildAnalysisCoordinator.RenderDiagnosticStore(); });
            diagnosticMenu.Items.Add("Compiler Diagnostics", null, delegate { _diagnosticSourceFilter = "Compiler"; _buildAnalysisCoordinator.RenderDiagnosticStore(); });
            diagnosticMenu.Items.Add("Errors Only", null, delegate { _diagnosticSeverityFilter = "error"; _buildAnalysisCoordinator.RenderDiagnosticStore(); });
            diagnosticMenu.Items.Add("Warnings Only", null, delegate { _diagnosticSeverityFilter = "warning"; _buildAnalysisCoordinator.RenderDiagnosticStore(); });
            _buildErrorsListView.DoubleClick += OnBuildErrorDoubleClick;
            _buildErrorsListView.KeyDown += OnBuildErrorKeyDown;
            _buildErrorsSubTab.Controls.Add(_buildErrorsListView);
            _buildInnerTabControl.TabPages.Add(_buildErrorsSubTab);

            _buildSummaryLabel = new Label();
            _buildSummaryLabel.Dock = DockStyle.Top;
            _buildSummaryLabel.Height = 26;
            _buildSummaryLabel.TextAlign = ContentAlignment.MiddleLeft;
            _buildSummaryLabel.Padding = new Padding(6, 0, 0, 0);
            _buildSummaryLabel.Font = LunaFonts.UiBold;
            // Blank until something actually runs. This used to read "No output yet — Run Startup
            // App (Ctrl+F5) or Compile (F6)." which was wrong twice over: nothing on the native
            // path reset it when a build STARTED, so it sat there contradicting the build log
            // streaming two lines below it; and Ctrl+F5/F6 are the Python commands, hidden in a
            // C++/CUDA workspace, so it advertised shortcuts that workspace does not have.
            // It is a live status line now — see BeginNativeBuildJob / EndNativeBuildJob.
            _buildSummaryLabel.Text = string.Empty;

            Panel buildContainer = new Panel();
            buildContainer.Dock = DockStyle.Fill;
            buildContainer.Controls.Add(_buildInnerTabControl);
            buildContainer.Controls.Add(_buildSummaryLabel);
            // Clear / Copy / Save for the Output pane. Text follows the selected sub-tab: the raw
            // build log, or the Errors & Warnings grid as TSV.
            HelixPythonNamespace.App.Controls.ResultsToolbar.Attach(buildContainer, "output",
                () => _buildInnerTabControl.SelectedTab == _buildOutputSubTab
                    ? _buildOutputTextBox.Text
                    : HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_buildErrorsListView, false),
                () =>
                {
                    _buildOutputTextBox.Clear();
                    _buildErrorsListView.Items.Clear();
                    _diagnosticStore.Clear();
                    _buildSummaryLabel.Text = string.Empty;
                });

            _buildResultsTab = new TabPage("Output");
            _buildResultsTab.ToolTipText = "Packager + Run output";
            _buildResultsTab.Controls.Add(buildContainer);
            _mainTabControl.TabPages.Add(_buildResultsTab);
            _permanentTabs.Add(_buildResultsTab);
            RegisterTopLevelTab("buildResults", _buildResultsTab);

            // --- Problems tab (Virtual Compiler output) ---
            _problemsPanel = new HelixPythonNamespace.App.Controls.ProblemsPanel();
            _problemsPanel.Dock = DockStyle.Fill;
            _problemsPanel.DiagnosticActivated += OnProblemActivated;
            _problemsPanel.QuickFixRequested += OnProblemQuickFix;
            _problemsPanel.DiagnosticsChanged += OnDiagnosticsChanged;
            _problemsTab = new TabPage("Compile");
            _problemsTab.ToolTipText = "Virtual-compiler diagnostics";
            _problemsTab.Controls.Add(_problemsPanel);
            _mainTabControl.TabPages.Add(_problemsTab);
            _permanentTabs.Add(_problemsTab);
            RegisterTopLevelTab("problems", _problemsTab);

            // Hidden Elements panel + tab (Scope 11)
            _hiddenElementsPanel = new HelixPythonNamespace.App.Controls.HiddenElementsPanel();
            _hiddenElementsPanel.Dock = DockStyle.Fill;
            _hiddenElementsPanel.UnhideRequested += (s, fqns) => { ApplyUnhide(fqns); };
            _hiddenElementsPanel.UnhideAllRequested += (s, _) => { ApplyUnhideAll(); };
            _hiddenElementsTab = new TabPage("Hidden Elements");
            _hiddenElementsTab.ToolTipText = "Types hidden from the canvas (click Unhide to restore)";
            _hiddenElementsTab.Controls.Add(_hiddenElementsPanel);
            _mainTabControl.TabPages.Add(_hiddenElementsTab);
            _permanentTabs.Add(_hiddenElementsTab);
            RegisterTopLevelTab("hiddenElements", _hiddenElementsTab);
            // Default to hidden. ApplyTabVisibilityFromSettings will re-show it if the
            // user had it open in a previous session. We use TabPages.Remove (not
            // CloseTab) so we don't stamp a false into VisibleTopLevelTabs and shadow
            // any saved preference.
            _mainTabControl.TabPages.Remove(_hiddenElementsTab);

            // --- Engine result tabs (Lint / Dead Code / Metrics / Profiler / Tests) ---
            // Registered but hidden by default; shown on demand when their command runs.
            _lintPanel = new HelixPythonNamespace.App.Controls.LintResultsPanel { Dock = DockStyle.Fill };
            _lintPanel.ItemActivated += (file, line) => OpenInEditor(file, line);
            RegisterHiddenEngineTab("lint", "Lint", "Python lint findings", _lintPanel);

            _deadCodePanel = new HelixPythonNamespace.App.Controls.DeadCodePanel { Dock = DockStyle.Fill };
            _deadCodePanel.ItemActivated += (file, line) => OpenInEditor(file, line);
            RegisterHiddenEngineTab("deadcode", "Dead Code", "Unused functions / classes / imports", _deadCodePanel);

            _metricsPanel = new HelixPythonNamespace.App.Controls.MetricsPanel { Dock = DockStyle.Fill };
            _metricsPanel.ItemActivated += (file, line) => OpenInEditor(file, line);
            RegisterHiddenEngineTab("metrics", "Metrics", "Code size & complexity metrics", _metricsPanel);

            _complexityPanel = new HelixPythonNamespace.App.Controls.ComplexityPanel { Dock = DockStyle.Fill };
            _complexityPanel.ItemActivated += (file, line) => OpenInEditor(file, line);
            RegisterHiddenEngineTab("complexity", "Complexity", "Asymptotic complexity (Big-O/Ω/Θ/o) analysis", _complexityPanel);

            // Code Review: the verdict on a body of work, with the rationale and the suggested
            // change for each comment. Distinct from Lint (style/bug patterns in one file) and from
            // Compile (does it build) — this one answers "would I sign off on this".
            _codeReviewPanel = new HelixPythonNamespace.App.Controls.CodeReviewPanel { Dock = DockStyle.Fill };
            _codeReviewPanel.ItemActivated += (file, line) => OpenInEditor(file, line);
            RegisterHiddenEngineTab("codeReview", "Code Review",
                "Review verdict and comments for the solution, a file, or your uncommitted changes",
                _codeReviewPanel);

            _profilePanel = new HelixPythonNamespace.App.Controls.ProfilePanel { Dock = DockStyle.Fill };
            _profilePanel.ItemActivated += (file, line) => OpenInEditor(file, line);
            RegisterHiddenEngineTab("profile", "Profiler", "cProfile per-function timings", _profilePanel);

            _testRunnerPanel = new HelixPythonNamespace.App.Controls.TestRunnerPanel { Dock = DockStyle.Fill };
            _testRunnerPanel.ItemActivated += (file, line) => OpenInEditor(file, line);
            RegisterHiddenEngineTab("tests", "Tests", "pytest / unittest results", _testRunnerPanel);

            // Find All References results live in a top-level tab (not a floating dialog), so they
            // stay put while the user reads code and navigate with the exact column (iter 367).
            _referencesPanel = new HelixPythonNamespace.App.Controls.ReferencesPanel { Dock = DockStyle.Fill };
            _referencesPanel.NavigateRequested += (path, line, col) => { try { OpenInEditor(path, line, true, col); } catch { } };
            RegisterHiddenEngineTab("references", "References", "Find All References results", _referencesPanel);

            // Call Hierarchy (incoming calls) — a top-level tab like References (iter 368), not a dialog.
            _callHierarchyPanel = new HelixPythonNamespace.App.Controls.CallHierarchyPanel { Dock = DockStyle.Fill };
            _callHierarchyPanel.NavigateRequested += (path, line, col) => { try { OpenInEditor(path, line, true, col); } catch { } };
            RegisterHiddenEngineTab("callHierarchy", "Call Hierarchy", "Incoming calls to the selected function", _callHierarchyPanel);

            // Keys are read through closures rather than captured values so a key pasted into
            // Preferences takes effect on the next request without rebuilding the registry.
            _aiProviders = new HelixPythonNamespace.Ai.Agent.AiProviderRegistry(
                delegate { return _settingsManager?.Settings?.AnthropicApiKey; },
                delegate { return _settingsManager?.Settings?.OpenAiApiKey; });

            // AI Terminal: an agent that reads, edits and runs commands in the open solution.
            // The panel is a view only — provider, tools and approval live in HelixPythonNamespace.Ai, and
            // the solution root is handed over on load because the agent's sandbox IS that
            // directory (see AiTerminalPanel.SetSolution).
            //
            // It is docked BELOW the editor rather than registered as a peer tab: the agent is
            // used while reading the code it is talking about, and a tab would force a switch
            // away from that code on every exchange.
            _aiTerminalPanel = new HelixPythonNamespace.App.Controls.AiTerminalPanel(_aiProviders)
            {
                Dock = DockStyle.Fill,
            };
            _aiTerminalDock = new HelixPythonNamespace.UI.Controls.DockablePanel
            {
                Dock = DockStyle.Fill,
                Title = "AI Terminal",
            };
            _aiTerminalDock.SetContent(_aiTerminalPanel);
            _aiTerminalDock.SetFloatTooltip("Maximize to a tab");
            // The header's three buttons are inert until something subscribes — the events exist
            // on DockablePanel but it does not act on them itself.
            _aiTerminalDock.CollapseStateChanged += OnAiTerminalMinimize;
            _aiTerminalDock.FloatRequested += OnAiTerminalMaximize;
            _aiTerminalDock.CloseRequested += OnAiTerminalClose;

            // Close hides the panel, so there has to be a way back — otherwise the ✕ is a
            // one-way door that costs the user a restart.
            _aiTerminalMenuItem = new ToolStripMenuItem("AI &Terminal");
            _aiTerminalMenuItem.ShortcutKeys = Keys.Control | Keys.Alt | Keys.A;
            // Its neighbours in this menu (Tools Palette, Snap to Grid, Word Wrap …) are checked
            // toggles, so this reads as one too: the tick shows whether the terminal is showing,
            // and clicking flips it. CheckOnClick stays OFF because the tick must follow the
            // panel's ACTUAL state — the header's ✕ and Maximize change it as well, and a
            // self-toggling item would drift out of sync with them.
            _aiTerminalMenuItem.CheckOnClick = false;
            _aiTerminalMenuItem.Click += delegate { ToggleAiTerminal(); };
            if (viewMenuItem != null)
            {
                viewMenuItem.DropDownItems.Add(_aiTerminalMenuItem);
                // Refresh on open as a backstop: any future path that hides the panel without
                // going through these handlers still shows the right tick.
                viewMenuItem.DropDownOpening += delegate { UpdateAiTerminalMenuChecked(); };
            }
            UpdateAiTerminalMenuChecked();

            // Project tree panel
            _projectTreePanel = new ProjectTreePanel();
            _projectTreePanel.Dock = DockStyle.Fill;
            _projectTreePanel.TypeSelected += OnProjectTreeTypeSelected;
            _projectTreePanel.TypeDoubleClicked += OnProjectTreeTypeDoubleClicked;
            _projectTreePanel.FunctionDoubleClicked += OnProjectTreeFunctionDoubleClicked;
            _projectTreePanel.ProjectVisibilityChanged += OnProjectVisibilityChanged;

            // Wire CRUD events from tree
            _projectTreePanel.AddClassRequested += OnTreeAddClass;
            _projectTreePanel.AddInterfaceRequested += OnTreeAddInterface;
            _projectTreePanel.AddAbstractClassRequested += OnTreeAddAbstractClass;
            _projectTreePanel.AddFileRequested += OnTreeAddFile;
            _projectTreePanel.AddNotebookRequested += OnTreeAddNotebook;
            _projectTreePanel.AddExistingFileRequested += OnTreeAddExistingFile;
            _projectTreePanel.NewFolderRequested += OnTreeNewFolder;
            _projectTreePanel.AddProjectRequested += OnTreeAddProject;
            _projectTreePanel.AddExistingProjectRequested += OnTreeAddExistingProject;
            _projectTreePanel.DeleteRequested += OnTreeDelete;
            _projectTreePanel.RemoveProjectFromSolutionRequested += OnTreeRemoveProjectFromSolution;
            _projectTreePanel.AddProjectReferenceRequested += (s, args) =>
            {
                var pm = args != null ? args.Item as ProjectModel : null;
                var group = args != null
                    ? args.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.StructuralGroup
                    : null;
                if (pm == null && group != null) pm = group.Project;
                if (pm != null) AddProjectReferenceToProject(pm.Name);
            };
            _projectTreePanel.RemoveProjectReferenceRequested += OnTreeRemoveProjectReference;
            _projectTreePanel.MoveProjectUpRequested += (s, args) => MoveTreeProject(args, -1);
            _projectTreePanel.MoveProjectDownRequested += (s, args) => MoveTreeProject(args, 1);
            _projectTreePanel.RenameRequested += OnTreeRename;
            _projectTreePanel.RenameSymbolRequested += OnTreeRenameSymbol;
            _projectTreePanel.FileDoubleClicked += (s, path) =>
            {
                if (string.IsNullOrEmpty(path)) return;
                // Resolve robustly (the tree stores forward-slashed keys; normalize to the OS form),
                // then tell the user if it still can't be found rather than silently doing nothing.
                string resolved = path;
                try { resolved = Path.GetFullPath(path); } catch { }
                if (File.Exists(resolved)) { OpenDocument(resolved); return; }
                if (File.Exists(path)) { OpenDocument(path); return; }
                _statusLabel.Text = "Could not open \"" + Path.GetFileName(path) + "\" — file not found on disk.";
            };
            _projectTreePanel.RenamePackageRequested += OnTreeRenamePackage;
            _projectTreePanel.ExcludeFromProjectRequested += OnTreeExcludeFromProject;
            _projectTreePanel.IncludeInProjectRequested += OnTreeIncludeInProject;
            _projectTreePanel.MoveTypeRequested += OnTreeMoveType;
            _projectTreePanel.FilePasteRequested += OnTreeFilePaste;
            _projectTreePanel.EditSignatureRequested += OnTreeEditSignature;
            _projectTreePanel.HideRequested += OnTreeHide;
            _projectTreePanel.UnhideRequested += OnTreeUnhide;
            _projectTreePanel.SaveRequested += OnTreeSave;
            _projectTreePanel.ViewSourceRequested += OnTreeViewSource;
            _projectTreePanel.OpenInDesignerRequested += OnTreeOpenInDesigner;
            _projectTreePanel.AnalyzeRequested += (s, e) => OnRunAnalysis(s, EventArgs.Empty);
            _projectTreePanel.BuildProjectRequested += OnTreeBuildProject;
            _projectTreePanel.ManagePackagesRequested += OnTreeManagePackages;
            _projectTreePanel.RestorePackagesRequested += OnTreeRestorePackages;
            _projectTreePanel.PropertiesRequested += OnTreeProperties;
            _projectTreePanel.SetStartupProjectRequested += OnSetStartupProject;
            _projectTreePanel.EditRunArgumentsRequested += OnEditRunArguments;
            _projectTreePanel.RunTestsRequested += OnTreeRunTests;
            _projectTreePanel.DebugTestsRequested += OnTreeDebugTests;
            _projectTreePanel.RunProjectRequested += OnTreeRunProject;
            _projectTreePanel.DebugProjectRequested += OnTreeDebugProject;
            _projectTreePanel.LinkedCMakeTargetBuildRequested += (s, t) => BuildLinkedCMakeTarget(t.TargetName);
            _projectTreePanel.LinkedCMakeTargetRunRequested += (s, t) => RunLinkedCMakeTarget(t.TargetName);

            // §5.8: the workspace tab strip accepts tree drags — dropping onto the
            // Editor tab header opens the item in the text editor; onto the Graphical
            // Designer Canvas tab header opens it in the designer.
            _mainTabControl.AllowDrop = true;
            _mainTabControl.DragOver += OnMainTabsDragOver;
            _mainTabControl.DragDrop += OnMainTabsDragDrop;

            // Left dockable panel wrapping project tree
            _leftDockPanel = new DockablePanel();
            // A tool window's caption: auto-hide and undock, not minimize and maximize.
            _leftDockPanel.Captions = DockablePanel.CaptionStyle.ToolWindow;
            _leftDockPanel.Title = "Solution Explorer";
            _leftDockPanel.Dock = DockStyle.Fill;
            _leftDockPanel.SetContent(_projectTreePanel);
            _leftDockPanel.CollapseStateChanged += _explorerCoordinator.OnLeftPanelCollapseChanged;
            _leftDockPanel.PinStateChanged += _explorerCoordinator.OnLeftPanelPinChanged;
            _leftDockPanel.AutoHideRequested += _explorerCoordinator.OnLeftPanelAutoHide;
            _leftDockPanel.AutoShowRequested += _explorerCoordinator.OnLeftPanelAutoShow;
            _leftDockPanel.FloatRequested += OnFloatExplorer;
            // The float button's other half: docking again. Routed to Dock Left, which redocks a
            // floating explorer before applying the side.
            _leftDockPanel.DockRequested += OnDockExplorerLeft;
            _leftDockPanel.DockLeftRequested += OnDockExplorerLeft;
            _leftDockPanel.DockRightRequested += OnDockExplorerRight;

            // Analysis panel (kept for internal use but not shown in UI)
            _analysisPanel = new AnalysisFeedbackPanel();

            // Left side splitter: project tree on left, canvas on right
            _leftSplitter = new SplitContainer();
            _leftSplitter.Dock = DockStyle.Fill;
            _leftSplitter.SplitterDistance = 200;
            _leftSplitter.Panel1.Controls.Add(_leftDockPanel);

            // Editor above, AI Terminal below — both visible at once. Splitting here rather than
            // adding another tab is the point: the agent is used while reading the code under
            // discussion, so it has to share the screen with it.
            _editorAiSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
            };
            _editorAiSplitter.Panel1.Controls.Add(_mainTabControl);
            _editorAiSplitter.Panel2.Controls.Add(_aiTerminalDock);
            // Keep the editor dominant; the console earns its height only while in use.
            //
            // SplitterDistance is clamped against the control's CURRENT height, so setting it here
            // — while the splitter is still at its default design size — is silently ignored or
            // clamped to something wrong. It is applied on the first real layout instead, once,
            // and then left alone so a user's drag is never overridden.
            // Must allow the header-only height, or Minimize cannot actually collapse the pane —
            // the splitter would clamp it back open and the button would look broken.
            _editorAiSplitter.Panel2MinSize = _aiTerminalDock.CollapsedHeight;
            EventHandler applyInitialSplit = null;
            applyInitialSplit = delegate
            {
                if (_editorAiSplitter.Height < 240) return;   // not laid out yet
                _editorAiSplitter.SizeChanged -= applyInitialSplit;
                try
                {
                    _editorAiSplitter.SplitterDistance = (int)(_editorAiSplitter.Height * 0.72);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Window too short for the ratio; the default split is fine.
                }
            };
            _editorAiSplitter.SizeChanged += applyInitialSplit;
            LunaColors.ApplyTo(_editorAiSplitter);

            _leftSplitter.Panel2.Controls.Add(_editorAiSplitter);
            LunaColors.ApplyTo(_leftSplitter);

            // Add controls in order
            Controls.Add(_leftSplitter);
            Controls.Add(_toolStrip);
            Controls.Add(_menuStrip);
            Controls.Add(_statusStrip);

            // Apply Luna (XP) theme to panels
            ApplyThemeToControls();

            // Wire selection changed to update property panel
            _canvas.SelectionManager.SelectionChanged += OnSelectionChanged;

            // Wire command executed to persist changes
            _canvas.CommandDispatcher.CommandExecuted += OnCommandExecuted;

            // Commands replayed via Edit ▸ Undo/Redo rewrite solution files; suspend the file
            // watcher for the duration so our own writes never show as "External Change Detected".
            // Resolved per call — StartFileWatcher replaces the watcher instance per solution.
            Func<IDisposable> commandWriteGuard = () => _fileWatcher != null ? _fileWatcher.SuspendWhileEditing() : null;
            _canvas.CommandDispatcher.WriteGuard = commandWriteGuard;

            // Composition: drag-drop onto a type offers to create fields.
            WireCompositionHandler(_canvas);

            // The Solution Explorer is a TOOL PANEL from the start, not only once a designer turns
            // up and builds the group. It used to be the dock panel's bare content until something
            // else claimed a tab, which meant View ▸ Panels had no entry for it — so a panel that
            // had been collapsed, floated or squeezed to a sliver had no menu route back at all.
            EnsureExplorerTabGroup();
        }

        private void OnForegroundJobStarted(object sender, HelixPythonNamespace.App.Services.JobRunnerEventArgs e)
        {
            ApplyForegroundCommandEnablement(false);
        }

        private void OnForegroundJobFinished(object sender, HelixPythonNamespace.App.Services.JobRunnerEventArgs e)
        {
            ApplyForegroundCommandEnablement(true);
        }

        /// <summary>
        /// Central command-state projection for the single foreground-operation lifecycle.
        /// Editing and navigation remain available; commands that would start another build,
        /// run, compile, analysis, test, or profiling operation are disabled. Build stays open
        /// so Stop Execution remains reachable, matching SharpDevelop's Build/Abort behavior.
        /// </summary>
        internal void ApplyForegroundCommandEnablement(bool enabled)
        {
            bool canStart = enabled && _currentSolution != null;

            foreach (ToolStripItem item in new ToolStripItem[]
            {
                _runButton, _buildButton,
                _runStartupMenuItem, _buildMenuItem, _runInTerminalMenuItem
            })
                if (item != null) item.Enabled = canStart;

            if (_terminalButton != null) _terminalButton.Enabled = enabled;

            foreach (string name in new[] { "tbAnalyze", "tbCompile" })
            {
                ToolStripItem item = ToolbarItem(name);
                if (item != null) item.Enabled = canStart;
            }

            if (analysisMenuItem != null) analysisMenuItem.Enabled = canStart;

            // The Build top-level menu itself remains enabled because it owns Stop Execution.
            // All other children are starters/settings that must not mutate or re-enter an active
            // operation. Separators harmlessly follow the same state.
            if (buildTopMenuItem != null)
            {
                buildTopMenuItem.Enabled = true;
                foreach (ToolStripItem child in buildTopMenuItem.DropDownItems)
                {
                    if (ReferenceEquals(child, _stopExecutionMenuItem)) continue;
                    child.Enabled = canStart;
                }
            }
        }

        // After the menu is built, binds designer-generated menu items to their keybinding IDs and
        // registers the Panels-menu toggles so shortcuts and the panel-visibility checkmarks work.
        private void RegisterDesignerMenuRuntimeState()
        {
            if (_keybindings == null || panelsMenuItem == null) return;
            // §8.2: heavy edit-time analysis (CUDA virtual compiler, non-Python
            // linter/analyzer passes) runs only with the user's explicit opt-in,
            // restored from settings here and toggled from the Analysis menu.
            HelixPythonNamespace.Editor.Completion.AnalysisGate.BackgroundAnalysisEnabled =
                _settingsManager != null && _settingsManager.Settings.EditorBackgroundAnalysis;
            EnsureBackgroundAnalysisToggle();
            EnsureEditorOverlayToggles();
            _keybindings.Register("file.new", newProjectWizardMenuItem);
            _keybindings.Register("file.open", openSolutionMenuItem);
            _keybindings.Register("file.import", importDirectoryMenuItem);
            _keybindings.Register("file.openEditor", openEditorMenuItem);
            _keybindings.Register("file.newNotebook", newNotebookMenuItem);
            _keybindings.Register("file.save", saveFileMenuItem);
            _keybindings.Register("file.saveAll", saveAllFilesMenuItem);
            _keybindings.Register("file.closeFile", closeFileMenuItem);
            _keybindings.Register("file.saveLayout", saveLayoutMenuItem);
            _keybindings.Register("build.compile", compileMenuItem);
            _keybindings.Register("analysis.lint", lintMenuItem);
            _keybindings.Register("analysis.deadCode", deadCodeMenuItem);
            _keybindings.Register("tests.runAll", runAllTestsMenuItem);
            _keybindings.Register("metrics.compute", computeMetricsMenuItem);
            _keybindings.Register("profiler.run", profileFileMenuItem);
            _keybindings.Register("run.startup", _runStartupMenuItem);
            _keybindings.Register("view.hideSelected", hideSelectedMenuItem);
            _keybindings.Register("view.showAllHidden", showHiddenMenuItem);
            _keybindings.Register("build.packager", _buildMenuItem);
            _keybindings.Register("debug.start", debugContinueMenuItem);
            _keybindings.Register("debug.stop", debugStopMenuItem);
            _keybindings.Register("debug.stepOver", debugStepOverMenuItem);
            _keybindings.Register("debug.stepInto", debugStepIntoMenuItem);
            _keybindings.Register("debug.stepOut", debugStepOutMenuItem);
            _keybindings.Register("debug.toggleBreakpoint", debugBreakpointMenuItem);
            RegisterPanelMenuItem(welcomePanelMenuItem, "welcome", "view.panel.welcome");
            RegisterPanelMenuItem(designerPanelMenuItem, "designer", "view.panel.designer");
            RegisterPanelMenuItem(editorPanelMenuItem, "editor", "view.panel.editor");
            RegisterPanelMenuItem(debugPanelMenuItem, "debug", "view.panel.debug");
            RegisterPanelMenuItem(analysisPanelMenuItem, "analysis", "view.panel.analysis");
            RegisterPanelMenuItem(outputPanelMenuItem, "buildResults", "view.panel.buildResults");
            RegisterPanelMenuItem(problemsPanelMenuItem, "problems", "view.panel.problems");
            RegisterPanelMenuItem(hiddenElementsPanelMenuItem, "hiddenElements", "view.panel.hiddenElements");
            RegisterPanelMenuItem(lintPanelMenuItem, "lint", "view.panel.lint");
            RegisterPanelMenuItem(deadCodePanelMenuItem, "deadcode", "view.panel.deadcode");
            RegisterPanelMenuItem(metricsPanelMenuItem, "metrics", "view.panel.metrics");
            RegisterPanelMenuItem(profilerPanelMenuItem, "profile", "view.panel.profile");
            RegisterPanelMenuItem(testsPanelMenuItem, "tests", "view.panel.tests");
            // Iter 368/369: References and Call Hierarchy are now top-level tabs, so they need the
            // same View ▸ Panels toggle every other result panel has — otherwise closing one leaves
            // no way to bring it back.
            RegisterPanelMenuItem(referencesPanelMenuItem, "references", "view.panel.references");
            RegisterPanelMenuItem(callHierarchyPanelMenuItem, "callHierarchy", "view.panel.callHierarchy");

            EnsureThemeMenuItems();
            EnsureComplexityMenuItems();
            EnsureCodeReviewMenuItems();
            EnsureSyncDocMenuItem();
            EnsureWordWrapMenuItem();
            EnsureHelpMenuItems();
            EnsureDiagramMenuItems();
        }

        // Align / Distribute / Refactor were only ever built in the dead MenuCoordinator fallback
        // (which runs solely when there is no designer menu), so these working features had no live
        // entry point. Append them to the live Diagram menu. Mnemonics chosen to not clash with the
        // designer items (Add I/A/C, Auto Layout L, Zoom Z, Grid G, Link Selected S).
        private bool _diagramMenuAugmented;
        private ToolStripMenuItem _alignMenu, _distributeMenu, _refactorMenu;
        private void EnsureDiagramMenuItems()
        {
            if (diagramMenuItem == null || _diagramMenuAugmented) return;
            _diagramMenuAugmented = true;

            _alignMenu = new ToolStripMenuItem("Alig&n");
            _alignMenu.DropDownItems.Add("Align &Left", null, delegate { AlignSelected(AlignmentKind.Left); });
            _alignMenu.DropDownItems.Add("Align &Right", null, delegate { AlignSelected(AlignmentKind.Right); });
            _alignMenu.DropDownItems.Add("Align &Top", null, delegate { AlignSelected(AlignmentKind.Top); });
            _alignMenu.DropDownItems.Add("Align &Bottom", null, delegate { AlignSelected(AlignmentKind.Bottom); });
            _alignMenu.DropDownItems.Add(new ToolStripSeparator());
            _alignMenu.DropDownItems.Add("Center &Horizontally", null, delegate { AlignSelected(AlignmentKind.CenterHorizontal); });
            _alignMenu.DropDownItems.Add("Center &Vertically", null, delegate { AlignSelected(AlignmentKind.CenterVertical); });

            _distributeMenu = new ToolStripMenuItem("&Distribute");
            _distributeMenu.DropDownItems.Add("Distribute &Horizontally", null, delegate { DistributeSelected(DistributeKind.Horizontal); });
            _distributeMenu.DropDownItems.Add("Distribute &Vertically", null, delegate { DistributeSelected(DistributeKind.Vertical); });

            _refactorMenu = new ToolStripMenuItem("&Refactor");
            _refactorMenu.DropDownItems.Add("&Extract Interface...", null, OnExtractInterface);
            _refactorMenu.DropDownItems.Add("&Pull Up Member...", null, OnPullUpMember);
            _refactorMenu.DropDownItems.Add("Push &Down Member...", null, OnPushDownMember);

            diagramMenuItem.DropDownItems.Add(new ToolStripSeparator());
            diagramMenuItem.DropDownItems.Add(_alignMenu);
            diagramMenuItem.DropDownItems.Add(_distributeMenu);
            diagramMenuItem.DropDownItems.Add(_refactorMenu);

            // Grey out commands that don't apply to the current selection (VS-style), so the user sees
            // at a glance what's available: Align needs 2+ nodes, Distribute 3+, Refactor a single type.
            diagramMenuItem.DropDownOpening += OnDiagramMenuOpening;

            // Grey Undo/Redo when the canvas command stack has nothing to undo/redo (VS-style), instead
            // of leaving them permanently enabled.
            if (editMenuItem != null) editMenuItem.DropDownOpening += OnEditMenuOpening;

            // Show only what the current workflow can actually use — see OnFileMenuOpening.
            if (fileMenuItem != null) fileMenuItem.DropDownOpening += OnFileMenuOpening;
            if (debugMenuItem != null) debugMenuItem.DropDownOpening += OnDebugMenuOpening;

            // §6.2–6.7: per-language filtering inside the Build / Analysis / Tools menus —
            // Python commands only in Python workspaces, CMake/CUDA commands only in
            // native workspaces, per-language designers only for their language.
            if (buildTopMenuItem != null) buildTopMenuItem.DropDownOpening += OnBuildMenuOpening;
            if (analysisMenuItem != null) analysisMenuItem.DropDownOpening += OnAnalysisMenuOpening;
            if (toolsMenuItem != null) toolsMenuItem.DropDownOpening += OnToolsMenuOpening;

            // Recompute which top-level menus apply every time the menu bar is activated, so the bar
            // stays in sync with the workflow without hooking every individual state change.
            if (_menuStrip != null) _menuStrip.MenuActivate += OnMenuBarActivated;
            UpdateMenuWorkflowVisibility();
        }

        private void OnMenuBarActivated(object sender, EventArgs e) => UpdateMenuWorkflowVisibility();

        /// <summary>
        /// A top-level menu is shown only when the current workflow can use it. With no solution
        /// loaded there is nothing to build, analyse, test, measure, profile, diagram, debug or
        /// commit, so those menus are hidden rather than standing there full of dead commands.
        /// File / Edit / View / Tools / Help always remain — they are how the user gets in and
        /// configures the IDE.
        /// </summary>
        internal void UpdateMenuWorkflowVisibility()
        {
            bool hasSolution = _currentSolution != null;
            // §6.3: a native CMake/CUDA workspace shows only C++/CUDA-relevant menus —
            // the Analysis/Tests/Metrics/Profiler/Diagram commands are the Python
            // engines and would all dead-end there.
            bool nativeWorkspace = IsNativeWorkspaceActive();
            // Tests is solution-scoped (not python-scoped) since the native workspaces got a
            // real test pipeline: Run All routes to ctest for C++/CUDA (EngineCoordinator.
            // RunNativeCMakeTests) and to cargo/dotnet/Pkg.test/pytest elsewhere.
            ToolStripMenuItem[] solutionScoped =
            {
                buildTopMenuItem, debugMenuItem, gitMenuItem, testsMenuItem,
            };
            // Analysis / Metrics / Profiler are NOT Python-only any more. Their engines cover the
            // other languages too — Run Analysis is language-neutral (it reads the TypeModel graph
            // every loader populates), Metrics has per-language profiles, and the Profiler has real
            // native sampling / nvcc-occupancy backends. What stays gated
            // is the individual COMMAND inside each menu, per language (OnAnalysisMenuOpening,
            // OnProfilerMenuOpening) — the menu is shown only when at least one of its commands is
            // live, so nothing here opens onto a row of dead entries.
            //
            // Diagram remains Python-scoped: its Add Interface/Abstract/Concrete commands write
            // Python source through the Python TypeService.
            ToolStripMenuItem[] pythonScoped = { diagramMenuItem };
            ToolStripMenuItem[] languageNeutral = { analysisMenuItem, metricsMenuItem };
            foreach (ToolStripMenuItem m in languageNeutral)
                if (m != null) m.Available = hasSolution;
            if (profilerMenuItem != null)
                profilerMenuItem.Available = hasSolution &&
                    Coordinators.EngineCoordinator.CanProfileExtension(CurrentLanguageExtension());

            // Build and Debug survive into single-file mode, because a loose file is something you
            // compile, run and breakpoint. Git and Tests do not: one acts on a repository the IDE
            // has not been pointed at, the other on a test project that by rule 3 cannot exist
            // outside a solution.
            bool canLaunch = hasSolution || IsSingleFileMode;
            foreach (ToolStripMenuItem m in solutionScoped)
                if (m != null)
                    m.Available = (m == buildTopMenuItem || m == debugMenuItem) ? canLaunch : hasSolution;
            foreach (ToolStripMenuItem m in pythonScoped)
                if (m != null) m.Available = hasSolution && !nativeWorkspace;

            UpdateToolbarWorkflowVisibility();

            // The tab strip follows the same rule: a tab that needs a solution is removed until
            // there is one (PanelCoordinator.ApplySolutionGate keeps the user's own show/hide
            // preferences intact while doing it).
            if (_panelCoordinator != null) _panelCoordinator.ApplySolutionGate(hasSolution, IsSingleFileMode);
        }

        /// <summary>
        /// The toolbar follows the same rule as the menus: a button the current workflow cannot use
        /// is hidden, not left sitting there greyed. With no solution loaded that removes the
        /// type-creation, run/build/analyse, debug and canvas-zoom groups, leaving the entry
        /// commands (New / Open / Import) and the panel toggles. Session-only debug buttons appear
        /// with a live session, matching the Debug menu exactly.
        ///
        /// Enablement of the debug buttons stays with DebugCoordinator.UpdateDebugToolbar — this
        /// only decides which of them are on screen at all.
        /// </summary>
        internal void UpdateToolbarWorkflowVisibility()
        {
            if (_toolStrip == null) return;

            bool hasSolution = _currentSolution != null;
            bool debugging = false;
            var controller = _debugCoordinator != null ? _debugCoordinator.Controller : null;
            if (controller != null) debugging = controller.IsDebugging;

            // NOTE: the toolbar buttons declared in MainForm.Designer.cs are a DESIGN-TIME mirror.
            // MenuCoordinator.Build replaces host._toolStrip wholesale at runtime, so only the
            // fields it assigns back (_linkButton, _runButton, _dbg*, _zoomLabel …) are live objects.
            // The rest must be reached by the Name that MenuCoordinator stamps on them.
            SetAvailable(ToolbarItem("tbSaveLayout"), hasSolution);
            SetAvailable(ToolbarItem("tbRefreshCanvas"), hasSolution);
            SetAvailable(_drillUpButton, hasSolution);

            foreach (string n in new[] { "tbAddInterface", "tbAddAbstract", "tbAddConcrete" })
                SetAvailable(ToolbarItem(n), hasSolution);
            SetAvailable(_linkButton, hasSolution);

            // Single-file mode is a runnable workflow without a solution: one file, which Run,
            // Debug and Compile all act on. Only those three are un-gated — Analyse, Build
            // (packaging), the canvas and the type-creation commands act on a solution's structure
            // and would dead-end on a loose file, which is the state this whole method exists to
            // avoid putting the user in.
            bool canLaunch = hasSolution || IsSingleFileMode;

            SetAvailable(ToolbarItem("tbAnalyze"), hasSolution);
            SetAvailable(ToolbarItem("tbCompile"), canLaunch);
            SetAvailable(_runButton, canLaunch);
            SetAvailable(_buildButton, hasSolution);

            // Start/Continue and Toggle Breakpoint are how a session BEGINS, so they show as soon as
            // there is something to launch; the rest only exist while one is running.
            SetAvailable(_dbgContinueButton, canLaunch);
            SetAvailable(_dbgToggleBpButton, canLaunch);
            foreach (ToolStripItem b in new ToolStripItem[]
                     { _dbgPauseButton, _dbgRestartButton, _dbgStopButton,
                       _dbgStepOverButton, _dbgStepIntoButton, _dbgStepOutButton })
                SetAvailable(b, canLaunch && debugging);

            foreach (string n in new[] { "tbZoomIn", "tbZoomOut", "tbZoomFit" })
                SetAvailable(ToolbarItem(n), hasSolution);
            SetAvailable(_zoomLabel, hasSolution);

            UpdateCanvasScopedToolbarEnabled();
            CollapseEmptyToolbarSeparators();
        }

        /// <summary>
        /// Grey out the toolbar buttons that act on the DIAGRAM CANVAS while the canvas is not the
        /// view on screen.
        ///
        /// <para>
        /// A second, orthogonal axis to <see cref="UpdateToolbarWorkflowVisibility"/>. That one
        /// answers "does this command exist in this workflow" and drives <c>Visible</c>; this one
        /// answers "can it act right now" and drives <c>Enabled</c>. Keeping them apart is what
        /// stops the toolbar flickering items in and out as the user moves between documents — the
        /// same objection that made the Solution Explorer reappearing a bug rather than a feature.
        /// </para>
        ///
        /// <para>
        /// Reported against a read-only <c>.py</c> opened from a notebook: "Add Abstract Class" and
        /// "Refresh Canvas" sat live over a text document with no canvas in sight. Read-only was
        /// never the discriminator — those buttons do not act on a text document whether it is
        /// writable or not.
        /// </para>
        ///
        /// <para>
        /// Deliberately does NOT touch <c>_drillUpButton</c> or <c>_linkButton</c>: both already
        /// have owners that compute their enabled state from the navigation level and the canvas
        /// selection, and a second writer would simply fight the first.
        /// </para>
        /// </summary>
        internal void UpdateCanvasScopedToolbarEnabled()
        {
            if (_toolStrip == null) return;

            // The same predicate the Edit menu and the clipboard shortcuts already use, so the
            // toolbar cannot disagree with the menu about whether the canvas is in play.
            bool canvas = IsDesignerTabActive();

            foreach (string name in new[] { "tbRefreshCanvas", "tbAddInterface", "tbAddAbstract", "tbAddConcrete" })
            {
                ToolStripItem item = ToolbarItem(name);
                if (item == null) continue;
                if (item.Enabled == canvas) continue;

                item.Enabled = canvas;
                // A greyed button with no explanation is the thing users file bugs about. The
                // reason is appended to, and stripped from, the button's own tooltip.
                item.ToolTipText = canvas
                    ? StripDisabledReason(item.ToolTipText)
                    : StripDisabledReason(item.ToolTipText) + CanvasOnlyReason;
            }
        }

        private const string CanvasOnlyReason = " — available on the Designer canvas";

        private static string StripDisabledReason(string tooltip)
        {
            if (string.IsNullOrEmpty(tooltip)) return tooltip;
            int at = tooltip.IndexOf(CanvasOnlyReason, StringComparison.Ordinal);
            return at < 0 ? tooltip : tooltip.Substring(0, at);
        }

        /// <summary>Looks up a runtime-built toolbar button by the Name MenuCoordinator stamps on it.</summary>
        private ToolStripItem ToolbarItem(string name)
        {
            if (_toolStrip == null) return null;
            foreach (ToolStripItem it in _toolStrip.Items)
                if (string.Equals(it.Name, name, StringComparison.Ordinal)) return it;
            return null;
        }

        private static void SetAvailable(ToolStripItem item, bool on)
        {
            if (item != null) item.Available = on;
        }

        /// <summary>
        /// Hides any separator whose preceding group has no visible buttons left, so scoping the
        /// toolbar never leaves a run of orphaned dividers.
        /// </summary>
        private void CollapseEmptyToolbarSeparators()
        {
            bool groupHasContent = false;
            foreach (ToolStripItem item in _toolStrip.Items)
            {
                if (item is ToolStripSeparator sep)
                {
                    sep.Available = groupHasContent;
                    groupHasContent = false;
                    continue;
                }
                if (item.Available) groupHasContent = true;
            }
        }

        /// <summary>
        /// Workflow-scoped Debug menu: only the commands that make sense for the current session
        /// state are shown. With nothing running, the session commands (Pause, Restart, Stop, the
        /// Step family, Run to Cursor) are hidden rather than displayed dead. Start/Continue, Toggle
        /// Breakpoint and the Break-on-exception settings stay — all are meaningful before a session
        /// exists.
        ///
        /// This also closes a real inconsistency: enable-state was applied to the debug TOOLBAR only
        /// (DebugCoordinator.UpdateDebugToolbar), so the menu's Step Over / Into / Out sat fully
        /// enabled with no session at all while the identical toolbar buttons were correctly greyed.
        /// </summary>
        private void OnDebugMenuOpening(object sender, EventArgs e)
        {
            RefreshDebugMenuState();
        }

        /// <summary>
        /// Applies the current session state to the Debug menu. Called both when the menu is opened
        /// AND from <c>DebugCoordinator.UpdateDebugToolbar</c> on every session-state transition.
        ///
        /// The second caller is not cosmetic — it is what keeps F10 / F11 / Shift+F11 alive. These
        /// chords are <see cref="ToolStripMenuItem.ShortcutKeys"/> on the Debug menu items, and
        /// WinForms refuses to dispatch a shortcut whose menu item is disabled. Refreshing only on
        /// DropDownOpening meant that opening the Debug menu once with no session latched the Step
        /// items to Enabled=false, and nothing re-enabled them when a session later paused at a
        /// breakpoint: the toolbar Step buttons and the menu entries themselves still worked (the
        /// menu re-enables itself as it opens), while every press of F10/F11 was silently swallowed
        /// and the debugger looked wedged. Stop (Shift+F5), Restart (Ctrl+Shift+F5) and Run to
        /// Cursor (Ctrl+F10) were dead the same way.
        /// </summary>
        internal void RefreshDebugMenuState()
        {
            if (debugMenuItem == null) return;
            bool debugging = false, paused = false;
            var controller = _debugCoordinator != null ? _debugCoordinator.Controller : null;
            if (controller != null) { debugging = controller.IsDebugging; paused = controller.IsPaused; }

            var st = HelixPythonNamespace.App.Coordinators.DebugCoordinator.ComputeToolbarState(debugging, paused);

            SetDebugMenuItem("Continue", true, st.Continue);
            SetDebugMenuItem("Pause", debugging, st.Pause);
            SetDebugMenuItem("Restart", debugging, st.Restart);
            SetDebugMenuItem("Stop", debugging, st.Stop);
            SetDebugMenuItem("Step Over", debugging, st.Step);
            SetDebugMenuItem("Step Into", debugging, st.Step);
            SetDebugMenuItem("Step Out", debugging, st.Step);
            SetDebugMenuItem("Run to Cursor", debugging, st.Step);
            SetDebugMenuItem("Toggle Breakpoint", true, st.ToggleBreakpoint);
        }

        /// <summary>
        /// Finds a Debug menu entry by a fragment of its label and applies visibility + enablement.
        /// Matched on text rather than a field because Pause / Restart / Run to Cursor are appended
        /// at runtime by MenuCoordinator.EnsureDebugMenuItems and have no designer fields.
        /// </summary>
        private void SetDebugMenuItem(string token, bool show, bool enabled)
        {
            if (debugMenuItem == null) return;
            foreach (ToolStripItem it in debugMenuItem.DropDownItems)
            {
                string text = (it.Text ?? string.Empty).Replace("&", string.Empty);
                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) continue;
                it.Available = show;
                it.Enabled = enabled;
                return;
            }
        }

        /// <summary>
        /// Workflow-scoped File menu: commands that cannot apply to the current state are hidden,
        /// not just greyed, so the menu only ever shows what the user can do right now.
        ///
        /// With nothing open the menu is the entry workflow (New / Open / Import / Recent / Exit).
        /// Opening a solution adds the solution-scoped commands; opening a document adds the
        /// document-scoped ones. Entry points are never hidden — hiding those would leave a user
        /// with no way in.
        /// </summary>
        private void OnFileMenuOpening(object sender, EventArgs e)
        {
            bool hasSolution = _currentSolution != null;
            bool hasDocument = false;
            if (_editorInnerTabControl != null)
                foreach (TabPage tab in _editorInnerTabControl.TabPages)
                    if (tab is HelixPythonNamespace.Editor.EditorTabPage) { hasDocument = true; break; }

            // Solution-scoped: meaningless with no solution loaded.
            if (openEditorMenuItem != null) openEditorMenuItem.Visible = hasSolution;
            if (closeSolutionMenuItem != null) closeSolutionMenuItem.Visible = hasSolution;
            if (saveLayoutMenuItem != null) saveLayoutMenuItem.Visible = hasSolution;
            if (exportMenuItem != null) exportMenuItem.Visible = hasSolution;

            // Document-scoped: only once a file is actually open in the editor.
            if (saveFileMenuItem != null)
            {
                saveFileMenuItem.Visible = hasDocument;
                // Visible but disabled on a read-only document. Hiding it would be worse: Save
                // vanishing from the File menu reads as a broken menu, where a greyed-out Save
                // says what is true — this file is being read, not edited. The tab title says
                // "(read-only)" for the same reason.
                HelixPythonNamespace.Editor.EditorTabPage document = ActiveDocumentTab();
                saveFileMenuItem.Enabled = document == null || !document.IsReadOnly;
            }
            if (saveAllFilesMenuItem != null) saveAllFilesMenuItem.Visible = hasDocument;
            if (closeFileMenuItem != null) closeFileMenuItem.Visible = hasDocument;

            // §6.3: inside a native CMake/CUDA workspace the Python import command is
            // an unrelated-language command; it stays available as an entry point
            // everywhere else (including with nothing open).
            if (importDirectoryMenuItem != null) importDirectoryMenuItem.Visible = !IsNativeWorkspaceActive();
        }

        // ---- §6.2–6.7: per-language menu filtering ----

        // The active workspace is native (C++/CUDA via CMake) rather than Python.
        internal bool IsNativeWorkspaceActive()
        {
            return _currentCMakeProject != null;
        }

        // Lower-case extension of the active editor document ("" when none).
        internal string ActiveDocumentExtension()
        {
            string path = ActiveDocumentPath();
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant(); }
            catch (ArgumentException) { return string.Empty; }
        }

        // Finds a dropdown item by its text with '&' mnemonics stripped, matching by
        // prefix — the runtime-backfilled items (CMake build, per-language designers)
        // have no designer fields to reference.
        internal static ToolStripMenuItem FindMenuItemByText(ToolStripMenuItem parent, string textPrefix)
        {
            if (parent == null) return null;
            foreach (ToolStripItem item in parent.DropDownItems)
            {
                var mi = item as ToolStripMenuItem;
                if (mi == null) continue;
                string text = (mi.Text ?? string.Empty).Replace("&", string.Empty);
                if (text.StartsWith(textPrefix, StringComparison.OrdinalIgnoreCase)) return mi;
            }
            return null;
        }

        // §6.2/§6.3: Build menu — Python packaging/run/compile commands only in a
        // Python workspace; the CMake build only in a native workspace. "Open CMake
        // Folder…" stays: it is the entry point INTO a native workspace, like Open.
        private void OnBuildMenuOpening(object sender, EventArgs e)
        {
            bool python = _currentSolution != null && !IsNativeWorkspaceActive();
            if (_runStartupMenuItem != null) _runStartupMenuItem.Visible = python;
            if (_buildMenuItem != null) _buildMenuItem.Visible = python;
            if (compileMenuItem != null) compileMenuItem.Visible = python;
            if (buildViaCMenuItem != null) buildViaCMenuItem.Visible = python;
            if (selectInterpreterMenuItem != null) selectInterpreterMenuItem.Visible = python;
            // Packager Settings hosts the native-toolchain (MSVC/GCC) configuration
            // too, so it applies to both workspace kinds.
            // The native Build/Rebuild/Clean group (solution + per project) belongs to a C++/CUDA
            // workspace only. Found by Name so renaming a caption can't silently unhook the gate.
            bool native = IsNativeWorkspaceActive();
            string[] nativeBuildItems =
            {
                "buildCMakeSolutionMenuItem", "rebuildCMakeSolutionMenuItem", "cleanCMakeSolutionMenuItem",
                "buildCMakeProjectMenu", "rebuildCMakeProjectMenu", "cleanCMakeProjectMenu",
            };
            foreach (string n in nativeBuildItems)
            {
                ToolStripMenuItem mi = BuildMenuItemByName(n);
                if (mi != null) mi.Visible = native;
            }
            // Rebuild the per-project lists now, so they reflect the solution that is actually open
            // rather than whatever was loaded when the menu was first constructed.
            if (native)
                PopulateNativeProjectBuildMenus(
                    BuildMenuItemByName("buildCMakeProjectMenu"),
                    BuildMenuItemByName("rebuildCMakeProjectMenu"),
                    BuildMenuItemByName("cleanCMakeProjectMenu"));
            // §6.2: inside a Python solution even the CMake ENTRY point is an
            // unrelated-language command; it remains available with nothing open
            // (the entry workflow) and in native workspaces.
            ToolStripMenuItem openCMake = FindMenuItemByText(buildTopMenuItem, "Open CMake Folder");
            if (openCMake != null) openCMake.Visible = !python;
        }

        /// <summary>
        /// Per-COMMAND language gating inside the Analysis menu. The menu itself is language-neutral
        /// now; what varies is which of its commands has a backend for the current language:
        ///   Run Analysis  — every language (the 14 rules read the TypeModel graph, and every
        ///                   loader populates it; nothing in them is Python-specific).
        ///   Lint          — Python, C++, CUDA (each has its own native linter).
        ///   Find Dead Code / Asymptotic Complexity — Python only. Both are bound to the Python AST
        ///                   (DeadCodeEngine walks VirtualCompiler modules; ComplexityEngine calls
        ///                   Python.ParseModule) and there is no other implementation, so offering
        ///                   them elsewhere would be a command that can only report nothing.
        /// </summary>
        private void OnAnalysisMenuOpening(object sender, EventArgs e)
        {
            bool hasSolution = _currentSolution != null;
            string ext = CurrentLanguageExtension();
            bool python = hasSolution && ext == ".py";

            if (runAnalysisMenuItem != null) runAnalysisMenuItem.Visible = hasSolution;
            if (lintMenuItem != null)
            {
                lintMenuItem.Visible = hasSolution && Coordinators.EngineCoordinator.CanLintExtension(ext);
                // The label used to say "Lint Python Sources" — wrong once it lints six languages.
                lintMenuItem.Text = "&Lint Sources";
            }
            if (deadCodeMenuItem != null) deadCodeMenuItem.Visible = python;
            ToolStripMenuItem complexity = FindMenuItemByText(analysisMenuItem, "Asymptotic Complexity");
            if (complexity != null) complexity.Visible = python;
            // Code Review — Python only, for the same reason as Dead Code and Complexity: every
            // rule reads the Python AST, and there is no other implementation, so offering it on a
            // C++/CUDA workspace would be a command that can only report nothing.
            if (_codeReviewMenuItem != null) _codeReviewMenuItem.Visible = python;
        }

        /// <summary>
        /// Which language's engines apply right now — the active document's, else the solution's.
        /// Delegates to the coordinator that owns the routing so the menus and the commands can
        /// never disagree about what language is in play.
        /// </summary>
        internal string CurrentLanguageExtension()
        {
            return _engineCoordinator != null ? _engineCoordinator.CurrentLanguageExtension() : ".py";
        }

        // §3.2–3.4: View-menu toggles for the editor's virtual overlays. "Virtual
        // Braces" checked renders C#-style braces; unchecked switches to the Bézier
        // scope connectors (the SRS pairs the two). "Virtual Type Markers" controls
        // the §3.3 inferred-type inlays. Both persist and apply to open tabs live.
        private void EnsureEditorOverlayToggles()
        {
            if (viewMenuItem == null) return;
            if (FindMenuItemByText(viewMenuItem, "Virtual Braces") != null) return;
            viewMenuItem.DropDownItems.Add(new ToolStripSeparator());

            var braces = new ToolStripMenuItem("Virtual &Braces (blocks as { })")
            {
                CheckOnClick = true,
                Checked = _settingsManager == null || _settingsManager.Settings.EditorVirtualBraces,
                ToolTipText = "Checked: C#-style virtual braces on Python blocks. Unchecked: Bézier scope connectors.",
            };
            braces.CheckedChanged += (s, e) =>
            {
                if (_settingsManager != null)
                {
                    _settingsManager.Settings.EditorVirtualBraces = braces.Checked;
                    try { _settingsManager.Save(); } catch { }
                }
                ApplyOverlaySettingsToOpenEditors();
            };
            viewMenuItem.DropDownItems.Add(braces);

            var markers = new ToolStripMenuItem("Virtual T&ype Markers")
            {
                CheckOnClick = true,
                Checked = _settingsManager == null || _settingsManager.Settings.EditorTypeMarkers,
                ToolTipText = "Show inferred static types (`: int`) after assignments — display only, never written to the file.",
            };
            markers.CheckedChanged += (s, e) =>
            {
                if (_settingsManager != null)
                {
                    _settingsManager.Settings.EditorTypeMarkers = markers.Checked;
                    try { _settingsManager.Save(); } catch { }
                }
                ApplyOverlaySettingsToOpenEditors();
            };
            viewMenuItem.DropDownItems.Add(markers);
        }

        private void ApplyOverlaySettingsToOpenEditors()
        {
            if (_editorInnerTabControl == null || _settingsManager == null) return;
            foreach (TabPage page in _editorInnerTabControl.TabPages)
            {
                var etp = page as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp == null || etp.Editor == null) continue;
                try
                {
                    etp.Editor.VirtualBracesEnabled = _settingsManager.Settings.EditorVirtualBraces;
                    etp.Editor.TypeMarkersEnabled = _settingsManager.Settings.EditorTypeMarkers;
                }
                catch { }
            }
        }

        // §8.2: the explicit background-analysis opt-in lives on the Analysis menu.
        private void EnsureBackgroundAnalysisToggle()
        {
            if (analysisMenuItem == null) return;
            if (FindMenuItemByText(analysisMenuItem, "Live Background Analysis") != null) return;
            analysisMenuItem.DropDownItems.Add(new ToolStripSeparator());
            var toggle = new ToolStripMenuItem("Live &Background Analysis")
            {
                CheckOnClick = true,
                Checked = _settingsManager != null && _settingsManager.Settings.EditorBackgroundAnalysis,
                ToolTipText = "Run the CUDA virtual compiler and per-language linters/analyzers on typing pauses",
            };
            toggle.CheckedChanged += (s, e) =>
            {
                HelixPythonNamespace.Editor.Completion.AnalysisGate.BackgroundAnalysisEnabled = toggle.Checked;
                if (_settingsManager != null)
                {
                    _settingsManager.Settings.EditorBackgroundAnalysis = toggle.Checked;
                    try { _settingsManager.Save(); } catch { /* non-fatal */ }
                }
                if (_statusLabel != null)
                    _statusLabel.Text = toggle.Checked
                        ? "Live background analysis enabled."
                        : "Live background analysis disabled (edits re-parse only).";
            };
            analysisMenuItem.DropDownItems.Add(toggle);
        }

        // §6.2–6.7: Tools — each per-language designer/canvas shows only when its
        // language is in play: the workspace's language, or the active document's.
        private void OnToolsMenuOpening(object sender, EventArgs e)
        {
            bool python = _currentSolution != null && !IsNativeWorkspaceActive();
            bool native = IsNativeWorkspaceActive();
            string ext = ActiveDocumentExtension();
            bool cudaDoc = ext == ".cu" || ext == ".cuh" || ext == ".cpp" || ext == ".h" || ext == ".hpp";
            bool cmakeDoc = ext == ".cmake" || string.Equals(
                Path.GetFileName(ActiveDocumentPath() ?? string.Empty), "CMakeLists.txt",
                StringComparison.OrdinalIgnoreCase);

            SetToolsItemVisible("Verify Active .cu with nvcc", native || ext == ".cu" || ext == ".cuh");
            // The GUI Designer Canvas is an add-in (spec 004): its item and its visibility rule are
            // under Add-in ▸ GUI Designer Canvas.
            SetToolsItemVisible("CMake Designer", native || cmakeDoc || cudaDoc);
            SetToolsItemVisible("TOML Config Canvas", python || ext == ".toml");
        }

        private void SetToolsItemVisible(string textPrefix, bool visible)
        {
            ToolStripMenuItem item = FindMenuItemByText(toolsMenuItem, textPrefix);
            if (item != null) item.Visible = visible;
        }

        // Reflect the canvas undo/redo availability + selection/clipboard state onto the Edit menu as
        // it opens, so commands grey out when they can't apply (VS-style).
        private void OnEditMenuOpening(object sender, EventArgs e)
        {
            // The Edit menu is scoped to whatever the user is actually editing. On the Editor tab
            // that is the text document; on a designer tab it is the diagram canvas. Before this
            // split the whole menu was canvas-only, so every command sat greyed out while the user
            // was typing in a file — the place they spend most of their time.
            HelixPythonNamespace.Editor.PythonEditorControl editor = ActiveTextEditor();
            if (editor != null)
            {
                if (undoMenuItem != null) undoMenuItem.Enabled = editor.CanUndo;
                if (redoMenuItem != null) redoMenuItem.Enabled = editor.CanRedo;
                bool hasText = !string.IsNullOrEmpty(editor.SelectedText);
                bool writable = !editor.ReadOnly;
                if (cutMenuItem != null) cutMenuItem.Enabled = hasText && writable;
                if (copyMenuItem != null) copyMenuItem.Enabled = hasText;
                if (deleteMenuItem != null) deleteMenuItem.Enabled = hasText && writable;
                if (pasteMenuItem != null) pasteMenuItem.Enabled = editor.CanPaste && writable;
                return;
            }

            var dispatcher = _canvas != null ? _canvas.CommandDispatcher : null;
            if (undoMenuItem != null) undoMenuItem.Enabled = dispatcher != null && dispatcher.CanUndo;
            if (redoMenuItem != null) redoMenuItem.Enabled = dispatcher != null && dispatcher.CanRedo;

            // Cut/Copy/Delete need a selection on the active designer canvas; Paste needs diagram-clipboard
            // content. Off a designer tab there is no canvas selection, so all four are unavailable.
            DiagramCanvas active = IsDesignerTabActive() ? GetActiveCanvas() : null;
            bool hasSelection = active != null && active.SelectionManager.SelectedNodes.Count > 0;
            if (cutMenuItem != null) cutMenuItem.Enabled = hasSelection;
            if (copyMenuItem != null) copyMenuItem.Enabled = hasSelection;
            if (deleteMenuItem != null) deleteMenuItem.Enabled = hasSelection;
            if (pasteMenuItem != null)
                pasteMenuItem.Enabled = active != null && HelixPythonNamespace.UI.Services.DiagramClipboard.HasPayload();
        }

        /// <summary>
        /// The text editor the Edit menu should act on: the active document, but only while the
        /// Editor tab is the one on screen. Returns null on a designer tab so the diagram keeps the
        /// menu. Public for tests.
        /// </summary>
        internal HelixPythonNamespace.Editor.PythonEditorControl ActiveTextEditor()
        {
            HelixPythonNamespace.Editor.EditorTabPage tab = ActiveDocumentTab();
            return tab != null ? tab.Editor : null;
        }

        /// <summary>
        /// The document tab on screen: the selected editor sub-tab, but only while the Editor
        /// top-level tab is the one being shown. Null on a designer/panel tab.
        /// </summary>
        /// <summary>
        /// Path of the document currently on screen, or null. Used to open the user's OWN file on
        /// the class canvas instead of a hard-coded sample.
        /// </summary>
        internal string ActiveDocumentPath()
        {
            HelixPythonNamespace.Editor.EditorTabPage tab = ActiveDocumentTab();
            if (tab != null && !string.IsNullOrEmpty(tab.FilePath)) return tab.FilePath;

            // Fall back to the selected editor document even when another top-level tab is showing —
            // the user still thinks of it as "the file I'm working on".
            tab = _editorInnerTabControl != null
                ? _editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.EditorTabPage
                : null;
            return tab != null ? tab.FilePath : null;
        }

        /// <summary>
        /// The notebook on screen, or null. The same gate <see cref="ActiveDocumentTab"/> applies —
        /// a notebook sitting in a background tab must not answer the shell's Edit commands.
        /// </summary>
        internal HelixPythonNamespace.Editor.Notebook.NotebookTabPage ActiveNotebookDocumentTab()
        {
            if (_mainTabControl == null || _editorTab == null) return null;
            if (_mainTabControl.SelectedTab != _editorTab) return null;
            if (_editorInnerTabControl == null) return null;
            return _editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.Notebook.NotebookTabPage;
        }

        internal HelixPythonNamespace.Editor.EditorTabPage ActiveDocumentTab()
        {
            if (_mainTabControl == null || _editorTab == null) return null;
            if (_mainTabControl.SelectedTab != _editorTab) return null;
            return _editorInnerTabControl != null
                ? _editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.EditorTabPage
                : null;
        }

        // When on, the Solution Explorer tracks the active editor document (View ▸ Sync with Active
        // Document). Session-scoped; defaults on, the professional-IDE default.
        private bool _syncTreeWithEditor = true;
        private ToolStripMenuItem _syncDocMenuItem;

        private bool _wordWrap;
        private ToolStripMenuItem _wordWrapMenuItem;

        /// <summary>The View menu, for tests that assert its contributed items.</summary>
        internal ToolStripMenuItem ViewMenuForTests { get { return viewMenuItem; } }

        /// <summary>Whether View ▸ Word Wrap is on. Applied to the Output pane and every editor tab.</summary>
        internal bool WordWrapEnabled { get { return _wordWrap; } }

        // View ▸ Word Wrap — wraps long lines to the pane width so nothing runs off the right edge.
        private void EnsureWordWrapMenuItem()
        {
            if (viewMenuItem == null || _wordWrapMenuItem != null) return;

            // Restore last session's choice. Word Wrap is a persistent editor preference, like the
            // theme and the §3.2–3.4 overlays: turning it on and finding it off after a restart
            // makes it read as broken. Seeded BEFORE the item is built so the tick mark matches,
            // and applied below so the surfaces match the tick.
            if (_settingsManager != null && _settingsManager.Settings != null)
                _wordWrap = _settingsManager.Settings.WordWrap;

            _wordWrapMenuItem = new ToolStripMenuItem("&Word Wrap")
            {
                CheckOnClick = true,
                Checked = _wordWrap,
                ToolTipText = "Wrap long lines in the editor and the Output pane to the window width",
            };
            _wordWrapMenuItem.CheckedChanged += delegate
            {
                _wordWrap = _wordWrapMenuItem.Checked;
                ApplyWordWrap();
                PersistWordWrap();
            };
            viewMenuItem.DropDownItems.Add(_wordWrapMenuItem);
            // NOT applied here: the menus are registered before the Output pane and the editor tab
            // control exist (InitializeRuntimeComponent builds them afterwards), so a restored
            // setting pushed now would reach nothing. ApplySettings does it once the shell is up.
        }

        /// <summary>
        /// Pushes the current Word Wrap setting into every surface that shows text.
        ///
        /// Called both from the menu toggle and after a new editor tab opens, so a tab created
        /// while wrap is on comes up wrapped rather than inheriting the control default.
        /// </summary>
        internal void ApplyWordWrap()
        {
            // Output pane: a plain multiline TextBox, so this is its own WordWrap property.
            // Turning wrap ON also removes the horizontal scrollbar, which is what makes the
            // text actually fit the margin rather than merely soft-breaking behind it.
            if (_buildOutputTextBox != null)
            {
                _buildOutputTextBox.WordWrap = _wordWrap;
                _buildOutputTextBox.ScrollBars = _wordWrap ? ScrollBars.Vertical : ScrollBars.Both;
            }

            if (_editorInnerTabControl != null)
            {
                foreach (TabPage tab in _editorInnerTabControl.TabPages)
                {
                    var editorTab = tab as HelixPythonNamespace.Editor.EditorTabPage;
                    if (editorTab != null) editorTab.SetWordWrap(_wordWrap);
                }
            }
        }

        private void PersistWordWrap()
        {
            try
            {
                if (_settingsManager?.Settings == null) return;
                _settingsManager.Settings.WordWrap = _wordWrap;
                _settingsManager.Save();
            }
            catch { /* a preference that fails to persist must not break the toggle */ }
        }

        // View ▸ Sync with Active Document — a checkable toggle for the tree's track-selection.
        private void EnsureSyncDocMenuItem()
        {
            if (viewMenuItem == null || _syncDocMenuItem != null) return;
            _syncDocMenuItem = new ToolStripMenuItem("Sync with Active &Document")
            {
                CheckOnClick = true,
                Checked = _syncTreeWithEditor,
                ToolTipText = "Select the active document's file in the Solution Explorer",
            };
            _syncDocMenuItem.CheckedChanged += delegate
            {
                _syncTreeWithEditor = _syncDocMenuItem.Checked;
                if (_syncTreeWithEditor) SyncTreeToActiveDocument();   // catch up immediately
            };
            viewMenuItem.DropDownItems.Add(_syncDocMenuItem);
        }

        // Track selection: highlight the active editor document's file in the Solution Explorer, the
        // way "Sync with Active Document" does, so the tree always points at what you're editing.
        internal void SyncTreeToActiveDocument()
        {
            if (!_syncTreeWithEditor) return;
            if (_projectTreePanel == null) return;
            HelixPythonNamespace.Editor.EditorTabPage doc =
                _editorInnerTabControl != null ? _editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.EditorTabPage : null;
            if (doc == null || string.IsNullOrEmpty(doc.FilePath)) return;
            try { _projectTreePanel.SelectFileNode(doc.FilePath); }
            catch { /* selection is best-effort */ }
        }

        /// <summary>File ▸ Save All — writes every modified document to disk and reports the count.</summary>
        internal void OnFileSaveAll(object sender, EventArgs e)
        {
            int modified = 0;
            if (_editorInnerTabControl != null)
                foreach (TabPage tab in _editorInnerTabControl.TabPages)
                    if (tab is HelixPythonNamespace.Editor.EditorTabPage etp && etp.IsModified) modified++;

            if (modified == 0) { if (_statusLabel != null) _statusLabel.Text = "No unsaved changes"; return; }

            SaveAllModifiedEditorTabs();

            int stillDirty = 0;
            if (_editorInnerTabControl != null)
                foreach (TabPage tab in _editorInnerTabControl.TabPages)
                    if (tab is HelixPythonNamespace.Editor.EditorTabPage etp && etp.IsModified) stillDirty++;

            if (_statusLabel != null)
                _statusLabel.Text = stillDirty == 0
                    ? "Saved " + modified + " file(s)"
                    : "Saved " + (modified - stillDirty) + " of " + modified + " file(s) — " + stillDirty + " failed";
        }

        /// <summary>File ▸ Close File — closes the active document (same as Ctrl+W and the tab's ×).</summary>
        internal void OnFileCloseDocument(object sender, EventArgs e)
        {
            TabPage doc = _mainTabControl != null && _mainTabControl.SelectedTab == _editorTab
                ? _editorInnerTabControl?.SelectedTab : null;
            if (doc == null) { if (_statusLabel != null) _statusLabel.Text = "No file to close"; return; }
            _editorCoordinator.CloseDocumentTab(doc);
        }

        private void OnDiagramMenuOpening(object sender, EventArgs e)
        {
            int count = 0;
            bool singleType = false;
            try
            {
                DiagramCanvas canvas = GetActiveCanvas();
                if (canvas != null && canvas.SelectionManager != null)
                    count = canvas.SelectionManager.SelectedNodes.Count;
                singleType = GetSelectedClassType() != null;
            }
            catch { /* menu enable-state is best-effort */ }

            if (_alignMenu != null) _alignMenu.Enabled = count >= 2;
            if (_distributeMenu != null) _distributeMenu.Enabled = count >= 3;
            if (_refactorMenu != null) _refactorMenu.Enabled = singleType;
        }

        // Help ▸ Documentation / Report an Issue — standard product Help entries, inserted before
        // About. Both open the project on GitHub in the default browser.
        private const string RepoUrl = "https://github.com/mns-csharp/HelixStudio";
        private bool _helpMenuAugmented;
        private void EnsureHelpMenuItems()
        {
            if (helpMenuItem == null || aboutMenuItem == null || _helpMenuAugmented) return;
            _helpMenuAugmented = true;

            int idx = helpMenuItem.DropDownItems.IndexOf(aboutMenuItem);
            if (idx < 0) idx = helpMenuItem.DropDownItems.Count;

            var toInsert = new ToolStripItem[]
            {
                new ToolStripSeparator(),
                new ToolStripMenuItem("&Documentation", null, delegate { OpenUrl(RepoUrl + "#readme"); }),
                new ToolStripMenuItem("&Report an Issue...", null, delegate { OpenUrl(RepoUrl + "/issues"); }),
                new ToolStripSeparator(),
            };
            // Insert in reverse at the same index so the final order is sep, Docs, Report, sep, About.
            for (int i = toInsert.Length - 1; i >= 0; i--)
                helpMenuItem.DropDownItems.Insert(idx, toInsert[i]);
        }

        private void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(url); }
            catch { if (_statusLabel != null) _statusLabel.Text = "Couldn't open " + url; }
        }

        // View ▸ Theme submenu — quick, discoverable switching between the three Luna accent themes
        // (Preferences also has the picker). Radio-style checks track the active theme.
        private ToolStripMenuItem _themeMenuItem;
        private void EnsureThemeMenuItems()
        {
            if (viewMenuItem == null || _themeMenuItem != null) return;

            _themeMenuItem = new ToolStripMenuItem("&Theme");
            foreach (HelixPythonNamespace.UI.Controls.LunaThemeKind kind in new[]
            {
                HelixPythonNamespace.UI.Controls.LunaThemeKind.Blue,
                HelixPythonNamespace.UI.Controls.LunaThemeKind.Green,
                HelixPythonNamespace.UI.Controls.LunaThemeKind.Crimson,
            })
            {
                var item = new ToolStripMenuItem(HelixPythonNamespace.UI.Controls.LunaThemes.DisplayName(kind))
                {
                    Tag = kind,
                    Checked = HelixPythonNamespace.UI.Controls.LunaThemes.Kind == kind,
                    CheckOnClick = false,
                };
                item.Click += OnThemeMenuItemClick;
                _themeMenuItem.DropDownItems.Add(item);
            }
            viewMenuItem.DropDownItems.Add(_themeMenuItem);
        }

        // Analysis ▸ Asymptotic Complexity runs one pass that reports every supported case.
        private bool _complexityMenuAugmented;
        private void EnsureComplexityMenuItems()
        {
            if (analysisMenuItem == null || _complexityMenuAugmented) return;
            _complexityMenuAugmented = true;

            analysisMenuItem.DropDownItems.Add(new ToolStripSeparator());
            analysisMenuItem.DropDownItems.Add("Asymptotic &Complexity", null,
                delegate { _engineCoordinator.OnAnalyzeComplexity(); });
        }

        // Analysis > Code Review submits different bodies of work to the same engine. Export
        // acts on the review already on screen rather than starting a new one.
        private bool _codeReviewMenuAugmented;
        private ToolStripMenuItem _codeReviewMenuItem;
        private void EnsureCodeReviewMenuItems()
        {
            if (analysisMenuItem == null || _codeReviewMenuAugmented) return;
            _codeReviewMenuAugmented = true;

            _codeReviewMenuItem = new ToolStripMenuItem("Code &Review");

            var solution = new ToolStripMenuItem("Review Whole &Solution", null,
                delegate { _engineCoordinator.OnRunCodeReview(HelixPythonNamespace.App.Coordinators.CodeReviewScope.Solution); })
            {
                ToolTipText = "Review every Python file in the solution and give a verdict",
            };
            _keybindings.Register("analysis.codeReview", solution);
            _codeReviewMenuItem.DropDownItems.Add(solution);

            _codeReviewMenuItem.DropDownItems.Add("Review Active &File", null,
                delegate { _engineCoordinator.OnRunCodeReview(HelixPythonNamespace.App.Coordinators.CodeReviewScope.ActiveFile); });

            _codeReviewMenuItem.DropDownItems.Add(new ToolStripMenuItem("Review &Uncommitted Changes", null,
                delegate { _engineCoordinator.OnRunCodeReview(HelixPythonNamespace.App.Coordinators.CodeReviewScope.UncommittedChanges); })
            {
                ToolTipText = "Review the Python files git reports as changed, weighting the lines you touched",
            });

            _codeReviewMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _codeReviewMenuItem.DropDownItems.Add(new ToolStripMenuItem("&Export Last Review…", null,
                delegate { _engineCoordinator.OnExportCodeReview(); })
            {
                ToolTipText = "Save the review on screen as a Markdown write-up",
            });

            analysisMenuItem.DropDownItems.Add(_codeReviewMenuItem);
        }

        private void OnThemeMenuItemClick(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            if (item == null || !(item.Tag is HelixPythonNamespace.UI.Controls.LunaThemeKind kind)) return;

            HelixPythonNamespace.UI.Controls.LunaThemes.Apply(kind);
            if (_settingsManager != null)
            {
                _settingsManager.Settings.Theme = kind.ToString();
                try { _settingsManager.Save(); } catch { /* a persist failure shouldn't block the switch */ }
            }
            ReapplyThemeVisuals();   // also re-syncs the Theme submenu checkmarks

            if (_statusLabel != null)
                _statusLabel.Text = "Theme: " + HelixPythonNamespace.UI.Controls.LunaThemes.DisplayName(kind);
        }

        // Records a Panels-menu item under its tab key (so its checkmark can be synced to the tab's
        // visibility) and binds it to a shortcut. Panels start Checked = true (all visible).
        private void RegisterPanelMenuItem(ToolStripMenuItem item, string key, string bindingName)
        {
            item.Checked = true;
            _panelMenuItemsByKey[key] = item;
            _keybindings.Register(bindingName, item);
        }

        // Panels-menu click handler: reads the tab key stashed in the item's Tag and asks the
        // PanelCoordinator to toggle that panel/tab on or off.
        private void OnPanelMenuItemClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item != null && item.Tag is string key)
                _panelCoordinator.TogglePanelByKey(key);
        }

        // Factory for the three category diagram canvases (Interfaces / Abstract / General). Each
        // shares the single SharedInteractionPreferences instance so tool/zoom state stays in sync
        // across canvases, and gets its own context menu rebuilt per navigation level on Opening.
        // Maps a TypeKind to the CategoryFilterMode that shows it as a full node rather than a
        // circle. Mirrors SolutionProjectionBuilder.GetTargetTabName's kind→category mapping.
        internal static CategoryFilterMode CategoryFilterModeFor(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.Interface: return CategoryFilterMode.InterfacesOnly;
                case TypeKind.AbstractClass: return CategoryFilterMode.AbstractClassesOnly;
                default: return CategoryFilterMode.ConcreteClassesOnly;
            }
        }

        // Fired when the "Show:" combo on the Class Designer Canvas tab changes. Switches the shared
        // builder's CategoryFilter and rebuilds at whatever navigation level is currently active —
        // replaces the old per-tab SyncCategoryTabsToLevel, since there's now only one builder.
        private void OnDesignerCategoryFilterChanged(object sender, EventArgs e)
        {
            if (_designerCategoryCombo == null || _projectionBuilder == null) return;

            CategoryFilterMode mode = (CategoryFilterMode)_designerCategoryCombo.SelectedIndex;
            _projectionBuilder.CategoryFilter = mode;
            // Category-filtered views compact positions independently of the All view's persisted
            // layout (same semantics the three former per-tab builders each had individually).
            _projectionBuilder.UseSidecarLayout = (mode != CategoryFilterMode.All);

            RebuildDesignerCanvasForCurrentFilter();
        }

        // Re-runs whichever Build*Level call matches _navigationState.Level against the (possibly
        // just-changed) CategoryFilter, so the combo and the drill-down breadcrumb stay consistent.
        internal void RebuildDesignerCanvasForCurrentFilter()
        {
            if (_currentSolution == null || _navigationState == null || _projectionBuilder == null) return;

            switch (_navigationState.Level)
            {
                case NavigationLevel.Solution:
                    // Solution level shows project containers only — categories have no meaning
                    // until you're looking at actual types, so a non-All filter shows nothing yet.
                    if (_projectionBuilder.CategoryFilter == CategoryFilterMode.All)
                        _projectionBuilder.BuildSolutionLevel(_currentSolution);
                    else
                        _canvas.Clear();
                    break;

                case NavigationLevel.Project:
                    _projectionBuilder.BuildProjectLevel(_currentSolution);
                    break;

                case NavigationLevel.Namespace:
                    _projectionBuilder.BuildNamespaceLevel(_currentSolution, _navigationState.ProjectName);
                    break;

                case NavigationLevel.Type:
                    List<string> checkedProjects = _projectTreePanel != null
                        ? _projectTreePanel.GetCheckedProjects() : new List<string>();
                    _projectionBuilder.SetVisibleProjects(checkedProjects);
                    _projectionBuilder.BuildTypeLevelForNamespace(
                        _currentSolution, _navigationState.ProjectName, _navigationState.NamespaceName);
                    break;
            }

            _canvas.Invalidate();
            UpdateDesignerCanvasEmptyHint();
        }

        private Label _designerEmptyHint;

        /// <summary>
        /// Says why the class diagram is empty, on the canvas, in the canvas's own words.
        ///
        /// An empty grid reads as broken. Reported exactly that way — "Class Designer Canvas is empty,
        /// something is broken" — when nothing was broken at all: the Show filter was left on
        /// Interfaces, and a category filter has nothing to say at SOLUTION level, where the
        /// diagram lists projects rather than types. The canvas cleared itself and said nothing,
        /// so the only reading left was that it had failed.
        ///
        /// Three different reasons, three different sentences, each naming the control that fixes
        /// it. The message is a Label over the canvas rather than painted into the scene: the
        /// scene is cleared and rebuilt constantly, and a hint that lives in it is a hint that has
        /// to be re-added correctly every time.
        /// </summary>
        private void UpdateDesignerCanvasEmptyHint()
        {
            if (_designerCanvasHost == null || _canvas == null) return;

            if (_designerEmptyHint == null)
            {
                _designerEmptyHint = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.FromArgb(120, 126, 140),
                    BackColor = Color.Transparent,
                    Padding = new Padding(24),
                    Visible = false,
                };
                _designerCanvasHost.Controls.Add(_designerEmptyHint);
                _designerEmptyHint.BringToFront();
            }

            string why = WhyTheCanvasIsEmpty();
            _designerEmptyHint.Text = why ?? string.Empty;
            _designerEmptyHint.Visible = why != null;
            if (why != null) _designerEmptyHint.BringToFront();
        }

        /// <summary>The reason there is nothing on the diagram, or null when there is something.</summary>
        private string WhyTheCanvasIsEmpty()
        {
            if (_canvas.SceneGraph != null && _canvas.SceneGraph.Nodes.Count > 0) return null;
            if (_currentSolution == null) return "No solution open.";

            // Read from the COMBO, not the projection builder: the message is about what the user
            // can see and change, and the combo is that. The builder is also null before a solution
            // has ever been projected, which would make the commonest case unexplainable.
            bool filtered = _designerCategoryCombo != null && _designerCategoryCombo.SelectedIndex > 0;
            string filter = _designerCategoryCombo != null && _designerCategoryCombo.SelectedItem != null
                ? _designerCategoryCombo.SelectedItem.ToString() : "this category";

            if (filtered && _navigationState != null && _navigationState.Level == NavigationLevel.Solution)
                return "The solution view lists projects, and \"" + filter + "\" only filters TYPES."
                    + Environment.NewLine + Environment.NewLine
                    + "Set Show to All to see the solution, or open a project to filter its types.";

            List<string> checkedProjects = _projectTreePanel != null
                ? _projectTreePanel.GetCheckedProjects() : new List<string>();
            if (checkedProjects.Count == 0)
                return "Nothing is checked to show." + Environment.NewLine + Environment.NewLine
                    + "Tick a project in the Solution Explorer to put its types on the canvas.";

            if (filtered)
                return "No \"" + filter + "\" in the checked projects."
                    + Environment.NewLine + Environment.NewLine + "Set Show to All to see everything.";

            return null;      // genuinely empty, and nothing here explains it better than the grid
        }

        // Handler for a dynamically-added "Go to <Type>" item (a circle-mode node representing a
        // type that belongs to another category). The TypeModel is carried in the item's Tag.
        private void OnGoToTypeFromCircle(object sender, EventArgs e)
        {
            ToolStripMenuItem menuItem = sender as ToolStripMenuItem;
            if (menuItem == null) return;

            TypeModel typeModel = menuItem.Tag as TypeModel;
            if (typeModel == null) return;

            NavigateToTypeByFilter(typeModel);
        }

        // Switches the "Show:" combo to whichever category renders the given type as a full node
        // (by TypeKind), rebuilds, then finds, selects and centers the viewport on that type. Used
        // both for the dynamic "Go to" context-menu item and for double-clicking a circle node.
        internal void NavigateToTypeByFilter(TypeModel typeModel)
        {
            if (typeModel == null || _projectionBuilder == null || _canvas == null) return;

            if (_designerCategoryCombo != null)
            {
                CategoryFilterMode targetMode = CategoryFilterModeFor(typeModel.TypeKind);
                if ((CategoryFilterMode)_designerCategoryCombo.SelectedIndex != targetMode)
                {
                    // Fires OnDesignerCategoryFilterChanged synchronously, rebuilding before we
                    // go looking for the node below.
                    _designerCategoryCombo.SelectedIndex = (int)targetMode;
                }
            }

            NodeVisual visual = _projectionBuilder.FindVisual(typeModel.FullyQualifiedName);
            if (visual == null) return;

            _canvas.SelectionManager.ClearSelection();
            _canvas.SelectionManager.Select(visual);

            // Center the viewport on the node: convert the node's world-space center to the pan
            // offset that places it at the canvas's screen center (accounting for zoom).
            var viewport = _canvas.Viewport;
            float centerX = visual.WorldBounds.X + visual.WorldBounds.Width / 2f;
            float centerY = visual.WorldBounds.Y + visual.WorldBounds.Height / 2f;
            float screenCenterX = _canvas.Width / 2f;
            float screenCenterY = _canvas.Height / 2f;
            viewport.SetPanOffset(
                screenCenterX - centerX * viewport.Zoom,
                screenCenterY - centerY * viewport.Zoom);
            _canvas.Invalidate();

            _statusLabel.Text = string.Format("Navigated to {0} ({1})", typeModel.Name, _designerCategoryCombo != null ? _designerCategoryCombo.Text : typeModel.Name);
        }

        // The Designer Canvas is now a single tab/canvas/builder (the former Interfaces/Abstract
        // Classes/General Classes sub-tabs are gone — see the "Show:" combo instead), so this
        // simply resolves to the main canvas whenever the Class Designer Canvas tab is active.
        internal DiagramCanvas GetActiveCanvas()
        {
            return _canvas;
        }

        // Kept as a thin accessor (rather than inlining "== _canvas ? _projectionBuilder : null"
        // at every call site) since callers pass in whatever GetActiveCanvas() returned.
        internal SolutionProjectionBuilder GetProjectionBuilderForCanvas(DiagramCanvas canvas)
        {
            if (canvas == _canvas) return _projectionBuilder;
            return null;
        }

        // Opening handler for the main (All) canvas context menu; rebuilds its items to match the
        // current navigation level, same as the category-canvas variant above.
        internal void OnCanvasContextMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            ContextMenuStrip menu = sender as ContextMenuStrip;
            if (menu == null) return;

            // R6: Rebuild context menu dynamically based on current navigation level
            RebuildCanvasContextMenuForLevel(menu);
        }

        // True only when exactly one type is selected AND that type has a backing source file —
        // gates the "View Source" command's enabled state.
        private bool CanViewSource()
        {
            DiagramCanvas activeCanvas = GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = GetProjectionBuilderForCanvas(activeCanvas);

            if (activeCanvas.SelectionManager.SelectedNodes.Count != 1) return false;
            if (_currentSolution == null || activeBuilder == null) return false;

            NodeVisual visual = activeCanvas.SelectionManager.SelectedNodes[0];
            string fqn = activeBuilder.FindFQN(visual);
            if (string.IsNullOrEmpty(fqn)) return false;

            TypeModel typeModel = _currentSolution.FindType(fqn);
            return typeModel != null && !string.IsNullOrEmpty(typeModel.SourceFilePath);
        }

        // "View Source" command: resolves the single selected node to its FQN, looks up the
        // TypeModel, and opens its source file in the editor.
        internal void OnViewSource(object sender, EventArgs e)
        {
            DiagramCanvas activeCanvas = GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = GetProjectionBuilderForCanvas(activeCanvas);

            if (activeCanvas.SelectionManager.SelectedNodes.Count != 1) return;
            if (_currentSolution == null || activeBuilder == null) return;

            NodeVisual visual = activeCanvas.SelectionManager.SelectedNodes[0];
            string fqn = activeBuilder.FindFQN(visual);
            if (string.IsNullOrEmpty(fqn)) return;

            TypeModel typeModel = _currentSolution.FindType(fqn);
            if (typeModel == null) return;

            OpenSourceForType(typeModel);
        }

        // Opens a type's source file at its declaration line in the editor, or shows an info box if
        // the type has no associated file (e.g. a purely designed type not yet code-generated).
        internal void OpenSourceForType(TypeModel typeModel)
        {
            if (typeModel == null || string.IsNullOrEmpty(typeModel.SourceFilePath))
            {
                MessageBox.Show(
                    "No source file associated with this type.",
                    "View Source",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            OpenInEditor(typeModel.SourceFilePath, typeModel.SourceLineNumber);

            _statusLabel.Text = string.Format("Viewing source: {0}", Path.GetFileName(typeModel.SourceFilePath));
        }

        // ---- Closable/reopenable top-level tabs ----

        /// <summary>
        /// Registers a top-level tab under a stable key so the View menu and toolbar
        /// can toggle its visibility. The tab's original insertion index is recorded
        /// so <see cref="ShowTab"/> can restore it to the correct position later.
        /// </summary>
        // ---- Panel/tab visibility: thin delegates to PanelCoordinator ----
        internal void RegisterTopLevelTab(string key, TabPage page) => _panelCoordinator.RegisterTopLevelTab(key, page);

        internal void ShowTab(string key) => _panelCoordinator.ShowTab(key);

        /// <summary>
        /// Closes a top-level tab. A designer add-in that lent its Toolbox and Properties to the
        /// shell's docks gets them back through its tab's <c>Removed</c> callback, which the host
        /// runs only for a real close (not for the remove-and-reinsert the solution gate and
        /// ShowTab do to reorder the strip).
        /// </summary>
        internal void CloseTab(TabPage tab)
        {
            _panelCoordinator?.CloseTab(tab);
        }

        internal void SetTabVisibleWithoutPersisting(string key, bool visible) => _panelCoordinator?.SetTabVisibleWithoutPersisting(key, visible);

        internal void HideResultTabs() => _panelCoordinator?.HideResultTabs();

        // Ctrl+F4 (Visual Studio) and Ctrl+W (VS Code) both close the active document tab.
        internal static bool IsCloseTabShortcut(Keys keyData)
        {
            return keyData == (Keys.Control | Keys.F4) || keyData == (Keys.Control | Keys.W);
        }

        internal void AddPanelMenuItem(ToolStripMenuItem parent, string key, string text, string bindingName) =>
            _panelCoordinator.AddPanelMenuItem(parent, key, text, bindingName);

        internal void AddPanelToolbarButton(string key, string text, string tooltip, System.Drawing.Image image = null) =>
            _panelCoordinator.AddPanelToolbarButton(key, text, tooltip, image);

        private void SyncPanelToggleState() => _panelCoordinator.SyncPanelToggleState();

        private void ApplyTabVisibilityFromSettings() => _panelCoordinator.ApplyTabVisibilityFromSettings();

        // ---- R4: Main Tab Control (Designer + Source tabs) ----

        // ---- Top-level tab strip ----
        //
        // The strip itself is HelixTabControl's business now: head layout, the scroll arrows at
        // either end, hit-testing, the per-tab close box and drag-reorder all live there. What
        // stays here is what only MainForm knows — which tab a close means, and remembering the
        // order a drag produced. Both arrive as events wired where the control is created.

        /// <summary>
        /// Re-derive <see cref="_tabDefaultOrder"/> from the current tab-strip order.
        /// Called after drag-reorder or after applying a persisted layout so that
        /// <see cref="ComputeInsertIndex"/> (used when re-showing a hidden tab) places
        /// tabs into the user's preferred layout rather than the registration order.
        /// </summary>
        private void RebuildTabDefaultOrderFromCurrentStrip()
        {
            int i = 0;
            foreach (TabPage p in _mainTabControl.TabPages)
            {
                if (_tabKeysByPage.TryGetValue(p, out string key))
                    _tabDefaultOrder[key] = i;
                i++;
            }
        }

        // Saves the current left-to-right tab-key order to per-solution settings (no-op without a
        // loaded solution, since order is keyed by the solution's file path).
        private void PersistTopLevelTabOrder()
        {
            if (_settingsManager == null || _currentSolution == null) return;
            List<string> keys = new List<string>();
            foreach (TabPage p in _mainTabControl.TabPages)
            {
                if (_tabKeysByPage.TryGetValue(p, out string key)) keys.Add(key);
            }
            _settingsManager.Settings.SetTopLevelTabOrder(_currentSolution.FilePath, keys);
        }

        // On solution load, reorders the visible tabs to match the saved per-solution order,
        // skipping keys whose tabs are currently hidden/absent, then refreshes the default order.
        private void ApplyTabOrderFromSettings()
        {
            if (_settingsManager == null || _currentSolution == null) return;
            List<string> saved = _settingsManager.Settings.GetTopLevelTabOrder(_currentSolution.FilePath);
            if (saved == null || saved.Count == 0) return;

            TabPage wasSelected = _mainTabControl.SelectedTab;
            _mainTabControl.SuspendLayout();
            try
            {
                int target = 0;
                foreach (string key in saved)
                {
                    if (!_knownTabs.TryGetValue(key, out TabPage page) || page == null) continue;
                    if (!_mainTabControl.TabPages.Contains(page)) continue;
                    int current = _mainTabControl.TabPages.IndexOf(page);
                    if (current != target)
                    {
                        _mainTabControl.TabPages.Remove(page);
                        int insertAt = Math.Min(target, _mainTabControl.TabPages.Count);
                        _mainTabControl.TabPages.Insert(insertAt, page);
                    }
                    target++;
                }
                if (wasSelected != null && _mainTabControl.TabPages.Contains(wasSelected))
                    _mainTabControl.SelectedTab = wasSelected;
            }
            finally
            {
                _mainTabControl.ResumeLayout();
            }
            RebuildTabDefaultOrderFromCurrentStrip();
        }

        // ---------------- Debug: thin wrappers over DebugCoordinator ----------------

        internal void OnDebugStop(object sender, EventArgs e) => _debugCoordinator.Stop();
        internal void OnDebugContinue(object sender, EventArgs e) => _debugCoordinator.Continue();
        internal void OnDebugPause(object sender, EventArgs e) => _debugCoordinator.Pause();
        internal void OnDebugRestart(object sender, EventArgs e) => _debugCoordinator.Restart();
        internal void OnDebugRunToCursor(object sender, EventArgs e) => _debugCoordinator.RunToCursorAtCaret();
        internal void OnDebugStepOver(object sender, EventArgs e) => _debugCoordinator.StepOver();
        internal void OnDebugStepInto(object sender, EventArgs e) => _debugCoordinator.StepInto();
        internal void OnDebugStepOut(object sender, EventArgs e) => _debugCoordinator.StepOut();
        internal void OnDebugToggleBreakpoint(object sender, EventArgs e) => _debugCoordinator.ToggleBreakpoint();

        // Debug exception-filter toggles: flip the coordinator's filter and reflect the new armed
        // state back onto the menu item's checkmark (the coordinator owns the truth, the menu mirrors it).
        internal void OnDebugToggleExceptionRaised(object sender, EventArgs e)
        {
            bool armed = _debugCoordinator.ToggleExceptionFilter("raised");
            if (sender is System.Windows.Forms.ToolStripMenuItem mi) mi.Checked = armed;
        }

        internal void OnDebugToggleExceptionUncaught(object sender, EventArgs e)
        {
            bool armed = _debugCoordinator.ToggleExceptionFilter("uncaught");
            if (sender is System.Windows.Forms.ToolStripMenuItem mi) mi.Checked = armed;
        }

        // Toggles "stop on first line" for launched debug sessions, syncing the menu checkmark.
        internal void OnDebugToggleStopOnEntry(object sender, EventArgs e)
        {
            bool armed = _debugCoordinator.ToggleStopOnEntry();
            if (sender is System.Windows.Forms.ToolStripMenuItem mi) mi.Checked = armed;
        }

        /// <summary>Debug ▸ Break on CUDA Kernel Entry.</summary>
        internal void OnDebugToggleStopOnKernelEntry(object sender, EventArgs e)
        {
            bool armed = _debugCoordinator.ToggleStopOnKernelEntry();
            if (sender is System.Windows.Forms.ToolStripMenuItem mi) mi.Checked = armed;
        }

        /// <summary>Debug ▸ CUDA Debug Lane...</summary>
        internal void OnDebugChooseCudaLane(object sender, EventArgs e)
        {
            _debugCoordinator.ChooseCudaLane();
        }
        // ---------------- Scintilla Editor tab (from HelixPythonNamespace.Editor) ----------------


        /// <summary>
        /// Returns true if the given path ends in <c>pyproject.toml</c>. MSBuild chokes on
        /// those with MSB4025, so callers reroute to a Python toolchain instead.
        /// </summary>
        internal static bool IsPythonProjectFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return string.Equals(Path.GetFileName(path), "pyproject.toml", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Run a Python build/restore command against the directory containing
        /// <paramref name="pyprojectPath"/>. Streams stdout+stderr to the Build Output
        /// panel. When the first run fails with <c>No module named X</c>, prompts the
        /// user to <c>pip install X</c> and retries once. Returns <c>true</c> on
        /// eventual exit code 0.
        /// </summary>
        internal struct ProcessRunResult
        {
            public int ExitCode;
            public string CapturedOutput;
        }

        // Launches an external process off the UI thread, streaming stdout+stderr live into the
        // Build Output panel while also buffering it for the caller. Cancellation kills the whole
        // process TREE (see KillProcessTree) because net472's Process.Kill only kills the immediate
        // child, which previously left orphaned python/pip subprocesses running after Cancel.
        internal async Task<ProcessRunResult> RunProcessAsync(string fileName, string[] args, string workingDir,
            IReadOnlyDictionary<string, string> extraEnv = null,
            System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            // Quote each argument per the Windows CRT rules the child's own argv parser applies.
            // A bare string.Join(" ", args) — what this used to do — silently broke any argument
            // containing a space, which on Windows means most absolute paths: an entry file under
            // "C:\My Projects\" arrived as two arguments and Python reported the wrong filename.
            // Callers therefore pass RAW arguments and must not pre-quote.
            string commandLine = HelixPythonNamespace.Core.CommandLineArguments.Join(args);
            AppendBuildOutputLine("> " + fileName + " " + commandLine);
            var buffer = new System.Text.StringBuilder();
            int exitCode = -1;
            try
            {
                await Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = commandLine,
                        WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    if (extraEnv != null)
                    {
                        foreach (var kv in extraEnv) psi.EnvironmentVariables[kv.Key] = kv.Value;
                    }
                    using (var process = System.Diagnostics.Process.Start(psi))
                    {
                        if (process == null) return;
                        // Stopping a build/run must kill the whole tree: the launched process
                        // (e.g. `python -u -m app`, `python -m build`, pip, PyInstaller) spawns
                        // children, and net472's Process.Kill() only kills the immediate one —
                        // which is why Cancel didn't stop a run. Tree-kill on cancellation.
                        using (cancel.Register(() => { try { KillProcessTree(process); } catch { } }))
                        {
                            process.OutputDataReceived += (s, a) => {
                                if (a.Data != null) { AppendBuildOutputLine(a.Data); lock (buffer) buffer.AppendLine(a.Data); }
                            };
                            process.ErrorDataReceived += (s, a) => {
                                if (a.Data != null) { AppendBuildOutputLine(a.Data); lock (buffer) buffer.AppendLine(a.Data); }
                            };
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            process.WaitForExit();
                            exitCode = process.ExitCode;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                AppendBuildOutputLine("ERROR: " + ex.Message);
            }
            if (cancel.IsCancellationRequested) AppendBuildOutputLine("--- cancelled ---");
            return new ProcessRunResult { ExitCode = exitCode, CapturedOutput = buffer.ToString() };
        }

        /// <summary>
        /// Kill a process and all its descendants. .NET Framework 4.7.2 lacks
        /// <c>Process.Kill(entireProcessTree)</c>; a plain Kill orphans children, so
        /// <c>taskkill /T /F</c> is used to terminate the whole tree (direct-Kill fallback).
        /// </summary>
        internal static void KillProcessTree(System.Diagnostics.Process p)
        {
            if (p == null) return;
            int pid;
            try { pid = p.Id; } catch { return; }
            bool treeKilled = false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("taskkill", "/T /F /PID " + pid)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var tk = System.Diagnostics.Process.Start(psi))
                {
                    if (tk != null) { tk.WaitForExit(3000); treeKilled = tk.HasExited && tk.ExitCode == 0; }
                }
            }
            catch { }
            if (!treeKilled)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
        }

        /// <summary>
        /// Finds a "No module named 'X'" / "No module named X" pattern in the output
        /// and returns <c>X</c>, or null if none found.
        /// </summary>
        internal static string ExtractMissingModule(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(
                output,
                @"No module named ['""]?([A-Za-z_][A-Za-z0-9_.]*)['""]?");
            if (m.Success)
            {
                // pip-install the top-level package name (e.g. `build` for `build.__main__`).
                string name = m.Groups[1].Value;
                int dot = name.IndexOf('.');
                return dot > 0 ? name.Substring(0, dot) : name;
            }
            return null;
        }

        /// <summary>
        /// The full dotted name of the import that failed, taken from the traceback's source line.
        ///
        /// "No module named 'inference'" names only the FIRST segment — that is all Python could
        /// not find, so that is all it reports. The statement itself is a line further up:
        ///
        ///     File "...\main.py", line 30, in execute
        ///         from inference.export_onnx import export_checkpoint
        ///     ModuleNotFoundError: No module named 'inference'
        ///
        /// Diagnosing from the error message alone therefore judges `inference.export_onnx` on the
        /// strength of the word `inference`, which is how a coincidental name match in the
        /// workspace gets announced as the answer. The statement is the evidence; the message is a
        /// summary of it.
        /// </summary>
        internal static string ExtractFailingImport(string output, string topLevel)
        {
            if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(topLevel)) return null;

            // Anchored on the top-level name, so an unrelated import elsewhere in the traceback
            // cannot be mistaken for the one that failed.
            string escaped = System.Text.RegularExpressions.Regex.Escape(topLevel);
            var m = System.Text.RegularExpressions.Regex.Match(
                output,
                @"(?:^|\n)\s*(?:from|import)\s+(" + escaped + @"(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\b");
            return m.Success ? m.Groups[1].Value : null;
        }

        // Registers the built-in default shortcut for every command ID. Users can later override
        // these via KeyBindingsDialog; the defaults are what ships and what "Reset" restores.
        private void SeedDefaultKeybindings()
        {
            _keybindings.RegisterDefault("file.new", "New Project", Keys.Control | Keys.N);
            _keybindings.RegisterDefault("file.open", "Open Solution", Keys.Control | Keys.O);
            _keybindings.RegisterDefault("file.import", "Import Python Directory", Keys.Control | Keys.I);
            _keybindings.RegisterDefault("file.openEditor", "Open File in Editor", Keys.Control | Keys.E);
            // Ships with NO chord, deliberately: every free Ctrl+letter in this menu is spoken for
            // and stealing one would be a worse trade than a menu click. Registered anyway so the
            // command appears in the Keybindings dialog and a user who wants a chord can give it
            // one — and so KeybindingManager.Register below is a real binding rather than the
            // silent no-op it performs for an unknown command id. It is not in KeybindingPresets:
            // those are alternative keymaps and none of them binds it.
            _keybindings.RegisterDefault("file.newNotebook", "New Jupyter Notebook", Keys.None);
            // No default chord: Ctrl+N is New Project, and quietly taking it would change what the
            // most-used creation shortcut in the IDE does. Registered all the same so the command
            // exists in the keybindings editor and a user who lives in single files can bind it.
            _keybindings.RegisterDefault("file.newPythonFile", "New Python File", Keys.None);
            // Ctrl+S saves the active file (the universal expectation). Save Layout — a much
            // rarer action — moves to Ctrl+Shift+S so it no longer hijacks the primary save chord.
            _keybindings.RegisterDefault("file.save", "Save File", Keys.Control | Keys.S);
            // Save All keeps clear of Ctrl+Shift+S (deliberately Save Layout here) and of the
            // Ctrl+Shift+{L,D,T,M,P,B,H} analysis/test chords; Ctrl+Alt+S was free. Remappable in
            // the Keybindings dialog like everything else.
            _keybindings.RegisterDefault("file.saveAll", "Save All Files", Keys.Control | Keys.Alt | Keys.S);
            // Ctrl+W is dispatched in ProcessCmdKey (with Ctrl+F4); registering it here puts the
            // hint on the menu item and lists it in the Keybindings dialog.
            _keybindings.RegisterDefault("file.closeFile", "Close File", Keys.Control | Keys.W);
            _keybindings.RegisterDefault("file.saveLayout", "Save Layout", Keys.Control | Keys.Shift | Keys.S);
            _keybindings.RegisterDefault("debug.start", "Start / Continue Debugging", Keys.F5);
            _keybindings.RegisterDefault("debug.stop", "Stop Debugging", Keys.Shift | Keys.F5);
            _keybindings.RegisterDefault("debug.stepOver", "Step Over", Keys.F10);
            _keybindings.RegisterDefault("debug.stepInto", "Step Into", Keys.F11);
            _keybindings.RegisterDefault("debug.stepOut", "Step Out", Keys.Shift | Keys.F11);
            _keybindings.RegisterDefault("debug.toggleBreakpoint", "Toggle Breakpoint", Keys.F9);
            _keybindings.RegisterDefault("view.panel.designer", "Show Class Designer Canvas", Keys.Control | Keys.D1);
            _keybindings.RegisterDefault("view.panel.editor", "Show Editor", Keys.Control | Keys.D2);
            _keybindings.RegisterDefault("view.panel.debug", "Show Debug Panel", Keys.Control | Keys.D4);
            _keybindings.RegisterDefault("view.panel.analysis", "Show Analysis Results", Keys.Control | Keys.D5);
            _keybindings.RegisterDefault("view.panel.buildResults", "Show Output", Keys.Control | Keys.D6);
            _keybindings.RegisterDefault("view.panel.problems", "Show Compile", Keys.Control | Keys.D7);
            _keybindings.RegisterDefault("build.compile", "Compile (Virtual Compiler)", Keys.F6);
            // Analysis / test / metrics / profiler commands — global actions, so a real chord is
            // correct. These were previously only wired on the dead MenuCoordinator menu, so they
            // neither showed nor worked; register them on the live menu items here.
            _keybindings.RegisterDefault("analysis.lint", "Lint Python Sources", Keys.Control | Keys.Shift | Keys.L);
            _keybindings.RegisterDefault("analysis.deadCode", "Find Dead Code", Keys.Control | Keys.Shift | Keys.D);
            // Ctrl+Shift+R was free; it binds the whole-solution review, which is the entry point
            // of the three (the other two are scoped variants reached from the same submenu).
            _keybindings.RegisterDefault("analysis.codeReview", "Review Whole Solution", Keys.Control | Keys.Shift | Keys.R);
            _keybindings.RegisterDefault("tests.runAll", "Run All Tests", Keys.Control | Keys.Shift | Keys.T);
            _keybindings.RegisterDefault("metrics.compute", "Compute Metrics", Keys.Control | Keys.Shift | Keys.M);
            _keybindings.RegisterDefault("profiler.run", "Profile Active File", Keys.Control | Keys.Shift | Keys.P);
            _keybindings.RegisterDefault("run.startup", "Run Startup App", Keys.Control | Keys.F5);
            _keybindings.RegisterDefault("view.hideSelected", "Hide Selected", Keys.Control | Keys.H);
            _keybindings.RegisterDefault("view.showAllHidden", "Show All Hidden", Keys.Control | Keys.Shift | Keys.H);
            // Editor commands (dispatched in ProcessCmdKey via ChordFor, so they're both listed in the
            // Keybindings dialog and remappable). No menu item, hence RegisterDefault only.
            _keybindings.RegisterDefault("editor.toggleComment", "Toggle Line Comment", Keys.Control | Keys.OemQuestion);
            _keybindings.RegisterDefault("editor.moveLineUp", "Move Line Up", Keys.Alt | Keys.Up);
            _keybindings.RegisterDefault("editor.moveLineDown", "Move Line Down", Keys.Alt | Keys.Down);
            _keybindings.RegisterDefault("editor.duplicateLine", "Duplicate Line", Keys.Control | Keys.D);
            _keybindings.RegisterDefault("editor.goToLine", "Go to Line", Keys.Control | Keys.G);
            // The Packager toolbar button + Build ▸ Packager already advertise Ctrl+Shift+B (VS
            // "Build Solution"); wire it so the tooltip is truthful.
            _keybindings.RegisterDefault("build.packager", "Packager", Keys.Control | Keys.Shift | Keys.B);
        }

        // Opens the modal keybindings editor so the user can view/remap command shortcuts.
        internal void OnShowKeybindings(object sender, EventArgs e)
        {
            using (var dlg = new HelixPythonNamespace.App.Dialogs.KeyBindingsDialog(_keybindings))
                dlg.ShowDialog(this);
        }

        internal readonly HelixPythonNamespace.App.Services.WorkbenchDocumentRegistry _openEditorTabs = new HelixPythonNamespace.App.Services.WorkbenchDocumentRegistry();

        /// <summary>
        /// Owner-draw uniform-width tabs normally truncate long filenames with an ellipsis.
        /// Resize <see cref="TabControl.ItemSize"/> to accommodate the widest current tab so
        /// every filename renders in full. Called after tab add/remove/rename/pin/modify.
        /// </summary>
        /// <summary>
        /// Prompts for a new filename and renames the file on disk, then rewrites every
        /// import across the solution that referenced the old module path. The open tab
        /// is re-pointed at the new path; its editor content is preserved untouched.
        /// </summary>
        /// <summary>
        /// For every .py file in the current solution except <paramref name="excludePath"/>
        /// (usually the file we just renamed — its imports reference other modules, not
        /// itself), rewrite <c>from oldNs[.x] import …</c> and <c>import oldNs[.x]</c>
        /// occurrences so the module path starts with <paramref name="newNs"/>.
        /// Returns (filesChanged, totalHits).
        /// </summary>
        /// <summary>
        /// Owner-draw handler for the inner editor tab strip: paints background,
        /// tab label (with left pin-bullet / right close-icon padding), and the
        /// close icon itself. The tab.Text already carries the pin bullet and
        /// modified-asterisk suffix from EditorTabPage.UpdateTitle.
        /// </summary>
        /// <summary>
        /// Opens the given file in the Editor tab using the Scintilla-backed editor.
        /// If the file is already open, the existing tab is selected. If the editor
        /// tab is not yet visible, it is brought to front.
        /// </summary>
        /// <summary>
        /// Switches to the Class Designer Canvas, routes to the type's category sub-tab,
        /// selects its node, and centers the viewport on it. Shared by every surface
        /// (editor context menu, canvas context menu, project tree) so navigation
        /// stays consistent.
        /// </summary>
        /// <summary>Opens the .py file containing <paramref name="type"/> in an editor tab and moves the cursor to its first line.</summary>
        /// <summary>Opens the .py file containing <paramref name="fn"/> and moves the cursor to its def line.</summary>
        // ---- R2/R3: Undocking and Promotion ----

        internal void OnToggleAnalysisResultsDocking(object sender, EventArgs e) => _buildAnalysisCoordinator.OnToggleAnalysisResultsDocking(sender, e);

        internal void AppendBuildOutputLine(string line) => _buildAnalysisCoordinator.AppendBuildOutputLine(line);

        internal void PopulateEmbeddedBuildResults(List<BuildResultItem> results, bool buildSucceeded) => _buildAnalysisCoordinator.PopulateEmbeddedBuildResults(results, buildSucceeded);
        internal void RenderDiagnosticStore() => _buildAnalysisCoordinator.RenderDiagnosticStore();

        internal void CloseAnalysisWindows() => _buildAnalysisCoordinator.CloseAnalysisWindows();

        // Null-conditional because this runs on the SHUTDOWN path. A MainForm built through the
        // parameterless designer constructor never composes its coordinators — that constructor
        // calls InitializeComponent() and nothing else, and the DI constructor chains to it — so
        // _buildAnalysisCoordinator is null there and quitting threw a NullReferenceException.
        // The other ~90 coordinator delegators are deliberately left un-guarded: outside shutdown a
        // null coordinator means composition is broken, and swallowing that would hide a real bug.
        private void CloseFloatingPanes() => _buildAnalysisCoordinator?.CloseFloatingPanes();

        internal void OnRunAnalysis(object sender, EventArgs e) => _buildAnalysisCoordinator.OnRunAnalysis(sender, e);

        internal void OnFileOpenInEditor(object sender, EventArgs e) => _editorCoordinator.OnFileOpenInEditor(sender, e);

        internal (int filesChanged, int totalHits) RewriteImportsAcrossSolution(string oldNs, string newNs, string excludePath) => _navigationCoordinator.RewriteImportsAcrossSolution(oldNs, newNs, excludePath);

        internal void OnEditorGoToDefinitionRequested(object sender, HelixPythonNamespace.Editor.EditorGoToDefinitionArgs e) => _navigationCoordinator.OnEditorGoToDefinitionRequested(sender, e);

        internal void OnEditorResolveSymbolRequested(object sender, HelixPythonNamespace.Editor.EditorSymbolRequestArgs e) => _navigationCoordinator.OnEditorResolveSymbolRequested(sender, e);

        internal void OnNavigateBackward(object sender, EventArgs e) => _navigationCoordinator.NavigateBackward();

        internal void OnNavigateForward(object sender, EventArgs e) => _navigationCoordinator.NavigateForward();

        internal void RecordNavigationTarget(string filePath, int line, int column) =>
            _navigationCoordinator.RecordNavigationTarget(filePath, line, column);

        internal HelixPythonNamespace.Workspace.Python.SymbolResolver GetSymbolResolver() => _navigationCoordinator.GetSymbolResolver();

        public void GoToDesigner(TypeModel type) => _navigationCoordinator.GoToDesigner(type);

        public void GoToSource(TypeModel type) => _navigationCoordinator.GoToSource(type);

        public void GoToFunction(FunctionModel fn) => _navigationCoordinator.GoToFunction(fn);

        // ---- §5.8: drag a tree item onto the Editor / Designer tab headers ----

        private TabPage TabHeaderAtScreenPoint(int screenX, int screenY)
        {
            Point client = _mainTabControl.PointToClient(new Point(screenX, screenY));
            for (int i = 0; i < _mainTabControl.TabPages.Count; i++)
            {
                if (_mainTabControl.GetTabRect(i).Contains(client)) return _mainTabControl.TabPages[i];
            }
            return null;
        }

        private static bool IsTreeDragPayload(IDataObject data)
        {
            return data != null &&
                   (data.GetDataPresent(HelixPythonNamespace.UI.Controls.ProjectTreePanel.TypeDragFormat) ||
                    data.GetDataPresent(HelixPythonNamespace.UI.Controls.ProjectTreePanel.FunctionDragFormat) ||
                    data.GetDataPresent(HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileDragFormat));
        }

        private bool IsKnownTab(string key, TabPage page)
        {
            return _knownTabs.TryGetValue(key, out TabPage known) && ReferenceEquals(known, page);
        }

        private void OnMainTabsDragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (!IsTreeDragPayload(e.Data)) return;
            TabPage page = TabHeaderAtScreenPoint(e.X, e.Y);
            if (page != null && (IsKnownTab("editor", page) || IsKnownTab("designer", page)))
                e.Effect = DragDropEffects.Copy;
        }

        private void OnMainTabsDragDrop(object sender, DragEventArgs e)
        {
            if (!IsTreeDragPayload(e.Data)) return;
            TabPage page = TabHeaderAtScreenPoint(e.X, e.Y);
            if (page == null) return;
            bool isEditor = IsKnownTab("editor", page);
            bool isDesigner = IsKnownTab("designer", page);
            if (!isEditor && !isDesigner) return;

            var type = e.Data.GetData(HelixPythonNamespace.UI.Controls.ProjectTreePanel.TypeDragFormat) as TypeModel;
            var fn = e.Data.GetData(HelixPythonNamespace.UI.Controls.ProjectTreePanel.FunctionDragFormat) as FunctionModel;
            string filePath = e.Data.GetData(HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileDragFormat) as string;

            if (isEditor)
            {
                ShowTab("editor");
                if (type != null && !string.IsNullOrEmpty(type.SourceFilePath))
                    OpenInEditor(type.SourceFilePath, type.SourceLineNumber);
                else if (fn != null && !string.IsNullOrEmpty(fn.SourceFilePath))
                    OpenInEditor(fn.SourceFilePath, fn.SourceLineNumber);
                else if (!string.IsNullOrEmpty(filePath))
                    OpenInEditor(filePath);
                return;
            }

            object item = type != null ? (object)type
                : fn != null ? (object)fn
                : !string.IsNullOrEmpty(filePath)
                    ? new HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup { FilePath = filePath }
                    : null;
            if (item != null)
                OnTreeOpenInDesigner(this, new HelixPythonNamespace.UI.Controls.TreeOperationEventArgs(null, item));
        }

        // §5.4 "Open in Class Designer Canvas" for ANY tree node: types go to their canvas
        // visual; files/functions/members resolve to a type in their file; projects get
        // checked (visible) and the canvas shown; the solution just shows the canvas.
        internal void OnTreeOpenInDesigner(object sender, HelixPythonNamespace.UI.Controls.TreeOperationEventArgs e)
        {
            if (e == null || e.Item == null) return;

            if (e.Item is TypeModel)
            {
                _navigationCoordinator.OnTreeGoToDesigner(sender, e);
                return;
            }

            TypeModel resolved = null;
            string sourceFile = null;
            if (e.Item is HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup fg) sourceFile = fg.FilePath;
            else if (e.Item is FunctionModel fn) sourceFile = fn.SourceFilePath;
            else if (e.Item is MemberModel && e.Node != null && e.Node.Parent != null)
                resolved = e.Node.Parent.Tag as TypeModel;

            if (resolved == null && !string.IsNullOrEmpty(sourceFile) && _currentSolution != null)
            {
                foreach (var project in _currentSolution.Projects)
                {
                    foreach (var t in project.Types)
                    {
                        if (PathsEqual(t.SourceFilePath, sourceFile)) { resolved = t; break; }
                    }
                    if (resolved != null) break;
                }
            }
            if (resolved != null)
            {
                _navigationCoordinator.OnTreeGoToDesigner(sender,
                    new HelixPythonNamespace.UI.Controls.TreeOperationEventArgs(e.Node, resolved));
                return;
            }

            // Project / folder / solution (or a file with no types): make the owning
            // project visible on the canvas, then bring the canvas tab up.
            ProjectModel owner = e.Item as ProjectModel;
            if (owner == null && e.Item is HelixPythonNamespace.UI.Controls.ProjectTreePanel.FolderGroup folder)
                owner = folder.Project;
            if (owner == null && e.Item is HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup fg2)
                owner = fg2.Project;
            if (owner != null)
            {
                var visible = _projectTreePanel.GetCheckedProjects();
                if (!visible.Contains(owner.Name))
                {
                    visible.Add(owner.Name);
                    _projectTreePanel.SetCheckedProjects(visible);
                    OnProjectVisibilityChanged(_projectTreePanel,
                        new HelixPythonNamespace.UI.Controls.ProjectVisibilityChangedEventArgs(owner.Name, true));
                }
            }
            ShowTab("designer");

            // A specific FILE was targeted (not a project/folder/solution) but it has no classes —
            // the Designer Canvas only shows classes, not free functions, so there is genuinely
            // nothing to navigate to. Making the project visible above is still useful (the user can
            // see its OTHER files' classes), but say so explicitly: without this, clicking a
            // classless file looked exactly like nothing happened at all.
            if (!string.IsNullOrEmpty(sourceFile))
            {
                _statusLabel.Text = string.Format(
                    "{0} has no classes to show on the Designer Canvas (it only defines functions)",
                    Path.GetFileName(sourceFile));
            }
        }

        private ContextMenuStrip CreateCanvasContextMenu() => _canvasCoordinator.CreateCanvasContextMenu();

        private void OnCanvasNodeContextRequested(object sender, NodeContextRequestedEventArgs e) => _canvasCoordinator.OnCanvasNodeContextRequested(sender, e);

        private void OnCommandExecuted(object sender, CommandExecutedEventArgs e) => _canvasCoordinator.OnCommandExecuted(sender, e);

        private void OnNodeDoubleClicked(object sender, NodeVisualEventArgs e) => _canvasCoordinator.OnNodeDoubleClicked(sender, e);

        private void OnSelectionChanged(object sender, EventArgs e) => _canvasCoordinator.OnSelectionChanged(sender, e);

        private void OnTypeDroppedFromTree(object sender, TypeDroppedEventArgs e) => _canvasCoordinator.OnTypeDroppedFromTree(sender, e);

        internal void AddType(TypeKind kind) => _canvasCoordinator.AddType(kind);

        internal void AlignSelected(AlignmentKind kind) => _canvasCoordinator.AlignSelected(kind);

        internal void DistributeSelected(DistributeKind kind) => _canvasCoordinator.DistributeSelected(kind);

        internal void OnAutoLayout(object sender, EventArgs e) => _canvasCoordinator.OnAutoLayout(sender, e);

        internal void OnZoomToFit(object sender, EventArgs e) => _canvasCoordinator.OnZoomToFit(sender, e);

        internal void OnZoomIn(object sender, EventArgs e) => _canvasCoordinator.OnZoomIn(sender, e);

        internal void OnZoomOut(object sender, EventArgs e) => _canvasCoordinator.OnZoomOut(sender, e);

        internal void OnCopyDiagramImage(object sender, EventArgs e) => _canvasCoordinator.OnCopyDiagramImage(sender, e);

        internal void OnRefreshCanvas(object sender, EventArgs e) => _canvasCoordinator.OnRefreshCanvas(sender, e);

        internal void OnGridSettings(object sender, EventArgs e) => _canvasCoordinator.OnGridSettings(sender, e);

        internal void OnLinkSelected(object sender, EventArgs e) => _canvasCoordinator.OnLinkSelected(sender, e);

        private void NewEmptyWorkspace() => _solutionCoordinator.NewEmptyWorkspace();

        // "New" from the toolbar / Welcome screen now opens the single template-driven wizard — the
        // standalone New-Solution dialog was retired (iter 246) so there is one creation path, VS2022-style.
        internal void OnFileNew(object sender, EventArgs e) => OnNewProjectWizard(sender, e);

        // New Project / Solution Wizard (docs/New_project_sln.md): template-driven project creation.
        // With a solution OPEN, the created project must JOIN it — same eligibility-filtered flow as
        // Solution Explorer's "Add New Project..." — never open as a fresh solution replacing it.
        // With no solution open, the created project opens as a new solution.
        private void OnNewProjectWizard(object sender, EventArgs e)
        {
            if (NewProjectShouldJoinOpenSolution())
            {
                OnTreeAddProject(sender, new HelixPythonNamespace.UI.Controls.TreeOperationEventArgs(null, null));
                return;
            }
            RunStandaloneNewProjectWizard();
        }

        // File ▸ "New Solution...": ALWAYS creates a standalone solution and opens it, even
        // while a solution is open — the explicit escape from "New Project joins the open
        // solution" (which otherwise nests each new solution inside the previous one when the
        // user never closes anything).
        internal void OnFileNewSolution(object sender, EventArgs e) => RunStandaloneNewProjectWizard();

        private void RunStandaloneNewProjectWizard()
        {
            using (var wiz = new HelixPythonNamespace.App.Dialogs.NewProjectWizardForm(
                       HelixPythonNamespace.Workspace.Templates.TemplateCatalog.Default(), _settingsManager))
            {
                wiz.SetStandaloneSolutionMode(_currentSolution != null
                    ? _currentSolution.SolutionDirectory
                    : null);
                if (wiz.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(wiz.CreatedProjectRoot))
                    OpenCreatedProject(wiz.CreatedProjectRoot,
                        wiz.SelectedTemplate != null ? wiz.SelectedTemplate.Id : null);
            }
        }

        // Open whatever the wizard just created, whatever its language. Every language must be
        // covered here or its templates LOOK broken: the files land on disk but the IDE opens
        // NOTHING (reported in turn for CUDA, then for every manifest language the combined
        // build shipped). Python and CMake keep their dedicated routes; everything else defers to
        // the language-services registry, the same dispatch File ▸ Open Solution uses.
        internal void OpenCreatedProject(string projectRoot)
        {
            OpenCreatedProject(projectRoot, null);
        }

        internal void OpenCreatedProject(string projectRoot, string templateId)
        {
            string anchor = FindOpenableProjectAnchor(projectRoot);
            if (anchor == null)
            {
                _statusLabel.Text = "Created project at " + projectRoot +
                                    " — no openable project manifest found; open it via File ▸ Open.";
                return;
            }
            if (string.Equals(Path.GetFileName(anchor), "pyproject.toml", StringComparison.OrdinalIgnoreCase))
            {
                // A notebook solution is a library by design, so it has no startup entry. Carry
                // its generated document through the async solution load and open it only after
                // the workspace/tree are adopted. Otherwise the wizard appears to create only a
                // Python library even though the .ipynb is present under Resources.
                string createdDocument = CreatedTemplateDocument(projectRoot, templateId);
                OpenSolutionAsync(anchor, openStartupEntry: createdDocument == null,
                    fileToOpen: createdDocument);
            }
            else if (string.Equals(Path.GetFileName(anchor), "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
            {
                LoadCMakeFolderAsNativeProject(projectRoot);
            }
            else
            {
                OpenSolutionAsync(anchor);   // registry-recognized manifest → its language provider
            }
        }

        internal static string CreatedTemplateDocument(string projectRoot, string templateId)
        {
            if (!string.Equals(templateId, "py.notebook", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(projectRoot)) return null;
            string normalized = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.Combine(normalized, Path.GetFileName(normalized) + ".ipynb");
        }

        /// <summary>The file that makes a created project directory openable: pyproject.toml,
        /// CMakeLists.txt, or any top-level manifest the language registry recognizes
        /// (Cargo.toml, *.csproj, pom.xml, Project.toml, ...). Null when none exists.</summary>
        internal static string FindOpenableProjectAnchor(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot)) return null;
            string pyproj = Path.Combine(projectRoot, "pyproject.toml");
            if (File.Exists(pyproj)) return pyproj;
            string cmake = Path.Combine(projectRoot, "CMakeLists.txt");
            if (File.Exists(cmake)) return cmake;
            string[] files;
            try { files = Directory.GetFiles(projectRoot); } catch { return null; }
            foreach (string f in files)
            {
                if (HelixPythonNamespace.Workspace.LanguageServices.LanguageWorkspaceRegistry.IsRecognizedProjectPath(f))
                    return f;
            }
            return null;
        }

        /// <summary>True when a Python solution is open, so a wizard-created project joins it
        /// rather than opening as a new solution.</summary>
        internal bool NewProjectShouldJoinOpenSolution()
        {
            return _currentSolution != null && !string.IsNullOrEmpty(_currentSolution.FilePath);
        }

        internal void OnFileOpen(object sender, EventArgs e) => _solutionCoordinator.OnFileOpen(sender, e);

        /// <summary>
        /// File ▸ "Open..." — ONE command for the two things this IDE can open.
        ///
        /// <para>
        /// There is no reason to make the user classify their own file before the dialog will show
        /// it to them. A `pyproject.toml` and an `.ipynb` are told apart by their names, and this
        /// process already does exactly that for the Recent list, where a notebook row and a
        /// solution row sit together and <see cref="OpenRecentPath"/> routes each correctly. Two
        /// separate Open commands asked the user to answer a question the program can answer.
        /// </para>
        /// </summary>
        internal void OnFileOpenAnything(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = OpenFileFilter();
                dlg.Title = "Open";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                OpenRecentPath(dlg.FileName);
            }
        }

        /// <summary>
        /// File ▸ Open's file types: the shell's own rows, then each loaded add-in's
        /// (<c>IAddInHost.AddOpenFileFilter</c>, for example the PDB Viewer's structures), then All
        /// Files. The first row offers everything at once, the add-ins' patterns included.
        /// </summary>
        internal string OpenFileFilter()
        {
            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Python Solution (pyproject.toml)", "pyproject.toml"),
                new KeyValuePair<string, string>("Canvas designs (*.canvas)", "*.canvas"),
                new KeyValuePair<string, string>("Python Files (*.py;*.pyw)", "*.py;*.pyw"),
                new KeyValuePair<string, string>("Jupyter Notebooks (*.ipynb)", "*.ipynb"),
            };
            rows.AddRange(AddInOpenFileFilters());

            var all = new List<string>();
            foreach (KeyValuePair<string, string> row in rows)
                foreach (string pattern in row.Value.Split(';'))
                    if (pattern.Length > 0 && !all.Contains(pattern, StringComparer.OrdinalIgnoreCase)) all.Add(pattern);

            var filter = new System.Text.StringBuilder("All supported files|" + string.Join(";", all));
            foreach (KeyValuePair<string, string> row in rows) filter.Append('|').Append(row.Key).Append('|').Append(row.Value);
            filter.Append("|All Files (*.*)|*.*");
            return filter.ToString();
        }

        internal void OnFileClose(object sender, EventArgs e) => _solutionCoordinator.OnFileClose(sender, e);

        internal void OnFileImportDirectory(object sender, EventArgs e) => _solutionCoordinator.OnFileImportDirectory(sender, e);

        private void OnFormDragEnter(object sender, DragEventArgs e) => _solutionCoordinator.OnFormDragEnter(sender, e);

        private void OnFormDragDrop(object sender, DragEventArgs e) => _solutionCoordinator.OnFormDragDrop(sender, e);

        internal void OpenSolutionAsync(string slnPath) => _solutionCoordinator.OpenSolutionAsync(slnPath);
        internal void OpenSolutionAsync(string slnPath, bool openStartupEntry) => _solutionCoordinator.OpenSolutionAsync(slnPath, openStartupEntry);
        internal void OpenSolutionAsync(string slnPath, bool openStartupEntry, string fileToOpen) =>
            _solutionCoordinator.OpenSolutionAsync(slnPath, openStartupEntry, fileToOpen);

        // The path Explorer (or a command line) handed this process, held until the form is shown.
        private string _pendingShellOpenPath;

        /// <summary>
        /// Remember a path to open once the window exists. Called by <c>Program.Main</c> BEFORE
        /// <c>Application.Run</c>, when the form has no handle yet and nothing can be marshalled to
        /// the UI thread: opening there would run the whole solution load against an unrealized
        /// form. Deferring to <c>Shown</c> is what makes the double-click path exercise the same
        /// code a menu open does.
        /// </summary>
        internal void QueueShellOpen(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _pendingShellOpenPath = path;
            Shown += OnShownOpenPendingShellPath;
        }

        private void OnShownOpenPendingShellPath(object sender, EventArgs e)
        {
            Shown -= OnShownOpenPendingShellPath;      // one shot; a re-show must not reopen
            string path = _pendingShellOpenPath;
            _pendingShellOpenPath = null;
            if (path != null) OpenShellPath(path);
        }

        /// <summary>
        /// Open whatever the shell handed us. A <c>.toml</c> double-click is answered with the
        /// SOLUTION that owns the file, not the file — that widening is
        /// <see cref="HelixPythonNamespace.Workspace.Services.SolutionAnchorResolver"/>'s job, because the
        /// file Explorer passes may be a member package's manifest or an unrelated config sitting
        /// inside the tree. Only when nothing above it is a solution does the file itself open.
        /// </summary>
        internal void OpenShellPath(string path)
        {
            // A design this IDE wrote opens on the canvas that wrote it, before the solution
            // resolver ever sees it. Both formats are .json, so the resolver would widen to the
            // owning solution or fall through to the text editor, and the user who double-clicked
            // their simulation would get a wall of serialised nodes instead of their diagram.
            if (OpenDesignSurfaceFor(path)) return;

            var anchor = HelixPythonNamespace.Workspace.Services.SolutionAnchorResolver.Resolve(path);
            if (_statusLabel != null) _statusLabel.Text = anchor.Reason;

            if (!string.IsNullOrEmpty(anchor.SolutionPath))
            {
                OpenSolutionAsync(anchor.SolutionPath);
                return;
            }
            if (!string.IsNullOrEmpty(anchor.FileToOpen))
            {
                // A .py with no solution above it is exactly what single-file mode is for, and it
                // must not merely land in the editor: an editor tab with no mode behind it has no
                // Run target, no rule set and nothing in the status bar to say so — the file looks
                // open and every command that acts on it says "No solution loaded".
                //
                // The resolver has already widened to a solution where one exists, so a file that
                // reaches here genuinely has none above it. That check comes first for a reason:
                // a .py INSIDE a solution belongs to that solution and its own rules, and opening
                // it as a loose file would be handing the user PY8005 for imports that are correct.
                if (IsLooseSourceFile(anchor.FileToOpen)) OpenSingleFile(anchor.FileToOpen);
                else OpenInEditor(anchor.FileToOpen);
            }
        }

        /// <summary>
        /// Opens <paramref name="path"/> on its designer canvas when it is a design file, and
        /// reports whether it did.
        ///
        /// <para>Matched on the compound extension rather than on ".json" alone: the extensions
        /// are the repositories' own constants, so a format that is renamed cannot leave this
        /// silently opening the old one in a text editor.</para>
        /// </summary>
        private bool OpenDesignSurfaceFor(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            // One binary format for every canvas: the header, not the name, says which designer opens
            // it (Task 10i). A refused file has still claimed its path: the text editor and the
            // solution loader must not be handed a binary file.
            if (HelixPythonNamespace.Core.IO.CanvasFile.IsCanvasPath(path))
            {
                OpenCanvas(path);
                return true;
            }

            // Add-ins first (spec 001, §3 step 3): a file type an add-in registered is its to open.
            if (OpenWithAddIn(path)) return true;

            return false;
        }

        internal void OnFileSaveLayout(object sender, EventArgs e) => _solutionCoordinator.OnFileSaveLayout(sender, e);

        /// <summary>
        /// File ▸ Save (Ctrl+S): write the active editor document to disk now. Edits already
        /// autosave on a debounce, but the universal Ctrl+S reflex must save the current file
        /// immediately (and confirm it) rather than persisting the window layout. Falls back to a
        /// status message when no editor tab is active or the buffer has no on-disk path yet.
        /// </summary>
        internal void OnFileSave(object sender, EventArgs e)
        {
            TabPage selected = _editorInnerTabControl != null
                ? _editorInnerTabControl.SelectedTab
                : null;
            var notebook = selected as HelixPythonNamespace.Editor.Notebook.NotebookTabPage;
            if (notebook != null)
            {
                string notebookName = string.IsNullOrEmpty(notebook.FilePath)
                    ? "Untitled.ipynb"
                    : Path.GetFileName(notebook.FilePath);
                try
                {
                    // Save commits the live cell editor before writing the notebook model. Calling
                    // FlushPendingSave here would do nothing when the only dirty text still lives
                    // in that editor and the tab has not received its Modified event yet.
                    bool saved = notebook.Save();
                    _statusLabel.Text = saved ? "Saved " + notebookName : "Save failed: " + notebookName;
                }
                catch (Exception ex) { _statusLabel.Text = "Save failed: " + ex.Message; }
                UpdateTitle();
                return;
            }

            HelixPythonNamespace.Editor.EditorTabPage etp = _editorInnerTabControl != null
                ? selected as HelixPythonNamespace.Editor.EditorTabPage
                : null;
            if (etp == null) { _statusLabel.Text = "No file to save"; return; }

            string name = ActiveEditorFileName() ?? "file";
            if (etp.IsNewFile || string.IsNullOrEmpty(etp.FilePath))
            {
                _statusLabel.Text = "\"" + name + "\" has no file path yet — use the editor to save it.";
                return;
            }
            if (!etp.IsModified) { _statusLabel.Text = name + " — no changes to save"; return; }

            try
            {
                etp.FlushPendingSave();               // cancels the debounce and writes if dirty
                _statusLabel.Text = etp.IsModified ? "Save failed: " + name : "Saved " + name;
            }
            catch (Exception ex) { _statusLabel.Text = "Save failed: " + ex.Message; }
            UpdateTitle();
        }

        private void SaveLayout() => _solutionCoordinator.SaveLayout();

        internal void RefreshRecentFilesMenu() => _solutionCoordinator.RefreshRecentFilesMenu();

        internal void ReloadSolutionAsync() => _solutionCoordinator.ReloadSolutionAsync();
        internal void ReloadSolutionAsync(string fileToOpen) => _solutionCoordinator.ReloadSolutionAsync(fileToOpen);

        internal void OnRunStartupApp(object sender, EventArgs e) => _buildRunCoordinator.OnRunStartupApp(sender, e);
        internal void OnOpenTerminal(object sender, EventArgs e) => _buildRunCoordinator.OnOpenTerminal();

        /// <summary>Renders a failed run's traceback into the navigable Build Errors list.</summary>
        internal void PublishPythonErrors(string capturedOutput) => _buildRunCoordinator.PublishPythonErrors(capturedOutput);

        internal void OnBuildSolution(object sender, EventArgs e) => _buildRunCoordinator.OnBuildSolution(sender, e);

        internal void OnBuildToolSettings(object sender, EventArgs e) => _buildRunCoordinator.OnBuildToolSettings(sender, e);

        internal void OnSelectPythonInterpreter(object sender, EventArgs e) => _buildRunCoordinator.OnSelectPythonInterpreter(sender, e);

        internal void OnBuildViaCpp(object sender, EventArgs e) => _buildRunCoordinator.OnBuildViaCpp(sender, e);

        internal void OnCompile(object sender, EventArgs e) => _buildRunCoordinator.OnCompile(sender, e);

        internal Task<bool> RunPythonCommandAsync(string pyprojectPath, string[] pyArgs, string verbForStatus, IReadOnlyDictionary<string, string> extraEnv = null) => _buildRunCoordinator.RunPythonCommandAsync(pyprojectPath, pyArgs, verbForStatus, extraEnv);

        // ---- Single-file mode -------------------------------------------------------------
        //
        // One Python file, open on its own, with no solution. See SingleFileCoordinator for why
        // it is a mode rather than a second kind of document, and why PY8005 replaces PY8001
        // rather than joining it.

        /// <summary>True while a file is open on its own, outside any solution.</summary>
        internal bool IsSingleFileMode
        {
            get { return _singleFileCoordinator != null && _singleFileCoordinator.IsActive; }
        }

        /// <summary>The loose file currently open, or null.</summary>
        internal string SingleFilePath
        {
            get { return _singleFileCoordinator == null ? null : _singleFileCoordinator.ActiveFile; }
        }

        internal void OnFileNewPythonFile(object sender, EventArgs e) => _singleFileCoordinator.NewFile();

        internal Task RunSingleFileFromMenuAsync() => _singleFileCoordinator.RunAsync();

        internal void DebugSingleFileFromMenu() => _singleFileCoordinator.Debug();

        /// <summary>Build ▸ Compile in single-file mode: check the loose file and show the result.</summary>
        internal void CheckSingleFileFromMenu() => _singleFileCoordinator.Check(reportClean: false);

        /// <summary>Open a loose .py. False when the user declined to close an open solution.</summary>
        internal bool OpenSingleFile(string path) => _singleFileCoordinator.Open(path);

        internal void CloseSingleFile() => _singleFileCoordinator.Close();

        internal bool IsNonInteractive() => IsNonInteractiveHost();

        /// <summary>
        /// True when a real solution is open — one with a manifest on disk, as opposed to the
        /// blank in-memory workspace the IDE starts on. Same predicate New Project uses to decide
        /// whether it is joining something.
        /// </summary>
        internal bool HasOpenSolution() => NewProjectShouldJoinOpenSolution();

        /// <summary>
        /// Put the open solution away so a loose file can take its place. Goes through the same
        /// File ▸ Close the user would have used, so the unsaved-work prompt, the panel teardown
        /// and the recent-list bookkeeping all happen exactly once and in one place.
        /// </summary>
        internal void CloseSolutionForSingleFile() => _solutionCoordinator.OnFileClose(this, EventArgs.Empty);

        internal void ShowBuildResultsTabFromSingleFile() => _buildRunCoordinator.ShowBuildResultsTab();

        /// <summary>
        /// Put diagnostics in the Problems panel WITHOUT bringing it out in front of the user.
        ///
        /// <see cref="FinishCompile"/> re-adds a closed Problems tab, which also selects it. That
        /// is right for a compile the user asked for and wrong for the check that runs when a file
        /// is merely opened: asking to open a file and being shown an empty grid instead of the
        /// file is not an answer to what was asked.
        /// </summary>
        internal void PublishDiagnosticsQuietly(
            List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> diagnostics)
        {
            if (_problemsPanel != null) _problemsPanel.ShowDiagnostics(diagnostics);
        }

        /// <summary>
        /// Run a loose file with a <c>sys.path</c> of exactly its own directory plus the
        /// installation.
        ///
        /// <para>
        /// PYTHONPATH is REMOVED, not set empty, and the difference is not pedantic: an empty
        /// PYTHONPATH contributes an empty entry to <c>sys.path</c>, and CPython resolves an empty
        /// entry to the current working directory. Clearing it "safely" would therefore add back a
        /// root, and PY8005's enumeration of the ways a lone script can reach user-written code —
        /// which is what makes the rule a guarantee rather than a hope — would be incomplete.
        /// Assigning null to a <c>ProcessStartInfo.EnvironmentVariables</c> entry deletes it.
        /// </para>
        /// </summary>
        internal Task<bool> RunSingleFileAsync(string file, string workingDirectory)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "PYTHONPATH", null },
            };
            return _buildRunCoordinator.RunPythonCommandAsync(
                file, new[] { file }, "run", env, workingDirectory);
        }

        /// <summary>Debug a loose file, with the same cleared import path the run uses.</summary>
        internal void DebugSingleFile(string file, string workingDirectory) =>
            _debugCoordinator.StartSingleFile(file, workingDirectory);

        /// <summary>
        /// Re-sync whatever the shell shows differently with and without a loose file open. Called
        /// on entering and leaving the mode.
        /// </summary>
        internal void UpdateSingleFileUi()
        {
            UpdateTitle();
            // Run, Debug and Compile are solution-gated, and single-file mode is the one workflow
            // that has none. Without this the feature is unreachable: the file opens, and every
            // command that could act on it is off the menu bar and off the toolbar.
            UpdateMenuWorkflowVisibility();
            if (_statusLabel == null) return;
            if (IsSingleFileMode)
            {
                // The status bar is the one place that says which world the window is in. Without
                // it, a loose file looks exactly like a solution with an empty tree, and the first
                // surprise is a Run that refuses over a rule the user has no reason to expect.
                _statusLabel.Text = "Single file: " + System.IO.Path.GetFileName(SingleFilePath)
                    + " - installed packages only (no solution open)";
            }
            else if (!HasOpenSolution())
            {
                _statusLabel.Text = "Ready - No solution loaded";
            }
        }

        // ---- Helpers used by EngineCoordinator (lint/dead-code/metrics/profiler/tests) ----
        internal List<HelixPythonNamespace.VirtualCompiler.WorkspaceFile> EnumerateSolutionWorkspaceFiles(SolutionModel sol) =>
            _buildRunCoordinator.EnumerateSolutionWorkspaceFiles(sol);

        internal string FindPythonInterpreter() => _buildRunCoordinator.FindPythonInterpreter();

        /// <summary>Path of the file in the currently-selected editor tab, or null.</summary>
        internal string ActiveEditorFilePath()
        {
            var etp = _editorInnerTabControl != null
                ? _editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.EditorTabPage
                : null;
            return etp != null ? etp.FilePath : null;
        }

        // Engine handlers (Analysis ▸ Lint / Dead Code, Metrics, Profiler, Tests, Git).
        internal void OnRunLint(object sender, EventArgs e) => _engineCoordinator.OnRunLint(sender, e);
        internal void OnFindDeadCode(object sender, EventArgs e) => _engineCoordinator.OnFindDeadCode(sender, e);
        internal void OnComputeMetrics(object sender, EventArgs e) => _engineCoordinator.OnComputeMetrics(sender, e);
        internal void OnProfileActiveFile(object sender, EventArgs e) => _engineCoordinator.OnProfileActiveFile(sender, e);
        internal void OnRunAllTests(object sender, EventArgs e) => _engineCoordinator.OnRunAllTests(sender, e);
        internal void OnRunFailedTests(object sender, EventArgs e) => _engineCoordinator.OnRunFailedTests(sender, e);
        internal void OnStopTests(object sender, EventArgs e) => _engineCoordinator.OnStopTests(sender, e);
        internal void OnClearTests(object sender, EventArgs e) => _engineCoordinator.OnClearTests(sender, e);
        internal void OnManageGitIgnore(object sender, EventArgs e) => _engineCoordinator.OnManageGitIgnore(sender, e);
        internal void OnPersistRepo(object sender, EventArgs e) => _engineCoordinator.OnPersistRepo(sender, e);

        private void InitializeEditorTab() => _editorCoordinator.InitializeEditorTab();

        public void OpenInEditor(string filePath, int line = -1, bool activate = true, int column = -1) => _editorCoordinator.OpenInEditor(filePath, line, activate, column);

        /// <summary>
        /// Register a closable top-level tab for an engine result panel, then hide it by
        /// default (the engine command re-shows it via <see cref="ShowTab"/>). Mirrors the
        /// Hidden Elements tab pattern so saved visibility preferences are honoured.
        /// </summary>
        private void RegisterHiddenEngineTab(string key, string title, string tooltip, Control content)
        {
            var tab = new TabPage(title) { ToolTipText = tooltip };
            tab.Controls.Add(content);
            _mainTabControl.TabPages.Add(tab);
            RegisterTopLevelTab(key, tab);
            _mainTabControl.TabPages.Remove(tab);
        }

        private void RecomputeEditorTabWidths() => _editorCoordinator.RecomputeEditorTabWidths();

        internal void SaveOpenEditorFilesForSolution(string solutionPath) => _editorCoordinator.SaveOpenEditorFilesForSolution(solutionPath);

        internal void CloseAllEditorTabs(bool includePinned) => _editorCoordinator.CloseAllEditorTabs(includePinned);

        internal void RestoreLastOpenEditorFiles(string solutionPath) => _editorCoordinator.RestoreLastOpenEditorFiles(solutionPath);

        internal void OnToggleBuildResultsDocking(object sender, EventArgs e) => _buildAnalysisCoordinator.OnToggleBuildResultsDocking(sender, e);
        /// <summary>
        /// R3: When any of B/C/D is undocked, A's sub-tabs become top-level.
        /// When all are re-docked, reverse the promotion.
        /// </summary>
        internal void UpdateTabPromotion()
        {
            bool anyUndocked = _analysisResultsUndocked || _buildResultsUndocked;

            if (anyUndocked && !_isPromotedMode)
            {
                // Promote: remove Class Designer Canvas wrapper, add sub-tabs as top-level
                _mainTabControl.SuspendLayout();

                // Remove the wrapper tab
                _mainTabControl.TabPages.Remove(_graphicalDesignerTab);

                // Move the canvas tab from the inner tab control to a top-level tab page
                _designerInnerTabControl.TabPages.Remove(_designerTab);
                _mainTabControl.TabPages.Insert(0, _designerTab);

                _isPromotedMode = true;
                _mainTabControl.ResumeLayout(true);

                // Select the All tab
                _mainTabControl.SelectedTab = _designerTab;
            }
            else if (!anyUndocked && _isPromotedMode)
            {
                // Demote: move sub-tabs back into the inner tab control
                _mainTabControl.SuspendLayout();

                _mainTabControl.TabPages.Remove(_designerTab);
                _designerInnerTabControl.TabPages.Add(_designerTab);

                // Re-add the wrapper tab at position 0
                _mainTabControl.TabPages.Insert(0, _graphicalDesignerTab);

                _isPromotedMode = false;
                _mainTabControl.ResumeLayout(true);

                // Select the wrapper tab
                _mainTabControl.SelectedTab = _graphicalDesignerTab;
            }
        }

        /// <summary>
        /// Insert a tab at its natural position based on the fixed order:
        /// Class Designer Canvas (or promoted tabs), Analysis Results, Build Results
        /// </summary>
        internal void InsertTabInOrder(TabPage tab)
        {
            // A dock/undock cycle can race with a View > Panels toggle: the toggle can already
            // have re-inserted this same TabPage while it was still (nominally) floating, so by
            // the time redock runs, the tab may already be back in the strip. TabPages.Insert
            // does not dedupe like a plain Controls collection — inserting an already-present
            // page adds a second, visually duplicate header for the same tab. Guard it here so
            // every caller (redock, and anything else routed through this helper) is safe.
            if (_mainTabControl.TabPages.Contains(tab))
            {
                _mainTabControl.SelectedTab = tab;
                return;
            }
            int targetIndex = GetTabInsertIndex(tab);
            if (targetIndex >= _mainTabControl.TabPages.Count)
            {
                _mainTabControl.TabPages.Add(tab);
            }
            else
            {
                _mainTabControl.TabPages.Insert(targetIndex, tab);
            }
        }

        // Computes the strip index at which a re-shown Analysis/Build tab should slot back in,
        // keeping the canonical left-to-right order (designer=0, analysis=2, build=3) intact.
        private int GetTabInsertIndex(TabPage tab)
        {
            // Define the natural order
            int tabOrder;
            if (tab == _analysisResultsTab) tabOrder = 2;
            else if (tab == _buildResultsTab) tabOrder = 3;
            else return _mainTabControl.TabPages.Count;

            // Find where to insert based on existing tabs
            for (int i = 0; i < _mainTabControl.TabPages.Count; i++)
            {
                TabPage existing = _mainTabControl.TabPages[i];
                int existingOrder;
                if (existing == _graphicalDesignerTab || existing == _designerTab) existingOrder = 0;
                else if (existing == _analysisResultsTab) existingOrder = 2;
                else if (existing == _buildResultsTab) existingOrder = 3;
                else existingOrder = 4;

                if (existingOrder > tabOrder)
                {
                    return i;
                }
            }
            return _mainTabControl.TabPages.Count;
        }

        // ---- R1.5: Output Error Activation ----

        // Double-clicking a build error/warning row jumps to the offending file+line in the editor.
        // Column layout: [2]=File, [3]=Line (see the ListView column setup in InitializeRuntimeComponent).
        private void OnBuildErrorDoubleClick(object sender, EventArgs e)
        {
            ActivateSelectedBuildError();
        }

        internal void OnBuildErrorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            ActivateSelectedBuildError();
        }

        internal void ActivateSelectedBuildError()
        {
            if (_buildErrorsListView.SelectedItems.Count == 0) return;

            var item = _buildErrorsListView.SelectedItems[0];
            BuildResultItem diagnostic = item.Tag as BuildResultItem;
            string filePath = diagnostic != null
                ? diagnostic.File
                : (item.SubItems.Count > 2 ? item.SubItems[2].Text : null);
            int line = diagnostic != null ? diagnostic.Line : 0;
            int column = diagnostic != null ? diagnostic.Column : 0;
            if (diagnostic == null && item.SubItems.Count > 3)
            {
                int.TryParse(item.SubItems[3].Text, out line);
            }

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                OpenInEditor(filePath, line, true, column);
            }
        }

        // ---- R1/R2: Project Explorer Dock/Float ----

        internal void OnDockExplorerLeft(object sender, EventArgs e) => _explorerCoordinator.OnDockExplorerLeft(sender, e);

        internal void OnDockExplorerRight(object sender, EventArgs e) => _explorerCoordinator.OnDockExplorerRight(sender, e);

        internal void OnFloatExplorer(object sender, EventArgs e) => _explorerCoordinator.OnFloatExplorer(sender, e);

        /// <summary>The pointer left an unpinned panel. For tests; the product wires this to the panel.</summary>
        internal void OnLeftPanelAutoHideForTests() => _explorerCoordinator.OnLeftPanelAutoHide(this, EventArgs.Empty);

        /// <summary>The pointer came back. For tests.</summary>
        internal void OnLeftPanelAutoShowForTests() => _explorerCoordinator.OnLeftPanelAutoShow(this, EventArgs.Empty);

        // ---- Tree CRUD Event Handlers ----

        // Solution Explorer "Add Class" context command — the tree passes the clicked node's tag
        // as e.Item (project or file group); forwards to AddTypeToProject with a concrete-class kind.
        private void OnTreeAddClass(object sender, TreeOperationEventArgs e)
        {
            if (TryResolveAddTypeTarget(e.Item, out ProjectModel project, out string ns))
                AddTypeToProject(project, TypeKind.ConcreteClass, ns);
        }

        // Solution Explorer "Add Interface" context command; same pattern with an interface kind.
        private void OnTreeAddInterface(object sender, TreeOperationEventArgs e)
        {
            if (TryResolveAddTypeTarget(e.Item, out ProjectModel project, out string ns))
                AddTypeToProject(project, TypeKind.Interface, ns);
        }

        // Solution Explorer "Add Abstract Class" context command; same pattern — AddTypeToProject
        // already fully supports TypeKind.AbstractClass (naming + TypeService.AddClass(isAbstract:
        // true)), matching what the canvas's "Add <Type>" context menu has offered all along.
        private void OnTreeAddAbstractClass(object sender, TreeOperationEventArgs e)
        {
            if (TryResolveAddTypeTarget(e.Item, out ProjectModel project, out string ns))
                AddTypeToProject(project, TypeKind.AbstractClass, ns);
        }

        // §5.7: Add Class/Interface creates the element under the exact node the command was
        // invoked from. A project node targets its default package (ns null); a file node targets
        // the clicked file's own package, derived from its directory under the project's src tree;
        // a folder node targets that folder as the package.
        internal static bool TryResolveAddTypeTarget(object item, out ProjectModel project, out string ns)
        {
            project = item as ProjectModel;
            ns = null;
            if (project != null) return true;

            var fg = item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup;
            if (fg != null && fg.Project != null)
            {
                project = fg.Project;
                ns = HelixPythonNamespace.Workspace.Services.TypeService.PackageNamespaceForFile(fg.Project, fg.FilePath);
                return true;
            }

            var folder = item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FolderGroup;
            if (folder != null && folder.Project != null)
            {
                project = folder.Project;
                ns = NamespaceForFolder(folder);
                return true;
            }
            return false;
        }

        // Maps a tree folder (project-relative path, '/'-separated) to the package namespace a new
        // type created in it should join. Mirrors TypeService.PackageNamespaceForFile.
        //
        // The folder's own path IS the namespace: the canonical layout puts a package directly
        // under its project, so `sympy/core` is `sympy.core`. A leading `src` is stripped when
        // present, because that was the older layout and those projects still open. (This comment
        // used to say only folders under `src` map at all, which stopped being true when the
        // scaffolder was flattened — the rule below had already changed.)
        internal static string NamespaceForFolder(HelixPythonNamespace.UI.Controls.ProjectTreePanel.FolderGroup folder)
        {
            if (folder == null || string.IsNullOrEmpty(folder.RelativePath)) return null;
            string[] segments = folder.RelativePath.Replace('\\', '/')
                .Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;
            int first = string.Equals(segments[0], "src", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (segments.Length <= first) return null;
            return string.Join(".", segments, first, segments.Length - first);
        }

        // Adds a new type of the given kind to a project, choosing a non-colliding default name
        // (INew/AbstractNew/NewClass) by appending an incrementing suffix until it's unique.
        // ns overrides the project's default namespace (the §5.7 file-node case); null keeps it.
        private void AddTypeToProject(ProjectModel project, TypeKind kind, string ns = null)
        {
            if (_currentSolution == null) return;

            string prefix;
            switch (kind)
            {
                case TypeKind.Interface:
                    prefix = "INew";
                    break;
                case TypeKind.AbstractClass:
                    prefix = "AbstractNew";
                    break;
                default:
                    prefix = "NewClass";
                    break;
            }

            string targetNs = !string.IsNullOrEmpty(ns) ? ns : project.DefaultNamespace;
            string baseName = prefix;
            string name = baseName;
            int counter = 1;
            while (_currentSolution.FindType(targetNs + "." + name) != null)
            {
                counter++;
                name = baseName + counter;
            }

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    TypeModel newType;
                    if (project.Language == ProjectLanguage.Cpp)
                    {
                        // C++ scaffolding (writes a .hpp + .cpp pair via CppSourceEmitter) instead
                        // of Python's .py-file TypeService — same TypeModel/TypeKind contract, so
                        // everything below (layout, projection, tree, status) needs no branching.
                        newType = kind == TypeKind.Interface
                            ? _cppTypeService.AddInterface(project, name, targetNs)
                            : _cppTypeService.AddClass(project, name, targetNs, kind == TypeKind.AbstractClass);
                    }
                    else if (kind == TypeKind.Interface)
                    {
                        newType = _typeService.AddInterface(project, name, targetNs);
                    }
                    else
                    {
                        newType = _typeService.AddClass(project, name, targetNs, kind == TypeKind.AbstractClass);
                    }

                    var layout = new NodeLayoutData
                    {
                        PositionX = 100,
                        PositionY = 100,
                        Width = 200,
                        Height = 150
                    };

                    if (_layoutStore != null)
                    {
                        _layoutStore.SetPosition(newType.FullyQualifiedName, layout);
                    }

                    if (_projectionBuilder != null)
                    {
                        _projectionBuilder.AddType(newType);
                    }
                    _projectTreePanel.AddType(newType);
                    _canvas.Invalidate();

                    _statusLabel.Text = string.Format("Added {0}: {1}", kind, newType.Name);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to create type: {0}", ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        // Python tooling is byte-order-mark averse, so every file this form writes uses UTF-8 *without* a BOM.
        internal static readonly UTF8Encoding Utf8NoBomForNewFiles = new UTF8Encoding(false);

        // Solution Explorer "Add File": prompts for a .py path, writes an empty module, scaffolds the
        // __init__.py package markers up the directory chain, then reloads the solution to pick it up.
        // Force a .py extension on a user-typed new-file name (Save dialog lets them omit or mistype it).
        internal static string NormalizeNewFilePath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return rawPath;
            return rawPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                ? rawPath
                : Path.ChangeExtension(rawPath, ".py");
        }

        // §5.7: the creation verbs are invoked from a project node OR a folder node — a folder
        // targets that exact folder so the new file lands under the clicked node. Shared by Add
        // File, Add Existing File and Add Jupyter Notebook: three copies of this is three places
        // for a folder-resolution bug to hide.
        private static string ResolveTreeTargetDirectory(TreeOperationEventArgs e, out ProjectModel project)
        {
            project = e.Item as ProjectModel;
            string directory = project != null ? project.ProjectDirectory : null;
            var folder = e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FolderGroup;
            if (project == null && folder != null && folder.Project != null)
            {
                project = folder.Project;
                try
                {
                    string candidate = Path.Combine(project.ProjectDirectory,
                        (folder.RelativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
                    directory = Directory.Exists(candidate) ? candidate : project.ProjectDirectory;
                }
                catch (ArgumentException) { directory = project.ProjectDirectory; }
            }
            return directory;
        }

        private void OnTreeAddFile(object sender, TreeOperationEventArgs e)
        {
            ProjectModel project;
            string initialDir = ResolveTreeTargetDirectory(e, out project);
            if (project == null) return;

            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Python Files (*.py)|*.py|All Files (*.*)|*.*";
                dlg.Title = "Add New File";
                dlg.InitialDirectory = initialDir;
                dlg.DefaultExt = "py";
                dlg.FileName = "new_module.py";

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string targetPath = NormalizeNewFilePath(dlg.FileName);

                try
                {
                    // Python modules need no in-file "package declaration" — the file body
                    // is empty; the __init__.py in the parent directory chain (scaffolded
                    // below) is what makes this a regular-package module.
                    string parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    File.WriteAllText(targetPath, string.Empty, Utf8NoBomForNewFiles);

                    EnsurePackageMarkersForNewFile(project, targetPath);

                    ReloadSolutionAsync();

                    // Open the new file so the user can start editing immediately (VS/VS Code behaviour).
                    // The reload above doesn't touch editor tabs, so this tab survives it.
                    OpenInEditor(targetPath);

                    _statusLabel.Text = string.Format("Added file: {0}", Path.GetFileName(targetPath));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to create file: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        // Force an .ipynb extension on a user-typed notebook name, the same way
        // NormalizeNewFilePath forces .py. Separate from it deliberately: a notebook routed
        // through that one came back as a .py file, which is why the IDE could not create one.
        internal static string NormalizeNewNotebookPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return rawPath;
            return rawPath.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase)
                ? rawPath
                : Path.ChangeExtension(rawPath, ".ipynb");
        }

        /// <summary>
        /// Solution Explorer "Add Jupyter Notebook...": writes a real nbformat 4.5 file through
        /// <see cref="NotebookFactory"/> and the notebook writer, reloads, and opens it.
        ///
        /// <para>
        /// The file is created ON DISK FIRST and then opened through the ordinary
        /// <c>OpenInEditor</c> route, rather than opening an empty in-memory tab the user has to
        /// remember to save. Two reasons. A notebook tab with no path cannot Save at all
        /// (<c>NotebookTabPage.Save</c> refuses without one), so an unsaved-tab design would need a
        /// second Save-As path for the one case that matters most. And rule 4 (PY8004) says
        /// creating something creates BOTH halves — the disk entry and the tree node — so the
        /// reload below is not a refresh, it is the other half of the creation.
        /// </para>
        /// </summary>
        private void OnTreeAddNotebook(object sender, TreeOperationEventArgs e)
        {
            ProjectModel project;
            string initialDir = ResolveTreeTargetDirectory(e, out project);
            if (project == null) return;
            CreateNotebookThroughDialog(initialDir, project.PythonVersion, true);
        }

        /// <summary>
        /// File ▸ "New Jupyter Notebook..." — the route that does NOT require a solution.
        ///
        /// <para>
        /// A notebook is not a project deliverable, it is how somebody starts. Until this existed
        /// the only way to create one was the Solution Explorer verb on a project node, so making
        /// a scratch notebook meant first inventing a solution and a <c>pyproject.toml</c> to hang
        /// it on — and the New Project wizard's notebook template creates a whole solution too.
        /// Opening an existing one was already solution-free; creating one was not, and that
        /// asymmetry is what made the IDE feel like it refused.
        /// </para>
        ///
        /// <para>
        /// With a solution open this behaves exactly like the tree verb, including the PY8004
        /// reload that draws the node. With none open there is nothing to reload and nothing to
        /// join: the file is written where the user asked and opened on its own.
        /// </para>
        /// </summary>
        internal void OnFileNewNotebook(object sender, EventArgs e)
        {
            bool hasSolution = _currentSolution != null;

            // Somewhere the user can find again. A solution's own folder when there is one;
            // otherwise Documents, never the process's working directory — which is wherever the
            // shortcut happened to point and is not a place anybody looks for their own files.
            string initialDir = hasSolution ? _currentSolution.SolutionDirectory : null;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            // The version is the open project's when there is one and NULL otherwise — the factory
            // omits `language_info.version` rather than guessing, which is what Jupyter does when
            // it does not know either. Writing a version we made up would be a claim about an
            // interpreter this notebook has never been run against.
            string pythonVersion = null;
            if (hasSolution)
                foreach (ProjectModel project in _currentSolution.Projects)
                    if (project != null && !string.IsNullOrEmpty(project.PythonVersion))
                    { pythonVersion = project.PythonVersion; break; }

            CreateNotebookThroughDialog(initialDir, pythonVersion, hasSolution);
        }

        /// <summary>
        /// Prompt, write a real nbformat 4.5 file, and open it. Shared by the Solution Explorer
        /// verb and by File ▸ New Jupyter Notebook..., which is the solution-free route. New
        /// notebook SOLUTIONS remain the business of the template-driven New Project/Solution
        /// wizard — this makes a notebook, and only a notebook.
        ///
        /// <paramref name="reloadSolution"/> is the PY8004 half: creating something creates BOTH
        /// the disk entry and the tree node, and the reload is what draws the node. It is skipped
        /// when there is no solution to reload.
        /// </summary>
        private void CreateNotebookThroughDialog(string initialDirectory, string pythonVersion, bool reloadSolution)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Jupyter Notebook (*.ipynb)|*.ipynb|All Files (*.*)|*.*";
                dlg.Title = "New Jupyter Notebook";
                dlg.InitialDirectory = initialDirectory;
                dlg.DefaultExt = "ipynb";
                dlg.FileName = "Untitled.ipynb";

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string targetPath = NormalizeNewNotebookPath(dlg.FileName);
                try
                {
                    string parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    NotebookWriter.WriteFile(NotebookFactory.CreateNew(pythonVersion), targetPath);

                    if (reloadSolution) ReloadSolutionAsync();

                    // Routes through EditorCoordinator.IsNotebookPath, so this opens the Colab
                    // surface rather than loading the JSON into the code editor.
                    OpenInEditor(targetPath);

                    _statusLabel.Text = string.Format("Added notebook: {0}", Path.GetFileName(targetPath));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to create notebook: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        // "Add Existing File..." — VS's "Add Existing Item": like "Add File..." but copies an
        // ALREADY-EXISTING external file into the target project/folder instead of creating a new
        // empty one. Never overwrites an existing file at the destination (same rule RenameFileOnDisk
        // uses); a name collision blocks with a clear message rather than silently replacing content.
        private void OnTreeAddExistingFile(object sender, TreeOperationEventArgs e)
        {
            ProjectModel project;
            string targetDir = ResolveTreeTargetDirectory(e, out project);
            if (project == null) return;

            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "Python Files (*.py)|*.py|All Files (*.*)|*.*";
                dlg.Title = "Add Existing File";
                dlg.InitialDirectory = targetDir;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string sourcePath = dlg.FileName;
                string targetPath = Path.Combine(targetDir ?? project.ProjectDirectory, Path.GetFileName(sourcePath));

                if (PathsEqual(sourcePath, targetPath))
                {
                    _statusLabel.Text = "That file is already part of the project.";
                    return;
                }
                if (File.Exists(targetPath))
                {
                    MessageBox.Show(this,
                        "A file named '" + Path.GetFileName(targetPath) + "' already exists there.",
                        "Add Existing File", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    Directory.CreateDirectory(targetDir ?? project.ProjectDirectory);
                    File.Copy(sourcePath, targetPath);
                    EnsurePackageMarkersForNewFile(project, targetPath);
                    ReloadSolutionAsync();
                    OpenInEditor(targetPath);
                    _statusLabel.Text = string.Format("Added existing file: {0}", Path.GetFileName(targetPath));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to add file: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        // "New Folder" — creates an empty Python package (a directory + __init__.py marker) under
        // the clicked project or folder node. Reuses EnsurePackageMarkersForNewFile by handing it a
        // synthetic "__init__.py inside the new folder" path: that method only ever uses the path to
        // compute its starting directory and walk upward, so it scaffolds the new folder itself (and
        // any still-missing ancestor markers) without needing a separate directory-walking helper.
        private void OnTreeNewFolder(object sender, TreeOperationEventArgs e)
        {
            ProjectModel project = e.Item as ProjectModel;
            string baseDir = project != null ? project.ProjectDirectory : null;
            var folder = e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FolderGroup;
            if (project == null && folder != null && folder.Project != null)
            {
                project = folder.Project;
                try
                {
                    string candidate = Path.Combine(project.ProjectDirectory,
                        (folder.RelativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
                    baseDir = Directory.Exists(candidate) ? candidate : project.ProjectDirectory;
                }
                catch (ArgumentException) { baseDir = project.ProjectDirectory; }
            }
            if (project == null || string.IsNullOrEmpty(baseDir)) return;

            using (var inputForm = CreateInputDialog("New Folder", "Folder name:", "NewPackage"))
            {
                if (inputForm.ShowDialog(this) != DialogResult.OK) return;
                string name = (inputForm.Tag as string ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    _statusLabel.Text = "Invalid folder name.";
                    return;
                }

                string newDir = Path.Combine(baseDir, name);
                if (Directory.Exists(newDir))
                {
                    MessageBox.Show(this, "A folder named '" + name + "' already exists there.",
                        "New Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    Directory.CreateDirectory(newDir);
                    EnsurePackageMarkersForNewFile(project, Path.Combine(newDir, "__init__.py"));
                    ReloadSolutionAsync();
                    _statusLabel.Text = string.Format("Added folder: {0}", name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to create folder: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// After a new .py file is created, ensure every directory between the
        /// project root and the file's parent has an <c>__init__.py</c> marker.
        /// Python needs these to treat the directory chain as a regular-package
        /// hierarchy. Also updates the workspace-root pyproject.toml's
        /// <c>[tool.uv.workspace].members</c> list if a brand-new top-level
        /// package was just created.
        /// </summary>
        private void EnsurePackageMarkersForNewFile(ProjectModel project, string newFilePath)
        {
            if (project == null || string.IsNullOrEmpty(newFilePath)) return;

            string projectDir = project.ProjectDirectory;
            if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) return;

            string parent = Path.GetDirectoryName(newFilePath);
            while (!string.IsNullOrEmpty(parent) &&
                   parent.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(parent, projectDir, StringComparison.OrdinalIgnoreCase))
            {
                string initPath = Path.Combine(parent, "__init__.py");
                if (!File.Exists(initPath))
                {
                    try { File.WriteAllText(initPath, string.Empty, Utf8NoBomForNewFiles); }
                    catch { /* best-effort */ }
                }
                string next = Path.GetDirectoryName(parent);
                if (string.IsNullOrEmpty(next) || string.Equals(next, parent, StringComparison.OrdinalIgnoreCase)) break;
                parent = next;
            }

            TryUpdateWorkspaceRootToml(project, newFilePath);
        }

        /// <summary>
        /// If the new file created a brand-new top-level package directly under the
        /// workspace root, append that package's name to the workspace-root pyproject.toml's
        /// <c>members</c> list. Best-effort: if the members array spans multiple lines or uses
        /// unusual formatting, we silently skip rather than risk mangling the file.
        /// </summary>
        private void TryUpdateWorkspaceRootToml(ProjectModel project, string newFilePath)
        {
            try
            {
                string workspaceRoot = FindWorkspaceRoot(project.ProjectDirectory);
                if (string.IsNullOrEmpty(workspaceRoot)) return;

                string rootToml = Path.Combine(workspaceRoot, "pyproject.toml");
                if (!File.Exists(rootToml)) return;

                // First-segment directory between the workspace root and the new file.
                // If the file sits directly in the workspace root, there's no package to add.
                Uri baseUri = new Uri(workspaceRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? workspaceRoot : workspaceRoot + Path.DirectorySeparatorChar);
                Uri fileUri = new Uri(newFilePath);
                string rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString())
                    .Replace('\\', '/');
                int firstSlash = rel.IndexOf('/');
                if (firstSlash <= 0) return;
                string topLevelPkg = rel.Substring(0, firstSlash);

                // Confirm this top-level directory is now a Python package.
                string pkgDir = Path.Combine(workspaceRoot, topLevelPkg);
                if (!File.Exists(Path.Combine(pkgDir, "__init__.py"))) return;

                string toml = File.ReadAllText(rootToml);

                // Single-line members array only — multi-line / comment-interleaved cases
                // fall through to a no-op to avoid data loss.
                var m = System.Text.RegularExpressions.Regex.Match(
                    toml,
                    @"(?m)^\s*members\s*=\s*\[(?<body>[^\]\n]*)\]\s*$");
                if (!m.Success) return;

                string body = m.Groups["body"].Value;
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        body,
                        "\"\\s*" + System.Text.RegularExpressions.Regex.Escape(topLevelPkg) + "\\s*\""))
                {
                    return; // already listed
                }

                string trimmed = body.Trim();
                string newBody;
                if (string.IsNullOrEmpty(trimmed))
                {
                    newBody = "\"" + topLevelPkg + "\"";
                }
                else
                {
                    string sep = trimmed.EndsWith(",") ? " " : ", ";
                    newBody = trimmed + sep + "\"" + topLevelPkg + "\"";
                }

                string updated = toml.Substring(0, m.Index)
                    + "members = [" + newBody + "]"
                    + toml.Substring(m.Index + m.Length);
                File.WriteAllText(rootToml, updated, Utf8NoBomForNewFiles);
            }
            catch { /* best-effort TOML refresh */ }
        }

        /// <summary>
        /// Rewrites the workspace-root pyproject.toml's <c>[tool.uv.workspace].members</c> list to
        /// match <c>_currentSolution.Projects</c> exactly. Call after any operation that adds or
        /// removes a project so the persisted member list never goes stale — project delete
        /// previously left a dangling entry that <c>SolutionService</c> silently skipped on the next
        /// load rather than erroring, which is how the gap went unnoticed.
        /// By default only touches the file when it already declares <c>[tool.uv.workspace]</c> — a
        /// single-project solution whose root TOML doubles as its one project's own TOML never had a
        /// workspace section, and a project DELETE must not invent one. Pass
        /// <paramref name="forceWorkspaceSection"/> = true from an ADD path (new or existing
        /// project), where registering a second project genuinely does need that section created.
        /// </summary>
        private void RewriteWorkspaceRootMembers(bool forceWorkspaceSection = false)
        {
            if (_currentSolution == null || string.IsNullOrEmpty(_currentSolution.FilePath)) return;
            try
            {
                string rootToml = _currentSolution.FilePath;
                if (!File.Exists(rootToml)) return;
                if (!forceWorkspaceSection &&
                    File.ReadAllText(rootToml).IndexOf("[tool.uv.workspace]", StringComparison.Ordinal) < 0) return;

                string rootDir = Path.GetDirectoryName(rootToml);
                if (string.IsNullOrEmpty(rootDir)) return;

                var packages = new List<HelixPythonNamespace.Workspace.Python.PackageDescriptor>();
                foreach (var p in _currentSolution.Projects)
                {
                    if (string.IsNullOrEmpty(p.ProjectDirectory)) continue;
                    packages.Add(new HelixPythonNamespace.Workspace.Python.PackageDescriptor(p.Name, p.ProjectDirectory));
                }
                HelixPythonNamespace.Workspace.Python.TomlWorkspaceRootWriter.Write(
                    rootToml, _currentSolution.Name, null, packages, rootDir);
            }
            catch { /* best-effort — the underlying file-system change has already succeeded either way */ }
        }

        /// <summary>
        /// Walks up from <paramref name="startDir"/> looking for the first directory
        /// whose pyproject.toml contains a <c>[tool.uv.workspace]</c> section. Returns
        /// that directory, or null if none found.
        /// </summary>
        private static string FindWorkspaceRoot(string startDir)
        {
            string dir = startDir;
            while (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                string candidate = Path.Combine(dir, "pyproject.toml");
                if (File.Exists(candidate))
                {
                    try
                    {
                        string content = File.ReadAllText(candidate);
                        if (content.IndexOf("[tool.uv.workspace]", StringComparison.Ordinal) >= 0)
                            return dir;
                    }
                    catch { /* read failures: keep walking */ }
                }
                string parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) break;
                dir = parent;
            }
            return null;
        }

        // Solution Explorer "Add New Project": opens the template-driven New Project wizard with
        // the Select Template page restricted to templates ELIGIBLE for this solution's language
        // (TemplateEligibility — e.g. a Python solution offers Python app/library/Flask/GUI plus a
        // linked CUDA library), the Location locked to the solution root (workspace members are
        // root-relative), then registers the created project in the open solution.
        private void OnTreeAddProject(object sender, TreeOperationEventArgs e)
        {
            if (_currentSolution == null || string.IsNullOrEmpty(_currentSolution.FilePath)) return;
            string rootDir = Path.GetDirectoryName(_currentSolution.FilePath);
            if (string.IsNullOrEmpty(rootDir)) return;

            using (_fileWatcher?.SuspendWhileEditing())
            using (var wiz = new HelixPythonNamespace.App.Dialogs.NewProjectWizardForm(
                       HelixPythonNamespace.Workspace.Templates.TemplateCatalog.Default(), _settingsManager))
            {
                wiz.Text = "Add New Project";
                wiz.RestrictToTemplates(
                    HelixPythonNamespace.Workspace.Templates.TemplateEligibility.EligibleTemplateIds(
                        SolutionLanguageNameForTemplates()));
                wiz.SetFixedLocation(rootDir);
                wiz.SetAddToSolutionMode();
                if (wiz.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(wiz.CreatedProjectRoot))
                    return;
                AddCreatedProjectToSolution(wiz.CreatedProjectRoot,
                    wiz.SelectedTemplate != null ? wiz.SelectedTemplate.Id : null);
            }
        }

        /// <summary>Template-eligibility language of the open solution ("Python", "CUDA", "C++", ...).</summary>
        private string SolutionLanguageNameForTemplates()
        {
            if (_currentSolution == null) return "Python";
            foreach (var p in _currentSolution.Projects)
            {
                switch (p.Language)
                {
                    case HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Python: return "Python";
                    case HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp:
                        // Cpp covers both native languages; CUDA-ness is per-project (kind).
                        return (p.Kind == HelixPythonNamespace.Core.Model.Enums.ProjectKind.CudaExecutable ||
                                p.Kind == HelixPythonNamespace.Core.Model.Enums.ProjectKind.CudaLibrary) ? "CUDA" : "C++";
                }
            }
            return "Python";   // an empty (TOML-only) solution is a Python workspace
        }

        // Register a wizard-created project directory (inside the solution root) with the open
        // solution, then reload. The UI tail (async-void reload + status/error surfaces) lives
        // here; the file/model mutations live in RegisterCreatedProjectInSolution so tests can
        // exercise them without racing ReloadSolutionAsync's continuation (suite convention).
        private void AddCreatedProjectToSolution(string projectRoot, string templateId)
        {
            string name = Path.GetFileName((projectRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar));
            try
            {
                if (!RegisterCreatedProjectInSolution(projectRoot))
                {
                    _statusLabel.Text = "Created '" + name + "' on disk, but it cannot join this solution " +
                        "(a Python project cannot join a CMake workspace and vice versa) — open it via File ▸ Open.";
                    return;
                }
                ReloadSolutionAsync(CreatedTemplateDocument(projectRoot, templateId));
                _statusLabel.Text = string.Format("Added project: {0}", name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to add project: {0}", ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Into a PYTHON solution: a Python project becomes a workspace member (never stealing
        /// startup), a native CMake project is linked via [tool.helixstudio].linked-cmake-project.
        /// Into a NATIVE (CMake) solution — whose FilePath IS the root CMakeLists.txt, so the
        /// TOML machinery must never touch it — a CMake project joins via add_subdirectory (the
        /// importer follows those), and a Python project cannot join at all. False when the
        /// created directory holds no usable project anchor for this solution type.
        /// </summary>
        internal bool RegisterCreatedProjectInSolution(string projectRoot)
        {
            if (_currentSolution == null || string.IsNullOrEmpty(projectRoot)) return false;
            string name = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar));
            string projectToml = Path.Combine(projectRoot, "pyproject.toml");
            string projectCmake = Path.Combine(projectRoot, "CMakeLists.txt");

            bool nativeParent = string.Equals(Path.GetFileName(_currentSolution.FilePath ?? string.Empty),
                                              "CMakeLists.txt", StringComparison.OrdinalIgnoreCase);
            if (nativeParent)
            {
                if (!File.Exists(projectCmake)) return false;   // Python-in-CMake: unsupported
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    // A test subproject (GTest template: enable_testing/gtest_discover_tests in
                    // its OWN CMakeLists) is invisible to a root-level `ctest` unless the ROOT
                    // also calls enable_testing() before the add_subdirectory — without it CMake
                    // writes no root CTestTestfile.cmake and Run All Tests finds nothing.
                    string subText; try { subText = File.ReadAllText(projectCmake); } catch { subText = string.Empty; }
                    if (subText.IndexOf("enable_testing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        subText.IndexOf("add_test", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        subText.IndexOf("gtest_discover_tests", StringComparison.OrdinalIgnoreCase) >= 0)
                        EnsureEnableTesting(_currentSolution.FilePath);
                    AppendAddSubdirectory(_currentSolution.FilePath, name);
                }
                return true;
            }

            using (_fileWatcher?.SuspendWhileEditing())
            {
                if (File.Exists(projectToml))
                {
                    // The templates mark fresh apps startup=true — correct for a NEW solution,
                    // but a project ADDED to an existing solution must not steal its startup.
                    if (_currentSolution.Projects.Any(p => p.IsStartup))
                        HelixPythonNamespace.Workspace.Python.PyProjectStartupWriter.SetStartup(projectToml, false);
                    var newProject = new ProjectModel(projectToml, name, _currentSolution);
                    _currentSolution.Projects.Add(newProject);
                    RewriteWorkspaceRootMembers(forceWorkspaceSection: true);
                    return true;
                }
                if (File.Exists(projectCmake))
                {
                    var meta = new System.Collections.Generic.Dictionary<string, string>
                    {
                        { "linked-cmake-project", "\"" + name + "\"" }
                    };
                    HelixPythonNamespace.Workspace.Python.TomlTableMerger.EnsureTable(
                        _currentSolution.FilePath, "tool.helixstudio", meta);
                    return true;
                }
                return false;
            }
        }

        // Idempotently insert `enable_testing()` into the root CMakeLists.txt so ctest run from
        // the build root can reach tests registered in subdirectories. Placed right after the
        // project(...) line — it must precede every add_subdirectory to take effect for them.
        internal static void EnsureEnableTesting(string rootCMakeListsPath)
        {
            string text = File.ReadAllText(rootCMakeListsPath);
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\benable_testing\s*\(",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(text, @"\binclude\s*\(\s*CTest\s*\)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return;
            string eol = text.Contains("\r\n") ? "\r\n" : "\n";
            var projectLine = System.Text.RegularExpressions.Regex.Match(
                text, @"^[ \t]*project\s*\([^)]*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
            if (projectLine.Success)
            {
                int insertAt = projectLine.Index + projectLine.Length;
                text = text.Substring(0, insertAt) + eol + "enable_testing()" + text.Substring(insertAt);
            }
            else
            {
                if (text.Length > 0 && !text.EndsWith("\n")) text += eol;
                text += "enable_testing()" + eol;
            }
            File.WriteAllText(rootCMakeListsPath, text);
        }

        // Idempotently append `add_subdirectory("<name>")` to the root CMakeLists.txt.
        private static void AppendAddSubdirectory(string rootCMakeListsPath, string name)
        {
            string text = File.ReadAllText(rootCMakeListsPath);
            // Already referenced (quoted or bare, any spacing)? Then leave the file alone.
            var existing = System.Text.RegularExpressions.Regex.Match(
                text, @"add_subdirectory\s*\(\s*""?" + System.Text.RegularExpressions.Regex.Escape(name) + @"""?\s*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (existing.Success) return;
            string eol = text.Contains("\r\n") ? "\r\n" : "\n";
            if (text.Length > 0 && !text.EndsWith("\n")) text += eol;
            text += "add_subdirectory(\"" + name + "\")" + eol;
            File.WriteAllText(rootCMakeListsPath, text);
        }

        // "Add Existing Project..." — registers an ALREADY-EXISTING folder (with its own
        // pyproject.toml) as a new workspace member, unlike "Add Project..." above which always
        // scaffolds a brand-new one. Refuses folders outside the solution root:
        // [tool.uv.workspace].members are root-relative paths, and importing an external tree
        // wholesale is the Import Wizard's job (File ▸ Import Python Directory), not this command's.
        private void OnTreeAddExistingProject(object sender, TreeOperationEventArgs e)
        {
            if (_currentSolution == null || string.IsNullOrEmpty(_currentSolution.FilePath)) return;
            string rootDir = Path.GetDirectoryName(_currentSolution.FilePath);
            if (string.IsNullOrEmpty(rootDir)) return;

            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select an existing project folder (must contain a pyproject.toml)";
                dlg.SelectedPath = rootDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string projectDir = dlg.SelectedPath;
                string projectToml = Path.Combine(projectDir, "pyproject.toml");
                if (!File.Exists(projectToml))
                {
                    MessageBox.Show(this, "That folder does not contain a pyproject.toml.",
                        "Add Existing Project", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar);
                string fullProject = Path.GetFullPath(projectDir).TrimEnd(Path.DirectorySeparatorChar);
                if (!fullProject.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this,
                        "The project must live inside the solution's own folder to be added as a " +
                        "workspace member.\n\nTo import a project from elsewhere, use " +
                        "File ▸ Import Python Directory instead.",
                        "Add Existing Project", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                HelixPythonNamespace.Workspace.Python.PyProjectMetadata meta;
                try { meta = HelixPythonNamespace.Workspace.Python.PyProjectReader.Read(projectToml); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not read that project's pyproject.toml: " + ex.Message,
                        "Add Existing Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                string projectName = !string.IsNullOrEmpty(meta?.Name) ? meta.Name : Path.GetFileName(fullProject);

                if (_currentSolution.Projects.Any(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(this, "A project named '" + projectName + "' is already part of this solution.",
                        "Add Existing Project", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        var newProject = new ProjectModel(projectToml, projectName, _currentSolution);
                        _currentSolution.Projects.Add(newProject);
                        RewriteWorkspaceRootMembers(forceWorkspaceSection: true);
                    }
                    ReloadSolutionAsync();
                    _statusLabel.Text = string.Format("Added existing project: {0}", projectName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to add project: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        // Solution Explorer "Delete": polymorphic on the selected node. A TypeModel deletes the type
        // (with confirmation) and drops it from projection/tree; a ProjectModel removes the whole
        // project and its files from disk; a SolutionModel is treated as a request to close the solution.
        // All destructive model edits run inside a file-watcher suspension so they don't self-trigger a reload.
        // SRS §5.1: AST-level drag-rearrange — a class dragged onto a different file
        // row physically moves its source text there. Both files are read fresh
        // (flushing any open tab's pending autosave first) and BOTH the extraction
        // and the insertion must succeed before either file is written, so a
        // name collision in the target never costs the source its content.
        // Known limitation (matches the Import Engine's honest scope, loop 529):
        // this does not rewrite import statements elsewhere in the solution that
        // reference the type's old module — only the physical text moves.
        internal void OnTreeMoveType(object sender, HelixPythonNamespace.UI.Controls.MoveTypeEventArgs e)
        {
            if (e == null || e.Type == null || _currentSolution == null) return;
            TypeModel type = e.Type;
            var targetFg = e.TargetItem as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup;
            if (targetFg == null || string.IsNullOrEmpty(targetFg.FilePath)) return;
            string sourcePath = type.SourceFilePath;
            string targetPath = targetFg.FilePath;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath) || !File.Exists(targetPath)) return;
            if (PathsEqual(sourcePath, targetPath)) return;

            FlushOpenTab(sourcePath);
            FlushOpenTab(targetPath);

            string sourceText, targetText;
            try
            {
                sourceText = File.ReadAllText(sourcePath);
                targetText = File.ReadAllText(targetPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not read the file(s): " + ex.Message, "Move Class",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var extract = HelixPythonNamespace.Workspace.Python.PythonTypeMover.ExtractTopLevelClass(sourceText, type.Name);
            if (!extract.Success)
            {
                MessageBox.Show(this, "Could not move '" + type.Name + "': " + extract.ErrorMessage,
                    "Move Class", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var inserted = HelixPythonNamespace.Workspace.Python.PythonTypeMover.InsertClass(
                targetText, extract.ExtractedText, type.Name);
            if (!inserted.Success)
            {
                MessageBox.Show(this, "Could not move '" + type.Name + "': " + inserted.ErrorMessage,
                    "Move Class", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    File.WriteAllText(sourcePath, extract.RemainingSource);
                    File.WriteAllText(targetPath, inserted.ExtractedText);
                }
                ReloadOpenTabFromDisk(sourcePath);
                ReloadOpenTabFromDisk(targetPath);
                _statusLabel.Text = "Moved '" + type.Name + "' to " + Path.GetFileName(targetPath) + ".";
                ReloadSolutionAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Move failed: " + ex.Message, "Move Class",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Flushes any open editor tab's pending autosave for `path` so a read of
        // the file from disk reflects the latest in-editor content.
        private void FlushOpenTab(string path)
        {
            if (_editorInnerTabControl == null) return;
            foreach (TabPage p in _editorInnerTabControl.TabPages)
            {
                var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp != null && PathsEqual(etp.FilePath, path)) etp.FlushPendingSave();
            }
        }

        // Re-reads `path` into any open editor tab showing it, so the visible
        // buffer reflects a disk write this method itself just made (the file
        // watcher is suspended for that write, so it will not notice on its own).
        private void ReloadOpenTabFromDisk(string path)
        {
            if (_editorInnerTabControl == null) return;
            foreach (TabPage p in _editorInnerTabControl.TabPages)
            {
                var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp != null && PathsEqual(etp.FilePath, path)) etp.LoadFile(path);
            }
        }

        // SRS §5.9: Exclude/Include from Project. Both write directly into the owning
        // project's own pyproject.toml (never the workspace root's) and reload — the
        // loader's exclusion filter (SolutionService) does the rest: an excluded file
        // simply stops being scanned, so it vanishes from the tree/analysis on its own.
        internal void OnTreeExcludeFromProject(object sender, TreeOperationEventArgs e)
        {
            if (e == null || _currentSolution == null) return;
            ProjectModel project;
            string absolutePath;
            var fg = e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup;
            var fdg = e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FolderGroup;
            if (fg != null && fg.Project != null && !string.IsNullOrEmpty(fg.FilePath))
            {
                project = fg.Project;
                absolutePath = fg.FilePath;
            }
            else if (fdg != null && fdg.Project != null && !string.IsNullOrEmpty(fdg.RelativePath))
            {
                project = fdg.Project;
                absolutePath = Path.Combine(project.ProjectDirectory ?? string.Empty,
                    fdg.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            }
            else return;

            if (!SetExcludedForProject(project, absolutePath, true)) return;
            _statusLabel.Text = "Excluded from project: " + Path.GetFileName(absolutePath.TrimEnd(Path.DirectorySeparatorChar));
            ReloadSolutionAsync();
        }

        internal void OnTreeIncludeInProject(object sender, TreeOperationEventArgs e)
        {
            if (e == null || _currentSolution == null) return;
            var entry = e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.ExcludedEntry;
            if (entry == null || entry.Project == null || string.IsNullOrEmpty(entry.RelativePath)) return;

            string absolutePath = Path.Combine(entry.Project.ProjectDirectory ?? string.Empty,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!SetExcludedForProject(entry.Project, absolutePath, false)) return;
            _statusLabel.Text = "Included in project: " + entry.RelativePath;
            ReloadSolutionAsync();
        }

        // <paramref name="fullPath"/> expressed relative to <paramref name="baseDir"/>
        // (backslash-separated); the full path unchanged when it lies outside baseDir
        // or either input is malformed.
        private static string MakeRelative(string baseDir, string fullPath)
        {
            try
            {
                Uri baseUri = new Uri(baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                Uri fileUri = new Uri(fullPath);
                if (!baseUri.IsBaseOf(fileUri)) return fullPath;
                return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString()).Replace('/', '\\');
            }
            catch { return fullPath; }
        }

        // Writes the exclude/include entry into the project's OWN pyproject.toml.
        // Workspace members without one (a synthesized ".helixstudio-member" anchor)
        // have nowhere durable to persist the entry, so the command is refused
        // rather than silently writing into a non-TOML file.
        private bool SetExcludedForProject(ProjectModel project, string absolutePath, bool excluded)
        {
            if (project == null || string.IsNullOrEmpty(project.FilePath) ||
                !project.FilePath.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    "This project has no pyproject.toml of its own, so files can't be excluded from it.",
                    "Exclude from Project", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string projectDir = project.ProjectDirectory;
            if (string.IsNullOrEmpty(projectDir)) return false;
            string relative = MakeRelative(projectDir, absolutePath).Replace('\\', '/');
            using (_fileWatcher?.SuspendWhileEditing())
            {
                HelixPythonNamespace.Workspace.Python.PyProjectExcludeWriter.SetExcluded(project.FilePath, relative, excluded);
            }
            return true;
        }

        // "Remove from Solution": unregister the project, KEEP its files (VS parity — the
        // non-destructive sibling of the ProjectModel branch in OnTreeDelete below). Works per
        // parent-solution type: Python → drop from the members list; native CMake → remove the
        // add_subdirectory line (the root CMakeLists' own targets can only be edited there).
        internal void OnTreeRemoveProjectFromSolution(object sender, TreeOperationEventArgs e)
        {
            ProjectModel pm = e != null ? e.Item as ProjectModel : null;
            if (pm == null || _currentSolution == null || string.IsNullOrEmpty(_currentSolution.FilePath)) return;

            string rootDir = Path.GetDirectoryName(_currentSolution.FilePath);
            bool nativeParent = string.Equals(Path.GetFileName(_currentSolution.FilePath),
                                              "CMakeLists.txt", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (nativeParent)
                {
                    bool lineRemoved;
                    using (_fileWatcher?.SuspendWhileEditing())
                        lineRemoved = TryRemoveAddSubdirectory(_currentSolution.FilePath, pm.ProjectDirectory);
                    if (!lineRemoved)
                    {
                        _statusLabel.Text = "'" + pm.Name + "' is declared by the root CMakeLists.txt itself — " +
                                            "edit its targets there to remove it.";
                        return;
                    }
                    ReloadSolutionAsync();
                    _statusLabel.Text = "Removed '" + pm.Name + "' from the solution (files kept on disk).";
                    return;
                }

                if (!string.IsNullOrEmpty(pm.ProjectDirectory) && PathsEqual(pm.ProjectDirectory, rootDir))
                {
                    _statusLabel.Text = "'" + pm.Name + "' is the solution's own root project — " +
                                        "delete the whole solution from the Welcome page instead.";
                    return;
                }

                using (_fileWatcher?.SuspendWhileEditing())
                {
                    foreach (var type in pm.Types.ToList())
                        if (_projectionBuilder != null) _projectionBuilder.RemoveType(type.FullyQualifiedName);
                    _currentSolution.Projects.Remove(pm);
                    RewriteWorkspaceRootMembers();
                    _projectTreePanel.RemoveProject(pm.Name);
                    _canvas.Invalidate();
                }
                _statusLabel.Text = "Removed '" + pm.Name + "' from the solution (files kept on disk).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to remove the project: " + ex.Message, "Remove from Solution",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Remove the root CMakeLists' add_subdirectory(...) line referencing
        /// <paramref name="projectDirectory"/> (quoted or bare, any separator style).
        /// False when no such line exists (a root-level target, not a subdirectory project).</summary>
        internal static bool TryRemoveAddSubdirectory(string rootCMakeListsPath, string projectDirectory)
        {
            if (string.IsNullOrEmpty(rootCMakeListsPath) || string.IsNullOrEmpty(projectDirectory)) return false;
            string rootDir = Path.GetDirectoryName(Path.GetFullPath(rootCMakeListsPath));
            string full = Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string prefix = rootDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string rel = full.Substring(prefix.Length);

            string[] parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            string relPattern = string.Join(@"[\\/]",
                parts.Select(System.Text.RegularExpressions.Regex.Escape).ToArray());
            var re = new System.Text.RegularExpressions.Regex(
                @"^[ \t]*add_subdirectory\s*\(\s*""?" + relPattern + @"""?\s*\)[ \t]*\r?\n?",
                System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            string text = File.ReadAllText(rootCMakeListsPath);
            string updated = re.Replace(text, string.Empty, 1);
            if (string.Equals(updated, text, StringComparison.Ordinal)) return false;
            File.WriteAllText(rootCMakeListsPath, updated);
            return true;
        }

        private void OnTreeDelete(object sender, TreeOperationEventArgs e)
        {
            if (_currentSolution == null) return;

            TypeModel typeModel = e.Item as TypeModel;
            if (typeModel != null)
            {
                var result = MessageBox.Show(
                    string.Format("Delete type '{0}'?\n\nThis will remove the type from the source file.", typeModel.Name),
                    "Confirm Delete",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.OK) return;

                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        _typeService.RemoveType(typeModel.Project, typeModel);
                        if (_projectionBuilder != null)
                        {
                            _projectionBuilder.RemoveType(typeModel.FullyQualifiedName);
                        }
                        _projectTreePanel.RemoveType(typeModel.FullyQualifiedName);
                        _canvas.SelectionManager.ClearSelection();
                        _canvas.Invalidate();
                        // Properties panel removed
                        _statusLabel.Text = string.Format("Deleted type: {0}", typeModel.Name);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to delete type: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return;
            }

            // Top-level function delete: span-remove the function's source text (decorators
            // included) via PythonTypeMover.ExtractTopLevelFunction, mirroring how type-delete
            // removes a class. If the function is the file's only top-level statement, delete the
            // whole file instead of leaving a hollowed-out module — the same rule TypeService.
            // RemoveType already applies to a type alone in its file.
            FunctionModel functionModel = e.Item as FunctionModel;
            if (functionModel != null)
            {
                if (string.IsNullOrEmpty(functionModel.SourceFilePath) || !File.Exists(functionModel.SourceFilePath))
                {
                    _statusLabel.Text = "Could not resolve that function's source file.";
                    return;
                }

                var result = MessageBox.Show(
                    string.Format("Delete function '{0}'?\n\nThis will remove the function from the source file.", functionModel.Name),
                    "Confirm Delete",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.OK) return;

                string fnPath = functionModel.SourceFilePath;
                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        FlushOpenTab(fnPath);
                        string source = File.ReadAllText(fnPath);

                        if (HelixPythonNamespace.Workspace.Python.PythonTypeMover.IsOnlyTopLevelStatement(source))
                        {
                            if (_editorInnerTabControl != null)
                            {
                                var openTabs = new List<HelixPythonNamespace.Editor.EditorTabPage>();
                                foreach (TabPage p in _editorInnerTabControl.TabPages)
                                {
                                    var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                                    if (etp != null && PathsEqual(etp.FilePath, fnPath)) openTabs.Add(etp);
                                }
                                foreach (var etp in openTabs) _editorCoordinator.CloseEditorTab(etp);
                            }
                            File.Delete(fnPath);
                        }
                        else
                        {
                            var extract = HelixPythonNamespace.Workspace.Python.PythonTypeMover.ExtractTopLevelFunction(source, functionModel.Name);
                            if (!extract.Success)
                            {
                                MessageBox.Show(this,
                                    "Could not delete '" + functionModel.Name + "': " + extract.ErrorMessage,
                                    "Delete Function", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                return;
                            }
                            File.WriteAllText(fnPath, extract.RemainingSource);
                            ReloadOpenTabFromDisk(fnPath);
                        }
                        _statusLabel.Text = string.Format("Deleted function: {0}", functionModel.Name);
                    }
                    ReloadSolutionAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to delete function: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return;
            }

            // Member (method/property/field/constructor) delete. A Method/Property/Constructor is
            // span-removed via PythonTypeMover.ExtractMethod, same shape as function delete. A Field
            // has no single authoritative source line (PythonTypeExtractor synthesizes it from
            // possibly several assignment sites) — TryLocateUniqueFieldAssignment only proceeds when
            // there is EXACTLY one, and refuses with an explanatory message otherwise rather than
            // guessing which assignment to remove.
            MemberModel memberModel = e.Item as MemberModel;
            if (memberModel != null)
            {
                TypeModel owner = e.Node != null && e.Node.Parent != null ? e.Node.Parent.Tag as TypeModel : null;
                if (owner == null || string.IsNullOrEmpty(owner.SourceFilePath) || !File.Exists(owner.SourceFilePath))
                {
                    _statusLabel.Text = "Could not resolve that member's declaring source file.";
                    return;
                }

                var result = MessageBox.Show(
                    string.Format("Delete {0} '{1}'?\n\nThis will remove it from the source file.",
                        memberModel.MemberKind.ToString().ToLowerInvariant(), memberModel.Name),
                    "Confirm Delete",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);
                if (result != DialogResult.OK) return;

                string memberPath = owner.SourceFilePath;
                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        FlushOpenTab(memberPath);
                        string source = File.ReadAllText(memberPath);

                        HelixPythonNamespace.Workspace.Python.PythonTypeMover.ExtractResult extract =
                            memberModel.MemberKind == MemberKind.Field
                                ? HelixPythonNamespace.Workspace.Python.PythonTypeMover.TryLocateUniqueFieldAssignment(
                                    source, owner.Name, memberModel.Name)
                                : HelixPythonNamespace.Workspace.Python.PythonTypeMover.ExtractMethod(
                                    source, owner.Name, memberModel.Name);

                        if (!extract.Success)
                        {
                            MessageBox.Show(this,
                                "Could not delete '" + memberModel.Name + "': " + extract.ErrorMessage,
                                "Delete Member", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        File.WriteAllText(memberPath, extract.RemainingSource);
                        ReloadOpenTabFromDisk(memberPath);
                        _statusLabel.Text = string.Format("Deleted {0}: {1}", memberModel.MemberKind, memberModel.Name);
                    }
                    ReloadSolutionAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to delete member: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return;
            }

            // §5.9: Delete on a file row — VS parity: confirm, delete from disk,
            // close any open tab for it, and reload so the tree/model drop the file.
            var fileGroup = e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup;
            if (fileGroup != null)
            {
                string path = fileGroup.FilePath;
                if (string.IsNullOrEmpty(path)) return;
                var confirm = MessageBox.Show(this,
                    string.Format("Delete '{0}'?\n\nThe file will be permanently deleted from disk.",
                        Path.GetFileName(path)),
                    "Confirm Delete", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (confirm != DialogResult.OK) return;
                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        if (_editorInnerTabControl != null)
                        {
                            var openTabs = new List<HelixPythonNamespace.Editor.EditorTabPage>();
                            foreach (TabPage p in _editorInnerTabControl.TabPages)
                            {
                                var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                                if (etp != null && PathsEqual(etp.FilePath, path)) openTabs.Add(etp);
                            }
                            foreach (var etp in openTabs) _editorCoordinator.CloseEditorTab(etp);
                        }
                        if (File.Exists(path)) File.Delete(path);
                        _statusLabel.Text = string.Format("Deleted file: {0}", Path.GetFileName(path));
                    }
                    ReloadSolutionAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to delete file: " + ex.Message, "Delete",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            ProjectModel projectModel = e.Item as ProjectModel;
            if (projectModel != null)
            {
                var result = MessageBox.Show(
                    string.Format("Delete project '{0}'?\n\nThis will remove the project and all its files from disk.", projectModel.Name),
                    "Confirm Delete",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);

                if (result != DialogResult.OK) return;

                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        // Remove all types from projection
                        foreach (var type in projectModel.Types.ToList())
                        {
                            if (_projectionBuilder != null)
                            {
                                _projectionBuilder.RemoveType(type.FullyQualifiedName);
                            }
                        }

                        _projectService.RemoveProject(_currentSolution, projectModel, true);
                        RewriteWorkspaceRootMembers();
                        // Native solution: also drop the now-dangling add_subdirectory reference.
                        if (string.Equals(Path.GetFileName(_currentSolution.FilePath ?? string.Empty),
                                          "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                            TryRemoveAddSubdirectory(_currentSolution.FilePath, projectModel.ProjectDirectory);
                        _projectTreePanel.RemoveProject(projectModel.Name);
                        _canvas.Invalidate();
                        _statusLabel.Text = string.Format("Deleted project: {0}", projectModel.Name);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to delete project: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return;
            }

            // "Delete Package" — a package/sub-folder node, not the project root itself (that's the
            // ProjectModel branch above). Recursively deletes the directory from disk. Unlike project
            // delete, no workspace-root TOML update is needed: a package sub-folder is never itself a
            // `[tool.uv.workspace].members` entry (only whole projects are), so the member list is
            // unaffected — the solution reload alone picks up the missing types/files.
            var folderGroup = e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FolderGroup;
            if (folderGroup != null && folderGroup.Project != null)
            {
                string folderPath = ResolvePackageDirectory(folderGroup);
                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                {
                    _statusLabel.Text = "Could not resolve that package's directory on disk.";
                    return;
                }
                // A folder node's path can never legitimately equal a project's own root
                // directory (that's always represented by the ProjectModel node instead), but
                // guard against it anyway rather than deleting an entire project's tree here.
                if (_currentSolution.Projects.Any(p => PathsEqual(p.ProjectDirectory, folderPath)))
                {
                    MessageBox.Show(this,
                        "This is a project's root folder — use \"Delete Project\" on the project node instead.",
                        "Delete Package", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                int fileCount;
                try { fileCount = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories).Length; }
                catch { fileCount = 0; }

                var confirm = MessageBox.Show(this,
                    string.Format("Delete package '{0}'?\n\nThis permanently deletes {1} file(s) from disk.",
                        Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar)), fileCount),
                    "Confirm Delete", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (confirm != DialogResult.OK) return;

                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        if (_editorInnerTabControl != null)
                        {
                            var openTabs = new List<HelixPythonNamespace.Editor.EditorTabPage>();
                            string prefix = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
                            foreach (TabPage p in _editorInnerTabControl.TabPages)
                            {
                                var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                                if (etp == null || string.IsNullOrEmpty(etp.FilePath)) continue;
                                string full;
                                try { full = Path.GetFullPath(etp.FilePath); } catch { continue; }
                                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) openTabs.Add(etp);
                            }
                            foreach (var etp in openTabs) _editorCoordinator.CloseEditorTab(etp);
                        }
                        Directory.Delete(folderPath, recursive: true);
                        _statusLabel.Text = string.Format("Deleted package: {0}",
                            Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar)));
                    }
                    ReloadSolutionAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to delete package: " + ex.Message, "Delete Package",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            SolutionModel solutionModel = e.Item as SolutionModel;
            if (solutionModel != null)
            {
                var result = MessageBox.Show(
                    "Close the current solution?",
                    "Close Solution",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question);

                if (result == DialogResult.OK)
                {
                    OnFileClose(sender, e);
                }
            }
        }

        // Solution Explorer inline rename, also polymorphic. Renaming a TYPE routes through the
        // ref-aware ExecuteScopedRename so every usage across the solution is updated (not just the
        // declaration); a PROJECT rename goes through the project service; a SOLUTION rename is an
        // in-memory display-name change only.
        internal void OnTreeRename(object sender, TreeRenameEventArgs e)
        {
            if (_currentSolution == null) return;

            TypeModel typeModel = e.Item as TypeModel;
            if (typeModel != null)
            {
                string newName = e.NewName;
                if (string.IsNullOrEmpty(newName) || string.Equals(typeModel.Name, newName, StringComparison.Ordinal)) return;

                // Route through the ref-aware rename (SymbolRenameService): it updates the class
                // definition AND every USAGE across the solution, builds an undoable command, and
                // handles the declaring-file move. The legacy TypeService.RenameType renamed only the
                // class def, leaving call sites (e.g. `Point(3, 4)`) referencing a now-nonexistent
                // name — broken code. Solution scope so all references are caught.
                ExecuteScopedRename(typeModel, null, newName,
                    HelixPythonNamespace.Workspace.Services.RenameScope.Solution);
                return;
            }

            // §5 (loop 533): F2 inline rename for a top-level function — the quick path,
            // same ref-aware engine as "Rename Symbol...", solution-wide by default (no
            // scope picker in an inline tree edit; a narrower scope stays reachable via
            // the modal for when that matters).
            FunctionModel functionModel = e.Item as FunctionModel;
            if (functionModel != null)
            {
                string newName = e.NewName;
                if (string.IsNullOrEmpty(newName) || string.Equals(functionModel.Name, newName, StringComparison.Ordinal)) return;
                ExecuteScopedRename(null, functionModel, newName,
                    HelixPythonNamespace.Workspace.Services.RenameScope.Solution);
                return;
            }

            // Member (method/property/field) F2 inline rename. Constructor is deliberately excluded
            // from the tree's F2/menu Rename (see ProjectTreePanel's MemberModel branch) — __init__
            // is a fixed dunder Python calls automatically, so renaming it breaks the class — but
            // guard here too in case this handler is ever reached some other way.
            MemberModel memberModel = e.Item as MemberModel;
            if (memberModel != null)
            {
                string newName = e.NewName;
                if (string.IsNullOrEmpty(newName) || string.Equals(memberModel.Name, newName, StringComparison.Ordinal)) return;
                if (memberModel.MemberKind == MemberKind.Constructor)
                {
                    _statusLabel.Text = "The constructor (__init__) cannot be renamed.";
                    return;
                }
                TypeModel owner = e.Node != null && e.Node.Parent != null ? e.Node.Parent.Tag as TypeModel : null;
                if (owner == null)
                {
                    _statusLabel.Text = "Could not resolve the declaring class for this member.";
                    return;
                }
                ExecuteMemberRename(owner, memberModel, newName);
                return;
            }

            ProjectModel projectModel = e.Item as ProjectModel;
            if (projectModel != null)
            {
                // A root-anchored project owns the solution directory itself. Renaming only its
                // manifest produces a project label that disagrees with the execution path. Use
                // the solution transaction, which validates collisions, confirms the disk move,
                // rebases tabs/settings, moves the folder, and reopens the new manifest path.
                if (_currentSolution != null &&
                    PathsEqual(projectModel.ProjectDirectory, _currentSolution.SolutionDirectory))
                {
                    ExecuteSolutionRename(_currentSolution, e.NewName);
                    return;
                }
                List<string> renamedExpansion = _projectTreePanel.GetExpandedPathsAfterRename(
                    projectModel.Name, e.NewName);
                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        _projectService.RenameProject(_currentSolution, projectModel, e.NewName);
                        _expandedTreePathsAfterRename = renamedExpansion;
                        _statusLabel.Text = string.Format("Renamed project to: {0}", e.NewName);
                        // ProjectModel identity and paths are immutable. Reload from the completed
                        // transaction so the tree never keeps stale paths after a directory move.
                        ReloadSolutionAsync();
                    }
                }
                catch (Exception ex)
                {
                    _expandedTreePathsAfterRename = null;
                    MessageBox.Show(
                        string.Format("Failed to rename project: {0}", ex.Message),
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return;
            }

            SolutionModel solutionModel = e.Item as SolutionModel;
            if (solutionModel != null)
            {
                ExecuteSolutionRename(solutionModel, e.NewName);
                return;
            }

            // §5.2: file nodes rename the file ON DISK. The new name keeps the old
            // extension when the user typed none; an existing target blocks the rename
            // (never overwrite). Open tabs for the file are re-pointed, then the
            // solution reloads so types/functions re-anchor to the new module path.
            var fileGroup = e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup;
            if (fileGroup != null && !string.IsNullOrEmpty(fileGroup.FilePath))
            {
                RenameFileOnDisk(fileGroup.FilePath, e.NewName);
            }
        }

        // Solution rename: unlike every other rename in this file, this ALSO moves the solution's
        // root folder on disk (same-parent, renamed leaf — never an arbitrary relocation), not just
        // the display name / [project].name field. This is the highest-blast-radius rename in the
        // tree (touches every project's paths, every open tab, and the persisted per-solution
        // settings), so it: refuses up front if the destination already exists (no partial state),
        // confirms explicitly since it moves a real folder, and on any failure after the physical
        // move best-effort rolls the directory back rather than leaving the solution orphaned under
        // a name nothing on disk or in settings points at anymore.
        // Status bar always; modal only when there is somebody to dismiss it (same guard as
        // ReportFixProblem — a box raised under HELIXSTUDIO_NONINTERACTIVE blocks the STA thread).
        private void ReportRenameRefusal(string message)
        {
            if (_statusLabel != null) _statusLabel.Text = message;
            if (IsNonInteractiveHost())
            {
                System.Diagnostics.Debug.WriteLine("[noninteractive] Rename Solution: " + message);
                return;
            }
            MessageBox.Show(this, message, "Rename Solution", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ExecuteSolutionRename(SolutionModel solutionModel, string newName)
        {
            if (string.IsNullOrEmpty(newName)) return;

            string oldRootToml = solutionModel.FilePath;
            string oldRootDir = !string.IsNullOrEmpty(oldRootToml) ? Path.GetDirectoryName(oldRootToml) : null;
            string oldFolderName = !string.IsNullOrEmpty(oldRootDir) ? Path.GetFileName(oldRootDir) : null;
            if (string.Equals(solutionModel.Name, newName, StringComparison.Ordinal) &&
                string.Equals(oldFolderName, newName, StringComparison.Ordinal)) return;

            if (string.IsNullOrEmpty(oldRootDir) || !Directory.Exists(oldRootDir))
            {
                // No real folder backs this solution (e.g. a synthetic/in-memory instance) — fall
                // back to the previous display-name-only behavior rather than fail outright.
                solutionModel.Name = newName;
                UpdateTitle();
                _statusLabel.Text = string.Format("Renamed solution to: {0}", newName);
                return;
            }

            string parentDir = Path.GetDirectoryName(oldRootDir);
            if (string.IsNullOrEmpty(parentDir))
            {
                ReportRenameRefusal("Cannot rename the solution folder: it has no parent directory.");
                return;
            }
            string newRootDir = Path.Combine(parentDir, newName);
            if (Directory.Exists(newRootDir) || File.Exists(newRootDir))
            {
                ReportRenameRefusal("A folder or file named '" + newName + "' already exists there.");
                return;
            }

            var confirm = MessageBox.Show(this,
                string.Format(
                    "Rename solution '{0}' to '{1}'?\n\nThis moves the solution's folder on disk:\n{2}\n→\n{3}",
                    solutionModel.Name, newName, oldRootDir, newRootDir),
                "Rename Solution", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (confirm != DialogResult.OK) return;

            string newRootToml = Path.Combine(newRootDir, Path.GetFileName(oldRootToml));
            List<string> renamedExpansion = _projectTreePanel.GetExpandedPathsAfterRename(
                solutionModel.Name, newName);
            bool moved = false;

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    // Flush every open tab under the old root before the move so no pending
                    // autosave is lost.
                    if (_editorInnerTabControl != null)
                    {
                        foreach (TabPage p in _editorInnerTabControl.TabPages)
                        {
                            var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                            if (etp == null || string.IsNullOrEmpty(etp.FilePath)) continue;
                            if (RebaseOpenTabPath(etp.FilePath, oldRootDir, newRootDir) != null)
                                etp.FlushPendingSave();
                        }
                    }

                    Directory.Move(oldRootDir, newRootDir);
                    moved = true;

                    // Re-point every open tab's FilePath to live under the new root.
                    if (_editorInnerTabControl != null)
                    {
                        foreach (TabPage p in _editorInnerTabControl.TabPages)
                        {
                            var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                            if (etp == null || string.IsNullOrEmpty(etp.FilePath)) continue;
                            string rebased = RebaseOpenTabPath(etp.FilePath, oldRootDir, newRootDir);
                            if (rebased != null) etp.FilePath = rebased;
                        }
                        RecomputeEditorTabWidths();
                    }

                    // Rewrite the (now-moved) root manifest so its own recorded name matches the
                    // new folder. For a Python solution the manifest is a GENERATED pyproject.toml
                    // (safe to fully regenerate), but for a CMake solution it is CMakeLists.txt — a
                    // real, hand-authored build script (add_subdirectory calls, compile options,
                    // set() variables like the startup-target marker). Regenerating it the same way
                    // would blow away everything but the project name, corrupting the C++ build —
                    // do minimal text surgery instead: rename just the project(<name> ...) call's
                    // name token and leave the rest of the file untouched.
                    bool isCMakeSolution = string.Equals(Path.GetFileName(newRootToml), "CMakeLists.txt",
                        StringComparison.OrdinalIgnoreCase);
                    if (isCMakeSolution)
                    {
                        try
                        {
                            string cmakeText = File.ReadAllText(newRootToml);
                            string rewritten = RenameCMakeProjectDeclaration(cmakeText, solutionModel.Name, newName);
                            if (rewritten != null) File.WriteAllText(newRootToml, rewritten);
                        }
                        catch { /* best-effort — the folder move (the part that actually matters) already succeeded */ }
                    }
                    else
                    {
                        // Every project's directory has physically moved along with the root, so
                        // their paths need rebasing too for the workspace member list to stay correct.
                        var packages = new List<HelixPythonNamespace.Workspace.Python.PackageDescriptor>();
                        foreach (var p in solutionModel.Projects)
                        {
                            if (string.IsNullOrEmpty(p.ProjectDirectory)) continue;
                            string rebasedDir = RebaseUnderMovedRoot(p.ProjectDirectory, oldRootDir, newRootDir);
                            packages.Add(new HelixPythonNamespace.Workspace.Python.PackageDescriptor(p.Name, rebasedDir));
                        }
                        HelixPythonNamespace.Workspace.Python.TomlWorkspaceRootWriter.Write(newRootToml, newName, null, packages, newRootDir);
                    }

                    // Re-key persisted per-solution settings and the recent-files entry so they
                    // still resolve after relaunch.
                    RekeySolutionSettings(oldRootToml, newRootToml);
                    _settingsManager.Settings.SetExpandedTreePaths(newRootToml, renamedExpansion);
                    _settingsManager.Save();
                    _recentFilesManager.RemoveRecent(oldRootToml);
                    _recentFilesManager.AddRecent(newRootToml);

                    solutionModel.Name = newName;
                    _statusLabel.Text = string.Format("Renamed solution '{0}' → folder: {1}", newName, newRootDir);
                }

                // Not ReloadSolutionAsync(): that reloads from _currentSolution.FilePath, which is
                // still the OLD (now-nonexistent) path — SolutionModel.FilePath has no setter, and
                // the solution's identity has genuinely changed. OpenSolutionAsync with the new path
                // is the same real "open a solution" entry point File ▸ Open Solution uses, so it
                // re-derives _currentSolution, the tree, and checked-project visibility correctly.
                OpenSolutionAsync(newRootToml);
                UpdateTitle();
            }
            catch (Exception ex)
            {
                // Best-effort rollback: the physical move succeeded but a later step threw — move
                // the folder back so the solution isn't left orphaned under a path nothing on disk
                // or in settings points at.
                if (moved && Directory.Exists(newRootDir) && !Directory.Exists(oldRootDir))
                {
                    try { Directory.Move(newRootDir, oldRootDir); } catch { }
                }
                MessageBox.Show(this, "Rename failed: " + ex.Message,
                    "Rename Solution", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Renames just the name token of a CMakeLists.txt's `project(<name> ...)` call, leaving
        // every other line (add_subdirectory, compile options, set() variables) untouched. Returns
        // null if the call can't be found (e.g. the recorded solution name drifted from the actual
        // CMakeLists.txt content) so the caller can skip the rewrite rather than write back
        // unmodified text as if it had succeeded.
        internal static string RenameCMakeProjectDeclaration(string cmakeText, string oldName, string newName)
        {
            if (string.IsNullOrEmpty(cmakeText) || string.IsNullOrEmpty(oldName)) return null;
            string pattern = @"(\bproject\s*\(\s*)" + System.Text.RegularExpressions.Regex.Escape(oldName) + @"\b";
            var rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!rx.IsMatch(cmakeText)) return null;
            return rx.Replace(cmakeText, "$1" + newName.Replace("$", "$$"), 1);
        }

        internal static string ComputeRenamedFilePath(string oldPath, string rawNewName)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrWhiteSpace(rawNewName)) return null;
            string newName = rawNewName.Trim();
            // The tree label reads "name.py (3)" — strip a pasted-back count suffix.
            int paren = newName.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0 && newName.EndsWith(")", StringComparison.Ordinal)) newName = newName.Substring(0, paren);
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            if (!Path.HasExtension(newName))
            {
                string oldExt = Path.GetExtension(oldPath);
                if (!string.IsNullOrEmpty(oldExt)) newName += oldExt;
            }
            string dir = Path.GetDirectoryName(Path.GetFullPath(oldPath));
            return Path.Combine(dir ?? string.Empty, newName);
        }

        private void RenameFileOnDisk(string oldPath, string rawNewName)
        {
            string newPath = ComputeRenamedFilePath(oldPath, rawNewName);
            if (newPath == null)
            {
                _statusLabel.Text = "Invalid file name.";
                return;
            }
            if (string.Equals(newPath, Path.GetFullPath(oldPath), StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(oldPath))
            {
                _statusLabel.Text = "File not found on disk: " + Path.GetFileName(oldPath);
                return;
            }
            if (File.Exists(newPath) || Directory.Exists(newPath))
            {
                MessageBox.Show(this, "A file named '" + Path.GetFileName(newPath) + "' already exists.",
                    "Rename File", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    // Flush the file's tab (if open) so the moved bytes are current.
                    if (_editorInnerTabControl != null)
                    {
                        foreach (TabPage p in _editorInnerTabControl.TabPages)
                        {
                            var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                            if (etp != null && PathsEqual(etp.FilePath, oldPath))
                            {
                                try { etp.FlushPendingSave(); } catch { }
                            }
                        }
                    }
                    File.Move(oldPath, newPath);
                    if (_editorInnerTabControl != null)
                    {
                        foreach (TabPage p in _editorInnerTabControl.TabPages)
                        {
                            var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                            if (etp != null && PathsEqual(etp.FilePath, oldPath)) etp.FilePath = newPath;
                        }
                        RecomputeEditorTabWidths();
                    }
                    _statusLabel.Text = string.Format("Renamed {0} → {1}",
                        Path.GetFileName(oldPath), Path.GetFileName(newPath));
                }
                ReloadSolutionAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Rename failed: " + ex.Message, "Rename File",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
        }

        // §5.3: executes a tree-clipboard paste — Cut moves the file, Copy duplicates it
        // with a non-colliding name; both re-anchor the solution afterwards.
        private void OnTreeFilePaste(object sender, HelixPythonNamespace.UI.Controls.FilePasteEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.SourcePath)) return;
            if (!File.Exists(e.SourcePath))
            {
                _statusLabel.Text = "Cannot paste: source file no longer exists.";
                return;
            }
            string targetDir = ResolvePasteTargetDirectory(e.TargetItem, out ProjectModel targetProject);
            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;
            string target = Path.Combine(targetDir, Path.GetFileName(e.SourcePath));

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    if (e.IsCut)
                    {
                        if (PathsEqual(target, e.SourcePath)) return;   // moved onto itself
                        if (File.Exists(target))
                        {
                            MessageBox.Show(this,
                                "A file named '" + Path.GetFileName(target) + "' already exists there.",
                                "Paste", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        // Flush + re-point any open tab for the moving file.
                        if (_editorInnerTabControl != null)
                        {
                            foreach (TabPage p in _editorInnerTabControl.TabPages)
                            {
                                var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                                if (etp != null && PathsEqual(etp.FilePath, e.SourcePath))
                                {
                                    try { etp.FlushPendingSave(); } catch { }
                                }
                            }
                        }
                        File.Move(e.SourcePath, target);
                        if (_editorInnerTabControl != null)
                        {
                            foreach (TabPage p in _editorInnerTabControl.TabPages)
                            {
                                var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                                if (etp != null && PathsEqual(etp.FilePath, e.SourcePath)) etp.FilePath = target;
                            }
                            RecomputeEditorTabWidths();
                        }
                        _statusLabel.Text = string.Format("Moved {0} → {1}", Path.GetFileName(target), targetDir);
                    }
                    else
                    {
                        target = DedupePastePath(target);
                        File.Copy(e.SourcePath, target);
                        _statusLabel.Text = string.Format("Copied {0} → {1}", Path.GetFileName(target), targetDir);
                    }
                    if (targetProject != null) EnsurePackageMarkersForNewFile(targetProject, target);
                }
                ReloadSolutionAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Paste failed: " + ex.Message, "Paste",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // The directory a paste onto this tree item lands in (and its owning project).
        private string ResolvePasteTargetDirectory(object targetItem, out ProjectModel project)
        {
            project = null;
            var fg = targetItem as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup;
            if (fg != null && !string.IsNullOrEmpty(fg.FilePath))
            {
                project = fg.Project;
                try { return Path.GetDirectoryName(Path.GetFullPath(fg.FilePath)); }
                catch (ArgumentException) { return null; }
            }
            var folder = targetItem as HelixPythonNamespace.UI.Controls.ProjectTreePanel.FolderGroup;
            if (folder != null && folder.Project != null && !string.IsNullOrEmpty(folder.RelativePath))
            {
                project = folder.Project;
                try
                {
                    return Path.GetFullPath(Path.Combine(folder.Project.ProjectDirectory,
                        folder.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (ArgumentException) { return null; }
            }
            var pm = targetItem as ProjectModel;
            if (pm != null)
            {
                project = pm;
                return pm.ProjectDirectory;
            }
            return null;
        }

        // name.py -> name_copy.py -> name_copy_2.py ... (underscored so the result
        // stays an importable Python module name, unlike Explorer's " - Copy").
        internal static string DedupePastePath(string target)
        {
            if (!File.Exists(target) && !Directory.Exists(target)) return target;
            string dir = Path.GetDirectoryName(target);
            string stem = Path.GetFileNameWithoutExtension(target);
            string ext = Path.GetExtension(target);
            string candidate = Path.Combine(dir ?? string.Empty, stem + "_copy" + ext);
            int n = 2;
            while (File.Exists(candidate) || Directory.Exists(candidate))
            {
                candidate = Path.Combine(dir ?? string.Empty, stem + "_copy_" + n + ext);
                n++;
            }
            return candidate;
        }

        // "Rename Symbol" context command (types or functions): pops the scope-aware rename dialog
        // and, on OK, defers to ExecuteScopedRename with the user's chosen scope.
        private void OnTreeRenameSymbol(object sender, TreeOperationEventArgs e)
        {
            if (e == null || _currentSolution == null) return;

            TypeModel typeTarget = e.Item as TypeModel;
            FunctionModel fnTarget = e.Item as FunctionModel;
            if (typeTarget == null && fnTarget == null) return;

            string oldName = typeTarget != null ? typeTarget.Name : fnTarget.Name;
            string kind = typeTarget != null ? "Type" : "Function";

            using (var dlg = new HelixPythonNamespace.App.Dialogs.RenameSymbolDialog(kind, oldName))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string newName = dlg.NewName;
                if (string.IsNullOrEmpty(newName) || newName == oldName) return;
                ExecuteScopedRename(typeTarget, fnTarget, newName, dlg.SelectedScope);
            }
        }

        // ScopedFileEditCommand writes every rename edit straight to disk (File.WriteAllText) and
        // never touches an already-open editor buffer, so without this a tab left open on any
        // touched file — the renamed class's own declaration or just a call site elsewhere in the
        // solution — keeps showing the pre-rename text until manually closed and reopened. Mirrors
        // RenameEditorTabFile's re-keying for the file that itself got moved (class name == file
        // stem, PSM convention), then reloads each affected tab from disk via EditorTabPage.LoadFile.
        // A tab with unsaved edits is left untouched — same rule as IsFileOpenWithUnsavedEdits/
        // OnConflictDetected — so a rename never silently clobbers work in progress.
        private void ReloadOpenEditorTabsAfterEdit(IEnumerable<string> editedPaths, string movedFromPath, string movedToPath)
        {
            if (_openEditorTabs == null || editedPaths == null) return;

            string movedFromFull = null;
            if (!string.IsNullOrEmpty(movedFromPath))
            {
                try { movedFromFull = Path.GetFullPath(movedFromPath); } catch { movedFromFull = null; }
            }

            foreach (string rawPath in editedPaths)
            {
                if (string.IsNullOrEmpty(rawPath)) continue;
                string full;
                try { full = Path.GetFullPath(rawPath); } catch { continue; }

                TabPage tab;
                if (!_openEditorTabs.TryGetValue(full, out tab)) continue;

                var etp = tab as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp == null || etp.IsModified) continue;

                // A rename only rewrites identifier text in place — it never inserts or removes
                // lines — so the caret's line/column from before the reload is still exactly where
                // the user was looking. LoadFile resets the caret to line 1 (it's the same code
                // path as opening the file fresh), which without this dropped the user at the top
                // of the file instead of leaving them on the declaration/call site they had open.
                int savedLine = etp.CaretLine;
                int savedColumn = etp.CaretColumn;

                if (movedFromFull != null && string.Equals(full, movedFromFull, StringComparison.OrdinalIgnoreCase))
                {
                    _openEditorTabs.Remove(full);
                    etp.FilePath = movedToPath;
                    full = Path.GetFullPath(movedToPath);
                    _openEditorTabs[full] = etp;
                }

                etp.LoadFile(full);
                try { etp.Editor.GoToLineColumn(savedLine, savedColumn); } catch { }
            }
        }

        // Core ref-aware rename. Uses SymbolRenameService to compute every text edit across the
        // chosen scope, wraps them in an undoable ScopedFileEditCommand pushed through the canvas
        // dispatcher (so Ctrl+Z reverts the whole rename), then applies any declaring-file move
        // (NOT undoable — a filesystem rename), updates the in-memory model, and reloads the solution.
        internal void ExecuteScopedRename(
            TypeModel typeTarget, FunctionModel fnTarget, string newName,
            HelixPythonNamespace.Workspace.Services.RenameScope scope)
        {
            string oldName = typeTarget != null ? typeTarget.Name : fnTarget.Name;
            ProjectModel owningProject = typeTarget != null ? typeTarget.Project : fnTarget.Project;

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    // C++ rides on a separate engine (CppSymbolRenameService, over
                    // CppIdentifierRewriter) but deliberately shares FileTextEdit/FileMove with the
                    // Python one, so everything below this point — the undoable command, the
                    // declaring-file move, tab reload, status text — needs no per-language branch.
                    List<HelixPythonNamespace.Workspace.Services.FileTextEdit> computedEdits;
                    HelixPythonNamespace.Workspace.Services.FileMove? declaringFileMove;
                    int totalHits;
                    if (owningProject != null && owningProject.Language == ProjectLanguage.Cpp)
                    {
                        var cppService = new HelixPythonNamespace.Workspace.Cuda.CppSymbolRenameService();
                        HelixPythonNamespace.Workspace.Cuda.CppRenameScope cppScope = ToCppRenameScope(scope);
                        HelixPythonNamespace.Workspace.Cuda.CppSymbolRenameService.ComputedRename c =
                            typeTarget != null
                                ? cppService.ComputeRenameType(_currentSolution, typeTarget, newName, cppScope)
                                : cppService.ComputeRenameFunction(_currentSolution, fnTarget, newName, cppScope);
                        computedEdits = c.Edits;
                        declaringFileMove = c.DeclaringFileMove;
                        totalHits = c.TotalHits;
                    }
                    else
                    {
                        var service = new HelixPythonNamespace.Workspace.Services.SymbolRenameService();
                        HelixPythonNamespace.Workspace.Services.SymbolRenameService.ComputedRename c =
                            typeTarget != null
                                ? service.ComputeRenameType(_currentSolution, typeTarget, newName, scope)
                                : service.ComputeRenameFunction(_currentSolution, fnTarget, newName, scope);
                        computedEdits = c.Edits;
                        declaringFileMove = c.DeclaringFileMove;
                        totalHits = c.TotalHits;
                    }

                    // Build the undoable file-edit command from the computed edits.
                    var edits = new List<HelixPythonNamespace.App.Commands.ScopedFileEditCommand.FileEdit>(computedEdits.Count);
                    foreach (var ed in computedEdits)
                        edits.Add(new HelixPythonNamespace.App.Commands.ScopedFileEditCommand.FileEdit(ed.Path, ed.OldText, ed.NewText));

                    var cmd = new HelixPythonNamespace.App.Commands.ScopedFileEditCommand(
                        "Rename " + oldName + " → " + newName, edits);
                    if (_canvas != null && _canvas.CommandDispatcher != null)
                        _canvas.CommandDispatcher.Execute(cmd);
                    else
                        cmd.Execute();

                    // Declaring-file move (if the file stem matched old name) — applied
                    // after text edits. Not undoable through the command stack; documented.
                    string movedFrom = null, movedTo = null;
                    if (declaringFileMove.HasValue)
                    {
                        var mv = declaringFileMove.Value;
                        try
                        {
                            if (!File.Exists(mv.NewPath)) File.Move(mv.OldPath, mv.NewPath);
                            if (typeTarget != null) typeTarget.SourceFilePath = mv.NewPath;
                            movedFrom = mv.OldPath;
                            movedTo = mv.NewPath;
                        }
                        catch { }
                    }

                    if (typeTarget != null) typeTarget.Name = newName;
                    if (fnTarget != null) fnTarget.Name = newName;

                    var editedPaths = new List<string>(edits.Count);
                    foreach (var ed in edits) editedPaths.Add(ed.Path);
                    ReloadOpenEditorTabsAfterEdit(editedPaths, movedFrom, movedTo);

                    _statusLabel.Text = string.Format(
                        "Renamed {0} → {1} in {2} file(s) ({3} occurrence{4})",
                        oldName, newName, computedEdits.Count, totalHits, totalHits == 1 ? "" : "s");
                }
                ReloadSolutionAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Rename failed: " + ex.Message,
                    "Rename Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal void ExecuteScopedVariableRename(
            HelixPythonNamespace.Workspace.Python.VariableSymbol variable, string newName,
            HelixPythonNamespace.Workspace.Services.RenameScope scope)
        {
            if (variable == null) return;
            string oldName = variable.Name;
            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    var service = new HelixPythonNamespace.Workspace.Services.SymbolRenameService();
                    var computed = service.ComputeRenameVariable(_currentSolution, variable, newName, scope);
                    var edits = new List<HelixPythonNamespace.App.Commands.ScopedFileEditCommand.FileEdit>(computed.Edits.Count);
                    foreach (var edit in computed.Edits)
                        edits.Add(new HelixPythonNamespace.App.Commands.ScopedFileEditCommand.FileEdit(
                            edit.Path, edit.OldText, edit.NewText));
                    var command = new HelixPythonNamespace.App.Commands.ScopedFileEditCommand(
                        "Rename " + oldName + " â†’ " + newName, edits);
                    if (_canvas != null && _canvas.CommandDispatcher != null) _canvas.CommandDispatcher.Execute(command);
                    else command.Execute();
                    variable.Name = newName;
                    var editedPaths = new List<string>(edits.Count);
                    foreach (var edit in edits) editedPaths.Add(edit.Path);
                    ReloadOpenEditorTabsAfterEdit(editedPaths, null, null);
                    _statusLabel.Text = string.Format(
                        "Renamed {0} â†’ {1} in {2} file(s) ({3} occurrence{4})",
                        oldName, newName, computed.Edits.Count, computed.TotalHits,
                        computed.TotalHits == 1 ? "" : "s");
                }
                ReloadSolutionAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Rename failed: " + ex.Message,
                    "Rename Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Maps the Python-side scope picker (RenameSymbolDialog only knows the one enum) onto the
        // narrower C++ scope (see CppRenameScope's doc comment for why there's no Package member):
        // File -> File, Solution -> Solution, and Package/Project both collapse to Project, since a
        // CMake-backed ProjectModel already IS "the one target this symbol lives in" — the same
        // grouping Package exists to express for Python.
        private static HelixPythonNamespace.Workspace.Cuda.CppRenameScope ToCppRenameScope(HelixPythonNamespace.Workspace.Services.RenameScope scope)
        {
            switch (scope)
            {
                case HelixPythonNamespace.Workspace.Services.RenameScope.File:
                    return HelixPythonNamespace.Workspace.Cuda.CppRenameScope.File;
                case HelixPythonNamespace.Workspace.Services.RenameScope.Solution:
                    return HelixPythonNamespace.Workspace.Cuda.CppRenameScope.Solution;
                default:
                    return HelixPythonNamespace.Workspace.Cuda.CppRenameScope.Project;
            }
        }

        // Member (method/property/field) rename — SymbolRenameService.ComputeRenameMember is scoped
        // to the owning class's body only (see its doc comment for why), so unlike
        // ExecuteScopedRename there is exactly one file edit and no scope picker. Reuses the same
        // undoable ScopedFileEditCommand plumbing so member rename gets the same Ctrl+Z coverage as
        // type/function rename, and surfaces ComputedRename.ErrorMessage (a failure mode types/
        // functions don't have — e.g. a Field assigned in more than one place isn't ambiguous for
        // rename the way it is for delete, but the class itself might not parse) via a message box
        // instead of silently doing nothing.
        private void ExecuteMemberRename(TypeModel owner, MemberModel member, string newName)
        {
            string oldName = member.Name;

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    List<HelixPythonNamespace.Workspace.Services.FileTextEdit> computedEdits;
                    int totalHits;
                    string errorMessage;
                    if (owner.Project != null && owner.Project.Language == ProjectLanguage.Cpp)
                    {
                        var cppService = new HelixPythonNamespace.Workspace.Cuda.CppSymbolRenameService();
                        HelixPythonNamespace.Workspace.Cuda.CppSymbolRenameService.ComputedRename c =
                            cppService.ComputeRenameMember(_currentSolution, owner, member, newName);
                        computedEdits = c.Edits;
                        totalHits = c.TotalHits;
                        errorMessage = c.ErrorMessage;
                    }
                    else
                    {
                        var service = new HelixPythonNamespace.Workspace.Services.SymbolRenameService();
                        HelixPythonNamespace.Workspace.Services.SymbolRenameService.ComputedRename c =
                            service.ComputeRenameMember(_currentSolution, owner, member, newName);
                        computedEdits = c.Edits;
                        totalHits = c.TotalHits;
                        errorMessage = c.ErrorMessage;
                    }

                    if (computedEdits.Count == 0)
                    {
                        if (!string.IsNullOrEmpty(errorMessage))
                        {
                            MessageBox.Show(this, "Could not rename '" + oldName + "': " + errorMessage,
                                "Rename Member", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        return;
                    }

                    var edits = new List<HelixPythonNamespace.App.Commands.ScopedFileEditCommand.FileEdit>(computedEdits.Count);
                    foreach (var ed in computedEdits)
                        edits.Add(new HelixPythonNamespace.App.Commands.ScopedFileEditCommand.FileEdit(ed.Path, ed.OldText, ed.NewText));

                    var cmd = new HelixPythonNamespace.App.Commands.ScopedFileEditCommand(
                        "Rename " + oldName + " → " + newName, edits);
                    if (_canvas != null && _canvas.CommandDispatcher != null)
                        _canvas.CommandDispatcher.Execute(cmd);
                    else
                        cmd.Execute();

                    member.Name = newName;

                    var editedPaths = new List<string>(edits.Count);
                    foreach (var ed in edits) editedPaths.Add(ed.Path);
                    ReloadOpenEditorTabsAfterEdit(editedPaths, null, null);

                    _statusLabel.Text = string.Format(
                        "Renamed {0} → {1} ({2} occurrence{3})",
                        oldName, newName, totalHits, totalHits == 1 ? "" : "s");
                }
                ReloadSolutionAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Rename failed: " + ex.Message,
                    "Rename Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Find All References for a type: resolves every reference across the solution (same scope-aware
        /// binding logic the rename uses), then shows a modeless results list. Activating a row opens the
        /// file and jumps to the reference. Returns the results window (for headless capture / testing).
        /// </summary>
        internal Form ShowFindAllReferences(TypeModel type)
        {
            if (type == null || _currentSolution == null) return null;

            // Phase timing behind the same switch as the solution-load trace: parallelising the file
            // scan in iter 344 gained far less than 28 cores should, so the cost has to be measured
            // rather than guessed at.
            bool trace = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HELIXSTUDIO_TRACE_LOAD"));
            var refWatch = trace ? System.Diagnostics.Stopwatch.StartNew() : null;

            var service = new HelixPythonNamespace.Workspace.Services.SymbolRenameService();
            List<HelixPythonNamespace.Workspace.Services.ReferenceHit> hits =
                service.FindReferencesType(_currentSolution, type,
                    HelixPythonNamespace.Workspace.Services.RenameScope.Solution);

            if (trace) { Console.WriteLine("[refs] search {0,6} ms ({1} hits)", refWatch.ElapsedMilliseconds, hits.Count); refWatch.Restart(); }

            ShowReferencesInTab(type.Name, hits);
            if (trace) Console.WriteLine("[refs] populate tab {0,6} ms", refWatch.ElapsedMilliseconds);
            return null;
        }

        /// <summary>Find All References for a top-level function (same as the type overload).</summary>
        internal Form ShowFindAllReferences(FunctionModel fn)
        {
            if (fn == null || _currentSolution == null) return null;
            var service = new HelixPythonNamespace.Workspace.Services.SymbolRenameService();
            List<HelixPythonNamespace.Workspace.Services.ReferenceHit> hits =
                service.FindReferencesFunction(_currentSolution, fn,
                    HelixPythonNamespace.Workspace.Services.RenameScope.Solution);

            ShowReferencesInTab(fn.Name, hits);
            return null;
        }

        /// <summary>
        /// Populate the References top-level tab and bring it forward. Replaces the old modeless
        /// FindReferencesForm dialog (iter 367): a docked tab keeps the results visible while the user
        /// reads code and navigates, sits alongside Compile/Tests, and never covers the editor.
        /// </summary>
        private void ShowReferencesInTab(string symbol, List<HelixPythonNamespace.Workspace.Services.ReferenceHit> hits)
        {
            if (_referencesPanel != null) _referencesPanel.ShowReferences(symbol, hits);
            ShowTab("references");
            if (_statusLabel != null)
                _statusLabel.Text = string.Format("Find All References: '{0}' — {1} reference(s)",
                    symbol ?? string.Empty, hits != null ? hits.Count : 0);
        }

        /// <summary>
        /// Extract Variable: lifts the active editor's selected expression onto a new assignment above
        /// the current statement and replaces the selection with <paramref name="varName"/>. Returns
        /// false (with a status message) when there's no usable selection.
        /// </summary>
        internal bool ExtractVariableInActiveEditor(string varName)
        {
            if (!(_editorInnerTabControl != null
                && _editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage etp)) return false;
            var editor = etp.Editor;
            int selStart = editor.SelectionStart;
            int selEnd = editor.SelectionEnd;
            HelixPythonNamespace.Workspace.Services.ExtractVariableService.Result r =
                HelixPythonNamespace.Workspace.Services.ExtractVariableService.Extract(editor.Text, selStart, selEnd, varName);
            if (!r.Ok)
            {
                if (_statusLabel != null) _statusLabel.Text = "Extract Variable: " + (r.Error ?? "failed");
                return false;
            }
            editor.Text = r.NewText;
            try
            {
                editor.SelectionStart = r.VariableOffset;
                editor.SelectionEnd = r.VariableOffset + varName.Length;
            }
            catch { }
            if (_statusLabel != null) _statusLabel.Text = "Extracted variable '" + varName + "'";
            return true;
        }

        /// <summary>Prompt for a name and Extract Variable from the active editor's current selection.</summary>
        internal void PromptExtractVariable()
        {
            using (var dlg = new HelixPythonNamespace.App.Dialogs.SimpleInputDialog(
                "Extract Variable", "Name for the extracted variable:", "value"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (string.IsNullOrWhiteSpace(dlg.Value)) return;
                ExtractVariableInActiveEditor(dlg.Value.Trim());
            }
        }

        /// <summary>Extract Constant: hoists the active editor's selection to a module-level constant.</summary>
        internal bool ExtractConstantInActiveEditor(string constName)
        {
            if (!(_editorInnerTabControl != null
                && _editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage etp)) return false;
            var editor = etp.Editor;
            HelixPythonNamespace.Workspace.Services.ExtractVariableService.Result r =
                HelixPythonNamespace.Workspace.Services.ExtractVariableService.ExtractConstant(
                    editor.Text, editor.SelectionStart, editor.SelectionEnd, constName);
            if (!r.Ok)
            {
                if (_statusLabel != null) _statusLabel.Text = "Extract Constant: " + (r.Error ?? "failed");
                return false;
            }
            editor.Text = r.NewText;
            try
            {
                editor.SelectionStart = r.VariableOffset;
                editor.SelectionEnd = r.VariableOffset + constName.Length;
            }
            catch { }
            if (_statusLabel != null) _statusLabel.Text = "Extracted constant '" + constName + "'";
            return true;
        }

        /// <summary>
        /// Verify the active `.cu` editor against the REAL nvcc toolchain and mark its authoritative
        /// errors in the editor. Blocks briefly (nvcc runs a real compile) with a wait cursor. Returns
        /// the nvcc error count, or a negative code (see <c>PythonEditorControl.VerifyWithNvcc</c>).
        /// </summary>
        internal int VerifyActiveCudaWithNvcc()
        {
            if (!(_editorInnerTabControl != null
                && _editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage etp)) return -1;
            Cursor prev = Cursor.Current;
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                string summary;
                int n = etp.Editor.VerifyWithNvcc(out summary);
                if (_statusLabel != null)
                    _statusLabel.Text = summary ?? ("nvcc: " + n + " error(s)");
                return n;
            }
            finally { Cursor.Current = prev; }
        }

        internal void OnVerifyCudaWithNvcc(object sender, EventArgs e) => VerifyActiveCudaWithNvcc();

        /// <summary>
        /// Tools ▸ Register TOML Files. Makes this build the per-user handler for <c>.toml</c> in
        /// Explorer, so double-clicking a <c>pyproject.toml</c> starts HelixPython on the whole
        /// solution (see <see cref="OpenShellPath"/>).
        ///
        /// It edits the user's registry, so it confirms first and then reports what it actually
        /// achieved — including the one case it CANNOT fix: an existing Explorer "always open with"
        /// choice outranks the ProgId this writes and is hash-protected, so the honest answer there
        /// is the manual step, not a success box.
        /// </summary>
        internal void OnRegisterTomlFiles(object sender, EventArgs e)
        {
            const string caption = "Register TOML Files";
            string exePath = Application.ExecutablePath;

            if (!HelixPythonNamespace.Core.Shell.TomlFileAssociation.IsSupportedPlatform)
            {
                ReportTomlAssociation("File associations are a Windows feature; nothing to register on this host.",
                                      caption, MessageBoxIcon.Information);
                return;
            }

            bool interactive = !string.Equals(
                Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1", StringComparison.Ordinal);
            if (interactive)
            {
                string prompt =
                    "Associate .toml files with HelixPython for the current user?" + Environment.NewLine + Environment.NewLine
                    + "Double-clicking a pyproject.toml in Explorer will then open the whole solution that owns it"
                    + " in this IDE." + Environment.NewLine + Environment.NewLine
                    + "This writes three keys under HKEY_CURRENT_USER\\Software\\Classes (no administrator rights"
                    + " needed, other users unaffected) pointing at:" + Environment.NewLine
                    + "    " + exePath;
                if (MessageBox.Show(this, prompt, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
                    != DialogResult.OK)
                {
                    if (_statusLabel != null) _statusLabel.Text = "TOML registration cancelled.";
                    return;
                }
            }

            var report = HelixPythonNamespace.Core.Shell.TomlFileAssociation.Register(exePath);
            string message = HelixPythonNamespace.Core.Shell.TomlFileAssociation.Explain(report);
            ReportTomlAssociation(message, caption,
                report.Succeeded
                    ? (report.BlockingUserChoiceProgId == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning)
                    : MessageBoxIcon.Error);
        }

        // Status bar always; modal only when there is somebody to dismiss it (same guard as
        // ReportFixProblem — a box raised under HELIXSTUDIO_NONINTERACTIVE blocks the STA thread).
        private void ReportTomlAssociation(string message, string caption, MessageBoxIcon icon)
        {
            if (_statusLabel != null)
                _statusLabel.Text = message.Replace(Environment.NewLine, " ");
            if (string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1",
                              StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine("[noninteractive] " + caption + ": " + message);
                return;
            }
            MessageBox.Show(this, message, caption, MessageBoxButtons.OK, icon);
        }

        /// <summary>Remove unused imports from the active Python editor (Organize Imports). Returns 1 when
        /// something was removed, 0 when there were none, -1 when not applicable.</summary>
        internal int OrganizeImportsInActiveEditor()
        {
            if (!(_editorInnerTabControl != null
                && _editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage etp)) return -1;
            var editor = etp.Editor;
            string before = editor.Text;
            string after = HelixPythonNamespace.Workspace.Services.OrganizeImportsService.RemoveUnusedImports(before);
            if (string.Equals(after, before, StringComparison.Ordinal))
            {
                if (_statusLabel != null) _statusLabel.Text = "Organize Imports: no unused imports";
                return 0;
            }
            editor.Text = after;
            if (_statusLabel != null) _statusLabel.Text = "Organize Imports: removed unused imports";
            return 1;
        }

        /// <summary>Prompt for a name and Extract Constant from the active editor's current selection.</summary>
        internal void PromptExtractConstant()
        {
            using (var dlg = new HelixPythonNamespace.App.Dialogs.SimpleInputDialog(
                "Extract Constant", "Name for the module-level constant:", "CONSTANT"))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (string.IsNullOrWhiteSpace(dlg.Value)) return;
                ExtractConstantInActiveEditor(dlg.Value.Trim());
            }
        }

        /// <summary>True when the active editor tab has a non-empty selection.</summary>
        internal bool ActiveEditorHasSelection()
        {
            if (_editorInnerTabControl != null
                && _editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage etp)
                return etp.Editor.SelectionEnd > etp.Editor.SelectionStart;
            return false;
        }

        /// <summary>
        /// Call Hierarchy (incoming calls) for a function: resolves every caller across the solution and
        /// shows a modeless results list. Activating a row opens the call site.
        /// </summary>
        internal Form ShowCallHierarchy(FunctionModel fn)
        {
            if (fn == null || _currentSolution == null) return null;
            var service = new HelixPythonNamespace.Workspace.Services.SymbolRenameService();
            List<HelixPythonNamespace.Workspace.Services.CallerHit> callers =
                service.FindCallers(_currentSolution, fn, HelixPythonNamespace.Workspace.Services.RenameScope.Solution);

            // Populate the Call Hierarchy tab and bring it forward (iter 368; was a modeless dialog).
            if (_callHierarchyPanel != null) _callHierarchyPanel.ShowCallers(fn.Name, callers);
            ShowTab("callHierarchy");
            if (_statusLabel != null)
                _statusLabel.Text = string.Format("Call Hierarchy: '{0}' — {1} caller(s)", fn.Name, callers.Count);
            return null;
        }

        /// <summary>Call Hierarchy for a function identified by name + declaration site (used by the
        /// headless harness and by call sites that only have raw coordinates).</summary>
        internal Form ShowCallHierarchyFor(string funcName, string filePath, int declLine)
        {
            var fn = new FunctionModel { Name = funcName, SourceFilePath = filePath, SourceLineNumber = declLine };
            return ShowCallHierarchy(fn);
        }

        // ---- Type-hierarchy navigation (Go To Base / Derived) ----------------

        /// <summary>Resolved base types (Inherits/Implements edges) of the given type within the
        /// loaded solution. External bases (e.g. <c>ABC</c>) that don't resolve to a solution type are
        /// omitted.</summary>
        internal List<TypeModel> GetBaseTypes(TypeModel type)
        {
            var list = new List<TypeModel>();
            if (type == null || _currentSolution == null || type.BaseTypes == null) return list;
            foreach (HelixPythonNamespace.Core.Model.TypeReference br in type.BaseTypes)
            {
                if (br == null) continue;
                if (br.RelationshipKind != HelixPythonNamespace.Core.Model.Enums.RelationshipKind.Inherits
                    && br.RelationshipKind != HelixPythonNamespace.Core.Model.Enums.RelationshipKind.Implements) continue;
                TypeModel resolved = br.Resolve(_currentSolution);
                if (resolved != null && !list.Contains(resolved)) list.Add(resolved);
            }
            return list;
        }

        /// <summary>Types in the solution that directly inherit from (or implement) the given type.</summary>
        internal List<TypeModel> GetDerivedTypes(TypeModel type)
        {
            var list = new List<TypeModel>();
            if (type == null || _currentSolution == null) return list;
            foreach (TypeModel t in _currentSolution.GetAllTypes())
            {
                if (t == null || ReferenceEquals(t, type) || t.BaseTypes == null) continue;
                foreach (HelixPythonNamespace.Core.Model.TypeReference br in t.BaseTypes)
                {
                    if (br == null) continue;
                    if (br.RelationshipKind != HelixPythonNamespace.Core.Model.Enums.RelationshipKind.Inherits
                        && br.RelationshipKind != HelixPythonNamespace.Core.Model.Enums.RelationshipKind.Implements) continue;
                    if (ReferenceEquals(br.Resolve(_currentSolution), type)) { list.Add(t); break; }
                }
            }
            return list;
        }

        /// <summary>
        /// All CONCRETE implementations of an interface/abstract type — the transitive set of derived
        /// types (any depth) whose kind is a concrete class. This is the "Go To Implementation" set: it
        /// skips intermediate interfaces/abstract bases and reaches the concrete leaves, so it handles
        /// multi-level hierarchies that the direct-only Go To Derived does not.
        /// </summary>
        internal List<TypeModel> GetImplementations(TypeModel type)
        {
            var result = new List<TypeModel>();
            if (type == null || _currentSolution == null) return result;
            var seen = new HashSet<TypeModel>();
            var queue = new Queue<TypeModel>(GetDerivedTypes(type));
            while (queue.Count > 0)
            {
                TypeModel t = queue.Dequeue();
                if (t == null || !seen.Add(t)) continue;   // guard cycles / diamonds
                if (t.TypeKind == HelixPythonNamespace.Core.Model.Enums.TypeKind.ConcreteClass && !result.Contains(t))
                    result.Add(t);
                foreach (TypeModel d in GetDerivedTypes(t)) queue.Enqueue(d);
            }
            return result;
        }

        /// <summary>Go To Implementation: navigate to a concrete implementer (picker disambiguates
        /// several). Returns how many implementations were found.</summary>
        internal int GoToImplementation(TypeModel type)
        {
            return NavigateAmongTypes(GetImplementations(type), type != null ? type.Name : string.Empty, "implementation");
        }

        /// <summary>Go To Base Type: navigate to the type's base (a picker disambiguates several).
        /// Returns how many base types were found.</summary>
        internal int GoToBaseType(TypeModel type)
        {
            return NavigateAmongTypes(GetBaseTypes(type), type != null ? type.Name : string.Empty, "base type");
        }

        /// <summary>Go To Derived Type: navigate to a subclass (a picker disambiguates several).
        /// Returns how many derived types were found.</summary>
        internal int GoToDerivedType(TypeModel type)
        {
            return NavigateAmongTypes(GetDerivedTypes(type), type != null ? type.Name : string.Empty, "derived type");
        }

        private int NavigateAmongTypes(List<TypeModel> targets, string fromName, string kind)
        {
            if (targets == null || targets.Count == 0)
            {
                if (_statusLabel != null)
                    _statusLabel.Text = string.Format("No {0} found for '{1}'", kind, fromName);
                return 0;
            }
            TypeModel chosen = targets[0];
            if (targets.Count > 1)
            {
                using (var picker = new HelixPythonNamespace.App.Dialogs.GoToTypePickerForm(fromName + " " + kind, targets))
                {
                    if (picker.ShowDialog(this) == DialogResult.OK) chosen = picker.Selected;
                    else return targets.Count;
                }
            }
            if (chosen != null)
            {
                OpenSourceForType(chosen);
                if (_statusLabel != null)
                    _statusLabel.Text = string.Format("Go To {0}: {1} → {2}", kind, fromName, chosen.Name);
            }
            return targets.Count;
        }

        // "Rename Package" tree command: resolves the package's directory, prompts for a new name,
        // then delegates the on-disk rename + solution-wide import rewrite to PackageRenameService.
        // Open editor tabs are flushed first (so disk is consistent) and re-pointed afterward, then
        // the solution is reloaded to pick up the new module paths.
        internal void OnTreeRenamePackage(object sender, TreeOperationEventArgs e)
        {
            if (e == null || _currentSolution == null) return;

            // Resolve the target package directory and a sensible default new-name
            // based on the node's Tag.
            string packageDir = ResolvePackageDirectory(e.Item);
            if (string.IsNullOrEmpty(packageDir) || !Directory.Exists(packageDir))
            {
                MessageBox.Show(this, "Could not resolve a package directory for this node.",
                    "Rename Package", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string oldName = Path.GetFileName(packageDir.TrimEnd(Path.DirectorySeparatorChar));
            string newName;
            using (var dlg = new HelixPythonNamespace.App.Dialogs.SimpleInputDialog(
                "Rename Package", "New package name for '" + oldName + "':", oldName))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                newName = (dlg.Value ?? string.Empty).Trim();
            }
            if (string.IsNullOrEmpty(newName) || newName == oldName) return;

            try
            {
                string workspaceRoot = FindWorkspaceRoot(packageDir);
                var svc = new HelixPythonNamespace.Workspace.Services.PackageRenameService();
                var c = svc.Compute(_currentSolution, packageDir, newName, workspaceRoot);

                using (_fileWatcher?.SuspendWhileEditing())
                {
                    // Flush all open tabs' pending saves first so the disk is consistent.
                    if (_editorInnerTabControl != null)
                    {
                        foreach (TabPage p in _editorInnerTabControl.TabPages)
                        {
                            if (p is HelixPythonNamespace.Editor.EditorTabPage etp) { try { etp.FlushPendingSave(); } catch { } }
                        }
                    }
                    (int filesChanged, int _) = svc.Apply(c);

                    // Re-point open editor tabs whose paths were under the old directory.
                    if (_editorInnerTabControl != null)
                    {
                        foreach (TabPage p in _editorInnerTabControl.TabPages)
                        {
                            var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                            if (etp == null || string.IsNullOrEmpty(etp.FilePath)) continue;
                            string rebased = RebaseOpenTabPath(etp.FilePath, c.DirectoryMove.OldPath, c.DirectoryMove.NewPath);
                            if (rebased != null) etp.FilePath = rebased;
                        }
                        RecomputeEditorTabWidths();
                    }

                    _statusLabel.Text = string.Format(
                        "Renamed package '{0}' → '{1}' ({2} file(s) updated)",
                        oldName, newName, filesChanged);
                }
                ReloadSolutionAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Rename package failed: " + ex.Message,
                    "Rename Package", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // "Edit Signature" tree command for a function node — forwards to the signature editor.
        private void OnTreeEditSignature(object sender, TreeOperationEventArgs e)
        {
            if (e == null || _currentSolution == null) return;
            FunctionModel fn = e.Item as FunctionModel;
            if (fn == null) return;
            ShowEditSignatureDialog(fn);
        }

        // If tabPath lives under oldDir, returns the equivalent path rebased onto newDir (case-
        // insensitive prefix match); otherwise null. Used to re-point open editor tabs whose files
        // moved as part of a package/directory rename.
        private static string RebaseOpenTabPath(string tabPath, string oldDir, string newDir)
        {
            if (string.IsNullOrEmpty(tabPath) || string.IsNullOrEmpty(oldDir) || string.IsNullOrEmpty(newDir))
                return null;
            string fullOld = Path.GetFullPath(oldDir).TrimEnd(Path.DirectorySeparatorChar);
            string fullTab = Path.GetFullPath(tabPath);
            if (fullTab.StartsWith(fullOld + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(newDir).TrimEnd(Path.DirectorySeparatorChar)
                    + fullTab.Substring(fullOld.Length);
            }
            return null;
        }

        // Same idea as RebaseOpenTabPath but also handles the path being EXACTLY the moved root
        // itself (a project directory can equal the solution root in a single-project solution,
        // where RebaseOpenTabPath's "must be strictly under, with a separator" rule would miss it).
        // Falls back to the original path unchanged when it isn't under the old root at all.
        private static string RebaseUnderMovedRoot(string path, string oldRootDir, string newRootDir)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string fullOld = Path.GetFullPath(oldRootDir).TrimEnd(Path.DirectorySeparatorChar);
            string fullNew = Path.GetFullPath(newRootDir).TrimEnd(Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(fullPath, fullOld, StringComparison.OrdinalIgnoreCase)) return fullNew;
            string prefix = fullOld + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return fullNew + fullPath.Substring(fullOld.Length);
            return path;
        }

        // A solution folder move/rename leaves every per-solution setting (docking layout, startup
        // project, checked-project visibility, last-open editor files, tab order) keyed under the
        // OLD root TOML path — re-key them to the new path so they aren't silently lost, the same
        // way _recentFilesManager's entry is re-pointed by its caller.
        private void RekeySolutionSettings(string oldTomlPath, string newTomlPath)
        {
            if (_settingsManager == null || string.IsNullOrEmpty(oldTomlPath) || string.IsNullOrEmpty(newTomlPath)) return;
            UserSettings s = _settingsManager.Settings;

            MoveKey(s.SolutionProjectVisibility, oldTomlPath, newTomlPath);
            MoveKey(s.SolutionDockingLayouts, oldTomlPath, newTomlPath);
            MoveKey(s.LastOpenEditorFiles, oldTomlPath, newTomlPath);
            MoveKey(s.SolutionStartupProject, oldTomlPath, newTomlPath);
            MoveKey(s.SolutionTopLevelTabOrder, oldTomlPath, newTomlPath);

            _settingsManager.Save();
        }

        private static void MoveKey<TValue>(Dictionary<string, TValue> dict, string oldKey, string newKey)
        {
            if (dict == null) return;
            TValue value;
            if (dict.TryGetValue(oldKey, out value))
            {
                dict.Remove(oldKey);
                dict[newKey] = value;
            }
        }

        /// <summary>
        /// Given a tree-node tag, derive the directory of the package to rename.
        /// </summary>
        private string ResolvePackageDirectory(object tag)
        {
            if (tag is HelixPythonNamespace.UI.Controls.ProjectTreePanel.FolderGroup folder &&
                folder.Project != null && !string.IsNullOrEmpty(folder.RelativePath))
            {
                // §5.2/§5.7: the folder node IS the package directory the user pointed at.
                try
                {
                    string dir = Path.Combine(folder.Project.ProjectDirectory,
                        folder.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    return Path.GetFullPath(dir);
                }
                catch (ArgumentException) { return null; }
            }
            if (tag is HelixPythonNamespace.UI.Controls.ProjectTreePanel.FileGroup fg)
            {
                // Parent directory of the file IS the package dir (assuming __init__.py lives there).
                if (!string.IsNullOrEmpty(fg.FilePath))
                    return Path.GetDirectoryName(Path.GetFullPath(fg.FilePath));
                return null;
            }
            if (tag is ProjectModel pm)
            {
                // Use the project's first top-level package directory if any, else
                // fall back to the project directory itself.
                if (!string.IsNullOrEmpty(pm.ProjectDirectory) && Directory.Exists(pm.ProjectDirectory))
                {
                    // Prefer `<project>/src/<single-pkg>` if it exists and has one package.
                    string srcDir = Path.Combine(pm.ProjectDirectory, "src");
                    if (Directory.Exists(srcDir))
                    {
                        string[] subdirs = Directory.GetDirectories(srcDir);
                        foreach (string sd in subdirs)
                        {
                            if (File.Exists(Path.Combine(sd, "__init__.py"))) return sd;
                        }
                    }
                    return pm.ProjectDirectory;
                }
            }
            if (tag is TypeModel tm && !string.IsNullOrEmpty(tm.SourceFilePath))
            {
                return Path.GetDirectoryName(Path.GetFullPath(tm.SourceFilePath));
            }
            if (tag is FunctionModel fnm && !string.IsNullOrEmpty(fnm.SourceFilePath))
            {
                return Path.GetDirectoryName(Path.GetFullPath(fnm.SourceFilePath));
            }
            return null;
        }

        // Thin wrapper so the tree handler and other callers share one entry point into the editor.
        private void ShowEditSignatureDialog(FunctionModel fn)
        {
            EditFunctionSignature(fn);
        }

        /// <summary>
        /// Prompt the user for a new signature for <paramref name="fn"/> and apply
        /// the edits (def site + call sites across the chosen scope), threaded
        /// through the undo stack. Warnings (e.g. calls with *args) surface in the
        /// status bar.
        /// </summary>
        internal void EditFunctionSignature(FunctionModel fn)
        {
            if (fn == null || _currentSolution == null) return;
            if (string.IsNullOrEmpty(fn.SourceFilePath) || !File.Exists(fn.SourceFilePath))
            {
                MessageBox.Show(this, "Declaring file not found for this function.",
                    "Edit Signature", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Parse the declaring file to recover the current parameter list.
            HelixPythonNamespace.Python.Syntax.ModuleSyntax module;
            string source;
            try
            {
                source = File.ReadAllText(fn.SourceFilePath);
                module = global::HelixPythonNamespace.Python.Python.ParseModule(source, fn.SourceFilePath).Module;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to parse declaring file: " + ex.Message,
                    "Edit Signature", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            HelixPythonNamespace.Python.Syntax.FunctionDefSyntax target = null;
            foreach (HelixPythonNamespace.Python.Syntax.StatementSyntax s in module.Statements)
            {
                if (s is HelixPythonNamespace.Python.Syntax.FunctionDefSyntax fd && fd.Name == fn.Name)
                {
                    if (fn.SourceLineNumber <= 0 || fd.Span.Start.Line == fn.SourceLineNumber ||
                        fd.Span.Start.Line + 1 == fn.SourceLineNumber)
                    {
                        target = fd;
                        break;
                    }
                }
            }
            if (target == null)
            {
                MessageBox.Show(this, "Could not locate the function definition in the source file.",
                    "Edit Signature", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Seed the dialog with current parameters.
            List<HelixPythonNamespace.Workspace.Services.ParameterEdit> initial =
                new List<HelixPythonNamespace.Workspace.Services.ParameterEdit>();
            foreach (var p in target.Parameters)
            {
                initial.Add(new HelixPythonNamespace.Workspace.Services.ParameterEdit
                {
                    OldName = p.Name,
                    NewName = p.Name,
                    Annotation = p.Annotation != null
                        ? source.Substring(p.Annotation.Span.Start.Offset,
                            p.Annotation.Span.End.Offset - p.Annotation.Span.Start.Offset) : "",
                    DefaultValue = p.DefaultValue != null
                        ? source.Substring(p.DefaultValue.Span.Start.Offset,
                            p.DefaultValue.Span.End.Offset - p.DefaultValue.Span.Start.Offset) : "",
                    Kind = MapParamKind(p.Kind),
                });
            }
            string returnAnnot = target.ReturnAnnotation != null
                ? source.Substring(target.ReturnAnnotation.Span.Start.Offset,
                    target.ReturnAnnotation.Span.End.Offset - target.ReturnAnnotation.Span.Start.Offset)
                : "";

            using (var dlg = new HelixPythonNamespace.App.Dialogs.EditSignatureDialog(fn.Name, initial, returnAnnot))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var newParams = dlg.EditedParams;
                if (newParams == null) return;

                var service = new HelixPythonNamespace.Workspace.Services.SignatureEditService();
                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        var c = service.Compute(_currentSolution, fn.Project,
                            fn.SourceFilePath, fn.SourceLineNumber, fn.Name,
                            newParams, dlg.NewReturnAnnotation, dlg.SelectedScope);

                        var edits = new List<HelixPythonNamespace.App.Commands.ScopedFileEditCommand.FileEdit>(c.Edits.Count);
                        foreach (var ed in c.Edits)
                            edits.Add(new HelixPythonNamespace.App.Commands.ScopedFileEditCommand.FileEdit(ed.Path, ed.OldText, ed.NewText));
                        var cmd = new HelixPythonNamespace.App.Commands.ScopedFileEditCommand(
                            "Edit signature " + fn.Name, edits);
                        if (_canvas != null && _canvas.CommandDispatcher != null)
                            _canvas.CommandDispatcher.Execute(cmd);
                        else cmd.Execute();

                        int warns = c.Warnings != null ? c.Warnings.Count : 0;
                        string warnSuffix = warns > 0 ? " — " + warns + " warning(s)" : "";
                        _statusLabel.Text = string.Format(
                            "Edited signature of {0} — {1} file(s), {2} call(s) rewritten{3}",
                            fn.Name, c.Edits.Count, c.CallsRewritten, warnSuffix);

                        if (c.Warnings != null && _problemsPanel != null && warns > 0)
                        {
                            var diags = new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>();
                            foreach (string w in c.Warnings)
                            {
                                diags.Add(new HelixPythonNamespace.VirtualCompiler.CompileDiagnostic(
                                    fn.SourceFilePath, "PY9100",
                                    HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Warning,
                                    new HelixPythonNamespace.Python.Text.SourceSpan(),
                                    w));
                            }
                            _problemsPanel.ShowDiagnostics(diags);
                        }
                    }
                    ReloadSolutionAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Edit signature failed: " + ex.Message,
                        "Edit Signature", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // Translates a parsed Python ParameterKind (positional-only, keyword-only, *args, **kwargs)
        // into the workspace signature-service's ParamKind enum.
        private static HelixPythonNamespace.Workspace.Services.ParamKind MapParamKind(HelixPythonNamespace.Python.Syntax.ParameterKind k)
        {
            switch (k)
            {
                case HelixPythonNamespace.Python.Syntax.ParameterKind.PositionalOnly: return HelixPythonNamespace.Workspace.Services.ParamKind.PositionalOnly;
                case HelixPythonNamespace.Python.Syntax.ParameterKind.KeywordOnly:    return HelixPythonNamespace.Workspace.Services.ParamKind.KeywordOnly;
                case HelixPythonNamespace.Python.Syntax.ParameterKind.VarPositional:  return HelixPythonNamespace.Workspace.Services.ParamKind.VarPositional;
                case HelixPythonNamespace.Python.Syntax.ParameterKind.VarKeyword:     return HelixPythonNamespace.Workspace.Services.ParamKind.VarKeyword;
                default: return HelixPythonNamespace.Workspace.Services.ParamKind.Regular;
            }
        }

        /// <summary>
        /// Refresh the UI after an undoable rename is replayed (from Undo or Redo).
        /// The rename service already updated the TypeModel + source files; we only
        /// need to push the change through the projection and tree view.
        /// </summary>
        internal void RefreshAfterRename()
        {
            if (_projectionBuilder != null)
            {
                // Re-route edges and re-render so the new name shows on the canvas
                _projectionBuilder.RebuildFromSolution(_currentSolution);
            }
            if (_projectTreePanel != null && _currentSolution != null)
            {
                _projectTreePanel.LoadSolutionPreservingExpansion(_currentSolution);
                RefreshStartupProjectStylingFromSettings();
            }
            if (_canvas != null) _canvas.Invalidate();
        }

        /// <summary>
        /// Reads the persisted startup-project name for the current solution
        /// (if any) and forwards it to the tree panel so the correct node goes
        /// bold. No-op when nothing is loaded or no startup project was ever set.
        /// </summary>
        internal void RefreshStartupProjectStylingFromSettings()
        {
            if (_projectTreePanel == null || _currentSolution == null) return;
            // Style from the authoritative model (ProjectModel.IsStartup, loaded from the TOML) so
            // the highlight never disagrees with what Run will actually launch (PSM-10/11).
            string startup = null;
            if (_currentSolution.Projects != null)
                foreach (ProjectModel p in _currentSolution.Projects)
                    if (p != null && p.IsStartup) { startup = p.Name; break; }
            _projectTreePanel.RefreshStartupProjectStyling(startup);
        }

        // "Save" tree command, polymorphic on the node: TypeService/ProjectService edits already
        // auto-persist, so a type/project node just re-saves layout; a solution node additionally
        // writes the .sln. All runs inside a file-watcher suspension to avoid a self-triggered reload.
        private void OnTreeSave(object sender, TreeOperationEventArgs e)
        {
            if (_currentSolution == null) return;

            try
            {
                TypeModel typeModel = e.Item as TypeModel;
                if (typeModel != null)
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        // TypeService operations auto-persist; save layout too
                        SaveLayout();
                        _statusLabel.Text = string.Format("Saved: {0}", typeModel.Name);
                    }
                    return;
                }

                ProjectModel projectModel = e.Item as ProjectModel;
                if (projectModel != null)
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        SaveLayout();
                        _statusLabel.Text = string.Format("Saved project: {0}", projectModel.Name);
                    }
                    return;
                }

                SolutionModel solutionModel = e.Item as SolutionModel;
                if (solutionModel != null)
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        _solutionService.Save(solutionModel);
                        SaveLayout();
                        _statusLabel.Text = string.Format("Saved solution: {0}", solutionModel.Name);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to save: {0}", ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        // "View Source" tree command — opens the selected type's backing .py file in the editor.
        private void OnTreeViewSource(object sender, TreeOperationEventArgs e)
        {
            TypeModel typeModel = e.Item as TypeModel;
            if (typeModel != null)
            {
                OpenSourceForType(typeModel);
            }
        }

        // "Set as Startup Project" tree command: rejects library projects (no __main__.py), persists
        // the chosen project into per-solution settings, and asks the tree to bold that node.
        // ==================================================================================
        // CodebaseSolutionToSolutionExplorerPanelConnector — the layer that owns per-language
        // solution loading, tree icons, context-menu scoping and subsystem launching for the
        // Solution Explorer. The tree-verb handlers below are one-line dispatches into it;
        // MainForm's own role shrinks to hosting the panels the connectors stream into.
        // ==================================================================================

        private Connectors.CodebaseSolutionToSolutionExplorerPanelConnector _explorerConnector;

        internal Connectors.CodebaseSolutionToSolutionExplorerPanelConnector ExplorerConnector
        {
            get
            {
                return _explorerConnector
                    ?? (_explorerConnector = new Connectors.CodebaseSolutionToSolutionExplorerPanelConnector(this));
            }
        }

        // ---- ISolutionExplorerHostServices: the narrow surface the connectors may touch.
        // Explicit implementations so these stay invisible on MainForm's own API.

        void Connectors.ISolutionExplorerHostServices.SetExplorerStatus(string text)
        {
            if (_statusLabel != null) _statusLabel.Text = text;
        }

        void Connectors.ISolutionExplorerHostServices.ShowExplorerInfo(string caption, string message)
        {
            MessageBox.Show(this, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        Task Connectors.ISolutionExplorerHostServices.RunPythonProjectCommandAsync(ProjectModel project, string[] args, string operationVerb)
        {
            return RunPythonCommandAsync(project.FilePath, args, operationVerb);
        }

        void Connectors.ISolutionExplorerHostServices.BuildCMakeTargetFromExplorer(string targetName)
        {
            BuildNativeCMakeTarget(targetName);
        }

        Task<bool> Connectors.ISolutionExplorerHostServices.RunProcessToBuildResultsAsync(string fileName, string arguments, string workingDirectory, string operationTitle)
        {
            return RunProcessToBuildResultsAsync(fileName, arguments, workingDirectory, operationTitle);
        }

        void Connectors.ISolutionExplorerHostServices.RunPythonTestsForProject(string projectDirectory)
        {
            _engineCoordinator.RunTestsForProject(projectDirectory);
        }

        void Connectors.ISolutionExplorerHostServices.RunNativeTestsForProject(string projectDirectory)
        {
            _engineCoordinator.RunNativeCMakeTests(projectDirectory);
        }

        void Connectors.ISolutionExplorerHostServices.DebugPythonTestsForProject(string projectDirectory)
        {
            _debugCoordinator.StartTests(projectDirectory);
        }

        void Connectors.ISolutionExplorerHostServices.PersistPythonStartupSelection(SolutionModel solution, ProjectModel chosen)
        {
            // §8.1: these are IDE-internal pyproject.toml writes — suspend the file
            // watcher so they don't surface as "External Change Detected".
            using (_fileWatcher?.SuspendWhileEditing())
            {
                PersistStartupToToml(solution, chosen);
            }
        }

        bool Connectors.ISolutionExplorerHostServices.HasPythonMainModule(ProjectModel project)
        {
            return HasMainModule(project);
        }

        void Connectors.ISolutionExplorerHostServices.ShowManagePackagesDialog(ProjectModel project)
        {
            // §8.1: the dialog rewrites the project's pyproject.toml on add/remove —
            // IDE-internal writes must not surface as "External Change Detected".
            // The modal blocks all other editing, so suspending for its lifetime is safe.
            using (_fileWatcher?.SuspendWhileEditing())
            using (Form dialog = CreatePackageManagerDialog(project))
            {
                dialog.ShowDialog(this);
            }
        }

        private Form CreatePackageManagerDialog(ProjectModel project)
        {
            var adapter = HelixPythonNamespace.Workspace.LanguageServices.PackageManagerAdapterRegistry.For(project.Language, project.FilePath);
            if (project.Language == ProjectLanguage.Python)
                return new ManagePackagesDialog(project);
            if (adapter != null)
                return new HelixPythonNamespace.App.Dialogs.WorkspacePackageManagerDialog(project, adapter);
            throw new InvalidOperationException("No package manager is registered for " + project.Language + ".");
        }

        private void OnSetStartupProject(object sender, TreeOperationEventArgs e)
        {
            if (_currentSolution == null) return;
            var pm = e?.Item as ProjectModel;
            if (pm == null || _settingsManager == null) return;

            // Belt-and-suspenders: the tree disables this menu item for non-runnable projects,
            // but reject assignments here too (corrupt persisted settings or any programmatic
            // path that bypasses the UI gate). Eligibility is the owning language's rule —
            // Python checks entry files, the manifest languages check their loader-derived kind.
            if (!ExplorerConnector.CanBeStartup(pm))
            {
                MessageBox.Show(this,
                    "The package '" + pm.Name + "' has no runnable entry point — it is a " +
                    "library, not an app.\n\nA startup package must be runnable.",
                    "Run",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // Update the in-memory model: exactly one startup at a time (PSM-08). Then persist
            // the selection into the language's own manifest format where one exists (Python's
            // pyproject.toml, PSM-10), and keep the per-solution user setting too
            // (cross-session convenience; the only persistence for manifest-less languages).
            foreach (ProjectModel other in _currentSolution.Projects)
                other.IsStartup = ReferenceEquals(other, pm);
            ExplorerConnector.PersistStartupSelection(_currentSolution, pm);

            _settingsManager.Settings.SetStartupProject(_currentSolution.FilePath, pm.Name);
            try { _settingsManager.Save(); } catch { /* non-fatal */ }

            if (_projectTreePanel != null)
            {
                _projectTreePanel.RefreshStartupProjectStyling(pm.Name);
            }
            if (_statusLabel != null) _statusLabel.Text = "Startup package: " + pm.Name;
        }

        // "Run Arguments..." tree command: prompts for the package's Run/Debug command line and
        // persists it into its own pyproject.toml under [tool.helixstudio].run-args. A surgical
        // write, so the rest of the manifest — comments, [build-system], [project.scripts] — is
        // untouched. Clearing the box removes the key rather than writing an empty string.
        private void OnEditRunArguments(object sender, TreeOperationEventArgs e)
        {
            var pm = e?.Item as ProjectModel;
            if (pm == null) return;

            string entryFile;
            string entryName = TryFindEntryFile(pm, out entryFile) && !string.IsNullOrEmpty(entryFile)
                ? System.IO.Path.GetFileName(entryFile)
                : null;

            // Seed from the manifest so the box shows what is actually on disk, even if the file
            // was hand-edited since the solution was opened, and show the working directory the
            // arguments will be resolved against.
            string workDir = !string.IsNullOrEmpty(entryFile)
                ? ResolveWorkingDirectoryForScript(_currentSolution, entryFile)
                : pm.ProjectDirectory;
            using (var dialog = new Dialogs.RunArgumentsDialog(
                pm.Name, entryName, ResolveEffectiveRunArguments(pm), workDir))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string toml = ResolvePyprojectPath(pm);
                if (string.IsNullOrEmpty(toml) || !System.IO.File.Exists(toml))
                {
                    MessageBox.Show(this,
                        "This package has no pyproject.toml to store run arguments in.",
                        "Run Arguments", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    HelixPythonNamespace.Workspace.Python.PyProjectRunArgsWriter.SetRunArgs(toml, dialog.RunArguments);
                }
                catch (Exception ex)
                {
                    // The in-memory model is deliberately NOT updated on a failed write: a value
                    // that Run would use but that no longer matches the file on disk is worse than
                    // no change at all.
                    MessageBox.Show(this,
                        "Could not save run arguments to " + toml + ":\n\n" + ex.Message,
                        "Run Arguments", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                pm.RunArguments = string.IsNullOrWhiteSpace(dialog.RunArguments) ? null : dialog.RunArguments;
                if (_statusLabel != null)
                    _statusLabel.Text = string.IsNullOrEmpty(pm.RunArguments)
                        ? "Run arguments cleared for " + pm.Name
                        : "Run arguments for " + pm.Name + ": " + pm.RunArguments;
            }
        }

        // Persists the startup selection into each executable package's pyproject.toml (PSM-10):
        // `startup = true` for the chosen package, and the key cleared from every other executable.
        // A surgical write preserves the rest of each TOML.
        private static void PersistStartupToToml(SolutionModel solution, ProjectModel chosen)
        {
            if (solution == null || solution.Projects == null) return;
            foreach (ProjectModel p in solution.Projects)
            {
                if (!p.IsExecutable) continue;
                string toml = ResolvePyprojectPath(p);
                if (string.IsNullOrEmpty(toml) || !System.IO.File.Exists(toml)) continue;
                try { HelixPythonNamespace.Workspace.Python.PyProjectStartupWriter.SetStartup(toml, ReferenceEquals(p, chosen)); }
                catch { /* non-fatal: styling/user-setting still reflect the choice this session */ }
            }
        }

        // The package's pyproject.toml path: the project anchor when it already is one, else the
        // conventional pyproject.toml in the project directory.
        private static string ResolvePyprojectPath(ProjectModel p)
        {
            if (p == null) return null;
            string fp = p.FilePath;
            if (!string.IsNullOrEmpty(fp) &&
                fp.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase)) return fp;
            string dir = p.ProjectDirectory;
            return string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, "pyproject.toml");
        }

        // True when the package directory has a runnable entry point (__main__.py / main.py /
        // *_main.py), i.e. it is startable and thus eligible to be the startup package.
        private static bool HasMainModule(ProjectModel p)
        {
            string ignore;
            return TryFindEntryFile(p, out ignore);
        }

        // "Run Tests" on a project Solution Explorer node — the owning language's test runner,
        // scoped to just that project (VS2022 MSTest-runner parity; the Tests menu is
        // whole-solution only). Dispatch lives in the connector layer, one arm per language.
        private HelixPythonNamespace.UI.Controls.ToolTabStrip _explorerTabs;

        /// <summary>
        /// The current text of a Python file: from the open editor's BUFFER when it has one, so the
        /// designer reads what the user is looking at rather than a stale copy on disk.
        /// </summary>
        private string ReadPythonSource(string path)
        {
            HelixPythonNamespace.Editor.EditorTabPage tab = FindOpenEditorTab(path);
            if (tab != null) return tab.Editor.Text;
            return System.IO.File.ReadAllText(path);
        }

        /// <summary>
        /// Applies designer edits to a Python file. When it is open, the change goes into the
        /// editor's buffer as ONE undoable action — Ctrl+Z takes the whole designer edit back, and
        /// the user saves it with Ctrl+S like anything else they typed. Only a file nobody has open
        /// is written straight to disk.
        /// </summary>
        private void WritePythonSource(string path, string text)
        {
            HelixPythonNamespace.Editor.EditorTabPage tab = FindOpenEditorTab(path);
            if (tab == null) { System.IO.File.WriteAllText(path, text); return; }

            var editor = tab.Editor;
            int caret = editor.CurrentPosition;
            editor.DeleteRange(0, editor.TextLength);
            editor.InsertText(0, text);
            // Put the caret back roughly where it was; a designer edit should not also scroll the
            // user's editor to the top.
            try { editor.GotoPosition(Math.Min(caret, editor.TextLength)); } catch (Exception) { }
        }

        private HelixPythonNamespace.Editor.EditorTabPage FindOpenEditorTab(string path)
        {
            if (_openEditorTabs == null || string.IsNullOrEmpty(path)) return null;
            TabPage page;
            return _openEditorTabs.TryGetValue(System.IO.Path.GetFullPath(path), out page)
                ? page as HelixPythonNamespace.Editor.EditorTabPage : null;
        }

        // Every tool panel that has been shown, so a closed one can be put back from the menu.
        private readonly Dictionary<string, TabPage> _toolPanels =
            new Dictionary<string, TabPage>(StringComparer.Ordinal);

        /// <summary>
        /// Horizontal space the tool-panel tab painter takes out of the tab for breathing room.
        ///
        /// Shared with the test that checks labels do not truncate, because the whole defect was
        /// the two disagreeing: WinForms sized each tab to its text, the painter then spent some of
        /// that width on an inset, and the label lost the difference to an ellipsis.
        /// </summary>
        internal const int ToolPanelTabInset = 6;

        /// <summary>
        /// Paints the whole strip flat, AFTER the control has drawn itself.
        ///
        /// Not DrawItem: that is clipped to each tab's interior, and the raised bevel the control
        /// puts around every tab is drawn afterwards regardless. Painting over the finished frame
        /// is the only way a flat strip stays flat.
        /// </summary>
        private void OnExplorerTabsOverlay(object sender, PaintEventArgs e)
        {
            // THE STRIP THAT ASKED, not the one this painter was written for. Both dock sites use
            // this overlay now, and reading the left strip's tabs here drew the left strip's LABELS
            // onto the right one: the right-hand dock showed "Solution Explorer | Toolbox" under a
            // pane titled Properties, which reads as a second Solution Explorer and is a painter
            // describing the wrong control.
            var tabs = sender as TabControl ?? _explorerTabs;
            if (tabs == null || tabs.TabPages.Count == 0) return;
            Graphics g = e.Graphics;

            // The band the tabs live in. Aligned to the bottom, so it runs from the first tab's top
            // edge to the foot of the control.
            Rectangle first = tabs.GetTabRect(0);
            var band = new Rectangle(0, first.Top - 2, tabs.Width, tabs.Height - first.Top + 2);
            using (var strip = new SolidBrush(SystemColors.Control)) g.FillRectangle(strip, band);

            // A single hairline where the content ends and the strip begins. This is all the relief
            // the strip gets: enough to separate it from the pane, not enough to emboss it.
            using (var divider = new Pen(SystemColors.ControlDark))
                g.DrawLine(divider, band.Left, band.Top, band.Right, band.Top);

            for (int i = 0; i < tabs.TabPages.Count; i++)
            {
                Rectangle rect = tabs.GetTabRect(i);
                bool selected = i == tabs.SelectedIndex;
                TabPage page = tabs.TabPages[i];

                // The selected tab is the pane's own colour and joins it; the rest stay strip
                // colour. That, plus the accent, is the whole of the selected/unselected signal.
                var face = new Rectangle(rect.Left, band.Top + 1, rect.Width, band.Bottom - band.Top - 1);
                using (var back = new SolidBrush(selected ? SystemColors.Window : SystemColors.Control))
                    g.FillRectangle(back, face);

                if (selected)
                {
                    using (var accent = new SolidBrush(HelixPythonNamespace.UI.Controls.LunaThemes.Current.CaptionLeft))
                        g.FillRectangle(accent, face.Left, face.Top, face.Width, 2);
                }
                else
                {
                    // One hairline down the right-hand side keeps the tabs apart without giving
                    // each of them a box.
                    using (var separator = new Pen(SystemColors.ControlDark))
                        g.DrawLine(separator, face.Right - 1, face.Top + 4, face.Right - 1, face.Bottom - 4);
                }

                // Label only. Closing lives on the panel's title bar at the top, which is what makes
                // a tool pane look like one: bottom strip carries names, top bar carries the ✕ and
                // the drag handle. A ✕ per tab is how DOCUMENTS are closed.
                //
                // NoPadding because TextRenderer silently reserves a few pixels on each side on top
                // of whatever box it is given, and the label lost exactly that much to an ellipsis.
                var textRect = new Rectangle(face.Left + ToolPanelTabInset / 2, face.Top,
                                             Math.Max(0, face.Width - ToolPanelTabInset), face.Height);
                TextRenderer.DrawText(g, page.Text, tabs.Font, textRect,
                    selected ? SystemColors.WindowText : SystemColors.ControlText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        /// <summary>
        /// The header's ✕ closes the tool window the panel is SHOWING, not the whole group — the
        /// same thing it closes in Visual Studio, where the title bar names the active tab.
        /// </summary>
        private void OnToolPanelCloseRequested(object sender, EventArgs e)
        {
            if (_explorerTabs == null || _explorerTabs.SelectedTab == null) return;
            HideToolPanel(_explorerTabs.SelectedTab.Text);
        }

        /// <summary>Keeps the title bar naming whichever tool window is in front.</summary>
        private void SyncToolPanelTitle()
        {
            if (_leftDockPanel != null)
                _leftDockPanel.Title = _explorerTabs != null && _explorerTabs.SelectedTab != null
                    ? _explorerTabs.SelectedTab.Text
                    : "Solution Explorer";
            if (_rightDockPanel != null && _rightTabs != null && _rightTabs.SelectedTab != null)
                _rightDockPanel.Title = _rightTabs.SelectedTab.Text;
        }

        /// <summary>
        /// Tab height for the tool panels, taken FROM the editor sub-tabs rather than chosen.
        ///
        /// Two strips a few pixels apart on the same window read as one component, so a hardcoded
        /// height here would be a second source of truth that drifts the first time either is
        /// touched — the same reason the close box is shared geometry rather than a matching
        /// constant. Falls back only when there is no editor strip to measure yet.
        /// </summary>
        private int ToolPanelTabHeight()
        {
            if (_editorInnerTabControl != null && _editorInnerTabControl.ItemSize.Height > 0)
                return _editorInnerTabControl.ItemSize.Height;
            // No documents open yet, so there is no live strip to measure — fall back to the same
            // constant the editor strip itself uses, not to a number chosen here.
            return HelixPythonNamespace.App.Coordinators.EditorCoordinator.TabStripHeight;
        }

        /// <summary>
        /// Widens the strip to fit its widest label plus the close icon.
        ///
        /// Fixed-size tabs share one width, and a width guessed too small is what produced
        /// "Solution Exp..." and a pair of scroll arrows. The reservation is the same one the
        /// editor sub-tabs use, for the same reason: WinForms measures the text and knows nothing
        /// about an icon the owner-draw painter adds.
        /// </summary>
        private void SizeExplorerTabs()
        {
            SizeToolTabs(_explorerTabs);
            SizeToolTabs(_rightTabs);
        }

        private void SizeToolTabs(TabControl strip)
        {
            if (strip == null || strip.TabPages.Count == 0) return;
            try
            {
                // Only the HEIGHT is ours now — the width of each tab is the control's own text
                // measurement, which is exactly right once the painter stopped adding an icon.
                strip.ItemSize = new Size(0, ToolPanelTabHeight());
            }
            catch (Exception) { /* before the handle exists there is nothing to measure against */ }
        }

        /// <summary>Removes a tool panel from the dock without destroying it, so it can come back.</summary>
        internal void HideToolPanel(string title)
        {
            TabPage page = ToolPanelTab(title);
            TabControl strip = StripHolding(page);
            if (page == null || strip == null) return;
            _toolPanels[title] = page;
            strip.TabPages.Remove(page);
            SizeExplorerTabs();
            UpdateDockVisibility();
            SyncToolPanelTitle();
            SyncToolPanelMenu();
        }

        /// <summary>Puts a closed tool panel back and brings it forward.</summary>
        internal void ShowToolPanel(string title)
        {
            TabPage page;
            if (!_toolPanels.TryGetValue(title, out page) || page == null) return;
            TabControl strip = StripHolding(page) ?? ToolStrip(SideOf(title));
            if (strip == null) return;
            if (!strip.TabPages.Contains(page)) strip.TabPages.Add(page);
            strip.SelectedTab = page;
            SizeExplorerTabs();
            UpdateDockVisibility();
            SyncToolPanelTitle();
            SyncToolPanelMenu();
            // Reopening is a request to SEE the panel: a dock that is collapsed, unpinned or a few
            // pixels wide would put it back somewhere the user still cannot read it.
            if (StripHolding(page) == _explorerTabs) _explorerCoordinator.RestoreExplorer();
        }

        private readonly Dictionary<string, ToolStripMenuItem> _toolPanelMenuItems =
            new Dictionary<string, ToolStripMenuItem>(StringComparer.Ordinal);

        /// <summary>
        /// View ▸ Panels entries for the tool panels, so a closed one is not lost. Added when the
        /// group is built, because until then these panels do not exist.
        /// </summary>
        private void EnsureToolPanelMenuItems()
        {
            if (panelsMenuItem == null) return;
            AddToolPanelMenuItems(_explorerTabs);
            AddToolPanelMenuItems(_rightTabs);
            SyncToolPanelMenu();
        }

        /// <summary>
        /// One View ▸ Panels entry per tool panel, wherever it is docked.
        ///
        /// It used to walk the left strip only, so a panel docked anywhere else had no entry — and
        /// a tool window you can close and cannot reopen is gone for the rest of the session.
        /// Reported from the running IDE against the Properties pane, which had been moved out of
        /// this group and took its menu entry with it.
        /// </summary>
        private void AddToolPanelMenuItems(TabControl strip)
        {
            if (strip == null) return;
            foreach (TabPage page in strip.TabPages)
            {
                string title = page.Text;
                if (_toolPanelMenuItems.ContainsKey(title)) continue;
                _toolPanels[title] = page;
                var item = new ToolStripMenuItem(title) { CheckOnClick = false, Checked = true };
                item.Click += delegate
                {
                    TabPage known;
                    if (_toolPanels.TryGetValue(title, out known) && StripHolding(known) != null) HideToolPanel(title);
                    else ShowToolPanel(title);
                };
                _toolPanelMenuItems[title] = item;
                panelsMenuItem.DropDownItems.Add(item);
            }
        }

        /// <summary>Drops a View ▸ Panels entry whose tool window no longer exists.</summary>
        private void RemoveToolPanelMenuItem(string title)
        {
            ToolStripMenuItem item;
            if (!_toolPanelMenuItems.TryGetValue(title, out item)) return;
            _toolPanelMenuItems.Remove(title);
            if (panelsMenuItem != null) panelsMenuItem.DropDownItems.Remove(item);
        }

        private void SyncToolPanelMenu()
        {
            foreach (var entry in _toolPanelMenuItems)
            {
                TabPage page;
                entry.Value.Checked = _toolPanels.TryGetValue(entry.Key, out page) && StripHolding(page) != null;
            }
        }

        private void OnMainTabRemoved(object sender, ControlEventArgs e)
        {
            // However an add-in's tab goes away - its own close button, View > Panels, closing the
            // solution - the add-in is told, so its Toolbox and Properties go back to it. Watching
            // the tab strip catches every route; hooking only the explicit close once left a
            // designer's tool pane stranded in the dock after its tab had gone.
            var removedPage = e.Control as TabPage;
            if (IsAddInTab(removedPage)) NotifyAddInTabRemovedIfGone(removedPage);
        }

        /// <summary>
        /// Builds the tool-panel group in the left dock, with the Solution Explorer as its first
        /// tab. Idempotent — every designer that wants to dock a panel calls this first, and the
        /// group belongs to none of them.
        /// </summary>
        private void EnsureExplorerTabGroup()
        {
            if (_explorerTabs != null || _leftDockPanel == null || _projectTreePanel == null) return;

            // TABBED with the Solution Explorer rather than stacked under it: both want the full
            // height of the dock, and splitting gave each half a pane neither could use well.
            // Normal appearance, owner-drawn: FlatButtons paints RAISED buttons — the opposite of
            // flat — and its narrower metrics forced the labels to ellipsize and the strip to grow
            // scroll arrows. The strip is a plain fill with an accent edge on the selected tab,
            // which is painted below rather than asked for from the control.
            _explorerTabs = new HelixPythonNamespace.UI.Controls.ToolTabStrip
            {
                Dock = DockStyle.Fill,
                // Each tab as wide as its OWN label. Fixed gave them all the widest one's width,
                // which overflowed the dock and grew scroll arrows on a three-tab strip. Fixed was
                // right while the painter added a close icon WinForms could not measure; with the
                // icon moved to the title bar there is nothing left to reserve for, so the
                // control's own measurement is now the correct one.
                SizeMode = TabSizeMode.Normal,
                ItemSize = new Size(0, ToolPanelTabHeight()),
                // Visual Studio puts TOOL-window tabs along the bottom of the pane and DOCUMENT
                // tabs along the top. That split is most of why a tool pane reads as a tool pane
                // there: the panel's own title bar owns the top edge, and the tabs sit under the
                // content they switch between.
                Alignment = TabAlignment.Bottom,
            };
            // Overlaid rather than owner-drawn: see OnExplorerTabsOverlay.
            _explorerTabs.PaintOverlay += OnExplorerTabsOverlay;
            _explorerTabs.SelectedIndexChanged += delegate { SyncToolPanelTitle(); };
            AttachToolTabMenu(_explorerTabs);
            _leftDockPanel.CloseRequested -= OnToolPanelCloseRequested;
            _leftDockPanel.CloseRequested += OnToolPanelCloseRequested;
            MakeTitleBarDraggable(_leftDockPanel);

            var treeTab = new TabPage("Solution Explorer");
            _leftDockPanel.SetContent(_explorerTabs);
            _explorerTabs.TabPages.Add(treeTab);
            _projectTreePanel.Dock = DockStyle.Fill;
            treeTab.Controls.Add(_projectTreePanel);

            SizeExplorerTabs();
            SyncToolPanelTitle();
            EnsureToolPanelMenuItems();
        }

        // What a tool-panel tab held before its current occupant took it, so releasing gives it
        // back rather than deleting a tab someone else is still using. Both designers want a tab
        // called "Properties", and whichever closed second used to take the other's with it.
        private readonly Dictionary<string, Control> _toolPanelStash =
            new Dictionary<string, Control>(StringComparer.Ordinal);

        /// <summary>
        /// Puts <paramref name="content"/> into the group's tab named <paramref name="title"/>,
        /// creating the tab if it is new and setting aside whatever was there.
        /// </summary>
        private void ClaimToolPanel(string title, Control content)
        {
            ClaimToolPanel(title, content, ToolDockSide.Left);
        }

        /// <summary>
        /// As above, into the dock on <paramref name="side"/> — unless the panel already exists,
        /// in which case it stays where the user last put it.
        /// </summary>
        private void ClaimToolPanel(string title, Control content, ToolDockSide side)
        {
            if (content == null) return;
            EnsureExplorerTabGroup();
            if (_explorerTabs == null) return;

            TabPage page = ToolPanelTab(title);
            if (page == null)
            {
                page = new TabPage(title);
                TabControl strip = ToolStrip(SideOrDefault(title, side));
                if (strip == null) return;
                strip.TabPages.Add(page);
            }
            else if (page.Controls.Count > 0 && !ReferenceEquals(page.Controls[0], content))
            {
                _toolPanelStash[title] = page.Controls[0];
                page.Controls.Clear();
            }

            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            _toolPanels[title] = page;
            SizeExplorerTabs();
            UpdateDockVisibility();
            SyncToolPanelTitle();
            EnsureToolPanelMenuItems();
        }

        /// <summary>
        /// The side a panel is claimed on: its remembered one when it has been placed before, so a
        /// designer reopening does not drag a panel back from where the user moved it.
        /// </summary>
        private ToolDockSide SideOrDefault(string title, ToolDockSide fallback)
        {
            ToolDockSide remembered;
            if (_toolPanelSide.TryGetValue(title, out remembered)) return remembered;
            _toolPanelSide[title] = fallback;
            return fallback;
        }

        /// <summary>
        /// Takes <paramref name="content"/> back out of its tab and hands it to the caller. The tab
        /// survives if someone else's panel was set aside in it; otherwise it goes.
        /// </summary>
        private void ReleaseToolPanel(string title, Control content)
        {
            TabPage page = ToolPanelTab(title);
            if (page == null) return;
            if (content != null && page.Controls.Count > 0 && !ReferenceEquals(page.Controls[0], content))
            {
                // Someone else owns the tab now; this panel is the one set aside, so just forget it.
                Control stashed;
                if (_toolPanelStash.TryGetValue(title, out stashed) && ReferenceEquals(stashed, content))
                    _toolPanelStash.Remove(title);
                return;
            }

            page.Controls.Clear();
            Control previous;
            if (_toolPanelStash.TryGetValue(title, out previous) && previous != null && !previous.IsDisposed)
            {
                _toolPanelStash.Remove(title);
                previous.Dock = DockStyle.Fill;
                page.Controls.Add(previous);
            }
            else
            {
                _toolPanelStash.Remove(title);
                TabControl strip = StripHolding(page);
                if (strip != null) strip.TabPages.Remove(page);
                _toolPanels.Remove(title);
                RemoveToolPanelMenuItem(title);
            }
            SizeExplorerTabs();
            UpdateDockVisibility();
            SyncToolPanelTitle();
        }

        /// <summary>The tab named <paramref name="title"/>, in EITHER dock.</summary>
        private TabPage ToolPanelTab(string title)
        {
            if (_explorerTabs != null)
                foreach (TabPage page in _explorerTabs.TabPages)
                    if (page.Text == title) return page;
            if (_rightTabs != null)
                foreach (TabPage page in _rightTabs.TabPages)
                    if (page.Text == title) return page;
            return null;
        }

        private void SelectToolPanel(string title)
        {
            TabPage page = ToolPanelTab(title);
            TabControl strip = StripHolding(page);
            if (page != null && strip != null) strip.SelectedTab = page;
            SyncToolPanelTitle();
        }

        // ================= the right-hand tool dock =================
        //
        // A SECOND dock site, because one is not enough to work in. The tool panels of this shell
        // are tabs in a single dockable pane, so with only that pane a property grid and the
        // palette it is used beside can never be on screen together: selecting a layer to change
        // its parameters means hiding the Toolbox you are placing layers from. Visual Studio's
        // answer — and this one — is two sites, palette left and inspector right, with every panel
        // free to move between them.

        internal enum ToolDockSide { Left = 0, Right = 1 }

        private SplitContainer _workspaceSplitter;
        private DockablePanel _rightDockPanel;
        private HelixPythonNamespace.UI.Controls.ToolTabStrip _rightTabs;

        /// <summary>Which side each tool panel belongs to, so a hidden one comes back where it was.</summary>
        private readonly Dictionary<string, ToolDockSide> _toolPanelSide =
            new Dictionary<string, ToolDockSide>(StringComparer.Ordinal);

        private const int RightDockWidth = 320;

        /// <summary>
        /// Narrower than this and the pane is a sliver rather than a width somebody chose, so a
        /// number below it is not read back as the user's preference.
        /// </summary>
        private const int MinRightDockWidth = DockablePanel.RailWidth + 8;

        /// <summary>
        /// The width the right dock is re-applied at: the user's own once they have dragged its
        /// splitter, and <see cref="RightDockWidth"/> until then.
        ///
        /// THE BUG THIS EXISTS FOR: the width used to be re-applied from the CONSTANT on every
        /// SizeChanged, and the workspace splitter changes width whenever anything beside it does.
        /// Narrowing the Toolbox — on the far side of the window, nowhere near this dock — therefore
        /// threw away a Properties pane the user had just narrowed and put it back at 320px.
        /// Reported from the running IDE as the panel "becoming bigger again without my
        /// intervention", which is exactly what a remembered default overwriting a choice looks
        /// like. A width the user set is the answer to "how wide"; the constant is only the answer
        /// before they have given one.
        /// </summary>
        private int _savedRightDockWidth = RightDockWidth;

        /// <summary>
        /// True while THIS class is moving the splitter, so its own moves are not read back as the
        /// user's. Without it every re-apply would re-record the number it just applied, and a
        /// clamped one — applied while the window was too narrow to honour it — would become the
        /// remembered width.
        /// </summary>
        private bool _applyingRightDockWidth;

        /// <summary>
        /// Builds the right-hand dock, wrapping the editor side in a splitter the first time a
        /// panel asks to live there.
        ///
        /// Built on demand rather than up front: an empty dock is a strip of grey taking width from
        /// the document, and most sessions never open a designer at all.
        /// </summary>
        private void EnsureRightToolDock()
        {
            if (_rightTabs != null) return;
            Control editor = (Control)_editorAiSplitter ?? (Control)_mainTabControl;
            if (editor == null) return;
            Control slot = editor.Parent;
            if (slot == null) return;

            _rightTabs = new HelixPythonNamespace.UI.Controls.ToolTabStrip
            {
                Dock = DockStyle.Fill,
                SizeMode = TabSizeMode.Normal,
                ItemSize = new Size(0, ToolPanelTabHeight()),
                Alignment = TabAlignment.Bottom,
            };
            _rightTabs.PaintOverlay += OnExplorerTabsOverlay;
            _rightTabs.SelectedIndexChanged += delegate { SyncToolPanelTitle(); };
            AttachToolTabMenu(_rightTabs);

            _rightDockPanel = new DockablePanel { Title = "Properties", Dock = DockStyle.Fill,
                                                  Captions = DockablePanel.CaptionStyle.ToolWindow,
                                                  DockedOnRight = true };
            _rightDockPanel.SetContent(_rightTabs);
            MakeTitleBarDraggable(_rightDockPanel);
            _rightDockPanel.CloseRequested += delegate
            {
                if (_rightTabs != null && _rightTabs.SelectedTab != null) HideToolPanel(_rightTabs.SelectedTab.Text);
            };
            // The same three caption buttons mean the same three things on this side: auto-hide to
            // the edge, undock into a window, close. A panel whose buttons work only in one dock is
            // a panel that behaves differently depending on where the user happens to have put it.
            // Auto-hide as an OVERLAY, exactly as on the left: the pane it leaves keeps rail width
            // and the panel slides out over the document, so nothing beside it moves. It used to
            // resize this splitter instead, which moved the whole workspace on every peek.
            _rightDockPanel.PinStateChanged += delegate
            {
                if (_rightDockPanel.IsPinned)
                {
                    RightAutoHide.Leave();
                    _rightDockPanel.IsExpanded = true;
                    UpdateRightDockVisibility();
                    _statusLabel.Text = _rightDockPanel.Title + " pinned open";
                }
                else
                {
                    RightAutoHide.Enter();
                    _statusLabel.Text = _rightDockPanel.Title
                        + " auto-hides — hover the right edge to bring it back";
                }
            };
            _rightDockPanel.AutoHideRequested += delegate
            {
                if (_rightDockPanel.IsPinned) return;
                RightAutoHide.Unpeek();
            };
            _rightDockPanel.AutoShowRequested += delegate
            {
                if (!_rightDockPanel.IsPinned) RightAutoHide.Peek();
            };
            _rightDockPanel.FloatRequested += delegate { FloatRightDock(); };
            // The title bar's own dock buttons move THIS SITE's front panel, which is the gesture a
            // user reaches for first; the tab's context menu does the same per panel.
            _rightDockPanel.DockLeftRequested += delegate
            {
                if (_rightTabs != null && _rightTabs.SelectedTab != null)
                    MoveToolPanel(_rightTabs.SelectedTab.Text, ToolDockSide.Left);
            };
            _rightDockPanel.DockRequested += delegate
            {
                if (_rightTabs != null && _rightTabs.SelectedTab != null)
                    MoveToolPanel(_rightTabs.SelectedTab.Text, ToolDockSide.Left);
            };

            _workspaceSplitter = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
            slot.Controls.Remove(editor);
            _workspaceSplitter.Panel1.Controls.Add(editor);
            _workspaceSplitter.Panel2.Controls.Add(_rightDockPanel);
            slot.Controls.Add(_workspaceSplitter);
            // The width has to be re-applied as the window lays out and resizes: computed once, at
            // a moment when the splitter had no width yet, it is a number that means nothing.
            _workspaceSplitter.SizeChanged += delegate { UpdateRightDockVisibility(); };
            // Dragging this splitter IS the user choosing a width. Without reading it back, the
            // very next re-apply overwrites the choice with the default.
            _workspaceSplitter.SplitterMoved += delegate { RememberRightDockWidth(); };
            UpdateDockVisibility();
        }

        /// <summary>
        /// Keeps BOTH dock sites honest: a site with panels in it is visible and wide enough to
        /// read them in, and a site with none takes no width at all.
        ///
        /// THE BUG THIS EXISTS FOR: dragging the Solution Explorer to the right-hand dock left an
        /// empty cream panel on the left — the strip it came from still drawn, with nothing in it —
        /// and put it into a site whose width had never been computed, because the width was only
        /// set when the splitter happened to be wider than the dock already. Reported from the
        /// running IDE as "a solution was loaded, but I don't see shit", which is exactly what an
        /// empty panel beside an empty document area looks like.
        /// </summary>
        private void UpdateDockVisibility()
        {
            UpdateRightDockVisibility();
            UpdateLeftDockVisibility();
        }

        private void UpdateRightDockVisibility()
        {
            if (_workspaceSplitter == null) return;
            bool any = _rightTabs != null && _rightTabs.TabPages.Count > 0;
            try
            {
                if (_workspaceSplitter.Panel2Collapsed != !any)
                    _workspaceSplitter.Panel2Collapsed = !any;
                if (!any) return;
                // A rail's width while the panel is auto-hidden, its own width otherwise. Without
                // the first case anything that re-sizes the dock — a panel arriving, a window
                // resize — would silently undo the auto-hide and push the document back.
                bool railed = _rightAutoHide != null && _rightAutoHide.Hidden;
                SetRightDockWidth(railed ? DockablePanel.RailWidth : _savedRightDockWidth);
            }
            catch (Exception) { /* before the splitter is sized there is nothing to divide */ }
        }

        /// <summary>
        /// The right dock at the width its state calls for: a rail while it is auto-hidden and
        /// waiting at the edge, its full width while it is pinned or peeking.
        /// </summary>
        /// <summary>Gives the right-hand pane <paramref name="width"/>, clamped to what there is.</summary>
        private void SetRightDockWidth(int width)
        {
            if (_workspaceSplitter == null) return;
            bool outer = _applyingRightDockWidth;
            _applyingRightDockWidth = true;
            try
            {
                int room = _workspaceSplitter.Width - _workspaceSplitter.SplitterWidth;
                int wanted = Math.Min(width, Math.Max(0, room - _workspaceSplitter.Panel1MinSize));
                int distance = Math.Max(_workspaceSplitter.Panel1MinSize, room - wanted);
                if (distance >= 0 && distance <= room && _workspaceSplitter.SplitterDistance != distance)
                    _workspaceSplitter.SplitterDistance = distance;
            }
            catch (Exception) { /* before the splitter is sized there is nothing to divide */ }
            finally { _applyingRightDockWidth = outer; }
        }

        /// <summary>
        /// A remembered right-dock width, vetted against the window it is about to be applied to.
        /// </summary>
        ///
        /// <remarks>
        /// A width outside the sane band is treated as a BAD RECORD rather than clamped into it,
        /// exactly as <see cref="RestorableExplorerWidth"/> does. Nobody chooses a Properties pane
        /// that fills half the window, so honouring such a number as far as a cap allows would still
        /// open wrong; and a settings file written before this setting existed reads back as 0,
        /// which would otherwise restore as a pane with nothing visible in it.
        /// </remarks>
        internal int RestorableRightDockWidth(int remembered)
        {
            if (remembered < MinRightDockWidth) return RightDockWidth;

            int available = _workspaceSplitter != null
                ? _workspaceSplitter.Width - _workspaceSplitter.SplitterWidth
                : ClientSize.Width;
            if (available <= 0) return remembered;

            int cap = Math.Max(MinRightDockWidth, available / 2);
            return remembered <= cap ? remembered : RightDockWidth;
        }

        /// <summary>
        /// Reads a splitter drag back as the width the user wants the right dock at, so the next
        /// re-apply honours it instead of the default.
        ///
        /// Three moves are deliberately NOT the user's: one this class made (the guard), one made
        /// while the panel is railed or collapsed — where the pane measures the rail and not the
        /// panel — and one that left a sliver too narrow to have been aimed at. Recording any of
        /// them is how a remembered width turns into a wrong one that then outlives the session.
        /// </summary>
        private void RememberRightDockWidth()
        {
            if (_applyingRightDockWidth || _workspaceSplitter == null) return;
            if (_workspaceSplitter.Panel2Collapsed) return;
            if (_rightAutoHide != null && _rightAutoHide.Hidden) return;
            if (_rightDockPanel == null || !_rightDockPanel.IsExpanded) return;

            int width = _workspaceSplitter.Width
                        - _workspaceSplitter.SplitterDistance
                        - _workspaceSplitter.SplitterWidth;
            if (width >= MinRightDockWidth) _savedRightDockWidth = width;
        }

        /// <summary>
        /// Undocks the right-hand dock into a window of its own, and takes it back when that window
        /// is closed or the button is pressed again.
        ///
        /// The explorer's float has lived in ExplorerCoordinator since before there was a second
        /// dock; this is the same gesture for the other one, kept here because only this class
        /// knows the splitter it comes out of.
        /// </summary>
        private void FloatRightDock()
        {
            if (_rightDockPanel == null || _workspaceSplitter == null) return;
            if (_floatingRightDock != null && !_floatingRightDock.IsDisposed)
            {
                DockRightDockBack();
                return;
            }

            _floatingRightDock = new HelixPythonNamespace.UI.Controls.LunaForm
            {
                Text = _rightDockPanel.Title,
                Size = new Size(Math.Max(RightDockWidth, 320), Math.Max(420, Height - 200)),
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = false,
                Owner = this,
            };
            _floatingRightDock.FormClosing += delegate(object s2, FormClosingEventArgs e2)
            {
                if (e2.CloseReason != CloseReason.UserClosing) return;
                // ✕ on a floating tool window means GO AWAY. It used to mean "dock again", which is
                // the one thing the button does not say — and it left the user with no way to
                // dismiss a panel they had undocked. It comes back from View ▸ Panels; it goes back
                // to an edge by being dragged there.
                e2.Cancel = true;
                string front = _rightTabs != null && _rightTabs.SelectedTab != null ? _rightTabs.SelectedTab.Text : null;
                DockRightDockBack();
                if (front != null) HideToolPanel(front);
                _statusLabel.Text = (front ?? "Panel") + " closed — View ▸ Panels brings it back";
            };
            // Dragging the window to an edge re-docks it, with the same preview the docked drag has.
            _floatingRightDock.Move += delegate { PreviewFloatDrop(Cursor.Position, _rightDockPanel.Title); };
            _floatingRightDock.ResizeEnd += delegate
            {
                bool? left = FloatDropSide(Cursor.Position);
                DragDock().HideDropPreview();
                if (left == null) return;
                var moving = new List<string>();
                if (_rightTabs != null) foreach (TabPage page in _rightTabs.TabPages) moving.Add(page.Text);
                DockRightDockBack();
                if (left.Value) foreach (string title in moving) MoveToolPanel(title, ToolDockSide.Left);
            };
            _workspaceSplitter.Panel2.Controls.Remove(_rightDockPanel);
            _rightDockPanel.Dock = DockStyle.Fill;
            _floatingRightDock.Controls.Add(_rightDockPanel);
            _rightDockPanel.IsFloating = true;
            _workspaceSplitter.Panel2Collapsed = true;
            _floatingRightDock.Show(this);
            _statusLabel.Text = _rightDockPanel.Title + " undocked";
        }

        private void DockRightDockBack()
        {
            if (_floatingRightDock == null || _rightDockPanel == null || _workspaceSplitter == null) return;
            Form window = _floatingRightDock;
            _floatingRightDock = null;
            // Taken back by ADDING it to the splitter rather than removing it from the window: a
            // LunaForm keeps its children a level down inside its own host panel, so a Remove on the
            // form matches nothing and Close then disposes the panel with the window.
            _rightDockPanel.Dock = DockStyle.Fill;
            _workspaceSplitter.Panel2.Controls.Add(_rightDockPanel);
            _rightDockPanel.IsFloating = false;
            try { window.Close(); window.Dispose(); } catch (Exception) { /* already gone */ }
            UpdateDockVisibility();
            _statusLabel.Text = _rightDockPanel.Title + " docked right";
        }

        private Form _floatingRightDock;

        /// <summary>
        /// The left site disappears when its last panel leaves, and comes back — at a width it can
        /// be read at — when one arrives.
        /// </summary>
        private void UpdateLeftDockVisibility()
        {
            if (_leftDockPanel == null || _leftSplitter == null) return;
            // Notebook mode hid and collapsed this column and restores it itself when the last
            // notebook closes. A tool panel leaving underneath it -- the CG designer releasing its
            // Toolbox because notebook mode took its tab off the strip -- must not bring the
            // explorer back beside the notebook.
            if (_editorCoordinator != null && _editorCoordinator.NotebookModeActive) return;
            bool any = _explorerTabs != null && _explorerTabs.TabPages.Count > 0;
            _leftDockPanel.Visible = any;
            try
            {
                if (any) _explorerCoordinator.RestoreExplorer();
                else if (_explorerDockedRight) _leftSplitter.Panel2Collapsed = true;
                else _leftSplitter.Panel1Collapsed = true;
                if (any)
                {
                    _leftSplitter.Panel1Collapsed = false;
                    _leftSplitter.Panel2Collapsed = false;
                }
            }
            catch (Exception) { /* before the splitter is sized there is nothing to divide */ }
        }

        private TabControl ToolStrip(ToolDockSide side)
        {
            if (side == ToolDockSide.Right) { EnsureRightToolDock(); return _rightTabs; }
            EnsureExplorerTabGroup();
            return _explorerTabs;
        }

        /// <summary>Whichever strip currently holds <paramref name="page"/>, or null if it is hidden.</summary>
        private TabControl StripHolding(TabPage page)
        {
            if (page == null) return null;
            if (_explorerTabs != null && _explorerTabs.TabPages.Contains(page)) return _explorerTabs;
            if (_rightTabs != null && _rightTabs.TabPages.Contains(page)) return _rightTabs;
            return null;
        }

        /// <summary>
        /// View ▸ Reset Panel Layout: every tool window back where it started, visible and usable.
        ///
        /// Panels can be closed, collapsed, auto-hidden, floated and dragged from one dock to the
        /// other, and some of those combinations leave a site holding nothing while the panel the
        /// user wanted sits somewhere they cannot see. Rather than trying to make every combination
        /// unreachable, there is one command that ends all of them.
        /// </summary>
        internal void OnResetPanelLayout(object sender, EventArgs e)
        {
            foreach (string title in _toolPanels.Keys.ToList())
            {
                ShowToolPanel(title);
                MoveToolPanel(title, string.Equals(title, "Properties", StringComparison.Ordinal)
                    ? ToolDockSide.Right : ToolDockSide.Left);
            }
            _explorerCoordinator.RestoreExplorer();
            SelectToolPanel("Solution Explorer");
            // "Back where it started" includes the width: this is the one command that undoes every
            // combination, and a remembered width is one of the things it has to undo.
            _savedRightDockWidth = RightDockWidth;
            UpdateDockVisibility();
            _statusLabel.Text = "Panel layout reset";
        }

        /// <summary>Which dock a tool panel is on. For tests; the product reads _toolPanelSide.</summary>
        internal ToolDockSide ToolPanelSideForTests(string title) { return SideOf(title); }

        /// <summary>What a tool panel is currently showing, wherever it is docked. For tests.</summary>
        internal Control ToolPanelContentForTests(string title)
        {
            TabPage page = ToolPanelTab(title);
            return page != null && page.Controls.Count > 0 ? page.Controls[0] : null;
        }

        /// <summary>The tool panels on one side, in strip order. For tests.</summary>
        internal string[] ToolPanelsOnForTests(ToolDockSide side)
        {
            TabControl strip = side == ToolDockSide.Right ? (TabControl)_rightTabs : _explorerTabs;
            if (strip == null) return new string[0];
            var names = new List<string>();
            foreach (TabPage page in strip.TabPages) names.Add(page.Text);
            return names.ToArray();
        }

        private ToolDockSide SideOf(string title)
        {
            ToolDockSide side;
            return _toolPanelSide.TryGetValue(title, out side) ? side : ToolDockSide.Left;
        }

        /// <summary>
        /// Moves a tool panel to the other dock. Moving one to the left groups it with the
        /// Solution Explorer and the Toolbox, which is the arrangement a user who wants one wide
        /// column asks for; moving it right gives it the room to be read beside the document.
        /// </summary>
        internal void MoveToolPanel(string title, ToolDockSide side)
        {
            TabPage page;
            if (!_toolPanels.TryGetValue(title, out page) || page == null) return;
            _toolPanelSide[title] = side;

            TabControl from = StripHolding(page);
            TabControl to = ToolStrip(side);
            if (to == null || ReferenceEquals(from, to)) { UpdateDockVisibility(); return; }

            if (from != null) from.TabPages.Remove(page);
            to.TabPages.Add(page);
            to.SelectedTab = page;
            SizeExplorerTabs();
            UpdateDockVisibility();
            SyncToolPanelTitle();
            SyncToolPanelMenu();
            _statusLabel.Text = title + " docked " + (side == ToolDockSide.Left ? "left" : "right");
        }

        private ToolPanelDragDock _dragDock;

        /// <summary>
        /// The drag gesture, shared by every handle a tool window can be picked up by.
        ///
        /// Two drop zones, measured live off the splitter rather than remembered: the left third of
        /// the workspace is the explorer's dock, the right third is the inspector's, and a drop
        /// anywhere else is a drag the user thought better of.
        /// </summary>
        private ToolPanelDragDock DragDock()
        {
            if (_dragDock != null) return _dragDock;
            _dragDock = new ToolPanelDragDock(
                this,
                PanelBeingDragged,
                DropZoneAt,
                at => DropZoneIsLeft(at),
                (title, left) => MoveToolPanel(title, left ? ToolDockSide.Left : ToolDockSide.Right),
                PanelFrameFor,
                line => { if (_statusLabel != null) _statusLabel.Text = line; });
            return _dragDock;
        }

        /// <summary>
        /// The rectangle a tool panel occupies on screen, so the drag trail is the panel's own size
        /// rather than a shape invented for it.
        /// </summary>
        private Rectangle PanelFrameFor(string title)
        {
            TabPage page = ToolPanelTab(title);
            TabControl strip = StripHolding(page);
            DockablePanel host = ReferenceEquals(strip, _rightTabs) ? _rightDockPanel : _leftDockPanel;
            if (host == null || !host.IsHandleCreated || host.Width < 40) return Rectangle.Empty;
            return host.RectangleToScreen(host.ClientRectangle);
        }

        /// <summary>
        /// Everything on a tool window's title bar is a place to pick it up by — the bar, and the
        /// LABEL that fills it.
        ///
        /// The label is docked Fill, so it covers the bar it sits on and swallowed every mouse
        /// event over the title text. Attaching the gesture only to the bar therefore left the most
        /// obvious grab point in the whole shell inert: the user pressed on the name of the panel
        /// and nothing happened. Buttons are left alone — they are the bar's own commands.
        /// </summary>
        private void MakeTitleBarDraggable(DockablePanel panel)
        {
            if (panel == null || panel.TitleBar == null) return;
            DragDock().MakeDraggable(panel.TitleBar);
            foreach (Control child in panel.TitleBar.Controls)
                if (!(child is ButtonBase)) DragDock().MakeDraggable(child);
        }

        /// <summary>
        /// A floating tool window is being dragged by its own title bar: show where it would land.
        ///
        /// Windows moves the window itself, so there is no mouse gesture here to hook — only the
        /// Move it reports. The preview is the same one the docked drag uses.
        /// </summary>
        internal void PreviewFloatDrop(Point cursor, string title)
        {
            Rectangle zone = DropZoneAt(cursor);
            if (zone.Width <= 0 || zone.Height <= 0) { DragDock().HideDropPreview(); return; }
            DragDock().ShowDropPreview(zone, title,
                DropZoneIsLeft(cursor) ? "Dock left" : "Dock right", true);
        }

        /// <summary>
        /// The drag ended: dock the floating window if it was let go over a dock site.
        ///
        /// Dragging a floating panel back to an edge is how a tool window is re-docked — the
        /// caption's ✕ means "go away", not "go home", and a user who wants it back at the side
        /// puts it there.
        /// </summary>
        internal bool CommitFloatDrop(Point cursor)
        {
            DragDock().HideDropPreview();
            Rectangle zone = DropZoneAt(cursor);
            if (zone.Width <= 0 || zone.Height <= 0) return false;
            return DropZoneIsLeft(cursor);
        }

        private AutoHideDock _leftAutoHide;

        /// <summary>
        /// The left dock's auto-hide, built on first use.
        ///
        /// The pane it hands back is whichever splitter pane the explorer is docked in — the user
        /// can put it on either side, and the rail has to take its place wherever that is.
        /// </summary>
        internal AutoHideDock LeftAutoHide
        {
            get
            {
                if (_leftAutoHide == null)
                    _leftAutoHide = new AutoHideDock(
                        this, _leftDockPanel,
                        () => _explorerDockedRight ? _leftSplitter.Panel2 : _leftSplitter.Panel1,
                        () => _savedLeftSplitterDistance,
                        width => _explorerCoordinator.SetExplorerPaneWidth(width),
                        onLeft: true);
                return _leftAutoHide;
            }
        }

        private AutoHideDock _rightAutoHide;

        /// <summary>
        /// The right dock's auto-hide. The same overlay as the left one — a tool window behaves the
        /// same way whichever side the user has put it on.
        /// </summary>
        internal AutoHideDock RightAutoHide
        {
            get
            {
                if (_rightAutoHide == null && _rightDockPanel != null)
                    _rightAutoHide = new AutoHideDock(
                        this, _rightDockPanel,
                        () => _workspaceSplitter.Panel2,
                        () => _savedRightDockWidth,
                        SetRightDockWidth,
                        onLeft: false);
                return _rightAutoHide;
            }
        }

        /// <summary>The panel in front in one of the two docks, for code that acts on "this panel".</summary>
        internal string FrontToolPanelTitle(bool leftDock)
        {
            TabControl strip = leftDock ? _explorerTabs : (TabControl)_rightTabs;
            return strip != null && strip.SelectedTab != null ? strip.SelectedTab.Text : null;
        }

        /// <summary>Puts the floating-drag preview away.</summary>
        internal void HideFloatDropPreview()
        {
            DragDock().HideDropPreview();
        }

        /// <summary>Where a floating drop would land, or null when it would land nowhere.</summary>
        internal bool? FloatDropSide(Point cursor)
        {
            Rectangle zone = DropZoneAt(cursor);
            if (zone.Width <= 0 || zone.Height <= 0) return null;
            return DropZoneIsLeft(cursor);
        }

        /// <summary>The panel a drag is carrying right now, or null. For tests.</summary>
        internal string DraggingPanelForTests { get { return _dragDock == null ? null : _dragDock.Carrying; } }

        /// <summary>The panel a drag starting at this screen point is carrying.</summary>
        private string PanelBeingDragged(Point screenPoint)
        {
            string onTab = TabAt(_explorerTabs, screenPoint) ?? TabAt(_rightTabs, screenPoint);
            if (onTab != null) return onTab;
            // Started on a title bar: the panel that bar names is whichever one is in front.
            if (_leftDockPanel != null && _leftDockPanel.TitleBar != null
                && _leftDockPanel.TitleBar.RectangleToScreen(_leftDockPanel.TitleBar.ClientRectangle).Contains(screenPoint))
                return _explorerTabs != null && _explorerTabs.SelectedTab != null ? _explorerTabs.SelectedTab.Text : null;
            if (_rightDockPanel != null && _rightDockPanel.TitleBar != null
                && _rightDockPanel.TitleBar.RectangleToScreen(_rightDockPanel.TitleBar.ClientRectangle).Contains(screenPoint))
                return _rightTabs != null && _rightTabs.SelectedTab != null ? _rightTabs.SelectedTab.Text : null;
            return null;
        }

        private static string TabAt(TabControl strip, Point screenPoint)
        {
            if (strip == null || !strip.IsHandleCreated) return null;
            Point at = strip.PointToClient(screenPoint);
            for (int i = 0; i < strip.TabPages.Count; i++)
                if (strip.GetTabRect(i).Contains(at)) return strip.TabPages[i].Text;
            return null;
        }

        /// <summary>The workspace rectangle the drop zones are carved out of, in screen coordinates.</summary>
        private Rectangle WorkspaceBounds()
        {
            Control area = _leftSplitter;
            if (area == null || !area.IsHandleCreated) return Rectangle.Empty;
            return area.RectangleToScreen(area.ClientRectangle);
        }

        private bool DropZoneIsLeft(Point screenPoint)
        {
            Rectangle area = WorkspaceBounds();
            return area.Width <= 0 || screenPoint.X < area.Left + area.Width / 2;
        }

        /// <summary>
        /// Where a drop at this point would put the panel, or empty for "nowhere".
        ///
        /// A third of the width each side, so the middle of the workspace is dead space: a drag the
        /// user abandons over the document should do nothing, and a zone that covered everything
        /// would make that impossible to express.
        /// </summary>
        private Rectangle DropZoneAt(Point screenPoint)
        {
            Rectangle area = WorkspaceBounds();
            if (area.Width <= 0) return Rectangle.Empty;
            int third = Math.Max(1, area.Width / 3);
            if (screenPoint.X <= area.Left + third)
                return new Rectangle(area.Left, area.Top, Math.Min(RightDockWidth, third), area.Height);
            if (screenPoint.X >= area.Right - third)
                return new Rectangle(area.Right - Math.Min(RightDockWidth, third), area.Top,
                                     Math.Min(RightDockWidth, third), area.Height);
            return Rectangle.Empty;
        }

        /// <summary>
        /// Dock Left / Dock Right / Close on a tool tab, which is where a user right-clicks to move
        /// a panel — the title bar moves only whichever one is in front.
        /// </summary>
        private void AttachToolTabMenu(TabControl strip)
        {
            var menu = new ContextMenuStrip();
            var dockLeft = new ToolStripMenuItem("Dock Left");
            var dockRight = new ToolStripMenuItem("Dock Right");
            var close = new ToolStripMenuItem("Close");
            menu.Items.AddRange(new ToolStripItem[] { dockLeft, dockRight, new ToolStripSeparator(), close });

            string target = null;
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                target = TabUnderCursor(strip);
                if (target == null) { e.Cancel = true; return; }
                dockLeft.Enabled = SideOf(target) != ToolDockSide.Left;
                dockRight.Enabled = SideOf(target) != ToolDockSide.Right;
            };
            dockLeft.Click += delegate { if (target != null) MoveToolPanel(target, ToolDockSide.Left); };
            dockRight.Click += delegate { if (target != null) MoveToolPanel(target, ToolDockSide.Right); };
            close.Click += delegate { if (target != null) HideToolPanel(target); };
            strip.ContextMenuStrip = menu;
            DragDock().MakeDraggable(strip);
        }

        /// <summary>The tool panel whose TAB the pointer is over, or null for the empty strip area.</summary>
        private static string TabUnderCursor(TabControl strip)
        {
            if (strip == null) return null;
            Point at = strip.PointToClient(Cursor.Position);
            for (int i = 0; i < strip.TabPages.Count; i++)
                if (strip.GetTabRect(i).Contains(at)) return strip.TabPages[i].Text;
            return null;
        }

        // "Run" on an executable package node: launch THAT package, whether or not it is the
        // startup one, and show its output in the Output pane.
        private async void OnTreeRunProject(object sender, TreeOperationEventArgs e)
        {
            var project = e?.Item as ProjectModel;
            if (project == null) return;
            await _buildRunCoordinator.RunProjectAsync(project);
        }

        // "Start Debugging" on an executable package node. The same blockers as Run apply — a
        // package the IDE refuses to run must not become debuggable by a different route — so the
        // pre-flight is consulted before the session starts, and its entry file is what is debugged.
        private void OnTreeDebugProject(object sender, TreeOperationEventArgs e)
        {
            var project = e?.Item as ProjectModel;
            if (project == null || _currentSolution == null) return;

            SolutionRunPlan pre = ComputeRunPreflightFor(_currentSolution, project);
            if (!pre.CanRun)
            {
                _buildOutputTextBox.Clear();
                _buildInnerTabControl.SelectedTab = _buildOutputSubTab;
                AppendBuildOutputLine("==> Debug blocked");
                AppendBuildOutputLine(pre.BlockReason);
                if (pre.PolicyViolations != null && pre.PolicyViolations.Count > 0)
                    FinishCompile(new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>(pre.PolicyViolations));
                _buildRunCoordinator.ShowBuildResultsTab();
                _buildSummaryLabel.Text = "Debug blocked";
                return;
            }

            // The debugger streams into the same Output pane, so bring it forward first for the
            // same reason Run does: the user asked for this launch and should see it start.
            _buildOutputTextBox.Clear();
            _buildInnerTabControl.SelectedTab = _buildOutputSubTab;
            _buildSummaryLabel.Text = "Debugging " + pre.StartupName + "...";
            _buildRunCoordinator.ShowBuildResultsTab();
            _debugCoordinator.StartForScript(pre.EntryFile);
        }

        private void OnTreeRunTests(object sender, TreeOperationEventArgs e)
        {
            var project = e?.Item as ProjectModel;
            if (project == null || string.IsNullOrEmpty(project.ProjectDirectory)) return;
            ExplorerConnector.HandleRunTests(project);
        }

        // "Debug Tests" on a test-package Solution Explorer node — the scoped test run launched
        // under the language's real debugger (Python's debugpy; the only language surfacing the
        // verb today) so breakpoints in test files actually pause execution.
        private void OnTreeDebugTests(object sender, TreeOperationEventArgs e)
        {
            var project = e?.Item as ProjectModel;
            if (project == null || string.IsNullOrEmpty(project.ProjectDirectory)) return;
            ExplorerConnector.HandleDebugTests(project);
        }

        // "Build Project" tree command — the owning language's build pipeline (CMake target /
        // `python -m build` / cargo / dotnet / mvn-gradle / Pkg.build), dispatched through the
        // connector layer. async void because it's an event handler.
        private async void OnTreeBuildProject(object sender, TreeOperationEventArgs e)
        {
            var project = e.Item as ProjectModel;
            if (project == null || string.IsNullOrEmpty(project.FilePath))
            {
                return;
            }
            await ExplorerConnector.HandleBuildProjectAsync(project);
        }

        // Streams one external toolchain process into the Build Results panel: output lines +
        // parsed error/warning rows + summary/status text. The generic pipeline every
        // non-Python, non-CMake language's explorer verbs (build/test/restore) run through —
        // extracted verbatim from the historical inline `dotnet build` block.
        internal async Task<bool> RunProcessToBuildResultsAsync(string fileName, string arguments, string workingDirectory, string operationTitle)
        {
            try
            {
                _buildButton.Enabled = false;
                _buildMenuItem.Enabled = false;
                _statusLabel.Text = operationTitle + "...";

                // Clear embedded build output
                _buildOutputTextBox.Clear();
                _buildErrorsListView.Items.Clear();
                _buildSummaryLabel.Text = operationTitle + "...";
                _buildInnerTabControl.SelectedTab = _buildOutputSubTab;

                // Navigate to Build Results tab
                if (!_buildResultsUndocked)
                {
                    _mainTabControl.SelectedTab = _buildResultsTab;
                }
                else if (_floatingBuildResults != null)
                {
                    _floatingBuildResults.BringToFront();
                }

                var results = new List<BuildResultItem>();
                bool succeeded = false;

                await Task.Run(() =>
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;

                    using (var process = System.Diagnostics.Process.Start(psi))
                    {
                        if (process == null) return;

                        process.OutputDataReceived += (s, args) =>
                        {
                            if (args.Data != null)
                            {
                                AppendBuildOutputLine(args.Data);
                                HelixPythonNamespace.App.Coordinators.BuildAnalysisCoordinator.ParseBuildLine(args.Data, results);
                            }
                        };

                        process.ErrorDataReceived += (s, args) =>
                        {
                            if (args.Data != null)
                            {
                                AppendBuildOutputLine(args.Data);
                                HelixPythonNamespace.App.Coordinators.BuildAnalysisCoordinator.ParseBuildLine(args.Data, results);
                            }
                        };

                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        // 5 min: cargo/mvn cold builds routinely outlast the old 2-min dotnet cap.
                        process.WaitForExit(300000);

                        succeeded = process.ExitCode == 0;
                    }
                });

                _buildButton.Enabled = true;
                _buildMenuItem.Enabled = true;

                // Populate errors/warnings in embedded tab
                PopulateEmbeddedBuildResults(results, succeeded);

                int errorCount = 0;
                int warningCount = 0;
                foreach (var r in results)
                {
                    if (string.Equals(r.Severity, "error", StringComparison.OrdinalIgnoreCase))
                        errorCount++;
                    else if (string.Equals(r.Severity, "warning", StringComparison.OrdinalIgnoreCase))
                        warningCount++;
                }

                _statusLabel.Text = succeeded
                    ? string.Format("{0} succeeded - {1} warning(s)", operationTitle, warningCount)
                    : string.Format("{0} failed - {1} error(s), {2} warning(s)", operationTitle, errorCount, warningCount);
                return succeeded;
            }
            catch (Exception ex)
            {
                _buildButton.Enabled = true;
                _buildMenuItem.Enabled = true;
                MessageBox.Show(
                    string.Format("{0} failed: {1}", operationTitle, ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
        }

        // "Manage Packages" tree command — the owning language's package-manager dialog
        // (only Python surfaces the verb today), dispatched through the connector layer.
        private void OnTreeManagePackages(object sender, TreeOperationEventArgs e)
        {
            var project = e.Item as ProjectModel;
            if (project == null) return;
            ExplorerConnector.HandleManagePackages(project);
        }

        // "Restore Packages" tree command. Python projects run an editable pip install (`pip install
        // -e .`); .NET projects run `dotnet restore`. Output streams into the Build Results panel.
        // "Restore Packages" tree command — the owning language's dependency-restore verb
        // (pip install -e / cargo fetch / dotnet restore / mvn-gradle resolve / Pkg.instantiate),
        // dispatched through the connector layer. async void because it's an event handler.
        private async void OnTreeRestorePackages(object sender, TreeOperationEventArgs e)
        {
            var project = e.Item as ProjectModel;
            if (project == null || string.IsNullOrEmpty(project.FilePath)) return;
            await ExplorerConnector.HandleRestorePackagesAsync(project);
        }

        private void OnTreeProperties(object sender, TreeOperationEventArgs e)
        {
            var project = e.Item as ProjectModel;
            if (project == null) return;
            var actions = new HelixPythonNamespace.App.Dialogs.ProjectPropertyActions
            {
                OpenManifest = delegate
                {
                    if (!string.IsNullOrEmpty(project.FilePath)) OpenInEditor(project.FilePath, -1, true);
                },
                OpenBuildSettings = delegate { OnBuildToolSettings(this, EventArgs.Empty); },
                RestorePackages = delegate
                {
                    OnTreeRestorePackages(this, new TreeOperationEventArgs(e.Node, project));
                },
                ManagePackages = delegate
                {
                    OnTreeManagePackages(this, new TreeOperationEventArgs(e.Node, project));
                }
            };
            using (var dialog = new HelixPythonNamespace.App.Dialogs.ProjectPropertiesDialog(project, actions))
                dialog.ShowDialog(this);
        }

        // Builds a small modal single-line input dialog (Luna-themed) with a label, text box, and
        // OK/Cancel. On OK the entered text is stashed in dialog.Tag for the caller to read back.
        private Form CreateInputDialog(string title, string prompt, string defaultValue)
        {
            Form dialog = new LunaForm();
            dialog.Text = title;
            dialog.Size = new Size(400, 160);
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.MaximizeBox = false;
            dialog.MinimizeBox = false;

            Label label = new Label();
            label.Text = prompt;
            label.Location = new Point(12, 15);
            label.AutoSize = true;

            TextBox textBox = new TextBox();
            textBox.Text = defaultValue;
            textBox.Location = new Point(12, 40);
            textBox.Width = 360;
            textBox.SelectAll();

            Button okButton = new Button();
            okButton.Text = "OK";
            okButton.DialogResult = DialogResult.OK;
            okButton.Location = new Point(216, 80);
            okButton.Width = 75;

            Button cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Location = new Point(297, 80);
            cancelButton.Width = 75;

            dialog.AcceptButton = okButton;
            dialog.CancelButton = cancelButton;

            okButton.Click += delegate { dialog.Tag = textBox.Text.Trim(); };

            dialog.Controls.AddRange(new Control[] { label, textBox, okButton, cancelButton });

            return dialog;
        }

        // Entry point for theming: walks the whole control tree applying Luna (XP) colors.
        private void ApplyThemeToControls()
        {
            // Recursively apply Luna (XP) theme to all controls
            ApplyThemeRecursive(this);
        }

        // Re-apply the active Luna accent theme (after a change in Preferences) to the live window.
        // Accent colors are read live by LunaForm (caption), LunaRenderer/LunaColorTable (menus,
        // toolbars, status bar) and the canvases, so most of the app recolors on repaint; we only
        // re-assign the few controls that captured an accent color, then invalidate everything.
        private void ReapplyThemeVisuals()
        {
            if (_statusStrip != null) _statusStrip.BackColor = LunaColors.StatusBarBackground;
            ApplyThemeRecursive(this);   // refresh neutral surfaces too (harmless, keeps them in sync)
            // Keep the View ▸ Theme radio checks in sync with the active theme (menu + Preferences).
            if (_themeMenuItem != null)
                foreach (ToolStripMenuItem sib in _themeMenuItem.DropDownItems)
                    sib.Checked = sib.Tag is HelixPythonNamespace.UI.Controls.LunaThemeKind k
                        && k == HelixPythonNamespace.UI.Controls.LunaThemes.Kind;
            Invalidate(true);
            Update();
            // Repaint every other open window (floating tree, tools palette, minimap, any dialog) so
            // their captions/accents pick up the new theme too, not just the main window.
            foreach (Form f in Application.OpenForms)
            {
                if (ReferenceEquals(f, this)) continue;
                try { f.Invalidate(true); f.Update(); } catch { /* a closing form is fine to skip */ }
            }
        }

        // Per-control Luna theming, dispatched by control type. Menu/tool/status strips are skipped
        // (LunaRenderer already paints them) and custom-painted buttons are deliberately left alone
        // so they keep their own look. Recurses into every child.
        private void ApplyThemeRecursive(Control control)
        {
            if (control is TreeView)
            {
                LunaColors.ApplyTo((TreeView)control);
            }
            else if (control is ListView)
            {
                LunaColors.ApplyTo((ListView)control);
            }
            else if (control is SplitContainer)
            {
                LunaColors.ApplyTo((SplitContainer)control);
            }
            else if (control is MenuStrip || control is ToolStrip || control is StatusStrip)
            {
                // Already handled by LunaRenderer
            }
            else if (control is TextBox)
            {
                control.BackColor = LunaColors.InputBackground;
                control.ForeColor = LunaColors.TextPrimary;
            }
            else if (control is ListBox)
            {
                control.BackColor = LunaColors.PanelBackground;
                control.ForeColor = LunaColors.TextPrimary;
            }
            else if (control is ComboBox)
            {
                control.BackColor = LunaColors.InputBackground;
                control.ForeColor = LunaColors.TextPrimary;
            }
            else if (control is Label)
            {
                control.ForeColor = LunaColors.TextPrimary;
            }
            else if (control is Button)
            {
                // Don't override DockablePanel buttons
            }
            else if (control is UserControl || control is Panel)
            {
                control.BackColor = LunaColors.PanelBackground;
                control.ForeColor = LunaColors.TextPrimary;
            }

            foreach (Control child in control.Controls)
            {
                ApplyThemeRecursive(child);
            }
        }

        /// <summary>Record a notebook as opened. Called from EditorCoordinator.OpenNotebook.</summary>
        internal void RememberRecentNotebook(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _recentFilesManager.AddRecent(path);
            RefreshRecentFilesMenu();
        }

        /// <summary>
        /// Record a file the user opened that is neither a solution nor a notebook: a loose source
        /// file (single-file mode), or a molecular structure (the PDB Viewer add-in, through
        /// <c>IAddInHost.AddRecentFile</c>, wired to its panel's FileChanged).
        ///
        /// <para>
        /// The recent list has always been able to REOPEN a structure — <see cref="OpenRecentPath"/>
        /// routes one to the viewer — but nothing ever put one in it, so that branch was unreachable
        /// in practice. A user who spent a session reading structures came back to a list of other
        /// people's Python projects.
        /// </para>
        /// <para>
        /// The existence check is what keeps a failed load out. FileChanged also fires when the
        /// viewer CLOSES a structure (empty path) and after a load that failed (the path unchanged
        /// from the previous file), so recording unconditionally would put a file that never opened
        /// into the list of files to open.
        /// </para>
        /// </summary>
        internal void RememberRecentFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (!File.Exists(path)) return; }
            catch (ArgumentException) { return; }
            _recentFilesManager.AddRecent(path);
            RefreshRecentFilesMenu();
        }

        /// <summary>Move entries written by older builds into the unified Recent Solutions list.</summary>
        private void MigrateRecentNotebooks()
        {
            var legacy = _recentNotebooksManager.GetRecentFiles();
            for (int i = legacy.Count - 1; i >= 0; i--)
                if (File.Exists(legacy[i])) _recentFilesManager.AddRecent(legacy[i]);
            if (legacy.Count > 0) _recentNotebooksManager.Clear();
            RefreshRecentFilesMenu();
        }

        // ---- Recent Files ----

        // Both Recent Files surfaces share one dispatcher.  The MRU intentionally contains
        // notebooks alongside solution manifests, so sending every row to OpenSolutionAsync
        // makes Welcome-page notebook entries fail with "Expected a pyproject.toml...".
        internal void OpenRecentPath(string path)
        {
            // A design opens on the canvas that wrote it, as from the shell (OpenShellPath).
            if (OpenDesignSurfaceFor(path)) return;
            if (HelixPythonNamespace.App.Coordinators.EditorCoordinator.IsNotebookPath(path)) OpenInEditor(path);
            // A lone .py is a program, not a solution. It used to reach OpenSolutionAsync, which
            // told the user it "expected a pyproject.toml" — a true statement about a file they
            // never claimed was one. Single-file mode is what it is for; the rules that come with
            // it are SingleFileCoordinator's business, not this dispatcher's.
            else if (IsLooseSourceFile(path)) OpenSingleFile(path);
            // A molecular structure never gets here when the PDB Viewer add-in is loaded: its
            // opener (OpenDesignSurfaceFor, above) claims it by CONTENT, so File > Open, the
            // recent-files list and the Welcome page all reach the viewer through that one route.
            else OpenSolutionAsync(path);
        }

        /// <summary>
        /// True for a Python source file that is not a solution manifest — the thing single-file
        /// mode opens.
        ///
        /// <c>pyproject.toml</c> is excluded by extension and nothing else needs excluding: a
        /// notebook and a structure are already routed above, and everything else that reaches
        /// this dispatcher is a manifest or a mistake.
        /// </summary>
        internal static bool IsLooseSourceFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext;
            try { ext = Path.GetExtension(path); }
            catch (ArgumentException) { return false; }
            return string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".pyw", StringComparison.OrdinalIgnoreCase);
        }

        // Click on a recent-files menu entry: the path is carried in the item's Tag.
        internal void OnRecentFileClick(object sender, EventArgs e)
        {
            var menuItem = sender as ToolStripMenuItem;
            if (menuItem != null && menuItem.Tag is string path)
            {
                OpenRecentPath(path);
            }
        }

        // "Clear Recent" menu command — empties the MRU list and rebuilds the menu.
        internal void OnClearRecentFiles(object sender, EventArgs e)
        {
            _recentFilesManager.Clear();
            _recentNotebooksManager.Clear();
            RefreshRecentFilesMenu();
            // The Welcome page paints the same list; without this it kept showing the cleared
            // rows until the user happened to navigate back to the tab.
            if (_welcomePanel != null) _welcomePanel.RefreshRecentFiles();
        }

        // ---- File operations ----


        // Drives the "Import Python Directory" wizard: collects options from the dialog, runs
        // PythonDirectoryImporter to scaffold projects + a generated .sln/pyproject.toml, and opens
        // the result. Errors and import failures are surfaced via message boxes.
        internal void RunImportDirectoryWizard(string initialPath)
        {
            // SRS solution_explorer_rules §4: the Import Engine wizard analyzes the
            // directory read-only, lets the user rename/merge/exclude packages, pick
            // the startup project and the workspace layout, then executes the plan
            // (generation + approved physical rearrangement) itself.
            // §8.1: when the imported directory lies under the OPEN solution's root,
            // the wizard's writes are IDE-internal — keep the watcher quiet for the
            // modal's lifetime.
            using (_fileWatcher?.SuspendWhileEditing())
            using (var wiz = new ImportWizardForm(initialPath, _settingsManager))
            {
                if (wiz.ShowDialog(this) != DialogResult.OK) return;
                HelixPythonNamespace.ImportEngine.Execution.ImportExecutionResult result = wiz.Result;
                if (result == null || !result.Success) return;
                _statusLabel.Text = string.Format(
                    "Imported {0} project(s); generated {1}",
                    result.ProjectCount, result.PyProjectTomlPath ?? result.SlnPath);

                // Surface the post-import audit and offer resolutions before opening.
                // If the user declines a non-compliant project, stop.
                if (!HelixPythonNamespace.App.Services.SolutionAuditPresenter.ConfirmOrBlock(this, result.PostAudit, "Import"))
                {
                    _statusLabel.Text = "Import complete — open cancelled due to compliance issues.";
                    return;
                }
                if (!string.IsNullOrEmpty(result.PyProjectTomlPath) && System.IO.File.Exists(result.PyProjectTomlPath))
                {
                    // ConfirmOrBlock above checks a DIFFERENT, narrower rule set
                    // (PythonSolutionAuditor) than ComputeRunPreflight actually gates
                    // Run/Debug on (PackageComplianceValidator, PSM-06..24) — a project
                    // could previously finish import with no warning and only discover
                    // Run refuses it later. Load once synchronously to check the SAME
                    // validator Run uses, and warn now if it will be blocked.
                    var preflightLoad = new HelixPythonNamespace.Workspace.Services.SolutionService()
                        .LoadAsync(result.PyProjectTomlPath);
                    preflightLoad.Wait();
                    if (preflightLoad.Result.Success)
                    {
                        var runBlockers = HelixPythonNamespace.Workspace.Python.PackageComplianceValidator
                            .Validate(preflightLoad.Result.Solution);
                        HelixPythonNamespace.App.Services.SolutionAuditPresenter.WarnRunWillBeBlocked(this, runBlockers, "Import");
                    }
                    OpenSolutionAsync(result.PyProjectTomlPath);
                }
            }
        }

        // True when the path is an openable solution/project file: CMakeLists.txt, pyproject.toml, or .sln/.slnx
        // (which SolutionService loads natively or via its adjacent pyproject.toml). Kept in sync with
        // the File > Open filter so drag-drop accepts exactly what the Open dialog does.
        internal static bool IsProjectPath(string p)
        {
            // Language-services layer: each language's provider owns its manifest names
            // (CMakeLists.txt | pyproject.toml | *.sln | *.slnx today) — adding a language
            // registers a provider instead of growing an inline list here.
            return HelixPythonNamespace.Workspace.LanguageServices.LanguageWorkspaceRegistry.IsRecognizedProjectPath(p);
        }

        // True when the path is an existing directory (candidate for the import wizard).
        internal static bool IsImportableDirectory(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            return System.IO.Directory.Exists(p);
        }

        // Pushes current node positions + viewport (pan/zoom) from the primary "All" canvas back into
        // the layout store, ready to be persisted. Only the All canvas is authoritative for layout.
        internal void SyncLayoutFromCanvas()
        {
            // Only sync from the All Types canvas (primary layout source)
            if (_projectionBuilder == null)
            {
                return;
            }

            // SaveCurrentPositions handles all node types:
            // type nodes (FQN keys), container nodes ([SOL]/[PROJ]/[NS] keys)
            _projectionBuilder.SaveCurrentPositions();

            // Save viewport state (pan and zoom)
            if (_layoutStore != null)
            {
                var viewportState = _layoutStore.LayoutModel.ViewportState;
                viewportState.PanX = _canvas.Viewport.PanOffset.X;
                viewportState.PanY = _canvas.Viewport.PanOffset.Y;
                viewportState.Zoom = _canvas.Viewport.Zoom;
            }
        }

        // ---- File Watcher ----

        // (Re)starts the filesystem watcher over the solution directory. Wires change/conflict events
        // and hands it a predicate so it can tell a plain external edit from a conflict with an
        // unsaved editor buffer. No-op without a loaded solution.
        internal void StartFileWatcher()
        {
            StopFileWatcher();

            if (_currentSolution == null)
            {
                return;
            }

            var solutionDir = Path.GetDirectoryName(_currentSolution.FilePath);
            _fileWatcher = new SolutionFileWatcher(solutionDir);
            _fileWatcher.ExternalChangeDetected += OnExternalChangeDetected;
            _fileWatcher.ConflictDetected += OnConflictDetected;
            // An external change to a file open with unsaved edits is a conflict, not a plain change.
            _fileWatcher.IsFileModifiedInEditor = IsFileOpenWithUnsavedEdits;
            _fileWatcher.Start();
        }

        // Detaches events and disposes the file watcher (idempotent).
        internal void StopFileWatcher()
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.ExternalChangeDetected -= OnExternalChangeDetected;
                _fileWatcher.ConflictDetected -= OnConflictDetected;
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }
        }

        // File-watcher callback for a benign external change. Marshals back to the UI thread (the
        // watcher fires on a worker thread) and offers to reload the solution.
        private void OnExternalChangeDetected(object sender, FileChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnExternalChangeDetected(sender, e)));
                return;
            }

            // §8.1: the watcher's suppression is purely temporal (guard scope + 1 s
            // grace). A late OS notification for a write the IDE itself made can
            // still land here — but then the on-disk bytes match what the editor
            // already shows, so there is no "external change" to report.
            if (ShouldIgnoreExternalChange(e.FilePath)) return;

            // Non-interactive host (test harness / CI / headless): never raise this prompt. It fires
            // from a worker-thread notification, so it can appear at any moment — including after the
            // work that caused it has finished — and a YesNo modal then blocks the STA thread with
            // nobody to answer it. Observed: a rename test's own writes triggered this box, which
            // stalled the run and left "Reload solution?" sitting on a live desktop.
            // Answering "No" is the safe default: keep what is in memory, change nothing.
            if (string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1",
                              StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[noninteractive] External change ignored: " + e.FilePath);
                return;
            }

            var result = MessageBox.Show(
                string.Format("External file change detected:\n{0}\n\nReload solution?", e.FilePath),
                "External Change",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                ReloadSolutionAsync();
            }
        }

        // True when the changed file is open, clean, and byte-identical to the
        // editor buffer — i.e. the notification is an echo of the IDE's own write.
        internal bool ShouldIgnoreExternalChange(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || _openEditorTabs == null) return false;
            string full;
            try { full = Path.GetFullPath(filePath); } catch { return false; }
            if (!_openEditorTabs.TryGetValue(full, out TabPage tab)) return false;
            var etp = tab as HelixPythonNamespace.Editor.EditorTabPage;
            if (etp == null || etp.IsModified) return false;
            try
            {
                string onDisk = File.ReadAllText(full);
                string inEditor = etp.Editor != null ? (etp.Editor.Text ?? string.Empty) : null;
                return inEditor != null && string.Equals(onDisk, inEditor, StringComparison.Ordinal);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>
        /// True when the given file is open in the editor with unsaved changes — used by the file
        /// watcher to classify an external change as a conflict rather than a plain reload prompt.
        /// </summary>
        private bool IsFileOpenWithUnsavedEdits(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || _openEditorTabs == null) return false;
            string full;
            try { full = Path.GetFullPath(filePath); } catch { full = filePath; }
            return _openEditorTabs.TryGetValue(full, out TabPage tab)
                && tab is HelixPythonNamespace.Editor.EditorTabPage etp && etp.IsModified;
        }

        // File-watcher callback when an external change hits a file with unsaved editor edits.
        // Marshals to the UI thread and shows the conflict-resolution dialog.
        private void OnConflictDetected(object sender, FileConflictEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnConflictDetected(sender, e)));
                return;
            }

            // Same guard as OnExternalChangeDetected: this is a worker-thread notification that ends
            // in a modal, so in a non-interactive host it would block forever on a dialog no one can
            // answer. Keeping the in-memory buffer is the conservative choice — it discards nothing.
            if (string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1",
                              StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[noninteractive] File conflict ignored: " + e.FilePath + " — " + e.Message);
                return;
            }

            using (var dialog = new FileConflictDialog(e.FilePath, e.Message))
            {
                dialog.ShowDialog(this);
            }
        }

        /// <summary>
        /// Snapshot contract types (interfaces and abstract classes) and their members
        /// before a reload, so we can detect what changed afterward.
        /// </summary>
        internal Dictionary<string, List<MemberSnapshot>> SnapshotContractTypes(SolutionModel solution)
        {
            var snapshot = new Dictionary<string, List<MemberSnapshot>>();
            foreach (var type in solution.GetAllTypes())
            {
                if (type.TypeKind != TypeKind.Interface && type.TypeKind != TypeKind.AbstractClass)
                    continue;

                var members = new List<MemberSnapshot>();
                foreach (var m in type.Members)
                {
                    if (type.TypeKind == TypeKind.AbstractClass && !m.IsAbstract)
                        continue; // Only track contractual members

                    members.Add(new MemberSnapshot
                    {
                        Name = m.Name,
                        MemberKind = m.MemberKind,
                        ReturnType = m.ReturnType,
                        HasGetter = m.HasGetter,
                        HasSetter = m.HasSetter,
                        ParameterSignature = FormatParameterSignature(m)
                    });
                }
                snapshot[type.FullyQualifiedName] = members;
            }
            return snapshot;
        }

        // Renders a member's parameter list as a comparable "Type name, Type name" string, used to
        // detect signature changes between contract snapshots.
        private static string FormatParameterSignature(MemberModel m)
        {
            if (m.Parameters == null || m.Parameters.Count == 0)
                return "";
            var parts = new List<string>();
            foreach (var p in m.Parameters)
            {
                parts.Add(p.TypeName + " " + p.Name);
            }
            return string.Join(", ", parts);
        }

        /// <summary>
        /// Compare old contract type snapshots against the reloaded solution to detect
        /// member renames, additions, and signature changes. Then run compliance analysis
        /// and show the results dialog if gaps are found.
        /// </summary>
        internal void DetectAndPropagateUpstreamChanges(Dictionary<string, List<MemberSnapshot>> oldSnapshots)
        {
            if (_currentSolution == null || oldSnapshots == null || oldSnapshots.Count == 0)
                return;

            try
            {
                var graph = new HelixPythonNamespace.Core.Services.InheritanceGraph(_currentSolution);
                var impactEngine = new HelixPythonNamespace.Core.Services.ImpactAnalysisEngine(graph);
                var complianceService = new HelixPythonNamespace.Workspace.Services.ComplianceUpdateService();

                var allGaps = new List<ComplianceGap>();
                TypeModel firstChangedType = null;

                foreach (var type in _currentSolution.GetAllTypes())
                {
                    if (type.TypeKind != TypeKind.Interface && type.TypeKind != TypeKind.AbstractClass)
                        continue;

                    List<MemberSnapshot> oldMembers;
                    if (!oldSnapshots.TryGetValue(type.FullyQualifiedName, out oldMembers))
                        continue;

                    // Build current contractual members list
                    var newMembers = new List<MemberModel>();
                    foreach (var m in type.Members)
                    {
                        if (type.TypeKind == TypeKind.AbstractClass && !m.IsAbstract)
                            continue;
                        newMembers.Add(m);
                    }

                    // Detect changes by comparing old snapshot to new members
                    var changes = DetectMemberChanges(oldMembers, newMembers);

                    foreach (var change in changes)
                    {
                        ComplianceReport report = null;

                        switch (change.Kind)
                        {
                            case MemberChangeKind.Renamed:
                                report = impactEngine.AnalyzeMemberRenamed(
                                    type, change.OldName, change.NewMember.Name, change.NewMember);
                                break;

                            case MemberChangeKind.Added:
                                report = impactEngine.AnalyzeMemberAdded(type, change.NewMember);
                                break;

                            case MemberChangeKind.SignatureChanged:
                                report = impactEngine.AnalyzeSignatureChanged(
                                    type, change.OldMemberModel, change.NewMember);
                                break;
                        }

                        if (report != null && report.Gaps.Count > 0)
                        {
                            if (firstChangedType == null)
                                firstChangedType = type;
                            allGaps.AddRange(report.Gaps);
                        }
                    }
                }

                if (allGaps.Count > 0 && firstChangedType != null)
                {
                    var combinedReport = new ComplianceReport(firstChangedType);
                    combinedReport.Gaps.AddRange(allGaps);

                    using (var dialog = new Dialogs.ComplianceResultsDialog(combinedReport, () =>
                    {
                        using (_fileWatcher?.SuspendWhileEditing())
                        {
                            int fixedCount = complianceService.ApplyAutoFixes(combinedReport);
                            _statusLabel.Text = string.Format(
                                "Applied {0} auto-fix(es) for downstream compliance", fixedCount);

                        }

                        // Reload after applying fixes so the canvas reflects changes
                        ReloadAfterComplianceFix();
                    }))
                    {
                        dialog.ShowDialog(this);
                    }
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = string.Format("Change detection error: {0}", ex.Message);
            }
        }

        // Reloads the solution from disk after compliance auto-fixes were applied, preserving the
        // checked-project set and rebuilding the canvas + category tabs so fixes are visible. Best-effort.
        private void ReloadAfterComplianceFix()
        {
            if (_currentSolution == null)
                return;

            try
            {
                var slnPath = _currentSolution.FilePath;
                var fixResult = Task.Run(() => _solutionService.LoadAsync(slnPath)).Result;
                if (fixResult.Success)
                {
                    var checkedProjects = _projectTreePanel.GetCheckedProjects();
                    _currentSolution = fixResult.Solution;

                    foreach (var type in _currentSolution.GetAllTypes())
                    {
                        foreach (var baseType in type.BaseTypes)
                        {
                            baseType.Bind(_currentSolution);
                        }
                    }

                    _projectTreePanel.LoadSolution(_currentSolution);
                    _projectTreePanel.SetCheckedProjects(checkedProjects);

                    if (_projectionBuilder != null)
                    {
                        _projectionBuilder.SetVisibleProjects(checkedProjects);
                        _projectionBuilder.RebuildFromSolution(_currentSolution);
                        _canvas.Invalidate();

                        // Rebuild category tabs
                        RebuildCategoryTabs(checkedProjects);
                    }
                }
            }
            catch
            {
                // Best-effort reload after fix
            }
        }

        /// <summary>
        /// Detect member-level changes between old snapshot and new members.
        /// Identifies renames, additions, and signature changes.
        /// </summary>
        private List<MemberChange> DetectMemberChanges(
            List<MemberSnapshot> oldMembers,
            List<MemberModel> newMembers)
        {
            var changes = new List<MemberChange>();

            // Build lookup of new members by name+kind
            var newByKey = new Dictionary<string, MemberModel>();
            foreach (var m in newMembers)
            {
                newByKey[m.Name + "|" + m.MemberKind] = m;
            }

            // Find old members that are missing in the new set
            var missingOld = new List<MemberSnapshot>();
            var matchedNewKeys = new HashSet<string>();

            foreach (var old in oldMembers)
            {
                var key = old.Name + "|" + old.MemberKind;
                MemberModel matched;
                if (newByKey.TryGetValue(key, out matched))
                {
                    matchedNewKeys.Add(key);

                    // Same name exists — check for signature change
                    var newSig = FormatParameterSignature(matched);
                    if (old.ReturnType != matched.ReturnType || old.ParameterSignature != newSig)
                    {
                        // Build an OldMemberModel for signature change analysis
                        var oldModel = new MemberModel(old.Name, old.MemberKind);
                        oldModel.ReturnType = old.ReturnType;
                        changes.Add(new MemberChange
                        {
                            Kind = MemberChangeKind.SignatureChanged,
                            OldName = old.Name,
                            OldMemberModel = oldModel,
                            NewMember = matched
                        });
                    }
                }
                else
                {
                    missingOld.Add(old);
                }
            }

            // Find new members that weren't in the old set
            var addedNew = new List<MemberModel>();
            foreach (var m in newMembers)
            {
                var key = m.Name + "|" + m.MemberKind;
                if (!matchedNewKeys.Contains(key))
                {
                    addedNew.Add(m);
                }
            }

            // Try to match missing old members with added new members as renames
            // Heuristic: same MemberKind and same return type → likely a rename
            var usedOld = new HashSet<int>();
            var usedNew = new HashSet<int>();

            for (int i = 0; i < missingOld.Count; i++)
            {
                for (int j = 0; j < addedNew.Count; j++)
                {
                    if (usedNew.Contains(j))
                        continue;

                    var old = missingOld[i];
                    var added = addedNew[j];

                    if (old.MemberKind == added.MemberKind && old.ReturnType == added.ReturnType)
                    {
                        // Match: this is a rename
                        changes.Add(new MemberChange
                        {
                            Kind = MemberChangeKind.Renamed,
                            OldName = old.Name,
                            NewMember = added
                        });
                        usedOld.Add(i);
                        usedNew.Add(j);
                        break;
                    }
                }
            }

            // Remaining added members that weren't matched as renames are new additions
            for (int j = 0; j < addedNew.Count; j++)
            {
                if (!usedNew.Contains(j))
                {
                    changes.Add(new MemberChange
                    {
                        Kind = MemberChangeKind.Added,
                        NewMember = addedNew[j]
                    });
                }
            }

            return changes;
        }

        // Classification of a detected contract-member delta between two solution loads.
        private enum MemberChangeKind { Renamed, Added, SignatureChanged }

        // One detected change to a contract member (rename / add / signature change), carrying enough
        // old + new state for the impact engine to analyze downstream compliance.
        private class MemberChange
        {
            public MemberChangeKind Kind;
            public string OldName;
            public MemberModel OldMemberModel;
            public MemberModel NewMember;
        }

        // Lightweight pre-reload snapshot of a contract member, used to diff against the reloaded model.
        internal class MemberSnapshot
        {
            public string Name;
            public MemberKind MemberKind;
            public string ReturnType;
            public bool HasGetter;
            public bool HasSetter;
            public string ParameterSignature;
        }

        // ---- Edit operations ----

        // Edit ▸ Undo — pops the canvas command dispatcher's undo stack and repaints.
        internal void OnEditUndo(object sender, EventArgs e)
        {
            // On the Editor tab these act on the text document (see ActiveTextEditor).
            HelixPythonNamespace.Editor.PythonEditorControl editor = ActiveTextEditor();
            if (editor != null) { if (editor.CanUndo) editor.Undo(); return; }

            if (_canvas.CommandDispatcher.CanUndo)
            {
                _canvas.CommandDispatcher.Undo();
                _canvas.Invalidate();
            }
        }

        // Edit ▸ Redo — replays the last undone command and repaints.
        internal void OnEditRedo(object sender, EventArgs e)
        {
            HelixPythonNamespace.Editor.PythonEditorControl editor = ActiveTextEditor();
            if (editor != null) { if (editor.CanRedo) editor.Redo(); return; }

            if (_canvas.CommandDispatcher.CanRedo)
            {
                _canvas.CommandDispatcher.Redo();
                _canvas.Invalidate();
            }
        }

        // ---- Scope 11: Hide / Unhide ----

        // Tree "Hide" command — hides the selected type from all canvases.
        private void OnTreeHide(object sender, TreeOperationEventArgs e)
        {
            var type = e != null ? e.Item as TypeModel : null;
            if (type == null) return;
            ApplyHide(new[] { type.FullyQualifiedName });
            _statusLabel.Text = string.Format("Hidden: {0}", type.FullyQualifiedName);
        }

        // Tree "Unhide" command — restores a previously hidden type.
        private void OnTreeUnhide(object sender, TreeOperationEventArgs e)
        {
            var type = e != null ? e.Item as TypeModel : null;
            if (type == null) return;
            ApplyUnhide(new[] { type.FullyQualifiedName });
            _statusLabel.Text = string.Format("Unhidden: {0}", type.FullyQualifiedName);
        }


        // Ctrl+H / canvas command: hides every currently-selected node on the active canvas.
        internal void OnHideSelected(object sender, EventArgs e)
        {
            DiagramCanvas activeCanvas = GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = GetProjectionBuilderForCanvas(activeCanvas);
            if (_currentSolution == null || activeBuilder == null || activeCanvas == null) return;

            var fqns = new List<string>();
            foreach (var v in activeCanvas.SelectionManager.SelectedNodes)
            {
                string fqn = activeBuilder.FindFQN(v);
                if (!string.IsNullOrEmpty(fqn)) fqns.Add(fqn);
            }
            if (fqns.Count == 0) return;

            ApplyHide(fqns);
            activeCanvas.SelectionManager.ClearSelection();
            _statusLabel.Text = string.Format("Hidden {0} type(s). Ctrl+Shift+H to restore.", fqns.Count);
        }

        // Ctrl+Shift+H / command: restores every hidden type across all canvases.
        internal void OnShowAllHidden(object sender, EventArgs e)
        {
            if (_projectionBuilder == null) return;
            int count = _projectionBuilder.GetHiddenFqns().Count;
            if (count == 0)
            {
                _statusLabel.Text = "No hidden types.";
                return;
            }
            ApplyUnhideAll();
            _statusLabel.Text = string.Format("Restored {0} hidden type(s).", count);
        }

        /// <summary>
        /// Apply a hide action across all four canvases + refresh tree + hidden-panel UI.
        /// Centralized so context menu / Ctrl+H / tree checkbox / hidden-panel all share a path.
        /// </summary>
        private void ApplyHide(IEnumerable<string> fqns)
        {
            if (_projectionBuilder == null) return;
            _projectionBuilder.HideTypes(fqns);
            RefreshAfterHideUnhide();
        }

        // Unhide a specific set of FQNs, then refresh all canvases/tree/hidden panel.
        private void ApplyUnhide(IEnumerable<string> fqns)
        {
            if (_projectionBuilder == null) return;
            _projectionBuilder.UnhideTypes(fqns);
            RefreshAfterHideUnhide();
        }

        // Unhide everything, then refresh all canvases/tree/hidden panel.
        private void ApplyUnhideAll()
        {
            if (_projectionBuilder == null) return;
            _projectionBuilder.UnhideAll();
            RefreshAfterHideUnhide();
        }

        // Shared post-hide/unhide refresh: rebuilds the projection, repaints the canvas, and
        // refreshes the Hidden Elements panel.
        private void RefreshAfterHideUnhide()
        {
            if (_currentSolution == null) return;
            _projectionBuilder.RebuildFromSolution(_currentSolution);
            _canvas?.Invalidate();
            if (_hiddenElementsPanel != null) _hiddenElementsPanel.Refresh(_currentSolution, _projectionBuilder);
        }

        // Ctrl+F (global) / command: opens the type finder and navigates to the chosen type on the All tab.
        internal void OnFindType(object sender, EventArgs e)
        {
            if (_currentSolution == null)
            {
                _statusLabel.Text = "No solution loaded";
                return;
            }

            using (var dlg = new HelixPythonNamespace.App.Dialogs.FindTypesDialog(_currentSolution))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedType != null)
                {
                    NavigateToTypeOnAllTab(dlg.SelectedType);
                }
            }
        }

        // Thin wrapper — CanvasCoordinator owns Designer Canvas navigation (mirrors how
        // EditorCoordinator owns OpenInEditor for the editor side).
        internal void NavigateToTypeOnAllTab(TypeModel typeModel) => _canvasCoordinator.NavigateToType(typeModel);

        // ---- Type operations ----

        // Context-menu "Add <Type>" — creates a type at the last right-click world position.
        internal void AddTypeAtMouse(TypeKind kind)
        {
            DiagramCanvas activeCanvas = GetActiveCanvas();
            PointF worldPos = activeCanvas.LastRightClickWorldPosition;
            AddTypeAtPosition(kind, worldPos.X, worldPos.Y);
        }

        // Creates a new type of the given kind at world coordinates (x,y) in the first project,
        // choosing a unique default name, seeding its node layout, and adding it to projection + tree.
        // Returns the created TypeModel (or null on error / precondition failure).
        internal TypeModel AddTypeAtPosition(TypeKind kind, float x, float y)
        {
            if (_currentSolution == null)
            {
                MessageBox.Show(
                    "Please create or open a solution first.",
                    "No Solution",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return null;
            }

            if (_currentSolution.Projects.Count == 0)
            {
                MessageBox.Show(
                    "The solution has no projects. Please add a project first.",
                    "No Projects",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return null;
            }

            var project = _currentSolution.Projects[0];

            string prefix;
            switch (kind)
            {
                case TypeKind.Interface:
                    prefix = "INew";
                    break;
                case TypeKind.AbstractClass:
                    prefix = "AbstractNew";
                    break;
                default:
                    prefix = "NewClass";
                    break;
            }

            string baseName = prefix;
            string name = baseName;
            int counter = 1;
            while (_currentSolution.FindType(project.DefaultNamespace + "." + name) != null)
            {
                counter++;
                name = baseName + counter;
            }

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    TypeModel newType;
                    if (kind == TypeKind.Interface)
                    {
                        newType = _typeService.AddInterface(project, name, project.DefaultNamespace);
                    }
                    else
                    {
                        newType = _typeService.AddClass(project, name, project.DefaultNamespace, kind == TypeKind.AbstractClass);
                    }

                    var layout = new NodeLayoutData
                    {
                        PositionX = x,
                        PositionY = y,
                        Width = 200,
                        Height = 150
                    };
                    _layoutStore.SetPosition(newType.FullyQualifiedName, layout);

                    _projectionBuilder.AddType(newType);
                    _projectTreePanel.AddType(newType);
                    _canvas.Invalidate();

                    _statusLabel.Text = string.Format("Added {0}: {1}", kind, newType.Name);
                    return newType;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to create type: {0}", ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return null;
            }
        }

        /// <summary>
        /// Create a type at the current viewport center and immediately open the type editor
        /// dialog on it. Invoked from the tools palette icon buttons.
        /// </summary>
        private void CreateTypeAndOpenEditor(TypeKind kind)
        {
            DiagramCanvas activeCanvas = GetActiveCanvas() ?? _canvas;
            if (activeCanvas == null) return;

            // Viewport-center world coordinates so the new node appears where the user is looking.
            SizeF vs = activeCanvas.Viewport.ViewportSize;
            PointF screenCenter = new PointF(vs.Width / 2f, vs.Height / 2f);
            PointF worldCenter = activeCanvas.Viewport.ScreenToWorld(screenCenter);

            TypeModel newType = AddTypeAtPosition(kind, worldCenter.X - 100f, worldCenter.Y - 75f);
            if (newType == null) return;

            try
            {
                using (var dialog = new TypeModelEditorDialog(newType, _currentSolution, _typeService))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        using (_fileWatcher?.SuspendWhileEditing())
                        {
                            _projectionBuilder.UpdateType(newType);
                            _projectTreePanel.UpdateType(newType);
                            _canvas.Invalidate();
                            _statusLabel.Text = string.Format("Updated type: {0}", newType.Name);
                            RefreshDownstreamProjection(newType);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to edit type: {0}", ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        // Begins interactive relationship authoring: with one node selected as the source, puts the
        // canvas into ConnectingState so the next node click completes the implements/inherits edge.
        internal void StartRelationship(RelationshipKind kind)
        {
            DiagramCanvas activeCanvas = GetActiveCanvas();

            if (activeCanvas.SelectionManager.SelectedNodes.Count != 1)
            {
                MessageBox.Show(
                    "Select a single source type first, then use Add Relationship.",
                    "Info",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            NodeVisual sourceVisual = activeCanvas.SelectionManager.SelectedNodes[0];
            activeCanvas.InteractionContext.TransitionTo(
                new ConnectingState(activeCanvas.InteractionContext, sourceVisual, kind));
            _statusLabel.Text = string.Format("Click a target type to create {0} relationship", kind);
        }

        // "Edit Type..." — opens the full type editor on the single selected node. Snapshots the
        // source file + model before/after so a whole dialog edit becomes one undoable command
        // (EditTypeSnapshotCommand pushed without re-execute), and refreshes downstream views.
        internal void OnEditType(object sender, EventArgs e)
        {
            DiagramCanvas activeCanvas = GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = GetProjectionBuilderForCanvas(activeCanvas);

            if (activeCanvas.SelectionManager.SelectedNodes.Count != 1) return;
            if (_currentSolution == null || activeBuilder == null) return;

            NodeVisual visual = activeCanvas.SelectionManager.SelectedNodes[0];
            string fqn = activeBuilder.FindFQN(visual);
            if (string.IsNullOrEmpty(fqn)) return;

            TypeModel typeModel = _currentSolution.FindType(fqn);
            if (typeModel == null) return;

            // Snapshot the type + its source file BEFORE the dialog edits anything.
            string filePath = typeModel.SourceFilePath;
            string beforeSource = SafeReadAllText(filePath);
            string beforeModelJson = SafeSerializeTypeModel(typeModel);

            using (var dialog = new TypeModelEditorDialog(typeModel, _currentSolution, _typeService))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        using (_fileWatcher?.SuspendWhileEditing())
                        {
                            _projectionBuilder.UpdateType(typeModel);
                            _projectTreePanel.UpdateType(typeModel);
                            _canvas.Invalidate();
                            _statusLabel.Text = string.Format("Updated type: {0}", typeModel.Name);

                            // R4-R7: Refresh source viewer for affected downstream files
                            RefreshDownstreamProjection(typeModel);
                        }

                        // Capture "after" state and push a snapshot command so Ctrl+Z reverts the entire dialog edit.
                        string afterSource = SafeReadAllText(filePath);
                        string afterModelJson = SafeSerializeTypeModel(typeModel);
                        bool sourceChanged = !string.Equals(beforeSource, afterSource, StringComparison.Ordinal);
                        bool modelChanged = !string.Equals(beforeModelJson, afterModelJson, StringComparison.Ordinal);
                        if (sourceChanged || modelChanged)
                        {
                            var cmd = new HelixPythonNamespace.App.Commands.EditTypeSnapshotCommand(
                                typeModel, filePath,
                                beforeSource, afterSource,
                                beforeModelJson, afterModelJson,
                                new Action(RefreshAfterRename));
                            activeCanvas.CommandDispatcher.PushWithoutExecute(cmd);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            string.Format("Failed to update type: {0}", ex.Message),
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }
        }

        // Reads a file's text, returning "" on any error/missing path — used for before/after snapshots.
        private static string SafeReadAllText(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return string.Empty;
                return System.IO.File.ReadAllText(path);
            }
            catch { return string.Empty; }
        }

        // JSON-serializes a TypeModel (ignoring reference loops) so two model states can be compared;
        // returns null on failure. Used to detect whether a type-editor dialog actually changed the model.
        private static string SafeSerializeTypeModel(TypeModel type)
        {
            try
            {
                var settings = new Newtonsoft.Json.JsonSerializerSettings
                {
                    ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore
                };
                return Newtonsoft.Json.JsonConvert.SerializeObject(type, settings);
            }
            catch { return null; }
        }

        // ---- Link-selected relationship authoring (Scope 8) ----

        // ---- Nested Type creation (Scope 7) ----

        // "Add Nested Type" — with a single class/interface selected, prompts for a nested type and
        // asks the type service to create it (respecting codegen policy), then refreshes views.
        internal void OnAddNestedType(object sender, EventArgs e)
        {
            TypeModel parent = GetSelectedClassType();
            if (parent == null)
            {
                MessageBox.Show(this, "Select a single class, interface, or struct first.",
                    "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new HelixPythonNamespace.App.Dialogs.AddNestedTypeDialog(parent))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        TypeModel nested = _typeService.AddNestedType(parent, dlg.TypeName, dlg.SelectedKind);
                        if (nested == null)
                        {
                            _statusLabel.Text = "Add nested type cancelled (codegen policy).";
                            return;
                        }
                        RefreshAfterRename();
                        _statusLabel.Text = string.Format("Added nested type: {0}", nested.FullyQualifiedName);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        string.Format("Failed to add nested type: {0}", ex.Message),
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ---- Refactorings (Scope 5) ----

        // Resolves the single selected node on the active canvas to its TypeModel, or null when the
        // selection isn't exactly one type. Shared precondition for the refactoring commands below.
        private TypeModel GetSelectedClassType()
        {
            DiagramCanvas activeCanvas = GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = GetProjectionBuilderForCanvas(activeCanvas);
            if (_currentSolution == null || activeBuilder == null || activeCanvas == null) return null;
            if (activeCanvas.SelectionManager.SelectedNodes.Count != 1) return null;

            NodeVisual visual = activeCanvas.SelectionManager.SelectedNodes[0];
            string fqn = activeBuilder.FindFQN(visual);
            if (string.IsNullOrEmpty(fqn)) return null;
            return _currentSolution.FindType(fqn);
        }

        // Refactoring: Extract Interface — pulls selected members of the chosen class into a new
        // interface (via RefactoringService), which the class then implements.
        internal void OnExtractInterface(object sender, EventArgs e)
        {
            TypeModel source = GetSelectedClassType();
            if (source == null)
            {
                MessageBox.Show(this, "Select a single class on the canvas first.",
                    "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (source.TypeKind != TypeKind.ConcreteClass && source.TypeKind != TypeKind.AbstractClass)
            {
                MessageBox.Show(this, "Extract Interface works on classes only.",
                    "Invalid Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new HelixPythonNamespace.App.Dialogs.ExtractInterfaceDialog(source))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                using (_fileWatcher?.SuspendWhileEditing())
                {
                    var result = HelixPythonNamespace.App.Services.RefactoringService.ExtractInterface(
                        _typeService, _currentSolution, source, dlg.InterfaceName, dlg.SelectedMembers);
                    HandleRefactoringResult(result);
                }
            }
        }

        // Refactoring: Pull Up Member — moves chosen members from the selected class up into a base type.
        internal void OnPullUpMember(object sender, EventArgs e)
        {
            TypeModel source = GetSelectedClassType();
            if (source == null)
            {
                MessageBox.Show(this, "Select a single class on the canvas first.",
                    "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new HelixPythonNamespace.App.Dialogs.PullUpMemberDialog(source, _currentSolution))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                using (_fileWatcher?.SuspendWhileEditing())
                {
                    var result = HelixPythonNamespace.App.Services.RefactoringService.PullUpMembers(
                        _typeService, _currentSolution, source, dlg.SelectedBase, dlg.SelectedMembers);
                    HandleRefactoringResult(result);
                }
            }
        }

        // Refactoring: Push Down Member — moves chosen members from the selected base class down into
        // selected derived types.
        internal void OnPushDownMember(object sender, EventArgs e)
        {
            TypeModel source = GetSelectedClassType();
            if (source == null)
            {
                MessageBox.Show(this, "Select a single class on the canvas first.",
                    "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new HelixPythonNamespace.App.Dialogs.PushDownMemberDialog(source, _currentSolution))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                using (_fileWatcher?.SuspendWhileEditing())
                {
                    var result = HelixPythonNamespace.App.Services.RefactoringService.PushDownMembers(
                        _typeService, _currentSolution, source, dlg.SelectedDerivedTypes, dlg.SelectedMembers);
                    HandleRefactoringResult(result);
                }
            }
        }

        // Shared handling for a refactoring result: report failure, else refresh projection + tree.
        // Note in the status bar that refactorings are not undoable (git is the recovery path).
        private void HandleRefactoringResult(HelixPythonNamespace.App.Services.RefactoringService.RefactoringResult result)
        {
            if (result == null) return;
            if (!result.Success)
            {
                MessageBox.Show(this, result.Message ?? "Refactoring failed.", "Refactoring",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Refresh projection + tree to pick up new/removed types and members.
            RefreshAfterRename();
            _statusLabel.Text = result.Message + " (not undoable — use git to revert if needed)";
        }

        // Delete key / "Delete" command: resolves the selected nodes to types, runs a deletion-impact
        // analysis, shows a confirmation dialog listing the fallout, then removes the confirmed types
        // from every projection builder, the tree, and their source files (inside a watcher suspension).
        internal void OnDeleteSelection(object sender, EventArgs e)
        {
            // Editor tab: Del removes the selected text rather than deleting types from the model.
            HelixPythonNamespace.Editor.PythonEditorControl editor = ActiveTextEditor();
            if (editor != null)
            {
                if (!string.IsNullOrEmpty(editor.SelectedText) && !editor.ReadOnly)
                    editor.ReplaceSelection(string.Empty);
                return;
            }

            DiagramCanvas activeCanvas = GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = GetProjectionBuilderForCanvas(activeCanvas);

            if (activeCanvas.SelectionManager.IsEmpty) return;
            if (_currentSolution == null || activeBuilder == null) return;

            var selectedNodes = activeCanvas.SelectionManager.SelectedNodes.ToList();
            if (selectedNodes.Count == 0) return;

            var typesToDelete = selectedNodes
                .Select(n => activeBuilder.FindFQN(n))
                .Where(fqn => !string.IsNullOrEmpty(fqn))
                .Select(fqn => _currentSolution.FindType(fqn))
                .Where(t => t != null)
                .ToList();

            if (typesToDelete.Count == 0) return;

            var overallImpact = new DeletionImpactSummary();
            foreach (var type in typesToDelete)
            {
                var impact = _deletionAnalyzer.AnalyzeTypeDeletion(_currentSolution, type.FullyQualifiedName);
                foreach (var item in impact.Impacts)
                {
                    overallImpact.Impacts.Add(item);
                }
                foreach (var affected in impact.AffectedTypes)
                {
                    if (!overallImpact.AffectedTypes.Contains(affected))
                    {
                        overallImpact.AffectedTypes.Add(affected);
                    }
                }
            }

            string itemName = typesToDelete.Count == 1
                ? typesToDelete[0].Name
                : string.Format("{0} types", typesToDelete.Count);

            using (var dialog = new DeletionConfirmationDialog(itemName, overallImpact))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || !dialog.Confirmed)
                {
                    return;
                }
            }

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    foreach (var type in typesToDelete)
                    {
                        _typeService.RemoveType(type.Project, type);
                        if (_projectionBuilder != null) _projectionBuilder.RemoveType(type.FullyQualifiedName);
                        _projectTreePanel.RemoveType(type.FullyQualifiedName);
                    }
                }

                activeCanvas.SelectionManager.ClearSelection();
                activeCanvas.Invalidate();
                // Properties panel removed

                _statusLabel.Text = string.Format("Deleted {0} type(s)", typesToDelete.Count);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to delete: {0}", ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        // Ctrl+C on a designer tab: serializes the selected types (members, params, base refs) into a
        // ClipboardPayload and puts it on the diagram clipboard for OnEditPaste to reconstruct.
        internal void OnEditCopy(object sender, EventArgs e)
        {
            HelixPythonNamespace.Editor.PythonEditorControl editor = ActiveTextEditor();
            if (editor != null) { if (!string.IsNullOrEmpty(editor.SelectedText)) editor.Copy(); return; }

            // A notebook tab is not an EditorTabPage, so ActiveTextEditor() cannot see the editor
            // embedded in the cell being typed in — without this branch Ctrl+C on a notebook fell
            // through to the diagram canvas and copied nothing at all.
            HelixPythonNamespace.Editor.Notebook.NotebookTabPage notebook = ActiveNotebookDocumentTab();
            if (notebook != null) { notebook.CopyForEditCommand(); return; }

            if (_currentSolution == null) return;

            DiagramCanvas activeCanvas = GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = GetProjectionBuilderForCanvas(activeCanvas);
            if (activeCanvas == null || activeBuilder == null) return;

            var selectedNodes = activeCanvas.SelectionManager.SelectedNodes.ToList();
            if (selectedNodes.Count == 0) return;

            ClipboardPayload payload = new ClipboardPayload();
            payload.SourceSolution = _currentSolution.Name;

            foreach (NodeVisual node in selectedNodes)
            {
                string fqn = activeBuilder.FindFQN(node);
                if (string.IsNullOrEmpty(fqn)) continue;
                TypeModel type = _currentSolution.FindType(fqn);
                if (type == null) continue;
                payload.Types.Add(DiagramClipboard.BuildEntry(type));
            }

            if (payload.Types.Count == 0) return;

            try
            {
                DiagramClipboard.SetPayload(payload);
                _statusLabel.Text = string.Format("Copied {0} type(s) to clipboard", payload.Types.Count);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to copy to clipboard: {0}", ex.Message),
                    "Copy Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Ctrl+X: copy, then (only if the copy succeeded) delete the selection.
        internal void OnEditCut(object sender, EventArgs e)
        {
            HelixPythonNamespace.Editor.PythonEditorControl editor = ActiveTextEditor();
            if (editor != null)
            {
                if (!string.IsNullOrEmpty(editor.SelectedText) && !editor.ReadOnly) editor.Cut();
                return;
            }

            OnEditCopy(sender, e);
            if (DiagramClipboard.HasPayload())
            {
                OnDeleteSelection(sender, e);
            }
        }

        // Ctrl+V: reconstructs the clipboard's types into the resolved target project/namespace,
        // giving each a unique name, recreating members/params, then re-linking base-type relationships
        // (remapping FQNs so intra-clipboard inheritance points at the new copies), and laying them out.
        internal void OnEditPaste(object sender, EventArgs e)
        {
            HelixPythonNamespace.Editor.PythonEditorControl editor = ActiveTextEditor();
            if (editor != null) { if (editor.CanPaste && !editor.ReadOnly) editor.Paste(); return; }

            if (_currentSolution == null) return;

            ClipboardPayload payload = DiagramClipboard.GetPayload();
            if (payload == null || payload.Types == null || payload.Types.Count == 0)
            {
                _statusLabel.Text = "Nothing to paste";
                return;
            }

            ProjectModel targetProject = ResolvePasteTargetProject();
            if (targetProject == null)
            {
                MessageBox.Show(
                    "No target project available for paste.",
                    "Paste Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string targetNamespace = ResolvePasteTargetNamespace(targetProject);

            Dictionary<string, string> fqnRemap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<TypeModel> created = new List<TypeModel>();

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    foreach (ClipboardTypeEntry entry in payload.Types)
                    {
                        string desiredName = entry.Name;
                        string newName = GenerateUniqueName(targetProject, targetNamespace, desiredName);

                        TypeModel newType;
                        if (entry.TypeKind == TypeKind.Interface)
                        {
                            newType = _typeService.AddInterface(targetProject, newName, targetNamespace);
                        }
                        else
                        {
                            bool isAbstract = entry.TypeKind == TypeKind.AbstractClass;
                            newType = _typeService.AddClass(targetProject, newName, targetNamespace, isAbstract);
                        }

                        fqnRemap[entry.OriginalFqn] = newType.FullyQualifiedName;
                        created.Add(newType);

                        foreach (ClipboardMemberEntry mEntry in entry.Members)
                        {
                            MemberModel member = new MemberModel(mEntry.Name, mEntry.Kind);
                            member.ReturnType = mEntry.ReturnType;
                            member.AccessModifier = mEntry.Access;
                            member.IsAbstract = mEntry.IsAbstract;
                            member.IsVirtual = mEntry.IsVirtual;
                            member.IsStatic = mEntry.IsStatic;
                            member.IsOverride = mEntry.IsOverride;
                            member.HasGetter = mEntry.HasGetter;
                            member.HasSetter = mEntry.HasSetter;

                            foreach (ClipboardParameterEntry pEntry in mEntry.Parameters)
                            {
                                MethodParameterModel p = new MethodParameterModel(pEntry.Name, pEntry.TypeName);
                                p.IsRef = pEntry.IsRef;
                                p.IsOut = pEntry.IsOut;
                                p.IsParams = pEntry.IsParams;
                                p.HasDefaultValue = pEntry.HasDefaultValue;
                                p.DefaultValue = pEntry.DefaultValue;
                                member.Parameters.Add(p);
                            }

                            try
                            {
                                _typeService.AddMember(newType, member);
                            }
                            catch (Exception memberEx)
                            {
                                _statusLabel.Text = string.Format(
                                    "Skipped member {0}: {1}", mEntry.Name, memberEx.Message);
                            }
                        }
                    }

                    foreach (ClipboardTypeEntry entry in payload.Types)
                    {
                        string newFqn;
                        if (!fqnRemap.TryGetValue(entry.OriginalFqn, out newFqn)) continue;
                        TypeModel derived = _currentSolution.FindType(newFqn);
                        if (derived == null) continue;

                        foreach (ClipboardBaseRef baseRef in entry.BaseTypeFqns)
                        {
                            string remappedBaseFqn;
                            if (!fqnRemap.TryGetValue(baseRef.FullyQualifiedName, out remappedBaseFqn))
                            {
                                remappedBaseFqn = baseRef.FullyQualifiedName;
                            }

                            TypeModel baseType = _currentSolution.FindType(remappedBaseFqn);
                            if (baseType == null) continue;

                            try
                            {
                                if (baseRef.RelationshipKind == RelationshipKind.Implements)
                                {
                                    _typeService.AddImplementation(derived, baseType);
                                }
                                else if (baseRef.RelationshipKind == RelationshipKind.Inherits)
                                {
                                    _typeService.AddInheritance(derived, baseType);
                                }
                            }
                            catch (Exception baseEx)
                            {
                                _statusLabel.Text = string.Format(
                                    "Skipped relationship {0} -> {1}: {2}",
                                    derived.Name, baseType.Name, baseEx.Message);
                            }
                        }
                    }
                }

                foreach (TypeModel t in created)
                {
                    if (_layoutStore != null)
                    {
                        NodeLayoutData layout = new NodeLayoutData
                        {
                            PositionX = 150 + (created.IndexOf(t) % 5) * 40,
                            PositionY = 150 + (created.IndexOf(t) / 5) * 40,
                            Width = 200,
                            Height = 150
                        };
                        _layoutStore.SetPosition(t.FullyQualifiedName, layout);
                    }

                    if (_projectionBuilder != null) _projectionBuilder.AddType(t);
                    _projectTreePanel.AddType(t);
                }

                DiagramCanvas activeCanvas = GetActiveCanvas();
                if (activeCanvas != null) activeCanvas.Invalidate();

                _statusLabel.Text = string.Format("Pasted {0} type(s) into {1}",
                    created.Count, targetProject.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Paste failed: {0}", ex.Message),
                    "Paste Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Picks the project a paste lands in: the currently-navigated project if any, else the first project.
        private ProjectModel ResolvePasteTargetProject()
        {
            if (_currentSolution == null || _currentSolution.Projects.Count == 0) return null;

            if (_navigationState != null && !string.IsNullOrEmpty(_navigationState.ProjectName))
            {
                ProjectModel byName = FindProjectByName(_navigationState.ProjectName);
                if (byName != null) return byName;
            }

            return _currentSolution.Projects[0];
        }

        // Picks the namespace a paste lands in: the currently-navigated (non-global) namespace, else
        // the project's default namespace.
        private string ResolvePasteTargetNamespace(ProjectModel project)
        {
            if (_navigationState != null
                && !string.IsNullOrEmpty(_navigationState.NamespaceName)
                && _navigationState.NamespaceName != "(global)")
            {
                return _navigationState.NamespaceName;
            }
            return project.DefaultNamespace;
        }

        // Returns a name not already used in (ns), appending "_Copy"/"_CopyN" until unique.
        private string GenerateUniqueName(ProjectModel project, string ns, string desired)
        {
            if (_currentSolution == null) return desired;

            string candidate = desired;
            int counter = 1;
            while (_currentSolution.FindType(
                string.IsNullOrEmpty(ns) ? candidate : ns + "." + candidate) != null)
            {
                counter++;
                candidate = desired + "_Copy" + (counter == 2 ? string.Empty : counter.ToString());
            }
            return candidate;
        }

        // Adapts a modern TypeModel into the legacy TypeNode/MemberDefinition shape still used by some
        // export/interop paths, copying members, parameters, and generic parameters across.
        private TypeNode ConvertToLegacyNode(TypeModel typeModel)
        {
            var node = new TypeNode(typeModel.Name, typeModel.TypeKind);
            node.Namespace = typeModel.Namespace;

            foreach (var member in typeModel.Members)
            {
                var legacyMember = new MemberDefinition(member.Name, member.MemberKind);
                legacyMember.ReturnType = member.ReturnType;
                legacyMember.AccessModifier = member.AccessModifier;
                legacyMember.IsAbstract = member.IsAbstract;
                legacyMember.IsVirtual = member.IsVirtual;
                legacyMember.IsStatic = member.IsStatic;
                legacyMember.HasGetter = member.HasGetter;
                legacyMember.HasSetter = member.HasSetter;

                foreach (var param in member.Parameters)
                {
                    legacyMember.Parameters.Add(new MethodParameter(param.Name, param.TypeName));
                }

                node.Members.Add(legacyMember);
            }

            foreach (var gp in typeModel.GenericParameters)
            {
                var legacyGp = new GenericParameter(gp.Name, new System.Collections.Generic.List<string>());
                node.GenericParameters.Add(legacyGp);
            }

            return node;
        }

        // A node property changed elsewhere — just repaint the canvas.
        private void OnNodePropertyChanged(object sender, EventArgs e)
        {
            _canvas.Invalidate();
        }

        // Handles a TypeModel change event: either opens the member editor for a specific member index,
        // or persists added/removed members to source (inside a watcher suspension), refreshes the
        // projection, and runs a downstream compliance check when a contract type changed.
        private void OnTypeModelChanged(object sender, TypeModelChangedEventArgs e)
        {
            if (e == null || e.TypeModel == null)
            {
                return;
            }

            if (e.EditMemberIndex >= 0 && e.EditMemberIndex < e.TypeModel.Members.Count)
            {
                var member = e.TypeModel.Members[e.EditMemberIndex];
                using (var dialog = new MemberEditorDialog(member))
                {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        // Properties panel removed
                    }
                }
                return;
            }

            // Persist member changes to source files
            if (_typeService != null && _currentSolution != null)
            {
                try
                {
                    using (_fileWatcher?.SuspendWhileEditing())
                    {
                        if (e.RemovedMembers != null)
                        {
                            foreach (var member in e.RemovedMembers)
                            {
                                _typeService.RemoveMember(e.TypeModel, member, false);
                            }
                        }

                        if (e.AddedMembers != null)
                        {
                            foreach (var member in e.AddedMembers)
                            {
                                _typeService.AddMember(e.TypeModel, member, false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _statusLabel.Text = string.Format("Error persisting members: {0}", ex.Message);
                }
            }

            _projectionBuilder.UpdateType(e.TypeModel);
            _canvas.Invalidate();
            _statusLabel.Text = string.Format("Updated type: {0}", e.TypeModel.Name);

            // Check for downstream compliance impact if the changed type is a contract type
            CheckDownstreamCompliance(e.TypeModel, e.AddedMembers);
        }

        /// <summary>
        /// After an upstream type (interface/abstract class) changes, check if downstream types need updating.
        /// </summary>
        private void CheckDownstreamCompliance(TypeModel changedType, IEnumerable<MemberModel> addedMembers)
        {
            if (_currentSolution == null || changedType == null)
                return;

            // Only check contract types (interfaces and abstract classes)
            if (changedType.TypeKind != TypeKind.Interface && changedType.TypeKind != TypeKind.AbstractClass)
                return;

            try
            {
                var graph = new HelixPythonNamespace.Core.Services.InheritanceGraph(_currentSolution);
                var impactEngine = new HelixPythonNamespace.Core.Services.ImpactAnalysisEngine(graph);
                var complianceService = new HelixPythonNamespace.Workspace.Services.ComplianceUpdateService();

                ComplianceReport report;

                if (addedMembers != null)
                {
                    // Analyze specific member additions
                    report = new ComplianceReport(changedType);
                    foreach (var member in addedMembers)
                    {
                        var memberReport = impactEngine.AnalyzeMemberAdded(changedType, member);
                        report.Gaps.AddRange(memberReport.Gaps);
                    }
                }
                else
                {
                    // Full compliance check
                    report = impactEngine.AnalyzeCompliance(changedType);
                }

                if (report.Gaps.Count > 0)
                {
                    using (var dialog = new Dialogs.ComplianceResultsDialog(report, () =>
                    {
                        using (_fileWatcher?.SuspendWhileEditing())
                        {
                            int fixed_count = complianceService.ApplyAutoFixes(report);
                            _statusLabel.Text = string.Format("Applied {0} auto-fix(es) for downstream compliance", fixed_count);
                        }

                        // Refresh canvas
                        if (_projectionBuilder != null)
                        {
                            _projectionBuilder.RebuildFromSolution(_currentSolution);
                            _canvas.Invalidate();
                        }
                    }))
                    {
                        dialog.ShowDialog(this);
                    }
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = string.Format("Compliance check error: {0}", ex.Message);
            }
        }

        // Callback from an AddRelationshipCommand executed on the canvas: writes the implements/inherits
        // edge into the source type's .py (via the type service), then runs compliance auto-fix on the
        // target and refreshes the source node so newly-generated stub members appear.
        internal void PersistRelationship(AddRelationshipCommand cmd)
        {
            string sourceFqn = cmd.SourceNodeId;
            string targetFqn = cmd.TargetNodeId;

            if (string.IsNullOrEmpty(sourceFqn) || string.IsNullOrEmpty(targetFqn))
            {
                _statusLabel.Text = "Error: Could not determine source/target for relationship";
                return;
            }

            TypeModel sourceType = _currentSolution.FindType(sourceFqn);
            TypeModel targetType = _currentSolution.FindType(targetFqn);

            if (sourceType == null || targetType == null)
            {
                _statusLabel.Text = string.Format("Error: Type not found - source:{0} target:{1}",
                    sourceType != null ? "OK" : "NULL",
                    targetType != null ? "OK" : "NULL");
                return;
            }

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    var kind = cmd.Edge.Style.Kind;
                    if (kind == RelationshipKind.Implements)
                    {
                        _typeService.AddImplementation(sourceType, targetType);
                        _statusLabel.Text = string.Format("{0} now implements {1}", sourceType.Name, targetType.Name);
                    }
                    else if (kind == RelationshipKind.Inherits)
                    {
                        _typeService.AddInheritance(sourceType, targetType);
                        _statusLabel.Text = string.Format("{0} now inherits {1}", sourceType.Name, targetType.Name);
                    }
                    else
                    {
                        _statusLabel.Text = string.Format("Relationship kind {0} not persisted to source", kind);
                    }
                }

                // R4-R7: Run compliance checks and auto-fix downstream types
                try
                {
                    var graph = new InheritanceGraph(_currentSolution);
                    var engine = new ImpactAnalysisEngine(graph);
                    var report = engine.AnalyzeCompliance(targetType);
                    if (report != null && report.Gaps != null && report.Gaps.Count > 0)
                    {
                        var complianceService = new ComplianceUpdateService();
                        int fixCount = complianceService.ApplyAutoFixes(report);
                        if (fixCount > 0)
                        {
                            _statusLabel.Text = string.Format("{0} downstream type(s) auto-updated", fixCount);
                        }
                    }
                }
                catch
                {
                    // Don't block relationship creation for compliance issues
                }

                // Refresh the source node visual to show new stub members
                if (_projectionBuilder != null)
                {
                    _projectionBuilder.UpdateType(sourceType);
                    _canvas.Invalidate();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to persist relationship: {0}", ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        // Callback from a DeleteSelectionCommand: removes the inheritance/implementation relationships
        // for every deleted edge (both explicitly-deleted edges and edges orphaned by deleted nodes)
        // from the underlying source files.
        internal void PersistEdgeDeletions(DeleteSelectionCommand cmd)
        {
            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    // Combine explicitly deleted edges and edges connected to deleted nodes
                    var allDeletedEdges = new List<EdgeVisual>();
                    allDeletedEdges.AddRange(cmd.DeletedEdges);
                    allDeletedEdges.AddRange(cmd.ConnectedEdges);

                    foreach (var edge in allDeletedEdges)
                    {
                        string sourceFqn = edge.SourceNodeId;
                        string targetFqn = edge.TargetNodeId;

                        TypeModel sourceType = _currentSolution.FindType(sourceFqn);
                        TypeModel targetType = _currentSolution.FindType(targetFqn);

                        if (sourceType == null || targetType == null)
                        {
                            continue;
                        }

                        _typeService.RemoveInheritance(sourceType, targetType);
                    }
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = string.Format("Error persisting edge deletion: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Sanitize saved project names by stripping stale language suffixes like " [C#]" or " [C++]".
        /// Returns only names that match actual projects in the solution.
        /// </summary>
        internal static List<string> SanitizeProjectNames(List<string> savedNames, SolutionModel solution)
        {
            if (savedNames == null || savedNames.Count == 0 || solution == null)
            {
                return new List<string>();
            }

            var validNames = new HashSet<string>();
            foreach (var project in solution.Projects)
            {
                validNames.Add(project.Name);
            }

            var result = new List<string>();
            foreach (var name in savedNames)
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // If it's already a valid project name, use it directly
                if (validNames.Contains(name))
                {
                    result.Add(name);
                    continue;
                }

                // Strip known language suffixes for backward compatibility
                var stripped = name;
                if (stripped.EndsWith(" [C#]"))
                {
                    stripped = stripped.Substring(0, stripped.Length - 5);
                }
                else if (stripped.EndsWith(" [C++]"))
                {
                    stripped = stripped.Substring(0, stripped.Length - 6);
                }

                if (validNames.Contains(stripped))
                {
                    result.Add(stripped);
                }
            }

            return result;
        }

        // Defensive cleanup run after load: strips duplicate base-type references from each type's
        // in-memory model (they can arise from malformed/legacy source), keeping the first of each FQN.
        internal void RepairDuplicateBaseTypes(SolutionModel solution)
        {
            if (solution == null)
            {
                return;
            }

            try
            {
                foreach (var type in solution.GetAllTypes())
                {
                    var seen = new HashSet<string>();
                    bool hasDuplicates = false;

                    foreach (var bt in type.BaseTypes)
                    {
                        if (!seen.Add(bt.FullyQualifiedName))
                        {
                            hasDuplicates = true;
                            break;
                        }
                    }

                    if (!hasDuplicates)
                    {
                        continue;
                    }

                    // Collect unique base type names and relationship kinds
                    seen.Clear();
                    var uniqueBaseTypes = new List<TypeReference>();
                    var toRemove = new List<TypeReference>();

                    foreach (var bt in type.BaseTypes)
                    {
                        if (seen.Add(bt.FullyQualifiedName))
                        {
                            uniqueBaseTypes.Add(bt);
                        }
                        else
                        {
                            toRemove.Add(bt);
                        }
                    }

                    // Remove duplicates from in-memory model
                    foreach (var dup in toRemove)
                    {
                        type.BaseTypes.Remove(dup);
                    }

                    // Rewriting base-type lists in-place is handled during the next
                    // TypeService operation; no Roslyn-specific fixup runs here for Python.
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = string.Format("Warning: Error repairing base types: {0}", ex.Message);
            }
        }

        // Single-click on a type in the Solution Explorer: selects the corresponding node on the All
        // canvas (no navigation/zoom — that's the double-click behavior below).
        private void OnProjectTreeTypeSelected(object sender, TypeSelectedEventArgs e)
        {
            if (e == null || e.TypeModel == null || _projectionBuilder == null)
            {
                return;
            }

            NodeVisual visual = _projectionBuilder.FindVisual(e.TypeModel.FullyQualifiedName);
            if (visual != null)
            {
                _canvas.SelectionManager.ClearSelection();
                _canvas.SelectionManager.Select(visual);
                _canvas.Invalidate();
            }

            // Properties panel removed
        }

        // Double-click on a type (class/interface/ABC/struct/enum/...) in the Solution Explorer:
        // open its source file in the Editor tab and jump to the declaration line, with focus —
        // the same behavior as double-clicking a function or member below it. Designer Canvas
        // navigation is a context-menu-only action ("Open in Class Designer Canvas" / "Go to
        // Designer" — see OnTreeOpenInDesigner / GoToDesigner), never an implicit double-click
        // side effect.
        private void OnProjectTreeTypeDoubleClicked(object sender, TypeSelectedEventArgs e)
        {
            if (e == null || e.TypeModel == null) return;
            if (string.IsNullOrEmpty(e.TypeModel.SourceFilePath)) return;
            try { OpenInEditor(e.TypeModel.SourceFilePath, e.TypeModel.SourceLineNumber); }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Double-click on a top-level function node (e.g. <c>main</c>) in the
        /// left tree → open the source file in the Editor tab and jump to the
        /// declaration line. Unlike the type handler, there's no Designer
        /// Canvas representation for a free function, so we switch focus to
        /// the Editor via the default <c>activate: true</c>.
        /// </summary>
        private void OnProjectTreeFunctionDoubleClicked(object sender, FunctionSelectedEventArgs e)
        {
            if (e == null || e.Function == null) return;
            if (string.IsNullOrEmpty(e.Function.SourceFilePath)) return;
            try { OpenInEditor(e.Function.SourceFilePath, e.Function.SourceLineNumber); }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Refresh downstream diagram projections after a type edit.
        /// </summary>
        private void RefreshDownstreamProjection(TypeModel typeModel)
        {
            try
            {
                var graph = new InheritanceGraph(_currentSolution);
                var descendants = graph.GetAllDescendants(typeModel);
                foreach (var desc in descendants)
                {
                    // Also update projection for downstream types that may have changed
                    if (_projectionBuilder != null)
                    {
                        _projectionBuilder.UpdateType(desc);
                    }
                }

                if (descendants.Count > 0)
                {
                    _canvas.Invalidate();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("RefreshDownstreamProjection error: " + ex.Message);
            }
        }

        // A project checkbox in the tree toggled: recomputes the visible-project set, rebuilds the All
        // canvas + category tabs to match, and persists the choice per-solution.
        internal void OnProjectVisibilityChanged(object sender, ProjectVisibilityChangedEventArgs e)
        {
            if (_currentSolution == null || _projectionBuilder == null || _layoutStore == null)
            {
                return;
            }

            var checkedProjects = _projectTreePanel.GetCheckedProjects();
            _projectionBuilder.SetVisibleProjects(checkedProjects);
            _projectionBuilder.RebuildFromSolution(_currentSolution);
            _canvas.Invalidate();

            // Rebuild category tabs
            RebuildCategoryTabs(checkedProjects);

            if (_settingsManager != null)
            {
                _settingsManager.Settings.SetVisibleProjects(_currentSolution.FilePath, checkedProjects);
                _settingsManager.Save();
            }

            _statusLabel.Text = string.Format("Showing {0} project(s)", checkedProjects.Count);
        }

        // ---- Layout operations ----

        // Multiplies the current zoom by `factor` about the viewport center, repaints, updates the label.
        internal void ApplyZoom(float factor)
        {
            if (_canvas == null) return;
            var vp = _canvas.Viewport;
            PointF center = new PointF(vp.ViewportSize.Width / 2f, vp.ViewportSize.Height / 2f);
            vp.SetZoom(vp.Zoom * factor, center);
            _canvas.Invalidate();
            UpdateZoomLabel();
        }

        // Jump to an absolute zoom level (from the toolbar zoom-preset dropdown), centered on the
        // viewport. The viewport clamps to its min/max, so out-of-range presets are handled safely.
        internal enum ZoomKey { None, In, Out, Reset }

        // Map a chord to a canvas-zoom action: Ctrl++ (=/numpad+) in, Ctrl+- out, Ctrl+0 reset to 100%.
        internal static ZoomKey ClassifyZoomShortcut(Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Oemplus) || keyData == (Keys.Control | Keys.Add)) return ZoomKey.In;
            if (keyData == (Keys.Control | Keys.OemMinus) || keyData == (Keys.Control | Keys.Subtract)) return ZoomKey.Out;
            if (keyData == (Keys.Control | Keys.D0) || keyData == (Keys.Control | Keys.NumPad0)) return ZoomKey.Reset;
            return ZoomKey.None;
        }

        internal void SetZoomPercent(int percent)
        {
            if (_canvas == null || percent <= 0) return;
            var vp = _canvas.Viewport;
            PointF center = new PointF(vp.ViewportSize.Width / 2f, vp.ViewportSize.Height / 2f);
            vp.SetZoom(percent / 100f, center);
            _canvas.Invalidate();
            UpdateZoomLabel();
        }

        // Refreshes the status-bar zoom-percentage label from the current viewport zoom.
        internal void UpdateZoomLabel()
        {
            if (_zoomLabel == null || _canvas == null) return;
            int pct = (int)Math.Round(_canvas.Viewport.Zoom * 100f);
            _zoomLabel.Text = pct + " %";
        }

        // Toggles global snap-to-grid, syncing the menu checkmark and persisting the preference.
        internal void OnToggleSnapToGrid(object sender, EventArgs e)
        {
            bool newState = !HelixPythonNamespace.UI.Rendering.GridSnapper.Enabled;
            HelixPythonNamespace.UI.Rendering.GridSnapper.Enabled = newState;
            if (_snapToGridMenuItem != null) _snapToGridMenuItem.Checked = newState;
            if (_settingsManager != null)
            {
                _settingsManager.Settings.SnapToGrid = newState;
                _settingsManager.Save();
            }
            _statusLabel.Text = newState ? "Snap to grid: on" : "Snap to grid: off";
        }

        // Help ▸ About — shows the modal About dialog.
        internal void OnShowAbout(object sender, EventArgs e)
        {
            using (var dlg = new HelixPythonNamespace.App.Dialogs.AboutDialog())
            {
                dlg.ShowDialog(this);
            }
        }

        /// <summary>
        /// Toggle welcome-panel visibility based on solution state + user preference.
        /// Call this after any open/close/new solution, and once on startup.
        /// </summary>
        internal void OnShowPreferences(object sender, EventArgs e)
        {
            ShowPreferences(null);
        }

        /// <summary>Preferences, opened on the tab whose text is <paramref name="tab"/> (null: the first).</summary>
        internal void ShowPreferences(string tab)
        {
            if (_settingsManager == null) return;
            using (var dlg = CreatePreferencesDialog(tab, _buildToolDetector))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SettingsChanged)
                {
                    // Reflect snap state on the menu toggle.
                    if (_snapToGridMenuItem != null)
                    {
                        _snapToGridMenuItem.Checked = _settingsManager.Settings.SnapToGrid;
                    }
                    // Force all canvases to repaint with the new grid spacing.
                    if (_canvas != null) _canvas.Invalidate();
                    // Re-apply the (possibly changed) editor font to every open document.
                    ApplyEditorFontToOpenTabs();
                    // Re-apply the (possibly changed) Luna accent theme to the live window.
                    ReapplyThemeVisuals();
                    _statusLabel.Text = "Preferences updated";
                }
            }
        }

        private TabPage _devEnvTab;
        private HelixPythonNamespace.App.Controls.DevelopmentEnvironmentPanel _devEnvPanel;

        /// <summary>
        /// Tools ▸ Development Env: the programming-language kits installed on this machine.
        /// Machine state rather than solution state, so it is available with nothing open — the
        /// question "do I even have the toolchain for this?" comes up before a solution exists,
        /// and the tab is registered NOT solution-scoped so closing a solution cannot take it away.
        ///
        /// A workbench tab rather than a modal dialog (changed): the inventory is
        /// something you consult while working, and a dialog has to be dismissed before you can
        /// look at the code that raised the question.
        /// </summary>
        internal void OnShowDevelopmentEnvironment(object sender, EventArgs e)
        {
            HelixPythonNamespace.App.Controls.DevelopmentEnvironmentPanel panel = EnsureDevelopmentEnvironmentTab();
            ShowTab("devEnv");

            // THE only automatic scan, and it is not really automatic: choosing Tools ▸ Development
            // Env IS the user asking what this machine has, so the first such ask answers itself.
            // Every other route into this tab — restoring it at startup, the solution gate, the
            // Panels menu — leaves the machine alone until the refresh button is pressed.
            //
            // Reported: a restored tab probed eighty toolchains during boot, while the
            // user was still looking at the Welcome page and had asked for nothing.
            if (panel != null && !panel.HasScanned && !IsNonInteractiveHost()) panel.StartScan();
        }

        /// <summary>
        /// A test harness, CI or headless run, where nobody is looking. Scanning launches one child
        /// process per catalog entry, so an unattended host must never be made to do it.
        /// </summary>
        private static bool IsNonInteractiveHost()
        {
            return string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1",
                                 StringComparison.Ordinal);
        }

        /// <summary>
        /// Create and register the Development Environment tab if it does not exist yet, without
        /// showing it. Lazy because the panel is only worth its scan once someone asks for it, and
        /// registered separately from <see cref="ShowTab"/> so a restored session can put the tab
        /// back before <c>ApplyTabVisibilityFromSettings</c> replays the user's preference — an
        /// unregistered key is silently skipped there, which is how a lazily-built tab loses its
        /// persistence.
        /// </summary>
        internal HelixPythonNamespace.App.Controls.DevelopmentEnvironmentPanel EnsureDevelopmentEnvironmentTab()
        {
            if (_devEnvPanel != null && !_devEnvPanel.IsDisposed) return _devEnvPanel;

            // Building the tab probes nothing. This method is also the STARTUP restore path, and a
            // panel that scanned on construction — or on becoming visible, which is what it used to
            // do — would turn "the user had this tab open last session" into eighty child processes
            // during boot. Only OnShowDevelopmentEnvironment and the refresh button start a scan.
            _devEnvPanel = new HelixPythonNamespace.App.Controls.DevelopmentEnvironmentPanel();
            // Probing every kit on the machine takes seconds, so it runs as a JOB — the same status
            // text, marquee progress bar and Cancel button the user gets for Code Review, Compile
            // and the rest. Without it the IDE looks frozen for the length of the scan.
            // The panel has no status line of its own — the shell's is right there under it, and
            // two copies of the same sentence is one too many. This is also what RETIRES the text:
            // JobRunner.EndJob leaves the status bar alone unless it is handed a final message, so
            // the summary the panel reports on completion is what replaces "Probing installed
            // language kits…" when the scan ends.
            _devEnvPanel.ReportStatus = text => _statusLabel.Text = text;
            _devEnvPanel.RunScan = (work, onFinished, onCancelled) =>
                _jobRunner.Run("Probing installed language kits", work, onFinished, null, onCancelled);
            _devEnvTab = new TabPage("Development Env")
            {
                ToolTipText = "The programming-language kits installed on this machine — interpreters, "
                            + "compilers, SDKs and build tools, with their versions and paths.",
            };
            _devEnvTab.Controls.Add(_devEnvPanel);
            // false, false: the machine's toolchains belong to the machine, not to the open
            // solution, so this is neither solution-scoped (removed when nothing is open) nor
            // result-scoped (wiped when the solution that produced it closes).
            _panelCoordinator.RegisterTopLevelTab("devEnv", _devEnvTab, false, false);
            return _devEnvPanel;
        }

        /// <summary>
        /// Open a file the user double-clicked in the Solution Explorer, in whatever surface can
        /// actually show it. A file type an add-in registered opens there, as from File ▸ Open
        /// (spec 004 §4.3): a molecular structure in the PDB Viewer (a .pdb in a text editor is a
        /// wall of fixed-column records, or MSVC debug symbols rendered as mojibake), a
        /// <c>.cgmc.json</c> on the CG canvas. Everything else goes to the editor.
        /// </summary>
        internal void OpenDocument(string path)
        {
            // A .canvas is a design, not text: it opens in its designer (Task 10i).
            if (HelixPythonNamespace.Core.IO.CanvasFile.IsCanvasPath(path)) OpenCanvas(path);
            else if (!OpenWithAddIn(path)) OpenInEditor(path);
        }

        // ---- The explorer steps aside for an add-in tab (IAddInHost.SetExplorerCollapsed) -------

        private bool _explorerSteppedAside;
        private bool _explorerPaneWasCollapsedBeforeAside;
        private int _explorerDistanceBeforeAside;

        /// <summary>
        /// The Solution Explorer steps aside (true) or comes back (false) for an add-in tab that
        /// wants the window while it is in front: the PDB Viewer's structure mode, which calls this
        /// from its tab's Activated and Deactivated callbacks.
        ///
        /// <para>
        /// This is a MODE, not a preference. It never writes user settings, and it restores the
        /// explorer to the state it found: hidden stays hidden, a floating tree stays floating, and
        /// the column comes back at its own width rather than the default. Idempotent.
        /// </para>
        /// </summary>
        internal void SetExplorerSteppedAside(bool aside)
        {
            if (aside == _explorerSteppedAside) return;
            _explorerSteppedAside = aside;

            try
            {
                if (aside) StepExplorerAside();
                else BringExplorerBack();
            }
            catch (Exception)
            {
                // A layout failure must never block switching documents: the same contract
                // ApplyNotebookMode holds. Worst case the explorer keeps its current width.
            }
        }

        private void StepExplorerAside()
        {
            // Floating: the explorer is in its own window, not in the LHS column, so there is
            // nothing here to step aside and collapsing a pane would only take space off the
            // viewer's neighbour. Leave the splitter alone.
            if (_floatingExplorerForm != null || _treeFloating || _leftSplitter == null) return;

            // The COLLAPSE flag is what is remembered, not _leftDockPanel.Visible. Control.Visible
            // is EFFECTIVE visibility — it reads false for every child of a form that is not itself
            // on screen — so capturing it would record "the user had the explorer hidden" for any
            // host that never shows the window, and then refuse to bring the tree back. The
            // splitter flag is the actual mechanism (it is what gives the viewer the width) and it
            // means the same thing whoever is hosting the form.
            _explorerDistanceBeforeAside = _leftSplitter.SplitterDistance;
            _explorerPaneWasCollapsedBeforeAside = ExplorerPaneCollapsed;
            // Hiding the panel is not enough: it lives in a SplitContainer pane, and a hidden child
            // still holds its ~200 px — a blank column beside a viewer that is meant to have the
            // width. Collapsing the pane is what actually hands the space over.
            ExplorerPaneCollapsed = true;
            if (_leftDockPanel != null) _leftDockPanel.Visible = false;
        }

        private void BringExplorerBack()
        {
            if (_floatingExplorerForm != null || _treeFloating || _leftSplitter == null) return;

            ExplorerPaneCollapsed = _explorerPaneWasCollapsedBeforeAside;
            // Driven from the same remembered flag, so the panel and its pane can never disagree:
            // an explorer the user had already collapsed stays collapsed and hidden, and one that
            // was open comes back open.
            if (_leftDockPanel != null)
                _leftDockPanel.Visible = !_explorerPaneWasCollapsedBeforeAside;

            if (!_explorerPaneWasCollapsedBeforeAside && _explorerDistanceBeforeAside > 0)
            {
                try { _leftSplitter.SplitterDistance = _explorerDistanceBeforeAside; }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
            }
        }

        /// <summary>
        /// Collapse state of whichever splitter pane currently holds the explorer.
        ///
        /// <para>
        /// View &#8250; Dock Explorer Right swaps the two panes' contents
        /// (<c>ExplorerCoordinator.SwapSplitterPanels</c>), so the explorer is in Panel1 only while
        /// it is docked left. Reading <c>Panel1Collapsed</c> unconditionally is right half the time
        /// and collapses the EDITOR the other half.
        /// </para>
        /// </summary>
        private bool ExplorerPaneCollapsed
        {
            get
            {
                return _explorerDockedRight ? _leftSplitter.Panel2Collapsed : _leftSplitter.Panel1Collapsed;
            }
            set
            {
                if (_explorerDockedRight) _leftSplitter.Panel2Collapsed = value;
                else _leftSplitter.Panel1Collapsed = value;
            }
        }

        // Ctrl+MouseWheel in an editor document changed the font size. The size is a global
        // preference, so it is mirrored into every other open document and persisted for the next
        // session. Writing user-settings.json on every wheel notch would mean a dozen disk writes
        // per gesture, so the flush is debounced; the in-memory setting updates immediately, which
        // is what newly-opened tabs and the Preferences dialog read.
        internal void OnEditorFontSizeZoomed(float size)
        {
            if (_settingsManager == null || _settingsManager.Settings == null) return;
            if (Math.Abs(_settingsManager.Settings.EditorFontSize - size) < 0.001f) return;
            _settingsManager.Settings.EditorFontSize = size;
            ApplyEditorFontToOpenTabs();
            if (_statusLabel != null) _statusLabel.Text = "Editor font size: " + ((int)Math.Round(size));
            ScheduleEditorFontSave();
        }

        private Timer _editorFontSaveTimer;

        private void ScheduleEditorFontSave()
        {
            if (_editorFontSaveTimer == null)
            {
                _editorFontSaveTimer = new Timer { Interval = 800 };
                _editorFontSaveTimer.Tick += (s, e) =>
                {
                    _editorFontSaveTimer.Stop();
                    try { _settingsManager?.Save(); } catch { /* a locked settings file must not break editing */ }
                };
            }
            _editorFontSaveTimer.Stop();
            _editorFontSaveTimer.Start();
        }

        // Push the persisted editor font (name + size) into every open editor document, so a change
        // in Preferences takes effect immediately without reopening files.
        private void ApplyEditorFontToOpenTabs()
        {
            if (_editorInnerTabControl == null || _settingsManager == null) return;
            foreach (TabPage p in _editorInnerTabControl.TabPages)
            {
                if (p is HelixPythonNamespace.Editor.EditorTabPage etp)
                {
                    try { etp.Editor.ApplyFontSettings(_settingsManager.Settings.EditorFontName, _settingsManager.Settings.EditorFontSize); }
                    catch { /* a missing font shouldn't break the dialog */ }
                }
            }
        }

        // File ▸ Export ▸ PNG — renders the active canvas's scene graph to a 2x bitmap and saves it.
        internal void OnExportPng(object sender, EventArgs e)
        {
            DiagramCanvas canvas = GetActiveCanvas() ?? _canvas;
            if (canvas == null || canvas.SceneGraph == null) return;

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG Image (*.png)|*.png";
                dlg.Title = "Export Diagram as PNG";
                dlg.FileName = (_currentSolution != null ? _currentSolution.Name : "diagram") + ".png";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    using (Bitmap bmp = HelixPythonNamespace.UI.Rendering.CanvasImageRenderer.Render(
                        canvas.SceneGraph, 2f, Color.White))
                    {
                        bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    _statusLabel.Text = string.Format("Exported PNG: {0}", dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to export PNG: {0}", ex.Message),
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // File ▸ Export ▸ SVG — renders the active canvas's scene graph to vector SVG text and saves it.
        internal void OnExportSvg(object sender, EventArgs e)
        {
            DiagramCanvas canvas = GetActiveCanvas() ?? _canvas;
            if (canvas == null || canvas.SceneGraph == null) return;

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "SVG Vector Image (*.svg)|*.svg";
                dlg.Title = "Export Diagram as SVG";
                dlg.FileName = (_currentSolution != null ? _currentSolution.Name : "diagram") + ".svg";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string svg = HelixPythonNamespace.UI.Rendering.CanvasSvgRenderer.Render(
                        canvas.SceneGraph, Color.White);
                    File.WriteAllText(dlg.FileName, svg);
                    _statusLabel.Text = string.Format("Exported SVG: {0}", dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to export SVG: {0}", ex.Message),
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // File ▸ Export ▸ PDF — exports the active canvas's scene graph to a PDF via CanvasPdfExporter.
        internal void OnExportPdf(object sender, EventArgs e)
        {
            DiagramCanvas canvas = GetActiveCanvas() ?? _canvas;
            if (canvas == null || canvas.SceneGraph == null) return;

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "PDF Document (*.pdf)|*.pdf";
                dlg.Title = "Export Diagram as PDF";
                dlg.FileName = (_currentSolution != null ? _currentSolution.Name : "diagram") + ".pdf";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    HelixPythonNamespace.App.Services.CanvasPdfExporter.Export(canvas.SceneGraph, dlg.FileName);
                    _statusLabel.Text = string.Format("Exported PDF: {0}", dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        string.Format("Failed to export PDF: {0}", ex.Message),
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// Refresh Canvas — re-pack all four designer tabs in-memory without touching
        /// .csooptool-layout.json on disk. Users hit File → Save Layout to commit.
        /// </summary>
        // ---- Run Startup App (Ctrl+F5) ----

        /// <summary>
        /// Resolve which project the Run button should invoke:
        /// 1. the explicit selection persisted in UserSettings, if set;
        /// 2. else a single project that exposes a <c>__main__.py</c> (auto-pick);
        /// 3. else null (caller prompts the user).
        /// </summary>
        internal string ResolveStartupProject(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.ResolveStartupProject(sol);
        }

        /// <summary>
        /// What this shell contributes to the shared run planner: the one preference that relaxes
        /// rule 2, the PY8004 verdict this session reached, and the wording of the two sentences
        /// that name a piece of UI. Everything else about a launch is decided by
        /// <see cref="HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner"/>, which the headless
        /// <c>helixstudio</c> launcher calls too — so the IDE and the command line cannot disagree
        /// about whether a solution may run.
        /// </summary>
        internal HelixPythonNamespace.Workspace.Execution.SolutionRunOptions BuildRunOptions()
        {
            return new HelixPythonNamespace.Workspace.Execution.SolutionRunOptions
            {
                // Tools ▸ Preferences ▸ General can relax rule 2 (PY8002) for solutions of existing
                // procedural code. Rules 1, 3 and 4 are not user-configurable.
                EnforceObjectOrientation = _settingsManager == null || _settingsManager.Settings == null
                    || !_settingsManager.Settings.AllowNonObjectOrientedCode,
                StructureBlockReason = StructureBlockReason(),
                MessageStyle = new HelixPythonNamespace.Workspace.Execution.PolicyMessageStyle
                {
                    QuickFixCaption = HelixPythonNamespace.App.Controls.ProblemsPanel.QuickFixCaption,
                    FullListHint = "See the Problems panel for the full list.",
                },
            };
        }

        /// <summary>
        /// The run pre-flight for ONE package, rather than for whichever package is the startup.
        ///
        /// Launching a package from its own context menu skips the "which one is the startup?"
        /// question — the user pointed at it — but it must NOT skip the blockers: package
        /// compliance and the PY8001/PY8002 solution policy are properties of the whole solution,
        /// and code the IDE refuses to run from the toolbar must not become runnable just because
        /// it was reached from a different menu.
        /// </summary>
        internal SolutionRunPlan ComputeRunPreflightFor(SolutionModel sol, ProjectModel project)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner
                .ComputeRunPlanFor(sol, project, BuildRunOptions());
        }

        /// <summary>
        /// The solution-wide reasons a run cannot start, or null when there are none. Shared so a
        /// per-package launch and the startup launch cannot drift apart on what they enforce.
        /// </summary>
        /// <summary>
        /// Non-null when the open solution's directory structure does not match the Solution
        /// Explorer and the mismatch was not corrected — the user declined the restructure, the
        /// repair failed, or the defects are ones the IDE cannot act on yet (PY8004).
        ///
        /// It is what makes Cancel mean something. Without it the prompt would be decorative:
        /// declining the repair would leave the IDE drawing, compiling and running a structure it
        /// has explicitly determined it cannot vouch for. Set by
        /// <c>SolutionCoordinator.EnsureStructureParityAsync</c> on every load; cleared there too,
        /// so it can never outlive the solution that caused it.
        /// </summary>
        internal string StructureUnverifiedReason { get; set; }

        /// <summary>
        /// What the user should actually DO about it — set alongside
        /// <see cref="StructureUnverifiedReason"/> by the coordinator, which is the only place that
        /// knows which of the several unverified states this is.
        ///
        /// It exists because the block message used to end with a single hardcoded sentence:
        /// "reopen the solution and accept the restructure when prompted". That is true in exactly
        /// one of the states — the user declined the prompt. It is a dead end in every other: no
        /// prompt is raised for a plan with no disk work, none is raised when the planner has
        /// refused, and none is raised when the scan itself could not finish. A blocked user
        /// following that instruction reopens the solution, is prompted for nothing, and is told
        /// again to reopen the solution and accept the prompt.
        /// </summary>
        internal string StructureUnverifiedRemedy { get; set; }

        /// <summary>
        /// The PY8004 half of the Run/Debug/Compile gate, shared by both preflight paths.
        ///
        /// Checked FIRST, before the other three rules, for the reason the parity contract gives:
        /// PY8001 asks whether a file is inside a member, which is a path-containment question, and
        /// containment is exactly what a structure mismatch puts in doubt. Reporting a membership
        /// error computed from a layout the IDE has already flagged as untrustworthy would send the
        /// user to fix the wrong thing.
        /// </summary>
        private string StructureBlockReason()
        {
            return HelixPythonNamespace.Workspace.Structure.StructureParityGate.ComposeBlockReason(
                StructureUnverifiedReason, StructureUnverifiedRemedy);
        }

        internal SolutionRunPlan ComputeRunPreflight(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner
                .ComputeRunPlan(sol, BuildRunOptions());
        }

        /// <summary>
        /// Resolve a package's runnable entry-point FILE — see
        /// <see cref="HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.TryFindEntryFile"/>, which
        /// is the single dispatch point shared by Run, Debug, Terminal and the headless
        /// launcher.
        /// </summary>
        internal static bool TryFindEntryFile(ProjectModel p, out string entryPath)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.TryFindEntryFile(p, out entryPath);
        }

        /// <summary>
        /// The directory a launch of <paramref name="filePath"/> should run in: the folder of the
        /// PROJECT that owns the file, which is Visual Studio's default working directory.
        ///
        /// Every launch route used to answer this differently — Run and Run in Terminal used the
        /// SOLUTION root (so a relative argument meant something else entirely), while Debug and
        /// the Profiler used the launched file's own folder (which is not the project folder when
        /// the entry point is nested, e.g. <c>&lt;project&gt;/pkg/__main__.py</c>). Relative paths
        /// in run arguments therefore resolved against three different roots depending on how you
        /// started the program. This is the single answer for all of them.
        ///
        /// The DEEPEST containing project wins, so a workspace member beats the workspace-root
        /// project that nominally contains it. Falls back to the file's own directory when no
        /// project claims it.
        /// </summary>
        internal string ResolveWorkingDirectoryForScript(SolutionModel sol, string filePath)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner
                .ResolveWorkingDirectoryForScript(sol, filePath);
        }

        /// <summary>
        /// The package's run arguments as they stand ON DISK right now, refreshing the in-memory
        /// model from the manifest.
        ///
        /// The manifest is the source of truth, and the user can change it two ways the loaded
        /// <see cref="SolutionModel"/> never hears about: editing <c>pyproject.toml</c> in the
        /// IDE's own editor, or editing it outside the IDE. Reading the cached value at launch
        /// meant a <c>run-args</c> line added after the solution was opened was simply ignored, and
        /// Run kept launching with no arguments while the file plainly said otherwise. One small
        /// file read per launch removes that entire class of staleness. Falls back to the cached
        /// value only when the manifest cannot be read.
        /// </summary>
        internal static string ResolveEffectiveRunArguments(ProjectModel project)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.ResolveEffectiveRunArguments(project);
        }

        /// <summary>
        /// The <c>run-args</c> that belong to <paramref name="scriptPath"/> — the arguments of the
        /// package whose ENTRY FILE this script is. Debug launches whatever file is active, which
        /// may be any package's <c>main.py</c> or an ordinary module, so the arguments are matched
        /// to the program actually being launched rather than always taken from the startup
        /// package. Returns null for a script that is nobody's entry point.
        /// </summary>
        internal string ResolveRunArgumentsForScript(SolutionModel sol, string scriptPath)
        {
            if (sol == null || sol.Projects == null || string.IsNullOrEmpty(scriptPath)) return null;
            string full;
            try { full = System.IO.Path.GetFullPath(scriptPath); }
            catch { return null; }

            foreach (ProjectModel p in sol.Projects)
            {
                if (p == null) continue;
                string entry;
                if (!TryFindEntryFile(p, out entry) || string.IsNullOrEmpty(entry)) continue;
                string entryFull;
                try { entryFull = System.IO.Path.GetFullPath(entry); }
                catch { continue; }
                if (string.Equals(entryFull, full, StringComparison.OrdinalIgnoreCase))
                    return ResolveEffectiveRunArguments(p);
            }
            return null;
        }

        // ---- AI Terminal placement -------------------------------------------------
        //
        // Three states, driven by the dock header's three buttons. DockablePanel raises the
        // events but takes no action itself, so all the behaviour lives here.
        //
        //   Docked     — a pane under the editor (the default)
        //   Collapsed  — only the title bar, giving the editor the height back
        //   Maximized  — a top-level tab, for a long agent session
        //
        // Minimize means "one step smaller": Maximized → Docked, Docked → Collapsed. It never
        // jumps from Maximized straight to Collapsed, because that would hide the console the
        // user was just working full-screen in.

        /// <summary>
        /// The control that occupies the editor side of <see cref="_leftSplitter"/>.
        ///
        /// This is <see cref="_editorAiSplitter"/> (editor tabs + AI Terminal), NOT
        /// <c>_mainTabControl</c> — the tab control is nested one level deeper now. Anything that
        /// re-parents the editor side must place THIS, or it evicts the AI Terminal from the
        /// control tree: the panel disappears and the splitter is left detached, so View ▸ AI
        /// Terminal silently does nothing. <c>ExplorerCoordinator</c> clears and repopulates both
        /// splitter panels on every dock/undock/float/restore, which is exactly that path.
        ///
        /// Falls back to the tab control so the designer-only form (no runtime layout) still works.
        /// </summary>
        internal Control EditorHost
        {
            // The right-hand tool dock wraps the editor side, so THAT is what a re-parent must
            // move; moving the inner splitter would leave the dock detached and its panels gone.
            get { return (Control)_workspaceSplitter ?? (Control)_editorAiSplitter ?? _mainTabControl; }
        }

        /// <summary>Restore the docked pane to this fraction of the splitter's height.</summary>
        private const double AiTerminalDockedFraction = 0.72;

        internal enum AiTerminalPlacement
        {
            Docked = 0,
            Collapsed = 1,
            Maximized = 2,
        }

        internal AiTerminalPlacement AiTerminalState { get; private set; }

        /// <summary>The tab hosting the terminal while maximized; null otherwise.</summary>
        internal TabPage _aiTerminalTab;

        /// <summary>Minimize: Maximized → Docked, Docked → Collapsed, Collapsed → Docked.</summary>
        private void OnAiTerminalMinimize(object sender, EventArgs e)
        {
            if (AiTerminalState == AiTerminalPlacement.Maximized)
            {
                // From full screen the useful step is back to the dock, not out of sight. The
                // header button already flipped IsExpanded on the way in; put it back so the
                // restored pane shows its content rather than reappearing collapsed.
                RestoreAiTerminalToDock();
                _aiTerminalDock.IsExpanded = true;
                return;
            }

            SetAiTerminalDockedHeight(_aiTerminalDock.IsExpanded);
            AiTerminalState = _aiTerminalDock.IsExpanded
                ? AiTerminalPlacement.Docked
                : AiTerminalPlacement.Collapsed;
        }

        /// <summary>Maximize: move the whole dock into a top-level tab.</summary>
        private void OnAiTerminalMaximize(object sender, EventArgs e)
        {
            if (AiTerminalState == AiTerminalPlacement.Maximized) return;
            if (_mainTabControl == null || _editorAiSplitter == null) return;

            // The DOCK moves, not just the panel: its header carries the minimize button that is
            // the way back, so hosting only the console would strand the user in the tab.
            _editorAiSplitter.Panel2.Controls.Remove(_aiTerminalDock);
            _editorAiSplitter.Panel2Collapsed = true;

            _aiTerminalTab = new TabPage("AI Terminal") { ToolTipText = "AI agent (maximized)" };
            _aiTerminalDock.Dock = DockStyle.Fill;
            _aiTerminalDock.IsExpanded = true;
            _aiTerminalTab.Controls.Add(_aiTerminalDock);
            _mainTabControl.TabPages.Add(_aiTerminalTab);
            _mainTabControl.SelectedTab = _aiTerminalTab;

            // Lit-but-inert reads as broken; it is re-enabled on the way back.
            _aiTerminalDock.FloatButtonEnabled = false;
            AiTerminalState = AiTerminalPlacement.Maximized;
            UpdateAiTerminalMenuChecked();
            FocusAiTerminal();
        }

        /// <summary>Close: hide the terminal entirely. View ▸ AI Terminal brings it back.</summary>
        private void OnAiTerminalClose(object sender, EventArgs e)
        {
            if (AiTerminalState == AiTerminalPlacement.Maximized) RestoreAiTerminalToDock();
            if (_editorAiSplitter != null) _editorAiSplitter.Panel2Collapsed = true;
            AiTerminalState = AiTerminalPlacement.Docked;   // where it reappears from
            _aiTerminalHidden = true;
            UpdateAiTerminalMenuChecked();
        }

        internal bool _aiTerminalHidden;

        /// <summary>
        /// Starts the AI Terminal hidden whatever the saved preference says.
        ///
        /// <para>Set by explicit instruction — "permanently hide the AI terminal
        /// panel until I explicitly tell you to unhide it". Setting this back to <c>false</c> is the
        /// whole of undoing it: the user's real preference is left untouched in their settings file
        /// (see <see cref="PersistAiTerminalVisibility"/>), so un-suppressing restores whatever they
        /// last chose rather than a default.</para>
        ///
        /// <para>View ▸ AI Terminal still works — this suppresses the panel at startup, it does not
        /// lock anyone out of it. Opening it by hand lasts for that session only.</para>
        /// </summary>
        internal static readonly bool AiTerminalSuppressed = true;

        /// <summary>
        /// Record whether the AI Terminal is showing, so closing it survives a restart. Called from
        /// <c>OnFormClosing</c> before the settings are flushed.
        ///
        /// This is the user's INTENT and not a transient visibility: <see cref="IsAiTerminalVisible"/>
        /// reads the hidden flag, so a terminal merely collapsed to its title bar, or maximized to a
        /// tab, still counts as showing. Reading <c>_aiTerminalDock.Visible</c> instead would record
        /// "hidden" whenever the editor split happened to be collapsed, and reopen wrongly.
        ///
        /// Split out of OnFormClosing so it can be tested on its own: the rest of that method tears
        /// down floating panes and the debug session, which a headless harness cannot set up.
        /// </summary>
        internal void PersistAiTerminalVisibility()
        {
            if (_settingsManager == null || _settingsManager.Settings == null) return;
            // While suppressed, leave the saved preference alone. Writing "hidden" over it would
            // mean that turning AiTerminalSuppressed back off reopened the terminal for someone who
            // had deliberately closed it, and kept it shut for someone who had not - the setting
            // would have been overwritten by a decision that was never theirs.
            if (AiTerminalSuppressed) return;
            _settingsManager.Settings.AiTerminalVisible = IsAiTerminalVisible;
        }

        /// <summary>
        /// True when the terminal is on screen in any placement — docked, collapsed to its title
        /// bar, or maximized to a tab. Collapsed still counts as showing: the header is visible and
        /// one click reopens it, so ticking the menu would be wrong only if it were gone entirely.
        /// </summary>
        internal bool IsAiTerminalVisible
        {
            get { return !_aiTerminalHidden; }
        }

        /// <summary>View ▸ AI Terminal — hide it when showing, bring it back when not.</summary>
        internal void ToggleAiTerminal()
        {
            if (IsAiTerminalVisible) HideAiTerminal();
            else ShowAiTerminal();
        }

        /// <summary>Hide the terminal, whatever placement it is currently in.</summary>
        internal void HideAiTerminal()
        {
            OnAiTerminalClose(this, EventArgs.Empty);
        }

        private void UpdateAiTerminalMenuChecked()
        {
            if (_aiTerminalMenuItem == null) return;
            _aiTerminalMenuItem.Checked = IsAiTerminalVisible;
        }

        /// <summary>View ▸ AI Terminal — reopen after a close, or focus it if already showing.</summary>
        internal void ShowAiTerminal()
        {
            if (_editorAiSplitter == null) return;
            if (AiTerminalState == AiTerminalPlacement.Maximized)
            {
                if (_aiTerminalTab != null) _mainTabControl.SelectedTab = _aiTerminalTab;
                FocusAiTerminal();
                return;
            }

            _editorAiSplitter.Panel2Collapsed = false;
            _aiTerminalHidden = false;
            _aiTerminalDock.IsExpanded = true;
            SetAiTerminalDockedHeight(true);
            AiTerminalState = AiTerminalPlacement.Docked;
            UpdateAiTerminalMenuChecked();
            FocusAiTerminal();
        }

        private void RestoreAiTerminalToDock()
        {
            if (_aiTerminalTab != null)
            {
                _aiTerminalTab.Controls.Remove(_aiTerminalDock);
                _mainTabControl.TabPages.Remove(_aiTerminalTab);
                _aiTerminalTab.Dispose();
                _aiTerminalTab = null;
            }

            _aiTerminalDock.Dock = DockStyle.Fill;
            if (!_editorAiSplitter.Panel2.Controls.Contains(_aiTerminalDock))
                _editorAiSplitter.Panel2.Controls.Add(_aiTerminalDock);
            _editorAiSplitter.Panel2Collapsed = false;
            _aiTerminalHidden = false;

            SetAiTerminalDockedHeight(true);
            _aiTerminalDock.FloatButtonEnabled = true;
            AiTerminalState = AiTerminalPlacement.Docked;
            UpdateAiTerminalMenuChecked();
            FocusAiTerminal();
        }

        /// <summary>
        /// Size the bottom pane: the normal fraction when expanded, just the title bar when not.
        /// SplitterDistance is measured from the TOP, so a collapsed bottom pane means a LARGE
        /// distance — and it is clamped against the current height, so an unrealized or tiny
        /// splitter rejects the value rather than applying it.
        /// </summary>
        private void SetAiTerminalDockedHeight(bool expanded)
        {
            if (_editorAiSplitter == null || _editorAiSplitter.Panel2Collapsed) return;
            int height = _editorAiSplitter.Height;
            if (height < 160) return;

            int bottom = expanded
                ? (int)(height * (1.0 - AiTerminalDockedFraction))
                : _aiTerminalDock.CollapsedHeight;
            int distance = height - bottom - _editorAiSplitter.SplitterWidth;

            int min = _editorAiSplitter.Panel1MinSize;
            int max = height - _editorAiSplitter.SplitterWidth - _editorAiSplitter.Panel2MinSize;
            if (distance < min) distance = min;
            if (distance > max) distance = max;
            try { _editorAiSplitter.SplitterDistance = distance; }
            catch (ArgumentOutOfRangeException) { /* window too small; leave it */ }
        }

        private void FocusAiTerminal()
        {
            if (_aiTerminalPanel == null) return;
            try { _aiTerminalPanel.FocusConsole(); } catch (Exception) { }
        }

        /// <summary>
        /// The solution's policy object: member directories plus the solution root.
        /// </summary>
        internal HelixPythonNamespace.VirtualCompiler.Compliance.SolutionPolicy BuildSolutionPolicy(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.BuildSolutionPolicy(sol, BuildRunOptions());
        }

        /// <summary>
        /// Every PY8001/PY8002/PY8003 violation in the solution — see
        /// <see cref="HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.FindPolicyViolations"/>,
        /// which the headless launcher sweeps with too.
        /// </summary>
        internal List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> FindPolicyViolations(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner
                .FindPolicyViolations(sol, BuildRunOptions());
        }

        /// <summary>A short, actionable summary of what is blocking the launch.</summary>
        internal static string DescribePolicyViolations(
            List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> violations)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.DescribePolicyViolations(
                violations,
                new HelixPythonNamespace.Workspace.Execution.PolicyMessageStyle
                {
                    QuickFixCaption = HelixPythonNamespace.App.Controls.ProblemsPanel.QuickFixCaption,
                    FullListHint = "See the Problems panel for the full list.",
                });
        }

        /// <summary>The ProjectModel designated as startup (explicit setting, else sole-app auto-pick).</summary>
        private ProjectModel ResolveStartupProjectModel(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.ResolveStartupProjectModel(sol);
        }

        /// <summary>
        /// The startup package's entry-point FILE path — what the debugger launches (it needs a
        /// concrete script, not a module name). Null when no startup package resolves.
        /// </summary>
        internal string ResolveStartupEntryFile(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.ResolveStartupEntryFile(sol);
        }

        /// <summary>
        /// The startup package's importable module path — what Run launches via
        /// <c>python -m &lt;module&gt;</c>. See <see cref="ResolveRunModule"/>.
        /// </summary>
        internal string ResolveStartupModule(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.ResolveStartupModule(sol);
        }

        /// <summary>
        /// The dotted module name <paramref name="entryFile"/> must be launched under, or null when
        /// it should be launched as a plain script.
        ///
        /// An entry module that lives INSIDE a package has to be started as
        /// <c>python -m &lt;pkg&gt;.&lt;mod&gt;</c>. Only that sets <c>__package__</c>; started by
        /// path, the very first <c>from .experiments.cli import Cli</c> dies with
        /// <c>ImportError: attempted relative import with no known parent package</c> — reported
        /// against <c>BugMap_oop</c>'s <c>rouse_model_verification/main.py</c>, which the
        /// IDE launched as <c>python -u &lt;path&gt;\main.py</c>. The parent directory that makes the
        /// package importable is always on the launch PYTHONPATH
        /// (<see cref="BuildPythonPathEnv"/> → <c>PythonSearchPath.ImportRootsForProjects</c>, which
        /// adds every project's parent), so <c>-m</c> resolves from any working directory.
        ///
        /// The name comes from <c>PythonSearchPath.ModuleFqnOf</c> — the same authority the virtual
        /// compiler resolves imports with, which walks the real <c>__init__.py</c> chain — rather
        /// than from string-concatenating the project name, which was wrong for every layout whose
        /// folder name and package name differ (src-layouts, nested packages).
        ///
        /// Returns null for an entry that is NOT in a package: it has no relative imports to keep
        /// working, and launching it by path is what every other Python tool does.
        /// <c>&lt;pkg&gt;/__main__.py</c> collapses to <c>-m &lt;pkg&gt;</c>, the canonical spelling.
        /// </summary>
        internal string ResolveRunModule(SolutionModel sol, string entryFile)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.ResolveRunModule(sol, entryFile);
        }

        /// <summary>
        /// Generalizes <see cref="ResolveStartupModule"/> to ANY file inside a project, not just its
        /// entry file — e.g. <c>mypkg/chain.py</c> -> <c>mypkg.chain</c>, a nested
        /// <c>mypkg/sub/foo.py</c> -> <c>mypkg.sub.foo</c>, <c>mypkg/__init__.py</c> -> <c>mypkg</c>.
        /// Needed so the Profiler can run an arbitrary file as a real package submodule (<c>python
        /// -m mypkg.chain</c>) instead of a bare script — only that sets <c>__package__</c> so the
        /// file's own genuine relative imports (<c>from . import x</c>) resolve. Returns null when
        /// the file isn't under the project's own directory.
        /// </summary>
        internal static string ResolveModuleName(ProjectModel project, string filePath)
        {
            if (project == null || string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(project.ProjectDirectory))
                return null;
            string fullFile = Path.GetFullPath(filePath);
            string normDir = Path.GetFullPath(project.ProjectDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullFile.StartsWith(normDir, StringComparison.OrdinalIgnoreCase)) return null;

            string relative = fullFile.Substring(normDir.Length);
            string stem = Path.GetFileNameWithoutExtension(relative);
            string dir = Path.GetDirectoryName(relative) ?? string.Empty;

            var parts = new List<string> { project.Name };
            if (!string.IsNullOrEmpty(dir))
                parts.AddRange(dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.Equals(stem, "__init__", StringComparison.Ordinal))
                parts.Add(stem);
            return string.Join(".", parts);
        }

        /// <summary>
        /// Open the bundled sample CUDA kernel in a tabbed editor with a breakpoint on its device
        /// function, ready to debug with F5 (Debug ▸ Start) in the SAME Debug panel + editor markers
        /// as Python. Replaces the old standalone CUDA debugger window — CUDA now debugs from the same
        /// position as Python: the file is a real editor tab, F5 routes to the SIMT emulator engine,
        /// and the stop populates the shared Variables / Call Stack panels and the editor arrow.
        /// </summary>
        internal void OpenSampleCudaKernelForDebug(int bpLine = 5)
        {
            const string sample =
                "#include <cuda_runtime.h>\n" +
                "#include <stdio.h>\n" +
                "\n" +
                "__device__ int square(int v) {\n" +
                "    int r = v * v;\n" +          // line 5 — breakpoint
                "    return r;\n" +
                "}\n" +
                "\n" +
                "__global__ void compute(int* out) {\n" +
                "    int i = threadIdx.x;\n" +
                "    int s = square(i);\n" +
                "    out[i] = s;\n" +
                "}\n" +
                "\n" +
                "int main() {\n" +
                "    int* d;\n" +
                "    cudaMalloc(&d, 8 * sizeof(int));\n" +
                "    compute<<<1, 8>>>(d);\n" +
                "    cudaDeviceSynchronize();\n" +
                "    cudaFree(d);\n" +
                "    return 0;\n" +
                "}\n";
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "HelixPython");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "sample_kernel.cu");
                File.WriteAllText(path, sample);
                OpenInEditor(path, bpLine);
                string full = Path.GetFullPath(path);
                TabPage tab;
                if (_openEditorTabs != null && _openEditorTabs.TryGetValue(full, out tab)
                    && tab is HelixPythonNamespace.Editor.EditorTabPage etp)
                {
                    etp.Editor.ToggleBreakpointAtLine(bpLine);
                }
                if (_statusLabel != null)
                    _statusLabel.Text = "Sample CUDA kernel opened — press F5 (Debug ▸ Start) to debug on the SIMT emulator.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to open sample CUDA kernel: " + ex.Message,
                    "CUDA Debugger", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// The import roots this solution's projects contribute, in <c>sys.path</c> order. Single
        /// source of truth for both the launcher's <c>PYTHONPATH</c> (<see cref="BuildPythonPathEnv"/>)
        /// and the virtual compiler's compile-time import resolution (<see cref="BuildPythonSearchPath"/>),
        /// so the two can never disagree about where a module comes from.
        /// </summary>
        internal List<string> CollectProjectDirectories(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.CollectProjectDirectories(sol);
        }

        /// <summary>
        /// The declared kind of each project directory, so solution policy rule 3 (PY8003) can
        /// tell a test project — the one kind allowed to contain test files — from every other.
        /// </summary>
        internal List<KeyValuePair<string, HelixPythonNamespace.Core.Model.Enums.ProjectKind>> CollectProjectKinds(
            SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.CollectProjectKinds(sol);
        }

        /// <summary>
        /// The <c>sys.path</c> model for this solution — what the virtual compiler resolves
        /// imports against, and what <see cref="BuildPythonPathEnv"/> renders into the environment.
        /// </summary>
        internal HelixPythonNamespace.VirtualCompiler.Imports.PythonSearchPath BuildPythonSearchPath(
            SolutionModel sol, string entryFile)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.BuildPythonSearchPath(sol, entryFile);
        }

        /// <summary>The directory holding the solution's own manifest, or null.</summary>
        private static string SolutionRootDirectory(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.SolutionRootDirectory(sol);
        }

        /// <summary>
        /// Build a PYTHONPATH env-var value that makes every workspace member importable by its
        /// bare package name.
        ///
        /// Each project contributes its OWN directory, and additionally its parent only when the
        /// project directory is itself a regular package. Adding the parent unconditionally — which
        /// this method used to do, and nothing else — is what broke the <c>sample_workspace</c> layout:
        /// with only <c>projects/</c> on the path, <c>import sample_common</c> found the outer
        /// <c>projects/sample_common/</c> folder, which has no <c>__init__.py</c>, and bound it as an
        /// empty PEP 420 namespace package instead of the real
        /// <c>projects/sample_common/sample_common/</c> package one level down — producing
        /// <c>cannot import name 'TsvPreparationWorkflow' from 'sample_common' (unknown location)</c>
        /// at runtime. See <see cref="HelixPythonNamespace.VirtualCompiler.Imports.PythonSearchPath"/>.
        /// </summary>
        internal IReadOnlyDictionary<string, string> BuildPythonPathEnv(SolutionModel sol)
        {
            IReadOnlyDictionary<string, string> env =
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlanner.BuildPythonPathEnv(sol);
            return AddCudaEmulationEnv(env);
        }

        /// <summary>
        /// When the CUDA toolchain is the emulator, add what a Python run needs to route its
        /// kernels there: the <c>helix_cuda</c> shim and its <c>sitecustomize</c> bootstrap on
        /// <c>PYTHONPATH</c>, the bridge executable's location, and the switch that arms it.
        ///
        /// <para>
        /// Gated on the SAME toolchain setting the native build path uses, so "which CUDA am I
        /// running against" has one answer for a solution rather than one per language. Emulating
        /// a Python program's kernels while building its C++ half against a real nvcc would be two
        /// different machines in one project.
        /// </para>
        /// <para>
        /// <c>HELIX_CUDA_EMULATE</c> is what actually arms it: the bootstrap does nothing without
        /// that variable, so putting the directory on the path cannot change the behaviour of a
        /// program that was not meant to be emulated — including, on this machine, one that has a
        /// real CuPy installed and should go on using it.
        /// </para>
        /// </summary>
        private IReadOnlyDictionary<string, string> AddCudaEmulationEnv(
            IReadOnlyDictionary<string, string> baseEnv)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (baseEnv != null)
                foreach (KeyValuePair<string, string> pair in baseEnv) env[pair.Key] = pair.Value;

            bool ignored;
            if (EffectiveCMakeToolchain(out ignored)
                != HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Emulator)
                return env;

            string shim = FindHelixCudaShimDirectory();
            if (shim == null) return env;   // not a source checkout; leave the run untouched

            string bootstrap = Path.Combine(shim, "helix_cuda_bootstrap");
            string existing;
            env.TryGetValue("PYTHONPATH", out existing);
            var parts = new List<string> { shim, bootstrap };
            if (!string.IsNullOrEmpty(existing)) parts.Add(existing);
            env["PYTHONPATH"] = string.Join(Path.PathSeparator.ToString(), parts.ToArray());

            env["HELIX_CUDA_EMULATE"] = "1";
            string bridge = FindCudaBridgeExecutable();
            if (bridge != null) env["HELIX_CUDA_BRIDGE"] = bridge;
            return env;
        }

        /// <summary>The folder holding the <c>helix_cuda</c> package, or null.</summary>
        private static string FindHelixCudaShimDirectory()
        {
            return FindBesideRepositoryRoot(Path.Combine("src", "python"), "helix_cuda");
        }

        /// <summary>The built bridge executable, or null when it has not been built.</summary>
        private static string FindCudaBridgeExecutable()
        {
            string directory = FindBesideRepositoryRoot(
                Path.Combine("artifacts", "bin", "HelixStudio.CudaBridge", "Debug", "net472"),
                "helix_cuda_bridge.exe");
            return directory == null ? null : Path.Combine(directory, "helix_cuda_bridge.exe");
        }

        // Walk up from the running assembly to the repository marker, then down the given
        // relative path, and confirm `expected` is in it. Mirrors the toolbar/tree icon loaders.
        private static string FindBesideRepositoryRoot(string relative, string expected)
        {
            try
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                    {
                        string candidate = Path.Combine(dir.FullName, relative);
                        bool found = Directory.Exists(Path.Combine(candidate, expected))
                            || File.Exists(Path.Combine(candidate, expected));
                        return found ? candidate : null;
                    }
                    dir = dir.Parent;
                }
            }
            catch { }
            return null;
        }

        // ---- Build ----

        // ---- Analysis ----

        // ---------- Virtual compiler (Build → Compile) ----------

        internal bool _compileBusy;

        internal void OnToggleFloatNavigationTree(object sender, EventArgs e) => _explorerCoordinator.OnToggleFloatNavigationTree(sender, e);

        // Completion callback for a virtual-compile run: clears the busy flag, populates the Problems
        // panel, summarizes error/warning counts in the status bar, and re-shows the Problems tab if
        // it had been closed (deliberately WITHOUT selecting it, to avoid stealing keyboard focus).
        internal void FinishCompile(List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> diagnostics)
        {
            _compileBusy = false;
            _problemsPanel.ShowDiagnostics(diagnostics);

            int errors = 0, warnings = 0;
            foreach (var d in diagnostics)
            {
                if (d.Severity >= HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error) errors++;
                else if (d.Severity == HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Warning) warnings++;
            }
            _statusLabel.Text = errors == 0 && warnings == 0
                ? "Compile complete: no issues"
                : string.Format("Compile complete: {0} error(s), {1} warning(s)", errors, warnings);

            // Surface the Problems tab in the strip if the user had closed it, but
            // don't change the selected tab — selecting a TabPage in WinForms moves
            // keyboard focus to its first focusable child (the Problems grid),
            // stealing focus from wherever the user is currently typing.
            if (_problemsTab != null && _mainTabControl != null &&
                !_mainTabControl.TabPages.Contains(_problemsTab))
            {
                ShowTab("problems");
            }
        }

        /// <summary>
        /// Walk each project directory for <c>.py</c> files. The earlier
        /// implementation only considered files appearing on some
        /// <see cref="TypeModel.SourceFilePath"/>, which silently skipped every
        /// module made entirely of functions (e.g. app entry-points) — those
        /// never got compiled. Walking the filesystem directly guarantees
        /// function-only modules participate.
        /// </summary>
        private static List<string> EnumerateSolutionPythonFiles(SolutionModel sol)
        {
            return HelixPythonNamespace.Workspace.Execution.SolutionSourceInventory.PythonFiles(sol, null);
        }

        /// <summary>
        /// Walk each project directory for <c>.py</c> files and derive the dotted
        /// module FQN for each (e.g. <c>lib_utils/geometry.py</c> under project
        /// <c>lib_utils</c> → <c>lib_utils.geometry</c>). Used by
        /// <see cref="HelixPythonNamespace.VirtualCompiler.VirtualCompiler.CompileSolution"/> so
        /// that <c>from mod import foo</c> resolves to real callable signatures for
        /// cross-module type checking.
        /// </summary>

        internal static void CollectPyFilesRecursive(
            string dir, HashSet<string> skipDirs, HashSet<string> seen, List<string> result)
        {
            HelixPythonNamespace.Workspace.Execution.SolutionSourceInventory
                .CollectPyFilesRecursive(dir, skipDirs, seen, result);
        }

        // Double-click / activate on a Problems-panel diagnostic — opens its file at the offending line.
        // Mirror the Compile panel's error/warning totals in the status bar AND on the Compile tab
        // after every compile, so the health of the last build is visible from any tab. Status bar is
        // blank when there is nothing to report; the tab shows a "(N)" badge of the issue count.
        private void OnDiagnosticsChanged(object sender, EventArgs e)
        {
            if (_problemsPanel == null) return;
            if (_diagnosticStore != null)
            {
                _diagnosticStore.Replace("Compiler", _problemsPanel.Diagnostics.Select(d =>
                    new HelixPythonNamespace.App.Dialogs.BuildResultItem
                    {
                        Source = "Compiler",
                        Severity = d.Severity.ToString().ToLowerInvariant(),
                        Code = d.Code,
                        Message = d.Message,
                        File = d.FilePath,
                        Line = d.Span.Start.Line,
                        Column = d.Span.Start.Column
                    }));
            }
            int errors = _problemsPanel.ErrorCount;
            int warnings = _problemsPanel.WarningCount;

            if (_diagnosticsStatusLabel != null)
            {
                if (errors == 0 && warnings == 0)
                {
                    _diagnosticsStatusLabel.Text = string.Empty;
                    _diagnosticsStatusLabel.IsLink = false;   // nothing to jump to
                }
                else
                {
                    string errText = errors + (errors == 1 ? " error" : " errors");
                    string warnText = warnings + (warnings == 1 ? " warning" : " warnings");
                    _diagnosticsStatusLabel.Text = errText + ", " + warnText;
                    Color sev = errors > 0
                        ? Color.FromArgb(255, 150, 150)   // red-ish when the build has errors
                        : Color.FromArgb(255, 220, 150);  // amber when only warnings
                    _diagnosticsStatusLabel.ForeColor = sev;
                    // As a link the LinkColor wins over ForeColor, so keep it in sync with severity.
                    _diagnosticsStatusLabel.LinkColor = sev;
                    _diagnosticsStatusLabel.IsLink = true;    // clickable → Compile panel
                }
            }

            if (_problemsTab != null) _problemsTab.Text = FormatCompileTabTitle(errors, warnings);
        }

        // The Compile tab caption: "Compile" when clean, "Compile (N)" with the issue count otherwise.
        internal static string FormatCompileTabTitle(int errors, int warnings)
        {
            int total = errors + warnings;
            return total > 0 ? "Compile (" + total + ")" : "Compile";
        }

        private void OnProblemActivated(object sender, HelixPythonNamespace.VirtualCompiler.CompileDiagnostic d)
        {
            if (d == null || string.IsNullOrEmpty(d.FilePath)) return;
            int line = d.Span.Start.Line > 0 ? d.Span.Start.Line : -1;
            // Carry the diagnostic's column so the caret lands on the flagged token, not column 1.
            int column = d.Span.Start.Column > 0 ? d.Span.Start.Column : -1;
            OpenInEditor(d.FilePath, line, true, column);
        }

        /// <summary>
        /// Apply the resolution offered for a PY8001 row: let the user pick the project folder the
        /// stray file belongs in, move it there, then reload so the tree, the compiler and the
        /// Run/Debug gate all agree it is now a member.
        ///
        /// PY8001 is a HARD blocker — the message could previously only tell the user their Run
        /// button was disabled and leave them to fix the workspace by hand. Membership is decided by
        /// containment, so the fix genuinely is a move; the dialog says so before anything happens.
        /// </summary>
        internal void OnProblemQuickFix(object sender, HelixPythonNamespace.VirtualCompiler.CompileDiagnostic d)
        {
            if (!HelixPythonNamespace.App.Controls.ProblemsPanel.HasQuickFix(d)) return;
            if (_currentSolution == null)
            {
                ReportFixProblem("Open the solution this file belongs to first.");
                return;
            }

            List<HelixPythonNamespace.Workspace.Python.MembershipTarget> targets = MembershipTargets(_currentSolution);
            if (targets.Count == 0)
            {
                ReportFixProblem("This solution has no member projects to add the file to." + Environment.NewLine
                    + Environment.NewLine
                    + "Create one with File ▸ New ▸ Project, or register the file's own folder with "
                    + "Solution Explorer ▸ right-click the solution ▸ Add Existing Project.");
                return;
            }

            string fileName = System.IO.Path.GetFileName(d.FilePath);
            HelixPythonNamespace.Workspace.Python.MembershipTarget chosen;
            using (var dlg = new HelixPythonNamespace.App.Dialogs.AddFileToProjectDialog(fileName, targets))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedTarget == null) return;
                chosen = dlg.SelectedTarget;
            }

            var result = HelixPythonNamespace.Workspace.Python.SolutionMembershipFixer.AddFileToProject(d.FilePath, chosen);
            if (!result.Succeeded)
            {
                ReportFixProblem(result.Message);
                return;
            }

            // The open editor tab still points at the old path; move it with the file so the user
            // isn't left editing a document that no longer exists.
            RetargetOpenDocument(d.FilePath, result.NewPath);

            // The blocker is resolved, so take its row out now rather than leaving a stale error
            // sitting there until the user thinks to re-run Compile.
            if (_problemsPanel != null) _problemsPanel.RemoveDiagnostic(d);

            if (_statusLabel != null) _statusLabel.Text = result.Message;
            ReloadSolutionAsync();
        }

        // Why the fix could not be applied. Routed through a helper so it stays silent under
        // HELIXSTUDIO_NONINTERACTIVE — a modal box in a headless run blocks the STA thread with
        // nobody left to dismiss it (see the external-change handler for the same guard).
        private void ReportFixProblem(string message)
        {
            if (_statusLabel != null) _statusLabel.Text = message.Replace(Environment.NewLine, " ");
            if (string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1",
                              StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine("[noninteractive] Add to Project: " + message);
                return;
            }
            MessageBox.Show(this, message, "Add to Project", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Every folder in the current solution a stray file could join.
        private List<HelixPythonNamespace.Workspace.Python.MembershipTarget> MembershipTargets(SolutionModel sol)
        {
            var packages = new List<HelixPythonNamespace.Workspace.Python.PackageDescriptor>();
            if (sol != null && sol.Projects != null)
            {
                foreach (ProjectModel p in sol.Projects)
                {
                    if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                    string dir = !string.IsNullOrEmpty(p.ProjectDirectory)
                        ? p.ProjectDirectory
                        : (string.IsNullOrEmpty(p.FilePath) ? null : System.IO.Path.GetDirectoryName(p.FilePath));
                    if (string.IsNullOrEmpty(dir)) continue;
                    packages.Add(new HelixPythonNamespace.Workspace.Python.PackageDescriptor(p.Name, dir));
                }
            }
            return HelixPythonNamespace.Workspace.Python.SolutionMembershipFixer.TargetsFor(
                SolutionRootDirectory(sol), packages);
        }

        // Point an already-open document at the file's new location after a move.
        private void RetargetOpenDocument(string oldPath, string newPath)
        {
            if (_editorInnerTabControl == null || string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
            foreach (TabPage p in _editorInnerTabControl.TabPages)
            {
                var etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp == null || etp.Editor == null) continue;
                if (!string.Equals(etp.FilePath, oldPath, StringComparison.OrdinalIgnoreCase)) continue;
                // The setter already forwards to Editor.SetFilePath.
                try { etp.FilePath = newPath; etp.Text = System.IO.Path.GetFileName(newPath); }
                catch { /* a retitle failure must not undo a completed move */ }
            }
        }

        /// <summary>
        /// Push per-type violation counts onto their NodeVisuals so Render draws a badge
        /// in the top-right corner. Called after each analysis run.
        /// </summary>
        // ---- Export ----

        // Export ▸ C#: a no-op informational command. Types already live in source files (the single
        // source of truth), so there is nothing to export — this just tells the user where they are.
        internal void OnExportCSharp(object sender, EventArgs e)
        {
            if (_currentSolution == null)
            {
                MessageBox.Show(
                    "No solution loaded. Solution types are already in C# source files.",
                    "Info",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            MessageBox.Show(
                string.Format(
                    "Solution types are located in:\n{0}\n\nNo export needed - source files are the single source of truth.",
                    Path.GetDirectoryName(_currentSolution.FilePath)),
                "Source Location",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        // Export ▸ JSON: builds a diagram projection (model + layout) and writes it as metadata JSON.
        internal void OnExportJson(object sender, EventArgs e)
        {
            if (_currentSolution == null)
            {
                _statusLabel.Text = "No solution loaded";
                return;
            }

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*";
                dlg.DefaultExt = "json";
                dlg.FileName = _currentSolution.Name + "-metadata.json";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var layout = _layoutStore?.LayoutModel ?? new LayoutModel(_currentSolution.FilePath);
                        var projection = new DiagramProjection(_currentSolution, layout);
                        var diagram = projection.Build();

                        _metadataExporter.ExportJson(diagram, dlg.FileName);
                        _statusLabel.Text = string.Format("Exported JSON to: {0}", dlg.FileName);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            string.Format("Export failed: {0}", ex.Message),
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>Folder of the currently-open native CMake project (set by <see cref="LoadCMakeFolderAsNativeProject"/>).</summary>
        internal string _currentCMakeFolder;
        internal HelixPythonNamespace.Workspace.Cuda.CMakeProjectDescriptor _currentCMakeProject;

        // The [tool.helixstudio] linked-cmake-project — deliberately SEPARATE fields from
        // _currentCMakeFolder/_currentCMakeProject above: those two are IsNativeWorkspaceActive's
        // gate (setting them REPLACES the Python workspace). A linked project attaches ALONGSIDE
        // the active Python solution instead, so it must never touch that gate.
        internal string _linkedCMakeFolder;
        internal HelixPythonNamespace.Workspace.Cuda.CMakeProjectDescriptor _linkedCMakeProject;
        internal TreeNode _linkedCMakeTreeNode;

        /// <summary>
        /// SS §linked-cmake-project step 2: resolve+import the solution's linked CMake folder (if
        /// any) and attach it as a secondary Solution Explorer node. Safe to call unconditionally —
        /// no-ops when the solution has none (SolutionModel.LinkedCMakeProjectPath is already
        /// validated null-or-real by SolutionService). Called right after a normal Python solution
        /// load finishes (SolutionCoordinator.OpenSolutionAsync), and torn down by
        /// <see cref="DisposeLinkedCMakeProject"/> on solution close.
        /// </summary>
        internal void AttachLinkedCMakeProjectIfPresent(SolutionModel solution)
        {
            DisposeLinkedCMakeProject();
            string folder = solution?.LinkedCMakeProjectPath;
            if (string.IsNullOrEmpty(folder) || _projectTreePanel == null) return;

            var importer = new HelixPythonNamespace.Workspace.Cuda.CMakeProjectImporter();
            HelixPythonNamespace.Workspace.Cuda.CMakeImportResult res = importer.Import(folder);
            if (res == null || !res.Success || res.Project == null)
            {
                // Same tolerance as a missing/invalid key — never block the Python solution.
                if (_statusLabel != null)
                    _statusLabel.Text = "Linked CMake project not attached: " + (res != null ? res.Error : "import failed");
                return;
            }

            HelixPythonNamespace.Workspace.Cuda.CMakeProjectDescriptor d = res.Project;
            var targets = new List<ProjectTreePanel.CMakeTargetInfo>();
            foreach (HelixPythonNamespace.Workspace.Cuda.CMakeResolvedTarget t in d.Targets)
            {
                var info = new ProjectTreePanel.CMakeTargetInfo { Name = t.Name, Kind = t.Kind.ToString() };
                info.SourceFiles.AddRange(t.SourceFiles);
                targets.Add(info);
            }

            _linkedCMakeFolder = folder;
            _linkedCMakeProject = d;
            _linkedCMakeTreeNode = _projectTreePanel.AddLinkedNativeProjectNode(d.Name, d.Kind.ToString(), targets);
        }

        /// <summary>SS §linked-cmake-project step 4: dispose the secondary node on solution close (parity with NewEmptyWorkspace's other teardown).</summary>
        internal void DisposeLinkedCMakeProject()
        {
            if (_linkedCMakeTreeNode != null && _projectTreePanel != null)
                _projectTreePanel.RemoveLinkedNativeProjectNode(_linkedCMakeTreeNode);
            _linkedCMakeTreeNode = null;
            _linkedCMakeProject = null;
            _linkedCMakeFolder = null;
        }

        /// <summary>
        /// Configure+build ONE target of the currently-open (primary, non-linked) native CMake
        /// project. Used by the per-ProjectModel "Build Project" context command — each CMake
        /// target is its own ProjectModel now (see CMakeSolutionLoader), so "build THIS project"
        /// means "build THIS one target", not the whole folder the way F6/Build menu's
        /// BuildActiveCMakeProject does. Mirrors BuildLinkedCMakeTarget exactly, parametrized by
        /// _currentCMakeFolder instead of _linkedCMakeFolder (the separate "attached alongside a
        /// Python solution" flow) — kept as a sibling method rather than a shared refactor so
        /// neither flow risks regressing the other.
        /// </summary>
        internal HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult BuildNativeCMakeTarget(
            string targetName, bool clearOutput = true)
        {
            if (string.IsNullOrEmpty(_currentCMakeFolder) || !Directory.Exists(_currentCMakeFolder))
            {
                if (_statusLabel != null) _statusLabel.Text = "No native CMake project is open to build.";
                return null;
            }
            string buildDir = CMakeBuildDirectory(_currentCMakeFolder);
            var toolchain = ActiveCMakeToolchain();
            // clearOutput:false when this is one PHASE of a bigger command (Rebuild = clean then
            // build) that already cleared the pane and owns the whole transcript.
            if (clearOutput) BeginNativeCommandOutput(null);
            AppendBuildOutputLine("==> cmake --build " + buildDir + " --target " + targetName);
            _buildInnerTabControl.SelectedTab = _buildOutputSubTab;
            ShowLinkedCMakeOutputTab();

            if (_statusLabel != null) _statusLabel.Text = "Configuring (cmake) " + _currentCMakeFolder + " …";
            var stdArgs = HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.CreateLanguageStandardArguments(
                ActiveCxxStandard(), ActiveCudaStandard(), ProjectUsesCuda(_currentCMakeFolder));
            string msvcEnv = ActiveMsvcEnvironmentScript();
            var cfg = RunCMakePhasePumped(() => HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.Configure(
                _currentCMakeFolder, buildDir, "Ninja", stdArgs,
                120000, toolchain, msvcEnv, AppendBuildOutputLine));
            DumpCMakeRunResult("configure", cfg, streamed: true);
            if (!cfg.Available || cfg.ExitCode != 0)
            {
                if (_statusLabel != null) _statusLabel.Text = "Configure failed for " + targetName + ".";
                return cfg;
            }

            if (_statusLabel != null) _statusLabel.Text = "Building target " + targetName + " …";
            var bld = RunCMakePhasePumped(() => HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.Build(
                buildDir, targetName, 180000, toolchain, msvcEnv, AppendBuildOutputLine));
            DumpCMakeRunResult("build", bld, streamed: true);
            if (_statusLabel != null)
                _statusLabel.Text = bld.Succeeded
                    ? "Build " + targetName + " succeeded"
                    : "Build " + targetName + " failed";
            return bld;
        }

        // ---- VS-style Build menu commands for a native C++/CUDA workspace (Build/Rebuild/Clean,
        // at solution scope and per project). "Build Solution" is OnBuildCMakeProject; the rest
        // are here.

        /// <summary>
        /// Start a native build/run/clean: claim the job runner, grey out every command that must
        /// not be re-entered, and clear the Output pane for this command's own output.
        ///
        /// Re-entrancy is a real hazard here, not a theoretical one: these flows are synchronous
        /// UI-thread entry points that pump the message loop (RunCMakePhasePumped) so output can
        /// stream live — which leaves the whole menu bar clickable mid-build. Nothing stopped a
        /// second Build (or a Clean, or closing the solution) from starting on top of the first.
        ///
        /// Returns false when something is already running, in which case the caller must not
        /// proceed and must NOT call <see cref="EndNativeBuildJob"/>.
        /// </summary>
        internal bool BeginNativeBuildJob(string title, string outputHeader)
        {
            if (_jobRunner != null && _jobRunner.IsBusy) return false;
            if (!SaveAllModifiedEditorTabsBeforeExternalOperation(title)) return false;
            if (_jobRunner != null)
                _jobRunner.BeginExternal(title, delegate
                {
                    // The Cancel/Stop affordance: tree-kill the cmake/ninja process. The build's
                    // own pumped wait then returns with a non-zero exit and unwinds normally.
                    try { HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.CancelCurrent(); } catch { }
                });
            SetCommandsEnabledDuringNativeBuild(false);
            BeginNativeCommandOutput(outputHeader);
            // The Output pane's summary line is a LIVE status, so say what is running. Nothing on
            // the native path used to set it at build start, which is how it ended up still reading
            // the empty-state hint while a build streamed underneath it.
            if (_buildSummaryLabel != null) _buildSummaryLabel.Text = title + "…";
            return true;
        }

        internal void EndNativeBuildJob(string finalStatus)
        {
            SetCommandsEnabledDuringNativeBuild(true);
            // Settle the status line — but only if nothing more specific already replaced it.
            // PopulateCMakeOutput writes "CMake build succeeded/failed", which is the better
            // summary; commands that never reach it (Clean) still need their own outcome.
            if (_buildSummaryLabel != null &&
                (_buildSummaryLabel.Text ?? string.Empty).EndsWith("…", StringComparison.Ordinal))
                _buildSummaryLabel.Text = finalStatus ?? string.Empty;
            if (_jobRunner != null) _jobRunner.Finish(finalStatus);
        }

        /// <summary>
        /// While a native build runs, the ONLY command that stays live is Stop Execution (the same
        /// item the red toolbar Stop button drives). Everything else on the menu bar and toolbar is
        /// disabled — the Build menu itself stays enabled purely so Stop can be reached inside it.
        /// </summary>
        internal void SetCommandsEnabledDuringNativeBuild(bool enabled)
        {
            if (_menuStrip != null)
            {
                foreach (ToolStripItem top in _menuStrip.Items)
                {
                    ToolStripMenuItem topMenu = top as ToolStripMenuItem;
                    if (topMenu == null) continue;
                    bool isBuildMenu = ReferenceEquals(topMenu, buildTopMenuItem);
                    // Keep Build reachable so its Stop Execution child can be clicked.
                    topMenu.Enabled = enabled || isBuildMenu;
                    if (!isBuildMenu) continue;
                    foreach (ToolStripItem child in topMenu.DropDownItems)
                    {
                        if (ReferenceEquals(child, _stopExecutionMenuItem)) continue;   // the Cancel
                        child.Enabled = enabled;
                    }
                }
            }
            if (_toolStrip != null)
            {
                foreach (ToolStripItem item in _toolStrip.Items)
                {
                    if (ReferenceEquals(item, _stopExecutionButton)) continue;          // the Cancel
                    item.Enabled = enabled;
                }
            }
        }

        /// <summary>
        /// Every Build/Rebuild/Clean/Run starts from an EMPTY Output pane: without this the next
        /// command's output was appended under the previous one, so a short Clean looked like it
        /// had reprinted a whole build. Also selects the Output sub-tab and raises the tab.
        /// </summary>
        private void BeginNativeCommandOutput(string header)
        {
            if (_buildOutputTextBox != null) _buildOutputTextBox.Clear();
            if (_buildErrorsListView != null) _buildErrorsListView.Items.Clear();
            if (_buildInnerTabControl != null && _buildOutputSubTab != null)
                _buildInnerTabControl.SelectedTab = _buildOutputSubTab;
            ShowLinkedCMakeOutputTab();
            if (!string.IsNullOrEmpty(header)) AppendBuildOutputLine(header);
        }

        private bool EnsureNativeCMakeOpen(string caption)
        {
            if (!string.IsNullOrEmpty(_currentCMakeFolder)) return true;
            if (!string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1", StringComparison.Ordinal))
                MessageBox.Show(this, "Open a CMake folder first (Build ▸ Open CMake Folder…).",
                    caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        /// <summary>Build ▸ Clean Solution — <c>cmake --build &lt;dir&gt; --target clean</c>.</summary>
        internal void OnCleanCMakeSolution(object sender, EventArgs e)
        {
            if (!EnsureNativeCMakeOpen("Clean Solution")) return;
            if (!BeginNativeBuildJob("Cleaning solution", "==> Clean Solution")) return;
            try
            {
                BuildNativeCMakeTarget("clean", clearOutput: false);
                if (_statusLabel != null) _statusLabel.Text = "Clean Solution finished";
            }
            finally { EndNativeBuildJob("Clean Solution finished"); }
        }

        /// <summary>Build ▸ Rebuild Solution — clean the whole tree, then configure + build it all.</summary>
        internal void OnRebuildCMakeSolution(object sender, EventArgs e)
        {
            if (!EnsureNativeCMakeOpen("Rebuild Solution")) return;
            if (!BeginNativeBuildJob("Rebuilding solution", "==> Rebuild Solution")) return;
            try
            {
                BuildNativeCMakeTarget("clean", clearOutput: false);
                BuildActiveCMakeProject(clearOutput: false);
            }
            finally { EndNativeBuildJob(_statusLabel != null ? _statusLabel.Text : "Rebuild finished"); }
        }

        /// <summary>
        /// Per-target clean. CMake has no per-target clean of its own — <c>--target clean</c> always
        /// wipes the whole tree — so this removes exactly what rebuilding that one target has to
        /// regenerate: its <c>&lt;target&gt;.dir</c> object directories and its built binary.
        /// </summary>
        internal void CleanNativeCMakeTarget(string targetName, bool clearOutput = true)
        {
            if (string.IsNullOrEmpty(_currentCMakeFolder) || string.IsNullOrEmpty(targetName)) return;
            string buildDir = CMakeBuildDirectory(_currentCMakeFolder);
            if (clearOutput) BeginNativeCommandOutput(null);
            ShowLinkedCMakeOutputTab();
            _buildInnerTabControl.SelectedTab = _buildOutputSubTab;
            AppendBuildOutputLine("==> clean target " + targetName);
            if (!Directory.Exists(buildDir))
            {
                AppendBuildOutputLine("nothing to clean — " + buildDir + " has not been configured yet");
                if (_statusLabel != null) _statusLabel.Text = "Clean " + targetName + ": nothing built yet";
                return;
            }

            int removed = 0;
            string[] objDirs;
            try { objDirs = Directory.GetDirectories(buildDir, targetName + ".dir", SearchOption.AllDirectories); }
            catch (Exception ex) { AppendBuildOutputLine("could not scan " + buildDir + ": " + ex.Message); objDirs = new string[0]; }
            foreach (string dir in objDirs)
            {
                try { Directory.Delete(dir, true); removed++; AppendBuildOutputLine("removed " + dir); }
                catch (Exception ex) { AppendBuildOutputLine("could not remove " + dir + ": " + ex.Message); }
            }

            string exe = FindCMakeExecutable(buildDir, targetName);
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                try { File.Delete(exe); removed++; AppendBuildOutputLine("removed " + exe); }
                catch (Exception ex) { AppendBuildOutputLine("could not remove " + exe + ": " + ex.Message); }
            }

            AppendBuildOutputLine("==> clean " + targetName + ": " + removed + " item(s) removed");
            if (_statusLabel != null) _statusLabel.Text = "Cleaned " + targetName;
        }

        /// <summary>
        /// Fill the three per-project submenus from the open native project's real CMake targets.
        /// Rebuilt on every Build-menu open so it tracks the loaded solution instead of a snapshot
        /// taken when the menu was first constructed.
        /// </summary>
        internal void PopulateNativeProjectBuildMenus(
            ToolStripMenuItem buildMenu, ToolStripMenuItem rebuildMenu, ToolStripMenuItem cleanMenu)
        {
            ToolStripMenuItem[] menus = { buildMenu, rebuildMenu, cleanMenu };
            foreach (ToolStripMenuItem m in menus) { if (m != null) m.DropDownItems.Clear(); }

            var targets = new List<string>();
            if (_currentCMakeProject != null && _currentCMakeProject.Targets != null)
            {
                foreach (var t in _currentCMakeProject.Targets)
                {
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    // "Unassigned Sources" is the importer's synthetic bucket for files no real
                    // target claims — cmake has no such target, so offering to build it would hand
                    // the user a command that can only fail.
                    if (t.Name == HelixPythonNamespace.Workspace.Cuda.CMakeConventions.UnassignedSourcesTargetName) continue;
                    if (!targets.Contains(t.Name)) targets.Add(t.Name);
                }
            }
            targets.Sort(StringComparer.OrdinalIgnoreCase);

            if (targets.Count == 0)
            {
                foreach (ToolStripMenuItem m in menus)
                {
                    if (m == null) continue;
                    m.DropDownItems.Add(new ToolStripMenuItem("(no CMake targets)") { Enabled = false });
                }
                return;
            }

            foreach (string name in targets)
            {
                string t = name;   // capture per iteration
                if (buildMenu != null)
                    buildMenu.DropDownItems.Add(new ToolStripMenuItem(t, null, delegate
                    {
                        if (!BeginNativeBuildJob("Building " + t, "==> Build " + t)) return;
                        try { BuildNativeCMakeTarget(t, clearOutput: false); }
                        finally { EndNativeBuildJob(_statusLabel != null ? _statusLabel.Text : "Build finished"); }
                    }));
                if (rebuildMenu != null)
                    rebuildMenu.DropDownItems.Add(new ToolStripMenuItem(t, null, delegate
                    {
                        if (!BeginNativeBuildJob("Rebuilding " + t, "==> Rebuild " + t)) return;
                        try
                        {
                            CleanNativeCMakeTarget(t, clearOutput: false);
                            BuildNativeCMakeTarget(t, clearOutput: false);
                        }
                        finally { EndNativeBuildJob(_statusLabel != null ? _statusLabel.Text : "Rebuild finished"); }
                    }));
                if (cleanMenu != null)
                    cleanMenu.DropDownItems.Add(new ToolStripMenuItem(t, null, delegate
                    {
                        if (!BeginNativeBuildJob("Cleaning " + t, "==> Clean " + t)) return;
                        try { CleanNativeCMakeTarget(t, clearOutput: false); }
                        finally { EndNativeBuildJob("Cleaned " + t); }
                    }));
            }
        }

        /// <summary>A Build-menu child by its control Name (survives menu text changes).</summary>
        private ToolStripMenuItem BuildMenuItemByName(string name)
        {
            if (buildTopMenuItem == null) return null;
            foreach (ToolStripItem it in buildTopMenuItem.DropDownItems)
            {
                ToolStripMenuItem m = it as ToolStripMenuItem;
                if (m != null && string.Equals(m.Name, name, StringComparison.Ordinal)) return m;
            }
            return null;
        }

        /// <summary>Build (if needed) then run ONE target's real executable, for the currently-open
        /// primary native CMake project. Mirrors RunLinkedCMakeTarget/_linkedCMakeFolder.</summary>
        internal HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult RunNativeCMakeTarget(string targetName)
        {
            // Run = build + execute, both pumped, so it takes the same busy gate as the Build
            // commands: only Stop Execution stays live, and the Output pane starts empty.
            if (!BeginNativeBuildJob("Running " + targetName, "==> Run " + targetName)) return null;
            try { return RunNativeCMakeTargetCore(targetName); }
            finally { EndNativeBuildJob(_statusLabel != null ? _statusLabel.Text : "Run finished"); }
        }

        private HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult RunNativeCMakeTargetCore(string targetName)
        {
            var build = BuildNativeCMakeTarget(targetName, clearOutput: false);
            if (build == null || !build.Succeeded) return build;

            string buildDir = CMakeBuildDirectory(_currentCMakeFolder);
            string executable = FindCMakeExecutable(buildDir, targetName);
            if (executable == null)
            {
                var missing = new HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult
                {
                    Phase = "run",
                    Reason = "Built executable for target '" + targetName + "' was not found.",
                };
                DumpCMakeRunResult("run", missing);
                return missing;
            }

            if (_statusLabel != null) _statusLabel.Text = "Running " + Path.GetFileName(executable) + " ...";
            string gccBin = ActiveCMakeToolchain() == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Gcc
                ? Path.GetDirectoryName(ActiveGccCppCompiler()) : null;
            var run = RunCMakePhasePumped(() => HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.RunExecutable(
                executable, Path.GetDirectoryName(executable), null, 120000, gccBin, AppendBuildOutputLine));
            DumpCMakeRunResult("run", run, streamed: true);
            if (_statusLabel != null)
                _statusLabel.Text = run.Succeeded ? "Run " + targetName + " succeeded" : "Run " + targetName + " failed";
            return run;
        }

        /// <summary>SS §linked-cmake-project step 3: configure+build ONE target of the linked project (not "the active project" — a linked project commonly has many targets, e.g. 9 debug_&lt;kernel&gt; executables sharing one static library).</summary>
        internal HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult BuildLinkedCMakeTarget(string targetName)
        {
            if (string.IsNullOrEmpty(_linkedCMakeFolder) || !Directory.Exists(_linkedCMakeFolder))
            {
                if (_statusLabel != null) _statusLabel.Text = "No linked CMake project is attached.";
                return null;
            }
            string buildDir = CMakeBuildDirectory(_linkedCMakeFolder);
            var toolchain = ActiveCMakeToolchain();
            AppendBuildOutputLine("==> cmake --build " + buildDir + " --target " + targetName);
            _buildInnerTabControl.SelectedTab = _buildOutputSubTab;
            ShowLinkedCMakeOutputTab();

            if (_statusLabel != null) _statusLabel.Text = "Configuring (cmake) " + _linkedCMakeFolder + " …";
            var stdArgs = HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.CreateLanguageStandardArguments(
                ActiveCxxStandard(), ActiveCudaStandard(), ProjectUsesCuda(_linkedCMakeFolder));
            string msvcEnv = ActiveMsvcEnvironmentScript();
            var cfg = RunCMakePhasePumped(() => HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.Configure(
                _linkedCMakeFolder, buildDir, "Ninja", stdArgs,
                120000, toolchain, msvcEnv, AppendBuildOutputLine));
            DumpCMakeRunResult("configure", cfg, streamed: true);
            if (!cfg.Available || cfg.ExitCode != 0)
            {
                if (_statusLabel != null) _statusLabel.Text = "Configure failed for linked CMake project.";
                return cfg;
            }

            if (_statusLabel != null) _statusLabel.Text = "Building target " + targetName + " …";
            var bld = RunCMakePhasePumped(() => HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.Build(
                buildDir, targetName, 180000, toolchain, msvcEnv, AppendBuildOutputLine));
            DumpCMakeRunResult("build", bld, streamed: true);
            if (_statusLabel != null)
                _statusLabel.Text = bld.Succeeded
                    ? "Build " + targetName + " succeeded"
                    : "Build " + targetName + " failed";
            return bld;
        }

        /// <summary>SS §linked-cmake-project step 3: build (if needed) then run ONE target's real executable.</summary>
        internal HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult RunLinkedCMakeTarget(string targetName)
        {
            var build = BuildLinkedCMakeTarget(targetName);
            if (build == null || !build.Succeeded) return build;

            string buildDir = CMakeBuildDirectory(_linkedCMakeFolder);
            string executable = FindCMakeExecutable(buildDir, targetName);
            if (executable == null)
            {
                var missing = new HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult
                {
                    Phase = "run",
                    Reason = "Built executable for target '" + targetName + "' was not found.",
                };
                DumpCMakeRunResult("run", missing);
                return missing;
            }

            if (_statusLabel != null) _statusLabel.Text = "Running " + Path.GetFileName(executable) + " ...";
            string gccBin = ActiveCMakeToolchain() == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Gcc
                ? Path.GetDirectoryName(ActiveGccCppCompiler()) : null;
            var run = RunCMakePhasePumped(() => HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.RunExecutable(
                executable, Path.GetDirectoryName(executable), null, 120000, gccBin, AppendBuildOutputLine));
            DumpCMakeRunResult("run", run, streamed: true);
            if (_statusLabel != null)
                _statusLabel.Text = run.Succeeded ? "Run " + targetName + " succeeded" : "Run " + targetName + " failed";
            return run;
        }

        // Bring the Output pane into view the same way BuildRunCoordinator's ShowBuildResultsTab
        // does (kept local since this is MainForm, not BuildRunCoordinator).
        private void ShowLinkedCMakeOutputTab()
        {
            if (!_buildResultsUndocked && _buildResultsTab != null)
                ShowTab("buildResults");
            else if (_floatingBuildResults != null)
                _floatingBuildResults.BringToFront();
        }

        private void DumpCMakeRunResult(string phase, HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult r,
            bool streamed = false)
        {
            if (r == null) { AppendBuildOutputLine("==> " + phase + ": no result"); return; }
            // streamed: the phase already wrote its lines live via onOutputLine — re-dumping
            // RawOutput here would print the whole log a second time.
            if (!streamed && !string.IsNullOrEmpty(r.RawOutput)) foreach (string ln in r.RawOutput.Replace("\r", "").Split('\n')) AppendBuildOutputLine(ln);
            if (!string.IsNullOrEmpty(r.Reason)) AppendBuildOutputLine(phase + ": " + r.Reason);
            AppendBuildOutputLine("==> " + phase + (r.Succeeded ? " succeeded" : " failed") +
                (r.Available ? " (exit " + r.ExitCode + ")" : " (tool unavailable)"));
        }

        // Runs one blocking cmake phase on a worker thread while pumping the UI message loop.
        // The Output tab fills through AppendBuildOutputLine (BeginInvoke), so the UI thread must
        // keep pumping for streamed lines to paint LIVE — these build flows are synchronous
        // UI-thread entry points, and without the pump every line would appear at once at the end.
        private static T RunCMakePhasePumped<T>(Func<T> work)
        {
            var task = System.Threading.Tasks.Task.Run(work);
            while (!task.IsCompleted)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(30);
            }
            return task.GetAwaiter().GetResult();
        }

        /// <summary>Guards one-time wiring of the Analysis Results list double-click → diagnostic navigation.</summary>
        private bool _cmakeListNavWired;

        /// <summary>
        /// FR-PM-03: import a folder containing a <c>CMakeLists.txt</c> as a native CUDA/C++ project and
        /// show it in the Solution Explorer — targets as first-class nodes, each expanding to its source
        /// files. This is the end-to-end path (folder → <c>CMakeProjectImporter</c> → project tree) that a
        /// user takes to open a CMake project. Returns the imported descriptor, or null on failure.
        /// </summary>
        internal object LoadCMakeFolderAsNativeProject(string folderPath)
        {
            if (_projectTreePanel == null) return null;
            // Language-services layer: the C++ provider owns import + model building
            // (CMakeProjectImporter → CMakeSolutionLoader); this method keeps only the
            // language-neutral GUI adoption below. Initial open was always synchronous under
            // WaitCursor, so block here; the provider runs its work via Task.Run internally.
            HelixPythonNamespace.Workspace.LanguageServices.WorkspaceOpenResult res =
                HelixPythonNamespace.Workspace.LanguageServices.LanguageWorkspaceRegistry
                    .For(HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp)
                    .OpenAsync(folderPath).GetAwaiter().GetResult();
            if (res == null || !res.Success || res.Solution == null)
            {
                if (_statusLabel != null) _statusLabel.Text = "CMake import failed: " + (res != null ? res.ErrorMessage : "unknown");
                return null;
            }

            HelixPythonNamespace.Workspace.Cuda.CMakeProjectDescriptor d =
                (HelixPythonNamespace.Workspace.Cuda.CMakeProjectDescriptor)res.NativeProject;

            // Adopt CMake as a real IDE workspace, not a side-channel tree. The workflow gate,
            // canvas, panels, title, terminal and persistence all key off _currentSolution.
            string cmakePath = Path.Combine(d.FolderPath, "CMakeLists.txt");
            // One ProjectModel PER CMAKE TARGET (not one lump project for the whole folder),
            // each populated with real Types/Functions/Members via CppTypeExtractor — see
            // CMakeSolutionLoader's doc comment. This is what makes every context-menu branch
            // already keyed on SolutionModel/ProjectModel/TypeModel/FunctionModel/MemberModel
            // (Rename, Rename Symbol, Delete, Save, Build Project, ...) apply to C++ nodes the
            // same way it already does for Python nodes, via LoadSolution below instead of the
            // old untagged, presentational LoadNativeCMakeProject tree.
            SolutionModel solution = res.Solution;
            _currentSolution = solution;
            _currentCMakeFolder = d.FolderPath;
            _currentCMakeProject = d;

            _layoutStore = new JsonLayoutMetadataStore(cmakePath);
            _layoutStore.Load();
            _projectionBuilder = new SolutionProjectionBuilder(
                _canvas.SceneGraph, _layoutStore, new GridAutoLayoutStrategy());
            _navigationState = new NavigationViewState(solution.Name);
            _projectionBuilder.BuildSolutionLevel(solution);
            _canvas.Viewport.Reset();
            _canvas.RequestFitOnFirstPaint(80f);
            _canvas.Invalidate();
            if (_designerCategoryCombo != null) _designerCategoryCombo.SelectedIndex = 0;
            UpdateBreadcrumb();

            _projectTreePanel.LoadSolution(solution);
            UpdateMenuWorkflowVisibility();
            // A newly opened native workspace must expose its primary workflow surfaces even when
            // stale per-user panel preferences previously hid them. Otherwise CMake appears to be
            // only a tree importer, with no editor or designer available.
            //
            // Output is deliberately NOT forced up here any more. It is a result tab
            // (PanelCoordinator.ResultTabs): it belongs on the strip once this project has actually
            // configured/built/run something, and both routes that produce that output raise it
            // themselves (BuildRunCoordinator.ShowBuildResultsTab / ShowLinkedCMakeOutputTab).
            // Forcing it on open put an empty Output pane over every freshly opened C++/CUDA
            // solution — which, after a Python session, read as the previous solution's tab coming
            // back (user report during testing). The "stale preferences" this guarded against are gone:
            // result tabs no longer write a visibility preference at all.
            if (_panelCoordinator != null)
            {
                _panelCoordinator.ShowTab("designer");
                _panelCoordinator.ShowTab("editor");
            }
            SelectWorkspaceTabAfterSolutionOpen();
            // Session restore — parity with OpenSolutionAsync: a native workspace used to always
            // open blank, forcing the user to re-browse the whole tree back to their files.
            RestoreLastOpenEditorFiles(cmakePath);
            if (_settingsManager != null)
                _projectTreePanel.ApplyExpandedPaths(_settingsManager.Settings.GetExpandedTreePaths(cmakePath));
            if (_aiTerminalPanel != null) _aiTerminalPanel.SetSolution(d.FolderPath, d.Name);
            _recentFilesManager.AddRecent(cmakePath);
            RefreshRecentFilesMenu();
            UpdateTitle();
            RaiseAddInSolutionChanged();
            if (_statusLabel != null)
                _statusLabel.Text = string.Format("Opened native CMake project '{0}' ({1}) — {2} target(s)",
                    d.Name, d.Kind, d.Targets.Count);
            return d;
        }

        /// <summary>
        /// FR-PM-04 (user-facing): build the currently-open native CMake project with the REAL cmake
        /// toolchain (<c>CMakeBuildRunner</c>) and route the ground-truth result to the Analysis Results
        /// panel — a per-diagnostic error list, the raw tool output, and a pass/fail summary. Returns the
        /// <c>CMakeRunResult</c> of the build (or the failed configure), or null if no project is open.
        /// </summary>
        internal object BuildActiveCMakeProject(bool clearOutput = true)
        {
            string folder = _currentCMakeFolder;
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            {
                if (_statusLabel != null) _statusLabel.Text = "No native CMake project is open to build.";
                return null;
            }

            bool autoFellBackToEmulator;
            var toolchain = EffectiveCMakeToolchain(out autoFellBackToEmulator);

            // Build on the bundled emulator: either the user chose it, or Auto found no cmake.
            // The notice is passed along rather than swallowed, so the Output pane says which
            // toolchain produced the result the user is about to read.
            if (toolchain == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Emulator)
                return CompileOrRunActiveEmulatorProject(false, autoFellBackToEmulator
                    ? "No real cmake/compiler toolchain detected — using Helix CUDA/C++ Emulator."
                    : null);

            if (toolchain == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Gcc &&
                ProjectUsesCuda(folder))
            {
                var incompatible = new HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult
                {
                    Phase = "configure",
                    Reason = "GCC is supported for C/C++ projects on Windows, but NVIDIA nvcc does not support MinGW GCC as its Windows host compiler. Select MSVC or Auto for this CUDA project."
                };
                PopulateCMakeOutput(incompatible, null, null, null);
                if (_statusLabel != null) _statusLabel.Text = "GCC cannot host nvcc on Windows; select MSVC.";
                return incompatible;
            }
            string configuredMsvc = _settingsManager?.Settings?.NativeMsvcEnvironmentScriptPath;
            if (toolchain != HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Gcc &&
                !string.IsNullOrWhiteSpace(configuredMsvc) && ActiveMsvcEnvironmentScript() == null)
            {
                var invalidMsvc = new HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult
                {
                    Phase = "configure",
                    Reason = "The configured MSVC environment script is invalid. Select an existing vcvars64.bat in Build > Tool Settings."
                };
                PopulateCMakeOutput(invalidMsvc, null, null, null);
                if (_statusLabel != null) _statusLabel.Text = "Invalid MSVC environment script; open Tool Settings.";
                return invalidMsvc;
            }
            string buildDir = CMakeBuildDirectory(folder);
            if (_statusLabel != null) _statusLabel.Text = "Configuring (cmake) " + folder + " …";
            System.Windows.Forms.Application.DoEvents();

            string gccCpp = ActiveGccCppCompiler();
            string gccC = HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.FindGccCCompiler(gccCpp);
            var gccProbe = toolchain == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Gcc
                ? HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.ProbeGccCppCompiler(gccCpp)
                : null;
            if (toolchain == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Gcc &&
                (gccCpp == null || gccC == null || !gccProbe.Usable))
            {
                var missingGcc = new HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult
                {
                    Phase = "configure",
                    Reason = gccProbe == null || string.IsNullOrEmpty(gccProbe.Reason)
                        ? "GNU gcc.exe/g++.exe were not found. Open Build > Tool Settings and select g++.exe."
                        : "GNU compiler validation failed: " + gccProbe.Reason
                };
                PopulateCMakeOutput(missingGcc, null, null, null);
                if (_statusLabel != null) _statusLabel.Text = "GCC toolchain not found; configure it in Tool Settings.";
                return missingGcc;
            }
            var extra = HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.CreateLanguageStandardArguments(
                ActiveCxxStandard(), ActiveCudaStandard(), ProjectUsesCuda(folder));
            if (toolchain == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Gcc)
            {
                extra.Add("-DCMAKE_C_COMPILER=\"" + gccC.Replace('\\', '/') + "\"");
                extra.Add("-DCMAKE_CXX_COMPILER=\"" + gccCpp.Replace('\\', '/') + "\"");
            }
            BeginStreamedCMakeOutput(clearOutput);
            string msvcEnv = ActiveMsvcEnvironmentScript();
            var cfg = RunCMakePhasePumped(() => HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.Configure(
                folder, buildDir, "Ninja", extra, 120000, toolchain, msvcEnv, AppendBuildOutputLine));
            HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult toShow = cfg;
            HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult bld = null;
            if (cfg.Available && cfg.ExitCode == 0)
            {
                if (_statusLabel != null) _statusLabel.Text = "Building (cmake --build) …";
                AppendBuildOutputLine("> cmake --build \"" + buildDir + "\"");
                bld = RunCMakePhasePumped(() => HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.Build(
                    buildDir, null, 180000, toolchain, msvcEnv, AppendBuildOutputLine));
                toShow = bld;
            }

            PopulateCMakeBuildResults(cfg, bld);
            PopulateCMakeOutput(cfg, bld, null, null, streamed: true);
            return toShow;
        }

        /// <summary>
        /// Build and execute the first concrete executable target in the active CMake workspace.
        /// The normal Run command calls this path and the real configure/build/program output is
        /// rendered in the user-facing Output tab.
        /// </summary>
        internal object RunActiveCMakeProject()
        {
            // The emulator compiles and runs in one pass -- there is no separate binary to launch
            // afterwards -- so it is checked before the build/launch split below.
            bool autoFellBackToEmulator;
            if (EffectiveCMakeToolchain(out autoFellBackToEmulator) ==
                HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Emulator)
                return CompileOrRunActiveEmulatorProject(true, autoFellBackToEmulator
                    ? "No real cmake/compiler toolchain detected — using Helix CUDA/C++ Emulator."
                    : null);

            var build = BuildActiveCMakeProject() as HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult;
            if (build == null || !build.Succeeded) return build;

            string targetName = null;
            if (_currentCMakeProject != null)
                foreach (HelixPythonNamespace.Workspace.Cuda.CMakeResolvedTarget target in _currentCMakeProject.Targets)
                    if (target.Kind == HelixPythonNamespace.Workspace.Cuda.CMakeTargetKind.Executable)
                    {
                        targetName = target.Name;
                        break;
                    }

            string buildDir = CMakeBuildDirectory(_currentCMakeFolder);
            string executable = FindCMakeExecutable(buildDir, targetName);
            if (executable == null)
            {
                var missing = new HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult
                {
                    Phase = "run",
                    Reason = string.IsNullOrEmpty(targetName)
                        ? "No executable target is defined by this CMake project."
                        : "Built executable for target '" + targetName + "' was not found.",
                };
                PopulateCMakeOutput(null, null, missing, executable);
                return missing;
            }

            if (_statusLabel != null) _statusLabel.Text = "Running " + Path.GetFileName(executable) + " ...";
            AppendBuildOutputLine("> " + executable);
            string gccBin = ActiveCMakeToolchain() == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Gcc
                ? Path.GetDirectoryName(ActiveGccCppCompiler()) : null;
            var run = RunCMakePhasePumped(() => HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.RunExecutable(
                executable, Path.GetDirectoryName(executable), null, 120000, gccBin, AppendBuildOutputLine));
            PopulateCMakeOutput(null, null, run, executable, streamed: true);
            if (_statusLabel != null)
                _statusLabel.Text = run.Succeeded
                    ? "Run " + targetName + " succeeded"
                    : "Run " + targetName + " failed";
            return run;
        }

        internal static string FindCMakeExecutable(string buildDir, string targetName)
        {
            if (!Directory.Exists(buildDir) || string.IsNullOrWhiteSpace(targetName)) return null;
            string expected = targetName + ".exe";
            try
            {
                foreach (string file in Directory.EnumerateFiles(buildDir, expected, SearchOption.AllDirectories))
                {
                    string normalized = file.Replace('/', '\\');
                    if (normalized.IndexOf("\\CMakeFiles\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    return file;
                }
                foreach (string file in Directory.EnumerateFiles(buildDir, targetName, SearchOption.AllDirectories))
                    return file;
            }
            catch { }
            return null;
        }

        private HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain ActiveCMakeToolchain()
        {
            string selected = _settingsManager?.Settings?.NativeCMakeToolchain ?? "Auto";
            HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain parsed;
            if (!Enum.TryParse(selected, true, out parsed))
                return HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Auto;
            // The managed CUDA/C++ emulator is HelixCudaCpp's product surface and does not ship
            // here, so a settings file carried over from another Helix product (or from the
            // pre-split combined build) cannot select it. Treat it as Auto.
            if (parsed == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Emulator)
                return HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Auto;
            return parsed;
        }

        /// <summary>
        /// Resolves the configured toolchain for the native (C/C++) side of a Python project — the
        /// build-via-C++ acceleration path and the output of the Convert-to-C/C++/CUDA commands.
        /// In the combined pre-split build "Auto" fell back to the managed emulator when no real
        /// cmake was found; HelixPython ships no emulator, so a missing cmake now surfaces as a real
        /// toolchain error instead. <paramref name="autoFellBackToEmulator"/> is retained as always
        /// false so the call sites keep their shape across the six product forks.
        /// </summary>
        /// <summary>
        /// The toolchain a build should actually use, and whether that choice was forced.
        ///
        /// <para>
        /// On <c>Auto</c> with no <c>cmake</c> on the machine, this falls back to the bundled
        /// CUDA/C++ emulator rather than failing. The fallback is reported, never silent: every
        /// caller passes the notice through to the Output pane, so a user whose build ran on the
        /// emulator is told so in the same breath as the result. A silent fallback would be worse
        /// than an error -- it would make "my kernel works" mean two different things depending on
        /// what happened to be installed.
        /// </para>
        /// <para>
        /// An EXPLICIT toolchain choice is never overridden. Asking for Msvc or Gcc and getting the
        /// emulator because cmake was missing would answer a question the user did not ask.
        /// </para>
        /// </summary>
        internal HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain EffectiveCMakeToolchain(out bool autoFellBackToEmulator)
        {
            var toolchain = ActiveCMakeToolchain();
            autoFellBackToEmulator = false;
            if (toolchain == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Auto &&
                HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.FindCMake() == null)
            {
                autoFellBackToEmulator = true;
                return HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Emulator;
            }
            return toolchain;
        }

        private object CompileOrRunActiveEmulatorProject(bool runProgram, string autoFallbackNotice = null)
        {
            var sourceFiles = new List<string>();
            string targetName = null;
            if (_currentCMakeProject != null)
                foreach (HelixPythonNamespace.Workspace.Cuda.CMakeResolvedTarget target in _currentCMakeProject.Targets)
                    if (target.Kind == HelixPythonNamespace.Workspace.Cuda.CMakeTargetKind.Executable)
                    {
                        targetName = target.Name;
                        foreach (string path in target.SourceFiles) sourceFiles.Add(path);
                        break;
                    }

            var includeDirectories = new List<string>();
            if (_currentCMakeProject != null && _currentCMakeProject.Metadata != null)
                foreach (string includeDirectory in _currentCMakeProject.Metadata.IncludeDirectories)
                {
                    if (string.IsNullOrWhiteSpace(includeDirectory)) continue;
                    string resolved = Path.IsPathRooted(includeDirectory)
                        ? includeDirectory
                        : Path.Combine(_currentCMakeProject.FolderPath, includeDirectory);
                    if (!includeDirectories.Contains(resolved)) includeDirectories.Add(resolved);
                }

            var emulated = HelixPythonNamespace.CudaEmulator.CudaCppEmulatorToolchain.CompileAndRun(
                sourceFiles, runProgram, "main", true,
                ActiveCxxStandard(), ActiveCudaStandard(), includeDirectories);
            var result = new HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult
            {
                Available = emulated.Compilation != null,
                Phase = runProgram ? "emulator-run" : "emulator-compile",
                ExitCode = emulated.ExitCode,
                Reason = emulated.FailureReason,
                RawOutput = emulated.Output ?? string.Empty
            };
            if (emulated.Compilation != null)
                foreach (HelixPythonNamespace.CudaVirtualCompiler.CudaCompileDiagnostic diagnostic
                    in emulated.Compilation.Diagnostics)
                {
                    result.Diagnostics.Add(new HelixPythonNamespace.CudaVirtualCompiler.CMakeDiagnostic
                    {
                        FilePath = diagnostic.FilePath,
                        Line = diagnostic.Span.Start.Line,
                        Column = diagnostic.Span.Start.Column,
                        IsError = diagnostic.Severity >=
                            HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error,
                        Message = diagnostic.Code + ": " + diagnostic.Message
                    });
                }

            PopulateCMakeBuildResults(result, null);
            if (_buildOutputTextBox != null)
            {
                _buildOutputTextBox.Clear();
                if (_mainTabControl != null && _buildResultsTab != null &&
                    _mainTabControl.TabPages.Contains(_buildResultsTab))
                    _mainTabControl.SelectedTab = _buildResultsTab;
                _buildInnerTabControl.SelectedTab = _buildOutputSubTab;
                if (!string.IsNullOrEmpty(autoFallbackNotice))
                {
                    AppendBuildOutputLine(autoFallbackNotice);
                    AppendBuildOutputLine("");
                }
                AppendBuildOutputLine("Native toolchain: " +
                    HelixPythonNamespace.CudaEmulator.CudaCppEmulatorToolchain.DisplayName + " " +
                    HelixPythonNamespace.CudaEmulator.CudaCppEmulatorToolchain.Version);
                AppendBuildOutputLine("Backend: managed CUDA/C++ AST compiler + deterministic CPU CUDA runtime");
                AppendBuildOutputLine("Language dialects: C++" + ActiveCxxStandard() +
                    " | CUDA C++" + ActiveCudaStandard());
                AppendBuildOutputLine("Project link: " + emulated.SourceFileCount +
                    " source translation unit(s) -> deterministic executable AST image");
                AppendBuildOutputLine("Local headers: " + emulated.IncludedFiles.Count +
                    " expanded (quoted include resolver)");
                AppendBuildOutputLine("Include search paths: " + includeDirectories.Count +
                    " configured CMake director" + (includeDirectories.Count == 1 ? "y" : "ies"));
                foreach (string includeDirectory in includeDirectories)
                    AppendBuildOutputLine("  [include] " + includeDirectory);
                AppendBuildOutputLine("Preprocessing limits: depth <= " +
                    HelixPythonNamespace.CudaEmulator.CudaCppEmulatorToolchain.MaxIncludeDepth +
                    ", headers <= " + HelixPythonNamespace.CudaEmulator.CudaCppEmulatorToolchain.MaxIncludedFiles +
                    ", expanded <= " + HelixPythonNamespace.CudaEmulator.CudaCppEmulatorToolchain.MaxExpandedHeaderCharacters +
                    " chars");
                AppendBuildOutputLine("Observed include depth: " + emulated.MaximumIncludeDepthReached +
                    ", expanded header chars: " + emulated.ExpandedHeaderCharacters);
                AppendBuildOutputLine("Conditional preprocessing: " + emulated.ConditionalDirectiveCount +
                    " directive(s), " + emulated.SkippedConditionalIncludes +
                    " inactive include(s), " + emulated.MacroIncludeCount + " macro include(s), " +
                    emulated.UnknownConditionalExpressionCount + " unknown expression(s), " +
                    emulated.ExpandedMacroTokenCount + " expanded macro token(s)");
                foreach (string header in emulated.IncludedFiles)
                    AppendBuildOutputLine("  [header] " + header);
                AppendBuildOutputLine("> helix-emulator compile --target \"" +
                    (targetName ?? "(none)") + "\" --std=c++" + ActiveCxxStandard() +
                    " --cuda-std=" + ActiveCudaStandard() +
                    " --sources " + emulated.SourceFileCount);
                foreach (HelixPythonNamespace.CudaEmulator.CudaCppTranslationUnitInfo unit
                    in emulated.TranslationUnits)
                    AppendBuildOutputLine("  [" + Path.GetExtension(unit.FilePath).TrimStart('.') +
                        "] " + unit.FilePath + " (" + unit.LineCount + " lines)");
                if (emulated.Compilation != null)
                {
                    AppendBuildOutputLine("Diagnostics: " + emulated.Compilation.ErrorCount() +
                        " error(s), " + emulated.Compilation.WarningCount() + " warning(s)");
                    foreach (HelixPythonNamespace.CudaVirtualCompiler.CudaCompileDiagnostic diagnostic
                        in emulated.Compilation.Diagnostics)
                        AppendBuildOutputLine(diagnostic.ToString());
                }
                AppendBuildOutputLine("==> emulator compile exit code " +
                    (emulated.CompileSucceeded ? "0 (success)" : "1 (failed)") +
                    " [" + emulated.CompileElapsedMs + " ms]");
                if (runProgram && emulated.CompileSucceeded)
                {
                    AppendBuildOutputLine("CUDA safety gate: " +
                        (emulated.SafetyGatePassed ? "PASS" : "REFUSED"));
                    if (!emulated.SafetyGatePassed)
                    {
                        if (!string.IsNullOrEmpty(emulated.FailureReason))
                            AppendBuildOutputLine("error: " + emulated.FailureReason);
                        if (_statusLabel != null) _statusLabel.Text =
                            "Emulator run refused by CUDA safety gate";
                        return result;
                    }
                    AppendBuildOutputLine("> helix-emulator run --entry main");
                    if (!string.IsNullOrEmpty(emulated.Output))
                        _buildOutputTextBox.AppendText(emulated.Output.Replace("\n", "\r\n"));
                    foreach (HelixPythonNamespace.CudaEmulator.KernelTimingSample kernel in emulated.Kernels)
                        AppendBuildOutputLine("kernel " + kernel.Name + " grid=(" +
                            kernel.Grid.X + "," + kernel.Grid.Y + "," + kernel.Grid.Z + ") block=(" +
                            kernel.Block.X + "," + kernel.Block.Y + "," + kernel.Block.Z +
                            ") threads=" + kernel.TotalThreads +
                            " cpu-emulation=" + kernel.ElapsedNs + " ns");
                    AppendBuildOutputLine("==> emulator run exit code " + emulated.ExitCode +
                        (emulated.RunSucceeded ? " (success)" : " (failed)") +
                        " [" + emulated.RunElapsedMs + " ms]");
                }
                if (!string.IsNullOrEmpty(emulated.FailureReason))
                    AppendBuildOutputLine("error: " + emulated.FailureReason);
            }
            if (_statusLabel != null)
                _statusLabel.Text = (runProgram ? "Emulator run " : "Emulator compile ") +
                    (emulated.ExitCode == 0 ? "succeeded" : "failed") +
                    (string.IsNullOrEmpty(targetName) ? string.Empty : ": " + targetName);
            return result;
        }

        /// <summary>Always false in HelixPython: there is no managed emulator, so every native run
        /// produces a real compiled executable that the HelixPython Terminal can launch.</summary>
        internal bool IsActiveCMakeProjectUsingEmulator()
        {
            return false;
        }

        private string ActiveGccCppCompiler()
        {
            return HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.FindGccCppCompiler(
                _settingsManager?.Settings?.NativeGccCppCompilerPath);
        }

        private string ActiveMsvcEnvironmentScript()
        {
            return HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.FindMsvcEnvironmentScript(
                _settingsManager?.Settings?.NativeMsvcEnvironmentScriptPath);
        }

        private static bool ProjectUsesCuda(string projectFolder)
        {
            try
            {
                string cmake = File.ReadAllText(Path.Combine(projectFolder, "CMakeLists.txt"));
                return cmake.IndexOf("CUDA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    Directory.EnumerateFiles(projectFolder, "*.cu", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }

        internal string CMakeBuildDirectory(string projectFolder)
        {
            string variant = ActiveCMakeToolchain().ToString().ToLowerInvariant() +
                "-cxx" + ActiveCxxStandard() + "-cuda" + ActiveCudaStandard();
            string colocated = Path.Combine(projectFolder, "cmake-build-helixstudio-" + variant);
            // nvcc/cl.exe intermediate files (TryCompile scratch dirs, device-link tmpxft_*
            // temporaries) routinely add 60-90+ chars of nested depth below the build root.
            // A deeply nested project folder can push the full path past Windows' legacy
            // ~260-char MAX_PATH, which cl.exe still enforces even with long-path support
            // enabled system-wide. Fall back to a short, stable out-of-tree location keyed
            // by a hash of the project path so the common (short-path) case is unaffected.
            if (colocated.Length > 140)
            {
                string shortId = ShortHash(Path.GetFullPath(projectFolder));
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HelixPython", "cmake-builds", shortId + "-" + variant);
            }
            return colocated;
        }

        private static string ShortHash(string s)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
                return BitConverter.ToString(hash, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private string ActiveCxxStandard()
        {
            return HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.NormalizeCxxStandard(
                _settingsManager?.Settings?.NativeCxxStandard, "20");
        }

        private string ActiveCudaStandard()
        {
            return HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.NormalizeCxxStandard(
                _settingsManager?.Settings?.NativeCudaStandard, "20");
        }

        // Writes the toolchain banner + configure command line that precede a configure phase's
        // output. Shared by the buffered path (PopulateCMakeOutput) and the streamed path
        // (BeginStreamedCMakeOutput, which must emit it BEFORE the live cmake lines arrive).
        private void EmitCMakeConfigureHeader()
        {
            AppendBuildOutputLine("Native toolchain: " + ActiveCMakeToolchain());
            AppendBuildOutputLine("Language dialects: C++" + ActiveCxxStandard() +
                (ProjectUsesCuda(_currentCMakeFolder)
                    ? " | CUDA C++" + ActiveCudaStandard() : string.Empty));
            if (ActiveCMakeToolchain() == HelixPythonNamespace.CudaVirtualCompiler.CMakeHostToolchain.Gcc)
            {
                var probe = HelixPythonNamespace.CudaVirtualCompiler.CMakeBuildRunner.ProbeGccCppCompiler(
                    ActiveGccCppCompiler());
                AppendBuildOutputLine("C++ compiler: " + probe.CompilerPath +
                    " (GNU " + probe.Version + ", target " + probe.Target + ")");
            }
            else
                AppendBuildOutputLine("MSVC/CUDA environment: " + ActiveMsvcEnvironmentScript());
            AppendBuildOutputLine("> cmake -S \"" + _currentCMakeFolder + "\" -B \""
                + CMakeBuildDirectory(_currentCMakeFolder) + "\" -G Ninja" +
                " -DCMAKE_CXX_STANDARD=" + ActiveCxxStandard() +
                (ProjectUsesCuda(_currentCMakeFolder)
                    ? " -DCMAKE_CUDA_STANDARD=" + ActiveCudaStandard() : string.Empty));
        }

        // Resets the Output pane and writes the configure header ahead of a STREAMED configure,
        // so the live cmake lines land under the banner instead of in an unlabeled empty pane.
        private void BeginStreamedCMakeOutput(bool clearOutput = true)
        {
            if (_buildOutputTextBox == null) return;
            if (clearOutput) _buildOutputTextBox.Clear();
            _buildInnerTabControl.SelectedTab = _buildOutputSubTab;
            // Bring the Output pane onto the strip for the run that is about to stream into it.
            // Selecting the inner sub-tab is not enough when the top-level Output tab itself is
            // not shown — configure/build then streamed into a pane nobody could see. Output is a
            // result tab (PanelCoordinator.ResultTabs), so it appears when a run produces output
            // rather than being parked on the strip from the moment a solution opens.
            ShowLinkedCMakeOutputTab();
            EmitCMakeConfigureHeader();
        }

        private void PopulateCMakeOutput(
            HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult cfg,
            HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult bld,
            HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult run,
            string executable,
            bool streamed = false)
        {
            if (_buildOutputTextBox == null) return;
            // streamed: the header + phase output already went to the pane live; only the
            // per-phase Reason/exit-code summaries and the label/tab bookkeeping remain.
            if (cfg != null && !streamed) _buildOutputTextBox.Clear();
            _buildInnerTabControl.SelectedTab = _buildOutputSubTab;
            // Also raise the top-level Output tab. The non-streamed callers are the ones that
            // report a refusal without ever starting a build (no toolchain, GCC-cannot-host-nvcc,
            // bad MSVC script); their explanation went into a pane that was never shown, so the
            // command looked like it did nothing at all.
            ShowLinkedCMakeOutputTab();

            if (cfg != null)
            {
                if (!streamed) EmitCMakeConfigureHeader();
                AppendCMakePhaseOutput(cfg, streamed);
            }
            if (bld != null)
            {
                if (!streamed)
                    AppendBuildOutputLine("> cmake --build \"" + CMakeBuildDirectory(_currentCMakeFolder) + "\"");
                AppendCMakePhaseOutput(bld, streamed);
            }
            if (run != null)
            {
                if (!streamed) AppendBuildOutputLine("> " + (executable ?? "<no executable>"));
                AppendCMakePhaseOutput(run, streamed);
            }

            var last = run ?? bld ?? cfg;
            bool ok = last != null && last.Succeeded;
            if (_buildSummaryLabel != null)
                _buildSummaryLabel.Text = ok
                    ? "CMake " + last.Phase + " succeeded"
                    : "CMake " + (last != null ? last.Phase : "operation") + " failed";
            if (!_buildResultsUndocked && _buildResultsTab != null &&
                _mainTabControl.TabPages.Contains(_buildResultsTab))
                _mainTabControl.SelectedTab = _buildResultsTab;
            else if (_floatingBuildResults != null)
                _floatingBuildResults.BringToFront();
        }

        private void AppendCMakePhaseOutput(HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult result,
            bool streamed = false)
        {
            if (result == null) return;
            if (!streamed && !string.IsNullOrWhiteSpace(result.RawOutput))
                foreach (string line in result.RawOutput.Replace("\r", "").Split('\n'))
                    AppendBuildOutputLine(line);
            if (!string.IsNullOrWhiteSpace(result.Reason))
                AppendBuildOutputLine(result.Reason);
            AppendBuildOutputLine("==> " + result.Phase + " exit code " + result.ExitCode
                + (result.Succeeded ? "  (success)" : "  (failed)"));
            AppendBuildOutputLine("");
        }

        /// <summary>Build menu handler: prompt for a folder with a CMakeLists.txt and open it as a native project (FR-PM-03).</summary>
        internal void OnOpenCMakeFolder(object sender, EventArgs e)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1", StringComparison.Ordinal))
                return; // no modal dialogs in headless mode
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select a folder containing a CMakeLists.txt";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!File.Exists(Path.Combine(dlg.SelectedPath, "CMakeLists.txt")))
                {
                    MessageBox.Show(this, "No CMakeLists.txt found in the selected folder.",
                        "Open CMake Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                LoadCMakeFolderAsNativeProject(dlg.SelectedPath);
            }
        }

        /// <summary>Build menu handler: build the open native CMake project with the real cmake toolchain (FR-PM-04).</summary>
        internal void OnBuildCMakeProject(object sender, EventArgs e)
        {
            if (!EnsureNativeCMakeOpen("Build Solution")) return;
            // No header: BuildActiveCMakeProject's own BeginStreamedCMakeOutput emits the
            // configure banner (and clears again, harmlessly).
            if (!BeginNativeBuildJob("Building solution", null)) return;
            try { BuildActiveCMakeProject(); }
            finally { EndNativeBuildJob(_statusLabel != null ? _statusLabel.Text : "Build finished"); }
        }

        /// <summary>
        /// FR-PM-04: mark real cmake build errors as red gutter markers + squiggles on the offending lines.
        /// Any already-open editor whose file matches a diagnostic is marked in place; if the first error's
        /// file isn't open, it is opened at the error line (VS-style "jump to first error").
        /// </summary>
        private void MarkCMakeDiagnosticsInEditors(
            List<HelixPythonNamespace.CudaVirtualCompiler.CMakeDiagnostic> diags)
        {
            if (_editorInnerTabControl == null || diags == null) return;

            // Group diagnostics by absolute file path.
            var byFile = new Dictionary<string, List<HelixPythonNamespace.CudaVirtualCompiler.CMakeDiagnostic>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var d in diags)
            {
                if (string.IsNullOrEmpty(d.FilePath)) continue;
                string key; try { key = Path.GetFullPath(d.FilePath); } catch { key = d.FilePath; }
                if (!byFile.TryGetValue(key, out var list)) { list = new List<HelixPythonNamespace.CudaVirtualCompiler.CMakeDiagnostic>(); byFile[key] = list; }
                list.Add(d);
            }

            var markedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TabPage tp in _editorInnerTabControl.TabPages)
            {
                var etp = tp as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp == null || string.IsNullOrEmpty(etp.FilePath)) continue;
                string key; try { key = Path.GetFullPath(etp.FilePath); } catch { key = etp.FilePath; }
                if (byFile.TryGetValue(key, out var list))
                {
                    etp.Editor.SetBuildErrors(ToBuildErrorMarks(list));
                    markedFiles.Add(key);
                }
                else
                {
                    // No diagnostics for this file in THIS build: clear whatever a previous
                    // build painted. Skipping instead of clearing left stale red gutter dots +
                    // squiggles on lines whose errors were long since fixed (or that only ever
                    // failed under a broken environment), surviving every green build after.
                    etp.Editor.SetBuildErrors(
                        new List<HelixPythonNamespace.Editor.PythonEditorControl.BuildErrorMark>());
                }
            }

            // Jump to the first error whose file isn't already open+marked.
            foreach (var d in diags)
            {
                if (!d.IsError || string.IsNullOrEmpty(d.FilePath) || !File.Exists(d.FilePath)) continue;
                string key; try { key = Path.GetFullPath(d.FilePath); } catch { key = d.FilePath; }
                if (markedFiles.Contains(key)) continue;
                OpenInEditor(d.FilePath, d.Line, true, d.Column);
                MarkOpenEditorErrors(key, byFile[key]);
                break;
            }
        }

        private void MarkOpenEditorErrors(string fullPath,
            List<HelixPythonNamespace.CudaVirtualCompiler.CMakeDiagnostic> list)
        {
            if (_editorInnerTabControl == null) return;
            foreach (TabPage tp in _editorInnerTabControl.TabPages)
            {
                var etp = tp as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp == null || string.IsNullOrEmpty(etp.FilePath)) continue;
                string key; try { key = Path.GetFullPath(etp.FilePath); } catch { key = etp.FilePath; }
                if (!string.Equals(key, fullPath, StringComparison.OrdinalIgnoreCase)) continue;
                etp.Editor.SetBuildErrors(ToBuildErrorMarks(list));
                return;
            }
        }

        private static List<HelixPythonNamespace.Editor.PythonEditorControl.BuildErrorMark> ToBuildErrorMarks(
            List<HelixPythonNamespace.CudaVirtualCompiler.CMakeDiagnostic> list)
        {
            var marks = new List<HelixPythonNamespace.Editor.PythonEditorControl.BuildErrorMark>();
            foreach (var d in list)
                if (d.IsError)
                    marks.Add(new HelixPythonNamespace.Editor.PythonEditorControl.BuildErrorMark
                    { Line = d.Line, Column = d.Column, Message = "cmake: " + d.Message });
            return marks;
        }

        private void OnCMakeDiagnosticActivated(object sender, EventArgs e)
        {
            if (_analysisResultsListView == null || _analysisResultsListView.SelectedItems.Count == 0) return;
            var d = _analysisResultsListView.SelectedItems[0].Tag as HelixPythonNamespace.CudaVirtualCompiler.CMakeDiagnostic;
            if (d == null || string.IsNullOrEmpty(d.FilePath) || !File.Exists(d.FilePath)) return;
            OpenInEditor(d.FilePath, d.Line, true, d.Column);
            string key; try { key = Path.GetFullPath(d.FilePath); } catch { key = d.FilePath; }
            MarkOpenEditorErrors(key, new List<HelixPythonNamespace.CudaVirtualCompiler.CMakeDiagnostic> { d });
        }

        private void PopulateCMakeBuildResults(
            HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult cfg,
            HelixPythonNamespace.CudaVirtualCompiler.CMakeRunResult bld)
        {
            var diags = new List<HelixPythonNamespace.CudaVirtualCompiler.CMakeDiagnostic>();
            if (cfg != null) diags.AddRange(cfg.Diagnostics);
            if (bld != null) diags.AddRange(bld.Diagnostics);

            if (_analysisResultsListView != null)
            {
                if (!_cmakeListNavWired)
                {
                    _analysisResultsListView.ItemActivate += OnCMakeDiagnosticActivated;
                    _cmakeListNavWired = true;
                }
                _analysisResultsListView.BeginUpdate();
                _analysisResultsListView.Items.Clear();
                foreach (var d in diags)
                {
                    var it = new ListViewItem(d.IsError ? "Error" : "Warning");
                    it.SubItems.Add("cmake-build");
                    it.SubItems.Add(System.IO.Path.GetFileName(d.FilePath) + "(" + d.Line + ")");
                    it.SubItems.Add(d.Message);
                    it.ForeColor = d.IsError ? System.Drawing.Color.Firebrick : System.Drawing.Color.DarkGoldenrod;
                    it.Tag = d;   // double-click navigates to file+line (see OnCMakeDiagnosticActivated)
                    _analysisResultsListView.Items.Add(it);
                }
                _analysisResultsListView.EndUpdate();
            }

            // Squiggle the error lines in any matching open editor, and open the first error file so the
            // user lands on the offending line (VS-style) with the red gutter marker + underline.
            MarkCMakeDiagnosticsInEditors(diags);

            var last = bld ?? cfg;
            if (_analysisOutputTextBox != null)
            {
                var sb = new StringBuilder();
                sb.AppendLine("cmake " + (cfg != null ? cfg.CMakeVersion : "") + "   (real toolchain)");
                if (cfg != null) sb.AppendLine("configure: exit=" + cfg.ExitCode + (cfg.ExitCode == 0 ? "  OK" : "  FAILED"));
                if (bld != null) sb.AppendLine("build:     exit=" + bld.ExitCode + (bld.ExitCode == 0 ? "  OK" : "  FAILED"));
                sb.AppendLine(new string('-', 60));
                if (last != null && !string.IsNullOrEmpty(last.RawOutput)) sb.AppendLine(last.RawOutput);
                _analysisOutputTextBox.Text = sb.ToString();
            }

            int errs = 0; foreach (var d in diags) if (d.IsError) errs++;
            bool ok = last != null && last.Succeeded;
            if (_analysisSummaryLabel != null)
                _analysisSummaryLabel.Text = ok
                    ? string.Format("CMake build succeeded ({0})", last.Phase)
                    : string.Format("CMake {0} FAILED — {1} error(s)", last != null ? last.Phase : "run", errs);

            if (_mainTabControl != null && _analysisResultsTab != null)
            { try { _mainTabControl.SelectedTab = _analysisResultsTab; } catch { } }
        }

        // ---- Category Filter (single canvas) ----
        //
        // The Interfaces/Abstract Classes/General Classes sub-tabs are gone — one shared canvas/
        // builder (_canvas / _projectionBuilder) now serves All + the three category filters via
        // the "Show:" combo (see OnDesignerCategoryFilterChanged / RebuildDesignerCanvasForCurrentFilter
        // above). RebuildCategoryTabs and SyncCategoryTabsToLevel are kept as thin compatibility
        // wrappers so call sites written for the old per-tab-builder design still work unchanged.

        private void OnDesignerInnerTabChanged(object sender, EventArgs e)
        {
            // The floating Tools palette belongs to the class-diagram canvas only. GUI/Form
            // Designer documents share this host tab but are a different surface and must not
            // be covered by the palette.
            UpdateToolsPaletteVisibility();
            // Rebind minimap to whichever canvas is now active (still meaningful: promoted mode
            // can put other top-level canvases, e.g. the Cuda designer, in front of this one).
            if (_minimap != null && !_minimap.IsDisposed)
            {
                _minimap.SetCanvas(GetActiveCanvas() ?? _canvas);
            }
        }

        /// <summary>
        /// Re-applies the checked-project set to the shared builder when a non-All filter is
        /// active. All-mode call sites already rebuild _projectionBuilder themselves immediately
        /// before/after calling this, so this only has visible work to do for a category filter.
        /// </summary>
        internal void RebuildCategoryTabs(List<string> checkedProjects)
        {
            if (_projectionBuilder == null || _designerCategoryCombo == null) return;
            if (_projectionBuilder.CategoryFilter == CategoryFilterMode.All) return;
            _projectionBuilder.SetVisibleProjects(checkedProjects);
            RebuildDesignerCanvasForCurrentFilter();
        }

        /// <summary>Re-syncs the shared canvas to the current CategoryFilter + navigation level.</summary>
        internal void SyncCategoryTabsToLevel() => RebuildDesignerCanvasForCurrentFilter();

        // ---- R6: Level-Aware Context Menus ----

        /// <summary>
        /// R6: Create a context menu appropriate for the current navigation level.
        /// Called when opening the canvas context menu.
        /// </summary>
        private void RebuildCanvasContextMenuForLevel(ContextMenuStrip menu)
        {
            if (menu == null) return;
            menu.Items.Clear();

            if (_navigationState == null || _currentSolution == null)
            {
                // Fallback: type-level menu
                BuildTypeLevelContextMenu(menu);
                return;
            }

            switch (_navigationState.Level)
            {
                case NavigationLevel.Solution:
                    BuildSolutionLevelContextMenu(menu);
                    break;

                case NavigationLevel.Project:
                    BuildProjectLevelContextMenu(menu);
                    break;

                case NavigationLevel.Namespace:
                    BuildNamespaceLevelContextMenu(menu);
                    break;

                case NavigationLevel.Type:
                    BuildTypeLevelContextMenu(menu);
                    break;
            }
        }

        private void BuildSolutionLevelContextMenu(ContextMenuStrip menu)
        {
            // R6.1: sln-level context menu
            menu.Items.Add("Rename Solution", null, OnRenameSolution);
        }

        private void BuildProjectLevelContextMenu(ContextMenuStrip menu)
        {
            // R6.2: csproj-level context menu
            DiagramCanvas activeCanvas = GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = GetProjectionBuilderForCanvas(activeCanvas);

            // Check if right-clicked on a project node
            PointF worldPos = activeCanvas.LastRightClickWorldPosition;
            float tolerance = 5f / activeCanvas.Viewport.Zoom;
            NodeVisual hitNode = activeCanvas.SceneGraph.HitTestNode(worldPos, tolerance);

            if (hitNode != null && hitNode.ContainerData != null)
            {
                string projectName = hitNode.ContainerData.ScopeKey;
                menu.Items.Add(string.Format("Rename Project: {0}", projectName), null,
                    delegate { RenameProjectByName(projectName); });
                menu.Items.Add(string.Format("Edit Project: {0}", projectName), null,
                    delegate { EditProjectByName(projectName); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(string.Format("Add Project Reference to {0}...", projectName), null,
                    delegate { AddProjectReferenceToProject(projectName); });
                menu.Items.Add(string.Format("Manage NuGet Packages for {0}...", projectName), null,
                    delegate { ManagePackagesForProject(projectName); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(string.Format("Delete Project: {0}", projectName), null,
                    delegate { DeleteProjectByName(projectName); });
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add("Add New Project...", null, delegate { OnTreeAddProject(this, new TreeOperationEventArgs(null, null)); });
        }

        private ProjectModel FindProjectByName(string projectName)
        {
            if (_currentSolution == null) return null;
            foreach (ProjectModel project in _currentSolution.Projects)
            {
                if (string.Equals(project.Name, projectName, StringComparison.OrdinalIgnoreCase))
                    return project;
            }
            return null;
        }

        private void MoveTreeProject(TreeOperationEventArgs args, int offset)
        {
            ProjectModel project = args != null ? args.Item as ProjectModel : null;
            if (project == null || _currentSolution == null) return;
            try
            {
                bool moved;
                using (_fileWatcher?.SuspendWhileEditing())
                    moved = _projectService.MoveProject(_currentSolution, project, offset);
                if (!moved) return;
                _projectTreePanel.LoadSolutionPreservingExpansion(_currentSolution);
                _statusLabel.Text = string.Format("Moved {0} {1}", project.Name,
                    offset < 0 ? "up" : "down");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to reorder the project: " + ex.Message,
                    "Move Project", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AddProjectReferenceToProject(string projectName)
        {
            ProjectModel project = FindProjectByName(projectName);
            if (project == null) return;

            using (var dialog = new ReferenceManagerForm(_currentSolution, project))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                int added = 0;
                foreach (ProjectModel referenced in dialog.SelectedProjects)
                {
                    try
                    {
                        _projectService.AddProjectReference(project, referenced);
                        // Persist in the referencing project's OWN manifest — the same element
                        // its loader (and its real toolchain) consumes, so the reference
                        // survives reload. The in-memory edge alone evaporated on close, which
                        // made the command look like it did nothing.
                        using (_fileWatcher?.SuspendWhileEditing())
                            PersistProjectReference(project, referenced);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            string.Format("Failed to add reference to {0}: {1}", referenced.Name, ex.Message),
                            "Add Reference Failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                foreach (var pkg in dialog.PendingPackages)
                {
                    try
                    {
                        using (_fileWatcher?.SuspendWhileEditing())
                            PersistPackageReference(project, pkg.Key, pkg.Value);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            string.Format("Failed to add package '{0}': {1}", pkg.Key, ex.Message),
                            "Add Reference Failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                if (added > 0)
                {
                    _statusLabel.Text = string.Format("Added {0} reference(s) to {1}", added, project.Name);
                    _projectTreePanel.LoadSolutionPreservingExpansion(_currentSolution);
                }
            }
        }

        /// <summary>Persist one project-to-project reference in the referencing project's own
        /// manifest, in that language's native form. False when the language/shape has no
        /// persistable form (the in-memory edge still exists for the session).</summary>
        internal static bool PersistProjectReference(ProjectModel project, ProjectModel referenced)
        {
            switch (project.Language)
            {
                case HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp:
                    {
                        string cmake = Path.Combine(project.ProjectDirectory ?? string.Empty, "CMakeLists.txt");
                        if (!File.Exists(cmake)) return false;
                        return HelixPythonNamespace.Workspace.Cuda.CMakeReferenceWriter.AddTargetLink(cmake, referenced.Name);
                    }

                default:   // Python: the full list in [tool.helixstudio].references (PSM-41…46)
                    if (!string.IsNullOrEmpty(project.FilePath) &&
                        project.FilePath.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase))
                    {
                        HelixPythonNamespace.Workspace.Python.PyProjectReferenceWriter.SetReferences(
                            project.FilePath, project.ProjectReferences.Select(r => r.Name));
                        return true;
                    }
                    return false;
            }
        }

        // "Remove Reference" on a Dependencies child: drop the in-memory edge AND its persisted
        // form (references were add-only before — reported as "can add but can't remove").
        internal void OnTreeRemoveProjectReference(object sender, TreeOperationEventArgs e)
        {
            // A DANGLING reference (its project was deleted) has no ProjectModel to pass around,
            // only a name — but it is exactly the case the user most needs to remove, so it gets
            // its own path straight to the manifest writer.
            var missing = e != null ? e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.UnresolvedReferenceGroup : null;
            if (missing != null && missing.Owner != null && !string.IsNullOrEmpty(missing.Name))
            {
                try
                {
                    bool removed;
                    using (_fileWatcher?.SuspendWhileEditing())
                        removed = RemovePersistedProjectReferenceByName(missing.Owner, missing.Name);
                    missing.Owner.UnresolvedProjectReferences.Remove(missing.Name);
                    _projectTreePanel.LoadSolutionPreservingExpansion(_currentSolution);
                    _statusLabel.Text = removed
                        ? string.Format("Removed dangling reference {0} → {1}", missing.Owner.Name, missing.Name)
                        : string.Format("Could not remove '{0}' from {1}'s manifest", missing.Name, missing.Owner.Name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Failed to remove the reference: " + ex.Message,
                        "Remove Reference", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            var grp = e != null ? e.Item as HelixPythonNamespace.UI.Controls.ProjectTreePanel.ProjectReferenceGroup : null;
            if (grp == null || grp.Owner == null || grp.Target == null) return;
            try
            {
                _projectService.RemoveProjectReference(grp.Owner, grp.Target);
                using (_fileWatcher?.SuspendWhileEditing())
                    RemovePersistedProjectReference(grp.Owner, grp.Target);
                _projectTreePanel.LoadSolutionPreservingExpansion(_currentSolution);
                _statusLabel.Text = string.Format("Removed reference {0} → {1}", grp.Owner.Name, grp.Target.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to remove the reference: " + ex.Message,
                    "Remove Reference", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Remove a reference from the manifest knowing only its NAME — the dangling case, where
        /// the referenced project no longer exists so there is no ProjectModel to describe it.
        /// </summary>
        internal static bool RemovePersistedProjectReferenceByName(ProjectModel project, string referencedName)
        {
            if (project == null || string.IsNullOrEmpty(referencedName)) return false;
            string dir = project.ProjectDirectory ?? string.Empty;
            switch (project.Language)
            {
                case HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp:
                    return HelixPythonNamespace.Workspace.Cuda.CMakeReferenceWriter.RemoveTargetLink(
                        Path.Combine(dir, "CMakeLists.txt"), referencedName);
                default:
                    if (!string.IsNullOrEmpty(project.FilePath) &&
                        project.FilePath.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase))
                    {
                        var kept = new List<string>();
                        foreach (ProjectModel r in project.ProjectReferences)
                            if (r != null && !string.Equals(r.Name, referencedName, StringComparison.Ordinal))
                                kept.Add(r.Name);
                        HelixPythonNamespace.Workspace.Python.PyProjectReferenceWriter.SetReferences(project.FilePath, kept);
                        return true;
                    }
                    return false;
            }
        }

        /// <summary>Undo <see cref="PersistProjectReference"/> in the language's native form.</summary>
        internal static bool RemovePersistedProjectReference(ProjectModel project, ProjectModel referenced)
        {
            switch (project.Language)
            {
                case HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp:
                    return HelixPythonNamespace.Workspace.Cuda.CMakeReferenceWriter.RemoveTargetLink(
                        Path.Combine(project.ProjectDirectory ?? string.Empty, "CMakeLists.txt"), referenced.Name);
                default:   // Python: rewrite the (already-shrunk) full list
                    if (!string.IsNullOrEmpty(project.FilePath) &&
                        project.FilePath.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase))
                    {
                        HelixPythonNamespace.Workspace.Python.PyProjectReferenceWriter.SetReferences(
                            project.FilePath, project.ProjectReferences.Select(r => r.Name));
                        return true;
                    }
                    return false;
            }
        }

        /// <summary>Persist one package reference in the language's native manifest form.</summary>
        internal static void PersistPackageReference(ProjectModel project, string name, string version)
        {
            switch (project.Language)
            {
                case HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp:
                    throw new InvalidOperationException("Native C++/CUDA projects have no package ecosystem here.");
                default:   // Python: PEP 508 requirement
                    HelixPythonNamespace.Workspace.Python.PyprojectDependencyService.Add(
                        project.FilePath, string.IsNullOrEmpty(version) ? name : name + "==" + version);
                    break;
            }
        }

        private void ManagePackagesForProject(string projectName)
        {
            ProjectModel project = FindProjectByName(projectName);
            if (project == null) return;
            OnTreeManagePackages(this, new TreeOperationEventArgs(null, project));
        }

        private void BuildNamespaceLevelContextMenu(ContextMenuStrip menu)
        {
            // R6.3: namespace-level context menu
            DiagramCanvas activeCanvas = GetActiveCanvas();

            PointF worldPos = activeCanvas.LastRightClickWorldPosition;
            float tolerance = 5f / activeCanvas.Viewport.Zoom;
            NodeVisual hitNode = activeCanvas.SceneGraph.HitTestNode(worldPos, tolerance);

            if (hitNode != null && hitNode.ContainerData != null)
            {
                string nsName = hitNode.ContainerData.ScopeKey;
                menu.Items.Add(string.Format("Rename Namespace: {0}", nsName), null,
                    delegate { RenameNamespaceByName(nsName); });
                menu.Items.Add(new ToolStripSeparator());

                // R6.4: Check if this is the global namespace
                if (nsName == "(global)" || string.IsNullOrEmpty(nsName))
                {
                    menu.Items.Add("Manage Global Namespace", null, OnManageGlobalNamespace);
                    menu.Items.Add(new ToolStripSeparator());
                }

                menu.Items.Add(string.Format("Delete Namespace: {0}", nsName), null,
                    delegate { DeleteNamespaceByName(nsName); });
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add("Add Namespace...", null, OnAddNamespace);
        }

        private void BuildTypeLevelContextMenu(ContextMenuStrip menu)
        {
            // Existing type-level context menu items
            menu.Items.Add("Add Interface", null, delegate { AddTypeAtMouse(TypeKind.Interface); });
            menu.Items.Add("Add Abstract Class", null, delegate { AddTypeAtMouse(TypeKind.AbstractClass); });
            menu.Items.Add("Add Concrete Class", null, delegate { AddTypeAtMouse(TypeKind.ConcreteClass); });
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem relMenu = new ToolStripMenuItem("Add Relationship");
            relMenu.DropDownItems.Add("Implements", null, delegate { StartRelationship(RelationshipKind.Implements); });
            relMenu.DropDownItems.Add("Inherits", null, delegate { StartRelationship(RelationshipKind.Inherits); });
            menu.Items.Add(relMenu);

            menu.Items.Add(new ToolStripSeparator());

            // "Go to" submenu for circle nodes on category canvases
            DiagramCanvas activeCanvas = GetActiveCanvas();
            SolutionProjectionBuilder activeBuilder = GetProjectionBuilderForCanvas(activeCanvas);
            if (activeCanvas != null && activeBuilder != null && _currentSolution != null)
            {
                ToolStripMenuItem goToMenu = new ToolStripMenuItem("Go to");
                bool hasGoTo = false;

                // Check selected node
                if (activeCanvas.SelectionManager.SelectedNodes.Count == 1)
                {
                    NodeVisual selectedNode = activeCanvas.SelectionManager.SelectedNodes[0];
                    if (selectedNode.IsCircleMode)
                    {
                        string fqn = activeBuilder.FindFQN(selectedNode);
                        if (!string.IsNullOrEmpty(fqn))
                        {
                            TypeModel typeModel = _currentSolution.FindType(fqn);
                            if (typeModel != null)
                            {
                                string targetTab = SolutionProjectionBuilder.GetTargetTabName(typeModel.TypeKind);
                                ToolStripMenuItem goToItem = new ToolStripMenuItem(
                                    string.Format("{0} ({1})", typeModel.Name, targetTab));
                                goToItem.Tag = typeModel;
                                goToItem.Click += OnGoToTypeFromCircle;
                                goToMenu.DropDownItems.Add(goToItem);
                                hasGoTo = true;
                            }
                        }
                    }
                }

                // Check right-click position for circle nodes
                if (!hasGoTo)
                {
                    PointF worldPos = activeCanvas.LastRightClickWorldPosition;
                    float tolerance = 5f / activeCanvas.Viewport.Zoom;
                    NodeVisual hitNode = activeCanvas.SceneGraph.HitTestNode(worldPos, tolerance);
                    if (hitNode != null && hitNode.IsCircleMode)
                    {
                        string fqn = activeBuilder.FindFQN(hitNode);
                        if (!string.IsNullOrEmpty(fqn))
                        {
                            TypeModel typeModel = _currentSolution.FindType(fqn);
                            if (typeModel != null)
                            {
                                string targetTab = SolutionProjectionBuilder.GetTargetTabName(typeModel.TypeKind);
                                ToolStripMenuItem goToItem = new ToolStripMenuItem(
                                    string.Format("{0} ({1})", typeModel.Name, targetTab));
                                goToItem.Tag = typeModel;
                                goToItem.Click += OnGoToTypeFromCircle;
                                goToMenu.DropDownItems.Add(goToItem);
                                hasGoTo = true;
                            }
                        }
                    }
                }

                if (hasGoTo)
                {
                    menu.Items.Add(goToMenu);
                    menu.Items.Add(new ToolStripSeparator());
                }
            }

            var viewSourceItem = new ToolStripMenuItem("View Source", null, OnViewSource);
            viewSourceItem.Enabled = CanViewSource();
            menu.Items.Add(viewSourceItem);
            menu.Items.Add("Edit Type...", null, OnEditType);
            // The canvas deletes the selection on the Delete key (DiagramCanvas.IsInputKey), so
            // advertise it here — this is the live menu the user sees (built on every Opening).
            ((ToolStripMenuItem)menu.Items.Add("Delete", null, OnDeleteSelection)).ShortcutKeyDisplayString = "Del";
        }

        // ---- R6: Context Menu Actions ----

        private void OnRenameSolution(object sender, EventArgs e)
        {
            if (_currentSolution == null) return;

            using (var inputForm = CreateInputDialog("Rename Solution", "New solution name:", _currentSolution.Name))
            {
                if (inputForm.ShowDialog(this) == DialogResult.OK)
                {
                    string newName = inputForm.Tag as string;
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        _currentSolution.Name = newName;
                        UpdateTitle();

                        // Rebuild solution-level scene
                        if (_projectionBuilder != null && _navigationState != null &&
                            _navigationState.Level == NavigationLevel.Solution)
                        {
                            _navigationState = new NavigationViewState(newName);
                            _projectionBuilder.BuildSolutionLevel(_currentSolution);
                            _canvas.Invalidate();
                            UpdateBreadcrumb();
                        }

                        _statusLabel.Text = string.Format("Renamed solution to: {0}", newName);
                    }
                }
            }
        }

        private void RenameProjectByName(string projectName)
        {
            if (_currentSolution == null) return;
            var project = _currentSolution.Projects.FirstOrDefault(p => p.Name == projectName);
            if (project == null) return;

            using (var inputForm = CreateInputDialog("Rename Project", "New project name:", projectName))
            {
                if (inputForm.ShowDialog(this) == DialogResult.OK)
                {
                    string newName = inputForm.Tag as string;
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        if (PathsEqual(project.ProjectDirectory, _currentSolution.SolutionDirectory))
                        {
                            ExecuteSolutionRename(_currentSolution, newName);
                            return;
                        }
                        try
                        {
                            using (_fileWatcher?.SuspendWhileEditing())
                            {
                                _projectService.RenameProject(_currentSolution, project, newName);
                                _projectTreePanel.LoadSolution(_currentSolution);

                                // Rebuild project-level scene
                                if (_projectionBuilder != null && _navigationState != null &&
                                    _navigationState.Level == NavigationLevel.Project)
                                {
                                    _projectionBuilder.BuildProjectLevel(_currentSolution);
                                    _canvas.Invalidate();
                                    SyncCategoryTabsToLevel();
                                }

                                _statusLabel.Text = string.Format("Renamed project to: {0}", newName);
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(string.Format("Failed to rename project: {0}", ex.Message),
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
        }

        private void EditProjectByName(string projectName)
        {
            // Navigate into the project (drill down to namespaces)
            if (_projectionBuilder != null && _navigationState != null &&
                _navigationState.Level == NavigationLevel.Project)
            {
                _projectionBuilder.SaveCurrentPositions();
                _navigationState.SavedViewport.PanX = _canvas.Viewport.PanOffset.X;
                _navigationState.SavedViewport.PanY = _canvas.Viewport.PanOffset.Y;
                _navigationState.SavedViewport.Zoom = _canvas.Viewport.Zoom;

                _navigationState = _navigationState.DrillDownToNamespaces(projectName);
                _projectionBuilder.BuildNamespaceLevel(_currentSolution, projectName);

                _canvas.Viewport.Reset();
                _canvas.ZoomToFit(80f);
                _canvas.Invalidate();
                UpdateBreadcrumb();
                SyncCategoryTabsToLevel();

                // See CanvasCoordinator: the breadcrumb belongs in its own segment, not on top of the
                // primary status message.
                UpdateBreadcrumb();
            }
        }

        private void DeleteProjectByName(string projectName)
        {
            if (_currentSolution == null) return;
            var project = _currentSolution.Projects.FirstOrDefault(p => p.Name == projectName);
            if (project == null) return;

            var result = MessageBox.Show(
                string.Format("Delete project '{0}'?\n\nThis will remove the project and all its files.", projectName),
                "Confirm Delete", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (result != DialogResult.OK) return;

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    foreach (var type in project.Types.ToList())
                    {
                        if (_projectionBuilder != null)
                            _projectionBuilder.RemoveType(type.FullyQualifiedName);
                    }
                    _projectService.RemoveProject(_currentSolution, project, true);
                    _projectTreePanel.RemoveProject(projectName);

                    // Rebuild current level
                    if (_projectionBuilder != null && _navigationState != null &&
                        _navigationState.Level == NavigationLevel.Project)
                    {
                        _projectionBuilder.BuildProjectLevel(_currentSolution);
                        _canvas.Invalidate();
                        SyncCategoryTabsToLevel();
                    }

                    _statusLabel.Text = string.Format("Deleted project: {0}", projectName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Failed to delete project: {0}", ex.Message),
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RenameNamespaceByName(string namespaceName)
        {
            if (_currentSolution == null || _navigationState == null) return;
            if (namespaceName == "(global)") return; // Cannot rename global namespace

            using (var inputForm = CreateInputDialog("Rename Namespace", "New namespace name:", namespaceName))
            {
                if (inputForm.ShowDialog(this) == DialogResult.OK)
                {
                    string newName = inputForm.Tag as string;
                    if (!string.IsNullOrWhiteSpace(newName) && newName != namespaceName)
                    {
                        try
                        {
                            using (_fileWatcher?.SuspendWhileEditing())
                            {
                                // Find all types in this namespace and change their namespace
                                string projectName = _navigationState.ProjectName;
                                var project = _currentSolution.Projects.FirstOrDefault(p => p.Name == projectName);
                                if (project == null) return;

                                foreach (var type in project.Types.ToList())
                                {
                                    if (type.Namespace == namespaceName)
                                    {
                                        _typeService.MoveToNamespace(_currentSolution, type, newName);
                                    }
                                }

                                // Reload to reflect changes
                                ReloadSolutionAsync();
                                _statusLabel.Text = string.Format("Renamed namespace to: {0}", newName);
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(string.Format("Failed to rename namespace: {0}", ex.Message),
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
        }

        private void DeleteNamespaceByName(string namespaceName)
        {
            if (_currentSolution == null || _navigationState == null) return;
            if (namespaceName == "(global)") return;

            string projectName = _navigationState.ProjectName;
            var project = _currentSolution.Projects.FirstOrDefault(p => p.Name == projectName);
            if (project == null) return;

            var typesInNs = project.Types.Where(t => t.Namespace == namespaceName).ToList();
            if (typesInNs.Count > 0)
            {
                MessageBox.Show(
                    string.Format("Cannot delete namespace '{0}' because it contains {1} type(s).\nDelete or move the types first.",
                        namespaceName, typesInNs.Count),
                    "Cannot Delete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _statusLabel.Text = string.Format("Namespace '{0}' is empty and has been removed", namespaceName);
        }

        private void OnAddNamespace(object sender, EventArgs e)
        {
            // Adding a namespace means creating a type in that namespace
            MessageBox.Show(
                "To create a new namespace, add a type (interface, class) and specify the new namespace name.",
                "Add Namespace", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---- R7: Manage Global Namespace ----


        internal SolutionProjectionBuilder _manageGlobalNsBuilder;

        private void OnManageGlobalNamespace(object sender, EventArgs e)
        {
            if (_currentSolution == null || _navigationState == null) return;

            string projectName = _navigationState.ProjectName;
            var project = _currentSolution.Projects.FirstOrDefault(p => p.Name == projectName);
            if (project == null) return;

            // Find types in the global namespace
            var globalTypes = project.Types.Where(t => string.IsNullOrEmpty(t.Namespace) || t.Namespace == "(global)").ToList();

            if (globalTypes.Count == 0)
            {
                MessageBox.Show("No types found in the global namespace.", "Global Namespace",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Create or reuse the management tab
            if (_manageGlobalNsTab == null || _manageGlobalNsTab.IsDisposed)
            {
                _manageGlobalNsCanvas = new DiagramCanvas();
                if (_sharedPreferences != null)
                {
                    _manageGlobalNsCanvas.SetSharedPreferences(_sharedPreferences);
                }
                _manageGlobalNsCanvas.Dock = DockStyle.Fill;

                // Context menu for the management canvas
                ContextMenuStrip mgmtMenu = new ContextMenuStrip();
                mgmtMenu.Items.Add("Create Namespace and Move Selected...", null, OnCreateNamespaceAndMove);
                _manageGlobalNsCanvas.ContextMenuStrip = mgmtMenu;

                _manageGlobalNsTab = new TabPage("Manage Global Namespace");
                _manageGlobalNsTab.ToolTipText = "Manage types in the global namespace";
                _manageGlobalNsTab.Controls.Add(_manageGlobalNsCanvas);
            }

            // Build the global namespace view
            if (_layoutStore != null)
            {
                var autoLayout = new GridAutoLayoutStrategy();
                _manageGlobalNsBuilder = new SolutionProjectionBuilder(
                    _manageGlobalNsCanvas.SceneGraph, _layoutStore, autoLayout);

                var visibleProjects = new List<string>();
                visibleProjects.Add(projectName);
                _manageGlobalNsBuilder.SetVisibleProjects(visibleProjects);

                // Build type-level for global namespace
                _manageGlobalNsBuilder.BuildTypeLevelForNamespace(_currentSolution, projectName, "(global)");
                _manageGlobalNsCanvas.Viewport.Reset();
                _manageGlobalNsCanvas.ZoomToFit(80f);
                _manageGlobalNsCanvas.Invalidate();
            }

            // Add the tab to the main tab control if not already present
            if (!_mainTabControl.TabPages.Contains(_manageGlobalNsTab))
            {
                _mainTabControl.TabPages.Add(_manageGlobalNsTab);
            }
            _mainTabControl.SelectedTab = _manageGlobalNsTab;

            _statusLabel.Text = string.Format("Managing global namespace: {0} type(s)", globalTypes.Count);
        }

        private void OnCreateNamespaceAndMove(object sender, EventArgs e)
        {
            if (_currentSolution == null || _manageGlobalNsCanvas == null || _manageGlobalNsBuilder == null)
                return;

            // Get selected types
            var selectedNodes = _manageGlobalNsCanvas.SelectionManager.SelectedNodes.ToList();
            if (selectedNodes.Count == 0)
            {
                MessageBox.Show("Select one or more types to move.", "No Selection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Collect the type models
            var typesToMove = new List<TypeModel>();
            foreach (var node in selectedNodes)
            {
                string fqn = _manageGlobalNsBuilder.FindFQN(node);
                if (!string.IsNullOrEmpty(fqn))
                {
                    TypeModel type = _currentSolution.FindType(fqn);
                    if (type != null) typesToMove.Add(type);
                }
            }

            if (typesToMove.Count == 0)
            {
                MessageBox.Show("Could not identify selected types.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Ask for namespace name
            using (var inputForm = CreateInputDialog("Create Namespace", "New namespace name:", "NewNamespace"))
            {
                if (inputForm.ShowDialog(this) == DialogResult.OK)
                {
                    string newNamespace = inputForm.Tag as string;
                    if (string.IsNullOrWhiteSpace(newNamespace)) return;

                    try
                    {
                        using (_fileWatcher?.SuspendWhileEditing())
                        {
                            // R7.4/R7.6: Move types to the new namespace using the real type service
                            foreach (var type in typesToMove)
                            {
                                _typeService.MoveToNamespace(_currentSolution, type, newNamespace);
                            }

                            // R7.5: Reload to reflect changes everywhere
                            ReloadSolutionAsync();

                            // Close the management tab since types have moved
                            if (_mainTabControl.TabPages.Contains(_manageGlobalNsTab))
                            {
                                _mainTabControl.TabPages.Remove(_manageGlobalNsTab);
                            }

                            _statusLabel.Text = string.Format("Moved {0} type(s) to namespace '{1}'",
                                typesToMove.Count, newNamespace);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(string.Format("Failed to move types: {0}", ex.Message),
                            "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        // ---- Hierarchical Navigation ----

        // Chord state for Ctrl+K, Ctrl+I (quick info). Has to live at form level
        // because Ctrl+I collides with file.import's menu shortcut and menu-shortcut
        // dispatch happens before child controls see the key.
        private DateTime _ctrlKChordAt = DateTime.MinValue;
        private const double CtrlKChordMaxSeconds = 2.0;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // --- Ctrl+K, Ctrl+I chord (quick info in the editor) ---
            Keys modifiers = keyData & (Keys.Control | Keys.Shift | Keys.Alt);
            Keys code = keyData & Keys.KeyCode;

            if (code == Keys.F1 && modifiers == Keys.None)
            {
                var quickInfoEditor = ActiveEditorIfFocused();
                if (quickInfoEditor != null) { quickInfoEditor.ShowQuickInfoAtCaret(); return true; }
            }

            // Ctrl+A / Ctrl+C in a results grid or a read-only log box (Output, Problems, Complexity,
            // Metrics, Profiler, Debug Call Stack, …). This must run BEFORE base.ProcessCmdKey, because
            // that is where the global Edit ▸ Copy menu shortcut (also bound to Ctrl+C) gets checked —
            // a ListView never claims Ctrl+A/Ctrl+C for itself the way a writable text box does, so
            // without this the menu shortcut wins and the grid's own KeyDown handler never even fires.
            if (modifiers == Keys.Control && (code == Keys.A || code == Keys.C))
            {
                Control focused = Control.FromHandle(msg.HWnd);
                if (focused is ListView focusedList)
                {
                    if (HelixPythonNamespace.UI.Controls.ListViewClipboard.TryHandleShortcut(focusedList, keyData)) return true;
                }
                else if (focused is TextBoxBase focusedBox && focusedBox.ReadOnly)
                {
                    if (code == Keys.A) { focusedBox.SelectAll(); return true; }
                    if (!string.IsNullOrEmpty(focusedBox.SelectedText)) { focusedBox.Copy(); return true; }
                }
            }

            // F8 / Shift+F8 walk the Compile panel's diagnostics, jumping the editor to each in turn —
            // the standard "next error / previous error" workflow. No-op (falls through) when clean.
            if (code == Keys.F8 && (modifiers == Keys.None || modifiers == Keys.Shift))
            {
                if (_problemsPanel != null && _problemsPanel.ActivateAdjacent(modifiers == Keys.Shift ? -1 : 1))
                    return true;
            }
            if (modifiers == Keys.Control)
            {
                if (code == Keys.K && ActiveEditorIfFocused() != null)
                {
                    _ctrlKChordAt = DateTime.UtcNow;
                    return true; // swallow so no other handler reacts
                }
                if (code == Keys.I &&
                    (DateTime.UtcNow - _ctrlKChordAt).TotalSeconds < CtrlKChordMaxSeconds)
                {
                    _ctrlKChordAt = DateTime.MinValue;
                    var editor = ActiveEditorIfFocused();
                    if (editor != null)
                    {
                        editor.ShowQuickInfoAtCaret();
                        return true; // consumed — don't let file.import fire
                    }
                }
                // Ctrl+K, Ctrl+X = Insert Snippet; Ctrl+K, Ctrl+0/J = fold/unfold.
                if (code == Keys.X &&
                    (DateTime.UtcNow - _ctrlKChordAt).TotalSeconds < CtrlKChordMaxSeconds)
                {
                    _ctrlKChordAt = DateTime.MinValue;
                    var editor = ActiveEditorIfFocused();
                    if (editor != null) { editor.ShowPythonSnippets(); return true; }
                }
                if ((code == Keys.D0 || code == Keys.NumPad0 || code == Keys.J) &&
                    (DateTime.UtcNow - _ctrlKChordAt).TotalSeconds < CtrlKChordMaxSeconds)
                {
                    _ctrlKChordAt = DateTime.MinValue;
                    var editor = ActiveEditorIfFocused();
                    if (editor != null)
                    {
                        if (code == Keys.J) editor.UnfoldAll(); else editor.FoldAll();
                        return true;
                    }
                }
            }

            // Ctrl+F4 / Ctrl+W close the active DOCUMENT while the Editor tab is on screen. They
            // used to close the whole top-level tab, so one Ctrl+W on the Editor tab dismissed the
            // editor and every file open in it — no IDE behaves that way. With no document left
            // (or on any other top-level tab) they still close that panel, which is reopenable from
            // View ▸ Panels.
            if (IsCloseTabShortcut(keyData))
            {
                HelixPythonNamespace.Editor.EditorTabPage doc = ActiveDocumentTab();
                if (doc != null) { _editorCoordinator.CloseEditorTab(doc); return true; }

                TabPage active = _mainTabControl != null ? _mainTabControl.SelectedTab : null;
                if (active != null) { CloseTab(active); return true; }
            }

            // Editor-scoped Find / Replace. Only fires when an editor tab (or its
            // find bar) holds focus, so the global Ctrl+F (find type) and Ctrl+H
            // (hide element) shortcuts keep working everywhere else.
            {
                HelixPythonNamespace.Editor.EditorTabPage etp = ActiveEditorTabIfFocused();
                if (etp != null)
                {
                    if (keyData == (Keys.Control | Keys.F)) { etp.ShowFind(); return true; }
                    if (keyData == (Keys.Control | Keys.H)) { etp.ShowReplace(); return true; }
                    if (keyData == Keys.F3) { etp.FindNext(true); return true; }
                    if (keyData == (Keys.Shift | Keys.F3)) { etp.FindNext(false); return true; }
                    // Editor commands — dispatched via the (remappable) keybindings so the Keybindings
                    // dialog is the single source of truth. Guarded against a null manager / cleared chord.
                    if (_keybindings != null)
                    {
                        if (keyData == _keybindings.ChordFor("editor.toggleComment")) { etp.Editor.ToggleComment(); return true; }
                        if (keyData == _keybindings.ChordFor("editor.moveLineUp")) { etp.Editor.MoveLinesUp(); return true; }
                        if (keyData == _keybindings.ChordFor("editor.moveLineDown")) { etp.Editor.MoveLinesDown(); return true; }
                        if (keyData == _keybindings.ChordFor("editor.duplicateLine")) { etp.Editor.DuplicateSelection(); return true; }
                        if (keyData == _keybindings.ChordFor("editor.goToLine")) { ShowGoToLineDialog(etp); return true; }
                    }
                }
            }

            // Alt+Up is the explicit Drill Up command. Escape/Backspace remain convenient
            // canvas-only aliases for the existing workflow.
            if (keyData == (Keys.Alt | Keys.Up) && CanDrillUp)
            {
                NavigateBack();
                return true;
            }
            if (keyData == Keys.Escape || keyData == Keys.Back)
            {
                if (_navigationState != null && _navigationState.Level != NavigationLevel.Solution)
                {
                    bool onDesignerTab = IsDesignerTabActive();
                    if (onDesignerTab)
                    {
                        NavigateBack();
                        return true;
                    }
                }
            }

            // Ctrl+S must be handled before the nested editor/menu hierarchy gets a chance to
            // swallow it. Notebook cells use an embedded editor, but the document that owns the
            // save is the surrounding NotebookTabPage.
            if (keyData == (Keys.Control | Keys.S) &&
                _mainTabControl != null && _mainTabControl.SelectedTab == _editorTab &&
                _editorInnerTabControl != null &&
                (_editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage ||
                 _editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.Notebook.NotebookTabPage))
            {
                OnFileSave(this, EventArgs.Empty);
                return true;
            }

            // Clipboard shortcuts apply only when a designer tab is active
            if (IsDesignerTabActive())
            {
                if (keyData == (Keys.Control | Keys.C))
                {
                    OnEditCopy(this, EventArgs.Empty);
                    return true;
                }
                if (keyData == (Keys.Control | Keys.X))
                {
                    OnEditCut(this, EventArgs.Empty);
                    return true;
                }
                if (keyData == (Keys.Control | Keys.V))
                {
                    OnEditPaste(this, EventArgs.Empty);
                    return true;
                }
                // Canvas zoom — Ctrl++ / Ctrl+- / Ctrl+0 (reset to 100%), like every viewer.
                switch (ClassifyZoomShortcut(keyData))
                {
                    case ZoomKey.In: OnZoomIn(this, EventArgs.Empty); return true;
                    case ZoomKey.Out: OnZoomOut(this, EventArgs.Empty); return true;
                    case ZoomKey.Reset: SetZoomPercent(100); return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private bool IsDesignerTabActive()
        {
            TabPage selected = _mainTabControl.SelectedTab;
            // Promoted mode: the designer tab is directly in the main tab control
            if (selected == _designerTab)
            {
                return true;
            }
            // Nested mode: check if the GDC wrapper is selected
            if (selected == _graphicalDesignerTab)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Return the <see cref="HelixPythonNamespace.Editor.PythonEditorControl"/> that currently
        /// has keyboard focus (via <see cref="Control.ContainsFocus"/>), or null when
        /// focus lives outside the editor. Used by <see cref="ProcessCmdKey"/> to scope
        /// the Ctrl+K,Ctrl+I chord to the editor tabs.
        /// </summary>
        private HelixPythonNamespace.Editor.PythonEditorControl ActiveEditorIfFocused()
        {
            if (_editorInnerTabControl == null) return null;
            foreach (TabPage p in _editorInnerTabControl.TabPages)
            {
                if (p is HelixPythonNamespace.Editor.EditorTabPage etp && etp.Editor != null)
                {
                    if (etp.Editor.Focused || etp.Editor.ContainsFocus)
                        return etp.Editor;
                }
            }
            return null;
        }

        /// <summary>
        /// The <see cref="HelixPythonNamespace.Editor.EditorTabPage"/> that currently contains
        /// keyboard focus — true when either its editor or its Find bar is focused.
        /// Used to scope the editor Find/Replace shortcuts.
        /// </summary>
        private HelixPythonNamespace.Editor.EditorTabPage ActiveEditorTabIfFocused()
        {
            if (_editorInnerTabControl == null) return null;
            foreach (TabPage p in _editorInnerTabControl.TabPages)
            {
                if (p is HelixPythonNamespace.Editor.EditorTabPage etp && etp.ContainsFocus)
                    return etp;
            }
            return null;
        }

        internal void UpdateBreadcrumb()
        {
            if (_navigationState != null)
            {
                _breadcrumbLabel.Text = _navigationState.GetBreadcrumbDisplay();
            }
            else
            {
                _breadcrumbLabel.Text = "";
            }
            UpdateDrillUpAvailability();
        }

        internal void OnDrillUp(object sender, EventArgs e)
        {
            NavigateBack();
        }

        internal bool CanDrillUp
        {
            get
            {
                return _navigationState != null &&
                    _navigationState.Level != NavigationLevel.Solution;
            }
        }

        private void UpdateDrillUpAvailability()
        {
            bool canDrillUp = CanDrillUp;
            if (_drillUpButton != null)
            {
                _drillUpButton.Enabled = canDrillUp;
                _drillUpButton.ToolTipText = canDrillUp
                    ? "Drill Up — return to " + ParentNavigationLabel()
                    : "Drill Up — already at solution level";
            }
            if (_breadcrumbLabel != null)
                _breadcrumbLabel.Enabled = canDrillUp;
            if (_canvas != null) _canvas.ShowDrillUpButton = canDrillUp;
        }

        private string ParentNavigationLabel()
        {
            if (_navigationState == null) return "parent";
            switch (_navigationState.Level)
            {
                case NavigationLevel.Type: return "namespaces";
                case NavigationLevel.Namespace: return "projects";
                case NavigationLevel.Project: return "solution";
                default: return "parent";
            }
        }

        internal void NavigateBack()
        {
            if (_navigationState == null || _currentSolution == null || _projectionBuilder == null)
            {
                return;
            }

            NavigationViewState parentState = _navigationState.NavigateBack();
            if (parentState == null)
            {
                // Already at solution level
                return;
            }

            // Save current positions before navigating back
            _projectionBuilder.SaveCurrentPositions();

            // Save current viewport
            _navigationState.SavedViewport.PanX = _canvas.Viewport.PanOffset.X;
            _navigationState.SavedViewport.PanY = _canvas.Viewport.PanOffset.Y;
            _navigationState.SavedViewport.Zoom = _canvas.Viewport.Zoom;

            _navigationState = parentState;

            // Rebuild scene for the target level
            switch (_navigationState.Level)
            {
                case NavigationLevel.Solution:
                    _projectionBuilder.BuildSolutionLevel(_currentSolution);
                    break;

                case NavigationLevel.Project:
                    _projectionBuilder.BuildProjectLevel(_currentSolution);
                    break;

                case NavigationLevel.Namespace:
                    _projectionBuilder.BuildNamespaceLevel(_currentSolution, _navigationState.ProjectName);
                    break;

                case NavigationLevel.Type:
                    _projectionBuilder.BuildTypeLevelForNamespace(
                        _currentSolution, _navigationState.ProjectName, _navigationState.NamespaceName);
                    break;
            }

            // Restore saved viewport for this level
            var savedVp = _navigationState.SavedViewport;
            if (savedVp != null && savedVp.Zoom > 0.09f)
            {
                _canvas.Viewport.SetZoom(savedVp.Zoom, new PointF(0, 0));
                _canvas.Viewport.SetPanOffset(savedVp.PanX, savedVp.PanY);
            }
            else
            {
                _canvas.Viewport.Reset();
                _canvas.ZoomToFit(80f);
            }

            _canvas.Invalidate();
            UpdateBreadcrumb();

            // R5/R8: Synchronize category tabs to the same level
            SyncCategoryTabsToLevel();

            _statusLabel.Text = string.Format("Navigated back to: {0}", _navigationState.GetBreadcrumbDisplay());
        }

        // ---- Helpers ----

        internal void UpdateTitle()
        {
            // Professional IDE title convention (Visual Studio / VS Code):
            //   "{dirty}{document} - {context} - {ProductName}"
            // The active document comes first so the taskbar/Alt-Tab preview tells the
            // user which file they are editing without focusing the window.
            string dirtyMark = (_solutionDirty || CountModifiedEditorTabs() > 0) ? "* " : string.Empty;
            string activeFile = ActiveEditorFileName();
            bool hasFile = !string.IsNullOrEmpty(activeFile);
            bool hasSolution = _currentSolution != null;

            if (!hasFile && !hasSolution)
            {
                // Cold start / welcome: no document, no solution.
                Text = ProductTitle;
            }
            else if (hasFile && hasSolution)
            {
                Text = string.Format("{0}{1} - {2} - {3}", dirtyMark, activeFile, _currentSolution.Name, ProductName);
            }
            else if (hasSolution)
            {
                Text = string.Format("{0}{1} - {2}", dirtyMark, _currentSolution.Name, ProductName);
            }
            else
            {
                Text = string.Format("{0}{1} - {2}", dirtyMark, activeFile, ProductName);
            }
        }

        /// <summary>
        /// Refresh the status-bar Ln/Col readout from the active editor tab, or clear it when the
        /// Editor tab isn't the active view. Called on tab changes; live caret moves go through
        /// <see cref="OnEditorCaretMoved"/>.
        /// </summary>
        internal void UpdateCaretStatus()
        {
            if (_caretLabel == null) return;
            HelixPythonNamespace.Editor.EditorTabPage etp = ActiveEditorTabOrNull();
            _caretLabel.Text = etp != null
                ? FormatCaretText(etp.CaretLine, etp.CaretColumn, etp.SelectionLength)
                : string.Empty;
            // Only reads as a clickable link (Go to Line) while there is an editor to jump within.
            _caretLabel.IsLink = etp != null;
        }

        // Refresh the CRLF / LF indicator from the active editor. Called on tab-switch / open (not on
        // every caret move), so reading the document text here is cheap enough.
        internal void UpdateEolStatus()
        {
            HelixPythonNamespace.Editor.EditorTabPage etp = ActiveEditorTabOrNull();
            string text = etp != null && etp.Editor != null ? etp.Editor.Text : null;
            if (_eolLabel != null)
            {
                _eolLabel.Text = etp != null ? DetectLineEnding(text) : string.Empty;
                // Clickable only when there is a document with a line ending to convert.
                _eolLabel.IsLink = etp != null && _eolLabel.Text.Length > 0;
            }
            if (_indentLabel != null)
            {
                _indentLabel.Text = etp != null ? DetectIndentation(text) : string.Empty;
                _indentLabel.IsLink = etp != null && _indentLabel.Text.Length > 0;
            }
        }

        // Clicking the indentation indicator offers to convert the document's indentation (VS Code).
        private void OnIndentLabelClick(object sender, EventArgs e)
        {
            HelixPythonNamespace.Editor.EditorTabPage etp = ActiveEditorTabOrNull();
            if (etp == null || etp.Editor == null) return;

            int tabWidth = etp.Editor.TabWidth > 0 ? etp.Editor.TabWidth : 4;
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Convert Indentation to Spaces", null,
                delegate { ConvertActiveEditorIndent(etp, toTabs: false, tabWidth: tabWidth); }));
            menu.Items.Add(new ToolStripMenuItem("Convert Indentation to Tabs", null,
                delegate { ConvertActiveEditorIndent(etp, toTabs: true, tabWidth: tabWidth); }));
            Rectangle b = _indentLabel.Bounds;
            menu.Show(_statusStrip, new Point(b.Left, b.Top - menu.PreferredSize.Height));
        }

        // Rewrite the whole document with its leading indentation converted to tabs or spaces, then
        // refresh. Preserves the caret's line and keeps it as a single undoable edit.
        private void ConvertActiveEditorIndent(HelixPythonNamespace.Editor.EditorTabPage etp, bool toTabs, int tabWidth)
        {
            if (etp == null || etp.Editor == null) return;
            try
            {
                string converted = ConvertIndentation(etp.Editor.Text, toTabs, tabWidth);
                if (!string.Equals(converted, etp.Editor.Text, StringComparison.Ordinal))
                {
                    int caretLine = etp.Editor.CurrentLine;
                    etp.Editor.BeginUndoAction();
                    try { etp.Editor.Text = converted; }
                    finally { etp.Editor.EndUndoAction(); }
                    if (caretLine >= 0 && caretLine < etp.Editor.Lines.Count)
                        etp.Editor.GotoPosition(etp.Editor.Lines[caretLine].Position);
                }
            }
            catch { /* editor may be mid-teardown */ }
            UpdateEolStatus();
        }

        // Convert every line's LEADING whitespace to tabs or spaces, preserving the visual indent
        // column (a tab advances to the next multiple of tabWidth). Only leading whitespace is touched —
        // whitespace inside the line (e.g. alignment, strings) is left exactly as-is. Pure + testable.
        internal static string ConvertIndentation(string text, bool toTabs, int tabWidth)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            if (tabWidth <= 0) tabWidth = 4;
            var sb = new System.Text.StringBuilder(text.Length + 16);
            int i = 0, n = text.Length;
            while (i < n)
            {
                int lineStart = i;
                while (i < n && text[i] != '\n') i++;
                bool hasNl = i < n;
                int lineEnd = i;                 // exclusive, before '\n'
                // Measure + strip leading whitespace of [lineStart, lineEnd).
                int j = lineStart, col = 0;
                while (j < lineEnd && (text[j] == ' ' || text[j] == '\t'))
                {
                    col += text[j] == '\t' ? tabWidth - (col % tabWidth) : 1;
                    j++;
                }
                if (toTabs) { sb.Append('\t', col / tabWidth); sb.Append(' ', col % tabWidth); }
                else sb.Append(' ', col);
                sb.Append(text, j, lineEnd - j);   // the rest of the line, untouched
                if (hasNl) { sb.Append('\n'); i++; }
            }
            return sb.ToString();
        }

        // The file's indentation, VS Code style: "Tabs" when the first indented line starts with a tab,
        // else "Spaces: N" where N is the smallest positive count of leading spaces across the file (its
        // indent unit). "" when nothing is indented — there is no unit to report.
        internal static string DetectIndentation(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int minSpaces = int.MaxValue;
            int lineStart = 0;
            int n = text.Length;
            for (int i = 0; i <= n; i++)
            {
                if (i == n || text[i] == '\n')
                {
                    // Measure the leading whitespace of the line [lineStart, i).
                    int j = lineStart;
                    if (j < i && text[j] == '\t') return "Tabs";   // tab-indented file
                    int spaces = 0;
                    while (j < i && text[j] == ' ') { spaces++; j++; }
                    // Ignore blank lines (all-whitespace) and unindented lines.
                    if (spaces > 0 && j < i && text[j] != '\r' && text[j] != '\n')
                        if (spaces < minSpaces) minSpaces = spaces;
                    lineStart = i + 1;
                }
            }
            return minSpaces == int.MaxValue ? string.Empty : "Spaces: " + minSpaces;
        }

        // Clicking the CRLF/LF indicator offers to convert the document's line endings (VS Code parity).
        private void OnEolLabelClick(object sender, EventArgs e)
        {
            HelixPythonNamespace.Editor.EditorTabPage etp = ActiveEditorTabOrNull();
            if (etp == null || etp.Editor == null) return;

            string current = DetectLineEnding(etp.Editor.Text);
            var menu = new ContextMenuStrip();
            AddEolMenuItem(menu, "Windows (CRLF)", "CRLF", HelixTextEditorNET.Eol.CrLf, current, etp);
            AddEolMenuItem(menu, "Unix (LF)", "LF", HelixTextEditorNET.Eol.Lf, current, etp);
            // Anchor to the label (not the cursor) and open UPWARD — the status bar is at the window
            // bottom, so a downward menu would spill off-screen.
            Rectangle b = _eolLabel.Bounds;
            menu.Show(_statusStrip, new Point(b.Left, b.Top - menu.PreferredSize.Height));
        }

        // One line-ending choice; the active one is checked. Selecting it converts the whole document.
        private void AddEolMenuItem(ContextMenuStrip menu, string text, string tag,
            HelixTextEditorNET.Eol eol, string current, HelixPythonNamespace.Editor.EditorTabPage etp)
        {
            var item = new ToolStripMenuItem(text) { Checked = string.Equals(current, tag, StringComparison.Ordinal) };
            item.Click += delegate { ConvertActiveEditorEol(etp, eol); };
            menu.Items.Add(item);
        }

        // Convert every line ending in the document and set the mode for new lines, then refresh.
        private void ConvertActiveEditorEol(HelixPythonNamespace.Editor.EditorTabPage etp, HelixTextEditorNET.Eol eol)
        {
            if (etp == null || etp.Editor == null) return;
            try
            {
                etp.Editor.ConvertEols(eol);
                etp.Editor.EolMode = eol;
            }
            catch { /* editor may be mid-teardown */ }
            UpdateEolStatus();
        }

        // The file's dominant line ending, decided by the FIRST newline (files are near-always
        // consistent, and this keeps the scan O(first line) rather than O(text)). "" when there is no
        // newline yet — a single-line or empty document has no line ending to report.
        internal static string DetectLineEnding(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n') return "LF";
                if (c == '\r') return (i + 1 < text.Length && text[i + 1] == '\n') ? "CRLF" : "CR";
            }
            return string.Empty;
        }

        // The editor sub-tab that currently owns the caret — non-null only while the Editor view is the
        // active main tab. Shared by the caret status readout and the clickable Ln/Col → Go to Line.
        private HelixPythonNamespace.Editor.EditorTabPage ActiveEditorTabOrNull()
        {
            return (_mainTabControl != null && _editorTab != null && _mainTabControl.SelectedTab == _editorTab
                    && _editorInnerTabControl != null)
                ? _editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.EditorTabPage
                : null;
        }

        // Clicking the "Ln x, Col y" readout opens Go to Line for the active editor (VS Code parity).
        private void OnCaretLabelClick(object sender, EventArgs e)
        {
            HelixPythonNamespace.Editor.EditorTabPage etp = ActiveEditorTabOrNull();
            if (etp != null) ShowGoToLineDialog(etp);
        }

        // Ctrl+G: prompt for a line number and jump there. Seeds the current line and shows the range.
        private void ShowGoToLineDialog(HelixPythonNamespace.Editor.EditorTabPage etp)
        {
            if (etp == null) return;
            int total = etp.Editor.Lines.Count;
            using (var dlg = new HelixPythonNamespace.App.Dialogs.SimpleInputDialog(
                "Go to Line",
                string.Format("Line number (1–{0}):", total),
                etp.CaretLine.ToString()))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    ApplyGoToLine(etp, dlg.Value);
            }
        }

        // Parse the entered line number and move the caret there (GoToLine clamps to the valid range).
        // Non-numeric or empty input is ignored. Extracted so the parse/jump is unit-testable.
        internal static void ApplyGoToLine(HelixPythonNamespace.Editor.EditorTabPage etp, string input)
        {
            if (etp == null) return;
            int line;
            if (int.TryParse((input ?? string.Empty).Trim(), out line) && line >= 1)
                etp.Editor.GoToLine(line);
        }

        // "Ln L, Col C" plus a " (N selected)" suffix while a selection is active (VS/VS Code parity).
        internal static string FormatCaretText(int line, int col, int selectionLength)
        {
            return selectionLength > 0
                ? string.Format("Ln {0}, Col {1} ({2} selected)", line, col, selectionLength)
                : string.Format("Ln {0}, Col {1}", line, col);
        }

        /// <summary>Live caret-move handler wired to each editor tab; updates the Ln/Col readout.</summary>
        internal void OnEditorCaretMoved(int line, int col)
        {
            // Scintilla's UpdateUI (which raises CaretMoved) fires on selection changes too, so route
            // through UpdateCaretStatus to keep the "(N selected)" suffix live as the selection grows.
            UpdateCaretStatus();
        }

        /// <summary>
        /// Display file name of the currently selected editor sub-tab (not focus-
        /// dependent, so it is valid while a menu or another panel holds focus), or
        /// <c>null</c> when no editor tab is open. Unsaved tabs report "Untitled".
        /// </summary>
        private string ActiveEditorFileName()
        {
            if (_editorInnerTabControl == null) return null;
            var etp = _editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.EditorTabPage;
            if (etp == null) return null;
            string fp = etp.FilePath;
            return string.IsNullOrEmpty(fp) ? "Untitled" : System.IO.Path.GetFileName(fp);
        }

        internal void MarkSolutionDirty()
        {
            if (_solutionDirty) return;
            _solutionDirty = true;
            UpdateTitle();
        }

        internal void ClearSolutionDirty()
        {
            if (!_solutionDirty && CountModifiedEditorTabs() == 0) return;
            _solutionDirty = false;
            UpdateTitle();
        }

        /// <summary>
        /// Count EditorTabPage instances that have unsaved edits.
        /// </summary>
        private int CountModifiedEditorTabs()
        {
            if (_editorInnerTabControl == null) return 0;
            int n = 0;
            foreach (TabPage tab in _editorInnerTabControl.TabPages)
            {
                if (tab is HelixPythonNamespace.Editor.EditorTabPage etp && etp.IsModified) n++;
            }
            return n;
        }

        /// <summary>
        /// Crash-time best-effort flush of unsaved editor edits. Self-guards: only runs when called
        /// on the UI thread with a live handle, so a background-thread fault can't corrupt UI state.
        /// Wired to <see cref="GlobalExceptionHandler.EmergencySave"/> in Program.Main.
        /// </summary>
        internal void FlushAllEditorSavesSafe()
        {
            try
            {
                if (!IsDisposed && IsHandleCreated && !InvokeRequired)
                    SaveAllModifiedEditorTabs();
            }
            catch { /* best-effort; the crash handler continues regardless */ }
        }

        /// <summary>Save every modified EditorTabPage. Called from the close-prompt.</summary>
        /// <summary>
        /// Best-effort save of every dirty document as the IDE closes.
        ///
        /// <para>
        /// <b>Notebooks are documents too, and they were being skipped.</b> This walked only
        /// <c>EditorTabPage</c>, so closing the IDE silently discarded everything unsaved in an open
        /// `.ipynb` — cell edits, outputs, and the notebook's registered import paths. Reported as
        /// "the added path is gone after reboot", which was the visible corner of it.
        /// <c>FlushPendingSave</c> rather than <c>Save</c>, because a notebook's newest keystrokes
        /// live in the embedded cell editor until something commits them, and a save that skipped
        /// that commit would write the file WITHOUT the edit that made it dirty.
        /// </para>
        /// </summary>
        private void SaveAllModifiedEditorTabs()
        {
            if (_editorInnerTabControl == null) return;
            foreach (TabPage tab in _editorInnerTabControl.TabPages)
            {
                if (tab is HelixPythonNamespace.Editor.EditorTabPage etp && etp.IsModified)
                {
                    try { etp.SaveFile(); } catch { /* best-effort per-file */ }
                }
                else if (tab is HelixPythonNamespace.Editor.Notebook.NotebookTabPage notebook)
                {
                    try { notebook.FlushPendingSave(); } catch { /* best-effort per-file */ }
                }
            }
        }

        /// <summary>
        /// Strict Save All used as a Build/Run precondition. Unlike crash/close best-effort
        /// saving, this reports failure when any dirty buffer is vetoed or remains unsaved so an
        /// external toolchain never silently compiles stale disk content.
        /// </summary>
        internal bool SaveAllModifiedEditorTabsBeforeExternalOperation(string operation)
        {
            if (_editorInnerTabControl == null) return true;
            foreach (TabPage tab in _editorInnerTabControl.TabPages)
            {
                var editorTab = tab as HelixPythonNamespace.Editor.EditorTabPage;
                if (editorTab == null || !editorTab.IsModified) continue;
                bool saved = false;
                try { saved = editorTab.SaveFile(); } catch { saved = false; }
                if (!saved || editorTab.IsModified)
                {
                    string name = string.IsNullOrEmpty(editorTab.FilePath)
                        ? (editorTab.Text ?? "untitled document")
                        : Path.GetFileName(editorTab.FilePath);
                    if (_statusLabel != null)
                        _statusLabel.Text = (operation ?? "Operation") + " cancelled: could not save " + name;
                    return false;
                }
            }
            return true;
        }

        private void ApplySettings()
        {
            if (_settingsManager == null)
            {
                return;
            }

            var settings = _settingsManager.Settings;

            // View ▸ Word Wrap. The menu item seeded _wordWrap from settings while the menus were
            // built, but the surfaces it drives (Output pane, editor tabs) are composed after that,
            // so the restored preference is pushed into them here — otherwise it comes back ticked
            // and does nothing.
            if (_wordWrap) ApplyWordWrap();

            // Cap the restored size to the primary work area — a size saved on a larger monitor must
            // not open bigger than the current screen (the OnShown clamp is the final safety net, but
            // this avoids the window ever flashing oversized/off-screen).
            System.Drawing.Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = settings.WindowWidth, h = settings.WindowHeight;
            if (w < 800) w = 800;
            if (h < 600) h = 600;
            if (w > wa.Width) w = wa.Width;
            if (h > wa.Height) h = wa.Height;
            Width = w;
            Height = h;

            if (settings.WindowMaximized)
            {
                WindowState = FormWindowState.Maximized;
            }

            if (settings.GridSpacing >= 2f && settings.GridSpacing <= 200f)
            {
                HelixPythonNamespace.UI.Rendering.LayoutConstants.GridSpacing = settings.GridSpacing;
            }

            HelixPythonNamespace.UI.Rendering.GridSnapper.Enabled = settings.SnapToGrid;
            if (_snapToGridMenuItem != null) _snapToGridMenuItem.Checked = settings.SnapToGrid;

            // Wire zoom-label updates to viewport zoom changes
            if (_canvas != null && _canvas.Viewport != null)
            {
                _canvas.Viewport.ZoomChanged += new Action(UpdateZoomLabel);
            }

            EnableTrayCaptionButton();

            Load += ApplySettingsOnLoad;
        }

        private void EnsureToolsPaletteCreated()
        {
            if (_toolsPalette != null && !_toolsPalette.IsDisposed) return;
            if (_sharedPreferences == null) return;

            _toolsPalette = new HelixPythonNamespace.UI.Controls.ToolsPaletteForm(_sharedPreferences);
            _toolsPalette.TopLevel = false;
            _toolsPalette.EmbeddedSurface = true;
            _toolsPalette.FormBorderStyle = FormBorderStyle.None;
            _toolsPalette.ShowInTaskbar = false;
            _toolsPalette.TopMost = false;
            // Place the compact designer toolbar in the marked filter strip, immediately beside
            // the Show combo. Parenting it here makes the placement follow WinForms layout rather
            // than relying on screen coordinates or overlaying the diagram canvas.
            _toolsPalette.Dock = DockStyle.None;
            _toolsPalette.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _toolsPalette.Location = new Point(_designerCategoryCombo.Right + 6, 1);
            _toolsPalette.Size = new Size(122, 24);
            if (_designerFilterBar != null)
            {
                _designerFilterBar.Controls.Add(_toolsPalette);
                _toolsPalette.BringToFront();
            }
            _toolsPalette.TypeRequested += OnPaletteTypeRequested;
            // Closing the palette with its X clears the enabled intent (+ menu checkmark) so it
            // does not spring back the next time the Class Designer Canvas tab becomes active.
            _toolsPalette.UserClosed += (s, e) => HideToolsPalette();

            // This embedded toolbar has no user-positionable screen coordinates; the filter strip
            // owns its placement beside _designerCategoryCombo.
        }

        private void OnPaletteTypeRequested(object sender, HelixPythonNamespace.UI.Controls.TypeRequestedEventArgs e)
        {
            if (e == null) return;
            CreateTypeAndOpenEditor(e.Kind);
        }

        /// <summary>
        /// Register the composition handler on a canvas's interaction context so drag-drop
        /// onto a type node opens the ComposeConfirmDialog and routes to field creation.
        /// </summary>
        private void WireCompositionHandler(HelixPythonNamespace.UI.Controls.DiagramCanvas canvas)
        {
            if (canvas == null || canvas.InteractionContext == null) return;
            canvas.InteractionContext.CompositionHandler = (targetNodeId, sourceNodeIds) =>
                HandleCompositionRequest(canvas, targetNodeId, sourceNodeIds);
        }

        private HelixPythonNamespace.UI.Interaction.InteractionContext.CompositionOutcome
            HandleCompositionRequest(HelixPythonNamespace.UI.Controls.DiagramCanvas canvas, string targetNodeId, IList<string> sourceNodeIds)
        {
            if (_currentSolution == null) return HelixPythonNamespace.UI.Interaction.InteractionContext.CompositionOutcome.JustMove;

            TypeModel target = _currentSolution.FindType(targetNodeId);
            if (target == null) return HelixPythonNamespace.UI.Interaction.InteractionContext.CompositionOutcome.JustMove;

            var sources = new List<TypeModel>();
            var sourceNames = new List<string>();
            foreach (var id in sourceNodeIds)
            {
                TypeModel s = _currentSolution.FindType(id);
                if (s != null && s != target)
                {
                    sources.Add(s);
                    sourceNames.Add(s.Name);
                }
            }
            if (sources.Count == 0) return HelixPythonNamespace.UI.Interaction.InteractionContext.CompositionOutcome.JustMove;

            using (var dlg = new HelixPythonNamespace.App.Dialogs.ComposeConfirmDialog(target.Name, sourceNames))
            {
                dlg.ShowDialog(this);
                switch (dlg.Outcome)
                {
                    case HelixPythonNamespace.App.Dialogs.ComposeConfirmDialog.Result.CreateFields:
                        ComposeFieldsOnTarget(target, sources);
                        return HelixPythonNamespace.UI.Interaction.InteractionContext.CompositionOutcome.Composed;
                    case HelixPythonNamespace.App.Dialogs.ComposeConfirmDialog.Result.JustMove:
                        return HelixPythonNamespace.UI.Interaction.InteractionContext.CompositionOutcome.JustMove;
                    default:
                        return HelixPythonNamespace.UI.Interaction.InteractionContext.CompositionOutcome.Cancelled;
                }
            }
        }

        private void ComposeFieldsOnTarget(TypeModel target, IList<TypeModel> sources)
        {
            if (target == null || sources == null || sources.Count == 0) return;

            try
            {
                using (_fileWatcher?.SuspendWhileEditing())
                {
                    var existingNames = new HashSet<string>(System.StringComparer.Ordinal);
                    foreach (var m in target.Members) existingNames.Add(m.Name);

                    int added = 0;
                    foreach (var src in sources)
                    {
                        string baseName = "_" + LowerCamel(src.Name);
                        string fieldName = baseName;
                        int n = 2;
                        while (existingNames.Contains(fieldName))
                        {
                            fieldName = baseName + n;
                            n++;
                        }
                        existingNames.Add(fieldName);

                        MemberModel field = new MemberModel(fieldName, MemberKind.Field)
                        {
                            AccessModifier = AccessModifier.Private,
                            ReturnType = src.Name
                        };
                        _typeService.AddMember(target, field);
                        added++;
                    }

                    RefreshAfterRename();
                    _statusLabel.Text = string.Format("Added {0} private field(s) to {1}", added, target.Name);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Composition failed: " + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string LowerCamel(string name)
        {
            if (string.IsNullOrEmpty(name)) return "field";
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private void EnsureMinimapCreated()
        {
            if (_minimap != null && !_minimap.IsDisposed) return;

            _minimap = new HelixPythonNamespace.UI.Controls.MinimapForm();
            _minimap.SetCanvas(GetActiveCanvas() ?? _canvas);
            // Keep the View ▸ Minimap checkmark in sync however the window is shown or hidden —
            // in particular when the user closes it with the title-bar X (MinimapForm cancels the
            // close and hides), which previously left the menu checked while the minimap was gone.
            _minimap.VisibleChanged += (s, e) =>
            {
                if (_minimapMenuItem != null && _minimap != null && !_minimap.IsDisposed)
                    _minimapMenuItem.Checked = _minimap.Visible;
            };

            Point target;
            if (_settingsManager != null
                && _settingsManager.Settings.MinimapX >= 0
                && _settingsManager.Settings.MinimapY >= 0)
            {
                target = new Point(_settingsManager.Settings.MinimapX, _settingsManager.Settings.MinimapY);
            }
            else
            {
                Rectangle r = Bounds;
                target = new Point(r.Right - 260, r.Top + 80);
            }
            _minimap.Location = target;
        }

        private void ShowMinimap()
        {
            EnsureMinimapCreated();
            if (_minimap == null) return;
            if (!_minimap.Visible) _minimap.Show(this);
            else _minimap.BringToFront();
            if (_minimapMenuItem != null) _minimapMenuItem.Checked = true;
        }

        private void HideMinimap()
        {
            if (_minimap != null && _minimap.Visible) _minimap.Hide();
            if (_minimapMenuItem != null) _minimapMenuItem.Checked = false;
        }

        internal void OnToggleMinimap(object sender, EventArgs e)
        {
            if (_minimap != null && _minimap.Visible) HideMinimap();
            else ShowMinimap();
        }

        private void ShowToolsPalette()
        {
            // "Enable" the palette (user intent); actual visibility is decided by the active tab.
            _toolsPaletteEnabled = true;
            if (_toolsPaletteMenuItem != null) _toolsPaletteMenuItem.Checked = true;
            UpdateToolsPaletteVisibility();
        }

        private void HideToolsPalette()
        {
            _toolsPaletteEnabled = false;
            if (_toolsPaletteMenuItem != null) _toolsPaletteMenuItem.Checked = false;
            UpdateToolsPaletteVisibility();
        }

        /// <summary>True when the Class Designer Canvas is the active top-level tab.</summary>
        private bool IsCanvasTabActive()
        {
            // An inner control with NO page chosen is one that has not realized its handle yet --
            // which is where it is during Form.Load, the moment the palette's saved state is
            // applied. That is not a user sitting on some other surface, so it does not disqualify
            // the canvas: the palette is built, and if the inner control then settles on a GUI
            // Designer document its own SelectedIndexChanged calls straight back here and hides it.
            // Guessing and correcting costs a frame; refusing costs the palette for the session.
            return _mainTabControl != null && _graphicalDesignerTab != null
                && _mainTabControl.SelectedTab == _graphicalDesignerTab
                && (_designerInnerTabControl == null
                    || _designerInnerTabControl.SelectedTab == null
                    || _designerInnerTabControl.SelectedTab == _designerTab);
        }

        /// <summary>
        /// The Tools palette is a canvas tool, so it is shown only when the user has enabled it AND
        /// the Class Designer Canvas tab is active. On any other tab (Editor, Debug, …) it is hidden so
        /// the TopMost palette doesn't float over unrelated content. Called on every tab change and
        /// whenever the enabled state changes; the menu check reflects the user's intent, not the
        /// transient visibility.
        /// </summary>
        private void UpdateToolsPaletteVisibility()
        {
            // BOTH conditions, which is what the summary above always said and the code did not do.
            // Reading only the active tab made the palette ignore the user entirely: closing it
            // with its X set _toolsPaletteEnabled = false and then called straight back into here,
            // which saw the canvas tab still active and showed it again -- so the X appeared to do
            // nothing, the exact defect the UserClosed handler was written to fix. The same
            // omission re-showed a palette the user had turned off in a previous session as soon as
            // they touched the canvas tab.
            // A TabControl with NOTHING selected is mid-mutation, not a user who has navigated
            // away. Removing the selected page -- which startup does, closing the Welcome tab and
            // whatever the solution gate hides -- leaves SelectedTab null for the moment it takes
            // WinForms to settle on the next page, and raises SelectedIndexChanged while it is
            // null. Reading that as "the canvas is not active" hid the palette, and because the
            // control then settles WITHOUT raising the event again, nothing ever brought it back:
            // the palette was gone for the rest of the session. That is the whole of the
            // "sometimes there are icons, sometimes there are none" report -- a session that
            // happened to remove a tab during startup lost it, one that did not kept it.
            //
            // Only the OUTER control is answered here by doing nothing. The inner one is handled
            // in IsCanvasTabActive, because an unchosen inner page has to still allow the palette
            // to be CREATED -- returning early on it instead meant a shell that enabled the palette
            // before that control had settled never built it at all, which is the same bug wearing
            // the other face.
            if (_mainTabControl != null && _mainTabControl.SelectedTab == null) return;

            bool shouldShow = _toolsPaletteEnabled && IsCanvasTabActive();
            // Show and Hide are idempotent, so neither is guarded on the CURRENT visibility. The
            // guards that used to be here read Control.Visible, which is the whole ancestor chain
            // rather than this control's own state -- so on a window that is not itself on screen
            // the palette reported hidden however it had been set, and the hide branch never ran.
            // That made the rule untestable without putting the IDE window on someone's desktop.
            if (shouldShow)
            {
                EnsureToolsPaletteCreated();
                if (_toolsPalette == null) return;
                if (_toolsPalette.Parent != null) _toolsPalette.Show();
                else _toolsPalette.Show(this);
                _toolsPalette.BringToFront();
            }
            else if (_toolsPalette != null)
            {
                _toolsPalette.Hide();
            }
        }

        internal void OnToggleToolsPalette(object sender, EventArgs e)
        {
            if (_toolsPaletteEnabled) HideToolsPalette();
            else ShowToolsPalette();
        }

        /// <summary>Narrowest the Solution Explorer may be restored to: below this its tree is unreadable.</summary>
        internal const int MinExplorerWidth = 160;

        /// <summary>The width it opens at when nothing usable was remembered.</summary>
        internal const int DefaultExplorerWidth = 240;

        /// <summary>
        /// The most of the window the Solution Explorer may claim on startup. A tool window is a
        /// margin around the work, not the work — past this share the document area stops being
        /// somewhere you can read code and the layout is simply broken.
        /// </summary>
        internal const double MaxExplorerWidthFraction = 0.40;

        /// <summary>
        /// The width of the Solution Explorer ITSELF — not the raw splitter distance.
        ///
        /// Docked right the two panes swap roles, so the raw distance measures the TAB area; while
        /// the explorer is floating or collapsed it measures nothing the explorer owns at all.
        /// Persisting the raw number is what let a right-docked or collapsed session come back as a
        /// left dock wide enough to squeeze the editor into a strip. In those states the remembered
        /// width is the honest answer, which is exactly what <see cref="_savedLeftSplitterDistance"/>
        /// holds.
        /// </summary>
        internal int CurrentExplorerWidth()
        {
            if (_leftSplitter == null) return _savedLeftSplitterDistance;
            if (_floatingExplorerForm != null || _treeFloating || _leftSplitter.Panel1Collapsed ||
                (_leftDockPanel != null && !_leftDockPanel.IsExpanded))
            {
                return _savedLeftSplitterDistance;
            }
            if (_explorerDockedRight)
            {
                return Math.Max(0,
                    _leftSplitter.Width - _leftSplitter.SplitterDistance - _leftSplitter.SplitterWidth);
            }
            return _leftSplitter.SplitterDistance;
        }

        /// <summary>
        /// The remembered explorer width, vetted against the window it is about to be applied to.
        ///
        /// A width outside the sane band is treated as a BAD RECORD rather than clamped to the edge
        /// of it: nobody chooses a Solution Explorer that fills four fifths of the window, so
        /// honouring it as far as the cap allows would still open wrong. Falling back to the default
        /// means one bad session — a distance saved on a wider monitor, or before
        /// <see cref="CurrentExplorerWidth"/> existed to save the right number — cannot poison every
        /// launch after it.
        /// </summary>
        internal int RestorableExplorerWidth(int remembered)
        {
            int available = _leftSplitter.Width - _leftSplitter.SplitterWidth;
            if (available <= 0) return remembered;

            int cap = Math.Max(MinExplorerWidth, (int)(available * MaxExplorerWidthFraction));
            if (remembered >= MinExplorerWidth && remembered <= cap) return remembered;
            return Math.Min(DefaultExplorerWidth, cap);
        }

        private void ApplySettingsOnLoad(object sender, EventArgs e)
        {
            if (_settingsManager == null)
            {
                return;
            }

            var settings = _settingsManager.Settings;

            try
            {
                int explorerWidth = RestorableExplorerWidth(settings.LeftSplitterDistance);
                if (explorerWidth > 0 &&
                    explorerWidth < _leftSplitter.Width - _leftSplitter.Panel2MinSize)
                {
                    _leftSplitter.SplitterDistance = explorerWidth;
                }
                if (settings.TreeFloating)
                {
                    _explorerCoordinator.FloatNavigationTree();
                    if (_floatTreeMenuItem != null) _floatTreeMenuItem.Checked = true;
                }
            }
            catch
            {
                // Use defaults if settings are invalid
            }

            _savedLeftSplitterDistance = _leftSplitter.SplitterDistance;

            // AND THE RIGHT DOCK'S WIDTH, which until now was the one panel width that did not
            // survive a quit. Vetted rather than trusted, the same way the explorer's is: a width
            // saved on a wider monitor, or a zero left by a settings file written before this
            // existed, becomes the default instead of a pane nobody can read.
            _savedRightDockWidth = RestorableRightDockWidth(settings.RightDockWidth);

            // Show tools palette if the user had it visible last session (default: visible)
            bool showPalette = settings.ToolsPaletteVisible;
            if (showPalette)
            {
                ShowToolsPalette();
            }
            else
            {
                if (_toolsPaletteMenuItem != null) _toolsPaletteMenuItem.Checked = false;
            }

            // Show minimap if the user had it visible last session (default: hidden)
            if (settings.MinimapVisible)
            {
                ShowMinimap();
            }
            else
            {
                if (_minimapMenuItem != null) _minimapMenuItem.Checked = false;
            }

            // Restore the AI Terminal the user closed last session (default: showing).
            //
            // Only the HIDE direction is applied. The terminal is already docked and visible by the
            // time this runs, so calling ShowAiTerminal() in the other branch would be a no-op
            // except for its FocusAiTerminal() — which would steal the caret into the terminal on
            // every startup, before the user has even chosen a file.
            if (AiTerminalSuppressed || !settings.AiTerminalVisible)
            {
                HideAiTerminal();
            }
            UpdateAiTerminalMenuChecked();

            // Welcome is now a standalone top-level tab. On first run (no saved
            // VisibleTopLevelTabs["welcome"] entry) show it when there's no solution
            // and the user's startup preference allows it. Any saved preference wins
            // — ApplyTabVisibilityFromSettings() below applies it uniformly.
            if (_welcomeTab != null && settings.VisibleTopLevelTabs != null &&
                !settings.VisibleTopLevelTabs.ContainsKey("welcome"))
            {
                bool show = _currentSolution == null && settings.ShowWelcomeOnStartup;
                if (show) ShowTab("welcome");
                else if (_mainTabControl.TabPages.Contains(_welcomeTab)) CloseTab(_welcomeTab);
            }
            if (_welcomePanel != null) _welcomePanel.RefreshRecentFiles();

            // The Development Environment tab is built on demand, so it is not in _knownTabs at
            // this point and ApplyTabVisibilityFromSettings would skip its saved preference
            // entirely. Register it first when the user had it open; the scan still waits for the
            // tab to actually be looked at.
            if (settings.VisibleTopLevelTabs != null)
            {
                bool devEnvVisible;
                if (settings.VisibleTopLevelTabs.TryGetValue("devEnv", out devEnvVisible) && devEnvVisible)
                    EnsureDevelopmentEnvironmentTab();
            }

            ApplyTabVisibilityFromSettings();
            ApplyTabOrderFromSettings();
            // MUST run after the two restores above: they re-add every tab the user had visible, so
            // gating any earlier (e.g. during construction) is immediately undone.
            UpdateMenuWorkflowVisibility();
            SelectStartupTab();

            // Claim the CG designer's tool panels once the layout has settled.
            //
            // Relying on SelectedIndexChanged alone was not enough on the startup path: the restore
            // adds, reorders and re-selects tabs, and whether the CG tab's selection event lands
            // before or after the explorer tab group exists depends on how many other tabs the user
            // had open. Measured from the running IDE - with only the designer's tab restored, it
            // came up with no Toolbox and no Properties anywhere. Doing it explicitly here, after
            // everything else, does not depend on that ordering at all.
            SyncToolboxToActiveTab();
        }

        /// <summary>
        /// Picks the tab the IDE opens on. Restoring visibility calls ShowTab per key, and ShowTab
        /// selects the tab it just added — so without this the startup tab is whichever key happened
        /// to be last in the persisted VisibleTopLevelTabs dictionary (most recently the Terminal,
        /// i.e. the IDE greeted the user with a full-window black console). Land somewhere
        /// deliberate: the Welcome page when nothing is loaded, the designer canvas otherwise.
        /// </summary>
        private void SelectStartupTab()
        {
            TabPage tab = ChooseStartupTab(_mainTabControl, _knownTabs, _currentSolution != null);
            if (tab != null) _mainTabControl.SelectedTab = tab;
        }

        /// <summary>
        /// Leaves the Welcome page once a solution is actually open. SelectStartupTab only runs
        /// during layout restore, so opening a solution afterwards left the user staring at the
        /// Welcome page — with the solution loaded and its menus enabled behind it — until they
        /// navigated away by hand.
        ///
        /// Deliberately only moves OFF Welcome: if the user has already chosen a tab, opening a
        /// solution must not yank them somewhere else.
        /// </summary>
        /// <summary>
        /// Returns to the Welcome page when the solution closes. The mirror of
        /// <see cref="SelectWorkspaceTabAfterSolutionOpen"/>: without it, File ▸ Close Solution left
        /// the user staring at an empty designer canvas with the entry page sitting unselected next
        /// to it.
        ///
        /// The Welcome tab is unconditionally re-shown (re-added to the strip if the user had
        /// previously closed it) — closing a solution always makes it reappear, regardless of its
        /// prior visibility. This uses <see cref="SetTabVisibleWithoutPersisting"/>, NOT
        /// <see cref="ShowTab"/>: this is an automatic reaction to the solution closing, not a
        /// deliberate user action, so it must not overwrite the user's saved View ▸ Panels preference
        /// (the same "gating vs. a real close" distinction <see cref="Coordinators.PanelCoordinator.ApplySolutionGate"/>
        /// already makes for the other workspace tabs) — otherwise the very next app launch with no
        /// solution loaded would see a persisted "hidden" Welcome tab and never show it.
        ///
        /// Only the SELECTION is conditional: it only moves off a workspace tab that has just been
        /// emptied — if the user is on a panel (Output, Terminal, …) they are left where they are,
        /// just with Welcome available again in the strip.
        /// </summary>
        internal void SelectEntryTabAfterSolutionClose()
        {
            if (_mainTabControl == null || _welcomeTab == null) return;

            TabPage current = _mainTabControl.SelectedTab;
            bool onEmptiedWorkspace =
                current == _graphicalDesignerTab || current == _designerTab || current == _editorTab;

            if (!_mainTabControl.TabPages.Contains(_welcomeTab))
            {
                SetTabVisibleWithoutPersisting("welcome", true);
                // TabControl tracks selection by INDEX — inserting Welcome before the current tab's
                // slot can silently shift SelectedTab onto a different page even when the user isn't
                // meant to move at all. Restore the actual previously-selected page explicitly.
                if (!onEmptiedWorkspace && current != null && _mainTabControl.TabPages.Contains(current))
                    _mainTabControl.SelectedTab = current;
            }
            if (onEmptiedWorkspace) _mainTabControl.SelectedTab = _welcomeTab;

            // Refresh EXPLICITLY: re-inserting Welcome at slot 0 can shift the TabControl's
            // index-based selection onto it silently, making the SelectedTab assignment above a
            // no-op — so the SelectedIndexChanged refresh never fires and the panel kept showing
            // its construction-time list (a just-closed solution only appeared after an IDE
            // restart). Idempotent, so the occasional double refresh is harmless.
            if (_welcomePanel != null) _welcomePanel.RefreshRecentFiles();
        }

        /// <summary>
        /// Leaves the Welcome page once a solution is actually open, AND removes it from the tab
        /// strip entirely — a solution being open makes the Welcome/start page irrelevant, the same
        /// way VS/VS Code drop their start page the moment a project loads. Re-added on the next
        /// solution close by <see cref="SelectEntryTabAfterSolutionClose"/>. Uses
        /// <see cref="SetTabVisibleWithoutPersisting"/>, NOT <see cref="CloseTab"/>: opening a
        /// solution is not the user deliberately closing the Welcome panel, so it must not persist as
        /// if it were — a persisted "hidden" entry would then suppress Welcome on every later launch,
        /// even a fresh one with no solution loaded at all.
        ///
        /// Selection is only redirected if the user was actually looking at Welcome; if they'd
        /// already navigated elsewhere before the solution finished loading, opening a solution must
        /// not yank them somewhere else.
        /// </summary>
        internal void SelectWorkspaceTabAfterSolutionOpen()
        {
            if (_mainTabControl == null || _welcomeTab == null) return;

            TabPage previouslySelected = _mainTabControl.SelectedTab;
            bool wasOnWelcome = previouslySelected == _welcomeTab;

            if (_mainTabControl.TabPages.Contains(_welcomeTab))
            {
                SetTabVisibleWithoutPersisting("welcome", false);
                // TabControl tracks selection by INDEX, not by page reference — removing a tab that
                // sits before the selected one silently shifts SelectedTab onto whatever page slid
                // into that index, even though the user's actual tab is untouched and still present.
                // Restore it explicitly rather than trusting the index to still point at the right page.
                if (!wasOnWelcome && previouslySelected != null && _mainTabControl.TabPages.Contains(previouslySelected))
                    _mainTabControl.SelectedTab = previouslySelected;
            }

            if (wasOnWelcome)
            {
                TabPage target = ChooseStartupTab(_mainTabControl, _knownTabs, hasSolution: true);
                if (target != null) _mainTabControl.SelectedTab = target;
            }
        }

        /// <summary>
        /// The startup-tab policy, kept static so it is testable without a live form. Returns the
        /// tab to select, or null when there is nothing to select.
        /// </summary>
        internal static TabPage ChooseStartupTab(
            TabControl tabs, IDictionary<string, TabPage> known, bool hasSolution)
        {
            if (tabs == null || tabs.TabPages.Count == 0 || known == null) return null;

            string[] preferred = hasSolution
                ? new[] { "designer", "editor", "welcome" }
                : new[] { "welcome", "designer", "editor" };

            foreach (string key in preferred)
            {
                if (known.TryGetValue(key, out TabPage tab) && tab != null && tabs.TabPages.Contains(tab))
                {
                    return tab;
                }
            }

            // Every preferred tab was closed by the user — fall back to the leftmost one rather
            // than leaving the selection wherever tab restoration happened to stop.
            return tabs.TabPages[0];
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // The title-bar [X] (and Alt+F4) hides the IDE to the notification area instead of
            // quitting it; File ▸ Exit and the tray menu's Exit set _exitRequested and fall through
            // to the real shutdown below. This is the FIRST thing in the method deliberately —
            // everything after it tears the session down, and a close that is going to be cancelled
            // must not have stopped the debuggee, cancelled the running job or retired the timers on
            // its way to being cancelled. See MainForm.Tray.cs.
            if (ShouldHideInsteadOfClosing(e))
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            // Persist breakpoints (lines + conditions) so they survive a restart.
            try { _debugCoordinator?.PersistBreakpoints(); } catch { }

            // A Ctrl+MouseWheel zoom in the last second may still be waiting on its debounce. The
            // shutdown path below saves settings anyway, so just retire the timer.
            try { _editorFontSaveTimer?.Stop(); _editorFontSaveTimer?.Dispose(); _editorFontSaveTimer = null; } catch { }

            // Kill any live debug session so a paused/running debuggee and its adapter don't
            // outlive the IDE. Debugging is NOT routed through _jobRunner (the Cancel() below
            // won't reach it), and each engine's StopDebugging() terminates the debuggee
            // (e.g. debugpy disconnect+terminate, CUDA emulator abort). Best-effort on shutdown.
            try { _debugCoordinator?.Stop(); } catch { }

            // Each add-in's Shutdown, isolated per add-in, before the settings below are saved so
            // what an add-in stores on the way out is kept.
            try { ShutdownAddIns(); } catch { }

            // Source-file changes autosave; flush any pending debounce and persist silently on
            // close. Layout changes are saved silently too — never prompt the user about saving,
            // just save everything.
            if (e != null && e.CloseReason != CloseReason.WindowsShutDown && !e.Cancel)
            {
                if (CountModifiedEditorTabs() > 0)
                {
                    try { SaveAllModifiedEditorTabs(); } catch { /* best-effort */ }
                }
                if (_solutionDirty)
                {
                    try { SaveLayout(); } catch { /* best-effort */ }
                }
            }

            base.OnFormClosing(e);

            // Stop any in-flight background job (compile / lint / analyze / metrics) or external
            // engine (tests / profiler / package build) so its work — and any child process tree —
            // doesn't outlive the form. Cancel() trips the token for blocking jobs and invokes the
            // registered Stop() (tree-kill) for process engines. Best-effort during shutdown.
            try { _jobRunner?.Cancel(); } catch { }

            if (_settingsManager != null)
            {
                _settingsManager.Settings.LeftSplitterDistance = CurrentExplorerWidth();
                _settingsManager.Settings.RightDockWidth = _savedRightDockWidth;

                if (WindowState == FormWindowState.Maximized)
                {
                    _settingsManager.Settings.WindowMaximized = true;
                }
                else
                {
                    _settingsManager.Settings.WindowMaximized = false;
                    _settingsManager.Settings.WindowWidth = Width;
                    _settingsManager.Settings.WindowHeight = Height;
                }

                if (_currentSolution != null)
                {
                    var checkedProjects = _projectTreePanel.GetCheckedProjects();
                    _settingsManager.Settings.SetVisibleProjects(_currentSolution.FilePath, checkedProjects);
                    // Session restore: open tabs + tree expansion were only persisted by the
                    // explicit File ▸ Close Solution — quitting the IDE with a solution open
                    // (the normal way out) lost them, so every launch started from a blank
                    // editor and a collapsed tree.
                    SaveOpenEditorFilesForSolution(_currentSolution.FilePath);
                    _settingsManager.Settings.SetExpandedTreePaths(
                        _currentSolution.FilePath, _projectTreePanel.GetExpandedPaths());
                }

                // Persist tools palette state
                if (_sharedPreferences != null)
                {
                    _settingsManager.Settings.ActiveTool =
                        _sharedPreferences.ActiveTool == HelixPythonNamespace.UI.Interaction.CanvasTool.Select ? "Select" : "Pan";
                }
                if (_toolsPalette != null && !_toolsPalette.IsDisposed)
                {
                    // Persist the user's INTENT, not the transient visibility (which is false
                    // whenever the active tab isn't the canvas), so the palette returns next session.
                    _settingsManager.Settings.ToolsPaletteVisible = _toolsPaletteEnabled;
                    _settingsManager.Settings.ToolsPaletteX = _toolsPalette.Left;
                    _settingsManager.Settings.ToolsPaletteY = _toolsPalette.Top;
                }
                if (_minimap != null && !_minimap.IsDisposed)
                {
                    _settingsManager.Settings.MinimapVisible = _minimap.Visible;
                    _settingsManager.Settings.MinimapX = _minimap.Left;
                    _settingsManager.Settings.MinimapY = _minimap.Top;
                }

                PersistAiTerminalVisibility();

                _settingsManager.Save();
            }

            if (_currentSolution != null && _layoutStore != null)
            {
                try
                {
                    SyncLayoutFromCanvas();
                    _layoutStore.Save();
                }
                catch
                {
                    // Best effort save
                }
            }

            // Teardown from here down is best-effort, and each step is isolated so one failure
            // cannot skip the rest — the same convention the top of this method already uses for
            // the debug session. Nothing below persists anything the user would lose (settings and
            // layout were written above), so the worst case of a swallowed failure is a stale
            // watcher handle in a process that is exiting anyway. The alternative is worse: an
            // exception here escapes into GlobalExceptionHandler and greets the user with a crash
            // dialog for an IDE they just asked to close.
            try { StopFileWatcher(); } catch { }

            // Unwire main canvas events
            try
            {
                if (_canvas != null)
                {
                    _canvas.SelectionManager.SelectionChanged -= OnSelectionChanged;
                    _canvas.CommandDispatcher.CommandExecuted -= OnCommandExecuted;
                    _canvas.NodeDoubleClicked -= OnNodeDoubleClicked;
                }
            }
            catch { }

            // Close floating explorer if open
            try
            {
                if (_floatingExplorerForm != null)
                {
                    if (_explorerCoordinator != null)
                        _floatingExplorerForm.FormClosing -= _explorerCoordinator.OnFloatingExplorerClosing;
                    _floatingExplorerForm.Close();
                    _floatingExplorerForm = null;
                }
            }
            catch { }

            // Save docking state BEFORE closing floating panes (they reset undocked flags)
            try { SaveDockingState(); } catch { }

            // Close floating panes
            try { CloseFloatingPanes(); } catch { }
        }

        internal void SaveDockingState()
        {
            if (_currentSolution == null || _settingsManager == null) return;

            var dockState = new DockingLayoutState();
            dockState.AnalysisResultsDocked = !_analysisResultsUndocked;
            dockState.BuildResultsDocked = !_buildResultsUndocked;

            // Save floating window positions
            if (_floatingAnalysisResults != null)
            {
                dockState.AnalysisResultsX = _floatingAnalysisResults.Left;
                dockState.AnalysisResultsY = _floatingAnalysisResults.Top;
                dockState.AnalysisResultsWidth = _floatingAnalysisResults.Width;
                dockState.AnalysisResultsHeight = _floatingAnalysisResults.Height;
            }
            if (_floatingBuildResults != null)
            {
                dockState.BuildResultsX = _floatingBuildResults.Left;
                dockState.BuildResultsY = _floatingBuildResults.Top;
                dockState.BuildResultsWidth = _floatingBuildResults.Width;
                dockState.BuildResultsHeight = _floatingBuildResults.Height;
            }

            // Save the active "Show:" filter (field repurposed from the old per-tab design, where
            // it stored which of the four designer sub-tabs was selected).
            if (_designerCategoryCombo != null) dockState.ActiveDesignerTab = _designerCategoryCombo.Text;

            _settingsManager.Settings.SetDockingLayout(_currentSolution.FilePath, dockState);
            _settingsManager.Save();
        }

        internal void RestoreDockingState()
        {
            if (_currentSolution == null || _settingsManager == null) return;

            var dockState = _settingsManager.Settings.GetDockingLayout(_currentSolution.FilePath);

            // Restore undocked panes
            if (!dockState.AnalysisResultsDocked)
            {
                _buildAnalysisCoordinator.UndockPane("AnalysisResults");
                if (_floatingAnalysisResults != null && dockState.AnalysisResultsWidth > 0)
                {
                    _floatingAnalysisResults.SetBounds(
                        dockState.AnalysisResultsX, dockState.AnalysisResultsY,
                        dockState.AnalysisResultsWidth, dockState.AnalysisResultsHeight);
                }
            }
            if (!dockState.BuildResultsDocked)
            {
                _buildAnalysisCoordinator.UndockPane("BuildResults");
                if (_floatingBuildResults != null && dockState.BuildResultsWidth > 0)
                {
                    _floatingBuildResults.SetBounds(
                        dockState.BuildResultsX, dockState.BuildResultsY,
                        dockState.BuildResultsWidth, dockState.BuildResultsHeight);
                }
            }

            // Restore the active "Show:" filter.
            if (!string.IsNullOrEmpty(dockState.ActiveDesignerTab) && _designerCategoryCombo != null)
            {
                int idx = _designerCategoryCombo.Items.IndexOf(dockState.ActiveDesignerTab);
                if (idx >= 0) _designerCategoryCombo.SelectedIndex = idx;
            }
        }
    }
}
