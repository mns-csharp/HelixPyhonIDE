namespace HelixPythonNamespace.Workspace.Execution
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    /// <summary>
    /// The single authority on what pressing Run does — used by the IDE's Run/Debug/Run-in-Terminal
    /// paths and by the headless <c>helixstudio</c> launcher.
    ///
    /// Everything here was lifted out of <c>MainForm</c> unchanged when the headless launcher was
    /// added. It is deliberately GUI-free and static: the alternative was a second implementation
    /// of the four hard blockers for the console, and this project has already measured what that
    /// costs — rule 2 exists twice (compliance pass + OOP auditor) and keeping those two honest is
    /// a standing tax. There is no third copy.
    ///
    /// The four rules are checked in the order the parity contract requires: PY8004 (structure)
    /// first, because PY8001 asks whether a file is inside a member and containment is exactly what
    /// a structure mismatch puts in doubt; then package compliance (PSM); then PY8001/2/3.
    /// </summary>
    public static class SolutionRunPlanner
    {
        // ---- blockers -------------------------------------------------------------------

        /// <summary>
        /// The solution-wide reasons a run cannot start, or null when there are none. Shared so a
        /// per-package launch and the startup launch cannot drift apart on what they enforce.
        /// </summary>
        public static SolutionRunPlan ComputeBlockers(SolutionModel sol, SolutionRunOptions options)
        {
            if (options == null) options = new SolutionRunOptions();
            var r = new SolutionRunPlan();
            if (sol == null) { r.BlockReason = "No solution loaded."; return r; }

            string structure = StructureBlockReason(options);
            if (structure != null) { r.BlockReason = structure; return r; }

            var compliance = HelixPythonNamespace.Workspace.Python.PackageComplianceValidator.Validate(sol);
            if (compliance.Count > 0)
            {
                r.BlockReason = compliance[0].ToString() +
                    " Run and Debug are blocked until project-model errors are corrected.";
                return r;
            }

            var violations = FindPolicyViolations(sol, options);
            if (violations.Count > 0)
            {
                r.PolicyViolations = violations;
                r.BlockReason = DescribePolicyViolations(violations, options.MessageStyle);
                return r;
            }
            return null;
        }

        /// <summary>
        /// The PY8004 half of the gate. Reported as one sentence with its own remedy, because the
        /// several unverified states have different remedies and a single hardcoded one sent a
        /// blocked user round a loop with no exit.
        /// </summary>
        private static string StructureBlockReason(SolutionRunOptions options)
        {
            return string.IsNullOrEmpty(options.StructureBlockReason) ? null : options.StructureBlockReason;
        }

        // ---- the plan --------------------------------------------------------------------

        /// <summary>
        /// Pure decision for "which package does Run launch, and if none, why not" (PSM-08). Kept
        /// side-effect free so it is unit-testable and so a host can surface the outcome without
        /// popping a modal dialog.
        /// </summary>
        public static SolutionRunPlan ComputeRunPlan(SolutionModel sol, SolutionRunOptions options)
        {
            if (options == null) options = new SolutionRunOptions();

            SolutionRunPlan blocked = ComputeBlockers(sol, options);
            if (blocked != null) return blocked;

            var r = new SolutionRunPlan();

            int runnable = 0;
            if (sol.Projects != null)
                foreach (ProjectModel p in sol.Projects)
                {
                    if (p == null || string.IsNullOrEmpty(p.FilePath)) continue;
                    string ignore;
                    if (TryFindEntryFile(p, out ignore)) runnable++;
                }

            string startup = ResolveStartupProject(sol);
            string module = ResolveStartupModule(sol);
            string entryFile = ResolveStartupEntryFile(sol);
            if (!string.IsNullOrEmpty(startup) && !string.IsNullOrEmpty(entryFile))
            {
                r.CanRun = true;
                r.StartupName = startup;
                r.RunModule = module;
                r.EntryFile = entryFile;
                // Read from the manifest, not the cached model — see ResolveEffectiveRunArguments.
                r.RunArguments = ResolveEffectiveRunArguments(ResolveStartupProjectModel(sol));
                r.WorkingDirectory = ResolveWorkingDirectoryForScript(sol, entryFile);
                return r;
            }

            if (runnable > 1)
            {
                r.BlockReason = "This solution has " + runnable + " executable packages and none is set as the startup. " +
                    "Right-click a package in the Solution Explorer -> 'Set as Startup Package' to choose which one Run launches.";
                return r;
            }

            // The advice must be in the terms of the language the user is actually writing. A
            // solution of native packages must not be told to add a main.py — and neither must a
            // native one, which is how this went wrong a second time.
            var languages = new List<ProjectLanguage>();
            bool anyNative = false;
            if (sol.Projects != null)
                foreach (ProjectModel p in sol.Projects)
                {
                    if (p == null || string.IsNullOrEmpty(p.FilePath)) continue;
                    if (ProjectKinds.IsNative(p.Kind)) { anyNative = true; continue; }
                    if (!languages.Contains(p.Language)) languages.Add(p.Language);
                }

            if (languages.Count == 1)
            {
                string advice = HelixPythonNamespace.Workspace.LanguageServices.EntryPointResolver
                    .MissingEntryPointAdvice(languages[0]);
                if (advice != null)
                {
                    r.BlockReason = "No runnable package found. " + advice;
                    return r;
                }
            }

            r.BlockReason = anyNative
                ? "No runnable target found. A CUDA/C++ package needs a CMakeLists.txt executable " +
                  "target with a resolvable main source file."
                : "No runnable package found. Set a startup package with a resolvable entry point " +
                  "(right-click a package -> 'Set as Startup Package').";
            return r;
        }

        /// <summary>
        /// The run plan for ONE package, rather than for whichever package is the startup.
        ///
        /// Launching a package by name skips the "which one is the startup?" question — the caller
        /// pointed at it — but it must NOT skip the blockers: package compliance and the
        /// PY8001/PY8002/PY8003/PY8004 solution policy are properties of the whole solution, and
        /// code the IDE refuses to run from the toolbar must not become runnable just because it
        /// was reached from a different menu, or from a command line.
        /// </summary>
        public static SolutionRunPlan ComputeRunPlanFor(
            SolutionModel sol, ProjectModel project, SolutionRunOptions options)
        {
            if (options == null) options = new SolutionRunOptions();

            SolutionRunPlan blocked = ComputeBlockers(sol, options);
            if (blocked != null) return blocked;

            var r = new SolutionRunPlan();
            if (project == null) { r.BlockReason = "No package selected."; return r; }

            string entryFile;
            if (!TryFindEntryFile(project, out entryFile) || string.IsNullOrEmpty(entryFile))
            {
                r.BlockReason = "'" + project.Name + "' has no runnable entry point, so it is a "
                    + "library rather than an application.";
                return r;
            }

            r.CanRun = true;
            r.StartupName = project.Name;
            r.EntryFile = entryFile;
            // Non-null when the entry lives in a package, and then it is what Run launches — a
            // per-package launch must resolve the target exactly the way the startup launch does.
            r.RunModule = ResolveRunModule(sol, entryFile);
            // Read from the manifest, not the cached model — see ResolveEffectiveRunArguments.
            r.RunArguments = ResolveEffectiveRunArguments(project);
            r.WorkingDirectory = ResolveWorkingDirectoryForScript(sol, entryFile);
            return r;
        }

        /// <summary>
        /// The interpreter argv for a launch, shared by every Run path so they cannot drift on HOW
        /// the program is started.
        ///
        /// An entry module inside a package is launched as <c>python -m &lt;pkg&gt;.&lt;mod&gt;</c>,
        /// never by file path: only <c>-m</c> sets <c>__package__</c>, and a path launch makes the
        /// entry module's own <c>from .x import y</c> fail with "attempted relative import with no
        /// known parent package" before a line of the program runs. A top-level module that is in no
        /// package has no relative imports to preserve and is still launched by path.
        ///
        /// <c>-u</c> forces stdout+stderr unbuffered so the output appears in the order it happens,
        /// not in the order the OS drains each stream's buffer. Anything after the target is the
        /// package's own argv, from <c>[tool.helixstudio].run-args</c>.
        /// </summary>
        public static List<string> BuildPythonLaunchArgs(SolutionRunPlan plan)
        {
            var args = new List<string> { "-u" };
            if (!string.IsNullOrEmpty(plan.RunModule)) { args.Add("-m"); args.Add(plan.RunModule); }
            else args.Add(plan.EntryFile);
            args.AddRange(HelixPythonNamespace.Core.CommandLineArguments.Split(plan.RunArguments));
            return args;
        }

        // ---- startup resolution -----------------------------------------------------------

        /// <summary>
        /// Resolve which project Run should invoke:
        /// 1. a package explicitly marked startup in its pyproject.toml (PSM-10);
        /// 2. else a sole runnable package (auto-pick, PSM-08);
        /// 3. else null (caller prompts the user).
        /// </summary>
        public static string ResolveStartupProject(SolutionModel sol)
        {
            if (sol == null) return null;

            // 1. Authoritative: a package explicitly marked startup in its pyproject.toml (PSM-10).
            //    The TOML is the single source of truth — a stale per-solution user setting must not
            //    resurrect a startup once the TOML no longer designates one.
            if (sol.Projects != null)
                foreach (ProjectModel p in sol.Projects)
                    if (p != null && p.IsStartup) return p.Name;

            // 2. Implicit: a sole runnable package is the startup (PSM-08). With more than one and
            //    none designated, return null so Run blocks and asks the user to choose.
            if (sol.Projects == null) return null;
            string onlyAppish = null;
            int count = 0;
            foreach (ProjectModel p in sol.Projects)
            {
                if (p == null || string.IsNullOrEmpty(p.FilePath)) continue;
                string ignore;
                if (TryFindEntryFile(p, out ignore))
                {
                    onlyAppish = p.Name;
                    count++;
                    if (count > 1) return null;   // ambiguous — don't auto-pick
                }
            }
            return count == 1 ? onlyAppish : null;
        }

        /// <summary>The ProjectModel designated as startup (explicit setting, else sole-app auto-pick).</summary>
        public static ProjectModel ResolveStartupProjectModel(SolutionModel sol)
        {
            string name = ResolveStartupProject(sol);
            if (string.IsNullOrEmpty(name) || sol == null || sol.Projects == null) return null;
            foreach (ProjectModel p in sol.Projects)
                if (p != null && string.Equals(p.Name, name, StringComparison.Ordinal)) return p;
            return null;
        }

        /// <summary>
        /// The startup package's entry-point FILE path — what the debugger launches (it needs a
        /// concrete script, not a module name). Null when no startup package resolves.
        /// </summary>
        public static string ResolveStartupEntryFile(SolutionModel sol)
        {
            ProjectModel p = ResolveStartupProjectModel(sol);
            string entry;
            return (p != null && TryFindEntryFile(p, out entry)) ? entry : null;
        }

        /// <summary>
        /// The startup package's importable module path — what Run launches via
        /// <c>python -m &lt;module&gt;</c>. See <see cref="ResolveRunModule"/>.
        /// </summary>
        public static string ResolveStartupModule(SolutionModel sol)
        {
            return ResolveRunModule(sol, ResolveStartupEntryFile(sol));
        }

        /// <summary>
        /// Resolve a package's runnable entry-point FILE. Dispatches on <see cref="ProjectKind"/>:
        /// native (CUDA/C++, CMake-driven) packages resolve via <c>TryFindNativeEntryFile</c>
        /// (a real <c>int main()</c> source file from the CMake target graph); everything else uses
        /// the language's own resolver — for Python the priority order <c>__main__.py</c>,
        /// <c>main.py</c>, <c>&lt;pkg&gt;_main.py</c>, then any <c>*_main.py</c>. Returns false for
        /// libraries (no entry file), which are therefore ineligible as a startup package. This is
        /// the single dispatch point shared by Run, Debug, Command Prompt and the headless launcher
        /// so "what counts as a startable package" never re-diverges per language the way it did
        /// before (Command Prompt used to hardcode Python-only rules).
        /// </summary>
        public static bool TryFindEntryFile(ProjectModel p, out string entryPath)
        {
            if (p != null && ProjectKinds.IsNative(p.Kind))
                return TryFindNativeEntryFile(p, out entryPath);
            // Everything else asks the LANGUAGE, not Python. Assuming Python here is what told the
            // author of a brand-new native console app to add a main.py.
            return HelixPythonNamespace.Workspace.LanguageServices.EntryPointResolver.TryFindEntryFile(p, out entryPath);
        }

        /// <summary>
        /// Resolve a native (CUDA/C++) package's entry file from its real CMake target graph —
        /// the first executable target's <c>main.*</c> source file, falling back to that target's
        /// first source file. Mirrors the CMake Run path's own resolution so the preflight
        /// recognizes exactly the projects that path does.
        /// </summary>
        private static bool TryFindNativeEntryFile(ProjectModel p, out string entryPath)
        {
            entryPath = null;
            if (p == null || string.IsNullOrEmpty(p.ProjectDirectory) ||
                !Directory.Exists(p.ProjectDirectory)) return false;
            try
            {
                var imported = new HelixPythonNamespace.Workspace.Cuda.CMakeProjectImporter().Import(p.ProjectDirectory);
                if (imported == null || !imported.Success || imported.Project == null) return false;
                foreach (HelixPythonNamespace.Workspace.Cuda.CMakeResolvedTarget target in imported.Project.Targets)
                {
                    if (target.Kind != HelixPythonNamespace.Workspace.Cuda.CMakeTargetKind.Executable) continue;
                    if (target.SourceFiles == null || target.SourceFiles.Count == 0) continue;
                    foreach (string src in target.SourceFiles)
                        if (string.Equals(Path.GetFileNameWithoutExtension(src), "main", StringComparison.OrdinalIgnoreCase))
                        { entryPath = src; return true; }
                    entryPath = target.SourceFiles[0];
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// The dotted module name <paramref name="entryFile"/> must be launched under, or null when
        /// it should be launched as a plain script.
        ///
        /// An entry module that lives INSIDE a package has to be started as
        /// <c>python -m &lt;pkg&gt;.&lt;mod&gt;</c>. Only that sets <c>__package__</c>; started by
        /// path, the very first <c>from .experiments.cli import Cli</c> dies with
        /// <c>ImportError: attempted relative import with no known parent package</c> — reported
        /// against <c>BugMap_oop</c>'s <c>rouse_model_verification/main.py</c>, which the
        /// IDE launched as <c>python -u &lt;path&gt;\main.py</c>. The parent directory that makes the
        /// package importable is always on the launch PYTHONPATH
        /// (<see cref="BuildPythonPathEnv"/> → <c>PythonSearchPath.ImportRootsForProjects</c>, which
        /// adds every project's parent), so <c>-m</c> resolves from any working directory.
        ///
        /// The name comes from <c>PythonSearchPath.ModuleFqnOf</c> — the same authority the virtual
        /// compiler resolves imports with, which walks the real <c>__init__.py</c> chain — rather
        /// than from string-concatenating the project name, which was wrong for every layout whose
        /// folder name and package name differ (src-layouts, nested packages).
        ///
        /// Returns null for an entry that is NOT in a package: it has no relative imports to keep
        /// working, and launching it by path is what every other Python tool does.
        /// <c>&lt;pkg&gt;/__main__.py</c> collapses to <c>-m &lt;pkg&gt;</c>, the canonical spelling.
        /// </summary>
        public static string ResolveRunModule(SolutionModel sol, string entryFile)
        {
            if (string.IsNullOrEmpty(entryFile)) return null;
            if (!string.Equals(Path.GetExtension(entryFile), ".py", StringComparison.OrdinalIgnoreCase))
                return null;   // a native entry point is not launched by the Python launcher

            string fqn;
            try { fqn = BuildPythonSearchPath(sol, entryFile).ModuleFqnOf(entryFile); }
            catch { return null; }
            if (string.IsNullOrEmpty(fqn) || fqn.IndexOf('.') < 0) return null;

            const string dunderMain = ".__main__";
            return fqn.EndsWith(dunderMain, StringComparison.Ordinal)
                ? fqn.Substring(0, fqn.Length - dunderMain.Length)
                : fqn;
        }

        /// <summary>
        /// The directory a launch of <paramref name="filePath"/> should run in: the folder of the
        /// PROJECT that owns the file, which is Visual Studio's default working directory.
        ///
        /// Every launch route used to answer this differently — Run and Run in Terminal used the
        /// SOLUTION root (so a relative argument meant something else entirely), while Debug and
        /// the Profiler used the launched file's own folder (which is not the project folder when
        /// the entry point is nested, e.g. <c>&lt;project&gt;/pkg/__main__.py</c>). Relative paths
        /// in run arguments therefore resolved against three different roots depending on how you
        /// started the program. This is the single answer for all of them.
        ///
        /// The DEEPEST containing project wins, so a workspace member beats the workspace-root
        /// project that nominally contains it. Falls back to the file's own directory when no
        /// project claims it.
        /// </summary>
        public static string ResolveWorkingDirectoryForScript(SolutionModel sol, string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string full;
            try { full = Path.GetFullPath(filePath); }
            catch { return null; }

            string best = null;
            if (sol != null && sol.Projects != null)
            {
                foreach (ProjectModel p in sol.Projects)
                {
                    if (p == null || string.IsNullOrEmpty(p.ProjectDirectory)) continue;
                    string dir;
                    try { dir = Path.GetFullPath(p.ProjectDirectory); }
                    catch { continue; }
                    string prefix = dir.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || dir.Length > best.Length) best = dir;
                }
            }

            if (best != null) return best;
            try { return Path.GetDirectoryName(full); }
            catch { return null; }
        }

        /// <summary>
        /// The package's run arguments as they stand ON DISK right now, refreshing the in-memory
        /// model from the manifest.
        ///
        /// The manifest is the source of truth, and the user can change it two ways the loaded
        /// <see cref="SolutionModel"/> never hears about: editing <c>pyproject.toml</c> in the
        /// IDE's own editor, or editing it outside the IDE. Reading the cached value at launch
        /// meant a <c>run-args</c> line added after the solution was opened was simply ignored, and
        /// Run kept launching with no arguments while the file plainly said otherwise. One small
        /// file read per launch removes that entire class of staleness. Falls back to the cached
        /// value only when the manifest cannot be read.
        /// </summary>
        public static string ResolveEffectiveRunArguments(ProjectModel project)
        {
            if (project == null) return null;
            string toml = ResolvePyprojectPath(project);
            if (!string.IsNullOrEmpty(toml) && File.Exists(toml))
            {
                try
                {
                    var meta = HelixPythonNamespace.Workspace.Python.PyProjectReader.Read(toml);
                    string onDisk = null;
                    if (meta != null && meta.ToolHelixStudio != null)
                        meta.ToolHelixStudio.TryGetValue("run-args", out onDisk);
                    // A key deleted on disk must clear the cached value too, not linger.
                    project.RunArguments = string.IsNullOrWhiteSpace(onDisk) ? null : onDisk;
                    return project.RunArguments;
                }
                catch { /* unreadable/malformed manifest — fall back to what we loaded */ }
            }
            return project.RunArguments;
        }

        // The package's pyproject.toml path: the project anchor when it already is one, else the
        // conventional pyproject.toml in the project directory.
        private static string ResolvePyprojectPath(ProjectModel p)
        {
            if (p == null) return null;
            string fp = p.FilePath;
            if (!string.IsNullOrEmpty(fp) &&
                fp.EndsWith("pyproject.toml", StringComparison.OrdinalIgnoreCase)) return fp;
            string dir = p.ProjectDirectory;
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "pyproject.toml");
        }

        // ---- sys.path model ---------------------------------------------------------------

        /// <summary>
        /// The import roots this solution's projects contribute, in <c>sys.path</c> order. Single
        /// source of truth for both the launcher's <c>PYTHONPATH</c> (<see cref="BuildPythonPathEnv"/>)
        /// and the virtual compiler's compile-time import resolution (<see cref="BuildPythonSearchPath"/>),
        /// so the two can never disagree about where a module comes from.
        /// </summary>
        public static List<string> CollectProjectDirectories(SolutionModel sol)
        {
            var dirs = new List<string>();
            if (sol == null || sol.Projects == null) return dirs;
            foreach (ProjectModel p in sol.Projects)
            {
                if (p == null || string.IsNullOrEmpty(p.FilePath)) continue;
                string projectDir = Path.GetDirectoryName(p.FilePath);
                if (!string.IsNullOrEmpty(projectDir)) dirs.Add(projectDir);
            }
            return dirs;
        }

        /// <summary>
        /// The declared kind of each project directory, so solution policy rule 3 (PY8003) can
        /// tell a test project — the one kind allowed to contain test files — from every other.
        /// </summary>
        public static List<KeyValuePair<string, ProjectKind>> CollectProjectKinds(SolutionModel sol)
        {
            var kinds = new List<KeyValuePair<string, ProjectKind>>();
            if (sol == null || sol.Projects == null) return kinds;
            foreach (ProjectModel p in sol.Projects)
            {
                if (p == null || string.IsNullOrEmpty(p.FilePath)) continue;
                string projectDir = Path.GetDirectoryName(p.FilePath);
                if (string.IsNullOrEmpty(projectDir)) continue;
                kinds.Add(new KeyValuePair<string, ProjectKind>(projectDir, p.Kind));
            }
            return kinds;
        }

        /// <summary>
        /// The <c>sys.path</c> model for this solution — what the virtual compiler resolves
        /// imports against, and what <see cref="BuildPythonPathEnv"/> renders into the environment.
        /// </summary>
        public static HelixPythonNamespace.VirtualCompiler.Imports.PythonSearchPath BuildPythonSearchPath(
            SolutionModel sol, string entryFile)
        {
            return HelixPythonNamespace.VirtualCompiler.Imports.PythonSearchPath.ForRun(
                entryFile, CollectProjectDirectories(sol), SolutionRootDirectory(sol));
        }

        /// <summary>The directory holding the solution's own manifest, or null.</summary>
        public static string SolutionRootDirectory(SolutionModel sol)
        {
            if (sol == null || string.IsNullOrEmpty(sol.FilePath)) return null;
            try { return Path.GetDirectoryName(Path.GetFullPath(sol.FilePath)); }
            catch { return null; }
        }

        /// <summary>
        /// Build a PYTHONPATH env-var value that makes every workspace member importable by its
        /// bare package name.
        ///
        /// Each project contributes its OWN directory, and additionally its parent only when the
        /// project directory is itself a regular package. Adding the parent unconditionally — which
        /// this method used to do, and nothing else — is what broke the <c>sample_workspace</c> layout:
        /// with only <c>projects/</c> on the path, <c>import sample_common</c> found the outer
        /// <c>projects/sample_common/</c> folder, which has no <c>__init__.py</c>, and bound it as an
        /// empty PEP 420 namespace package instead of the real
        /// <c>projects/sample_common/sample_common/</c> package one level down — producing
        /// <c>cannot import name 'TsvPreparationWorkflow' from 'sample_common' (unknown location)</c>
        /// at runtime. See <see cref="HelixPythonNamespace.VirtualCompiler.Imports.PythonSearchPath"/>.
        /// </summary>
        public static IReadOnlyDictionary<string, string> BuildPythonPathEnv(SolutionModel sol)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (sol == null || sol.Projects == null) return result;

            var roots = HelixPythonNamespace.VirtualCompiler.Imports.PythonSearchPath.ImportRootsForProjects(
                CollectProjectDirectories(sol), SolutionRootDirectory(sol));
            if (roots.Count == 0) return result;

            string existing = Environment.GetEnvironmentVariable("PYTHONPATH") ?? string.Empty;
            result["PYTHONPATH"] = HelixPythonNamespace.VirtualCompiler.Imports.PythonSearchPath
                .FromRoots(roots).ToEnvironmentValue(existing);
            return result;
        }

        // ---- solution policy (PY8001 / PY8002 / PY8003) ------------------------------------

        /// <summary>
        /// The solution's policy object: member directories plus the solution root.
        /// </summary>
        public static HelixPythonNamespace.VirtualCompiler.Compliance.SolutionPolicy BuildSolutionPolicy(
            SolutionModel sol, SolutionRunOptions options)
        {
            var policy = new HelixPythonNamespace.VirtualCompiler.Compliance.SolutionPolicy(
                SolutionRootDirectory(sol), CollectProjectDirectories(sol), CollectProjectKinds(sol));
            // Tools ▸ Preferences ▸ General can relax rule 2 (PY8002) for solutions of existing
            // procedural code. Rules 1 and 3 are not user-configurable: the IDE never executes
            // source it cannot show, compile-check or breakpoint, and never builds a package whose
            // own tests are welded into it.
            if (options != null) policy.EnforceObjectOrientation = options.EnforceObjectOrientation;
            return policy;
        }

        /// <summary>
        /// Every PY8001/PY8002/PY8003 violation in the solution.
        ///
        /// The sweep walks the SOLUTION DIRECTORY, not the member projects: rule 1 is precisely
        /// about files that belong to no member, so enumerating members would be blind to exactly
        /// the case it exists to catch. Third-party and generated trees are skipped by
        /// <c>SolutionPolicy.IsUserWritten</c>.
        ///
        /// Two passes, because the rules have different reach. The Python virtual compiler covers
        /// <c>.py</c> files and all three rules. Rule 3 also binds the non-Python kinds (a CUDA
        /// library must not carry <c>test_*.cu</c> either), and those files never reach a Python
        /// compiler — so <see cref="FindNativeTestFilePlacementViolations"/> applies the same
        /// shared rule object to them directly.
        /// </summary>
        public static List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> FindPolicyViolations(
            SolutionModel sol, SolutionRunOptions options)
        {
            var found = new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>();
            string root = SolutionRootDirectory(sol);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return found;

            var policy = BuildSolutionPolicy(sol, options);
            var compileOptions = new HelixPythonNamespace.VirtualCompiler.CompileOptions
            {
                // Policy only: the other passes are irrelevant to whether a launch is legal, and
                // type-checking the whole tree would make pressing Run feel broken.
                EnableAstValidator = false,
                EnableSymtablePass = false,
                EnableCompileValidator = false,
                EnableTypeChecker = false,
                EnableImportResolution = false,
                EnableCompliancePass = true,
                Policy = policy,
            };

            foreach (string file in EnumerateSolutionSourceFiles(root, policy))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }
                try
                {
                    var result = HelixPythonNamespace.VirtualCompiler.VirtualCompiler.CompileSource(text, file, compileOptions);
                    foreach (var d in result.Diagnostics)
                        if (d.Code == HelixPythonNamespace.VirtualCompiler.VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace
                            || d.Code == HelixPythonNamespace.VirtualCompiler.VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented
                            || d.Code == HelixPythonNamespace.VirtualCompiler.VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject)
                            found.Add(d);
                }
                catch { /* an unparseable file is a syntax problem, not a policy one */ }
            }

            found.AddRange(FindNativeTestFilePlacementViolations(sol, options));
            return found;
        }

        /// <summary>
        /// Rule 3 (PY8003) for the non-Python languages. A CUDA/C++ project is barred
        /// from containing test files exactly as a Python one is, but its sources never pass
        /// through the Python virtual compiler, so the shared rule is applied to them here.
        ///
        /// Walks each project's OWN files only — a file inside a nested member belongs to that
        /// member, and blaming the ancestor would report the violation against the wrong project
        /// (the same nesting mistake the tree's "[Unit Test]" heuristic had to fix).
        /// </summary>
        private static List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> FindNativeTestFilePlacementViolations(
            SolutionModel sol, SolutionRunOptions options)
        {
            var found = new List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic>();
            if (sol == null || sol.Projects == null) return found;

            var policy = BuildSolutionPolicy(sol, options);
            foreach (ProjectModel p in sol.Projects)
            {
                if (p == null || string.IsNullOrEmpty(p.FilePath)) continue;
                if (p.Language == ProjectLanguage.Python) continue;  // covered above
                if (ProjectKinds.IsTest(p.Kind)) continue;           // the one exempt kind

                string projectDir = Path.GetDirectoryName(p.FilePath);
                if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) continue;

                var descriptor = HelixPythonNamespace.Core.Languages.LanguageDescriptors.For(p.Language);
                if (descriptor.TestFileExtensions == null) continue;

                foreach (string ext in descriptor.TestFileExtensions)
                {
                    string[] files;
                    try { files = Directory.GetFiles(projectDir, "*" + ext, SearchOption.AllDirectories); }
                    catch { continue; }

                    foreach (string f in files)
                    {
                        if (!policy.IsUserWritten(f)) continue;
                        // The deepest member owning the file — skip anything a nested project owns.
                        // Path identity, so the comparison follows the filesystem's case rules.
                        string owner = policy.FindOwningProjectDirectory(f);
                        if (!HelixPythonNamespace.Core.IO.PathComparison.AreSame(owner, projectDir)) continue;
                        if (!HelixPythonNamespace.Core.Policy.TestFilePlacementRule.IsTestFile(f, projectDir, p.Language))
                            continue;

                        found.Add(new HelixPythonNamespace.VirtualCompiler.CompileDiagnostic(
                            f,
                            HelixPythonNamespace.VirtualCompiler.VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject,
                            HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error,
                            default(HelixPythonNamespace.Python.Text.SourceSpan),
                            HelixPythonNamespace.Core.Policy.TestFilePlacementRule.BuildMessage(f, p.Name, p.Kind)));
                    }
                }
            }
            return found;
        }

        // Every .py under the solution root that the user is responsible for.
        private static IEnumerable<string> EnumerateSolutionSourceFiles(
            string root, HelixPythonNamespace.VirtualCompiler.Compliance.SolutionPolicy policy)
        {
            string[] files;
            try { files = Directory.GetFiles(root, "*.py", SearchOption.AllDirectories); }
            catch { yield break; }
            foreach (string f in files)
                if (policy.IsUserWritten(f)) yield return f;
        }

        /// <summary>A short, actionable summary of what is blocking the launch.</summary>
        public static string DescribePolicyViolations(
            IList<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> violations, PolicyMessageStyle style)
        {
            if (style == null) style = new PolicyMessageStyle();
            int outside = 0, procedural = 0, misplacedTests = 0;
            foreach (var v in violations)
            {
                if (v.Code == HelixPythonNamespace.VirtualCompiler.VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace) outside++;
                else if (v.Code == HelixPythonNamespace.VirtualCompiler.VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject) misplacedTests++;
                else procedural++;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("Run and Debug are blocked by solution policy.");
            if (outside > 0)
            {
                sb.Append(Environment.NewLine + Environment.NewLine)
                  .Append(style.Bullet).Append(outside)
                  .Append(outside == 1 ? " file is" : " files are")
                  .Append(" not a member of any project (PY8001). The IDE will not execute code it cannot show, compile-check or debug — add the directory to [tool.uv.workspace].members");
                // A console has no context menu, so naming one there would be an instruction the
                // user cannot follow.
                if (!string.IsNullOrEmpty(style.QuickFixCaption))
                    sb.Append(", or right-click the PY8001 row in the Problems panel and choose \"")
                      .Append(style.QuickFixCaption).Append("\" to move the file into a project");
                sb.Append('.');
            }
            if (procedural > 0)
                sb.Append(Environment.NewLine + Environment.NewLine)
                  .Append(style.Bullet).Append(procedural)
                  .Append(procedural == 1 ? " module-level function is" : " module-level functions are")
                  .Append(" not object-oriented (PY8002). Move them into classes; only 'def main()' may stay at module level.");
            if (misplacedTests > 0)
                sb.Append(Environment.NewLine + Environment.NewLine)
                  .Append(style.Bullet).Append(misplacedTests)
                  .Append(misplacedTests == 1 ? " test file is" : " test files are")
                  .Append(" inside a project that is not a test project (PY8003). Class library, executable app and executable GUI app packages must not contain unit tests — move them to a test project (kind = \"test\"), conventionally the sibling '<project>_tests'.");
            if (!string.IsNullOrEmpty(style.FullListHint))
                sb.Append(Environment.NewLine + Environment.NewLine).Append(style.FullListHint);
            return sb.ToString();
        }
    }
}
