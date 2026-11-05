namespace HelixPythonNamespace.Refactoring.Structural
{
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.Refactoring.Vectorization;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Verbatim source-text slicing keyed off AST node spans. Port of the C++
    /// <c>structural::BodySlicer</c>.
    /// <para>
    /// Transforms that relocate <i>arbitrary</i> statement bodies (O1 functions&lt;-&gt;class,
    /// P6 Facade, SOLID split/merge, ...) cannot round-trip byte-exact through a
    /// pretty-printer, so instead they lift the original body text unchanged and only
    /// re-indent it. This helper centralizes that machinery.
    /// </para>
    /// <para>
    /// All offsets / line numbers carried by the AST are relative to the <i>normalized</i>
    /// source that <see cref="SelectionParser.Parse"/> actually fed the parser (common
    /// indent stripped, trailing newline ensured, line endings as "\n"). Always slice
    /// against <see cref="NormalizedSource"/>, never the raw selection.
    /// </para>
    /// </summary>
    public static class BodySlicer
    {
        /// <summary>
        /// Reproduce exactly the string <see cref="SelectionParser.Parse"/> parsed: strip
        /// the selection's common indent and ensure a trailing newline.
        /// </summary>
        public static string NormalizedSource(string selectedSource)
        {
            string indent = SelectionParser.ComputeCommonIndent(selectedSource);
            string normalized = SelectionParser.StripCommonIndent(selectedSource, indent);
            if (normalized.Length == 0 || normalized[normalized.Length - 1] != '\n')
            {
                normalized += "\n";
            }
            return normalized;
        }

        /// <summary>Verbatim substring covered by a node's span (offset-based).</summary>
        public static string SliceSpan(string normalizedSource, AstNode node)
        {
            int start = node.Span.Start.Offset;
            int len = node.Span.Length;
            if (start < 0 || start > normalizedSource.Length) return "";
            if (start + len > normalizedSource.Length) len = normalizedSource.Length - start;
            return normalizedSource.Substring(start, len);
        }

        /// <summary>
        /// Whole source lines spanned by a body, from the first statement's start line
        /// through the last statement's end line, inclusive.
        /// <para>
        /// NOTE: the parser's compound-statement end spans overshoot into the following
        /// statement, so this is only reliable for bodies of simple (non-compound)
        /// statements. Prefer <see cref="SliceIndentedBody"/> for general bodies.
        /// </para>
        /// </summary>
        public static List<string> SliceBodyLines(string normalizedSource, IReadOnlyList<Stmt> body)
        {
            List<string> outLines = new List<string>();
            if (body == null || body.Count == 0) return outLines;

            int startLine = body[0].Span.Start.Line;             // 1-based
            int endLine = body[body.Count - 1].Span.End.Line;    // 1-based inclusive
            List<string> all = SplitLinesRaw(normalizedSource);
            for (int ln = startLine; ln <= endLine; ln++)
            {
                int idx = ln - 1;
                if (idx < 0 || idx >= all.Count) break;
                outLines.Add(StripCr(all[idx]));
            }

            // A compound statement's end span can overshoot onto the following blank
            // line; never carry trailing blank lines as part of a body.
            TrimTrailingBlanks(outLines);
            return outLines;
        }

        /// <summary>
        /// Slice a def/class suite body by indentation — robust against the
        /// compound-statement span overshoot. <paramref name="defLine"/> is the 1-based
        /// header line (its leading-space count fixes the suite's base indent);
        /// <paramref name="bodyFirstLine"/> is the 1-based line of the first body statement.
        /// </summary>
        public static List<string> SliceIndentedBody(string normalizedSource, int defLine, int bodyFirstLine)
        {
            List<string> all = SplitLinesRaw(normalizedSource);
            int headerIndent = LeadingSpaces(LineAt(all, defLine));

            List<string> outLines = new List<string>();
            for (int ln = bodyFirstLine; ln <= all.Count; ln++)
            {
                string line = LineAt(all, ln);
                if (IsBlank(line)) { outLines.Add(line); continue; }
                if (LeadingSpaces(line) <= headerIndent) break;  // dedent ends the suite
                outLines.Add(line);
            }
            TrimTrailingBlanks(outLines);
            return outLines;
        }

        /// <summary>
        /// Smallest leading-space count over the non-blank lines. Returns 0 if every line
        /// is blank.
        /// </summary>
        public static int CommonIndentWidth(IList<string> lines)
        {
            int common = -1;
            foreach (string l in lines)
            {
                if (IsBlank(l)) continue;
                int n = LeadingSpaces(l);
                common = (common < 0) ? n : System.Math.Min(common, n);
            }
            return common < 0 ? 0 : common;
        }

        /// <summary>Remove up to <paramref name="n"/> leading spaces from each line (blank lines -&gt; empty).</summary>
        public static List<string> Dedent(IList<string> lines, int n)
        {
            List<string> outLines = new List<string>(lines.Count);
            foreach (string l in lines)
            {
                if (IsBlank(l)) { outLines.Add(""); continue; }
                int strip = System.Math.Min(n, LeadingSpaces(l));
                outLines.Add(l.Substring(strip));
            }
            return outLines;
        }

        /// <summary>Prepend <paramref name="n"/> spaces to each non-blank line (blank lines stay empty).</summary>
        public static List<string> Indent(IList<string> lines, int n)
        {
            List<string> outLines = new List<string>(lines.Count);
            string pad = new string(' ', System.Math.Max(0, n));
            foreach (string l in lines)
            {
                if (IsBlank(l)) { outLines.Add(""); continue; }
                outLines.Add(pad + l);
            }
            return outLines;
        }

        /// <summary>
        /// Dedent a body to column 0 then re-indent to <paramref name="targetIndent"/> —
        /// the canonical "relocate a body to a new nesting level" op.
        /// </summary>
        public static List<string> Reindent(IList<string> lines, int targetIndent)
        {
            int w = CommonIndentWidth(lines);
            return Indent(Dedent(lines, w), targetIndent);
        }

        /// <summary>
        /// Number of consecutive blank lines immediately above 1-based <paramref name="line"/>.
        /// Used to require exactly one blank line between relocated members.
        /// </summary>
        public static int BlankLinesAbove(string normalizedSource, int line)
        {
            List<string> all = SplitLinesRaw(normalizedSource);
            int count = 0;
            for (int idx = line - 2; idx >= 0; idx--)  // line-1 is `line` itself (0-based)
            {
                if (idx >= all.Count) continue;
                if (IsBlank(all[idx])) count++;
                else break;
            }
            return count;
        }

        /// <summary>The verbatim text of 1-based <paramref name="line"/>. Empty if out of range.</summary>
        public static string LineText(string normalizedSource, int line)
        {
            return LineAt(SplitLinesRaw(normalizedSource), line);
        }

        /// <summary>Join with '\n', no trailing newline.</summary>
        public static string Join(IList<string> lines)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append(lines[i]);
                if (i + 1 < lines.Count) sb.Append('\n');
            }
            return sb.ToString();
        }

        internal static List<string> SplitLinesRaw(string s)
        {
            List<string> outList = new List<string>();
            StringBuilder cur = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\n') { outList.Add(cur.ToString()); cur.Length = 0; }
                else cur.Append(c);
            }
            outList.Add(cur.ToString());
            return outList;
        }

        internal static bool IsBlank(string line)
        {
            foreach (char c in line)
            {
                if (c != ' ' && c != '\t' && c != '\r') return false;
            }
            return true;
        }

        internal static int LeadingSpaces(string line)
        {
            int n = 0;
            while (n < line.Length && line[n] == ' ') n++;
            return n;
        }

        private static string StripCr(string s)
        {
            if (s.Length > 0 && s[s.Length - 1] == '\r') return s.Substring(0, s.Length - 1);
            return s;
        }

        private static string LineAt(List<string> all, int line)
        {
            int idx = line - 1;
            if (idx < 0 || idx >= all.Count) return "";
            return StripCr(all[idx]);
        }

        private static void TrimTrailingBlanks(List<string> lines)
        {
            while (lines.Count > 0 && IsBlank(lines[lines.Count - 1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }
        }
    }
}
