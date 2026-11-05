using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Parsing
{
    /// <summary>
    /// Expression productions. Precedence (lowest → highest):
    /// lambda → named (<c>:=</c>) → ternary → or → and → not →
    /// comparison → | → ^ → &amp; → shift → +/- → *//%@ → unary +/-/~ →
    /// ** → await → primary (call/subscript/attribute) → atom.
    /// </summary>
    public sealed partial class PythonParser
    {
        private const string AwaitOutsideAsyncMessage = "'await' outside async function.";

        /// <summary>Entry point: a single expression or tuple-of-expressions.</summary>
        public ExpressionSyntax ParseExpression()
        {
            return ParseStarExpressions();
        }

        /// <summary>Parses a single expression (no comma/tuple).</summary>
        private ExpressionSyntax ParseSingleExpression()
        {
            return ParseNamedOrLambda();
        }

        /// <summary>Comma-separated expressions (produces TupleExpression if &gt;1 element OR trailing comma).</summary>
        private ExpressionSyntax ParseStarExpressions()
        {
            SourcePosition start = Current.Span.Start;
            ExpressionSyntax first = ParseStarExpression();
            if (Current.Kind != TokenKind.Comma) return first;
            List<ExpressionSyntax> elements = new List<ExpressionSyntax> { first };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (IsExpressionTerminator(Current.Kind)) break;
                elements.Add(ParseStarExpression());
            }
            return new TupleExpression(elements, new SourceSpan(start, Previous.Span.End));
        }

        private ExpressionSyntax ParseStarExpression()
        {
            if (Current.Kind == TokenKind.Star)
            {
                SourcePosition s = Current.Span.Start;
                Advance();
                ExpressionSyntax inner = ParseBitOr();
                return new StarredExpression(inner, isDouble: false, new SourceSpan(s, Previous.Span.End));
            }
            return ParseSingleExpression();
        }

        private ExpressionSyntax ParseNamedOrLambda()
        {
            if (Current.Kind == TokenKind.KwLambda) return ParseLambda();
            ExpressionSyntax e = ParseTernary();
            if (Current.Kind == TokenKind.Walrus && e is NameExpression target)
            {
                Advance();
                ExpressionSyntax value = ParseSingleExpression();
                return new NamedExpression(target, value, new SourceSpan(e.Span.Start, Previous.Span.End));
            }
            return e;
        }

        private ExpressionSyntax ParseLambda()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwLambda);
            List<ParameterSyntax> parameters = new List<ParameterSyntax>();
            if (Current.Kind != TokenKind.Colon)
            {
                parameters = ParseLambdaParameterList();
            }
            Expect(TokenKind.Colon);
            // A lambda body is a function scope: `lambda: (yield)` is a legal generator
            // lambda (Lib/_collections_abc.py), and `return`/`break` inside it follow the
            // same rules as any def body.
            int savedLoopDepth = _loopDepth;
            bool savedInAsync = _inAsyncFunction;
            _loopDepth = 0;
            _inAsyncFunction = false;
            _functionDepth++;
            _enclosingFunctionDepth++;
            ExpressionSyntax body = ParseSingleExpression();
            _enclosingFunctionDepth--;
            _functionDepth--;
            _inAsyncFunction = savedInAsync;
            _loopDepth = savedLoopDepth;
            return new LambdaExpression(parameters, body, new SourceSpan(start, Previous.Span.End));
        }

        private List<ParameterSyntax> ParseLambdaParameterList()
        {
            // Reduced form of ParseParameterList that stops at ':' rather than ')'.
            List<ParameterSyntax> list = new List<ParameterSyntax>();
            bool seenStar = false;
            while (Current.Kind != TokenKind.Colon && Current.Kind != TokenKind.EndOfFile)
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
                    Advance();
                    if (Current.Kind == TokenKind.Name)
                    {
                        string n = Current.Text; Advance();
                        list.Add(new ParameterSyntax(n, null, null, ParameterKind.VarPositional,
                            new SourceSpan(start, Previous.Span.End)));
                    }
                    seenStar = true;
                    if (Current.Kind == TokenKind.Comma) Advance();
                    continue;
                }
                if (Current.Kind == TokenKind.DoubleStar)
                {
                    Advance();
                    string n = ExpectName();
                    list.Add(new ParameterSyntax(n, null, null, ParameterKind.VarKeyword,
                        new SourceSpan(start, Previous.Span.End)));
                    if (Current.Kind == TokenKind.Comma) Advance();
                    continue;
                }
                if (Current.Kind != TokenKind.Name) break;
                string name = Current.Text; Advance();
                ExpressionSyntax defaultValue = null;
                if (Current.Kind == TokenKind.Assign) { Advance(); defaultValue = ParseSingleExpression(); }
                ParameterKind pk = seenStar ? ParameterKind.KeywordOnly : ParameterKind.Positional;
                list.Add(new ParameterSyntax(name, null, defaultValue, pk,
                    new SourceSpan(start, Previous.Span.End)));
                if (Current.Kind == TokenKind.Comma) Advance();
                else break;
            }
            return list;
        }

        private ExpressionSyntax ParseTernary()
        {
            ExpressionSyntax left = ParseOr();
            if (Current.Kind == TokenKind.KwIf)
            {
                Advance();
                ExpressionSyntax cond = ParseOr();
                Expect(TokenKind.KwElse);
                ExpressionSyntax right = ParseSingleExpression();
                return new ConditionalExpression(left, cond, right, new SourceSpan(left.Span.Start, Previous.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseOr()
        {
            ExpressionSyntax left = ParseAnd();
            while (Current.Kind == TokenKind.KwOr)
            {
                Advance();
                ExpressionSyntax right = ParseAnd();
                left = new BinaryExpression(BinaryOperator.Or, left, right, new SourceSpan(left.Span.Start, right.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseAnd()
        {
            ExpressionSyntax left = ParseNot();
            while (Current.Kind == TokenKind.KwAnd)
            {
                Advance();
                ExpressionSyntax right = ParseNot();
                left = new BinaryExpression(BinaryOperator.And, left, right, new SourceSpan(left.Span.Start, right.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseNot()
        {
            if (Current.Kind == TokenKind.KwNot)
            {
                SourcePosition s = Current.Span.Start;
                Advance();
                ExpressionSyntax inner = ParseNot();
                return new UnaryExpression(UnaryOperator.Not, inner, new SourceSpan(s, Previous.Span.End));
            }
            return ParseComparison();
        }

        private ExpressionSyntax ParseComparison()
        {
            ExpressionSyntax left = ParseBitOr();
            List<ComparisonOperator> ops = null;
            List<ExpressionSyntax> comparators = null;
            while (TryTakeComparisonOperator(out ComparisonOperator op))
            {
                if (ops == null) { ops = new List<ComparisonOperator>(); comparators = new List<ExpressionSyntax>(); }
                ExpressionSyntax next = ParseBitOr();
                ops.Add(op);
                comparators.Add(next);
            }
            if (ops == null) return left;
            return new ComparisonExpression(left, ops, comparators,
                new SourceSpan(left.Span.Start, Previous.Span.End));
        }

        private bool TryTakeComparisonOperator(out ComparisonOperator op)
        {
            switch (Current.Kind)
            {
                case TokenKind.Less: op = ComparisonOperator.Less; Advance(); return true;
                case TokenKind.LessEqual: op = ComparisonOperator.LessEq; Advance(); return true;
                case TokenKind.Greater: op = ComparisonOperator.Greater; Advance(); return true;
                case TokenKind.GreaterEqual: op = ComparisonOperator.GreaterEq; Advance(); return true;
                case TokenKind.EqualEqual: op = ComparisonOperator.Eq; Advance(); return true;
                case TokenKind.NotEqual: op = ComparisonOperator.NotEq; Advance(); return true;
                case TokenKind.KwIn: op = ComparisonOperator.In; Advance(); return true;
                case TokenKind.KwIs:
                    Advance();
                    if (Current.Kind == TokenKind.KwNot) { Advance(); op = ComparisonOperator.IsNot; return true; }
                    op = ComparisonOperator.Is; return true;
                case TokenKind.KwNot when Peek(1).Kind == TokenKind.KwIn:
                    Advance(); Advance();
                    op = ComparisonOperator.NotIn; return true;
                default: op = ComparisonOperator.Eq; return false;
            }
        }

        private ExpressionSyntax ParseBitOr()
        {
            ExpressionSyntax left = ParseBitXor();
            while (Current.Kind == TokenKind.Pipe)
            {
                Advance();
                ExpressionSyntax right = ParseBitXor();
                left = new BinaryExpression(BinaryOperator.BitOr, left, right, new SourceSpan(left.Span.Start, right.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseBitXor()
        {
            ExpressionSyntax left = ParseBitAnd();
            while (Current.Kind == TokenKind.Caret)
            {
                Advance();
                ExpressionSyntax right = ParseBitAnd();
                left = new BinaryExpression(BinaryOperator.BitXor, left, right, new SourceSpan(left.Span.Start, right.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseBitAnd()
        {
            ExpressionSyntax left = ParseShift();
            while (Current.Kind == TokenKind.Amp)
            {
                Advance();
                ExpressionSyntax right = ParseShift();
                left = new BinaryExpression(BinaryOperator.BitAnd, left, right, new SourceSpan(left.Span.Start, right.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseShift()
        {
            ExpressionSyntax left = ParseSum();
            while (Current.Kind == TokenKind.LeftShift || Current.Kind == TokenKind.RightShift)
            {
                BinaryOperator op = Current.Kind == TokenKind.LeftShift ? BinaryOperator.LShift : BinaryOperator.RShift;
                Advance();
                ExpressionSyntax right = ParseSum();
                left = new BinaryExpression(op, left, right, new SourceSpan(left.Span.Start, right.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseSum()
        {
            ExpressionSyntax left = ParseTerm();
            while (Current.Kind == TokenKind.Plus || Current.Kind == TokenKind.Minus)
            {
                BinaryOperator op = Current.Kind == TokenKind.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
                Advance();
                ExpressionSyntax right = ParseTerm();
                left = new BinaryExpression(op, left, right, new SourceSpan(left.Span.Start, right.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseTerm()
        {
            ExpressionSyntax left = ParseFactor();
            while (true)
            {
                BinaryOperator? op = null;
                switch (Current.Kind)
                {
                    case TokenKind.Star: op = BinaryOperator.Multiply; break;
                    case TokenKind.Slash: op = BinaryOperator.Divide; break;
                    case TokenKind.DoubleSlash: op = BinaryOperator.FloorDivide; break;
                    case TokenKind.Percent: op = BinaryOperator.Modulo; break;
                    case TokenKind.At: op = BinaryOperator.MatMul; break;
                }
                if (!op.HasValue) break;
                Advance();
                ExpressionSyntax right = ParseFactor();
                left = new BinaryExpression(op.Value, left, right, new SourceSpan(left.Span.Start, right.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseFactor()
        {
            if (Current.Kind == TokenKind.Plus || Current.Kind == TokenKind.Minus || Current.Kind == TokenKind.Tilde)
            {
                SourcePosition s = Current.Span.Start;
                UnaryOperator op =
                    Current.Kind == TokenKind.Plus ? UnaryOperator.Plus :
                    Current.Kind == TokenKind.Minus ? UnaryOperator.Minus :
                    UnaryOperator.BitNot;
                Advance();
                ExpressionSyntax inner = ParseFactor();
                return new UnaryExpression(op, inner, new SourceSpan(s, Previous.Span.End));
            }
            return ParsePower();
        }

        private ExpressionSyntax ParsePower()
        {
            ExpressionSyntax left = ParseAwait();
            if (Current.Kind == TokenKind.DoubleStar)
            {
                Advance();
                ExpressionSyntax right = ParseFactor();
                return new BinaryExpression(BinaryOperator.Power, left, right,
                    new SourceSpan(left.Span.Start, right.Span.End));
            }
            return left;
        }

        private ExpressionSyntax ParseAwait()
        {
            if (Current.Kind == TokenKind.KwAwait)
            {
                SourcePosition s = Current.Span.Start;
                if (!_inAsyncFunction && _comprehensionDepth == 0)
                    Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                        AwaitOutsideAsyncMessage);
                Advance();
                ExpressionSyntax inner = ParsePrimary();
                return new UnaryExpression(UnaryOperator.Await, inner, new SourceSpan(s, Previous.Span.End));
            }
            return ParsePrimary();
        }

        private ExpressionSyntax ParsePrimary()
        {
            ExpressionSyntax expr = ParseAtom();
            while (true)
            {
                if (Current.Kind == TokenKind.Dot && Peek(1).Kind == TokenKind.Name)
                {
                    Advance();
                    string name = Current.Text;
                    SourcePosition end = Current.Span.End;
                    Advance();
                    expr = new AttributeExpression(expr, name, new SourceSpan(expr.Span.Start, end));
                }
                else if (Current.Kind == TokenKind.LeftParen)
                {
                    Advance();
                    List<CallArgument> args = new List<CallArgument>();
                    while (Current.Kind != TokenKind.RightParen && Current.Kind != TokenKind.EndOfFile)
                    {
                        args.Add(ParseCallArgument());
                        if (Current.Kind == TokenKind.Comma) { Advance(); continue; }
                        break;
                    }
                    SourcePosition end = Current.Span.End;
                    Expect(TokenKind.RightParen);
                    expr = new CallExpression(expr, args, new SourceSpan(expr.Span.Start, end));
                }
                else if (Current.Kind == TokenKind.LeftBracket)
                {
                    Advance();
                    ExpressionSyntax index = ParseSubscriptIndex();
                    SourcePosition end = Current.Span.End;
                    Expect(TokenKind.RightBracket);
                    expr = new SubscriptExpression(expr, index, new SourceSpan(expr.Span.Start, end));
                }
                else break;
            }
            return expr;
        }

        private CallArgument ParseCallArgument()
        {
            SourcePosition start = Current.Span.Start;
            if (Current.Kind == TokenKind.Star)
            {
                Advance();
                ExpressionSyntax v = ParseSingleExpression();
                return new CallArgument(CallArgument.ArgKind.Star, null, v, new SourceSpan(start, Previous.Span.End));
            }
            if (Current.Kind == TokenKind.DoubleStar)
            {
                Advance();
                ExpressionSyntax v = ParseSingleExpression();
                return new CallArgument(CallArgument.ArgKind.DoubleStar, null, v, new SourceSpan(start, Previous.Span.End));
            }
            if (Current.Kind == TokenKind.Name && Peek(1).Kind == TokenKind.Assign)
            {
                string name = Current.Text; Advance(); Advance();
                ExpressionSyntax v = ParseSingleExpression();
                return new CallArgument(CallArgument.ArgKind.Keyword, name, v, new SourceSpan(start, Previous.Span.End));
            }
            ExpressionSyntax value = ParseSingleExpression();
            // Generator-expression argument: `func(x for x in y)` — rewrap as a ComprehensionExpression.
            if (Current.Kind == TokenKind.KwFor || (Current.Kind == TokenKind.KwAsync && Peek(1).Kind == TokenKind.KwFor)) // incl. PEP 530 async comprehensions
            {
                List<ComprehensionClause> clauses = ParseComprehensionClauses();
                ComprehensionExpression comp = new ComprehensionExpression(
                    ComprehensionKind.Generator, value, null, clauses,
                    new SourceSpan(start, Previous.Span.End));
                return new CallArgument(CallArgument.ArgKind.Positional, null, comp, new SourceSpan(start, Previous.Span.End));
            }
            return new CallArgument(CallArgument.ArgKind.Positional, null, value, new SourceSpan(start, Previous.Span.End));
        }

        private ExpressionSyntax ParseSubscriptIndex()
        {
            // A subscript may be a slice, a simple expression, or a tuple of those.
            SourcePosition start = Current.Span.Start;
            List<ExpressionSyntax> items = new List<ExpressionSyntax> { ParseSliceOrExpression() };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (Current.Kind == TokenKind.RightBracket) break;
                items.Add(ParseSliceOrExpression());
            }
            if (items.Count == 1) return items[0];
            return new TupleExpression(items, new SourceSpan(start, Previous.Span.End));
        }

        private ExpressionSyntax ParseSliceOrExpression()
        {
            SourcePosition start = Current.Span.Start;
            // PEP 646 unpacking in a subscript: `tuple[int, *Ts]`, `A[float, *tuple[int, ...]]`.
            if (Current.Kind == TokenKind.Star)
            {
                Advance();
                ExpressionSyntax unpacked = ParseSingleExpression();
                return new StarredExpression(unpacked, isDouble: false, new SourceSpan(start, Previous.Span.End));
            }
            ExpressionSyntax lower = null;
            if (Current.Kind != TokenKind.Colon) lower = ParseSingleExpression();
            if (Current.Kind != TokenKind.Colon)
            {
                // Not a slice — just an expression.
                return lower ?? new EllipsisExpression(new SourceSpan(start, start));
            }
            Advance(); // consume first ':'
            ExpressionSyntax upper = null;
            if (Current.Kind != TokenKind.Colon && Current.Kind != TokenKind.Comma && Current.Kind != TokenKind.RightBracket)
                upper = ParseSingleExpression();
            ExpressionSyntax step = null;
            if (Current.Kind == TokenKind.Colon)
            {
                Advance();
                if (Current.Kind != TokenKind.Comma && Current.Kind != TokenKind.RightBracket)
                    step = ParseSingleExpression();
            }
            return new SliceExpression(lower, upper, step, new SourceSpan(start, Previous.Span.End));
        }

        private ExpressionSyntax ParseAtom()
        {
            SourcePosition start = Current.Span.Start;
            switch (Current.Kind)
            {
                case TokenKind.KwTrue:
                case TokenKind.KwFalse:
                case TokenKind.KwNone:
                    {
                        Token t = Current;
                        Advance();
                        return new LiteralExpression(t, new SourceSpan(start, t.Span.End));
                    }
                case TokenKind.Number:
                    {
                        Token t = Current;
                        Advance();
                        return new LiteralExpression(t, new SourceSpan(start, t.Span.End));
                    }
                case TokenKind.String:
                case TokenKind.FStringStart:
                    // Adjacent string AND f-string literals concatenate per the Python spec
                    // (implicit concatenation), including mixed runs like `f"a" "b" f"c"`.
                    return ParseStringConcatenation();
                case TokenKind.Ellipsis:
                    {
                        Advance();
                        return new EllipsisExpression(new SourceSpan(start, Previous.Span.End));
                    }
                case TokenKind.Name:
                    {
                        string n = Current.Text;
                        Advance();
                        return new NameExpression(n, new SourceSpan(start, Previous.Span.End));
                    }
                case TokenKind.LeftParen:
                    return ParseParenOrTupleOrGenExp();
                case TokenKind.LeftBracket:
                    return ParseListOrComprehension();
                case TokenKind.LeftBrace:
                    return ParseSetOrDictOrComprehension();
                case TokenKind.KwYield:
                    return ParseYieldExpression();
                default:
                    Report(DiagnosticCodes.UnexpectedToken, ParseSeverity.Error, Current.Span,
                        "Unexpected token in expression: " + Current.Kind);
                    Advance();
                    return new NameExpression("<error>", new SourceSpan(start, Previous.Span.End));
            }
        }

        private ExpressionSyntax ParseYieldExpression()
        {
            SourcePosition start = Current.Span.Start;
            if (_functionDepth == 0)
                Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                    "'yield' outside function.");
            Expect(TokenKind.KwYield);
            bool isFrom = false;
            if (Current.Kind == TokenKind.KwFrom) { isFrom = true; Advance(); }
            ExpressionSyntax value = null;
            if (!IsExpressionTerminator(Current.Kind))
            {
                value = isFrom ? ParseSingleExpression() : ParseStarExpressions();
            }
            return new YieldExpression(isFrom, value, new SourceSpan(start, Previous.Span.End));
        }

        private ExpressionSyntax ParseParenOrTupleOrGenExp()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.LeftParen);

            if (Current.Kind == TokenKind.RightParen)
            {
                Advance();
                return new TupleExpression(new List<ExpressionSyntax>(), new SourceSpan(start, Previous.Span.End));
            }

            // A generator expression runs in its own frame, so an `await` in its element
            // expression makes the GENERATOR asynchronous and stays legal even inside a
            // plain def. We only learn it is a genexp after parsing the element, so any
            // await complaint raised while parsing it is withdrawn here.
            int diagMark = _diagnostics.Count;
            ExpressionSyntax first = ParseStarExpression();
            if (Current.Kind == TokenKind.KwFor || (Current.Kind == TokenKind.KwAsync && Peek(1).Kind == TokenKind.KwFor)) // incl. PEP 530 async comprehensions
            {
                WithdrawAwaitDiagnosticsFrom(diagMark);
                List<ComprehensionClause> clauses = ParseComprehensionClauses();
                Expect(TokenKind.RightParen);
                return new ComprehensionExpression(ComprehensionKind.Generator, first, null, clauses,
                    new SourceSpan(start, Previous.Span.End));
            }

            if (Current.Kind == TokenKind.RightParen)
            {
                Advance();
                return new ParenthesizedExpression(first, new SourceSpan(start, Previous.Span.End));
            }

            List<ExpressionSyntax> elements = new List<ExpressionSyntax> { first };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (Current.Kind == TokenKind.RightParen) break;
                elements.Add(ParseStarExpression());
            }
            Expect(TokenKind.RightParen);
            return new TupleExpression(elements, new SourceSpan(start, Previous.Span.End));
        }

        private ExpressionSyntax ParseListOrComprehension()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.LeftBracket);
            if (Current.Kind == TokenKind.RightBracket)
            {
                Advance();
                return new ListExpression(new List<ExpressionSyntax>(), new SourceSpan(start, Previous.Span.End));
            }
            ExpressionSyntax first = ParseStarExpression();
            if (Current.Kind == TokenKind.KwFor || (Current.Kind == TokenKind.KwAsync && Peek(1).Kind == TokenKind.KwFor)) // incl. PEP 530 async comprehensions
            {
                List<ComprehensionClause> clauses = ParseComprehensionClauses();
                Expect(TokenKind.RightBracket);
                return new ComprehensionExpression(ComprehensionKind.List, first, null, clauses,
                    new SourceSpan(start, Previous.Span.End));
            }
            List<ExpressionSyntax> elements = new List<ExpressionSyntax> { first };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (Current.Kind == TokenKind.RightBracket) break;
                elements.Add(ParseStarExpression());
            }
            Expect(TokenKind.RightBracket);
            return new ListExpression(elements, new SourceSpan(start, Previous.Span.End));
        }

        private ExpressionSyntax ParseSetOrDictOrComprehension()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.LeftBrace);
            if (Current.Kind == TokenKind.RightBrace)
            {
                Advance();
                return new DictExpression(new List<DictEntry>(), new SourceSpan(start, Previous.Span.End));
            }

            // Peek: is this a dict (key:value) or set?
            if (Current.Kind == TokenKind.DoubleStar)
            {
                // **kwargs-style unpack at start of dict.
                return ParseDictBody(start, firstKey: null, firstValueIsDoubleStar: true);
            }

            // A leading `*` makes it unambiguously a SET with a starred element ({*a, ...}) — dicts
            // unpack with `**`, and the set-element expression can't be starred in a comprehension.
            // Without this, PEP 448 set unpacking (black's `{id(x), *omit}`) failed to parse (iter 374).
            if (Current.Kind == TokenKind.Star)
            {
                List<ExpressionSyntax> starSet = new List<ExpressionSyntax> { ParseStarExpression() };
                while (Current.Kind == TokenKind.Comma)
                {
                    Advance();
                    if (Current.Kind == TokenKind.RightBrace) break;
                    starSet.Add(ParseStarExpression());
                }
                Expect(TokenKind.RightBrace);
                return new SetExpression(starSet, new SourceSpan(start, Previous.Span.End));
            }

            ExpressionSyntax first = ParseSingleExpression();
            if (Current.Kind == TokenKind.Colon)
            {
                Advance();
                ExpressionSyntax firstValue = ParseSingleExpression();
                return ParseDictBodyWithFirstPair(start, first, firstValue);
            }

            // It's a set (literal or comprehension).
            if (Current.Kind == TokenKind.KwFor || (Current.Kind == TokenKind.KwAsync && Peek(1).Kind == TokenKind.KwFor)) // incl. PEP 530 async comprehensions
            {
                List<ComprehensionClause> clauses = ParseComprehensionClauses();
                Expect(TokenKind.RightBrace);
                return new ComprehensionExpression(ComprehensionKind.Set, first, null, clauses,
                    new SourceSpan(start, Previous.Span.End));
            }
            List<ExpressionSyntax> setElements = new List<ExpressionSyntax> { first };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (Current.Kind == TokenKind.RightBrace) break;
                // Later elements may be starred: {3, *a} (PEP 448).
                setElements.Add(ParseStarExpression());
            }
            Expect(TokenKind.RightBrace);
            return new SetExpression(setElements, new SourceSpan(start, Previous.Span.End));
        }

        private ExpressionSyntax ParseDictBody(SourcePosition start, ExpressionSyntax firstKey, bool firstValueIsDoubleStar)
        {
            List<DictEntry> entries = new List<DictEntry>();
            if (firstValueIsDoubleStar)
            {
                Expect(TokenKind.DoubleStar);
                ExpressionSyntax v = ParseSingleExpression();
                entries.Add(new DictEntry(null, v, new SourceSpan(start, Previous.Span.End)));
                while (Current.Kind == TokenKind.Comma)
                {
                    Advance();
                    if (Current.Kind == TokenKind.RightBrace) break;
                    entries.Add(ParseDictEntry());
                }
                Expect(TokenKind.RightBrace);
                return new DictExpression(entries, new SourceSpan(start, Previous.Span.End));
            }
            return null;
        }

        private ExpressionSyntax ParseDictBodyWithFirstPair(SourcePosition start, ExpressionSyntax firstKey, ExpressionSyntax firstValue)
        {
            // Check for dict comprehension: first pair then `for ...`.
            if (Current.Kind == TokenKind.KwFor || (Current.Kind == TokenKind.KwAsync && Peek(1).Kind == TokenKind.KwFor)) // incl. PEP 530 async comprehensions
            {
                List<ComprehensionClause> clauses = ParseComprehensionClauses();
                Expect(TokenKind.RightBrace);
                return new ComprehensionExpression(ComprehensionKind.Dict, firstKey, firstValue, clauses,
                    new SourceSpan(start, Previous.Span.End));
            }
            List<DictEntry> entries = new List<DictEntry>
            {
                new DictEntry(firstKey, firstValue, new SourceSpan(firstKey.Span.Start, firstValue.Span.End))
            };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (Current.Kind == TokenKind.RightBrace) break;
                entries.Add(ParseDictEntry());
            }
            Expect(TokenKind.RightBrace);
            return new DictExpression(entries, new SourceSpan(start, Previous.Span.End));
        }

        private DictEntry ParseDictEntry()
        {
            SourcePosition s = Current.Span.Start;
            if (Current.Kind == TokenKind.DoubleStar)
            {
                Advance();
                ExpressionSyntax v = ParseSingleExpression();
                return new DictEntry(null, v, new SourceSpan(s, Previous.Span.End));
            }
            ExpressionSyntax key = ParseSingleExpression();
            Expect(TokenKind.Colon);
            ExpressionSyntax value = ParseSingleExpression();
            return new DictEntry(key, value, new SourceSpan(s, Previous.Span.End));
        }

        private List<ComprehensionClause> ParseComprehensionClauses()
        {
            List<ComprehensionClause> clauses = new List<ComprehensionClause>();
            _comprehensionDepth++;
            while (Current.Kind == TokenKind.KwFor || Current.Kind == TokenKind.KwAsync)
            {
                SourcePosition s = Current.Span.Start;
                bool isAsync = false;
                if (Current.Kind == TokenKind.KwAsync) { isAsync = true; Advance(); }
                Expect(TokenKind.KwFor);
                ExpressionSyntax target = ParseTargetList();
                Expect(TokenKind.KwIn);
                ExpressionSyntax iter = ParseOr();
                List<ExpressionSyntax> ifs = new List<ExpressionSyntax>();
                while (Current.Kind == TokenKind.KwIf)
                {
                    Advance();
                    ifs.Add(ParseOr());
                }
                clauses.Add(new ComprehensionClause(isAsync, target, iter, ifs,
                    new SourceSpan(s, Previous.Span.End)));
            }
            _comprehensionDepth--;
            return clauses;
        }

        private void WithdrawAwaitDiagnosticsFrom(int mark)
        {
            for (int i = _diagnostics.Count - 1; i >= mark; i--)
            {
                if (_diagnostics[i].Message == AwaitOutsideAsyncMessage) _diagnostics.RemoveAt(i);
            }
        }

        private ExpressionSyntax ParseTargetList()
        {
            SourcePosition s = Current.Span.Start;
            ExpressionSyntax first = ParseTargetElement();
            if (Current.Kind != TokenKind.Comma) return first;
            List<ExpressionSyntax> elements = new List<ExpressionSyntax> { first };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (Current.Kind == TokenKind.KwIn) break;
                elements.Add(ParseTargetElement());
            }
            return new TupleExpression(elements, new SourceSpan(s, Previous.Span.End));
        }

        // A single element of a for/comprehension target list. Python allows exactly one starred
        // target (`for a, *rest in xs`, `[e for a, *rest in xs]`) — without this the `*rest` was
        // mis-parsed and `rest` never bound, false-flagging PY3006 (rich control.py:64).
        private ExpressionSyntax ParseTargetElement()
        {
            if (Current.Kind == TokenKind.Star)
            {
                SourcePosition s = Current.Span.Start;
                Advance();
                ExpressionSyntax inner = ParseBitOr();
                return new StarredExpression(inner, isDouble: false, new SourceSpan(s, Previous.Span.End));
            }
            return ParseBitOr();
        }

        // Python implicit concatenation of adjacent string / f-string literals: `"a" "b"`,
        // `f"a" f"b"`, `f"a" "b"`, `"a" f"b"`. A run with no f-string collapses to a single
        // LiteralExpression (prior behaviour); any f-string in the run yields one flattened
        // FStringExpression. Fixes "Expected RightParen, got FStringStart" on the extremely
        // common wrapped-print / wrapped-write idiom (`f"..." f"..."` across two lines).
        private ExpressionSyntax ParseStringConcatenation()
        {
            SourcePosition start = Current.Span.Start;
            Token firstString = null;
            bool anyFString = false;
            List<SyntaxNode> parts = new List<SyntaxNode>();
            List<char?> conversions = new List<char?>();
            List<string> formatSpecs = new List<string>();
            SourcePosition end = Current.Span.End;

            while (Current.Kind == TokenKind.String || Current.Kind == TokenKind.FStringStart)
            {
                if (Current.Kind == TokenKind.String)
                {
                    if (firstString == null) firstString = Current;
                    parts.Add(new LiteralExpression(Current, Current.Span));
                    conversions.Add(null);
                    formatSpecs.Add(null);
                    end = Current.Span.End;
                    Advance();
                }
                else
                {
                    anyFString = true;
                    ExpressionSyntax fs = ParseFString();
                    if (fs is FStringExpression fse)
                    {
                        // flatten
                        parts.AddRange(fse.Parts);
                        conversions.AddRange(fse.Conversions);
                        formatSpecs.AddRange(fse.FormatSpecs);
                    }
                    else { parts.Add(fs); conversions.Add(null); formatSpecs.Add(null); }
                    end = Previous.Span.End;
                }
            }

            if (!anyFString)
                return new LiteralExpression(firstString, new SourceSpan(start, end));
            return new FStringExpression(parts, conversions, formatSpecs, new SourceSpan(start, end));
        }

        private ExpressionSyntax ParseFString()
        {
            SourcePosition start = Current.Span.Start;
            List<SyntaxNode> parts = new List<SyntaxNode>();
            List<char?> conversions = new List<char?>();
            List<string> formatSpecs = new List<string>();
            Expect(TokenKind.FStringStart);
            while (Current.Kind != TokenKind.FStringEnd && Current.Kind != TokenKind.EndOfFile)
            {
                if (Current.Kind == TokenKind.FStringMiddle)
                {
                    parts.Add(new LiteralExpression(Current, Current.Span));
                    conversions.Add(null);
                    formatSpecs.Add(null);
                    Advance();
                }
                else if (Current.Kind == TokenKind.LeftBrace)
                {
                    Advance();
                    ExpressionSyntax expr = ParseSingleExpression();

                    char? conversion = null;
                    string formatSpec = null;

                    // Optional `!r` / `!s` / `!a` conversion — the lexer emits it as one
                    // FStringMiddle token whose text starts with '!'.
                    if (Current.Kind == TokenKind.FStringMiddle && Current.Text.Length > 0 && Current.Text[0] == '!')
                    {
                        conversion = Current.Text.Length > 1 ? Current.Text[1] : (char?)null;
                        Advance();
                    }

                    // Optional `:format_spec` — the lexer emits it as one FStringMiddle token
                    // whose text starts with the ':' itself, which must be stripped. A
                    // nested/dynamic spec (`{x:{width}}`) still arrives as one literal token
                    // here (the lexer is brace-aware but doesn't re-tokenize the interior) —
                    // callers that need it resolved statically reject a spec that still
                    // contains an unresolved '{' rather than silently mistranslating it.
                    if (Current.Kind == TokenKind.FStringMiddle && Current.Text.Length > 0 && Current.Text[0] == ':')
                    {
                        formatSpec = Current.Text.Substring(1);
                        Advance();
                    }

                    // Anything else between here and '}' is unrecognized f-string syntax the
                    // parser doesn't model — skip defensively rather than getting stuck.
                    while (Current.Kind != TokenKind.RightBrace && Current.Kind != TokenKind.EndOfFile) Advance();
                    Expect(TokenKind.RightBrace);

                    parts.Add(expr);
                    conversions.Add(conversion);
                    formatSpecs.Add(formatSpec);
                }
                else
                {
                    Advance(); // defensive
                }
            }
            Expect(TokenKind.FStringEnd);
            return new FStringExpression(parts, conversions, formatSpecs, new SourceSpan(start, Previous.Span.End));
        }

        private static bool IsExpressionTerminator(TokenKind k)
        {
            switch (k)
            {
                case TokenKind.Comma:
                case TokenKind.Colon:
                case TokenKind.Assign:
                case TokenKind.PlusAssign:
                case TokenKind.MinusAssign:
                case TokenKind.StarAssign:
                case TokenKind.SlashAssign:
                case TokenKind.DoubleSlashAssign:
                case TokenKind.DoubleStarAssign:
                case TokenKind.PercentAssign:
                case TokenKind.AtAssign:
                case TokenKind.AmpAssign:
                case TokenKind.PipeAssign:
                case TokenKind.CaretAssign:
                case TokenKind.LeftShiftAssign:
                case TokenKind.RightShiftAssign:
                case TokenKind.NewLine:
                case TokenKind.Semicolon:
                case TokenKind.RightParen:
                case TokenKind.RightBracket:
                case TokenKind.RightBrace:
                case TokenKind.Dedent:
                case TokenKind.Indent:
                case TokenKind.EndOfFile:
                    return true;
                default:
                    return false;
            }
        }
    }
}
