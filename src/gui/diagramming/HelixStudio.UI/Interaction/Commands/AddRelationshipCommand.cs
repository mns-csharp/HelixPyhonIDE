namespace HelixPythonNamespace.UI.Interaction.Commands
{
    using System;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class AddRelationshipCommand : ICommand
    {
        private readonly SceneGraph _sceneGraph;
        private readonly EdgeVisual _edge;
        private readonly EdgeRouter _edgeRouter;

        public AddRelationshipCommand(SceneGraph sceneGraph, EdgeVisual edge, EdgeRouter edgeRouter)
        {
            if (sceneGraph == null)
            {
                throw new ArgumentNullException("sceneGraph");
            }
            if (edge == null)
            {
                throw new ArgumentNullException("edge");
            }
            if (edgeRouter == null)
            {
                throw new ArgumentNullException("edgeRouter");
            }

            _sceneGraph = sceneGraph;
            _edge = edge;
            _edgeRouter = edgeRouter;
        }

        public string Description
        {
            get { return "Add relationship"; }
        }

        public string SourceNodeId
        {
            get { return _edge.SourceNodeId; }
        }

        public string TargetNodeId
        {
            get { return _edge.TargetNodeId; }
        }

        public EdgeVisual Edge
        {
            get { return _edge; }
        }

        public void Execute()
        {
            _sceneGraph.AddEdge(_edge);
            _edgeRouter.RouteAllEdges(_sceneGraph);
        }

        public void Undo()
        {
            _sceneGraph.RemoveEdge(_edge);
        }
    }
}
