namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Operation to add a new project to the solution.
    /// </summary>
    public sealed class AddProjectOperation : SolutionEditOperation
    {
        public AddProjectOperation(string projectName)
        {
            ProjectName = projectName ?? string.Empty;
            TargetFramework = "net8.0";
        }

        /// <summary>
        /// Name of the new project.
        /// </summary>
        public string ProjectName { get; }

        /// <summary>
        /// Target framework for the new project.
        /// </summary>
        public string TargetFramework { get; set; }

        /// <summary>
        /// Optional directory for the project. If not set, uses src/{ProjectName}.
        /// </summary>
        public string ProjectDirectory { get; set; }

        public override string Description
        {
            get { return string.Format("Add project {0}", ProjectName); }
        }
    }
}
