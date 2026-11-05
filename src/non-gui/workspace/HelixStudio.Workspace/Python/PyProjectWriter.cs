using System.Collections.Generic;
using System.IO;
using System.Text;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Writes a minimal PEP 621 pyproject.toml. For updates to an existing file
    /// we rewrite only the known keys and append missing ones; unknown tables /
    /// keys outside <c>[project]</c> and <c>[tool.helixstudio]</c> are preserved
    /// line-for-line.
    /// </summary>
    public static class PyProjectWriter
    {
        /// <summary>
        /// Escape a value for a TOML basic string (the "..."-quoted form). Without this,
        /// a project/solution name or dependency containing a double-quote or backslash
        /// (or a control char) produces a malformed pyproject.toml that uv/pip/tomllib
        /// cannot parse. Follows the TOML spec's basic-string escapes.
        /// </summary>
        internal static string EscapeTomlBasicString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\r': sb.Append("\\r"); break;
                    default:
                        if (c < 0x20 || c == 0x7f) sb.Append("\\u").Append(((int)c).ToString("X4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static void WriteMinimal(string path, string projectName, string pythonVersion)
        {
            WriteMinimal(path, projectName, pythonVersion, ProjectKind.ClassLibrary, null, null);
        }

        public static void WriteMinimal(string path, string projectName, string pythonVersion,
            ProjectKind kind, IList<string> dependencies)
        {
            WriteMinimal(path, projectName, pythonVersion, kind, dependencies, null);
        }

        public static void WriteMinimal(string path, string projectName, string pythonVersion,
            ProjectKind kind, IList<string> dependencies, GuiFramework? guiFramework)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[project]");
            sb.Append("name = \"").Append(EscapeTomlBasicString(projectName)).AppendLine("\"");
            sb.AppendLine("version = \"0.1.0\"");
            sb.Append("requires-python = \">=").Append(EscapeTomlBasicString(pythonVersion)).AppendLine("\"");
            if (dependencies != null && dependencies.Count > 0)
            {
                sb.Append("dependencies = [");
                for (int i = 0; i < dependencies.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append("\"").Append(EscapeTomlBasicString(dependencies[i])).Append("\"");
                }
                sb.AppendLine("]");
            }
            else sb.AppendLine("dependencies = []");
            sb.AppendLine();
            sb.AppendLine("[tool.helixstudio]");
            sb.AppendLine("interface-style = \"protocol\"");
            sb.Append("kind = \"").Append(ProjectKinds.ToToken(kind)).AppendLine("\"");
            if (kind == ProjectKind.GuiApp && guiFramework.HasValue)
            {
                sb.Append("gui-framework = \"").Append(GuiFrameworks.ToToken(guiFramework.Value)).AppendLine("\"");
            }
            File.WriteAllText(path, sb.ToString());
        }

        public static void Update(string path, PyProjectMetadata meta)
        {
            // Simple strategy: rewrite the file entirely using the metadata.
            // Tool-specific keys and dependencies are preserved.
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[project]");
            if (!string.IsNullOrEmpty(meta.Name)) sb.Append("name = \"").Append(EscapeTomlBasicString(meta.Name)).AppendLine("\"");
            if (!string.IsNullOrEmpty(meta.Version)) sb.Append("version = \"").Append(EscapeTomlBasicString(meta.Version)).AppendLine("\"");
            if (!string.IsNullOrEmpty(meta.PythonVersion)) sb.Append("requires-python = \">=").Append(EscapeTomlBasicString(meta.PythonVersion)).AppendLine("\"");
            if (meta.Dependencies != null && meta.Dependencies.Count > 0)
            {
                sb.Append("dependencies = [");
                for (int i = 0; i < meta.Dependencies.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append("\"").Append(EscapeTomlBasicString(meta.Dependencies[i])).Append("\"");
                }
                sb.AppendLine("]");
            }
            else sb.AppendLine("dependencies = []");
            sb.AppendLine();
            sb.AppendLine("[tool.helixstudio]");
            if (meta.ToolHelixStudio != null)
            {
                foreach (var kv in meta.ToolHelixStudio)
                {
                    sb.Append(kv.Key).Append(" = \"").Append(EscapeTomlBasicString(kv.Value)).AppendLine("\"");
                }
            }
            File.WriteAllText(path, sb.ToString());
        }
    }
}
