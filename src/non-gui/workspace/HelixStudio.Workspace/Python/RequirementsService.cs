using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>One physical line of a <c>requirements.txt</c>: a requirement, a comment, an option, or a blank.</summary>
    public sealed class RequirementLine
    {
        public enum LineKind { Requirement, Comment, Option, Blank }
        public LineKind Kind;
        public string Raw;                 // verbatim original text (trivia-preserving)
        public Pep508Dependency Requirement; // set only when Kind == Requirement
        public override string ToString() { return Raw; }
    }

    /// <summary>The result of diffing a requirements.txt against a pyproject's dependencies.</summary>
    public sealed class RequirementsDiff
    {
        public readonly List<string> OnlyInRequirements = new List<string>(); // canonical names
        public readonly List<string> OnlyInPyproject = new List<string>();
        public readonly List<string> SpecifierDiffers = new List<string>();   // present in both, different specifier
        public bool InSync { get { return OnlyInRequirements.Count == 0 && OnlyInPyproject.Count == 0 && SpecifierDiffers.Count == 0; } }
    }

    /// <summary>
    /// FR-PROJ-04: read / write a <c>requirements.txt</c> and keep it in sync with a <c>pyproject.toml</c>'s
    /// <c>[project].dependencies</c>. Parsing is line-oriented and trivia-preserving (comments, <c>-r other.txt</c>
    /// includes, <c>-e .</c> editable installs, and blank lines survive a round-trip), reusing the PEP 508 parser
    /// from <see cref="PyprojectDependencyService"/> for requirement identity (PEP 503 canonical names). The sync
    /// is a pure diff + additive apply: <see cref="SyncFromPyproject"/> appends pyproject deps missing from the
    /// file without touching existing lines. Native-first: no <c>pip</c> needed.
    /// </summary>
    public static class RequirementsService
    {
        /// <summary>Parse requirements.txt text into structured lines (trivia preserved verbatim in <c>Raw</c>).</summary>
        public static List<RequirementLine> Parse(string text)
        {
            var lines = new List<RequirementLine>();
            if (text == null) return lines;
            foreach (string raw in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                string t = raw.Trim();
                var line = new RequirementLine { Raw = raw };
                if (t.Length == 0) line.Kind = RequirementLine.LineKind.Blank;
                else if (t.StartsWith("#")) line.Kind = RequirementLine.LineKind.Comment;
                else if (t.StartsWith("-")) line.Kind = RequirementLine.LineKind.Option; // -r, -e, --hash, -c, ...
                else
                {
                    line.Kind = RequirementLine.LineKind.Requirement;
                    // Strip an inline comment (" # ...") before parsing the requirement.
                    string reqText = t;
                    int hash = IndexOfInlineComment(t);
                    if (hash >= 0) reqText = t.Substring(0, hash).Trim();
                    line.Requirement = PyprojectDependencyService.Parse(reqText);
                }
                lines.Add(line);
            }
            // A trailing newline produces a final empty element; keep it only if the source ended without one.
            if (text.Length > 0 && (text.EndsWith("\n") || text.EndsWith("\r")) && lines.Count > 0
                && lines[lines.Count - 1].Kind == RequirementLine.LineKind.Blank
                && lines[lines.Count - 1].Raw.Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            return lines;
        }

        public static List<RequirementLine> ReadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new List<RequirementLine>();
            return Parse(File.ReadAllText(path));
        }

        /// <summary>Only the actual requirements (skips comments / options / blanks).</summary>
        public static List<Pep508Dependency> Requirements(IEnumerable<RequirementLine> lines)
        {
            var reqs = new List<Pep508Dependency>();
            foreach (var l in lines) if (l.Kind == RequirementLine.LineKind.Requirement && l.Requirement != null) reqs.Add(l.Requirement);
            return reqs;
        }

        /// <summary>Serialize lines back to text (round-trips verbatim for unmodified input).</summary>
        public static string Write(IEnumerable<RequirementLine> lines)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var l in lines) { if (!first) sb.Append('\n'); sb.Append(l.Raw); first = false; }
            return sb.ToString();
        }

        /// <summary>Diff a requirements.txt (by path) against a pyproject.toml (by path), by canonical name.</summary>
        public static RequirementsDiff Diff(string requirementsPath, string pyprojectPath)
        {
            var reqLines = ReadFile(requirementsPath);
            var reqs = Requirements(reqLines);
            var proj = PyprojectDependencyService.List(pyprojectPath);
            return DiffCore(reqs, proj);
        }

        private static RequirementsDiff DiffCore(List<Pep508Dependency> reqs, List<Pep508Dependency> proj)
        {
            var diff = new RequirementsDiff();
            var reqMap = new Dictionary<string, Pep508Dependency>(StringComparer.Ordinal);
            foreach (var r in reqs) if (!string.IsNullOrEmpty(r.CanonicalName)) reqMap[r.CanonicalName] = r;
            var projMap = new Dictionary<string, Pep508Dependency>(StringComparer.Ordinal);
            foreach (var p in proj) if (!string.IsNullOrEmpty(p.CanonicalName)) projMap[p.CanonicalName] = p;

            foreach (var kv in reqMap) if (!projMap.ContainsKey(kv.Key)) diff.OnlyInRequirements.Add(kv.Key);
            foreach (var kv in projMap)
            {
                if (!reqMap.ContainsKey(kv.Key)) { diff.OnlyInPyproject.Add(kv.Key); continue; }
                if (!string.Equals(reqMap[kv.Key].Specifier ?? "", kv.Value.Specifier ?? "", StringComparison.Ordinal))
                    diff.SpecifierDiffers.Add(kv.Key);
            }
            diff.OnlyInRequirements.Sort(StringComparer.Ordinal);
            diff.OnlyInPyproject.Sort(StringComparer.Ordinal);
            diff.SpecifierDiffers.Sort(StringComparer.Ordinal);
            return diff;
        }

        /// <summary>
        /// Append every pyproject dependency missing from requirements.txt (matched by canonical name), preserving
        /// all existing lines/comments verbatim. Returns the number of requirements added. Creates the file if
        /// absent. Existing lines are never rewritten (additive sync).
        /// </summary>
        public static int SyncFromPyproject(string requirementsPath, string pyprojectPath)
        {
            var lines = ReadFile(requirementsPath);
            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in Requirements(lines)) if (!string.IsNullOrEmpty(r.CanonicalName)) existing.Add(r.CanonicalName);

            var proj = PyprojectDependencyService.List(pyprojectPath);
            int added = 0;
            var toAdd = new List<Pep508Dependency>();
            foreach (var p in proj) if (!string.IsNullOrEmpty(p.CanonicalName) && !existing.Contains(p.CanonicalName)) toAdd.Add(p);
            if (toAdd.Count == 0) return 0;

            if (lines.Count > 0 && lines[lines.Count - 1].Kind != RequirementLine.LineKind.Blank)
                lines.Add(new RequirementLine { Kind = RequirementLine.LineKind.Comment, Raw = "# synced from pyproject.toml" });
            foreach (var p in toAdd)
            {
                lines.Add(new RequirementLine { Kind = RequirementLine.LineKind.Requirement, Raw = p.Raw, Requirement = p });
                existing.Add(p.CanonicalName);
                added++;
            }
            File.WriteAllText(requirementsPath, Write(lines) + "\n");
            return added;
        }

        private static int IndexOfInlineComment(string s)
        {
            // pip treats " #" (hash preceded by whitespace) as an inline comment start.
            for (int i = 1; i < s.Length; i++)
                if (s[i] == '#' && (s[i - 1] == ' ' || s[i - 1] == '\t')) return i;
            return -1;
        }
    }
}
