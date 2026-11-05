namespace HelixPythonNamespace.App.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// High-level refactoring operations that compose the low-level ITypeService
    /// + Roslyn rewriters to perform multi-step, multi-file edits.
    /// None of these are undoable via Ctrl+Z in this pass (known limitation —
    /// see Scope 5 plan).
    /// </summary>
    public static class RefactoringService
    {
        public sealed class RefactoringResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public List<TypeModel> AffectedTypes { get; set; }

            public RefactoringResult()
            {
                AffectedTypes = new List<TypeModel>();
            }
        }

        /// <summary>
        /// Create a new interface with the selected members and make the source class implement it.
        /// </summary>
        public static RefactoringResult ExtractInterface(
            ITypeService typeService,
            SolutionModel solution,
            TypeModel source,
            string interfaceName,
            IList<MemberModel> members)
        {
            var result = new RefactoringResult();
            // Validate all preconditions up front; each returns a user-facing Message on failure.
            if (typeService == null || solution == null || source == null)
            {
                result.Message = "Invalid arguments.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(interfaceName))
            {
                result.Message = "Interface name is required.";
                return result;
            }
            if (members == null || members.Count == 0)
            {
                result.Message = "Select at least one member to include in the interface.";
                return result;
            }
            if (source.Project == null)
            {
                result.Message = "Source type has no containing project.";
                return result;
            }

            // Reject duplicate interface name in the same namespace.
            string ns = source.Namespace ?? string.Empty;
            string newFqn = string.IsNullOrEmpty(ns) ? interfaceName : ns + "." + interfaceName;
            if (solution.FindType(newFqn) != null)
            {
                result.Message = string.Format("A type named '{0}' already exists in '{1}'.",
                    interfaceName, string.IsNullOrEmpty(ns) ? "(global)" : ns);
                return result;
            }

            try
            {
                // 1) Create the empty interface file.
                TypeModel iface = typeService.AddInterface(source.Project, interfaceName, ns);

                // 2) Populate it with abstract stubs of the selected members.
                //    Use AddMember which routes through the AddMemberRewriter.
                foreach (var m in members)
                {
                    MemberModel copy = CloneForInterface(m);
                    if (!iface.Members.Any(x => x.Name == copy.Name))
                    {
                        typeService.AddMember(iface, copy);
                    }
                }

                // 3) Add `: IFoo` to the source class. TypeService.AddImplementation
                //    handles source rewriting via ModifyBaseTypeRewriter.
                typeService.AddImplementation(source, iface);

                result.Success = true;
                result.AffectedTypes.Add(source);
                result.AffectedTypes.Add(iface);
                result.Message = string.Format("Extracted interface '{0}' with {1} member(s).",
                    interfaceName, members.Count);
            }
            catch (Exception ex)
            {
                result.Message = "Extract Interface failed: " + ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Move the selected members from the derived class up into the base class.
        /// </summary>
        public static RefactoringResult PullUpMembers(
            ITypeService typeService,
            SolutionModel solution,
            TypeModel derived,
            TypeModel baseType,
            IList<MemberModel> members)
        {
            var result = new RefactoringResult();
            if (typeService == null || solution == null || derived == null || baseType == null)
            {
                result.Message = "Invalid arguments.";
                return result;
            }
            if (members == null || members.Count == 0)
            {
                result.Message = "Select at least one member to pull up.";
                return result;
            }

            try
            {
                // For each member: add a copy to the base, then remove it from the derived class.
                foreach (var m in members)
                {
                    // Skip if the base already has a member with the same name.
                    if (baseType.Members.Any(x => x.Name == m.Name))
                    {
                        continue;
                    }
                    MemberModel copy = CloneForPullUp(m, baseType.TypeKind);
                    typeService.AddMember(baseType, copy);
                    typeService.RemoveMember(derived, m);
                }

                result.Success = true;
                result.AffectedTypes.Add(derived);
                result.AffectedTypes.Add(baseType);
                result.Message = string.Format("Pulled up {0} member(s) from '{1}' to '{2}'.",
                    members.Count, derived.Name, baseType.Name);
            }
            catch (Exception ex)
            {
                result.Message = "Pull Up Member failed: " + ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Move the selected members from the base class down into each selected derived class.
        /// </summary>
        public static RefactoringResult PushDownMembers(
            ITypeService typeService,
            SolutionModel solution,
            TypeModel baseType,
            IList<TypeModel> targets,
            IList<MemberModel> members)
        {
            var result = new RefactoringResult();
            if (typeService == null || solution == null || baseType == null)
            {
                result.Message = "Invalid arguments.";
                return result;
            }
            if (targets == null || targets.Count == 0)
            {
                result.Message = "Select at least one derived class to push members into.";
                return result;
            }
            if (members == null || members.Count == 0)
            {
                result.Message = "Select at least one member to push down.";
                return result;
            }

            try
            {
                // Add to every target first, then remove from the base. Doing the removal only
                // after all copies land avoids losing a member if an intermediate add throws.
                foreach (var target in targets)
                {
                    foreach (var m in members)
                    {
                        // Don't duplicate a member the derived class already declares.
                        if (target.Members.Any(x => x.Name == m.Name))
                        {
                            continue;
                        }
                        MemberModel copy = CloneForPushDown(m);
                        typeService.AddMember(target, copy);
                    }
                }

                // Now strip the pushed-down members from the base class.
                foreach (var m in members)
                {
                    typeService.RemoveMember(baseType, m);
                }

                result.Success = true;
                result.AffectedTypes.Add(baseType);
                foreach (var t in targets) result.AffectedTypes.Add(t);
                result.Message = string.Format(
                    "Pushed {0} member(s) from '{1}' to {2} derived class(es).",
                    members.Count, baseType.Name, targets.Count);
            }
            catch (Exception ex)
            {
                result.Message = "Push Down Member failed: " + ex.Message;
            }
            return result;
        }

        // Build an interface-shaped copy of a member: forced public + abstract, never static,
        // signature preserved. Members become the interface's declarations.
        private static MemberModel CloneForInterface(MemberModel src)
        {
            MemberModel c = new MemberModel(src.Name, src.MemberKind);
            c.ReturnType = src.ReturnType;
            c.AccessModifier = AccessModifier.Public;
            c.IsAbstract = true;
            c.IsStatic = false;
            c.HasGetter = src.HasGetter;
            c.HasSetter = src.HasSetter;
            c.Documentation = src.Documentation;
            foreach (var p in src.Parameters)
            {
                c.Parameters.Add(ClonePackage(p));
            }
            return c;
        }

        // Copy for a pull-up: preserve modifiers but clear the override flag, since the member
        // now lives in the base and no longer overrides anything.
        private static MemberModel CloneForPullUp(MemberModel src, Core.Model.Enums.TypeKind baseKind)
        {
            MemberModel c = CopyBasicFields(src);
            // When pulling up into an abstract class, keep as a concrete member;
            // caller can mark abstract explicitly via the dialog in future.
            c.IsOverride = false;
            return c;
        }

        // Copy for a push-down: the derived copy overrides the base's virtual/abstract member,
        // and is itself concrete (abstract cleared).
        private static MemberModel CloneForPushDown(MemberModel src)
        {
            MemberModel c = CopyBasicFields(src);
            // Pushed-down members are now overrides (if the base had it virtual).
            c.IsOverride = src.IsVirtual || src.IsAbstract;
            c.IsAbstract = false;
            return c;
        }

        // Deep-ish copy of a member: all modifier flags, accessors, docs, and cloned parameters.
        private static MemberModel CopyBasicFields(MemberModel src)
        {
            MemberModel c = new MemberModel(src.Name, src.MemberKind);
            c.ReturnType = src.ReturnType;
            c.AccessModifier = src.AccessModifier;
            c.IsAbstract = src.IsAbstract;
            c.IsVirtual = src.IsVirtual;
            c.IsStatic = src.IsStatic;
            c.IsOverride = src.IsOverride;
            c.IsAsync = src.IsAsync;
            c.IsReadOnly = src.IsReadOnly;
            c.IsConst = src.IsConst;
            c.IsNew = src.IsNew;
            c.IsPartialMember = src.IsPartialMember;
            c.IsExtern = src.IsExtern;
            c.IsSealedOverride = src.IsSealedOverride;
            c.IsExtension = src.IsExtension;
            c.HasGetter = src.HasGetter;
            c.HasSetter = src.HasSetter;
            c.Documentation = src.Documentation;
            foreach (var p in src.Parameters)
            {
                c.Parameters.Add(ClonePackage(p));
            }
            return c;
        }

        private static MethodParameterModel ClonePackage(MethodParameterModel src)
        {
            var p = new MethodParameterModel(src.Name, src.TypeName);
            p.IsRef = src.IsRef;
            p.IsOut = src.IsOut;
            p.IsIn = src.IsIn;
            p.IsParams = src.IsParams;
            p.HasDefaultValue = src.HasDefaultValue;
            p.DefaultValue = src.DefaultValue;
            return p;
        }
    }
}
