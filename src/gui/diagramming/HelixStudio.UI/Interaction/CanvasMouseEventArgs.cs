namespace HelixPythonNamespace.UI.Interaction
{
    using System.Drawing;
    using System.Windows.Forms;

    public sealed class CanvasMouseEventArgs
    {
        private readonly MouseButtons _button;
        private readonly PointF _screenLocation;
        private readonly PointF _worldLocation;
        private readonly Keys _modifierKeys;

        public CanvasMouseEventArgs(MouseButtons button, PointF screenLocation, PointF worldLocation, Keys modifierKeys)
        {
            _button = button;
            _screenLocation = screenLocation;
            _worldLocation = worldLocation;
            _modifierKeys = modifierKeys;
        }

        public MouseButtons Button
        {
            get { return _button; }
        }

        public PointF ScreenLocation
        {
            get { return _screenLocation; }
        }

        public PointF WorldLocation
        {
            get { return _worldLocation; }
        }

        public Keys ModifierKeys
        {
            get { return _modifierKeys; }
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
