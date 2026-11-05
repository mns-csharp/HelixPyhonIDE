namespace HelixPythonNamespace.App
{
    using HelixPythonNamespace.App.Events;
    using HelixPythonNamespace.App.Services;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.UI.Services;
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// Shared context handed to every coordinator. Bundles the host form, the event
    /// bus, the long-lived services injected into <see cref="MainForm"/>, and the
    /// current solution. Coordinators read services/host from here instead of taking
    /// a growing constructor argument list, and communicate via <see cref="Events"/>.
    /// </summary>
    public sealed class MainFormContext
    {
        public MainFormContext(
            MainForm host,
            IAppEventBus events,
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
            UserSettingsManager settingsManager,
            BuildToolDetector buildToolDetector,
            KeybindingManager keybindings)
        {
            // Pure fan-out: every dependency is stored on a read-only property so
            // coordinators pull what they need from the context instead of the ctor.
            Host = host;
            Events = events;
            SolutionService = solutionService;
            TypeService = typeService;
            ProjectService = projectService;
            LayoutService = layoutService;
            AnalysisEngine = analysisEngine;
            DeletionAnalyzer = deletionAnalyzer;
            RecentFilesManager = recentFilesManager;
            SolutionGenerator = solutionGenerator;
            CodeExporter = codeExporter;
            MetadataExporter = metadataExporter;
            SettingsManager = settingsManager;
            BuildToolDetector = buildToolDetector;
            Keybindings = keybindings;
        }

        public MainForm Host { get; private set; }

        public IAppEventBus Events { get; private set; }

        /// <summary>The solution currently open in the shell, or null when none is loaded.</summary>
        public SolutionModel CurrentSolution { get; set; }

        public ISolutionService SolutionService { get; private set; }

        public ITypeService TypeService { get; private set; }

        public IProjectService ProjectService { get; private set; }

        public ILayoutService LayoutService { get; private set; }

        public IAnalysisEngine AnalysisEngine { get; private set; }

        public DeletionImpactAnalyzer DeletionAnalyzer { get; private set; }

        public RecentFilesManager RecentFilesManager { get; private set; }

        public SolutionGenerator SolutionGenerator { get; private set; }

        public ICodeExporter CodeExporter { get; private set; }

        public IMetadataExporter MetadataExporter { get; private set; }

        public UserSettingsManager SettingsManager { get; private set; }

        public BuildToolDetector BuildToolDetector { get; private set; }

        public KeybindingManager Keybindings { get; private set; }
    }
}
