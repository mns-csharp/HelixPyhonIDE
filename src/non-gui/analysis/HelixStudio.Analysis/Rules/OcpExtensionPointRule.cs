namespace HelixPythonNamespace.Analysis.Rules
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    /// <summary>
    /// OCP001: Flags concrete classes that appear closed to extension — non-sealed
    /// classes without any virtual members and without a descendant implement an
    /// abstraction are flagged as "hard to extend".
    /// Heuristic: a ConcreteClass with 0 virtual/abstract members, no descendants,
    /// and that is depended-upon by other types, fails OCP.
    /// </summary>
    public sealed class OcpExtensionPointRule : IAnalysisRule
    {
        public string RuleId { get { return "OCP001"; } }

        public string Description { get { return "Open/Closed violation: type lacks extension points"; } }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();
            if (diagram == null || diagram.Nodes == null) return results;

            // Count incoming dependencies per type (fan-in)
            Dictionary<System.Guid, int> fanIn = new Dictionary<System.Guid, int>();
            Dictionary<System.Guid, int> descendantCount = new Dictionary<System.Guid, int>();

            foreach (TypeNode node in diagram.Nodes)
            {
                fanIn[node.Id] = 0;
                descendantCount[node.Id] = 0;
            }

            foreach (Relationship rel in diagram.Relationships)
            {
                if (rel.Source == null || rel.Target == null) continue;

                if (fanIn.ContainsKey(rel.Target.Id))
                {
                    fanIn[rel.Target.Id]++;
                }

                if ((rel.Kind == RelationshipKind.Inherits || rel.Kind == RelationshipKind.Implements)
                    && descendantCount.ContainsKey(rel.Target.Id))
                {
                    descendantCount[rel.Target.Id]++;
                }
            }

            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.ConcreteClass) continue;
                if (fanIn[node.Id] < 2) continue; // only flag if other types depend on it
                if (descendantCount[node.Id] > 0) continue; // already extended

                bool hasVirtualOrAbstract = false;
                foreach (MemberDefinition member in node.Members)
                {
                    if (member.IsVirtual || member.IsAbstract)
                    {
                        hasVirtualOrAbstract = true;
                        break;
                    }
                }

                if (hasVirtualOrAbstract) continue;

                string message = string.Format(
                    "Concrete class '{0}' is depended upon by {1} type(s) but has no virtual/abstract members or descendants — closed to extension. Consider adding virtual members or an interface.",
                    node.Name,
                    fanIn[node.Id]);

                results.Add(new AnalysisResult(
                    RuleId,
                    AnalysisSeverity.Warning,
                    message,
                    node.Id));
            }

            return results;
        }
    }
}
