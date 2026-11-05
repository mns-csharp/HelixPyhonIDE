namespace HelixPythonNamespace.CodeReview.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// How the code behaves when something goes wrong. Every rule here is about a failure the
    /// program will not report — the class of defect that turns a five-minute diagnosis into a day.
    /// </summary>
    internal static class ErrorHandlingRuleSet
    {
        public static IEnumerable<IReviewRule> All()
        {
            yield return new SwallowedExceptionRule();
            yield return new BroadExceptionCatchRule();
            yield return new RaiseWithoutCauseRule();
            yield return new PrintInsteadOfRaiseRule();
            yield return new UnclosedResourceRule();
            yield return new OversizedTryBodyRule();
            yield return new ShadowedExceptionHandlerRule();
        }

        /// <summary>The exception types that catch everything.</summary>
        internal static bool IsCatchAll(ExpressionSyntax type)
        {
            if (type == null) return true;   // bare `except:`
            string name = ReviewHelpers.LastSegment(ReviewHelpers.DottedNameOf(type));
            return name == "Exception" || name == "BaseException";
        }

        internal static bool ContainsRaise(SuiteSyntax body)
        {
            foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(body))
                if (node is RaiseStatement) return true;
            return false;
        }
    }

    /// <summary>CR-ERR-001 — an `except` whose whole body is `pass`.</summary>
    internal sealed class SwallowedExceptionRule : IReviewRule
    {
        public string RuleId { get { return "CR-ERR-001"; } }
        public string Title { get { return "Exception silently swallowed"; } }
        public ReviewCategory Category { get { return ReviewCategory.ErrorHandling; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ExceptHandler handler = node as ExceptHandler;
                if (handler == null) continue;

                IReadOnlyList<StatementSyntax> statements = ReviewHelpers.StatementsOf(handler.Body);
                if (statements.Count != 1) continue;
                if (!(statements[0] is PassStatement) && !(statements[0] is ContinueStatement)) continue;

                bool catchAll = ErrorHandlingRuleSet.IsCatchAll(handler.ExceptionType);
                string caught = handler.ExceptionType != null
                    ? ReviewHelpers.DottedNameOf(handler.ExceptionType) ?? "the exception"
                    : "every exception";

                output.Add(context.Report(handler, RuleId, Title, Category,
                    catchAll ? ReviewSeverity.Critical : ReviewSeverity.Major,
                    ReviewConfidence.High,
                    "`except " + caught + "` discards the error and continues.",
                    "The failure leaves no trace: no log line, no metric, no stack trace. The program carries " +
                    "on with a half-built state, and whatever breaks later does so somewhere unrelated. " +
                    (catchAll
                        ? "Because this catches everything, it also hides typos, `KeyboardInterrupt` and " +
                          "`MemoryError` — the code cannot be stopped or diagnosed."
                        : "Even for one narrow exception type, a silent skip needs a reason a reader can see."),
                    "Log it with the traceback (`logging.exception(...)`), narrow the caught type, or re-raise. " +
                    "If ignoring really is correct, say why in a comment — that is what makes it reviewable.",
                    ReviewEffort.Small));
            }
        }
    }

    /// <summary>CR-ERR-002 — `except Exception:` that handles rather than re-raises.</summary>
    internal sealed class BroadExceptionCatchRule : IReviewRule
    {
        public string RuleId { get { return "CR-ERR-002"; } }
        public string Title { get { return "Catch-all exception handler"; } }
        public ReviewCategory Category { get { return ReviewCategory.ErrorHandling; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ExceptHandler handler = node as ExceptHandler;
                if (handler == null || handler.ExceptionType == null) continue;   // bare except is a lint rule
                if (!ErrorHandlingRuleSet.IsCatchAll(handler.ExceptionType)) continue;
                // Catching broadly to log-and-re-raise, or to clean up and re-raise, is correct.
                if (ErrorHandlingRuleSet.ContainsRaise(handler.Body)) continue;
                // An `except Exception: pass` is the sharper CR-ERR-001; do not say it twice.
                IReadOnlyList<StatementSyntax> statements = ReviewHelpers.StatementsOf(handler.Body);
                if (statements.Count == 1 && (statements[0] is PassStatement || statements[0] is ContinueStatement)) continue;

                string caught = ReviewHelpers.DottedNameOf(handler.ExceptionType);
                output.Add(context.Report(handler, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.Medium,
                    "`except " + caught + "` handles every failure the block can produce and does not re-raise.",
                    "The handler was written for one expected failure, but it also absorbs `AttributeError` " +
                    "from a typo, `KeyError` from a renamed field, and every future exception the body grows. " +
                    "Those get the recovery path meant for something else, so a bug in the try block presents " +
                    "as the handled condition instead of as a bug.",
                    "Catch the specific types the block can raise. Where a catch-all is genuinely right " +
                    "(a request boundary, a plugin host), log the traceback and re-raise or record it — do not " +
                    "convert it into a normal return.",
                    ReviewEffort.Moderate));
            }
        }
    }

    /// <summary>CR-ERR-003 — a new exception raised inside a handler with no `from`.</summary>
    internal sealed class RaiseWithoutCauseRule : IReviewRule
    {
        public string RuleId { get { return "CR-ERR-003"; } }
        public string Title { get { return "Re-raise loses the original cause"; } }
        public ReviewCategory Category { get { return ReviewCategory.ErrorHandling; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ExceptHandler handler = node as ExceptHandler;
                if (handler == null) continue;

                foreach (SyntaxNode inner in ReviewHelpers.DescendantsInSameScope(handler.Body))
                {
                    RaiseStatement raise = inner as RaiseStatement;
                    if (raise == null) continue;
                    if (raise.Exception == null) continue;     // bare `raise` already preserves everything
                    if (raise.Cause != null) continue;         // `raise X from err`

                    string thrown = ReviewHelpers.DottedNameOf(
                        ReviewHelpers.Unwrap(raise.Exception) is CallExpression call
                            ? call.Callee : raise.Exception) ?? "a new exception";

                    output.Add(context.Report(raise, RuleId, Title, Category,
                        ReviewSeverity.Minor, ReviewConfidence.High,
                        "`raise " + thrown + "(...)` inside an `except` block, with no `from`.",
                        "Python still chains the traceback implicitly, but it prints it as \"During handling of " +
                        "the above exception, another exception occurred\" — which reads as a bug in the " +
                        "handler. `from` states that the first exception is the CAUSE, and that is what the " +
                        "next person debugging this needs to know.",
                        "Name the original: `except OSError as err: raise ConfigError(...) from err`. " +
                        "Use `from None` when hiding the internal cause is deliberate.",
                        ReviewEffort.Trivial));
                    break;
                }
            }
        }
    }

    /// <summary>CR-ERR-004 — an error path that prints.</summary>
    internal sealed class PrintInsteadOfRaiseRule : IReviewRule
    {
        public string RuleId { get { return "CR-ERR-004"; } }
        public string Title { get { return "Error handler reports via print()"; } }
        public ReviewCategory Category { get { return ReviewCategory.ErrorHandling; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                ExceptHandler handler = node as ExceptHandler;
                if (handler == null) continue;

                IReadOnlyList<StatementSyntax> statements = ReviewHelpers.StatementsOf(handler.Body);
                if (statements.Count == 0) continue;

                bool onlyPrints = true;
                foreach (StatementSyntax statement in statements)
                {
                    ExpressionStatement expression = statement as ExpressionStatement;
                    CallExpression call = expression != null ? ReviewHelpers.Unwrap(expression.Expression) as CallExpression : null;
                    if (call == null || ReviewHelpers.CalleeName(call) != "print") { onlyPrints = false; break; }
                }
                if (!onlyPrints) continue;

                output.Add(context.Report(handler, RuleId, Title, Category,
                    ReviewSeverity.Major, ReviewConfidence.Medium,
                    "This handler's entire response to the failure is `print(...)`.",
                    "Printing sends the message to stdout with no severity, no timestamp and no traceback, " +
                    "and the function then returns as if it had succeeded. In a service the line goes " +
                    "nowhere; in a pipeline it corrupts the output stream; in both cases the caller is told " +
                    "nothing went wrong.",
                    "Use `logging.exception(\"...\")` to keep the traceback, and then either re-raise or return " +
                    "a value that tells the caller the operation failed.",
                    ReviewEffort.Small));
            }
        }
    }

    /// <summary>CR-ERR-005 — `open()` with no `with` and no `close()`.</summary>
    internal sealed class UnclosedResourceRule : IReviewRule
    {
        public string RuleId { get { return "CR-ERR-005"; } }
        public string Title { get { return "File opened without a context manager and never closed"; } }
        public ReviewCategory Category { get { return ReviewCategory.ErrorHandling; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (ReviewFunction function in context.Functions)
            {
                if (function.Node.Body == null) continue;

                foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(function.Node.Body))
                {
                    AssignmentStatement assignment = node as AssignmentStatement;
                    if (assignment == null || assignment.Targets == null || assignment.Targets.Count != 1) continue;

                    CallExpression call = ReviewHelpers.Unwrap(assignment.Value) as CallExpression;
                    if (call == null || ReviewHelpers.CalleeName(call) != "open") continue;

                    NameExpression target = ReviewHelpers.Unwrap(assignment.Targets[0]) as NameExpression;
                    if (target == null) continue;
                    // Inside a `with` the handle is already managed; a returned handle is the
                    // caller's to close.
                    if (assignment.FirstAncestor<WithStatement>() != null) continue;
                    if (IsClosedOrReturned(function.Node.Body, target.Name)) continue;

                    output.Add(context.Report(assignment, RuleId, Title, Category,
                        ReviewSeverity.Major, ReviewConfidence.Medium,
                        "`" + target.Name + " = open(...)` — this handle is never closed on any path.",
                        "CPython's refcounting usually closes it eventually, which is why this passes tests and " +
                        "then fails under PyPy, under load, or on Windows where the open handle blocks the next " +
                        "rename or delete. An exception between here and any close makes the leak certain.",
                        "Use `with open(...) as " + target.Name + ":` — the file closes on every path out of the " +
                        "block, including the exceptional ones.",
                        ReviewEffort.Trivial));
                }
            }
        }

        private static bool IsClosedOrReturned(SuiteSyntax body, string name)
        {
            foreach (SyntaxNode node in ReviewHelpers.DescendantsInSameScope(body))
            {
                CallExpression call = node as CallExpression;
                if (call != null)
                {
                    AttributeExpression member = ReviewHelpers.Unwrap(call.Callee) as AttributeExpression;
                    if (member != null && member.Name == "close")
                    {
                        NameExpression receiver = ReviewHelpers.Unwrap(member.Target) as NameExpression;
                        if (receiver != null && receiver.Name == name) return true;
                    }
                    continue;
                }
                ReturnStatement ret = node as ReturnStatement;
                if (ret != null && ret.Value != null && ReviewHelpers.NamesIn(ret.Value).Contains(name)) return true;
            }
            return false;
        }
    }

    /// <summary>CR-ERR-006 — a `try` body so large the handler cannot know what failed.</summary>
    internal sealed class OversizedTryBodyRule : IReviewRule
    {
        public string RuleId { get { return "CR-ERR-006"; } }
        public string Title { get { return "`try` block covers too much to attribute the failure"; } }
        public ReviewCategory Category { get { return ReviewCategory.ErrorHandling; } }

        private const int Threshold = 15;

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                TryStatement tryStatement = node as TryStatement;
                if (tryStatement == null || tryStatement.Handlers == null || tryStatement.Handlers.Count == 0) continue;

                int statements = ReviewHelpers.StatementCount(tryStatement.Body);
                if (statements <= Threshold) continue;

                output.Add(context.Report(tryStatement, RuleId, Title, Category,
                    ReviewSeverity.Minor, ReviewConfidence.Medium,
                    "This `try` guards " + statements + " statements with one handler.",
                    "The handler runs for a failure anywhere in the block, so it cannot tell which operation " +
                    "failed or how much of the work already happened. Recovery written for the first statement " +
                    "then also runs after the tenth, on a partially-updated state.",
                    "Narrow the `try` to the statements that can actually raise the handled exception, and move " +
                    "the rest out — usually into the `else:` clause, which runs only when the guarded part " +
                    "succeeded.",
                    ReviewEffort.Moderate));
            }
        }
    }

    /// <summary>CR-ERR-007 — a narrow handler after a catch-all in the same `try`.</summary>
    internal sealed class ShadowedExceptionHandlerRule : IReviewRule
    {
        public string RuleId { get { return "CR-ERR-007"; } }
        public string Title { get { return "Exception handler is unreachable"; } }
        public ReviewCategory Category { get { return ReviewCategory.ErrorHandling; } }

        public void Review(ReviewContext context, List<ReviewFinding> output)
        {
            foreach (SyntaxNode node in ReviewHelpers.SelfAndDescendants(context.Module))
            {
                TryStatement tryStatement = node as TryStatement;
                if (tryStatement == null || tryStatement.Handlers == null || tryStatement.Handlers.Count < 2) continue;

                for (int i = 0; i < tryStatement.Handlers.Count - 1; i++)
                {
                    if (!ErrorHandlingRuleSet.IsCatchAll(tryStatement.Handlers[i].ExceptionType)) continue;

                    ExceptHandler shadowed = tryStatement.Handlers[i + 1];
                    string broad = tryStatement.Handlers[i].ExceptionType != null
                        ? ReviewHelpers.DottedNameOf(tryStatement.Handlers[i].ExceptionType) : "";
                    string narrow = shadowed.ExceptionType != null
                        ? ReviewHelpers.DottedNameOf(shadowed.ExceptionType) : "";

                    output.Add(context.Report(shadowed, RuleId, Title, Category,
                        ReviewSeverity.Critical, ReviewConfidence.High,
                        "`except " + narrow + "` can never run: the `except " + broad + "` on line " +
                        tryStatement.Handlers[i].Span.Start.Line + " already matches it.",
                        "Handlers are tried in order and the first matching type wins. The specific recovery " +
                        "written here is dead, so the failure it was written for silently gets the generic " +
                        "path instead — the handler exists, is tested by reading, and never executes.",
                        "Move the specific handler above the catch-all. Ordering exception handlers " +
                        "narrowest-first is the rule that makes this impossible.",
                        ReviewEffort.Trivial));
                    break;
                }
            }
        }
    }
}
