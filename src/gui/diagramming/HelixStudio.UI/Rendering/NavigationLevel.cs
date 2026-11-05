namespace HelixPythonNamespace.UI.Rendering
{
    /// <summary>
    /// Represents the current drill-down level in the hierarchical view.
    /// </summary>
    public enum NavigationLevel
    {
        /// <summary>Show single solution rectangle with project list.</summary>
        Solution = 0,

        /// <summary>Show one rectangle per project with namespace lists.</summary>
        Project = 1,

        /// <summary>Show one rectangle per namespace with type lists.</summary>
        Namespace = 2,

        /// <summary>Show type nodes with edges (existing behavior).</summary>
        Type = 3
    }
}
