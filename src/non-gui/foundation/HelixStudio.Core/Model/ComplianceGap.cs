using System.Collections.Generic;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Classification of how a compliance gap should be resolved.
    /// </summary>
    public enum ComplianceFixKind
    {
        /// <summary>Safe to auto-fix: add missing stub, update signature.</summary>
        AutoFix,
        /// <summary>Needs user attention: ambiguous but identifiable.</summary>
        NeedsAttention,
        /// <summary>Cannot be resolved automatically: semantic ambiguity.</summary>
        Ambiguous
    }

    /// <summary>
    /// Describes what kind of upstream change occurred.
    /// </summary>
    public enum UpstreamChangeKind
    {
        MemberAdded,
        MemberRemoved,
        SignatureChanged,
        MemberRenamed,
        AccessModifierChanged,
        ReturnTypeChanged
    }

    /// <summary>
    /// Represents a single compliance gap between an upstream contract and a downstream type.
    /// </summary>
    public sealed class ComplianceGap
    {
        public ComplianceGap(
            TypeModel upstreamType,
            TypeModel downstreamType,
            UpstreamChangeKind changeKind,
            ComplianceFixKind fixKind,
            string description)
        {
            UpstreamType = upstreamType;
            DownstreamType = downstreamType;
            ChangeKind = changeKind;
            FixKind = fixKind;
            Description = description;
        }

        /// <summary>The upstream type (interface/base class) that changed.</summary>
        public TypeModel UpstreamType { get; }

        /// <summary>The downstream type that needs updating.</summary>
        public TypeModel DownstreamType { get; }

        /// <summary>What kind of change occurred upstream.</summary>
        public UpstreamChangeKind ChangeKind { get; }

        /// <summary>How this gap should be resolved.</summary>
        public ComplianceFixKind FixKind { get; }

        /// <summary>Human-readable description of the gap.</summary>
        public string Description { get; }

        /// <summary>The upstream member involved (if applicable).</summary>
        public MemberModel UpstreamMember { get; set; }

        /// <summary>The downstream member that needs updating (if exists).</summary>
        public MemberModel DownstreamMember { get; set; }

        /// <summary>Whether this gap has been resolved (auto-fixed or manually addressed).</summary>
        public bool IsResolved { get; set; }

        /// <summary>Description of the fix that was applied, if any.</summary>
        public string FixDescription { get; set; }
    }

    /// <summary>
    /// Result of an impact analysis for a single upstream type change.
    /// </summary>
    public sealed class ComplianceReport
    {
        public ComplianceReport(TypeModel upstreamType)
        {
            UpstreamType = upstreamType;
            Gaps = new List<ComplianceGap>();
        }

        /// <summary>The upstream type that was changed.</summary>
        public TypeModel UpstreamType { get; }

        /// <summary>All compliance gaps found.</summary>
        public List<ComplianceGap> Gaps { get; }

        /// <summary>Whether any gaps need user attention.</summary>
        public bool HasManualItems
        {
            get
            {
                foreach (var gap in Gaps)
                {
                    if (gap.FixKind != ComplianceFixKind.AutoFix)
                        return true;
                }
                return false;
            }
        }

        /// <summary>Count of auto-fixable gaps.</summary>
        public int AutoFixCount
        {
            get
            {
                int count = 0;
                foreach (var gap in Gaps)
                {
                    if (gap.FixKind == ComplianceFixKind.AutoFix)
                        count++;
                }
                return count;
            }
        }
    }
}
