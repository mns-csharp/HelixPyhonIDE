namespace HelixPythonNamespace.Analysis.Rules
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    /// <summary>
    /// COUPLING001: Reports types with excessively high fan-out (efferent coupling)
    /// or fan-in (afferent coupling). Extreme values suggest architectural hotspots.
    /// Informational severity — not every outlier is a problem, but they are worth seeing.
    /// </summary>
    public sealed class CouplingMetricsRule : IAnalysisRule
    {
        private const int HighFanOutThreshold = 6;
        private const int HighFanInThreshold = 8;

        public string RuleId { get { return "COUPLING001"; } }

        public string Description { get { return "High coupling: fan-in or fan-out exceeds threshold"; } }

        public IEnumerable<AnalysisResult> Evaluate(Diagram diagram)
        {
            List<AnalysisResult> results = new List<AnalysisResult>();
            if (diagram == null || diagram.Nodes == null) return results;

            Dictionary<Guid, int> fanOut = new Dictionary<Guid, int>();
            Dictionary<Guid, int> fanIn = new Dictionary<Guid, int>();

            foreach (TypeNode node in diagram.Nodes)
            {
                fanOut[node.Id] = 0;
                fanIn[node.Id] = 0;
            }

            foreach (Relationship rel in diagram.Relationships)
            {
                if (rel.Source == null || rel.Target == null) continue;
                if (fanOut.ContainsKey(rel.Source.Id)) fanOut[rel.Source.Id]++;
                if (fanIn.ContainsKey(rel.Target.Id)) fanIn[rel.Target.Id]++;
            }

            foreach (TypeNode node in diagram.Nodes)
            {
                int f_out = fanOut[node.Id];
                int f_in = fanIn[node.Id];

                if (f_out > HighFanOutThreshold)
                {
                    string message = string.Format(
                        "Type '{0}' has efferent coupling of {1} (depends on {1} other types). High fan-out makes changes expensive.",
                        node.Name, f_out);
                    results.Add(new AnalysisResult(RuleId, AnalysisSeverity.Info, message, node.Id));
                }

                if (f_in > HighFanInThreshold)
                {
                    string message = string.Format(
                        "Type '{0}' has afferent coupling of {1} ({1} other types depend on it). High fan-in makes this type a structural hotspot.",
                        node.Name, f_in);
                    results.Add(new AnalysisResult(RuleId, AnalysisSeverity.Info, message, node.Id));
                }
            }

            return results;
        }
    }
}
