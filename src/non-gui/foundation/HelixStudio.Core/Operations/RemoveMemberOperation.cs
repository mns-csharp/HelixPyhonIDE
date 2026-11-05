namespace HelixPythonNamespace.Core.Operations
{
    /// <summary>
    /// Operation to remove a member from a type.
    /// </summary>
    public sealed class RemoveMemberOperation : SolutionEditOperation
    {
        public RemoveMemberOperation(string typeFQN, string memberName)
        {
            TypeFullyQualifiedName = typeFQN ?? string.Empty;
            MemberName = memberName ?? string.Empty;
        }

        /// <summary>
        /// FQN of the type containing the member.
        /// </summary>
        public string TypeFullyQualifiedName { get; }

        /// <summary>
        /// Name of the member to remove.
        /// </summary>
        public string MemberName { get; }

        public override string Description
        {
            get { return string.Format("Remove member {0} from {1}", MemberName, TypeFullyQualifiedName); }
        }
    }
}
