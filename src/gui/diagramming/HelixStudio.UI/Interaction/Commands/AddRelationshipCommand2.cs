using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Operations;

namespace HelixPythonNamespace.UI.Interaction.Commands
{
    /// <summary>
    /// Command to add a relationship (inheritance/implementation) between types.
    /// Uses the new solution-based architecture.
    /// </summary>
    public sealed class AddRelationshipCommand2 : ISolutionCommand
    {
        private readonly string _sourceFQN;
        private readonly string _targetFQN;
        private readonly RelationshipKind _kind;

        public AddRelationshipCommand2(string sourceFQN, string targetFQN, RelationshipKind kind)
        {
            _sourceFQN = sourceFQN;
            _targetFQN = targetFQN;
            _kind = kind;
        }

        public string Description
        {
            get
            {
                var verb = _kind == RelationshipKind.Implements ? "implements" : "inherits from";
                return string.Format("{0} {1} {2}", GetSimpleName(_sourceFQN), verb, GetSimpleName(_targetFQN));
            }
        }

        public SolutionEditOperation CreateEditOperation()
        {
            return new AddRelationshipOperation(_sourceFQN, _targetFQN, _kind);
        }

        public SolutionEditOperation CreateUndoOperation()
        {
            return new RemoveRelationshipOperation(_sourceFQN, _targetFQN, _kind);
        }

        private static string GetSimpleName(string fqn)
        {
            if (string.IsNullOrEmpty(fqn))
            {
                return fqn;
            }
            var lastDot = fqn.LastIndexOf('.');
            return lastDot >= 0 ? fqn.Substring(lastDot + 1) : fqn;
        }
    }
}
