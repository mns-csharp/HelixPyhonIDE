using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Lexing
{
    /// <summary>
    /// PEP 617-compliant tokenizer for Python 3.12. Produces a stream of tokens
    /// that includes layout tokens (NewLine, Indent, Dedent) and preserves
    /// whitespace and comments as trivia for lossless round-tripping.
    ///
    /// F-strings follow PEP 701: `f"..."` is decomposed into FStringStart,
    /// alternating FStringMiddle and nested expression tokens, and FStringEnd.
    /// </summary>
    public sealed class PythonLexer
    {
        private readonly string _source;
        private readonly string _fileName;
        private readonly List<Token> _tokens = new List<Token>();
        private readonly List<ParseDiagnostic> _diagnostics = new List<ParseDiagnostic>();
        private readonly Stack<int> _indentStack = new Stack<int>();
        private readonly Stack<FStringState> _fstringStack = new Stack<FStringState>();
        private int _offset;
        private int _line = 1;
        private int _column = 1;
        private int _bracketDepth;
        private bool _atLineStart = true;

        public PythonLexer(string source, string fileName = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _fileName = fileName ?? string.Empty;
            _indentStack.Push(0);
        }

        public IReadOnlyList<ParseDiagnostic> Diagnostics => _diagnostics;

        public IReadOnlyList<Token> Tokenize(
            System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            // Cooperative cancellation: lexing a very large file is a single pass with no other
            // check-points, so poll the token periodically (cheap — every few thousand chars) so
            // Cancel stops a giant file during tokenization rather than only after the whole file
            // is lexed. The parser then checks again between top-level statements.
            int sinceCancelCheck = 0;
            while (_offset < _source.Length)
            {
                if ((++sinceCancelCheck & 0x1FFF) == 0) cancel.ThrowIfCancellationRequested();

                if (_fstringStack.Count > 0 && _fstringStack.Peek().InExpression == false)
                {
                    LexFStringBody();
                    continue;
                }

                if (_atLineStart && _bracketDepth == 0)
                {
                    HandleIndentation();
                    _atLineStart = false;
                    if (_offset >= _source.Length) break;
                }

                char c = _source[_offset];

                if (c == '#')
                {
                    LexComment();
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    LexNewline();
                    continue;
                }

                if (c == '\\' && PeekNext() == '\n')
                {
                    SourcePosition start = CurrentPosition();
                    Advance(); Advance();
                    _tokens.Add(new Token(TokenKind.LineContinuation, "\\\n", new SourceSpan(start, CurrentPosition())));
                    continue;
                }
                if (c == '\\' && PeekNext() == '\r')
                {
                    SourcePosition start = CurrentPosition();
                    Advance(); Advance();
                    if (_offset < _source.Length && _source[_offset] == '\n') Advance();
                    _tokens.Add(new Token(TokenKind.LineContinuation, "\\\r\n", new SourceSpan(start, CurrentPosition())));
                    continue;
                }

                if (c == ' ' || c == '\t')
                {
                    LexInlineWhitespace();
                    continue;
                }

                if (IsStringStart(out int prefixLen, out bool isFString, out bool isRaw, out bool isBytes))
                {
                    if (isFString) LexFStringStart(prefixLen, isRaw);
                    else LexString(prefixLen, isRaw, isBytes);
                    continue;
                }

                if (IsIdentifierStartAt(_offset))
                {
                    LexIdentifierOrKeyword();
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && _offset + 1 < _source.Length && char.IsDigit(_source[_offset + 1])))
                {
                    LexNumber();
                    continue;
                }

                LexOperator();
            }

            // Flush remaining dedents.
            while (_indentStack.Count > 1)
            {
                _indentStack.Pop();
                _tokens.Add(new Token(TokenKind.Dedent, string.Empty, new SourceSpan(CurrentPosition(), CurrentPosition())));
            }

            _tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, new SourceSpan(CurrentPosition(), CurrentPosition())));
            return _tokens;
        }

        // ---------- Indentation ----------

        private void HandleIndentation()
        {
            int indent = 0;
            int start = _offset;
            int startCol = _column;
            int startLine = _line;
            while (_offset < _source.Length && (_source[_offset] == ' ' || _source[_offset] == '\t'))
            {
                // PEP 8 tab stops at 8. For token text we keep exact bytes; for indent comparison we use normalized width.
                if (_source[_offset] == '\t') indent = ((indent / 8) + 1) * 8;
                else indent++;
                Advance();
            }
            // Blank line or comment-only line: do not emit indent tokens.
            if (_offset >= _source.Length) return;
            char c = _source[_offset];
            if (c == '\r' || c == '\n' || c == '#') return;

            int current = _indentStack.Peek();
            if (indent > current)
            {
                _indentStack.Push(indent);
                _tokens.Add(new Token(TokenKind.Indent, _source.Substring(start, _offset - start),
                    new SourceSpan(new SourcePosition(start, startLine, startCol), CurrentPosition())));
            }
            else if (indent < current)
            {
                while (_indentStack.Count > 0 && _indentStack.Peek() > indent)
                {
                    _indentStack.Pop();
                    _tokens.Add(new Token(TokenKind.Dedent, string.Empty,
                        new SourceSpan(CurrentPosition(), CurrentPosition())));
                }
                if (_indentStack.Count == 0 || _indentStack.Peek() != indent)
                {
                    Report(DiagnosticCodes.InconsistentIndent, ParseSeverity.Error,
                        new SourceSpan(new SourcePosition(start, startLine, startCol), CurrentPosition()),
                        "Inconsistent dedent.");
                    _indentStack.Push(indent);
                }
            }
        }

        private void LexNewline()
        {
            SourcePosition start = CurrentPosition();
            char c = _source[_offset];
            int len = 0;
            if (c == '\r')
            {
                Advance();
                len++;
                if (_offset < _source.Length && _source[_offset] == '\n') { Advance(); len++; }
            }
            else // '\n'
            {
                Advance();
                len++;
            }
            _line++;
            _column = 1;

            // Inside brackets, physical newlines are invisible to the logical line structure.
            if (_bracketDepth > 0) return;

            _tokens.Add(new Token(TokenKind.NewLine, _source.Substring(_offset - len, len),
                new SourceSpan(start, CurrentPosition())));
            _atLineStart = true;
        }

        private void LexInlineWhitespace()
        {
            SourcePosition start = CurrentPosition();
            int s = _offset;
            while (_offset < _source.Length && (_source[_offset] == ' ' || _source[_offset] == '\t'))
                Advance();
            // Inline whitespace inside a line is trivia.
            _tokens.Add(new Token(TokenKind.Whitespace, _source.Substring(s, _offset - s),
                new SourceSpan(start, CurrentPosition())));
        }

        private void LexComment()
        {
            SourcePosition start = CurrentPosition();
            int s = _offset;
            while (_offset < _source.Length && _source[_offset] != '\n' && _source[_offset] != '\r')
                Advance();
            _tokens.Add(new Token(TokenKind.Comment, _source.Substring(s, _offset - s),
                new SourceSpan(start, CurrentPosition())));
        }

        // ---------- Identifiers / keywords ----------

        private bool IsIdentifierStartAt(int offset)
        {
            if (offset < 0 || offset >= _source.Length) return false;
            if (_source[offset] == '_') return true;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(_source, offset);
            return category == UnicodeCategory.UppercaseLetter
                || category == UnicodeCategory.LowercaseLetter
                || category == UnicodeCategory.TitlecaseLetter
                || category == UnicodeCategory.ModifierLetter
                || category == UnicodeCategory.OtherLetter
                || category == UnicodeCategory.LetterNumber;
        }

        private bool IsIdentifierPartAt(int offset)
        {
            if (IsIdentifierStartAt(offset)) return true;
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(_source, offset);
            return category == UnicodeCategory.NonSpacingMark
                || category == UnicodeCategory.SpacingCombiningMark
                || category == UnicodeCategory.DecimalDigitNumber
                || category == UnicodeCategory.ConnectorPunctuation;
        }

        private void AdvanceIdentifierCodePoint()
        {
            if (_offset + 1 < _source.Length &&
                char.IsHighSurrogate(_source[_offset]) &&
                char.IsLowSurrogate(_source[_offset + 1]))
            {
                Advance();
                Advance();
            }
            else Advance();
        }

        private void LexIdentifierOrKeyword()
        {
            SourcePosition start = CurrentPosition();
            int s = _offset;
            while (_offset < _source.Length && IsIdentifierPartAt(_offset))
                AdvanceIdentifierCodePoint();
            string text = _source.Substring(s, _offset - s);
            TokenKind kind = Keywords.Map.TryGetValue(text, out TokenKind k) ? k : TokenKind.Name;
            _tokens.Add(new Token(kind, text, new SourceSpan(start, CurrentPosition())));
        }

        // ---------- Numbers ----------

        private void LexNumber()
        {
            SourcePosition start = CurrentPosition();
            int s = _offset;
            bool isFloat = false;
            bool isComplex = false;
            bool error = false;

            if (_source[_offset] == '0' && _offset + 1 < _source.Length)
            {
                char p = _source[_offset + 1];
                if (p == 'x' || p == 'X') { Advance(); Advance(); ConsumeDigitsWithUnderscores(IsHexDigit); goto done; }
                if (p == 'o' || p == 'O') { Advance(); Advance(); ConsumeDigitsWithUnderscores(IsOctDigit); goto done; }
                if (p == 'b' || p == 'B') { Advance(); Advance(); ConsumeDigitsWithUnderscores(IsBinDigit); goto done; }
            }

            ConsumeDigitsWithUnderscores(char.IsDigit);
            if (_offset < _source.Length && _source[_offset] == '.')
            {
                // Only treat '.' as part of the number when followed by digit OR when we already saw digits.
                if (_offset + 1 < _source.Length && char.IsDigit(_source[_offset + 1]))
                {
                    isFloat = true;
                    Advance();
                    ConsumeDigitsWithUnderscores(char.IsDigit);
                }
                else if (_offset > s)
                {
                    isFloat = true;
                    Advance();
                }
            }
            if (_offset < _source.Length && (_source[_offset] == 'e' || _source[_offset] == 'E'))
            {
                isFloat = true;
                Advance();
                if (_offset < _source.Length && (_source[_offset] == '+' || _source[_offset] == '-')) Advance();
                if (_offset >= _source.Length || !char.IsDigit(_source[_offset]))
                {
                    error = true;
                }
                else
                {
                    ConsumeDigitsWithUnderscores(char.IsDigit);
                }
            }
            if (_offset < _source.Length && (_source[_offset] == 'j' || _source[_offset] == 'J'))
            {
                isComplex = true;
                Advance();
            }

            done:
            string text = _source.Substring(s, _offset - s);
            SourceSpan span = new SourceSpan(start, CurrentPosition());
            if (error) Report(DiagnosticCodes.InvalidNumber, ParseSeverity.Error, span, "Invalid numeric literal.");
            object value = ParseNumeric(text, isFloat, isComplex);
            _tokens.Add(new Token(TokenKind.Number, text, span, value));
        }

        private void ConsumeDigitsWithUnderscores(Func<char, bool> isDigit)
        {
            bool lastWasUnderscore = false;
            while (_offset < _source.Length)
            {
                char c = _source[_offset];
                if (isDigit(c)) { Advance(); lastWasUnderscore = false; }
                else if (c == '_' && !lastWasUnderscore && _offset + 1 < _source.Length && isDigit(_source[_offset + 1]))
                {
                    Advance();
                    lastWasUnderscore = true;
                }
                else break;
            }
        }

        private static bool IsHexDigit(char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }
        private static bool IsOctDigit(char c) { return c >= '0' && c <= '7'; }
        private static bool IsBinDigit(char c) { return c == '0' || c == '1'; }

        private static object ParseNumeric(string text, bool isFloat, bool isComplex)
        {
            string cleaned = text.Replace("_", string.Empty);
            if (isComplex)
            {
                string trimmed = cleaned.Substring(0, cleaned.Length - 1);
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return new System.Numerics.Complex(0, d);
                return null;
            }
            if (isFloat)
            {
                if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return d;
                return null;
            }
            if (cleaned.Length >= 2 && cleaned[0] == '0')
            {
                char p = cleaned[1];
                try
                {
                    if (p == 'x' || p == 'X') return Convert.ToInt64(cleaned.Substring(2), 16);
                    if (p == 'o' || p == 'O') return Convert.ToInt64(cleaned.Substring(2), 8);
                    if (p == 'b' || p == 'B') return Convert.ToInt64(cleaned.Substring(2), 2);
                }
                catch (OverflowException) { return BigFromRadix(cleaned.Substring(2), p); }
                catch (FormatException) { return null; }
            }
            if (long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i)) return i;

            // Python integers are arbitrary precision, so a literal that does not fit in a long is
            // still an int. Returning the decimal TEXT here (as this used to) made every consumer
            // see a string: the type inferencer reported `str` for `x = 10**30`, and the NASM
            // backend reported "string constants are not supported" for an integer.
            System.Numerics.BigInteger big;
            if (System.Numerics.BigInteger.TryParse(cleaned, NumberStyles.Integer,
                                                    CultureInfo.InvariantCulture, out big))
                return big;
            return null;
        }

        /// <summary>A hex/octal/binary literal too large for <c>long</c>, as a <c>BigInteger</c>.</summary>
        private static object BigFromRadix(string digits, char prefix)
        {
            int radix = (prefix == 'x' || prefix == 'X') ? 16 : (prefix == 'o' || prefix == 'O') ? 8 : 2;
            System.Numerics.BigInteger value = System.Numerics.BigInteger.Zero;
            foreach (char c in digits)
            {
                int d;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
                else return null;
                if (d >= radix) return null;
                value = value * radix + d;
            }
            return value;
        }

        // ---------- Strings ----------

        private bool IsStringStart(out int prefixLen, out bool isFString, out bool isRaw, out bool isBytes)
        {
            prefixLen = 0;
            isFString = false;
            isRaw = false;
            isBytes = false;

            int p = _offset;
            int maxPrefix = 2;
            int seen = 0;
            while (seen < maxPrefix && p < _source.Length)
            {
                char c = _source[p];
                if ((c == 'r' || c == 'R') && !isRaw) { isRaw = true; p++; seen++; continue; }
                if ((c == 'b' || c == 'B') && !isBytes && !isFString) { isBytes = true; p++; seen++; continue; }
                if ((c == 'f' || c == 'F') && !isFString && !isBytes) { isFString = true; p++; seen++; continue; }
                if ((c == 'u' || c == 'U') && seen == 0 && !isRaw && !isBytes && !isFString) { p++; seen++; continue; }
                break;
            }
            if (p >= _source.Length) return false;
            char q = _source[p];
            if (q != '"' && q != '\'') return false;
            prefixLen = p - _offset;
            return true;
        }

        private void LexString(int prefixLen, bool isRaw, bool isBytes)
        {
            SourcePosition start = CurrentPosition();
            int startOffset = _offset;
            for (int i = 0; i < prefixLen; i++) Advance();
            char quote = _source[_offset];
            bool triple = _offset + 2 < _source.Length && _source[_offset + 1] == quote && _source[_offset + 2] == quote;
            if (triple) { Advance(); Advance(); Advance(); }
            else Advance();

            StringBuilder body = new StringBuilder();
            while (_offset < _source.Length)
            {
                char c = _source[_offset];
                if (triple)
                {
                    // A backslash escapes the next character for TERMINATION purposes here too, so the
                    // escaped quote in `"""... "%(url)s\""""` is content and does not begin the closing
                    // `"""` one quote early (Django staticfiles/storage.py:78). Same rule as the single-
                    // line branch below, and applies to raw literals as well (CPython).
                    if (c == '\\')
                    {
                        Advance();
                        if (_offset < _source.Length)
                        {
                            if (_source[_offset] == '\n') { _line++; _column = 1; }
                            body.Append('\\'); body.Append(_source[_offset]); Advance();
                        }
                        else body.Append('\\');
                        continue;
                    }
                    if (c == quote && _offset + 2 < _source.Length && _source[_offset + 1] == quote && _source[_offset + 2] == quote)
                    { Advance(); Advance(); Advance(); goto done; }
                    if (c == '\n') { _line++; _column = 1; }
                    body.Append(c);
                    Advance();
                }
                else
                {
                    if (c == '\n' || c == '\r')
                    {
                        Report(DiagnosticCodes.UnterminatedString, ParseSeverity.Error,
                            new SourceSpan(start, CurrentPosition()), "Unterminated string literal.");
                        break;
                    }
                    if (c == quote) { Advance(); goto done; }
                    if (c == '\\')
                    {
                        // A backslash escapes the NEXT character for termination purposes in BOTH raw
                        // and non-raw strings. r'x\'y' is one raw string whose content is x\'y — the
                        // \' does not close it — not r'x\' followed by a stray y and an unterminated
                        // quote. CPython: "even in a raw literal, quotes can be escaped with a
                        // backslash, but the backslash remains in the result." The backslash is always
                        // kept; a raw string keeps the escaped char literally (no decoding), a non-raw
                        // string keeps the pair for later escape processing. Before iter 358 this was
                        // gated on !isRaw, so r'...["\']...' (idiomatic in requests' regexes) closed at
                        // the \' and cascaded into ~128 phantom parse errors on three lines.
                        Advance();
                        if (_offset < _source.Length && _source[_offset] == '\r' &&
                            _offset + 1 < _source.Length && _source[_offset + 1] == '\n')
                        {
                            body.Append('\\'); body.Append('\r'); body.Append('\n');
                            Advance(); Advance();
                            _line++;
                            _column = 1;
                        }
                        else if (_offset < _source.Length && _source[_offset] == '\n')
                        {
                            body.Append('\\'); body.Append('\n');
                            Advance();
                            _line++;
                            _column = 1;
                        }
                        else if (_offset < _source.Length) { body.Append('\\'); body.Append(_source[_offset]); Advance(); }
                        else { body.Append('\\'); }
                    }
                    else
                    {
                        body.Append(c);
                        Advance();
                    }
                }
            }

            done:
            SourceSpan span = new SourceSpan(start, CurrentPosition());
            string raw = _source.Substring(startOffset, _offset - startOffset);
            if (!isRaw) ValidateStringEscapes(raw, start, isBytes);
            _tokens.Add(new Token(TokenKind.String, raw, span, body.ToString()));
        }

        /// <summary>
        /// Port of CPython's <c>Parser/string_parser.c</c> invalid-escape warning
        /// (emitted as <c>SyntaxWarning</c> there, <see cref="ParseSeverity.Warning"/>
        /// here). Scans the raw token text for <c>\X</c> pairs and flags any escape
        /// that isn't in Python's allowed set. Called only for non-raw strings; raw
        /// strings treat backslashes as literal characters and have no escape syntax.
        /// </summary>
        /// <param name="raw">Raw source substring covering the whole string literal
        /// including quote delimiters.</param>
        /// <param name="stringStart">Position of the first char of <paramref name="raw"/>.</param>
        /// <param name="isBytes">Whether the string is a byte string — bytes disallow
        /// <c>\N{...}</c>, <c>\uHHHH</c>, <c>\UHHHHHHHH</c>.</param>
        private void ValidateStringEscapes(string raw, SourcePosition stringStart, bool isBytes)
        {
            if (string.IsNullOrEmpty(raw)) return;
            int offset = stringStart.Offset;
            int line = stringStart.Line;
            int col = stringStart.Column;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char next = raw[i + 1];
                    bool ok;
                    switch (next)
                    {
                        case '\\': case '\'': case '"':
                        case 'a': case 'b': case 'f': case 'n': case 'r': case 't': case 'v':
                        case '\n': case '\r':
                        case '0': case '1': case '2': case '3':
                        case '4': case '5': case '6': case '7':
                        case 'x':
                            ok = true; break;
                        case 'N': case 'u': case 'U':
                            ok = !isBytes; break;
                        default:
                            ok = false; break;
                    }
                    if (!ok)
                    {
                        SourcePosition escStart = new SourcePosition(offset, line, col);
                        SourcePosition escEnd = new SourcePosition(offset + 2, line, col + 2);
                        Report(DiagnosticCodes.InvalidStringEscape, ParseSeverity.Warning,
                            new SourceSpan(escStart, escEnd),
                            "invalid escape sequence '\\" + next + "'");
                    }
                    // Consume both the backslash and the following character so the
                    // next iteration starts at the char after the escape.
                    offset += 2;
                    if (next == '\n') { line++; col = 1; }
                    else col += 2;
                    i++;   // skip the next-char
                }
                else
                {
                    offset++;
                    if (c == '\n') { line++; col = 1; }
                    else col++;
                }
            }
        }

        // ---------- F-strings (PEP 701 simplified) ----------

        private sealed class FStringState
        {
            public char Quote;
            public bool Triple;
            public bool IsRaw;
            public int BraceDepth;      // counts { / } inside an expression
            public bool InExpression;   // true when we are tokenizing a replacement expression
            public int FieldBracketDepth; // _bracketDepth right after this field's '{' opened
        }

        private void LexFStringStart(int prefixLen, bool isRaw)
        {
            SourcePosition start = CurrentPosition();
            int s = _offset;
            for (int i = 0; i < prefixLen; i++) Advance();
            char quote = _source[_offset];
            bool triple = _offset + 2 < _source.Length && _source[_offset + 1] == quote && _source[_offset + 2] == quote;
            if (triple) { Advance(); Advance(); Advance(); }
            else Advance();

            string text = _source.Substring(s, _offset - s);
            _tokens.Add(new Token(TokenKind.FStringStart, text, new SourceSpan(start, CurrentPosition())));
            _fstringStack.Push(new FStringState { Quote = quote, Triple = triple, IsRaw = isRaw });
        }

        private void LexFStringBody()
        {
            FStringState state = _fstringStack.Peek();
            SourcePosition start = CurrentPosition();
            int s = _offset;
            StringBuilder body = new StringBuilder();

            while (_offset < _source.Length)
            {
                char c = _source[_offset];
                if (c == '{' && _offset + 1 < _source.Length && _source[_offset + 1] == '{')
                { body.Append('{'); Advance(); Advance(); continue; }
                if (c == '}' && _offset + 1 < _source.Length && _source[_offset + 1] == '}')
                { body.Append('}'); Advance(); Advance(); continue; }

                if (c == '{')
                {
                    if (_offset > s)
                    {
                        _tokens.Add(new Token(TokenKind.FStringMiddle, _source.Substring(s, _offset - s),
                            new SourceSpan(start, CurrentPosition()), body.ToString()));
                    }
                    // Emit the opening brace as LeftBrace and switch to expression mode.
                    SourcePosition bs = CurrentPosition();
                    Advance();
                    _tokens.Add(new Token(TokenKind.LeftBrace, "{", new SourceSpan(bs, CurrentPosition())));
                    state.InExpression = true;
                    state.BraceDepth = 1;
                    _bracketDepth++;
                    state.FieldBracketDepth = _bracketDepth; // for format-spec ':' detection
                    return;
                }

                if (state.Triple)
                {
                    if (c == state.Quote && _offset + 2 < _source.Length && _source[_offset + 1] == state.Quote && _source[_offset + 2] == state.Quote)
                    {
                        if (_offset > s)
                        {
                            _tokens.Add(new Token(TokenKind.FStringMiddle, _source.Substring(s, _offset - s),
                                new SourceSpan(start, CurrentPosition()), body.ToString()));
                        }
                        SourcePosition es = CurrentPosition();
                        Advance(); Advance(); Advance();
                        _tokens.Add(new Token(TokenKind.FStringEnd, _source.Substring(es.Offset, 3),
                            new SourceSpan(es, CurrentPosition())));
                        _fstringStack.Pop();
                        return;
                    }
                    if (c == '\n') { _line++; _column = 1; }
                    body.Append(c);
                    Advance();
                }
                else
                {
                    if (c == '\n' || c == '\r')
                    {
                        Report(DiagnosticCodes.UnterminatedString, ParseSeverity.Error,
                            new SourceSpan(start, CurrentPosition()), "Unterminated f-string literal.");
                        _fstringStack.Pop();
                        return;
                    }
                    if (c == state.Quote)
                    {
                        if (_offset > s)
                        {
                            _tokens.Add(new Token(TokenKind.FStringMiddle, _source.Substring(s, _offset - s),
                                new SourceSpan(start, CurrentPosition()), body.ToString()));
                        }
                        SourcePosition es = CurrentPosition();
                        Advance();
                        _tokens.Add(new Token(TokenKind.FStringEnd, state.Quote.ToString(),
                            new SourceSpan(es, CurrentPosition())));
                        _fstringStack.Pop();
                        return;
                    }
                    if (c == '\\' && !state.IsRaw)
                    {
                        Advance();
                        if (_offset < _source.Length && _source[_offset] == '\r' &&
                            _offset + 1 < _source.Length && _source[_offset + 1] == '\n')
                        {
                            body.Append('\\'); body.Append('\r'); body.Append('\n');
                            Advance(); Advance();
                            _line++;
                            _column = 1;
                        }
                        else if (_offset < _source.Length && _source[_offset] == '\n')
                        {
                            body.Append('\\'); body.Append('\n');
                            Advance();
                            _line++;
                            _column = 1;
                        }
                        else if (_offset < _source.Length) { body.Append('\\'); body.Append(_source[_offset]); Advance(); }
                    }
                    else
                    {
                        body.Append(c);
                        Advance();
                    }
                }
            }

            Report(DiagnosticCodes.UnterminatedString, ParseSeverity.Error,
                new SourceSpan(start, CurrentPosition()), "Unterminated f-string literal.");
            _fstringStack.Pop();
        }

        // Consume an f-string format spec (the text after ':' up to the field's closing '}') as one
        // literal FStringMiddle token. Brace-aware so a nested replacement `{x:>{w}}` doesn't end the
        // field early. The closing '}' is left for the '}' handler, which closes the field.
        private void LexFStringFormatSpec()
        {
            SourcePosition start = CurrentPosition();
            int s = _offset;
            int depth = 0;
            while (_offset < _source.Length)
            {
                char c = _source[_offset];
                if (c == '{') { depth++; Advance(); continue; }
                if (c == '}')
                {
                    if (depth == 0) break;   // closes the field — hand back to the '}' handler
                    depth--; Advance(); continue;
                }
                if (!_fstringStack.Peek().Triple && (c == '\n' || c == '\r')) break; // single-line safety
                Advance();
            }
            if (_offset > s)
                _tokens.Add(new Token(TokenKind.FStringMiddle, _source.Substring(s, _offset - s),
                    new SourceSpan(start, CurrentPosition())));
        }

        // ---------- Operators ----------

        private void LexOperator()
        {
            SourcePosition start = CurrentPosition();
            char c = _source[_offset];
            char n1 = PeekNext();
            char n2 = PeekAt(2);

            // Inside an f-string replacement field, a bare '!' introduces a conversion
            // (`{value!r}`, `!s`, `!a`) — it is NOT an operator. Without this the lexer hit the
            // "Unexpected character '!'" fallthrough and every `{x!r}` became a phantom error.
            // Emit `!` (+ the conversion letter) as a benign FStringMiddle token: the f-string
            // parser skips from the end of the expression to the closing '}', so it is consumed
            // there and never reaches expression parsing. `!=` (comparison) still lexes normally.
            if (c == '!' && n1 != '=' &&
                _fstringStack.Count > 0 && _fstringStack.Peek().InExpression)
            {
                int len = (n1 == 'r' || n1 == 's' || n1 == 'a') ? 2 : 1;
                string conv = _source.Substring(_offset, len);
                for (int k = 0; k < len; k++) Advance();
                _tokens.Add(new Token(TokenKind.FStringMiddle, conv, new SourceSpan(start, CurrentPosition())));
                return;
            }

            // Inside an f-string replacement field, a ':' at the field's top level starts a FORMAT
            // SPEC (`{x:8.3f}`, `{p:.2e}`, `{n:<3d}`) — which is LITERAL text, not Python tokens.
            // Lexing it as tokens made `.2e` read `2e` (exponent with no digits) -> "Invalid numeric
            // literal", another phantom-error source. A ':' inside brackets (slice `{a[1:2]}`) or a
            // nested brace (dict `{ {1:2} }`) is NOT a format spec — guard on bracket + brace depth.
            if (c == ':' && _fstringStack.Count > 0)
            {
                FStringState fs = _fstringStack.Peek();
                if (fs.InExpression && fs.BraceDepth == 1 && _bracketDepth == fs.FieldBracketDepth)
                {
                    LexFStringFormatSpec();
                    return;
                }
            }

            // Triple-character: **=, //=, <<=, >>=, ...
            if (c == '*' && n1 == '*' && n2 == '=') { EmitOp(TokenKind.DoubleStarAssign, 3, start); return; }
            if (c == '/' && n1 == '/' && n2 == '=') { EmitOp(TokenKind.DoubleSlashAssign, 3, start); return; }
            if (c == '<' && n1 == '<' && n2 == '=') { EmitOp(TokenKind.LeftShiftAssign, 3, start); return; }
            if (c == '>' && n1 == '>' && n2 == '=') { EmitOp(TokenKind.RightShiftAssign, 3, start); return; }
            if (c == '.' && n1 == '.' && n2 == '.') { EmitOp(TokenKind.Ellipsis, 3, start); return; }

            // Double-character
            if (c == '*' && n1 == '*') { EmitOp(TokenKind.DoubleStar, 2, start); return; }
            if (c == '*' && n1 == '=') { EmitOp(TokenKind.StarAssign, 2, start); return; }
            if (c == '/' && n1 == '/') { EmitOp(TokenKind.DoubleSlash, 2, start); return; }
            if (c == '/' && n1 == '=') { EmitOp(TokenKind.SlashAssign, 2, start); return; }
            if (c == '<' && n1 == '<') { EmitOp(TokenKind.LeftShift, 2, start); return; }
            if (c == '>' && n1 == '>') { EmitOp(TokenKind.RightShift, 2, start); return; }
            if (c == '<' && n1 == '=') { EmitOp(TokenKind.LessEqual, 2, start); return; }
            if (c == '>' && n1 == '=') { EmitOp(TokenKind.GreaterEqual, 2, start); return; }
            if (c == '=' && n1 == '=') { EmitOp(TokenKind.EqualEqual, 2, start); return; }
            if (c == '!' && n1 == '=') { EmitOp(TokenKind.NotEqual, 2, start); return; }
            if (c == '+' && n1 == '=') { EmitOp(TokenKind.PlusAssign, 2, start); return; }
            if (c == '-' && n1 == '=') { EmitOp(TokenKind.MinusAssign, 2, start); return; }
            if (c == '%' && n1 == '=') { EmitOp(TokenKind.PercentAssign, 2, start); return; }
            if (c == '@' && n1 == '=') { EmitOp(TokenKind.AtAssign, 2, start); return; }
            if (c == '&' && n1 == '=') { EmitOp(TokenKind.AmpAssign, 2, start); return; }
            if (c == '|' && n1 == '=') { EmitOp(TokenKind.PipeAssign, 2, start); return; }
            if (c == '^' && n1 == '=') { EmitOp(TokenKind.CaretAssign, 2, start); return; }
            if (c == '-' && n1 == '>') { EmitOp(TokenKind.Arrow, 2, start); return; }
            if (c == ':' && n1 == '=') { EmitOp(TokenKind.Walrus, 2, start); return; }

            // Single-character
            switch (c)
            {
                case '+': EmitOp(TokenKind.Plus, 1, start); return;
                case '-': EmitOp(TokenKind.Minus, 1, start); return;
                case '*': EmitOp(TokenKind.Star, 1, start); return;
                case '/': EmitOp(TokenKind.Slash, 1, start); return;
                case '%': EmitOp(TokenKind.Percent, 1, start); return;
                case '@': EmitOp(TokenKind.At, 1, start); return;
                case '&': EmitOp(TokenKind.Amp, 1, start); return;
                case '|': EmitOp(TokenKind.Pipe, 1, start); return;
                case '^': EmitOp(TokenKind.Caret, 1, start); return;
                case '~': EmitOp(TokenKind.Tilde, 1, start); return;
                case '<': EmitOp(TokenKind.Less, 1, start); return;
                case '>': EmitOp(TokenKind.Greater, 1, start); return;
                case '=': EmitOp(TokenKind.Assign, 1, start); return;
                case '(': _bracketDepth++; EmitOp(TokenKind.LeftParen, 1, start); return;
                case ')': if (_bracketDepth > 0) _bracketDepth--; EmitOp(TokenKind.RightParen, 1, start); return;
                case '[': _bracketDepth++; EmitOp(TokenKind.LeftBracket, 1, start); return;
                case ']': if (_bracketDepth > 0) _bracketDepth--; EmitOp(TokenKind.RightBracket, 1, start); return;
                case '{':
                    _bracketDepth++;
                    if (_fstringStack.Count > 0 && _fstringStack.Peek().InExpression) _fstringStack.Peek().BraceDepth++;
                    EmitOp(TokenKind.LeftBrace, 1, start); return;
                case '}':
                    if (_fstringStack.Count > 0 && _fstringStack.Peek().InExpression)
                    {
                        FStringState st = _fstringStack.Peek();
                        st.BraceDepth--;
                        if (_bracketDepth > 0) _bracketDepth--;
                        EmitOp(TokenKind.RightBrace, 1, start);
                        if (st.BraceDepth == 0) st.InExpression = false;
                        return;
                    }
                    if (_bracketDepth > 0) _bracketDepth--;
                    EmitOp(TokenKind.RightBrace, 1, start); return;
                case ',': EmitOp(TokenKind.Comma, 1, start); return;
                case ':': EmitOp(TokenKind.Colon, 1, start); return;
                case ';': EmitOp(TokenKind.Semicolon, 1, start); return;
                case '.': EmitOp(TokenKind.Dot, 1, start); return;
            }

            // Unknown character — advance one and report.
            Advance();
            Report(DiagnosticCodes.UnexpectedCharacter, ParseSeverity.Error,
                new SourceSpan(start, CurrentPosition()),
                "Unexpected character '" + c + "'.");
            _tokens.Add(new Token(TokenKind.Unknown, c.ToString(), new SourceSpan(start, CurrentPosition())));
        }

        private void EmitOp(TokenKind kind, int length, SourcePosition start)
        {
            int s = _offset;
            for (int i = 0; i < length; i++) Advance();
            _tokens.Add(new Token(kind, _source.Substring(s, length), new SourceSpan(start, CurrentPosition())));
        }

        // ---------- Utilities ----------

        private void Advance()
        {
            if (_offset < _source.Length)
            {
                char c = _source[_offset];
                _offset++;
                if (c == '\n') { /* handled by LexNewline; position already updated there */ }
                else _column++;
            }
        }

        private char PeekNext() { return _offset + 1 < _source.Length ? _source[_offset + 1] : '\0'; }
        private char PeekAt(int k) { return _offset + k < _source.Length ? _source[_offset + k] : '\0'; }

        private SourcePosition CurrentPosition()
        {
            return new SourcePosition(_offset, _line, _column);
        }

        private void Report(string code, ParseSeverity severity, SourceSpan span, string message)
        {
            _diagnostics.Add(new ParseDiagnostic(code, severity, span, message));
        }
    }
}
