using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Operation to add a relationship (inheritance/implementation) between types.
    /// </summary>
    public sealed class AddRelationshipOperation : SolutionEditOperation
    {
        public AddRelationshipOperation(string sourceFQN, string targetFQN, RelationshipKind kind)
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
        /// Kind of relationship to add.
        /// </summary>
        public RelationshipKind RelationshipKind { get; }

        public override string Description
        {
            get
            {
                var verb = RelationshipKind == RelationshipKind.Implements ? "implement" : "inherit from";
                return string.Format("Make {0} {1} {2}", SourceFullyQualifiedName, verb, TargetFullyQualifiedName);
            }
        }
    }
}
