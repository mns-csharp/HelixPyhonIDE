using System.Threading.Tasks;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Result of loading a solution.
    /// </summary>
    public sealed class SolutionLoadResult
    {
        public SolutionLoadResult(SolutionModel solution)
        {
            Solution = solution;
            Success = solution != null;
        }

        public SolutionLoadResult(string error)
        {
            Success = false;
            ErrorMessage = error;
        }

        public bool Success { get; }
        public SolutionModel Solution { get; }
        public string ErrorMessage { get; }

        /// <summary>
        /// Structure parity (PY8004): whether the Solution Explorer can draw this solution as it
        /// actually is on disk. Non-null only for TOML-format solutions — rules 2 and 5 of the
        /// parity contract are scoped to "when loading a TOML solution", and a natively-parsed
        /// <c>.sln</c> has no canonical member layout to compare against.
        ///
        /// It is attached to the LOAD RESULT rather than checked by the caller so that every route
        /// into a solution carries it: menu, Recent Files, drag-and-drop and command-line open all
        /// funnel through <see cref="ISolutionService.LoadAsync"/>, and a check bolted onto one
        /// menu handler would be bypassed by the other three.
        ///
        /// <see cref="StructureParityReport.IsEmpty"/> is the only clean answer. The service does
        /// not act on it — verification is non-GUI and repair needs the user's consent, so the
        /// decision belongs to whoever owns a window.
        /// </summary>
        public HelixPythonNamespace.Workspace.Structure.StructureParityReport StructureParity { get; internal set; }
    }

    /// <summary>
    /// Primary service for loading and managing solutions.
    /// </summary>
    public interface ISolutionService
    {
        /// <summary>
        /// Load a solution from disk asynchronously.
        /// </summary>
        Task<SolutionLoadResult> LoadAsync(string slnPath);

        /// <summary>
        /// Save all pending changes to the solution.
        /// </summary>
        void Save(SolutionModel model);

        /// <summary>
        /// Create a new solution at the specified path.
        /// </summary>
        SolutionModel CreateNew(string directoryPath, string name);

        /// <summary>
        /// Reload the solution from disk, preserving layout.
        /// </summary>
        Task<SolutionLoadResult> ReloadAsync(SolutionModel model);
    }
}
