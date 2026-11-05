using System.Text;

namespace HelixPythonNamespace.Terminal.Shell.Lexing;

public sealed class ShellSyntaxException : Exception
{
    public ShellSyntaxException(string message) : base(message) { }
}

/// <summary>
/// Turns shell source into tokens. Words keep their internal structure (quoting, parameter
/// references, substitutions) so the expander does not have to re-scan raw text later.
/// </summary>
public sealed class Lexer
{
    private const string BlankChars = " \t";

    private readonly string _src;
    private readonly List<Token> _tokens = new();
    private readonly List<Token> _pendingHereDocs = new();
    private int _pos;
    private bool _atCommandStart = true;
    private Token? _hereDocAwaitingDelimiter;

    public Lexer(string source) => _src = source ?? string.Empty;

    private bool Eof => _pos >= _src.Length;

    private char Cur => _pos < _src.Length ? _src[_pos] : '\0';

    private char At(int offset) => _pos + offset < _src.Length ? _src[_pos + offset] : '\0';

    public static List<Token> Tokenize(string source) => new Lexer(source).Run();

    public List<Token> Run()
    {
        while (true)
        {
            var skipped = SkipBlanksAndContinuations();

            if (Eof)
            {
                FinishHereDocs();
                Add(new Token { Type = TokenType.EndOfInput, Position = _pos });
                return _tokens;
            }

            var c = Cur;

            if (c == '#')
            {
                while (!Eof && Cur != '\n') _pos++;
                continue;
            }

            if (c == '\n')
            {
                _pos++;
                Add(new Token { Type = TokenType.Newline, Text = "\n", Position = _pos - 1 });
                ReadPendingHereDocBodies();
                _atCommandStart = true;
                continue;
            }

            // Compound tokens that must be grabbed whole, before '(' or '[' is read as anything else.
            if (_atCommandStart && c == '(' && At(1) == '(')
            {
                if (TryReadArithmeticCommand(skipped)) continue;
            }

            if (_atCommandStart && c == '[' && At(1) == '[' && (At(2) == '\0' || BlankChars.Contains(At(2)) || At(2) == '\n'))
            {
                ReadConditionalExpression(skipped);
                continue;
            }

            // '<(' and '>(' are process substitutions, i.e. words, not redirection operators.
            if ((c == '<' || c == '>') && At(1) == '(')
            {
                ReadWordToken(skipped);
                continue;
            }

            if (char.IsAsciiDigit(c) && TryReadIoNumber(skipped)) continue;

            if (TryReadOperator(skipped)) continue;

            ReadWordToken(skipped);
        }
    }

    private void Add(Token token)
    {
        _tokens.Add(token);

        switch (token.Type)
        {
            case TokenType.Semicolon:
            case TokenType.DoubleSemi:
            case TokenType.Ampersand:
            case TokenType.AndIf:
            case TokenType.OrIf:
            case TokenType.Pipe:
            case TokenType.PipeAmp:
            case TokenType.LeftParen:
            case TokenType.RightParen:
            case TokenType.Newline:
                _atCommandStart = true;
                break;
            case TokenType.Word:
                // '{', 'do', 'then' etc. keep us in command position; the parser re-checks anyway.
                var text = token.Word?.LiteralText ?? string.Empty;
                _atCommandStart = text is "{" or "!" or "do" or "then" or "else" or "elif" or "in" or "while"
                    or "until" or "if" or "for" or "time";
                break;
            default:
                _atCommandStart = false;
                break;
        }
    }

    /// <summary>Consumes blanks and backslash-newline pairs; returns true if anything was skipped.</summary>
    private bool SkipBlanksAndContinuations()
    {
        var skipped = false;

        while (!Eof)
        {
            if (BlankChars.Contains(Cur))
            {
                _pos++;
                skipped = true;
            }
            else if (Cur == '\\' && At(1) == '\n')
            {
                _pos += 2;
                skipped = true;
            }
            else if (Cur == '\\' && At(1) == '\r' && At(2) == '\n')
            {
                _pos += 3;
                skipped = true;
            }
            else if (Cur == '\r' && At(1) == '\n')
            {
                _pos++; // normalise CRLF: drop the CR, let the LF become a Newline token
                skipped = true;
            }
            else
            {
                break;
            }
        }

        return skipped;
    }

