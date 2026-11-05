using System;

namespace HelixPythonNamespace.Python.Syntax
{
    /// <summary>
    /// PEP 257 docstring extraction, shared by every consumer that needs the prose attached to a
    /// <c>def</c> or <c>class</c>: editor Quick Info / hover, and the type extractor that fills the
    /// Solution Explorer's symbol tree. One implementation so the tree tooltip and the editor
    /// doctip never disagree about what a symbol's documentation is.
    /// </summary>
    public static class Docstrings
    {
        /// <summary>Longest docstring returned; the doc surfaces that show these all scroll, but
        /// none of them want to be handed a runaway string.</summary>
        public const int MaxLength = 2000;

        /// <summary>
        /// The docstring of a def/class body: its first statement, when that statement is a bare
        /// string literal. Null when there is none.
        /// </summary>
        public static string Of(SuiteSyntax body)
        {
            try
            {
                if (body == null || body.Statements == null || body.Statements.Count == 0) return null;
                ExpressionStatement first = body.Statements[0] as ExpressionStatement;
                LiteralExpression lit = first != null ? first.Expression as LiteralExpression : null;
                if (lit == null || lit.Token == null) return null;

                string doc = lit.Token.Value as string;
                if (doc == null)
                {
                    string t = lit.Token.Text ?? string.Empty;
                    // Only treat quoted tokens as docstrings (skip numbers/other literals).
                    int q = t.IndexOfAny(new[] { '"', '\'' });
                    if (q < 0 || q > 2) return null;   // allow r/b/f/u prefixes
                    doc = StripQuotes(t);
                }
                if (doc == null) return null;
                doc = doc.Trim();
                if (doc.Length == 0) return null;
                return doc.Length > MaxLength ? doc.Substring(0, MaxLength) + "…" : doc;
            }
            catch { return null; }
        }

        /// <summary>
        /// The first sentence/line of a docstring — what a one-line surface (a tree tooltip) shows
        /// when the full text would be a wall of prose. Null when there is no docstring.
        /// </summary>
        public static string SummaryOf(SuiteSyntax body)
        {
            return FirstLine(Of(body));
        }

        /// <summary>The first non-empty line of <paramref name="doc"/>, trimmed.</summary>
        public static string FirstLine(string doc)
        {
            if (string.IsNullOrEmpty(doc)) return null;
            foreach (string line in doc.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t;
            }
            return null;
        }

        /// <summary>Strip an r/b/f/u prefix and the surrounding quote run from a string token.</summary>
        public static string StripQuotes(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            int i = 0;
            while (i < raw.Length && (raw[i] == 'r' || raw[i] == 'R' || raw[i] == 'b' || raw[i] == 'B'
                   || raw[i] == 'f' || raw[i] == 'F' || raw[i] == 'u' || raw[i] == 'U')) i++;
            string s = raw.Substring(i);
            foreach (string q in new[] { "\"\"\"", "'''", "\"", "'" })
            {
                if (s.Length >= 2 * q.Length && s.StartsWith(q, StringComparison.Ordinal)
                    && s.EndsWith(q, StringComparison.Ordinal))
                    return s.Substring(q.Length, s.Length - 2 * q.Length);
            }
            return s;
        }
    }
}
