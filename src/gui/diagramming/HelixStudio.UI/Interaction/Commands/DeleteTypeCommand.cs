using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Operations;

namespace HelixPythonNamespace.UI.Interaction.Commands
{
    /// <summary>
    /// Command to delete a type from the solution.
    /// </summary>
    public sealed class DeleteTypeCommand : ISolutionCommand
    {
        private readonly string _fullyQualifiedName;
        private readonly string _projectPath;
        private readonly string _typeName;
        private readonly string _ns;
        private readonly TypeKind _typeKind;

        public DeleteTypeCommand(TypeModel type)
        {
            _fullyQualifiedName = type.FullyQualifiedName;
            _projectPath = type.Project.FilePath;
            _typeName = type.Name;
            _ns = type.Namespace;
            _typeKind = type.TypeKind;
        }

        public string Description
        {
            get { return string.Format("Delete {0}", _typeName); }
        }

        public SolutionEditOperation CreateEditOperation()
        {
            return new DeleteTypeOperation(_fullyQualifiedName);
        }

        public SolutionEditOperation CreateUndoOperation()
        {
            return new AddTypeOperation(_projectPath, _typeName, _ns, _typeKind);
        }
    }
}
