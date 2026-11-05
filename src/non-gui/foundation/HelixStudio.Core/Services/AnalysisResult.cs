namespace HelixPythonNamespace.Core.Services
{
    using System;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed class AnalysisResult
    {
        private readonly string _ruleId;
        private readonly AnalysisSeverity _severity;
        private readonly string _message;
        private readonly Nullable<Guid> _affectedNodeId;

        public AnalysisResult(string ruleId, AnalysisSeverity severity, string message, Nullable<Guid> affectedNodeId)
        {
            _ruleId = ruleId;
            _severity = severity;
            _message = message;
            _affectedNodeId = affectedNodeId;
        }

        public string RuleId
        {
            get { return _ruleId; }
        }

        public AnalysisSeverity Severity
        {
            get { return _severity; }
        }

        public string Message
        {
            get { return _message; }
        }

        public Nullable<Guid> AffectedNodeId
        {
            get { return _affectedNodeId; }
        }
    }
}
