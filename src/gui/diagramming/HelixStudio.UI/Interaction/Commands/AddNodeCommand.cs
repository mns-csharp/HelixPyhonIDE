namespace HelixPythonNamespace.UI.Interaction.Commands
{
    using System;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class AddNodeCommand : ICommand
    {
        private readonly SceneGraph _sceneGraph;
        private readonly NodeVisual _node;
        private readonly EdgeRouter _edgeRouter;

        public AddNodeCommand(SceneGraph sceneGraph, NodeVisual node, EdgeRouter edgeRouter)
        {
            if (sceneGraph == null)
            {
                throw new ArgumentNullException("sceneGraph");
            }
            if (node == null)
            {
                throw new ArgumentNullException("node");
            }
            if (edgeRouter == null)
            {
                throw new ArgumentNullException("edgeRouter");
            }

            _sceneGraph = sceneGraph;
            _node = node;
            _edgeRouter = edgeRouter;
        }

        public string Description
        {
            get { return "Add node"; }
        }

        public void Execute()
        {
            _sceneGraph.AddNode(_node);
        }

        public void Undo()
        {
            _sceneGraph.RemoveNode(_node);
        }
    }
}
