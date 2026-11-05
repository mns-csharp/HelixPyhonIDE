using System.Threading.Tasks;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.App.Connectors
{
    /// <summary>
    /// The narrow slice of MainForm the CodebaseSolutionToSolutionExplorerPanelConnector layer
    /// is allowed to touch. Per-language connectors launch subsystems exclusively through these
    /// members — they never see the form, its panels, or its coordinators directly, so a
    /// connector cannot reach around the layer and re-create the inline cross-language
    /// dispatches this layer replaced.
    ///
    /// The Python/CMake members deliberately wrap MainForm's PROVEN existing implementations
    /// (RunPythonCommandAsync, BuildNativeCMakeTarget, EngineCoordinator.RunTestsForProject,
    /// PersistStartupToToml) rather than re-implementing them: the layer moves the DISPATCH,
    /// not the battle-tested behavior.
    /// </summary>
    internal interface ISolutionExplorerHostServices
    {
        /// <summary>Status-strip text (best-effort; safe with no status label).</summary>
        void SetExplorerStatus(string text);

        /// <summary>Modal info box (used for "toolchain not installed" refusals).</summary>
        void ShowExplorerInfo(string caption, string message);

        /// <summary>Python verb runner — wraps MainForm.RunPythonCommandAsync (build/pip flows).</summary>
        Task RunPythonProjectCommandAsync(ProjectModel project, string[] args, string operationVerb);

        /// <summary>Build one CMake target — wraps MainForm.BuildNativeCMakeTarget.</summary>
        void BuildCMakeTargetFromExplorer(string targetName);

        /// <summary>
        /// Stream an external toolchain process into the Build Results panel (output +
        /// parsed error/warning list + summary/status lines) and report success. This is the
        /// generic pipeline the historical inline `dotnet build` block became; every
        /// non-Python, non-CMake language builds/tests/restores through it.
        /// </summary>
        Task<bool> RunProcessToBuildResultsAsync(string fileName, string arguments, string workingDirectory, string operationTitle);

        /// <summary>Scoped pytest run — wraps EngineCoordinator.RunTestsForProject.</summary>
        void RunPythonTestsForProject(string projectDirectory);

        /// <summary>Scoped real-ctest run for one native C++/CUDA project — wraps
        /// EngineCoordinator.RunNativeCMakeTests (build first, then ctest in the project's
        /// own build subdirectory).</summary>
        void RunNativeTestsForProject(string projectDirectory);

        /// <summary>Scoped debugpy pytest run — wraps DebugCoordinator.StartTests.</summary>
        void DebugPythonTestsForProject(string projectDirectory);

        /// <summary>PSM-10 startup persistence into pyproject.toml (file-watcher-suspended).</summary>
        void PersistPythonStartupSelection(SolutionModel solution, ProjectModel chosen);

        /// <summary>True when the package has a runnable Python entry file (__main__.py / main.py / *_main.py).</summary>
        bool HasPythonMainModule(ProjectModel project);

        /// <summary>Open the package-manager dialog for a project (Python pyproject flow).</summary>
        void ShowManagePackagesDialog(ProjectModel project);
    }
}
