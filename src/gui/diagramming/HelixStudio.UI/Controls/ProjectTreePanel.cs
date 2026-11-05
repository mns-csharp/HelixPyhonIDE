namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Extensibility;


    public class ProjectTreePanel : UserControl
    {
        private TreeView _treeView;
        private Label _emptyLabel;
        private TextBox _searchBox;
        private Button _sortButton;
        private Button _collapseAllButton;
        private Button _expandAllButton;
        private ToolTip _headerToolTip;
        // Docstring tooltip for symbol rows, plus the node it is currently showing for (so it is
        // rebuilt only when the mouse crosses into a different row).
        private ToolTip _symbolToolTip;
        private TreeNode _symbolToolTipNode;
        private Panel _headerToolRow;
        private Panel _headerSearchRow;
        private bool _headerWrapped;

        /// <summary>
        /// Narrowest the search box may be squeezed to before it moves to a row of its own.
        ///
        /// The filter combo, the two expand/collapse buttons and the sort toggle are fixed widths
        /// totalling more than a Solution Explorer is normally docked at, so the Fill'd search box
        /// between them was the one that paid — at any ordinary width it collapsed to nothing and
        /// the panel silently lost a control. Wrapping costs one 24px row and only when it must.
        /// </summary>
        private const int MinSearchWidth = 96;

        // SPPFE Sorting: members inside a type are laid out either in source declaration order (the
        // default, matching how the code reads) or alphabetically by name (fast lookup by name). The
        // toggle button cycles the two; the tree is rebuilt so every type honours the current mode.
        private enum MemberSortMode { Declaration, Alphabetical, Access, Kind }
        private MemberSortMode _sortMode = MemberSortMode.Declaration;

        // SPPFE Filtering: narrow the tree to one kind of symbol (composes with the text search).
        // Container nodes (solution/project/folder/file) are kept only for context when a matching
        // descendant survives.
        private enum SymbolKindFilter { All, Classes, Interfaces, Enums, Methods, Properties, Fields, Functions }
        private ComboBox _kindCombo;
        private SymbolKindFilter _kindFilter = SymbolKindFilter.All;
        // Deep snapshot of the full (unfiltered) tree, taken after each load, so the incremental
        // filter can prune from it and restore it when the search box is cleared.
        private TreeNode[] _unfilteredRoots;
        private SolutionModel _currentSolution;
        private Dictionary<string, TreeNode> _fqnToNode;
        private bool _suppressCheckEvents;
        private TreeNode _inlineProjectRenameNode;
        private string _inlineProjectDisplayText;

        private const string SolutionImageKey = "solution";
        private const string ProjectImageKey = "project";
        private const string InterfaceImageKey = "interface";
        private const string AbstractClassImageKey = "abstractclass";
        private const string ClassImageKey = "class";
        private const string StructImageKey = "struct";
        private const string EnumImageKey = "enum";
        private const string StartupProjectImageKey = "startupProject";
        private const string FunctionImageKey = "function";
        private const string AsyncFunctionImageKey = "asyncFunction";
        private const string MethodImageKey = "method";
        private const string PropertyImageKey = "property";
        private const string MemberFieldImageKey = "memberField";
        private const string ConstructorImageKey = "constructor";
        private const string FileImageKey = "file";

        // ---- per-extension file icons ---------------------------------------------------------
        // Every file row used to wear one generic icon whatever the file was. That is the same
        // complaint CreateContentFileNode already recorded -- "a README or a .png wearing the
        // Python-module icon would misreport what it is" -- and it applied just as much to the
        // generic icon it fell back to: a .csv, a .log and a torch checkpoint all looked alike.
        //
        // An extension the set has artwork for is drawn as itself; everything else keeps the
        // generic icon. Adding a format is an asset plus one entry here, not a change at any call
        // site.
        // Rows that are Python things but NOT files: an environment fact, an imported module, a
        // symbol. They shared the generic file icon, which was survivable while that icon was a
        // vague Python-source mark. It is not survivable now that the icon literally reads "*.py":
        // "Requires Python: >=3.11" is not a file, and a row that says it is, lies.
        private const string PythonMarkImageKey = "pythonMark";

        // ---- structural folders ---------------------------------------------------------------
        // Dependencies, Resources, the environment node and the native toolchain all wore the same
        // amber folder, so the SHAPE of a project told a reader nothing -- every group looked like
        // every other group until the labels were read. These are chosen by kind instead.
        //
        // "Excluded Files" deliberately keeps the plain folder. The obvious candidate was a red
        // cross, and excluded files are not an error: they are files somebody chose to leave out,
        // and a warning mark would report a decision as a fault.
        private const string ResourcesFolderImageKey = "folderResources";
        private const string DependenciesFolderImageKey = "folderDependencies";
        private const string ToolchainFolderImageKey = "folderToolchain";

        private const string FileKindPrefix = "file:";
        private static readonly string[] FileKindExtensions =
        {
            ".7z", ".bat", ".bmp", ".c", ".config", ".cpp", ".cs",
            ".csv", ".dat", ".db", ".dll", ".exe", ".go", ".gz",
            ".h", ".h5", ".hpp", ".html", ".ini", ".ipynb", ".jar",
            ".java", ".joblib", ".jpeg", ".jpg", ".json", ".log", ".markdown",
            ".md", ".mp3", ".mp4", ".npz", ".onnx", ".pdf", ".pickle",
            ".pkl", ".png", ".ps1", ".pt", ".pyc", ".rar", ".rb",
            ".rs", ".sh", ".so", ".swift", ".tar", ".toml", ".tsv",
            ".txt", ".wav", ".xml", ".yaml", ".yml", ".zip",

            // Scientific data formats. These are the files a structural-biology or genomics
            // project is actually made of, and they all used to land on the generic file glyph:
            // a directory of 50 PISCES structures beside their FASTA sequences and a .h5 of
            // features was 50+ identical rows, so the tree could tell you a package HAD data but
            // never what kind. Structures (.pdb .cif .xyz .sdf .mol .mol2 .pdbqt .gro), sequence
            // and alignment (.fasta .fastq .bam .sam), genome annotation and variants (.vcf .bed
            // .gff .gtf), and array/matrix containers (.hdf5 .loom .anndata .mtx).
            //
            // .pdb is deliberately here despite ALSO being MSVC's debug-symbol extension, which a
            // C/CUDA build of a converted project drops beside every binary. The row is an icon,
            // not a routing decision -- MainForm.IsMolecularStructureFile reads the file before it
            // sends anything to the PDB viewer -- so the worst case is a symbol file wearing a
            // protein badge in a tree, next to the .dll and .exe that give it away.
            ".anndata", ".bam", ".bed", ".cif", ".ent", ".fasta", ".fastq",
            ".gff", ".gro", ".gtf", ".hdf5", ".loom", ".mmcif", ".mol",
            ".mol2", ".mtx", ".pdb", ".pdb1", ".pdbqt", ".pdbx", ".sam",
            ".sdf", ".vcf", ".xyz",
        };

        // Some extensions name ONE format, and share one drawing rather than one being a copy of
        // the other: .jpeg is .jpg, and .markdown is .md. The structure formats add two more
        // families, both of them spellings the PDB viewer already treats as the same file --
        // PdbFileProbe accepts .pdb/.ent/.pdb1 and MmcifReader accepts .cif/.mmcif/.pdbx -- so an
        // icon that split them would disagree with what happens on double-click.
        private static string FileKindAsset(string extension)
        {
            if (string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)) extension = ".jpg";
            if (string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase)) extension = ".md";
            if (string.Equals(extension, ".ent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdb1", StringComparison.OrdinalIgnoreCase)) extension = ".pdb";
            if (string.Equals(extension, ".mmcif", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".pdbx", StringComparison.OrdinalIgnoreCase)) extension = ".cif";
            return "file_" + extension.TrimStart('.') + ".png";
        }

        /// <summary>
        /// Extensions whose artwork was actually found on disk, recorded as the image list is built.
        /// </summary>
        /// <remarks>
        /// The set exists so the lookup can be STATIC: file rows are created from static helpers
        /// (AddFileChild) as well as instance ones, and threading an ImageList through those is a
        /// lot of parameter for a question that has one process-wide answer. It also keeps the
        /// fallback honest -- an extension whose PNG is missing is simply never in here, so the
        /// row degrades to the generic file icon rather than to a blank square.
        /// </remarks>
        private static readonly HashSet<string> AvailableFileKinds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The image key for a file row, by extension, else the generic file icon.</summary>
        private static string FileImageKeyForPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return FileImageKey;
            string extension;
            try { extension = Path.GetExtension(path); }
            catch (ArgumentException) { return FileImageKey; }
            if (string.IsNullOrEmpty(extension)) return FileImageKey;

            // Keyed on the extension as written, because .md and .markdown are separate keys in
            // the image list even though they resolve to the same picture -- the alias lives in
            // FileKindAsset, where the file name is decided, not here.
            return AvailableFileKinds.Contains(extension)
                ? FileKindPrefix + extension.ToLowerInvariant()
                : FileImageKey;
        }
        private const string FolderImageKey = "folder";

        // Per-project-kind package icons (SPPFE Rule 1: language-specific icons). Loaded from
        // assets/product/tree; fall back to a coloured square when an asset is missing.
        private const string PySolutionImageKey = "pySolution";
        private const string PkgAppImageKey = "pkgApp";
        private const string PkgGuiImageKey = "pkgGui";
        private const string PkgFlaskImageKey = "pkgFlask";
        private const string PkgLibraryImageKey = "pkgLibrary";
        private const string PkgUnitTestImageKey = "pkgUnitTest";
        private const string PkgGenericImageKey = "pkgGeneric";

        // Public so hosts (e.g. HelixPythonNamespace.App) can react to events carrying a file-group Tag.
        public sealed class FileGroup
        {
            public ProjectModel Project;
            public string FilePath;
        }

        // SPPFE "error / warning indicator": a diagnostic located by file + line, so the host can
        // badge the owning symbol in the tree without the UI layer depending on the compiler.
        public sealed class SymbolDiagnostic
        {
            public string FilePath;
            public int Line;
            public bool IsError;   // true = error, false = warning
        }

        // Nodes currently carrying a diagnostic colour, so ApplyDiagnostics can reset them before
        // re-applying (idempotent across recompiles).
        private readonly List<TreeNode> _diagnosticNodes = new List<TreeNode>();
        private static readonly Color ErrorColor = Color.FromArgb(200, 32, 32);
        private static readonly Color WarningColor = Color.FromArgb(150, 100, 0);

        // Keep project-system-specific tree construction behind a small provider boundary. This
        // follows the useful part of SharpDevelop's project-browser node-builder design without
        // importing its AddIn tree, WPF workbench, or project model. Put specialised providers
        // before the fallback provider; the first matching provider owns the structural nodes.
        public interface IProjectNodeProvider
        {
            bool CanBuild(ProjectModel project);
            void AppendNodes(ProjectTreePanel owner, TreeNode projectNode, ProjectModel project);
        }

        private sealed class NativeProjectNodeProvider : IProjectNodeProvider
        {
            public bool CanBuild(ProjectModel project)
            {
                return project != null && project.Language == ProjectLanguage.Cpp;
            }

            public void AppendNodes(ProjectTreePanel owner, TreeNode projectNode, ProjectModel project)
            {
                owner.AppendNativeProjectNodes(projectNode, project);
            }
        }

        private sealed class DefaultProjectNodeProvider : IProjectNodeProvider
        {
            public bool CanBuild(ProjectModel project)
            {
                return project != null;
            }

            public void AppendNodes(ProjectTreePanel owner, TreeNode projectNode, ProjectModel project)
            {
                owner.AppendDefaultProjectNodes(projectNode, project);
            }
        }

        private readonly ContributionRegistry<IProjectNodeProvider> _projectNodeProviders =
            new ContributionRegistry<IProjectNodeProvider>();

        public ProjectTreePanel()
        {
            _fqnToNode = new Dictionary<string, TreeNode>();
            _projectNodeProviders.Register("native", new NativeProjectNodeProvider(), 100);
            _projectNodeProviders.Register("default", new DefaultProjectNodeProvider(), -1000);
            InitializeControls();
        }

        /// <summary>
        /// Adds a structural node provider. This is the supported extension seam for a project
        /// system that needs nodes beyond the canonical source/dependency/configuration groups.
        /// </summary>
        public void RegisterProjectNodeProvider(string id, IProjectNodeProvider provider, int priority = 0)
        {
            _projectNodeProviders.Register(id, provider, priority);
        }

        // Selection events
        public event EventHandler<TypeSelectedEventArgs> TypeSelected;
        public event EventHandler<TypeSelectedEventArgs> TypeDoubleClicked;
        public event EventHandler<ProjectVisibilityChangedEventArgs> ProjectVisibilityChanged;

        // CRUD operation events
        public event EventHandler<TreeOperationEventArgs> AddClassRequested;
        public event EventHandler<TreeOperationEventArgs> AddInterfaceRequested;
        public event EventHandler<TreeOperationEventArgs> AddAbstractClassRequested;
        public event EventHandler<TreeOperationEventArgs> AddFileRequested;
        /// <summary>
        /// "Add Jupyter Notebook..." — a separate verb from <see cref="AddFileRequested"/> because
        /// that one forces a <c>.py</c> extension onto whatever the user types, which is exactly
        /// what made a notebook impossible to create from here.
        /// </summary>
        public event EventHandler<TreeOperationEventArgs> AddNotebookRequested;
        public event EventHandler<TreeOperationEventArgs> AddExistingFileRequested;
        public event EventHandler<TreeOperationEventArgs> NewFolderRequested;
        public event EventHandler<TreeOperationEventArgs> AddProjectRequested;
        public event EventHandler<TreeOperationEventArgs> AddExistingProjectRequested;
        public event EventHandler<TreeOperationEventArgs> DeleteRequested;

        /// <summary>"Remove from Solution": unregister the project but KEEP its files on disk
        /// (VS parity) — the non-destructive sibling of <see cref="DeleteRequested"/>.</summary>
        public event EventHandler<TreeOperationEventArgs> RemoveProjectFromSolutionRequested;

        /// <summary>"Add Project Reference...": reference sibling projects from this one
        /// (was only reachable from the canvas context menu; VS puts it on the tree).</summary>
        public event EventHandler<TreeOperationEventArgs> AddProjectReferenceRequested;
        public event EventHandler<TreeOperationEventArgs> MoveProjectUpRequested;
        public event EventHandler<TreeOperationEventArgs> MoveProjectDownRequested;

        /// <summary>"Remove Reference" on a Dependencies child (args.Item = ProjectReferenceGroup).</summary>
        public event EventHandler<TreeOperationEventArgs> RemoveProjectReferenceRequested;
        public event EventHandler<TreeRenameEventArgs> RenameRequested;
        public event EventHandler<TreeOperationEventArgs> SaveRequested;
        public event EventHandler<TreeOperationEventArgs> ViewSourceRequested;
        public event EventHandler<TreeOperationEventArgs> BuildProjectRequested;
        public event EventHandler<TreeOperationEventArgs> ManagePackagesRequested;
        public event EventHandler<TreeOperationEventArgs> RestorePackagesRequested;
        public event EventHandler<TreeOperationEventArgs> PropertiesRequested;
        public event EventHandler<TreeOperationEventArgs> SetStartupProjectRequested;

        /// <summary>
        /// "Run Arguments…" on a runnable project node — the host prompts for the command line and
        /// persists it to the package's <c>[tool.helixstudio].run-args</c>.
        /// </summary>
        public event EventHandler<TreeOperationEventArgs> EditRunArgumentsRequested;

        /// <summary>"Run Tests" on a test-package node — pytest scoped to just this project's directory.</summary>
        public event EventHandler<TreeOperationEventArgs> RunTestsRequested;

        /// <summary>"Debug Tests" on a test-package node — same scoped pytest run, launched under the debugger.</summary>
        public event EventHandler<TreeOperationEventArgs> DebugTestsRequested;
        public event EventHandler<FunctionSelectedEventArgs> FunctionDoubleClicked;

        /// <summary>User double-clicked a file node (FileGroup tag). Arg carries the absolute file path.</summary>
        public event EventHandler<string> FileDoubleClicked;

        /// <summary>User asked to hide the given type from the canvas.</summary>
        public event EventHandler<TreeOperationEventArgs> HideRequested;

        /// <summary>User asked to unhide the given type.</summary>
        public event EventHandler<TreeOperationEventArgs> UnhideRequested;

        /// <summary>User asked to perform a scoped rename-refactor on the node's symbol (TypeModel or FunctionModel).</summary>
        public event EventHandler<TreeOperationEventArgs> RenameSymbolRequested;

        /// <summary>User asked to rename a package (directory) — context depends on the node: ProjectModel, FileGroup, TypeModel, or FunctionModel.</summary>
        public event EventHandler<TreeOperationEventArgs> RenamePackageRequested;

        /// <summary>
        /// User asked to exclude (Tag is FileGroup/FolderGroup) or include (Tag is
        /// ExcludedEntry) an item (SRS §5.9). The host writes/removes the entry in
        /// the owning project's pyproject.toml and reloads the solution.
        /// </summary>
        public event EventHandler<TreeOperationEventArgs> ExcludeFromProjectRequested;
        public event EventHandler<TreeOperationEventArgs> IncludeInProjectRequested;

        /// <summary>
        /// SRS §5.1: a top-level class dragged from its own row onto a different
        /// file row within the tree — a physical, AST-level move of its source
        /// text, not the Link/Copy a drop onto the canvas or editor performs.
        /// </summary>
        public event EventHandler<MoveTypeEventArgs> MoveTypeRequested;

        /// <summary>User asked to edit a function's signature.</summary>
        public event EventHandler<TreeOperationEventArgs> EditSignatureRequested;

        public SolutionModel CurrentSolution
        {
            get { return _currentSolution; }
        }

        public void LoadSolution(SolutionModel solution)
        {
            // A rebuild recreates every row with Checked=false, silently blanking the
            // canvas selection (§7): sort-mode clicks and refreshes both come through
            // here. Remember the current selection and re-apply it after the build.
            List<string> previouslyChecked = GetCheckedProjects();
            _currentSolution = solution;
            _fqnToNode.Clear();
            _diagnosticNodes.Clear();   // node references become stale on rebuild
            _treeView.Nodes.Clear();
            // The load this panel was waiting on has arrived (or failed with a null model), so the
            // placeholder goes back to the idle text either way.
            ClearLoadingText();

            if (solution == null)
            {
                UpdateEmptyState();
                return;
            }

            TreeNode solutionNode = new TreeNode(solution.Name);
            solutionNode.Tag = solution;
            solutionNode.ImageKey = SolutionImageKeyFor(solution);
            solutionNode.SelectedImageKey = solutionNode.ImageKey;
            // Built detached and attached at the end: the folder pass below reorders each node's
            // children, which is a plain list operation while the subtree is not in a TreeView, and
            // on a 1599-file solution it avoids the control doing layout work per inserted node.

            foreach (var project in solution.Projects)
            {
                // The startup package (PSM-11) is highlighted so the user can tell at a glance which
                // executable the IDE will Run/Debug: a "(Startup)" suffix plus a bold node font.
                var projectDisplayName = FormatProjectDisplayName(project, project.Name, project.IsStartup);
                TreeNode projectNode = new TreeNode(projectDisplayName);
                projectNode.Tag = project;
                // Startup package shows the play button; otherwise its language/kind icon (Rule 1).
                projectNode.ImageKey = project.IsStartup ? StartupProjectImageKey : ProjectImageKeyForKind(project);
                projectNode.SelectedImageKey = projectNode.ImageKey;
                projectNode.Checked = false;
                if (project.IsStartup)
                    projectNode.NodeFont = new System.Drawing.Font(this.Font, System.Drawing.FontStyle.Bold);
                solutionNode.Nodes.Add(projectNode);

                // Group types and module-level functions by their source file.
                // A single .py file is a Python module, so this layer mirrors
                // how users think about their code: "which file does X live in?"
                var fileBuckets = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

                foreach (var type in project.Types)
                {
                    string key = NormalizeFileKey(type.SourceFilePath);
                    List<object> bucket;
                    if (!fileBuckets.TryGetValue(key, out bucket))
                    {
                        bucket = new List<object>();
                        fileBuckets[key] = bucket;
                    }
                    bucket.Add(type);
                }

                if (project.Functions != null)
                {
                    foreach (FunctionModel fn in project.Functions)
                    {
                        string key = NormalizeFileKey(fn.SourceFilePath);
                        List<object> bucket;
                        if (!fileBuckets.TryGetValue(key, out bucket))
                        {
                            bucket = new List<object>();
                            fileBuckets[key] = bucket;
                        }
                        bucket.Add(fn);
                    }
                }

                // Order: files alphabetically by basename, then full path as tiebreaker.
                // Empty-path bucket (unassigned) sorts last so it's visually out of the way.
                var orderedKeys = new List<string>(fileBuckets.Keys);
                orderedKeys.Sort(CompareFileKeys);

                // Folder nodes are created on demand and shared between the files under them, so a
                // package appears once no matter how many modules it holds.
                var folderNodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

                // What the symbol pass draws, so the PY8004 content pass below can draw the rest
                // without giving any file a second node.
                var renderedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string fileKey in orderedKeys)
                {
                    List<object> entries = fileBuckets[fileKey];
                    if (entries.Count == 0) continue;

                    TreeNode fileNode = CreateFileNode(project, fileKey, entries.Count);
                    renderedFiles.Add(fileKey);
                    FolderNodeFor(projectNode, project, fileKey, folderNodes).Add(fileNode);

                    // Classes first (alphabetical), then functions (alphabetical).
                    entries.Sort(CompareEntries);

                    foreach (object entry in entries)
                    {
                        TypeModel type = entry as TypeModel;
                        if (type != null)
                        {
                            TreeNode typeNode = CreateTypeNode(type);
                            fileNode.Nodes.Add(typeNode);
                            _fqnToNode[type.FullyQualifiedName] = typeNode;
                            continue;
                        }
                        FunctionModel fn = entry as FunctionModel;
                        if (fn != null)
                        {
                            fileNode.Nodes.Add(CreateFunctionNode(fn));
                        }
                    }
                }
                // Before the sort, so the files and folders it adds are ordered with the rest, and
                // before the canonical (virtual) nodes, so real structure stays above adornments.
                AppendUncoveredProjectContent(projectNode, project, folderNodes, renderedFiles);
                SortChildrenFoldersFirst(projectNode);
                AppendCanonicalProjectNodes(projectNode, project);
            }

            AppendSolutionContent(solutionNode, solution);

            _treeView.Nodes.Add(solutionNode);
            solutionNode.Checked = false;
            StripCheckboxes(_treeView.Nodes);
            solutionNode.Expand();          // raises BeforeExpand -> strips the project row's children
            // Restore the pre-rebuild canvas selection before the snapshot is taken, so
            // both the live tree and the filter-restore copy agree with the canvas (§7).
            if (previouslyChecked.Count > 0) SetCheckedProjects(previouslyChecked);
            CaptureUnfilteredSnapshot();
            if ((_searchBox != null && _searchBox.Text.Length > 0) || _kindFilter != SymbolKindFilter.All) ApplyFilters();
            UpdateEmptyState();
        }

        // --- Incremental search/filter (SPPFE Rule 2) ---

        private void CaptureUnfilteredSnapshot()
        {
            var roots = new List<TreeNode>(_treeView.Nodes.Count);
            foreach (TreeNode n in _treeView.Nodes) roots.Add((TreeNode)n.Clone());
            _unfilteredRoots = roots.ToArray();
        }

        private void OnSearchTextChanged(object sender, EventArgs e)
        {
            ApplyFilters();
        }

        private void OnKindFilterChanged(object sender, EventArgs e)
        {
            _kindFilter = (SymbolKindFilter)Math.Max(0, _kindCombo.SelectedIndex);
            ApplyFilters();
        }

        private void OnSortButtonClick(object sender, EventArgs e)
        {
            // Cycle through the sort modes (SPPFE Sorting): declaration order, alphabetical, by access
            // level, by symbol kind. The button label names the active mode.
            _sortMode = (MemberSortMode)(((int)_sortMode + 1) % 4);
            _sortButton.Text = SortButtonLabel(_sortMode);
            if (_currentSolution == null) return;
            LoadSolutionPreservingExpansion(_currentSolution);
        }

        /// <summary>
        /// Rebuilds the tree exactly like <see cref="LoadSolution"/>, but snapshots which nodes were
        /// expanded (by <see cref="TreeNode.FullPath"/>) first and re-opens the same ones afterward.
        /// Any full rebuild — sorting, a plain solution reload, or replaying a rename — otherwise
        /// collapses every node back to just the solution root (<see cref="LoadSolution"/> only
        /// re-expands that root), forcing the user to re-expand the whole tree by hand.
        /// </summary>
        public void LoadSolutionPreservingExpansion(SolutionModel solution)
        {
            var expanded = new HashSet<string>(StringComparer.Ordinal);
            CollectExpandedPaths(_treeView.Nodes, expanded);
            _treeView.BeginUpdate();
            try
            {
                LoadSolution(solution);
                RestoreExpandedPaths(_treeView.Nodes, expanded);
            }
            finally { _treeView.EndUpdate(); }
        }

        /// <summary>Full paths of every expanded node — persisted per solution so the user's
        /// place in the tree survives closing and reopening the solution/IDE.</summary>
        public List<string> GetExpandedPaths()
        {
            var acc = new HashSet<string>(StringComparer.Ordinal);
            CollectExpandedPaths(_treeView.Nodes, acc);
            return new List<string>(acc);
        }

        /// <summary>
        /// Captures expansion state while translating an exactly-matching renamed node segment.
        /// FullPath cannot be replayed verbatim after rename because the visible project/solution
        /// name is part of every descendant path. Segment matching deliberately avoids rewriting
        /// filenames or symbols which merely contain the old project name.
        /// </summary>
        public List<string> GetExpandedPathsAfterRename(string oldName, string newName)
        {
            return RemapExpandedPathSegment(GetExpandedPaths(), oldName, newName, _treeView.PathSeparator);
        }

        internal static List<string> RemapExpandedPathSegment(
            IEnumerable<string> paths,
            string oldName,
            string newName,
            string separator)
        {
            var result = new List<string>();
            if (paths == null) return result;
            if (string.IsNullOrEmpty(separator)) separator = "\\";

            foreach (string path in paths)
            {
                if (path == null) continue;
                string[] segments = path.Split(new[] { separator }, StringSplitOptions.None);
                for (int i = 0; i < segments.Length; i++)
                {
                    if (string.Equals(segments[i], oldName, StringComparison.Ordinal))
                        segments[i] = newName;
                    else if (!string.IsNullOrEmpty(oldName) &&
                        segments[i].StartsWith(oldName + "  [", StringComparison.Ordinal))
                        segments[i] = newName + segments[i].Substring(oldName.Length);
                }
                result.Add(string.Join(separator, segments));
            }
            return result;
        }

        /// <summary>Re-expands the nodes whose <c>FullPath</c> was captured by
        /// <see cref="GetExpandedPaths"/> in an earlier session.</summary>
        public void ApplyExpandedPaths(IEnumerable<string> paths)
        {
            if (paths == null) return;
            var set = new HashSet<string>(paths, StringComparer.Ordinal);
            if (set.Count == 0) return;
            _treeView.BeginUpdate();
            try { RestoreExpandedPaths(_treeView.Nodes, set); }
            finally { _treeView.EndUpdate(); }
        }

        private static void CollectExpandedPaths(TreeNodeCollection nodes, HashSet<string> acc)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.IsExpanded) acc.Add(n.FullPath);
                if (n.Nodes.Count > 0) CollectExpandedPaths(n.Nodes, acc);
            }
        }

        private static void RestoreExpandedPaths(TreeNodeCollection nodes, HashSet<string> expanded)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Nodes.Count > 0 && expanded.Contains(n.FullPath)) n.Expand();
                if (n.Nodes.Count > 0) RestoreExpandedPaths(n.Nodes, expanded);
            }
        }

        private sealed class SymbolNodeInfo
        {
            public TreeNode Node;
            public string File;
            public int Line;
        }

        /// <summary>
        /// SPPFE "error / warning indicator": colour each symbol that owns a diagnostic (red for an
        /// error, amber for a warning) and propagate the colour up to its ancestors, so a collapsed
        /// project still signals that it contains problems. Idempotent — prior markers are cleared
        /// first, so it can be called after every recompile. Diagnostics are matched to the nearest
        /// enclosing declaration in the same file (greatest declaration line at or before the
        /// diagnostic line), falling back to the file node.
        /// </summary>
        public void ApplyDiagnostics(IEnumerable<SymbolDiagnostic> diags)
        {
            foreach (TreeNode n in _diagnosticNodes) n.ForeColor = Color.Empty;
            _diagnosticNodes.Clear();
            if (diags == null || _treeView == null) return;

            var symbols = new List<SymbolNodeInfo>();
            CollectSymbolNodes(_treeView.Nodes, symbols, null);
            if (symbols.Count == 0) return;

            _treeView.BeginUpdate();
            try
            {
                foreach (SymbolDiagnostic d in diags)
                {
                    if (d == null || string.IsNullOrEmpty(d.FilePath)) continue;
                    TreeNode target = BestSymbolNode(symbols, d);
                    if (target != null) MarkNode(target, d.IsError);
                }
            }
            finally { _treeView.EndUpdate(); }
        }

        private void CollectSymbolNodes(TreeNodeCollection nodes, List<SymbolNodeInfo> acc, string contextFile)
        {
            foreach (TreeNode n in nodes)
            {
                string fileForChildren = contextFile;
                if (n.Tag is TypeModel t)
                {
                    acc.Add(new SymbolNodeInfo { Node = n, File = t.SourceFilePath, Line = t.SourceLineNumber });
                    if (!string.IsNullOrEmpty(t.SourceFilePath)) fileForChildren = t.SourceFilePath;
                }
                else if (n.Tag is FunctionModel fn)
                {
                    acc.Add(new SymbolNodeInfo { Node = n, File = fn.SourceFilePath, Line = fn.SourceLineNumber });
                }
                else if (n.Tag is MemberModel m)
                {
                    acc.Add(new SymbolNodeInfo { Node = n, File = contextFile, Line = m.SourceLineNumber });
                }
                else if (n.Tag is FileGroup fg)
                {
                    acc.Add(new SymbolNodeInfo { Node = n, File = fg.FilePath, Line = 0 });
                    if (!string.IsNullOrEmpty(fg.FilePath)) fileForChildren = fg.FilePath;
                }
                if (n.Nodes.Count > 0) CollectSymbolNodes(n.Nodes, acc, fileForChildren);
            }
        }

        private static TreeNode BestSymbolNode(List<SymbolNodeInfo> symbols, SymbolDiagnostic d)
        {
            TreeNode best = null, fileNode = null;
            int bestLine = -1;
            foreach (SymbolNodeInfo s in symbols)
            {
                if (!PathEquals(s.File, d.FilePath)) continue;
                if (s.Line <= 0) { fileNode = s.Node; continue; }
                if (s.Line <= d.Line && s.Line > bestLine) { bestLine = s.Line; best = s.Node; }
            }
            return best ?? fileNode;
        }

        private void MarkNode(TreeNode node, bool isError)
        {
            ApplyDiagColor(node, isError);
            for (TreeNode p = node.Parent; p != null; p = p.Parent) ApplyDiagColor(p, isError);
        }

        private void ApplyDiagColor(TreeNode node, bool isError)
        {
            // Error outranks warning — never downgrade an already-error node to the warning colour.
            if (node.ForeColor == ErrorColor && !isError)
            {
                if (!_diagnosticNodes.Contains(node)) _diagnosticNodes.Add(node);
                return;
            }
            node.ForeColor = isError ? ErrorColor : WarningColor;
            if (!_diagnosticNodes.Contains(node)) _diagnosticNodes.Add(node);
        }

        private static bool PathEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(NormPath(a), NormPath(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormPath(string p)
        {
            return p.Replace('/', '\\').TrimEnd('\\');
        }

        private static string SortButtonLabel(MemberSortMode mode)
        {
            switch (mode)
            {
                case MemberSortMode.Alphabetical: return "Sort: A→Z";
                case MemberSortMode.Access: return "Sort: Access";
                case MemberSortMode.Kind: return "Sort: Kind";
                default: return "Sort: Decl";
            }
        }

        // Order a type's members by the current sort mode. Every mode falls back to declaration order
        // (source line) for ties, so overloads / accessor pairs stay stable rather than jittering.
        private void SortMembers(List<MemberModel> members)
        {
            switch (_sortMode)
            {
                case MemberSortMode.Alphabetical:
                    members.Sort((a, b) => Then(string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase), a, b));
                    break;
                case MemberSortMode.Access:
                    // Public first, then Protected, Internal, Private — the class-diagram convention.
                    members.Sort((a, b) => Then(AccessRank(a.AccessModifier).CompareTo(AccessRank(b.AccessModifier)), a, b));
                    break;
                case MemberSortMode.Kind:
                    // Group by symbol kind: constructors, then methods, properties, fields.
                    members.Sort((a, b) => Then(KindRank(a.MemberKind).CompareTo(KindRank(b.MemberKind)), a, b));
                    break;
                default:
                    members.Sort((a, b) => a.SourceLineNumber.CompareTo(b.SourceLineNumber));
                    break;
            }
        }

        // Resolve a primary comparison, breaking ties by declaration order (source line).
        private static int Then(int primary, MemberModel a, MemberModel b)
        {
            return primary != 0 ? primary : a.SourceLineNumber.CompareTo(b.SourceLineNumber);
        }

        private static int AccessRank(AccessModifier a)
        {
            switch (a)
            {
                case AccessModifier.Public: return 0;
                case AccessModifier.Protected: return 1;
                case AccessModifier.Internal: return 2;
                default: return 3;   // Private and the protected/internal blends
            }
        }

        private static int KindRank(MemberKind k)
        {
            switch (k)
            {
                case MemberKind.Constructor: return 0;
                case MemberKind.Method: return 1;
                case MemberKind.Property: return 2;
                case MemberKind.Field: return 3;
                default: return 4;
            }
        }

        // Rebuild the tree under the two active filters — text search AND symbol-kind — which compose:
        // a node survives when it (or a descendant) matches BOTH. "All" kind + empty text is the
        // no-op that restores the full tree.
        private void ApplyFilters()
        {
            if (_unfilteredRoots == null) return;
            string f = (_searchBox != null ? _searchBox.Text : string.Empty).Trim().ToLowerInvariant();
            SymbolKindFilter kind = _kindFilter;
            _treeView.BeginUpdate();
            try
            {
                _treeView.Nodes.Clear();
                if (f.Length == 0 && kind == SymbolKindFilter.All)
                {
                    foreach (TreeNode n in _unfilteredRoots) _treeView.Nodes.Add((TreeNode)n.Clone());
                    if (_treeView.Nodes.Count > 0) _treeView.Nodes[0].Expand();
                }
                else
                {
                    foreach (TreeNode n in _unfilteredRoots)
                    {
                        TreeNode kept = FilterNode(n, f, kind);
                        if (kept != null) _treeView.Nodes.Add(kept);
                    }
                }
            }
            finally { _treeView.EndUpdate(); }

            // Filtered rebuilds add pre-expanded rows, so BeforeExpand (the usual lazy
            // strip point) never fires for them — strip every visible level explicitly.
            StripCheckboxesRecursive(_treeView.Nodes);

            UpdateFilterEmptyState(f, kind);
        }

        private void StripCheckboxesRecursive(TreeNodeCollection nodes)
        {
            if (nodes == null || !_treeView.IsHandleCreated) return;
            foreach (TreeNode node in nodes)
            {
                if (!ShowsCheckbox(node)) HideCheckbox(node);
                StripCheckboxesRecursive(node.Nodes);
            }
        }

        /// <summary>
        /// After a filter runs, keep the placeholder honest. When a search or kind filter is active
        /// and matches nothing — but a solution IS loaded — a blank tree reads as "empty solution",
        /// so show "No symbols match …" instead. When the filter is cleared or has hits, hand back to
        /// the ordinary idle/loading empty-state rule.
        /// </summary>
        private void UpdateFilterEmptyState(string filterLower, SymbolKindFilter kind)
        {
            bool filterActive = filterLower.Length > 0 || kind != SymbolKindFilter.All;
            bool solutionLoaded = _unfilteredRoots != null && _unfilteredRoots.Length > 0;
            if (filterActive && solutionLoaded && _treeView.Nodes.Count == 0)
            {
                if (_emptyLabel != null)
                {
                    _emptyLabel.Text = BuildNoMatchText(kind);
                    _emptyLabel.Visible = true;
                }
                _treeView.Visible = false;
                return;
            }

            // Filter cleared or matched: restore the standard idle text (unless a load is in flight)
            // and defer to UpdateEmptyState for the show/hide decision.
            if (!_loading && _emptyLabel != null) _emptyLabel.Text = IdleEmptyText;
            UpdateEmptyState();
        }

        // The message shown when the active filter matches nothing. Reports the search term (from the
        // box, in its original case) and, when a kind filter narrows it, which kind was in play.
        private string BuildNoMatchText(SymbolKindFilter kind)
        {
            string term = _searchBox != null ? _searchBox.Text.Trim() : string.Empty;
            string kindLabel = KindFilterNoun(kind);
            string subject = kindLabel ?? "symbols";
            if (term.Length > 0)
                return "No " + subject + " match “" + term + "”." + Environment.NewLine + Environment.NewLine
                    + "Clear the search box to see the whole solution.";
            return "No " + subject + " in this solution." + Environment.NewLine + Environment.NewLine
                + "Choose “All” in the kind filter to see everything.";
        }

        // A plural noun for the kind filter, for the no-match message ("No methods match …"). Null for
        // All, which means the message falls back to the generic "symbols".
        private static string KindFilterNoun(SymbolKindFilter kind)
        {
            switch (kind)
            {
                case SymbolKindFilter.Classes: return "classes";
                case SymbolKindFilter.Interfaces: return "interfaces";
                case SymbolKindFilter.Enums: return "enums";
                case SymbolKindFilter.Methods: return "methods";
                case SymbolKindFilter.Properties: return "properties";
                case SymbolKindFilter.Fields: return "fields";
                case SymbolKindFilter.Functions: return "functions";
                default: return null;
            }
        }

        // True when a node itself is a symbol of the selected kind. Container nodes
        // (solution/project/folder/file) are never a kind self-match — they survive only for context.
        private static bool KindMatches(TreeNode node, SymbolKindFilter kind)
        {
            if (kind == SymbolKindFilter.All) return true;
            if (node.Tag is TypeModel tm)
            {
                switch (kind)
                {
                    case SymbolKindFilter.Interfaces: return tm.TypeKind == TypeKind.Interface;
                    case SymbolKindFilter.Enums: return tm.TypeKind == TypeKind.Enum;
                    // "Classes" is the concrete/abstract/record/struct family — not interfaces or enums.
                    case SymbolKindFilter.Classes:
                        return tm.TypeKind == TypeKind.ConcreteClass || tm.TypeKind == TypeKind.AbstractClass
                            || tm.TypeKind == TypeKind.Record || tm.TypeKind == TypeKind.Struct;
                    default: return false;
                }
            }
            if (node.Tag is FunctionModel) return kind == SymbolKindFilter.Functions;
            if (node.Tag is MemberModel m)
            {
                switch (kind)
                {
                    case SymbolKindFilter.Methods: return m.MemberKind == MemberKind.Method || m.MemberKind == MemberKind.Constructor;
                    case SymbolKindFilter.Properties: return m.MemberKind == MemberKind.Property;
                    case SymbolKindFilter.Fields: return m.MemberKind == MemberKind.Field;
                    default: return false;
                }
            }
            return false;
        }

        // Returns a filtered clone of <paramref name="src"/> (the node plus only the descendant
        // branches that match both filters), or null when neither the node nor any descendant
        // matches. Matches keep their ancestors and expand so the hit is visible in context.
        private static TreeNode FilterNode(TreeNode src, string filterLower, SymbolKindFilter kind)
        {
            bool textMatch = filterLower.Length == 0 || (src.Text ?? string.Empty).ToLowerInvariant().Contains(filterLower);
            bool selfMatch = textMatch && KindMatches(src, kind);
            var keptChildren = new List<TreeNode>();
            foreach (TreeNode c in src.Nodes)
            {
                TreeNode fc = FilterNode(c, filterLower, kind);
                if (fc != null) keptChildren.Add(fc);
            }
            if (!selfMatch && keptChildren.Count == 0) return null;

            TreeNode copy = new TreeNode(src.Text)
            {
                Tag = src.Tag,
                ImageKey = src.ImageKey,
                SelectedImageKey = src.SelectedImageKey,
                NodeFont = src.NodeFont,
                ToolTipText = src.ToolTipText,
                // Without this, typing in the search box rebuilt every project row
                // unchecked and the next GetCheckedProjects() blanked the canvas (§7).
                Checked = src.Checked,
            };
            foreach (TreeNode kc in keptChildren) copy.Nodes.Add(kc);
            if (keptChildren.Count > 0) copy.Expand();
            return copy;
        }

        private static void SetCueBanner(TextBox box, string cue)
        {
            try { SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, cue); }
            catch { /* cosmetic only */ }
        }

        private const int EM_SETCUEBANNER = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>
        /// Orders a node's children the way a Solution Explorer does: folders first, alphabetically,
        /// then everything else in the order it was already put in (files are inserted alphabetically
        /// by name, and a file's own children — classes then functions — are deliberately ordered by
        /// CompareEntries and must not be disturbed).
        ///
        /// Recurses through folders only, so it never touches the inside of a file node.
        /// </summary>
        private static void SortChildrenFoldersFirst(TreeNode node)
        {
            if (node == null || node.Nodes.Count == 0) return;

            var folders = new List<TreeNode>();
            var rest = new List<TreeNode>();
            foreach (TreeNode child in node.Nodes)
            {
                if (child.Tag is FolderGroup) folders.Add(child);
                else rest.Add(child);
            }
            if (folders.Count == 0) return;   // nothing to hoist; leave the collection untouched

            folders.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase));

            node.Nodes.Clear();
            foreach (TreeNode f in folders) node.Nodes.Add(f);
            foreach (TreeNode r in rest) node.Nodes.Add(r);

            foreach (TreeNode f in folders) SortChildrenFoldersFirst(f);
        }

        /// <summary>
        /// A native (CUDA/C++) build target for the project tree — the render-side DTO produced by
        /// mapping a <c>CMakeProjectDescriptor</c> (FR-PM-03). Kept dependency-free (plain strings)
        /// so HelixPythonNamespace.UI need not reference the workspace layer.
        /// </summary>
        public sealed class CMakeTargetInfo
        {
            public string Name;
            public string Kind;
            public List<string> SourceFiles = new List<string>();
        }

        /// <summary>
        /// Render a native CMake project (FR-PM-03): the project appears with its build targets as
        /// first-class nodes, each expanding to its source files. Double-clicking a source file opens
        /// it (via <see cref="FileDoubleClicked"/>, reusing the <see cref="FileGroup"/> tag). This is
        /// the CUDA/native analogue of <see cref="LoadSolution"/>'s type/function tree.
        /// </summary>
        public void LoadNativeCMakeProject(string solutionName, string projectName, string kindLabel,
            IList<CMakeTargetInfo> targets)
        {
            _currentSolution = null;
            _fqnToNode.Clear();
            _treeView.Nodes.Clear();

            TreeNode solutionNode = new TreeNode(string.IsNullOrEmpty(solutionName) ? projectName : solutionName);
            solutionNode.ImageKey = SolutionImageKey;
            solutionNode.SelectedImageKey = SolutionImageKey;
            _treeView.Nodes.Add(solutionNode);

            string projLabel = string.IsNullOrEmpty(kindLabel) ? projectName : projectName + "  [" + kindLabel + "]";
            TreeNode projectNode = new TreeNode(projLabel);
            projectNode.ImageKey = ProjectImageKey;
            projectNode.SelectedImageKey = ProjectImageKey;
            solutionNode.Nodes.Add(projectNode);

            if (targets != null)
            {
                foreach (CMakeTargetInfo t in targets)
                {
                    if (t == null) continue;
                    int count = t.SourceFiles != null ? t.SourceFiles.Count : 0;
                    TreeNode targetNode = new TreeNode(t.Name + "  (" + t.Kind + ", " + count + ")");
                    // Runnable executables get the "startup/run" glyph; libraries the neutral project glyph.
                    bool runnable = !string.IsNullOrEmpty(t.Kind) &&
                        t.Kind.IndexOf("Exec", StringComparison.OrdinalIgnoreCase) >= 0;
                    targetNode.ImageKey = runnable ? StartupProjectImageKey : ProjectImageKey;
                    targetNode.SelectedImageKey = targetNode.ImageKey;
                    targetNode.ToolTipText = t.Kind + " target: " + t.Name;
                    projectNode.Nodes.Add(targetNode);

                    if (t.SourceFiles != null)
                    {
                        foreach (string sf in t.SourceFiles)
                        {
                            if (string.IsNullOrEmpty(sf)) continue;
                            TreeNode fileNode = new TreeNode(Path.GetFileName(sf));
                            fileNode.Tag = new FileGroup { Project = null, FilePath = sf };
                            fileNode.ImageKey = FileImageKeyForPath(sf);
                            fileNode.SelectedImageKey = fileNode.ImageKey;
                            fileNode.ToolTipText = sf;
                            targetNode.Nodes.Add(fileNode);
                        }
                    }
                    StripCheckboxes(targetNode.Nodes);
                    targetNode.Expand();
                }
            }

            // Nothing in this view feeds the canvas — GetCheckedProjects only reads rows tagged with
            // a ProjectModel, and these carry none — so no row here draws a checkbox either.
            StripCheckboxes(_treeView.Nodes);
            StripCheckboxes(solutionNode.Nodes);
            solutionNode.Expand();
            projectNode.Expand();
            UpdateEmptyState();
        }

        /// <summary>
        /// Tag for a target node under a linked native (CMake) project — see
        /// <see cref="AddLinkedNativeProjectNode"/>. Carries just the target name; the host
        /// (MainForm) keeps the actual <c>CMakeProjectDescriptor</c>/build-dir and resolves the
        /// rest from that when handling <see cref="LinkedCMakeTargetBuildRequested"/>/
        /// <see cref="LinkedCMakeTargetRunRequested"/>, so this stays dependency-free like
        /// <see cref="CMakeTargetInfo"/>.
        /// </summary>
        public sealed class LinkedCMakeTargetTag
        {
            public string TargetName;
        }

        /// <summary>Raised when the user picks "Build" on a linked-CMake-project target node.</summary>
        public event EventHandler<LinkedCMakeTargetTag> LinkedCMakeTargetBuildRequested;

        /// <summary>Raised when the user picks "Run" on a linked-CMake-project target node.</summary>
        public event EventHandler<LinkedCMakeTargetTag> LinkedCMakeTargetRunRequested;

        /// <summary>
        /// Attaches a native CMake project as a SECOND, independent top-level Solution Explorer
        /// node, alongside whatever <see cref="LoadSolution"/> already populated — unlike
        /// <see cref="LoadNativeCMakeProject"/> (which clears the tree and REPLACES the active
        /// workspace), this is purely additive: the Python solution tree at <c>Nodes[0]</c> is left
        /// untouched, and every method here that assumes "the solution node is Nodes[0]"
        /// (SetCheckedProjects/GetCheckedProjects/AddProject/...) keeps working unaffected because
        /// this new node is always appended after it. Each executable target gets a Build/Run
        /// context menu (via <see cref="LinkedCMakeTargetTag"/>) since a linked project commonly has
        /// many targets with no single "active" one (see the [tool.helixstudio] linked-cmake-project
        /// convention — one static library + N debug-entry-point executables, one per kernel).
        /// </summary>
        public TreeNode AddLinkedNativeProjectNode(string projectName, string kindLabel, IList<CMakeTargetInfo> targets)
        {
            TreeNode rootNode = new TreeNode("Linked Native Project: " + projectName);
            rootNode.ImageKey = SolutionImageKey;
            rootNode.SelectedImageKey = SolutionImageKey;
            _treeView.Nodes.Add(rootNode);

            string projLabel = string.IsNullOrEmpty(kindLabel) ? projectName : projectName + "  [" + kindLabel + "]";
            TreeNode projectNode = new TreeNode(projLabel);
            projectNode.ImageKey = ProjectImageKey;
            projectNode.SelectedImageKey = ProjectImageKey;
            rootNode.Nodes.Add(projectNode);

            if (targets != null)
            {
                foreach (CMakeTargetInfo t in targets)
                {
                    if (t == null) continue;
                    int count = t.SourceFiles != null ? t.SourceFiles.Count : 0;
                    TreeNode targetNode = new TreeNode(t.Name + "  (" + t.Kind + ", " + count + ")");
                    bool runnable = !string.IsNullOrEmpty(t.Kind) &&
                        t.Kind.IndexOf("Exec", StringComparison.OrdinalIgnoreCase) >= 0;
                    targetNode.ImageKey = runnable ? StartupProjectImageKey : ProjectImageKey;
                    targetNode.SelectedImageKey = targetNode.ImageKey;
                    targetNode.ToolTipText = t.Kind + " target: " + t.Name;
                    // Only executables carry the Build/Run tag — a plain static-library target
                    // (the shared "launcher shim" library here) is never run on its own.
                    if (runnable) targetNode.Tag = new LinkedCMakeTargetTag { TargetName = t.Name };
                    projectNode.Nodes.Add(targetNode);

                    if (t.SourceFiles != null)
                    {
                        foreach (string sf in t.SourceFiles)
                        {
                            if (string.IsNullOrEmpty(sf)) continue;
                            TreeNode fileNode = new TreeNode(Path.GetFileName(sf));
                            fileNode.Tag = new FileGroup { Project = null, FilePath = sf };
                            fileNode.ImageKey = FileImageKeyForPath(sf);
                            fileNode.SelectedImageKey = fileNode.ImageKey;
                            fileNode.ToolTipText = sf;
                            targetNode.Nodes.Add(fileNode);
                        }
                    }
                    StripCheckboxes(targetNode.Nodes);
                }
            }

            StripCheckboxes(rootNode.Nodes);
            rootNode.Expand();
            projectNode.Expand();
            UpdateEmptyState();
            return rootNode;
        }

        /// <summary>Removes a node previously returned by <see cref="AddLinkedNativeProjectNode"/> (solution close).</summary>
        public void RemoveLinkedNativeProjectNode(TreeNode node)
        {
            if (node == null) return;
            if (_treeView.Nodes.Contains(node)) _treeView.Nodes.Remove(node);
            UpdateEmptyState();
        }

        public void SetCheckedProjects(IEnumerable<string> projectNames)
        {
            if (_treeView.Nodes.Count == 0)
            {
                return;
            }

            var projectSet = new HashSet<string>();
            if (projectNames != null)
            {
                foreach (var name in projectNames)
                {
                    projectSet.Add(name);
                }
            }

            TreeNode solutionNode = _treeView.Nodes[0];
            foreach (TreeNode projectNode in solutionNode.Nodes)
            {
                var project = projectNode.Tag as ProjectModel;
                projectNode.Checked = project != null && projectSet.Contains(project.Name);
            }

            // Keep the unfiltered snapshot in step: clearing a search/kind filter
            // restores rows from it, and a snapshot captured with stale check state
            // used to blank every checkbox — desynchronizing the canvas (§7).
            MirrorChecksIntoSnapshot(projectSet);
        }

        // Applies the same project check set to the detached _unfilteredRoots clones.
        private void MirrorChecksIntoSnapshot(HashSet<string> projectSet)
        {
            if (_unfilteredRoots == null) return;
            foreach (TreeNode root in _unfilteredRoots)
            {
                foreach (TreeNode child in root.Nodes)
                {
                    var project = child.Tag as ProjectModel;
                    if (project != null) child.Checked = projectSet.Contains(project.Name);
                }
            }
        }

        public List<string> GetCheckedProjects()
        {
            var result = new List<string>();
            if (_treeView.Nodes.Count == 0)
            {
                return result;
            }

            TreeNode solutionNode = _treeView.Nodes[0];
            foreach (TreeNode projectNode in solutionNode.Nodes)
            {
                if (projectNode.Checked)
                {
                    var project = projectNode.Tag as ProjectModel;
                    if (project != null)
                    {
                        result.Add(project.Name);
                    }
                }
            }
            return result;
        }

        public void Clear()
        {
            _currentSolution = null;
            _fqnToNode.Clear();
            _treeView.Nodes.Clear();
            ClearLoadingText();
            UpdateEmptyState();
        }

        public void SelectType(string fullyQualifiedName)
        {
            TreeNode node;
            if (_fqnToNode.TryGetValue(fullyQualifiedName, out node))
            {
                _treeView.SelectedNode = node;
                node.EnsureVisible();
            }
        }

        // Select and reveal the file node for <paramref name="filePath"/> — used to sync the tree with
        // the active editor document. Returns true when a matching file node was found and selected.
        public bool SelectFileNode(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || _treeView == null) return false;
            TreeNode match = FindNodeForFile(_treeView.Nodes, filePath);
            if (match == null) return false;
            _treeView.SelectedNode = match;
            match.EnsureVisible();
            return true;
        }

        private static TreeNode FindNodeForFile(TreeNodeCollection nodes, string filePath)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is FileGroup fg && PathEquals(fg.FilePath, filePath)) return n;
                TreeNode inner = FindNodeForFile(n.Nodes, filePath);
                if (inner != null) return inner;
            }
            return null;
        }

        public void AddType(TypeModel type)
        {
            if (type == null || type.Project == null)
            {
                return;
            }

            TreeNode projectNode = FindProjectNode(type.Project.Name);
            if (projectNode == null)
            {
                return;
            }

            string fileKey = NormalizeFileKey(type.SourceFilePath);
            TreeNode fileNode = FindFileNode(projectNode, fileKey);
            if (fileNode == null)
            {
                fileNode = CreateFileNode(type.Project, fileKey, 0);
                // Place it under its package folders, same as the bulk load, so a type added after
                // the initial load doesn't reappear as a stray file at project level.
                FolderNodeFor(projectNode, type.Project, fileKey, null).Add(fileNode);
            }

            TreeNode typeNode = CreateTypeNode(type);
            fileNode.Nodes.Add(typeNode);
            _fqnToNode[type.FullyQualifiedName] = typeNode;

            fileNode.Text = FormatFileLabel(fileKey, fileNode.Nodes.Count);

            // Expand() is a no-op on an already-open row, so it cannot be relied on to raise
            // BeforeExpand for the rows just added.
            StripCheckboxes(fileNode.Nodes);
            if (fileNode.Parent != null) StripCheckboxes(fileNode.Parent.Nodes);

            fileNode.Expand();
            projectNode.Expand();
        }

        // Recurses through folder nodes: since iter 351 a file lives under its package chain, not as
        // a direct child of the project.
        private static TreeNode FindFileNode(TreeNode projectNode, string fileKey)
        {
            foreach (TreeNode child in projectNode.Nodes)
            {
                FileGroup fg = child.Tag as FileGroup;
                if (fg != null && string.Equals(fg.FilePath, fileKey, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
                if (child.Tag is FolderGroup)
                {
                    TreeNode hit = FindFileNode(child, fileKey);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        public void AddProject(ProjectModel project)
        {
            if (project == null || _treeView.Nodes.Count == 0)
            {
                return;
            }

            TreeNode solutionNode = _treeView.Nodes[0];
            var projectDisplayName = project.Name;
            TreeNode projectNode = new TreeNode(projectDisplayName);
            projectNode.Tag = project;
            projectNode.ImageKey = ProjectImageKeyForKind(project);
            projectNode.SelectedImageKey = projectNode.ImageKey;
            projectNode.Checked = true;
            solutionNode.Nodes.Add(projectNode);
            solutionNode.Expand();
        }

        public void RemoveType(string fullyQualifiedName)
        {
            TreeNode node;
            if (_fqnToNode.TryGetValue(fullyQualifiedName, out node))
            {
                _fqnToNode.Remove(fullyQualifiedName);
                TreeNode parent = node.Parent;
                node.Remove();

                // Update the enclosing file-group node's count (or drop it if empty).
                FileGroup fg = parent != null ? parent.Tag as FileGroup : null;
                if (fg != null)
                {
                    if (parent.Nodes.Count == 0)
                    {
                        parent.Remove();
                    }
                    else
                    {
                        parent.Text = FormatFileLabel(fg.FilePath, parent.Nodes.Count);
                    }
                }
            }
        }

        public void RemoveProject(string projectName)
        {
            TreeNode projectNode = FindProjectNode(projectName);
            if (projectNode != null)
            {
                // Remove FQN mappings for every type nested under the project's
                // file-group nodes. Function nodes aren't tracked in _fqnToNode,
                // so their presence is harmless.
                foreach (TreeNode fileNode in projectNode.Nodes)
                {
                    foreach (TreeNode childNode in fileNode.Nodes)
                    {
                        TypeModel type = childNode.Tag as TypeModel;
                        if (type != null)
                        {
                            _fqnToNode.Remove(type.FullyQualifiedName);
                        }
                    }
                }
                projectNode.Remove();
            }
        }

        public void UpdateType(TypeModel type)
        {
            if (type == null)
            {
                return;
            }

            TreeNode node;
            if (_fqnToNode.TryGetValue(type.FullyQualifiedName, out node))
            {
                node.Text = FormatTypeName(type);
                node.ImageKey = GetTypeImageKey(type.TypeKind);
                node.SelectedImageKey = node.ImageKey;
            }
        }

        public void RenameNodeInTree(TreeNode node, string newText)
        {
            if (node != null)
            {
                node.Text = newText;
            }
        }

        public void RefreshFromSolution()
        {
            if (_currentSolution != null)
            {
                LoadSolution(_currentSolution);
            }
        }

        public void BeginRenameSelectedNode()
        {
            if (_treeView.SelectedNode != null)
            {
                PrepareInlineRename(_treeView.SelectedNode);
                _treeView.LabelEdit = true;
                _treeView.SelectedNode.BeginEdit();
            }
        }

        private void InitializeControls()
        {
            SuspendLayout();

            _treeView = new TreeView();
            _treeView.Dock = DockStyle.Fill;
            _treeView.ShowRootLines = true;
            _treeView.ShowLines = true;
            _treeView.ShowPlusMinus = true;
            // Symbol documentation is shown through _symbolToolTip below rather than through
            // TreeNode.ToolTipText: the built-in node infotip truncates at 80 characters, which
            // cuts a one-line class docstring off mid-sentence.
            _treeView.ShowNodeToolTips = false;
            _treeView.HideSelection = false;
            _treeView.CheckBoxes = true;
            _treeView.ImageList = CreateImageList();
            _treeView.AfterSelect += OnTreeAfterSelect;
            _treeView.AfterCheck += OnTreeAfterCheck;
            // A row's checkbox is drawn lazily: the children of a node get theirs stripped just
            // before the node opens, so a 1599-file solution never pays for rows nobody has looked
            // at. See ShowsCheckbox for which rows keep one.
            _treeView.BeforeExpand += OnTreeBeforeExpand;
            _treeView.BeforeCollapse += OnTreeBeforeCollapse;
            // Double-click opens the element AND expands the row one level, so the immediate
            // children come into view without hunting for the +/- glyph. The default TreeView
            // toggles on the double-click before NodeMouseDoubleClick fires and at an unspecified
            // point relative to it, so the second MouseDown arms a cancel flag consumed by
            // BeforeExpand/BeforeCollapse: the built-in toggle never lands, and
            // ExpandOnDoubleClick performs the one deliberate expansion from the handler.
            _treeView.MouseDown += OnTreeMouseDown;
            _treeView.MouseUp += OnTreeMouseUp;
            _treeView.NodeMouseDoubleClick += OnTreeNodeDoubleClick;
            _treeView.NodeMouseClick += OnTreeNodeMouseClick;
            _treeView.AfterLabelEdit += OnTreeAfterLabelEdit;
            // §5.1: the tree is a drop target for its own file drags (move/copy a module
            // onto a file/folder/project row). Type/function drags still go outward only.
            _treeView.AllowDrop = true;
            _treeView.ItemDrag += OnTreeItemDrag;
            _treeView.DragOver += OnTreeDragOver;
            _treeView.DragDrop += OnTreeDragDrop;
            _treeView.KeyDown += OnTreeKeyDown;   // F2 = rename, Delete = delete (Solution-Explorer standard)

            // Hovering a class / method / function row shows its docstring, the same text the
            // editor's doctip shows for that symbol.
            _symbolToolTip = new ToolTip
            {
                InitialDelay = 500,
                ReshowDelay = 100,
                AutoPopDelay = 20000,
                ShowAlways = true,
                UseAnimation = false,
                UseFading = false,
            };
            _treeView.MouseMove += OnTreeMouseMoveForToolTip;
            _treeView.MouseLeave += (s, e) => HideSymbolToolTip();
            _treeView.MouseDown += (s, e) => HideSymbolToolTip();

            // Empty-state placeholder. The tree and this label both Dock=Fill and exactly one is
            // visible at a time (see UpdateEmptyState), so the Solution Explorer reads as a
            // deliberate empty state instead of a blank white void when nothing is loaded. Same
            // treatment the Editor tab already gets.
            _emptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = IdleEmptyText,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = LunaFonts.Sized(-0.5f),
                BackColor = SystemColors.Window,
            };

            // Incremental search/filter (SPPFE Rule 2). Typing narrows the tree to nodes whose text
            // matches (case-insensitive), keeping the ancestors of every match so context is
            // preserved; clearing it restores the full tree.
            _searchBox = new TextBox { Dock = DockStyle.Fill };
            _searchBox.TextChanged += OnSearchTextChanged;
            SetCueBanner(_searchBox, "Search symbols…");

            // SPPFE Sorting: a compact toggle to flip member layout between declaration order and
            // alphabetical. Sits to the right of the search box so both live on one header row.
            _sortButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 104,
                FlatStyle = FlatStyle.System,
                Text = "Sort: Decl",
                TabStop = false,
                Margin = Padding.Empty,
            };
            _sortButton.Click += OnSortButtonClick;

            // Collapse All — the signature Solution Explorer toolbar button, one click to fold a deep
            // tree back to the projects (previously only reachable from the right-click menu).
            _collapseAllButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 28,
                FlatStyle = FlatStyle.System,
                Text = "⊟",
                TabStop = false,
                Margin = Padding.Empty,
            };
            _collapseAllButton.Click += delegate { CollapseAllNodes(); };
            _headerToolTip = new ToolTip();
            _headerToolTip.SetToolTip(_collapseAllButton, "Collapse All");

            // Expand All — the companion to Collapse All, unfolds every project/file/type in one click.
            _expandAllButton = new Button
            {
                Dock = DockStyle.Right,
                Width = 28,
                FlatStyle = FlatStyle.System,
                Text = "⊞",
                TabStop = false,
                Margin = Padding.Empty,
            };
            _expandAllButton.Click += delegate { ExpandAllNodes(); };
            _headerToolTip.SetToolTip(_expandAllButton, "Expand All");

            // SPPFE Filtering: a kind dropdown on the left narrows the tree to one symbol kind and
            // composes with the text search. "All" is the no-op default.
            _kindCombo = new ComboBox
            {
                Dock = DockStyle.Left,
                Width = 92,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.System,
                TabStop = false,
            };
            _kindCombo.Items.AddRange(new object[] { "All", "Classes", "Interfaces", "Enums", "Methods", "Properties", "Fields", "Functions" });
            _kindCombo.SelectedIndex = 0;
            _kindCombo.SelectedIndexChanged += OnKindFilterChanged;

            // The header is two rows, but only ever SHOWS two when one cannot hold everything. Wide
            // enough, the search box fills the middle of the tool row exactly as before; too narrow,
            // it drops into the second row rather than being squeezed out of existence. See
            // ReflowHeader.
            _headerToolRow = new Panel { Dock = DockStyle.Top, Height = 24 };
            _headerToolRow.Controls.Add(_searchBox);         // Fill — added first so the edges reserve their space
            _headerToolRow.Controls.Add(_expandAllButton);   // Right — ends up left of Collapse All
            _headerToolRow.Controls.Add(_collapseAllButton); // Right — between Expand All and the sort toggle
            _headerToolRow.Controls.Add(_kindCombo);         // Left
            _headerToolRow.Controls.Add(_sortButton);        // Right — added last among Right, so it stays rightmost

            _headerSearchRow = new Panel { Dock = DockStyle.Top, Height = 24, Visible = false };

            Controls.Add(_treeView);
            Controls.Add(_emptyLabel);
            // Added last-to-first among the Top rows: the search row must sit BELOW the tool row when
            // both are showing, so it goes in first and the tool row docks above it.
            Controls.Add(_headerSearchRow);
            Controls.Add(_headerToolRow);
            ReflowHeader();
            Resize += delegate { ReflowHeader(); };
            UpdateEmptyState();

            ResumeLayout(false);
        }

        /// <summary>
        /// Puts the search box wherever there is actually room for it.
        ///
        /// On one row it competes with four fixed-width controls that together want 252px — wider
        /// than the Solution Explorer's own default dock width — so at every ordinary width the Fill
        /// took what was left, which was nothing, and the search box vanished with no hint that it
        /// existed. Below the threshold it gets a row to itself instead; above it, nothing changes
        /// and the panel looks exactly as it always did.
        /// </summary>
        private void ReflowHeader()
        {
            if (_headerToolRow == null || _headerSearchRow == null || _searchBox == null) return;

            int fixedWidth = _kindCombo.Width + _expandAllButton.Width
                           + _collapseAllButton.Width + _sortButton.Width;
            bool wrap = ClientSize.Width - fixedWidth < MinSearchWidth;
            // Tracked in a field, not read back off the row: Control.Visible reports the EFFECTIVE
            // visibility, so while the panel is still being built — parent not shown yet — it answers
            // false however it was set, and the shape would be recomputed on every resize.
            if (wrap == _headerWrapped) return;
            _headerWrapped = wrap;

            SuspendLayout();
            if (wrap)
            {
                _headerSearchRow.Controls.Add(_searchBox);
                _headerSearchRow.Visible = true;
            }
            else
            {
                _headerToolRow.Controls.Add(_searchBox);
                // Back to index 0: docked children are laid out from the END of the collection, so
                // the Fill must be first in it to be laid out LAST — i.e. into what the edges left.
                _headerToolRow.Controls.SetChildIndex(_searchBox, 0);
                _headerSearchRow.Visible = false;
            }
            ResumeLayout(true);
        }

        /// <summary>The idle placeholder: nothing is loaded and nothing is on its way.</summary>
        public static readonly string IdleEmptyText =
            "No solution open." + Environment.NewLine + Environment.NewLine
            + "Open one from the Welcome page," + Environment.NewLine
            + "File → Open (Ctrl+O)," + Environment.NewLine
            + "or drop a pyproject.toml here.";

        // True between BeginLoading and the LoadSolution/Clear that ends it. Kept as state rather
        // than inferred from the tree, because "empty" looks identical loading or idle.
        private bool _loading;

        /// <summary>
        /// Say that a solution is on its way.
        ///
        /// Loading a large solution takes real time — SymPy is 1599 files — and during that window
        /// the Solution Explorer used to read "No solution open. Open one from the Welcome page…",
        /// which is not merely unhelpful but the opposite of what is happening: it invites the user
        /// to start the load they are already waiting on. Caught in an iter-350 screenshot where the
        /// status bar read "Loading solution..." and this panel still said nothing was open.
        /// </summary>
        public void BeginLoading(string solutionName)
        {
            _loading = true;
            if (_emptyLabel != null)
            {
                _emptyLabel.Text = string.IsNullOrEmpty(solutionName)
                    ? "Loading solution…"
                    : "Loading " + solutionName + "…" + Environment.NewLine + Environment.NewLine
                      + "Reading projects and parsing sources.";
            }
            UpdateEmptyState();
        }

        /// <summary>Back to the idle placeholder — the load finished, failed, or was cancelled.</summary>
        public void EndLoading()
        {
            ClearLoadingText();
            UpdateEmptyState();
        }

        private void ClearLoadingText()
        {
            _loading = false;
            if (_emptyLabel != null) _emptyLabel.Text = IdleEmptyText;
        }

        /// <summary>Whether the panel is currently showing its loading placeholder.</summary>
        public bool IsShowingLoading { get { return _loading; } }

        /// <summary>
        /// Shows the placeholder only while the tree is empty. Both controls are Dock=Fill, so
        /// exactly one may be visible — two visible Fill siblings fight over the same area.
        /// </summary>
        private void UpdateEmptyState()
        {
            if (_emptyLabel == null || _treeView == null) return;
            bool empty = _treeView.Nodes.Count == 0;
            _emptyLabel.Visible = empty;
            _treeView.Visible = !empty;
        }

        public const string TypeDragFormat = "HelixPythonNamespace.TypeDrag.v1";
        public const string FunctionDragFormat = "HelixPythonNamespace.FunctionDrag.v1";
        public const string FileDragFormat = "HelixPythonNamespace.FileDrag.v1";

        private void OnTreeItemDrag(object sender, ItemDragEventArgs e)
        {
            TreeNode node = e.Item as TreeNode;
            if (node == null) return;

            if (node.Tag is TypeModel type)
            {
                // Canvas uses Link (place node in diagram); editor uses Copy (insert
                // import line); dropping onto a different file ROW WITHIN THIS TREE
                // is a Move (§5.1 AST-level rearrange) — only top-level classes
                // qualify (own DragOver/DragDrop below refuse nested ones).
                _treeView.DoDragDrop(
                    new DataObject(TypeDragFormat, type),
                    DragDropEffects.Link | DragDropEffects.Copy | DragDropEffects.Move);
                return;
            }

            if (node.Tag is FunctionModel fn)
            {
                _treeView.DoDragDrop(
                    new DataObject(FunctionDragFormat, fn),
                    DragDropEffects.Copy);
                return;
            }

            // §5.1: file rows drag too — within the tree that is a Move (re-home the
            // module; Ctrl copies), and the payload doubles as a FileDrop so the same
            // drag lands in Explorer or any external target.
            if (node.Tag is FileGroup dragFg && !string.IsNullOrEmpty(dragFg.FilePath))
            {
                var data = new DataObject(FileDragFormat, dragFg.FilePath);
                try
                {
                    var paths = new System.Collections.Specialized.StringCollection { dragFg.FilePath };
                    data.SetFileDropList(paths);
                }
                catch { }
                _treeView.DoDragDrop(data, DragDropEffects.Move | DragDropEffects.Copy);
            }
        }

        // §5.1: the tree accepts its own file drags — dropping onto a file/folder/project
        // row re-homes the module there (Move; hold Ctrl to Copy). Routed through the
        // same FilePasteRequested pipeline the clipboard uses, so the host's move/copy,
        // tab re-pointing and reload behavior are identical.
        private void OnTreeDragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (e.Data.GetDataPresent(TypeDragFormat))
            {
                // §5.1: a class dropped onto a different FILE row is a Move; nested
                // classes are refused up front (their source text can't be safely
                // pulled out of the enclosing body by this pass).
                TypeModel dragged = e.Data.GetData(TypeDragFormat) as TypeModel;
                TreeNode fileTarget = NodeAtScreenPoint(e.X, e.Y);
                FileGroup fg = fileTarget?.Tag as FileGroup;
                if (dragged != null && !dragged.IsNested && fg != null && !string.IsNullOrEmpty(fg.FilePath) &&
                    !PathsEqualOrdinalIgnoreCase(fg.FilePath, dragged.SourceFilePath))
                {
                    e.Effect = DragDropEffects.Move;
                    _treeView.SelectedNode = fileTarget;
                }
                return;
            }
            if (!e.Data.GetDataPresent(FileDragFormat)) return;
            TreeNode target = NodeAtScreenPoint(e.X, e.Y);
            if (!IsPasteTarget(target)) return;
            bool copy = (e.KeyState & 8) != 0;   // Ctrl held
            e.Effect = copy ? DragDropEffects.Copy : DragDropEffects.Move;
            _treeView.SelectedNode = target;     // live highlight of the drop target
        }

        private void OnTreeDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(TypeDragFormat))
            {
                TypeModel dragged = e.Data.GetData(TypeDragFormat) as TypeModel;
                TreeNode fileTarget = NodeAtScreenPoint(e.X, e.Y);
                FileGroup fg = fileTarget?.Tag as FileGroup;
                if (dragged == null || dragged.IsNested || fg == null || string.IsNullOrEmpty(fg.FilePath) ||
                    PathsEqualOrdinalIgnoreCase(fg.FilePath, dragged.SourceFilePath))
                    return;
                EventHandler<MoveTypeEventArgs> moveHandler = MoveTypeRequested;
                if (moveHandler != null) moveHandler(this, new MoveTypeEventArgs(dragged, fg));
                return;
            }
            if (!e.Data.GetDataPresent(FileDragFormat)) return;
            string sourcePath = e.Data.GetData(FileDragFormat) as string;
            if (string.IsNullOrEmpty(sourcePath)) return;
            TreeNode target = NodeAtScreenPoint(e.X, e.Y);
            if (!IsPasteTarget(target)) return;
            bool copy = (e.KeyState & 8) != 0;
            EventHandler<FilePasteEventArgs> handler = FilePasteRequested;
            if (handler != null)
                handler(this, new FilePasteEventArgs(sourcePath, !copy, target, target.Tag));
        }

        private static bool PathsEqualOrdinalIgnoreCase(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private TreeNode NodeAtScreenPoint(int screenX, int screenY)
        {
            Point client = _treeView.PointToClient(new Point(screenX, screenY));
            return _treeView.GetNodeAt(client);
        }

        private void OnTreeNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.Node != null)
            {
                _treeView.SelectedNode = e.Node;
                ShowContextMenuForNode(e.Node, e.Location);
            }
        }

        // Standard Solution-Explorer keyboard shortcuts on the selected node:
        //   F2     → inline rename (types/projects/solution) or ref-aware Rename Symbol (functions)
        //   Delete → delete the selected project, type, file, or package folder (host confirms first)
        // Mirrors exactly what the context menu already offers, so no node gains a new capability.
        private void OnTreeKeyDown(object sender, KeyEventArgs e)
        {
            TreeNode node = _treeView.SelectedNode;
            if (node == null) return;

            if (e.KeyCode == Keys.F2)
            {
                // §5.2: file rows rename inline too (the host renames the file on disk).
                // §5 (loop 533): F2 on a function is inline too, matching VS — a plain
                // rename-everywhere at solution scope. "Rename Symbol..." stays on the
                // context menu for when the user wants to pick a narrower scope.
                if (node.Tag is SolutionModel || node.Tag is ProjectModel || node.Tag is TypeModel ||
                    node.Tag is FileGroup || node.Tag is FunctionModel ||
                    (node.Tag is MemberModel mmF2 && mmF2.MemberKind != MemberKind.Constructor))
                {
                    BeginRenameSelectedNode();
                    e.Handled = e.SuppressKeyPress = true;
                }
            }
            else if (e.KeyCode == Keys.Delete)
            {
                // §5.9: file rows delete from disk too (host confirms first).
                if (node.Tag is ProjectModel || node.Tag is TypeModel || node.Tag is FileGroup ||
                    node.Tag is FunctionModel || node.Tag is MemberModel ||
                    (node.Tag is FolderGroup fgDel && fgDel.Project != null))
                {
                    RaiseDelete(node);
                    e.Handled = e.SuppressKeyPress = true;
                }
            }
            // §5.3: Cut/Copy a file row, Paste onto file/folder/project rows.
            else if (e.Control && e.KeyCode == Keys.C && node.Tag is FileGroup)
            {
                CopyFileNode(node, cut: false);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.X && node.Tag is FileGroup)
            {
                CopyFileNode(node, cut: true);
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.V && IsPasteTarget(node))
            {
                PasteIntoNode(node);
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void ShowContextMenuForNode(TreeNode node, Point location)
        {
            ContextMenuStrip menu = BuildContextMenuForNode(node);
            if (menu.Items.Count > 0)
            {
                menu.Show(_treeView, location);
            }
        }

        /// <summary>Raised for "Open in Class Designer Canvas" on any node; the host maps the tag to a canvas view.</summary>
        public event EventHandler<TreeOperationEventArgs> OpenInDesignerRequested;

        /// <summary>A command an add-in put on type nodes (IAddInHost.RegisterExplorerTypeCommand):
        /// shown wherever the "Open in ..." verbs are, enabled where <see cref="AppliesTo"/> says so.</summary>
        public sealed class TypeCommand
        {
            public string Caption { get; set; }
            public Func<TypeModel, bool> AppliesTo { get; set; }
            public Action<TypeModel> Run { get; set; }
        }

        private readonly List<TypeCommand> _typeCommands = new List<TypeCommand>();

        /// <summary>Commands shown after "Open in Class Designer Canvas", in registration order.</summary>
        public void AddTypeCommand(TypeCommand command)
        {
            if (command != null && !_typeCommands.Contains(command)) _typeCommands.Add(command);
        }

        public void RemoveTypeCommand(TypeCommand command)
        {
            _typeCommands.Remove(command);
        }

        /// <summary>
        /// The type a type command is asked about for <paramref name="node"/>: the node's own type,
        /// or, for a file node, a stand-in named after the file whose <c>SourceFilePath</c> is the
        /// file (no base types, so only a file-based rule such as a design sidecar can apply to it).
        /// Null for every other node, which shows the commands disabled.
        /// </summary>
        internal static TypeModel TypeForCommands(TreeNode node)
        {
            if (node == null) return null;
            var type = node.Tag as TypeModel;
            if (type != null) return type;
            var file = node.Tag as FileGroup;
            if (file == null || string.IsNullOrEmpty(file.FilePath)) return null;
            string stem;
            try { stem = System.IO.Path.GetFileNameWithoutExtension(file.FilePath); }
            catch (ArgumentException) { return null; }
            return new TypeModel(stem, file.Project != null ? file.Project.Name : null, TypeKind.Static, file.Project)
            {
                SourceFilePath = file.FilePath,
            };
        }

        /// <summary>A command an add-in put on file nodes: shown when <see cref="AppliesTo"/> says so.</summary>
        public sealed class FileCommand
        {
            public string Caption { get; set; }
            public Func<string, bool> AppliesTo { get; set; }

            /// <summary>Null, or the reason the command cannot run on a file: then it is shown
            /// disabled with this as its tooltip. Null itself (or a null answer) means enabled.</summary>
            public Func<string, string> DisabledReason { get; set; }
            public Action<string> Run { get; set; }
        }

        private readonly List<FileCommand> _fileCommands = new List<FileCommand>();

        /// <summary>Commands shown on a file node's context menu, after the built-in verbs.</summary>
        public void AddFileCommand(FileCommand command)
        {
            if (command != null && !_fileCommands.Contains(command)) _fileCommands.Add(command);
        }

        public void RemoveFileCommand(FileCommand command)
        {
            _fileCommands.Remove(command);
        }

        /// <summary>
        /// The file commands that apply to <paramref name="path"/>. One that throws while deciding
        /// is left out: a bad add-in must not stop the context menu from opening.
        /// </summary>
        private IEnumerable<FileCommand> FileCommandsFor(string path)
        {
            foreach (FileCommand command in _fileCommands.ToArray())
            {
                bool applies;
                try { applies = command.AppliesTo == null || command.AppliesTo(path); }
                catch (Exception) { applies = false; }
                if (applies) yield return command;
            }
        }

        /// <summary>Raised for "Analyze" on a project node (§5.9); the host runs the analysis engine.</summary>
        public event EventHandler<TreeOperationEventArgs> AnalyzeRequested;

        private void RaiseAnalyze(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AnalyzeRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseOpenInDesigner(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = OpenInDesignerRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        // §5.4: the file (module) that "Open in Editor" opens for a given node, or null
        // when the node has no backing file (which disables the command).
        internal string EditorPathForNode(TreeNode node)
        {
            if (node == null || node.Tag == null) return null;
            if (node.Tag is FileGroup fg) return string.IsNullOrEmpty(fg.FilePath) ? null : fg.FilePath;
            if (node.Tag is TypeModel tm) return string.IsNullOrEmpty(tm.SourceFilePath) ? null : tm.SourceFilePath;
            if (node.Tag is FunctionModel fn) return string.IsNullOrEmpty(fn.SourceFilePath) ? null : fn.SourceFilePath;
            if (node.Tag is MemberModel && node.Parent != null && node.Parent.Tag is TypeModel parentType)
                return string.IsNullOrEmpty(parentType.SourceFilePath) ? null : parentType.SourceFilePath;
            if (node.Tag is ProjectModel pm) return string.IsNullOrEmpty(pm.FilePath) ? null : pm.FilePath;
            if (node.Tag is SolutionModel sm) return string.IsNullOrEmpty(sm.FilePath) ? null : sm.FilePath;
            if (node.Tag is FolderGroup folder && folder.Project != null && !string.IsNullOrEmpty(folder.RelativePath))
            {
                try
                {
                    string init = System.IO.Path.Combine(folder.Project.ProjectDirectory,
                        folder.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar), "__init__.py");
                    return System.IO.File.Exists(init) ? init : null;
                }
                catch (ArgumentException) { return null; }
            }
            return null;
        }

        // §5.4: every node leads with "Open in Editor" / "Open in Class Designer Canvas".
        // File rows keep their historical bold "Open" as the editor verb instead of a
        // duplicate item; disabled (not hidden) when the node has no backing file, so
        // the command's presence is uniform across the tree.
        private void AddOpenCommands(ContextMenuStrip menu, TreeNode node)
        {
            var fgTag = node.Tag as FileGroup;
            if (fgTag == null)
            {
                string editorPath = EditorPathForNode(node);
                var openEditor = new ToolStripMenuItem("Open in Editor");
                openEditor.Enabled = editorPath != null;
                string capturedPath = editorPath;
                openEditor.Click += delegate
                {
                    // Types/functions/members navigate to their exact declaration
                    // through the richer existing routes; plain files/TOMLs open by path.
                    if (node.Tag is TypeModel) { RaiseViewSource(node); return; }
                    if (node.Tag is FunctionModel f) { RaiseFunctionDoubleClicked(f); return; }
                    if (node.Tag is MemberModel && node.Parent != null && node.Parent.Tag is TypeModel pt)
                    {
                        OpenMemberInEditor(node, pt);
                        return;
                    }
                    EventHandler<string> h = FileDoubleClicked;
                    if (h != null && capturedPath != null) h(this, capturedPath);
                };
                menu.Items.Add(openEditor);
            }

            var openDesigner = new ToolStripMenuItem("Open in Class Designer Canvas");
            openDesigner.Enabled = !(node.Tag is StructuralGroup) && !(node.Tag is ProjectReferenceGroup);
            openDesigner.Click += delegate { RaiseOpenInDesigner(node); };
            menu.Items.Add(openDesigner);

            // Add-in verbs on types (IAddInHost.RegisterExplorerTypeCommand), for example the GUI
            // Designer Canvas add-in's "GUI Design View". Shown on every node that has the verbs
            // above and enabled only where the add-in says the type applies, as that item always
            // was; with the add-in removed there is no such item at all.
            TypeModel commandType = TypeForCommands(node);
            foreach (TypeCommand command in _typeCommands.ToArray())
            {
                TypeCommand c = command;
                bool applies = false;
                // A bad add-in must not stop the context menu from opening.
                try { applies = commandType != null && (c.AppliesTo == null || c.AppliesTo(commandType)); }
                catch (Exception) { applies = false; }
                var item = new ToolStripMenuItem(c.Caption) { Enabled = applies };
                item.Click += delegate { if (c.Run != null && commandType != null) c.Run(commandType); };
                menu.Items.Add(item);
            }

            menu.Items.Add(new ToolStripSeparator());
        }

        private void OpenMemberInEditor(TreeNode node, TypeModel parentType)
        {
            var member = node.Tag as MemberModel;
            if (member == null) return;
            if (member.SourceLineNumber > 0 && !string.IsNullOrEmpty(parentType.SourceFilePath))
            {
                RaiseFunctionDoubleClicked(new FunctionModel
                {
                    Name = member.Name,
                    SourceFilePath = parentType.SourceFilePath,
                    SourceLineNumber = member.SourceLineNumber,
                    Project = parentType.Project,
                });
            }
            else
            {
                RaiseTypeDoubleClicked(parentType);
            }
        }

        // Builds (without showing) the right-click menu for a tree node. Extracted so its shape and
        // the F2/Del shortcut hints on Rename/Delete are unit-testable.
        internal ContextMenuStrip BuildContextMenuForNode(TreeNode node)
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            // §5.4 header verbs for every recognized node kind (file rows add theirs
            // in their own branch, keeping the historical bold "Open" first).
            if (node.Tag is SolutionModel || node.Tag is ProjectModel || node.Tag is FolderGroup ||
                node.Tag is TypeModel || node.Tag is FunctionModel || node.Tag is MemberModel)
            {
                AddOpenCommands(menu, node);
            }

            if (node.Tag is SolutionModel)
            {
                menu.Items.Add("Add New Project...", null, delegate { RaiseAddProject(node); });
                menu.Items.Add("Add Existing Project...", null, delegate { RaiseAddExistingProject(node); });
                menu.Items.Add(new ToolStripSeparator());
                ((ToolStripMenuItem)menu.Items.Add("Rename", null, delegate { BeginRename(node); })).ShortcutKeyDisplayString = "F2";
                menu.Items.Add(new ToolStripSeparator());
                SolutionModel sln = node.Tag as SolutionModel;
                ToolStripMenuItem copySolutionPath = new ToolStripMenuItem("Copy Full Path");
                copySolutionPath.Enabled = sln != null && !string.IsNullOrEmpty(sln.FilePath);
                copySolutionPath.Click += delegate
                {
                    try { if (sln != null && !string.IsNullOrEmpty(sln.FilePath)) Clipboard.SetText(sln.FilePath); }
                    catch { /* clipboard access can fail transiently */ }
                };
                menu.Items.Add(copySolutionPath);
                ToolStripMenuItem revealSolutionFolder = new ToolStripMenuItem("Open Containing Folder");
                revealSolutionFolder.Enabled = sln != null && !string.IsNullOrEmpty(sln.FilePath);
                revealSolutionFolder.Click += delegate { OpenContainingFolder(sln.FilePath); };
                menu.Items.Add(revealSolutionFolder);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Save Solution", null, delegate { RaiseSave(node); });
                // §5.9: VS-parity Refresh — rebuild the tree from the current model
                // (check state survives, see LoadSolution).
                menu.Items.Add("Refresh", null, delegate { RefreshFromSolution(); });
            }
            else if (node.Tag is ProjectModel)
            {
                // Language-services layer (SRS §2.3: never expose functionality irrelevant to
                // the loaded language): startup/test/package-manager/scaffolding verbs and their
                // labels come from the language's descriptor — one home per decision, so fixing
                // one language's menu can never silently change another's.
                ProjectModel startupCandidate = node.Tag as ProjectModel;
                var projectLang = HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(startupCandidate.Language);
                // Only packages with a runnable entry point (__main__.py / main.py / *_main.py
                // for Python; the manifest's own executable convention elsewhere) can be
                // started, so libraries get a disabled menu item that explains why.
                // Launch verbs first: an executable package can be started from its own node
                // without being made the startup package. Placed above "Set as Startup" because
                // running the thing you pointed at is the more common intent, and requiring the
                // startup dance first is exactly the friction this removes.
                if (IsAppProject(startupCandidate))
                {
                    menu.Items.Add("Run", null, delegate { RaiseRunProject(node); });
                    menu.Items.Add("Start Debugging", null, delegate { RaiseDebugProject(node); });
                    menu.Items.Add(new ToolStripSeparator());
                }

                if (projectLang.StartupVerbLabel != null)
                {
                    bool canStartup = IsAppProject(startupCandidate);
                    string startupLabel = canStartup
                        ? projectLang.StartupVerbLabel
                        : projectLang.StartupVerbLabel + " (library — no main)";
                    var startupItem = new ToolStripMenuItem(startupLabel, null, delegate { RaiseSetStartupProject(node); });
                    startupItem.Enabled = canStartup;
                    menu.Items.Add(startupItem);

                    // Command-line arguments for Run/Debug (VS's launch-profile "Command line
                    // arguments"). Only meaningful for something that can actually be launched, so
                    // it shares the startup verb's eligibility gate.
                    var argsItem = new ToolStripMenuItem(
                        string.IsNullOrEmpty(startupCandidate.RunArguments)
                            ? "Run Arguments..."
                            : "Run Arguments: " + Ellipsize(startupCandidate.RunArguments, 40),
                        null, delegate { RaiseEditRunArguments(node); });
                    argsItem.Enabled = canStartup;
                    menu.Items.Add(argsItem);
                    menu.Items.Add(new ToolStripSeparator());
                }
                menu.Items.Add("Build Project", null, delegate { RaiseBuildProject(node); });
                // A project holding test files (per the language's own naming convention) gets
                // VS2022 MSTest-runner-style per-project Run/Debug Tests verbs — the IDE's test
                // runner otherwise only offers whole-solution "Run All"/"Run Failed" from the
                // Tests menu. Debug Tests only where a real debug-tests pipeline exists
                // (Python's debugpy launch) — it used to appear on C++ test packages and run
                // pytest against .cpp sources.
                if (HasTestFiles(startupCandidate))
                {
                    if (projectLang.SupportsRunTests)
                        menu.Items.Add("Run Tests", null, delegate { RaiseRunTests(node); });
                    if (projectLang.SupportsDebugTests)
                        menu.Items.Add("Debug Tests", null, delegate { RaiseDebugTests(node); });
                }
                // §5.9: VS-parity Analyze — runs the solution analysis engine.
                menu.Items.Add("Analyze", null, delegate { RaiseAnalyze(node); });
                if (projectLang.SupportsPackageManager)
                {
                    // Each verb appears only when the language actually has it — a language with
                    // have a restore-equivalent but no manage dialog, so manage stays hidden.
                    if (projectLang.RestorePackagesMenuLabel != null)
                        menu.Items.Add(projectLang.RestorePackagesMenuLabel, null, delegate { RaiseRestorePackages(node); });
                    if (projectLang.ManagePackagesMenuLabel != null)
                        menu.Items.Add(projectLang.ManagePackagesMenuLabel, null, delegate { RaiseManagePackages(node); });
                }
                menu.Items.Add(new ToolStripSeparator());
                // Add Class / Interface / Abstract Class create real code through the
                // type-scaffolding services, which only exist for some languages (descriptor
                // trait) — plain file/folder verbs below stay for everyone.
                if (projectLang.SupportsTypeScaffolding)
                {
                    menu.Items.Add("Add Class", null, delegate { RaiseAddClass(node); });
                    menu.Items.Add("Add Interface", null, delegate { RaiseAddInterface(node); });
                    menu.Items.Add("Add Abstract Class", null, delegate { RaiseAddAbstractClass(node); });
                }
                menu.Items.Add("Add File...", null, delegate { RaiseAddFile(node); });
                // Python only: the notebook kernel is a Python process, so the verb would be an
                // offer the IDE cannot keep anywhere else.
                if (startupCandidate != null && startupCandidate.Language == ProjectLanguage.Python)
                    menu.Items.Add("Add Jupyter Notebook...", null, delegate { RaiseAddNotebook(node); });
                menu.Items.Add("Add Existing File...", null, delegate { RaiseAddExistingFile(node); });
                menu.Items.Add("New Folder", null, delegate { RaiseNewFolder(node); });
                menu.Items.Add(new ToolStripSeparator());
                var projectPaste = (ToolStripMenuItem)menu.Items.Add("Paste", null, delegate { PasteIntoNode(node); });
                projectPaste.ShortcutKeyDisplayString = "Ctrl+V";
                projectPaste.Enabled = HasFileClipboard;
                menu.Items.Add(new ToolStripSeparator());
                int projectIndex = startupCandidate.Solution == null
                    ? -1 : startupCandidate.Solution.Projects.IndexOf(startupCandidate);
                var moveUp = (ToolStripMenuItem)menu.Items.Add("Move Up", null,
                    delegate { RaiseMoveProjectUp(node); });
                moveUp.Enabled = projectIndex > 0;
                var moveDown = (ToolStripMenuItem)menu.Items.Add("Move Down", null,
                    delegate { RaiseMoveProjectDown(node); });
                moveDown.Enabled = projectIndex >= 0 && startupCandidate.Solution != null &&
                    projectIndex < startupCandidate.Solution.Projects.Count - 1;
                menu.Items.Add(new ToolStripSeparator());
                ((ToolStripMenuItem)menu.Items.Add("Rename", null, delegate { BeginRename(node); })).ShortcutKeyDisplayString = "F2";
                // "Rename Package..." moves a directory and rewrites dotted-import statements —
                // a namespace-to-folder convention only some languages have (descriptor trait;
                // see CppSymbolRenameService's CppRenameScope doc comment for the C++ rationale).
                // Plain F2 "Rename" above still renames the ProjectModel's display name.
                if (projectLang.SupportsPackageRename)
                    menu.Items.Add("Rename Package...", null, delegate { RaiseRenamePackage(node); });
                menu.Items.Add("Add Project Reference...", null, delegate { RaiseAddProjectReference(node); });
                menu.Items.Add(new ToolStripSeparator());
                // Two distinct exits, VS-style: Remove keeps the files, Delete destroys them.
                menu.Items.Add("Remove from Solution", null, delegate { RaiseRemoveProject(node); });
                ((ToolStripMenuItem)menu.Items.Add("Delete from Disk", null, delegate { RaiseDelete(node); })).ShortcutKeyDisplayString = "Del";
                menu.Items.Add(new ToolStripSeparator());
                // Show the package's own folder in Explorer. Given its own group rather than sitting
                // under "Delete from Disk": both act on the same directory, and a harmless navigation
                // verb directly beneath a destructive one is a mis-click waiting to happen.
                //
                // The manifest path is handed to RevealInExplorer rather than the bare directory, so
                // the folder opens with the project's own pyproject.toml/.csproj selected — the same
                // thing the solution node's "Open Containing Folder" does one level up.
                string projectManifest = startupCandidate != null ? startupCandidate.FilePath : null;
                // Same label as the solution node's verb, on purpose: the node you right-clicked is
                // what says which folder "containing" means, exactly as it does in Visual Studio.
                var openProjectLocation = new ToolStripMenuItem("Open Containing Folder");
                openProjectLocation.Enabled = !string.IsNullOrEmpty(projectManifest);
                openProjectLocation.ToolTipText = string.IsNullOrEmpty(projectManifest)
                    ? "This project has no manifest on disk, so it has no directory to open."
                    : ProjectDirectoryOf(projectManifest);
                openProjectLocation.Click += delegate { RevealInExplorer(projectManifest); };
                menu.Items.Add(openProjectLocation);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Save Project", null, delegate { RaiseSave(node); });
                menu.Items.Add("Properties", null, delegate { RaiseProperties(node); });
            }
            else if (node.Tag is StructuralGroup)
            {
                StructuralGroup group = (StructuralGroup)node.Tag;
                // Dependencies is the natural VS-style entry point for adding references. The
                // project row keeps the same command as a convenience, but users should not have
                // to discover it there while looking directly at the dependency collection.
                if (string.Equals(group.Kind, "Dependencies", StringComparison.Ordinal) &&
                    group.Project != null)
                {
                    menu.Items.Add("Add Reference...", null, delegate { RaiseAddProjectReference(node); });
                }
            }
            else if (node.Tag is ProjectReferenceGroup)
            {
                ProjectReferenceGroup prg = (ProjectReferenceGroup)node.Tag;
                menu.Items.Add("Remove Reference", null, delegate
                {
                    EventHandler<TreeOperationEventArgs> handler = RemoveProjectReferenceRequested;
                    if (handler != null) handler(this, new TreeOperationEventArgs(node, prg));
                });
            }
            else if (node.Tag is UnresolvedReferenceGroup)
            {
                UnresolvedReferenceGroup urg = (UnresolvedReferenceGroup)node.Tag;
                menu.Items.Add("Remove Reference", null, delegate
                {
                    EventHandler<TreeOperationEventArgs> handler = RemoveProjectReferenceRequested;
                    if (handler != null) handler(this, new TreeOperationEventArgs(node, urg));
                });
            }
            else if (node.Tag is LinkedCMakeTargetTag)
            {
                LinkedCMakeTargetTag t = (LinkedCMakeTargetTag)node.Tag;
                ToolStripMenuItem build = new ToolStripMenuItem("Build " + t.TargetName);
                build.Click += delegate { LinkedCMakeTargetBuildRequested?.Invoke(this, t); };
                menu.Items.Add(build);
                ToolStripMenuItem run = new ToolStripMenuItem("Run " + t.TargetName);
                run.Click += delegate { LinkedCMakeTargetRunRequested?.Invoke(this, t); };
                menu.Items.Add(run);
            }
            else if (node.Tag is FileGroup)
            {
                FileGroup fg = (FileGroup)node.Tag;

                // Open in the editor — the primary action for a file, matching double-click. Shown
                // first (bold, like Visual Studio's default verb) so right-click ▸ Open just works.
                ToolStripMenuItem open = new ToolStripMenuItem("Open", null, delegate { RaiseFileOpen(fg); });
                open.Enabled = !string.IsNullOrEmpty(fg.FilePath);
                open.Font = new Font(open.Font, FontStyle.Bold);
                menu.Items.Add(open);
                // §5.4: files carry the designer verb too (the host reveals the file's
                // types on the canvas).
                var fileDesigner = new ToolStripMenuItem("Open in Class Designer Canvas");
                fileDesigner.Enabled = !string.IsNullOrEmpty(fg.FilePath);
                fileDesigner.Click += delegate { RaiseOpenInDesigner(node); };
                menu.Items.Add(fileDesigner);

                // Add-in verbs (IAddInHost.RegisterExplorerFileCommand), only where they apply.
                if (!string.IsNullOrEmpty(fg.FilePath))
                {
                    foreach (FileCommand command in FileCommandsFor(fg.FilePath))
                    {
                        FileCommand c = command;
                        string path = fg.FilePath;
                        var item = new ToolStripMenuItem(c.Caption, null, delegate { if (c.Run != null) c.Run(path); });
                        // A command that applies but cannot run here is shown disabled with its
                        // reason: that teaches what it wants, where hiding it leaves the user guessing.
                        string reason = null;
                        try { if (c.DisabledReason != null) reason = c.DisabledReason(path); }
                        catch (Exception) { reason = "This command failed to check the file."; }
                        if (reason != null)
                        {
                            item.Enabled = false;
                            item.ToolTipText = reason;
                        }
                        menu.Items.Add(item);
                    }
                }

                menu.Items.Add(new ToolStripSeparator());

                ToolStripMenuItem copyPath = new ToolStripMenuItem("Copy Full Path");
                copyPath.Enabled = !string.IsNullOrEmpty(fg.FilePath);
                copyPath.Click += delegate
                {
                    try { if (!string.IsNullOrEmpty(fg.FilePath)) Clipboard.SetText(fg.FilePath); }
                    catch { /* clipboard access can fail transiently */ }
                };
                menu.Items.Add(copyPath);

                // Path relative to the solution folder — the form that travels well in bug reports and
                // imports, and matches how the tree names the file. Standard next to Copy Full Path.
                string solutionDir = _currentSolution != null && !string.IsNullOrEmpty(_currentSolution.FilePath)
                    ? Path.GetDirectoryName(_currentSolution.FilePath) : null;
                ToolStripMenuItem copyRel = new ToolStripMenuItem("Copy Relative Path");
                copyRel.Enabled = !string.IsNullOrEmpty(fg.FilePath) && !string.IsNullOrEmpty(solutionDir);
                copyRel.Click += delegate
                {
                    try { Clipboard.SetText(RelativePath(solutionDir, fg.FilePath)); }
                    catch { /* clipboard access can fail transiently */ }
                };
                menu.Items.Add(copyRel);

                // Reveal the file in Windows Explorer with it selected — a standard file-node verb.
                ToolStripMenuItem revealFolder = new ToolStripMenuItem("Open Containing Folder");
                revealFolder.Enabled = !string.IsNullOrEmpty(fg.FilePath);
                revealFolder.Click += delegate { OpenContainingFolder(fg.FilePath); };
                menu.Items.Add(revealFolder);

                // §5.9: VS-parity "Open With…" — the Windows open-with picker, so a
                // .py can go to an external editor or a .png to a viewer.
                ToolStripMenuItem openWith = new ToolStripMenuItem("Open With...");
                openWith.Enabled = !string.IsNullOrEmpty(fg.FilePath);
                openWith.Click += delegate { ShowOpenWithDialog(fg.FilePath); };
                menu.Items.Add(openWith);

                // File node — Add Class/Interface passes THIS node (the FileGroup carries its
                // project), so the host creates the type in the clicked file's own package rather
                // than the project's default namespace. §5.7: the element is created under the
                // exact node the command was invoked from.
                // §5.3: standard clipboard verbs on a file row.
                menu.Items.Add(new ToolStripSeparator());
                var cutItem = (ToolStripMenuItem)menu.Items.Add("Cut", null, delegate { CopyFileNode(node, true); });
                cutItem.ShortcutKeyDisplayString = "Ctrl+X";
                cutItem.Enabled = !string.IsNullOrEmpty(fg.FilePath);
                var copyItem = (ToolStripMenuItem)menu.Items.Add("Copy", null, delegate { CopyFileNode(node, false); });
                copyItem.ShortcutKeyDisplayString = "Ctrl+C";
                copyItem.Enabled = !string.IsNullOrEmpty(fg.FilePath);
                var pasteItem = (ToolStripMenuItem)menu.Items.Add("Paste", null, delegate { PasteIntoNode(node); });
                pasteItem.ShortcutKeyDisplayString = "Ctrl+V";
                pasteItem.Enabled = HasFileClipboard;

                if (fg.Project != null)
                {
                    // Scaffolding verbs on a file row target the clicked file's own package;
                    // only languages with a scaffolding service get them (descriptor trait).
                    if (HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(fg.Project.Language).SupportsTypeScaffolding)
                    {
                        menu.Items.Add(new ToolStripSeparator());
                        menu.Items.Add("Add Class", null, delegate { RaiseAddClass(node); });
                        menu.Items.Add("Add Interface", null, delegate { RaiseAddInterface(node); });
                        menu.Items.Add("Add Abstract Class", null, delegate { RaiseAddAbstractClass(node); });
                    }
                    menu.Items.Add(new ToolStripSeparator());
                    // §5.2: file rows rename on disk via the same inline-edit path.
                    var renameFile = (ToolStripMenuItem)menu.Items.Add("Rename", null, delegate { BeginRename(node); });
                    renameFile.ShortcutKeyDisplayString = "F2";
                    renameFile.Enabled = !string.IsNullOrEmpty(fg.FilePath);
                    // Package rename is a language trait (namespace ↔ directory), not a file trait.
                    if (HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(fg.Project.Language).SupportsPackageRename)
                        menu.Items.Add("Rename Containing Package...", null, delegate { RaiseRenamePackage(node); });
                    // §5.9: VS-parity Delete on a file row — the host confirms and
                    // deletes from disk.
                    var deleteFile = (ToolStripMenuItem)menu.Items.Add("Delete", null, delegate { RaiseDelete(node); });
                    deleteFile.ShortcutKeyDisplayString = "Del";
                    deleteFile.Enabled = !string.IsNullOrEmpty(fg.FilePath);

                    // §5.9: VS-parity Exclude from Project — the file stops appearing
                    // anywhere in the Explorer/analysis until re-Included.
                    var excludeFile = menu.Items.Add("Exclude from Project", null, delegate { RaiseExcludeFromProject(node); });
                    excludeFile.Enabled = !string.IsNullOrEmpty(fg.FilePath);
                }
            }
            else if (node.Tag is FolderGroup folderGroup && folderGroup.Project != null)
            {
                // §5.7: creating from a folder targets that exact folder's package —
                // the host resolves the namespace from the folder's relative path.
                // Scaffolding verbs only where a scaffolding service exists (descriptor trait).
                if (HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(folderGroup.Project.Language).SupportsTypeScaffolding)
                {
                    menu.Items.Add("Add Class", null, delegate { RaiseAddClass(node); });
                    menu.Items.Add("Add Interface", null, delegate { RaiseAddInterface(node); });
                    menu.Items.Add("Add Abstract Class", null, delegate { RaiseAddAbstractClass(node); });
                }
                menu.Items.Add("Add File...", null, delegate { RaiseAddFile(node); });
                if (folderGroup.Project.Language == ProjectLanguage.Python)
                    menu.Items.Add("Add Jupyter Notebook...", null, delegate { RaiseAddNotebook(node); });
                menu.Items.Add("Add Existing File...", null, delegate { RaiseAddExistingFile(node); });
                menu.Items.Add("New Folder", null, delegate { RaiseNewFolder(node); });
                menu.Items.Add(new ToolStripSeparator());
                var folderPaste = (ToolStripMenuItem)menu.Items.Add("Paste", null, delegate { PasteIntoNode(node); });
                folderPaste.ShortcutKeyDisplayString = "Ctrl+V";
                folderPaste.Enabled = HasFileClipboard;
                menu.Items.Add(new ToolStripSeparator());
                // Package rename is a language trait (namespace ↔ directory) — see the
                // ProjectModel branch above for the rationale.
                if (HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(folderGroup.Project.Language).SupportsPackageRename)
                    menu.Items.Add("Rename Package...", null, delegate { RaiseRenamePackage(node); });
                ((ToolStripMenuItem)menu.Items.Add("Delete Package", null, delegate { RaiseDelete(node); })).ShortcutKeyDisplayString = "Del";
                menu.Items.Add("Exclude from Project", null, delegate { RaiseExcludeFromProject(node); });
            }
            else if (node.Tag is ExcludedEntry)
            {
                menu.Items.Add("Include in Project", null, delegate { RaiseIncludeInProject(node); });
            }
            else if (node.Tag is TypeModel)
            {
                // "Go to Designer"/"Go to Source" used to live here, but AddOpenCommands (called for
                // every TypeModel node above) already adds "Open in Class Designer Canvas"/"Open in
                // Editor" — and for a TypeModel those route through the EXACT SAME handlers
                // (RaiseOpenInDesigner → OnTreeOpenInDesigner special-cases TypeModel to call
                // OnTreeGoToDesigner; "Open in Editor"'s click handler calls RaiseViewSource
                // directly), making the pair pure duplicates in the menu.
                // Symbol edits (Rename/Rename Symbol/Delete/Save) only for languages whose
                // rename/rewrite services exist (descriptor trait) — the others keep the
                // navigation and canvas verbs but never route into another language's rewriter.
                if (SupportsCodeModelEdits(node))
                {
                    ((ToolStripMenuItem)menu.Items.Add("Rename", null, delegate { BeginRename(node); })).ShortcutKeyDisplayString = "F2";
                    menu.Items.Add("Rename Symbol...", null, delegate { RaiseRenameSymbol(node); });
                    ((ToolStripMenuItem)menu.Items.Add("Delete", null, delegate { RaiseDelete(node); })).ShortcutKeyDisplayString = "Del";
                    menu.Items.Add(new ToolStripSeparator());
                }
                menu.Items.Add("Hide from Canvas", null, delegate { RaiseHide(node); });
                menu.Items.Add("Unhide", null, delegate { RaiseUnhide(node); });
                if (SupportsCodeModelEdits(node))
                {
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("Save", null, delegate { RaiseSave(node); });
                }
            }
            else if (node.Tag is FunctionModel)
            {
                // §5 (loop 533): F2 is the quick inline rename (solution-wide); "Rename
                // Symbol..." stays for picking a narrower scope, matching the type-node menu shape.
                if (SupportsCodeModelEdits(node))
                {
                    ((ToolStripMenuItem)menu.Items.Add("Rename", null, delegate { BeginRename(node); })).ShortcutKeyDisplayString = "F2";
                    menu.Items.Add("Rename Symbol...", null, delegate { RaiseRenameSymbol(node); });
                    menu.Items.Add("Edit Signature...", null, delegate { RaiseEditSignature(node); });
                    menu.Items.Add(new ToolStripSeparator());
                    ((ToolStripMenuItem)menu.Items.Add("Delete", null, delegate { RaiseDelete(node); })).ShortcutKeyDisplayString = "Del";
                }
            }
            else if (node.Tag is MemberModel memberTag)
            {
                // A method/property/field can be renamed like any other symbol; the constructor
                // (__init__) cannot — it's a fixed dunder Python calls automatically, and renaming
                // it would just break the class, so the command stays visible but disabled rather
                // than silently vanishing (consistent with this tree's "uniform presence" rule for
                // node-kind-appropriate commands elsewhere, e.g. AddOpenCommands).
                if (SupportsCodeModelEdits(node))
                {
                    bool canRename = memberTag.MemberKind != MemberKind.Constructor;
                    var renameMember = (ToolStripMenuItem)menu.Items.Add("Rename", null, delegate { BeginRename(node); });
                    renameMember.ShortcutKeyDisplayString = "F2";
                    renameMember.Enabled = canRename;
                    if (!canRename) renameMember.ToolTipText = "The constructor (__init__) cannot be renamed.";
                    menu.Items.Add(new ToolStripSeparator());
                    ((ToolStripMenuItem)menu.Items.Add("Delete", null, delegate { RaiseDelete(node); })).ShortcutKeyDisplayString = "Del";
                }
            }

            // Tree-wide expand/collapse, available on every node the way Solution Explorer offers them.
            // Only meaningful when there is a hierarchy to fold, so gate on having any top-level node.
            if (_treeView != null && _treeView.Nodes.Count > 0)
            {
                if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Expand All", null, delegate { ExpandAllNodes(); });
                menu.Items.Add("Collapse All", null, delegate { CollapseAllNodes(); });
            }

            return menu;
        }

        // Expand the whole tree (a user-initiated action; the lazy checkbox stripping still runs via
        // BeforeExpand so a large solution stays responsive).
        private void ExpandAllNodes()
        {
            _treeView.BeginUpdate();
            try { _treeView.ExpandAll(); }
            finally { _treeView.EndUpdate(); }
        }

        // Collapse everything, then re-open the solution root so the top-level projects stay visible —
        // the same result Visual Studio's "Collapse All" gives.
        private void CollapseAllNodes()
        {
            _treeView.BeginUpdate();
            try
            {
                _treeView.CollapseAll();
                if (_treeView.Nodes.Count > 0) _treeView.Nodes[0].Expand();
            }
            finally { _treeView.EndUpdate(); }
        }

        // Reveal a file in Windows Explorer, selecting it. Best-effort — a shell launch can fail and
        // is simply ignored. Falls back to opening the containing directory when the file is gone.
        private static void OpenContainingFolder(string filePath)
        {
            RevealInExplorer(filePath);
        }

        /// <summary>
        /// The directory a project's manifest lives in — the package folder itself, which for every
        /// workspace member is a direct child of the solution directory named after the project.
        /// Returns the manifest path unchanged if it has no directory part, so a caller using this
        /// for display never ends up showing an empty string.
        /// </summary>
        internal static string ProjectDirectoryOf(string manifestPath)
        {
            if (string.IsNullOrEmpty(manifestPath)) return "";
            try
            {
                string directory = Path.GetDirectoryName(manifestPath);
                return string.IsNullOrEmpty(directory) ? manifestPath : directory;
            }
            catch (ArgumentException) { return manifestPath; }
        }

        /// <summary>
        /// Reveal a file in Windows Explorer with it selected. Canonicalizes first (CMake-derived
        /// paths carry forward slashes, which `explorer /select,` silently rejects), then prefers
        /// the SHELL API — `explorer /select,"…"` ALSO fails for paths beyond MAX_PATH (260) and
        /// opens the DESKTOP instead; SHOpenFolderAndSelectItems handles long paths. The command
        /// line remains as fallback. Shared with the editor tab's "Open File Location".
        /// </summary>
        public static void RevealInExplorer(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            try
            {
                filePath = Path.GetFullPath(filePath);
                if (File.Exists(filePath))
                {
                    if (TryRevealWithShellApi(filePath)) return;
                    System.Diagnostics.Process.Start("explorer.exe", ExplorerSelectArgs(filePath));
                    return;
                }
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    if (TryRevealWithShellApi(dir)) return;
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch { /* shell launch can fail transiently */ }
        }

        private static bool TryRevealWithShellApi(string fullPath)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                pidl = ILCreateFromPathW(fullPath);
                if (pidl == IntPtr.Zero) return false;
                return SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) == 0;
            }
            catch { return false; }
            finally
            {
                if (pidl != IntPtr.Zero) ILFree(pidl);
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr ILCreateFromPathW(string pszPath);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        // The explorer.exe arguments that open the folder with <paramref name="filePath"/> selected.
        internal static string ExplorerSelectArgs(string filePath)
        {
            // Backslashes only: explorer's /select argument does not accept forward slashes.
            try { filePath = Path.GetFullPath(filePath); } catch { }
            return "/select,\"" + filePath + "\"";
        }

        // §5.9 "Open With…": the Windows open-with picker for this file. Best-effort
        // like the other shell verbs.
        private static void ShowOpenWithDialog(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            try
            {
                System.Diagnostics.Process.Start("rundll32.exe", OpenWithArgs(filePath));
            }
            catch { /* shell launch can fail transiently */ }
        }

        // The rundll32 arguments that show the shell's Open With dialog for a file.
        internal static string OpenWithArgs(string filePath)
        {
            return "shell32.dll,OpenAs_RunDLL " + filePath;
        }

        // <paramref name="filePath"/> expressed relative to <paramref name="baseDir"/> using backslashes,
        // or the full path when it lies outside the base (or either is missing).
        internal static string RelativePath(string baseDir, string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath ?? string.Empty;
            if (string.IsNullOrEmpty(baseDir)) return filePath;
            try
            {
                string baseWithSep = baseDir.TrimEnd('\\', '/') + "\\";
                Uri baseUri = new Uri(baseWithSep);
                Uri fileUri = new Uri(filePath);
                if (!baseUri.IsBaseOf(fileUri)) return filePath;   // outside the solution tree
                string rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString());
                return rel.Replace('/', '\\');
            }
            catch { return filePath; }
        }

        private void BeginRename(TreeNode node)
        {
            PrepareInlineRename(node);
            _treeView.LabelEdit = true;
            node.BeginEdit();
        }

        private void PrepareInlineRename(TreeNode node)
        {
            ProjectModel project = node == null ? null : node.Tag as ProjectModel;
            if (project == null) return;
            _inlineProjectRenameNode = node;
            _inlineProjectDisplayText = node.Text;
            // The native TreeView edit box edits TreeNode.Text verbatim. Temporarily expose only
            // the model name; kind/startup suffixes are presentation markers, not rename input.
            node.Text = project.Name;
        }

        private void OnTreeAfterLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            _treeView.LabelEdit = false;

            string oldEditableText = e.Node.Text;
            if (ReferenceEquals(e.Node, _inlineProjectRenameNode))
            {
                oldEditableText = (e.Node.Tag as ProjectModel)?.Name ?? oldEditableText;
                e.Node.Text = _inlineProjectDisplayText ?? e.Node.Text;
                _inlineProjectRenameNode = null;
                _inlineProjectDisplayText = null;
            }

            if (e.Label == null || e.Label == oldEditableText)
            {
                e.CancelEdit = true;
                return;
            }

            string newName = e.Label.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                e.CancelEdit = true;
                return;
            }

            // Cancel the edit - we'll update the text from the handler after rename succeeds
            e.CancelEdit = true;

            EventHandler<TreeRenameEventArgs> handler = RenameRequested;
            if (handler != null)
            {
                handler(this, new TreeRenameEventArgs(e.Node, e.Node.Tag, newName));
            }
        }

        private void RaiseAddClass(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AddClassRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseAddInterface(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AddInterfaceRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseAddAbstractClass(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AddAbstractClassRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseAddFile(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AddFileRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseAddNotebook(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AddNotebookRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseAddProject(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AddProjectRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseAddExistingFile(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AddExistingFileRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseNewFolder(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = NewFolderRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseAddExistingProject(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AddExistingProjectRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseExcludeFromProject(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = ExcludeFromProjectRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseIncludeInProject(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = IncludeInProjectRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseRemoveProject(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = RemoveProjectFromSolutionRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseAddProjectReference(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = AddProjectReferenceRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseMoveProjectUp(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = MoveProjectUpRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseMoveProjectDown(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = MoveProjectDownRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseDelete(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = DeleteRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseSave(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = SaveRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        // Open a file node in the editor — same path as double-click (reuses FileDoubleClicked).
        private void RaiseFileOpen(FileGroup fg)
        {
            if (fg == null || string.IsNullOrEmpty(fg.FilePath)) return;
            EventHandler<string> handler = FileDoubleClicked;
            if (handler != null) handler(this, fg.FilePath);
        }

        private void RaiseViewSource(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = ViewSourceRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }


        private void RaiseHide(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = HideRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseUnhide(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = UnhideRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseRenameSymbol(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = RenameSymbolRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseRenamePackage(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = RenamePackageRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseEditSignature(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = EditSignatureRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseBuildProject(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = BuildProjectRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseManagePackages(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = ManagePackagesRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseRestorePackages(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = RestorePackagesRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseProperties(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = PropertiesRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseSetStartupProject(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = SetStartupProjectRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseEditRunArguments(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = EditRunArgumentsRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        // Shortens a menu label's inline preview so a long argument string cannot stretch the
        // context menu across the screen.
        private static string Ellipsize(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        /// <summary>Raised for "Run" on an executable package node; the host launches that package.</summary>
        public event EventHandler<TreeOperationEventArgs> RunProjectRequested;

        /// <summary>Raised for "Start Debugging" on an executable package node.</summary>
        public event EventHandler<TreeOperationEventArgs> DebugProjectRequested;

        private void RaiseRunProject(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = RunProjectRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseDebugProject(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = DebugProjectRequested;
            if (handler != null) handler(this, new TreeOperationEventArgs(node, node.Tag));
        }

        private void RaiseRunTests(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = RunTestsRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseDebugTests(TreeNode node)
        {
            EventHandler<TreeOperationEventArgs> handler = DebugTestsRequested;
            if (handler != null)
            {
                handler(this, new TreeOperationEventArgs(node, node.Tag));
            }
        }

        private void RaiseFunctionDoubleClicked(FunctionModel fn)
        {
            EventHandler<FunctionSelectedEventArgs> handler = FunctionDoubleClicked;
            if (handler != null)
            {
                handler(this, new FunctionSelectedEventArgs(fn));
            }
        }

        /// <summary>
        /// True when the project has a runnable entry point. For Python packages that means a
        /// <c>__main__.py</c>/<c>main.py</c>/<c>*_main.py</c> under the package directory. Native
        /// (CUDA/C++) packages are CMakeLists-driven, not main.py-driven — this project (HelixPythonNamespace.UI)
        /// has no reference to HelixPythonNamespace.Workspace, so a cheap CMakeLists.txt presence check stands
        /// in for a full CMake target parse here; MainForm.TryFindEntryFile does the full parse for
        /// the actual Run/Debug/Command-Prompt path. Libraries return false either way.
        /// </summary>
        private static bool IsAppProject(ProjectModel p)
        {
            if (p == null) return false;
            // Generic runnable kind: a project that is runnable without being a Python entry
            // script): the loader already decided runnability from the language's own manifest
            // conventions, so no Python entry-file or CMakeLists sniffing applies.
            if (p.Kind == ProjectKind.Executable) return true;
            if (ProjectKinds.IsNative(p.Kind))
            {
                // Since CMakeSolutionLoader models one ProjectModel PER CMAKE TARGET rather than
                // one per folder, several sibling ProjectModels (a library plus its own test
                // executable, say) share the same folder's CMakeLists.txt — checking only for
                // that file's presence would mark every target in the folder "startable",
                // including static/shared libraries that can't actually be run. Kind must also
                // say Executable specifically.
                string dir = p.ProjectDirectory;
                return p.Kind == ProjectKind.CudaExecutable &&
                    !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "CMakeLists.txt"));
            }
            string ignore;
            return ProjectModel.TryFindEntryFile(p, out ignore);
        }

        /// <summary>
        /// Per-kind text shown next to a project node, e.g. "app_cli  [Console App]". Mirrors
        /// <see cref="ProjectImageKeyForKind"/>'s per-kind granularity so the icon and the text
        /// never disagree about what a project is — collapsing every runnable kind into a single
        /// generic "Executable" label (the previous behavior) hid the App/GUI App/Web App
        /// distinction pyproject.toml's <c>kind</c> key actually encodes.
        /// </summary>
        private static string ProjectKindLabel(ProjectModel p)
        {
            switch (p.Kind)
            {
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.App: return "Console App";
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.GuiApp: return "GUI App";
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.FlaskWebApp: return "Web App";
                // The native kinds cover BOTH languages (see ProjectLanguage.Cpp's design note);
                // whether THIS project actually uses CUDA is the per-project HasCudaSources flag.
                // A GTest project IS an executable to CMake, so "Unit Test" comes from THIS
                // project's own extracted TEST(...) symbols — NOT the disk-sweeping
                // HasTestFiles heuristic: a root-level target's directory is the whole
                // workspace, so that sweep saw a nested test project's files and mislabeled
                // the main console app itself a unit test.
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.CudaExecutable:
                    if (HasGoogleTestSymbols(p)) return "Unit Test";
                    return p.HasCudaSources ? "CUDA Executable" : "C++ Executable";
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.CudaLibrary:
                    if (HasGoogleTestSymbols(p)) return "Unit Test";
                    return p.HasCudaSources ? "CUDA Library" : "C++ Library";
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.Executable: return "Executable";
                // A persisted kind = "test" (also inferred at load for pre-existing `<x>_tests`
                // libraries). This is the ONE kind allowed to contain test files — solution policy
                // rule 3 (PY8003) — so the label comes from the kind, not from sniffing the disk.
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.TestProject: return "Unit Test";
                default:
                    // ClassLibrary. A library holding test files is now a PY8003 violation rather
                    // than a legitimate test package, but the heuristic stays: labelling it
                    // "Unit Test" is exactly what makes the mislabelled project findable in the
                    // tree next to the error the Problems panel is reporting against it.
                    return HasTestFiles(p) ? "Unit Test" : "Library";
            }
        }

        private static string FormatProjectDisplayName(ProjectModel project, string name, bool isStartup)
        {
            return name + "  [" + ProjectKindLabel(project) + "]" +
                (isStartup ? "  (Startup)" : string.Empty);
        }

        /// <summary>Refresh a renamed project's visible label without exposing its display-only
        /// kind/startup markers as part of the editable model name.</summary>
        public void UpdateProjectNodeDisplayName(TreeNode node, string projectName)
        {
            ProjectModel project = node == null ? null : node.Tag as ProjectModel;
            if (project == null || string.IsNullOrEmpty(projectName)) return;
            node.Text = FormatProjectDisplayName(project, projectName, project.IsStartup);
        }

        // The project a tree node's symbol belongs to, found by walking up through the
        // file-group / project ancestry. Null for detached nodes (defensive; shouldn't happen
        // in a populated tree).
        private static ProjectModel OwningProject(TreeNode node)
        {
            for (TreeNode n = node; n != null; n = n.Parent)
            {
                if (n.Tag is ProjectModel pm) return pm;
                if (n.Tag is FileGroup fg && fg.Project != null) return fg.Project;
                if (n.Tag is FolderGroup folder && folder.Project != null) return folder.Project;
            }
            return null;
        }

        // Whether symbol-level edit verbs (Rename Symbol / Edit Signature / Delete / Save) are
        // backed by a real rewrite service for the node's language — descriptor trait; an
        // unresolvable owner keeps the historical (edits-on) behavior.
        private static bool SupportsCodeModelEdits(TreeNode node)
        {
            ProjectModel owner = OwningProject(node);
            if (owner == null) return true;
            return HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(owner.Language).SupportsCodeModelEdits;
        }

        // Native test detection: the project's OWN extracted gtest symbols (TEST/TEST_F/TEST_P),
        // produced by CppTypeExtractor from the target's claimed sources — per-project by
        // construction, immune to the directory-nesting that fooled the disk sweep below.
        private static bool HasGoogleTestSymbols(ProjectModel p)
        {
            if (p == null || p.Functions == null) return false;
            foreach (FunctionModel f in p.Functions)
            {
                if (f == null || f.Name == null) continue;
                if (f.Name.StartsWith("TEST(", StringComparison.Ordinal) ||
                    f.Name.StartsWith("TEST_F(", StringComparison.Ordinal) ||
                    f.Name.StartsWith("TEST_P(", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // What counts as "a test file" is a language convention (test_*.py vs *Tests.cs vs
        // runtests.jl), so the sniff reads the owning language's descriptor instead of one
        // hard-coded pattern list. The descriptor defaults reproduce the historical heuristic
        // (*.py/*.cpp/*.cxx/*.cc named test_* / *_test) exactly.
        private static bool HasTestFiles(ProjectModel p)
        {
            if (p == null) return false;
            string dir = p.ProjectDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var descriptor = HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(p.Language);
            try
            {
                foreach (string ext in descriptor.TestFileExtensions)
                foreach (string f in Directory.EnumerateFiles(dir, "*" + ext, SearchOption.AllDirectories))
                {
                    // A file inside a NESTED member project belongs to that project, not this one.
                    // Without this, a solution root that contains a test crate labelled the root
                    // itself "Unit Test" — the same nesting mistake the gtest heuristic hit.
                    if (!OwnsPath(p, f)) continue;

                    string fileName = Path.GetFileName(f);
                    foreach (string exact in descriptor.TestFileNames)
                        if (string.Equals(fileName, exact, StringComparison.OrdinalIgnoreCase)) return true;
                    string name = Path.GetFileNameWithoutExtension(f);
                    foreach (string prefix in descriptor.TestFileNamePrefixes)
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                    foreach (string suffix in descriptor.TestFileNameSuffixes)
                        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;

                    // The language's own test-DIRECTORY convention: everything in a GTest `test/`
                    // is a test target, and Maven/Gradle put tests under src/test. Cargo's test
                    // crate has NO file whose name matches a test pattern (`src/lib.rs` +
                    // `tests/integration.rs`), which is why it kept reading as a plain Library.
                    foreach (string testDir in descriptor.TestDirectoryNames)
                        if (IsUnderDirectory(dir, f, testDir)) return true;
                }
            }
            catch { /* best-effort — a locked/inaccessible directory just hides the verb */ }
            return false;
        }

        /// <summary>True when no OTHER project of the solution sits between <paramref name="file"/> and <paramref name="p"/>.</summary>
        private static bool OwnsPath(ProjectModel p, string file)
        {
            if (p.Solution == null || p.Solution.Projects == null) return true;
            string full = Path.GetFullPath(file);
            string ownDir = Path.GetFullPath(p.ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar);
            foreach (ProjectModel other in p.Solution.Projects)
            {
                if (other == null || ReferenceEquals(other, p) || string.IsNullOrEmpty(other.ProjectDirectory)) continue;
                string otherDir = Path.GetFullPath(other.ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar);
                if (otherDir.Length <= ownDir.Length) continue;                       // not nested inside p
                if (full.StartsWith(otherDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // `<projectDir>/<segment>/…` — the segment must be a whole path component.
        private static bool IsUnderDirectory(string projectDir, string file, string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            string root = Path.GetFullPath(projectDir).TrimEnd(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(file);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            string relative = full.Substring(root.Length + 1).Replace('/', Path.DirectorySeparatorChar);
            string wanted = segment.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
            return relative.StartsWith(wanted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Bold the project node whose <see cref="ProjectModel.Name"/> matches
        /// <paramref name="startupProjectName"/>; restore default font on every
        /// other project node. Called after the user picks a startup project
        /// and on solution load.
        /// </summary>
        public void RefreshStartupProjectStyling(string startupProjectName)
        {
            if (_treeView == null || _treeView.Nodes.Count == 0) return;
            // Walk every project-level node across every solution/root node.
            foreach (TreeNode rootNode in _treeView.Nodes)
            {
                ApplyStartupStylingRecursive(rootNode, startupProjectName);
            }
        }

        private void ApplyStartupStylingRecursive(TreeNode node, string startupProjectName)
        {
            if (node == null) return;
            if (node.Tag is ProjectModel pm)
            {
                bool isStartup = !string.IsNullOrEmpty(startupProjectName)
                    && string.Equals(pm.Name, startupProjectName, StringComparison.Ordinal);

                node.NodeFont = isStartup
                    ? new Font(_treeView.Font, FontStyle.Bold)
                    : _treeView.Font;

                // Keep the "(Startup)" label suffix in sync with the styling (matches LoadSolution),
                // resetting from the authoritative project name so re-selecting a different package
                // clears the old suffix (PSM-11).
                node.Text = FormatProjectDisplayName(pm, pm.Name, isStartup);

                // Swap the node's icon — play button when startup, regular
                // project cube otherwise. WinForms treats an unknown key as
                // "no image", so if the play icon failed to load the node
                // falls back visually to bold-only — acceptable degradation.
                string targetKey = isStartup ? StartupProjectImageKey : ProjectImageKeyForKind(pm);
                if (node.ImageKey != targetKey)
                {
                    node.ImageKey = targetKey;
                    node.SelectedImageKey = targetKey;
                }
            }
            foreach (TreeNode child in node.Nodes)
            {
                ApplyStartupStylingRecursive(child, startupProjectName);
            }
        }

        private ImageList CreateImageList()
        {
            ImageList list = new ImageList();
            list.ColorDepth = ColorDepth.Depth32Bit;
            list.ImageSize = new Size(16, 16);

            list.Images.Add(SolutionImageKey, CreateIcon(Color.FromArgb(64, 64, 128)));
            list.Images.Add(ProjectImageKey, CreateIcon(Color.FromArgb(180, 120, 50)));
            list.Images.Add(AsyncFunctionImageKey, CreateIcon(Color.FromArgb(150, 30, 190))); // violet for async
            list.Images.Add(MemberFieldImageKey, CreateIcon(Color.FromArgb(110, 110, 190))); // periwinkle for fields
            list.Images.Add(ConstructorImageKey, CreateIcon(Color.FromArgb(200, 80, 40))); // burnt orange for ctors

            // Per-project-kind package icons from assets/product/tree (SPPFE Rule 1). Each falls
            // back to a coloured square when the asset can't be found, so the tree never breaks.
            AddKindIcon(list, PySolutionImageKey, "python_solution.png", Color.FromArgb(64, 64, 128));
            AddKindIcon(list, PkgAppImageKey, "python_console_package.png", Color.FromArgb(180, 120, 50));
            AddKindIcon(list, PkgGuiImageKey, "python_gui_package.png", Color.FromArgb(120, 90, 190));
            AddKindIcon(list, PkgFlaskImageKey, "python_flask_package.png", Color.FromArgb(40, 140, 90));
            AddKindIcon(list, PkgLibraryImageKey, "python_library_package.png", Color.FromArgb(70, 120, 180));
            AddKindIcon(list, PkgUnitTestImageKey, "python_unittest_package.png", Color.FromArgb(160, 80, 80));
            AddKindIcon(list, PkgGenericImageKey, "python_package.png", Color.FromArgb(180, 120, 50));

            // Type-declaration icons: the "alphabet badge" family (assets/icons/app_<letter>.ico) —
            // a native 16x16 frame per letter, one per OOP construct.
            AddKindIcon(list, ClassImageKey, "app_C.ico", Color.FromArgb(0, 128, 0));
            AddKindIcon(list, InterfaceImageKey, "app_I.ico", Color.FromArgb(128, 0, 255));
            AddKindIcon(list, AbstractClassImageKey, "app_A.ico", Color.FromArgb(0, 128, 128));
            AddKindIcon(list, EnumImageKey, "app_E.ico", Color.FromArgb(200, 140, 0));
            // Struct completes the family (tools/icon-generation/gen_struct_badge_icon.py). It was
            // the one TypeKind with no letter asset, so it fell back to an unlabeled coloured square
            // sitting among lettered siblings — most obvious in C++, whose only concrete-type
            // construct can be `struct`, so such a tree showed nothing but blank squares.
            AddKindIcon(list, StructImageKey, "app_S.ico", Color.FromArgb(0, 100, 180));

            // Python source file icon (SPPFE file-group rows).
            AddKindIcon(list, FileImageKey, "python_source.ico", Color.FromArgb(120, 120, 120));

            // Registered ONLY when the artwork loads. AddKindIcon would substitute a coloured
            // square for a missing file, and the lookup above would then prefer that square over
            // the generic file icon -- a worse result than not having the extension at all.
            foreach (string extension in FileKindExtensions)
            {
                Image art = LoadPythonKindIcon(FileKindAsset(extension));
                if (art == null) continue;
                list.Images.Add(FileKindPrefix + extension, art);
                AvailableFileKinds.Add(extension);
            }

            AddKindIcon(list, PythonMarkImageKey, "python_mark.png", Color.FromArgb(70, 120, 180));
            AddKindIcon(list, ResourcesFolderImageKey, "folder_resources.png", Color.FromArgb(230, 190, 40));
            AddKindIcon(list, DependenciesFolderImageKey, "folder_dependencies.png", Color.FromArgb(140, 90, 190));
            AddKindIcon(list, ToolchainFolderImageKey, "folder_toolchain.png", Color.FromArgb(60, 110, 180));

            // Function icon: the "F" badge, slimmed from its original bold/block weight (erode the
            // white glyph, then fill the vacated rim from a smooth per-channel polynomial fit of the
            // surrounding gradient, so no ghost of the original stroke remains at render size). Hue
            // later swapped green<->orange with the folder badge below (HSV hue-rotation of the
            // baked-in gradient/gloss/shadow pixels, so none of that shading work had to be redone).
            AddKindIcon(list, FunctionImageKey, "function.png", Color.FromArgb(140, 195, 60));

            // Method/Property badges, generated to match the "F" family's exact recipe (same
            // rounded-square silhouette, vertical gradient, and a subtle top-edge gloss — NOT the
            // much brighter/broader glow tried first, which washed the colours out pastel next to
            // the reference app_<letter> badges). Distinct hues from their nearest existing
            // neighbours: Method is bluer than the library badge's warmer pink, Property is bluer
            // than the flask badge's greener teal.
            AddKindIcon(list, MethodImageKey, "method.png", Color.FromArgb(150, 30, 120));
            AddKindIcon(list, PropertyImageKey, "property.png", Color.FromArgb(60, 130, 130));

            // Folder icon: same badge recipe, with a two-rectangle folder glyph (back tab + front
            // pocket) in place of a letter. Coloured with the actual Python-logo yellow (sampled
            // from assets/product/tree/python_logo_icon_168886.png, ~(255,204,62)), not an
            // arbitrary yellow.
            AddKindIcon(list, FolderImageKey, "folder.png", Color.FromArgb(230, 190, 40));

            // Play-button icon for the currently-selected startup project.
            Image play = LoadStartupProjectIcon();
            if (play != null) list.Images.Add(StartupProjectImageKey, play);

            // Per-language explorer icon sets (CodebaseSolutionToSolutionExplorerPanelConnector
            // layer): every language whose descriptor registers an ExplorerIconSet gets its own
            // solution/project/file keys, loaded from the same assets folders as every icon
            // above, with the language's accent colour as the coloured-square fallback. Python
            // and C++/CUDA register no set on purpose — their nodes keep the exact keys and
            // pixels the pre-connector tree always used.
            foreach (var descriptor in HelixPythonNamespace.Core.Languages.LanguageDescriptors.All)
            {
                var icons = descriptor.ExplorerIcons;
                if (icons == null) continue;
                Color accent = Color.FromArgb(icons.AccentArgb);
                AddKindIcon(list, LanguageIconKey(descriptor.Language, "solution"), icons.SolutionIconFileName, accent);
                AddKindIcon(list, LanguageIconKey(descriptor.Language, "exe"), icons.ExecutableProjectIconFileName, accent);
                AddKindIcon(list, LanguageIconKey(descriptor.Language, "lib"), icons.LibraryProjectIconFileName, accent);
                AddKindIcon(list, LanguageIconKey(descriptor.Language, "test"), icons.UnitTestProjectIconFileName, accent);
                AddKindIcon(list, LanguageIconKey(descriptor.Language, "file"), icons.SourceFileIconFileName, accent);
            }

            return list;
        }

        // ImageList key for one language's explorer icon in a given role ("solution"/"exe"/
        // "lib"/"test"/"file"). Only languages with a registered ExplorerIconSet use these keys.
        private static string LanguageIconKey(HelixPythonNamespace.Core.Model.Enums.ProjectLanguage language, string role)
        {
            return "lang:" + language + ":" + role;
        }

        // Registers an icon under <paramref name="key"/>: the asset from assets/product/tree or
        // assets/icons if found, else a coloured-square placeholder. A null file name goes
        // straight to the placeholder (language icon sets may name only some roles).
        private void AddKindIcon(ImageList list, string key, string fileName, Color fallback)
        {
            Image img = string.IsNullOrEmpty(fileName) ? null : LoadPythonKindIcon(fileName);
            list.Images.Add(key, img ?? CreateIcon(fallback));
        }

        // Loads a 16x16 icon from assets/product/tree or assets/product/app/<fileName>, resolving the
        // assets folder by walking up from the running assembly / working directory to the repo
        // marker (Directory.Build.props) — mirrors ToolbarIconDiskLoader. Null when not found.
        //
        // Always decoded via Bitmap(path) — including for .ico files — and bicubic-scaled to
        // 16x16. Deliberately NOT System.Drawing.Icon(path, 16, 16): that legacy GDI icon loader
        // corrupts these assets' PNG-compressed icon-directory frames into visual noise (confirmed
        // by isolated repro — Bitmap(path) decodes the identical file correctly, picking its
        // largest frame via GDI+'s own ICO codec, same as it does for a plain .png).
        private static Image LoadPythonKindIcon(string fileName)
        {
            try
            {
                foreach (string dir in PythonIconSearchDirs())
                {
                    string path = System.IO.Path.Combine(dir, fileName);
                    if (!System.IO.File.Exists(path)) continue;

                    using (var src = new Bitmap(path))
                    {
                        var scaled = new Bitmap(16, 16);
                        using (Graphics g = Graphics.FromImage(scaled))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, new Rectangle(0, 0, 16, 16));
                        }
                        return scaled;
                    }
                }
            }
            catch { /* fall through to null → coloured square */ }
            return null;
        }

        private static IEnumerable<string> PythonIconSearchDirs()
        {
            var roots = new List<string>();

            // Tree artwork -- project kinds, type and member badges, per-extension file icons --
            // lives in assets/product/tree; a few general-purpose UI icons (play_button.png for the
            // startup project, and the palette's A/C/I) live in assets/product/app. Search both.
            //
            // EXE-RELATIVE FIRST, then the repository walk. A DEPLOYED build has the assets copied
            // next to the exe and NO Directory.Build.props anywhere above it, so a repo-only search
            // finds nothing and every file row in the tree draws a blank square -- which is exactly
            // what a packaged build did. This mirrors ToolbarIconDiskLoader, which already searched
            // exe-relative; the two loaders were inconsistent and only the toolbar one worked when
            // shipped.
            foreach (string start in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
            {
                if (string.IsNullOrEmpty(start)) continue;
                string baseDir = System.IO.Path.GetFullPath(start);
                roots.Add(System.IO.Path.Combine(baseDir, "assets", "product", "tree"));
                roots.Add(System.IO.Path.Combine(baseDir, "assets", "product", "app"));
            }

            foreach (string start in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory })
            {
                if (string.IsNullOrEmpty(start)) continue;
                var dir = new System.IO.DirectoryInfo(System.IO.Path.GetFullPath(start));
                while (dir != null)
                {
                    if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Directory.Build.props")))
                    {
                        roots.Add(System.IO.Path.Combine(dir.FullName, "assets", "product", "tree"));
                        roots.Add(System.IO.Path.Combine(dir.FullName, "assets", "product", "app"));
                    }
                    dir = dir.Parent;
                }
            }
            return roots;
        }

        // The image key for a project node given its kind (SPPFE Rule 1). A declared
        // ProjectKind.TestProject takes the unit-test icon outright; a ClassLibrary still splits
        // into the plain library icon vs. the unit-test icon via the same HasTestFiles content
        // heuristic ProjectKindLabel uses for the "[Unit Test]" text label, which is what makes a
        // library that wrongly holds tests (PY8003) visible in the tree.
        private static string ProjectImageKeyForKind(ProjectModel project)
        {
            if (project == null) return PkgGenericImageKey;
            bool isTestProject = HelixPythonNamespace.Core.Model.Enums.ProjectKinds.IsTest(project.Kind);
            // A language that registers its own ExplorerIconSet uses per-language
            // keys; Python and C++/CUDA register none and keep the historical mapping below.
            var languageIcons = HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(project.Language).ExplorerIcons;
            if (languageIcons != null)
            {
                if (project.IsExecutable) return LanguageIconKey(project.Language, "exe");
                return isTestProject || HasTestFiles(project)
                    ? LanguageIconKey(project.Language, "test")
                    : LanguageIconKey(project.Language, "lib");
            }
            switch (project.Kind)
            {
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.TestProject: return PkgUnitTestImageKey;
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.App: return PkgAppImageKey;
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.GuiApp: return PkgGuiImageKey;
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.FlaskWebApp: return PkgFlaskImageKey;
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.ClassLibrary:
                    return HasTestFiles(project) ? PkgUnitTestImageKey : PkgLibraryImageKey;
                // Native C++/CUDA packages (CMake targets) get the SAME kind-based icons as their
                // Python counterparts — CudaExecutable reuses the generic "app" icon (there's no
                // GUI/Flask distinction for native targets) and CudaLibrary reuses the
                // library/unit-test pair. Previously both fell through to the generic default,
                // so every C++ project showed an identical icon regardless of executable-vs-library.
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.CudaExecutable: return PkgAppImageKey;
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.CudaLibrary:
                    return HasTestFiles(project) ? PkgUnitTestImageKey : PkgLibraryImageKey;
                // Generic runnable kind for icon-set-less languages: same visual as App.
                case HelixPythonNamespace.Core.Model.Enums.ProjectKind.Executable: return PkgAppImageKey;
                default: return PkgGenericImageKey;
            }
        }

        /// <summary>
        /// Load the play-button bitmap for the startup project's node, from
        /// <c>assets/icons/play_button.png</c> on disk (see <see cref="PythonIconSearchDirs"/>) —
        /// the same disk-based loader every other kind icon uses. Previously this read an
        /// embedded <c>play_button.ico</c> resource that was removed from the project (see the
        /// former comment in HelixPythonNamespace.UI.csproj) without ever being replaced: since
        /// <see cref="StartupProjectImageKey"/> was then never registered in the ImageList, every
        /// startup-project node's <c>ImageKey</c> resolved to nothing, which WinForms' TreeView
        /// renders as a blank/black square rather than degrading to "bold-only" as the old comment
        /// claimed. Returns <c>null</c> when the PNG can't be found, so the caller can still fall
        /// back gracefully.
        /// </summary>
        private static Image LoadStartupProjectIcon()
        {
            return LoadPythonKindIcon("play_button.png");
        }

        private Bitmap CreateIcon(Color color)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(color))
                {
                    g.FillRectangle(brush, 2, 2, 12, 12);
                }
                using (Pen pen = new Pen(Color.FromArgb(60, 60, 60), 1))
                {
                    g.DrawRectangle(pen, 2, 2, 11, 11);
                }
            }
            return bmp;
        }

        // ------------------------------------------------------------ symbol tooltips

        private void OnTreeMouseMoveForToolTip(object sender, MouseEventArgs e)
        {
            TreeNode node = _treeView.GetNodeAt(e.X, e.Y);
            // Only the row's own label area counts — sliding along the empty space to the right of
            // a short label shouldn't keep a tooltip alive for it.
            if (node != null && !node.Bounds.Contains(e.X, e.Y)) node = null;
            if (ReferenceEquals(node, _symbolToolTipNode)) return;

            _symbolToolTipNode = node;
            string text = node == null ? null : SymbolToolTipFor(node);
            if (string.IsNullOrEmpty(text))
            {
                _symbolToolTip.Hide(_treeView);
                return;
            }
            // Explicit placement (rather than SetToolTip) so the tip appears next to the row the
            // mouse is actually on, and so a row change re-anchors it immediately.
            _symbolToolTip.Show(text, _treeView, e.X + 18, e.Y + 22, _symbolToolTip.AutoPopDelay);
        }

        private void HideSymbolToolTip()
        {
            _symbolToolTipNode = null;
            if (_symbolToolTip != null && _treeView != null)
            {
                try { _symbolToolTip.Hide(_treeView); } catch { }
            }
        }

        /// <summary>
        /// The documentation tooltip for a tree row: a signature line plus the symbol's docstring.
        /// Null for rows that carry no symbol (projects, folders, structural nodes) and for symbols
        /// with neither a signature worth repeating nor documentation.
        /// </summary>
        internal static string SymbolToolTipFor(TreeNode node)
        {
            if (node == null) return null;

            TypeModel type = node.Tag as TypeModel;
            if (type != null)
                return ComposeToolTip(type.TypeKind + " " + type.FullyQualifiedName, type.Documentation);

            FunctionModel fn = node.Tag as FunctionModel;
            if (fn != null)
                return ComposeToolTip((fn.IsAsync ? "async def " : "def ") + fn.FullyQualifiedName, fn.Documentation);

            MemberModel member = node.Tag as MemberModel;
            if (member != null)
                return ComposeToolTip(MemberSignature(member), member.Documentation);

            return null;
        }

        // `acquire(self) -> None`-style one-liner, matching how the class canvas renders the member.
        private static string MemberSignature(MemberModel m)
        {
            var sb = new System.Text.StringBuilder();
            if (m.IsStatic) sb.Append("static ");
            if (m.IsAbstract) sb.Append("abstract ");
            if (m.IsAsync) sb.Append("async ");
            sb.Append(m.Name);
            if (m.MemberKind != MemberKind.Field && m.MemberKind != MemberKind.Property)
            {
                sb.Append("(");
                if (m.Parameters != null)
                {
                    for (int i = 0; i < m.Parameters.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(m.Parameters[i].Name);
                        if (!string.IsNullOrEmpty(m.Parameters[i].TypeName))
                            sb.Append(": ").Append(m.Parameters[i].TypeName);
                    }
                }
                sb.Append(")");
            }
            if (!string.IsNullOrEmpty(m.ReturnType)) sb.Append(" -> ").Append(m.ReturnType);
            return sb.ToString();
        }

        // Signature on the first line, documentation below it — the layout every IDE's symbol
        // tooltip uses. A long docstring is clipped so one row can't produce a full-screen tip.
        private static string ComposeToolTip(string signature, string documentation)
        {
            string doc = ClipDocumentation(documentation);
            if (string.IsNullOrEmpty(doc)) return string.IsNullOrEmpty(signature) ? null : signature;
            if (string.IsNullOrEmpty(signature)) return doc;
            return signature + Environment.NewLine + Environment.NewLine + doc;
        }

        private const int ToolTipDocLineLimit = 14;
        private const int ToolTipDocCharLimit = 800;

        private static string ClipDocumentation(string doc)
        {
            if (string.IsNullOrEmpty(doc)) return null;
            doc = doc.Replace("\r\n", "\n").Trim();
            if (doc.Length == 0) return null;

            string[] lines = doc.Split('\n');
            bool clipped = false;
            if (lines.Length > ToolTipDocLineLimit)
            {
                Array.Resize(ref lines, ToolTipDocLineLimit);
                clipped = true;
            }
            string result = string.Join(Environment.NewLine, lines).TrimEnd();
            if (result.Length > ToolTipDocCharLimit)
            {
                result = result.Substring(0, ToolTipDocCharLimit).TrimEnd();
                clipped = true;
            }
            return clipped ? result + " …" : result;
        }

        private TreeNode CreateTypeNode(TypeModel type)
        {
            TreeNode node = new TreeNode(FormatTypeName(type));
            node.Tag = type;
            node.ImageKey = GetTypeImageKey(type.TypeKind);
            node.SelectedImageKey = node.ImageKey;

            // SPPFE: a class exposes its members (constructor / method / property / field) as child
            // nodes with per-kind icons and static/abstract/async indicators, ordered by the current
            // sort mode (declaration order by default, alphabetical when the header toggle is on).
            if (type.Members != null && type.Members.Count > 0)
            {
                var ordered = new List<MemberModel>(type.Members);
                SortMembers(ordered);
                foreach (MemberModel m in ordered)
                {
                    if (m == null) continue;
                    TreeNode mn = new TreeNode(FormatMemberName(m));
                    mn.Tag = m;
                    mn.ImageKey = MemberImageKey(m);
                    mn.SelectedImageKey = mn.ImageKey;
                    // Deprecated members are struck through, the way professional IDEs render them.
                    if (m.IsDeprecated)
                        mn.NodeFont = new Font(this.Font, FontStyle.Strikeout);
                    node.Nodes.Add(mn);
                }
            }
            return node;
        }

        private static string FormatMemberName(MemberModel m)
        {
            // Leading UML visibility marker (SPPFE "access modifier" indicator), matching the notation
            // the designer canvas uses for the same members: + public, # protected, ~ internal,
            // - private. For Python these come from naming convention (__x private, _x protected).
            string label = VisibilityMarker(m.AccessModifier) + m.Name;
            // One trailing kind indicator, most specific first (async > abstract > static).
            if (m.IsAsync) label += "  (async)";
            else if (m.IsAbstract) label += "  (abstract)";
            else if (m.IsStatic) label += "  (static)";
            // Deprecation (SPPFE "deprecated" indicator) is orthogonal to the kind indicator, so it
            // is appended in addition — a member can be, e.g., a deprecated static method.
            if (m.IsDeprecated) label += "  (deprecated)";
            return label;
        }

        // UML visibility markers, identical to SceneBuilder.FormatAccessModifier so the tree and the
        // GDI+ class canvas read the same for any given member.
        private static string VisibilityMarker(AccessModifier access)
        {
            switch (access)
            {
                case AccessModifier.Protected: return "# ";
                case AccessModifier.Internal: return "~ ";
                case AccessModifier.Private: return "- ";
                default: return "+ ";
            }
        }

        private static string MemberImageKey(MemberModel m)
        {
            switch (m.MemberKind)
            {
                case MemberKind.Constructor: return ConstructorImageKey;
                case MemberKind.Property: return PropertyImageKey;
                case MemberKind.Field: return MemberFieldImageKey;
                default: return MethodImageKey;
            }
        }

        private TreeNode CreateFunctionNode(FunctionModel fn)
        {
            string label = fn.IsAsync ? (fn.Name + "  (async)") : fn.Name;
            TreeNode node = new TreeNode(label);
            node.Tag = fn;
            node.ImageKey = fn.IsAsync ? AsyncFunctionImageKey : FunctionImageKey;
            node.SelectedImageKey = node.ImageKey;
            return node;
        }

        /// <summary>
        /// The node a file belongs under, creating the folder chain for it on the way.
        ///
        /// Before iter 351 every file in a project hung directly off the project node, labelled by
        /// its bare file name. On SymPy that produced 1599 siblings in one flat alphabetical list —
        /// and, worse, files that could not be told apart: "add.py (4)" and "add.py (10)" are
        /// sympy/core/add.py and sympy/vector/add.py, distinguishable only by hovering for a
        /// tooltip. Mirroring the directory layout is what makes the tree navigable and each entry
        /// unambiguous, and it is what every Solution Explorer the user has used does.
        ///
        /// A file that is not under the project directory — or has no path at all, the "(unassigned)"
        /// bucket — stays at project level rather than inventing a folder for it.
        /// </summary>
        private TreeNodeCollection FolderNodeFor(
            TreeNode projectNode, ProjectModel project, string fileKey, Dictionary<string, TreeNode> folderNodes)
        {
            string[] segments = RelativeFolderSegments(project, fileKey);
            if (segments == null || segments.Length == 0) return projectNode.Nodes;
            return FolderChain(projectNode.Nodes, project, segments, folderNodes);
        }

        /// <summary>
        /// Walks (creating as needed) the folder-node chain for <paramref name="segments"/> under
        /// <paramref name="root"/> and returns the innermost node's child collection.
        ///
        /// Split out of <see cref="FolderNodeFor"/> because the parity contract needs folder nodes
        /// for directories that contain no rendered file at all — an EMPTY directory has no file
        /// key to derive segments from, and a directory the user sees in Explorer and not in the
        /// tree is the same PY8004 defect as a hidden file.
        /// </summary>
        private static TreeNodeCollection FolderChain(
            TreeNodeCollection root, ProjectModel project, string[] segments, Dictionary<string, TreeNode> folderNodes)
        {
            TreeNodeCollection parent = root;
            string accumulated = string.Empty;
            foreach (string segment in segments)
            {
                accumulated = accumulated.Length == 0 ? segment : accumulated + "/" + segment;

                // The dictionary is the bulk-load fast path — scanning children per segment per file
                // would be quadratic across 1599 files. Incremental callers (AddType) pass null and
                // fall back to scanning, where there is exactly one file to place.
                TreeNode folder = null;
                if (folderNodes != null) folderNodes.TryGetValue(accumulated, out folder);
                else folder = FindChildFolder(parent, segment);

                if (folder == null)
                {
                    folder = new TreeNode(segment);
                    folder.Tag = new FolderGroup { Project = project, RelativePath = accumulated };
                    folder.ImageKey = FolderImageKey;
                    folder.SelectedImageKey = FolderImageKey;
                    folder.ToolTipText = accumulated;
                    if (folderNodes != null) folderNodes[accumulated] = folder;
                    parent.Add(folder);
                }
                parent = folder.Nodes;
            }
            return parent;
        }

        private static TreeNode FindChildFolder(TreeNodeCollection nodes, string name)
        {
            foreach (TreeNode child in nodes)
            {
                if (child.Tag is FolderGroup
                    && string.Equals(child.Text, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            }
            return null;
        }

        /// <summary>
        /// Directory segments of <paramref name="fileKey"/> relative to the project directory, or
        /// null when the file is not under it (so it should stay at project level).
        /// </summary>
        private static string[] RelativeFolderSegments(ProjectModel project, string fileKey)
        {
            if (string.IsNullOrEmpty(fileKey) || project == null) return null;
            string root = NormalizeFileKey(project.ProjectDirectory);
            if (string.IsNullOrEmpty(root)) return null;
            root = root.TrimEnd('/');

            string file = NormalizeFileKey(fileKey);
            if (!file.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) return null;

            string relative = file.Substring(root.Length + 1);
            int lastSlash = relative.LastIndexOf('/');
            if (lastSlash <= 0) return null;   // file sits directly in the project root

            return relative.Substring(0, lastSlash).Split('/');
        }

        /// <summary>
        /// The PY8004 render pass for one member: draw every real file and directory the
        /// symbol-derived pass above did not.
        ///
        /// The tree's file spine used to come entirely from SYMBOLS — a file appeared because it
        /// contributed a type, a module-level function or a resource — so a file that contributed
        /// none of them had no node at all. Every symbol-free <c>__init__.py</c> was invisible in
        /// the Solution Explorer while sitting plainly in Explorer. That is not a cosmetic gap: it
        /// is precisely the disagreement solution policy rule 4 forbids, it is reported as an
        /// Orphan by the parity checker, and because the repair for an Orphan is "render it" and
        /// nothing rendered, the correction engine could never clear it. Nine of them survived a
        /// successful structure repair on the motivating solution and left Compile/Run/Debug
        /// blocked with no prompt the user could accept.
        ///
        /// <paramref name="renderedFiles"/> carries the file keys the symbol pass already placed,
        /// so one file gets one node — the other half of the isomorphism.
        /// </summary>
        private void AppendUncoveredProjectContent(
            TreeNode projectNode, ProjectModel project,
            Dictionary<string, TreeNode> folderNodes, HashSet<string> renderedFiles)
        {
            if (project == null || projectNode == null) return;
            string projectDirectory = NormalizeFileKey(project.ProjectDirectory);
            if (string.IsNullOrEmpty(projectDirectory)) return;
            projectDirectory = projectDirectory.TrimEnd('/');

            // Resource files already have a node under the virtual "Resources" grouping, so they
            // are claimed here rather than drawn twice. That grouping puts them at a tree path
            // that is not their disk path, which is a real (pre-existing) deviation from the
            // contract — but drawing the file a SECOND time at its real path would be a worse one.
            foreach (string resource in project.ResourceFiles)
            {
                if (string.IsNullOrEmpty(resource)) continue;
                renderedFiles.Add(projectDirectory + "/" + NormalizeFileKey(resource).Trim('/'));
            }

            foreach (string relative in project.ContentFiles)
            {
                if (string.IsNullOrEmpty(relative)) continue;
                string absolute = projectDirectory + "/" + NormalizeFileKey(relative).Trim('/');
                if (!renderedFiles.Add(absolute)) continue;
                FolderNodeFor(projectNode, project, absolute, folderNodes)
                    .Add(CreateContentFileNode(project, absolute));
            }

            // Explicitly, because a directory holding no rendered file has no file key to imply it
            // — and an empty directory the user can see and the tree cannot is the same defect.
            foreach (string relative in project.ContentDirectories)
            {
                string normalized = NormalizeFileKey(relative).Trim('/');
                if (normalized.Length == 0) continue;
                FolderChain(projectNode.Nodes, project, normalized.Split('/'), folderNodes);
            }
        }

        /// <summary>
        /// The same render pass at solution level: the real files no member owns and the real
        /// directories that are neither a member nor inside one.
        ///
        /// Root-level files (<c>README.md</c>, <c>pytest.ini</c>, <c>requirements.txt</c>) go under
        /// a <c>Solution Items</c> container; anything nested in a non-member directory
        /// (<c>results/exp10.png</c>) goes under that directory's own folder node, because the
        /// directory is real and gets a real node. Five of the motivating solution's fourteen
        /// surviving defects were exactly these.
        /// </summary>
        private void AppendSolutionContent(TreeNode solutionNode, SolutionModel solution)
        {
            if (solutionNode == null || solution == null) return;
            if (solution.SolutionItems.Count == 0 && solution.ContentDirectories.Count == 0) return;

            string root = NormalizeFileKey(solution.SolutionDirectory);
            if (string.IsNullOrEmpty(root)) return;
            root = root.TrimEnd('/');

            // Project = null throughout: these paths belong to no member, and every FolderGroup /
            // FileGroup consumer already guards on a null Project (the project-scoped context
            // actions correctly refuse to act on them) — MainForm itself synthesises a
            // project-less FileGroup for the same reason.
            var folderNodes = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
            foreach (string relative in solution.ContentDirectories)
            {
                string normalized = NormalizeFileKey(relative).Trim('/');
                if (normalized.Length == 0) continue;
                FolderChain(solutionNode.Nodes, null, normalized.Split('/'), folderNodes);
            }

            TreeNode solutionItems = null;
            foreach (string relative in solution.SolutionItems)
            {
                string normalized = NormalizeFileKey(relative).Trim('/');
                if (normalized.Length == 0) continue;
                TreeNode fileNode = CreateContentFileNode(null, root + "/" + normalized);

                int lastSlash = normalized.LastIndexOf('/');
                if (lastSlash < 0)
                {
                    if (solutionItems == null)
                    {
                        solutionItems = CreateStructuralNode(null, "Solution Items");
                        solutionNode.Nodes.Add(solutionItems);
                    }
                    solutionItems.Nodes.Add(fileNode);
                    continue;
                }
                FolderChain(solutionNode.Nodes, null, normalized.Substring(0, lastSlash).Split('/'), folderNodes)
                    .Add(fileNode);
            }
        }

        /// <summary>
        /// A file node for a file that carries no symbols. Same <see cref="FileGroup"/> tag as a
        /// symbol-bearing file row, so double-click-to-open, the context menu and drag-drop all
        /// work on it — a node the user can see but not act on would only relocate the complaint.
        /// No "(n)" suffix, because there is no n.
        /// </summary>
        private TreeNode CreateContentFileNode(ProjectModel project, string fileKey)
        {
            TreeNode node = new TreeNode(Path.GetFileName(fileKey));
            node.Tag = new FileGroup { Project = project, FilePath = fileKey };
            // The language's source icon only for files that ARE source; a README or a .png
            // wearing the Python-module icon would misreport what it is.
            node.ImageKey = IsLanguageSourceFile(project, fileKey)
                ? FileImageKeyFor(project)
                : FileImageKeyForPath(fileKey);
            node.SelectedImageKey = node.ImageKey;
            node.ToolTipText = fileKey;
            return node;
        }

        private static bool IsLanguageSourceFile(ProjectModel project, string fileKey)
        {
            if (project == null || string.IsNullOrEmpty(fileKey)) return false;
            string extension = Path.GetExtension(fileKey);
            if (string.IsNullOrEmpty(extension)) return false;
            string[] extensions = HelixPythonNamespace.Core.Languages.LanguageDescriptors
                                      .For(project.Language).SourceFileExtensions;
            if (extensions == null) return false;
            foreach (string candidate in extensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Tag for a folder node, so context actions can tell it from a file or a type.</summary>
        public sealed class FolderGroup
        {
            public ProjectModel Project;
            public string RelativePath;
            public override string ToString() { return RelativePath ?? string.Empty; }
        }

        /// <summary>
        /// A file/folder excluded from its project (SRS §5.9), shown as a leaf under
        /// the project's "Excluded Files" node so there is somewhere to right-click
        /// "Include in Project" — excluded paths otherwise leave no trace in the tree.
        /// </summary>
        public sealed class ExcludedEntry
        {
            public ProjectModel Project;
            public string RelativePath;
            public override string ToString() { return RelativePath ?? string.Empty; }
        }

        /// <summary>Logical SPPFE container that is not itself a Python symbol.</summary>
        public sealed class StructuralGroup
        {
            public ProjectModel Project;
            public string Kind;
            public override string ToString() { return Kind ?? string.Empty; }
        }

        /// <summary>A displayed dependency edge from one project to another.</summary>
        public sealed class ProjectReferenceGroup
        {
            public ProjectModel Owner;
            public ProjectModel Target;
            public override string ToString() { return Target == null ? string.Empty : Target.Name; }
        }

        /// <summary>A reference the manifest declares whose target project is not in the solution.</summary>
        public sealed class UnresolvedReferenceGroup
        {
            public ProjectModel Owner;
            public string Name;
            public override string ToString() { return Name ?? string.Empty; }
        }

        /// <summary>What the project's toolchain/environment node is called, per language.</summary>
        internal static string EnvironmentNodeLabel(ProjectModel project)
        {
            switch (project == null ? ProjectLanguage.Python : project.Language)
            {
                case ProjectLanguage.Cpp: return "Native Toolchain";
                default: return "Python Environment";
            }
        }

        private static string EnvironmentNodeTooltip(ProjectModel project)
        {
            switch (project == null ? ProjectLanguage.Python : project.Language)
            {
                case ProjectLanguage.Cpp: return "Compiler, generator and CMake configuration";
                default: return "Interpreter and virtual-environment configuration";
            }
        }

        private void AppendCanonicalProjectNodes(TreeNode projectNode, ProjectModel project)
        {
            foreach (IProjectNodeProvider provider in _projectNodeProviders.Snapshot())
            {
                if (!provider.CanBuild(project)) continue;
                provider.AppendNodes(this, projectNode, project);
                return;
            }

            throw new InvalidOperationException("No project-tree node provider accepts the project.");
        }

        private void AppendDefaultProjectNodes(TreeNode projectNode, ProjectModel project)
        {

            // The environment node is named for the language the project is actually written in.
            // It used to say "Python Environment" under every project, so a native CMake project
            // looked like the IDE had loaded it as Python.
            TreeNode environment = CreateStructuralNode(project, EnvironmentNodeLabel(project));
            environment.ToolTipText = EnvironmentNodeTooltip(project);
            AppendEnvironmentFacts(environment, project);
            projectNode.Nodes.Add(environment);

            TreeNode dependencies = CreateStructuralNode(project, "Dependencies");
            dependencies.Text = DependencyNodeLabel(project);
            foreach (ProjectModel reference in project.ProjectReferences)
            {
                if (reference == null) continue;
                TreeNode child = new TreeNode(reference.Name);
                child.Tag = new ProjectReferenceGroup { Owner = project, Target = reference };
                child.ImageKey = ProjectImageKeyForKind(reference);
                child.SelectedImageKey = child.ImageKey;
                dependencies.Nodes.Add(child);
            }
            // A reference whose project is gone (deleted from the solution) still sits in the
            // manifest and still breaks the build. Show it, marked, so there is something to
            // right-click ▸ Remove Reference — hiding it left the user with a broken project and
            // no way to repair it from the IDE.
            if (project.UnresolvedProjectReferences != null)
                foreach (string missing in project.UnresolvedProjectReferences)
                {
                    if (string.IsNullOrEmpty(missing)) continue;
                    TreeNode child = new TreeNode(missing + "  (missing)");
                    child.Tag = new UnresolvedReferenceGroup { Owner = project, Name = missing };
                    child.ForeColor = System.Drawing.Color.Firebrick;
                    child.ToolTipText = "The referenced project is not in this solution. "
                                      + "Remove the reference, or add the project back.";
                    dependencies.Nodes.Add(child);
                }
            AppendModuleDependencies(dependencies, project);
            projectNode.Nodes.Add(dependencies);

            // Everything below a project row now has to earn its place.
            //
            // There used to be four more folders here — Tests, Resources, Documentation and
            // Generated Files — each pointing at a conventionally-named directory and each added
            // unconditionally, so a package that had none of them (which is nearly all of them)
            // showed four permanently-empty folders. Plus "Project Configuration", which opened the
            // pyproject.toml that double-clicking the project row already opens (§5.5).
            //
            //   Tests           — gone: unit tests are separate packages in this solution model,
            //                     so a per-package tests folder contradicts the layout.
            //   Documentation   — gone: IntelliSense and hover doctips are where docs are read.
            //   Generated Files — gone: designer-emitted .py files are listed as ordinary sources
            //                     already, and __pycache__/build/dist are noise.
            //   Project Config. — gone: double-click the project row.
            //   Resources       — kept, but populated and hidden when empty: the tree lists only
            //                     Python sources, so a package's data files are otherwise invisible.
            AppendResourceFiles(projectNode, project);

            // §5.9: excluded paths leave no other trace in the tree, so surface them
            // here as the one place to right-click "Include in Project".
            if (project.ExcludedPaths != null && project.ExcludedPaths.Count > 0)
            {
                TreeNode excludedRoot = CreateStructuralNode(project, "Excluded Files");
                foreach (string relPath in project.ExcludedPaths)
                {
                    TreeNode entryNode = new TreeNode(relPath);
                    entryNode.Tag = new ExcludedEntry { Project = project, RelativePath = relPath };
                    entryNode.ImageKey = FileImageKeyForPath(relPath);
                    entryNode.SelectedImageKey = entryNode.ImageKey;
                    entryNode.ForeColor = System.Drawing.SystemColors.GrayText;
                    excludedRoot.Nodes.Add(entryNode);
                }
                projectNode.Nodes.Add(excludedRoot);
            }
        }

        private void AppendNativeProjectNodes(TreeNode projectNode, ProjectModel project)
        {
            string workspaceCMake = project.Solution == null ? null : project.Solution.FilePath;
            string projectCMake = FindNearestCMakeLists(project.ProjectDirectory, workspaceCMake);

            TreeNode toolchain = CreateStructuralNode(project, "Native Toolchain");
            toolchain.ToolTipText = "CMake workspace, presets and native toolchain files";
            AddFileChild(toolchain, project, workspaceCMake, "Workspace CMakeLists.txt");
            string workspaceDir = File.Exists(workspaceCMake) ? Path.GetDirectoryName(workspaceCMake) : null;
            if (!string.IsNullOrEmpty(workspaceDir))
            {
                AddFileChild(toolchain, project, Path.Combine(workspaceDir, "CMakePresets.json"), null);
                AddFileChild(toolchain, project, Path.Combine(workspaceDir, "CMakeUserPresets.json"), null);
                AppendFilesFromDirectory(toolchain, project, Path.Combine(workspaceDir, "cmake"), workspaceDir,
                    delegate(string path) { return string.Equals(Path.GetExtension(path), ".cmake", StringComparison.OrdinalIgnoreCase); });
            }
            if (toolchain.Nodes.Count > 0) projectNode.Nodes.Add(toolchain);

            if (project.ProjectReferences.Count > 0 ||
                (project.UnresolvedProjectReferences != null && project.UnresolvedProjectReferences.Count > 0))
            {
                TreeNode dependencies = CreateStructuralNode(project, "Dependencies");
                dependencies.Text = DependencyNodeLabel(project);
                foreach (ProjectModel reference in project.ProjectReferences)
                {
                    if (reference == null) continue;
                    TreeNode child = new TreeNode(reference.Name);
                    child.Tag = new ProjectReferenceGroup { Owner = project, Target = reference };
                    child.ImageKey = ProjectImageKeyForKind(reference);
                    child.SelectedImageKey = child.ImageKey;
                    dependencies.Nodes.Add(child);
                }
                if (project.UnresolvedProjectReferences != null)
                    foreach (string missing in project.UnresolvedProjectReferences)
                    {
                        if (string.IsNullOrEmpty(missing)) continue;
                        TreeNode child = new TreeNode(missing + "  (missing)");
                        child.Tag = new UnresolvedReferenceGroup { Owner = project, Name = missing };
                        child.ForeColor = System.Drawing.Color.Firebrick;
                        child.ToolTipText = "The referenced project is not in this solution. Remove the reference, or add the project back.";
                        dependencies.Nodes.Add(child);
                    }
                projectNode.Nodes.Add(dependencies);
            }

            AppendNativeDirectoryIfPresent(projectNode, project, "Resources", "resources");
            if (!AppendNativeDirectoryIfPresent(projectNode, project, "Documentation", "docs"))
                AppendNativeDirectoryIfPresent(projectNode, project, "Documentation", "documentation");
            AppendNativeDirectoryIfPresent(projectNode, project, "Generated Files", "generated");

            if (!string.IsNullOrEmpty(projectCMake))
            {
                TreeNode configuration = new TreeNode("Project Configuration");
                configuration.Tag = new FileGroup { Project = project, FilePath = projectCMake };
                configuration.ImageKey = FileImageKeyForPath(projectCMake);
                configuration.SelectedImageKey = configuration.ImageKey;
                configuration.ToolTipText = projectCMake;
                projectNode.Nodes.Add(configuration);
            }
        }

        private bool AppendNativeDirectoryIfPresent(TreeNode projectNode, ProjectModel project,
            string label, string directoryName)
        {
            string directory;
            try { directory = Path.Combine(project.ProjectDirectory ?? string.Empty, directoryName); }
            catch { return false; }
            if (!Directory.Exists(directory)) return false;

            TreeNode root = CreateStructuralNode(project, label);
            root.ToolTipText = directory;
            AppendFilesFromDirectory(root, project, directory, project.ProjectDirectory, delegate { return true; });
            if (root.Nodes.Count == 0) return false;
            projectNode.Nodes.Add(root);
            return true;
        }

        private void AppendFilesFromDirectory(TreeNode parent, ProjectModel project, string directory,
            string relativeRoot, Predicate<string> include)
        {
            if (!Directory.Exists(directory)) return;
            string[] directories;
            string[] files;
            try
            {
                directories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory);
            }
            catch { return; }
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string childDirectory in directories)
            {
                TreeNode folder = new TreeNode(Path.GetFileName(childDirectory));
                folder.Tag = new FolderGroup
                {
                    Project = project,
                    RelativePath = RelativePathFrom(relativeRoot, childDirectory)
                };
                folder.ImageKey = FolderImageKey;
                folder.SelectedImageKey = FolderImageKey;
                folder.ToolTipText = childDirectory;
                AppendFilesFromDirectory(folder, project, childDirectory, relativeRoot, include);
                if (folder.Nodes.Count > 0) parent.Nodes.Add(folder);
            }
            foreach (string file in files)
                if (include == null || include(file)) AddFileChild(parent, project, file, null);
        }

        private static void AddFileChild(TreeNode parent, ProjectModel project, string filePath, string label)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            TreeNode child = new TreeNode(string.IsNullOrEmpty(label) ? Path.GetFileName(filePath) : label);
            child.Tag = new FileGroup { Project = project, FilePath = filePath };
            child.ImageKey = FileImageKeyForPath(filePath);
            child.SelectedImageKey = child.ImageKey;
            child.ToolTipText = filePath;
            parent.Nodes.Add(child);
        }

        private static string FindNearestCMakeLists(string startDirectory, string workspaceCMake)
        {
            try
            {
                string directory = startDirectory;
                string workspaceDirectory = File.Exists(workspaceCMake) ? Path.GetDirectoryName(workspaceCMake) : null;
                while (!string.IsNullOrEmpty(directory))
                {
                    string candidate = Path.Combine(directory, "CMakeLists.txt");
                    if (File.Exists(candidate)) return candidate;
                    if (!string.IsNullOrEmpty(workspaceDirectory) &&
                        string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar),
                            workspaceDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        break;
                    DirectoryInfo parent = Directory.GetParent(directory);
                    directory = parent == null ? null : parent.FullName;
                }
            }
            catch { }
            return File.Exists(workspaceCMake) ? workspaceCMake : null;
        }

        private static string RelativePathFrom(string root, string path)
        {
            try
            {
                string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Uri.UnescapeDataString(new Uri(basePath).MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString())
                    .Replace('\\', '/');
            }
            catch { return Path.GetFileName(path); }
        }

        private TreeNode CreateStructuralNode(ProjectModel project, string kind)
        {
            TreeNode node = new TreeNode(kind);
            node.Tag = new StructuralGroup { Project = project, Kind = kind };
            node.ImageKey = StructuralImageKey(kind);
            node.SelectedImageKey = node.ImageKey;
            return node;
        }

        /// <summary>The icon for a structural group, by what the group holds.</summary>
        /// <remarks>
        /// Matched on the node's own label, which is also its <c>StructuralGroup.Kind</c> — the one
        /// string that already identifies these groups, so this adds no second vocabulary to keep
        /// in step with the first. An unrecognised kind keeps the plain folder.
        /// </remarks>
        private static string StructuralImageKey(string kind)
        {
            if (string.Equals(kind, "Resources", StringComparison.Ordinal)) return ResourcesFolderImageKey;
            if (string.Equals(kind, "Dependencies", StringComparison.Ordinal)) return DependenciesFolderImageKey;
            if (string.Equals(kind, "Native Toolchain", StringComparison.Ordinal)) return ToolchainFolderImageKey;
            if (string.Equals(kind, "Python Environment", StringComparison.Ordinal)) return PythonMarkImageKey;
            return FolderImageKey;
        }

        private void AppendStructuralRoot(
            TreeNode projectNode,
            ProjectModel project,
            string label,
            string directoryName)
        {
            TreeNode node = CreateStructuralNode(project, label);
            node.ToolTipText = Path.Combine(project.ProjectDirectory ?? string.Empty, directoryName);
            projectNode.Nodes.Add(node);
        }

        /// <summary>A read-only fact under the environment node — nothing to open, nothing to edit.</summary>
        public sealed class EnvironmentFact
        {
            public ProjectModel Project;
            public string Label;
            public string Value;
            public override string ToString()
            {
                return string.IsNullOrEmpty(Value) ? (Label ?? string.Empty) : Label + ": " + Value;
            }
        }

        /// <summary>A module the package imports, grouped under its origin.</summary>
        public sealed class ModuleDependencyGroup
        {
            /// <summary>The package that does the importing — NOT the one being imported.</summary>
            public ProjectModel Project;

            public ModuleDependency Dependency;

            /// <summary>
            /// The imported name this row stands for (<c>StorageError</c>), or null when the row is
            /// the module itself. Double-click navigates to whichever of the two it is.
            /// </summary>
            public string SymbolName;

            public override string ToString()
            {
                if (!string.IsNullOrEmpty(SymbolName)) return SymbolName;
                return Dependency == null ? string.Empty : Dependency.ModuleName;
            }
        }

        /// <summary>A non-source file that ships inside the package.</summary>
        public sealed class ResourceFileEntry
        {
            public ProjectModel Project;
            public string RelativePath;
            public string FullPath;
            public override string ToString() { return RelativePath ?? string.Empty; }
        }

        // Which interpreter this package runs on, as plain read-only rows. Says so explicitly when
        // nothing could be determined — an empty folder would read as "the IDE is still thinking".
        private void AppendEnvironmentFacts(TreeNode environment, ProjectModel project)
        {
            PythonEnvironmentInfo info = project == null ? null : project.Environment;
            if (info == null || info.IsEmpty)
            {
                AddEnvironmentFact(environment, project, "No interpreter detected", null,
                    "No virtual environment was found beside this package or the solution. "
                    + "Create one with Tools ▸ Python Environments.");
                return;
            }

            if (!string.IsNullOrEmpty(info.VirtualEnvName))
                AddEnvironmentFact(environment, project, "Virtual environment", info.VirtualEnvName,
                    info.VirtualEnvPath);
            if (!string.IsNullOrEmpty(info.InterpreterPath))
                AddEnvironmentFact(environment, project, "Interpreter", info.InterpreterPath,
                    info.InterpreterPath);
            if (!string.IsNullOrEmpty(info.RequiresPython))
                AddEnvironmentFact(environment, project, "Requires Python", info.RequiresPython,
                    "The requires-python constraint declared in pyproject.toml.");
        }

        private void AddEnvironmentFact(TreeNode parent, ProjectModel project, string label, string value, string tip)
        {
            var fact = new EnvironmentFact { Project = project, Label = label, Value = value };
            TreeNode node = new TreeNode(fact.ToString());
            node.Tag = fact;
            node.ImageKey = PythonMarkImageKey;
            node.SelectedImageKey = PythonMarkImageKey;
            node.ToolTipText = tip;
            node.ForeColor = System.Drawing.SystemColors.GrayText;   // read-only, and it looks it
            parent.Nodes.Add(node);
        }

        // What the package's own source actually imports, in the three groups whose consequences
        // differ: the standard library is always there, third-party has to be installed, and a
        // solution package is something the user can open and edit right here.
        private void AppendModuleDependencies(TreeNode dependencies, ProjectModel project)
        {
            if (project == null || project.ModuleDependencies == null) return;

            AppendDependencyGroup(dependencies, project, DependencyOrigin.StandardLibrary,
                "Standard Library", "Modules that ship with CPython itself.");
            AppendDependencyGroup(dependencies, project, DependencyOrigin.ThirdParty,
                "Third-Party", "Packages installed from an index into the environment.");
            AppendDependencyGroup(dependencies, project, DependencyOrigin.SolutionPackage,
                "Solution Packages", "Other workspace members of this solution.");
        }

        private void AppendDependencyGroup(TreeNode dependencies, ProjectModel project,
            DependencyOrigin origin, string label, string tooltip)
        {
            var matching = new List<ModuleDependency>();
            foreach (ModuleDependency d in project.ModuleDependencies)
                if (d != null && d.Origin == origin) matching.Add(d);
            if (matching.Count == 0) return;   // no empty groups

            TreeNode group = CreateStructuralNode(project, label);
            group.Text = label + " (" + matching.Count + ")";
            group.ToolTipText = tooltip;

            foreach (ModuleDependency d in matching)
            {
                TreeNode moduleNode = new TreeNode(d.ModuleName);
                moduleNode.Tag = new ModuleDependencyGroup { Project = project, Dependency = d };
                moduleNode.ImageKey = PythonMarkImageKey;
                moduleNode.SelectedImageKey = PythonMarkImageKey;
                bool navigable = origin == DependencyOrigin.SolutionPackage;
                moduleNode.ToolTipText = (d.ImportedNames.Count == 0
                        ? "import " + d.ModuleName
                        : "from " + d.ModuleName + " import " + string.Join(", ", d.ImportedNames.ToArray()))
                    + (navigable ? Environment.NewLine + "Double-click to open the module." : string.Empty);

                // The names actually pulled out of the module — the classes, functions and
                // constants this package uses from it.
                foreach (string name in d.ImportedNames)
                {
                    TreeNode symbol = new TreeNode(name);
                    symbol.Tag = new ModuleDependencyGroup
                    {
                        Project = project,
                        Dependency = d,
                        SymbolName = name,
                    };
                    symbol.ImageKey = PythonMarkImageKey;
                    symbol.SelectedImageKey = PythonMarkImageKey;
                    if (navigable)
                        symbol.ToolTipText = d.ModuleName + "." + name
                            + Environment.NewLine + "Double-click to go to its declaration.";
                    else
                        symbol.ForeColor = System.Drawing.SystemColors.GrayText;
                    moduleNode.Nodes.Add(symbol);
                }
                group.Nodes.Add(moduleNode);
            }
            dependencies.Nodes.Add(group);
        }

        // Non-source files that ship inside the package. Added only when there are any: the node
        // exists to reveal files the rest of the tree cannot show, so with nothing to reveal it has
        // no reason to be on screen.
        private void AppendResourceFiles(TreeNode projectNode, ProjectModel project)
        {
            if (project == null || project.ResourceFiles == null || project.ResourceFiles.Count == 0) return;

            TreeNode resources = CreateStructuralNode(project, "Resources");
            resources.Text = "Resources (" + project.ResourceFiles.Count + ")";
            resources.ToolTipText = "Files that ship inside the package but are not Python source.";

            string dir = project.ProjectDirectory ?? string.Empty;
            foreach (string rel in project.ResourceFiles)
            {
                string full;
                try { full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)); }
                catch { full = null; }

                TreeNode node = new TreeNode(rel);
                // A FileGroup tag makes double-click open it in the editor, like any other file.
                node.Tag = string.IsNullOrEmpty(full)
                    ? (object)new ResourceFileEntry { Project = project, RelativePath = rel, FullPath = full }
                    : new FileGroup { Project = project, FilePath = full };
                node.ImageKey = FileImageKeyForPath(full ?? rel);
                node.SelectedImageKey = node.ImageKey;
                node.ToolTipText = full ?? rel;
                resources.Nodes.Add(node);
            }
            projectNode.Nodes.Add(resources);
        }

        private TreeNode CreateFileNode(ProjectModel project, string fileKey, int childCount)
        {
            TreeNode node = new TreeNode(FormatFileLabel(fileKey, childCount));
            node.Tag = new FileGroup { Project = project, FilePath = fileKey };
            node.ImageKey = FileImageKeyFor(project);
            node.SelectedImageKey = node.ImageKey;
            node.ToolTipText = string.IsNullOrEmpty(fileKey) ? "(no source file)" : fileKey;
            return node;
        }

        // The source-file icon for a project's file rows: the language's own icon when its
        // descriptor registers an ExplorerIconSet, else the historical Python source icon.
        private static string FileImageKeyFor(ProjectModel project)
        {
            if (project != null &&
                HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(project.Language).ExplorerIcons != null)
                return LanguageIconKey(project.Language, "file");
            return FileImageKey;
        }

        // The solution-root icon: the (first) project language's own solution icon when that
        // language registers an ExplorerIconSet, else the historical Python solution icon —
        // which is also what C++/CMake solutions always showed, so both stay pixel-identical.
        private static string SolutionImageKeyFor(SolutionModel solution)
        {
            if (solution != null && solution.Projects.Count > 0)
            {
                var language = solution.Projects[0].Language;
                if (HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(language).ExplorerIcons != null)
                    return LanguageIconKey(language, "solution");
            }
            return PySolutionImageKey;
        }

        private static string FormatFileLabel(string fileKey, int count)
        {
            string display = string.IsNullOrEmpty(fileKey)
                ? "(unassigned)"
                : Path.GetFileName(fileKey);
            return display + " (" + count + ")";
        }

        private static string NormalizeFileKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            // Normalize separators so files discovered via different path styles
            // (e.g. roundtripped through JSON vs. fresh from disk) collapse into
            // the same bucket. Case is ignored via the dictionary comparer.
            return path.Replace('\\', '/');
        }

        private static int CompareFileKeys(string a, string b)
        {
            // Empty path (unassigned bucket) sorts last.
            bool aEmpty = string.IsNullOrEmpty(a);
            bool bEmpty = string.IsNullOrEmpty(b);
            if (aEmpty && bEmpty) return 0;
            if (aEmpty) return 1;
            if (bEmpty) return -1;

            int byName = string.Compare(
                Path.GetFileName(a), Path.GetFileName(b),
                StringComparison.OrdinalIgnoreCase);
            if (byName != 0) return byName;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareEntries(object a, object b)
        {
            // Types before functions; alphabetical within each kind.
            bool aIsType = a is TypeModel;
            bool bIsType = b is TypeModel;
            if (aIsType && !bIsType) return -1;
            if (!aIsType && bIsType) return 1;

            string an = aIsType ? ((TypeModel)a).Name : ((FunctionModel)a).Name;
            string bn = bIsType ? ((TypeModel)b).Name : ((FunctionModel)b).Name;
            return string.Compare(an ?? string.Empty, bn ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private string FormatTypeName(TypeModel type)
        {
            string baseName = type.Name;
            if (type.GenericParameters != null && type.GenericParameters.Count > 0)
            {
                var paramNames = new List<string>();
                foreach (var gp in type.GenericParameters)
                {
                    paramNames.Add(gp.Name);
                }
                baseName = string.Format("{0}<{1}>", type.Name, string.Join(", ", paramNames));
            }
            // Nested types: prefix with parent short name so the hierarchy is visible
            // in the flat tree (e.g. "Outer+Nested").
            if (type.IsNested)
            {
                string parentShort = GetParentShortName(type.ParentTypeFqn);
                if (!string.IsNullOrEmpty(parentShort))
                {
                    return parentShort + "+" + baseName;
                }
            }
            return baseName;
        }

        private static string GetParentShortName(string parentFqn)
        {
            if (string.IsNullOrEmpty(parentFqn)) return string.Empty;
            int plus = parentFqn.LastIndexOf('+');
            if (plus >= 0) return parentFqn.Substring(plus + 1);
            int dot = parentFqn.LastIndexOf('.');
            return dot >= 0 ? parentFqn.Substring(dot + 1) : parentFqn;
        }

        private string GetTypeImageKey(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.Interface:
                    return InterfaceImageKey;
                case TypeKind.AbstractClass:
                    return AbstractClassImageKey;
                case TypeKind.Struct:
                    return StructImageKey;
                case TypeKind.Enum:
                    return EnumImageKey;
                default:
                    return ClassImageKey;
            }
        }

        private TreeNode FindProjectNode(string projectName)
        {
            if (_treeView.Nodes.Count == 0)
            {
                return null;
            }

            TreeNode solutionNode = _treeView.Nodes[0];
            foreach (TreeNode projectNode in solutionNode.Nodes)
            {
                var project = projectNode.Tag as ProjectModel;
                if (project != null && project.Name == projectName)
                {
                    return projectNode;
                }
            }
            return null;
        }

        private void OnTreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null || e.Node.Tag == null)
            {
                return;
            }

            TypeModel type = e.Node.Tag as TypeModel;
            if (type != null)
            {
                RaiseTypeSelected(type);
            }
        }

        private void OnTreeNodeDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            // Reveal the immediate children first, whatever the row turns out to be. This runs
            // before the Tag checks on purpose: adornment rows (Dependencies, Standard Library,
            // …) carry no model and would otherwise return below without ever toggling.
            ExpandOnDoubleClick(e.Node);

            if (e.Node == null || e.Node.Tag == null)
            {
                return;
            }

            TypeModel type = e.Node.Tag as TypeModel;
            if (type != null)
            {
                RaiseTypeDoubleClicked(type);
                return;
            }

            FunctionModel fn = e.Node.Tag as FunctionModel;
            if (fn != null)
            {
                RaiseFunctionDoubleClicked(fn);
                return;
            }

            // A member node navigates to its exact declaration LINE: reuse the function-open path,
            // which does OpenInEditor(file, line), by synthesizing a FunctionModel from the member's
            // line and its declaring type's file (the parent node carries the TypeModel).
            if (e.Node.Tag is MemberModel member && e.Node.Parent != null && e.Node.Parent.Tag is TypeModel parentType)
            {
                if (member.SourceLineNumber > 0 && !string.IsNullOrEmpty(parentType.SourceFilePath))
                {
                    RaiseFunctionDoubleClicked(new FunctionModel
                    {
                        Name = member.Name,
                        SourceFilePath = parentType.SourceFilePath,
                        SourceLineNumber = member.SourceLineNumber,
                        Project = parentType.Project,
                    });
                }
                else
                {
                    RaiseTypeDoubleClicked(parentType);   // fall back to the type when the line is unknown
                }
                return;
            }

            // A Dependencies row for another package in this solution is a real place in real
            // source, so it navigates like everything else in the tree: the module row opens the
            // module, an imported-name row goes to that name's declaration line. Rows for the
            // standard library and for installed packages resolve to nothing here and stay inert —
            // their source is not part of the solution.
            ModuleDependencyGroup dep = e.Node.Tag as ModuleDependencyGroup;
            if (dep != null)
            {
                NavigateToDependency(dep);
                return;
            }

            FileGroup fg = e.Node.Tag as FileGroup;
            if (fg != null && !string.IsNullOrEmpty(fg.FilePath))
            {
                EventHandler<string> h = FileDoubleClicked;
                if (h != null) h(this, fg.FilePath);
                return;
            }

            // §5.5: double-clicking a project or the solution opens its element too —
            // the backing pyproject.toml / solution file, on top of the one-level expansion
            // every row now gets.
            if (e.Node.Tag is ProjectModel projectModel && !string.IsNullOrEmpty(projectModel.FilePath)
                && System.IO.File.Exists(projectModel.FilePath))
            {
                EventHandler<string> h = FileDoubleClicked;
                if (h != null) h(this, projectModel.FilePath);
                return;
            }
            if (e.Node.Tag is SolutionModel solutionModel && !string.IsNullOrEmpty(solutionModel.FilePath)
                && System.IO.File.Exists(solutionModel.FilePath))
            {
                EventHandler<string> h = FileDoubleClicked;
                if (h != null) h(this, solutionModel.FilePath);
            }
        }

        // ---------------------------------------------------------------- dependency navigation

        /// <summary>
        /// Open what a Dependencies row points at: the declaration of the imported name, or the
        /// module itself for a module row. Silently does nothing when the target is not in the
        /// solution (standard library, installed packages), which is the honest outcome — the IDE
        /// has no source to show.
        /// </summary>
        private void NavigateToDependency(ModuleDependencyGroup dep)
        {
            if (dep == null || dep.Dependency == null) return;
            SolutionModel solution = dep.Project == null ? null : dep.Project.Solution;
            if (solution == null) return;

            TypeModel type;
            FunctionModel function;
            string filePath;
            if (!TryResolveDependencyTarget(solution, dep.Dependency.ModuleName, dep.SymbolName,
                    out type, out function, out filePath))
                return;

            if (type != null) RaiseTypeDoubleClicked(type);
            else if (function != null) RaiseFunctionDoubleClicked(function);
            else if (!string.IsNullOrEmpty(filePath))
            {
                EventHandler<string> h = FileDoubleClicked;
                if (h != null) h(this, filePath);
            }

            // Move the selection off the dependency row and onto the file the user just navigated
            // to. A Dependencies row is a REFERENCE to code that lives elsewhere in the tree, so
            // leaving the highlight on the reference after jumping to the definition would point at
            // the wrong place — and the host's track-selection cannot do it for us here, because
            // this row is not what it maps a document back to.
            //
            // Deferred to the next message: this runs inside the TreeView's own double-click
            // handling, which finishes by re-asserting the clicked node as the selected one.
            string target = type != null ? type.SourceFilePath
                          : function != null ? function.SourceFilePath
                          : filePath;
            SelectFileNodeWhenIdle(target);
        }

        // Set the selection after the current message has been processed. Assigning SelectedNode
        // from inside a double-click handler is undone by the TreeView's own post-click bookkeeping.
        private void SelectFileNodeWhenIdle(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || _treeView == null) return;
            if (!_treeView.IsHandleCreated) { SelectFileNode(filePath); return; }
            try
            {
                _treeView.BeginInvoke((Action)(() =>
                {
                    try { SelectFileNode(filePath); }
                    catch { /* the tree may have been rebuilt in between */ }
                }));
            }
            catch { SelectFileNode(filePath); }
        }

        /// <summary>
        /// Where a dependency row lives in this solution's own source.
        ///
        /// The module name written in an import IS its path on disk — <c>lib_core.errors</c> is
        /// <c>…/lib_core/errors.py</c> — which holds for both the flat layout and src-layout,
        /// because in src-layout the importable package directory is what the suffix matches. So
        /// the file is found by suffix rather than by reconstructing each project's package root,
        /// and a package renamed on disk simply stops matching instead of resolving to the wrong
        /// file. <paramref name="symbolName"/> null means "the module itself".
        /// </summary>
        internal static bool TryResolveDependencyTarget(
            SolutionModel solution, string moduleName, string symbolName,
            out TypeModel type, out FunctionModel function, out string filePath)
        {
            type = null;
            function = null;
            filePath = null;
            if (solution == null || string.IsNullOrEmpty(moduleName)) return false;

            string asPath = moduleName.Replace('.', Path.DirectorySeparatorChar);
            string moduleSuffix = Path.DirectorySeparatorChar + asPath + ".py";
            string packageSuffix = Path.DirectorySeparatorChar + asPath
                                 + Path.DirectorySeparatorChar + "__init__.py";

            // Every declaration the solution knows about that lives in that module's file.
            var typesInModule = new List<TypeModel>();
            var functionsInModule = new List<FunctionModel>();
            foreach (ProjectModel p in solution.Projects)
            {
                if (p == null) continue;
                foreach (TypeModel t in p.Types)
                    if (t != null && MatchesModuleFile(t.SourceFilePath, moduleSuffix, packageSuffix))
                        typesInModule.Add(t);
                foreach (FunctionModel f in p.Functions)
                    if (f != null && MatchesModuleFile(f.SourceFilePath, moduleSuffix, packageSuffix))
                        functionsInModule.Add(f);
            }

            if (typesInModule.Count > 0) filePath = typesInModule[0].SourceFilePath;
            else if (functionsInModule.Count > 0) filePath = functionsInModule[0].SourceFilePath;
            else return false;

            if (string.IsNullOrEmpty(symbolName)) return true;   // the module row: open the file

            foreach (TypeModel t in typesInModule)
                if (string.Equals(t.Name, symbolName, StringComparison.Ordinal)) { type = t; return true; }
            foreach (FunctionModel f in functionsInModule)
                if (string.Equals(f.Name, symbolName, StringComparison.Ordinal)) { function = f; return true; }

            // An imported name with no declaration of its own — a module-level constant, or a
            // re-export from somewhere else. Opening the module it came from still lands the user
            // in the right file, which beats the row doing nothing at all.
            return true;
        }

        private static bool MatchesModuleFile(string sourceFilePath, string moduleSuffix, string packageSuffix)
        {
            if (string.IsNullOrEmpty(sourceFilePath)) return false;
            string normalized = sourceFilePath.Replace('/', Path.DirectorySeparatorChar);
            return normalized.EndsWith(moduleSuffix, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(packageSuffix, StringComparison.OrdinalIgnoreCase);
        }

        private void RaiseTypeSelected(TypeModel type)
        {
            EventHandler<TypeSelectedEventArgs> handler = TypeSelected;
            if (handler != null)
            {
                handler(this, new TypeSelectedEventArgs(type));
            }
        }

        private void RaiseTypeDoubleClicked(TypeModel type)
        {
            EventHandler<TypeSelectedEventArgs> handler = TypeDoubleClicked;
            if (handler != null)
            {
                handler(this, new TypeSelectedEventArgs(type));
            }
        }

        /// <summary>
        /// Which rows own a checkbox: the solution and its projects, and nothing below them.
        ///
        /// The checkbox means one thing here — "show this project's types on the Designer canvas"
        /// (the status bar says so: "Check projects in tree to show on canvas"), and visibility is
        /// project-granular everywhere downstream: the projection builders, the category tabs, and
        /// the per-solution setting all take a set of project *names*.
        ///
        /// Until iter 352 every row carried a checkbox anyway, and the two things a user would try
        /// with one both lied. Ticking a single class walked up the parents setting each to "any
        /// child checked", so the whole project appeared on the canvas — 2083 types on SymPy, from
        /// one tick. Un-ticking a class did nothing at all: a sibling was still checked, so the
        /// project stayed checked and the class stayed on the canvas. A control that cannot do what
        /// it depicts is worse than no control, so the rows that cannot honour it no longer draw one.
        /// </summary>
        public static bool ShowsCheckbox(TreeNode node)
        {
            return node != null && (node.Tag is SolutionModel || node.Tag is ProjectModel);
        }

        // --- Tree file clipboard (§5.3): Cut/Copy a file row, Paste onto file/folder/project ---

        private string _pendingClipboardFile;
        private bool _pendingClipboardCut;

        /// <summary>Raised when the user pastes a file onto a tree node; the host moves/copies on disk.</summary>
        public event EventHandler<FilePasteEventArgs> FilePasteRequested;

        internal bool HasFileClipboard
        {
            get { return !string.IsNullOrEmpty(_pendingClipboardFile); }
        }

        internal string PendingClipboardFile
        {
            get { return _pendingClipboardFile; }
        }

        internal bool PendingClipboardIsCut
        {
            get { return _pendingClipboardCut; }
        }

        internal void CopyFileNode(TreeNode node, bool cut)
        {
            var fg = node != null ? node.Tag as FileGroup : null;
            if (fg == null || string.IsNullOrEmpty(fg.FilePath)) return;
            _pendingClipboardFile = fg.FilePath;
            _pendingClipboardCut = cut;
            // Mirror onto the system clipboard so the file can also be pasted into
            // Explorer; failures here are cosmetic (clipboard can be locked).
            try
            {
                var paths = new System.Collections.Specialized.StringCollection { fg.FilePath };
                Clipboard.SetFileDropList(paths);
            }
            catch { }
        }

        internal void PasteIntoNode(TreeNode node)
        {
            if (node == null) return;
            string source = _pendingClipboardFile;
            bool cut = _pendingClipboardCut;
            if (string.IsNullOrEmpty(source))
            {
                // Fall back to a file copied in Explorer (copy semantics).
                try
                {
                    if (Clipboard.ContainsFileDropList())
                    {
                        var list = Clipboard.GetFileDropList();
                        if (list != null && list.Count > 0) source = list[0];
                        cut = false;
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(source)) return;
            EventHandler<FilePasteEventArgs> handler = FilePasteRequested;
            if (handler != null) handler(this, new FilePasteEventArgs(source, cut, node, node.Tag));
            if (cut)
            {
                // A cut source exists only once; the paste consumed it.
                _pendingClipboardFile = null;
                _pendingClipboardCut = false;
            }
        }

        private static bool IsPasteTarget(TreeNode node)
        {
            return node != null &&
                   (node.Tag is FileGroup || node.Tag is FolderGroup || node.Tag is ProjectModel);
        }

        // Armed by the second MouseDown of a left double-click on a row (not on the +/- glyph);
        // consumed by BeforeExpand/BeforeCollapse to cancel the TreeView's built-in
        // toggle-on-double-click, and cleared on MouseUp (a leaf double-click never toggles,
        // so the flag must not leak into a later keyboard/glyph expand).
        //
        // The built-in toggle is still cancelled even though double-click now DOES expand: the
        // control fires it at an unspecified point around NodeMouseDoubleClick, so letting both
        // run would toggle twice and land back where it started — the exact "nothing happens"
        // the explicit toggle exists to fix. Armed also means "the click was on the row, not on
        // the glyph", which is what gates ToggleExpansionOnDoubleClick.
        private bool _suppressDoubleClickToggle;

        /// <summary>
        /// Opens a double-clicked row one level, so its immediate children become visible, and
        /// closes it again on the next double-click. One level only — <see cref="TreeNode.Expand"/>
        /// leaves the grandchildren as the user left them, and the glyph and keyboard keep working
        /// exactly as before. Rows with no children are untouched.
        /// </summary>
        private void ExpandOnDoubleClick(TreeNode node)
        {
            if (node == null || node.Nodes.Count == 0) return;
            // A double-click ON the glyph has already been toggled twice by the control itself;
            // toggling a third time here would fight it.
            if (!_suppressDoubleClickToggle) return;

            // Our own expand must not be cancelled by the guard that kills the built-in one.
            _suppressDoubleClickToggle = false;
            try
            {
                if (!node.IsExpanded) node.Expand();
            }
            finally
            {
                _suppressDoubleClickToggle = true;
            }
        }

        private static string DependencyNodeLabel(ProjectModel project)
        {
            if (project == null) return "Dependencies";
            int count = project.ProjectReferences == null ? 0 : project.ProjectReferences.Count;
            if (project.UnresolvedProjectReferences != null) count += project.UnresolvedProjectReferences.Count;
            if (project.ModuleDependencies != null) count += project.ModuleDependencies.Count;
            return count == 0 ? "Dependencies" : "Dependencies (" + count + ")";
        }

        private void OnTreeMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Clicks < 2) return;
            TreeViewHitTestInfo hit = _treeView.HitTest(e.Location);
            // Rapid clicks on the expansion glyph keep toggling — that is the glyph's job.
            _suppressDoubleClickToggle = hit.Location != TreeViewHitTestLocations.PlusMinus;
        }

        private void OnTreeMouseUp(object sender, MouseEventArgs e)
        {
            _suppressDoubleClickToggle = false;
        }

        private void OnTreeBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (_suppressDoubleClickToggle)
            {
                e.Cancel = true;
                return;
            }
            if (e.Node != null) StripCheckboxes(e.Node.Nodes);
        }

        private void OnTreeBeforeCollapse(object sender, TreeViewCancelEventArgs e)
        {
            if (_suppressDoubleClickToggle) e.Cancel = true;
        }

        /// <summary>
        /// Removes the state image (the checkbox) from every row in <paramref name="nodes"/> that
        /// does not own one. One level only — deeper rows are done as they are expanded into view.
        /// </summary>
        private void StripCheckboxes(TreeNodeCollection nodes)
        {
            if (nodes == null || !_treeView.IsHandleCreated) return;

            foreach (TreeNode node in nodes)
            {
                if (ShowsCheckbox(node)) continue;
                HideCheckbox(node);
            }
        }

        // TreeView has no per-node "no checkbox" property: a node draws one when its state-image
        // index is non-zero, so zeroing that index through TVM_SETITEM removes it. Standard
        // technique; the row keeps its normal image, indent and hit-testing.
        private const int TV_FIRST = 0x1100;
        private const int TVM_SETITEMW = TV_FIRST + 63;
        private const int TVIF_STATE = 0x0008;
        private const int TVIS_STATEIMAGEMASK = 0xF000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TVITEM
        {
            public int mask;
            public IntPtr hItem;
            public int state;
            public int stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public int iSelectedImage;
            public int cChildren;
            public IntPtr lParam;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TVITEM lParam);

        private void HideCheckbox(TreeNode node)
        {
            IntPtr hItem = node.Handle;
            if (hItem == IntPtr.Zero) return;

            TVITEM item = new TVITEM();
            item.hItem = hItem;
            item.mask = TVIF_STATE;
            item.stateMask = TVIS_STATEIMAGEMASK;
            item.state = 0;   // state image 0 = draw nothing
            SendMessage(_treeView.Handle, TVM_SETITEMW, IntPtr.Zero, ref item);
        }

        private void OnTreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null || e.Action == TreeViewAction.Unknown)
            {
                return;
            }

            if (_suppressCheckEvents)
            {
                return;
            }

            // A row with no checkbox holds no visibility state — a stray check on one (keyboard
            // space bar, a programmatic assignment) must not redraw the canvas.
            if (!ShowsCheckbox(e.Node))
            {
                return;
            }

            _suppressCheckEvents = true;
            try
            {
                bool isChecked = e.Node.Checked;

                // Propagate down to all children
                SetChildrenChecked(e.Node, isChecked);

                // Propagate up to update parent state
                UpdateParentCheckState(e.Node.Parent);

                // Fire visibility changed for all affected project nodes
                RaiseVisibilityForAffectedProjects(e.Node);
            }
            finally
            {
                _suppressCheckEvents = false;
            }
        }

        // Only rows that own a checkbox carry check state, so the solution row ticks its projects and
        // stops. (It also means ticking a project no longer sets Checked on every file, class and
        // function under it — on SymPy that was ~10,000 native item updates for one click.)
        private void SetChildrenChecked(TreeNode node, bool isChecked)
        {
            foreach (TreeNode child in node.Nodes)
            {
                if (!ShowsCheckbox(child)) continue;
                child.Checked = isChecked;
                SetChildrenChecked(child, isChecked);
            }
        }

        private void UpdateParentCheckState(TreeNode parent)
        {
            if (parent == null)
            {
                return;
            }

            bool anyChecked = false;

            foreach (TreeNode child in parent.Nodes)
            {
                if (child.Checked)
                {
                    anyChecked = true;
                    break;
                }
            }

            // WinForms TreeView does not support indeterminate state natively;
            // set parent checked if any child is checked
            parent.Checked = anyChecked;

            // Continue up the tree
            UpdateParentCheckState(parent.Parent);
        }

        private void RaiseVisibilityForAffectedProjects(TreeNode changedNode)
        {
            // Collect all project nodes affected by this check change
            if (changedNode.Tag is ProjectModel project)
            {
                RaiseProjectVisibilityChanged(project.Name, changedNode.Checked);
                return;
            }

            // If solution node or category node changed, notify for all project children
            if (changedNode.Tag is SolutionModel)
            {
                foreach (TreeNode projectNode in changedNode.Nodes)
                {
                    var proj = projectNode.Tag as ProjectModel;
                    if (proj != null)
                    {
                        RaiseProjectVisibilityChanged(proj.Name, projectNode.Checked);
                    }
                }
                return;
            }

            // If a category or type node, find the parent project node and notify
            TreeNode current = changedNode;
            while (current != null)
            {
                if (current.Tag is ProjectModel parentProject)
                {
                    RaiseProjectVisibilityChanged(parentProject.Name, current.Checked);
                    return;
                }
                current = current.Parent;
            }
        }

        private void RaiseProjectVisibilityChanged(string projectName, bool isVisible)
        {
            EventHandler<ProjectVisibilityChangedEventArgs> handler = ProjectVisibilityChanged;
            if (handler != null)
            {
                handler(this, new ProjectVisibilityChangedEventArgs(projectName, isVisible));
            }
        }
    }

    public class TypeSelectedEventArgs : EventArgs
    {
        public TypeSelectedEventArgs(TypeModel type)
        {
            TypeModel = type;
        }

        public TypeModel TypeModel { get; private set; }
    }

    public class FunctionSelectedEventArgs : EventArgs
    {
        public FunctionSelectedEventArgs(FunctionModel function)
        {
            Function = function;
        }

        public FunctionModel Function { get; private set; }
    }

    public class ProjectVisibilityChangedEventArgs : EventArgs
    {
        public ProjectVisibilityChangedEventArgs(string projectName, bool isVisible)
        {
            ProjectName = projectName;
            IsVisible = isVisible;
        }

        public string ProjectName { get; private set; }
        public bool IsVisible { get; private set; }
    }

    /// <summary>A pending tree-clipboard paste: which file, cut vs copy, and onto what.</summary>
    public class FilePasteEventArgs : EventArgs
    {
        public FilePasteEventArgs(string sourcePath, bool isCut, TreeNode node, object targetItem)
        {
            SourcePath = sourcePath;
            IsCut = isCut;
            Node = node;
            TargetItem = targetItem;
        }

        public string SourcePath { get; private set; }
        public bool IsCut { get; private set; }
        public TreeNode Node { get; private set; }
        public object TargetItem { get; private set; }
    }

    /// <summary>
    /// SRS §5.1 AST-level drag-rearrange: a TypeModel dropped onto a different file
    /// row within the tree, asking the host to physically move that class's source
    /// text into the target file.
    /// </summary>
    public class MoveTypeEventArgs : EventArgs
    {
        public MoveTypeEventArgs(HelixPythonNamespace.Core.Model.TypeModel type, object targetItem)
        {
            Type = type;
            TargetItem = targetItem;
        }

        public HelixPythonNamespace.Core.Model.TypeModel Type { get; private set; }
        public object TargetItem { get; private set; }
    }

    public class TreeOperationEventArgs : EventArgs
    {
        public TreeOperationEventArgs(TreeNode node, object item)
        {
            Node = node;
            Item = item;
        }

        public TreeNode Node { get; private set; }
        public object Item { get; private set; }
    }

    public class TreeRenameEventArgs : EventArgs
    {
        public TreeRenameEventArgs(TreeNode node, object item, string newName)
        {
            Node = node;
            Item = item;
            NewName = newName;
        }

        public TreeNode Node { get; private set; }
        public object Item { get; private set; }
        public string NewName { get; private set; }
    }
}
