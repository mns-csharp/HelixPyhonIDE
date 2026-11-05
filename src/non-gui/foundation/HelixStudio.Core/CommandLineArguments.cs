using System;
using System.Collections.Generic;
using System.Text;

namespace HelixPythonNamespace.Core
{
    /// <summary>
    /// Converts between a command-line string and an argument vector, following the Windows CRT
    /// rules that <c>CommandLineToArgvW</c> — and therefore Python's own <c>sys.argv</c> — apply:
    /// double quotes group, a run of backslashes is literal unless it precedes a quote, and
    /// <c>""</c> inside a quoted run produces a literal quote.
    ///
    /// This exists because a run-arguments feature is only as good as its quoting. A user's
    /// arguments are usually file paths, and on Windows those routinely contain spaces —
    /// <c>--out "C:\My Data\rows.tsv"</c> must arrive as ONE argv entry, not three.
    /// </summary>
    public static class CommandLineArguments
    {
        /// <summary>
        /// Splits a command-line string into individual arguments. Returns an empty array for
        /// null/blank input. Unbalanced quotes are tolerated (the trailing run becomes one
        /// argument) rather than throwing — this parses user input typed into a text box.
        /// </summary>
        public static string[] Split(string commandLine)
        {
            List<string> args = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine)) return args.ToArray();

            StringBuilder current = new StringBuilder();
            bool inQuotes = false;
            bool started = false;      // distinguishes `a "" b` (3 args) from plain whitespace
            int backslashes = 0;

            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];

                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    // 2n backslashes then a quote: n literal backslashes, quote is a delimiter.
                    // 2n+1: n literal backslashes and a literal quote.
                    current.Append('\\', backslashes / 2);
                    bool literalQuote = (backslashes % 2) == 1;
                    backslashes = 0;
                    started = true;
                    if (literalQuote) current.Append('"');
                    else if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                    {
                        current.Append('"');   // "" inside a quoted run → one literal quote
                        i++;
                    }
                    else inQuotes = !inQuotes;
                    continue;
                }

                current.Append('\\', backslashes);
                if (backslashes > 0) started = true;
                backslashes = 0;

                if (!inQuotes && (c == ' ' || c == '\t'))
                {
                    if (started) { args.Add(current.ToString()); current.Length = 0; started = false; }
                    continue;
                }

                current.Append(c);
                started = true;
            }

            current.Append('\\', backslashes);
            if (backslashes > 0) started = true;
            if (started) args.Add(current.ToString());
            return args.ToArray();
        }

        /// <summary>
        /// Quotes one argument so <see cref="Split"/> — and the Windows CRT — recover it exactly.
        /// Arguments with no whitespace, quote, or trailing backslash are returned unchanged so
        /// ordinary command lines stay readable in the Output pane.
        /// </summary>
        public static string Quote(string argument)
        {
            if (argument == null) return "\"\"";
            if (argument.Length == 0) return "\"\"";
            if (argument.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\r' }) < 0
                && !argument.EndsWith("\\", StringComparison.Ordinal))
                return argument;

            StringBuilder sb = new StringBuilder(argument.Length + 8);
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    // Double the backslash run, then escape the quote itself.
                    sb.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    continue;
                }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            // A trailing run must be doubled so it does not escape the closing quote.
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }

        /// <summary>Joins an argument vector into a correctly quoted command-line string.</summary>
        public static string Join(IEnumerable<string> arguments)
        {
            if (arguments == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            foreach (string a in arguments)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }
    }
}
