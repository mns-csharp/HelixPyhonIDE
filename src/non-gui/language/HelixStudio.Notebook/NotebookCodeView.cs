using System;
using System.Collections.Generic;
using System.Text;

namespace HelixPythonNamespace.Notebooks
{
    /// <summary>
    /// The notebook's code cells laid end to end as one Python source text, with a map back to the
    /// cell and line each character came from.
    ///
    /// <para>
    /// Everything in the IDE that reads Python — completion, the hover provider, the navigation
    /// bar's symbol lists, the linter, the virtual compiler — takes a source string and a caret
    /// offset. This is the adapter that lets all of them work on a notebook without any of them
    /// learning what a notebook is: a name bound in cell 3 is visible to completion in cell 7
    /// because by the time the engine sees it, it is one file.
    /// </para>
    ///
    /// <para>
    /// Two things it must get right, and both are why the map exists rather than a naive join:
    /// a diagnostic reported at global line 412 has to come back as "cell 9, line 4" or it is
    /// useless, and the caret offset inside one cell has to become the right offset in the joined
    /// text or completion answers about the wrong scope.
    /// </para>
    /// </summary>
    public sealed class NotebookCodeView
    {
        private readonly List<CellSpan> _spans = new List<CellSpan>();

        private NotebookCodeView(string source, List<CellSpan> spans)
        {
            Source = source ?? string.Empty;
            if (spans != null) _spans = spans;
        }

        /// <summary>The joined Python source.</summary>
        public string Source { get; private set; }

        /// <summary>One entry per code cell that contributed, in document order.</summary>
        public IList<CellSpan> Spans { get { return _spans; } }

        /// <summary>Where one cell's text ended up in the joined source.</summary>
        public sealed class CellSpan
        {
            public int CellIndex;        // index into NotebookDocument.Cells
            public int StartOffset;      // char offset of the cell's first character
            public int Length;           // characters contributed (excluding the separator)
            public int StartLine;        // 0-based line in the joined source
            public int LineCount;
        }

        /// <summary>
        /// Build the view. Cells are separated by a single newline, and a magic or shell line
        /// (<c>%matplotlib inline</c>, <c>!pip install …</c>) is replaced by a comment of the SAME
        /// LENGTH: it is not Python, it would derail every parser that reads this, and blanking it
        /// instead of deleting it keeps every offset and line number in the map exact.
        /// </summary>
        public static NotebookCodeView Build(NotebookDocument document)
        {
            var builder = new StringBuilder();
            var spans = new List<CellSpan>();
            if (document == null) return new NotebookCodeView(string.Empty, spans);

            int line = 0;
            for (int i = 0; i < document.Cells.Count; i++)
            {
                NotebookCell cell = document.Cells[i];
                if (cell.Kind != NotebookCellKind.Code) continue;

                string text = NotebookMagics.NeutraliseForParsing(cell.Source ?? string.Empty);
                // Every cell ends with a newline in the joined source, so the last statement of one
                // cell and the first of the next can never merge into one line. The cell's own
                // Length excludes that separator — it is what a caret inside the cell is clamped to.
                string joined = text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n";

                var span = new CellSpan
                {
                    CellIndex = i,
                    StartOffset = builder.Length,
                    Length = text.Length,
                    StartLine = line,
                    // Lines OCCUPIED, counted from the separator-terminated text: an empty cell
                    // still occupies one (empty) line, and a span of zero lines maps nowhere — which
                    // is how the first empty code cell in the corpus made line 79 unmappable.
                    LineCount = CountNewLines(joined),
                };
                spans.Add(span);
                builder.Append(joined);
                line += span.LineCount;
            }
            return new NotebookCodeView(builder.ToString(), spans);
        }

        /// <summary>
        /// The joined-source offset for a caret sitting at <paramref name="offsetInCell"/> of
        /// <paramref name="cellIndex"/>, or -1 when that cell contributes no code.
        /// </summary>
        public int ToGlobalOffset(int cellIndex, int offsetInCell)
        {
            foreach (CellSpan span in _spans)
            {
                if (span.CellIndex != cellIndex) continue;
                if (offsetInCell < 0) offsetInCell = 0;
                if (offsetInCell > span.Length) offsetInCell = span.Length;
                return span.StartOffset + offsetInCell;
            }
            return -1;
        }

