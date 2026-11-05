namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    /// <summary>
    /// A <see cref="TabControl"/> that lets its host paint the tab strip LAST.
    ///
    /// Owner-draw is not enough on its own. <c>DrawItem</c> hands over each tab's interior, clipped
    /// to it, and the control then draws its own raised bevel around every tab afterwards — so a
    /// flat design comes out embossed no matter what the item painter does, and inflating the fill
    /// only gets clipped back. Painting after <c>WM_PAINT</c> is the one place a caller can cover
    /// chrome it did not ask for.
    ///
    /// The strip is a row of labels naming tool windows, not a row of buttons, and the bevel was
    /// the only thing saying otherwise.
    /// </summary>
    public class ToolTabStrip : CompositedTabControl
    {
        private const int WM_PAINT = 0x000F;

        // DrawToBitmap does not go through WM_PAINT — it sends WM_PRINTCLIENT. Hooking only the
        // former leaves every screenshot showing the chrome the overlay exists to cover, which
        // makes a captured image disagree with the running window: the worst kind of evidence.
        private const int WM_PRINTCLIENT = 0x0318;

        /// <summary>Raised after the control has finished its own painting.</summary>
        public event PaintEventHandler PaintOverlay;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (PaintOverlay == null || !IsHandleCreated) return;

            if (m.Msg == WM_PAINT)
            {
                using (Graphics g = Graphics.FromHwnd(Handle))
                    PaintOverlay(this, new PaintEventArgs(g, ClientRectangle));
            }
            else if (m.Msg == WM_PRINTCLIENT && m.WParam != IntPtr.Zero)
            {
                using (Graphics g = Graphics.FromHdc(m.WParam))
                    PaintOverlay(this, new PaintEventArgs(g, ClientRectangle));
            }
        }
    }
}
