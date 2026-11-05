using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Workspace.Toml
{
    /// <summary>
    /// FR-ANA-04: semantic analysis of a <c>pyproject.toml</c> <see cref="TomlDocument"/> (deeper than the
    /// FR-LINT-03 linter). Validates the PEP-621 <c>[project]</c> schema (required + typed fields, unknown
    /// keys), the PEP-508 well-formedness of each dependency (reusing <see cref="PyprojectDependencyService"/>),
    /// and — given a base directory — the on-disk resolvability of <c>[tool.uv.workspace].members</c>.
    /// Native-first. Reuses <see cref="TomlLintFinding"/> with <c>TMA###</c> codes.
    /// </summary>
    public static class TomlAnalyzer
    {
        // PEP 621 [project] table field names.
        private static readonly HashSet<string> Pep621Fields = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "version", "description", "readme", "requires-python", "license", "license-files",
            "authors", "maintainers", "keywords", "classifiers", "urls", "scripts", "gui-scripts",
            "entry-points", "dependencies", "optional-dependencies", "dynamic",
        };

        public static List<TomlLintFinding> Analyze(string source, string basePath = null)
        {
            return Analyze(TomlParser.Parse(source ?? string.Empty), basePath);
        }

        public static List<TomlLintFinding> Analyze(TomlDocument doc, string basePath = null)
        {
            var findings = new List<TomlLintFinding>();
            if (doc == null) return findings;

            var project = doc.Root.Get("project") as TomlTable;
            if (project != null)
            {
                AnalyzeProject(project, findings);
                AnalyzeDependencies(project, findings);
            }

            AnalyzeWorkspaceMembers(doc, basePath, findings);

            findings.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
            return findings;
        }

        private static void AnalyzeProject(TomlTable project, List<TomlLintFinding> findings)
        {
            // dynamic fields may legitimately omit name/version.
            var dynamicSet = new HashSet<string>(StringComparer.Ordinal);
            var dyn = project.Get("dynamic") as TomlValue;
            if (dyn != null && dyn.Kind == TomlValueKind.Array && dyn.Array != null)
                foreach (var e in dyn.Array) if (e.Kind == TomlValueKind.String) dynamicSet.Add(e.StringValue);

            // Required: name (string). version required unless dynamic.
            var name = project.Get("name") as TomlValue;
            if (name == null)
                Add(findings, project.Line, project.Column, "TMA001", TomlLintSeverity.Error, "[project] is missing the required 'name' field (PEP 621)");
            else if (name.Kind != TomlValueKind.String)
                Add(findings, name.Line, name.Column, "TMA001", TomlLintSeverity.Error, "[project].name must be a string");

            var version = project.Get("version") as TomlValue;
            if (version == null && !dynamicSet.Contains("version"))
                Add(findings, project.Line, project.Column, "TMA001", TomlLintSeverity.Warning, "[project].version is missing (add it, or list 'version' in [project].dynamic)");
            else if (version != null && version.Kind != TomlValueKind.String)
                Add(findings, version.Line, version.Column, "TMA001", TomlLintSeverity.Error, "[project].version must be a string");

            // Typed fields.
            ExpectStringIfPresent(project, "description", findings);
            ExpectStringIfPresent(project, "requires-python", findings);
            ExpectArrayIfPresent(project, "dependencies", findings);
            ExpectArrayIfPresent(project, "keywords", findings);
            ExpectArrayIfPresent(project, "classifiers", findings);

            // Unknown [project] keys (typo detection).
            foreach (string key in project.KeyOrder)
                if (!Pep621Fields.Contains(key))
                {
                    var item = project.Get(key) as TomlValue;
                    int ln = item != null ? item.Line : project.Line;
                    int col = item != null ? item.Column : project.Column;
                    Add(findings, ln, col, "TMA003", TomlLintSeverity.Warning, "Unknown [project] key '" + key + "' (not a PEP 621 field — typo?)");
                }
        }

        private static void AnalyzeDependencies(TomlTable project, List<TomlLintFinding> findings)
        {
            var deps = project.Get("dependencies") as TomlValue;
            if (deps != null && deps.Kind == TomlValueKind.Array && deps.Array != null)
                foreach (var item in deps.Array) ValidateRequirement(item, findings);

            // optional-dependencies: table of group -> array of requirement strings.
            var opt = project.Get("optional-dependencies") as TomlTable;
            if (opt != null)
                foreach (string group in opt.KeyOrder)
                {
                    var arr = opt.Get(group) as TomlValue;
                    if (arr != null && arr.Kind == TomlValueKind.Array && arr.Array != null)
                        foreach (var item in arr.Array) ValidateRequirement(item, findings);
                }
        }

        private static void ValidateRequirement(TomlValue item, List<TomlLintFinding> findings)
        {
            if (item.Kind != TomlValueKind.String)
            {
                Add(findings, item.Line, item.Column, "TMA002", TomlLintSeverity.Error, "Dependency entries must be PEP 508 requirement strings");
                return;
            }
            var parsed = PyprojectDependencyService.Parse(item.StringValue);
            // A valid PEP 508 requirement must begin with a distribution name (letter/digit),
            // e.g. "numpy>=1.24" — a bare specifier like ">=1.0" or "" has no name.
            bool hasName = !string.IsNullOrEmpty(parsed.Name)
                && System.Text.RegularExpressions.Regex.IsMatch(parsed.Name, "^[A-Za-z0-9]");
            if (!hasName)
                Add(findings, item.Line, item.Column, "TMA002", TomlLintSeverity.Error,
                    "Malformed PEP 508 requirement '" + item.StringValue + "' (no distribution name)");
        }

        private static void AnalyzeWorkspaceMembers(TomlDocument doc, string basePath, List<TomlLintFinding> findings)
        {
            var members = doc.Find("tool", "uv", "workspace", "members") as TomlValue;
            if (members == null || members.Kind != TomlValueKind.Array || members.Array == null) return;
            foreach (var m in members.Array)
            {
                if (m.Kind != TomlValueKind.String) continue;
                if (string.IsNullOrEmpty(basePath)) continue; // can't resolve without a base
                string path;
                try { path = Path.Combine(basePath, m.StringValue.Replace('/', Path.DirectorySeparatorChar)); }
                catch { continue; }
                if (!Directory.Exists(path) && !File.Exists(Path.Combine(path, "pyproject.toml")))
                    Add(findings, m.Line, m.Column, "TMA004", TomlLintSeverity.Warning,
                        "Workspace member '" + m.StringValue + "' does not resolve to an existing directory");
            }
        }

        private static void ExpectStringIfPresent(TomlTable t, string key, List<TomlLintFinding> findings)
        {
            var v = t.Get(key) as TomlValue;
            if (v != null && v.Kind != TomlValueKind.String)
                Add(findings, v.Line, v.Column, "TMA001", TomlLintSeverity.Error, "[project]." + key + " must be a string");
        }

        private static void ExpectArrayIfPresent(TomlTable t, string key, List<TomlLintFinding> findings)
        {
            var v = t.Get(key) as TomlValue;
            if (v != null && v.Kind != TomlValueKind.Array)
                Add(findings, v.Line, v.Column, "TMA001", TomlLintSeverity.Error, "[project]." + key + " must be an array");
        }

        private static void Add(List<TomlLintFinding> list, int line, int col, string code, TomlLintSeverity sev, string msg)
        {
            list.Add(new TomlLintFinding { Line = line, Column = col, Code = code, Severity = sev, Message = msg });
        }
    }
}
