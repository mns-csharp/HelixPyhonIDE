using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Operations;

namespace HelixPythonNamespace.UI.Interaction.Commands
{
    /// <summary>
    /// Command to add a new type to the solution.
    /// </summary>
    public sealed class AddTypeCommand : ISolutionCommand
    {
        private readonly string _projectPath;
        private readonly string _typeName;
        private readonly string _ns;
        private readonly TypeKind _typeKind;
        private string _createdFQN;

        public AddTypeCommand(string projectPath, string typeName, string ns, TypeKind typeKind)
        {
            _projectPath = projectPath;
            _typeName = typeName;
            _ns = ns;
            _typeKind = typeKind;
            _createdFQN = string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName;
        }

        public string Description
        {
            get { return string.Format("Add {0} {1}", _typeKind, _typeName); }
        }

        public SolutionEditOperation CreateEditOperation()
        {
            return new AddTypeOperation(_projectPath, _typeName, _ns, _typeKind);
        }

        public SolutionEditOperation CreateUndoOperation()
        {
            return new DeleteTypeOperation(_createdFQN);
        }
    }
}
