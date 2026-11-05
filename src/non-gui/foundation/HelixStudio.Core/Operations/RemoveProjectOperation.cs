namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Operation to remove a project from the solution.
    /// </summary>
    public sealed class RemoveProjectOperation : SolutionEditOperation
    {
        public RemoveProjectOperation(string projectPath)
        {
            ProjectPath = projectPath ?? string.Empty;
        }

        /// <summary>
        /// Path to the project to remove.
        /// </summary>
        public string ProjectPath { get; }

        /// <summary>
        /// Whether to also delete the project files from disk.
        /// </summary>
        public bool DeleteFiles { get; set; }

        public override string Description
        {
            get { return string.Format("Remove project {0}", ProjectPath); }
        }
    }
}
