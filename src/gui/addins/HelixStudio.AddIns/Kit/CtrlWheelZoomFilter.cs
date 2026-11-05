namespace HelixPythonNamespace.AddIns.Kit
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    /// <summary>
    /// Ctrl+wheel anywhere OVER a design canvas, whether or not the canvas has the focus.
    ///
    /// <remarks>
    /// Windows delivers a wheel message to the FOCUSED control, and the focus is very often
    /// somewhere else on the panel: the user has just typed a width into the properties grid,
    /// or picked a framework from the combo, or clicked into the toolbox. Rolling the wheel over
    /// the drawing then zoomed nothing, or scrolled whatever control the focus had left behind —
    /// the one control that was not being pointed at. A filter is the only place this can be
    /// answered, because by the time the message reaches a control it has already gone to the
    /// wrong one.
    ///
    /// <para>It claims the message only when Ctrl is down AND the pointer is inside the canvas,
    /// so an ordinary wheel roll still scrolls whatever is under it.</para>
    /// </remarks>
    /// </summary>
    public sealed class CtrlWheelZoomFilter : IMessageFilter
    {
        private const int WmMouseWheel = 0x020A;
        private readonly Func<Control> _canvas;
        private readonly Action<Point, int> _zoomAbout;

        /// <summary>
        /// Where the pointer is, in screen coordinates. Defaults to the real
        /// <see cref="Control.MousePosition"/>.
        ///
        /// <para>Test seam: a test host cannot always place the real OS cursor reliably, so a test
        /// may overwrite this with a fixed point instead. Production code never sets it.</para>
        /// </summary>
        public Func<Point> Pointer = () => Control.MousePosition;

        /// <summary>
        /// Whether Ctrl is down. Defaults to the real <see cref="Control.ModifierKeys"/> check.
        ///
        /// <para>Test seam: holding a real key down for a test risks the same hazard as
        /// <c>SendKeys.SendWait</c> (the keystroke reaches whatever window actually has OS focus,
        /// not the test's off-screen form), so a test overrides this instead of touching the real
        /// keyboard. Production code never sets it.</para>
        /// </summary>
        public Func<bool> CtrlDown = () => (Control.ModifierKeys & Keys.Control) != 0;

        public CtrlWheelZoomFilter(Func<Control> canvas, Action<Point, int> zoomAbout)
        {
            _canvas = canvas;
            _zoomAbout = zoomAbout;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WmMouseWheel) return false;
            if (!CtrlDown()) return false;

            Control canvas = _canvas();
            if (canvas == null || !canvas.IsHandleCreated || !canvas.Visible) return false;

            Point screen = Pointer();
            if (!canvas.RectangleToScreen(canvas.ClientRectangle).Contains(screen)) return false;

            _zoomAbout(canvas.PointToClient(screen), (short)((long)m.WParam >> 16));
            return true;
        }
    }
}
