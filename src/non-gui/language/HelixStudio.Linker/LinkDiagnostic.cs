namespace HelixPythonNamespace.Linker
{
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;
    using HelixPythonNamespace.VirtualCompiler;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>Link-phase diagnostic codes (PY7xxx).</summary>
    public static class LinkDiagnosticCodes
    {
        public const string LinkUnresolvedModule = "PY7001";
        public const string LinkUnresolvedImport = "PY7002";
        public const string LinkUnresolvedAttribute = "PY7003";
        public const string LinkCircularImport = "PY7004";
        public const string LinkAmbiguousStarImport = "PY7005";
        public const string LinkRelativeImportEscapesPackage = "PY7006";
        public const string LinkStarImportShadowsLocal = "PY7007";
        public const string LinkUnusedReExport = "PY7008";
        public const string LinkShadowedBuiltin = "PY7009";
    }

    /// <summary>A link-phase diagnostic. Port of the C++ <c>LinkDiagnostic</c>.</summary>
    public sealed class LinkDiagnostic
    {
        public string FilePath { get; }
        public string Code { get; }
        public ParseSeverity Severity { get; }
        public SourceSpan Span { get; }
        public string Message { get; }

        public LinkDiagnostic(string filePath, string code, ParseSeverity severity, SourceSpan span, string message)
        {
            FilePath = filePath;
            Code = code;
            Severity = severity;
            Span = span;
            Message = message;
        }

        public CompileDiagnostic ToCompile()
        {
            return new CompileDiagnostic(FilePath, Code, Severity, Span, Message);
        }
    }

    /// <summary>
    /// One input file for the linker: its AST module, fully-qualified module name,
    /// and source path. The C# analogue of the C++ <c>CompileFileResult</c> triple.
    /// </summary>
    public sealed class CompileFileResult
    {
        public Module AstModule { get; }
        public string ModuleFqn { get; }
        public string FilePath { get; }

        public CompileFileResult(Module astModule, string moduleFqn, string filePath)
        {
            AstModule = astModule;
            ModuleFqn = moduleFqn ?? "";
            FilePath = filePath ?? "";
        }
    }
}
