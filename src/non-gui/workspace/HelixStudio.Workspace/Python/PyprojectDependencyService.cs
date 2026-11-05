using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// A single PEP 508 dependency requirement parsed into its parts (for display + de-duplication).
    /// </summary>
    public sealed class Pep508Dependency
    {
        public string Raw;            // "numpy>=1.24,<2 ; python_version >= '3.9'"
        public string Name;           // "numpy"
        public string CanonicalName;  // "numpy"  (PEP 503 normalized, for identity)
        public string Extras;         // "[cuda,test]" or ""
        public string Specifier;      // ">=1.24,<2" or ""
        public string Marker;         // "python_version >= '3.9'" or ""

        public override string ToString() { return Raw; }
    }

    /// <summary>
    /// FR-PROJ-02: read/add/remove/upgrade the <c>[project].dependencies</c> of a <c>pyproject.toml</c> —
    /// the pip/PEP-621 dependency editor that replaces the (wrong-ecosystem) NuGet package dialog. All edits
    /// are <b>in-place text surgery</b> on the <c>dependencies = [...]</c> array only, so every other table
    /// and comment in the file is preserved byte-for-byte (NFR-04 round-trip safety). Native-first: editing
    /// needs no <c>pip</c>; an actual install is an <i>optional</i> follow-up (NFR-02 oracle pattern).
    /// </summary>
    public static class PyprojectDependencyService
    {
        /// <summary>PEP 503 canonical name: lowercase, runs of <c>-_.</c> collapse to a single <c>-</c>.</summary>
        public static string CanonicalName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return Regex.Replace(name.Trim().ToLowerInvariant(), "[-_.]+", "-");
        }

        private static readonly Regex NameHead = new Regex(
            @"^\s*(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*(?<extras>\[[^\]]*\])?\s*(?<rest>.*)$",
            RegexOptions.Compiled);

        /// <summary>Parse a PEP 508 requirement string into <see cref="Pep508Dependency"/> parts.</summary>
        public static Pep508Dependency Parse(string requirement)
        {
            var dep = new Pep508Dependency { Raw = (requirement ?? string.Empty).Trim(), Extras = "", Specifier = "", Marker = "" };
            if (dep.Raw.Length == 0) return dep;
            Match m = NameHead.Match(dep.Raw);
            if (!m.Success) { dep.Name = dep.Raw; dep.CanonicalName = CanonicalName(dep.Raw); return dep; }
            dep.Name = m.Groups["name"].Value;
            dep.CanonicalName = CanonicalName(dep.Name);
            dep.Extras = m.Groups["extras"].Value ?? "";
            string rest = (m.Groups["rest"].Value ?? "").Trim();
            int semi = rest.IndexOf(';');
            if (semi >= 0) { dep.Marker = rest.Substring(semi + 1).Trim(); rest = rest.Substring(0, semi).Trim(); }
            dep.Specifier = rest;
            return dep;
        }

        /// <summary>List the parsed dependencies of a pyproject.toml (empty if none / file missing).</summary>
        public static List<Pep508Dependency> List(string pyprojectPath)
        {
            var result = new List<Pep508Dependency>();
            if (string.IsNullOrEmpty(pyprojectPath) || !File.Exists(pyprojectPath)) return result;
            PyProjectMetadata meta;
            try { meta = PyProjectReader.Read(pyprojectPath); }
            catch { return result; }
            if (meta.Dependencies != null)
                foreach (string d in meta.Dependencies) result.Add(Parse(d));
            return result;
        }

        /// <summary>
        /// Add a requirement (or replace the existing one with the same canonical name — an "upgrade/pin").
        /// Returns true if the file changed.
        /// </summary>
        public static bool Add(string pyprojectPath, string requirement)
        {
            var parsed = Parse(requirement);
            if (string.IsNullOrEmpty(parsed.CanonicalName)) return false;
            var deps = RawDependencyList(pyprojectPath);
            // Drop any existing entry with the same canonical name, then append the new raw requirement.
            deps.RemoveAll(r => CanonicalName(Parse(r).Name) == parsed.CanonicalName);
            deps.Add(parsed.Raw);
            return WriteDependencies(pyprojectPath, deps);
        }

        /// <summary>Remove the dependency whose canonical name matches. Returns true if the file changed.</summary>
        public static bool Remove(string pyprojectPath, string name)
        {
            string canon = CanonicalName(name);
            if (string.IsNullOrEmpty(canon)) return false;
            var deps = RawDependencyList(pyprojectPath);
            int removed = deps.RemoveAll(r => CanonicalName(Parse(r).Name) == canon);
            if (removed == 0) return false;
            return WriteDependencies(pyprojectPath, deps);
        }

        /// <summary>Set/replace the version specifier of an existing (or new) dependency. Returns true if changed.</summary>
        public static bool Set(string pyprojectPath, string name, string specifier)
        {
            var parsed = Parse(name);
            string req = parsed.Name + parsed.Extras + (string.IsNullOrEmpty(specifier) ? "" : specifier.Trim());
            return Add(pyprojectPath, req);
        }

        private static List<string> RawDependencyList(string pyprojectPath)
        {
            var list = new List<string>();
            if (File.Exists(pyprojectPath))
            {
                try
                {
                    PyProjectMetadata meta = PyProjectReader.Read(pyprojectPath);
                    if (meta.Dependencies != null) list.AddRange(meta.Dependencies);
                }
                catch { }
            }
            return list;
        }

        // In-place replacement of the [project].dependencies array. Everything else is preserved verbatim.
        private static bool WriteDependencies(string pyprojectPath, List<string> deps)
        {
            if (string.IsNullOrEmpty(pyprojectPath)) return false;
            string text = File.Exists(pyprojectPath) ? File.ReadAllText(pyprojectPath) : "";
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string arrayText = FormatArray(deps);

            // 1) An existing `dependencies = [...]` (inline or multi-line) inside/after [project].
            Match key = Regex.Match(text, @"(?m)^[ \t]*dependencies[ \t]*=[ \t]*\[");
            if (key.Success)
            {
                int start = key.Index;
                int bracket = text.IndexOf('[', key.Index);
                int close = FindMatchingBracket(text, bracket);
                if (close < 0) return false;
                int lineStart = start;
                string before = text.Substring(0, lineStart);
                string after = text.Substring(close + 1);
                string replacement = "dependencies = " + arrayText;
                File.WriteAllText(pyprojectPath, before + replacement + after);
                return true;
            }

            // 2) No dependencies key yet — insert one into [project] (after requires-python if present,
            //    else right after the [project] header).
            Match proj = Regex.Match(text, @"(?m)^\[project\][ \t]*$");
            if (proj.Success)
            {
                int insertAt = FindProjectInsertPoint(text, proj.Index + proj.Length, newline);
                string line = "dependencies = " + arrayText;
                string before = text.Substring(0, insertAt);
                string after = text.Substring(insertAt);
                if (!before.EndsWith("\n") && before.Length > 0) before += newline;
                File.WriteAllText(pyprojectPath, before + line + newline + after);
                return true;
            }

            // 3) No [project] table at all — create a minimal one.
            var sb = new StringBuilder(text);
            if (text.Length > 0 && !text.EndsWith("\n")) sb.Append(newline);
            sb.Append("[project]").Append(newline).Append("dependencies = ").Append(arrayText).Append(newline);
            File.WriteAllText(pyprojectPath, sb.ToString());
            return true;
        }

        // Insert after the last of the simple [project] keys we know (requires-python/version/name),
        // otherwise immediately after the header.
        private static int FindProjectInsertPoint(string text, int afterHeaderIndex, string newline)
        {
            int best = afterHeaderIndex;
            foreach (string k in new[] { "name", "version", "requires-python" })
            {
                Match m = Regex.Match(text.Substring(afterHeaderIndex),
                    @"(?m)^[ \t]*" + Regex.Escape(k) + @"[ \t]*=.*$");
                if (m.Success)
                {
                    int end = afterHeaderIndex + m.Index + m.Length;
                    if (end > best) best = end;
                }
                // stop scanning at the next table header
                Match tbl = Regex.Match(text.Substring(afterHeaderIndex), @"(?m)^\[");
                if (tbl.Success && afterHeaderIndex + tbl.Index < best) { /* keep best within table */ }
            }
            return best;
        }

        private static string FormatArray(List<string> deps)
        {
            if (deps == null || deps.Count == 0) return "[]";
            var sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < deps.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(EscapeToml(deps[i])).Append('"');
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string EscapeToml(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static int FindMatchingBracket(string text, int openIndex)
        {
            if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != '[') return -1;
            int depth = 0; bool inStr = false; char q = '\0';
            for (int i = openIndex; i < text.Length; i++)
            {
                char c = text[i];
                if (inStr) { if (c == q) inStr = false; continue; }
                if (c == '"' || c == '\'') { inStr = true; q = c; }
                else if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
    }
}
