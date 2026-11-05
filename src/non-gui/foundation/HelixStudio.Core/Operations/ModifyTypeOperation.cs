using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Operation to modify an existing type.
    /// </summary>
    public sealed class ModifyTypeOperation : SolutionEditOperation
    {
        public ModifyTypeOperation(string fullyQualifiedName)
        {
            FullyQualifiedName = fullyQualifiedName ?? string.Empty;
        }

        /// <summary>
        /// Fully qualified name of the type to modify.
        /// </summary>
        public string FullyQualifiedName { get; }

        /// <summary>
        /// New name for the type (null if not changing).
        /// </summary>
        public string NewName { get; set; }

        /// <summary>
        /// New namespace for the type (null if not changing).
        /// </summary>
        public string NewNamespace { get; set; }

        /// <summary>
        /// New type kind (null if not changing).
        /// </summary>
        public TypeKind? NewTypeKind { get; set; }

        public override string Description
        {
            get
            {
                if (!string.IsNullOrEmpty(NewName))
                    return string.Format("Rename {0} to {1}", FullyQualifiedName, NewName);
                if (!string.IsNullOrEmpty(NewNamespace))
                    return string.Format("Move {0} to namespace {1}", FullyQualifiedName, NewNamespace);
                if (NewTypeKind.HasValue)
                    return string.Format("Change {0} to {1}", FullyQualifiedName, NewTypeKind.Value);
                return string.Format("Modify type {0}", FullyQualifiedName);
            }
        }
    }
}
