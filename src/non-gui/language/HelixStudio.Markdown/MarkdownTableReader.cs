using System;
using System.Collections.Generic;
using System.Text;

namespace HelixPythonNamespace.Markdown
{
    /// <summary>
    /// GFM pipe tables.
    ///
    /// Not an optional extra here: the documents this engine was written for are full of them —
    /// the README in the screenshot that started this work opens with a two-column "was / is now"
    /// table and a three-column results table. Rendering those as paragraphs of pipes is most of
    /// what "displayed as plain text" looked like.
    /// </summary>
    internal static class MarkdownTableReader
    {
        /// <summary>
        /// True for a delimiter row: <c>|---|:--:|--:|</c>. This row, not the one above it, is what
        /// makes a table a table — <c>a | b</c> alone is an ordinary paragraph.
        /// </summary>
        public static bool IsDelimiterRow(string line)
        {
            if (line == null) return false;
            string trimmed = line.Trim();
            if (trimmed.Length == 0) return false;

            bool sawDash = false;
            bool sawPipe = false;
            foreach (char c in trimmed)
            {
                if (c == '-') sawDash = true;
                else if (c == '|') sawPipe = true;
                else if (c != ':' && c != ' ' && c != '\t') return false;
            }
            // A bare `---` is a thematic break, so at least one pipe is required as well.
            return sawDash && sawPipe;
        }

        public static int Parse(string[] lines, int index, int to, List<MarkdownBlock> into)
        {
            List<string> headers = SplitRow(lines[index]);
            List<MarkdownAlignment> alignments = ParseAlignments(lines[index + 1]);

            var table = new MarkdownBlock(MarkdownBlockKind.Table) { SourceLine = index + 1 };
            foreach (MarkdownAlignment a in alignments) table.ColumnAlignments.Add(a);

            // The header row's cell count wins. A delimiter row declaring more or fewer columns is
            // malformed; padding to the header keeps every body row rectangular so the renderer's
            // column-width pass never indexes past the end.
            int columns = headers.Count;
            while (table.ColumnAlignments.Count < columns) table.ColumnAlignments.Add(MarkdownAlignment.None);
            if (table.ColumnAlignments.Count > columns)
                table.ColumnAlignments.RemoveRange(columns, table.ColumnAlignments.Count - columns);

            table.Children.Add(BuildRow(headers, columns, true, index + 1));

            int i = index + 2;
            for (; i < to; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) break;
                if (line.IndexOf('|') < 0) break;         // the table ends at the first non-pipe line
                table.Children.Add(BuildRow(SplitRow(line), columns, false, i + 1));
            }

            into.Add(table);
            return i;
        }

        private static MarkdownBlock BuildRow(List<string> cells, int columns, bool isHeader, int sourceLine)
        {
            var row = new MarkdownBlock(MarkdownBlockKind.TableRow) { IsHeader = isHeader, SourceLine = sourceLine };
            for (int c = 0; c < columns; c++)
            {
                var cell = new MarkdownBlock(MarkdownBlockKind.TableCell)
                {
                    Literal = c < cells.Count ? cells[c] : string.Empty,
                    IsHeader = isHeader,
                    SourceLine = sourceLine,
                };
                row.Children.Add(cell);
            }
            return row;
        }

        /// <summary>
        /// Splits a row on unescaped pipes, dropping the optional leading and trailing ones.
        ///
        /// <c>\|</c> is a literal pipe inside a cell — without honouring the escape, any table
        /// documenting a shell pipeline or a regex alternation silently gains phantom columns.
        /// </summary>
        internal static List<string> SplitRow(string line)
        {
            var cells = new List<string>();
            var current = new StringBuilder();
            string trimmed = line.Trim();

            int start = 0;
            int end = trimmed.Length;
            if (start < end && trimmed[start] == '|') start++;
            if (end > start && trimmed[end - 1] == '|' && !IsEscapedAt(trimmed, end - 1)) end--;

            for (int i = start; i < end; i++)
            {
                char c = trimmed[i];
                if (c == '\\' && i + 1 < end && trimmed[i + 1] == '|') { current.Append('|'); i++; continue; }
                if (c == '|') { cells.Add(current.ToString().Trim()); current.Length = 0; continue; }
                current.Append(c);
            }
            cells.Add(current.ToString().Trim());
            return cells;
        }

        private static bool IsEscapedAt(string s, int index)
        {
            int backslashes = 0;
            for (int i = index - 1; i >= 0 && s[i] == '\\'; i--) backslashes++;
            return (backslashes % 2) == 1;
        }

        private static List<MarkdownAlignment> ParseAlignments(string delimiterRow)
        {
            var alignments = new List<MarkdownAlignment>();
            foreach (string cell in SplitRow(delimiterRow))
            {
                string s = cell.Trim();
                bool left = s.StartsWith(":", StringComparison.Ordinal);
                bool right = s.EndsWith(":", StringComparison.Ordinal);
                if (left && right) alignments.Add(MarkdownAlignment.Center);
                else if (right) alignments.Add(MarkdownAlignment.Right);
                else if (left) alignments.Add(MarkdownAlignment.Left);
                else alignments.Add(MarkdownAlignment.None);
            }
            return alignments;
        }
    }
}
