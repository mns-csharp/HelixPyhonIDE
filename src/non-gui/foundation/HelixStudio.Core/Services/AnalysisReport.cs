namespace HelixPythonNamespace.Core.Services
{
    using System;
    using System.Collections.Generic;

    public sealed class AnalysisReport
    {
        private readonly List<AnalysisResult> _violations;
        private readonly DateTime _analyzedAtUtc;

        public AnalysisReport(List<AnalysisResult> violations, DateTime analyzedAtUtc)
        {
            _violations = violations;
            _analyzedAtUtc = analyzedAtUtc;
        }

        public List<AnalysisResult> Violations
        {
            get { return _violations; }
        }

        public DateTime AnalyzedAtUtc
        {
            get { return _analyzedAtUtc; }
        }

        public bool HasViolations
        {
            get { return _violations.Count > 0; }
        }
    }
}
