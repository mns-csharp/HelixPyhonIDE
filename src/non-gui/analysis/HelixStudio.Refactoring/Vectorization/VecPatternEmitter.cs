namespace HelixPythonNamespace.Refactoring.Vectorization
{
    using System.Collections.Generic;
    using K = VectorizationCandidate.Kind;
    using H = VecHelpers;

    /// <summary>Pure-string templates composing a candidate from match data. Port of the C++ <c>PatternEmitter</c>.</summary>
    public static class VecPatternEmitter
    {
        private const string ImportNumpy = "import numpy as np";
        private const string ImportMath = "import math";

        public static void ApplyRelaxation(VectorizationCandidate c, List<CandidateIssue> issues, int confidence = 50)
        {
            c.Caveats = issues;
            c.Confidence = confidence;
            c.Label = "Possible vectorization (review carefully): " + c.Label;
        }

        public static VectorizationCandidate EmitF1(ElementwiseBinopData d)
        {
            string body = d.TargetName + " = np.asarray(" + d.LhsArrayName + ") " + H.OpToString(d.Op) + " np.asarray(" + d.RhsArrayName + ")";
            return new VectorizationCandidate { PatternKind = K.F1_ElementwiseBinop, Label = "Vectorize: " + body, ReplacementText = body, RequiredImports = { ImportNumpy } };
        }

        public static VectorizationCandidate EmitF2(ElementwiseNegateData d)
        {
            string body = d.TargetName + " = -np.asarray(" + d.SourceArrayName + ")";
            return new VectorizationCandidate { PatternKind = K.F2_ElementwiseNegate, Label = "Vectorize: " + body, ReplacementText = body, RequiredImports = { ImportNumpy } };
        }

        public static VectorizationCandidate EmitF3(ScalarBroadcastData d)
        {
            string opStr = H.OpToString(d.Op);
            string body = d.ScalarOnLeft
                ? d.TargetName + " = " + d.ScalarExpr + " " + opStr + " np.asarray(" + d.ArrayName + ")"
                : d.TargetName + " = np.asarray(" + d.ArrayName + ") " + opStr + " " + d.ScalarExpr;
            return new VectorizationCandidate { PatternKind = K.F3_ScalarBroadcast, Label = "Vectorize (broadcast): " + body, ReplacementText = body, RequiredImports = { ImportNumpy } };
        }

        public static VectorizationCandidate EmitF4(MathToNumpyData d)
        {
            string body = d.TargetName + " = np." + d.MathFn + "(np.asarray(" + d.ArrayName + "))";
            return new VectorizationCandidate { PatternKind = K.F4_MathToNumpy, Label = "Vectorize: " + body, ReplacementText = body, RequiredImports = { ImportNumpy } };
        }

        public static VectorizationCandidate EmitF5(MapAppendData d)
        {
            string body = d.ResultName + " = [" + d.ElementExpr + " for " + d.LoopVar + " in " + d.IterableExpr + "]";
            return new VectorizationCandidate { PatternKind = K.F5_MapAppend, Label = "Convert to comprehension: " + body, ReplacementText = body };
        }

        public static VectorizationCandidate EmitF6(FilterAppendData d)
        {
            string body = d.ResultName + " = [" + d.LoopVar + " for " + d.LoopVar + " in " + d.IterableExpr + " if " + d.CondExpr + "]";
            return new VectorizationCandidate { PatternKind = K.F6_FilterAppend, Label = "Convert to filter comprehension: " + body, ReplacementText = body };
        }

        public static VectorizationCandidate EmitF7(FilterMapAppendData d)
        {
            string body = d.ResultName + " = [" + d.ElementExpr + " for " + d.LoopVar + " in " + d.IterableExpr + " if " + d.CondExpr + "]";
            return new VectorizationCandidate { PatternKind = K.F7_FilterMapAppend, Label = "Convert to filter+map comprehension: " + body, ReplacementText = body };
        }

        public static VectorizationCandidate EmitF8(SumData d)
        {
            string body = string.IsNullOrEmpty(d.StartExpr)
                ? d.TargetName + " = sum(" + d.IterableExpr + ")"
                : d.TargetName + " = sum(" + d.IterableExpr + ", " + d.StartExpr + ")";
            return new VectorizationCandidate { PatternKind = K.F8_Sum, Label = "Reduce to sum(): " + body, ReplacementText = body };
        }

        public static VectorizationCandidate EmitF9(ProductData d)
        {
            string body = d.TargetName + " = math.prod(" + d.IterableExpr + ")";
            return new VectorizationCandidate { PatternKind = K.F9_Product, Label = "Reduce to math.prod(): " + body, ReplacementText = body, RequiredImports = { ImportMath } };
        }

        public static VectorizationCandidate EmitF10(MinMaxData d)
        {
            string fn = d.IsMax ? "max" : "min";
            string body = d.TargetName + " = " + fn + "(" + d.IterableExpr + ")";
            return new VectorizationCandidate { PatternKind = K.F10_MinMax, Label = "Reduce to " + fn + "(): " + body, ReplacementText = body };
        }

        public static VectorizationCandidate EmitF11(CountData d)
        {
            string body = d.TargetName + " = sum(1 for " + d.LoopVar + " in " + d.IterableExpr + " if " + d.CondExpr + ")";
            return new VectorizationCandidate { PatternKind = K.F11_Count, Label = "Reduce to count: " + body, ReplacementText = body };
        }

        public static VectorizationCandidate EmitF12(AnyAllData d)
        {
            string body = "return any(" + d.CondExpr + " for " + d.LoopVar + " in " + d.IterableExpr + ")";
            return new VectorizationCandidate { PatternKind = K.F12_Any, Label = "Reduce to any(): " + body, ReplacementText = body };
        }

        public static VectorizationCandidate EmitF13(AnyAllData d)
        {
            string body = "return all(" + d.CondExpr + " for " + d.LoopVar + " in " + d.IterableExpr + ")";
            return new VectorizationCandidate { PatternKind = K.F13_All, Label = "Reduce to all(): " + body, ReplacementText = body };
        }

        public static VectorizationCandidate EmitF14(WhereData d)
        {
            string body = d.TargetName + " = np.where(" + d.CondArrayName + ", " + d.TrueArrayName + ", " + d.FalseArrayName + ")";
            return new VectorizationCandidate { PatternKind = K.F14_Where, Label = "Vectorize to np.where(): " + body, ReplacementText = body, RequiredImports = { ImportNumpy } };
        }

        public static VectorizationCandidate EmitF15(CumsumData d)
        {
            string body = d.OutName + " = list(np.cumsum(" + d.IterableExpr + "))";
            return new VectorizationCandidate { PatternKind = K.F15_Cumsum, Label = "Vectorize to np.cumsum(): " + body, ReplacementText = body, RequiredImports = { ImportNumpy } };
        }

        public static VectorizationCandidate EmitF16(RangeFillData d)
        {
            string body = d.OutName + " = [" + d.ElementExpr + " for " + d.LoopVar + " in range(" + d.SizeExpr + ")]";
            return new VectorizationCandidate { PatternKind = K.F16_RangeFill, Label = "Vectorize: " + body, ReplacementText = body };
        }

        public static VectorizationCandidate EmitR1(ElementwiseBinopData d)
        {
            string body = "for i in range(len(" + d.LhsArrayName + ")):\n    " + d.TargetName + "[i] = " + d.LhsArrayName + "[i] " + H.OpToString(d.Op) + " " + d.RhsArrayName + "[i]";
            return new VectorizationCandidate { PatternKind = K.R1_ElementwiseBinopToLoop, Label = "Devectorize: explicit element-wise loop", ReplacementText = body };
        }

        public static VectorizationCandidate EmitR2(SumData d)
        {
            string init = string.IsNullOrEmpty(d.StartExpr) ? "0" : d.StartExpr;
            string body = d.TargetName + " = " + init + "\nfor x in " + d.IterableExpr + ":\n    " + d.TargetName + " += x";
            return new VectorizationCandidate { PatternKind = K.R2_SumToLoop, Label = "Devectorize sum() to explicit loop", ReplacementText = body };
        }

        public static VectorizationCandidate EmitR5(MapAppendData d)
        {
            string body = d.ResultName + " = []\nfor " + d.LoopVar + " in " + d.IterableExpr + ":\n    " + d.ResultName + ".append(" + d.ElementExpr + ")";
            return new VectorizationCandidate { PatternKind = K.R5_ComprehensionToLoop, Label = "Devectorize comprehension to explicit loop", ReplacementText = body };
        }

        public static VectorizationCandidate EmitR10(MinMaxData d)
        {
            string op = d.IsMax ? ">" : "<";
            string body = d.TargetName + " = " + d.IterableExpr + "[0]\nfor x in " + d.IterableExpr + "[1:]:\n    if x " + op + " " + d.TargetName + ":\n        " + d.TargetName + " = x";
            return new VectorizationCandidate { PatternKind = K.R10_MaxToLoop, Label = "Devectorize " + (d.IsMax ? "max()" : "min()") + " to explicit loop", ReplacementText = body };
        }

        public static VectorizationCandidate EmitR14(WhereData d)
        {
            string body = "for i in range(len(" + d.CondArrayName + ")):\n    " + d.TargetName + "[i] = " + d.TrueArrayName + "[i] if " + d.CondArrayName + "[i] else " + d.FalseArrayName + "[i]";
            return new VectorizationCandidate { PatternKind = K.R14_WhereToLoop, Label = "Devectorize np.where() to explicit loop", ReplacementText = body };
        }
    }
}
