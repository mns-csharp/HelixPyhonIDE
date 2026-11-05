namespace HelixPythonNamespace.UI.Interaction
{
    using System;
    using HelixPythonNamespace.UI.Rendering;

    public sealed class InteractionContext
    {
        private readonly SceneGraph _sceneGraph;
        private readonly ViewportTransform _viewport;
        private readonly SelectionManager _selection;
        private readonly CommandDispatcher _commandDispatcher;
        private readonly EdgeRouter _edgeRouter;
        private readonly Action _invalidateCanvas;
        private SharedInteractionPreferences _preferences;
        private IInteractionState _currentState;

        public InteractionContext(
            SceneGraph sceneGraph,
            ViewportTransform viewport,
            SelectionManager selection,
            CommandDispatcher commandDispatcher,
            EdgeRouter edgeRouter,
            Action invalidateCanvas)
            : this(sceneGraph, viewport, selection, commandDispatcher, edgeRouter, invalidateCanvas, null)
        {
        }

        public InteractionContext(
            SceneGraph sceneGraph,
            ViewportTransform viewport,
            SelectionManager selection,
            CommandDispatcher commandDispatcher,
            EdgeRouter edgeRouter,
            Action invalidateCanvas,
            SharedInteractionPreferences preferences)
        {
            if (sceneGraph == null)
            {
                throw new ArgumentNullException("sceneGraph");
            }
            if (viewport == null)
            {
                throw new ArgumentNullException("viewport");
            }
            if (selection == null)
            {
                throw new ArgumentNullException("selection");
            }
            if (commandDispatcher == null)
            {
                throw new ArgumentNullException("commandDispatcher");
            }
            if (edgeRouter == null)
            {
                throw new ArgumentNullException("edgeRouter");
            }
            if (invalidateCanvas == null)
            {
                throw new ArgumentNullException("invalidateCanvas");
            }

            _sceneGraph = sceneGraph;
            _viewport = viewport;
            _selection = selection;
            _commandDispatcher = commandDispatcher;
            _edgeRouter = edgeRouter;
            _invalidateCanvas = invalidateCanvas;
            _preferences = preferences ?? new SharedInteractionPreferences();
        }

        public SharedInteractionPreferences Preferences
        {
            get { return _preferences; }
            set { _preferences = value ?? new SharedInteractionPreferences(); }
        }

        /// <summary>
        /// Host-supplied hook called when a drag-drop lands one or more nodes on top
        /// of a different node and the user confirms composition. Returns true if the
        /// composition was applied (so the interaction should roll back the dragged
        /// node positions), false if the user chose "just move" (drag stands) or
        /// cancelled (caller rolls back positions).
        /// </summary>
        public enum CompositionOutcome
        {
            /// <summary>User cancelled — dragged positions should revert.</summary>
            Cancelled = 0,
            /// <summary>User composed — fields were added; dragged positions should revert.</summary>
            Composed = 1,
            /// <summary>User chose "just move" — dragged positions stand.</summary>
            JustMove = 2
        }

        public System.Func<string, System.Collections.Generic.IList<string>, CompositionOutcome> CompositionHandler { get; set; }

        public SceneGraph SceneGraph
        {
            get { return _sceneGraph; }
        }

        public ViewportTransform Viewport
        {
            get { return _viewport; }
        }

        public SelectionManager Selection
        {
            get { return _selection; }
        }

        public CommandDispatcher CommandDispatcher
        {
            get { return _commandDispatcher; }
        }

        public EdgeRouter EdgeRouter
        {
            get { return _edgeRouter; }
        }

        public IInteractionState CurrentState
        {
            get { return _currentState; }
        }

        public float HitTestTolerance
        {
            get { return 5f / _viewport.Zoom; }
        }

        public void TransitionTo(IInteractionState newState)
        {
            _currentState = newState;
        }

        public void InvalidateCanvas()
        {
            _invalidateCanvas();
        }
    }
}
