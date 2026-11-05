using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Parsing
{
    /// <summary>
    /// Statement productions: compound statements (if/while/for/try/with/match)
    /// and simple statements (return/raise/assert/del/global/nonlocal/yield/assignment).
    /// </summary>
    public sealed partial class PythonParser
    {
        // ---------- Compound statements ----------

        private IfStatement ParseIfStatement()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwIf);
            ExpressionSyntax test = ParseSingleExpression();
            Expect(TokenKind.Colon);
            SuiteSyntax body = ParseSuite();
            List<IfStatement> elifs = new List<IfStatement>();
            while (Current.Kind == TokenKind.KwElif)
            {
                SourcePosition elifStart = Current.Span.Start;
                Advance();
                ExpressionSyntax elifTest = ParseSingleExpression();
                Expect(TokenKind.Colon);
                SuiteSyntax elifBody = ParseSuite();
                elifs.Add(new IfStatement(elifTest, elifBody, null, null, new SourceSpan(elifStart, Previous.Span.End)));
            }
            SuiteSyntax elseBlock = null;
            if (Current.Kind == TokenKind.KwElse)
            {
                Advance();
                Expect(TokenKind.Colon);
                elseBlock = ParseSuite();
            }
            return new IfStatement(test, body, elifs, elseBlock, new SourceSpan(start, Previous.Span.End));
        }

        private WhileStatement ParseWhileStatement()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwWhile);
            ExpressionSyntax test = ParseSingleExpression();
            Expect(TokenKind.Colon);
            _loopDepth++;
            SuiteSyntax body = ParseSuite();
            _loopDepth--;
            SuiteSyntax elseBlock = null;
            if (Current.Kind == TokenKind.KwElse)
            {
                Advance();
                Expect(TokenKind.Colon);
                elseBlock = ParseSuite();
            }
            return new WhileStatement(test, body, elseBlock, new SourceSpan(start, Previous.Span.End));
        }

        private ForStatement ParseForStatement(bool isAsync)
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwFor);
            ExpressionSyntax target = ParseTargetList();
            Expect(TokenKind.KwIn);
            ExpressionSyntax iter = ParseStarExpressions();
            Expect(TokenKind.Colon);
            _loopDepth++;
            SuiteSyntax body = ParseSuite();
            _loopDepth--;
            SuiteSyntax elseBlock = null;
            if (Current.Kind == TokenKind.KwElse)
            {
                Advance();
                Expect(TokenKind.Colon);
                elseBlock = ParseSuite();
            }
            return new ForStatement(isAsync, target, iter, body, elseBlock, new SourceSpan(start, Previous.Span.End));
        }

        private TryStatement ParseTryStatement()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwTry);
            Expect(TokenKind.Colon);
            SuiteSyntax body = ParseSuite();
            List<ExceptHandler> handlers = new List<ExceptHandler>();
            while (Current.Kind == TokenKind.KwExcept)
            {
                SourcePosition exceptStart = Current.Span.Start;
                Advance();
                bool isStar = false;
                if (Current.Kind == TokenKind.Star) { isStar = true; Advance(); }
                ExpressionSyntax exceptionType = null;
                string name = null;
                if (Current.Kind != TokenKind.Colon)
                {
                    exceptionType = ParseSingleExpression();
                    // PEP 758: `except A, B:` — an unparenthesized tuple of exception types
                    // (only when no 'as' binding follows).
                    if (Current.Kind == TokenKind.Comma)
                    {
                        List<ExpressionSyntax> types = new List<ExpressionSyntax> { exceptionType };
                        while (Current.Kind == TokenKind.Comma)
                        {
                            Advance();
                            if (Current.Kind == TokenKind.Colon || Current.Kind == TokenKind.KwAs) break;
                            types.Add(ParseSingleExpression());
                        }
                        exceptionType = new TupleExpression(types,
                            new SourceSpan(types[0].Span.Start, Previous.Span.End));
                    }
                    if (Current.Kind == TokenKind.KwAs) { Advance(); name = ExpectName(); }
                }
                Expect(TokenKind.Colon);
                SuiteSyntax handlerBody = ParseSuite();
                handlers.Add(new ExceptHandler(isStar, exceptionType, name, handlerBody,
                    new SourceSpan(exceptStart, Previous.Span.End)));
            }
            SuiteSyntax elseBlock = null;
            if (Current.Kind == TokenKind.KwElse)
            {
                Advance();
                Expect(TokenKind.Colon);
                elseBlock = ParseSuite();
            }
            SuiteSyntax finallyBlock = null;
            if (Current.Kind == TokenKind.KwFinally)
            {
                Advance();
                Expect(TokenKind.Colon);
                finallyBlock = ParseSuite();
            }
            return new TryStatement(body, handlers, elseBlock, finallyBlock, new SourceSpan(start, Previous.Span.End));
        }

        private WithStatement ParseWithStatement(bool isAsync)
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwWith);

            // A leading '(' is AMBIGUOUS. It may open the PEP 617 (Py 3.10+) parenthesized
            // with-items GROUP — `with (a as x, b as y):` — OR it may just be an ordinary
            // parenthesized EXPRESSION that is (part of) a single context manager, e.g.
            // `with (out / "f").open("w") as f:`. The old code always consumed the '(' as a
            // group and then failed at the '.' after ')' ("Expected Colon, got Dot").
            // Resolve it CPython-style: try the group interpretation behind a savepoint and
            // accept it only if it ends at ')' immediately followed by ':'; otherwise
            // backtrack and parse the with-items normally (the '(' is an expression paren).
            List<WithItem> items = null;
            if (Current.Kind == TokenKind.LeftParen)
            {
                int save = _pos;
                List<WithItem> grouped = TryParseParenthesizedWithItems();
                if (grouped != null && grouped.Count > 0) items = grouped;
                else _pos = save;
            }

            if (items == null)
            {
                items = new List<WithItem>();
                while (true)
                {
                    SourcePosition itemStart = Current.Span.Start;
                    ExpressionSyntax ctx = ParseSingleExpression();
                    ExpressionSyntax vars = null;
                    if (Current.Kind == TokenKind.KwAs)
                    {
                        Advance();
                        vars = ParseSingleExpression();
                    }
                    items.Add(new WithItem(ctx, vars, new SourceSpan(itemStart, Previous.Span.End)));
                    if (Current.Kind == TokenKind.Comma) { Advance(); continue; }
                    break;
                }
            }

            Expect(TokenKind.Colon);
            SuiteSyntax body = ParseSuite();
            return new WithStatement(isAsync, items, body, new SourceSpan(start, Previous.Span.End));
        }

        // Trial parse of the PEP 617 parenthesized with-items group: `( item (',' item)* ','? )`.
        // Returns the items only when the group closes with ')' IMMEDIATELY followed by ':';
        // otherwise returns null so the caller backtracks and treats the '(' as an expression
        // paren (e.g. `with (a).b() as c:`). Must be called with Current == '('.
        private List<WithItem> TryParseParenthesizedWithItems()
        {
            Advance(); // consume '('
            List<WithItem> items = new List<WithItem>();
            while (true)
            {
                if (Current.Kind == TokenKind.RightParen) break;
                SourcePosition itemStart = Current.Span.Start;
                ExpressionSyntax ctx = ParseSingleExpression();
                ExpressionSyntax vars = null;
                if (Current.Kind == TokenKind.KwAs)
                {
                    Advance();
                    vars = ParseSingleExpression();
                }
                items.Add(new WithItem(ctx, vars, new SourceSpan(itemStart, Previous.Span.End)));
                if (Current.Kind == TokenKind.Comma) { Advance(); continue; }
                break;
            }
            if (Current.Kind != TokenKind.RightParen) return null;
            Advance(); // consume ')'
            if (Current.Kind != TokenKind.Colon) return null; // ')' not followed by ':' → expression paren
            return items;
        }

        // ---------- Soft-keyword match/case ----------

        private bool IsMatchStart()
        {
            // "match" at statement start is a soft keyword iff: the rest of the line is an
            // expression followed by ':' and the next line begins with 'case'.
            // Cheap heuristic: scan ahead to the statement-ending NEWLINE. If we see ':'
            // at bracket-depth 0 before NEWLINE, this is a match statement.
            // `match: str | None` (an annotated assignment) and `match = x` use 'match' as
            // an ordinary name — a match statement always has a subject before the ':'.
            if (Peek(1).Kind == TokenKind.Colon || Peek(1).Kind == TokenKind.Assign
                || Peek(1).Kind == TokenKind.Dot || Peek(1).Kind == TokenKind.Comma)
                return false;
            int save = _pos;
            Advance(); // consume 'match'
            int parenDepth = 0, bracketDepth = 0, braceDepth = 0;
            bool foundColon = false;
            while (Current.Kind != TokenKind.EndOfFile && Current.Kind != TokenKind.NewLine)
            {
                if (Current.Kind == TokenKind.LeftParen) parenDepth++;
                else if (Current.Kind == TokenKind.RightParen && parenDepth > 0) parenDepth--;
                else if (Current.Kind == TokenKind.LeftBracket) bracketDepth++;
                else if (Current.Kind == TokenKind.RightBracket && bracketDepth > 0) bracketDepth--;
                else if (Current.Kind == TokenKind.LeftBrace) braceDepth++;
                else if (Current.Kind == TokenKind.RightBrace && braceDepth > 0) braceDepth--;
                else if (Current.Kind == TokenKind.Colon && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                {
                    foundColon = true;
                    break;
                }
                Advance();
            }
            _pos = save;
            return foundColon;
        }

        private MatchStatement ParseMatchStatement()
        {
            SourcePosition start = Current.Span.Start;
            // Consume the soft 'match' token.
            if (Current.Kind == TokenKind.Name && Current.Text == "match") Advance();
            ExpressionSyntax subject = ParseStarExpressions();
            Expect(TokenKind.Colon);

            // Body: NEWLINE INDENT case_block+ DEDENT
            SkipNewlines();
            if (Current.Kind != TokenKind.Indent)
            {
                Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                    "Expected indented block after 'match:'.");
                return new MatchStatement(subject, new List<CaseBlock>(), new SourceSpan(start, Previous.Span.End));
            }
            Advance();
            SkipNewlines();
            List<CaseBlock> cases = new List<CaseBlock>();
            while (Current.Kind != TokenKind.Dedent && Current.Kind != TokenKind.EndOfFile)
            {
                if (Current.Kind == TokenKind.Name && Current.Text == "case")
                {
                    cases.Add(ParseCaseBlock());
                    SkipNewlines();
                }
                else
                {
                    Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                        "Expected 'case' inside 'match' body.");
                    Advance();
                }
            }
            if (Current.Kind == TokenKind.Dedent) Advance();
            return new MatchStatement(subject, cases, new SourceSpan(start, Previous.Span.End));
        }

        private CaseBlock ParseCaseBlock()
        {
            SourcePosition start = Current.Span.Start;
            if (Current.Kind == TokenKind.Name && Current.Text == "case") Advance();
            PatternSyntax pattern = ParsePatterns();
            ExpressionSyntax guard = null;
            if (Current.Kind == TokenKind.KwIf)
            {
                Advance();
                guard = ParseSingleExpression();
            }
            Expect(TokenKind.Colon);
            SuiteSyntax body = ParseSuite();
            return new CaseBlock(pattern, guard, body, new SourceSpan(start, Previous.Span.End));
        }

        // ---------- Simple statements ----------

        private ReturnStatement ParseReturnStatement()
        {
            SourcePosition start = Current.Span.Start;
            if (_functionDepth == 0)
                Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                    "'return' outside function.");
            Expect(TokenKind.KwReturn);
            ExpressionSyntax value = null;
            if (!IsExpressionTerminator(Current.Kind)) value = ParseStarExpressions();
            SourcePosition end = Previous.Span.End;
            ConsumeStatementEnd();
            return new ReturnStatement(value, new SourceSpan(start, end));
        }

        private RaiseStatement ParseRaiseStatement()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwRaise);
            ExpressionSyntax exception = null;
            ExpressionSyntax cause = null;
            if (!IsExpressionTerminator(Current.Kind))
            {
                exception = ParseSingleExpression();
                if (Current.Kind == TokenKind.KwFrom) { Advance(); cause = ParseSingleExpression(); }
            }
            SourcePosition end = Previous.Span.End;
            ConsumeStatementEnd();
            return new RaiseStatement(exception, cause, new SourceSpan(start, end));
        }

        private AssertStatement ParseAssertStatement()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwAssert);
            ExpressionSyntax test = ParseSingleExpression();
            ExpressionSyntax message = null;
            if (Current.Kind == TokenKind.Comma) { Advance(); message = ParseSingleExpression(); }
            SourcePosition end = Previous.Span.End;
            ConsumeStatementEnd();
            return new AssertStatement(test, message, new SourceSpan(start, end));
        }

        private DelStatement ParseDelStatement()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwDel);
            List<ExpressionSyntax> targets = new List<ExpressionSyntax> { ParseSingleExpression() };
            while (Current.Kind == TokenKind.Comma)
            {
                Advance();
                if (IsExpressionTerminator(Current.Kind)) break;
                targets.Add(ParseSingleExpression());
            }
            SourcePosition end = Previous.Span.End;
            ConsumeStatementEnd();
            return new DelStatement(targets, new SourceSpan(start, end));
        }

        private GlobalStatement ParseGlobalStatement()
        {
            SourcePosition start = Current.Span.Start;
            Expect(TokenKind.KwGlobal);
            List<string> names = new List<string> { ExpectName() };
            while (Current.Kind == TokenKind.Comma) { Advance(); names.Add(ExpectName()); }
            SourcePosition end = Previous.Span.End;
            ConsumeStatementEnd();
            return new GlobalStatement(names, new SourceSpan(start, end));
        }

        private NonlocalStatement ParseNonlocalStatement()
        {
            SourcePosition start = Current.Span.Start;
            if (_enclosingFunctionDepth == 0)
                Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                    "nonlocal declaration not allowed at module level.");
            Expect(TokenKind.KwNonlocal);
            List<string> names = new List<string> { ExpectName() };
            while (Current.Kind == TokenKind.Comma) { Advance(); names.Add(ExpectName()); }
            SourcePosition end = Previous.Span.End;
            ConsumeStatementEnd();
            return new NonlocalStatement(names, new SourceSpan(start, end));
        }

        private YieldStatement ParseYieldStatement()
        {
            SourcePosition start = Current.Span.Start;
            YieldExpression ye = (YieldExpression)ParseYieldExpression();
            SourcePosition end = Previous.Span.End;
            ConsumeStatementEnd();
            return new YieldStatement(ye, new SourceSpan(start, end));
        }

        // ---------- Assignment / expression statement ----------

        private StatementSyntax ParseExpressionOrAssignmentStatement()
        {
            SourcePosition start = Current.Span.Start;
            bool startsParenthesized = Current.Kind == TokenKind.LeftParen;
            ExpressionSyntax left = ParseStarExpressions();

            // Augmented assignment
            if (TryTakeAugOp(out BinaryOperator augOp))
            {
                ValidateAssignmentTarget(left, augmented: true);
                ExpressionSyntax rhs = ParseStarExpressions();
                SourcePosition endA = Previous.Span.End;
                ConsumeStatementEnd();
                return new AugmentedAssignmentStatement(left, augOp, rhs, new SourceSpan(start, endA));
            }

            // Annotated assignment: target ':' annotation ['=' value]
            if (Current.Kind == TokenKind.Colon)
            {
                ValidateAssignmentTarget(left, augmented: false);
                Advance();
                ExpressionSyntax annotation = ParseSingleExpression();
                ExpressionSyntax value = null;
                if (Current.Kind == TokenKind.Assign) { Advance(); value = ParseStarExpressions(); }
                SourcePosition endB = Previous.Span.End;
                ConsumeStatementEnd();
                List<ExpressionSyntax> targets = new List<ExpressionSyntax> { left };
                return new AssignmentStatement(targets, value, annotation, new SourceSpan(start, endB));
            }

            // Chained simple assignment: target ('=' target)* '=' value
            if (Current.Kind == TokenKind.Assign)
            {
                List<ExpressionSyntax> targets = new List<ExpressionSyntax> { left };
                while (Current.Kind == TokenKind.Assign)
                {
                    Advance();
                    ExpressionSyntax next = ParseStarExpressions();
                    if (Current.Kind == TokenKind.Assign) targets.Add(next);
                    else
                    {
                        foreach (ExpressionSyntax t in targets) ValidateAssignmentTarget(t, augmented: false);
                        SourcePosition endC = Previous.Span.End;
                        ConsumeStatementEnd();
                        return new AssignmentStatement(targets, next, null, new SourceSpan(start, endC));
                    }
                }
            }

            // Plain expression statement.
            // CPython rejects a bare walrus at statement level unless parenthesized:
            // `x := 13` → "unparenthesized assignment expression"; `(x := 13)` is fine.
            if (left is NamedExpression && !startsParenthesized)
                Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, left.Span,
                    "Unparenthesized assignment expression is not allowed at statement level. Did you mean '='?");

            // Anything left on the line that is not a statement terminator is trailing
            // junk CPython rejects — the classic case being Python-2 `print "hello"`.
            if (!IsStatementTerminatorToken(Current.Kind))
            {
                NameExpression bareName = left as NameExpression;
                if (bareName != null && bareName.Name == "print")
                    Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                        "Missing parentheses in call to 'print'. Did you mean print(...)?");
                else
                    Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, Current.Span,
                        "Invalid syntax: unexpected " + Current.Kind + " after expression.");
                SkipToStatementEnd();
            }

            SourcePosition endD = Previous.Span.End;
            ConsumeStatementEnd();
            return new ExpressionStatement(left, new SourceSpan(start, endD));
        }

        // Valid assignment-target shapes per the Python grammar: Name, attribute,
        // subscript, and (possibly starred / nested) tuple or list of those.
        private void ValidateAssignmentTarget(ExpressionSyntax target, bool augmented)
        {
            if (target == null) return;
            // `(x): int = 1` and `(a, b) = ...` — parentheses are transparent for targets.
            ParenthesizedExpression paren = target as ParenthesizedExpression;
            if (paren != null) { ValidateAssignmentTarget(paren.Inner, augmented); return; }
            if (target is NameExpression || target is AttributeExpression || target is SubscriptExpression) return;
            StarredExpression starred = target as StarredExpression;
            if (starred != null)
            {
                if (augmented)
                    Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, target.Span,
                        "Invalid target for augmented assignment.");
                else
                    ValidateAssignmentTarget(starred.Value, augmented: false);
                return;
            }
            TupleExpression tuple = target as TupleExpression;
            ListExpression list = target as ListExpression;
            if (tuple != null || list != null)
            {
                if (augmented)
                {
                    Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, target.Span,
                        "Invalid target for augmented assignment.");
                    return;
                }
                IReadOnlyList<ExpressionSyntax> elements = tuple != null ? tuple.Elements : list.Elements;
                foreach (ExpressionSyntax el in elements) ValidateAssignmentTarget(el, augmented: false);
                return;
            }
            Report(DiagnosticCodes.InvalidSyntax, ParseSeverity.Error, target.Span,
                "Cannot assign to " + DescribeAssignmentTarget(target) + ".");
        }

        private static string DescribeAssignmentTarget(ExpressionSyntax target)
        {
            if (target is LiteralExpression) return "literal";
            if (target is CallExpression) return "function call";
            if (target is ComparisonExpression) return "comparison";
            if (target is ConditionalExpression) return "conditional expression";
            if (target is LambdaExpression) return "lambda";
            if (target is NamedExpression) return "named expression";
            if (target is BinaryExpression || target is UnaryExpression) return "expression";
            return "expression";
        }

        private static bool IsStatementTerminatorToken(TokenKind k)
        {
            return k == TokenKind.NewLine || k == TokenKind.Semicolon
                || k == TokenKind.EndOfFile || k == TokenKind.Dedent;
        }

        private void SkipToStatementEnd()
        {
            while (Current.Kind != TokenKind.NewLine && Current.Kind != TokenKind.EndOfFile) Advance();
        }

        private bool TryTakeAugOp(out BinaryOperator op)
        {
            switch (Current.Kind)
            {
                case TokenKind.PlusAssign: op = BinaryOperator.Add; Advance(); return true;
                case TokenKind.MinusAssign: op = BinaryOperator.Subtract; Advance(); return true;
                case TokenKind.StarAssign: op = BinaryOperator.Multiply; Advance(); return true;
                case TokenKind.SlashAssign: op = BinaryOperator.Divide; Advance(); return true;
                case TokenKind.DoubleSlashAssign: op = BinaryOperator.FloorDivide; Advance(); return true;
                case TokenKind.PercentAssign: op = BinaryOperator.Modulo; Advance(); return true;
                case TokenKind.AtAssign: op = BinaryOperator.MatMul; Advance(); return true;
                case TokenKind.DoubleStarAssign: op = BinaryOperator.Power; Advance(); return true;
                case TokenKind.AmpAssign: op = BinaryOperator.BitAnd; Advance(); return true;
                case TokenKind.PipeAssign: op = BinaryOperator.BitOr; Advance(); return true;
                case TokenKind.CaretAssign: op = BinaryOperator.BitXor; Advance(); return true;
                case TokenKind.LeftShiftAssign: op = BinaryOperator.LShift; Advance(); return true;
                case TokenKind.RightShiftAssign: op = BinaryOperator.RShift; Advance(); return true;
                default: op = BinaryOperator.Add; return false;
            }
        }
    }
}
