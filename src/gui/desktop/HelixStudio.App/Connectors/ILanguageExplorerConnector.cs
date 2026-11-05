using System.Threading.Tasks;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.App.Connectors
{
    /// <summary>
    /// One language's arm of the CodebaseSolutionToSolutionExplorerPanelConnector: how Solution
    /// Explorer context-menu verbs on that language's nodes launch the IDE's subsystems
    /// (build runners, test runners, package managers, startup persistence). Menu VISIBILITY
    /// is decided earlier by the language's <c>LanguageWorkspaceDescriptor</c> (a verb hidden
    /// there never reaches its connector method); connector methods still degrade gracefully —
    /// a status-line refusal, never a crash and never another language's pipeline.
    /// </summary>
    internal interface ILanguageExplorerConnector
    {
        ProjectLanguage Language { get; }

        /// <summary>
        /// True when this connector owns <paramref name="project"/>. Usually a Language match;
        /// the CUDA connector additionally requires kernel sources, letting it claim
        /// CUDA-bearing CMake projects ahead of the plain C++ connector.
        /// </summary>
        bool Owns(ProjectModel project);

        /// <summary>Startup-eligibility gate behind the "Set as Startup" verb.</summary>
        bool CanBeStartup(ProjectModel project);

        /// <summary>
        /// Persist a startup selection into the language's own manifest format, if it has one
        /// (Python's pyproject.toml). Languages without a manifest marker no-op here — the
        /// per-solution user setting MainForm always writes is their persistence.
        /// </summary>
        void PersistStartupSelection(SolutionModel solution, ProjectModel chosen);

        /// <summary>"Build Project" on a project node.</summary>
        Task BuildProjectAsync(ProjectModel project);

        /// <summary>"Run Tests" on a project node.</summary>
        void RunTests(ProjectModel project);

        /// <summary>"Debug Tests" on a project node (only Python surfaces the verb today).</summary>
        void DebugTests(ProjectModel project);

        /// <summary>The language's restore-dependencies verb (pip / cargo fetch / dotnet restore / mvn / Pkg).</summary>
        Task RestorePackagesAsync(ProjectModel project);

        /// <summary>The language's manage-packages dialog (only Python surfaces the verb today).</summary>
        void ManagePackages(ProjectModel project);
    }
}
