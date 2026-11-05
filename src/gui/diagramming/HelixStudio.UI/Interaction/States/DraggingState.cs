namespace HelixPythonNamespace.UI.Interaction.States
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Interaction.Commands;
    using HelixPythonNamespace.UI.Rendering;
    using GridSnapper = HelixPythonNamespace.UI.Rendering.GridSnapper;

    public sealed class DraggingState : IInteractionState
    {
        private readonly InteractionContext _context;
        private readonly Dictionary<string, PointF> _originalPositions;
        private readonly PointF _startWorldPoint;
        private PointF _lastWorldPoint;

        public DraggingState(InteractionContext context, PointF startWorldPoint)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            _context = context;
            _startWorldPoint = startWorldPoint;
            _lastWorldPoint = startWorldPoint;

            // Capture original positions of all selected nodes
            _originalPositions = new Dictionary<string, PointF>();
            IList<NodeVisual> selectedNodes = _context.Selection.SelectedNodes;
            for (int i = 0; i < selectedNodes.Count; i++)
            {
                NodeVisual node = selectedNodes[i];
                _originalPositions[node.NodeId] = new PointF(
                    node.WorldBounds.X, node.WorldBounds.Y);
            }
        }

        public void OnMouseDown(CanvasMouseEventArgs e)
        {
            // Not expected during dragging
        }

        public void OnMouseMove(CanvasMouseEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            bool shiftHeld = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

            float targetX = e.WorldLocation.X;
            float targetY = e.WorldLocation.Y;

            if (shiftHeld)
            {
                float totalDX = Math.Abs(e.WorldLocation.X - _startWorldPoint.X);
                float totalDY = Math.Abs(e.WorldLocation.Y - _startWorldPoint.Y);
                if (totalDX >= totalDY)
                {
                    targetY = _startWorldPoint.Y;
                }
                else
                {
                    targetX = _startWorldPoint.X;
                }
            }

            float deltaX = targetX - _lastWorldPoint.X;
            float deltaY = targetY - _lastWorldPoint.Y;
            _lastWorldPoint = new PointF(targetX, targetY);

            // Move all selected nodes by the delta. When a selected node has nested
            // children (FQN prefix match, thanks to the "+" nested separator), move
            // them with it so parent/child stay spatially coherent.
            IList<NodeVisual> selectedNodes = _context.Selection.SelectedNodes;
            HashSet<string> movedIds = new HashSet<string>();
            for (int i = 0; i < selectedNodes.Count; i++)
            {
                NodeVisual node = selectedNodes[i];
                if (!movedIds.Add(node.NodeId)) continue;
                RectangleF bounds = node.WorldBounds;
                node.SetPosition(bounds.X + deltaX, bounds.Y + deltaY);
                MoveNestedDescendants(node, deltaX, deltaY, movedIds);
            }

            // Re-route edges connected to moved nodes
            _context.EdgeRouter.RouteAllEdges(_context.SceneGraph);
            _context.InvalidateCanvas();
        }

        /// <summary>
        /// If <paramref name="child"/> is a nested type (FQN contains `+`) and its current
        /// bounds extend outside its parent's bounds, expand the parent to enclose it
        /// with a small padding. Called after drop to keep the container invariant.
        /// </summary>
        private void ExpandParentToEncloseIfNeeded(NodeVisual child)
        {
            if (child == null || string.IsNullOrEmpty(child.NodeId)) return;
            int plus = child.NodeId.LastIndexOf('+');
            if (plus < 0) return; // top-level node

            string parentFqn = child.NodeId.Substring(0, plus);
            NodeVisual parent = _context.SceneGraph.FindNodeById(parentFqn);
            if (parent == null) return;

            RectangleF pb = parent.WorldBounds;
            RectangleF cb = child.WorldBounds;
            if (pb.Contains(cb)) return;

            const float pad = 16f;
            float newRight = Math.Max(pb.Right, cb.Right + pad);
            float newBottom = Math.Max(pb.Bottom, cb.Bottom + pad);
            float newLeft = Math.Min(pb.X, cb.X - pad);
            float newTop = Math.Min(pb.Y, cb.Y - pad);
            RectangleF grown = new RectangleF(newLeft, newTop, newRight - newLeft, newBottom - newTop);
            parent.SetBounds(grown);
        }

        /// <summary>
        /// Translate all nested descendants of <paramref name="parent"/> by the given delta.
        /// Descendants are identified by NodeId prefix match against the parent's FQN
        /// (the nested "+" separator makes this unambiguous).
        /// </summary>
        private void MoveNestedDescendants(NodeVisual parent, float dx, float dy, HashSet<string> movedIds)
        {
            if (parent == null || string.IsNullOrEmpty(parent.NodeId)) return;
            string prefix = parent.NodeId + "+";
            IList<NodeVisual> nodes = _context.SceneGraph.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                NodeVisual n = nodes[i];
                if (n == null || string.IsNullOrEmpty(n.NodeId)) continue;
                if (!n.NodeId.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!movedIds.Add(n.NodeId)) continue;
                RectangleF b = n.WorldBounds;
                n.SetPosition(b.X + dx, b.Y + dy);
            }
        }

        public void OnMouseUp(CanvasMouseEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            // Composition gesture: if the drop point lies inside a non-selected type node,
            // ask the host (MainForm) whether to compose the dragged types as fields on that target.
            IList<NodeVisual> selectedNodes = _context.Selection.SelectedNodes;
            if (_context.CompositionHandler != null && selectedNodes.Count > 0)
            {
                HashSet<string> selectedIds = new HashSet<string>();
                for (int i = 0; i < selectedNodes.Count; i++) selectedIds.Add(selectedNodes[i].NodeId);

                float tol = _context.HitTestTolerance;
                NodeVisual target = _context.SceneGraph.HitTestNode(e.WorldLocation, tol);
                if (target != null && !selectedIds.Contains(target.NodeId))
                {
                    List<string> sourceIds = new List<string>(selectedIds);
                    InteractionContext.CompositionOutcome outcome =
                        _context.CompositionHandler(target.NodeId, sourceIds);

                    if (outcome == InteractionContext.CompositionOutcome.Composed ||
                        outcome == InteractionContext.CompositionOutcome.Cancelled)
                    {
                        // Roll back the drag: restore original positions for every selected node.
                        for (int i = 0; i < selectedNodes.Count; i++)
                        {
                            NodeVisual n = selectedNodes[i];
                            PointF orig;
                            if (_originalPositions.TryGetValue(n.NodeId, out orig))
                            {
                                n.SetPosition(orig.X, orig.Y);
                            }
                        }
                        _context.EdgeRouter.RouteAllEdges(_context.SceneGraph);
                        _context.InvalidateCanvas();
                        _context.TransitionTo(new IdleState(_context));
                        return;
                    }
                    // JustMove: fall through to normal drop flow.
                }
            }

            // Snap final positions to grid. For parents with nested descendants,
            // translate descendants by the same snap-delta so they don't slip.
            HashSet<string> snappedIds = new HashSet<string>();
            for (int i = 0; i < selectedNodes.Count; i++)
            {
                NodeVisual node = selectedNodes[i];
                if (!snappedIds.Add(node.NodeId)) continue;
                PointF before = new PointF(node.WorldBounds.X, node.WorldBounds.Y);
                PointF snapped = GridSnapper.SnapToGrid(before);
                float sdx = snapped.X - before.X;
                float sdy = snapped.Y - before.Y;
                node.SetPosition(snapped.X, snapped.Y);
                if (sdx != 0f || sdy != 0f)
                {
                    MoveNestedDescendants(node, sdx, sdy, snappedIds);
                }
            }

            // If any dragged node has a parent in the scene and now sits outside the
            // parent's bounds, grow the parent to enclose it. Keeps the visual "container"
            // invariant intact.
            for (int i = 0; i < selectedNodes.Count; i++)
            {
                NodeVisual node = selectedNodes[i];
                ExpandParentToEncloseIfNeeded(node);
            }

            // Re-route edges after snapping
            _context.EdgeRouter.RouteAllEdges(_context.SceneGraph);
            _context.InvalidateCanvas();

            // Capture final (snapped) positions
            Dictionary<string, PointF> finalPositions = new Dictionary<string, PointF>();
            for (int i = 0; i < selectedNodes.Count; i++)
            {
                NodeVisual node = selectedNodes[i];
                finalPositions[node.NodeId] = new PointF(
                    node.WorldBounds.X, node.WorldBounds.Y);
            }

            // Create the command but don't re-execute (move already happened visually)
            MoveNodesCommand cmd = new MoveNodesCommand(
                _context.SceneGraph, _originalPositions, finalPositions, _context.EdgeRouter);
            _context.CommandDispatcher.PushWithoutExecute(cmd);

            // Transition back to idle
            _context.TransitionTo(new IdleState(_context));
        }

        public void OnMouseWheel(CanvasMouseWheelEventArgs e)
        {
            // Ignore wheel during drag
        }

        public void OnKeyDown(KeyEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                // Restore original positions
                IList<NodeVisual> selectedNodes = _context.Selection.SelectedNodes;
                for (int i = 0; i < selectedNodes.Count; i++)
                {
                    NodeVisual node = selectedNodes[i];
                    PointF original;
                    if (_originalPositions.TryGetValue(node.NodeId, out original))
                    {
                        node.SetPosition(original.X, original.Y);
                    }
                }

                _context.EdgeRouter.RouteAllEdges(_context.SceneGraph);
                _context.InvalidateCanvas();
                _context.TransitionTo(new IdleState(_context));
            }
        }

        public void OnPaint(Graphics g, ViewportTransform viewport)
        {
            // No additional painting during drag
        }

        public Cursor GetCursor()
        {
            return Cursors.SizeAll;
        }
    }
}
