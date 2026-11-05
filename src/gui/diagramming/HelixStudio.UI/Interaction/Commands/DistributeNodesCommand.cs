namespace HelixPythonNamespace.UI.Interaction.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using HelixPythonNamespace.UI.Rendering;

    public enum DistributeKind
    {
        Horizontal,
        Vertical
    }

    public sealed class DistributeNodesCommand : ICommand
    {
        private readonly SceneGraph _sceneGraph;
        private readonly EdgeRouter _edgeRouter;
        private readonly DistributeKind _kind;
        private readonly Dictionary<string, PointF> _original;
        private readonly Dictionary<string, PointF> _final;

        public DistributeNodesCommand(
            SceneGraph sceneGraph,
            EdgeRouter edgeRouter,
            IList<NodeVisual> nodes,
            DistributeKind kind)
        {
            if (sceneGraph == null) throw new ArgumentNullException("sceneGraph");
            if (edgeRouter == null) throw new ArgumentNullException("edgeRouter");
            if (nodes == null) throw new ArgumentNullException("nodes");

            _sceneGraph = sceneGraph;
            _edgeRouter = edgeRouter;
            _kind = kind;
            _original = new Dictionary<string, PointF>();
            _final = new Dictionary<string, PointF>();

            if (nodes.Count < 3) return;

            List<NodeVisual> sorted = new List<NodeVisual>(nodes);
            if (kind == DistributeKind.Horizontal)
            {
                sorted.Sort(delegate(NodeVisual a, NodeVisual b)
                {
                    return a.WorldBounds.X.CompareTo(b.WorldBounds.X);
                });
            }
            else
            {
                sorted.Sort(delegate(NodeVisual a, NodeVisual b)
                {
                    return a.WorldBounds.Y.CompareTo(b.WorldBounds.Y);
                });
            }

            NodeVisual first = sorted[0];
            NodeVisual last = sorted[sorted.Count - 1];

            foreach (NodeVisual node in sorted)
            {
                RectangleF b = node.WorldBounds;
                _original[node.NodeId] = new PointF(b.X, b.Y);
            }

            if (kind == DistributeKind.Horizontal)
            {
                float totalSpan = last.WorldBounds.Right - first.WorldBounds.Left;
                float totalWidths = 0f;
                for (int i = 0; i < sorted.Count; i++) totalWidths += sorted[i].WorldBounds.Width;
                float gap = (totalSpan - totalWidths) / (sorted.Count - 1);
                if (gap < 0f) gap = 0f;

                float cursor = first.WorldBounds.Left;
                for (int i = 0; i < sorted.Count; i++)
                {
                    NodeVisual node = sorted[i];
                    RectangleF b = node.WorldBounds;
                    _final[node.NodeId] = new PointF(cursor, b.Y);
                    cursor += b.Width + gap;
                }
            }
            else
            {
                float totalSpan = last.WorldBounds.Bottom - first.WorldBounds.Top;
                float totalHeights = 0f;
                for (int i = 0; i < sorted.Count; i++) totalHeights += sorted[i].WorldBounds.Height;
                float gap = (totalSpan - totalHeights) / (sorted.Count - 1);
                if (gap < 0f) gap = 0f;

                float cursor = first.WorldBounds.Top;
                for (int i = 0; i < sorted.Count; i++)
                {
                    NodeVisual node = sorted[i];
                    RectangleF b = node.WorldBounds;
                    _final[node.NodeId] = new PointF(b.X, cursor);
                    cursor += b.Height + gap;
                }
            }
        }

        public string Description
        {
            get { return "Distribute " + _kind; }
        }

        public void Execute()
        {
            foreach (var pair in _final)
            {
                NodeVisual node = _sceneGraph.FindNode(pair.Key);
                if (node != null) node.SetPosition(pair.Value.X, pair.Value.Y);
            }
            _edgeRouter.RouteAllEdges(_sceneGraph);
        }

        public void Undo()
        {
            foreach (var pair in _original)
            {
                NodeVisual node = _sceneGraph.FindNode(pair.Key);
                if (node != null) node.SetPosition(pair.Value.X, pair.Value.Y);
            }
            _edgeRouter.RouteAllEdges(_sceneGraph);
        }
    }
}
