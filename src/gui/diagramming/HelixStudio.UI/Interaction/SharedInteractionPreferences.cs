namespace HelixPythonNamespace.UI.Interaction
{
    using System;

    /// <summary>
    /// Ambient preferences that span multiple DiagramCanvas / InteractionContext instances.
    /// Currently holds the active canvas tool so a single floating palette can drive every canvas.
    /// </summary>
    public sealed class SharedInteractionPreferences
    {
        private CanvasTool _activeTool;

        public SharedInteractionPreferences()
        {
            _activeTool = CanvasTool.Pan;
        }

        public event Action ActiveToolChanged;

        public CanvasTool ActiveTool
        {
            get { return _activeTool; }
            set
            {
                if (_activeTool == value) return;
                _activeTool = value;
                Action handler = ActiveToolChanged;
                if (handler != null) handler();
            }
        }
    }
}
