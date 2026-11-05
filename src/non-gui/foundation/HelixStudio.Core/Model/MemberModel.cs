using System.Collections.Generic;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Represents a member (method or property) on a type.
    /// </summary>
    public sealed class MemberModel
    {
        public MemberModel(string name, MemberKind memberKind)
        {
            Name = name ?? string.Empty;
            MemberKind = memberKind;
            Parameters = new List<MethodParameterModel>();
            Attributes = new List<string>();
            AccessModifier = AccessModifier.Public;
            HasGetter = true;
        }

        /// <summary>
        /// Name of the member.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Kind of member (Method or Property).
        /// </summary>
        public MemberKind MemberKind { get; set; }

        /// <summary>
        /// Return type (for methods) or property type.
        /// </summary>
        public string ReturnType { get; set; }

        /// <summary>
        /// Access modifier (public, protected, internal, private).
        /// </summary>
        public AccessModifier AccessModifier { get; set; }

        /// <summary>
        /// Whether the member is abstract.
        /// </summary>
        public bool IsAbstract { get; set; }

        /// <summary>
        /// Whether the member is virtual.
        /// </summary>
        public bool IsVirtual { get; set; }

        /// <summary>
        /// Whether the member is static.
        /// </summary>
        public bool IsStatic { get; set; }

        /// <summary>
        /// Whether the member overrides a base member.
        /// </summary>
        public bool IsOverride { get; set; }

        /// <summary>
        /// Whether the property has a getter (properties only).
        /// </summary>
        public bool HasGetter { get; set; }

        /// <summary>
        /// Whether the property has a setter (properties only).
        /// </summary>
        public bool HasSetter { get; set; }

        /// <summary>
        /// Method parameters (methods only).
        /// </summary>
        public List<MethodParameterModel> Parameters { get; }

        /// <summary>
        /// Line number where the member declaration starts in the source file.
        /// </summary>
        public int SourceLineNumber { get; set; }

        /// <summary>
        /// Whether the member is async (methods only).
        /// </summary>
        public bool IsAsync { get; set; }

        /// <summary>
        /// Whether the member is marked deprecated (Python: PEP 702 <c>@deprecated</c> decorator).
        /// </summary>
        public bool IsDeprecated { get; set; }

        /// <summary>
        /// Whether the field/property is readonly (fields, properties with only getter).
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// Whether the field/member is declared const.
        /// </summary>
        public bool IsConst { get; set; }

        /// <summary>
        /// Whether this is an explicit interface implementation.
        /// Contains the FQN of the interface, or null for standard members.
        /// </summary>
        public string ExplicitInterfaceFqn { get; set; }

        /// <summary>
        /// Attributes applied to the member (e.g., "Obsolete", "JsonIgnore").
        /// Stored as raw attribute text without the surrounding brackets.
        /// </summary>
        public List<string> Attributes { get; private set; }

        /// <summary>
        /// For operators and indexers, the operator symbol ("+", "-", "[]", etc.).
        /// Ignored for other member kinds.
        /// </summary>
        public string OperatorSymbol { get; set; }

        /// <summary>
        /// Full XML documentation comment block (as captured from source leading trivia),
        /// e.g. "/// <summary>...</summary>". Null when absent.
        /// </summary>
        public string Documentation { get; set; }

        /// <summary>Method hides an inherited member with the same signature (`new` keyword).</summary>
        public bool IsNew { get; set; }

        /// <summary>Partial method declaration (signature only, no body).</summary>
        public bool IsPartialMember { get; set; }

        /// <summary>`extern` method (body provided externally, usually via P/Invoke).</summary>
        public bool IsExtern { get; set; }

        /// <summary>`sealed` modifier on an override, preventing further overriding. Requires IsOverride.</summary>
        public bool IsSealedOverride { get; set; }

        /// <summary>Extension method (first parameter takes `this` keyword). Implies IsStatic.</summary>
        public bool IsExtension { get; set; }

        /// <summary>C# 9+ init-only property accessor. Mutually exclusive with HasSetter.</summary>
        public bool HasInitAccessor { get; set; }
    }
}
