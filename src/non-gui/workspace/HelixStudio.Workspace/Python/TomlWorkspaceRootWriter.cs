using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Writes the workspace-root <c>pyproject.toml</c> that lists member
    /// packages under <c>[tool.uv.workspace]</c>. When the root file already
    /// exists, unknown tables and keys are preserved — only the workspace and
    /// HelixStudio sections are touched.
    /// </summary>
    public static class TomlWorkspaceRootWriter
    {
        public static void Write(
            string rootTomlPath,
            string solutionName,
            string pythonVersion,
            IReadOnlyList<PackageDescriptor> packages,
            string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootTomlPath)) throw new ArgumentException("rootTomlPath");
            if (string.IsNullOrEmpty(solutionName)) throw new ArgumentException("solutionName");
            if (packages == null) throw new ArgumentNullException(nameof(packages));

            List<string> memberPaths = new List<string>();
            foreach (PackageDescriptor pkg in packages)
            {
                string rel = GetRelativePath(rootDirectory, pkg.DirectoryPath);
                // Skip the root directory itself as a member (happens for the
                // synthesized root package when the workspace root has loose .py files).
                if (string.IsNullOrEmpty(rel) || rel == "." || rel == "./") continue;
                memberPaths.Add(NormalizeForwardSlash(rel));
            }

            if (!File.Exists(rootTomlPath))
            {
                File.WriteAllText(rootTomlPath, BuildFreshRoot(solutionName, pythonVersion ?? "3.12", memberPaths));
                return;
            }

            // Existing file: additive merge.
            EnsureProjectTable(rootTomlPath, solutionName, pythonVersion);
            EnsureWorkspaceTable(rootTomlPath, memberPaths);
            EnsureHelixStudioTable(rootTomlPath);
        }

        private static string BuildFreshRoot(string name, string pythonVersion, IReadOnlyList<string> members)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[project]");
            sb.Append("name = \"").Append(name).AppendLine("\"");
            sb.AppendLine("version = \"0.1.0\"");
            sb.Append("requires-python = \">=").Append(pythonVersion).AppendLine("\"");
            sb.AppendLine();
            sb.AppendLine("[tool.uv.workspace]");
            sb.Append("members = ").AppendLine(FormatArray(members));
            sb.AppendLine();
            sb.AppendLine("[tool.helixstudio]");
            sb.AppendLine("interface-style = \"protocol\"");
            return sb.ToString();
        }

        private static void EnsureProjectTable(string path, string name, string pythonVersion)
        {
            Dictionary<string, string> desired = new Dictionary<string, string>
            {
                { "name", "\"" + name + "\"" }
            };
            if (!string.IsNullOrEmpty(pythonVersion))
                desired["requires-python"] = "\">=" + pythonVersion + "\"";
            TomlTableMerger.EnsureTable(path, "project", desired);
        }

        private static void EnsureWorkspaceTable(string path, IReadOnlyList<string> members)
        {
            Dictionary<string, string> desired = new Dictionary<string, string>
            {
                { "members", FormatArray(members) }
            };
            TomlTableMerger.EnsureTable(path, "tool.uv.workspace", desired);
        }

        /// <summary>Persist the user-controlled Solution Explorer order without rewriting any
        /// unrelated TOML content. Names include the implicit root project, which cannot safely
        /// be represented as a uv workspace member path.</summary>
        public static void SetProjectOrder(string rootTomlPath, IReadOnlyList<string> projectNames)
        {
            if (string.IsNullOrEmpty(rootTomlPath)) throw new ArgumentException("rootTomlPath");
            var desired = new Dictionary<string, string>
            {
                { "project-order", FormatArray(projectNames) }
            };
            TomlTableMerger.EnsureTable(rootTomlPath, "tool.helixstudio", desired);
        }

        /// <summary>Replace only the uv workspace member paths, preserving every unrelated
        /// table and comment in the root manifest.</summary>
        public static void SetMembers(string rootTomlPath, IReadOnlyList<string> memberPaths)
        {
            if (string.IsNullOrEmpty(rootTomlPath)) throw new ArgumentException("rootTomlPath");
            EnsureWorkspaceTable(rootTomlPath, memberPaths ?? new List<string>());
        }

        private static void EnsureHelixStudioTable(string path)
        {
            Dictionary<string, string> desired = new Dictionary<string, string>
            {
                { "interface-style", "\"protocol\"" }
            };
            TomlTableMerger.EnsureTable(path, "tool.helixstudio", desired);
        }

        private static string FormatArray(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0) return "[]";
            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(values[i]).Append('"');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string NormalizeForwardSlash(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            string s = p.Replace('\\', '/');
            while (s.EndsWith("/")) s = s.Substring(0, s.Length - 1);
            return s;
        }

        private static string GetRelativePath(string fromDir, string toDir)
        {
            if (string.IsNullOrEmpty(fromDir) || string.IsNullOrEmpty(toDir)) return toDir;
            string f = Path.GetFullPath(fromDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string t = Path.GetFullPath(toDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(f, t, StringComparison.OrdinalIgnoreCase)) return string.Empty;
            string fWithSep = f + Path.DirectorySeparatorChar;
            Uri fromUri = new Uri(fWithSep);
            Uri toUri = new Uri(t);
            if (fromUri.Scheme != toUri.Scheme) return t;
            Uri rel = fromUri.MakeRelativeUri(toUri);
            return Uri.UnescapeDataString(rel.ToString());
        }
    }
}
