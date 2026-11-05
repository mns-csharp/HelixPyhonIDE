using System;
using System.Text;
using HelixPythonNamespace.Notebooks;

namespace HelixPythonNamespace.Editor.Notebook
{
    /// <summary>Which half of a cell a copy command asks for.</summary>
    public enum NotebookCopyPart
    {
        /// <summary>The code (or markdown) the user wrote, with no trace of what running it produced.</summary>
        Source,

        /// <summary>Only what running it produced — stdout, stderr, the repr, the traceback.</summary>
        Output,

        /// <summary>Both, source first, separated by a blank line.</summary>
        SourceAndOutput,
    }

    /// <summary>
    /// Turns one cell into the text that goes on the Windows clipboard.
    ///
    /// <para>
    /// A cell is two things stacked in one card, and the three reasons to copy one are different
    /// enough that a single "copy the cell" command serves none of them: the source alone is what
    /// you paste into a file or a review, the output alone is what you paste into a bug report or
    /// hand to somebody who cannot run it, and the two together are what you paste when the
    /// question is "why does this code do that". So there are three commands, not one with a
    /// setting.
    /// </para>
    ///
    /// <para>
    /// <b>The copy is NOT capped, and the painter's cap is not applied here.</b>
    /// <see cref="NotebookStyle.MaxOutputLines"/> exists because laying out forty thousand lines
    /// per scroll tick costs a second; it is a concession the DISPLAY makes, and the display says
    /// so on screen ("… 39,500 more lines not shown"). Carrying that cap into the clipboard would
    /// silently hand the user a truncated copy of their own output with no such note attached to
    /// it — the copy would be wrong in exactly the way the on-screen note exists to prevent.
    /// </para>
    ///
    /// <para>
    /// ANSI escapes are stripped, as they are on screen. Every traceback in the fixture corpus is
    /// full of them, and `[0;31m` pasted into an issue is noise the user then has to clean out by
    /// hand. Nothing else is rewritten: no prompt markers, no `Out[3]:` prefixes, no fenced code
    /// blocks. Text the user did not have is text they have to delete.
    /// </para>
    /// </summary>
    public static class NotebookCellClipboard
    {
        /// <summary>The clipboard text for one part of one cell — empty when there is nothing to copy.</summary>
        public static string TextFor(NotebookCell cell, NotebookCopyPart part)
        {
            switch (part)
            {
                case NotebookCopyPart.Output: return OutputText(cell);
                case NotebookCopyPart.SourceAndOutput: return SourceAndOutputText(cell);
                default: return SourceText(cell);
            }
        }

        /// <summary>The cell's own text, with the platform's line endings and no trailing blank line.</summary>
        public static string SourceText(NotebookCell cell)
        {
            if (cell == null) return string.Empty;
            return Normalise(TrimTrailingNewLines(cell.Source));
        }

        /// <summary>
        /// Every output this cell holds, in order, joined by a single newline.
        ///
        /// <para>
        /// An output with no plain text — a figure, a widget, a JavaScript bundle — contributes
        /// nothing rather than a placeholder describing it. The caller is told when that leaves
        /// the result empty, which is a different message from "this cell has never run".
        /// </para>
        /// </summary>
        public static string OutputText(NotebookCell cell)
        {
            if (cell == null || cell.Outputs == null || cell.Outputs.Count == 0) return string.Empty;

            var builder = new StringBuilder();
            foreach (NotebookOutput output in cell.Outputs)
            {
                if (output == null) continue;
                string text = TrimTrailingNewLines(AnsiText.Strip(output.PlainText()));
                if (text.Length == 0) continue;
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(text);
            }
            return Normalise(builder.ToString());
        }

        /// <summary>
        /// Source, a blank line, then output — and just one of them when the other is empty, so a
        /// cell that has never run does not copy with a blank line hanging off the bottom.
        /// </summary>
        public static string SourceAndOutputText(NotebookCell cell)
        {
            string source = SourceText(cell);
            string output = OutputText(cell);
            if (source.Length == 0) return output;
            if (output.Length == 0) return source;
            return source + Environment.NewLine + Environment.NewLine + output;
        }

        /// <summary>Lines in a clipboard string, for the "copied 7 lines" the status bar reports.</summary>
        public static int LineCount(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int lines = 1;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') lines++;
            return lines;
        }

        /// <summary>True when this cell has outputs but none of them carry any text.</summary>
        public static bool HasOnlyNonTextOutputs(NotebookCell cell)
        {
            if (cell == null || cell.Outputs == null || cell.Outputs.Count == 0) return false;
            return OutputText(cell).Length == 0;
        }

        private static string TrimTrailingNewLines(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : text.TrimEnd('\r', '\n');
        }

        /// <summary>
        /// The model stores "\n"; Notepad, an email client and half the world's text boxes want
        /// CRLF. Collapse whatever is there to "\n" first — a raw cell can carry CRLF already —
        /// so the expansion cannot produce "\r\r\n".
        /// </summary>
        private static string Normalise(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string unix = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return Environment.NewLine == "\n" ? unix : unix.Replace("\n", Environment.NewLine);
        }
    }
}
