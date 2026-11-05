namespace HelixPythonNamespace.Refactoring.LambdaTransform
{
    using System.Collections.Generic;
    using L = LambdaTransformCandidate;
    using K = LambdaTransformCandidate.Kind;

    /// <summary>Pure-string templates for function ↔ lambda candidates. Port of the C++ lambda <c>PatternEmitter</c>.</summary>
    public static class LambdaPatternEmitter
    {
        public static void ApplyRelaxation(LambdaTransformCandidate c, List<CandidateIssue> issues, int confidence)
        {
            c.Caveats = issues;
            c.Confidence = confidence;
            c.Label = "Possible lambda transform (review carefully): " + c.Label;
        }

        public static LambdaTransformCandidate EmitF2L1(DefToLambdaData d)
        {
            string rep = d.FuncName + " = lambda" + (string.IsNullOrEmpty(d.ArgsSignature) ? "" : " " + d.ArgsSignature) + ": " + d.BodyExpr;
            return new L { PatternKind = K.F2L1_DefToLambda, ReplacementText = rep, Label = "Convert to lambda: " + rep };
        }

        public static LambdaTransformCandidate EmitF2L2(DefToLambdaData d)
        {
            string rep = d.FuncName + " = lambda" + (string.IsNullOrEmpty(d.ArgsSignature) ? "" : " " + d.ArgsSignature) + ": " + d.BodyExpr;
            return new L { PatternKind = K.F2L2_DefDefaultsToLambda, ReplacementText = rep, Label = "Convert to lambda (with defaults): " + rep };
        }

        public static LambdaTransformCandidate EmitF2L3(DefIfReturnTernaryData d)
        {
            string rep = d.FuncName + " = lambda" + (string.IsNullOrEmpty(d.ArgsSignature) ? "" : " " + d.ArgsSignature) + ": " + d.TrueExpr + " if " + d.CondExpr + " else " + d.FalseExpr;
            return new L { PatternKind = K.F2L3_DefIfReturnToTernary, ReplacementText = rep, Label = "Convert to lambda (ternary): " + rep };
        }

        public static LambdaTransformCandidate EmitF2L4(NestedDefInlineData d)
        {
            return new L { PatternKind = K.F2L4_NestedDefToInlineLambda, ReplacementText = d.OuterCallReplacement, Label = "Inline nested def as lambda in: " + d.OuterCallSite };
        }

        public static LambdaTransformCandidate EmitL2F1(NamedLambdaToDefData d)
        {
            string rep = "def " + d.FuncName + "(" + d.ArgsSignature + "):\n    return " + d.BodyExpr;
            string lab = "Convert to function: def " + d.FuncName + "(" + d.ArgsSignature + "): ...";
            return new L { PatternKind = K.L2F1_NamedLambdaToDef, ReplacementText = rep, Label = lab };
        }

        public static LambdaTransformCandidate EmitL2F2(AnonLambdaToDefData d)
        {
            string rep = "def " + d.GeneratedName + "(" + d.ArgsSignature + "):\n    return " + d.BodyExpr + "\n" + d.CallSiteReplacement;
            string lab = "Extract lambda to function: def " + d.GeneratedName + "(" + d.ArgsSignature + "): ...";
            return new L { PatternKind = K.L2F2_AnonLambdaToDef, ReplacementText = rep, Label = lab };
        }

        public static LambdaTransformCandidate EmitL2F3(CurriedLambdaToDefData d)
        {
            string rep = "def " + d.FuncName + "(" + d.OuterArgs + "):\n    return " + d.InnerLambdaText;
            string lab = "Convert curried lambda to def: " + d.FuncName + "(" + d.OuterArgs + ") -> " + d.InnerLambdaText;
            return new L { PatternKind = K.L2F3_CurriedLambdaToDef, ReplacementText = rep, Label = lab };
        }

        public static LambdaTransformCandidate EmitL2F4(TypedLambdaToDefData d)
        {
            string rep = "def " + d.FuncName + "(" + d.ArgsWithAnnotations + ")";
            if (!string.IsNullOrEmpty(d.ReturnAnnotation)) rep += " -> " + d.ReturnAnnotation;
            rep += ":\n    return " + d.BodyExpr;
            string lab = "Convert typed lambda to def: " + d.FuncName + "(" + d.ArgsWithAnnotations + ")";
            if (!string.IsNullOrEmpty(d.ReturnAnnotation)) lab += " -> " + d.ReturnAnnotation;
            return new L { PatternKind = K.L2F4_TypedLambdaToDef, ReplacementText = rep, Label = lab };
        }
    }
}
