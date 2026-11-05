using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.Core.Services;

namespace HelixPythonNamespace.Analysis.Rules
{
    /// <summary>
    /// Flags concrete classes inheriting from more than one non-Interface base.
    /// Python's C3 MRO is deterministic but diamond hierarchies make reasoning
    /// about override behavior harder and are a common source of bugs.
    /// </summary>
    public sealed class MultipleInheritanceMroRule : IAnalysisRule
    {
        public string RuleId => "PY-MRO-001";
        public string Description => "Concrete class inherits from multiple non-interface bases (possible MRO confusion)";

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();
            if (diagram == null || diagram.Relationships == null) return results;

            Dictionary<System.Guid, List<TypeNode>> classBases = new Dictionary<System.Guid, List<TypeNode>>();
            foreach (Relationship r in diagram.Relationships)
            {
                if (r.Kind != RelationshipKind.Inherits) continue;
                if (r.Source == null || r.Target == null) continue;
                if (r.Target.TypeKind == TypeKind.Interface) continue;
                if (!classBases.TryGetValue(r.Source.Id, out List<TypeNode> list))
                    classBases[r.Source.Id] = list = new List<TypeNode>();
                list.Add(r.Target);
            }

            foreach (TypeNode node in diagram.Nodes)
            {
                if (node.TypeKind != TypeKind.ConcreteClass) continue;
                if (!classBases.TryGetValue(node.Id, out List<TypeNode> bases)) continue;
                if (bases.Count < 2) continue;
                System.Text.StringBuilder names = new System.Text.StringBuilder();
                for (int i = 0; i < bases.Count; i++)
                {
                    if (i > 0) names.Append(", ");
                    names.Append(bases[i].Name);
                }
                results.Add(new AnalysisResult(
                    RuleId,
                    AnalysisSeverity.Warning,
                    "Class '" + node.Name + "' inherits from " + bases.Count + " concrete/abstract bases (" + names + "). Consider favoring a Protocol for mixin-style reuse to avoid Python MRO surprises.",
                    node.Id));
            }
            return results;
        }
    }
}
