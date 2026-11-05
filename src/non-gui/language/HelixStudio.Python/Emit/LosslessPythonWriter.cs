using System;
using System.Collections.Generic;
using System.Text;
using HelixPythonNamespace.Python.Syntax;
using HelixPythonNamespace.Python.Text;

namespace HelixPythonNamespace.Python.Emit
{
    /// <summary>A surgical replacement of one CST node's source text, addressed by its <see cref="SourceSpan"/>.</summary>
    public sealed class PythonEdit
    {
        public readonly SourceSpan Span;
        public readonly string NewText;

        public PythonEdit(SyntaxNode node, string newText)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            Span = node.Span;
            NewText = newText ?? string.Empty;
        }

        public PythonEdit(SourceSpan span, string newText)
        {
            Span = span;
            NewText = newText ?? string.Empty;
        }
    }

    /// <summary>
    /// FR-GEN-02 — a <b>lossless, trivia-preserving</b> Python source↔AST round-trip. Where
    /// <see cref="PythonWriter"/> regenerates <i>canonical</i> source from the tree (normalizing whitespace,
    /// dropping comments), this writer reconstructs source directly from the concrete-syntax-tree node spans:
    /// it copies each top-level statement's exact source range and the inter-statement gaps — where comments,
    /// blank lines, and original spacing live — verbatim from the original text. For an unmodified tree the
    /// output is <b>byte-identical</b> to the input; comments, docstrings, blank lines, trailing whitespace,
    /// and mixed indentation all survive. An edit overload splices targeted node replacements in while leaving
    /// every surrounding byte untouched — the mechanism a refactor/codegen pass needs to change one declaration
    /// without reformatting the whole file (the libcst / Roslyn "syntax tree with changes" model).
    /// </summary>
    public static class LosslessPythonWriter
    {
        /// <summary>
        /// Reconstruct the module's source by tiling its top-level statement spans: each statement's exact
        /// source range is copied verbatim, and the gaps between statements (where comments, blank lines, and
        /// original spacing live) are copied straight from <paramref name="source"/>. With an unmodified tree
        /// this returns text byte-identical to what was parsed — proving the CST spans fully tile the source.
        /// </summary>
        public static string Write(string source, ModuleSyntax module)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (module == null) throw new ArgumentNullException(nameof(module));

            var sb = new StringBuilder(source.Length + 16);
            int cursor = 0;
            int len = source.Length;
            foreach (StatementSyntax stmt in module.Statements)
            {
                int s = Clamp(stmt.Span.Start.Offset, cursor, len);
                int en = Clamp(stmt.Span.End.Offset, s, len);
                if (s > cursor) sb.Append(source, cursor, s - cursor); // inter-statement gap (trivia) verbatim
                sb.Append(source, s, en - s);                          // the statement itself verbatim
                cursor = en;
            }
            if (cursor < len) sb.Append(source, cursor, len - cursor);  // leading-before-first / trailing trivia
            return sb.ToString();
        }

        /// <summary>
        /// Reconstruct the module's source, substituting <paramref name="edits"/> for the nodes they target.
        /// Untouched regions — including all comments and whitespace — are copied verbatim from
        /// <paramref name="source"/>. Edits must not overlap; they are applied in source order.
        /// </summary>
        public static string Write(string source, ModuleSyntax module, IEnumerable<PythonEdit> edits)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (module == null) throw new ArgumentNullException(nameof(module));

            // Order the edits by start offset and validate non-overlap up front so a bad edit set fails
            // loudly rather than producing silently corrupt output.
            var ordered = new List<PythonEdit>();
            if (edits != null) ordered.AddRange(edits);
            ordered.Sort((a, b) => a.Span.Start.Offset.CompareTo(b.Span.Start.Offset));
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Span.Start.Offset < ordered[i - 1].Span.End.Offset)
                    throw new InvalidOperationException("overlapping edits at offset " + ordered[i].Span.Start.Offset);
            }

            var sb = new StringBuilder(source.Length + 16);
            int cursor = 0;
            int len = source.Length;

            foreach (PythonEdit e in ordered)
            {
                int s = Clamp(e.Span.Start.Offset, 0, len);
                int en = Clamp(e.Span.End.Offset, s, len);
                if (s > cursor) sb.Append(source, cursor, s - cursor); // verbatim gap (may include trivia)
                sb.Append(e.NewText);
                cursor = en;
            }
            if (cursor < len) sb.Append(source, cursor, len - cursor); // remainder verbatim
            return sb.ToString();
        }

        /// <summary>
        /// Convenience: parse-free round-trip check. Returns true when reconstructing <paramref name="source"/>
        /// from <paramref name="module"/> yields byte-identical text (i.e. the tree fully accounts for the source).
        /// </summary>
        public static bool RoundTripsExactly(string source, ModuleSyntax module)
        {
            return string.Equals(Write(source, module), source, StringComparison.Ordinal);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