    private bool TryReadIoNumber(bool skipped)
    {
        var start = _pos;
        var i = _pos;
        while (i < _src.Length && char.IsAsciiDigit(_src[i])) i++;

        if (i >= _src.Length || (_src[i] != '<' && _src[i] != '>')) return false;

        // '2>' is a redirect only when the digits are their own token: `echo 12>x` redirects, but
        // `echo a2>x` does not, and that is handled by never reaching here mid-word.
        var text = _src[start..i];
        _pos = i;
        Add(new Token { Type = TokenType.IoNumber, Text = text, Position = start, AdjacentToPrevious = !skipped });
        return true;
    }

    private static readonly (string Text, TokenType Type)[] Operators =
    {
        ("&>>", TokenType.AmpDGreat),
        ("<<<", TokenType.TripleLess),
        ("<<-", TokenType.DLessDash),
        ("&&", TokenType.AndIf),
        ("||", TokenType.OrIf),
        (";;", TokenType.DoubleSemi),
        (">>", TokenType.DGreat),
        ("<<", TokenType.DLess),
        ("<&", TokenType.LessAnd),
        (">&", TokenType.GreatAnd),
        (">|", TokenType.Clobber),
        ("<>", TokenType.LessGreat),
        ("&>", TokenType.AmpGreat),
        ("|&", TokenType.PipeAmp),
        (";", TokenType.Semicolon),
        ("&", TokenType.Ampersand),
        ("|", TokenType.Pipe),
        ("(", TokenType.LeftParen),
        (")", TokenType.RightParen),
        ("<", TokenType.Less),
        (">", TokenType.Great),
    };

    private bool TryReadOperator(bool skipped)
    {
        foreach (var (text, type) in Operators)
        {
            if (string.CompareOrdinal(_src, _pos, text, 0, text.Length) != 0) continue;

            var start = _pos;
            _pos += text.Length;
            var token = new Token { Type = type, Text = text, Position = start, AdjacentToPrevious = !skipped };

            if (type is TokenType.DLess or TokenType.DLessDash)
            {
                token.HereDoc = new HereDocument { StripTabs = type == TokenType.DLessDash };
                _hereDocAwaitingDelimiter = token;
            }

            Add(token);
            return true;
        }

        return false;
    }

    private bool TryReadArithmeticCommand(bool skipped)
    {
        var save = _pos;
        _pos += 2;

        // Both parens were consumed, so the balancer starts two levels deep.
        var body = ReadBalanced('(', ')', out var closed, initialDepth: 2);

        // `((a))` is arithmetic; `( (cmd) )` is a nested subshell and must fall through to '('.
        if (!closed || !body.EndsWith("))", StringComparison.Ordinal))
        {
            _pos = save;
            return false;
        }

        Add(new Token
        {
            Type = TokenType.ArithmeticCommand,
            Text = body[..^2],
            Position = save,
            AdjacentToPrevious = !skipped,
        });
        return true;
    }

    private void ReadConditionalExpression(bool skipped)
    {
        var start = _pos;
        _pos += 2;
        var sb = new StringBuilder();
        var depth = 0;

        while (!Eof)
        {
            var c = Cur;

            if (c is '\'' or '"')
            {
                var q = c;
                sb.Append(c);
                _pos++;
                while (!Eof && Cur != q)
                {
                    if (Cur == '\\' && q == '"' && _pos + 1 < _src.Length)
                    {
                        sb.Append(Cur).Append(At(1));
                        _pos += 2;
                        continue;
                    }

                    sb.Append(Cur);
                    _pos++;
                }

                if (!Eof)
                {
                    sb.Append(q);
                    _pos++;
                }

                continue;
            }

            if (c == '\\' && _pos + 1 < _src.Length)
            {
                sb.Append(c).Append(At(1));
                _pos += 2;
                continue;
            }

            if (c == '(') depth++;
            if (c == ')') depth--;

            if (depth == 0 && c == ']' && At(1) == ']')
            {
                _pos += 2;
                Add(new Token
                {
                    Type = TokenType.ConditionalExpression,
                    Text = sb.ToString(),
                    Position = start,
                    AdjacentToPrevious = !skipped,
                });
                return;
            }

            sb.Append(c);
            _pos++;
        }

        throw new ShellSyntaxException("unexpected EOF while looking for `]]'");
    }

