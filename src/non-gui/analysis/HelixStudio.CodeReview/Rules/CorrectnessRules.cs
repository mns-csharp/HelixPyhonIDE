namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// "This code does not do what it says." These are the findings that justify blocking a
    /// change: every one of them is a defect that survives review because it reads correctly.
    /// </summary>
    internal static class CorrectnessRuleSet
    {
        public static IEnumerable<IReviewRule> All()
        {
            yield return new MutationDuringIterationRule();
            yield return new LoopVariableCapturedByClosureRule();
            yield return new ReturnInsideFinallyRule();
            yield return new DuplicateBranchConditionRule();
            yield return new IdenticalBranchBodiesRule();
            yield return new SelfAssignmentRule();
            yield return new FloatEqualityRule();
            yield return new UnreachableCodeRule();
            yield return new SelfComparisonRule();
            yield return new CallInDefaultArgumentRule();
            yield return new DuplicateDefinitionRule();
            yield return new AsyncWithoutAwaitRule();
            yield return new InconsistentReturnRule();
            yield return new TruthyLiteralInConditionRule();
        }
    }

    /// <summary>CR-COR-001 — appending to or removing from the very collection being iterated.</summary>
    internal sealed class MutationDuringIterationRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-001"; } }
        public string Title { get { return "Collection mutated while it is being iterated"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        private static readonly HashSet<string> Mutators = new HashSet<string>(StringComparer.Ordinal)
        {
            "append", "remove", "pop", "insert", "clear", "extend", "add", "discard", "update", "popitem",
        };

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ForStatement loop = node as ForStatement;
                if (loop == null) continue;

                // Only a directly-named collection can be judged: `self.items`, `order.lines` and
                // `items` all resolve to a dotted name, while `for x in list(coll)` (the documented
                // FIX for this bug) and `for x in a.b()` do not — both iterate a fresh object, so
                // DottedNameOf returning null is exactly the exclusion this rule needs.
                string iterated = ReviewHelpers.DottedNameOf(loop.Iter);
                if (iterated == null) continue;

                foreach (CallExpression call in ReviewHelpers.Calls(loop.Body))
                {
                    AttributeExpression member = ReviewHelpers.Unwrap(call.Callee) as AttributeExpression;
                    if (member == null || !Mutators.Contains(member.Name)) continue;
                    if (ReviewHelpers.DottedNameOf(member.Target) != iterated) continue;

                    output.Add(context.Report(call, RuleId, Title, Category,
                        ReviewSeverity.Critical, ReviewConfidence.Medium,
                        "`" + iterated + "." + member.Name + "(...)` mutates `" + iterated +
                        "` inside the `for` loop that is iterating it.",
                        "Python's list and set iterators hold a live index into the container. Adding or " +
                        "removing during iteration shifts the elements underneath that index, so items get " +
                        "skipped or visited twice — and the loop still finishes without an error, which is " +
                        "why this survives testing.",
                        "Iterate a snapshot (`for x in list(" + iterated + "):`) or build the result " +
                        "separately and assign it once the loop is done.",
                        ReviewEffort.Trivial));
                    break;   // one comment per loop is enough to make the point
                }
            }
        }
    }

    /// <summary>CR-COR-002 — a closure defined in a loop that reads the loop variable.</summary>
    internal sealed class LoopVariableCapturedByClosureRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-002"; } }
        public string Title { get { return "Closure captures the loop variable by reference"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ForStatement loop = node as ForStatement;
                if (loop == null) continue;

                HashSet<string> targets = ReviewHelpers.NamesIn(loop.Target);
                if (targets.Count == 0) continue;

                foreach (SyntaxNode inner in ReviewHelpers.SelfAndDescendants(loop.Body))
                {
                    SyntaxNode body;
                    string kind;
                    LambdaExpression lambda = inner as LambdaExpression;
                    FunctionDefSyntax nested = inner as FunctionDefSyntax;
                    if (lambda != null) { body = lambda.Body; kind = "lambda"; }
                    else if (nested != null) { body = nested.Body; kind = "nested `def " + nested.Name + "`"; }
                    else continue;

                    string captured = null;
                    foreach (string name in ReviewHelpers.NamesIn(body))
                        if (targets.Contains(name)) { captured = name; break; }
                    if (captured == null) continue;

                    output.Add(context.Report(inner, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.Medium,
                        "This " + kind + " reads the loop variable `" + captured + "`, which is late-bound.",
                        "Python closes over the VARIABLE, not its value. Every closure created by this loop " +
                        "shares one binding, so once the loop ends they all see the final value — the " +
                        "classic symptom is a list of callbacks that all do the same thing.",
                        "Bind the value at creation time with a default argument (`lambda " + captured +
                        "=" + captured + ": ...`) or `functools.partial`.",
                        ReviewEffort.Trivial));
                }
            }
        }
    }

    /// <summary>CR-COR-003 — a `finally` that returns, discarding an exception in flight.</summary>
    internal sealed class ReturnInsideFinallyRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-003"; } }
        public string Title { get { return "`finally` block exits early, discarding the pending exception"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                TryStatement tryStatement = node as TryStatement;
                if (tryStatement == null || tryStatement.Finally == null) continue;

                FunctionDefSyntax owningFunction = tryStatement.FirstAncestor<FunctionDefSyntax>();
                foreach (SyntaxNode inner in ReviewHelpers.SelfAndDescendants(tryStatement.Finally))
                {
                    string keyword;
                    if (inner is ReturnStatement) keyword = "return";
                    else if (inner is BreakStatement) keyword = "break";
                    else if (inner is ContinueStatement) keyword = "continue";
                    else continue;

                    // A `return` inside a def declared within the finally block belongs to that def,
                    // and a `break` belonging to a loop that is itself inside the finally block is
                    // ordinary control flow. Neither cancels the exception this rule is about.
                    if (!ReferenceEquals(inner.FirstAncestor<FunctionDefSyntax>(), owningFunction)) continue;
                    if (!(inner is ReturnStatement) && LoopInsideFinally(inner, tryStatement.Finally)) continue;

                    output.Add(context.Report(inner, RuleId, Title, Category,
                        ReviewSeverity.Critical, ReviewConfidence.High,
                        "`" + keyword + "` inside `finally` silently cancels any exception that was propagating.",
                        "The `finally` block runs while an exception is still in flight. Leaving it with " +
                        "`" + keyword + "` swallows that exception entirely — the caller sees a normal return " +
                        "and never learns the operation failed. This is one of the hardest failures to " +
                        "diagnose from a log, because there is nothing in the log.",
                        "Move the " + keyword + " out of `finally`, or capture the result and " + keyword +
                        " it after the `try` statement completes.",
                        ReviewEffort.Small));
                    break;
                }
            }
        }

        // True when the break/continue is bound to a loop that lives inside the finally block.
        private static bool LoopInsideFinally(SyntaxNode node, SuiteSyntax finallySuite)
        {
            SyntaxNode current = node.Parent;
            while (current != null && !ReferenceEquals(current, finallySuite))
            {
                if (current is ForStatement || current is WhileStatement) return true;
                current = current.Parent;
            }
            return false;
        }
    }

    /// <summary>CR-COR-004 — the same test twice in one if/elif chain.</summary>
    internal sealed class DuplicateBranchConditionRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-004"; } }
        public string Title { get { return "Duplicate condition in an if/elif chain"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                IfStatement chain = node as IfStatement;
                if (chain == null || IsElifOfAnotherChain(chain)) continue;
                if (chain.Elifs == null || chain.Elifs.Count == 0) continue;

                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                seen[ReviewHelpers.StructuralFingerprint(chain.Test)] = chain.Test != null ? chain.Test.Span.Start.Line : chain.Span.Start.Line;

                foreach (IfStatement elif in chain.Elifs)
                {
                    string fingerprint = ReviewHelpers.StructuralFingerprint(elif.Test);
                    int firstLine;
                    if (seen.TryGetValue(fingerprint, out firstLine))
                    {
                        output.Add(context.Report(elif.Test ?? (SyntaxNode)elif, RuleId, Title, Category,
                            ReviewSeverity.Major, ReviewConfidence.High,
                            "This `elif` repeats the condition already tested on line " + firstLine + ".",
                            "Only the first matching branch of an if/elif chain ever runs, so this branch is " +
                            "unreachable. Either the condition was meant to differ (a copy-paste that was not " +
                            "finished) or the branch is dead code claiming to handle a case it never sees.",
                            "Correct the condition, or delete the branch if the first one already handles the case.",
                            ReviewEffort.Trivial));
                    }
                    else
                    {
                        seen[fingerprint] = elif.Test != null ? elif.Test.Span.Start.Line : elif.Span.Start.Line;
                    }
                }
            }
        }

        internal static bool IsElifOfAnotherChain(IfStatement statement)
        {
            IfStatement parent = statement.Parent as IfStatement;
            if (parent == null || parent.Elifs == null) return false;
            foreach (IfStatement elif in parent.Elifs)
                if (ReferenceEquals(elif, statement)) return true;
            return false;
        }
    }

    /// <summary>CR-COR-005 — both arms of a branch do exactly the same thing.</summary>
    internal sealed class IdenticalBranchBodiesRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-005"; } }
        public string Title { get { return "Both branches of the condition run identical code"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                IfStatement statement = node as IfStatement;
                if (statement == null || statement.Else == null) continue;
                if (statement.Elifs != null && statement.Elifs.Count > 0) continue;   // a chain is a different shape
                // A one-line `pass` on either side is a deliberate placeholder, not a duplicate.
                if (IsTrivialSuite(statement.Body) || IsTrivialSuite(statement.Else)) continue;

                if (ReviewHelpers.StructuralFingerprint(statement.Body) !=
                    ReviewHelpers.StructuralFingerprint(statement.Else)) continue;

                output.Add(context.Report(statement, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.High,
                    "The `if` and `else` bodies are structurally identical, so the condition changes nothing.",
                    "Either the condition is redundant and the branch should go, or one of the two arms was " +
                    "supposed to differ and the edit was never made. Both readings are worth a comment: the " +
                    "code as written spends a branch to reach the same outcome.",
                    "Delete the branch and keep one copy of the body — or fix the arm that was meant to differ.",
                    ReviewEffort.Small));
            }
        }

        private static bool IsTrivialSuite(SuiteSyntax suite)
        {
            IReadOnlyList<StatementSyntax> statements = ReviewHelpers.StatementsOf(suite);
            return statements.Count == 0 || (statements.Count == 1 && statements[0] is PassStatement);
        }
    }

    /// <summary>CR-COR-006 — `x = x`.</summary>
    internal sealed class SelfAssignmentRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-006"; } }
        public string Title { get { return "Assignment has no effect"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                AssignmentStatement assignment = node as AssignmentStatement;
                if (assignment == null || assignment.Value == null) continue;
                if (assignment.Targets == null || assignment.Targets.Count != 1) continue;
                if (assignment.Annotation != null) continue;    // `x: int = x` is a (re)declaration

                string target = ReviewHelpers.DottedNameOf(assignment.Targets[0]);
                string value = ReviewHelpers.DottedNameOf(assignment.Value);
                if (target == null || value == null || target != value) continue;

                output.Add(context.Report(assignment, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.High,
                    "`" + target + " = " + value + "` assigns the value to itself.",
                    "This is almost always a typo for a different name on one side — commonly a constructor " +
                    "meant to write `self." + ReviewHelpers.LastSegment(target) + " = " +
                    ReviewHelpers.LastSegment(target) + "`, leaving the attribute never set and the failure " +
                    "surfacing far from here as a missing attribute.",
                    "Check which side was meant to be different; if the statement really is redundant, delete it.",
                    ReviewEffort.Trivial));
            }
        }
    }

    /// <summary>CR-COR-007 — equality against a float literal.</summary>
    internal sealed class FloatEqualityRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-007"; } }
        public string Title { get { return "Floating-point value compared for exact equality"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ComparisonExpression comparison = node as ComparisonExpression;
                if (comparison == null || comparison.Operators == null) continue;

                for (int i = 0; i < comparison.Operators.Count; i++)
                {
                    ComparisonOperator op = comparison.Operators[i];
                    if (op != ComparisonOperator.Eq && op != ComparisonOperator.NotEq) continue;

                    ExpressionSyntax left = i == 0 ? comparison.Left : comparison.Comparators[i - 1];
                    ExpressionSyntax right = comparison.Comparators[i];
                    bool floatSide = ReviewHelpers.IsFloatLiteral(left) || ReviewHelpers.IsFloatLiteral(right);
                    if (!floatSide) continue;
                    // `x == 0.0` is a legitimate exact test (zero is representable); the trap is
                    // every other value, which is not.
                    if (IsZeroLiteral(left) || IsZeroLiteral(right)) continue;

                    output.Add(context.Report(comparison, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.High,
                        "Comparing a float with `" + (op == ComparisonOperator.Eq ? "==" : "!=") +
                        "` against a literal that binary floating point cannot represent exactly.",
                        "The literal is rounded to the nearest double on the way in, and any arithmetic that " +
                        "produced the other side rounds differently. The test then fails for values that are " +
                        "correct to fifteen digits — intermittently, and usually only on real data.",
                        "Compare with a tolerance: `math.isclose(a, b, rel_tol=1e-9)`, or `abs(a - b) < eps`.",
                        ReviewEffort.Trivial));
                    break;
                }
            }
        }

        private static bool IsZeroLiteral(ExpressionSyntax expression)
        {
            string text = ReviewHelpers.NumberLiteralText(expression);
            if (text == null) return false;
            double value;
            return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) && value == 0.0;
        }
    }

    /// <summary>CR-COR-008 — statements after a return/raise/break/continue in the same suite.</summary>
    internal sealed class UnreachableCodeRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-008"; } }
        public string Title { get { return "Unreachable code"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                SuiteSyntax suite = node as SuiteSyntax;
                if (suite == null) continue;
                IReadOnlyList<StatementSyntax> statements = ReviewHelpers.StatementsOf(suite);

                for (int i = 0; i < statements.Count - 1; i++)
                {
                    if (!ReviewHelpers.TerminatesFlow(statements[i])) continue;
                    StatementSyntax dead = statements[i + 1];
                    string keyword = KeywordOf(statements[i]);

                    output.Add(context.Report(dead, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.High,
                        "Nothing here can run: line " + statements[i].Span.Start.Line + " already leaves the block with `" + keyword + "`.",
                        "Dead code is worse than no code — it reads as behaviour the program has, so the next " +
                        "reader reasons about a path that does not exist, and a fix applied here changes nothing.",
                        "Delete it, or move it above the `" + keyword + "` if it was meant to run.",
                        ReviewEffort.Trivial));
                    break;   // everything after the first dead statement is the same finding
                }
            }
        }

        private static string KeywordOf(StatementSyntax statement)
        {
            if (statement is ReturnStatement) return "return";
            if (statement is RaiseStatement) return "raise";
            if (statement is BreakStatement) return "break";
            return "continue";
        }
    }

    /// <summary>CR-COR-009 — `x == x`.</summary>
    internal sealed class SelfComparisonRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-009"; } }
        public string Title { get { return "Value compared against itself"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ComparisonExpression comparison = node as ComparisonExpression;
                if (comparison == null || comparison.Operators == null || comparison.Operators.Count != 1) continue;

                ComparisonOperator op = comparison.Operators[0];
                if (op == ComparisonOperator.In || op == ComparisonOperator.NotIn) continue;

                string left = ReviewHelpers.DottedNameOf(comparison.Left);
                string right = ReviewHelpers.DottedNameOf(comparison.Comparators[0]);
                if (left == null || right == null || left != right) continue;

                // `x != x` is the documented, intentional NaN test — do not flag the one case
                // where self-comparison is the correct thing to write.
                bool nanIdiom = op == ComparisonOperator.NotEq;

                output.Add(context.Report(comparison, RuleId, Title, Category,
                    nanIdiom ? ReviewSeverity.Info : ReviewSeverity.Major,
                    nanIdiom ? ReviewConfidence.Low : ReviewConfidence.High,
                    "`" + left + "` is compared against itself.",
                    nanIdiom
                        ? "`x != x` is the standard NaN test, so this may be deliberate — but it reads as a typo " +
                          "to everyone who has not seen the idiom before."
                        : "The result is constant, so the branch it guards either always runs or never does. " +
                          "One of the two operands is almost certainly the wrong name.",
                    nanIdiom
                        ? "If a NaN check is intended, say so with `math.isnan(" + left + ")`."
                        : "Correct the operand that was meant to be a different value.",
                    ReviewEffort.Trivial));
            }
        }
    }

    /// <summary>CR-COR-010 — a default argument that calls something.</summary>
    internal sealed class CallInDefaultArgumentRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-010"; } }
        public string Title { get { return "Default argument is evaluated once, at import time"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        // Frameworks whose whole design is a call in the default slot, evaluated once on purpose.
        private static readonly HashSet<string> DeclarativeDefaults = new HashSet<string>(StringComparer.Ordinal)
        {
            "field", "Field", "Depends", "Query", "Body", "Header", "Cookie", "Path", "Form", "File",
        };

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                foreach (ParameterSyntax parameter in function.Node.Parameters)
                {
                    CallExpression call = ReviewHelpers.Unwrap(parameter.DefaultValue) as CallExpression;
                    if (call == null) continue;
                    string callee = ReviewHelpers.LastSegment(ReviewHelpers.CalleeName(call));
                    if (callee != null && DeclarativeDefaults.Contains(callee)) continue;

                    output.Add(context.Report(parameter, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.Medium,
                        "The default for `" + parameter.Name + "` calls `" + (callee ?? "a function") +
                        "`, which runs once when the `def` is executed — not on each call.",
                        "Every caller that omits the argument gets the SAME value the module got at import " +
                        "time. For a timestamp that means every record is stamped with the process start " +
                        "time; for a mutable result it means callers share one object and each other's edits.",
                        "Default to `None` and compute inside the body: " +
                        "`if " + parameter.Name + " is None: " + parameter.Name + " = " + (callee ?? "...") + "()`.",
                        ReviewEffort.Trivial));
                }
            }
        }
    }

    /// <summary>CR-COR-011 — the same name defined twice in one scope.</summary>
    internal sealed class DuplicateDefinitionRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-011"; } }
        public string Title { get { return "Definition is immediately replaced by a later one"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (context.Module != null) Scan(context, context.Module.Statements, "module", output);
            foreach (ReviewClass cls in context.Classes)
                Scan(context, ReviewHelpers.StatementsOf(cls.Node.Body), "class `" + cls.Name + "`", output);
        }

        private void Scan(ReviewContext context, IReadOnlyList<StatementSyntax> statements,
            string scope, List<ReviewFinding> output)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (StatementSyntax statement in statements)
            {
                string name;
                bool decorated;
                FunctionDefSyntax function = statement as FunctionDefSyntax;
                ClassDefSyntax cls = statement as ClassDefSyntax;
                if (function != null) { name = function.Name; decorated = function.Decorators.Count > 0; }
                else if (cls != null) { name = cls.Name; decorated = cls.Decorators.Count > 0; }
                else continue;

                // `@property` + `@x.setter`, `@typing.overload` and `@singledispatch.register` all
                // legitimately repeat a name; a decorated definition is never judged here.
                if (decorated) { seen.Remove(name); continue; }

                int firstLine;
                if (seen.TryGetValue(name, out firstLine))
                {
                    output.Add(context.Report(statement, RuleId, Title, Category,
                        ReviewSeverity.Critical, ReviewConfidence.High,
                        "`" + name + "` was already defined at line " + firstLine + " in this " + scope + ".",
                        "The second definition silently replaces the first, so the earlier body is dead and " +
                        "any caller expecting it gets different behaviour. In a class this usually means a " +
                        "method was duplicated during a merge and only one copy was updated.",
                        "Delete or rename whichever definition is not the intended one.",
                        ReviewEffort.Small));
                }
                seen[name] = statement.Span.Start.Line;
            }
        }
    }

    /// <summary>CR-COR-012 — `async def` with nothing asynchronous in it.</summary>
    internal sealed class AsyncWithoutAwaitRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-012"; } }
        public string Title { get { return "`async def` never awaits"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (!function.Node.IsAsync || function.Node.Body == null) continue;
                if (function.Node.Decorators.Count > 0) continue;   // a decorator may supply the async behaviour

                bool asynchronous = false;
                foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(function.Node.Body))
                {
                    UnaryExpression unary = node as UnaryExpression;
                    if (unary != null && unary.Operator == UnaryOperator.Await) { asynchronous = true; break; }
                    ForStatement forStatement = node as ForStatement;
                    if (forStatement != null && forStatement.IsAsync) { asynchronous = true; break; }
                    WithStatement withStatement = node as WithStatement;
                    if (withStatement != null && withStatement.IsAsync) { asynchronous = true; break; }
                    if (node is YieldExpression || node is YieldStatement) { asynchronous = true; break; }
                }
                if (asynchronous) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.Medium,
                    "`" + function.QualifiedName + "` is declared `async` but contains no `await`.",
                    "Calling it returns a coroutine rather than running it, so a caller that forgets `await` " +
                    "gets a coroutine object where it expected a result — and nothing raises. If the body is " +
                    "genuinely synchronous, the `async` also blocks the event loop for its whole duration.",
                    "Drop the `async`, or await the call this function was meant to be waiting on.",
                    ReviewEffort.Trivial));
            }
        }
    }

    /// <summary>CR-COR-013 — some paths return a value, others return nothing.</summary>
    internal sealed class InconsistentReturnRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-013"; } }
        public string Title { get { return "Function returns a value on some paths and nothing on others"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (function.Node.Body == null) continue;

                int withValue = 0, bare = 0, bareLine = 0;
                foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(function.Node.Body))
                {
                    ReturnStatement ret = node as ReturnStatement;
                    if (ret == null) continue;
                    if (ret.Value != null) withValue++;
                    else { bare++; if (bareLine == 0) bareLine = ret.Span.Start.Line; }
                }
                if (withValue == 0 || bare == 0) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.Medium,
                    "`" + function.QualifiedName + "` has " + withValue + " `return <value>` statement(s) and a bare `return` on line " + bareLine + ".",
                    "The bare return yields `None`, so callers receive a value of a different shape depending " +
                    "on which path ran. The failure lands at the caller — an attribute lookup on `None` — with " +
                    "no indication of which branch produced it.",
                    "Return an explicit value on every path (`return None` is fine when that is the intent), " +
                    "or raise instead of returning nothing.",
                    ReviewEffort.Small));
            }
        }
    }

    /// <summary>CR-COR-014 — `if x == 1 or 2:`.</summary>
    internal sealed class TruthyLiteralInConditionRule : IReviewRule
    {
        public string RuleId { get { return "CR-COR-014"; } }
        public string Title { get { return "Literal operand makes the condition constant"; } }
        public ReviewCategory Category { get { return ReviewCategory.Correctness; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                BinaryExpression binary = node as BinaryExpression;
                if (binary == null) continue;
                if (binary.Operator != BinaryOperator.Or && binary.Operator != BinaryOperator.And) continue;

                ExpressionSyntax offender = BareValueLiteral(binary.Right) ?? BareValueLiteral(binary.Left);
                if (offender == null) continue;
                // Only a real condition matters: `timeout = arg or 30` is the idiomatic default.
                if (!IsUsedAsCondition(binary)) continue;

                string word = binary.Operator == BinaryOperator.Or ? "or" : "and";
                output.Add(context.Report(binary, RuleId, Title, Category,
                    ReviewSeverity.Critical, ReviewConfidence.High,
                    "`" + word + " " + context.LineText(offender.Span.Start.Line) + "` — the literal is truthy on its own, " +
                    "so this condition does not test what it appears to.",
                    "`x == 1 or 2` parses as `(x == 1) or 2`, and a non-zero literal is always truthy: the " +
                    "whole expression is true for every value of x. The intended reading (`x in (1, 2)`) is " +
                    "what a reader sees, which is why this survives review.",
                    "Write the membership test explicitly: `x in (1, 2)`, or repeat the comparison on both sides.",
                    ReviewEffort.Trivial));
            }
        }

        // A number/string literal used as a bare operand — not None/True/False, which read as
        // deliberate sentinels rather than an unfinished comparison.
        private static ExpressionSyntax BareValueLiteral(ExpressionSyntax expression)
        {
            LiteralExpression literal = ReviewHelpers.Unwrap(expression) as LiteralExpression;
            if (literal == null || literal.Token == null) return null;
            bool valueLiteral = literal.Token.Kind == TokenKind.Number || literal.Token.Kind == TokenKind.String;
            return valueLiteral ? (ExpressionSyntax)literal : null;
        }

        private static bool IsUsedAsCondition(SyntaxNode node)
        {
            SyntaxNode current = node;
            while (current != null)
            {
                SyntaxNode parent = current.Parent;
                IfStatement ifStatement = parent as IfStatement;
                if (ifStatement != null && ReferenceEquals(ifStatement.Test, current)) return true;
                WhileStatement whileStatement = parent as WhileStatement;
                if (whileStatement != null && ReferenceEquals(whileStatement.Test, current)) return true;
                AssertStatement assertStatement = parent as AssertStatement;
                if (assertStatement != null && ReferenceEquals(assertStatement.Test, current)) return true;
                // Keep climbing only through parenthesisation and further boolean operators.
                if (!(parent is ParenthesizedExpression) && !(parent is BinaryExpression)) return false;
                current = parent;
            }
            return false;
        }
    }
}
