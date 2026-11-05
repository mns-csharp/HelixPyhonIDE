using HelixPythonNamespace.Core.Operations;

namespace HelixPythonNamespace.UI.Interaction
{
    /// <summary>
    /// Interface for commands that modify the solution.
    /// Commands produce edit operations that can be executed and undone.
    /// </summary>
    public interface ISolutionCommand
    {
        /// <summary>
        /// Human-readable description of the command.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Create the edit operation to apply this command.
        /// </summary>
        SolutionEditOperation CreateEditOperation();

        /// <summary>
        /// Create the undo operation for this command.
        /// </summary>
        SolutionEditOperation CreateUndoOperation();
    }
}
