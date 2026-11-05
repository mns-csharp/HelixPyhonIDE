using System;
using System.Collections.Generic;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.ImportEngine.Execution
{
    /// <summary>One physical relocation the executor performed (SRS 4.6).</summary>
    public sealed class PerformedMove
    {
        public PerformedMove(string from, string to, string reason)
        {
            From = from;
            To = to;
            Reason = reason;
        }

        public string From { get; }

        public string To { get; }

        /// <summary>"rename" or "layout".</summary>
        public string Reason { get; }
    }

    /// <summary>Outcome of executing an ImportPlan.</summary>
    public sealed class ImportExecutionResult
    {
        public ImportExecutionResult()
        {
            Moves = new List<PerformedMove>();
            Notes = new List<string>();
            Diagnostics = new List<string>();
            OopViolations = new List<OopViolation>();
        }

        /// <summary>Modules the strict-OOP guard rewrote into class form.</summary>
        public int OopConvertedModuleCount { get; set; }

        /// <summary>
        /// Residual strict-OOP violations after conversion (empty on a compliant
        /// import). When the guard is on, any entry here failed the import.
        /// </summary>
        public List<OopViolation> OopViolations { get; }

        public bool Success { get; set; }

        public string ErrorMessage { get; set; }

        /// <summary>Root pyproject.toml path (TOML formats), for OpenSolutionAsync.</summary>
        public string PyProjectTomlPath { get; set; }

        /// <summary>.sln path (VS formats).</summary>
        public string SlnPath { get; set; }

        public int ProjectCount { get; set; }

        public List<PerformedMove> Moves { get; }

        /// <summary>Per-file writer notes ("created …", "kept existing …").</summary>
        public List<string> Notes { get; }

        public List<string> Diagnostics { get; }

        /// <summary>Fresh audit of the source tree after execution.</summary>
        public SolutionAuditReport PostAudit { get; set; }
    }
}
