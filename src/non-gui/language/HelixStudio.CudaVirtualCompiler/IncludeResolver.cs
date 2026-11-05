namespace HelixPythonNamespace.CudaVirtualCompiler
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Lexing;
    using HelixPythonNamespace.CudaVirtualCompiler.Parsing;

    /// <summary>Outcome of following an entry file's <c>#include</c> graph.</summary>
    public sealed class IncludeResolution
    {
        /// <summary>Parsed units of every resolved header (transitively, each once).</summary>
        public readonly List<TranslationUnit> Units = new List<TranslationUnit>();

        /// <summary>Absolute paths of the resolved headers, parallel in spirit to <see cref="Units"/>.</summary>
        public readonly List<string> ResolvedFiles = new List<string>();

        /// <summary>
        /// True when every QUOTED include (the project's own headers) resolved to a file,
        /// transitively. System <c>&lt;...&gt;</c> includes never clear this — their absence is
        /// expected. This is the gate for strict (Error-severity) undefined-name checking.
        /// </summary>
        public bool AllQuotedResolved = true;
    }

    /// <summary>
    /// Follows quoted <c>#include "..."</c> directives so cross-file symbols exist at analysis
    /// time — the single biggest reason the undefined-name pass used to be disabled for every
    /// real file (SRS §10: "absent headers make absence unprovable"). Headers are located with
    /// a workspace-layout-aware search: the including file's directory, any caller-supplied
    /// include roots, then each ancestor directory plus its <c>include/</c> child and each
    /// sibling package's <c>include/</c> — which finds <c>lib_core/errors.cuh</c> from
    /// <c>app_cli/include/app_cli/commands.cuh</c> in the standard
    /// <c>&lt;pkg&gt;/include/&lt;pkg&gt;/</c> layout the fixtures and wizard templates use.
    /// Parses are parse-only, transitive (depth/file capped), and memoized by (path, mtime)
    /// so the edit-time debounce doesn't re-read an unchanged header graph on every pause.
    /// </summary>
    public static class IncludeResolver
    {
        private const int MaxFiles = 64;
        private const int MaxDepth = 16;
        private const int MaxRoots = 64;

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private sealed class CacheEntry
        {
            public DateTime LastWriteUtc;
            public TranslationUnit Unit;
            public List<IncludeDirective> Includes;
        }

        public static IncludeResolution Resolve(string entryFilePath, string entrySource,
            IReadOnlyList<string> extraIncludeDirs)
        {
            var res = new IncludeResolution();
            string entryFull;
            string entryDir;
            try
            {
                entryFull = Path.GetFullPath(entryFilePath);
                entryDir = Path.GetDirectoryName(entryFull);
            }
            catch { return res; }
            if (string.IsNullOrEmpty(entryDir)) return res;

            List<string> roots = BuildSearchRoots(entryDir, extraIncludeDirs);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entryFull };

            var pp = new Preprocessor();
            pp.Scan(entrySource ?? string.Empty);
            for (int i = 0; i < pp.Includes.Count; i++)
                ResolveOne(pp.Includes[i], entryDir, roots, visited, res, 0);
            return res;
        }

        private static void ResolveOne(IncludeDirective inc, string includerDir, List<string> roots,
            HashSet<string> visited, IncludeResolution res, int depth)
        {
            if (inc == null || string.IsNullOrEmpty(inc.Path)) return;
            if (depth > MaxDepth || res.ResolvedFiles.Count >= MaxFiles) return;

            string found = FindFile(inc.Path, includerDir, roots);
            if (found == null)
            {
                if (!inc.IsSystem) res.AllQuotedResolved = false;
                return;
            }
            if (!visited.Add(found)) return;   // already merged (diamond includes)

            CacheEntry entry = LoadParsed(found);
            if (entry == null)
            {
                if (!inc.IsSystem) res.AllQuotedResolved = false;
                return;
            }

            string dir = Path.GetDirectoryName(found);
            for (int i = 0; i < entry.Includes.Count; i++)
                ResolveOne(entry.Includes[i], dir, roots, visited, res, depth + 1);

            res.Units.Add(entry.Unit);
            res.ResolvedFiles.Add(found);
        }

        private static string FindFile(string written, string includerDir, List<string> roots)
        {
            string rel = written.Replace('/', Path.DirectorySeparatorChar);
            string cand = TryCombine(includerDir, rel);
            if (cand != null) return cand;
            for (int i = 0; i < roots.Count; i++)
            {
                cand = TryCombine(roots[i], rel);
                if (cand != null) return cand;
            }
            return null;
        }

        private static string TryCombine(string dir, string rel)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                string p = Path.Combine(dir, rel);
                return File.Exists(p) ? Path.GetFullPath(p) : null;
            }
            catch { return null; }
        }

        private static List<string> BuildSearchRoots(string startDir, IReadOnlyList<string> extra)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (extra != null)
                for (int i = 0; i < extra.Count; i++) AddRoot(roots, seen, extra[i]);

            string d = startDir;
            int guard = 0;
            while (!string.IsNullOrEmpty(d) && guard++ < 8 && roots.Count < MaxRoots)
            {
                AddRoot(roots, seen, d);
                AddRoot(roots, seen, SafeCombine(d, "include"));
                // Sibling packages' include/ dirs: <root>/lib_core/include etc.
                try
                {
                    string[] children = Directory.GetDirectories(d);
                    for (int i = 0; i < children.Length && roots.Count < MaxRoots; i++)
                    {
                        string name = Path.GetFileName(children[i]);
                        if (name.Length == 0 || name[0] == '.') continue;
                        if (name.StartsWith("cmake-build", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(name, "build", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(name, "_deps", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(name, "artifacts", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(name, "CMakeFiles", StringComparison.OrdinalIgnoreCase)) continue;
                        AddRoot(roots, seen, SafeCombine(children[i], "include"));
                    }
                }
                catch { /* unreadable directory — skip its children */ }
                try { d = Path.GetDirectoryName(d); } catch { break; }
            }
            return roots;
        }

        private static string SafeCombine(string a, string b)
        {
            try { return Path.Combine(a, b); } catch { return null; }
        }

        private static void AddRoot(List<string> roots, HashSet<string> seen, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { if (!Directory.Exists(dir)) return; } catch { return; }
            if (seen.Add(dir)) roots.Add(dir);
        }

        private static CacheEntry LoadParsed(string fullPath)
        {
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(fullPath); } catch { return null; }
            lock (CacheLock)
            {
                CacheEntry cached;
                if (Cache.TryGetValue(fullPath, out cached) && cached.LastWriteUtc == mtime) return cached;
            }
            string src;
            try { src = File.ReadAllText(fullPath); } catch { return null; }

            TranslationUnit unit;
            var pp = new Preprocessor();
            try
            {
                pp.Scan(src);
                var lexer = new CudaLexer(src, fullPath, pp.Defines);
                var parser = new CudaParser(lexer.Tokenize(), fullPath);
                unit = parser.Parse();
            }
            catch { return null; }

            var entry = new CacheEntry
            {
                LastWriteUtc = mtime,
                Unit = unit,
                Includes = new List<IncludeDirective>(pp.Includes),
            };
            lock (CacheLock) Cache[fullPath] = entry;
            return entry;
        }
    }
}
