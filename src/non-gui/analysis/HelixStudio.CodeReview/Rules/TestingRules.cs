namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// Whether the tests actually test anything. A test that cannot fail is worse than no test:
    /// it is counted as coverage, so nobody writes the real one.
    /// </summary>
    internal static class TestingRuleSet
    {
        public static IEnumerable<IReviewRule> All()
        {
            yield return new TestWithoutAssertionRule();
            yield return new UnconditionalSkipRule();
            yield return new SleepInTestRule();
        }
    }

    /// <summary>CR-TST-001 — a test with nothing that can fail.</summary>
    internal sealed class TestWithoutAssertionRule : IReviewRule
    {
        public string RuleId { get { return "CR-TST-001"; } }
        public string Title { get { return "Test contains no assertion"; } }
        public ReviewCategory Category { get { return ReviewCategory.Testing; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (!context.IsTestFile) return;

            foreach (ReviewFunction function in context.Functions)
            {
                if (!function.Name.StartsWith("test", StringComparison.Ordinal)) continue;
                if (ReviewHelpers.HasDecorator(function.Node, "fixture", "skip", "xfail")) continue;
                if (function.Node.Body == null) continue;
                if (HasAssertion(function.Node.Body)) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.Medium,
                    "`" + function.QualifiedName + "` runs code but never asserts anything.",
                    "It passes as long as nothing raises, so it only tests that the code does not crash — and " +
                    "it will keep passing after the behaviour it was written for is broken. The coverage " +
                    "report still counts every line it touched, which is what makes this hard to notice.",
                    "Assert the outcome the test exists to protect. If \"does not raise\" really is the " +
                    "property, say so — `pytest.raises` for the negative case, or a comment stating the intent.",
                    ReviewEffort.Small));
            }
        }

        private static bool HasAssertion(SuiteSyntax body)
        {
            foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(body))
            {
                if (node is AssertStatement) return true;
                if (node is RaiseStatement) return true;              // a test that raises on failure

                CallExpression call = node as CallExpression;
                if (call != null)
                {
                    string callee = ReviewHelpers.CalleeName(call);
                    string leaf = ReviewHelpers.LastSegment(callee);
                    if (leaf != null &&
                        (leaf.StartsWith("assert", StringComparison.Ordinal) ||
                         leaf == "fail" || leaf == "raises" || leaf == "warns" ||
                         leaf.StartsWith("verify", StringComparison.Ordinal))) return true;
                    continue;
                }

                // `with pytest.raises(ValueError):` is the assertion for the failure path.
                WithStatement with = node as WithStatement;
                if (with != null)
                {
                    foreach (WithItem item in with.Items)
                    {
                        CallExpression contextCall = ReviewHelpers.Unwrap(item.ContextExpression) as CallExpression;
                        string leaf = ReviewHelpers.LastSegment(ReviewHelpers.CalleeName(contextCall));
                        if (leaf == "raises" || leaf == "warns" || (leaf != null && leaf.StartsWith("assert", StringComparison.Ordinal)))
                            return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>CR-TST-002 — a test disabled with no reason recorded.</summary>
    internal sealed class UnconditionalSkipRule : IReviewRule
    {
        public string RuleId { get { return "CR-TST-002"; } }
        public string Title { get { return "Test disabled without a stated reason"; } }
        public ReviewCategory Category { get { return ReviewCategory.Testing; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (!context.IsTestFile) return;

            foreach (ReviewFunction function in context.Functions)
            {
                foreach (DecoratorSyntax decorator in function.Node.Decorators)
                {
                    ExpressionSyntax expression = ReviewHelpers.Unwrap(decorator.Expression);
                    CallExpression call = expression as CallExpression;
                    string name = ReviewHelpers.DottedNameOf(call != null ? call.Callee : expression);
                    if (name == null) continue;
                    string leaf = ReviewHelpers.LastSegment(name);
                    if (leaf != "skip" && leaf != "xfail") continue;

                    bool hasReason = false;
                    if (call != null)
                        foreach (CallArgument argument in call.Arguments)
                            if (argument.Kind == CallArgument.ArgKind.Keyword && argument.Name == "reason" &&
                                !string.IsNullOrEmpty(ReviewHelpers.StringLiteralValue(argument.Value)))
                                hasReason = true;
                    if (hasReason) continue;

                    output.Add(context.Report(decorator, RuleId, Title, Category,
                        ReviewSeverity.Minor, ReviewConfidence.High,
                        "`" + function.QualifiedName + "` is marked `" + leaf + "` with no `reason=`.",
                        "A disabled test with no reason cannot be re-enabled by anyone but its author, and " +
                        "usually not by them either six months later. It stays green and silently stops " +
                        "protecting whatever it was written for.",
                        "Add `reason=\"…\"` naming the blocker (ideally with an issue number), or delete the " +
                        "test if the behaviour it covered is gone.",
                        ReviewEffort.Trivial));
                }
            }
        }
    }

    /// <summary>CR-TST-003 — a sleep in a test.</summary>
    internal sealed class SleepInTestRule : IReviewRule
    {
        public string RuleId { get { return "CR-TST-003"; } }
        public string Title { get { return "Test waits on a fixed delay"; } }
        public ReviewCategory Category { get { return ReviewCategory.Testing; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (!context.IsTestFile) return;

            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                if (ReviewHelpers.CalleeName(call) != "time.sleep") continue;

                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.Medium,
                    "`time.sleep(...)` in a test makes its result depend on machine speed.",
                    "The delay is either too short — so the test fails intermittently on a loaded CI machine, " +
                    "and the usual response is to double it rather than to fix the race — or too long, and the " +
                    "suite slows down for everyone. Both outcomes get worse over time.",
                    "Wait on the condition instead: poll with a timeout, use the library's own wait/join, or " +
                    "inject a fake clock so the test controls time outright.",
                    ReviewEffort.Moderate));
            }
        }
    }
}
