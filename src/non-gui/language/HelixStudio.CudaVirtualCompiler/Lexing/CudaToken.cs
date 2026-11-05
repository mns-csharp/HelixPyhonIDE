namespace HelixPythonNamespace.CudaVirtualCompiler.Lexing
{
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// A single lexed token. Value type stored contiguously in a list — the parser
    /// indexes the stream and the editor's custom highlighter reads
    /// Span.Start.Offset / Length to assign Scintilla style bytes. <see cref="Text"/>
    /// holds the exact source lexeme (so identifiers / literals keep their spelling).
    /// Port of the C++ helix::cuda_vcompiler::CudaToken (cuda_vcompiler/lexer/token.h).
    /// </summary>
    public sealed class CudaToken
    {
        public readonly CudaTokenKind Kind;
        public readonly string Text;
        public readonly SourceSpan Span;

        public CudaToken(CudaTokenKind kind, string text, SourceSpan span)
        {
            Kind = kind;
            Text = text ?? string.Empty;
            Span = span;
        }

        public int Offset { get { return Span.Start.Offset; } }
        public int Length { get { return Span.Length; } }
        public bool IsTrivia { get { return CudaTokenKinds.IsTrivia(Kind); } }
        public bool IsEnd { get { return Kind == CudaTokenKind.EndOfFile; } }
    }
}
