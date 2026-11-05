namespace HelixPythonNamespace.CodeReview
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// The AST predicates the rule library shares. Everything here is deliberately conservative:
    /// a helper that cannot answer returns null/false rather than guessing, because a wrong
    /// answer here becomes a wrong review comment in forty places at once.
    /// </summary>
    public static class ReviewHelpers
    {
        // ------------------------------------------------------------------ expressions

        /// <summary>Strips redundant parentheses so callers can pattern-match the real node.</summary>
        public static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        {
            ParenthesizedExpression paren = expression as ParenthesizedExpression;
            while (paren != null)
            {
                expression = paren.Inner;
                paren = expression as ParenthesizedExpression;
            }
            return expression;
        }

        /// <summary>
        /// The dotted source spelling of a name/attribute chain — <c>os.path.join</c> for
        /// <c>os.path.join(...)</c>'s callee. Null when the chain is rooted in something that is
        /// not a plain name (a subscript, a call result), because those cannot be matched by name.
        /// </summary>
        public static string DottedNameOf(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);
            NameExpression name = expression as NameExpression;
            if (name != null) return name.Name;

            DottedName dotted = expression as DottedName;
            if (dotted != null) return dotted.AsString;

            AttributeExpression attribute = expression as AttributeExpression;
            if (attribute != null)
            {
                string target = DottedNameOf(attribute.Target);
                return target == null ? null : target + "." + attribute.Name;
            }
            return null;
        }

        /// <summary>The dotted callee of a call, or null when it cannot be named.</summary>
        public static string CalleeName(CallExpression call)
        {
            return call == null ? null : DottedNameOf(call.Callee);
        }

        /// <summary>The last segment of a dotted name (<c>join</c> from <c>os.path.join</c>).</summary>
        public static string LastSegment(string dotted)
        {
            if (string.IsNullOrEmpty(dotted)) return dotted;
            int i = dotted.LastIndexOf('.');
            return i < 0 ? dotted : dotted.Substring(i + 1);
        }

        /// <summary>The first segment of a dotted name (<c>os</c> from <c>os.path.join</c>).</summary>
        public static string RootSegment(string dotted)
        {
            if (string.IsNullOrEmpty(dotted)) return dotted;
            int i = dotted.IndexOf('.');
            return i < 0 ? dotted : dotted.Substring(0, i);
        }

        /// <summary>How many attribute hops a chain has: <c>a.b.c.d</c> is 3.</summary>
        public static int AttributeChainDepth(ExpressionSyntax expression)
        {
            int depth = 0;
            AttributeExpression attribute = Unwrap(expression) as AttributeExpression;
            while (attribute != null)
            {
                depth++;
                attribute = Unwrap(attribute.Target) as AttributeExpression;
            }
            return depth;
        }

        /// <summary>The value of a plain string literal, or null for anything else.</summary>
        public static string StringLiteralValue(ExpressionSyntax expression)
        {
            LiteralExpression literal = Unwrap(expression) as LiteralExpression;
            if (literal == null || literal.Token == null) return null;
            if (literal.Token.Kind != TokenKind.String) return null;
            string value = literal.Token.Value as string;
            return value ?? StripQuotes(literal.Token.Text);
        }

        /// <summary>True for a literal the interpreter evaluates with no work — safe as an <c>eval</c> argument.</summary>
        public static bool IsConstantLiteral(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);
            if (expression is LiteralExpression || expression is EllipsisExpression) return true;
            TupleExpression tuple = expression as TupleExpression;
            if (tuple != null)
            {
                foreach (ExpressionSyntax e in tuple.Elements)
                    if (!IsConstantLiteral(e)) return false;
                return true;
            }
            return false;
        }

        public static bool IsNoneLiteral(ExpressionSyntax expression)
        {
            LiteralExpression literal = Unwrap(expression) as LiteralExpression;
            return literal != null && literal.Token != null && literal.Token.Kind == TokenKind.KwNone;
        }

        public static bool IsBooleanLiteral(ExpressionSyntax expression)
        {
            LiteralExpression literal = Unwrap(expression) as LiteralExpression;
            return literal != null && literal.Token != null &&
                (literal.Token.Kind == TokenKind.KwTrue || literal.Token.Kind == TokenKind.KwFalse);
        }

        /// <summary>The numeric text of a number literal (as written), or null.</summary>
        public static string NumberLiteralText(ExpressionSyntax expression)
        {
            LiteralExpression literal = Unwrap(expression) as LiteralExpression;
            if (literal == null || literal.Token == null || literal.Token.Kind != TokenKind.Number) return null;
            return literal.Token.Text;
        }

        /// <summary>True when the number literal is written with a decimal point or an exponent.</summary>
        public static bool IsFloatLiteral(ExpressionSyntax expression)
        {
            string text = NumberLiteralText(expression);
            if (text == null) return false;
            return text.IndexOf('.') >= 0 ||
                (text.IndexOf('e') >= 0 && text.IndexOf("0x", StringComparison.OrdinalIgnoreCase) < 0) ||
                (text.IndexOf('E') >= 0 && text.IndexOf("0x", StringComparison.OrdinalIgnoreCase) < 0);
        }

        public static string StripQuotes(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int start = 0;
            // Skip an r/b/f/u prefix (possibly two characters, e.g. rb"...").
            while (start < text.Length && text[start] != '"' && text[start] != '\'' && start < 2) start++;
            if (start >= text.Length) return text;
            char quote = text[start];
            if (quote != '"' && quote != '\'') return text;
            int quoteRun = 1;
            if (start + 2 < text.Length && text[start + 1] == quote && text[start + 2] == quote) quoteRun = 3;
            int bodyStart = start + quoteRun;
            int bodyEnd = text.Length - quoteRun;
            if (bodyEnd <= bodyStart) return string.Empty;
            return text.Substring(bodyStart, bodyEnd - bodyStart);
        }

        // ------------------------------------------------------------------- traversal

        /// <summary>The node and everything under it, in preorder.</summary>
        public static IEnumerable<SyntaxNode> SelfAndDescendants(SyntaxNode node)
        {
            if (node == null) yield break;
            yield return node;
            foreach (SyntaxNode d in node.Descendants())
                if (d != null) yield return d;
        }

        /// <summary>Every call anywhere under <paramref name="node"/>.</summary>
        public static IEnumerable<CallExpression> Calls(SyntaxNode node)
        {
            foreach (SyntaxNode n in SelfAndDescendants(node))
            {
                CallExpression call = n as CallExpression;
                if (call != null) yield return call;
            }
        }

        /// <summary>
        /// Every node under <paramref name="node"/> that is NOT inside a nested def/class/lambda.
        /// Rules about "this function's body" want this: a closure's statements belong to the
        /// closure, and counting them against the outer def reports the wrong member.
        /// </summary>
        public static IEnumerable<SyntaxNode> DescendantsInSameScope(SyntaxNode node)
        {
            if (node == null) yield break;
            var stack = new Stack<SyntaxNode>();
            foreach (SyntaxNode child in node.Children) if (child != null) stack.Push(child);
            while (stack.Count > 0)
            {
                SyntaxNode current = stack.Pop();
                if (current is FunctionDefSyntax || current is ClassDefSyntax || current is LambdaExpression)
                    continue;
                yield return current;
                foreach (SyntaxNode child in current.Children) if (child != null) stack.Push(child);
            }
        }

        /// <summary>Names read or written anywhere under a node.</summary>
        public static HashSet<string> NamesIn(SyntaxNode node)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (SyntaxNode n in SelfAndDescendants(node))
            {
                NameExpression name = n as NameExpression;
                if (name != null && name.Name.Length > 0) names.Add(name.Name);
            }
            return names;
        }

        /// <summary>Statements directly in a suite; never null.</summary>
        public static IReadOnlyList<StatementSyntax> StatementsOf(SuiteSyntax suite)
        {
            return suite != null && suite.Statements != null
                ? suite.Statements
                : (IReadOnlyList<StatementSyntax>)Array.Empty<StatementSyntax>();
        }

        /// <summary>Statements in a suite and everything nested inside it, excluding nested defs/classes.</summary>
        public static int StatementCount(SuiteSyntax suite)
        {
            int count = 0;
            foreach (StatementSyntax s in StatementsOf(suite))
            {
                if (s is FunctionDefSyntax || s is ClassDefSyntax) continue;
                count++;
                foreach (SyntaxNode n in DescendantsInSameScope(s))
                    if (n is StatementSyntax) count++;
            }
            return count;
        }

        /// <summary>Deepest run of nested block statements inside a suite (its own level is 0).</summary>
        public static int MaxNestingDepth(SuiteSyntax suite)
        {
            int max = 0;
            foreach (StatementSyntax s in StatementsOf(suite))
                max = Math.Max(max, NestingDepthOf(s, 0));
            return max;
        }

        private static int NestingDepthOf(StatementSyntax statement, int depth)
        {
            if (statement is FunctionDefSyntax || statement is ClassDefSyntax) return depth;

            SuiteSyntax[] bodies = BlockBodiesOf(statement);
            if (bodies == null || bodies.Length == 0) return depth;

            int max = depth + 1;
            foreach (SuiteSyntax body in bodies)
            {
                foreach (StatementSyntax inner in StatementsOf(body))
                    max = Math.Max(max, NestingDepthOf(inner, depth + 1));
            }
            return max;
        }

        /// <summary>The suites a compound statement owns, or null when it is a simple statement.</summary>
        public static SuiteSyntax[] BlockBodiesOf(StatementSyntax statement)
        {
            IfStatement ifStatement = statement as IfStatement;
            if (ifStatement != null)
            {
                var list = new List<SuiteSyntax> { ifStatement.Body };
                foreach (IfStatement elif in ifStatement.Elifs) list.Add(elif.Body);
                if (ifStatement.Else != null) list.Add(ifStatement.Else);
                return list.ToArray();
            }
            ForStatement forStatement = statement as ForStatement;
            if (forStatement != null)
                return forStatement.Else != null
                    ? new[] { forStatement.Body, forStatement.Else }
                    : new[] { forStatement.Body };
            WhileStatement whileStatement = statement as WhileStatement;
            if (whileStatement != null)
                return whileStatement.Else != null
                    ? new[] { whileStatement.Body, whileStatement.Else }
                    : new[] { whileStatement.Body };
            WithStatement withStatement = statement as WithStatement;
            if (withStatement != null) return new[] { withStatement.Body };
            TryStatement tryStatement = statement as TryStatement;
            if (tryStatement != null)
            {
                var list = new List<SuiteSyntax> { tryStatement.Body };
                foreach (ExceptHandler handler in tryStatement.Handlers) list.Add(handler.Body);
                if (tryStatement.Else != null) list.Add(tryStatement.Else);
                if (tryStatement.Finally != null) list.Add(tryStatement.Finally);
                return list.ToArray();
            }
            MatchStatement matchStatement = statement as MatchStatement;
            if (matchStatement != null)
            {
                var list = new List<SuiteSyntax>();
                foreach (CaseBlock c in matchStatement.Cases) list.Add(c.Body);
                return list.ToArray();
            }
            return null;
        }

        /// <summary>
        /// McCabe cyclomatic complexity of a def: one, plus one per independent branch. Nested
        /// defs are excluded — they are separately reported functions with their own numbers.
        /// </summary>
        public static int CyclomaticComplexity(FunctionDefSyntax function)
        {
            if (function == null || function.Body == null) return 1;
            int complexity = 1;
            foreach (SyntaxNode n in DescendantsInSameScope(function.Body))
            {
                if (n is IfStatement) complexity++;                 // elifs are nested IfStatements
                else if (n is ForStatement) complexity++;
                else if (n is WhileStatement) complexity++;
                else if (n is ExceptHandler) complexity++;
                else if (n is CaseBlock) complexity++;
                else if (n is AssertStatement) complexity++;
                else if (n is ConditionalExpression) complexity++;
                else if (n is ComprehensionClause) complexity++;
                else
                {
                    BinaryExpression binary = n as BinaryExpression;
                    if (binary != null && (binary.Operator == BinaryOperator.And || binary.Operator == BinaryOperator.Or))
                        complexity++;
                }
            }
            return complexity;
        }

        /// <summary>True for return/raise/break/continue — a statement nothing after it can follow.</summary>
        public static bool TerminatesFlow(StatementSyntax statement)
        {
            return statement is ReturnStatement || statement is RaiseStatement ||
                   statement is BreakStatement || statement is ContinueStatement;
        }

        // ---------------------------------------------------------------- declarations

        public static IReadOnlyList<string> DecoratorNames(IReadOnlyList<DecoratorSyntax> decorators)
        {
            var names = new List<string>();
            if (decorators == null) return names;
            foreach (DecoratorSyntax d in decorators)
            {
                if (d == null || d.Expression == null) continue;
                ExpressionSyntax expression = Unwrap(d.Expression);
                CallExpression call = expression as CallExpression;
                if (call != null) expression = call.Callee;
                string name = DottedNameOf(expression);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            return names;
        }

        public static bool HasDecorator(FunctionDefSyntax function, params string[] names)
        {
            if (function == null) return false;
            foreach (string decorator in DecoratorNames(function.Decorators))
            {
                string last = LastSegment(decorator);
                foreach (string wanted in names)
                    if (string.Equals(decorator, wanted, StringComparison.Ordinal) ||
                        string.Equals(last, wanted, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Parameters excluding an implicit <c>self</c>/<c>cls</c> receiver.</summary>
        public static List<ParameterSyntax> DeclaredParameters(FunctionDefSyntax function, bool isMethod)
        {
            var result = new List<ParameterSyntax>();
            if (function == null) return result;
            bool first = true;
            foreach (ParameterSyntax p in function.Parameters)
            {
                if (first)
                {
                    first = false;
                    if (isMethod && (p.Name == "self" || p.Name == "cls")) continue;
                }
                result.Add(p);
            }
            return result;
        }

        /// <summary>A name Python treats as non-public by convention.</summary>
        public static bool IsPublicName(string name)
        {
            return !string.IsNullOrEmpty(name) && name[0] != '_';
        }

        public static bool IsDunder(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Length > 4 &&
                   name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal);
        }

        /// <summary>
        /// A path that holds test code. Matches the layouts the repository's own test projects use
        /// (<c>tests/</c>, <c>test_x.py</c>, <c>x_test.py</c>, <c>conftest.py</c>) — several rules
        /// invert inside tests, so a wrong answer here silences real findings.
        /// </summary>
        public static bool LooksLikeTestPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string normalized = path.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            string file = slash >= 0 ? normalized.Substring(slash + 1) : normalized;

            if (file.StartsWith("test_", StringComparison.OrdinalIgnoreCase)) return true;
            if (file.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(file, "conftest.py", StringComparison.OrdinalIgnoreCase)) return true;

            string lower = normalized.ToLowerInvariant();
            return lower.Contains("/tests/") || lower.Contains("/test/") ||
                   lower.StartsWith("tests/", StringComparison.Ordinal) ||
                   lower.StartsWith("test/", StringComparison.Ordinal);
        }

        /// <summary>A def the test-runner protocol collects.</summary>
        public static bool IsTestFunction(FunctionDefSyntax function)
        {
            if (function == null) return false;
            if (function.Name.StartsWith("test_", StringComparison.Ordinal) ||
                string.Equals(function.Name, "test", StringComparison.Ordinal)) return true;
            return HasDecorator(function, "fixture", "pytest.fixture");
        }

        // ----------------------------------------------------------------- fingerprint

        /// <summary>
        /// A structural fingerprint of a statement: node kinds, operators and literal kinds, with
        /// identifiers normalised away. Copy-pasted code survives a rename, so matching on
        /// structure is what actually finds duplication — and normalising the names is also why
        /// the duplicate rule needs a long run of statements before it says anything.
        /// </summary>
        public static string StructuralFingerprint(SyntaxNode node)
        {
            var builder = new StringBuilder();
            AppendFingerprint(node, builder);
            return builder.ToString();
        }

        private static void AppendFingerprint(SyntaxNode node, StringBuilder builder)
        {
            if (node == null) { builder.Append("_;"); return; }

            builder.Append(node.GetType().Name);

            BinaryExpression binary = node as BinaryExpression;
            if (binary != null) builder.Append('#').Append((int)binary.Operator);

            UnaryExpression unary = node as UnaryExpression;
            if (unary != null) builder.Append('#').Append((int)unary.Operator);

            ComparisonExpression comparison = node as ComparisonExpression;
            if (comparison != null && comparison.Operators != null)
                foreach (ComparisonOperator op in comparison.Operators) builder.Append('#').Append((int)op);

            LiteralExpression literal = node as LiteralExpression;
            if (literal != null && literal.Token != null) builder.Append('#').Append((int)literal.Token.Kind);

            AttributeExpression attribute = node as AttributeExpression;
            if (attribute != null) builder.Append('#').Append(attribute.Name);   // member names are structure

            builder.Append('(');
            foreach (SyntaxNode child in node.Children) AppendFingerprint(child, builder);
            builder.Append(')');
        }
    }
}
