using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Service for managing layout persistence.
    /// </summary>
    public interface ILayoutService
    {
        /// <summary>
        /// Load layout for a solution.
        /// </summary>
        LayoutModel Load(string solutionPath);

        /// <summary>
        /// Save layout for a solution.
        /// </summary>
        void Save(LayoutModel layout);

        /// <summary>
        /// Get the layout file path for a solution.
        /// </summary>
        string GetLayoutPath(string solutionPath);
    }
}
