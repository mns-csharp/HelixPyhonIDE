using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Operation to add a new type to a project.
    /// </summary>
    public sealed class AddTypeOperation : SolutionEditOperation
    {
        public AddTypeOperation(string projectPath, string typeName, string ns, TypeKind typeKind)
        {
            ProjectPath = projectPath ?? string.Empty;
            TypeName = typeName ?? string.Empty;
            Namespace = ns ?? string.Empty;
            TypeKind = typeKind;
        }

        /// <summary>
        /// Path to the project file where the type will be added.
        /// </summary>
        public string ProjectPath { get; }

        /// <summary>
        /// Name of the new type.
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// Namespace for the new type.
        /// </summary>
        public string Namespace { get; }

        /// <summary>
        /// Kind of type to create.
        /// </summary>
        public TypeKind TypeKind { get; }

        /// <summary>
        /// Optional source file path. If not provided, will be generated.
        /// </summary>
        public string SourceFilePath { get; set; }

        public override string Description
        {
            get { return string.Format("Add {0} {1}.{2}", TypeKind, Namespace, TypeName); }
        }
    }
}
