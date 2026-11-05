namespace HelixPythonNamespace.UI.Rendering
{
    /// <summary>
    /// Specifies which category of types should be rendered as full nodes
    /// versus circles on a filtered designer tab.
    /// </summary>
    public enum CategoryFilterMode
    {
        /// <summary>
        /// All types rendered as full nodes (unfiltered view).
        /// </summary>
        All,

        /// <summary>
        /// Only interfaces rendered as full nodes; other types as circles.
        /// </summary>
        InterfacesOnly,

        /// <summary>
        /// Only abstract classes rendered as full nodes; other types as circles.
        /// </summary>
        AbstractClassesOnly,

        /// <summary>
        /// Only concrete classes rendered as full nodes; other types as circles.
        /// </summary>
        ConcreteClassesOnly
    }
}
