namespace HelixPythonNamespace.UI.Interaction.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using HelixPythonNamespace.UI.Rendering;

    public enum AlignmentKind
    {
        Left,
        Right,
        Top,
        Bottom,
        CenterHorizontal,
        CenterVertical
    }

    public sealed class AlignNodesCommand : ICommand
    {
        private readonly SceneGraph _sceneGraph;
        private readonly EdgeRouter _edgeRouter;
        private readonly AlignmentKind _kind;
        private readonly Dictionary<string, PointF> _original;
        private readonly Dictionary<string, PointF> _final;

        public AlignNodesCommand(
            SceneGraph sceneGraph,
            EdgeRouter edgeRouter,
            IList<NodeVisual> nodes,
            AlignmentKind kind)
        {
            if (sceneGraph == null) throw new ArgumentNullException("sceneGraph");
            if (edgeRouter == null) throw new ArgumentNullException("edgeRouter");
            if (nodes == null) throw new ArgumentNullException("nodes");

            _sceneGraph = sceneGraph;
            _edgeRouter = edgeRouter;
            _kind = kind;
            _original = new Dictionary<string, PointF>();
            _final = new Dictionary<string, PointF>();

            if (nodes.Count < 2) return;

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                RectangleF b = nodes[i].WorldBounds;
                if (b.Left < minX) minX = b.Left;
                if (b.Right > maxX) maxX = b.Right;
                if (b.Top < minY) minY = b.Top;
                if (b.Bottom > maxY) maxY = b.Bottom;
            }
            float midX = (minX + maxX) * 0.5f;
            float midY = (minY + maxY) * 0.5f;

            for (int i = 0; i < nodes.Count; i++)
            {
                NodeVisual node = nodes[i];
                RectangleF b = node.WorldBounds;
                _original[node.NodeId] = new PointF(b.X, b.Y);

                float newX = b.X;
                float newY = b.Y;
                switch (kind)
                {
                    case AlignmentKind.Left:
                        newX = minX;
                        break;
                    case AlignmentKind.Right:
                        newX = maxX - b.Width;
                        break;
                    case AlignmentKind.Top:
                        newY = minY;
                        break;
                    case AlignmentKind.Bottom:
                        newY = maxY - b.Height;
                        break;
                    case AlignmentKind.CenterHorizontal:
                        newX = midX - b.Width * 0.5f;
                        break;
                    case AlignmentKind.CenterVertical:
                        newY = midY - b.Height * 0.5f;
                        break;
                }

                _final[node.NodeId] = new PointF(newX, newY);
            }
        }

        public string Description
        {
            get { return "Align " + _kind; }
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