    // ---------------------------------------------------------------- words

    private void ReadWordToken(bool skipped)
    {
        var start = _pos;
        var word = ReadWord(topLevel: true);

        if (word.IsEmpty && _pos == start)
        {
            // Nothing consumed: avoid an infinite loop on a character we do not understand.
            _pos++;
            word.Parts.Add(new LiteralPart(_src[start].ToString(), false));
        }

        var token = new Token
        {
            Type = TokenType.Word,
            Text = _src[start.._pos],
            Word = word,
            Position = start,
            AdjacentToPrevious = !skipped,
        };

        if (_hereDocAwaitingDelimiter is { HereDoc: { } pending })
        {
            _hereDocAwaitingDelimiter.HereDoc = new HereDocument
            {
                Delimiter = string.Concat(word.Parts.OfType<LiteralPart>().Select(p => p.Text)),
                Quoted = word.Parts.Any(p => p.Quoted),
                StripTabs = pending.StripTabs,
            };
            _pendingHereDocs.Add(_hereDocAwaitingDelimiter);
            _hereDocAwaitingDelimiter = null;
        }

        Add(token);
    }

    private static bool IsWordDelimiter(char c) =>
        c is ' ' or '\t' or '\n' or '\r' or ';' or '&' or '|' or '(' or ')' or '<' or '>' or '\0';

    /// <summary>
    /// Reads one word. At top level it stops at unquoted metacharacters; otherwise it consumes
    /// the whole remaining input (used for the argument halves of <c>${v:-arg}</c>).
    /// </summary>
    private Word ReadWord(bool topLevel)
    {
        var word = new Word();
        var literal = new StringBuilder();
        var literalQuoted = false;
        var tildeAllowed = true;

        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            word.Parts.Add(new LiteralPart(literal.ToString(), literalQuoted));
            literal.Clear();
            literalQuoted = false;
        }

        void AppendLiteral(string text, bool quoted)
        {
            if (literal.Length > 0 && literalQuoted != quoted) FlushLiteral();
            literalQuoted = quoted;
            literal.Append(text);
        }

