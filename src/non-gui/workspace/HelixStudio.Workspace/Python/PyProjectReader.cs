using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Minimal TOML reader for pyproject.toml. Handles the subset we care about:
    /// [project] table with name/version/requires-python/dependencies,
    /// [tool.helixstudio] simple string keys. Not a full TOML spec parser.
    /// </summary>
    public static class PyProjectReader
    {
        public static PyProjectMetadata Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new FileNotFoundException(path);
            string[] lines = File.ReadAllLines(path);
            PyProjectMetadata meta = new PyProjectMetadata();
            string currentTable = string.Empty;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].TrimEnd();
                string line = StripComment(raw).Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentTable = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                if (currentTable == "project")
                {
                    if (key == "name") meta.Name = UnquoteString(value);
                    else if (key == "version") meta.Version = UnquoteString(value);
                    else if (key == "requires-python") meta.PythonVersion = NormalizePythonVersion(UnquoteString(value));
                    else if (key == "dependencies") meta.Dependencies = ReadArrayValue(lines, i, value);
                }
                else if (currentTable == "tool.helixstudio" || currentTable == "tool.HelixStudio")
                {
                    if (key == "excluded") meta.Excluded = ReadArrayValue(lines, i, value);
                    else if (key == "references") meta.References = ReadArrayValue(lines, i, value);
                    else if (key == "project-order") meta.ProjectOrder = ReadArrayValue(lines, i, value);
                    else meta.ToolHelixStudio[key] = UnquoteString(value);
                }
                else if (currentTable == "tool.uv.workspace")
                {
                    if (key == "members") meta.WorkspaceMembers = ReadArrayValue(lines, i, value);
                }
            }
            return meta;
        }

        private static string StripComment(string line)
        {
            // A '#' only starts a comment outside a string. Inside a BASIC string ("..."), a
            // backslash escapes the next character — without honoring that, an escaped quote
            // reads as the string's terminator and everything after it looks unquoted, so a
            // value like `run-args = "--label \"batch #7\""` was silently truncated at the '#'.
            // Literal strings ('...') process no escapes, so backslash is ordinary there.
            bool inQuote = false;
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuote)
                {
                    if (c == '\\' && quote == '"') { i++; continue; }   // skip the escaped char
                    if (c == quote) inQuote = false;
                }
                else if (c == '"' || c == '\'') { inQuote = true; quote = c; }
                else if (c == '#') return line.Substring(0, i);
            }
            return line;
        }

        private static string UnquoteString(string v)
        {
            if (string.IsNullOrEmpty(v)) return string.Empty;
            v = v.Trim();
            // Basic string ("...") processes escapes; literal string ('...') is raw.
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                return UnescapeTomlBasicString(v.Substring(1, v.Length - 2));
            if (v.Length >= 2 && v[0] == '\'' && v[v.Length - 1] == '\'')
                return v.Substring(1, v.Length - 2);
            return v;
        }

        /// <summary>
        /// Reverse of PyProjectWriter.EscapeTomlBasicString: turn TOML basic-string
        /// escapes back into their characters (\\ \" \b \t \n \f \r and \uXXXX / \UXXXXXXXX)
        /// so a value written with special chars round-trips exactly. Applies to
        /// double-quoted basic strings only; single-quoted literal strings stay raw.
        /// </summary>
        private static string UnescapeTomlBasicString(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'b': sb.Append('\b'); break;
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'u':
                        if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out int cp))
                        { sb.Append((char)cp); i += 4; }
                        else sb.Append(n);
                        break;
                    case 'U':
                        if (i + 8 < s.Length && int.TryParse(s.Substring(i + 1, 8),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out int cp8))
                        { sb.Append(char.ConvertFromUtf32(cp8)); i += 8; }
                        else sb.Append(n);
                        break;
                    default: sb.Append('\\').Append(n); break;
                }
            }
            return sb.ToString();
        }

        private static List<string> ReadArrayValue(string[] lines, int startIndex, string firstValue)
        {
            List<string> items = new List<string>();
            // Inline: dependencies = ["a", "b"]
            if (firstValue.StartsWith("[") && firstValue.EndsWith("]"))
            {
                ParseInlineArray(firstValue, items);
                return items;
            }
            // Multi-line array
            if (firstValue.StartsWith("["))
            {
                System.Text.StringBuilder acc = new System.Text.StringBuilder(firstValue);
                for (int j = startIndex + 1; j < lines.Length; j++)
                {
                    acc.Append(' ');
                    acc.Append(lines[j]);
                    if (lines[j].Contains("]")) break;
                }
                ParseInlineArray(acc.ToString(), items);
            }
            return items;
        }

        private static void ParseInlineArray(string s, List<string> target)
        {
            int open = s.IndexOf('[');
            int close = s.LastIndexOf(']');
            if (open < 0 || close < 0 || close <= open) return;
            string inner = s.Substring(open + 1, close - open - 1);
            foreach (Match m in Regex.Matches(inner, "\"([^\"]*)\"|'([^']*)'"))
            {
                string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(v)) target.Add(v);
            }
        }

        private static string NormalizePythonVersion(string requiresPython)
        {
            if (string.IsNullOrEmpty(requiresPython)) return "3.12";
            // ">=3.10" → "3.10", "^3.11" → "3.11", "3.12" → "3.12"
            Match m = Regex.Match(requiresPython, @"(\d+\.\d+)");
            return m.Success ? m.Value : "3.12";
        }
    }
}
