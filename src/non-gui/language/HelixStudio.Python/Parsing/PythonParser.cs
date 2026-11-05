using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Parsing
{
    /// <summary>
    /// Hand-rolled recursive-descent parser targeting the Python 3.10 PEG
    /// grammar (see <c>Python 3.10 Language Specification/chapter_10_full_grammar_specification.txt</c>).
    /// Split across multiple partial files:
    /// <list type="bullet">
    /// <item><b>PythonParser.cs</b> — façade, module parse, class/def, imports, decorators, parameters, utilities.</item>
    /// <item><b>PythonParser.Statements.cs</b> — if / while / for / try / with / match / simple statements / assignment.</item>
    /// <item><b>PythonParser.Expressions.cs</b> — full precedence ladder, lambda, walrus, comprehensions, f-strings.</item>
    /// <item><b>PythonParser.Patterns.cs</b> — match/case pattern grammar.</item>
    /// </list>
    /// </summary>
    public sealed partial class PythonParser
    {
        private readonly string _fileName;
        private readonly IReadOnlyList<Token> _raw;
        private readonly List<Token> _stream;
        private readonly List<ParseDiagnostic> _diagnostics = new List<ParseDiagnostic>();
        private int _pos;

        // Statement-context tracking for CPython-style contextual syntax errors
        // ('return' outside function, 'break' outside loop, 'await' outside async
        // function, nonlocal at module level). Function and class bodies open new
        // scopes, so each ParseFunctionDef/ParseClassDef saves and restores these.
        private int _functionDepth;
        private int _loopDepth;
        private bool _inAsyncFunction;
        // Unlike _functionDepth, this is NOT reset by a class body: `nonlocal` IS legal
        // in a class body nested inside a function (Lib/test/test_scope.py testNonLocalClass),
        // while `return`/`yield` there are not.
        private int _enclosingFunctionDepth;
        // Inside a comprehension or generator expression an `await` is legal even when the
        // immediately enclosing def is not async — it makes the comprehension asynchronous
        // (Lib/test/test_asyncgen.py, legal since 3.7).
        private int _comprehensionDepth;

        public PythonParser(IReadOnlyList<Token> tokens, string fileName = null)
        {
            if (tokens == null) throw new ArgumentNullException(nameof(tokens));
            _raw = tokens;
            _fileName = fileName ?? string.Empty;
            _stream = new List<Token>(tokens.Count);
            foreach (Token t in tokens)
            {
                if (t.Kind == TokenKind.Whitespace || t.Kind == TokenKind.LineContinuation || t.Kind == TokenKind.Comment) continue;
                _stream.Add(t);
            }
        }

        public IReadOnlyList<ParseDiagnostic> Diagnostics => _diagnostics;

        public ModuleSyntax ParseModule(
            System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            SourcePosition start = _stream.Count > 0 ? _stream[0].Span.Start : SourcePosition.Zero;
            List<StatementSyntax> statements = new List<StatementSyntax>();
            SkipNewlines();
            while (Current.Kind != TokenKind.EndOfFile)
            {
                // Cooperative cancellation between top-level statements keeps Cancel responsive
                // even on a single very large file (the per-file compile loop only checks between
                // files, so without this a giant module would run to completion after Cancel).
                cancel.ThrowIfCancellationRequested();
                StatementSyntax stmt = ParseStatement();
                if (stmt != null) statements.Add(stmt);
                SkipNewlines();
            }
            SourcePosition end = _stream.Count > 0 ? _stream[_stream.Count - 1].Span.End : start;
            return new ModuleSyntax(_fileName, new SourceSpan(start, end), statements, Array.Empty<Token>(), Array.Empty<Token>());
        }

        // ---------- Statement dispatcher ----------

        private StatementSyntax ParseStatement()
        {
            if (Current.Kind == TokenKind.At)
            {
                List<DecoratorSyntax> decorators = ParseDecorators();
                return ParseDecoratedDefinition(decorators);
            }

            switch (Current.Kind)
            {
                case TokenKind.KwClass:
                    return ParseClassDef(Array.Empty<DecoratorSyntax>());
                case TokenKind.KwDef:
                    return ParseFunctionDef(Array.Empty<DecoratorSyntax>(), isAsync: false);
                case TokenKind.KwAsync:
                    return ParseAsyncStatement();
                case TokenKind.KwImport:
                    return ParseImport();
                case TokenKind.KwFrom:
                    return ParseFromImport();
                case TokenKind.KwIf:
                    return ParseIfStatement();
                case TokenKind.KwWhile:
                    return ParseWhileStatement();
                case TokenKind.KwFor:
                    return ParseForStatement(isAsync: false);
                case TokenKind.KwTry:
                    return ParseTryStatement();
                case TokenKind.KwWith:
                    return ParseWithStatement(isAsync: false);
                case TokenKind.KwReturn:
                    return ParseReturnStatement();
                case TokenKind.KwRaise:
                    return ParseRaiseStatement();
                case TokenKind.KwAssert:
                    return ParseAssertStatement();
                case TokenKind.KwDel:
                    return ParseDelStatement();
                case TokenKind.KwGlobal:
                    return ParseGlobalStatement();
                case TokenKind.KwNonlocal:
                    return ParseNonlocalStatement();
                case TokenKind.KwYield:
                    return ParseYieldStatement();
                case TokenKind.KwBreak:
                    {
                        SourcePosition s = Current.Span.Start;
                        SourcePosition e = Current.Span.End;
                        if (_loopDepth == 0)
                            Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                                "'break' outside loop.");
                        Advance();
                        ConsumeStatementEnd();
                        return new BreakStatement(new SourceSpan(s, e));
                    }
                case TokenKind.KwContinue:
                    {
                        SourcePosition s = Current.Span.Start;
                        SourcePosition e = Current.Span.End;
                        if (_loopDepth == 0)
                            Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                                "'continue' not properly in loop.");
                        Advance();
                        ConsumeStatementEnd();
                        return new ContinueStatement(new SourceSpan(s, e));
                    }
                case TokenKind.KwPass:
                    {
                        SourcePosition s = Current.Span.Start;
                        SourcePosition e = Current.Span.End;
                        Advance();
                        ConsumeStatementEnd();
                        return new PassStatement(new SourceSpan(s, e));
                    }
            }

            // "match" and "case" are soft keywords — lexed as Name.
            if (Current.Kind == TokenKind.Name && Current.Text == "match" && IsMatchStart())
            {
                return ParseMatchStatement();
            }

            // PEP 695 type-alias statement: `type Alias[T] = value`. "type" is a soft
            // keyword, so it only applies when a Name follows and then '[' or '='.
            if (Current.Kind == TokenKind.Name && Current.Text == "type"
                && Peek(1).Kind == TokenKind.Name
                && (Peek(2).Kind == TokenKind.Assign || Peek(2).Kind == TokenKind.LeftBracket))
            {
                return ParseTypeAliasStatement();
            }

            // Fallback: expression statement / assignment.
            return ParseExpressionOrAssignmentStatement();
        }

        private StatementSyntax ParseAsyncStatement()
        {
            SourcePosition start = Current.Span.Start;
            Advance(); // consume 'async'
            if (Current.Kind == TokenKind.KwDef)
                return ParseFunctionDef(Array.Empty<DecoratorSyntax>(), isAsync: true);
            if (Current.Kind == TokenKind.KwFor)
                return ParseForStatement(isAsync: true);
            if (Current.Kind == TokenKind.KwWith)
                return ParseWithStatement(isAsync: true);
            Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                "'async' must be followed by 'def', 'for', or 'with'.");
            return ParseExpressionOrAssignmentStatement();
        }

        private StatementSyntax ParseDecoratedDefinition(List<DecoratorSyntax> decorators)
        {
            if (Current.Kind == TokenKind.KwClass) return ParseClassDef(decorators);
            if (Current.Kind == TokenKind.KwDef) return ParseFunctionDef(decorators, isAsync: false);
            if (Current.Kind == TokenKind.KwAsync && Peek(1).Kind == TokenKind.KwDef)
            {
                Advance();
                return ParseFunctionDef(decorators, isAsync: true);
            }
            Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                "Decorator must precede 'class', 'def', or 'async def'.");
            return ParseExpressionOrAssignmentStatement();
        }

        private List<DecoratorSyntax> ParseDecorators()
        {
            List<DecoratorSyntax> decorators = new List<DecoratorSyntax>();
            while (Current.Kind == TokenKind.At)
            {
                SourcePosition start = Current.Span.Start;
                Advance();
                ExpressionSyntax expr = ParseSingleExpression();
                SourcePosition end = Previous.Span.End;
                SkipNewlines();
                decorators.Add(new DecoratorSyntax(expr, new SourceSpan(start, end)));
            }
            return decorators;
        }

        // ---------- Class / function / imports / parameters / type params ----------

        private ClassDefSyntax ParseClassDef(IReadOnlyList<DecoratorSyntax> decorators)
        {
            SourcePosition start = decorators.Count > 0 ? decorators[0].Span.Start : Current.Span.Start;
            Expect(TokenKind.KwClass);
            string name = ExpectName();
            List<TypeParameterSyntax> typeParams = null;
            if (Current.Kind == TokenKind.LeftBracket) typeParams = ParseTypeParameters();
            List<ExpressionSyntax> bases = new List<ExpressionSyntax>();
            List<KeyValuePair<string, ExpressionSyntax>> kwargs = new List<KeyValuePair<string, ExpressionSyntax>>();
            if (Current.Kind == TokenKind.LeftParen)
            {
                Advance();
                while (Current.Kind != TokenKind.RightParen && Current.Kind != TokenKind.EndOfFile)
                {
                    if (Current.Kind == TokenKind.Name && Peek(1).Kind == TokenKind.Assign)
                    {
                        string key = Current.Text; Advance(); Advance();
                        ExpressionSyntax value = ParseSingleExpression();
                        kwargs.Add(new KeyValuePair<string, ExpressionSyntax>(key, value));
                    }
                    else if (Current.Kind == TokenKind.DoubleStar || Current.Kind == TokenKind.Star)
                    {
                        // `class C(Base, **kwargs)` / `class C(*bases)` — dynamic bases and
                        // class keywords are ordinary argument syntax in a class header.
                        bool isDouble = Current.Kind == TokenKind.DoubleStar;
                        SourcePosition starStart = Current.Span.Start;
                        Advance();
                        ExpressionSyntax unpacked = ParseSingleExpression();
                        StarredExpression starred = new StarredExpression(unpacked, isDouble,
                            new SourceSpan(starStart, Previous.Span.End));
                        if (isDouble) kwargs.Add(new KeyValuePair<string, ExpressionSyntax>(null, starred));
                        else bases.Add(starred);
                    }
                    else
                    {
                        ExpressionSyntax e = ParseSingleExpression();
                        if (e != null) bases.Add(e);
                    }
                    if (Current.Kind == TokenKind.Comma) Advance();
                    else break;
                }
                Expect(TokenKind.RightParen);
            }
            Expect(TokenKind.Colon);
            // A class body is a new scope: return/yield/break/continue/await from an
            // enclosing function or loop are NOT legal directly inside it (CPython
            // reports 'return' outside function / 'break' outside loop there too).
            int savedFunctionDepth = _functionDepth;
            int savedLoopDepth = _loopDepth;
            bool savedInAsync = _inAsyncFunction;
            _functionDepth = 0;
            _loopDepth = 0;
            _inAsyncFunction = false;
            SuiteSyntax body = ParseSuite();
            _functionDepth = savedFunctionDepth;
            _loopDepth = savedLoopDepth;
            _inAsyncFunction = savedInAsync;
            SourcePosition end = body != null ? body.Span.End : Previous.Span.End;
            return new ClassDefSyntax(name, decorators, typeParams, bases, kwargs, body, new SourceSpan(start, end));
        }

        private FunctionDefSyntax ParseFunctionDef(IReadOnlyList<DecoratorSyntax> decorators, bool isAsync)
        {
            SourcePosition start = decorators.Count > 0 ? decorators[0].Span.Start : Current.Span.Start;
            Expect(TokenKind.KwDef);
            string name = ExpectName();
            List<TypeParameterSyntax> typeParams = null;
            if (Current.Kind == TokenKind.LeftBracket) typeParams = ParseTypeParameters();
            Expect(TokenKind.LeftParen);
            List<ParameterSyntax> parameters = ParseParameterList();
            Expect(TokenKind.RightParen);
            ExpressionSyntax returnAnn = null;
            if (Current.Kind == TokenKind.Arrow)
            {
                Advance();
                returnAnn = ParseSingleExpression();
            }
            Expect(TokenKind.Colon);
            int savedLoopDepth = _loopDepth;
            bool savedInAsync = _inAsyncFunction;
            _loopDepth = 0;
            _inAsyncFunction = isAsync;
            _functionDepth++;
            _enclosingFunctionDepth++;
            SuiteSyntax body = ParseSuite();
            _enclosingFunctionDepth--;
            _functionDepth--;
            _inAsyncFunction = savedInAsync;
            _loopDepth = savedLoopDepth;
            SourcePosition end = body != null ? body.Span.End : Previous.Span.End;
            return new FunctionDefSyntax(name, isAsync, decorators, typeParams, parameters, returnAnn, body, new SourceSpan(start, end));
        }

        // A parameter annotation may itself be an unpacking under PEP 646:
        // `def f(*args: *Ts)`, `def f(*args: *tuple[int, str])`.
        private ExpressionSyntax ParseAnnotation()
        {
            if (Current.Kind == TokenKind.Star)
            {
                SourcePosition s = Current.Span.Start;
                Advance();
                ExpressionSyntax inner = ParseSingleExpression();
                return new StarredExpression(inner, isDouble: false, new SourceSpan(s, Previous.Span.End));
            }
            return ParseSingleExpression();
        }

        // `type Alias[T] = value` — kept verbatim: no downstream engine models a type
        // alias, and UnparsedStatement lets the emitter round-trip the line unchanged.
        private StatementSyntax ParseTypeAliasStatement()
        {
            SourcePosition start = Current.Span.Start;
            List<Token> tokens = new List<Token>();
            tokens.Add(Current); Advance();          // 'type'
            tokens.Add(Current); Advance();          // alias name
            if (Current.Kind == TokenKind.LeftBracket) ParseTypeParameters();
            if (Current.Kind == TokenKind.Assign)
            {
                Advance();
                ParseStarExpressions();
            }
            else
            {
                Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                    "Type alias requires a value: expected '=' after the alias name.");
            }
            SourcePosition end = Previous.Span.End;
            ConsumeStatementEnd();
            return new UnparsedStatement(tokens, new SourceSpan(start, end));
        }

        private List<TypeParameterSyntax> ParseTypeParameters()
        {
            List<TypeParameterSyntax> list = new List<TypeParameterSyntax>();
            Expect(TokenKind.LeftBracket);
            while (Current.Kind != TokenKind.RightBracket && Current.Kind != TokenKind.EndOfFile)
            {
                SourcePosition start = Current.Span.Start;
                TypeParameterSyntax.TypeParamKind kind = TypeParameterSyntax.TypeParamKind.Type;
                if (Current.Kind == TokenKind.Star) { kind = TypeParameterSyntax.TypeParamKind.TypeVarTuple; Advance(); }
                else if (Current.Kind == TokenKind.DoubleStar) { kind = TypeParameterSyntax.TypeParamKind.ParamSpec; Advance(); }
                string n = ExpectName();
                ExpressionSyntax bound = null;
                if (Current.Kind == TokenKind.Colon) { Advance(); bound = ParseSingleExpression(); }
                // PEP 696 type-parameter default: `[T = int]`, `[**P = int]`, `[*Ts = int]`.
                if (Current.Kind == TokenKind.Assign) { Advance(); ParseSingleExpression(); }
                list.Add(new TypeParameterSyntax(n, kind, bound, new SourceSpan(start, Previous.Span.End)));
                if (Current.Kind == TokenKind.Comma) Advance();
                else break;
            }
            Expect(TokenKind.RightBracket);
            return list;
        }

        private List<ParameterSyntax> ParseParameterList()
        {
            List<ParameterSyntax> list = new List<ParameterSyntax>();
            HashSet<string> seenNames = new HashSet<string>(StringComparer.Ordinal);
            bool seenStar = false;
            while (Current.Kind != TokenKind.RightParen && Current.Kind != TokenKind.EndOfFile)
            {
                SourcePosition start = Current.Span.Start;
                if (Current.Kind == TokenKind.Slash)
                {
                    Advance();
                    for (int i = 0; i < list.Count; i++)
                    {
                        ParameterSyntax p = list[i];
                        list[i] = new ParameterSyntax(p.Name, p.Annotation, p.DefaultValue,
                            ParameterKind.PositionalOnly, p.Span);
                    }
                    if (Current.Kind == TokenKind.Comma) Advance();
                    continue;
                }
                if (Current.Kind == TokenKind.Star)
                {
                    if (seenStar)
                        Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                            "* argument may appear only once.");
                    Advance();
                    if (Current.Kind == TokenKind.Name)
                    {
                        Token nameTok = Current;
                        string n = Current.Text; Advance();
                        if (!seenNames.Add(n))
                            Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, nameTok.Span,
                                "Duplicate argument '" + n + "' in function definition.");
                        ExpressionSyntax ann = null;
                        if (Current.Kind == TokenKind.Colon) { Advance(); ann = ParseAnnotation(); }
                        list.Add(new ParameterSyntax(n, ann, null, ParameterKind.VarPositional,
                            new SourceSpan(start, Previous.Span.End)));
                    }
                    seenStar = true;
                    if (Current.Kind == TokenKind.Comma) Advance();
                    continue;
                }
                if (Current.Kind == TokenKind.DoubleStar)
                {
                    Advance();
                    Token kwNameTok = Current;
                    string n = ExpectName();
                    if (n != null && n.Length > 0 && !seenNames.Add(n))
                        Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, kwNameTok.Span,
                            "Duplicate argument '" + n + "' in function definition.");
                    ExpressionSyntax ann = null;
                    if (Current.Kind == TokenKind.Colon) { Advance(); ann = ParseSingleExpression(); }
                    list.Add(new ParameterSyntax(n, ann, null, ParameterKind.VarKeyword,
                        new SourceSpan(start, Previous.Span.End)));
                    if (Current.Kind == TokenKind.Comma) Advance();
                    continue;
                }
                if (Current.Kind != TokenKind.Name)
                {
                    Report(DiagnosticCodes.UnexpectedToken, ParseSeverity.Error, Current.Span,
                        "Expected parameter name, got " + Current.Kind + ".");
                    Advance();
                    continue;
                }
                Token plainNameTok = Current;
                string name = Current.Text; Advance();
                if (!seenNames.Add(name))
                    Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, plainNameTok.Span,
                        "Duplicate argument '" + name + "' in function definition.");
                ExpressionSyntax annotation = null;
                ExpressionSyntax defaultValue = null;
                if (Current.Kind == TokenKind.Colon) { Advance(); annotation = ParseSingleExpression(); }
                if (Current.Kind == TokenKind.Assign) { Advance(); defaultValue = ParseSingleExpression(); }
                ParameterKind pkind = seenStar ? ParameterKind.KeywordOnly : ParameterKind.Positional;
                list.Add(new ParameterSyntax(name, annotation, defaultValue, pkind,
                    new SourceSpan(start, Previous.Span.End)));
                if (Current.Kind == TokenKind.Comma) Advance();
                else break;
            }
            return list;
        }

        private SuiteSyntax ParseSuite()
        {
            SourcePosition start = Current.Span.Start;
            List<StatementSyntax> stmts = new List<StatementSyntax>();

            if (Current.Kind == TokenKind.NewLine)
            {
                SkipNewlines();
                if (Current.Kind != TokenKind.Indent)
                {
                    Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                        "Expected an indented block.");
                    return new SuiteSyntax(stmts, new SourceSpan(start, Current.Span.Start));
                }
                Advance();
                SkipNewlines();
                while (Current.Kind != TokenKind.Dedent && Current.Kind != TokenKind.EndOfFile)
                {
                    StatementSyntax s = ParseStatement();
                    if (s != null) stmts.Add(s);
                    SkipNewlines();
                }
                if (Current.Kind == TokenKind.Dedent) Advance();
            }
            else
            {
                // Inline suite: `if x: a = 1; b = 2`. Every semicolon-separated simple
                // statement on the line belongs to this suite. ConsumeStatementEnd stops on
                // the ';' when more follow and swallows the NEWLINE on the last one, so
                // Previous tells us whether the line continues.
                while (true)
                {
                    StatementSyntax s = ParseStatement();
                    if (s != null) stmts.Add(s);
                    if (Previous.Kind != TokenKind.Semicolon) break;
                    if (Current.Kind == TokenKind.NewLine || Current.Kind == TokenKind.EndOfFile
                        || Current.Kind == TokenKind.Dedent)
                    {
                        ConsumeStatementEnd();
                        break;
                    }
                }
            }
            SourcePosition end = stmts.Count > 0 ? stmts[stmts.Count - 1].Span.End : Previous.Span.End;
            return new SuiteSyntax(stmts, new SourceSpan(start, end));
        }

        private ImportStatement ParseImport()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwImport);
            List<ImportStatement.ImportedName> names = new List<ImportStatement.ImportedName>();
            while (true)
            {
                DottedName n = ParseDottedName();
                string alias = null;
                if (Current.Kind == TokenKind.KwAs) { Advance(); alias = ExpectName(); }
                names.Add(new ImportStatement.ImportedName(n, alias));
                if (Current.Kind == TokenKind.Comma) { Advance(); continue; }
                break;
            }
            SourcePosition end = Previous.Span.End;
            ConsumeStatementEnd();
            return new ImportStatement(names, new SourceSpan(start, end));
        }

        private FromImportStatement ParseFromImport()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwFrom);
            int dots = 0;
            while (Current.Kind == TokenKind.Dot || Current.Kind == TokenKind.Ellipsis)
            {
                dots += Current.Kind == TokenKind.Ellipsis ? 3 : 1;
                Advance();
            }
            DottedName module = null;
            if (Current.Kind == TokenKind.Name) module = ParseDottedName();
            Expect(TokenKind.KwImport);
            bool isStar = false;
            List<KeyValuePair<string, string>> names = new List<KeyValuePair<string, string>>();
            if (Current.Kind == TokenKind.Star) { isStar = true; Advance(); }
            else
            {
                bool paren = Current.Kind == TokenKind.LeftParen;
                if (paren) Advance();
                while (Current.Kind == TokenKind.Name)
                {
                    string n = Current.Text; Advance();
                    string alias = null;
                    if (Current.Kind == TokenKind.KwAs) { Advance(); alias = ExpectName(); }
                    names.Add(new KeyValuePair<string, string>(n, alias));
                    if (Current.Kind == TokenKind.Comma) { Advance(); continue; }
                    break;
                }
                if (paren) Expect(TokenKind.RightParen);
            }
            SourcePosition end = Previous.Span.End;
            // CPython rejects `from __future__ import braces` with the famous
            // "not a chance" SyntaxError; any other unknown __future__ feature is
            // also a compile-time error, but 'braces' is the canonical bizarre one.
            if (module != null && module.Parts.Count == 1 && module.Parts[0] == "__future__")
            {
                foreach (KeyValuePair<string, string> n in names)
                {
                    if (n.Key == "braces")
                        Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, new SourceSpan(start, end),
                            "not a chance (braces are not a valid __future__ feature).");
                }
            }
            ConsumeStatementEnd();
            return new FromImportStatement(dots, module, names, isStar, new SourceSpan(start, end));
        }

        private DottedName ParseDottedName()
        {
            SourcePosition start = Current.Span.Start;
            List<string> parts = new List<string>();
            parts.Add(ExpectName());
            while (Current.Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Name)
            {
                Advance();
                parts.Add(ExpectName());
            }
            return new DottedName(parts, new SourceSpan(start, Previous.Span.End));
        }

        // ---------- Helpers ----------

        private void SkipNewlines() { while (Current.Kind == TokenKind.NewLine) Advance(); }

        private void ConsumeStatementEnd()
        {
            while (Current.Kind == TokenKind.Semicolon) Advance();
            if (Current.Kind == TokenKind.NewLine) Advance();
        }

        private Token Current => _pos < _stream.Count ? _stream[_pos] : _stream[_stream.Count - 1];
        private Token Previous => _pos > 0 ? _stream[_pos - 1] : _stream[0];

        private Token Peek(int offset)
        {
            int i = _pos + offset;
            if (i < 0) i = 0;
            if (i >= _stream.Count) return _stream[_stream.Count - 1];
            return _stream[i];
        }

        private void Advance() { if (_pos < _stream.Count - 1) _pos++; }

        private bool Accept(TokenKind kind)
        {
            if (Current.Kind == kind) { Advance(); return true; }
            return false;
        }

        private void Expect(TokenKind kind)
        {
            if (Current.Kind == kind) { Advance(); return; }
            Report(DiagnosticCodes.UnexpectedToken, ParseSeverity.Error, Current.Span,
                "Expected " + kind + ", got " + Current.Kind + ".");
        }

        private string ExpectName()
        {
            if (Current.Kind == TokenKind.Name)
            {
                string n = Current.Text;
                Advance();
                return n;
            }
            Report(DiagnosticCodes.UnexpectedToken, ParseSeverity.Error, Current.Span,
                "Expected identifier, got " + Current.Kind + ".");
            return string.Empty;
        }

        private void Report(string code, ParseSeverity severity, SourceSpan span, string message)
        {
            _diagnostics.Add(new ParseDiagnostic(code, severity, span, message));
        }
    }
}
