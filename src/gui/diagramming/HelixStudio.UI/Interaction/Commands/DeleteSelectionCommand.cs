namespace HelixPythonNamespace.UI.Interaction.Commands
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class DeleteSelectionCommand : ICommand
    {
        private readonly SceneGraph _sceneGraph;
        private readonly EdgeRouter _edgeRouter;
        private readonly List<NodeVisual> _deletedNodes;
        private readonly List<EdgeVisual> _deletedEdges;
        private readonly List<EdgeVisual> _connectedEdges;

        public DeleteSelectionCommand(
            SceneGraph sceneGraph,
            EdgeRouter edgeRouter,
            List<NodeVisual> nodesToDelete,
            List<EdgeVisual> edgesToDelete)
        {
            if (sceneGraph == null)
            {
                throw new ArgumentNullException("sceneGraph");
            }
            if (edgeRouter == null)
            {
                throw new ArgumentNullException("edgeRouter");
            }

            _sceneGraph = sceneGraph;
            _edgeRouter = edgeRouter;

            _deletedNodes = new List<NodeVisual>();
            if (nodesToDelete != null)
            {
                for (int i = 0; i < nodesToDelete.Count; i++)
                {
                    _deletedNodes.Add(nodesToDelete[i]);
                }
            }

            _deletedEdges = new List<EdgeVisual>();
            if (edgesToDelete != null)
            {
                for (int i = 0; i < edgesToDelete.Count; i++)
                {
                    _deletedEdges.Add(edgesToDelete[i]);
                }
            }

            // Will be populated during Execute to track edges connected to deleted nodes
            _connectedEdges = new List<EdgeVisual>();
        }

        public string Description
        {
            get { return "Delete selection"; }
        }

        public List<EdgeVisual> DeletedEdges
        {
            get { return _deletedEdges; }
        }

        public List<EdgeVisual> ConnectedEdges
        {
            get { return _connectedEdges; }
        }

        public List<NodeVisual> DeletedNodes
        {
            get { return _deletedNodes; }
        }

        public void Execute()
        {
            _connectedEdges.Clear();

            // Find and remove edges connected to nodes being deleted
            for (int i = 0; i < _deletedNodes.Count; i++)
            {
                NodeVisual node = _deletedNodes[i];
                List<EdgeVisual> connected = _sceneGraph.GetEdgesForNode(node.NodeId);
                for (int j = 0; j < connected.Count; j++)
                {
                    EdgeVisual edge = connected[j];
                    if (!_deletedEdges.Contains(edge) && !_connectedEdges.Contains(edge))
                    {
                        _connectedEdges.Add(edge);
                    }
                }
            }

            // Remove explicitly selected edges
            for (int i = 0; i < _deletedEdges.Count; i++)
            {
                _sceneGraph.RemoveEdge(_deletedEdges[i]);
            }

            // Remove edges connected to deleted nodes
            for (int i = 0; i < _connectedEdges.Count; i++)
            {
                _sceneGraph.RemoveEdge(_connectedEdges[i]);
            }

            // Remove nodes
            for (int i = 0; i < _deletedNodes.Count; i++)
            {
                _sceneGraph.RemoveNode(_deletedNodes[i]);
            }
        }

        public void Undo()
        {
            // Re-add nodes
            for (int i = 0; i < _deletedNodes.Count; i++)
            {
                _sceneGraph.AddNode(_deletedNodes[i]);
            }

            // Re-add explicitly deleted edges
            for (int i = 0; i < _deletedEdges.Count; i++)
            {
                _sceneGraph.AddEdge(_deletedEdges[i]);
            }

            // Re-add connected edges that were removed due to node deletion
            for (int i = 0; i < _connectedEdges.Count; i++)
            {
                _sceneGraph.AddEdge(_connectedEdges[i]);
            }

            // Re-route all edges
            _edgeRouter.RouteAllEdges(_sceneGraph);
        }
    }
}
