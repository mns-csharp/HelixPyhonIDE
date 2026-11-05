using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Structure
{
    /// <summary>
    /// Rewrites the path references a relocation invalidates, surgically.
    ///
    /// <b>Line-preserving by construction.</b> The content is never split and re-joined with
    /// <see cref="Environment.NewLine"/> — the whole-file, CRLF-ifying rewrite is the exact mistake
    /// <c>PyProjectStartupWriter</c> and <c>PyProjectRunArgsWriter</c> had to be fixed for. Where a
    /// line-by-line pass is unavoidable (the INI keys), the split is on <c>'\n'</c> only, so a
    /// trailing <c>'\r'</c> stays attached to its own line and CRLF survives byte-for-byte.
    ///
    /// It is also deliberately NOT a general search-and-replace. Only a WHOLE quoted string, or a
    /// whole whitespace-separated token on a named INI key, is eligible: replacing every occurrence
    /// of <c>src</c> in a manifest would corrupt a description, a URL and a dependency name on its
    /// way past the one array that needed changing.
    /// </summary>
    internal static class ManifestPathRewriter
    {
        /// <summary>Keys whose values are path lists, in INI-style files (pytest.ini, setup.cfg).</summary>
        private static readonly string[] PathKeys = { "testpaths", "pythonpath", "package_dir", "where", "packages" };

        /// <summary>
        /// Applies <paramref name="mappings"/> (old solution-relative path → new one) to a manifest.
        /// Returns true when the file changed.
        /// </summary>
        public static bool Rewrite(
            string filePath,
            IEnumerable<StructureProjectMove> mappings,
            IEnumerable<string> removedContainers)
        {
            if (!File.Exists(filePath)) return false;
            string original = File.ReadAllText(filePath);
            string updated = original;

            bool ini = filePath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                    || filePath.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);

            foreach (StructureProjectMove move in mappings)
            {
                if (move == null) continue;
                string from = StructurePath.Normalize(move.OldRelativePath);
                string to = StructurePath.Normalize(move.NewRelativePath);
                // ORDINAL. StructurePath.AreSame is case-insensitive on Windows, so it answers
                // "same directory?" — true for a case-only rename — and skipping on it drops
                // exactly the mapping a recase needs. The manifest then keeps the old spelling, the
                // loader rebuilds the project at the old path, and stage 6 rolls the whole repair
                // back reporting a Case defect the executor had in fact already fixed on disk.
                if (from.Length == 0 || string.Equals(from, to, StringComparison.Ordinal)) continue;
                updated = ReplaceQuoted(updated, from, to);
                if (ini) updated = ReplaceIniToken(updated, from, to);
            }

            // A container this repair emptied cannot stay in a discovery path. `where = ["src"]`
            // with no `src` left finds nothing, and setuptools reports that as an empty
            // distribution rather than as an error — a silent packaging failure.
            foreach (string container in removedContainers)
            {
                string c = StructurePath.Normalize(container);
                if (c.Length == 0) continue;
                updated = ReplaceQuoted(updated, c, ".");
                if (ini) updated = ReplaceIniToken(updated, c, ".");
            }

            if (string.Equals(updated, original, StringComparison.Ordinal)) return false;
            File.WriteAllText(filePath, updated);
            return true;
        }

        /// <summary>
        /// Replaces <c>"old"</c> / <c>'old'</c> with the same quote style around <c>new</c>, only
        /// when the quoted string is exactly the old path. Anything longer, shorter or merely
        /// containing it is left alone.
        /// </summary>
        private static string ReplaceQuoted(string content, string from, string to)
        {
            content = content.Replace("\"" + from + "\"", "\"" + to + "\"");
            content = content.Replace("'" + from + "'", "'" + to + "'");
            // The same path written with a trailing slash is the same path.
            content = content.Replace("\"" + from + "/\"", "\"" + to + "/\"");
            content = content.Replace("'" + from + "/'", "'" + to + "/'");
            return content;
        }

        /// <summary>
        /// Replaces a bare token in the value of one of <see cref="PathKeys"/>. INI values are
        /// unquoted, so the quoted form above never fires on <c>testpaths = tests src/bugmap</c>.
        /// </summary>
        private static string ReplaceIniToken(string content, string from, string to)
        {
            string[] lines = content.Split('\n');   // '\r' stays on the end of each line
            bool changed = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();

                bool isPathKey = false;
                foreach (string k in PathKeys)
                    if (string.Equals(key, k, StringComparison.OrdinalIgnoreCase)) { isPathKey = true; break; }
                if (!isPathKey) continue;

                string value = line.Substring(eq + 1);
                string rebuilt = ReplaceWholeTokens(value, from, to);
                if (!string.Equals(rebuilt, value, StringComparison.Ordinal))
                {
                    lines[i] = line.Substring(0, eq + 1) + rebuilt;
                    changed = true;
                }
            }
            return changed ? string.Join("\n", lines) : content;
        }

        /// <summary>Whitespace-delimited whole-token replacement, preserving the original spacing.</summary>
        private static string ReplaceWholeTokens(string value, string from, string to)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < value.Length)
            {
                if (char.IsWhiteSpace(value[i])) { sb.Append(value[i]); i++; continue; }
                int start = i;
                while (i < value.Length && !char.IsWhiteSpace(value[i])) i++;
                string token = value.Substring(start, i - start);
                sb.Append(StructurePath.AreSame(token, from) ? to : token);
            }
            return sb.ToString();
        }
    }
}
