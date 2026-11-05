using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Workspace.Services
{
    /// <summary>
    /// "Remove Unused Imports" (Organize Imports). Removes top-level <c>import</c> / <c>from … import</c>
    /// bindings whose names are never used anywhere outside the import statements themselves. A binding is
    /// "used" when <see cref="AstIdentifierRewriter.FindOccurrences"/> finds an occurrence of its bound
    /// name on a line that isn't inside any import statement. Multi-name imports keep the used names and
    /// drop only the unused ones; a fully-unused import line is removed entirely.
    ///
    /// Scope/limits (documented): top-level imports only; <c>import *</c> is left untouched (its names are
    /// unknown); a rewritten import is re-emitted as a single line (collapsing a parenthesised multi-line
    /// import). This is a pure text transform, so it is unit-testable without an editor.
    /// </summary>
    public static class OrganizeImportsService
    {
        public static string RemoveUnusedImports(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch { return source; }
            if (module == null) return source;

            // Line ranges (1-based, inclusive) covered by import statements — usages here don't count.
            var importRanges = new List<int[]>();
            foreach (StatementSyntax s in module.Statements)
                if (s is ImportStatement || s is FromImportStatement)
                    importRanges.Add(new[] { s.Span.Start.Line, s.Span.End.Line });

            Func<string, bool> usedInCode = name =>
            {
                if (string.IsNullOrEmpty(name)) return true; // don't remove what we can't name
                foreach (var occ in AstIdentifierRewriter.FindOccurrences(source, name))
                {
                    int line = LineOfOffset(source, occ.start);
                    bool inImport = false;
                    foreach (int[] r in importRanges) if (line >= r[0] && line <= r[1]) { inImport = true; break; }
                    if (!inImport) return true;
                }
                return false;
            };

            // (loLine, hiLine, replacementOrNull) — null means delete the line range.
            var edits = new List<Tuple<int, int, string>>();
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is ImportStatement imp)
                {
                    var kept = imp.Names.Where(n => usedInCode(BoundNameOf(n))).ToList();
                    if (kept.Count == imp.Names.Count) continue;
                    string repl = kept.Count == 0 ? null : "import " + string.Join(", ", kept.Select(RenderImportedName));
                    edits.Add(Tuple.Create(s.Span.Start.Line, s.Span.End.Line, repl));
                }
                else if (s is FromImportStatement fi && !fi.IsStar)
                {
                    var kept = fi.Names.Where(kv => usedInCode(string.IsNullOrEmpty(kv.Value) ? kv.Key : kv.Value)).ToList();
                    if (kept.Count == fi.Names.Count) continue;
                    string modPart = new string('.', fi.RelativeDots) + (fi.Module != null ? fi.Module.AsString : string.Empty);
                    string repl = kept.Count == 0
                        ? null
                        : "from " + modPart + " import " +
                          string.Join(", ", kept.Select(kv => string.IsNullOrEmpty(kv.Value) ? kv.Key : kv.Key + " as " + kv.Value));
                    edits.Add(Tuple.Create(s.Span.Start.Line, s.Span.End.Line, repl));
                }
            }
            if (edits.Count == 0) return source;

            // Preserve per-line CR so \r\n files round-trip.
            var lines = new List<string>(source.Split('\n'));
            edits.Sort((a, b) => b.Item1.CompareTo(a.Item1)); // bottom-up so indices stay valid
            foreach (var e in edits)
            {
                int loIdx = e.Item1 - 1, hiIdx = e.Item2 - 1;
                if (loIdx < 0 || hiIdx >= lines.Count) continue;
                string first = lines[loIdx];
                string cr = first.EndsWith("\r") ? "\r" : string.Empty;
                string indent = LeadingWhitespace(first);
                lines.RemoveRange(loIdx, hiIdx - loIdx + 1);
                if (e.Item3 != null) lines.Insert(loIdx, indent + e.Item3 + cr);
            }
            return string.Join("\n", lines);
        }

        private static string BoundNameOf(ImportStatement.ImportedName n)
        {
            if (!string.IsNullOrEmpty(n.Alias)) return n.Alias;
            return (n.Name != null && n.Name.Parts.Count > 0) ? n.Name.Parts[0] : null;
        }

        private static string RenderImportedName(ImportStatement.ImportedName n)
        {
            string dotted = n.Name != null ? n.Name.AsString : string.Empty;
            return string.IsNullOrEmpty(n.Alias) ? dotted : dotted + " as " + n.Alias;
        }

        private static int LineOfOffset(string source, int offset)
        {
            int line = 1;
            int n = Math.Min(offset, source.Length);
            for (int i = 0; i < n; i++) if (source[i] == '\n') line++;
            return line;
        }

        private static string LeadingWhitespace(string s)
        {
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return s.Substring(0, i);
        }
    }
}
