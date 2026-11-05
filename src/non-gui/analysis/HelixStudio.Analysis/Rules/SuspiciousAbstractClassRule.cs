namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed class SuspiciousAbstractClassRule : IAnalysisRule
    {
        public string RuleId
        {
            get { return "ABS001"; }
        }

        public string Description
        {
            get { return "Abstract class may be misused"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.AbstractClass)
                {
                    continue;
                }

                int abstractCount = 0;
                int concreteCount = 0;

                foreach (MemberDefinition member in node.Members)
                {
                    if (member.IsAbstract)
                    {
                        abstractCount++;
                    }
                    else
                    {
                        concreteCount++;
                    }
                }

                if (abstractCount == 0 && node.Members.Count > 0)
                {
                    string message = string.Format(
                        "Abstract class '{0}' has no abstract members. Consider making it concrete or converting to an interface.",
                        node.Name);

                    results.Add(new AnalysisResult(
                        RuleId,
                        AnalysisSeverity.Warning,
                        message,
                        node.Id));
                }
                else if (concreteCount == 0 && abstractCount > 0)
                {
                    string message = string.Format(
                        "Abstract class '{0}' has only abstract members. Consider using an interface instead.",
                        node.Name);

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
