namespace HelixPythonNamespace.Lint.Rules
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Python.Syntax;
    using HelixPythonNamespace.Python.Text;

    /// <summary>C001: Imported name never referenced in the module.</summary>
    public sealed class UnusedImportRule : ILintRule
    {
        public string Code { get { return "C001"; } }

        private struct Entry
        {
            public string Description;
            public SourceSpan Span;
        }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            Dictionary<string, Entry> imported = new Dictionary<string, Entry>();

            foreach (StatementSyntax stmt in module.Statements)
            {
                if (stmt is ImportStatement imp)
                {
                    foreach (ImportStatement.ImportedName n in imp.Names)
                    {
                        if (n.Name == null) continue;
                        string fqName = n.Name.AsString;
                        string bound = !string.IsNullOrEmpty(n.Alias)
                            ? n.Alias
                            : (n.Name.Parts.Count == 0 ? fqName : n.Name.Parts[0]);
                        imported[bound] = new Entry { Description = "import " + fqName, Span = stmt.Span };
                    }
                }
                else if (stmt is FromImportStatement from)
                {
                    if (from.IsStar) continue; // wildcard handled by I001
                    string mod = from.Module != null ? from.Module.AsString : "";
                    if (mod == "__future__") continue; // directives, never referenced
                    foreach (KeyValuePair<string, string> na in from.Names)
                    {
                        string bound = !string.IsNullOrEmpty(na.Value) ? na.Value : na.Key;
                        imported[bound] = new Entry { Description = "from " + mod + " import " + na.Key, Span = stmt.Span };
                    }
                }
            }

            if (imported.Count == 0)
            {
                return;
            }

            HashSet<string> usedNames = new HashSet<string>();
            foreach (StatementSyntax stmt in module.Statements)
            {
                if (stmt is ImportStatement || stmt is FromImportStatement)
                {
                    continue;
                }
                LintHelpers.WalkAll(stmt, node =>
                {
                    if (node is NameExpression nm)
                    {
                        usedNames.Add(nm.Name);
                    }
                    else if (node is DottedName dn)
                    {
                        if (dn.Parts.Count > 0) usedNames.Add(dn.Parts[0]);
                    }
                    else if (node is ClassPattern cp)
                    {
                        if (cp.ClassName.Count > 0) usedNames.Add(cp.ClassName[0]);
                    }
                    else if (node is ValuePattern vp)
                    {
                        if (vp.DottedName.Count > 0) usedNames.Add(vp.DottedName[0]);
                    }
                });
            }

            foreach (KeyValuePair<string, Entry> kv in imported)
            {
                if (!usedNames.Contains(kv.Key))
                {
                    output.Add(new LintFinding
                    {
                        Code = Code,
                        Severity = AnalysisSeverity.Warning,
                        Message = "Unused " + kv.Value.Description + ".",
                        Span = kv.Value.Span,
                    });
                }
            }
        }
    }

    /// <summary>C002: Mutable default argument (<c>def f(x=[])</c> / <c>{}</c> / <c>set()</c> literal).</summary>
    public sealed class MutableDefaultArgRule : ILintRule
    {
        public string Code { get { return "C002"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            LintHelpers.WalkAll(module, node =>
            {
                FunctionDefSyntax fn = node as FunctionDefSyntax;
                if (fn == null) return;
                foreach (ParameterSyntax p in fn.Parameters)
                {
                    ExpressionSyntax dv = p.DefaultValue;
                    bool mutable = dv is ListExpression || dv is DictExpression || dv is SetExpression;
                    if (mutable)
                    {
                        output.Add(new LintFinding
                        {
                            Code = "C002",
                            Severity = AnalysisSeverity.Warning,
                            Message = "Mutable default argument '" + p.Name + "' (use None and assign inside the body).",
                            Span = p.Span,
                        });
                    }
                }
            });
        }
    }

    /// <summary>C003: Comparison to None using == / != instead of is / is not.</summary>
    public sealed class CompareToNoneRule : ILintRule
    {
        public string Code { get { return "C003"; } }

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
                    bool isEq = op == ComparisonOperator.Eq || op == ComparisonOperator.NotEq;
                    if (isEq && (LiteralKinds.IsNone(prev) || LiteralKinds.IsNone(rhs)))
                    {
                        output.Add(new LintFinding
                        {
                            Code = "C003",
                            Severity = AnalysisSeverity.Warning,
                            Message = "Comparison to None should use 'is' / 'is not'.",
                            Span = cmp.Span,
                        });
                        return;
                    }
                    prev = rhs;
                }
            });
        }
    }

    /// <summary>C004: Comparison to True / False literals.</summary>
    public sealed class CompareToBoolLiteralRule : ILintRule
    {
        public string Code { get { return "C004"; } }

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
                    bool isEq = op == ComparisonOperator.Eq || op == ComparisonOperator.NotEq;
                    if (isEq && (LiteralKinds.IsBool(prev) || LiteralKinds.IsBool(rhs)))
                    {
                        output.Add(new LintFinding
                        {
                            Code = "C004",
                            Severity = AnalysisSeverity.Warning,
                            Message = "Comparison to True/False literal (use 'if x:' / 'if not x:' instead).",
                            Span = cmp.Span,
                        });
                        return;
                    }
                    prev = rhs;
                }
            });
        }
    }

    /// <summary>C005: f-string with no interpolated expressions — the f-prefix is dead weight.</summary>
    public sealed class FStringNoInterpolationRule : ILintRule
    {
        public string Code { get { return "C005"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            if (string.IsNullOrEmpty(sourceText)) return;
            LintHelpers.WalkAll(module, node =>
            {
                FStringExpression fs = node as FStringExpression;
                if (fs == null) return;

                // The AST cannot reliably distinguish a literal TEXT chunk from a
                // replacement field whose expression is itself a constant — e.g.
                // `f"{'n=50*K':>8}"` stores the inner `'n=50*K'` as a LiteralExpression,
                // exactly like plain text between braces — so a parts-only check
                // false-flagged real interpolations. Decide from the SOURCE instead: the
                // `f` prefix is dead weight ONLY when the string contains NO brace at all.
                // Any '{' or '}' means the f is meaningful — a replacement field
                // (`{expr}`, even `{'const':>8}`) OR an escaped brace (`{{`/`}}`, which the
                // f-prefix unescapes: `f"{{x}}"` -> "{x}" vs "{{x}}" without f). Matches
                // pylint W1309 (f-string-without-interpolation).
                int start = fs.Span.Start.Offset;
                int len = fs.Span.Length;
                if (start < 0 || len <= 0 || start + len > sourceText.Length) return; // unverifiable
                string raw = sourceText.Substring(start, len);
                if (raw.IndexOf('{') >= 0 || raw.IndexOf('}') >= 0) return;

                output.Add(new LintFinding
                {
                    Code = "C005",
                    Severity = AnalysisSeverity.Warning,
                    Message = "f-string has no interpolated expressions — drop the 'f' prefix.",
                    Span = fs.Span,
                });
            });
        }
    }

    internal static class LiteralKinds
    {
        public static bool IsNone(ExpressionSyntax e)
        {
            LiteralExpression lit = e as LiteralExpression;
            if (lit == null || lit.Token == null) return false;
            return lit.Token.Kind == TokenKind.KwNone;
        }

        public static bool IsBool(ExpressionSyntax e)
        {
            LiteralExpression lit = e as LiteralExpression;
            if (lit == null || lit.Token == null) return false;
            TokenKind k = lit.Token.Kind;
            return k == TokenKind.KwTrue || k == TokenKind.KwFalse;
        }

        public static bool IsValueLiteral(ExpressionSyntax e)
        {
            LiteralExpression lit = e as LiteralExpression;
            if (lit == null || lit.Token == null) return false;
            TokenKind k = lit.Token.Kind;
            return k == TokenKind.Number || k == TokenKind.String;
        }
    }
}
