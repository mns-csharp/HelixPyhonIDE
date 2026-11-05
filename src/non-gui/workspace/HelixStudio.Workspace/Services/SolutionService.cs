using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// Python-only solution loader. A "solution" is a pyproject.toml and the
    /// .py / .pyi files beneath it.
    /// </summary>
    public sealed class SolutionService : ISolutionService
    {
        // Public so the Import Engine shares the exact same exclusion list instead
        // of drifting its own copy.
        public static readonly HashSet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "__pycache__", ".venv", "venv", "env", ".env",
            "build", "dist", ".tox", ".pytest_cache", "site-packages",
            ".git", ".hg", ".svn", ".idea", ".vs", "node_modules"
        };

        public Task<SolutionLoadResult> LoadAsync(string path)
        {
            return Task.Run(() => LoadInternal(path));
        }

        public Task<SolutionLoadResult> ReloadAsync(SolutionModel model)
        {
            if (model == null) return Task.FromResult(new SolutionLoadResult("No solution to reload."));
            return LoadAsync(model.FilePath);
        }

        public void Save(SolutionModel model)
        {
            // Python source files are the source of truth; no solution-level serialization needed.
        }

        public SolutionModel CreateNew(string directoryPath, string name)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) throw new ArgumentException("directoryPath");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name");
            Directory.CreateDirectory(directoryPath);
            string pyproject = Path.Combine(directoryPath, "pyproject.toml");
            if (!File.Exists(pyproject)) PyProjectWriter.WriteMinimal(pyproject, name, "3.12");
            string packageDir = Path.Combine(directoryPath, SafePackage(name));
            Directory.CreateDirectory(packageDir);
            string init = Path.Combine(packageDir, "__init__.py");
            if (!File.Exists(init)) File.WriteAllText(init, "");
            SolutionModel model = new SolutionModel(pyproject, name);
            ProjectModel project = new ProjectModel(pyproject, name, model);
            project.DefaultNamespace = SafePackage(name);
            project.PythonVersion = "3.12";
            project.TargetFramework = "python3.12";
            model.Projects.Add(project);
            return model;
        }

        private SolutionLoadResult LoadInternal(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new SolutionLoadResult("File not found: " + path);

            // Resolution order:
            //   1. Direct pyproject.toml.
            //   2. .sln / .slnx with an adjacent pyproject.toml (scaffolder case).
            //   3. .sln / .slnx with no pyproject.toml — parse natively via
            //      VsSolutionReader + PyProjReader and build per-.pyproj projects.
            string pyprojectPath = NormalizePyProjectPath(path);
            if (pyprojectPath == null)
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".sln" || ext == ".slnx")
                    return LoadSlnNative(path);
                return new SolutionLoadResult("Expected a pyproject.toml, .sln, or .slnx file. Got: " + path);
            }

            PyProjectMetadata meta;
            try { meta = PyProjectReader.Read(pyprojectPath); }
            catch (Exception ex) { return new SolutionLoadResult("Failed to read pyproject.toml: " + ex.Message); }

            string name = meta.Name ?? Path.GetFileName(Path.GetDirectoryName(pyprojectPath));
            SolutionModel solution = new SolutionModel(pyprojectPath, name);
            string rootDir = Path.GetDirectoryName(pyprojectPath);
            solution.LinkedCMakeProjectPath = ResolveLinkedCMakeProject(meta, rootDir);

            // Declared [tool.helixstudio].references per project, resolved to edges once every
            // member exists (a reference may name a member that loads later in the list).
            var declaredReferences = new List<KeyValuePair<ProjectModel, List<string>>>();

            if (meta.IsWorkspace)
            {
                // The root pyproject.toml may itself BE a project as well as the workspace
                // aggregator — the shape the New Project wizard creates (main.py + an explicit
                // [tool.helixstudio] kind at the root). uv's own convention agrees: a workspace
                // root carrying a real [project] is implicitly a member. Without this, adding
                // the FIRST member made the root project vanish from the loaded solution — to
                // the user it looked like the new project had replaced their whole solution.
                // Only an EXPLICIT kind token qualifies: pure aggregator roots (which have a
                // [project] name but no kind) keep loading exactly as before.
                string rootKindTok = null;
                if (meta.ToolHelixStudio != null) meta.ToolHelixStudio.TryGetValue("kind", out rootKindTok);
                if (!string.IsNullOrEmpty(rootKindTok))
                {
                    ProjectModel rootProject = new ProjectModel(pyprojectPath, name, solution);
                    rootProject.DefaultNamespace = meta.Name ?? name;
                    rootProject.PythonVersion = meta.PythonVersion ?? "3.12";
                    rootProject.TargetFramework = "python" + rootProject.PythonVersion;
                    rootProject.Kind = HelixPythonNamespace.Core.Model.Enums.ProjectKinds.FromToken(rootKindTok);
                    string rootGuiTok = null;
                    meta.ToolHelixStudio.TryGetValue("gui-framework", out rootGuiTok);
                    if (rootProject.Kind == HelixPythonNamespace.Core.Model.Enums.ProjectKind.GuiApp && !string.IsNullOrEmpty(rootGuiTok))
                        rootProject.GuiFramework = HelixPythonNamespace.Core.Model.Enums.GuiFrameworks.FromToken(rootGuiTok);
                    string rootStartupTok = null;
                    meta.ToolHelixStudio.TryGetValue("startup", out rootStartupTok);
                    rootProject.IsStartup = ParseTomlBool(rootStartupTok);
                    string rootRunArgs = null;
                    meta.ToolHelixStudio.TryGetValue("run-args", out rootRunArgs);
                    rootProject.RunArguments = rootRunArgs;
                    // Only the USER's own exclusions surface as ExcludedPaths (the tree renders
                    // that list as the "Excluded Files" node). Member subtrees are a pure
                    // SCAN exclusion — root and members must never double-claim files, but a
                    // sibling member is not "excluded" in any user-facing sense (showing added
                    // projects under Excluded Files was a reported bug).
                    rootProject.ExcludedPaths = meta.Excluded;
                    var memberScanExclusions = new List<string>();
                    foreach (string memberRel in meta.WorkspaceMembers)
                        if (!string.IsNullOrWhiteSpace(memberRel)) memberScanExclusions.Add(memberRel);
                    solution.Projects.Add(rootProject);
                    declaredReferences.Add(new KeyValuePair<ProjectModel, List<string>>(rootProject, meta.References));
                    PopulateTypes(rootProject, rootDir, memberScanExclusions);
                }

                // Workspace mode: one ProjectModel per member, each scanning only its subtree.
                foreach (string memberRel in meta.WorkspaceMembers)
                {
                    string memberDir = Path.GetFullPath(Path.Combine(rootDir, memberRel));
                    if (!Directory.Exists(memberDir)) continue;
                    string memberToml = Path.Combine(memberDir, "pyproject.toml");
                    PyProjectMetadata memberMeta = null;
                    if (File.Exists(memberToml))
                    {
                        try { memberMeta = PyProjectReader.Read(memberToml); } catch { memberMeta = null; }
                    }
                    string memberName = (memberMeta != null && !string.IsNullOrEmpty(memberMeta.Name))
                        ? memberMeta.Name
                        : Path.GetFileName(memberDir.TrimEnd(Path.DirectorySeparatorChar));
                    string projectAnchor = File.Exists(memberToml) ? memberToml : Path.Combine(memberDir, ".helixstudio-member");
                    ProjectModel member = new ProjectModel(projectAnchor, memberName, solution);
                    member.DefaultNamespace = memberName;
                    member.PythonVersion = (memberMeta != null && !string.IsNullOrEmpty(memberMeta.PythonVersion))
                        ? memberMeta.PythonVersion : (meta.PythonVersion ?? "3.12");
                    member.TargetFramework = "python" + member.PythonVersion;
                    string memberKindTok = null;
                    string memberGuiTok = null;
                    if (memberMeta != null && memberMeta.ToolHelixStudio != null)
                    {
                        memberMeta.ToolHelixStudio.TryGetValue("kind", out memberKindTok);
                        memberMeta.ToolHelixStudio.TryGetValue("gui-framework", out memberGuiTok);
                    }
                    member.Kind = HelixPythonNamespace.Core.Model.Enums.ProjectKinds.FromToken(memberKindTok);
                    if (member.Kind == HelixPythonNamespace.Core.Model.Enums.ProjectKind.GuiApp && !string.IsNullOrEmpty(memberGuiTok))
                        member.GuiFramework = HelixPythonNamespace.Core.Model.Enums.GuiFrameworks.FromToken(memberGuiTok);
                    string memberStartupTok = null;
                    string memberRunArgs = null;
                    if (memberMeta != null && memberMeta.ToolHelixStudio != null)
                    {
                        memberMeta.ToolHelixStudio.TryGetValue("startup", out memberStartupTok);
                        memberMeta.ToolHelixStudio.TryGetValue("run-args", out memberRunArgs);
                    }
                    member.IsStartup = ParseTomlBool(memberStartupTok);
                    member.RunArguments = memberRunArgs;
                    if (memberMeta != null) member.ExcludedPaths = memberMeta.Excluded;
                    solution.Projects.Add(member);
                    if (memberMeta != null)
                        declaredReferences.Add(new KeyValuePair<ProjectModel, List<string>>(member, memberMeta.References));
                    PopulateTypes(member, memberDir);
                }
            }
            else
            {
                // Single-project pyproject.toml (legacy / classic behavior).
                ProjectModel project = new ProjectModel(pyprojectPath, name, solution);
                project.DefaultNamespace = meta.Name ?? name;
                project.PythonVersion = meta.PythonVersion ?? "3.12";
                project.TargetFramework = "python" + project.PythonVersion;
                string kindTok = null;
                string guiTok = null;
                if (meta.ToolHelixStudio != null)
                {
                    meta.ToolHelixStudio.TryGetValue("kind", out kindTok);
                    meta.ToolHelixStudio.TryGetValue("gui-framework", out guiTok);
                }
                project.Kind = HelixPythonNamespace.Core.Model.Enums.ProjectKinds.FromToken(kindTok);
                if (project.Kind == HelixPythonNamespace.Core.Model.Enums.ProjectKind.GuiApp && !string.IsNullOrEmpty(guiTok))
                    project.GuiFramework = HelixPythonNamespace.Core.Model.Enums.GuiFrameworks.FromToken(guiTok);
                string startupTok = null;
                string runArgs = null;
                if (meta.ToolHelixStudio != null)
                {
                    meta.ToolHelixStudio.TryGetValue("startup", out startupTok);
                    meta.ToolHelixStudio.TryGetValue("run-args", out runArgs);
                }
                project.IsStartup = ParseTomlBool(startupTok);
                project.RunArguments = runArgs;
                project.ExcludedPaths = meta.Excluded;
                solution.Projects.Add(project);
                PopulateTypes(project, rootDir);
            }

            ApplyProjectOrder(solution, meta.ProjectOrder);

            // PSM-41…46 read-back: without this, references added in the IDE lived only in
            // memory and silently evaporated on the next load.
            foreach (var declared in declaredReferences)
            {
                if (declared.Value == null) continue;
                foreach (string refName in declared.Value)
                {
                    ProjectModel target = solution.Projects.Find(
                        p => string.Equals(p.Name, refName, StringComparison.OrdinalIgnoreCase));
                    if (target != null && !ReferenceEquals(target, declared.Key) &&
                        !declared.Key.ProjectReferences.Contains(target))
                        declared.Key.ProjectReferences.Add(target);
                }
            }

            NormalizeTestProjects(solution);
            NormalizeStartup(solution);
            ClassifyDependencies(solution);

            foreach (TypeModel t in solution.GetAllTypes())
                foreach (TypeReference r in t.BaseTypes)
                    r.Bind(solution);

            solution.LastParsedUtc = DateTime.UtcNow;

            // What is REALLY on disk, recorded on the model before anything reads it. The tree
            // renders this and the parity checker projects it, so both halves of rule 4 answer from
            // one scan. Best-effort: an unreadable directory must not stop a solution opening, and
            // an empty inventory degrades to the old symbol-derived tree rather than to a wrong one.
            try
            {
                HelixPythonNamespace.Workspace.Structure.StructureInventory.Populate(solution);
            }
            catch { /* the parity report below is what reports a scan that could not complete */ }

            // Structure parity (PY8004), the load gate. Runs AFTER the model is built — it compares
            // the model against the disk, so it has nothing to compare until now — and before the
            // result reaches any caller, i.e. before the tree can be drawn. Read-only: it never
            // repairs anything, because repair needs the user's consent and this is a non-GUI
            // service. See HelixPythonNamespace.Workspace.Structure.DirectoryStructureCorrectionEngine.
            var loaded = new SolutionLoadResult(solution);
            try
            {
                loaded.StructureParity =
                    new HelixPythonNamespace.Workspace.Structure.DirectoryStructureCorrectionEngine().Verify(solution);
            }
            catch
            {
                // A solution must still open if the parity scan itself trips over an unreadable
                // directory. A null report means "not verified", which the caller treats as "do not
                // prompt" — never as "verified clean".
                loaded.StructureParity = null;
            }
            return loaded;
        }

        private static void ApplyProjectOrder(SolutionModel solution, IList<string> projectOrder)
        {
            if (solution == null || projectOrder == null || projectOrder.Count == 0) return;
            var ordered = new List<ProjectModel>();
            foreach (string name in projectOrder)
            {
                ProjectModel match = solution.Projects.Find(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null && !ordered.Contains(match)) ordered.Add(match);
            }
            foreach (ProjectModel project in solution.Projects)
                if (!ordered.Contains(project)) ordered.Add(project);
            solution.Projects.Clear();
            solution.Projects.AddRange(ordered);
        }

        // [tool.helixstudio] linked-cmake-project = "<path>" — a path, relative to the WORKSPACE
        // ROOT pyproject.toml's directory, to a folder containing a CMakeLists.txt. Same tolerant
        // default as ProjectKinds.FromToken: a missing key, a path that doesn't exist, or a folder
        // with no CMakeLists.txt all resolve to null (no linked project) rather than failing the
        // whole solution load — this key must never be able to block opening the Python solution.
        private static string ResolveLinkedCMakeProject(PyProjectMetadata meta, string rootDir)
        {
            string token = null;
            if (meta != null && meta.ToolHelixStudio != null) meta.ToolHelixStudio.TryGetValue("linked-cmake-project", out token);
            if (string.IsNullOrWhiteSpace(token)) return null;

            string resolved;
            try { resolved = Path.GetFullPath(Path.Combine(rootDir, token)); }
            catch { return null; }

            if (!Directory.Exists(resolved)) return null;
            if (!File.Exists(Path.Combine(resolved, "CMakeLists.txt"))) return null;
            return resolved;
        }

        /// <summary>
        /// Recognizes test packages written before <c>ProjectKind.TestProject</c> existed.
        ///
        /// Solution policy rule 3 (PY8003) bars every kind except a test project from containing
        /// test files. Test packages used to be stored as <c>kind = "library"</c> and identified
        /// only by the VS-mirroring <c>&lt;project&gt;_tests</c> naming convention, so without this
        /// bridge the rule would fire on every pre-existing test package the moment it shipped —
        /// blocking Run and Debug on solutions the user never touched.
        ///
        /// Only ever upgrades a <see cref="ProjectKind.ClassLibrary"/>: an explicit kind token in
        /// the TOML always wins, so a library genuinely named <c>foo_test</c> can opt out by
        /// declaring its kind. Nothing is written back to disk — the inference is in-memory, the
        /// same way <see cref="NormalizeStartup"/> neutralizes a stale startup flag without
        /// rewriting the file.
        /// </summary>
        internal static void NormalizeTestProjects(SolutionModel solution)
        {
            if (solution == null || solution.Projects == null) return;
            foreach (ProjectModel p in solution.Projects)
            {
                if (p == null) continue;
                if (p.Kind != HelixPythonNamespace.Core.Model.Enums.ProjectKind.ClassLibrary) continue;
                if (!HelixPythonNamespace.Core.Model.Enums.ProjectKinds.LooksLikeTestProjectName(p.Name)) continue;
                p.Kind = HelixPythonNamespace.Core.Model.Enums.ProjectKind.TestProject;
            }
        }

        /// <summary>
        /// Enforces the startup-package invariants (docs/python-solution-model.md, PSM-08):
        /// only an executable package may be startup; at most one package is startup at a time; and
        /// when a solution has exactly one executable package it is the implicit startup (no explicit
        /// selection required). With multiple executables and none marked, none is startup — the user
        /// must choose before Run/Debug.
        /// </summary>
        internal static void NormalizeStartup(SolutionModel solution)
        {
            if (solution == null || solution.Projects == null) return;

            // A non-executable package is never the startup.
            foreach (ProjectModel p in solution.Projects)
                if (!p.IsExecutable) p.IsStartup = false;

            var executables = new List<ProjectModel>();
            foreach (ProjectModel p in solution.Projects)
                if (p.IsExecutable) executables.Add(p);

            if (executables.Count == 0) return;

            if (executables.Count == 1)
            {
                // Sole executable is the implicit startup.
                executables[0].IsStartup = true;
                return;
            }

            // Multiple executables: keep at most the first one explicitly marked startup.
            ProjectModel chosen = null;
            foreach (ProjectModel p in executables)
            {
                if (p.IsStartup && chosen == null) chosen = p;
                else p.IsStartup = false;
            }
        }

        private static bool ParseTomlBool(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            string t = token.Trim().ToLowerInvariant();
            return t == "true" || t == "1" || t == "yes";
        }

        /// <summary>
        /// Native loading of <c>.sln</c> / <c>.slnx</c> solutions whose projects
        /// are PTVS <c>.pyproj</c> files (no <c>pyproject.toml</c> anywhere).
        /// </summary>
        private SolutionLoadResult LoadSlnNative(string slnPath)
        {
            List<VsSolutionReader.ProjectReference> all;
            try { all = VsSolutionReader.ReadAll(slnPath); }
            catch (Exception ex) { return new SolutionLoadResult("Failed to parse solution: " + ex.Message); }
            if (all.Count == 0)
                return new SolutionLoadResult("Solution lists no projects that exist on disk: " + slnPath);

            // A `.sln` is not a Python file — it is a container, and what is IN it decides which
            // loader owns it. The extension stays registered to Python (this IDE writes PTVS
            // solutions), so the dispatch happens here, on the contents. Without this, opening an
            // ordinary Visual Studio solution failed with "contains no .pyproj references".
            var refs = new List<VsSolutionReader.ProjectReference>();
            var csprojs = new List<string>();
            var otherKinds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VsSolutionReader.ProjectReference r in all)
            {
                string ext = (Path.GetExtension(r.PyProjAbsolutePath) ?? string.Empty).ToLowerInvariant();
                if (ext == ".pyproj") refs.Add(r);
                else if (ext == ".csproj") csprojs.Add(r.PyProjAbsolutePath);
                else if (ext.Length > 0) otherKinds.Add(ext);
            }

            if (refs.Count == 0 && csprojs.Count > 0)
                return new SolutionLoadResult(
                    "This .sln lists " + csprojs.Count + " C# project(s) and no .pyproj. HelixPython "
                    + "opens Python solutions; open this one in HelixCSharp: " + slnPath);

            if (refs.Count == 0)
                return new SolutionLoadResult(otherKinds.Count > 0
                    ? "This solution's projects are " + string.Join("/", new List<string>(otherKinds).ToArray())
                      + ", which HelixPython cannot open yet. Open the project file directly, or use "
                      + "File ▸ Import Directory."
                    : "Solution contains no .pyproj references: " + slnPath);

            string fullSlnPath = Path.GetFullPath(slnPath);
            string solutionName = Path.GetFileNameWithoutExtension(fullSlnPath);
            SolutionModel solution = new SolutionModel(fullSlnPath, solutionName);

            foreach (VsSolutionReader.ProjectReference r in refs)
            {
                PyProjReader.PyProjInfo info;
                try { info = PyProjReader.Read(r.PyProjAbsolutePath); }
                catch { continue; } // skip malformed .pyproj; rest of solution still loads

                string projectName = !string.IsNullOrEmpty(info.Name) ? info.Name : r.Name;
                ProjectModel project = new ProjectModel(r.PyProjAbsolutePath, projectName, solution)
                {
                    DefaultNamespace = projectName,
                    PythonVersion = "3.12",
                    TargetFramework = "python3.12",
                    Kind = HelixPythonNamespace.Core.Model.Enums.ProjectKind.ClassLibrary,
                };
                solution.Projects.Add(project);
                PopulateTypes(project, info.ProjectHome);
            }

            foreach (TypeModel t in solution.GetAllTypes())
                foreach (TypeReference rt in t.BaseTypes)
                    rt.Bind(solution);

            solution.LastParsedUtc = DateTime.UtcNow;
            return new SolutionLoadResult(solution);
        }

        private static void PopulateTypes(ProjectModel project, string scanRoot,
            List<string> extraScanExclusions = null)
        {
            HashSet<string> excludedFullPaths = ResolveExcludedFullPaths(scanRoot, project.ExcludedPaths);
            if (extraScanExclusions != null)
                foreach (string full in ResolveExcludedFullPaths(scanRoot, extraScanExclusions))
                    excludedFullPaths.Add(full);

            // The Solution Explorer's Dependencies node is built from the same parse as the type
            // list — the AST is already in hand here, so collecting imports costs nothing extra.
            var deps = new HelixPythonNamespace.Workspace.Python.PackageDependencyScanner(project.Name);

            foreach (string file in EnumeratePythonFiles(scanRoot, excludedFullPaths))
            {
                try
                {
                    string source = File.ReadAllText(file);
                    ParseResult parsed = global::HelixPythonNamespace.Python.Python.ParseModule(source, file);
                    PythonTypeExtractor.ExtractInto(project, parsed.Module, file);
                    deps.AddImportsFrom(parsed.Module);
                }
                catch { /* ignore unreadable / malformed files */ }
            }

            // Origin is provisional until every project is known — ClassifyDependencies finalizes it.
            project.ModuleDependencies.Clear();
            project.ModuleDependencies.AddRange(deps.Build(null));

            project.ResourceFiles.Clear();
            project.ResourceFiles.AddRange(HelixPythonNamespace.Workspace.Python.PackageResourceScanner.Scan(scanRoot));
        }

        /// <summary>
        /// Second pass over every package's imports, now that the solution's full member list
        /// exists: an import of <c>lib_core</c> is a SOLUTION dependency when lib_core is a member
        /// here and a third-party one when it isn't, and that cannot be known while the first
        /// package is still being read.
        /// </summary>
        private static void ClassifyDependencies(SolutionModel solution)
        {
            if (solution == null || solution.Projects == null) return;

            var memberNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProjectModel p in solution.Projects)
            {
                if (p == null) continue;
                if (!string.IsNullOrEmpty(p.Name)) memberNames.Add(p.Name);
                // The import name is the package DIRECTORY name, which need not equal the project
                // name (src-layout puts the importable package one level down).
                if (!string.IsNullOrEmpty(p.ProjectDirectory))
                {
                    string dirName = Path.GetFileName(p.ProjectDirectory.TrimEnd(Path.DirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(dirName)) memberNames.Add(dirName);
                    string srcDir = Path.Combine(p.ProjectDirectory, "src");
                    if (Directory.Exists(srcDir))
                    {
                        try
                        {
                            foreach (string inner in Directory.GetDirectories(srcDir))
                                if (File.Exists(Path.Combine(inner, "__init__.py")))
                                    memberNames.Add(Path.GetFileName(inner));
                        }
                        catch { /* unreadable src/ — the project name alone still classifies */ }
                    }
                }
            }

            foreach (ProjectModel p in solution.Projects)
            {
                if (p == null) continue;
                foreach (ModuleDependency d in p.ModuleDependencies)
                    d.Origin = HelixPythonNamespace.Workspace.Python.PackageDependencyScanner.Classify(d.ModuleName, memberNames);
                p.ModuleDependencies.Sort(delegate (ModuleDependency a, ModuleDependency b)
                {
                    if (a.Origin != b.Origin) return ((int)a.Origin).CompareTo((int)b.Origin);
                    return string.Compare(a.ModuleName, b.ModuleName, StringComparison.OrdinalIgnoreCase);
                });
                p.Environment = HelixPythonNamespace.Workspace.Python.PythonEnvironmentResolver.Resolve(p, solution);
            }
        }

        private static string NormalizePyProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string name = Path.GetFileName(path);
            if (string.Equals(name, "pyproject.toml", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(path);
            // .sln / .slnx companions (written by PythonProjectScaffolder, or
            // hand-placed next to a Python package) resolve to the adjacent
            // pyproject.toml when one exists. Native .sln / .slnx loading
            // (no adjacent .toml) is handled by LoadSlnNative via LoadInternal.
            if (!string.IsNullOrEmpty(name) &&
                (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                {
                    string sibling = Path.Combine(dir, "pyproject.toml");
                    if (File.Exists(sibling)) return sibling;
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves each project-relative excluded entry (SRS §5.9) to an absolute,
        /// normalized path under <paramref name="scanRoot"/>. A directory entry's
        /// full path is later matched as a prefix, so everything beneath it is
        /// excluded too; entries that don't resolve (bad path chars, etc.) are
        /// skipped rather than aborting the load.
        /// </summary>
        /// <summary>
        /// Shared with <see cref="HelixPythonNamespace.Workspace.Python.PackageComplianceValidator"/>
        /// so an excluded file/folder is exempt from BOTH the loader's Types/Functions
        /// population and PSM compliance checking — not just the former.
        /// </summary>
        internal static HashSet<string> ResolveExcludedFullPaths(string scanRoot, List<string> excludedRelativePaths)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(scanRoot) || excludedRelativePaths == null) return result;
            foreach (string rel in excludedRelativePaths)
            {
                if (string.IsNullOrWhiteSpace(rel)) continue;
                try
                {
                    string full = Path.GetFullPath(Path.Combine(scanRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                    result.Add(full.TrimEnd(Path.DirectorySeparatorChar));
                }
                catch { /* malformed entry — ignore rather than fail the whole load */ }
            }
            return result;
        }

        // True when path is excludedFullPaths itself or lies beneath one of its
        // directory entries (a directory exclusion covers everything under it).
        internal static bool IsExcluded(string path, HashSet<string> excludedFullPaths)
        {
            if (excludedFullPaths.Count == 0) return false;
            if (excludedFullPaths.Contains(path.TrimEnd(Path.DirectorySeparatorChar))) return true;
            foreach (string excluded in excludedFullPaths)
            {
                if (path.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static IEnumerable<string> EnumeratePythonFiles(string root, HashSet<string> excludedFullPaths = null)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
            excludedFullPaths = excludedFullPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { continue; }
                foreach (string sub in subs)
                {
                    if (IgnoredDirectories.Contains(Path.GetFileName(sub))) continue;
                    if (IsExcluded(sub, excludedFullPaths)) continue;
                    stack.Push(sub);
                }
                string[] files;
                try { files = Directory.GetFiles(dir); } catch { continue; }
                HashSet<string> stubBases = new HashSet<string>(
                    files.Where(f => f.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase))
                         .Select(f => Path.GetFileNameWithoutExtension(f)),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string f in files.Where(f => f.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase)))
                {
                    if (IsExcluded(f, excludedFullPaths)) continue;
                    yield return f;
                }
                foreach (string f in files.Where(f => f.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
                {
                    if (stubBases.Contains(Path.GetFileNameWithoutExtension(f))) continue;
                    if (IsExcluded(f, excludedFullPaths)) continue;
                    yield return f;
                }
            }
        }

        private static string SafePackage(string name)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (c == '_' || c == '-' || c == '.') sb.Append('_');
            }
            return sb.Length == 0 ? "pkg" : sb.ToString();
        }
    }
}
