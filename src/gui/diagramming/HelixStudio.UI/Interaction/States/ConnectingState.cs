namespace HelixPythonNamespace.UI.Interaction.States
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.UI.Interaction.Commands;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class ConnectingState : IInteractionState
    {
        private readonly InteractionContext _context;
        private readonly NodeVisual _sourceNode;
        private readonly RelationshipKind _kind;
        private PointF _currentWorld;

        public ConnectingState(InteractionContext context, NodeVisual sourceNode, RelationshipKind kind)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }
            if (sourceNode == null)
            {
                throw new ArgumentNullException("sourceNode");
            }

            _context = context;
            _sourceNode = sourceNode;
            _kind = kind;

            // Start preview line from source node center
            RectangleF bounds = _sourceNode.WorldBounds;
            _currentWorld = new PointF(
                bounds.X + bounds.Width / 2f,
                bounds.Y + bounds.Height / 2f);
        }

        public void OnMouseDown(CanvasMouseEventArgs e)
        {
            // Not expected during connecting
        }

        public void OnMouseMove(CanvasMouseEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            _currentWorld = e.WorldLocation;
            _context.InvalidateCanvas();
        }

        public void OnMouseUp(CanvasMouseEventArgs e)
        {
            if (e == null)
            {
                _context.TransitionTo(new IdleState(_context));
                return;
            }

            float tolerance = _context.HitTestTolerance;
            NodeVisual targetNode = _context.SceneGraph.HitTestNode(e.WorldLocation, tolerance);

            if (targetNode != null && targetNode.NodeId != _sourceNode.NodeId)
            {
                // Check for duplicate edge between same source and target
                var existingEdges = _context.SceneGraph.GetEdgesForNode(_sourceNode.NodeId);
                bool duplicate = false;
                for (int i = 0; i < existingEdges.Count; i++)
                {
                    if (existingEdges[i].SourceNodeId == _sourceNode.NodeId
                        && existingEdges[i].TargetNodeId == targetNode.NodeId)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (duplicate)
                {
                    _context.InvalidateCanvas();
                    _context.TransitionTo(new IdleState(_context));
                    return;
                }

                // Create edge and add via command
                EdgeVisual edge = _context.SceneGraph.CreateEdge(
                    _sourceNode.NodeId, targetNode.NodeId, _kind);

                if (edge != null)
                {
                    AddRelationshipCommand cmd = new AddRelationshipCommand(
                        _context.SceneGraph, edge, _context.EdgeRouter);
                    _context.CommandDispatcher.Execute(cmd);
                }
            }

            _context.InvalidateCanvas();
            _context.TransitionTo(new IdleState(_context));
        }

        public void OnMouseWheel(CanvasMouseWheelEventArgs e)
        {
            // Ignore wheel during connecting
        }

        public void OnKeyDown(KeyEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                _context.InvalidateCanvas();
                _context.TransitionTo(new IdleState(_context));
            }
        }

        public void OnPaint(Graphics g, ViewportTransform viewport)
        {
            if (g == null || viewport == null)
            {
                return;
            }

            // Draw a dashed line from source node center to current mouse position
            RectangleF sourceBounds = _sourceNode.WorldBounds;
            PointF sourceCenter = new PointF(
                sourceBounds.X + sourceBounds.Width / 2f,
                sourceBounds.Y + sourceBounds.Height / 2f);

            PointF screenStart = viewport.WorldToScreen(sourceCenter);
            PointF screenEnd = viewport.WorldToScreen(_currentWorld);

            using (Pen previewPen = new Pen(Color.FromArgb(180, 0, 120, 215), 2f))
            {
                previewPen.DashStyle = DashStyle.Dash;
                g.DrawLine(previewPen, screenStart, screenEnd);
            }
        }

        public Cursor GetCursor()
        {
            return Cursors.Cross;
        }
    }
}
