using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Service for manipulating types within a solution.
    /// </summary>
    public interface ITypeService
    {
        /// <summary>
        /// Era-specific codegen confirmation hook. Host (usually MainForm) sets this
        /// after a UI-thread-bound notifier is available. When null, codegen applies
        /// any required downgrades silently.
        /// </summary>
        ICodegenDowngradeNotifier DowngradeNotifier { get; set; }

        /// <summary>
        /// Add a new class to a project.
        /// </summary>
        TypeModel AddClass(ProjectModel project, string name, string ns, bool isAbstract = false);

        /// <summary>
        /// Add a new interface to a project.
        /// </summary>
        TypeModel AddInterface(ProjectModel project, string name, string ns);

        /// <summary>
        /// Add a new nested type inside an existing parent (class / interface / struct).
        /// The nested declaration is inserted into the parent's source file; no new file
        /// is created. The returned TypeModel has ParentTypeFqn set.
        /// </summary>
        TypeModel AddNestedType(TypeModel parent, string name, TypeKind kind);

        /// <summary>
        /// Remove a type from its project.
        /// </summary>
        void RemoveType(ProjectModel project, TypeModel type);

        /// <summary>
        /// Rename a type, updating all references within the solution.
        /// </summary>
        void RenameType(SolutionModel solution, TypeModel type, string newName);

        /// <summary>
        /// Move a type to a different namespace.
        /// </summary>
        void MoveToNamespace(SolutionModel solution, TypeModel type, string newNamespace);

        /// <summary>
        /// Add an inheritance relationship between types.
        /// </summary>
        void AddInheritance(TypeModel derived, TypeModel baseType);

        /// <summary>
        /// Add an interface implementation.
        /// </summary>
        void AddImplementation(TypeModel type, TypeModel interfaceType);

        /// <summary>
        /// Remove an inheritance relationship.
        /// </summary>
        void RemoveInheritance(TypeModel derived, TypeModel baseType);

        /// <summary>
        /// Add a member to a type.
        /// </summary>
        void AddMember(TypeModel type, MemberModel member);

        /// <summary>
        /// Add a member to a type, optionally skipping the in-memory model update.
        /// </summary>
        void AddMember(TypeModel type, MemberModel member, bool addToModel);

        /// <summary>
        /// Remove a member from a type.
        /// </summary>
        void RemoveMember(TypeModel type, MemberModel member);

        /// <summary>
        /// Remove a member from a type, optionally skipping the in-memory model update.
        /// </summary>
        void RemoveMember(TypeModel type, MemberModel member, bool removeFromModel);

        /// <summary>
        /// Change the kind of a type (e.g., ConcreteClass to AbstractClass).
        /// </summary>
        void ChangeTypeKind(TypeModel type, HelixPythonNamespace.Core.Model.Enums.TypeKind newKind);

        /// <summary>
        /// Update the generic type parameters in the source file to match the model.
        /// </summary>
        void UpdateGenericParameters(TypeModel type);

        /// <summary>
        /// Update the generic type parameters in the source file to match the model,
        /// and propagate changes to derived types in the solution.
        /// </summary>
        void UpdateGenericParameters(TypeModel type, SolutionModel solution);
    }
}
