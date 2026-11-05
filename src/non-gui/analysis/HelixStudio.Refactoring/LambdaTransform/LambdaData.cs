namespace HelixPythonNamespace.Refactoring.LambdaTransform
{
    /// <summary>def → lambda (F2L1 / F2L2) extracted data.</summary>
    public sealed class DefToLambdaData
    {
        public string FuncName;
        public string ArgsSignature;
        public string BodyExpr;
    }

    /// <summary>def with if/else-return → ternary lambda (F2L3) extracted data.</summary>
    public sealed class DefIfReturnTernaryData
    {
        public string FuncName;
        public string ArgsSignature;
        public string CondExpr;
        public string TrueExpr;
        public string FalseExpr;
    }

    /// <summary>nested def → inline lambda (F2L4) extracted data.</summary>
    public sealed class NestedDefInlineData
    {
        public string OuterCallSite;
        public string OuterCallReplacement;
    }

    /// <summary>named lambda → def (L2F1) extracted data.</summary>
    public sealed class NamedLambdaToDefData
    {
        public string FuncName;
        public string ArgsSignature;
        public string BodyExpr;
    }

    /// <summary>anonymous lambda → hoisted def (L2F2) extracted data.</summary>
    public sealed class AnonLambdaToDefData
    {
        public string GeneratedName;
        public string ArgsSignature;
        public string BodyExpr;
        public string CallSiteReplacement;
    }

    /// <summary>curried lambda → def (L2F3) extracted data.</summary>
    public sealed class CurriedLambdaToDefData
    {
        public string FuncName;
        public string OuterArgs;
        public string InnerLambdaText;
    }

    /// <summary>typed lambda → def (L2F4) extracted data.</summary>
    public sealed class TypedLambdaToDefData
    {
        public string FuncName;
        public string ArgsWithAnnotations;
        public string ReturnAnnotation;
        public string BodyExpr;
    }
}
