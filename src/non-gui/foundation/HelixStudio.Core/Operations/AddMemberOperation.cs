using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Operation to add a member to a type.
    /// </summary>
    public sealed class AddMemberOperation : SolutionEditOperation
    {
        public AddMemberOperation(string typeFQN, string memberName, MemberKind memberKind)
        {
            TypeFullyQualifiedName = typeFQN ?? string.Empty;
            MemberName = memberName ?? string.Empty;
            MemberKind = memberKind;
            AccessModifier = AccessModifier.Public;
        }

        /// <summary>
        /// FQN of the type to add the member to.
        /// </summary>
        public string TypeFullyQualifiedName { get; }

        /// <summary>
        /// Name of the new member.
        /// </summary>
        public string MemberName { get; }

        /// <summary>
        /// Kind of member (Method or Property).
        /// </summary>
        public MemberKind MemberKind { get; }

        /// <summary>
        /// Return type for the member.
        /// </summary>
        public string ReturnType { get; set; }

        /// <summary>
        /// Access modifier for the member.
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

        public override string Description
        {
            get { return string.Format("Add {0} {1} to {2}", MemberKind, MemberName, TypeFullyQualifiedName); }
        }
    }
}
