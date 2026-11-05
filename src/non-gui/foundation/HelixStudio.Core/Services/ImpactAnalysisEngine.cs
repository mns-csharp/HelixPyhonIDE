using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Services
{
    /// <summary>
    /// Analyzes upstream type changes and identifies compliance gaps in downstream types.
    /// Supports both C# and C++ types through the unified TypeModel.
    ///
    /// For interfaces: implementers must provide all declared members.
    /// For abstract classes: derived classes must override all abstract members.
    /// For base classes: signature changes to virtual/override members propagate.
    ///
    /// Each gap is classified as AutoFix, NeedsAttention, or Ambiguous.
    /// </summary>
    public sealed class ImpactAnalysisEngine
    {
        private readonly InheritanceGraph _graph;

        public ImpactAnalysisEngine(InheritanceGraph graph)
        {
            _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        }

        /// <summary>
        /// Analyze the impact of the current state of an upstream type on all its descendants.
        /// Finds missing implementations, signature mismatches, etc.
        /// </summary>
        public ComplianceReport AnalyzeCompliance(TypeModel upstreamType)
        {
            var report = new ComplianceReport(upstreamType);
            var descendants = _graph.GetAllDescendants(upstreamType);

            foreach (var descendant in descendants)
            {
                AnalyzeDescendant(upstreamType, descendant, report);
            }

            return report;
        }

        /// <summary>
        /// Analyze specific member addition on upstream type.
        /// </summary>
        public ComplianceReport AnalyzeMemberAdded(TypeModel upstreamType, MemberModel addedMember)
        {
            var report = new ComplianceReport(upstreamType);
            var descendants = _graph.GetAllDescendants(upstreamType);

            foreach (var descendant in descendants)
            {
                // Skip interfaces inheriting from interfaces - they don't need implementations
                if (descendant.TypeKind == TypeKind.Interface)
                    continue;

                var existingMember = FindMatchingMember(descendant, addedMember);
                if (existingMember == null)
                {
                    // Missing implementation
                    var fixKind = CanAutoFix(upstreamType, descendant, addedMember)
                        ? ComplianceFixKind.AutoFix
                        : ComplianceFixKind.NeedsAttention;

                    var gap = new ComplianceGap(
                        upstreamType,
                        descendant,
                        UpstreamChangeKind.MemberAdded,
                        fixKind,
                        string.Format("Missing implementation of '{0}' from {1}",
                            addedMember.Name, upstreamType.Name));
                    gap.UpstreamMember = addedMember;
                    report.Gaps.Add(gap);
                }
            }

            return report;
        }

        /// <summary>
        /// Analyze specific member signature change on upstream type.
        /// </summary>
        public ComplianceReport AnalyzeSignatureChanged(
            TypeModel upstreamType,
            MemberModel oldMember,
            MemberModel newMember)
        {
            var report = new ComplianceReport(upstreamType);
            var descendants = _graph.GetAllDescendants(upstreamType);

            foreach (var descendant in descendants)
            {
                if (descendant.TypeKind == TypeKind.Interface)
                    continue;

                var existingMember = FindMatchingMember(descendant, oldMember);
                if (existingMember != null)
                {
                    // Has an override/implementation that needs updating
                    var fixKind = ComplianceFixKind.NeedsAttention;

                    // Simple cases we can auto-fix
                    if (IsSimpleSignatureChange(oldMember, newMember))
                    {
                        fixKind = ComplianceFixKind.AutoFix;
                    }

                    var gap = new ComplianceGap(
                        upstreamType,
                        descendant,
                        UpstreamChangeKind.SignatureChanged,
                        fixKind,
                        string.Format("Signature of '{0}' changed in {1} - downstream override needs update",
                            oldMember.Name, upstreamType.Name));
                    gap.UpstreamMember = newMember;
                    gap.DownstreamMember = existingMember;
                    report.Gaps.Add(gap);
                }
            }

            return report;
        }

        /// <summary>
        /// Analyze member rename on upstream type.
        /// </summary>
        public ComplianceReport AnalyzeMemberRenamed(
            TypeModel upstreamType,
            string oldName,
            string newName,
            MemberModel renamedMember)
        {
            var report = new ComplianceReport(upstreamType);
            var descendants = _graph.GetAllDescendants(upstreamType);

            foreach (var descendant in descendants)
            {
                if (descendant.TypeKind == TypeKind.Interface)
                    continue;

                // Look for a member matching the old name
                var existingMember = descendant.Members
                    .FirstOrDefault(m => m.Name == oldName && m.MemberKind == renamedMember.MemberKind);

                if (existingMember != null)
                {
                    // Auto-fix when: override tracking a base rename, or interface
                    // implementation tracking an interface rename
                    var fixKind = (existingMember.IsOverride || upstreamType.TypeKind == TypeKind.Interface)
                        ? ComplianceFixKind.AutoFix
                        : ComplianceFixKind.NeedsAttention;

                    var gap = new ComplianceGap(
                        upstreamType,
                        descendant,
                        UpstreamChangeKind.MemberRenamed,
                        fixKind,
                        string.Format("Member '{0}' renamed to '{1}' in {2} - downstream needs update",
                            oldName, newName, upstreamType.Name));
                    gap.UpstreamMember = renamedMember;
                    gap.DownstreamMember = existingMember;
                    report.Gaps.Add(gap);
                }
                else if (upstreamType.TypeKind == TypeKind.Interface)
                {
                    // Interface member renamed but no matching implementation found
                    // Need to add the new-named implementation
                    var gap = new ComplianceGap(
                        upstreamType,
                        descendant,
                        UpstreamChangeKind.MemberRenamed,
                        ComplianceFixKind.AutoFix,
                        string.Format("Missing implementation of renamed member '{0}' from {1}",
                            newName, upstreamType.Name));
                    gap.UpstreamMember = renamedMember;
                    report.Gaps.Add(gap);
                }
            }

            return report;
        }

        private void AnalyzeDescendant(TypeModel upstream, TypeModel descendant, ComplianceReport report)
        {
            // Skip interfaces - they propagate contracts but don't implement
            if (descendant.TypeKind == TypeKind.Interface)
                return;

            // Check each required member from upstream
            foreach (var upstreamMember in upstream.Members)
            {
                // Only check contractual members
                if (!IsContractualMember(upstreamMember, upstream.TypeKind))
                    continue;

                var downstreamMember = FindMatchingMember(descendant, upstreamMember);

                if (downstreamMember == null)
                {
                    // Missing implementation
                    var fixKind = CanAutoFix(upstream, descendant, upstreamMember)
                        ? ComplianceFixKind.AutoFix
                        : ComplianceFixKind.NeedsAttention;

                    var gap = new ComplianceGap(
                        upstream,
                        descendant,
                        UpstreamChangeKind.MemberAdded,
                        fixKind,
                        string.Format("Missing implementation of '{0}' from {1}",
                            upstreamMember.Name, upstream.Name));
                    gap.UpstreamMember = upstreamMember;
                    report.Gaps.Add(gap);
                }
                else
                {
                    // Check signature match
                    if (!SignaturesMatch(upstreamMember, downstreamMember))
                    {
                        var gap = new ComplianceGap(
                            upstream,
                            descendant,
                            UpstreamChangeKind.SignatureChanged,
                            ComplianceFixKind.NeedsAttention,
                            string.Format("Signature mismatch for '{0}' between {1} and {2}",
                                upstreamMember.Name, upstream.Name, descendant.Name));
                        gap.UpstreamMember = upstreamMember;
                        gap.DownstreamMember = downstreamMember;
                        report.Gaps.Add(gap);
                    }

                    // Check access modifier compatibility
                    if (!IsAccessCompatible(upstreamMember.AccessModifier, downstreamMember.AccessModifier))
                    {
                        var gap = new ComplianceGap(
                            upstream,
                            descendant,
                            UpstreamChangeKind.AccessModifierChanged,
                            ComplianceFixKind.NeedsAttention,
                            string.Format("Access modifier mismatch for '{0}': {1} in {2} vs {3} in {4}",
                                upstreamMember.Name,
                                upstreamMember.AccessModifier, upstream.Name,
                                downstreamMember.AccessModifier, descendant.Name));
                        gap.UpstreamMember = upstreamMember;
                        gap.DownstreamMember = downstreamMember;
                        report.Gaps.Add(gap);
                    }
                }
            }
        }

        /// <summary>
        /// Determines if a member is part of the type's contract (must be implemented downstream).
        /// </summary>
        private bool IsContractualMember(MemberModel member, TypeKind ownerKind)
        {
            if (ownerKind == TypeKind.Interface)
            {
                // All interface members are contractual
                return true;
            }

            if (ownerKind == TypeKind.AbstractClass)
            {
                // Only abstract members are contractual
                return member.IsAbstract;
            }

            // Concrete class virtual methods are overridable but not contractual
            return false;
        }

        private MemberModel FindMatchingMember(TypeModel type, MemberModel target)
        {
            return type.Members.FirstOrDefault(m =>
                m.Name == target.Name &&
                m.MemberKind == target.MemberKind);
        }

        private bool SignaturesMatch(MemberModel upstream, MemberModel downstream)
        {
            if (upstream.MemberKind != downstream.MemberKind)
                return false;

            if (upstream.MemberKind == MemberKind.Method)
            {
                // Check return type
                if (!TypeNamesMatch(upstream.ReturnType, downstream.ReturnType))
                    return false;

                // Check parameter count
                if (upstream.Parameters.Count != downstream.Parameters.Count)
                    return false;

                // Check parameter types
                for (int i = 0; i < upstream.Parameters.Count; i++)
                {
                    if (!TypeNamesMatch(upstream.Parameters[i].TypeName, downstream.Parameters[i].TypeName))
                        return false;
                }
            }
            else if (upstream.MemberKind == MemberKind.Property)
            {
                if (!TypeNamesMatch(upstream.ReturnType, downstream.ReturnType))
                    return false;
            }

            return true;
        }

        private bool TypeNamesMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return true;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

            var normA = NormalizeTypeName(a);
            var normB = NormalizeTypeName(b);

            return string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes type names across C# and C++ for cross-language comparison.
        /// Strips qualifiers, collapses pointer/reference decorators, maps common
        /// typedefs and STL wrapper types to canonical names.
        /// </summary>
        private string NormalizeTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return "";

            var result = typeName.Trim();

            // Strip C++ qualifiers: const, volatile
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\bconst\b", "");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\bvolatile\b", "");

            // Strip pointer/reference decorators
            result = result.Replace("*", "").Replace("&", "");

            // Collapse whitespace
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ").Trim();

            // Convert C++ scope operator to dot notation
            result = result.Replace("::", ".");

            // C++ to C# type mappings
            var mapped = MapCrossLanguageType(result);
            if (mapped != null)
                return mapped;

            // Strip common namespace prefixes for comparison
            if (result.StartsWith("std."))
                result = result.Substring(4);

            // Strip leading namespace for simple comparison
            var lastDot = result.LastIndexOf('.');
            if (lastDot >= 0)
            {
                var simpleName = result.Substring(lastDot + 1);
                // Only strip namespace if the simple name is meaningful
                if (simpleName.Length > 0)
                    return simpleName;
            }

            return result;
        }

        private string MapCrossLanguageType(string typeName)
        {
            switch (typeName.ToLowerInvariant())
            {
                case "std.string":
                case "string":
                case "system.string":
                    return "string";

                case "bool":
                case "system.boolean":
                    return "bool";

                case "int":
                case "int32_t":
                case "system.int32":
                    return "int";

                case "long":
                case "long long":
                case "int64_t":
                case "system.int64":
                    return "long";

                case "short":
                case "int16_t":
                case "system.int16":
                    return "short";

                case "double":
                case "system.double":
                    return "double";

                case "float":
                case "system.single":
                    return "float";

                case "void":
                case "system.void":
                    return "void";

                case "char":
                case "wchar_t":
                case "system.char":
                    return "char";

                case "unsigned int":
                case "uint32_t":
                case "uint":
                case "system.uint32":
                    return "uint";

                case "unsigned long":
                case "uint64_t":
                case "ulong":
                case "system.uint64":
                    return "ulong";

                case "unsigned short":
                case "uint16_t":
                case "ushort":
                case "system.uint16":
                    return "ushort";

                case "unsigned char":
                case "uint8_t":
                case "byte":
                case "system.byte":
                    return "byte";

                case "size_t":
                    return "ulong";

                case "object":
                case "void*":
                case "system.object":
                    return "object";

                default:
                    return null;
            }
        }

        private bool IsAccessCompatible(AccessModifier upstream, AccessModifier downstream)
        {
            // Downstream access must be at least as permissive as upstream
            // Public >= Protected >= Internal >= Private
            return GetAccessLevel(downstream) >= GetAccessLevel(upstream);
        }

        private int GetAccessLevel(AccessModifier access)
        {
            switch (access)
            {
                case AccessModifier.Public: return 3;
                case AccessModifier.Protected: return 2;
                case AccessModifier.Internal: return 1;
                case AccessModifier.Private: return 0;
                default: return 0;
            }
        }

        private bool CanAutoFix(TypeModel upstream, TypeModel descendant, MemberModel member)
        {
            // We can auto-fix if:
            // 1. The downstream type is a concrete or abstract class (not interface)
            // 2. The member to add is a simple method/property
            // 3. No naming conflicts
            if (descendant.TypeKind == TypeKind.Interface)
                return false;

            // Check for naming conflict
            var existing = descendant.Members.FirstOrDefault(m => m.Name == member.Name);
            if (existing != null)
                return false; // Has a member with same name but different signature - ambiguous

            return true;
        }

        private bool IsSimpleSignatureChange(MemberModel oldMember, MemberModel newMember)
        {
            // Simple if only return type changed
            if (oldMember.Parameters.Count == newMember.Parameters.Count)
            {
                bool paramsMatch = true;
                for (int i = 0; i < oldMember.Parameters.Count; i++)
                {
                    if (!TypeNamesMatch(oldMember.Parameters[i].TypeName, newMember.Parameters[i].TypeName))
                    {
                        paramsMatch = false;
                        break;
                    }
                }
                if (paramsMatch)
                    return true; // Only return type changed
            }

            return false;
        }
    }
}
