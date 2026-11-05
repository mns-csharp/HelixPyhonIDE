using System;
using System.Collections.Generic;
using System.IO;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Surgically sets or clears the <c>startup</c> flag under <c>[tool.helixstudio]</c> in a
    /// pyproject.toml, preserving every other line, comment, table, and formatting (unlike
    /// <see cref="PyProjectWriter.Update"/>, which rewrites the whole file and would drop
    /// <c>[project.scripts]</c>, <c>[build-system]</c>, descriptions, etc.). This is the persistence
    /// path for the startup-package selection (docs/python-solution-model.md, PSM-10).
    /// </summary>
    public static class PyProjectStartupWriter
    {
        private const string ToolHeader = "[tool.helixstudio]";

        /// <summary>
        /// Writes <c>startup = true</c> when <paramref name="isStartup"/> is true; removes the
        /// <c>startup</c> line otherwise. Adds a <c>[tool.helixstudio]</c> table if one is needed and
        /// none exists. No-op (returns false) when the file does not exist.
        /// </summary>
        public static bool SetStartup(string pyprojectPath, bool isStartup)
        {
            if (string.IsNullOrEmpty(pyprojectPath) || !File.Exists(pyprojectPath)) return false;

            // Read the raw text, not ReadAllLines, so the file's OWN line ending survives the
            // rewrite. Joining with Environment.NewLine would convert an LF-authored pyproject.toml
            // to CRLF on every startup toggle — invisible to Python and TOML, but it turns a
            // one-line change into a whole-file diff for anyone sharing the repo with Linux or
            // macOS. Same EOL sniff the line-preserving writers already use
            // (PyprojectDependencyService, PythonOopConverter, CMakeReferenceWriter).
            string original = File.ReadAllText(pyprojectPath);
            string eol = original.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = new List<string>(SplitLines(original));
            string currentTable = string.Empty;
            int toolSectionHeaderIndex = -1;
            int toolSectionLastContentIndex = -1;   // last non-blank line still inside the tool table
            int existingStartupIndex = -1;

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
                    string key = KeyOf(trimmed);
                    if (key == "startup") existingStartupIndex = i;
                }
            }

            if (isStartup)
            {
                if (existingStartupIndex >= 0)
                {
                    lines[existingStartupIndex] = "startup = true";
                }
                else if (toolSectionHeaderIndex >= 0)
                {
                    int insertAt = (toolSectionLastContentIndex >= 0 ? toolSectionLastContentIndex : toolSectionHeaderIndex) + 1;
                    lines.Insert(insertAt, "startup = true");
                }
                else
                {
                    if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length != 0) lines.Add(string.Empty);
                    lines.Add(ToolHeader);
                    lines.Add("startup = true");
                }
            }
            else
            {
                if (existingStartupIndex >= 0) lines.RemoveAt(existingStartupIndex);
                else return true; // nothing to clear — already not a startup
            }

            File.WriteAllText(pyprojectPath, string.Join(eol, lines) + eol);
            return true;
        }

        /// <summary>
        /// Splits on the three line terminators <see cref="File.ReadAllLines"/> recognizes, without
        /// losing the information about which one the file used. A trailing terminator yields a
        /// final empty entry, which is dropped so the writer's own trailing EOL is not doubled.
        /// </summary>
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
