namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed class DeepInheritanceRule : IAnalysisRule
    {
        private const int MaxDepth = 3;

        public string RuleId
        {
            get { return "INH001"; }
        }

        public string Description
        {
            get { return "Inheritance chain is too deep"; }
        }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();

            // Build a map from child node Id to parent node Id via Inherits relationships
            Dictionary<Guid, Guid> parentMap = new Dictionary<Guid, Guid>();
            foreach (Relationship rel in diagram.Relationships)
            {
                if (rel.Kind == RelationshipKind.Inherits)
                {
                    parentMap[rel.Source.Id] = rel.Target.Id;
                }
            }

            foreach (TypeNode node in diagram.Nodes)
            {
                int depth = 0;
                Guid currentId = node.Id;

                while (parentMap.ContainsKey(currentId))
                {
                    depth++;
                    currentId = parentMap[currentId];

                    // Guard against infinite loops from malformed data
                    if (depth > 100)
                    {
                        break;
                    }
                }

                if (depth > MaxDepth)
                {
                    string message = string.Format(
                        "Type '{0}' has an inheritance depth of {1}, which exceeds the recommended maximum of {2}.",
                        node.Name,
                        depth,
                        MaxDepth);

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
