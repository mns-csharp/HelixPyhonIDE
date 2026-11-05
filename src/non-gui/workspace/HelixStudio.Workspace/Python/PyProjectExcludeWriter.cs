using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Adds or removes an entry in <c>[tool.helixstudio].excluded</c> (SRS §5.9
    /// "Exclude from Project" / "Include in Project"). Reads the current list via
    /// <see cref="PyProjectReader"/>, edits it in memory, and writes the whole
    /// array back through <see cref="TomlTableMerger"/> so every other key/table
    /// in the file survives untouched.
    /// </summary>
    public static class PyProjectExcludeWriter
    {
        /// <summary>
        /// <paramref name="relativePath"/> is project-relative (relative to the
        /// directory containing <paramref name="pyprojectPath"/>) and may use
        /// either slash direction; it is normalized to forward slashes before
        /// comparison and storage, matching <see cref="PyProjectMetadata.Excluded"/>.
        /// </summary>
        public static void SetExcluded(string pyprojectPath, string relativePath, bool excluded)
        {
            if (string.IsNullOrEmpty(pyprojectPath) || string.IsNullOrEmpty(relativePath)) return;
            string norm = relativePath.Replace('\\', '/');

            List<string> current = File.Exists(pyprojectPath)
                ? PyProjectReader.Read(pyprojectPath).Excluded
                : new List<string>();
            List<string> updated = new List<string>(current);

            if (excluded)
            {
                if (!updated.Any(p => string.Equals(p, norm, StringComparison.OrdinalIgnoreCase)))
                    updated.Add(norm);
            }
            else
            {
                updated.RemoveAll(p => string.Equals(p, norm, StringComparison.OrdinalIgnoreCase));
            }

            string arrayValue = "[" + string.Join(", ", updated.Select(p => "\"" + p + "\"")) + "]";
            var keys = new Dictionary<string, string> { { "excluded", arrayValue } };
            TomlTableMerger.EnsureTable(pyprojectPath, "tool.helixstudio", keys);
        }
    }
}
