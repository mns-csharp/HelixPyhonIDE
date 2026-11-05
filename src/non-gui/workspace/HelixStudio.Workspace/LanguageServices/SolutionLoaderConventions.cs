using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Workspace.LanguageServices
{
    /// <summary>
    /// Directory-walking conventions shared by the per-language solution loaders
    /// (<c>CargoSolutionLoader</c>, <c>CsprojSolutionLoader</c>, <c>JavaSolutionLoader</c>,
    /// <c>JuliaSolutionLoader</c>): which folders are never source (VCS/IDE metadata) and how
    /// to enumerate a language's source files while skipping that language's build-output
    /// directories. Kept deliberately name-based and I/O-tolerant — a locked or malformed
    /// directory hides its files rather than failing the whole load, matching the tree's own
    /// best-effort file sniffing.
    /// </summary>
    public static class SolutionLoaderConventions
    {
        // Folders that are metadata for EVERY language. Build outputs (target/, bin/, obj/,
        // build/) are per-language decisions passed as extras — "bin" is dead output for C#
        // but real source in some layouts (a src/bin directory of entry points).
        private static readonly string[] AlwaysExcluded = { ".git", ".vs", ".idea", ".svn", "node_modules", "__pycache__" };

        /// <summary>True when <paramref name="directoryPath"/>'s own name marks it as excluded.</summary>
        public static bool IsExcludedDirectory(string directoryPath, params string[] extraExcludedNames)
        {
            string name = Path.GetFileName(
                directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string excluded in AlwaysExcluded)
                if (string.Equals(name, excluded, StringComparison.OrdinalIgnoreCase)) return true;
            if (extraExcludedNames != null)
                foreach (string excluded in extraExcludedNames)
                    if (string.Equals(name, excluded, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// With no startup marker in the language's own manifest format, a solution with exactly
        /// one runnable project gets it as the implicit startup — the unambiguous case;
        /// multi-executable workspaces leave the choice to the user (persisted per-solution in
        /// user settings). Callers still run <c>SolutionService.NormalizeStartup</c> afterwards.
        /// </summary>
        public static void MarkSoleExecutableAsStartup(Core.Model.SolutionModel solution)
        {
            if (solution == null) return;
            Core.Model.ProjectModel sole = null;
            foreach (Core.Model.ProjectModel p in solution.Projects)
            {
                if (!p.IsExecutable) continue;
                if (sole != null) return;
                sole = p;
            }
            if (sole != null) sole.IsStartup = true;
        }

        /// <summary>
        /// Every file under <paramref name="rootDir"/> matching <paramref name="searchPattern"/>,
        /// recursing but skipping excluded directories (unlike
        /// <see cref="SearchOption.AllDirectories"/>, which cannot).
        /// </summary>
        public static IEnumerable<string> EnumerateSources(string rootDir, string searchPattern, params string[] extraExcludedDirNames)
        {
            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir)) yield break;
            var pending = new Stack<string>();
            pending.Push(rootDir);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();

                string[] files;
                try { files = Directory.GetFiles(dir, searchPattern); }
                catch { files = new string[0]; }
                foreach (string f in files) yield return f;

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (string sub in subDirs)
                    if (!IsExcludedDirectory(sub, extraExcludedDirNames)) pending.Push(sub);
            }
        }
    }
}
