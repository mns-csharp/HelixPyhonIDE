namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed class IspFatInterfaceRule : IAnalysisRule
    {
        public string RuleId
        {
            get { return "ISP001"; }
        }

        public string Description
        {
            get { return "Interface has too many members"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.TypeKind == TypeKind.Interface && node.Members.Count > 5)
                {
                    string message = string.Format(
                        "Interface '{0}' has {1} members. Consider splitting into smaller, focused interfaces (ISP).",
                        node.Name,
                        node.Members.Count);

                    results.Add(new AnalysisResult(
                        RuleId,
                        AnalysisSeverity.Warning,
                        message,
                        node.Id));
                }
            }

            return results;
        }
    }
}
