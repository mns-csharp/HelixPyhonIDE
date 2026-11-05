namespace HelixPythonNamespace.UI.Interaction.States
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Interaction.Commands;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class IdleState : IInteractionState
    {
        private const float DragThreshold = 4f;
        private const float ZoomFactor = 1.15f;

        private readonly InteractionContext _context;
        private PointF _mouseDownWorld;
        private PointF _mouseDownScreen;
        private bool _mouseIsDown;
        private MouseButtons _downButton;

        public IdleState(InteractionContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            _context = context;
        }

        public void OnMouseDown(CanvasMouseEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            _mouseDownWorld = e.WorldLocation;
            _mouseDownScreen = e.ScreenLocation;
            _mouseIsDown = true;
            _downButton = e.Button;
        }

        public void OnMouseMove(CanvasMouseEventArgs e)
        {
            if (e == null || !_mouseIsDown)
            {
                return;
            }

            float dx = e.ScreenLocation.X - _mouseDownScreen.X;
            float dy = e.ScreenLocation.Y - _mouseDownScreen.Y;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy);

            if (distance < DragThreshold)
            {
                return;
            }

            // Middle button => panning (for users with 3-button mice)
            if (_downButton == MouseButtons.Middle)
            {
                PanningState panState = new PanningState(_context, _mouseDownScreen);
                _context.TransitionTo(panState);
                _mouseIsDown = false;
                return;
            }

            if (_downButton == MouseButtons.Left)
            {
                // Check if mouse down was on a node
                float tolerance = _context.HitTestTolerance;
                NodeVisual hitNode = _context.SceneGraph.HitTestNode(_mouseDownWorld, tolerance);

                if (hitNode != null)
                {
                    // Ensure the node is selected before dragging
                    if (!hitNode.IsSelected)
                    {
                        if (!e.IsShiftHeld)
                        {
                            _context.Selection.ClearSelection();
                        }
                        _context.Selection.Select(hitNode);
                    }

                    DraggingState dragState = new DraggingState(_context, _mouseDownWorld);
                    _context.TransitionTo(dragState);
                    _mouseIsDown = false;
                    return;
                }

                // Empty space: the active tool decides — select tool always rubber-bands,
                // pan tool always pans. Shift does NOT override the active tool.
                CanvasTool tool = _context.Preferences != null
                    ? _context.Preferences.ActiveTool
                    : CanvasTool.Pan;

                if (tool == CanvasTool.Select)
                {
                    RubberBandState rubberBand = new RubberBandState(_context, _mouseDownWorld);
                    _context.TransitionTo(rubberBand);
                    _mouseIsDown = false;
                    return;
                }
                else
                {
                    PanningState panState = new PanningState(_context, _mouseDownScreen);
                    _context.TransitionTo(panState);
                    _mouseIsDown = false;
                    return;
                }
            }
        }

        public void OnMouseUp(CanvasMouseEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            _mouseIsDown = false;

            if (_downButton != MouseButtons.Left)
            {
                return;
            }

            // This was a click (no drag threshold exceeded)
            float tolerance = _context.HitTestTolerance;

            // Try hit-testing a node first
            NodeVisual hitNode = _context.SceneGraph.HitTestNode(e.WorldLocation, tolerance);
            if (hitNode != null)
            {
                if (e.IsShiftHeld)
                {
                    _context.Selection.ToggleSelect(hitNode);
                }
                else
                {
                    _context.Selection.ClearSelection();
                    _context.Selection.Select(hitNode);
                }
                _context.InvalidateCanvas();
                return;
            }

            // Try hit-testing an edge
            EdgeVisual hitEdge = _context.SceneGraph.HitTestEdge(e.WorldLocation, tolerance);
            if (hitEdge != null)
            {
                if (e.IsShiftHeld)
                {
                    _context.Selection.ToggleSelect(hitEdge);
                }
                else
                {
                    _context.Selection.ClearSelection();
                    _context.Selection.Select(hitEdge);
                }
                _context.InvalidateCanvas();
                return;
            }

            // Clicked on empty space
            if (!e.IsShiftHeld)
            {
                _context.Selection.ClearSelection();
                _context.InvalidateCanvas();
            }
        }

        public void OnMouseWheel(CanvasMouseWheelEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            // CTRL+wheel: zoom (unchanged)
            if (e.IsCtrlHeld)
            {
                float currentZoom = _context.Viewport.Zoom;
                float newZoom = e.Delta > 0 ? currentZoom * ZoomFactor : currentZoom / ZoomFactor;
                _context.Viewport.SetZoom(newZoom, e.ScreenLocation);
                _context.InvalidateCanvas();
                return;
            }

            // Wheel-pan when Select tool is active. Pan tool leaves wheel as a no-op.
            if (_context.Preferences != null && _context.Preferences.ActiveTool == CanvasTool.Select)
            {
                float panPx = e.Delta / 3f;
                if (e.IsShiftHeld)
                {
                    _context.Viewport.Pan(panPx, 0f);
                }
                else
                {
                    _context.Viewport.Pan(0f, panPx);
                }
                _context.InvalidateCanvas();
            }
        }

        public void OnKeyDown(KeyEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.KeyCode == Keys.Delete)
            {
                if (!_context.Selection.IsEmpty)
                {
                    IList<NodeVisual> selectedNodes = _context.Selection.SelectedNodes;
                    IList<EdgeVisual> selectedEdges = _context.Selection.SelectedEdges;

                    List<NodeVisual> nodesToDelete = new List<NodeVisual>();
                    for (int i = 0; i < selectedNodes.Count; i++)
                    {
                        nodesToDelete.Add(selectedNodes[i]);
                    }

                    List<EdgeVisual> edgesToDelete = new List<EdgeVisual>();
                    for (int i = 0; i < selectedEdges.Count; i++)
                    {
                        edgesToDelete.Add(selectedEdges[i]);
                    }

                    DeleteSelectionCommand cmd = new DeleteSelectionCommand(
                        _context.SceneGraph, _context.EdgeRouter, nodesToDelete, edgesToDelete);
                    _context.CommandDispatcher.Execute(cmd);
                    _context.Selection.ClearSelection();
                    _context.InvalidateCanvas();
                }
                return;
            }

            if (e.Control && e.KeyCode == Keys.Z)
            {
                _context.CommandDispatcher.Undo();
                _context.InvalidateCanvas();
                return;
            }

            if (e.Control && e.KeyCode == Keys.Y)
            {
                _context.CommandDispatcher.Redo();
                _context.InvalidateCanvas();
                return;
            }

            // CTRL + Plus: zoom in
            if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add))
            {
                float newZoom = _context.Viewport.Zoom * ZoomFactor;
                PointF center = new PointF(
                    _context.Viewport.ViewportSize.Width / 2f,
                    _context.Viewport.ViewportSize.Height / 2f);
                _context.Viewport.SetZoom(newZoom, center);
                _context.InvalidateCanvas();
                return;
            }

            // CTRL + Minus: zoom out
            if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract))
            {
                float newZoom = _context.Viewport.Zoom / ZoomFactor;
                PointF center = new PointF(
                    _context.Viewport.ViewportSize.Width / 2f,
                    _context.Viewport.ViewportSize.Height / 2f);
                _context.Viewport.SetZoom(newZoom, center);
                _context.InvalidateCanvas();
                return;
            }
        }

        public void OnPaint(Graphics g, ViewportTransform viewport)
        {
            // No-op in idle state
        }

        public Cursor GetCursor()
        {
            if (_context.Preferences == null)
            {
                return Cursors.Default;
            }
            return _context.Preferences.ActiveTool == CanvasTool.Select
                ? Cursors.Cross
                : Cursors.Hand;
        }
    }
}
