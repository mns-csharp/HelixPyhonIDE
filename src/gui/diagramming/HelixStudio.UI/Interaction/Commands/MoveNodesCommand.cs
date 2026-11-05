namespace HelixPythonNamespace.UI.Interaction.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class MoveNodesCommand : ICommand
    {
        private readonly SceneGraph _sceneGraph;
        private readonly Dictionary<string, PointF> _originalPositions;
        private readonly Dictionary<string, PointF> _finalPositions;
        private readonly EdgeRouter _edgeRouter;

        public MoveNodesCommand(
            SceneGraph sceneGraph,
            Dictionary<string, PointF> originalPositions,
            Dictionary<string, PointF> finalPositions,
            EdgeRouter edgeRouter)
        {
            if (sceneGraph == null)
            {
                throw new ArgumentNullException("sceneGraph");
            }
            if (originalPositions == null)
            {
                throw new ArgumentNullException("originalPositions");
            }
            if (finalPositions == null)
            {
                throw new ArgumentNullException("finalPositions");
            }
            if (edgeRouter == null)
            {
                throw new ArgumentNullException("edgeRouter");
            }

            _sceneGraph = sceneGraph;
            _originalPositions = new Dictionary<string, PointF>(originalPositions);
            _finalPositions = new Dictionary<string, PointF>(finalPositions);
            _edgeRouter = edgeRouter;
        }

        public string Description
        {
            get { return "Move nodes"; }
        }

        public void Execute()
        {
            foreach (KeyValuePair<string, PointF> pair in _finalPositions)
            {
                NodeVisual node = _sceneGraph.FindNode(pair.Key);
                if (node != null)
                {
                    node.SetPosition(pair.Value.X, pair.Value.Y);
                }
            }

            _edgeRouter.RouteAllEdges(_sceneGraph);
        }

        public void Undo()
        {
            foreach (KeyValuePair<string, PointF> pair in _originalPositions)
            {
                NodeVisual node = _sceneGraph.FindNode(pair.Key);
                if (node != null)
                {
                    node.SetPosition(pair.Value.X, pair.Value.Y);
                }
            }

            _edgeRouter.RouteAllEdges(_sceneGraph);
        }
    }
}
