namespace HelixPythonNamespace.Editor.Navigation
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;

    /// <summary>One match from a find-in-path search. Port of the C++ <c>FindInPathHit</c>.</summary>
    public sealed class FindInPathHit
    {
        public string FilePath = "";
        public int Line;        // 1-based
        public int Column;      // 1-based
        public string LineText = "";
        public int MatchLength;
    }

    /// <summary>Search options. Port of the C++ <c>FindInPathOptions</c>.</summary>
    public sealed class FindInPathOptions
    {
        public string Query = "";
        public string RootDir = "";
        public bool CaseSensitive;
        public bool WholeWord;
        public bool UseRegex;
        public List<string> IncludeExtensions = new List<string> { ".py", ".pyi" };
        public int MaxHits = 5000;
    }

    /// <summary>
    /// Recursive text search across a project root, skipping common build/cache dirs.
    /// Synchronous; callers can run on a worker thread. Port of the C++ <c>FindInPathService</c>
    /// (std::atomic cancel token → <see cref="CancellationToken"/>).
    /// </summary>
    public sealed class FindInPathService
    {
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".hg", ".svn", "__pycache__", ".venv", "venv",
            "node_modules", "dist", "build", ".idea", ".vscode", ".pytest_cache",
            ".mypy_cache", ".ruff_cache", "htmlcov",
        };

        public List<FindInPathHit> Search(FindInPathOptions opts, CancellationToken cancel = default(CancellationToken), Action<string> progress = null)
        {
            List<FindInPathHit> hits = new List<FindInPathHit>();
            if (opts == null || string.IsNullOrEmpty(opts.Query) || string.IsNullOrEmpty(opts.RootDir)) return hits;
            if (!Directory.Exists(opts.RootDir)) return hits;

            Regex re = null;
            if (opts.UseRegex)
            {
                try { re = new Regex(opts.Query, opts.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch { return hits; }
            }

            foreach (string filePath in EnumerateFiles(opts.RootDir, opts.IncludeExtensions, cancel))
            {
                if (cancel.IsCancellationRequested) return hits;
                if (progress != null) progress(filePath);

                string text;
                try { text = File.ReadAllText(filePath); }
                catch { continue; }

                // Literal search: a whole-file IndexOf is far cheaper than splitting the
                // file into per-line strings. The previous code allocated one substring per
                // line of EVERY scanned file (O(total source bytes) of garbage) even when a
                // file held no match — and in a project-wide search the vast majority of
                // files match nothing. Skip those files before the per-line loop. (Regex can
                // match across the IgnoreCase/word-boundary nuances, so it is not pre-filtered.)
                if (re == null &&
                    text.IndexOf(opts.Query, opts.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                int lineStart = 0;
                int line = 1;
                for (int i = 0; i <= text.Length; i++)
                {
                    if (i != text.Length && text[i] != '\n') continue;
                    int len = i - lineStart;
                    if (len > 0 && text[lineStart + len - 1] == '\r') len--;
                    string ln = text.Substring(lineStart, len);

                    if (re != null)
                    {
                        foreach (System.Text.RegularExpressions.Match m in re.Matches(ln))
                        {
                            hits.Add(new FindInPathHit { FilePath = filePath, Line = line, Column = m.Index + 1, MatchLength = m.Length, LineText = ln });
                            if (hits.Count >= opts.MaxHits) return hits;
                        }
                    }
                    else
                    {
                        int from = 0;
                        while (true)
                        {
                            int p = FindOnce(ln, opts.Query, from, opts.CaseSensitive, opts.WholeWord);
                            if (p < 0) break;
                            hits.Add(new FindInPathHit { FilePath = filePath, Line = line, Column = p + 1, MatchLength = opts.Query.Length, LineText = ln });
                            if (hits.Count >= opts.MaxHits) return hits;
                            from = p + opts.Query.Length;
                        }
                    }

                    lineStart = i + 1;
                    line++;
                }
            }
            return hits;
        }

        public int ReplaceAll(FindInPathOptions opts, string replacement)
        {
            if (opts == null || string.IsNullOrEmpty(opts.Query) || string.IsNullOrEmpty(opts.RootDir)) return 0;
            if (!Directory.Exists(opts.RootDir)) return 0;

            Regex re = null;
            if (opts.UseRegex)
            {
                try { re = new Regex(opts.Query, opts.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase); }
                catch { return 0; }
            }

            int total = 0;
            foreach (string filePath in EnumerateFiles(opts.RootDir, opts.IncludeExtensions, CancellationToken.None))
            {
                string text;
                try { text = File.ReadAllText(filePath); }
                catch { continue; }

                string outText;
                int replacedHere = 0;
                if (re != null)
                {
                    string replaced = re.Replace(text, replacement);
                    if (replaced != text) { outText = replaced; replacedHere = 1; }
                    else outText = text;
                }
                else
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
                    int from = 0;
                    while (true)
                    {
                        int p = FindOnce(text, opts.Query, from, opts.CaseSensitive, opts.WholeWord);
                        if (p < 0) { sb.Append(text, from, text.Length - from); break; }
                        sb.Append(text, from, p - from);
                        sb.Append(replacement);
                        from = p + opts.Query.Length;
                        replacedHere++;
                    }
                    outText = sb.ToString();
                }

                if (replacedHere > 0)
                {
                    try { File.WriteAllText(filePath, outText); total += replacedHere; }
                    catch { }
                }
            }
            return total;
        }

        // ---- helpers ----

        private IEnumerable<string> EnumerateFiles(string root, List<string> exts, CancellationToken cancel)
        {
            Stack<string> dirs = new Stack<string>();
            dirs.Push(root);
            while (dirs.Count > 0)
            {
                if (cancel.IsCancellationRequested) yield break;
                string dir = dirs.Pop();

                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { subDirs = new string[0]; }
                foreach (string sub in subDirs)
                {
                    string name = Path.GetFileName(sub);
                    if (!SkipDirs.Contains(name)) dirs.Push(sub);
                }

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { files = new string[0]; }
                foreach (string f in files)
                    if (ExtensionAllowed(f, exts)) yield return f;
            }
        }

        private static bool ExtensionAllowed(string path, List<string> exts)
        {
            if (exts == null || exts.Count == 0) return true;
            string e = Path.GetExtension(path);
            foreach (string ext in exts)
                if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsWordBoundary(char c)
        {
            return !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_');
        }

        internal static int FindOnce(string hay, string needle, int from, bool caseSensitive, bool wholeWord)
        {
            if (string.IsNullOrEmpty(needle) || from > hay.Length) return -1;
            StringComparison cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int p = from;
            while (p <= hay.Length - needle.Length)
            {
                int idx = hay.IndexOf(needle, p, cmp);
                if (idx < 0) return -1;
                bool ok = !wholeWord ||
                    ((idx == 0 || IsWordBoundary(hay[idx - 1])) &&
                     (idx + needle.Length == hay.Length || IsWordBoundary(hay[idx + needle.Length])));
                if (ok) return idx;
                p = idx + 1;
            }
            return -1;
        }
    }
}
