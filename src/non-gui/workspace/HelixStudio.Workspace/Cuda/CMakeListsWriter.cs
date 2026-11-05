using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// Emits a canonical <c>CMakeLists.txt</c> from a <see cref="CMakeListsMetadata"/> — the
    /// CUDA/native counterpart to <see cref="HelixPythonNamespace.Workspace.Python.PyProjectWriter"/>.
    /// Like the pyproject writer this produces a normalized form (it does not preserve the
    /// original comments/layout); <c>CMakeListsReader.Parse(Write(meta))</c> round-trips the
    /// modelled fields. Used to scaffold new CUDA projects and to persist edits to targets.
    /// </summary>
    public static class CMakeListsWriter
    {
        public static void Write(string path, CMakeListsMetadata meta)
        {
            File.WriteAllText(path, WriteToString(meta));
        }

        public static string WriteToString(CMakeListsMetadata meta)
        {
            if (meta == null) return string.Empty;
            var sb = new StringBuilder();

            sb.Append("cmake_minimum_required(VERSION ")
              .Append(string.IsNullOrEmpty(meta.CMakeMinimumVersion) ? "3.20" : meta.CMakeMinimumVersion)
              .AppendLine(")");

            sb.Append("project(").Append(string.IsNullOrEmpty(meta.ProjectName) ? "project" : meta.ProjectName);
            if (!string.IsNullOrEmpty(meta.ProjectVersion)) sb.Append(" VERSION ").Append(meta.ProjectVersion);
            if (meta.Languages.Count > 0) sb.Append(" LANGUAGES ").Append(string.Join(" ", meta.Languages));
            sb.AppendLine(")");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(meta.CxxStandard))
                sb.Append("set(CMAKE_CXX_STANDARD ").Append(meta.CxxStandard).AppendLine(")");
            if (!string.IsNullOrEmpty(meta.CudaStandard))
                sb.Append("set(CMAKE_CUDA_STANDARD ").Append(meta.CudaStandard).AppendLine(")");
            if (!string.IsNullOrEmpty(meta.CudaArchitectures))
                sb.Append("set(CMAKE_CUDA_ARCHITECTURES ").Append(meta.CudaArchitectures).AppendLine(")");

            // Emit user variables (skip the standard CMAKE_* ones already written above).
            foreach (var kv in meta.Variables)
            {
                if (IsStandardStandardVar(kv.Key)) continue;
                sb.Append("set(").Append(kv.Key);
                foreach (string v in kv.Value) sb.Append(' ').Append(Quote(v));
                sb.AppendLine(")");
            }

            if (meta.IncludeDirectories.Count > 0)
            {
                sb.AppendLine();
                sb.Append("include_directories(");
                sb.Append(string.Join(" ", Quote(meta.IncludeDirectories)));
                sb.AppendLine(")");
            }

            foreach (string sub in meta.Subdirectories)
                sb.Append("add_subdirectory(").Append(Quote(sub)).AppendLine(")");

            if (meta.Targets.Count > 0) sb.AppendLine();
            foreach (var t in meta.Targets)
            {
                switch (t.Kind)
                {
                    case CMakeTargetKind.Executable:
                        sb.Append("add_executable(").Append(t.Name);
                        AppendSources(sb, t);
                        sb.AppendLine(")");
                        break;
                    case CMakeTargetKind.Custom:
                        sb.Append("add_custom_target(").Append(t.Name).AppendLine(")");
                        break;
                    default:
                        sb.Append("add_library(").Append(t.Name).Append(' ').Append(LibKindToken(t.Kind));
                        AppendSources(sb, t);
                        sb.AppendLine(")");
                        break;
                }
            }

            // Link dependencies (target_link_libraries) after all targets are declared.
            bool anyLinks = false;
            foreach (var t in meta.Targets) if (t.LinkDependencies.Count > 0) { anyLinks = true; break; }
            if (anyLinks) sb.AppendLine();
            foreach (var t in meta.Targets)
            {
                if (t.LinkDependencies.Count == 0) continue;
                sb.Append("target_link_libraries(").Append(t.Name).Append(" PRIVATE");
                foreach (string dep in t.LinkDependencies) sb.Append(' ').Append(Quote(dep));
                sb.AppendLine(")");
            }

            return sb.ToString();
        }

        private static void AppendSources(StringBuilder sb, CMakeTarget t)
        {
            foreach (string s in t.Sources) sb.Append(' ').Append(Quote(s));
        }

        private static string LibKindToken(CMakeTargetKind kind)
        {
            switch (kind)
            {
                case CMakeTargetKind.SharedLibrary: return "SHARED";
                case CMakeTargetKind.ModuleLibrary: return "MODULE";
                case CMakeTargetKind.InterfaceLibrary: return "INTERFACE";
                default: return "STATIC";
            }
        }

        private static bool IsStandardStandardVar(string name)
        {
            return name == "CMAKE_CXX_STANDARD" || name == "CMAKE_CUDA_STANDARD" || name == "CMAKE_CUDA_ARCHITECTURES";
        }

        private static IEnumerable<string> Quote(IEnumerable<string> items)
        {
            foreach (string s in items) yield return Quote(s);
        }

        // Quote a token only if it contains whitespace; otherwise keep it bare (so ${VAR} stays literal).
        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            bool needs = false;
            foreach (char c in s) if (c == ' ' || c == '\t' || c == '(' || c == ')') { needs = true; break; }
            return needs ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
        }
    }
}
