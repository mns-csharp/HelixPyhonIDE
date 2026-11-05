namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed class GenericsWithoutVarianceRule : IAnalysisRule
    {
        public string RuleId
        {
            get { return "GEN001"; }
        }

        public string Description
        {
            get { return "Generic parameter has no constraints"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.GenericParameters.Count == 0)
                {
                    continue;
                }

                foreach (GenericParameter param in node.GenericParameters)
                {
                    if (param.Constraints.Count == 0)
                    {
                        string message = string.Format(
                            "Type '{0}' has generic parameter '{1}' with no constraints. Consider adding constraints to clarify intent (e.g., where T : class).",
                            node.Name,
                            param.Name);

                        results.Add(new AnalysisResult(
                            RuleId,
                            AnalysisSeverity.Info,
                            message,
                            node.Id));
                    }
                }
            }

            return results;
        }
    }
}
