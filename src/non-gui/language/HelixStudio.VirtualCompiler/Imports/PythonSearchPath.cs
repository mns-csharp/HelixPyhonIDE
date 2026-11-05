using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.VirtualCompiler.Imports
{
    /// <summary>
    /// The virtual compiler's model of the interpreter's <c>sys.path</c> — an ordered list of
    /// import roots. This is the SINGLE authority for "where do modules come from": both the
    /// compile-time <see cref="ModuleResolver"/> and the run-time <c>PYTHONPATH</c> the IDE hands
    /// to <c>python.exe</c> are built from it, so a solution that compiles clean cannot fail at
    /// runtime with a resolution error the compiler had no way to see.
    ///
    /// Why that matters: before this existed, compile-time import checking used heuristics
    /// (walk the <c>__init__.py</c> chain, then match dotted names against file-path suffixes)
    /// that were decoupled from the path the launcher actually set. A project laid out as
    /// <c>projects/sample_common/sample_common/__init__.py</c> resolved fine under the heuristic
    /// while the real interpreter — handed only <c>projects/</c> — saw the *outer*
    /// <c>projects/sample_common/</c> directory, which has no <c>__init__.py</c>, as a PEP 420
    /// namespace package and failed with
    /// <c>cannot import name 'X' from 'sample_common' (unknown location)</c>.
    /// </summary>
    public sealed class PythonSearchPath
    {
        private readonly List<string> _roots;

        private PythonSearchPath(List<string> roots)
        {
            _roots = roots ?? new List<string>();
        }

        /// <summary>Import roots, in <c>sys.path</c> order (earliest entry first).</summary>
        public IReadOnlyList<string> Roots { get { return _roots; } }

        public static PythonSearchPath FromRoots(IEnumerable<string> roots)
        {
            return new PythonSearchPath(Normalize(roots));
        }

        /// <summary>
        /// The path a run of <paramref name="entryFile"/> will actually see: CPython puts the
        /// script's own directory at <c>sys.path[0]</c>, then everything from <c>PYTHONPATH</c>.
        /// Passing a null/empty entry file yields just the project roots (the shape used by a
        /// whole-solution compile, which has no single script).
        /// </summary>
        public static PythonSearchPath ForRun(string entryFile, IEnumerable<string> projectDirs,
            string solutionRoot = null)
        {
            List<string> ordered = new List<string>();
            if (!string.IsNullOrEmpty(entryFile))
            {
                string scriptDir = SafeDirectoryName(entryFile);
                if (!string.IsNullOrEmpty(scriptDir)) ordered.Add(scriptDir);
            }
            ordered.AddRange(ImportRootsForProjects(projectDirs, solutionRoot));
            return new PythonSearchPath(Normalize(ordered));
        }

        /// <summary>
        /// The import roots a solution's projects contribute to <c>PYTHONPATH</c>.
        ///
        /// Two entries per project, because Python projects in this IDE come in two shapes and
        /// only one of them is served by each entry:
        ///
        /// <list type="bullet">
        /// <item><b>The project directory itself.</b> Its top-level <c>.py</c> files are importable
        /// as top-level modules and each package subdirectory (a folder with <c>__init__.py</c>) is
        /// importable as a top-level package. This is what a <c>src</c>-style library project needs:
        /// <c>projects/sample_common/</c> must be on the path for <c>import sample_common</c> to bind
        /// the real <c>projects/sample_common/sample_common/</c> package.</item>
        /// <item><b>The parent directory</b>, so a project that is itself a regular package
        /// (<c>src/&lt;pkg&gt;/__init__.py</c>) is importable under its own folder name, and so
        /// cross-project namespace-package imports keep working.</item>
        /// </list>
        ///
        /// Only the parent entry existed before, which is what broke the <c>sample_workspace</c> layout:
        /// <c>projects/</c> alone made <c>import sample_common</c> bind the <c>__init__</c>-free
        /// <c>projects/sample_common/</c> folder as an empty namespace package instead of the real
        /// package one level down. Adding the project directory is strictly additive — nothing that
        /// resolved before stops resolving — because CPython's <c>PathFinder</c> only *records*
        /// namespace portions and keeps scanning, so the regular package found under the new root
        /// wins over the namespace portion found under the old one regardless of their order
        /// (verified against CPython 3.11 on this exact layout). The project directory is still
        /// emitted first, so the intent is legible in a printed <c>PYTHONPATH</c>.
        /// </summary>
        /// <param name="solutionRoot">
        /// The solution's own directory. When it holds a <c>src/</c> folder, that folder is added
        /// too — the solution-level equivalent of the per-project rule below. A research solution
        /// commonly keeps its shared library packages in a top-level <c>src/</c> (here: <c>data/</c>,
        /// <c>model/</c>, <c>training/</c>) that no single project owns, so nothing else would ever
        /// put it on the path and every import of it fails with ModuleNotFoundError — including
        /// from a trainer a project launches as a subprocess, which inherits this same PYTHONPATH.
        /// </param>
        public static IReadOnlyList<string> ImportRootsForProjects(IEnumerable<string> projectDirs,
            string solutionRoot = null)
        {
            List<string> ordered = new List<string>();

            if (!string.IsNullOrEmpty(solutionRoot))
            {
                string solFull = SafeFullPath(solutionRoot);
                if (!string.IsNullOrEmpty(solFull))
                {
                    string solSrc = Path.Combine(solFull, "src");
                    if (DirectoryExistsSafe(solSrc)) ordered.Add(solSrc);
                }
            }

            if (projectDirs == null) return Normalize(ordered);

            List<string> parents = new List<string>();
            foreach (string dir in projectDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string full = SafeFullPath(dir);
                if (string.IsNullOrEmpty(full)) continue;

                ordered.Add(full);

                // PEP 621 src-layout: `<project>/src/<pkg>/` is importable as `<pkg>` because an
                // editable install puts `<project>/src` on the path. Without this entry a
                // perfectly conventional project cannot import its own package.
                string srcDir = Path.Combine(full, "src");
                if (DirectoryExistsSafe(srcDir)) ordered.Add(srcDir);

                string parent = SafeDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) parents.Add(parent);
            }
            ordered.AddRange(parents);
            return Normalize(ordered);
        }

        /// <summary>
        /// The dotted module name <paramref name="filePath"/> is imported under, given this path —
        /// the compile-time equivalent of what <c>sys.modules</c> keys the file by, and what
        /// <c>__package__</c> is set to for relative-import resolution.
        ///
        /// The <c>__init__.py</c> chain above the file is authoritative and is walked FIRST,
        /// because that chain is what fixes the module's package depth at runtime no matter which
        /// path entry the import arrived through. Deriving the name from the shortest root-relative
        /// path instead silently truncates that chain whenever a package directory is itself a
        /// root — <c>src/app_server/middleware.py</c> came out as <c>middleware</c> rather than
        /// <c>app_server.middleware</c>, which made every <c>from .routes import …</c> in the file
        /// look like a relative import escaping the top-level package. Any namespace-package
        /// directories ABOVE the regular-package chain are then prefixed from the longest matching
        /// root, so the shortest importable name wins.
        ///
        /// Returns an empty string when the file sits under no root and belongs to no package.
        /// </summary>
        public string ModuleFqnOf(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return string.Empty;
            string full = SafeFullPath(filePath);
            if (string.IsNullOrEmpty(full)) return string.Empty;

            string stem = Path.GetFileNameWithoutExtension(full);
            string dir = SafeDirectoryName(full);

            // 1. The regular-package chain: walk up while each ancestor has an __init__.py.
            List<string> chain = new List<string>();
            while (!string.IsNullOrEmpty(dir) && FileExistsSafe(Path.Combine(dir, "__init__.py")))
            {
                chain.Insert(0, Path.GetFileName(dir));
                dir = SafeDirectoryName(dir);
            }
            // `dir` is now the anchor — the first directory that is not a regular package.

            // 2. Namespace portions between the anchor and its closest enclosing root.
            List<string> prefix = new List<string>();
            string anchorRel = RelativeToLongestRoot(dir);
            if (anchorRel == null)
            {
                // Anchored outside every root. The package chain is still the truth about the
                // module's depth; without it there is nothing to say.
                if (chain.Count == 0) return string.Empty;
            }
            else if (anchorRel.Length > 0)
            {
                prefix.AddRange(anchorRel.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries));
            }

            List<string> parts = new List<string>();
            parts.AddRange(prefix);
            parts.AddRange(chain);
            if (!string.Equals(stem, "__init__", StringComparison.Ordinal)) parts.Add(stem);
            return string.Join(".", parts);
        }

        // The path of <paramref name="dir"/> relative to the longest root containing it (so the
        // shortest, most-canonical module name wins), or null when no root contains it.
        private string RelativeToLongestRoot(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            string best = null;
            foreach (string root in _roots)
            {
                if (string.Equals(dir, root, StringComparison.OrdinalIgnoreCase)) return string.Empty;
                string prefix = EnsureTrailingSeparator(root);
                if (dir.Length <= prefix.Length) continue;
                if (!dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rel = dir.Substring(prefix.Length);
                if (best == null || rel.Length < best.Length) best = rel;
            }
            return best;
        }

        /// <summary>True when <paramref name="filePath"/> is a package's <c>__init__.py</c>.</summary>
        public static bool IsPackageInit(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            return string.Equals(Path.GetFileName(filePath), "__init__.py", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Renders the path as a <c>PYTHONPATH</c> value, appending any inherited
        /// <paramref name="existing"/> value so a user-configured path is not discarded.
        /// </summary>
        public string ToEnvironmentValue(string existing)
        {
            string joined = string.Join(Path.PathSeparator.ToString(), _roots);
            if (string.IsNullOrEmpty(existing)) return joined;
            if (string.IsNullOrEmpty(joined)) return existing;
            return joined + Path.PathSeparator + existing;
        }

        // ---- helpers ----

        private static List<string> Normalize(IEnumerable<string> roots)
        {
            List<string> outList = new List<string>();
            if (roots == null) return outList;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                string full = SafeFullPath(r);
                if (string.IsNullOrEmpty(full)) continue;
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Length == 0) continue;
                if (seen.Add(full)) outList.Add(full);
            }
            return outList;
        }

        private static string EnsureTrailingSeparator(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return dir;
            char last = dir[dir.Length - 1];
            if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar) return dir;
            return dir + Path.DirectorySeparatorChar;
        }

        private static string SafeFullPath(string p)
        {
            try { return Path.GetFullPath(p); }
            catch { return null; }
        }

        private static string SafeDirectoryName(string p)
        {
            try { return Path.GetDirectoryName(Path.GetFullPath(p)); }
            catch { return null; }
        }

        private static bool FileExistsSafe(string p)
        {
            try { return File.Exists(p); }
            catch { return false; }
        }

        private static bool DirectoryExistsSafe(string p)
        {
            try { return Directory.Exists(p); }
            catch { return false; }
        }
    }
}
