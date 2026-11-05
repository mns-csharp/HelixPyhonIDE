using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Core.Diagnostics
{
    /// <summary>One located line from a Python error report — a traceback frame or a syntax error.</summary>
    public sealed class PythonErrorLocation
    {
        /// <summary>Absolute path as the interpreter printed it.</summary>
        public string File { get; set; }
        public int Line { get; set; }
        /// <summary>1-based column from the caret line, or 0 when the interpreter printed none.</summary>
        public int Column { get; set; }
        /// <summary>Enclosing function (<c>&lt;module&gt;</c> at top level); null for a syntax error.</summary>
        public string Function { get; set; }
        /// <summary>The source text the interpreter echoed under the frame, if any.</summary>
        public string SourceLine { get; set; }
        /// <summary>
        /// True for the frame the error actually occurred in — the LAST frame of a traceback, or
        /// the single location of a syntax error. This is the line the user wants to land on.
        /// </summary>
        public bool IsInnermost { get; set; }
    }

    /// <summary>A parsed Python failure: where it happened, and what it was.</summary>
    public sealed class PythonErrorReport
    {
        /// <summary>Frames outermost → innermost, exactly as the interpreter printed them.</summary>
        public List<PythonErrorLocation> Frames { get; } = new List<PythonErrorLocation>();
        /// <summary>Exception class name, e.g. <c>FileNotFoundError</c> / <c>SyntaxError</c>.</summary>
        public string ExceptionType { get; set; }
        /// <summary>Text after the colon on the final line; empty when the exception carried none.</summary>
        public string ExceptionMessage { get; set; }
        /// <summary>True when this came from a <c>SyntaxError</c>-family report (no stack).</summary>
        public bool IsSyntaxError { get; set; }

        public bool HasLocation { get { return Frames.Count > 0; } }

        /// <summary>The frame the error occurred in — where a double-click should land.</summary>
        public PythonErrorLocation Innermost
        {
            get { return Frames.Count == 0 ? null : Frames[Frames.Count - 1]; }
        }

        /// <summary>Exception type and message rendered the way the interpreter prints them.</summary>
        public string Headline
        {
            get
            {
                if (string.IsNullOrEmpty(ExceptionType)) return ExceptionMessage ?? string.Empty;
                return string.IsNullOrEmpty(ExceptionMessage)
                    ? ExceptionType
                    : ExceptionType + ": " + ExceptionMessage;
            }
        }
    }

    /// <summary>
    /// Turns CPython's error output into located diagnostics, so a failed run can be clicked
    /// through to the offending line instead of being read as flat text.
    ///
    /// Two shapes, both verified against CPython 3.11 rather than reconstructed from memory:
    ///
    /// <code>
    /// Traceback (most recent call last):
    ///   File "…\run.py", line 7, in &lt;module&gt;
    ///     main()
    ///   File "…\run.py", line 2, in inner
    ///     return open(p)
    ///            ^^^^^^^
    /// FileNotFoundError: [Errno 2] No such file or directory: 'nope.txt'
    /// </code>
    ///
    /// <code>
    ///   File "…\syn.py", line 2
    ///     111abc
    ///       ^
    /// SyntaxError: invalid decimal literal
    /// </code>
    ///
    /// A syntax error has no <c>Traceback</c> header, no <c>, in &lt;fn&gt;</c> suffix, and exactly
    /// one location — so frame parsing alone would miss it entirely, which is the case that
    /// prompted this. Both are recognized by the same frame regex; the trailing
    /// <c>Type: message</c> line closes the report.
    ///
    /// The parser is deliberately tolerant: interleaved program stdout, chained exceptions
    /// ("During handling of the above exception…"), and <c>~~~^^^</c> fine-grained error markers
    /// all appear in real output and must not derail it.
    /// </summary>
    public static class PythonTracebackParser
    {
        // `  File "<path>", line <n>` with an optional `, in <function>` — one regex covers both
        // a traceback frame and a syntax-error location.
        private static readonly Regex FrameLine = new Regex(
            "^\\s*File \"(?<path>.+?)\", line (?<line>\\d+)(?:, in (?<fn>.*))?\\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // The terminating `ExceptionType: message` (or a bare `ExceptionType`). Dotted names occur
        // for non-builtin exceptions (`json.decoder.JSONDecodeError`).
        private static readonly Regex ExceptionLine = new Regex(
            "^(?<type>[A-Za-z_][A-Za-z0-9_.]*(?:Error|Exception|Exit|Interrupt|Warning|StopIteration|KeyboardInterrupt))(?::\\s?(?<msg>.*))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses the LAST error report in <paramref name="output"/>. Returns null when the output
        /// contains none — the normal case for a program that failed for its own reasons (a
        /// non-zero exit with no traceback), where inventing a location would be worse than none.
        /// </summary>
        public static PythonErrorReport Parse(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            string[] lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            PythonErrorReport current = null;
            PythonErrorReport completed = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                Match frame = FrameLine.Match(line);
                if (frame.Success)
                {
                    // A frame after a completed report starts a new one (chained exceptions).
                    if (current == null) { current = new PythonErrorReport(); }

                    int lineNo;
                    if (!int.TryParse(frame.Groups["line"].Value, out lineNo)) continue;

                    var loc = new PythonErrorLocation
                    {
                        File = frame.Groups["path"].Value,
                        Line = lineNo,
                        Function = frame.Groups["fn"].Success ? frame.Groups["fn"].Value.Trim() : null,
                    };

                    // The interpreter echoes the source line, and since 3.11 may follow it with a
                    // marker row (`^^^` / `~~~^^^`) that pins the exact column.
                    if (i + 1 < lines.Length && IsIndentedSource(lines[i + 1]))
                    {
                        loc.SourceLine = lines[i + 1].Trim();
                        if (i + 2 < lines.Length)
                        {
                            int col = CaretColumn(lines[i + 2], lines[i + 1]);
                            if (col > 0) loc.Column = col;
                        }
                    }
                    current.Frames.Add(loc);
                    continue;
                }

                if (current == null || current.Frames.Count == 0) continue;

                // Indented continuation (source echo, caret row) — already consumed above.
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) continue;
                if (line.Trim().Length == 0) continue;

                Match ex = ExceptionLine.Match(line.Trim());
                if (ex.Success)
                {
                    current.ExceptionType = ex.Groups["type"].Value;
                    current.ExceptionMessage = ex.Groups["msg"].Success ? ex.Groups["msg"].Value : string.Empty;
                    current.IsSyntaxError = current.ExceptionType.EndsWith("SyntaxError", StringComparison.Ordinal)
                        || current.ExceptionType == "IndentationError"
                        || current.ExceptionType == "TabError";
                    if (current.Frames.Count > 0)
                        current.Frames[current.Frames.Count - 1].IsInnermost = true;
                    completed = current;
                    current = null;
                }
            }

            // A report whose exception line never arrived (truncated output) is still useful:
            // the frames alone are navigable.
            if (completed == null && current != null && current.Frames.Count > 0)
            {
                current.Frames[current.Frames.Count - 1].IsInnermost = true;
                completed = current;
            }
            return completed;
        }

        // The echoed source line under a frame: indented, and not itself a caret/marker row.
        private static bool IsIndentedSource(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            if (line[0] != ' ' && line[0] != '\t') return false;
            string t = line.Trim();
            return t.Length > 0 && !IsMarkerRow(t);
        }

        // `^`, `^^^^`, `~~~^^^` — CPython's fine-grained error range markers.
        private static bool IsMarkerRow(string trimmed)
        {
            if (trimmed.Length == 0) return false;
            foreach (char c in trimmed)
                if (c != '^' && c != '~') return false;
            return true;
        }

        /// <summary>
        /// 1-based column of the first <c>^</c>, translated from the marker row's indentation into
        /// the real source line's. The interpreter re-indents its echo, so the caret's raw offset
        /// is not the column in the file — the echo's own leading whitespace has to be subtracted
        /// and the file's added back.
        /// </summary>
        private static int CaretColumn(string markerRow, string echoedSourceRow)
        {
            if (string.IsNullOrEmpty(markerRow) || !IsMarkerRow(markerRow.Trim())) return 0;
            int caret = markerRow.IndexOf('^');
            if (caret < 0) caret = markerRow.IndexOf('~');
            if (caret < 0) return 0;

            int echoIndent = 0;
            while (echoIndent < echoedSourceRow.Length &&
                   (echoedSourceRow[echoIndent] == ' ' || echoedSourceRow[echoIndent] == '\t')) echoIndent++;

            int offsetInStatement = caret - echoIndent;
            return offsetInStatement < 0 ? 0 : offsetInStatement + 1;
        }
    }
}
