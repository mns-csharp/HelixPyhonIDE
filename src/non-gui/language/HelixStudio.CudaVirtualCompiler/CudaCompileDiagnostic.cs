namespace HelixPythonNamespace.CudaVirtualCompiler
{
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// A single CUDA-compiler diagnostic. Deliberately the same shape as
    /// vcompiler::CompileDiagnostic so the editor / problems-panel layer can consume
    /// CUDA and Python diagnostics through one code path. Reuses the Python
    /// project's value types (SourceSpan / ParseSeverity) — this library references
    /// HelixPythonNamespace.Python for those types only, not for the Python parser. Port of the
    /// C++ helix::cuda_vcompiler::CudaCompileDiagnostic (cuda_vcompiler/cuda_compile_diagnostic.h).
    /// </summary>
    public sealed class CudaCompileDiagnostic
    {
        private readonly string filePath_;
        private readonly string code_;
        private readonly ParseSeverity severity_;
        private readonly SourceSpan span_;
        private readonly string message_;

        public CudaCompileDiagnostic(string filePath, string code, ParseSeverity severity, SourceSpan span, string message)
        {
            filePath_ = filePath ?? string.Empty;
            code_ = code ?? string.Empty;
            severity_ = severity;
            span_ = span;
            message_ = message ?? string.Empty;
        }

        public string FilePath { get { return filePath_; } }
        public string Code { get { return code_; } }
        public ParseSeverity Severity { get { return severity_; } }
        public SourceSpan Span { get { return span_; } }
        public string Message { get { return message_; } }

        public override string ToString()
        {
            return filePath_ + "(" + span_.Start.Line + "," + span_.Start.Column + "): " +
                   SeverityName(severity_) + " " + code_ + ": " + message_;
        }

        private static string SeverityName(ParseSeverity s)
        {
            switch (s)
            {
                case ParseSeverity.Hidden: return "Hidden";
                case ParseSeverity.Info: return "Info";
                case ParseSeverity.Warning: return "Warning";
                case ParseSeverity.Error: return "Error";
                case ParseSeverity.Fatal: return "Fatal";
            }
            return "Unknown";
        }
    }
}
