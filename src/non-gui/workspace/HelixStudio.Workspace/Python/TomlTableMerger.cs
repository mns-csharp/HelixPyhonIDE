using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Line-preserving edits to an existing TOML file. The merger reads the file,
    /// finds or inserts a named table, and rewrites only the lines belonging to
    /// that table — everything outside the affected range (comments, blank lines,
    /// unknown tables) survives byte-for-byte.
    ///
    /// The TOML subset supported is the same one <see cref="PyProjectReader"/>
    /// understands: <c>[table.name]</c> headers and <c>key = value</c> lines.
    /// No inline tables, no array-of-tables.
    /// </summary>
    public static class TomlTableMerger
    {
        /// <summary>
        /// Ensure <paramref name="tableName"/> exists in <paramref name="filePath"/>
        /// and that every key in <paramref name="desiredKeys"/> is set to the given
        /// value. Keys already present in the table but not in
        /// <paramref name="desiredKeys"/> are preserved. The file is created if
        /// absent.
        /// </summary>
        /// <param name="filePath">Absolute path to the TOML file.</param>
        /// <param name="tableName">The table name without brackets, e.g. <c>tool.uv.workspace</c>.</param>
        /// <param name="desiredKeys">Key → raw-value map. Values are written verbatim after <c> = </c>.
        /// Callers are responsible for quoting strings and formatting arrays.</param>
        public static void EnsureTable(string filePath, string tableName, IReadOnlyDictionary<string, string> desiredKeys)
        {
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("filePath");
            if (string.IsNullOrEmpty(tableName)) throw new ArgumentException("tableName");

            List<string> lines = File.Exists(filePath)
                ? new List<string>(File.ReadAllLines(filePath))
                : new List<string>();

            int header = FindTableHeader(lines, tableName);
            int end;
            if (header < 0)
            {
                // Append a fresh table.
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                    lines.Add(string.Empty);
                header = lines.Count;
                lines.Add("[" + tableName + "]");
                end = lines.Count;
            }
            else
            {
                end = FindNextTableHeader(lines, header + 1);
            }

            HashSet<string> remaining = new HashSet<string>(desiredKeys.Keys, StringComparer.Ordinal);
            for (int i = header + 1; i < end; i++)
            {
                string key = ExtractKey(lines[i]);
                if (key != null && remaining.Contains(key))
                {
                    lines[i] = key + " = " + desiredKeys[key];
                    remaining.Remove(key);
                }
            }

            // Append the remaining keys just before the next header (or EOF).
            int insertAt = end;
            // Trim trailing blank line immediately before the next header, if any,
            // so inserted keys stay grouped with their table.
            while (insertAt - 1 > header && string.IsNullOrWhiteSpace(lines[insertAt - 1])) insertAt--;
            foreach (KeyValuePair<string, string> kv in desiredKeys)
            {
                if (!remaining.Contains(kv.Key)) continue;
                lines.Insert(insertAt, kv.Key + " = " + kv.Value);
                insertAt++;
            }

            File.WriteAllText(filePath, Join(lines));
        }

        private static int FindTableHeader(List<string> lines, string tableName)
        {
            string expected = "[" + tableName + "]";
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (string.Equals(trimmed, expected, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private static int FindNextTableHeader(List<string> lines, int startIndex)
        {
            for (int i = startIndex; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]")) return i;
            }
            return lines.Count;
        }

        private static string ExtractKey(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) return null;
            if (trimmed.StartsWith("#")) return null;
            if (trimmed.StartsWith("[")) return null;
            int eq = trimmed.IndexOf('=');
            if (eq <= 0) return null;
            return trimmed.Substring(0, eq).Trim();
        }

        private static string Join(List<string> lines)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append(lines[i]);
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
