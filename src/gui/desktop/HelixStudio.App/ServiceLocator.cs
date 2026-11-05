namespace HelixPythonNamespace.App
{
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.Persistence;
    using HelixPythonNamespace.Export;
    using HelixPythonNamespace.Analysis;
    using HelixPythonNamespace.Workspace.Services;
    using HelixPythonNamespace.UI.Services;

    // Central factory that maps each service interface to its concrete implementation.
    // Keeping the "new" calls here means Program (and tests) depend only on interfaces,
    // and swapping an implementation is a one-line change in this file.
    internal static class ServiceLocator
    {
        public static IDiagramRepository CreateDiagramRepository()
        {
            return new JsonDiagramRepository();
        }

        public static ICodeExporter CreateCodeExporter()
        {
            return new PythonExportService();
        }

        public static IMetadataExporter CreateMetadataExporter()
        {
            return new JsonMetadataExportService();
        }

        public static IAnalysisEngine CreateAnalysisEngine()
        {
            return new AnalysisEngine();
        }

        public static ISolutionService CreateSolutionService()
        {
            return new SolutionService();
        }

        public static ITypeService CreateTypeService()
        {
            return new TypeService();
        }

        // Overload wiring an optional notifier so TypeService can warn the user when a
        // code-generation feature must be downgraded for the target language version.
        public static ITypeService CreateTypeService(ICodegenDowngradeNotifier downgradeNotifier)
        {
            return new TypeService { DowngradeNotifier = downgradeNotifier };
        }

        public static IProjectService CreateProjectService()
        {
            return new ProjectService();
        }

        public static ILayoutService CreateLayoutService()
        {
            return new LayoutService();
        }

        public static DeletionImpactAnalyzer CreateDeletionImpactAnalyzer()
        {
            return new DeletionImpactAnalyzer();
        }

        public static RecentFilesManager CreateRecentFilesManager()
        {
            return new RecentFilesManager();
        }

        public static SolutionGenerator CreateSolutionGenerator()
        {
            return new SolutionGenerator();
        }
    }
}
