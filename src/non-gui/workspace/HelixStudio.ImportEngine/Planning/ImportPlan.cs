using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.ImportEngine.Analysis;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.ImportEngine.Planning
{
    /// <summary>How one detected package participates in the imported solution.</summary>
    public enum PackageDisposition
    {
        /// <summary>Emit as its own project (workspace member).</summary>
        SeparateProject = 0,

        /// <summary>Fold into the enclosing package's project (no member of its own).</summary>
        PartOfParent = 1,

        /// <summary>Leave the files on disk but keep them out of the solution.</summary>
        Excluded = 2,
    }

    /// <summary>Where the imported projects live on disk (SRS 4.4 "configure workspace layout" / 4.6).</summary>
    public enum ImportLayout
    {
        /// <summary>Generate TOMLs next to the sources as they are.</summary>
        InPlace = 0,

        /// <summary>Physically move each project package under &lt;root&gt;\src\&lt;name&gt;.</summary>
        RearrangeToSrc = 1,
    }

    /// <summary>The user-editable decisions for one detected package.</summary>
    public sealed class PackagePlan
    {
        public PackagePlan(PackageDescriptor source)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            FinalName = source.Name;
            Disposition = PackageDisposition.SeparateProject;
        }

        public PackageDescriptor Source { get; }

        /// <summary>Project/package name after any rename (SRS 4.4 "rename packages").</summary>
        public string FinalName { get; set; }

        public PackageDisposition Disposition { get; set; }

        /// <summary>True when <see cref="FinalName"/> differs from the detected name.</summary>
        public bool IsRenamed
        {
            get { return !string.Equals(FinalName, Source.Name, StringComparison.Ordinal); }
        }
    }

    /// <summary>
    /// The full, user-editable import plan the wizard builds and the executor
    /// applies (SRS 4.4/4.5/4.6).
    /// </summary>
    public sealed class ImportPlan
    {
        public ImportPlan()
        {
            Packages = new List<PackagePlan>();
            PythonVersion = "3.12";
            TargetFormat = ImportTargetFormat.TomlWorkspace;
            ConvertToObjectOriented = true;
        }

        /// <summary>
        /// Strict-OOP guard (default ON): after every other phase, encapsulate all
        /// module-level functions of every imported project into per-module classes
        /// (rewriting references solution-wide), then audit the whole imported
        /// solution with <see cref="HelixPythonNamespace.Workspace.Python.PythonOopAuditor"/>.
        /// Residual violations FAIL the import — the guard guarantees an imported
        /// solution is fully object-oriented or is not produced at all. Turning
        /// this off skips both the conversion and the enforcement.
        /// </summary>
        public bool ConvertToObjectOriented { get; set; }

        /// <summary>
        /// The directory everything in this plan actually reads/writes/moves — normally a
        /// disposable working COPY of what the user pointed the wizard at, never the original
        /// (see <see cref="OriginalSourceRoot"/>). The wizard is the only thing that should ever
        /// set this to a directory it's willing to see mutated.
        /// </summary>
        public string SourceRoot { get; set; }

        /// <summary>
        /// The user's actual selection (SRS import #1: never mutated). Empty/null when
        /// <see cref="SourceRoot"/> already IS the user's original directory (e.g. a caller that
        /// deliberately opted out of the copy-first safety net, such as the older
        /// non-wizard <c>PythonDirectoryImporter</c> path).
        /// </summary>
        public string OriginalSourceRoot { get; set; }

        public string SolutionName { get; set; }

        public string PythonVersion { get; set; }

        public ImportTargetFormat TargetFormat { get; set; }

        public ImportLayout Layout { get; set; }

        public bool OverwriteExisting { get; set; }

        /// <summary>FinalName of the project chosen as startup, or null for none.</summary>
        public string StartupProjectName { get; set; }

        /// <summary>Apply the auditor's surplus-main.py rename plan during execution.</summary>
        public bool ResolveEntryPoints { get; set; }

        /// <summary>
        /// Extract inline CUDA kernels (see CudaKernelScaffoldAnalyzer) into their own .cu files
        /// and generate the companion "cuda_kernel_debug" native CMake project (SS
        /// §linked-cmake-project), offered whenever <see cref="DetectedCudaScaffold"/> found any.
        /// </summary>
        public bool GenerateCudaDebugScaffold { get; set; }

        /// <summary>The wizard-time detection snapshot (for UI display); the executor re-analyzes against the final package paths before generating anything.</summary>
        public CudaScaffoldAnalysis DetectedCudaScaffold { get; set; }

        public List<PackagePlan> Packages { get; }

        public IEnumerable<PackagePlan> SeparateProjects
        {
            get { return Packages.Where(p => p.Disposition == PackageDisposition.SeparateProject); }
        }
    }
}
