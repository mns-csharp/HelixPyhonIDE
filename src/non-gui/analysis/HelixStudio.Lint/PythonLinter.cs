namespace HelixPythonNamespace.Lint
{
    using System.Collections.Generic;
    using System.Linq;
    using HelixPythonNamespace.Lint.Rules;
    using HelixPythonNamespace.Python;
    using HelixPythonNamespace.Python.Syntax;

    /// <summary>
    /// Orchestrates a fixed library of lint rules over a Python module. A
    /// default-constructed instance is preloaded with all v1 rules. Port of the
    /// C++ <c>helix::lint::PythonLinter</c>.
    /// </summary>
    public sealed class PythonLinter
    {
        private readonly List<ILintRule> _rules = new List<ILintRule>();

        public PythonLinter()
        {
            // Style (L)
            _rules.Add(new LineTooLongRule());
            _rules.Add(new TrailingWhitespaceRule());
            _rules.Add(new MixedIndentRule());
            _rules.Add(new MultipleStatementsRule());

            // Naming (N)
            _rules.Add(new ClassPascalCaseRule());
            _rules.Add(new FunctionSnakeCaseRule());
            _rules.Add(new ShadowsBuiltinRule());

            // Smell (C)
            _rules.Add(new UnusedImportRule());
            _rules.Add(new MutableDefaultArgRule());
            _rules.Add(new CompareToNoneRule());
            _rules.Add(new CompareToBoolLiteralRule());

            // Import (I)
            _rules.Add(new WildcardImportRule());
            _rules.Add(new MultipleImportsOnOneLineRule());

            // Bug (B)
            _rules.Add(new BareExceptRule());
            _rules.Add(new AssertTupleRule());
            _rules.Add(new IsLiteralRule());
            _rules.Add(new BareRaiseOutsideExceptRule());
            _rules.Add(new FStringNoInterpolationRule());
        }

        public void AddRule(ILintRule rule)
        {
            if (rule != null)
            {
                _rules.Add(rule);
            }
        }

        public List<LintFinding> Run(ModuleSyntax module, string sourceText,
            System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            List<LintFinding> findings = new List<LintFinding>();
            foreach (ILintRule rule in _rules)
            {
                // Check between rules so Cancel is responsive even on a single large file.
                cancel.ThrowIfCancellationRequested();
                rule.Visit(module, sourceText, findings);
            }
            // Rules run category-by-category, so raw findings are grouped by rule rather
            // than by source position — which makes the Problems/Lint panel jump around a
            // file when navigating top-to-bottom. Present them in source order instead.
            // OrderBy is a stable sort, so findings sharing a line+column keep their
            // (deterministic) rule-registration order.
            return findings
                .OrderBy(f => f.Span.Start.Line)
                .ThenBy(f => f.Span.Start.Column)
                .ToList();
        }

        /// <summary>Convenience: parse <paramref name="source"/> and run all rules.</summary>
        public List<LintFinding> Run(string source, string fileName = null,
            System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            ParseResult parsed = Python.ParseModule(source, fileName, cancel);
            return Run(parsed.Module, source, cancel);
        }
    }
}
