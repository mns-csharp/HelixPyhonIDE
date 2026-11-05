using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HelixPythonNamespace.Workspace.Python
{
    /// <summary>
    /// Persists PSM-41…46 project references: the full reference list of one member as
    /// <c>[tool.helixstudio] references = ["a", "b"]</c> in its own pyproject.toml, via the
    /// line-preserving <see cref="TomlTableMerger"/>. <see cref="PyProjectReader"/> reads the
    /// array back and <c>SolutionService</c> resolves it to ProjectReferences edges on load —
    /// before this pair existed, references added in the IDE evaporated on the next open.
    /// </summary>
    public static class PyProjectReferenceWriter
    {
        public static void SetReferences(string pyprojectPath, IEnumerable<string> referencedProjectNames)
        {
            if (string.IsNullOrEmpty(pyprojectPath)) throw new ArgumentException("pyprojectPath");
            var names = (referencedProjectNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder("[");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(names[i]).Append('"');
            }
            sb.Append(']');

            TomlTableMerger.EnsureTable(pyprojectPath, "tool.helixstudio",
                new Dictionary<string, string> { { "references", sb.ToString() } });
        }
    }
}
