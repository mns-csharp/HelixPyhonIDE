namespace HelixPythonNamespace.UI.Controls
{
    using System.Windows.Forms;

    /// <summary>
    /// A <see cref="TabControl"/> that does not flicker under the pointer.
    ///
    /// A TabControl hot-tracks: moving across it makes the native control repaint its tabs. When
    /// the host also paints — an owner-draw item, an overlay, a hover highlight — that is two
    /// visible passes per mouse move, and the native tab shows for a frame before the custom one
    /// covers it. That flash is what reads as the tab heads flickering.
    ///
    /// WS_EX_COMPOSITED buffers the whole sequence so only the finished result is ever shown. It is
    /// the right tool here rather than <see cref="ControlStyles.OptimizedDoubleBuffer"/>, which does
    /// not apply to a native control's OWN painting — and that is precisely the pass that shows.
    /// </summary>
    public class CompositedTabControl : TabControl
    {
        private const int WS_EX_COMPOSITED = 0x02000000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_COMPOSITED;
                return cp;
            }
        }
    }
}
