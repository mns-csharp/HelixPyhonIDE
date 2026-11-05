using System.Collections.Generic;

namespace HelixPythonNamespace.Core.Model
{
    /// <summary>
    /// Stores visual layout information separately from semantic model.
    /// Persisted in .csooptool-layout.json alongside the .sln file.
    /// </summary>
    public sealed class LayoutModel
    {
        public LayoutModel(string solutionFilePath)
        {
            SolutionFilePath = solutionFilePath ?? string.Empty;
            FormatVersion = 1;
            NodeLayouts = new Dictionary<string, NodeLayoutData>();
            EdgeLayouts = new Dictionary<string, EdgeLayoutData>();
            ViewportState = new ViewportState();
        }

        /// <summary>
        /// Path to the solution file this layout corresponds to.
        /// </summary>
        public string SolutionFilePath { get; set; }

        /// <summary>
        /// Schema version for forward-compatible migration.
        /// </summary>
        public int FormatVersion { get; set; }

        /// <summary>
        /// Layout data for each type node, keyed by fully qualified name.
        /// </summary>
        public Dictionary<string, NodeLayoutData> NodeLayouts { get; set; }

        /// <summary>
        /// Layout data for each edge, keyed by "SourceFQN->TargetFQN".
        /// </summary>
        public Dictionary<string, EdgeLayoutData> EdgeLayouts { get; set; }

        /// <summary>
        /// Current viewport state (pan and zoom).
        /// </summary>
        public ViewportState ViewportState { get; set; }

        /// <summary>
        /// Get layout for a type, or null if not found.
        /// </summary>
        public NodeLayoutData GetNodeLayout(string fullyQualifiedName)
        {
            NodeLayoutData layout;
            if (NodeLayouts.TryGetValue(fullyQualifiedName, out layout))
                return layout;
            return null;
        }

        /// <summary>
        /// Set layout for a type.
        /// </summary>
        public void SetNodeLayout(string fullyQualifiedName, NodeLayoutData layout)
        {
            NodeLayouts[fullyQualifiedName] = layout;
        }

        /// <summary>
        /// Handle type rename by migrating layout data.
        /// </summary>
        public void OnTypeRenamed(string oldFQN, string newFQN)
        {
            NodeLayoutData nodeLayout;
            if (NodeLayouts.TryGetValue(oldFQN, out nodeLayout))
            {
                NodeLayouts.Remove(oldFQN);
                NodeLayouts[newFQN] = nodeLayout;
            }

            // Update edge keys that reference the renamed type
            var keysToUpdate = new List<string>();
            foreach (var key in EdgeLayouts.Keys)
            {
                if (key.Contains(oldFQN))
                    keysToUpdate.Add(key);
            }

            foreach (var oldKey in keysToUpdate)
            {
                var edgeLayout = EdgeLayouts[oldKey];
                EdgeLayouts.Remove(oldKey);
                var newKey = oldKey.Replace(oldFQN, newFQN);
                EdgeLayouts[newKey] = edgeLayout;
            }
        }

        /// <summary>
        /// Create an edge key from source and target FQNs.
        /// </summary>
        public static string CreateEdgeKey(string sourceFQN, string targetFQN)
        {
            return sourceFQN + "->" + targetFQN;
        }
    }

    /// <summary>
    /// Layout data for a single type node.
    /// </summary>
    public sealed class NodeLayoutData
    {
        public NodeLayoutData()
        {
            Width = 200f;
            Height = 150f;
        }

        /// <summary>
        /// X position on the canvas.
        /// </summary>
        public float PositionX { get; set; }

        /// <summary>
        /// Y position on the canvas.
        /// </summary>
        public float PositionY { get; set; }

        /// <summary>
        /// Width of the node visual.
        /// </summary>
        public float Width { get; set; }

        /// <summary>
        /// Height of the node visual.
        /// </summary>
        public float Height { get; set; }

        /// <summary>
        /// Whether the node is collapsed (showing only header).
        /// </summary>
        public bool IsCollapsed { get; set; }

        /// <summary>
        /// When true, the node is filtered out of the canvas projection entirely.
        /// Edges connected to hidden nodes also disappear (handled by the projection's
        /// existing visibility gate — no stub / dangling lines). State persists with
        /// the solution's layout JSON so hides survive restart.
        /// </summary>
        public bool IsHidden { get; set; }
    }

    /// <summary>
    /// Layout data for a relationship edge.
    /// </summary>
    public sealed class EdgeLayoutData
    {
        public EdgeLayoutData()
        {
            RoutingPoints = new List<PointData>();
        }

        /// <summary>
        /// Intermediate routing points for the edge path.
        /// </summary>
        public List<PointData> RoutingPoints { get; set; }
    }

    /// <summary>
    /// Simple 2D point for layout storage.
    /// </summary>
    public sealed class PointData
    {
        public float X { get; set; }
        public float Y { get; set; }
    }

    /// <summary>
    /// Current viewport state.
    /// </summary>
    public sealed class ViewportState
    {
        public ViewportState()
        {
            Zoom = 1.0f;
        }

        /// <summary>
        /// Horizontal pan offset.
        /// </summary>
        public float PanX { get; set; }

        /// <summary>
        /// Vertical pan offset.
        /// </summary>
        public float PanY { get; set; }

        /// <summary>
        /// Zoom level (1.0 = 100%).
        /// </summary>
        public float Zoom { get; set; }
    }
}
