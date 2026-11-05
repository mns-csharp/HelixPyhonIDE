using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.UI.Projection
{
    /// <summary>
    /// Interface for managing layout metadata storage.
    /// </summary>
    public interface ILayoutMetadataStore
    {
        /// <summary>
        /// Get the position for a type by its FQN.
        /// </summary>
        NodeLayoutData GetPosition(string fullyQualifiedTypeName);

        /// <summary>
        /// Set the position for a type.
        /// </summary>
        void SetPosition(string fullyQualifiedTypeName, NodeLayoutData position);

        /// <summary>
        /// Handle type rename by migrating layout data.
        /// </summary>
        void OnTypeRenamed(string oldFQN, string newFQN);

        /// <summary>
        /// Get the underlying layout model.
        /// </summary>
        LayoutModel LayoutModel { get; }

        /// <summary>
        /// Save the layout to disk.
        /// </summary>
        void Save();

        /// <summary>
        /// Load the layout from disk.
        /// </summary>
        void Load();
    }
}
