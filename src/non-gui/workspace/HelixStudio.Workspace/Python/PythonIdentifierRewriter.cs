using System;
using System.Collections.Generic;
using System.Text;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Pure helpers that rewrite Python source without touching disk. Powers the
    /// file-rename (import-path rewrite) and symbol-rename (identifier rewrite)
    /// flows. Because rewriting happens over tokens / AST spans, strings and
    /// comments are never disturbed.
    /// </summary>
    public static class PythonIdentifierRewriter
    {
        /// <summary>
        /// Replace every <c>TokenKind.Name</c> token whose text equals
        /// <paramref name="oldName"/> with <paramref name="newName"/>.
        /// Keywords, string literals, and comments are untouched.
        /// </summary>
        public static RewriteResult RewriteIdentifier(string source, string oldName, string newName)
        {
            if (source == null) return new RewriteResult(null, 0);
            if (string.IsNullOrEmpty(oldName) || newName == null || oldName == newName)
                return new RewriteResult(source, 0);

            IReadOnlyList<Token> tokens;
            try { tokens = global::HelixPythonNamespace.Python.Python.Tokenize(source); }
            catch { return new RewriteResult(source, 0); }

            // Collect matching spans; we'll splice in reverse to preserve offsets.
            List<(int start, int end)> hits = new List<(int, int)>();
            foreach (Token t in tokens)
            {
                if (t.Kind != TokenKind.Name) continue;
                if (!string.Equals(t.Text, oldName, StringComparison.Ordinal)) continue;
                int start = t.Span.Start.Offset;
                int end = t.Span.End.Offset;
                if (end <= start || start < 0 || end > source.Length) continue;
                hits.Add((start, end));
            }

            if (hits.Count == 0) return new RewriteResult(source, 0);

            StringBuilder sb = new StringBuilder(source.Length + hits.Count * Math.Max(0, newName.Length - oldName.Length));
            int cursor = 0;
            foreach (var h in hits)
            {
                if (h.start < cursor) continue; // defensive: overlapping spans
                sb.Append(source, cursor, h.start - cursor);
                sb.Append(newName);
                cursor = h.end;
            }
            if (cursor < source.Length) sb.Append(source, cursor, source.Length - cursor);

            return new RewriteResult(sb.ToString(), hits.Count);
        }

        /// <summary>
        /// Rewrite the module path of every <c>from &lt;mod&gt; import ...</c> and
        /// <c>import &lt;mod&gt;</c> statement whose module equals
        /// <paramref name="oldModule"/> or starts with <c>oldModule + "."</c>.
        /// The matched prefix is replaced with <paramref name="newModule"/>.
        /// </summary>
        public static RewriteResult RewriteModuleImports(string source, string oldModule, string newModule)
        {
            if (source == null) return new RewriteResult(null, 0);
            if (string.IsNullOrEmpty(oldModule) || newModule == null || oldModule == newModule)
                return new RewriteResult(source, 0);

            ModuleSyntax module;
            try { module = global::HelixPythonNamespace.Python.Python.ParseModule(source).Module; }
            catch { return new RewriteResult(source, 0); }
            if (module == null) return new RewriteResult(source, 0);

            List<(int start, int end, string replacement)> hits = new List<(int, int, string)>();

            foreach (StatementSyntax s in module.Statements)
            {
                if (s is FromImportStatement fi && fi.Module != null)
                {
                    TryAddModuleHit(fi.Module, oldModule, newModule, hits);
                }
                else if (s is ImportStatement imp)
                {
                    foreach (ImportStatement.ImportedName n in imp.Names)
                    {
                        if (n?.Name != null) TryAddModuleHit(n.Name, oldModule, newModule, hits);
                    }
                }
            }

            if (hits.Count == 0) return new RewriteResult(source, 0);

            // Sort ascending by start offset so we can splice in a single pass.
            hits.Sort((a, b) => a.start.CompareTo(b.start));

            StringBuilder sb = new StringBuilder();
            int cursor = 0;
            foreach (var h in hits)
            {
                if (h.start < cursor) continue; // defensive: overlap
                if (h.start < 0 || h.end > source.Length || h.end <= h.start) continue;
                sb.Append(source, cursor, h.start - cursor);
                sb.Append(h.replacement);
                cursor = h.end;
            }
            if (cursor < source.Length) sb.Append(source, cursor, source.Length - cursor);

            return new RewriteResult(sb.ToString(), hits.Count);
        }

        private static void TryAddModuleHit(
            DottedName module, string oldModule, string newModule,
            List<(int start, int end, string replacement)> hits)
        {
            string cur = module.AsString;
            if (string.IsNullOrEmpty(cur)) return;

            string replacement;
            if (string.Equals(cur, oldModule, StringComparison.Ordinal))
            {
                replacement = newModule;
            }
            else if (cur.StartsWith(oldModule + ".", StringComparison.Ordinal))
            {
                replacement = newModule + cur.Substring(oldModule.Length);
            }
            else
            {
                return;
            }

            int start = module.Span.Start.Offset;
            int end = module.Span.End.Offset;
            if (end <= start) return;
            hits.Add((start, end, replacement));
        }

        public readonly struct RewriteResult
        {
            public readonly string Text;
            public readonly int Count;
            public RewriteResult(string text, int count) { Text = text; Count = count; }
        }
    }
}
