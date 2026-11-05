namespace HelixPythonNamespace.Lint.Rules
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>B001: Bare <c>except:</c> clause (catches BaseException incl. KeyboardInterrupt).</summary>
    public sealed class BareExceptRule : ILintRule
    {
        public string Code { get { return "B001"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            LintHelpers.WalkAll(module, node =>
            {
                ExceptHandler h = node as ExceptHandler;
                if (h == null) return;
                if (h.ExceptionType == null)
                {
                    output.Add(new LintFinding
                    {
                        Code = "B001",
                        Severity = AnalysisSeverity.Error,
                        Message = "Bare 'except:' catches BaseException (incl. KeyboardInterrupt). Catch a specific exception type.",
                        Span = h.Span,
                    });
                }
            });
        }
    }

    /// <summary>B002: <c>assert (cond, "message")</c> — the tuple is always truthy, so the assertion never fires.</summary>
    public sealed class AssertTupleRule : ILintRule
    {
        public string Code { get { return "B002"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            LintHelpers.WalkAll(module, node =>
            {
                AssertStatement a = node as AssertStatement;
                if (a == null) return;
                TupleExpression tup = a.Test as TupleExpression;
                if (tup == null) return;
                if (tup.Elements.Count < 2) return;
                output.Add(new LintFinding
                {
                    Code = "B002",
                    Severity = AnalysisSeverity.Error,
                    Message = "Assert on a tuple — the tuple is always truthy, so this assertion never fires. Did you mean `assert cond, message`?",
                    Span = a.Span,
                });
            });
        }
    }

    /// <summary>B003: <c>x is &lt;literal&gt;</c> / <c>is not</c> against a value literal (identity isn't guaranteed).</summary>
    public sealed class IsLiteralRule : ILintRule
    {
        public string Code { get { return "B003"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            LintHelpers.WalkAll(module, node =>
            {
                ComparisonExpression cmp = node as ComparisonExpression;
                if (cmp == null) return;
                IReadOnlyList<ComparisonOperator> ops = cmp.Operators;
                IReadOnlyList<ExpressionSyntax> comps = cmp.Comparators;
                ExpressionSyntax prev = cmp.Left;
                for (int i = 0; i < ops.Count && i < comps.Count; i++)
                {
                    ComparisonOperator op = ops[i];
                    ExpressionSyntax rhs = comps[i];
                    bool isIdentity = op == ComparisonOperator.Is || op == ComparisonOperator.IsNot;
                    if (isIdentity && (LiteralKinds.IsValueLiteral(prev) || LiteralKinds.IsValueLiteral(rhs)))
                    {
                        output.Add(new LintFinding
                        {
                            Code = "B003",
                            Severity = AnalysisSeverity.Warning,
                            Message = "Identity comparison ('is' / 'is not') against a literal — identity isn't guaranteed for value literals; use '==' / '!=' instead.",
                            Span = cmp.Span,
                        });
                        return;
                    }
                    prev = rhs;
                }
            });
        }
    }

    /// <summary>B004: bare <c>raise</c> outside an <c>except</c> handler.</summary>
    public sealed class BareRaiseOutsideExceptRule : ILintRule
    {
        public string Code { get { return "B004"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            Walk(module, 0, output);
        }

        private static void Walk(SyntaxNode n, int exceptDepth, List<LintFinding> output)
        {
            if (n == null)
            {
                return;
            }
            RaiseStatement r = n as RaiseStatement;
            if (r != null && r.Exception == null && exceptDepth == 0)
            {
                output.Add(new LintFinding
                {
                    Code = "B004",
                    Severity = AnalysisSeverity.Error,
                    Message = "Bare 'raise' outside of an 'except:' handler — at runtime this raises 'No active exception to re-raise'.",
                    Span = r.Span,
                });
            }

            bool entersHandler = n is ExceptHandler;
            // Function/class boundaries break the implicit except-context chain.
            bool resetsDepth = n is FunctionDefSyntax || n is ClassDefSyntax;
            int childDepth = resetsDepth ? 0 : exceptDepth + (entersHandler ? 1 : 0);
            foreach (SyntaxNode c in n.Children)
            {
                Walk(c, childDepth, output);
            }
        }
    }
}
