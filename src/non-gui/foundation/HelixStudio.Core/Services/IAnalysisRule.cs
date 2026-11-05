namespace HelixPythonNamespace.Core.Services
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;

    public interface IAnalysisRule
    {
        string RuleId { get; }
        string Description { get; }
        IEnumerable<AnalysisResult> Evaluate(Diagram diagram);
    }
}
