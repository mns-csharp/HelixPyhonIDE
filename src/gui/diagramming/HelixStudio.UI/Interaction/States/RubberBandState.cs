namespace HelixPythonNamespace.UI.Interaction.States
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class RubberBandState : IInteractionState
    {
        private readonly InteractionContext _context;
        private readonly PointF _anchorWorld;
        private PointF _currentWorld;

        public RubberBandState(InteractionContext context, PointF anchorWorld)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            _context = context;
            _anchorWorld = anchorWorld;
            _currentWorld = anchorWorld;
        }

        public void OnMouseDown(CanvasMouseEventArgs e)
        {
            // Not expected during rubber band
        }

        public void OnMouseMove(CanvasMouseEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            _currentWorld = e.WorldLocation;

            // Compute selection rectangle in world coordinates
            RectangleF selectionRect = GetWorldSelectionRect();

            // Find nodes within the rectangle and mark them selected
            List<NodeVisual> allNodes = _context.SceneGraph.GetAllNodes();
            for (int i = 0; i < allNodes.Count; i++)
            {
                NodeVisual node = allNodes[i];
                bool intersects = selectionRect.IntersectsWith(node.WorldBounds);
                node.IsSelected = intersects;
            }

            _context.InvalidateCanvas();
        }

        public void OnMouseUp(CanvasMouseEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            // Finalize selection via SelectionManager
            RectangleF selectionRect = GetWorldSelectionRect();
            List<NodeVisual> allNodes = _context.SceneGraph.GetAllNodes();

            for (int i = 0; i < allNodes.Count; i++)
            {
                NodeVisual node = allNodes[i];
                if (selectionRect.IntersectsWith(node.WorldBounds))
                {
                    _context.Selection.Select(node);
                }
                else
                {
                    node.IsSelected = false;
                }
            }

            _context.InvalidateCanvas();
            _context.TransitionTo(new IdleState(_context));
        }

        public void OnMouseWheel(CanvasMouseWheelEventArgs e)
        {
            // Ignore wheel during rubber band
        }

        public void OnKeyDown(KeyEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                // Cancel selection - deselect all nodes
                List<NodeVisual> allNodes = _context.SceneGraph.GetAllNodes();
                for (int i = 0; i < allNodes.Count; i++)
                {
                    allNodes[i].IsSelected = false;
                }

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

            RectangleF worldRect = GetWorldSelectionRect();
            RectangleF screenRect = viewport.WorldToScreen(worldRect);

            // Draw semi-transparent blue fill
            using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(40, 0, 120, 215)))
            {
                g.FillRectangle(fillBrush, screenRect);
            }

            // Draw dashed border
            using (Pen borderPen = new Pen(Color.FromArgb(180, 0, 120, 215), 1f))
            {
                borderPen.DashStyle = DashStyle.Dash;
                g.DrawRectangle(borderPen,
                    screenRect.X, screenRect.Y,
                    screenRect.Width, screenRect.Height);
            }
        }

        public Cursor GetCursor()
        {
            return Cursors.Cross;
        }

        private RectangleF GetWorldSelectionRect()
        {
            float x = Math.Min(_anchorWorld.X, _currentWorld.X);
            float y = Math.Min(_anchorWorld.Y, _currentWorld.Y);
            float width = Math.Abs(_currentWorld.X - _anchorWorld.X);
            float height = Math.Abs(_currentWorld.Y - _anchorWorld.Y);
            return new RectangleF(x, y, width, height);
        }
    }
}