        /// <summary>A 0-based joined-source line back to its cell and 0-based line within it.</summary>
        public bool ToCellLine(int globalLine, out int cellIndex, out int lineInCell)
        {
            cellIndex = -1;
            lineInCell = 0;
            foreach (CellSpan span in _spans)
            {
                if (globalLine < span.StartLine || globalLine >= span.StartLine + span.LineCount) continue;
                cellIndex = span.CellIndex;
                lineInCell = globalLine - span.StartLine;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The source of every code cell BEFORE <paramref name="cellIndex"/>, joined — the context a
        /// completion engine needs to know what names exist by the time the user is typing in that
        /// cell. Returns "" for the first code cell.
        /// </summary>
        public static string PrecedingCode(NotebookDocument document, int cellIndex)
        {
            var builder = new StringBuilder();
            if (document == null) return string.Empty;
            for (int i = 0; i < cellIndex && i < document.Cells.Count; i++)
            {
                NotebookCell cell = document.Cells[i];
                if (cell.Kind != NotebookCellKind.Code) continue;
                string text = NotebookMagics.NeutraliseForParsing(cell.Source ?? string.Empty);
                builder.Append(text);
                if (!text.EndsWith("\n", StringComparison.Ordinal)) builder.Append('\n');
            }
            return builder.ToString();
        }

        /// <summary>
        /// Lines occupied by a text that is known to end in "\n" — one per newline. Counting
        /// "lines of content" instead would give an empty cell zero, and a span of zero lines is a
        /// span nothing can map into.
        /// </summary>
        private static int CountNewLines(string text)
        {
            int lines = 0;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;
            return lines;
        }
    }

    /// <summary>
    /// IPython's non-Python lines. A notebook is full of them and none of them parse:
    /// <c>%matplotlib inline</c>, <c>%%time</c>, <c>!pip install pandas</c>, <c>?obj</c>.
    /// </summary>
    public static class NotebookMagics
    {
        /// <summary>A line magic (<c>%x</c>), cell magic (<c>%%x</c>), shell escape (<c>!x</c>) or help (<c>?x</c>).</summary>
        public static bool IsMagicLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= line.Length) return false;
            char c = line[i];
            if (c == '%' || c == '!' || c == '?') return true;
            // Trailing `?`/`??` on an expression is IPython's help, but only when it is the whole
            // line — `x = f(a?b:c)` is not Python either way, and `d = {"a?": 1}` is.
            string trimmed = line.TrimEnd();
            return trimmed.Length > 1 && trimmed.EndsWith("?", StringComparison.Ordinal)
                   && trimmed.IndexOf('"') < 0 && trimmed.IndexOf('\'') < 0;
        }

        /// <summary>True when the cell's FIRST line is a cell magic, which changes what the whole cell is.</summary>
        public static bool IsCellMagic(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            string first = source.Split('\n')[0].TrimStart();
            return first.StartsWith("%%", StringComparison.Ordinal);
        }

        /// <summary>
        /// Replace every magic line with a `#` comment of exactly the same length, so a Python
        /// parser can read the cell while every character offset and line number stays where it was.
        /// A cell whose FIRST line is a cell magic is neutralised entirely: `%%bash` means the rest
        /// of that cell is not Python at all.
        /// </summary>
        public static string NeutraliseForParsing(string source)
        {
            if (string.IsNullOrEmpty(source)) return string.Empty;
            if (source.IndexOf('%') < 0 && source.IndexOf('!') < 0 && source.IndexOf('?') < 0) return source;

            string[] lines = source.Split('\n');
            bool wholeCell = IsCellMagic(source);
            var builder = new StringBuilder(source.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) builder.Append('\n');
                string line = lines[i];
                bool comment = wholeCell || IsMagicLine(line);
                builder.Append(comment ? CommentOfSameLength(line) : line);
            }
            return builder.ToString();
        }

        private static string CommentOfSameLength(string line)
        {
            // Keep the leading whitespace: a magic inside an indented block must stay inside it, or
            // the parser sees a dedent that is not there.
            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;

            var builder = new StringBuilder(line.Length);
            builder.Append(line, 0, indent);
            for (int i = indent; i < line.Length; i++)
            {
                char c = line[i];
                // A "\r" at the end of a CRLF line must survive as itself.
                builder.Append(c == '\r' ? '\r' : (i == indent ? '#' : ' '));
            }
            return builder.ToString();
        }
    }
}
