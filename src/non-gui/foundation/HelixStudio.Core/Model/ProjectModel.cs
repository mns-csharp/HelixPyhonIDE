using System;
using System.Collections.Generic;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Represents a .csproj project within a solution.
    /// Identity: FilePath (absolute path to .csproj)
    /// </summary>
    public sealed class ProjectModel
    {
        public ProjectModel(string filePath, string name, SolutionModel solution)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));

            FilePath = filePath;
            Name = name;
            Solution = solution ?? throw new ArgumentNullException(nameof(solution));
            Types = new List<TypeModel>();
            Functions = new List<FunctionModel>();
            ProjectReferences = new List<ProjectModel>();
            UnresolvedProjectReferences = new List<string>();
            ExcludedPaths = new List<string>();
            TargetFramework = "python3.12";
            PythonVersion = "3.12";
            Kind = ProjectKind.ClassLibrary;
        }

        /// <summary>
        /// Template / role of the project. Drives save-time invariants
        /// (see <c>MainForm.OnEditorBeforeSave</c>). Persisted in
        /// <c>pyproject.toml</c> under <c>[tool.helixstudio].kind</c>.
        /// </summary>
        public ProjectKind Kind { get; set; }

        /// <summary>
        /// True when this package is the solution's startup (the executable the IDE runs when the
        /// user invokes Run/Debug on a solution with more than one executable package). Persisted in
        /// <c>pyproject.toml</c> under <c>[tool.helixstudio].startup</c>. At most one package in a
        /// solution is the startup at any time (docs/python-solution-model.md, PSM-08/10). Only an
        /// executable package (see <see cref="IsExecutable"/>) may be the startup.
        /// </summary>
        public bool IsStartup { get; set; }

        /// <summary>True when this package is runnable (see <see cref="ProjectKinds.IsExecutable"/>).</summary>
        public bool IsExecutable
        {
            get { return ProjectKinds.IsExecutable(Kind); }
        }

        /// <summary>
        /// Command-line arguments appended after the entry file when the IDE runs or debugs this
        /// package — the equivalent of Visual Studio's "Command line arguments" launch setting.
        /// Stored as the raw string the user typed and split with
        /// <see cref="HelixPythonNamespace.Core.CommandLineArguments.Split"/> at launch, so quoted paths
        /// survive. Persisted in <c>pyproject.toml</c> under <c>[tool.helixstudio].run-args</c>,
        /// which means it is committed and shared with the team rather than kept in per-developer
        /// IDE state. Null or empty means "no arguments".
        /// </summary>
        public string RunArguments { get; set; }

        /// <summary>
        /// GUI toolkit the project was scaffolded with. Populated only when
        /// <see cref="Kind"/> is <see cref="ProjectKind.GuiApp"/>. Persisted in
        /// <c>pyproject.toml</c> under <c>[tool.helixstudio].gui-framework</c>.
        /// </summary>
        public GuiFramework? GuiFramework { get; set; }

        /// <summary>
        /// Absolute path to the .csproj file (primary identity).
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// Display name of the project.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Parent solution containing this project.
        /// </summary>
        public SolutionModel Solution { get; }

        /// <summary>
        /// Types defined in this project.
        /// </summary>
        public List<TypeModel> Types { get; }

        /// <summary>
        /// Top-level (module-scope) functions defined in this project's .py files.
        /// Populated by <c>PythonTypeExtractor</c>; consumed by the project tree
        /// to surface entry points like <c>main()</c> for "open &amp; edit".
        /// </summary>
        public List<FunctionModel> Functions { get; }

        /// <summary>
        /// Other projects this project references.
        /// </summary>
        public List<ProjectModel> ProjectReferences { get; }

        /// <summary>
        /// Names of project references the manifest declares whose target is NOT in the solution
        /// — typically because the referenced project was deleted, leaving a dangling entry that
        /// breaks the build. They are surfaced in the Solution Explorer (rather than silently
        /// dropped) precisely so there is somewhere to right-click ▸ Remove Reference; a
        /// reference the UI cannot see is a reference the user cannot fix.
        /// </summary>
        public List<string> UnresolvedProjectReferences { get; }

        /// <summary>
        /// Files/folders excluded from this project (SRS §5.9 "Exclude from
        /// Project"), project-relative and forward-slash normalized. Persisted in
        /// <c>pyproject.toml</c> under <c>[tool.helixstudio].excluded</c>; the loader
        /// skips these paths when populating <see cref="Types"/>/<see cref="Functions"/>,
        /// so an excluded file never appears in the Solution Explorer or analysis.
        /// </summary>
        public List<string> ExcludedPaths { get; set; }

        /// <summary>
        /// Target framework (e.g., "net8.0", "net8.0-windows").
        /// </summary>
        public string TargetFramework { get; set; }

        /// <summary>
        /// Default namespace for the project (usually same as project name).
        /// </summary>
        public string DefaultNamespace { get; set; }

        /// <summary>
        /// The language this project's sources are written in — set by the loader that built
        /// the model (SolutionService → Python, CMakeSolutionLoader → Cpp). Drives every
        /// per-language Solution Explorer
        /// decision via <c>LanguageDescriptors.For(Language)</c> and the
        /// CodebaseSolutionToSolutionExplorerPanelConnector.
        /// </summary>
        public ProjectLanguage Language { get; set; }

        /// <summary>
        /// Native (<see cref="ProjectLanguage.Cpp"/>) projects only: true when this project
        /// actually uses CUDA — its own CMakeLists declares the CUDA language, or it owns
        /// .cu/.cuh sources. Per the <see cref="ProjectLanguage.Cpp"/> design note, CUDA-ness is
        /// a per-project property, not a separate language: this flag is what display surfaces
        /// use to label "CUDA Executable/Library" vs "C++ Executable/Library".
        /// </summary>
        public bool HasCudaSources { get; set; }

        /// <summary>
        /// Python version declared in pyproject.toml (e.g. "3.12").
        /// </summary>
        public string PythonVersion { get; set; }

        /// <summary>
        /// Get the directory containing the project file.
        /// </summary>
        public string ProjectDirectory
        {
            get { return System.IO.Path.GetDirectoryName(FilePath); }
        }

        /// <summary>
        /// Every module this package's own source imports, classified by where it comes from.
        /// Filled by the solution loader from the same parse it builds the type list with, and
        /// rendered as the Solution Explorer's Dependencies node. Never null.
        /// </summary>
        public List<ModuleDependency> ModuleDependencies { get; } = new List<ModuleDependency>();

        /// <summary>
        /// The interpreter / virtual environment this package runs on — read-only facts for the
        /// Solution Explorer's environment node. Null until the loader determines it.
        /// </summary>
        public PythonEnvironmentInfo Environment { get; set; }

        /// <summary>
        /// Non-source files that ship inside the package (data, assets, templates), as paths
        /// relative to <see cref="ProjectDirectory"/>. These are invisible everywhere else in the
        /// tree, which lists only Python sources. Never null.
        /// </summary>
        public List<string> ResourceFiles { get; } = new List<string>();

        /// <summary>
        /// EVERY non-excluded file that really exists under <see cref="ProjectDirectory"/>, as
        /// project-relative forward-slash paths. Filled by the solution loader from one disk scan;
        /// never null, empty until that scan has run.
        ///
        /// This exists because the tree used to be derived entirely from SYMBOLS —
        /// <see cref="Types"/>, <see cref="Functions"/>, <see cref="ResourceFiles"/> — and a file
        /// that contributes none of them therefore got no node at all. Every symbol-free
        /// <c>__init__.py</c> was invisible in the Solution Explorer while sitting plainly in
        /// Explorer, which is precisely the disagreement solution policy rule 4 (PY8004) exists to
        /// forbid. Nine of them survived a "successful" structure repair on the motivating
        /// solution and left it permanently blocked.
        ///
        /// It is the SHARED source of truth for both halves of the parity contract: the renderer
        /// (<c>ProjectTreePanel</c>) draws it, and the checker's <c>TreeLayoutProjector</c>
        /// projects it. Two consumers, one list — so the projection cannot claim a node the tree
        /// does not draw.
        /// </summary>
        public List<string> ContentFiles { get; } = new List<string>();

        /// <summary>
        /// Every non-excluded directory that really exists under <see cref="ProjectDirectory"/>,
        /// project-relative. Held separately from <see cref="ContentFiles"/> because an EMPTY
        /// directory has no file to imply it, and an empty directory the user can see in Explorer
        /// and not in the tree is the same defect as a hidden file. Never null.
        /// </summary>
        public List<string> ContentDirectories { get; } = new List<string>();

        /// <summary>
        /// Resolve this package's runnable entry-point FILE, in priority order:
        /// <c>__main__.py</c>, <c>main.py</c>, <c>&lt;Name&gt;_main.py</c>, then any <c>*_main.py</c>
        /// in the package directory. Returns <c>false</c> for libraries (no entry file), which are
        /// therefore ineligible as a VS2017-style startup package. Shared by the project tree
        /// (startup eligibility + styling) and the App's Run/Debug launch resolution so the
        /// "what counts as a startable package" rule lives in exactly one place.
        /// </summary>
        public static bool TryFindEntryFile(ProjectModel project, out string entryPath)
        {
            entryPath = null;
            if (project == null || !project.IsExecutable || string.IsNullOrEmpty(project.FilePath))
                return false;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(project.FilePath);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return false;
                string[] preferred =
                {
                    System.IO.Path.Combine(dir, "__main__.py"),
                    System.IO.Path.Combine(dir, "main.py"),
                    System.IO.Path.Combine(dir, (project.Name ?? string.Empty) + "_main.py"),
                };
                foreach (string c in preferred)
                    if (System.IO.File.Exists(c)) { entryPath = c; return true; }
                // Fallback: any <something>_main.py living in the package directory.
                foreach (string f in System.IO.Directory.GetFiles(dir, "*_main.py"))
                { entryPath = f; return true; }
                return false;
            }
            catch { entryPath = null; return false; }
        }

        /// <summary>
        /// Get distinct namespace strings from all types in this project.
        /// </summary>
        public List<string> GetNamespaces()
        {
            var namespaces = new HashSet<string>();
            for (int i = 0; i < Types.Count; i++)
            {
                string ns = Types[i].Namespace;
                if (string.IsNullOrEmpty(ns))
                {
                    ns = "(global)";
                }
                namespaces.Add(ns);
            }
            var result = new List<string>(namespaces);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Get types that belong to the specified namespace.
        /// </summary>
        public List<TypeModel> GetTypesByNamespace(string ns)
        {
            var result = new List<TypeModel>();
            string normalizedNs = ns;
            if (normalizedNs == "(global)")
            {
                normalizedNs = "";
            }
            for (int i = 0; i < Types.Count; i++)
            {
                string typeNs = Types[i].Namespace ?? "";
                if (string.Equals(typeNs, normalizedNs, StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrEmpty(typeNs) && string.IsNullOrEmpty(normalizedNs)))
                {
                    result.Add(Types[i]);
                }
            }
            return result;
        }
    }
}
