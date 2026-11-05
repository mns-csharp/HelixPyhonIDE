namespace HelixPythonNamespace.Workspace.Python
{
    public enum PyProjectConflictResolution
    {
        /// <summary>Leave the existing pyproject.toml untouched; the member is still listed in the workspace.</summary>
        Keep = 0,
        /// <summary>Preserve everything; only add or update the <c>[tool.helixstudio]</c> section.</summary>
        Merge = 1,
        /// <summary>Replace the file entirely with a freshly-authored minimal pyproject.toml.</summary>
        Overwrite = 2
    }

    /// <summary>
    /// Pluggable policy consulted by <see cref="PythonDirectoryImporter"/> each time
    /// it is about to write a per-package <c>pyproject.toml</c> that already exists on disk.
    /// Implementations may prompt a UI, return a cached "apply to all" choice, or be silent.
    /// </summary>
    public interface IPyProjectConflictResolver
    {
        /// <summary>
        /// Decide what to do with an existing <c>pyproject.toml</c>.
        /// </summary>
        /// <param name="existingPath">Absolute path to the existing file.</param>
        /// <returns>The chosen resolution.</returns>
        PyProjectConflictResolution Resolve(string existingPath);
    }

    /// <summary>Non-interactive resolver that always returns a fixed choice. Useful in tests and batch flows.</summary>
    public sealed class SilentPyProjectConflictResolver : IPyProjectConflictResolver
    {
        private readonly PyProjectConflictResolution _choice;
        public SilentPyProjectConflictResolver(PyProjectConflictResolution choice) { _choice = choice; }
        public PyProjectConflictResolution Resolve(string existingPath) { return _choice; }
    }
}
