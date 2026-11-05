namespace HelixPythonNamespace.UI.Interaction.States
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class PanningState : IInteractionState
    {
        private readonly InteractionContext _context;
        private PointF _lastScreenPoint;

        public PanningState(InteractionContext context, PointF startScreenPoint)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            _context = context;
            _lastScreenPoint = startScreenPoint;
        }

        public void OnMouseDown(CanvasMouseEventArgs e)
        {
            // Not expected during panning
        }

        public void OnMouseMove(CanvasMouseEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            float deltaX = e.ScreenLocation.X - _lastScreenPoint.X;
            float deltaY = e.ScreenLocation.Y - _lastScreenPoint.Y;

            _context.Viewport.Pan(deltaX, deltaY);
            _lastScreenPoint = e.ScreenLocation;
            _context.InvalidateCanvas();
        }

        public void OnMouseUp(CanvasMouseEventArgs e)
        {
            _context.TransitionTo(new IdleState(_context));
        }

        public void OnMouseWheel(CanvasMouseWheelEventArgs e)
        {
            // Ignore wheel during panning
        }

        public void OnKeyDown(KeyEventArgs e)
        {
            // No key handling during panning
        }

        public void OnPaint(Graphics g, ViewportTransform viewport)
        {
            // No additional painting during panning
        }

        public Cursor GetCursor()
        {
            return Cursors.Hand;
        }
    }
}
