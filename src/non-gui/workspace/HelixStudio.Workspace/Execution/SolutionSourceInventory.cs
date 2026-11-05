namespace HelixPythonNamespace.Workspace.Execution
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using HelixPythonNamespace.Core.Model;

    /// <summary>
    /// Which files an engine is asked to look at, for the whole solution or for one package.
    ///
    /// Every analysis surface — Build ▸ Compile, Analysis ▸ Lint, Analysis ▸ Code Review, and the
    /// headless <c>--compile</c>/<c>--lint</c>/<c>--code-review</c> verbs — has to agree on this,
    /// because "the review is clean" is a claim about a set of files and two surfaces answering it
    /// differently means one of them is quietly reviewing less than it says. Lifted out of
    /// <c>MainForm</c>/<c>BuildRunCoordinator</c> unchanged when the launcher was given the same
    /// verbs.
    ///
    /// The walk is of the FILESYSTEM, not of <c>TypeModel.SourceFilePath</c>: the earlier
    /// implementation only saw files some type was declared in, which silently skipped every module
    /// made entirely of functions — app entry points among them — so those were never compiled.
    /// </summary>
    public static class SolutionSourceInventory
    {
        /// <summary>
        /// Directories that hold no user-written source. Kept as one list so a tree skipped by
        /// Compile is also skipped by Lint and by the review.
        /// </summary>
        public static HashSet<string> NewSkipDirectorySet()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "__pycache__", ".venv", "venv", "env", ".env",
                "build", "dist", ".tox", ".pytest_cache", ".mypy_cache",
            };
        }

        /// <summary>
        /// Every <c>.py</c> file under the solution's member projects, de-duplicated by full path.
        /// <paramref name="scope"/> narrows the walk to one member; null means the whole solution.
        /// </summary>
        public static List<string> PythonFiles(SolutionModel sol, ProjectModel scope)
        {
            var result = new List<string>();
            if (sol == null || sol.Projects == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skipDirs = NewSkipDirectorySet();

            foreach (ProjectModel p in sol.Projects)
            {
                if (p == null || string.IsNullOrEmpty(p.FilePath)) continue;
                if (scope != null && !ReferenceEquals(p, scope)) continue;
                string projectDir = Path.GetDirectoryName(p.FilePath);
                if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)) continue;

                CollectPyFilesRecursive(projectDir, skipDirs, seen, result);
            }
            return result;
        }

        /// <summary>
        /// The same files paired with the dotted module FQN each one is importable as (e.g.
        /// <c>lib_utils/geometry.py</c> under project <c>lib_utils</c> → <c>lib_utils.geometry</c>).
        /// <see cref="HelixPythonNamespace.VirtualCompiler.VirtualCompiler.CompileSolution"/> needs the FQN so
        /// <c>from mod import foo</c> resolves to a real callable signature for cross-module type
        /// checking rather than to nothing.
        /// </summary>
        public static List<HelixPythonNamespace.VirtualCompiler.WorkspaceFile> WorkspaceFiles(
            SolutionModel sol, ProjectModel scope)
        {
            var result = new List<HelixPythonNamespace.VirtualCompiler.WorkspaceFile>();
            foreach (string file in PythonFiles(sol, scope))
                result.Add(new HelixPythonNamespace.VirtualCompiler.WorkspaceFile(
                    file, HelixPythonNamespace.VirtualCompiler.PythonModuleFqn.Derive(file)));
            return result;
        }

        public static void CollectPyFilesRecursive(
            string dir, HashSet<string> skipDirs, HashSet<string> seen, List<string> result)
        {
            string dirName = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(dirName))
            {
                if (skipDirs.Contains(dirName)) return;
                if (dirName.EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase)) return;
            }

            string[] files;
            try { files = Directory.GetFiles(dir, "*.py"); }
            catch { return; }
            foreach (var f in files)
            {
                string full;
                try { full = Path.GetFullPath(f); }
                catch { continue; }
                if (seen.Add(full)) result.Add(full);
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (var sd in subdirs)
                CollectPyFilesRecursive(sd, skipDirs, seen, result);
        }
    }
}
