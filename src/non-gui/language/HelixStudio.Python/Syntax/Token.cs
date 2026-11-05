using System;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Syntax
{
    public sealed class Token
    {
        public readonly TokenKind Kind;
        public readonly string Text;
        public readonly SourceSpan Span;
        public readonly object Value;

        public Token(TokenKind kind, string text, SourceSpan span, object value = null)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            Span = span;
            Value = value;
        }

        public bool IsKeyword => TokenKinds.IsKeyword(Kind);
        public bool IsTrivia => Kind == TokenKind.Comment || Kind == TokenKind.Whitespace || Kind == TokenKind.LineContinuation;
        public bool IsEnd => Kind == TokenKind.EndOfFile;

        public override string ToString()
        {
            string summary = Text.Length > 40 ? Text.Substring(0, 37) + "..." : Text;
            summary = summary.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
            return Kind + " " + Span + " \"" + summary + "\"";
        }
    }
}