        while (!Eof)
        {
            var c = Cur;

            if (topLevel && IsWordDelimiter(c))
            {
                // '<(' / '>(' only start a process substitution at the very beginning of a word.
                if ((c == '<' || c == '>') && At(1) == '(' && word.Parts.Count == 0 && literal.Length == 0)
                {
                    var input = c == '<';
                    _pos += 2;
                    var script = ReadBalanced('(', ')', out var closed);
                    if (!closed) throw new ShellSyntaxException("unexpected EOF in process substitution");
                    FlushLiteral();
                    word.Parts.Add(new ProcessSubstitutionPart(script[..^1], input));
                    tildeAllowed = false;
                    continue;
                }

                break;
            }

            switch (c)
            {
                case '\\':
                    _pos++;
                    if (Eof) { AppendLiteral("\\", true); break; }
                    if (Cur == '\n') { _pos++; break; }          // line continuation
                    if (Cur == '\r' && At(1) == '\n') { _pos += 2; break; }
                    AppendLiteral(Cur.ToString(), true);
                    _pos++;
                    tildeAllowed = false;
                    break;

                case '\'':
                {
                    _pos++;
                    var sb = new StringBuilder();
                    while (!Eof && Cur != '\'') { sb.Append(Cur); _pos++; }
                    if (Eof) throw new ShellSyntaxException("unexpected EOF while looking for matching `''");
                    _pos++;
                    AppendLiteral(sb.ToString(), true);
                    // An empty '' still has to produce a field, so make sure a part exists.
                    if (sb.Length == 0) { FlushLiteral(); word.Parts.Add(new LiteralPart(string.Empty, true)); }
                    tildeAllowed = false;
                    break;
                }

                case '"':
                {
                    _pos++;
                    var produced = ReadDoubleQuoted(word, FlushLiteral, AppendLiteral);
                    if (!produced) { FlushLiteral(); word.Parts.Add(new LiteralPart(string.Empty, true)); }
                    tildeAllowed = false;
                    break;
                }

                case '$':
                {
                    var part = ReadDollar(quoted: false, out var literalText);
                    if (part is null)
                    {
                        AppendLiteral(literalText, false);
                    }
                    else
                    {
                        FlushLiteral();
                        word.Parts.Add(part);
                    }

                    tildeAllowed = false;
                    break;
                }

                case '`':
                {
                    _pos++;
                    var script = ReadBackquoted();
                    FlushLiteral();
                    word.Parts.Add(new CommandSubstitutionPart(script) { Quoted = false });
                    tildeAllowed = false;
                    break;
                }

                case '~' when tildeAllowed:
                {
                    _pos++;
                    var sb = new StringBuilder();
                    while (!Eof && (char.IsLetterOrDigit(Cur) || Cur is '_' or '-' or '.')) { sb.Append(Cur); _pos++; }
                    FlushLiteral();
                    word.Parts.Add(new TildePart(sb.ToString()));
                    tildeAllowed = false;
                    break;
                }

                default:
                    AppendLiteral(c.ToString(), false);
                    _pos++;
                    // In `VAR=~/x` and `PATH=a:~/b` the tilde still expands.
                    tildeAllowed = c is '=' or ':' && LooksLikeAssignmentSoFar(word, literal);
                    break;
            }
        }

