using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.LanguageServices
{
    /// <summary>
    /// Outcome of <see cref="ILanguageWorkspaceProvider.OpenAsync"/>. Mirrors
    /// <see cref="Services.SolutionLoadResult"/>'s Success/Solution/ErrorMessage shape (so GUI
    /// call sites migrate without churn) and adds <see cref="NativeProject"/> for languages whose
    /// workspace carries a payload beyond the <see cref="SolutionModel"/> — C++/CUDA keeps its
    /// <c>CMakeProjectDescriptor</c> there, which MainForm needs for subsequent real-toolchain
    /// configure/build/run.
    /// </summary>
    public sealed class WorkspaceOpenResult
    {
        private WorkspaceOpenResult() { }

        public static WorkspaceOpenResult Opened(SolutionModel solution, object nativeProject = null)
        {
            return new WorkspaceOpenResult
            {
                Success = solution != null,
                Solution = solution,
                NativeProject = nativeProject,
                ErrorMessage = solution == null ? "Loader produced no solution model." : null
            };
        }

        public static WorkspaceOpenResult Failed(string error)
        {
            return new WorkspaceOpenResult { Success = false, ErrorMessage = error };
        }

        public bool Success { get; private set; }
        public SolutionModel Solution { get; private set; }
        public string ErrorMessage { get; private set; }

        /// <summary>Language-specific workspace payload; null when the language has none.</summary>
        public object NativeProject { get; private set; }
    }
}
