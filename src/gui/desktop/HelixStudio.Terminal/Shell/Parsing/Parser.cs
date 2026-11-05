using HelixPythonNamespace.Terminal.Shell.Lexing;

namespace HelixPythonNamespace.Terminal.Shell.Parsing;

/// <summary>Recursive-descent parser over the token stream produced by <see cref="Lexer"/>.</summary>
public sealed class Parser
{
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "if", "then", "elif", "else", "fi",
        "for", "while", "until", "do", "done",
        "case", "esac", "in",
        "function", "select", "time",
        "{", "}", "!",
    };

    /// <summary>Words that close an enclosing construct, so a nested list must stop before them.</summary>
    private static readonly HashSet<string> ListTerminators = new(StringComparer.Ordinal)
    {
        "then", "elif", "else", "fi", "do", "done", "esac", "}",
    };

    private static readonly HashSet<string> UnaryConditionalOperators = new(StringComparer.Ordinal)
    {
        "-e", "-f", "-d", "-s", "-r", "-w", "-x", "-h", "-L", "-b", "-c", "-p", "-S",
        "-t", "-z", "-n", "-o", "-v", "-G", "-O", "-N", "-a", "-g", "-k", "-u",
    };

    private static readonly HashSet<string> BinaryConditionalOperators = new(StringComparer.Ordinal)
    {
        "=", "==", "!=", "=~", "<", ">",
        "-eq", "-ne", "-lt", "-le", "-gt", "-ge",
        "-nt", "-ot", "-ef",
    };

    private readonly List<Token> _tokens;
    private int _i;

    public Parser(List<Token> tokens) => _tokens = tokens;

    public Parser(string source) : this(Lexer.Tokenize(source)) { }

    public static CompoundList ParseProgram(string source) => new Parser(source).ParseInput();

    private Token Cur => _tokens[_i];

    private Token Peek(int offset = 1) => _tokens[Math.Min(_i + offset, _tokens.Count - 1)];

    private bool AtEnd => Cur.Type == TokenType.EndOfInput;

    private Token Take()
    {
        var t = Cur;
        if (_i < _tokens.Count - 1) _i++;
        return t;
    }

    private bool Accept(TokenType type)
    {
        if (Cur.Type != type) return false;
        Take();
        return true;
    }

    private Token Expect(TokenType type, string what)
    {
        if (Cur.Type != type) throw Error($"expected {what}");
        return Take();
    }

    private ShellSyntaxException Error(string message) =>
        new($"syntax error near `{Describe(Cur)}': {message}");

    private static string Describe(Token t) => t.Type switch
    {
        TokenType.EndOfInput => "end of input",
        TokenType.Newline => "newline",
        TokenType.Word => t.Word?.LiteralText is { Length: > 0 } s ? s : t.Text,
        _ => t.Text,
    };

    private static bool IsReserved(Token t, string word) =>
        t.Type == TokenType.Word && t.Word is { } w && w.TryGetPlainLiteral(out var text) && text == word;

    private static bool IsReservedAny(Token t) =>
        t.Type == TokenType.Word && t.Word is { } w && w.TryGetPlainLiteral(out var text) && ReservedWords.Contains(text);

    private bool AtListTerminator() =>
        Cur.Type == TokenType.Word && Cur.Word is { } w && w.TryGetPlainLiteral(out var text) && ListTerminators.Contains(text);

    private void SkipNewlines()
    {
        while (Cur.Type == TokenType.Newline) Take();
    }

    private void SkipNewlinesAndSeparators()
    {
        while (Cur.Type is TokenType.Newline or TokenType.Semicolon) Take();
    }

    // ------------------------------------------------------------- program

    public CompoundList ParseInput()
    {
        var list = ParseCompoundList();
        if (!AtEnd) throw Error("unexpected token");
        return list;
    }

    private CompoundList ParseCompoundList()
    {
        var list = new CompoundList();
        SkipNewlinesAndSeparators();

        while (!AtEnd && Cur.Type != TokenType.RightParen && !AtListTerminator() && Cur.Type != TokenType.DoubleSemi)
        {
            var andOr = ParseAndOr();
            list.Items.Add(andOr);

            if (Cur.Type == TokenType.Ampersand)
            {
                andOr.Background = true;
                Take();
            }
            else if (Cur.Type == TokenType.Semicolon)
            {
                // `;&` and `;;&` belong to `case`; leave them for the case parser.
                if (Peek().Type == TokenType.Ampersand && Peek().AdjacentToPrevious) break;
                Take();
            }
            else if (Cur.Type == TokenType.Newline)
            {
                Take();
            }
            else
            {
                break;
            }

            SkipNewlinesAndSeparators();
        }

        return list;
    }

    private AndOrList ParseAndOr()
    {
        var list = new AndOrList { First = ParsePipeline() };

        while (Cur.Type is TokenType.AndIf or TokenType.OrIf)
        {
            var op = Cur.Type == TokenType.AndIf ? AndOrOperator.And : AndOrOperator.Or;
            Take();
            SkipNewlines();
            list.Rest.Add((op, ParsePipeline()));
        }

        return list;
    }

    private Pipeline ParsePipeline()
    {
        var pipeline = new Pipeline();

        while (true)
        {
            if (IsReserved(Cur, "time")) { pipeline.Timed = true; Take(); SkipNewlines(); continue; }
            if (IsReserved(Cur, "!")) { pipeline.Negated = !pipeline.Negated; Take(); SkipNewlines(); continue; }
            break;
        }

        var element = new PipelineElement { Command = ParseCommand() };
        pipeline.Elements.Add(element);

        while (Cur.Type is TokenType.Pipe or TokenType.PipeAmp)
        {
            element.PipeStandardError = Cur.Type == TokenType.PipeAmp;
            Take();
            SkipNewlines();
            element = new PipelineElement { Command = ParseCommand() };
            pipeline.Elements.Add(element);
        }

        return pipeline;
    }

    // ------------------------------------------------------------ commands

    private Command ParseCommand()
    {
        if (Cur.Type == TokenType.LeftParen) return FinishCompound(ParseSubshell());
        if (Cur.Type == TokenType.ArithmeticCommand) return FinishCompound(new ArithmeticCommand { Expression = Take().Text });
        if (Cur.Type == TokenType.ConditionalExpression) return FinishCompound(ParseConditionalCommand());

        if (Cur.Type == TokenType.Word && Cur.Word is { } w && w.TryGetPlainLiteral(out var keyword))
        {
            switch (keyword)
            {
                case "{": return FinishCompound(ParseGroup());
                case "if": return FinishCompound(ParseIf());
                case "while": return FinishCompound(ParseWhile(until: false));
                case "until": return FinishCompound(ParseWhile(until: true));
                case "for": return FinishCompound(ParseFor());
                case "case": return FinishCompound(ParseCase());
                case "function": return ParseFunctionDefinition(withKeyword: true);
            }

            // NAME() { ... }
            if (Peek().Type == TokenType.LeftParen && Peek(2).Type == TokenType.RightParen && IsValidFunctionName(keyword))
                return ParseFunctionDefinition(withKeyword: false);
        }

        return ParseSimpleCommand();
    }

    private static bool IsValidFunctionName(string name) =>
        name.Length > 0 && !ReservedWords.Contains(name) &&
        name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '+');

    private Command FinishCompound(Command command)
    {
        while (TryParseRedirection(out var redirection)) command.Redirections.Add(redirection!);
        return command;
    }

    private Command ParseSubshell()
    {
        Expect(TokenType.LeftParen, "`('");
        var body = ParseCompoundList();
        Expect(TokenType.RightParen, "`)'");
        return new SubshellCommand { Body = body };
    }

    private Command ParseGroup()
    {
        Take(); // {
        var body = ParseCompoundList();
        if (!IsReserved(Cur, "}")) throw Error("expected `}'");
        Take();
        return new GroupCommand { Body = body };
    }

    private Command ParseIf()
    {
        Take(); // if
        var command = new IfCommand();

        while (true)
        {
            var condition = ParseCompoundList();
            if (!IsReserved(Cur, "then")) throw Error("expected `then'");
            Take();
            var body = ParseCompoundList();
            command.Branches.Add((condition, body));

            if (IsReserved(Cur, "elif")) { Take(); continue; }
            break;
        }

        if (IsReserved(Cur, "else"))
        {
            Take();
            command.ElseBody = ParseCompoundList();
        }

        if (!IsReserved(Cur, "fi")) throw Error("expected `fi'");
        Take();
        return command;
    }

    private Command ParseWhile(bool until)
    {
        Take(); // while / until
        var condition = ParseCompoundList();
        var body = ParseDoGroup();
        return new WhileCommand { Condition = condition, Body = body, Until = until };
    }

    private CompoundList ParseDoGroup()
    {
        SkipNewlines();
        if (!IsReserved(Cur, "do")) throw Error("expected `do'");
        Take();
        var body = ParseCompoundList();
        if (!IsReserved(Cur, "done")) throw Error("expected `done'");
        Take();
        return body;
    }

    private Command ParseFor()
    {
        Take(); // for

        if (Cur.Type == TokenType.ArithmeticCommand)
        {
            var parts = SplitArithmeticForHeader(Take().Text);
            SkipNewlines();
            if (Cur.Type == TokenType.Semicolon) Take();
            var arithBody = ParseDoGroup();
            return new ForArithmeticCommand
            {
                Initialiser = parts[0],
                Condition = parts[1],
                Update = parts[2],
                Body = arithBody,
            };
        }

        if (Cur.Type != TokenType.Word || Cur.Word is null) throw Error("expected a variable name after `for'");
        var variable = Cur.Word.LiteralText;
        if (!IsValidFunctionName(variable)) throw Error("bad loop variable name");
        Take();

        List<Word>? items = null;

        SkipNewlines();
        if (IsReserved(Cur, "in"))
        {
            Take();
            items = new List<Word>();
            while (Cur.Type == TokenType.Word && !AtListTerminator())
            {
                items.Add(Cur.Word!);
                Take();
            }

            if (Cur.Type is TokenType.Semicolon or TokenType.Newline) Take();
        }
        else if (Cur.Type is TokenType.Semicolon)
        {
            Take();
        }

        var body = ParseDoGroup();
        return new ForCommand { Variable = variable, Items = items, Body = body };
    }

    /// <summary>Splits <c>init; cond; update</c>, tolerating empty sections.</summary>
    private static string[] SplitArithmeticForHeader(string header)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < header.Length; i++)
        {
            var c = header[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ';' && depth == 0)
            {
                parts.Add(header[start..i]);
                start = i + 1;
            }
        }

        parts.Add(header[start..]);
        while (parts.Count < 3) parts.Add(string.Empty);
        return parts.Take(3).Select(p => p.Trim()).ToArray();
    }

    private Command ParseCase()
    {
        Take(); // case
        if (Cur.Type != TokenType.Word) throw Error("expected a word after `case'");
        var subject = Cur.Word!;
        Take();

        SkipNewlines();
        if (!IsReserved(Cur, "in")) throw Error("expected `in'");
        Take();
        SkipNewlines();

        var command = new CaseCommand { Subject = subject };

        while (!IsReserved(Cur, "esac"))
        {
            if (AtEnd) throw Error("expected `esac'");

            Accept(TokenType.LeftParen); // optional leading '('

            var patterns = new List<Word>();
            while (true)
            {
                if (Cur.Type != TokenType.Word) throw Error("expected a case pattern");
                patterns.Add(Cur.Word!);
                Take();
                if (Cur.Type != TokenType.Pipe) break;
                Take();
            }

            Expect(TokenType.RightParen, "`)'");

            var body = ParseCompoundList();
            var clause = new CaseClause { Body = body };
            clause.Patterns.AddRange(patterns);

            if (Cur.Type == TokenType.DoubleSemi)
            {
                Take();
                if (Cur.Type == TokenType.Ampersand && Cur.AdjacentToPrevious)
                {
                    Take();
                    clause.Terminator = CaseTerminator.ContinueTesting;
                }
            }
            else if (Cur.Type == TokenType.Semicolon && Peek().Type == TokenType.Ampersand && Peek().AdjacentToPrevious)
            {
                Take();
                Take();
                clause.Terminator = CaseTerminator.FallThrough;
            }

            command.Clauses.Add(clause);
            SkipNewlinesAndSeparators();
        }

        Take(); // esac
        return command;
    }

    private Command ParseFunctionDefinition(bool withKeyword)
    {
        if (withKeyword) Take(); // function

        if (Cur.Type != TokenType.Word) throw Error("expected a function name");
        var name = Cur.Word!.LiteralText;
        if (!IsValidFunctionName(name)) throw Error("bad function name");
        Take();

        if (Cur.Type == TokenType.LeftParen)
        {
            Take();
            Expect(TokenType.RightParen, "`)'");
        }

        SkipNewlines();
        var body = ParseCommand();
        return new FunctionDefinition { Name = name, Body = body };
    }

    private Command ParseSimpleCommand()
    {
        var command = new SimpleCommand();
        var acceptingAssignments = true;

        while (true)
        {
            if (TryParseRedirection(out var redirection))
            {
                command.Redirections.Add(redirection!);
                continue;
            }

            if (Cur.Type != TokenType.Word) break;
            if (command.Words.Count == 0 && command.Assignments.Count == 0 && IsReservedAny(Cur) && !IsReserved(Cur, "!"))
                break;

            var word = Cur.Word!;

            if (acceptingAssignments && command.Words.Count == 0 && TryParseAssignment(word, out var assignment))
            {
                Take();

                // NAME=(a b c): the '(' is a separate token because it is a metacharacter.
                if (assignment!.Value is null && Cur.Type == TokenType.LeftParen && Cur.AdjacentToPrevious)
                {
                    Take();
                    var items = new List<Word>();
                    SkipNewlines();
                    while (Cur.Type == TokenType.Word)
                    {
                        items.Add(Cur.Word!);
                        Take();
                        SkipNewlines();
                    }

                    Expect(TokenType.RightParen, "`)'");
                    assignment = new Assignment
                    {
                        Name = assignment.Name,
                        Append = assignment.Append,
                        Subscript = assignment.Subscript,
                        ArrayItems = items,
                    };
                }

                command.Assignments.Add(assignment);
                continue;
            }

            acceptingAssignments = false;
            command.Words.Add(word);
            Take();
        }

        if (command.Words.Count == 0 && command.Assignments.Count == 0 && command.Redirections.Count == 0)
            throw Error("expected a command");

        return command;
    }

    /// <summary>Recognises <c>NAME=value</c>, <c>NAME+=value</c> and <c>NAME[i]=value</c> prefixes.</summary>
    internal static bool TryParseAssignment(Word word, out Assignment? assignment)
    {
        assignment = null;
        if (word.Parts.Count == 0) return false;
        if (word.Parts[0] is not LiteralPart { Quoted: false } head) return false;

        var eq = head.Text.IndexOf('=');
        if (eq <= 0) return false;

        var name = head.Text[..eq];
        var append = false;

        if (name.EndsWith('+'))
        {
            append = true;
            name = name[..^1];
            if (name.Length == 0) return false;
        }

        string? subscript = null;
        if (name.EndsWith(']'))
        {
            var open = name.IndexOf('[');
            if (open <= 0) return false;
            subscript = name[(open + 1)..^1];
            name = name[..open];
        }

        if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
        if (!name.All(c => char.IsLetterOrDigit(c) || c == '_')) return false;

        var rest = new List<WordPart>();
        var tail = head.Text[(eq + 1)..];
        if (tail.Length > 0) rest.Add(new LiteralPart(tail, false));
        rest.AddRange(word.Parts.Skip(1));

        assignment = new Assignment
        {
            Name = name,
            Append = append,
            Subscript = subscript,
            Value = rest.Count == 0 ? null : new Word(rest),
        };
        return true;
    }

    // -------------------------------------------------------- redirections

    private bool TryParseRedirection(out Redirection? redirection)
    {
        redirection = null;

        int? fd = null;
        var start = _i;

        if (Cur.Type == TokenType.IoNumber)
        {
            if (!int.TryParse(Cur.Text, out var parsed)) return false;
            fd = parsed;
            Take();
        }

        var type = Cur.Type switch
        {
            TokenType.Less => RedirectionType.Input,
            TokenType.Great => RedirectionType.Output,
            TokenType.DGreat => RedirectionType.Append,
            TokenType.Clobber => RedirectionType.Clobber,
            TokenType.LessGreat => RedirectionType.ReadWrite,
            TokenType.LessAnd => RedirectionType.DuplicateInput,
            TokenType.GreatAnd => RedirectionType.DuplicateOutput,
            TokenType.DLess or TokenType.DLessDash => RedirectionType.HereDocument,
            TokenType.TripleLess => RedirectionType.HereString,
            TokenType.AmpGreat => RedirectionType.OutputAndError,
            TokenType.AmpDGreat => RedirectionType.AppendOutputAndError,
            _ => (RedirectionType?)null,
        } ?? default;

        if (Cur.Type is not (TokenType.Less or TokenType.Great or TokenType.DGreat or TokenType.Clobber
            or TokenType.LessGreat or TokenType.LessAnd or TokenType.GreatAnd or TokenType.DLess
            or TokenType.DLessDash or TokenType.TripleLess or TokenType.AmpGreat or TokenType.AmpDGreat))
        {
            _i = start;
            return false;
        }

        var operatorToken = Take();

        if (type == RedirectionType.HereDocument)
        {
            if (Cur.Type == TokenType.Word) Take(); // the delimiter, already consumed by the lexer
            redirection = new Redirection
            {
                FileDescriptor = fd,
                Type = RedirectionType.HereDocument,
                HereDocument = operatorToken.HereDoc,
            };
            return true;
        }

        if (Cur.Type != TokenType.Word) throw Error("expected a filename after a redirection operator");
        var target = Take().Word!;

        if (type is RedirectionType.DuplicateInput or RedirectionType.DuplicateOutput)
        {
            var text = target.LiteralText;

            if (text == "-")
            {
                redirection = new Redirection { FileDescriptor = fd, Type = RedirectionType.CloseDescriptor };
                return true;
            }

            if (text.Length > 0 && text.All(char.IsAsciiDigit))
            {
                redirection = new Redirection
                {
                    FileDescriptor = fd,
                    Type = type,
                    TargetDescriptor = int.Parse(text),
                };
                return true;
            }

            // `>&file` (no descriptor number) sends both stdout and stderr to the file.
            if (type == RedirectionType.DuplicateOutput && fd is null)
            {
                redirection = new Redirection { Type = RedirectionType.OutputAndError, Target = target };
                return true;
            }

            throw Error("expected a file descriptor number");
        }

        redirection = new Redirection { FileDescriptor = fd, Type = type, Target = target };
        return true;
    }

    // ------------------------------------------------- [[ ]] conditionals

    private Command ParseConditionalCommand()
    {
        var text = Take().Text;
        var tokens = Lexer.Tokenize(text);
        var inner = new Parser(tokens);
        var expression = inner.ParseConditionalOr();
        if (!inner.AtEnd) throw inner.Error("unexpected token in conditional expression");
        return new ConditionalCommand { Expression = expression };
    }

    private ConditionalNode ParseConditionalOr()
    {
        var left = ParseConditionalAnd();

        while (Cur.Type == TokenType.OrIf)
        {
            Take();
            SkipNewlines();
            left = new ConditionalAndOr { IsAnd = false, Left = left, Right = ParseConditionalAnd() };
        }

        return left;
    }

    private ConditionalNode ParseConditionalAnd()
    {
        var left = ParseConditionalPrimary();

        while (Cur.Type == TokenType.AndIf)
        {
            Take();
            SkipNewlines();
            left = new ConditionalAndOr { IsAnd = true, Left = left, Right = ParseConditionalPrimary() };
        }

        return left;
    }

    private ConditionalNode ParseConditionalPrimary()
    {
        SkipNewlines();

        if (IsReserved(Cur, "!"))
        {
            Take();
            return new ConditionalNot { Operand = ParseConditionalPrimary() };
        }

        if (Cur.Type == TokenType.LeftParen)
        {
            Take();
            var inner = ParseConditionalOr();
            Expect(TokenType.RightParen, "`)'");
            return inner;
        }

        if (Cur.Type != TokenType.Word) throw Error("expected an operand in conditional expression");

        var first = Cur.Word!;
        var firstText = first.LiteralText;

        if (UnaryConditionalOperators.Contains(firstText) && Peek().Type == TokenType.Word)
        {
            Take();
            return new ConditionalUnary { Operator = firstText, Operand = Take().Word! };
        }

        Take();

        var opText = Cur.Type switch
        {
            TokenType.Less => "<",
            TokenType.Great => ">",
            TokenType.Word => Cur.Word!.LiteralText,
            _ => null,
        };

        if (opText is not null && BinaryConditionalOperators.Contains(opText))
        {
            Take();
            if (Cur.Type != TokenType.Word) throw Error("expected the right operand of a conditional operator");
            var right = Take().Word!;
            return new ConditionalBinary
            {
                Operator = opText,
                Left = first,
                Right = right,
                RightIsLiteral = right.Parts.Any(p => p.Quoted),
            };
        }

        return new ConditionalWord { Word = first };
    }
}
