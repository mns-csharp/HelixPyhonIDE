using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Toml
{
    public enum TomlLintSeverity { Error, Warning, Info }

    /// <summary>One TOML lint finding: rule code + message + position + severity.</summary>
    public sealed class TomlLintFinding
    {
        public int Line;
        public int Column;
        public string Code = "";     // TLN### / TML### (parser)
        public string Message = "";
        public TomlLintSeverity Severity = TomlLintSeverity.Warning;

        public override string ToString()
        {
            return "(" + Line + "," + Column + ") " + Severity.ToString().ToLowerInvariant() + " " + Code + ": " + Message;
        }
    }

    /// <summary>
    /// FR-LINT-03: a native TOML linter on the <see cref="TomlParser"/> AST — the TOML counterpart to the
    /// Python/CMake linters. Native-first. Folds parser diagnostics (syntax / duplicate key / redefined
    /// table) and adds style/smell checks: trailing whitespace (TLN001), tab indentation (TLN002),
    /// heterogeneous arrays (TLN003), mixed table styles at one level (TLN004 — some sub-tables via
    /// <c>[header]</c>, some via dotted keys), and a light <c>pyproject.toml</c> / PEP-621 profile
    /// (TLN010: a <c>[project]</c> table with a string <c>name</c>). Deeper schema validation is FR-ANA-04.
    /// </summary>
    public static class TomlLinter
    {
        public static List<TomlLintFinding> Lint(string source, string fileName = null)
        {
            return Lint(TomlParser.Parse(source ?? string.Empty), source ?? string.Empty, fileName);
        }

        public static List<TomlLintFinding> Lint(TomlDocument doc, string rawText, string fileName = null)
        {
            var findings = new List<TomlLintFinding>();
            if (doc == null) return findings;

            // Fold parser diagnostics.
            foreach (TomlDiagnostic d in doc.Diagnostics)
                findings.Add(new TomlLintFinding
                {
                    Line = d.Line, Column = d.Column, Code = d.Code, Message = d.Message,
                    Severity = d.IsError ? TomlLintSeverity.Error : TomlLintSeverity.Warning,
                });

            // Text-level checks: trailing whitespace + tab indentation.
            if (!string.IsNullOrEmpty(rawText))
            {
                string[] lines = rawText.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    if (ln.Length > 0 && (ln[ln.Length - 1] == ' ' || ln[ln.Length - 1] == '\t'))
                        Add(findings, i + 1, ln.Length, "TLN001", TomlLintSeverity.Info, "Trailing whitespace");
                    int lead = 0; while (lead < ln.Length && (ln[lead] == ' ' || ln[lead] == '\t')) lead++;
                    if (lead > 0 && ln.IndexOf('\t') >= 0 && ln.IndexOf('\t') < lead)
                        Add(findings, i + 1, 1, "TLN002", TomlLintSeverity.Info, "Tab used for indentation (prefer spaces)");
                }
            }

            // Heterogeneous arrays (readability smell — legal in TOML 1.0).
            WalkTable(doc.Root, findings);

            // Mixed table styles (TLN004): flag a table whose direct sub-tables mix declaration styles —
            // some via a standard [header] and some via dotted keys (a.b.c = …) at the same nesting level.
            // Inline tables ({…}, closed values) are intentionally excluded so real pyproject.toml files
            // (which legitimately mix [project] headers with inline `authors = [{…}]`) don't trip the rule.
            CheckMixedTableStyles(doc.Root, findings);

            // pyproject.toml / PEP-621 profile.
            if (IsPyproject(fileName))
            {
                var project = doc.Root.Get("project") as TomlTable;
                if (project == null)
                    Add(findings, 1, 1, "TLN010", TomlLintSeverity.Warning, "pyproject.toml has no [project] table (PEP 621)");
                else
                {
                    var name = project.Get("name") as TomlValue;
                    if (name == null || name.Kind != TomlValueKind.String || string.IsNullOrEmpty(name.StringValue))
                        Add(findings, project.Line, project.Column, "TLN010", TomlLintSeverity.Warning,
                            "[project] is missing a string 'name' (required by PEP 621)");
                }
            }

            findings.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
            return findings;
        }

        private static void CheckMixedTableStyles(TomlTable parent, List<TomlLintFinding> findings)
        {
            int headerStyle = 0, dottedStyle = 0;
            TomlTable firstDotted = null;
            foreach (string key in parent.KeyOrder)
            {
                var sub = parent.Get(key) as TomlTable;
                if (sub == null || sub.FromInlineTable) continue; // only standard sub-tables
                if (sub.DefinedByDottedKey) { dottedStyle++; if (firstDotted == null) firstDotted = sub; }
                else if (sub.ExplicitlyDefined) headerStyle++;
            }
            if (headerStyle > 0 && dottedStyle > 0 && firstDotted != null)
                Add(findings, firstDotted.Line, firstDotted.Column, "TLN004", TomlLintSeverity.Info,
                    "Mixed table styles at the same level — some sub-tables use [header] and some use dotted keys; prefer one style");

            // Recurse so nested levels are checked too.
            foreach (string key in parent.KeyOrder)
            {
                var sub = parent.Get(key) as TomlTable;
                if (sub != null && !sub.FromInlineTable) CheckMixedTableStyles(sub, findings);
            }
        }

        private static void WalkTable(TomlTable t, List<TomlLintFinding> findings)
        {
            foreach (string key in t.KeyOrder)
            {
                TomlItem item = t.Get(key);
                if (item is TomlTable sub) WalkTable(sub, findings);
                else if (item is TomlTableArray arr) { foreach (var e in arr.Elements) WalkTable(e, findings); }
                else if (item is TomlValue v) CheckValue(v, key, findings);
            }
        }

        private static void CheckValue(TomlValue v, string key, List<TomlLintFinding> findings)
        {
            if (v.Kind == TomlValueKind.Array && v.Array != null && v.Array.Count > 1)
            {
                TomlValueKind first = v.Array[0].Kind;
                bool mixed = false;
                foreach (var e in v.Array) if (e.Kind != first) { mixed = true; break; }
                if (mixed)
                    Add(findings, v.Line, v.Column, "TLN003", TomlLintSeverity.Info,
                        "Array '" + key + "' mixes value types — consider keeping arrays homogeneous for readability");
            }
            if (v.Kind == TomlValueKind.InlineTable && v.Inline != null) WalkTable(v.Inline, findings);
            if (v.Kind == TomlValueKind.Array && v.Array != null)
                foreach (var e in v.Array) if (e.Kind == TomlValueKind.InlineTable && e.Inline != null) WalkTable(e.Inline, findings);
        }

        private static bool IsPyproject(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string name = System.IO.Path.GetFileName(fileName);
            return string.Equals(name, "pyproject.toml", StringComparison.OrdinalIgnoreCase);
        }

        private static void Add(List<TomlLintFinding> list, int line, int col, string code, TomlLintSeverity sev, string msg)
        {
            list.Add(new TomlLintFinding { Line = line, Column = col, Code = code, Severity = sev, Message = msg });
        }
    }
}
