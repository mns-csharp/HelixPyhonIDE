using System;
using System.IO;
using System.Text.RegularExpressions;

namespace HelixPythonNamespace.Workspace.Cuda
{
    /// <summary>
    /// Persists a native project-to-project reference the CMake way:
    /// <c>target_link_libraries(&lt;this project's primary target&gt; PRIVATE &lt;referenced target&gt;)</c>
    /// appended to the referencing project's own CMakeLists.txt. The importer already parses the
    /// whole tree, so the link shows up on the next reload — and it is exactly what the real
    /// cmake build consumes.
    /// </summary>
    public static class CMakeReferenceWriter
    {
        /// <summary>Remove a linked target: drops the whole line when it was the only linked
        /// library, else just the target token. False when no link exists.</summary>
        public static bool RemoveTargetLink(string cmakeListsPath, string referencedTarget)
        {
            if (!File.Exists(cmakeListsPath)) return false;
            string text = File.ReadAllText(cmakeListsPath);
            var wholeLine = new Regex(
                @"^[ \t]*target_link_libraries\s*\(\s*\w+\s+(?:PRIVATE|PUBLIC|INTERFACE)?\s*" +
                Regex.Escape(referencedTarget) + @"\s*\)[ \t]*\r?\n?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            string updated = wholeLine.Replace(text, string.Empty, 1);
            if (string.Equals(updated, text, StringComparison.Ordinal))
            {
                // Multi-library line: remove just the token.
                var token = new Regex(@"(target_link_libraries\s*\([^)]*?)\s+" + Regex.Escape(referencedTarget) + @"\b",
                    RegexOptions.IgnoreCase);
                updated = token.Replace(text, "$1", 1);
                if (string.Equals(updated, text, StringComparison.Ordinal)) return false;
            }
            File.WriteAllText(cmakeListsPath, updated);
            return true;
        }

        /// <summary>Idempotently link <paramref name="referencedTarget"/> into the referencing
        /// CMakeLists' primary target. False when the link already exists.</summary>
        public static bool AddTargetLink(string cmakeListsPath, string referencedTarget)
        {
            if (string.IsNullOrEmpty(cmakeListsPath) || string.IsNullOrEmpty(referencedTarget))
                throw new ArgumentException("CMakeLists path and referenced target are required");
            if (!File.Exists(cmakeListsPath))
                throw new FileNotFoundException("CMakeLists.txt not found", cmakeListsPath);

            CMakeListsMetadata meta = CMakeListsReader.Read(cmakeListsPath);
            string primary = null;
            foreach (CMakeTarget t in meta.Targets)
            {
                if (t.Kind == CMakeTargetKind.Custom) continue;
                primary = t.Name;
                break;
            }
            if (primary == null)
                throw new InvalidOperationException(
                    "No add_executable/add_library target in " + cmakeListsPath + " to link against.");

            string text = File.ReadAllText(cmakeListsPath);
            var existing = new Regex(
                @"target_link_libraries\s*\(\s*" + Regex.Escape(primary) + @"\b[^)]*\b" +
                Regex.Escape(referencedTarget) + @"\b", RegexOptions.IgnoreCase);
            if (existing.IsMatch(text)) return false;

            string eol = text.Contains("\r\n") ? "\r\n" : "\n";
            if (text.Length > 0 && !text.EndsWith("\n")) text += eol;
            text += "target_link_libraries(" + primary + " PRIVATE " + referencedTarget + ")" + eol;
            File.WriteAllText(cmakeListsPath, text);
            return true;
        }
    }
}