        FlushLiteral();
        return word;
    }

    /// <summary>A tilde after '=' or ':' only expands inside something shaped like NAME=value.</summary>
    private static bool LooksLikeAssignmentSoFar(Word word, StringBuilder literal)
    {
        var text = string.Concat(word.Parts.OfType<LiteralPart>().Select(p => p.Text)) + literal;
        var eq = text.IndexOf('=');
        if (eq <= 0) return false;

        var name = text[..eq];
        return name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    /// <summary>Reads the body of a double-quoted section. Returns false if the section was empty.</summary>
    private bool ReadDoubleQuoted(Word word, Action flushLiteral, Action<string, bool> appendLiteral)
    {
        var produced = false;

        while (true)
        {
            if (Eof) throw new ShellSyntaxException("unexpected EOF while looking for matching `\"'");

            var c = Cur;
            if (c == '"') { _pos++; return produced; }

            produced = true;

            if (c == '\\')
            {
                var next = At(1);
                if (next is '$' or '`' or '"' or '\\')
                {
                    appendLiteral(next.ToString(), true);
                    _pos += 2;
                    continue;
                }

                if (next == '\n') { _pos += 2; continue; }

                appendLiteral("\\", true);
                _pos++;
                continue;
            }

            if (c == '$')
            {
                var part = ReadDollar(quoted: true, out var literalText);
                if (part is null) appendLiteral(literalText, true);
                else { flushLiteral(); word.Parts.Add(part); }
                continue;
            }

            if (c == '`')
            {
                _pos++;
                var script = ReadBackquoted();
                flushLiteral();
                word.Parts.Add(new CommandSubstitutionPart(script) { Quoted = true });
                continue;
            }

            appendLiteral(c.ToString(), true);
            _pos++;
        }
    }

    /// <summary>
    /// Handles everything that can follow '$'. Returns null when the '$' is not an expansion
    /// after all, in which case <paramref name="literalText"/> holds what to append instead.
    /// </summary>
    private WordPart? ReadDollar(bool quoted, out string literalText)
    {
        literalText = string.Empty;
        _pos++; // consume '$'

        if (Eof) { literalText = "$"; return null; }

        var c = Cur;

        if (c == '(')
        {
            if (At(1) == '(')
            {
                var save = _pos;
                _pos += 2;
                var arith = ReadBalanced('(', ')', out var closedArith, initialDepth: 2);
                if (closedArith && arith.EndsWith("))", StringComparison.Ordinal))
                    return new ArithmeticPart(arith[..^2]) { Quoted = quoted };
                _pos = save;
            }

            _pos++;
            var script = ReadBalanced('(', ')', out var closed);
            if (!closed) throw new ShellSyntaxException("unexpected EOF in command substitution");
            return new CommandSubstitutionPart(script[..^1]) { Quoted = quoted };
        }

        if (c == '{') return ReadBraceParameter(quoted);

        if (c == '\'' && !quoted)
        {
            _pos++;
            var text = ReadAnsiCQuoted();
            return new LiteralPart(text, true);
        }

        if (c == '"' && !quoted)
        {
            // $"..." is a localised string; with no translation catalogue it is just "...".
            literalText = string.Empty;
            return null; // the quote itself is handled by the caller's normal path
        }

        if (char.IsLetter(c) || c == '_')
        {
            var sb = new StringBuilder();
            while (!Eof && (char.IsLetterOrDigit(Cur) || Cur == '_')) { sb.Append(Cur); _pos++; }
            return new ParameterPart { Name = sb.ToString(), Quoted = quoted };
        }

        if (char.IsAsciiDigit(c))
        {
            _pos++;
            return new ParameterPart { Name = c.ToString(), Quoted = quoted };
        }

        if (c is '?' or '$' or '!' or '#' or '@' or '*' or '-' or '0')
        {
            _pos++;
            return new ParameterPart { Name = c.ToString(), Quoted = quoted };
        }

        literalText = "$";
        return null;
    }

    private WordPart ReadBraceParameter(bool quoted)
    {
        _pos++; // consume '{'
        var inner = ReadBalanced('{', '}', out var closed);
        if (!closed) throw new ShellSyntaxException("unexpected EOF while looking for matching `}'");
        inner = inner[..^1];

        return ParseBraceParameter(inner, quoted);
    }

    internal static WordPart ParseBraceParameter(string inner, bool quoted)
    {
        var i = 0;
        var length = false;
        var indirect = false;

        if (inner.Length > 1 && inner[0] == '!' && (char.IsLetter(inner[1]) || inner[1] == '_'))
        {
            indirect = true;
            inner = inner[1..];
        }

        if (inner.Length > 1 && inner[0] == '#' && inner != "#")
        {
            // ${#var} is a length, but ${#} and ${#-x} refer to the parameter '#'.
            var after = inner[1];
            if (char.IsLetterOrDigit(after) || after is '_' or '@' or '*')
            {
                length = true;
                i = 1;
            }
        }

        var nameStart = i;
        if (i < inner.Length && (inner[i] is '@' or '*' or '?' or '$' or '!' or '#' or '-'))
        {
            i++;
        }
        else
        {
            while (i < inner.Length && (char.IsLetterOrDigit(inner[i]) || inner[i] == '_')) i++;
        }

        var name = inner[nameStart..i];
        if (name.Length == 0) throw new ShellSyntaxException($"${{{inner}}}: bad substitution");

        string? subscript = null;
        if (i < inner.Length && inner[i] == '[')
        {
            var close = inner.IndexOf(']', i);
            if (close < 0) throw new ShellSyntaxException($"${{{inner}}}: bad substitution");
            subscript = inner[(i + 1)..close];
            i = close + 1;
        }

        if (i >= inner.Length)
            return new ParameterPart { Name = name, Length = length, Quoted = quoted, Subscript = subscript, Indirect = indirect };

        var rest = inner[i..];
        var colon = false;
        ParameterOperator op;
        var allChars = false;
        string argText;

        if (rest.StartsWith("://") || (rest.StartsWith(':') && rest.Length > 1 && rest[1] is not ('-' or '=' or '?' or '+')))
        {
            op = ParameterOperator.Substring;
            argText = rest[1..];
        }
        else if (rest[0] == ':' && rest.Length > 1)
        {
            colon = true;
            op = rest[1] switch
            {
                '-' => ParameterOperator.UseDefault,
                '=' => ParameterOperator.AssignDefault,
                '?' => ParameterOperator.ErrorIfUnset,
                '+' => ParameterOperator.UseAlternate,
                _ => ParameterOperator.Substring,
            };
            argText = rest[2..];
        }
        else if (rest[0] is '-' or '=' or '?' or '+')
        {
            op = rest[0] switch
            {
                '-' => ParameterOperator.UseDefault,
                '=' => ParameterOperator.AssignDefault,
                '?' => ParameterOperator.ErrorIfUnset,
                _ => ParameterOperator.UseAlternate,
            };
            argText = rest[1..];
        }
        else if (rest[0] == '#')
        {
            var doubled = rest.Length > 1 && rest[1] == '#';
            op = doubled ? ParameterOperator.RemoveLargestPrefix : ParameterOperator.RemoveSmallestPrefix;
            argText = rest[(doubled ? 2 : 1)..];
        }
        else if (rest[0] == '%')
        {
            var doubled = rest.Length > 1 && rest[1] == '%';
            op = doubled ? ParameterOperator.RemoveLargestSuffix : ParameterOperator.RemoveSmallestSuffix;
            argText = rest[(doubled ? 2 : 1)..];
        }
        else if (rest[0] == '/')
        {
            var doubled = rest.Length > 1 && rest[1] == '/';
            op = doubled ? ParameterOperator.ReplaceAll : ParameterOperator.ReplaceFirst;
            argText = rest[(doubled ? 2 : 1)..];

            var split = FindUnescaped(argText, '/');
            var pattern = split < 0 ? argText : argText[..split];
            var replacement = split < 0 ? string.Empty : argText[(split + 1)..];

            return new ParameterPart
            {
                Name = name,
                Length = length,
                Quoted = quoted,
                Subscript = subscript,
                Indirect = indirect,
                Operator = op,
                Argument = SubWord(pattern),
                Replacement = SubWord(replacement),
            };
        }
        else if (rest[0] == '^')
        {
            allChars = rest.Length > 1 && rest[1] == '^';
            op = ParameterOperator.UpperCase;
            argText = rest[(allChars ? 2 : 1)..];
        }
        else if (rest[0] == ',')
        {
            allChars = rest.Length > 1 && rest[1] == ',';
            op = ParameterOperator.LowerCase;
            argText = rest[(allChars ? 2 : 1)..];
        }
        else
        {
            throw new ShellSyntaxException($"${{{inner}}}: bad substitution");
        }

        return new ParameterPart
        {
            Name = name,
            Length = length,
            Quoted = quoted,
            Subscript = subscript,
            Indirect = indirect,
            Operator = op,
            ColonForm = colon,
            AllCharacters = allChars,
            Argument = SubWord(argText),
        };
    }

    private static int FindUnescaped(string text, char target)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == target) return i;
        }

        return -1;
    }

    /// <summary>Lexes a fragment (a default value, a pattern, ...) as a single word.</summary>
    internal static Word SubWord(string text)
    {
        var lexer = new Lexer(text);
        return lexer.ReadWord(topLevel: false);
    }

    private string ReadAnsiCQuoted()
    {
        var sb = new StringBuilder();

        while (!Eof && Cur != '\'')
        {
            if (Cur != '\\') { sb.Append(Cur); _pos++; continue; }

            _pos++;
            if (Eof) { sb.Append('\\'); break; }
            var e = Cur;
            _pos++;

            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case 'e' or 'E': sb.Append('\x1b'); break;
                case '\\': sb.Append('\\'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7':
                {
                    var oct = e.ToString();
                    while (oct.Length < 3 && !Eof && Cur >= '0' && Cur <= '7') { oct += Cur; _pos++; }
                    sb.Append((char)Convert.ToInt32(oct, 8));
                    break;
                }
                case 'x':
                {
                    var hex = string.Empty;
                    while (hex.Length < 2 && !Eof && Uri.IsHexDigit(Cur)) { hex += Cur; _pos++; }
                    if (hex.Length > 0) sb.Append((char)Convert.ToInt32(hex, 16));
                    break;
                }
                case 'u' or 'U':
                {
                    var max = e == 'u' ? 4 : 8;
                    var hex = string.Empty;
                    while (hex.Length < max && !Eof && Uri.IsHexDigit(Cur)) { hex += Cur; _pos++; }
                    if (hex.Length > 0) sb.Append(char.ConvertFromUtf32(Convert.ToInt32(hex, 16)));
                    break;
                }
                default:
                    sb.Append('\\').Append(e);
                    break;
            }
        }

        if (!Eof) _pos++; // closing quote
        return sb.ToString();
    }

    private string ReadBackquoted()
    {
        var sb = new StringBuilder();

        while (!Eof && Cur != '`')
        {
            if (Cur == '\\' && At(1) is '`' or '\\' or '$')
            {
                sb.Append(At(1));
                _pos += 2;
                continue;
            }

            sb.Append(Cur);
            _pos++;
        }

        if (Eof) throw new ShellSyntaxException("unexpected EOF while looking for matching ``'");
        _pos++;
        return sb.ToString();
    }

    /// <summary>
    /// Reads up to and including the closing delimiter, tracking nesting and skipping quoted
    /// regions. The returned text includes the closing character so callers can verify closure.
    /// </summary>
    private string ReadBalanced(char open, char close, out bool closed, int initialDepth = 1)
    {
        var sb = new StringBuilder();
        var depth = initialDepth;

        while (!Eof)
        {
            var c = Cur;

            if (c == '\\' && _pos + 1 < _src.Length)
            {
                sb.Append(c).Append(At(1));
                _pos += 2;
                continue;
            }

            if (c == '\'')
            {
                sb.Append(c);
                _pos++;
                while (!Eof && Cur != '\'') { sb.Append(Cur); _pos++; }
                if (!Eof) { sb.Append('\''); _pos++; }
                continue;
            }

            if (c == '"')
            {
                sb.Append(c);
                _pos++;
                while (!Eof && Cur != '"')
                {
                    if (Cur == '\\' && _pos + 1 < _src.Length) { sb.Append(Cur).Append(At(1)); _pos += 2; continue; }
                    sb.Append(Cur);
                    _pos++;
                }

                if (!Eof) { sb.Append('"'); _pos++; }
                continue;
            }

            if (c == open) depth++;
            else if (c == close) depth--;

            sb.Append(c);
            _pos++;

            if (depth == 0) { closed = true; return sb.ToString(); }
        }

        closed = false;
        return sb.ToString();
    }

    // ------------------------------------------------------------ heredocs

    private void ReadPendingHereDocBodies()
    {
        if (_pendingHereDocs.Count == 0) return;

        foreach (var token in _pendingHereDocs)
        {
            var hd = token.HereDoc!;
            var body = new StringBuilder();

            while (true)
            {
                if (Eof) break;

                var lineEnd = _src.IndexOf('\n', _pos);
                var line = lineEnd < 0 ? _src[_pos..] : _src[_pos..lineEnd];
                var raw = line.TrimEnd('\r');
                var compare = hd.StripTabs ? raw.TrimStart('\t') : raw;

                _pos = lineEnd < 0 ? _src.Length : lineEnd + 1;

                if (compare == hd.Delimiter) { hd.Terminated = true; break; }

                body.Append(hd.StripTabs ? raw.TrimStart('\t') : raw).Append('\n');

                if (lineEnd < 0) break;
            }

            hd.Body = body.ToString();
        }

        _pendingHereDocs.Clear();
    }

    private void FinishHereDocs()
    {
        _hereDocAwaitingDelimiter = null;
        ReadPendingHereDocBodies();
    }
}
