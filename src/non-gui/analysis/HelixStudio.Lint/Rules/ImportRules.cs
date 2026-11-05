namespace HelixPythonNamespace.Lint.Rules
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>I001: Wildcard import (<c>from x import *</c>).</summary>
    public sealed class WildcardImportRule : ILintRule
    {
        public string Code { get { return "I001"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            foreach (StatementSyntax stmt in module.Statements)
            {
                FromImportStatement from = stmt as FromImportStatement;
                if (from == null || !from.IsStar) continue;
                string mod = from.Module != null ? from.Module.AsString : "";
                output.Add(new LintFinding
                {
                    Code = "I001",
                    Severity = AnalysisSeverity.Warning,
                    Message = "Wildcard import 'from " + mod + " import *' (import explicit names instead).",
                    Span = stmt.Span,
                });
            }
        }
    }

    /// <summary>I002: Multiple <c>import os, sys</c> on one line.</summary>
    public sealed class MultipleImportsOnOneLineRule : ILintRule
    {
        public string Code { get { return "I002"; } }

        public void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output)
        {
            foreach (StatementSyntax stmt in module.Statements)
            {
                ImportStatement imp = stmt as ImportStatement;
                if (imp == null) continue;
                if (imp.Names.Count <= 1) continue;
                output.Add(new LintFinding
                {
                    Code = "I002",
                    Severity = AnalysisSeverity.Info,
                    Message = "Multiple imports on one line (use a separate line per import).",
                    Span = stmt.Span,
                });
            }
        }
    }
}
