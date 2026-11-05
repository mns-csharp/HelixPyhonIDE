namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Output format that <see cref="PythonDirectoryImporter"/> should produce.
    /// </summary>
    public enum ImportTargetFormat
    {
        /// <summary>Emit uv-compatible TOML workspace (root pyproject.toml + one per member).</summary>
        TomlWorkspace = 0,
        /// <summary>Emit Visual Studio PTVS files (.sln + .pyproj per package).</summary>
        VisualStudio = 1,
        /// <summary>Emit both formats against the same package scan.</summary>
        Both = 2
    }

    public sealed class ImportOptions
    {
        /// <summary>Directory to import. Required.</summary>
        public string SourceRoot { get; set; }

        /// <summary>Where to write the .sln. Null → same directory as <see cref="SourceRoot"/>.</summary>
        public string OutputRoot { get; set; }

        /// <summary>Name of the solution (and the root pyproject.toml project name).</summary>
        public string SolutionName { get; set; }

        /// <summary>Python version written into the generated pyproject.toml (when absent).</summary>
        public string PythonVersion { get; set; } = "3.12";

        /// <summary>When true, existing .sln / .pyproj files are overwritten. TOML-format
        /// conflicts are handled interactively via <see cref="IPyProjectConflictResolver"/>.</summary>
        public bool OverwriteExisting { get; set; }

        /// <summary>Which output format the importer should emit.</summary>
        public ImportTargetFormat TargetFormat { get; set; } = ImportTargetFormat.TomlWorkspace;

        /// <summary>
        /// Callback for per-package pyproject.toml conflicts. Required when
        /// <see cref="TargetFormat"/> is <see cref="ImportTargetFormat.TomlWorkspace"/>
        /// or <see cref="ImportTargetFormat.Both"/>. Null → defaults to silent Overwrite
        /// when <see cref="OverwriteExisting"/> is true, else Keep.
        /// </summary>
        public IPyProjectConflictResolver ConflictResolver { get; set; }

        /// <summary>
        /// IMP-02 analysis/preview mode. Scans and classifies the repository and returns the
        /// proposed projects without creating, moving, or modifying any file.
        /// </summary>
        public bool AnalysisOnly { get; set; }
    }
}
