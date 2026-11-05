namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Operation to delete a type from the solution.
    /// </summary>
    public sealed class DeleteTypeOperation : SolutionEditOperation
    {
        public DeleteTypeOperation(string fullyQualifiedName)
        {
            FullyQualifiedName = fullyQualifiedName ?? string.Empty;
        }

        /// <summary>
        /// Fully qualified name of the type to delete.
        /// </summary>
        public string FullyQualifiedName { get; }

        /// <summary>
        /// Whether to also delete the source file if it contains only this type.
        /// </summary>
        public bool DeleteSourceFile { get; set; }

        public override string Description
        {
            get { return string.Format("Delete type {0}", FullyQualifiedName); }
        }
    }
}
