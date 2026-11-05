namespace HelixPythonNamespace.UI.Rendering
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed class SceneGraph
    {
        private readonly List<IRenderable> _renderables;
        private readonly List<NodeVisual> _nodes;
        private readonly List<EdgeVisual> _edges;
        private bool _sortDirty;

        public SceneGraph()
        {
            _renderables = new List<IRenderable>();
            _nodes = new List<NodeVisual>();
            _edges = new List<EdgeVisual>();
            _sortDirty = false;
        }

        public IList<NodeVisual> Nodes
        {
            get { return _nodes; }
        }

        public IList<EdgeVisual> Edges
        {
            get { return _edges; }
        }

        public void AddRenderable(IRenderable renderable)
        {
            if (renderable == null)
            {
                throw new ArgumentNullException("renderable");
            }
            _renderables.Add(renderable);
            _sortDirty = true;
        }

        public void AddNode(NodeVisual node)
        {
            if (node == null)
            {
                throw new ArgumentNullException("node");
            }
            _nodes.Add(node);
            _renderables.Add(node);
            _sortDirty = true;
        }

        public void RemoveNode(NodeVisual node)
        {
            if (node == null)
            {
                return;
            }
            _nodes.Remove(node);
            _renderables.Remove(node);
            node.Dispose();
        }

        public void AddEdge(EdgeVisual edge)
        {
            if (edge == null)
            {
                throw new ArgumentNullException("edge");
            }
            _edges.Add(edge);
            _renderables.Add(edge);
            _sortDirty = true;
        }

        public void RemoveEdge(EdgeVisual edge)
        {
            if (edge == null)
            {
                return;
            }
            _edges.Remove(edge);
            _renderables.Remove(edge);
            edge.Dispose();
        }

        public void RenderAll(Graphics g, ViewportTransform viewport)
        {
            if (_sortDirty)
            {
                _renderables.Sort(CompareByZOrder);
                _sortDirty = false;
            }

            // R1.2: Compute visible world rect for viewport culling
            RectangleF visibleWorld = viewport.GetVisibleWorldRect();

            for (int i = 0; i < _renderables.Count; i++)
            {
                IRenderable renderable = _renderables[i];

                // Perform viewport culling for Visual-derived objects
                Visual visual = renderable as Visual;
                if (visual != null)
                {
                    RectangleF bounds = visual.GetWorldBounds();
                    // Always render visuals with empty bounds (e.g. GridVisual)
                    // or visuals whose bounds intersect the visible viewport
                    if (bounds.Width > 0 && bounds.Height > 0)
                    {
                        if (!visibleWorld.IntersectsWith(bounds))
                        {
                            continue;
                        }
                    }
                }

                renderable.Render(g, viewport);
            }
        }

        public NodeVisual HitTestNode(PointF worldPoint, float tolerance)
        {
            // Reverse iterate so topmost nodes are hit first
            for (int i = _nodes.Count - 1; i >= 0; i--)
            {
                if (_nodes[i].HitTest(worldPoint, tolerance))
                {
                    return _nodes[i];
                }
            }
            return null;
        }

        public EdgeVisual HitTestEdge(PointF worldPoint, float tolerance)
        {
            // Reverse iterate so topmost edges are hit first
            for (int i = _edges.Count - 1; i >= 0; i--)
            {
                if (_edges[i].HitTest(worldPoint, tolerance))
                {
                    return _edges[i];
                }
            }
            return null;
        }

        public List<NodeVisual> FindNodesInRect(RectangleF worldRect)
        {
            List<NodeVisual> result = new List<NodeVisual>();
            for (int i = 0; i < _nodes.Count; i++)
            {
                RectangleF nodeBounds = _nodes[i].GetWorldBounds();
                if (worldRect.IntersectsWith(nodeBounds))
                {
                    result.Add(_nodes[i]);
                }
            }
            return result;
        }

        public NodeVisual FindNodeById(string nodeId)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].NodeId == nodeId)
                {
                    return _nodes[i];
                }
            }
            return null;
        }

        public EdgeVisual FindEdgeById(string edgeId)
        {
            for (int i = 0; i < _edges.Count; i++)
            {
                if (_edges[i].EdgeId == edgeId)
                {
                    return _edges[i];
                }
            }
            return null;
        }

        public NodeVisual FindNode(string nodeId)
        {
            return FindNodeById(nodeId);
        }

        public List<NodeVisual> GetAllNodes()
        {
            return new List<NodeVisual>(_nodes);
        }

        public List<EdgeVisual> GetEdgesForNode(string nodeId)
        {
            List<EdgeVisual> result = new List<EdgeVisual>();
            for (int i = 0; i < _edges.Count; i++)
            {
                if (_edges[i].SourceNodeId == nodeId || _edges[i].TargetNodeId == nodeId)
                {
                    result.Add(_edges[i]);
                }
            }
            return result;
        }

        public EdgeVisual CreateEdge(string sourceNodeId, string targetNodeId, RelationshipKind kind)
        {
            string edgeId = Guid.NewGuid().ToString();
            IEdgeStyleStrategy style = EdgeStyleFactory.GetStyle(kind);
            return new EdgeVisual(edgeId, sourceNodeId, targetNodeId, style);
        }

        public void AddVisual(IRenderable renderable)
        {
            AddRenderable(renderable);
        }

        public void Clear()
        {
            for (int i = 0; i < _renderables.Count; i++)
            {
                IDisposable disposable = _renderables[i] as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
            _renderables.Clear();
            _nodes.Clear();
            _edges.Clear();
            _sortDirty = false;
        }

        /// <summary>
        /// Compute the bounding rectangle that contains all nodes.
        /// </summary>
        /// <returns>The world-space bounds, or an empty rectangle if no nodes.</returns>
        public RectangleF ComputeContentBounds()
        {
            if (_nodes.Count == 0)
            {
                return RectangleF.Empty;
            }

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            for (int i = 0; i < _nodes.Count; i++)
            {
                RectangleF bounds = _nodes[i].GetWorldBounds();
                if (bounds.Left < minX) minX = bounds.Left;
                if (bounds.Top < minY) minY = bounds.Top;
                if (bounds.Right > maxX) maxX = bounds.Right;
                if (bounds.Bottom > maxY) maxY = bounds.Bottom;
            }

            return new RectangleF(minX, minY, maxX - minX, maxY - minY);
        }

        private static int CompareByZOrder(IRenderable a, IRenderable b)
        {
            if (a.ZOrder < b.ZOrder)
            {
                return -1;
            }
            if (a.ZOrder > b.ZOrder)
            {
                return 1;
            }
            return 0;
        }
    }
}
