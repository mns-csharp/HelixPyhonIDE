using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Operation to remove a relationship between types.
    /// </summary>
    public sealed class RemoveRelationshipOperation : SolutionEditOperation
    {
        public RemoveRelationshipOperation(string sourceFQN, string targetFQN, RelationshipKind kind)
        {
            SourceFullyQualifiedName = sourceFQN ?? string.Empty;
            TargetFullyQualifiedName = targetFQN ?? string.Empty;
            RelationshipKind = kind;
        }

        /// <summary>
        /// FQN of the derived/implementing type.
        /// </summary>
        public string SourceFullyQualifiedName { get; }

        /// <summary>
        /// FQN of the base/interface type.
        /// </summary>
        public string TargetFullyQualifiedName { get; }

        /// <summary>
        /// Kind of relationship to remove.
        /// </summary>
        public RelationshipKind RelationshipKind { get; }

        public override string Description
        {
            get
            {
                var verb = RelationshipKind == RelationshipKind.Implements ? "implementation of" : "inheritance from";
                return string.Format("Remove {0}'s {1} {2}", SourceFullyQualifiedName, verb, TargetFullyQualifiedName);
            }
        }
    }
}
