namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed class IspUnusedMembersRule : IAnalysisRule
    {
        public string RuleId
        {
            get { return "ISP002"; }
        }

        public string Description
        {
            get { return "Concrete class may not use all members of implemented interface"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.ConcreteClass)
                {
                    continue;
                }

                foreach (Relationship rel in diagram.Relationships)
                {
                    if (rel.Kind != RelationshipKind.Implements)
                    {
                        continue;
                    }

                    if (rel.Source.Id != node.Id)
                    {
                        continue;
                    }

                    TypeNode interfaceNode = rel.Target;
                    int interfaceCount = interfaceNode.Members.Count;
                    int concreteCount = node.Members.Count;

                    if (concreteCount < interfaceCount)
                    {
                        string message = string.Format(
                            "Type '{0}' implements '{1}' but has fewer public members ({2}) than the interface requires ({3}). Verify all interface members are implemented.",
                            node.Name,
                            interfaceNode.Name,
                            concreteCount,
                            interfaceCount);

                        results.Add(new AnalysisResult(
                            RuleId,
                            AnalysisSeverity.Warning,
                            message,
                            node.Id));
                    }
                }
            }

            return results;
        }
    }
}
