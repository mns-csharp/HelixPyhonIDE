using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Python
{
    public sealed class ImportResult
    {
        public ImportResult()
        {
            Packages = new List<PackageDescriptor>();
            Diagnostics = new List<string>();
            ConflictsResolved = new List<string>();
        }

        public bool Success { get; set; }
        public string SlnPath { get; set; }
        public string PyProjectTomlPath { get; set; }
        public List<PackageDescriptor> Packages { get; }
        public List<string> Diagnostics { get; }

        /// <summary>Human-readable record of each per-package TOML conflict and how it was resolved.</summary>
        public List<string> ConflictsResolved { get; }

        public string ErrorMessage { get; set; }

        /// <summary>IMP-03 repository classification produced during the read-only scan.</summary>
        public string RepositoryClassification { get; set; }

        /// <summary>True when the result was produced without modifying the filesystem.</summary>
        public bool IsAnalysisOnly { get; set; }

        /// <summary>
        /// Solution-management audit (requirement #3 rules) run over the imported source.
        /// Null only when the source root was invalid. Callers inspect
        /// <see cref="SolutionAuditReport.IsCompliant"/> to decide whether to offer resolutions.
        /// </summary>
        public SolutionAuditReport AuditReport { get; set; }

        public int ProjectCount => Packages.Count;
    }
}
