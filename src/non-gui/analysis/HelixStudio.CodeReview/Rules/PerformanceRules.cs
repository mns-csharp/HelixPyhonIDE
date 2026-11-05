namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// Cost that is invisible at review size and decisive at production size. These rules only
    /// fire on shapes whose cost grows with the data — a slow line that runs once is not a review
    /// comment, and saying so would train people to ignore the category.
    /// </summary>
    internal static class PerformanceRuleSet
    {
        public static IEnumerable<IReviewRule> All()
        {
            yield return new StringConcatenationInLoopRule();
            yield return new RangeLenLoopRule();
            yield return new MembershipTestAgainstListLiteralRule();
            yield return new RedundantKeysCallRule();
            yield return new NestedLoopOverSameCollectionRule();
            yield return new SortInsideLoopRule();
            yield return new UnboundedFileReadRule();
        }

        /// <summary>The loop that encloses a node, or null.</summary>
        internal static StatementSyntax EnclosingLoop(SyntaxNode node)
        {
            SyntaxNode current = node != null ? node.Parent : null;
            while (current != null)
            {
                if (current is ForStatement || current is WhileStatement) return (StatementSyntax)current;
                // A def between the node and the loop means the node runs when the def is CALLED,
                // which the loop no longer tells us anything about.
                if (current is FunctionDefSyntax || current is ClassDefSyntax) return null;
                current = current.Parent;
            }
            return null;
        }

        /// <summary>True when <paramref name="name"/> is assigned anywhere inside the loop body.</summary>
        internal static bool AssignedInside(StatementSyntax loop, string name)
        {
            foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(loop))
            {
                AssignmentStatement assignment = node as AssignmentStatement;
                if (assignment != null && assignment.Targets != null)
                {
                    foreach (ExpressionSyntax target in assignment.Targets)
                        if (ReviewHelpers.NamesIn(target).Contains(name)) return true;
                    continue;
                }
                AugmentedAssignmentStatement augmented = node as AugmentedAssignmentStatement;
                if (augmented != null && ReviewHelpers.NamesIn(augmented.Target).Contains(name)) return true;
            }
            return false;
        }
    }

    /// <summary>CR-PERF-001 — building a string with `+=` in a loop.</summary>
    internal sealed class StringConcatenationInLoopRule : IReviewRule
    {
        public string RuleId { get { return "CR-PERF-001"; } }
        public string Title { get { return "String built by repeated concatenation in a loop"; } }
        public ReviewCategory Category { get { return ReviewCategory.Performance; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                AugmentedAssignmentStatement augmented = node as AugmentedAssignmentStatement;
                if (augmented == null || augmented.Operator != BinaryOperator.Add) continue;
                if (PerformanceRuleSet.EnclosingLoop(augmented) == null) continue;

                // Only claim it when the right-hand side is demonstrably text — a numeric `total += n`
                // in a loop is correct code and must not be reported.
                ExpressionSyntax value = ReviewHelpers.Unwrap(augmented.Value);
                bool textual = value is FStringExpression || ReviewHelpers.StringLiteralValue(value) != null;
                if (!textual)
                {
                    CallExpression call = value as CallExpression;
                    string callee = call != null ? ReviewHelpers.LastSegment(ReviewHelpers.CalleeName(call)) : null;
                    textual = callee == "str" || callee == "repr" || callee == "format" || callee == "join";
                }
                if (!textual) continue;

                string target = ReviewHelpers.DottedNameOf(augmented.Target) ?? "the accumulator";
                output.Add(context.Report(augmented, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.Medium,
                    "`" + target + " += ...` inside a loop rebuilds the whole string on every iteration.",
                    "Python strings are immutable, so each `+=` allocates a new string and copies everything " +
                    "accumulated so far. The loop is quadratic in the output length: fine for the ten-line " +
                    "fixture in the test, minutes for a real file. CPython has an in-place optimisation for " +
                    "this shape, but it only applies when the string has exactly one reference — it is not " +
                    "something to rely on.",
                    "Append the pieces to a list and join once at the end: " +
                    "`parts.append(...)` … `\"\".join(parts)`. That is linear regardless of interpreter.",
                    ReviewEffort.Small));
            }
        }
    }

    /// <summary>CR-PERF-002 — `for i in range(len(xs))`.</summary>
    internal sealed class RangeLenLoopRule : IReviewRule
    {
        public string RuleId { get { return "CR-PERF-002"; } }
        public string Title { get { return "Index loop where iteration would read better"; } }
        public ReviewCategory Category { get { return ReviewCategory.Performance; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ForStatement loop = node as ForStatement;
                if (loop == null) continue;

                CallExpression range = ReviewHelpers.Unwrap(loop.Iter) as CallExpression;
                if (range == null || ReviewHelpers.CalleeName(range) != "range") continue;
                if (range.Arguments == null || range.Arguments.Count != 1) continue;   // range(1, len(x)) is a real offset

                CallExpression length = ReviewHelpers.Unwrap(range.Arguments[0].Value) as CallExpression;
                if (length == null || ReviewHelpers.CalleeName(length) != "len") continue;

                string collection = length.Arguments != null && length.Arguments.Count == 1
                    ? ReviewHelpers.DottedNameOf(length.Arguments[0].Value) : null;
                string index = ReviewHelpers.DottedNameOf(loop.Target) ?? "i";

                output.Add(context.Report(loop, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.High,
                    "`for " + index + " in range(len(" + (collection ?? "...") + "))` iterates indices to look the elements back up.",
                    "Every use inside the body pays for a subscript, and the index is an extra name that can " +
                    "drift out of step with the collection — off-by-one bugs live here. The direct form also " +
                    "works unchanged for any iterable, not just sequences.",
                    collection != null
                        ? "Iterate the values: `for item in " + collection + ":`, or " +
                          "`for " + index + ", item in enumerate(" + collection + "):` when the index is genuinely needed."
                        : "Iterate the collection directly, or use `enumerate` when the index is genuinely needed.",
                    ReviewEffort.Trivial));
            }
        }
    }

    /// <summary>CR-PERF-003 — `x in [a, b, c, d]`.</summary>
    internal sealed class MembershipTestAgainstListLiteralRule : IReviewRule
    {
        public string RuleId { get { return "CR-PERF-003"; } }
        public string Title { get { return "Membership test scans a list literal"; } }
        public ReviewCategory Category { get { return ReviewCategory.Performance; } }

        private const int MinimumElements = 4;

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ComparisonExpression comparison = node as ComparisonExpression;
                if (comparison == null || comparison.Operators == null) continue;

                for (int i = 0; i < comparison.Operators.Count; i++)
                {
                    if (comparison.Operators[i] != ComparisonOperator.In &&
                        comparison.Operators[i] != ComparisonOperator.NotIn) continue;

                    ListExpression list = ReviewHelpers.Unwrap(comparison.Comparators[i]) as ListExpression;
                    if (list == null || list.Elements.Count < MinimumElements) continue;

                    bool allConstant = true;
                    foreach (ExpressionSyntax element in list.Elements)
                        if (!ReviewHelpers.IsConstantLiteral(element)) { allConstant = false; break; }
                    if (!allConstant) continue;

                    bool inLoop = PerformanceRuleSet.EnclosingLoop(comparison) != null;
                    output.Add(context.Report(comparison, RuleId, Title, Category,
                        inLoop ? ReviewSeverity.Minor : ReviewSeverity.Info,
                        ReviewConfidence.High,
                        "`in [...]` with " + list.Elements.Count + " constant elements" +
                        (inLoop ? ", inside a loop." : "."),
                        "A list membership test compares element by element, and the literal is rebuilt on " +
                        "every evaluation. A frozenset is hashed once at import and answers in constant time — " +
                        (inLoop
                            ? "inside a loop that difference is the loop's cost multiplied by the list length."
                            : "here the win is mostly that a set states 'this is a fixed set of options'."),
                        "Hoist it to a module-level constant: " +
                        "`_ALLOWED = frozenset({...})`, then test `x in _ALLOWED`.",
                        ReviewEffort.Trivial));
                    break;
                }
            }
        }
    }

    /// <summary>CR-PERF-004 — `.keys()` where the dict itself would do.</summary>
    internal sealed class RedundantKeysCallRule : IReviewRule
    {
        public string RuleId { get { return "CR-PERF-004"; } }
        public string Title { get { return "Redundant `.keys()`"; } }
        public ReviewCategory Category { get { return ReviewCategory.Performance; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ExpressionSyntax candidate = null;
                string usage = null;

                ForStatement loop = node as ForStatement;
                if (loop != null) { candidate = loop.Iter; usage = "iterating"; }

                ComparisonExpression comparison = node as ComparisonExpression;
                if (comparison != null && comparison.Operators != null)
                {
                    for (int i = 0; i < comparison.Operators.Count; i++)
                        if (comparison.Operators[i] == ComparisonOperator.In ||
                            comparison.Operators[i] == ComparisonOperator.NotIn)
                        { candidate = comparison.Comparators[i]; usage = "a membership test"; break; }
                }
                if (candidate == null) continue;

                CallExpression call = ReviewHelpers.Unwrap(candidate) as CallExpression;
                if (call == null || (call.Arguments != null && call.Arguments.Count > 0)) continue;
                AttributeExpression member = ReviewHelpers.Unwrap(call.Callee) as AttributeExpression;
                if (member == null || member.Name != "keys") continue;

                string receiver = ReviewHelpers.DottedNameOf(member.Target) ?? "d";
                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Info, ReviewConfidence.High,
                    "`" + receiver + ".keys()` in " + usage + " — a dict already yields its keys.",
                    "The call allocates a view object for no benefit, and the longer form hides the fact that " +
                    "the dict is being used as a key set. In `x in " + receiver + ".keys()` the difference is " +
                    "not just style: it is an extra object per test.",
                    "Drop the call: `" + receiver + "` on its own does the same thing.",
                    ReviewEffort.Trivial));
            }
        }
    }

    /// <summary>CR-PERF-005 — two nested loops over the same collection.</summary>
    internal sealed class NestedLoopOverSameCollectionRule : IReviewRule
    {
        public string RuleId { get { return "CR-PERF-005"; } }
        public string Title { get { return "Quadratic scan over the same collection"; } }
        public ReviewCategory Category { get { return ReviewCategory.Performance; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ForStatement outer = node as ForStatement;
                if (outer == null) continue;
                string outerName = ReviewHelpers.DottedNameOf(outer.Iter);
                if (outerName == null) continue;

                foreach (SyntaxNode inner in ReviewHelpers.DescendantsInSameScope(outer.Body))
                {
                    ForStatement innerLoop = inner as ForStatement;
                    if (innerLoop == null) continue;
                    if (ReviewHelpers.DottedNameOf(innerLoop.Iter) != outerName) continue;

                    output.Add(context.Report(innerLoop, RuleId, Title, Category,
                        ReviewSeverity.Minor, ReviewConfidence.Medium,
                        "This loop re-scans `" + outerName + "` for every element of the loop on line " +
                        outer.Span.Start.Line + " — O(n²) in its length.",
                        "All-pairs iteration is sometimes exactly what the algorithm needs. When it is not — " +
                        "when the inner loop is really looking something up — the cost is invisible on a " +
                        "fixture of twenty items and quadratic on real input.",
                        "If the inner loop is a lookup, index `" + outerName + "` into a dict or set once " +
                        "before the outer loop and look it up in constant time. If all pairs really are needed, " +
                        "`itertools.combinations` says so explicitly.",
                        ReviewEffort.Moderate));
                    break;
                }
            }
        }
    }

    /// <summary>CR-PERF-006 — sorting the same unchanged data inside a loop.</summary>
    internal sealed class SortInsideLoopRule : IReviewRule
    {
        public string RuleId { get { return "CR-PERF-006"; } }
        public string Title { get { return "Sort repeated inside a loop"; } }
        public ReviewCategory Category { get { return ReviewCategory.Performance; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                string callee = ReviewHelpers.CalleeName(call);
                if (callee == null) continue;
                bool isSort = callee == "sorted" || ReviewHelpers.LastSegment(callee) == "sort";
                if (!isSort) continue;

                StatementSyntax loop = PerformanceRuleSet.EnclosingLoop(call);
                if (loop == null) continue;

                // The subject: sorted(x) -> x, x.sort() -> x.
                string subject = callee == "sorted"
                    ? (call.Arguments != null && call.Arguments.Count > 0
                        ? ReviewHelpers.DottedNameOf(call.Arguments[0].Value) : null)
                    : ReviewHelpers.DottedNameOf((ReviewHelpers.Unwrap(call.Callee) as AttributeExpression)?.Target);
                if (subject == null) continue;
                // If the loop changes it, re-sorting is the point.
                if (PerformanceRuleSet.AssignedInside(loop, ReviewHelpers.RootSegment(subject))) continue;

                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.Medium,
                    "`" + subject + "` is sorted on every iteration, but the loop never changes it.",
                    "Sorting is O(n log n) and the result is identical each time, so the loop multiplies that " +
                    "cost by its own trip count for no change in behaviour.",
                    "Sort once before the loop and reuse the result inside it.",
                    ReviewEffort.Trivial));
            }
        }
    }

    /// <summary>CR-PERF-007 — reading a whole file into memory with no bound.</summary>
    internal sealed class UnboundedFileReadRule : IReviewRule
    {
        public string RuleId { get { return "CR-PERF-007"; } }
        public string Title { get { return "Whole file read into memory"; } }
        public ReviewCategory Category { get { return ReviewCategory.Performance; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                AttributeExpression member = ReviewHelpers.Unwrap(call.Callee) as AttributeExpression;
                if (member == null) continue;
                bool readAll = member.Name == "readlines" ||
                    (member.Name == "read" && (call.Arguments == null || call.Arguments.Count == 0));
                if (!readAll) continue;
                // Only claim it for something that looks like a file handle, not `response.read()`
                // on a small body or a mock in a test.
                if (context.IsTestFile) continue;

                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.Low,
                    "`." + member.Name + "()` with no size argument loads the entire stream into memory at once.",
                    "Peak memory becomes the size of the input rather than the size of a chunk. That is fine " +
                    "for a config file and fatal for a log or a dataset — and the failure mode is an " +
                    "out-of-memory kill with no stack trace, on the machine with the biggest input.",
                    "Iterate the file (`for line in handle:`) or read in fixed chunks " +
                    "(`while chunk := handle.read(65536):`) when the size is not known to be small.",
                    ReviewEffort.Small));
            }
        }
    }
}
