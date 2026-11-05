namespace HelixPythonNamespace.UI.Interaction
{
    using System.Drawing;
    using System.Windows.Forms;

    public sealed class CanvasMouseWheelEventArgs
    {
        private readonly int _delta;
        private readonly PointF _screenLocation;
        private readonly PointF _worldLocation;
        private readonly Keys _modifierKeys;

        public CanvasMouseWheelEventArgs(int delta, PointF screenLocation, PointF worldLocation)
            : this(delta, screenLocation, worldLocation, Keys.None)
        {
        }

        public CanvasMouseWheelEventArgs(int delta, PointF screenLocation, PointF worldLocation, Keys modifierKeys)
        {
            _delta = delta;
            _screenLocation = screenLocation;
            _worldLocation = worldLocation;
            _modifierKeys = modifierKeys;
        }

        public int Delta
        {
            get { return _delta; }
        }

        public PointF ScreenLocation
        {
            get { return _screenLocation; }
        }

        public PointF WorldLocation
        {
            get { return _worldLocation; }
        }

        public bool IsCtrlHeld
        {
            get { return (_modifierKeys & Keys.Control) == Keys.Control; }
        }

        public bool IsShiftHeld
        {
            get { return (_modifierKeys & Keys.Shift) == Keys.Shift; }
        }
    }
}
