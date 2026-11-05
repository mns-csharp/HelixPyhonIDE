namespace HelixPythonNamespace.Analysis.Rules
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    /// <summary>
    /// LOD001: Heuristic signal for Law of Demeter violations ("train wreck" calls).
    /// Detects types whose methods have property-style return types AND whose neighbors
    /// (types that depend on this type) also expose properties of the same return type —
    /// indicating chained navigation a.b.c.d.
    ///
    /// This is a signature-level approximation. Informational severity; actual LoD
    /// detection requires expression-tree analysis.
    /// </summary>
    public sealed class LodTrainWreckRule : IAnalysisRule
    {
        public string RuleId { get { return "LOD001"; } }

        public string Description { get { return "Possible Law of Demeter violation: chainable return types"; } }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();
            if (diagram == null || diagram.Nodes == null) return results;

            // Build a set of type names for quick lookup
            HashSet<string> typeNames = new HashSet<string>();
            Dictionary<string, TypeNode> byName = new Dictionary<string, TypeNode>();
            foreach (TypeNode node in diagram.Nodes)
            {
                typeNames.Add(node.Name);
                byName[node.Name] = node;
            }

            foreach (TypeNode node in diagram.Nodes)
            {
                int chainablePropertyCount = 0;
                MemberDefinition offender = null;

                foreach (MemberDefinition member in node.Members)
                {
                    if (string.IsNullOrEmpty(member.ReturnType)) continue;

                    // A return type like "Foo.Bar" or matching another type indicates the caller
                    // can dot-navigate into it — a train-wreck enabler.
                    string ret = StripGenerics(member.ReturnType);
                    if (typeNames.Contains(ret) && ret != node.Name)
                    {
                        chainablePropertyCount++;
                        if (offender == null) offender = member;
                    }
                }

                if (chainablePropertyCount >= 3)
                {
                    string message = string.Format(
                        "Type '{0}' exposes {1} members whose return types are other diagram types (e.g. '{2}'). Callers can chain into those types; consider Tell-Don't-Ask or a facade method.",
                        node.Name,
                        chainablePropertyCount,
                        offender != null ? offender.Name : "?");

                    results.Add(new AnalysisResult(
                        RuleId,
                        AnalysisSeverity.Info,
                        message,
                        node.Id));
                }
            }

            return results;
        }

        private static string StripGenerics(string typeName)
        {
            int idx = typeName.IndexOf('<');
            return idx >= 0 ? typeName.Substring(0, idx) : typeName;
        }
    }
}
