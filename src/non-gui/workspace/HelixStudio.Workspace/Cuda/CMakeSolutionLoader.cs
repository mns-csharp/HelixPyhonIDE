using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// Maps a <see cref="CMakeProjectDescriptor"/> (a scan-directory-based native folder import,
    /// see <see cref="CMakeProjectImporter"/>) onto a real <see cref="SolutionModel"/> — one
    /// <see cref="ProjectModel"/> PER OWNING PACKAGE DIRECTORY (not per raw CMake target — see
    /// <see cref="PackageOwnerDirectory"/>), each populated with real <see cref="TypeModel"/>/
    /// <see cref="FunctionModel"/>/<see cref="MemberModel"/> instances via <see cref="CppTypeExtractor"/>.
    ///
    /// This is what lets a CMake-imported project flow through the EXACT SAME
    /// <c>ProjectTreePanel.LoadSolution(SolutionModel)</c> pipeline a Python solution already uses,
    /// instead of the separate, untagged <c>LoadNativeCMakeProject</c> presentational tree — every
    /// context-menu branch keyed on <c>SolutionModel</c>/<c>ProjectModel</c>/<c>TypeModel</c>/
    /// <c>FunctionModel</c>/<c>MemberModel</c> (Rename, Rename Symbol, Delete, Save, Hide from
    /// Canvas, Build Project, ...) then applies to C++ nodes for free, the same way it already does
    /// for Python nodes, because it dispatches on the TAG'S RUNTIME TYPE, not on the project's
    /// language.
    ///
    /// One Python PACKAGE directory can compile into SEVERAL CMake link units for reasons that
    /// have no Python equivalent — a "_lib" STATIC library the sibling executable links against,
    /// or a second executable for a file with its own <c>main()</c> (two mains cannot share one
    /// link unit, but two functions can share one Python module). Modelling one ProjectModel PER
    /// TARGET made a C++ solution show visibly more "packages" than its Python ancestor for a
    /// reason with no conceptual meaning to a user comparing the two trees (confirmed against this
    /// fixture: Python has 6 workspace packages + 2 standalone root scripts = 8, but the naive
    /// per-target C++ tree showed 12, because app_cli split into app_cli_lib/app_cli/
    /// app_cli_debug_demo and app_server split into app_server_lib/app_server). Grouping every
    /// target that shares a genuine "&lt;pkg&gt;/src/*.cpp" package folder (see
    /// <see cref="PackageOwnerDirectory"/>) back into ONE ProjectModel restores the 1:1 count.
    /// Loose targets with no such folder (the fixture's root-level debug_target.cpp/
    /// exception_target.cpp, matching Python's own root-level standalone scripts, and the
    /// synthetic "Unassigned Sources" bucket, which spans every package and has no single owner)
    /// stay their own singleton ProjectModel exactly as before.
    /// </summary>
    public static class CMakeSolutionLoader
    {
        public static SolutionModel BuildSolution(CMakeProjectDescriptor descriptor)
        {
            if (descriptor == null) return null;
            string cmakePath = Path.Combine(descriptor.FolderPath, "CMakeLists.txt");
            SolutionModel solution = new SolutionModel(cmakePath, descriptor.Name);

            var groups = new List<TargetGroup>();
            var groupByPackageDir = new Dictionary<string, TargetGroup>(System.StringComparer.OrdinalIgnoreCase);

            foreach (CMakeResolvedTarget target in descriptor.Targets)
            {
                // The synthetic "Unassigned Sources" bucket spans every package directory at
                // once — it can never be merged into a real package's group, so it always gets
                // its own singleton, regardless of what stray files happen to be in it.
                if (target.Name == CMakeConventions.UnassignedSourcesTargetName)
                {
                    var solo = new TargetGroup(descriptor.FolderPath);
                    solo.Targets.Add(target);
                    groups.Add(solo);
                    continue;
                }

                string packageDir = PackageOwnerDirectory(target);
                if (packageDir == null)
                {
                    // No "<pkg>/src/*.cpp" convention matched — this target doesn't share a
                    // package folder with anything else on purpose (see class doc), so it gets
                    // its own singleton group using its resolved directory as-is.
                    var solo = new TargetGroup(DeriveTargetDirectory(descriptor.FolderPath, target));
                    solo.Targets.Add(target);
                    groups.Add(solo);
                    continue;
                }

                TargetGroup group;
                if (!groupByPackageDir.TryGetValue(packageDir, out group))
                {
                    group = new TargetGroup(packageDir);
                    groupByPackageDir[packageDir] = group;
                    groups.Add(group);
                }
                group.Targets.Add(target);
            }

            foreach (TargetGroup group in groups)
                solution.Projects.Add(BuildProject(solution, group));

            ApplyStartupMarker(solution, descriptor);
            return solution;
        }

        // One Solution Explorer node's worth of CMake targets — either a genuine multi-target
        // package group, or a singleton for a target that doesn't share a package folder.
        private sealed class TargetGroup
        {
            public TargetGroup(string directory)
            {
                Directory = directory;
                Targets = new List<CMakeResolvedTarget>();
            }

            public string Directory;
            public List<CMakeResolvedTarget> Targets;
        }

        private static ProjectModel BuildProject(SolutionModel solution, TargetGroup group)
        {
            // Synthetic identity path: doesn't need to exist on disk. ProjectModel only uses
            // FilePath for identity/equality and to derive ProjectDirectory (Path.GetDirectoryName),
            // which must resolve to the GROUP'S OWN directory (e.g. <root>/src/app_gui), not the
            // CMake root — a target declared via add_subdirectory lives in its own folder with its
            // own CMakeLists.txt, and CppTypeService writes new Add-Class scaffolding under
            // ProjectDirectory/include and ProjectDirectory/src.
            string projectName = ChoosePrimaryTargetName(group);
            string identityPath = Path.Combine(group.Directory, SafeFileName(projectName) + ".cmake-target");
            bool anyExecutable = group.Targets.Exists(t => t.Kind == CMakeTargetKind.Executable);
            // Per-PROJECT CUDA-ness (persisted in each project's own CMakeLists: its
            // `project(... LANGUAGES ...)` clause, with .cu/.cuh sources as the fallback for
            // files without a language clause) — NOT the workspace-wide kind. Without this,
            // a pure-C++ library inside a CUDA solution was labeled "CUDA Library".
            bool usesCuda = group.Targets.Exists(t => t.DeclaresCuda ||
                t.SourceFiles.Exists(f =>
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext == ".cu" || ext == ".cuh";
                }));

            ProjectModel project = new ProjectModel(identityPath, projectName, solution)
            {
                Kind = anyExecutable ? ProjectKind.CudaExecutable : ProjectKind.CudaLibrary,
                DefaultNamespace = projectName,
                Language = ProjectLanguage.Cpp,
                HasCudaSources = usesCuda,
                TargetFramework = usesCuda ? "CUDA C++" : "C++",
            };

            // CMake target source lists (add_library/add_executable) conventionally list only
            // .cpp compilation units, never headers — but in a "header declares, .cpp defines"
            // layout (this repo's own fixture convention:
            // <target>/include/<target>/*.hpp + <target>/src/*.cpp), the actual class
            // DEFINITIONS most often live in the headers, not the .cpp files, especially for
            // template-heavy or fully-inline C++. Since this parser doesn't follow #include (see
            // CudaVirtualCompiler's Preprocessor — deliberately not a conforming preprocessor,
            // no header-resolution infrastructure exists), the pragmatic fix is directory
            // convention rather than real include-resolution: for every listed .cpp, also scan
            // a sibling include/ tree for headers. The shared HashSet dedups across every target
            // in the group so two targets sharing the same header (e.g. app_cli_lib and
            // app_cli_debug_demo both including lib_core/shapes.hpp indirectly) don't extract twice.
            HashSet<string> extracted = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (CMakeResolvedTarget target in group.Targets)
            {
                foreach (string sourceFile in target.SourceFiles)
                {
                    if (extracted.Add(sourceFile)) CppTypeExtractor.ExtractFromFile(project, sourceFile);
                    foreach (string header in SiblingIncludeHeaders(sourceFile))
                        if (extracted.Add(header)) CppTypeExtractor.ExtractFromFile(project, header);
                }
            }

            return project;
        }

        // Picks which of a group's CMake target names represents it for Build/Run purposes:
        // prefer the target whose name matches the owning folder's own name (the conventional
        // "primary" link result — e.g. folder "app_cli" -> target "app_cli", not "app_cli_lib" or
        // "app_cli_debug_demo"), else the first executable target, else just the first target.
        private static string ChoosePrimaryTargetName(TargetGroup group)
        {
            string folderName = Path.GetFileName(group.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            foreach (CMakeResolvedTarget t in group.Targets)
                if (string.Equals(t.Name, folderName, System.StringComparison.OrdinalIgnoreCase)) return t.Name;
            foreach (CMakeResolvedTarget t in group.Targets)
                if (t.Kind == CMakeTargetKind.Executable) return t.Name;
            return group.Targets.Count > 0 ? group.Targets[0].Name : folderName;
        }

        // A `set(HELIXSTUDIO_STARTUP_TARGET <name>)` at the CMake root (see
        // CMakeConventions.StartupTargetVariable) marks which package auto-opens as startup — the
        // native mirror of a Python package's own `[tool.helixstudio] startup = true`. Without
        // this, CMakeSolutionLoader never ran ANY startup-normalization (SolutionService's
        // NormalizeStartup is Python-load-only), so no C++ solution ever had an implicit or
        // explicit startup package — every executable stayed unbolted, no matter how many there
        // were. Reuses SolutionService's own PSM-08 enforcement (single source of truth for the
        // "only an executable may be startup; at most one" invariant) rather than re-deriving it.
        private static void ApplyStartupMarker(SolutionModel solution, CMakeProjectDescriptor descriptor)
        {
            List<string> vals;
            if (descriptor.Metadata != null
                && descriptor.Metadata.Variables.TryGetValue(CMakeConventions.StartupTargetVariable, out vals)
                && vals.Count > 0)
            {
                string startupName = vals[0];
                foreach (ProjectModel p in solution.Projects)
                {
                    if (string.Equals(p.Name, startupName, System.StringComparison.OrdinalIgnoreCase))
                    {
                        p.IsStartup = true;
                        break;
                    }
                }
            }

            HelixPythonNamespace.Workspace.Services.SolutionService.NormalizeStartup(solution);
        }

        // Returns the package folder a target's sources conventionally belong to — the parent of
        // a "src" directory containing one of the target's own source files (this fixture's own
        // convention: <pkg>/src/*.cpp + <pkg>/include/<pkg>/*.hpp). Returns null when no source
        // matches that convention, so callers can tell "genuinely part of a package folder shared
        // with other targets" apart from "a loose file with no package folder at all" (root-level
        // debug_target.cpp/exception_target.cpp, or the synthetic Unassigned Sources bucket).
        private static string PackageOwnerDirectory(CMakeResolvedTarget target)
        {
            foreach (string sourceFile in target.SourceFiles)
            {
                string srcDir = Path.GetDirectoryName(sourceFile);
                if (string.IsNullOrEmpty(srcDir)) continue;
                if (string.Equals(Path.GetFileName(srcDir), "src", System.StringComparison.OrdinalIgnoreCase))
                {
                    string parent = Path.GetDirectoryName(srcDir);
                    if (!string.IsNullOrEmpty(parent)) return parent;
                }
            }
            return null;
        }

        // Derive SOME directory for a target that doesn't match the package-folder convention:
        // the first resolved source's own directory, else the CMake root (e.g. an interface
        // library with no sources at all).
        private static string DeriveTargetDirectory(string rootFolder, CMakeResolvedTarget target)
        {
            foreach (string sourceFile in target.SourceFiles)
            {
                string srcDir = Path.GetDirectoryName(sourceFile);
                if (!string.IsNullOrEmpty(srcDir)) return srcDir;
            }
            return rootFolder;
        }

        private static IEnumerable<string> SiblingIncludeHeaders(string sourceFile)
        {
            string srcDir = Path.GetDirectoryName(sourceFile);
            if (string.IsNullOrEmpty(srcDir)) yield break;
            string parent = Path.GetDirectoryName(srcDir);
            if (string.IsNullOrEmpty(parent)) yield break;
            string includeDir = Path.Combine(parent, "include");
            if (!Directory.Exists(includeDir)) yield break;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(includeDir, "*.*", SearchOption.AllDirectories); }
            catch { yield break; }

            foreach (string f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                // `.cuh` belongs here for the same reason `.hpp` does: it is where a CUDA target's
                // class definitions live. Omitting it meant a project whose headers are all `.cuh`
                // extracted NO types at all — silently, since an empty sweep looks like a project
                // that simply declares nothing. CMakeProjectImporter.IsHeader already counts
                // `.cuh` as a header, and this same file treats it as a CUDA source above.
                if (ext == ".hpp" || ext == ".h" || ext == ".hxx" || ext == ".hh"
                 || ext == ".cuh") yield return f;
            }
        }

        // A target name is usually already filesystem-safe (CMake target names are plain
        // identifiers), but "Unassigned Sources" (CMakeProjectImporter's synthetic bucket for
        // files no real target claims) has a space — sanitize defensively either way.
        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "target";
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (System.Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0 || chars[i] == ' ')
                    chars[i] = '_';
            return new string(chars);
        }
    }
}
