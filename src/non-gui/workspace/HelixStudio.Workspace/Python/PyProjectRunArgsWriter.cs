using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Surgically sets or clears <c>run-args</c> under <c>[tool.helixstudio]</c> in a
    /// pyproject.toml, preserving every other line, comment, table, and formatting — the same
    /// line-preserving contract as <see cref="PyProjectStartupWriter"/>, and for the same reason:
    /// <see cref="PyProjectWriter.Update"/> rewrites the whole file and would drop
    /// <c>[project.scripts]</c>, <c>[build-system]</c>, <c>[tool.uv.workspace]</c> and comments.
    ///
    /// The value is the command line the IDE appends after the entry file when running or
    /// debugging the package (Visual Studio's "Command line arguments"). It lives in the manifest
    /// rather than in per-developer IDE state so it is committed and shared with the team.
    /// </summary>
    public static class PyProjectRunArgsWriter
    {
        private const string ToolHeader = "[tool.helixstudio]";
        private const string Key = "run-args";

        /// <summary>
        /// Writes <c>run-args = "…"</c>, or removes the line when <paramref name="runArgs"/> is
        /// null/blank. Adds a <c>[tool.helixstudio]</c> table if one is needed and none exists.
        /// Returns false only when the file does not exist.
        /// </summary>
        public static bool SetRunArgs(string pyprojectPath, string runArgs)
        {
            if (string.IsNullOrEmpty(pyprojectPath) || !File.Exists(pyprojectPath)) return false;

            // Raw text, not ReadAllLines, so the file's own line ending survives the rewrite —
            // see the note in PyProjectStartupWriter.SetStartup.
            string original = File.ReadAllText(pyprojectPath);
            string eol = original.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = new List<string>(SplitLines(original));
            string currentTable = string.Empty;
            int toolSectionHeaderIndex = -1;
            int toolSectionLastContentIndex = -1;   // last non-blank line still inside the tool table
            int existingIndex = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    currentTable = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    if (IsToolTable(currentTable)) toolSectionHeaderIndex = i;
                    continue;
                }
                if (IsToolTable(currentTable))
                {
                    if (trimmed.Length > 0) toolSectionLastContentIndex = i;
                    if (KeyOf(trimmed) == Key) existingIndex = i;
                }
            }

            bool clearing = string.IsNullOrWhiteSpace(runArgs);
            if (clearing)
            {
                if (existingIndex < 0) return true;   // nothing to clear
                lines.RemoveAt(existingIndex);
            }
            else
            {
                string line = Key + " = " + EncodeTomlString(runArgs.Trim());
                if (existingIndex >= 0)
                {
                    lines[existingIndex] = line;
                }
                else if (toolSectionHeaderIndex >= 0)
                {
                    int insertAt = (toolSectionLastContentIndex >= 0
                        ? toolSectionLastContentIndex
                        : toolSectionHeaderIndex) + 1;
                    lines.Insert(insertAt, line);
                }
                else
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length != 0) lines.Add(string.Empty);
                    lines.Add(ToolHeader);
                    lines.Add(line);
                }
            }

            File.WriteAllText(pyprojectPath, string.Join(eol, lines) + eol);
            return true;
        }

        /// <summary>Line split that preserves nothing but the content — see the twin in
        /// <see cref="PyProjectStartupWriter"/>; the file's EOL is captured separately.</summary>
        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            string[] parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (parts.Length > 0 && parts[parts.Length - 1].Length == 0)
            {
                var trimmed = new string[parts.Length - 1];
                Array.Copy(parts, trimmed, trimmed.Length);
                return trimmed;
            }
            return parts;
        }

        /// <summary>
        /// A TOML basic string, escaped by the same helper <see cref="PyProjectWriter"/> uses so
        /// the value round-trips exactly through <c>PyProjectReader.UnescapeTomlBasicString</c>.
        /// This matters more here than for most keys: run arguments routinely contain Windows
        /// paths and embedded quotes (<c>--out "C:\data\rows.tsv"</c>), and an unescaped backslash
        /// either breaks the parse or silently turns <c>\d</c> into an escape sequence.
        /// </summary>
        private static string EncodeTomlString(string value)
        {
            return "\"" + PyProjectWriter.EscapeTomlBasicString(value) + "\"";
        }

        private static bool IsToolTable(string table)
        {
            return string.Equals(table, "tool.helixstudio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(table, "tool.HelixStudio", StringComparison.OrdinalIgnoreCase);
        }

        // The bare key of a `key = value` line (before '='), or null when the line is not an assignment.
        private static string KeyOf(string trimmedLine)
        {
            if (trimmedLine.Length == 0 || trimmedLine[0] == '#') return null;
            int eq = trimmedLine.IndexOf('=');
            if (eq <= 0) return null;
            return trimmedLine.Substring(0, eq).Trim();
        }
    }
}
