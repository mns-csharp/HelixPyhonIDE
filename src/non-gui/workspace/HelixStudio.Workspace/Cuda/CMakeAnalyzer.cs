using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>A target node in the analyzed dependency graph.</summary>
    public sealed class CMakeGraphTarget
    {
        public string Name = "";
        public CMakeTargetKind Kind = CMakeTargetKind.Executable;
        public int Line;
        public readonly List<string> LinkDependencies = new List<string>();  // as written (targets + external libs)
    }

    /// <summary>The target/dependency graph plus semantic findings produced by <see cref="CMakeAnalyzer"/>.</summary>
    public sealed class CMakeAnalysisResult
    {
        public readonly List<CMakeGraphTarget> Targets = new List<CMakeGraphTarget>();
        public readonly List<CMakeLintFinding> Findings = new List<CMakeLintFinding>();

        public CMakeGraphTarget FindTarget(string name)
        {
            foreach (var t in Targets)
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }
    }

    /// <summary>
    /// FR-ANA-05: semantic analysis of a <see cref="CMakeFile"/> AST — the CMake analyzer (deeper than the
    /// FR-LINT-04 linter). Builds the target/dependency graph and reports: linking a target that was never
    /// defined (CMA001), using a variable before it is <c>set()</c> (CMA002, def-before-use), duplicate
    /// target definitions (CMA003), and dependency cycles among project targets (CMA004). Native-first.
    /// </summary>
    public static class CMakeAnalyzer
    {
        private static readonly HashSet<string> Visibility = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "PUBLIC", "PRIVATE", "INTERFACE", "LINK_PUBLIC", "LINK_PRIVATE", "LINK_INTERFACE_LIBRARIES" };

        public static CMakeAnalysisResult Analyze(string source)
        {
            return Analyze(CMakeParser.Parse(source ?? string.Empty));
        }

        public static CMakeAnalysisResult Analyze(CMakeFile file)
        {
            var r = new CMakeAnalysisResult();
            if (file == null) return r;

            // 1) Collect targets (and flag duplicates: CMA003).
            var byName = new Dictionary<string, CMakeGraphTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (CMakeCommand c in file.Commands)
            {
                CMakeTargetKind kind;
                if (!TryTargetKind(c, out kind) || c.Arguments.Count < 1) continue;
                string name = c.Arguments[0].Text;
                if (string.IsNullOrEmpty(name) || HasReference(c.Arguments[0])) continue; // skip ${VAR}-named
                if (byName.ContainsKey(name))
                    Add(r, c.Line, c.Column, "CMA003", CMakeLintSeverity.Error,
                        "Duplicate target '" + name + "' (already defined at line " + byName[name].Line + ")");
                else
                {
                    var t = new CMakeGraphTarget { Name = name, Kind = kind, Line = c.Line };
                    byName[name] = t;
                    r.Targets.Add(t);
                }
            }

            // 2) Link edges + unresolved-target-command (CMA001).
            foreach (CMakeCommand c in file.Commands)
            {
                bool isTargetCmd = c.Name != null && c.Name.StartsWith("target_", StringComparison.OrdinalIgnoreCase);
                bool isLink = string.Equals(c.Name, "target_link_libraries", StringComparison.OrdinalIgnoreCase);
                if (!isTargetCmd || c.Arguments.Count < 1) continue;

                string targetName = c.Arguments[0].Text;
                if (HasReference(c.Arguments[0])) continue; // ${VAR} target — can't resolve statically
                CMakeGraphTarget t;
                if (!byName.TryGetValue(targetName, out t))
                {
                    Add(r, c.Line, c.Column, "CMA001", CMakeLintSeverity.Error,
                        c.Name + "() targets '" + targetName + "', which is not defined by any add_executable/add_library in this file");
                    continue;
                }
                if (isLink)
                    for (int i = 1; i < c.Arguments.Count; i++)
                    {
                        string dep = c.Arguments[i].Text;
                        if (Visibility.Contains(dep) || string.IsNullOrEmpty(dep)) continue;
                        if (HasReference(c.Arguments[i])) continue;
                        if (dep.StartsWith("$<", StringComparison.Ordinal)) continue;
                        t.LinkDependencies.Add(dep);
                    }
            }

            // 3) Def-before-use (CMA002): a ${VAR} referenced earlier than VAR's earliest set().
            var firstSetLine = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (CMakeCommand c in file.Commands)
                if (string.Equals(c.Name, "set", StringComparison.OrdinalIgnoreCase) && c.Arguments.Count >= 1)
                {
                    string v = c.Arguments[0].Text;
                    if (!string.IsNullOrEmpty(v) && !firstSetLine.ContainsKey(v)) firstSetLine[v] = c.Line;
                }
            var loopAndParams = CollectLoopVarsAndParams(file);
            var reportedDbu = new HashSet<string>(StringComparer.Ordinal);
            foreach (CMakeCommand c in file.Commands)
                foreach (CMakeArgument a in c.Arguments)
                    foreach (CMakeReference re in a.References)
                    {
                        if (re.IsGeneratorExpression || re.Scope != "" || string.IsNullOrEmpty(re.Name)) continue;
                        int setLine;
                        if (!firstSetLine.TryGetValue(re.Name, out setLine)) continue;   // never set here → external, don't flag
                        if (loopAndParams.Contains(re.Name)) continue;
                        if (a.Line < setLine)
                        {
                            string key = re.Name + "@" + a.Line;
                            if (reportedDbu.Add(key))
                                Add(r, a.Line, a.Column, "CMA002", CMakeLintSeverity.Warning,
                                    "Variable '" + re.Name + "' is used here (line " + a.Line + ") before it is set() at line " + setLine);
                        }
                    }

            // 4) Dependency cycles among project targets (CMA004).
            DetectCycles(r, byName);

            r.Findings.Sort((x, y) => x.Line != y.Line ? x.Line.CompareTo(y.Line) : x.Column.CompareTo(y.Column));
            return r;
        }

        private static bool TryTargetKind(CMakeCommand c, out CMakeTargetKind kind)
        {
            kind = CMakeTargetKind.Executable;
            if (string.Equals(c.Name, "add_executable", StringComparison.OrdinalIgnoreCase)) { kind = CMakeTargetKind.Executable; return true; }
            if (string.Equals(c.Name, "add_custom_target", StringComparison.OrdinalIgnoreCase)) { kind = CMakeTargetKind.Custom; return true; }
            if (string.Equals(c.Name, "add_library", StringComparison.OrdinalIgnoreCase))
            {
                kind = CMakeTargetKind.StaticLibrary;
                if (c.Arguments.Count >= 2)
                {
                    string k = c.Arguments[1].Text.ToUpperInvariant();
                    if (k == "SHARED") kind = CMakeTargetKind.SharedLibrary;
                    else if (k == "MODULE") kind = CMakeTargetKind.ModuleLibrary;
                    else if (k == "INTERFACE") kind = CMakeTargetKind.InterfaceLibrary;
                }
                return true;
            }
            return false;
        }

        private static HashSet<string> CollectLoopVarsAndParams(CMakeFile file)
        {
            var s = new HashSet<string>(StringComparer.Ordinal);
            foreach (CMakeCommand c in file.Commands)
            {
                if (string.Equals(c.Name, "foreach", StringComparison.OrdinalIgnoreCase) && c.Arguments.Count >= 1)
                    s.Add(c.Arguments[0].Text);
                if ((string.Equals(c.Name, "function", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.Name, "macro", StringComparison.OrdinalIgnoreCase)))
                    for (int i = 1; i < c.Arguments.Count; i++) s.Add(c.Arguments[i].Text);
            }
            return s;
        }

        private static void DetectCycles(CMakeAnalysisResult r, Dictionary<string, CMakeGraphTarget> byName)
        {
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unseen,1=on-stack,2=done
            var reported = new HashSet<string>();
            var pathStack = new List<string>();

            Func<string, bool> dfs = null;
            dfs = node =>
            {
                state[node] = 1;
                pathStack.Add(node);
                CMakeGraphTarget t;
                if (byName.TryGetValue(node, out t))
                {
                    foreach (string dep in t.LinkDependencies)
                    {
                        if (!byName.ContainsKey(dep)) continue; // external lib, not a graph node
                        int st; state.TryGetValue(dep, out st);
                        if (st == 1)
                        {
                            // Back-edge → cycle. Build the cycle description.
                            int idx = pathStack.IndexOf(dep);
                            var cyc = pathStack.GetRange(idx, pathStack.Count - idx);
                            cyc.Add(dep);
                            if (reported.Add(Normalize(cyc)))
                                Add(r, t.Line, t.Column0(), "CMA004", CMakeLintSeverity.Error,
                                    "Dependency cycle among targets: " + string.Join(" -> ", cyc.ToArray()));
                        }
                        else if (st == 0) dfs(dep);
                    }
                }
                pathStack.RemoveAt(pathStack.Count - 1);
                state[node] = 2;
                return false;
            };

            foreach (var t in r.Targets)
            {
                int st; state.TryGetValue(t.Name, out st);
                if (st == 0 || !state.ContainsKey(t.Name)) dfs(t.Name);
            }
        }

        private static string Normalize(List<string> cyc)
        {
            // Rotate the cycle so it starts at its lexicographically smallest node (dedupe rotations).
            if (cyc.Count <= 1) return string.Join(">", cyc.ToArray());
            var core = cyc.GetRange(0, cyc.Count - 1);
            int min = 0;
            for (int i = 1; i < core.Count; i++) if (string.CompareOrdinal(core[i], core[min]) < 0) min = i;
            var rot = new List<string>();
            for (int i = 0; i < core.Count; i++) rot.Add(core[(min + i) % core.Count]);
            return string.Join(">", rot.ToArray());
        }

        private static bool HasReference(CMakeArgument a) { return a != null && a.References.Count > 0; }

        private static void Add(CMakeAnalysisResult r, int line, int col, string code, CMakeLintSeverity sev, string msg)
        {
            r.Findings.Add(new CMakeLintFinding { Line = line, Column = col, Code = code, Severity = sev, Message = msg });
        }
    }

    internal static class CMakeGraphTargetExt
    {
        public static int Column0(this CMakeGraphTarget t) { return 1; }
    }
}
