namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed class SrpMemberCountRule : IAnalysisRule
    {
        public string RuleId
        {
            get { return "SRP001"; }
        }

        public string Description
        {
            get { return "Type has too many members (possible SRP violation)"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.Members.Count > 7)
                {
                    string message = string.Format(
                        "Type '{0}' has {1} members, which may indicate a Single Responsibility Principle violation. Consider splitting responsibilities.",
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
