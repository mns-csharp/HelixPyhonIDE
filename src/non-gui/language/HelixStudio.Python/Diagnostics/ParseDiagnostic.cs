using System;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Diagnostics
{
    public enum ParseSeverity
    {
        Hidden,
        Info,
        Warning,
        Error,
        Fatal
    }

    public sealed class ParseDiagnostic
    {
        public readonly string Code;
        public readonly ParseSeverity Severity;
        public readonly SourceSpan Span;
        public readonly string Message;

        public ParseDiagnostic(string code, ParseSeverity severity, SourceSpan span, string message)
        {
            if (string.IsNullOrEmpty(code)) throw new ArgumentException("code");
            Code = code;
            Severity = severity;
            Span = span;
            Message = message ?? string.Empty;
        }

        public override string ToString()
        {
            return Severity + " " + Code + " " + Span + ": " + Message;
        }
    }

    public static class DiagnosticCodes
    {
        public const string UnexpectedCharacter = "PY1001";
        public const string UnterminatedString = "PY1002";
        public const string InvalidNumber = "PY1003";
        public const string InconsistentIndent = "PY1004";
        public const string UnexpectedToken = "PY1101";
        public const string UnexpectedEof = "PY1102";
        public const string MissingNewline = "PY1103";
        public const string InvalidStringEscape = "PY1104";
        public const string InvalidSyntax = "PY1199";
    }
}
