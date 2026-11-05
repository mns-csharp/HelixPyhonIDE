namespace HelixPythonNamespace.Workspace.PyToCpp
{
    /// <summary>
    /// A Pass 1 rejection or informational diagnostic. Distinct codes/prefix ("PYCPP") from
    /// the retired C pipeline's "PY8xxx" so old and new diagnostics are never confused.
    /// </summary>
    public sealed class TypeCheckDiagnostic
    {
        public string Code { get; }
        public string FilePath { get; }
        public int Line { get; }
        public int Column { get; }
        public string Message { get; }

        public TypeCheckDiagnostic(string code, string filePath, int line, int column, string message)
        {
            Code = code ?? string.Empty;
            FilePath = filePath ?? string.Empty;
            Line = line;
            Column = column;
            Message = message ?? string.Empty;
        }

        public override string ToString() => $"{Code} {FilePath}:{Line}:{Column} {Message}";
    }

    /// <summary>
    /// Diagnostic codes Pass 1 can emit. Every code below corresponds to a documented
    /// boundary of the "rich static subset" — see PythonToCppTypeChecker's class comment
    /// for the authoritative supported/unsupported list.
    /// </summary>
    public static class PyCppDiagnosticCodes
    {
        public const string UnsupportedStatement = "PYCPP1001";
        public const string UnsupportedExpression = "PYCPP1002";
        public const string MultipleInheritance = "PYCPP1003";
        public const string Metaclass = "PYCPP1004";
        public const string UnsupportedDecorator = "PYCPP1005";
        public const string MutableDefaultArgument = "PYCPP1006";
        public const string DynamicAttributeAccess = "PYCPP1007";
        public const string UnresolvableType = "PYCPP1008";
        public const string TypeMismatch = "PYCPP1009";
        public const string UnknownName = "PYCPP1010";
        public const string UnknownAttribute = "PYCPP1011";
        public const string ArityMismatch = "PYCPP1012";
        public const string UnsupportedBuiltin = "PYCPP1013";
        public const string UnsupportedImport = "PYCPP1014";
        public const string InconsistentReturnTypes = "PYCPP1015";
        public const string NoEntryPoint = "PYCPP1016";
        public const string DuplicateDefinition = "PYCPP1017";
        public const string NestedFunctionCapture = "PYCPP1018";
        public const string DynamicFormatSpec = "PYCPP1019";
        public const string IntegerOverflowRisk = "PYCPP1020";
        public const string UnsupportedSlicingTarget = "PYCPP1021";
    }
}
