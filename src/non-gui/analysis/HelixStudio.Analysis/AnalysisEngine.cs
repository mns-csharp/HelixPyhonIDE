namespace HelixPythonNamespace.Analysis
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.Analysis.Rules;

    public sealed class AnalysisEngine : IAnalysisEngine
    {
        private readonly List<IAnalysisRule> _rules;

        public AnalysisEngine()
        {
            _rules = new List<IAnalysisRule>
            {
                new SrpMemberCountRule(),
                new IspFatInterfaceRule(),
                new IspUnusedMembersRule(),
                new SuspiciousAbstractClassRule(),
                new InterfaceWithConcreteHintsRule(),
                new GenericsWithoutVarianceRule(),
                new DeepInheritanceRule(),
                new CircularDependencyRule(),
                new LiskovNarrowingRule(),
                new DesignPatternSuggestionRule(),
                new DipConcreteDependencyRule(),
                new OcpExtensionPointRule(),
                new LodTrainWreckRule(),
                new CouplingMetricsRule()
            };
        }

        public AnalysisReport Analyze(Diagram diagram, CancellationToken cancel = default(CancellationToken))
        {
            if (diagram == null) throw new ArgumentNullException("diagram");

            List<AnalysisResult> violations = new List<AnalysisResult>();
            foreach (IAnalysisRule rule in _rules)
            {
                // Check between rules so a long analysis pass stops promptly on Cancel.
                cancel.ThrowIfCancellationRequested();
                IEnumerable<AnalysisResult> results = rule.Evaluate(diagram);
                violations.AddRange(results);
            }
            return new AnalysisReport(violations, DateTime.UtcNow);
        }
    }
}
