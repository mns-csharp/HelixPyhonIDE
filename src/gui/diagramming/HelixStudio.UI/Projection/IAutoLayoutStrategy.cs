using System.Collections.Generic;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Rendering;

namespace HelixPythonNamespace.UI.Projection
{
    /// <summary>
    /// Interface for auto-layout strategies that compute positions for new types.
    /// </summary>
    public interface IAutoLayoutStrategy
    {
        /// <summary>
        /// Set the expected total count of items for optimal grid calculation.
        /// </summary>
        void SetExpectedCount(int totalCount);

        /// <summary>
        /// Compute a position for a new type based on existing nodes.
        /// Used as a fallback when adding individual types after initial layout.
        /// </summary>
        NodeLayoutData ComputePosition(TypeModel type, IEnumerable<NodeVisual> existingNodes);

        /// <summary>
        /// Compute positions for all types at once using type-category banding.
        /// Band 0 (top): Interfaces, Band 1 (middle): Abstract Classes, Band 2 (bottom): Concrete Classes.
        /// Types are sorted alphabetically within each band for deterministic layout.
        /// Returns a dictionary mapping FullyQualifiedName to NodeLayoutData.
        /// </summary>
        Dictionary<string, NodeLayoutData> ComputeAllPositions(IEnumerable<TypeModel> types);
    }
}
