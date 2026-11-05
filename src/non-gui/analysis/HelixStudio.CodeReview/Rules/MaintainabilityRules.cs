namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// What the next person to open this file will pay. None of these is a defect today; each is
    /// a cost that lands on someone who was not in the room when the decision was made.
    /// </summary>
    internal static class MaintainabilityRuleSet
    {
        public static IEnumerable<IReviewRule> All()
        {
            yield return new HighCyclomaticComplexityRule();
            yield return new MagicNumberRule();
            yield return new ReviewDebtCommentRule();
            yield return new CommentedOutCodeRule();
            yield return new LongBooleanConditionRule();
            yield return new DebugLeftoverRule();
        }
    }

    /// <summary>CR-MNT-001 — too many independent paths through one function.</summary>
    internal sealed class HighCyclomaticComplexityRule : IReviewRule
    {
        public string RuleId { get { return "CR-MNT-001"; } }
        public string Title { get { return "High cyclomatic complexity"; } }
        public ReviewCategory Category { get { return ReviewCategory.Maintainability; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                int complexity = ReviewHelpers.CyclomaticComplexity(function.Node);
                if (complexity <= context.Options.MaxCyclomaticComplexity) continue;

                output.Add(context.Report(function.Node, RuleId, Title, Category,
                    complexity > context.Options.MaxCyclomaticComplexity * 2
                        ? ReviewSeverity.Major : ReviewSeverity.Minor,
                    ReviewConfidence.High,
                    "`" + function.QualifiedName + "` has a cyclomatic complexity of " + complexity +
                    " (limit " + context.Options.MaxCyclomaticComplexity + ").",
                    "Cyclomatic complexity is a lower bound on the number of tests needed to cover every " +
                    "path, so this function needs at least " + complexity + " of them to be exercised fully. " +
                    "In practice a function this branchy has untested combinations, and that is where the " +
                    "defects are.",
                    "Extract the independent decisions into named predicates or helpers, and replace long " +
                    "if/elif chains over a value with a lookup table. Each extraction removes its branches " +
                    "from this count and gives them a name.",
                    ReviewEffort.Large));
            }
        }
    }

    /// <summary>CR-MNT-002 — an unexplained number in a decision.</summary>
    internal sealed class MagicNumberRule : IReviewRule
    {
        public string RuleId { get { return "CR-MNT-002"; } }
        public string Title { get { return "Unexplained numeric literal"; } }
        public ReviewCategory Category { get { return ReviewCategory.Maintainability; } }

        // Values that carry their own meaning: identity elements, the two ends of a probability,
        // percentages and the usual byte/unit boundaries.
        private static readonly HashSet<string> WellKnown = new HashSet<string>(StringComparer.Ordinal)
        {
            "0", "1", "2", "-1", "0.0", "1.0", "0.5", "10", "100", "1000", "1024", "60", "24", "255", "360",
        };

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            if (context.IsTestFile) return;   // literal expectations are the point of a test

            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ComparisonExpression comparison = node as ComparisonExpression;
                if (comparison == null || comparison.Comparators == null) continue;

                foreach (ExpressionSyntax comparator in comparison.Comparators)
                {
                    string text = ReviewHelpers.NumberLiteralText(comparator);
                    if (text == null || WellKnown.Contains(text)) continue;
                    if (IsInsideConstantDefinition(comparison)) continue;

                    output.Add(context.Report(comparator, RuleId, Title, Category,
                        ReviewSeverity.Info, ReviewConfidence.Medium,
                        "The literal `" + text + "` decides a branch here, with nothing saying what it means.",
                        "A reader cannot tell whether " + text + " is a protocol limit, a tuned threshold or a " +
                        "guess — so nobody dares change it, and when the real limit moves this copy is missed. " +
                        "The same number usually appears in two or three other places by then.",
                        "Give it a name at module level (`MAX_RETRIES = " + text + "`) and use that. The name " +
                        "is the documentation, and there is then one place to change.",
                        ReviewEffort.Trivial));
                    break;
                }
            }
        }

        // `if x > MAX` inside `MAX = ...` cannot happen, but a comparison inside an ALL_CAPS
        // assignment's right-hand side is a computed constant, not a magic value in a decision.
        private static bool IsInsideConstantDefinition(SyntaxNode node)
        {
            AssignmentStatement assignment = node.FirstAncestor<AssignmentStatement>();
            if (assignment == null || assignment.Targets == null || assignment.Targets.Count == 0) return false;
            string name = ReviewHelpers.DottedNameOf(assignment.Targets[0]);
            return name != null && name.Length > 1 && name == name.ToUpperInvariant();
        }
    }

    /// <summary>CR-MNT-003 — TODO/FIXME/HACK/XXX left in the submitted code.</summary>
    internal sealed class ReviewDebtCommentRule : IReviewRule
    {
        public string RuleId { get { return "CR-MNT-003"; } }
        public string Title { get { return "Unresolved marker comment"; } }
        public ReviewCategory Category { get { return ReviewCategory.Maintainability; } }

        private static readonly string[] Markers = { "FIXME", "XXX", "HACK", "TODO", "BUG:", "WIP" };

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (Token comment in context.Comments)
            {
                string text = comment.Text ?? string.Empty;
                string marker = null;
                foreach (string candidate in Markers)
                    if (text.IndexOf(candidate, StringComparison.Ordinal) >= 0) { marker = candidate; break; }
                if (marker == null) continue;

                // FIXME/XXX/HACK/BUG assert something is wrong now; TODO/WIP record work that is
                // merely outstanding. Reviewers treat those two differently, and so does this.
                bool assertsADefect = marker == "FIXME" || marker == "XXX" || marker == "HACK" || marker == "BUG:";

                output.Add(context.ReportAt(comment.Span.Start.Line, comment.Span.End.Line,
                    comment.Span.Start.Column, RuleId, Title, Category,
                    assertsADefect ? ReviewSeverity.Minor : ReviewSeverity.Info, ReviewConfidence.High,
                    "`" + marker + "` marker: " + Trim(text),
                    assertsADefect
                        ? "The author has recorded that this code is known to be wrong or provisional. That is " +
                          "worth surfacing at review time, when it can still be discussed, rather than being " +
                          "found by whoever hits the failure it predicts."
                        : "A TODO that reaches the main branch is a note to nobody: it has no owner, no date " +
                          "and no way to be scheduled. Most survive for years.",
                    "Either fix it now, or move it to the tracker and reference the issue in the comment so it " +
                    "has an owner: `# TODO(#1234): ...`.",
                    ReviewEffort.Trivial));
            }
        }

        private static string Trim(string text)
        {
            string s = text.TrimStart('#', ' ', '\t').Trim();
            return s.Length > 100 ? s.Substring(0, 97) + "…" : s;
        }
    }

    /// <summary>CR-MNT-004 — commented-out code.</summary>
    internal sealed class CommentedOutCodeRule : IReviewRule
    {
        public string RuleId { get { return "CR-MNT-004"; } }
        public string Title { get { return "Commented-out code"; } }
        public ReviewCategory Category { get { return ReviewCategory.Maintainability; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            int runStart = 0, runEnd = 0, runLength = 0;

            foreach (Token comment in context.Comments)
            {
                bool code = LooksLikeCode(comment.Text);
                int line = comment.Span.Start.Line;

                if (code && runLength > 0 && line == runEnd + 1) { runEnd = line; runLength++; continue; }
                if (runLength >= 2) Emit(context, runStart, runEnd, runLength, output);
                if (code) { runStart = line; runEnd = line; runLength = 1; }
                else runLength = 0;
            }
            if (runLength >= 2) Emit(context, runStart, runEnd, runLength, output);
        }

        private void Emit(ReviewContext context, int start, int end, int lines, List<ReviewFinding> output)
        {
            output.Add(context.ReportAt(start, end, 1, RuleId, Title, Category,
                ReviewSeverity.Minor, ReviewConfidence.Medium,
                lines + " consecutive comment lines look like disabled code.",
                "Commented-out code is the worst kind of documentation: it is not run, not tested and not " +
                "updated, so it drifts until it is actively misleading — and nobody dares delete it because " +
                "nobody knows why it is there. Version control already keeps it, with the reason attached.",
                "Delete it. If it is needed again, `git log -S` finds it, and the commit that removed it says " +
                "why.",
                ReviewEffort.Trivial));
        }

        // Deliberately narrow: a comment counts as code only when it both starts like a statement
        // and ends like one. Prose about code ("# returns the parsed dict") must not match.
        private static bool LooksLikeCode(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            string text = raw.TrimStart('#').Trim();
            if (text.Length < 4) return false;
            if (text.StartsWith("!", StringComparison.Ordinal)) return false;      // shebang
            if (text.IndexOf(' ') < 0 && text.IndexOf('(') < 0 && text.IndexOf('=') < 0) return false;

            string[] starters =
            {
                "return ", "import ", "from ", "print(", "self.", "if ", "for ", "while ", "def ",
                "class ", "raise ", "assert ", "elif ", "else:", "try:", "except", "with ", "yield ",
            };
            foreach (string starter in starters)
                if (text.StartsWith(starter, StringComparison.Ordinal)) return true;

            // `name = expr` — an assignment whose left side is a single identifier path. `==` is a
            // comparison inside prose ("# note: a == b here") and does not qualify.
            int equals = text.IndexOf('=');
            if (equals > 0 && equals < text.Length - 1 && text[equals + 1] != '=' && text[equals - 1] != '=' &&
                IsIdentifierPath(text.Substring(0, equals).Trim())) return true;

            // `obj.method(...)` — a call statement, ending where it opened.
            int paren = text.IndexOf('(');
            return paren > 0 && text.EndsWith(")", StringComparison.Ordinal) &&
                   IsIdentifierPath(text.Substring(0, paren).Trim());
        }

        private static bool IsIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return !char.IsDigit(text[0]);
        }

        private static bool IsIdentifierPath(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string part in text.Split('.'))
                if (!IsIdentifier(part)) return false;
            return true;
        }
    }

    /// <summary>CR-MNT-005 — a condition with too many operands to read.</summary>
    internal sealed class LongBooleanConditionRule : IReviewRule
    {
        public string RuleId { get { return "CR-MNT-005"; } }
        public string Title { get { return "Condition has too many operands"; } }
        public ReviewCategory Category { get { return ReviewCategory.Maintainability; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ExpressionSyntax test = null;
                IfStatement ifStatement = node as IfStatement;
                if (ifStatement != null) test = ifStatement.Test;
                WhileStatement whileStatement = node as WhileStatement;
                if (whileStatement != null) test = whileStatement.Test;
                if (test == null) continue;

                int operands = CountBooleanOperands(test);
                if (operands <= context.Options.MaxBooleanOperands) continue;

                output.Add(context.Report(test, RuleId, Title, Category,
                    ReviewSeverity.Info, ReviewConfidence.High,
                    "This condition combines " + operands + " operands with `and`/`or` (limit " +
                    context.Options.MaxBooleanOperands + ").",
                    "Mixed `and`/`or` at this length depends on precedence that readers have to recall " +
                    "correctly, and the whole expression has to be evaluated mentally to answer \"when is this " +
                    "true?\". It is also untestable in parts — only the combined outcome is observable.",
                    "Name the sub-conditions (`is_expired = ...`, `is_owner = ...`) and combine the names, or " +
                    "extract a predicate function whose name states the rule.",
                    ReviewEffort.Small));
            }
        }

        private static int CountBooleanOperands(ExpressionSyntax expression)
        {
            expression = ReviewHelpers.Unwrap(expression);
            BinaryExpression binary = expression as BinaryExpression;
            if (binary == null || (binary.Operator != BinaryOperator.And && binary.Operator != BinaryOperator.Or))
                return 1;
            return CountBooleanOperands(binary.Left) + CountBooleanOperands(binary.Right);
        }
    }

    /// <summary>CR-MNT-006 — a debugger call or a stray print.</summary>
    internal sealed class DebugLeftoverRule : IReviewRule
    {
        public string RuleId { get { return "CR-MNT-006"; } }
        public string Title { get { return "Debugging leftover"; } }
        public ReviewCategory Category { get { return ReviewCategory.Maintainability; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (CallExpression call in ReviewHelpers.Calls(context.Module))
            {
                string callee = ReviewHelpers.CalleeName(call);
                if (callee == null) continue;

                if (callee == "breakpoint" || callee == "pdb.set_trace" || callee == "ipdb.set_trace")
                {
                    output.Add(context.Report(call, RuleId, Title, Category,
                        ReviewSeverity.Blocker, ReviewConfidence.High,
                        "`" + callee + "()` stops the process and waits for a terminal.",
                        "In anything non-interactive this hangs forever with no output — a CI job that never " +
                        "finishes, or a request that never returns. It is the one leftover that takes a system " +
                        "down rather than just adding noise.",
                        "Remove it. If a breakpoint is genuinely wanted in checked-in code, that is what a log " +
                        "statement and a conditional are for.",
                        ReviewEffort.Trivial));
                    continue;
                }

                if (callee != "print") continue;
                if (context.IsTestFile) continue;
                // A print in a CLI entry point is the program's output, not a leftover.
                string member = context.MemberAt(call.Span.Start.Line);
                if (member == "main" || member.StartsWith("main.", StringComparison.Ordinal)) continue;
                if (member.Length == 0) continue;   // module level: a script, not a library

                output.Add(context.Report(call, RuleId, Title, Category,
                    ReviewSeverity.Info, ReviewConfidence.Low,
                    "`print(...)` inside `" + member + "`.",
                    "A library that prints writes to whoever's stdout it happens to be running under, with no " +
                    "level, no timestamp and no way to switch it off. In a pipeline it also corrupts the " +
                    "output the next stage is parsing.",
                    "Use a module logger: `log = logging.getLogger(__name__)` and `log.debug(...)`. The caller " +
                    "then decides what is shown and where it goes.",
                    ReviewEffort.Trivial));
            }
        }
    }
}
