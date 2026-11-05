using System.Collections.Generic;
using System.Threading.Tasks;
using HelixPythonNamespace.Core.Extensibility;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.LanguageServices;

namespace HelixPythonNamespace.App.Connectors
{
    /// <summary>
    /// The single seam between a codebase solution on disk and the Solution Explorer panel,
    /// covering every supported language (Python, CUDA, C++). One home
    /// each for the five decisions that used to live as inline language conditionals behind
    /// MainForm:
    ///
    /// 1. HOW a solution loads — delegated to <see cref="LanguageWorkspaceRegistry"/>'s
    ///    per-language providers (manifest recognition + SolutionModel construction).
    /// 2. WHICH tree icons show — per-language <c>ExplorerIconSet</c>s on the
    ///    <c>LanguageWorkspaceDescriptor</c>, consumed by ProjectTreePanel.
    /// 3. WHICH context menus show — descriptor verb/capability traits, consumed by
    ///    ProjectTreePanel.BuildContextMenuForNode.
    /// 4. The RIGHT verbs on the RIGHT nodes — the same traits keyed by node kind + owning
    ///    project language (startup only on runnables, scaffolding only where a scaffolder
    ///    exists, Debug Tests only where a debug-tests pipeline exists, …).
    /// 5. HOW verbs launch subsystems — the per-language <see cref="ILanguageExplorerConnector"/>
    ///    arms this class dispatches to.
    ///
    /// Regression-containment is the design goal: each language's loading and launching lives
    /// in its own provider/connector pair, so fixing one language's arm cannot change another's
    /// — and MainForm shrinks to one-line dispatches into this class.
    /// </summary>
    internal sealed class CodebaseSolutionToSolutionExplorerPanelConnector
    {
        private readonly ContributionRegistry<ILanguageExplorerConnector> _connectors;
        private readonly ILanguageExplorerConnector _fallback;

        public CodebaseSolutionToSolutionExplorerPanelConnector(ISolutionExplorerHostServices host)
        {
            // Ownership order matters only where languages overlap: CUDA claims kernel-bearing
            // CMake projects ahead of plain C++. Python is also the fallback for a project with
            // an unknown language value — the pre-layer code treated "not C++" as Python, so
            // unknown keeps its historical semantics instead of a null-dispatch.
            var python = new PythonExplorerConnector(host);
            _connectors = new ContributionRegistry<ILanguageExplorerConnector>();
            _connectors.Register("cuda", new CudaExplorerConnector(host), 200);
            _connectors.Register("cpp", new CppExplorerConnector(host), 100);
            _connectors.Register("python", python, 50);
            _fallback = python;
        }

        /// <summary>The connector arm that owns <paramref name="project"/> (never null).</summary>
        public ILanguageExplorerConnector For(ProjectModel project)
        {
            return _connectors.FirstOrDefault(connector => connector.Owns(project)) ?? _fallback;
        }

        // ------------------------------------------------------------------ loading (resp. 1)

        /// <summary>Name-only recognition across every language (recent files, drag-drop).</summary>
        public bool IsRecognizedProjectPath(string path)
        {
            return LanguageWorkspaceRegistry.IsRecognizedProjectPath(path);
        }

        /// <summary>The provider whose language claims <paramref name="path"/>, or null (Import-wizard territory).</summary>
        public ILanguageWorkspaceProvider DetectWorkspaceProvider(string path)
        {
            return LanguageWorkspaceRegistry.TryDetect(path);
        }

        // ------------------------------------------- subsystem dispatch (resp. 4 + 5)

        public Task HandleBuildProjectAsync(ProjectModel project)
        {
            return For(project).BuildProjectAsync(project);
        }

        public void HandleRunTests(ProjectModel project)
        {
            For(project).RunTests(project);
        }

        public void HandleDebugTests(ProjectModel project)
        {
            For(project).DebugTests(project);
        }

        public Task HandleRestorePackagesAsync(ProjectModel project)
        {
            return For(project).RestorePackagesAsync(project);
        }

        public void HandleManagePackages(ProjectModel project)
        {
            For(project).ManagePackages(project);
        }

        public bool CanBeStartup(ProjectModel project)
        {
            return For(project).CanBeStartup(project);
        }

        public void PersistStartupSelection(SolutionModel solution, ProjectModel chosen)
        {
            For(chosen).PersistStartupSelection(solution, chosen);
        }
    }
}
