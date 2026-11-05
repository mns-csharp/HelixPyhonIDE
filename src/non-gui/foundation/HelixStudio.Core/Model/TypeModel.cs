using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Represents a type (class, interface, etc.) in the solution.
    /// Identity: FullyQualifiedName (Namespace.TypeName)
    /// </summary>
    public sealed class TypeModel
    {
        public TypeModel(string name, string ns, TypeKind typeKind, ProjectModel project)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));

            Name = name;
            Namespace = ns ?? string.Empty;
            TypeKind = typeKind;
            Project = project ?? throw new ArgumentNullException(nameof(project));
            Members = new List<MemberModel>();
            BaseTypes = new List<TypeReference>();
            GenericParameters = new List<GenericParameterModel>();
            EnumValues = new List<EnumValueModel>();
        }

        /// <summary>
        /// Values of this enum type. Populated only when TypeKind == Enum.
        /// </summary>
        public List<EnumValueModel> EnumValues { get; }

        /// <summary>
        /// Underlying integer type for enum declarations (e.g., "int", "byte").
        /// Null for non-enum types or when the declaration omits it.
        /// </summary>
        public string EnumUnderlyingType { get; set; }

        /// <summary>
        /// For TypeKind.Delegate: signature's return type (e.g., "void", "int").
        /// Null for non-delegate types.
        /// </summary>
        public string DelegateReturnType { get; set; }

        /// <summary>
        /// For TypeKind.Delegate: signature parameters. Empty for non-delegate types.
        /// </summary>
        public List<MethodParameterModel> DelegateParameters { get; } = new List<MethodParameterModel>();

        /// <summary>
        /// Whether this is a record (positional or nominal). Meaningful when TypeKind == Record.
        /// </summary>
        public bool IsPositionalRecord { get; set; }

        /// <summary>
        /// Simple name of the type (without namespace).
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Namespace containing this type.
        /// </summary>
        public string Namespace { get; set; }

        /// <summary>
        /// FQN of the enclosing type for nested types (e.g. `Acme.Foo` for a nested `Bar`).
        /// Null for top-level types. Uses the same `+` separator as the final FQN so a nested
        /// type's ParentTypeFqn can itself contain `+` when the parent is itself nested.
        /// </summary>
        public string ParentTypeFqn { get; set; }

        /// <summary>
        /// Fully qualified name — primary identity. For top-level types this is
        /// `Namespace.Name`. For nested types it is `ParentTypeFqn+Name` (reflection-style
        /// separator). The `+` keeps nested FQNs unambiguous against namespace paths and
        /// avoids breaking existing `.csooptool-layout.json` keys which never contain `+`.
        /// </summary>
        public string FullyQualifiedName
        {
            get
            {
                if (!string.IsNullOrEmpty(ParentTypeFqn))
                    return ParentTypeFqn + "+" + Name;
                return string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;
            }
        }

        /// <summary>True when this type is nested inside another type.</summary>
        public bool IsNested
        {
            get { return !string.IsNullOrEmpty(ParentTypeFqn); }
        }

        /// <summary>
        /// Kind of type (Interface, AbstractClass, ConcreteClass).
        /// </summary>
        public TypeKind TypeKind { get; set; }

        /// <summary>
        /// Absolute path to the source file containing this type.
        /// </summary>
        public string SourceFilePath { get; set; }

        /// <summary>
        /// Project containing this type (back-reference).
        /// </summary>
        public ProjectModel Project { get; }

        /// <summary>
        /// Members (properties, methods) defined on this type.
        /// </summary>
        public List<MemberModel> Members { get; }

        /// <summary>
        /// Base types (classes and interfaces) this type inherits/implements.
        /// Relationships are derived from these references.
        /// </summary>
        public List<TypeReference> BaseTypes { get; }

        /// <summary>
        /// Generic type parameters (e.g., T, TKey, TValue).
        /// </summary>
        public List<GenericParameterModel> GenericParameters { get; }

        /// <summary>
        /// Whether this type is partial.
        /// </summary>
        public bool IsPartial { get; set; }

        /// <summary>
        /// Whether this type is sealed.
        /// </summary>
        public bool IsSealed { get; set; }

        /// <summary>
        /// Whether this type is static.
        /// </summary>
        public bool IsStatic { get; set; }

        /// <summary>
        /// Line number where the type declaration starts in the source file.
        /// </summary>
        public int SourceLineNumber { get; set; }

        /// <summary>
        /// Full XML documentation comment block (as captured from source leading trivia),
        /// e.g. "/// <summary>...</summary>". Null when absent. Round-tripped through
        /// the C# code-gen so doc-comments survive edit cycles.
        /// </summary>
        public string Documentation { get; set; }
    }
}
