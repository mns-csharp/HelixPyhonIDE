namespace HelixPythonNamespace.Lint
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Python.Syntax;
    using HelixPythonNamespace.Python.Text;

    /// <summary>A single lint result: rule code, severity, message, source span.</summary>
    public sealed class LintFinding
    {
        public string Code;
        public AnalysisSeverity Severity = AnalysisSeverity.Warning;
        public string Message;
        public SourceSpan Span;
    }

    /// <summary>
    /// A lint rule visits a parsed module (and/or its raw source text), appending
    /// findings to <paramref name="output"/>. Port of the C++ <c>LintRule</c>.
    /// </summary>
    public interface ILintRule
    {
        string Code { get; }

        void Visit(ModuleSyntax module, string sourceText, List<LintFinding> output);
    }
}
