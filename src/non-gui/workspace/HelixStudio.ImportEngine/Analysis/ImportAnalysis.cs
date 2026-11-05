using System;
using System.Collections.Generic;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.ImportEngine.Analysis
{
    /// <summary>
    /// One directory that is Python-relevant but not a regular package
    /// (SRS solution_explorer_rules 4.3): namespace dirs, venvs, build
    /// artifacts, test roots, documentation roots.
    /// </summary>
    public sealed class DetectedDirectory
    {
        public DetectedDirectory(string path, string reason)
        {
            Path = path;
            Reason = reason;
        }

        public string Path { get; }

        /// <summary>Human-readable evidence ("contains pyvenv.cfg", "12 *.rst files", ...).</summary>
        public string Reason { get; }
    }

    /// <summary>Two or more detected packages sharing the same (sanitized) name.</summary>
    public sealed class DuplicateNameGroup
    {
        public DuplicateNameGroup(string name)
        {
            Name = name;
            DirectoryPaths = new List<string>();
        }

        public string Name { get; }

        public List<string> DirectoryPaths { get; }
    }

    /// <summary>
    /// Everything the Import Engine learned about a source directory during the
    /// read-only analysis pass (SRS 4.3). Wraps the importer's own analysis
    /// (packages, classification, main-module audit) and adds the detections the
    /// importer does not report.
    /// </summary>
    public sealed class ImportAnalysis
    {
        public ImportAnalysis()
        {
            Packages = new List<PackageDescriptor>();
            NamespaceDirectories = new List<DetectedDirectory>();
            OrphanFiles = new List<string>();
            VirtualEnvironments = new List<DetectedDirectory>();
            BuildArtifacts = new List<DetectedDirectory>();
            TestDirectories = new List<DetectedDirectory>();
            DocumentationDirectories = new List<DetectedDirectory>();
            DuplicateNames = new List<DuplicateNameGroup>();
            Diagnostics = new List<string>();
            OopViolations = new List<OopViolation>();
        }

        /// <summary>
        /// Strict-OOP findings of the read-only pass (SRS §4.3 detections +
        /// the OOP guard's forecast): module-level functions are auto-convertible
        /// at execution time; the rest will fail a guarded import unless fixed
        /// or excluded first.
        /// </summary>
        public List<OopViolation> OopViolations { get; }

        public string SourceRoot { get; set; }

        public bool Success { get; set; }

        public string ErrorMessage { get; set; }

        /// <summary>Regular packages (dirs with __init__.py) + the synthesized root, from the importer scan.</summary>
        public List<PackageDescriptor> Packages { get; }

        /// <summary>"Library project", "Single executable project", "Mixed repository", ...</summary>
        public string RepositoryClassification { get; set; }

        /// <summary>Main-module / def main() audit (multiple main.py, multiple main functions).</summary>
        public SolutionAuditReport AuditReport { get; set; }

        /// <summary>PEP 420-style dirs: contain .py files but no __init__.py (and are not the root).</summary>
        public List<DetectedDirectory> NamespaceDirectories { get; }

        /// <summary>.py files not owned by any detected package or namespace dir.</summary>
        public List<string> OrphanFiles { get; }

        public List<DetectedDirectory> VirtualEnvironments { get; }

        public List<DetectedDirectory> BuildArtifacts { get; }

        public List<DetectedDirectory> TestDirectories { get; }

        public List<DetectedDirectory> DocumentationDirectories { get; }

        public List<DuplicateNameGroup> DuplicateNames { get; }

        public List<string> Diagnostics { get; }

        /// <summary>
        /// Inline-CUDA-kernel-as-Python-string-literal detections (see
        /// CudaKernelScaffoldAnalyzer) — null-Kernels-empty when none were found. Drives the
        /// Import Wizard's "generate CUDA kernel-debug scaffold" option.
        /// </summary>
        public CudaScaffoldAnalysis DetectedCudaScaffold { get; set; }

        /// <summary>Paths of every module whose name is entry-equivalent (main.py & variants).</summary>
        public IReadOnlyList<string> MainModules
        {
            get
            {
                return AuditReport != null
                    ? (IReadOnlyList<string>)AuditReport.MainModules
                    : new string[0];
            }
        }

        /// <summary>Paths of every file containing a module-level def main().</summary>
        public IReadOnlyList<string> MainFunctionFiles
        {
            get
            {
                return AuditReport != null
                    ? (IReadOnlyList<string>)AuditReport.MainFunctionFiles
                    : new string[0];
            }
        }
    }
}
