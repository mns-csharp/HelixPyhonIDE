namespace HelixPythonNamespace.Refactoring
{
    using System.Collections.Generic;

    /// <summary>Why a relaxed (tier-2) match wasn't a strict match. Port of the C++ <c>CandidateIssue</c>.</summary>
    public sealed class CandidateIssue
    {
        public string Code;
        public string Description;

        public CandidateIssue() { }

        public CandidateIssue(string code, string description)
        {
            Code = code;
            Description = description;
        }
    }

    /// <summary>One offered vectorization / de-vectorization. Port of the C++ <c>VectorizationCandidate</c>.</summary>
    public sealed class VectorizationCandidate
    {
        public enum Kind
        {
            F1_ElementwiseBinop,
            F2_ElementwiseNegate,
            F3_ScalarBroadcast,
            F4_MathToNumpy,
            F5_MapAppend,
            F6_FilterAppend,
            F7_FilterMapAppend,
            F8_Sum,
            F9_Product,
            F10_MinMax,
            F11_Count,
            F12_Any,
            F13_All,
            F14_Where,
            F15_Cumsum,
            F16_RangeFill,
            R1_ElementwiseBinopToLoop,
            R2_SumToLoop,
            R5_ComprehensionToLoop,
            R10_MaxToLoop,
            R14_WhereToLoop,
        }

        public Kind PatternKind;
        public string Label;
        public string ReplacementText;
        public List<string> RequiredImports = new List<string>();
        public int Confidence = 100;
        public List<CandidateIssue> Caveats = new List<CandidateIssue>();

        public bool HasSubrange;
        public int SubrangeStartLine;
        public int SubrangeStartColumn;
        public int SubrangeEndLine;
        public int SubrangeEndColumn;
    }

    public sealed class SupportedPatternInfo
    {
        public VectorizationCandidate.Kind PatternKind;
        public string Name;
        public string ExampleSource;
        public string Replacement;
    }

    /// <summary>One offered function ↔ lambda transformation. Port of the C++ <c>LambdaTransformCandidate</c>.</summary>
    public sealed class LambdaTransformCandidate
    {
        public enum Kind
        {
            F2L1_DefToLambda,
            F2L2_DefDefaultsToLambda,
            F2L3_DefIfReturnToTernary,
            F2L4_NestedDefToInlineLambda,
            L2F1_NamedLambdaToDef,
            L2F2_AnonLambdaToDef,
            L2F3_CurriedLambdaToDef,
            L2F4_TypedLambdaToDef,
        }

        public Kind PatternKind;
        public string Label;
        public string ReplacementText;
        public List<string> RequiredImports = new List<string>();
        public int Confidence = 100;
        public List<CandidateIssue> Caveats = new List<CandidateIssue>();

        public bool HasSubrange;
        public int SubrangeStartLine;
        public int SubrangeStartColumn;
        public int SubrangeEndLine;
        public int SubrangeEndColumn;
    }

    public sealed class LambdaTransformPatternInfo
    {
        public LambdaTransformCandidate.Kind PatternKind;
        public string Name;
        public string ExampleSource;
        public string Replacement;
    }
}
