namespace HelixPythonNamespace.CudaVirtualCompiler.Lexing
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Hand-written lexer for C++/CUDA source. Plays the role HelixPythonNamespace.Python's
    /// PythonLexer plays for the Python engine, but is self-contained. It emits a
    /// significant-token stream (pure whitespace and newlines are dropped; the
    /// editor's highlighter fills those gaps with the default style). Comments and
    /// preprocessor directives ARE emitted as trivia tokens so the highlighter can
    /// color them and the parser can skip them. Lexical problems are reported as
    /// CU1xxx ParseDiagnostic (same currency the parser uses), which the facade
    /// converts to CudaCompileDiagnostic. Port of the C++
    /// helix::cuda_vcompiler::CudaLexer (cuda_vcompiler/lexer/cuda_lexer.{h,cpp}).
    /// </summary>
    public sealed class CudaLexer
    {
        private readonly string source_;
        private readonly string fileName_;
        private readonly List<CudaToken> tokens_;
        private readonly List<ParseDiagnostic> diagnostics_;
        private readonly IReadOnlyDictionary<string, string> defines_;   // object-like #define table; null when disabled

        private int offset_;
        private int line_;
        private int column_;
        private bool atLineStart_;

        public CudaLexer(string source, string fileName = null,
            IReadOnlyDictionary<string, string> defines = null)
        {
            source_ = source ?? string.Empty;
            fileName_ = fileName ?? string.Empty;
            tokens_ = new List<CudaToken>();
            diagnostics_ = new List<ParseDiagnostic>();
            defines_ = defines;
            offset_ = 0;
            line_ = 1;
            column_ = 1;
            atLineStart_ = true;
        }

        public IReadOnlyList<ParseDiagnostic> Diagnostics { get { return diagnostics_; } }

        public List<CudaToken> Tokenize()
        {
            while (true)
            {
                SkipWhitespaceAndNewlines();
                if (AtEnd()) break;

                SourcePosition start = CurrentPos();
                char c = Peek();

                if (atLineStart_ && c == '#')
                {
                    LexPreprocessorDirective();
                    continue;
                }
                // Anything that isn't leading whitespace clears line-start state.
                atLineStart_ = false;

                if (c == '/' && PeekAt(1) == '/') { LexLineComment(); continue; }
                if (c == '/' && PeekAt(1) == '*') { LexBlockComment(); continue; }
                if (IsIdentStart(c)) { LexIdentifierOrKeyword(); continue; }
                if (IsDigit(c) || (c == '.' && IsDigit(PeekAt(1)))) { LexNumber(); continue; }
                if (c == '"') { LexString(); continue; }
                if (c == '\'') { LexChar(); continue; }
                LexOperator();
            }

            SourcePosition end = CurrentPos();
            tokens_.Add(new CudaToken(CudaTokenKind.EndOfFile, string.Empty, new SourceSpan(end, end)));
            return tokens_;
        }

        private bool AtEnd() { return offset_ >= source_.Length; }
        private char Peek() { return AtEnd() ? '\0' : source_[offset_]; }

        private char PeekAt(int k)
        {
            int i = offset_ + k;
            if (i < 0 || i >= source_.Length) return '\0';
            return source_[i];
        }

        private void Advance()
        {
            if (AtEnd()) return;
            char c = source_[offset_];
            ++offset_;
            if (c == '\n')
            {
                ++line_;
                column_ = 1;
            }
            else
            {
                ++column_;
            }
        }

        private SourcePosition CurrentPos()
        {
            return new SourcePosition(offset_, line_, column_);
        }

        private void Emit(CudaTokenKind kind, string text, SourcePosition start)
        {
            tokens_.Add(new CudaToken(kind, text, new SourceSpan(start, CurrentPos())));
        }

        private void EmitOp(CudaTokenKind kind, int length, SourcePosition start)
        {
            string text = source_.Substring(start.Offset, length);
            for (int i = 0; i < length; ++i) Advance();
            Emit(kind, text, start);
        }

        private void Report(string code, ParseSeverity severity, SourceSpan span, string message)
        {
            diagnostics_.Add(new ParseDiagnostic(code, severity, span, message));
        }

        private void SkipWhitespaceAndNewlines()
        {
            while (!AtEnd())
            {
                char c = Peek();
                if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v')
                {
                    Advance();
                }
                else if (c == '\n')
                {
                    Advance();
                    atLineStart_ = true;
                }
                else if (c == '\\' && PeekAt(1) == '\n')
                {
                    // Line continuation outside a directive — splice.
                    Advance();
                    Advance();
                }
                else
                {
                    break;
                }
            }
        }

        private void LexPreprocessorDirective()
        {
            SourcePosition start = CurrentPos();
            // Consume the full logical directive line, honoring backslash-newline
            // continuation. We do not interpret the directive here (a separate
            // Preprocessor pass collects #define values); we emit it as trivia.
            while (!AtEnd())
            {
                char c = Peek();
                if (c == '\\' && (PeekAt(1) == '\n' || (PeekAt(1) == '\r' && PeekAt(2) == '\n')))
                {
                    Advance();  // backslash
                    if (Peek() == '\r') Advance();
                    Advance();  // newline
                    continue;
                }
                if (c == '\n') break;
                Advance();
            }
            Emit(CudaTokenKind.PreprocessorDirective, source_.Substring(start.Offset, offset_ - start.Offset), start);
        }

        private void LexLineComment()
        {
            SourcePosition start = CurrentPos();
            while (!AtEnd() && Peek() != '\n') Advance();
            Emit(CudaTokenKind.CommentLine, source_.Substring(start.Offset, offset_ - start.Offset), start);
        }

        private void LexBlockComment()
        {
            SourcePosition start = CurrentPos();
            Advance();  // '/'
            Advance();  // '*'
            bool closed = false;
            while (!AtEnd())
            {
                if (Peek() == '*' && PeekAt(1) == '/')
                {
                    Advance();
                    Advance();
                    closed = true;
                    break;
                }
                Advance();
            }
            SourceSpan span = new SourceSpan(start, CurrentPos());
            if (!closed)
            {
                Report(CudaDiagnosticCodes.LexUnterminatedComment, ParseSeverity.Error, span, "Unterminated block comment");
            }
            Emit(CudaTokenKind.CommentBlock, source_.Substring(start.Offset, offset_ - start.Offset), start);
        }

        private void LexIdentifierOrKeyword()
        {
            SourcePosition start = CurrentPos();
            while (!AtEnd() && IsIdentPart(Peek())) Advance();
            string text = source_.Substring(start.Offset, offset_ - start.Offset);
            CudaTokenKind kind = Keywords.KeywordKind(text);

            // Object-like macro expansion (#define NAME VALUE). Only genuine
            // identifiers (never keywords) are candidates. We expand ONE level —
            // the replacement is re-lexed with no further macro table — which
            // resolves the common `#define N 5` idiom (both in `<<<1, N>>>` and in
            // the kernel body) while being trivially safe against recursive or
            // self-referential macros. A macro that re-lexes to no significant
            // tokens falls through and the original identifier is emitted.
            if (kind == CudaTokenKind.Identifier && defines_ != null)
            {
                string macroValue;
                if (defines_.TryGetValue(text, out macroValue) &&
                    ExpandObjectMacro(macroValue, start))
                {
                    return;
                }
            }

            Emit(kind, text, start);
        }

        /// <summary>
        /// Re-lex an object-like macro's replacement text and splice the resulting
        /// tokens into the stream at the macro's use-site span (so diagnostics still
        /// point at the original source). Returns true iff at least one significant
        /// token was emitted. The sub-lexer is given no define table, so expansion
        /// is single-level and cannot loop.
        /// </summary>
        private bool ExpandObjectMacro(string value, SourcePosition useSite)
        {
            if (string.IsNullOrEmpty(value)) return false;
            CudaLexer sub = new CudaLexer(value, fileName_, null);
            List<CudaToken> subToks = sub.Tokenize();
            SourceSpan span = new SourceSpan(useSite, CurrentPos());
            bool emittedAny = false;
            for (int i = 0; i < subToks.Count; ++i)
            {
                CudaToken t = subToks[i];
                if (t.Kind == CudaTokenKind.EndOfFile) continue;
                if (t.Kind == CudaTokenKind.PreprocessorDirective) continue;
                tokens_.Add(new CudaToken(t.Kind, t.Text, span));
                emittedAny = true;
            }
            return emittedAny;
        }

        private void LexNumber()
        {
            SourcePosition start = CurrentPos();
            bool isFloat = false;
            bool invalid = false;
            string invalidWhy = null;

            if (Peek() == '0' && (PeekAt(1) == 'b' || PeekAt(1) == 'B')
                && (PeekAt(2) == '0' || PeekAt(2) == '1'))
            {
                // Binary literal (C++14): 0b1010, 0B1010'0101.
                Advance();  // 0
                Advance();  // b
                int binDigits = 0;
                while (!AtEnd() && (Peek() == '0' || Peek() == '1' || Peek() == '\''))
                {
                    if (Peek() != '\'') ++binDigits;
                    Advance();
                }
                if (binDigits == 0) { invalid = true; invalidWhy = "binary literal has no digits"; }
            }
            else if (Peek() == '0' && (PeekAt(1) == 'x' || PeekAt(1) == 'X'))
            {
                Advance();  // 0
                Advance();  // x
                int hexDigits = 0;
                while (!AtEnd() && (IsHexDigit(Peek()) || Peek() == '\''))
                {
                    if (Peek() != '\'') ++hexDigits;
                    Advance();
                }
                // Hexadecimal FLOATING literal: 0x1p3, 0x1.8p-4, 0x1.fffffffffffffP+1023.
                // The fraction is optional, the binary exponent (p/P) is what makes it a float.
                if (Peek() == '.')
                {
                    Advance();
                    while (!AtEnd() && (IsHexDigit(Peek()) || Peek() == '\'')) { ++hexDigits; Advance(); }
                }
                if (Peek() == 'p' || Peek() == 'P')
                {
                    isFloat = true;
                    Advance();
                    if (Peek() == '+' || Peek() == '-') Advance();
                    int expDigits = 0;
                    while (!AtEnd() && IsDigit(Peek())) { ++expDigits; Advance(); }
                    if (expDigits == 0) { invalid = true; invalidWhy = "binary exponent has no digits"; }
                }
                if (hexDigits == 0 && !invalid) { invalid = true; invalidWhy = "hexadecimal literal has no digits"; }
            }
            else
            {
                while (!AtEnd() && (IsDigit(Peek()) || Peek() == '\'')) Advance();
                if (Peek() == '.')
                {
                    isFloat = true;
                    Advance();
                    while (!AtEnd() && (IsDigit(Peek()) || Peek() == '\'')) Advance();
                }
                if (Peek() == 'e' || Peek() == 'E')
                {
                    isFloat = true;
                    Advance();
                    if (Peek() == '+' || Peek() == '-') Advance();
                    int expDigits = 0;
                    while (!AtEnd() && IsDigit(Peek())) { ++expDigits; Advance(); }
                    if (expDigits == 0) { invalid = true; invalidWhy = "exponent has no digits"; }
                }
                // `1.2.3` — a second decimal point inside one literal. Consume it so the
                // stray tail doesn't lex as a separate number and mask the error.
                if (Peek() == '.' && IsDigit(PeekAt(1)))
                {
                    invalid = true; invalidWhy = "too many decimal points";
                    Advance();
                    while (!AtEnd() && (IsDigit(Peek()) || Peek() == '\'')) Advance();
                }
            }
            // Numeric suffixes. Valid: u/U, l/L, f/F, z/Z (C++23) in any combination;
            // any other trailing letter means a malformed literal like `123abc`.
            while (!AtEnd() && IsAlpha(Peek()))
            {
                char s = Peek();
                if (s == 'f' || s == 'F') isFloat = true;
                else if (s != 'u' && s != 'U' && s != 'l' && s != 'L' && s != 'z' && s != 'Z')
                {
                    if (!invalid) { invalid = true; invalidWhy = "invalid suffix"; }
                }
                Advance();
            }
            string text = source_.Substring(start.Offset, offset_ - start.Offset);
            if (invalid)
            {
                Report(CudaDiagnosticCodes.LexInvalidNumber, ParseSeverity.Error,
                       new SourceSpan(start, CurrentPos()),
                       "Invalid numeric literal '" + text + "' (" + invalidWhy + ")");
            }
            Emit(isFloat ? CudaTokenKind.FloatLiteral : CudaTokenKind.IntLiteral, text, start);
        }

        private void LexString()
        {
            SourcePosition start = CurrentPos();
            Advance();  // opening quote
            bool closed = false;
            while (!AtEnd())
            {
                char c = Peek();
                if (c == '\\') { Advance(); if (!AtEnd()) Advance(); continue; }
                if (c == '\n') break;
                Advance();
                if (c == '"') { closed = true; break; }
            }
            SourceSpan span = new SourceSpan(start, CurrentPos());
            if (!closed)
            {
                Report(CudaDiagnosticCodes.LexUnterminatedString, ParseSeverity.Error, span, "Unterminated string literal");
            }
            Emit(CudaTokenKind.StringLiteral, source_.Substring(start.Offset, offset_ - start.Offset), start);
        }

        private void LexChar()
        {
            SourcePosition start = CurrentPos();
            Advance();  // opening quote
            bool closed = false;
            while (!AtEnd())
            {
                char c = Peek();
                if (c == '\\') { Advance(); if (!AtEnd()) Advance(); continue; }
                if (c == '\n') break;
                Advance();
                if (c == '\'') { closed = true; break; }
            }
            SourceSpan span = new SourceSpan(start, CurrentPos());
            if (!closed)
            {
                Report(CudaDiagnosticCodes.LexUnterminatedString, ParseSeverity.Error, span, "Unterminated character literal");
            }
            Emit(CudaTokenKind.CharLiteral, source_.Substring(start.Offset, offset_ - start.Offset), start);
        }

        private void LexOperator()
        {
            SourcePosition start = CurrentPos();
            char c = Peek();
            char c1 = PeekAt(1);
            char c2 = PeekAt(2);

            switch (c)
            {
                case '<':
                    if (c1 == '<' && c2 == '<') { EmitOp(CudaTokenKind.TripleLess, 3, start); return; }
                    if (c1 == '<' && c2 == '=') { EmitOp(CudaTokenKind.LeftShiftAssign, 3, start); return; }
                    // C++20 three-way comparison `<=>`, checked before `<=`.
                    if (c1 == '=' && c2 == '>') { EmitOp(CudaTokenKind.Spaceship, 3, start); return; }
                    if (c1 == '<') { EmitOp(CudaTokenKind.LeftShift, 2, start); return; }
                    if (c1 == '=') { EmitOp(CudaTokenKind.LessEqual, 2, start); return; }
                    EmitOp(CudaTokenKind.Less, 1, start); return;
                case '>':
                    if (c1 == '>' && c2 == '>') { EmitOp(CudaTokenKind.TripleGreater, 3, start); return; }
                    if (c1 == '>' && c2 == '=') { EmitOp(CudaTokenKind.RightShiftAssign, 3, start); return; }
                    if (c1 == '>') { EmitOp(CudaTokenKind.RightShift, 2, start); return; }
                    if (c1 == '=') { EmitOp(CudaTokenKind.GreaterEqual, 2, start); return; }
                    EmitOp(CudaTokenKind.Greater, 1, start); return;
                case '+':
                    if (c1 == '+') { EmitOp(CudaTokenKind.PlusPlus, 2, start); return; }
                    if (c1 == '=') { EmitOp(CudaTokenKind.PlusAssign, 2, start); return; }
                    EmitOp(CudaTokenKind.Plus, 1, start); return;
                case '-':
                    if (c1 == '>') { EmitOp(CudaTokenKind.Arrow, 2, start); return; }
                    if (c1 == '-') { EmitOp(CudaTokenKind.MinusMinus, 2, start); return; }
                    if (c1 == '=') { EmitOp(CudaTokenKind.MinusAssign, 2, start); return; }
                    EmitOp(CudaTokenKind.Minus, 1, start); return;
                case '*':
                    if (c1 == '=') { EmitOp(CudaTokenKind.StarAssign, 2, start); return; }
                    EmitOp(CudaTokenKind.Star, 1, start); return;
                case '/':
                    if (c1 == '=') { EmitOp(CudaTokenKind.SlashAssign, 2, start); return; }
                    EmitOp(CudaTokenKind.Slash, 1, start); return;
                case '%':
                    if (c1 == '=') { EmitOp(CudaTokenKind.PercentAssign, 2, start); return; }
                    EmitOp(CudaTokenKind.Percent, 1, start); return;
                case '&':
                    if (c1 == '&') { EmitOp(CudaTokenKind.AmpAmp, 2, start); return; }
                    if (c1 == '=') { EmitOp(CudaTokenKind.AmpAssign, 2, start); return; }
                    EmitOp(CudaTokenKind.Amp, 1, start); return;
                case '|':
                    if (c1 == '|') { EmitOp(CudaTokenKind.PipePipe, 2, start); return; }
                    if (c1 == '=') { EmitOp(CudaTokenKind.PipeAssign, 2, start); return; }
                    EmitOp(CudaTokenKind.Pipe, 1, start); return;
                case '^':
                    if (c1 == '=') { EmitOp(CudaTokenKind.CaretAssign, 2, start); return; }
                    EmitOp(CudaTokenKind.Caret, 1, start); return;
                case '~': EmitOp(CudaTokenKind.Tilde, 1, start); return;
                case '!':
                    if (c1 == '=') { EmitOp(CudaTokenKind.NotEqual, 2, start); return; }
                    EmitOp(CudaTokenKind.Bang, 1, start); return;
                case '=':
                    if (c1 == '=') { EmitOp(CudaTokenKind.EqualEqual, 2, start); return; }
                    EmitOp(CudaTokenKind.Assign, 1, start); return;
                case ':':
                    if (c1 == ':') { EmitOp(CudaTokenKind.Scope, 2, start); return; }
                    EmitOp(CudaTokenKind.Colon, 1, start); return;
                case '.':
                    if (c1 == '.' && c2 == '.') { EmitOp(CudaTokenKind.Ellipsis, 3, start); return; }
                    EmitOp(CudaTokenKind.Dot, 1, start); return;
                case '(': EmitOp(CudaTokenKind.LeftParen, 1, start); return;
                case ')': EmitOp(CudaTokenKind.RightParen, 1, start); return;
                case '{': EmitOp(CudaTokenKind.LeftBrace, 1, start); return;
                case '}': EmitOp(CudaTokenKind.RightBrace, 1, start); return;
                case '[': EmitOp(CudaTokenKind.LeftBracket, 1, start); return;
                case ']': EmitOp(CudaTokenKind.RightBracket, 1, start); return;
                case ';': EmitOp(CudaTokenKind.Semicolon, 1, start); return;
                case ',': EmitOp(CudaTokenKind.Comma, 1, start); return;
                case '?': EmitOp(CudaTokenKind.Question, 1, start); return;
                default: break;
            }

            // Unknown character — emit a single Unknown token and report.
            EmitOp(CudaTokenKind.Unknown, 1, start);
            SourceSpan uspan = new SourceSpan(start, CurrentPos());
            Report(CudaDiagnosticCodes.LexUnexpectedCharacter, ParseSeverity.Error, uspan, "Unexpected character '" + c + "'");
        }

        private static bool IsAlpha(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool IsIdentStart(char c)
        {
            return IsAlpha(c) || c == '_';
        }

        private static bool IsIdentPart(char c)
        {
            return IsAlpha(c) || IsDigit(c) || c == '_';
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        private static bool IsHexDigit(char c)
        {
            return IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
