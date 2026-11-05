using System;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.VirtualCompiler
{
    /// <summary>
    /// A single problem reported by the virtual compiler. Wraps <see cref="ParseDiagnostic"/>
    /// with a file path so multi-module reports can be rendered uniformly.
    /// </summary>
    public sealed class CompileDiagnostic
    {
        public readonly string FilePath;
        public readonly string Code;
        public readonly ParseSeverity Severity;
        public readonly SourceSpan Span;
        public readonly string Message;

        public CompileDiagnostic(string filePath, string code, ParseSeverity severity, SourceSpan span, string message)
        {
            if (string.IsNullOrEmpty(code)) throw new ArgumentException("code");
            FilePath = filePath ?? string.Empty;
            Code = code;
            Severity = severity;
            Span = span;
            Message = message ?? string.Empty;
        }

        public static CompileDiagnostic FromParse(string filePath, ParseDiagnostic d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            return new CompileDiagnostic(filePath, d.Code, d.Severity, d.Span, d.Message);
        }

        public override string ToString()
        {
            return FilePath + "(" + Span.Start.Line + "," + Span.Start.Column + "): "
                + Severity + " " + Code + ": " + Message;
        }
    }

    /// <summary>
    /// Diagnostic codes emitted by virtual-compiler passes downstream of the parser.
    /// Ports of CPython's <c>SyntaxError</c>-class messages live in the PY2xxx band.
    /// Type-checker diagnostics live in PY6xxx (phase 3+).
    /// </summary>
    public static class VirtualCompilerDiagnosticCodes
    {
        // Ast validation (PY2xxx)
        public const string AstInvalidAssignmentTarget = "PY2101";
        public const string AstStarredOutsideAssignmentOrCall = "PY2102";
        public const string AstInvalidAnnotationTarget = "PY2103";
        public const string AstAssignToReserved = "PY2104";            // None / True / False / __debug__
        public const string AstDeleteReserved = "PY2105";
        public const string AstCannotDeleteLiteral = "PY2106";
        public const string AstMultipleStarredInAssign = "PY2107";
        public const string AstMutableDefaultArgument = "PY2108";

        // Symtable (PY3xxx)
        public const string SymDuplicateParameter = "PY3001";
        public const string SymGlobalAndLocal = "PY3002";
        public const string SymNonlocalWithoutBinding = "PY3003";
        public const string SymAnnotationOnLocal = "PY3004";
        public const string SymAsyncOutsideAsync = "PY3005";
        public const string SymUndefinedName = "PY3006";
        public const string SymDuplicateDefinition = "PY3007";

        // Compile validation (PY4xxx) — port of Python/compile.c user-facing errors.
        public const string CompReturnOutsideFunction = "PY4001";
        public const string CompYieldOutsideFunction = "PY4002";
        public const string CompBreakOutsideLoop = "PY4003";
        public const string CompContinueOutsideLoop = "PY4004";
        public const string CompAwaitOutsideAsync = "PY4005";
        public const string CompAsyncForOutsideAsync = "PY4006";
        public const string CompAsyncWithOutsideAsync = "PY4007";
        public const string CompGlobalAfterAssign = "PY4008";          // name used before global
        public const string CompNonlocalAfterAssign = "PY4009";        // name used before nonlocal
        public const string CompAnnotatedGlobal = "PY4010";            // annotated name marked global
        public const string CompAnnotatedNonlocal = "PY4011";
        public const string CompAsyncComprehensionOutsideAsync = "PY4012";
        public const string CompIsCompareLiteral = "PY4013";
        public const string CompControlFlowInFinally = "PY4014";
        public const string CompUnreachableCode = "PY4015";

        // Import resolution (PY5xxx). PY5002-PY5004 are emitted by
        // <see cref="Imports.ImportChecker"/> and carry CPython's own wording, so a compile-time
        // report reads identically to the runtime traceback it prevents.
        public const string SymUnusedImport = "PY5001";
        public const string ImportModuleNotFound = "PY5002";        // ModuleNotFoundError
        public const string ImportNameNotFound = "PY5003";          // ImportError: cannot import name
        public const string ImportRelativeBeyondTopLevel = "PY5004";

        // Type-checker (PY6xxx).
        public const string TypeAttributeNotFound = "PY6001";
        public const string TypeArgCountMismatch = "PY6002";
        public const string TypeArgTypeMismatch = "PY6003";
        public const string TypeUnknownKeywordArg = "PY6004";
        public const string TypeReturnTypeMismatch = "PY6005";
        public const string TypeNotCallable = "PY6006";
        public const string TypeNotSubscriptable = "PY6007";
        public const string TypeAnnAssignMismatch = "PY6008";

        // Solution policy (PY8xxx). Hard blockers: Run and Debug refuse to launch while any of
        // these is present. Third-party code is exempt -- see Compliance.ThirdPartyDetector.
        public const string PolicyFileOutsideWorkspace = "PY8001";
        public const string PolicyNonObjectOriented = "PY8002";
        // Only a test project may contain test files. Defined by HelixPythonNamespace.Core.Policy.
        // TestFilePlacementRule, which is the single authority both this pass and the
        // solution-wide sweep (for the non-Python languages) consult.
        public const string PolicyTestFileInNonTestProject =
            HelixPythonNamespace.Core.Policy.TestFilePlacementRule.DiagnosticCode;
        // A file opened OUTSIDE any solution may import installed packages only. PY8001 restated
        // for the one context it cannot reach -- a lone file has no workspace to be a member of --
        // so the two are mutually exclusive and never both reported. Defined by
        // Compliance.SingleFileImportRule.
        public const string PolicyUserModuleInSingleFile =
            Compliance.SingleFileImportRule.DiagnosticCode;
    }
}
