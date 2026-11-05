namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Base class for all solution edit operations.
    /// Edit operations are pure data describing a change to be applied.
    /// </summary>
    public abstract class SolutionEditOperation
    {
        /// <summary>
        /// Human-readable description of this operation.
        /// </summary>
        public abstract string Description { get; }
    }
}
