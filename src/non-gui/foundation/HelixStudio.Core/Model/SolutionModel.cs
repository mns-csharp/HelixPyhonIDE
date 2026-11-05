using System;
using System.Collections.Generic;
using System.Linq;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Root aggregate representing a Visual Studio .sln file.
    /// Identity: FilePath (absolute path to .sln)
    /// </summary>
    public sealed class SolutionModel
    {
        public SolutionModel(string filePath, string name)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));

            FilePath = filePath;
            Name = name;
            Projects = new List<ProjectModel>();
            LastParsedUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Absolute path to the .sln file (primary identity).
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// Display name of the solution.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Projects contained in this solution.
        /// </summary>
        public List<ProjectModel> Projects { get; }

        /// <summary>
        /// Timestamp of last successful parse from disk.
        /// </summary>
        public DateTime LastParsedUtc { get; set; }

        /// <summary>
        /// Resolved absolute path to a CMake folder linked via the root pyproject.toml's
        /// <c>[tool.helixstudio] linked-cmake-project</c> key. Null when the solution has no
        /// linked CMake project. Populated by SolutionService, consumed by SolutionCoordinator
        /// to attach a secondary Solution Explorer node WITHOUT flipping
        /// MainForm.IsNativeWorkspaceActive (the Python workspace stays the active one).
        /// </summary>
        public string LinkedCMakeProjectPath { get; set; }

        /// <summary>
        /// Real files under the solution directory that belong to NO member project — the
        /// <c>README.md</c>, <c>pytest.ini</c>, <c>requirements.txt</c> and <c>.gitignore</c> of a
        /// normal repository — as solution-relative forward-slash paths. Filled by the loader from
        /// the same scan that fills <see cref="ProjectModel.ContentFiles"/>. Never null.
        ///
        /// The canonical layout calls these legal (they are not source, so PY8001 has no claim on
        /// them) and calls them REAL: they are rendered under a <c>Solution Items</c> node that is
        /// INSIDE the parity contract, at real paths a user can create a file next to. Before this
        /// list existed the tree simply omitted them and the parity checker reported four orphans
        /// nothing could ever repair.
        /// </summary>
        public List<string> SolutionItems { get; } = new List<string>();

        /// <summary>
        /// Real directories under the solution directory that are neither a member project nor
        /// inside one — output folders like <c>results/</c> and <c>artifacts/</c>. Legal, not
        /// third-party, and shown: the tree shows directories. Never null.
        /// </summary>
        public List<string> ContentDirectories { get; } = new List<string>();

        /// <summary>
        /// Find a type by its fully qualified name across all projects.
        /// </summary>
        public TypeModel FindType(string fullyQualifiedName)
        {
            if (string.IsNullOrWhiteSpace(fullyQualifiedName))
                return null;

            // Normalize input: convert :: to . for C++ names
            var normalized = fullyQualifiedName.Replace("::", ".");

            // Exact match first
            foreach (var project in Projects)
            {
                var type = project.Types.FirstOrDefault(t =>
                    string.Equals(t.FullyQualifiedName, normalized, StringComparison.OrdinalIgnoreCase));
                if (type != null)
                    return type;
            }

            // Fallback: match by simple name if no namespace match
            var simpleName = normalized;
            var lastDot = normalized.LastIndexOf('.');
            if (lastDot >= 0)
                simpleName = normalized.Substring(lastDot + 1);

            foreach (var project in Projects)
            {
                var type = project.Types.FirstOrDefault(t =>
                    string.Equals(t.Name, simpleName, StringComparison.OrdinalIgnoreCase));
                if (type != null)
                    return type;
            }

            return null;
        }

        /// <summary>
        /// Get all types across all projects.
        /// </summary>
        public IEnumerable<TypeModel> GetAllTypes()
        {
            return Projects.SelectMany(p => p.Types);
        }

        /// <summary>
        /// Direct nested children of the given parent type (one level — not transitive).
        /// Matches types whose ParentTypeFqn equals <paramref name="parentFqn"/>.
        /// </summary>
        public IEnumerable<TypeModel> GetNestedTypesOf(string parentFqn)
        {
            if (string.IsNullOrEmpty(parentFqn)) return System.Linq.Enumerable.Empty<TypeModel>();
            return GetAllTypes().Where(t =>
                string.Equals(t.ParentTypeFqn, parentFqn, System.StringComparison.Ordinal));
        }

        /// <summary>
        /// Get the directory containing the solution file.
        /// </summary>
        public string SolutionDirectory
        {
            get { return System.IO.Path.GetDirectoryName(FilePath); }
        }
    }
}
