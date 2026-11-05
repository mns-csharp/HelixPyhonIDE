using System;
using System.Drawing;
using System.Windows.Forms;

namespace HelixPythonNamespace.Editor.Notebook
{
    /// <summary>
    /// The draggable edge between the notebook's side pane and the page.
    ///
    /// <para>
    /// <b>Why not <see cref="Splitter"/>.</b> The workspace used one, docked <c>Left</c> and added
    /// in the order WinForms documents for exactly this arrangement — and it landed in the wrong
    /// place. Measured on a realized form: the rail occupied 0–40, the splitter 40–44 and the pane
    /// 44–344, so the splitter sat BETWEEN the rail and the pane, and <c>Splitter.FindTarget()</c>
    /// returned the 40 px icon rail. Dragging it would have resized the icon strip; the boundary a
    /// user actually points at had nothing on it at all. Docking order among sibling <c>Left</c>
    /// controls depends on z-order and on which of them were visible when the layout first ran —
    /// and the pane starts hidden, which is a fragile thing for a control whose whole job is to be
    /// in one specific place.
    /// </para>
    ///
    /// <para>
    /// So this lives INSIDE the pane, docked <c>Right</c>. Its position is then a consequence of
    /// the pane's own bounds rather than of sibling ordering: it is on the pane's right edge
    /// because it IS the pane's right edge, it appears and disappears with the pane, and no
    /// z-order accident can move it.
    /// </para>
    /// </summary>
    internal sealed class PaneResizer : Panel
    {
        /// <summary>Wide enough to hit without aiming. Four pixels is a pixel-hunt.</summary>
        internal const int Thickness = 6;

        /// <summary>The pane never shrinks below this — a sliver reads as a rendering fault.</summary>
        internal const int MinimumPaneWidth = 160;

        /// <summary>And never grows so far that the notebook has nowhere left to be.</summary>
        internal const int MinimumPageWidth = 240;

        private static readonly Color Idle = Color.FromArgb(232, 234, 237);
        private static readonly Color Hot = Color.FromArgb(180, 186, 194);

        private readonly NotebookWorkspace _workspace;
        private bool _dragging;
        private int _grabScreenX;
        private int _widthAtGrab;

        internal PaneResizer(NotebookWorkspace workspace)
        {
            _workspace = workspace;
            Dock = DockStyle.Right;
            Width = Thickness;
            BackColor = Idle;
            Cursor = Cursors.VSplit;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // The cursor already says "draggable"; this says it before the pointer has been over a
            // 6 px strip long enough for anyone to read the cursor.
            BackColor = Hot;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_dragging) BackColor = Idle;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            Panel pane = Parent as Panel;
            if (pane == null) return;

            _dragging = true;
            _grabScreenX = ScreenX(e);
            _widthAtGrab = pane.Width;
            BackColor = Hot;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            ApplyWidth(_widthAtGrab + (ScreenX(e) - _grabScreenX));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            ApplyWidth(_widthAtGrab + (ScreenX(e) - _grabScreenX));
            EndDrag();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            EndDrag();
        }

        /// <summary>
        /// Where the pointer is, in SCREEN coordinates.
        ///
        /// <para>
        /// This control travels with the pane it is resizing, so <c>e.X</c> alone is measured from
        /// an origin that moves as the drag proceeds — anchor to that and the edge runs away from
        /// the pointer. <see cref="Control.PointToScreen(Point)"/> re-anchors every event against
        /// the control's CURRENT bounds, which cancels the movement out. Deliberately derived from
        /// the event rather than read from <c>Control.MousePosition</c>: the two agree during a real
        /// drag, and only the former lets the drag be driven through the very handlers a real one
        /// reaches.
        /// </para>
        /// </summary>
        private int ScreenX(MouseEventArgs e)
        {
            return IsHandleCreated ? PointToScreen(new Point(e.X, e.Y)).X : e.X;
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            BackColor = Idle;
        }

        /// <summary>
        /// Set the pane's width, clamped so neither side can be erased.
        ///
        /// <para>
        /// Both clamps matter, for different reasons. A pane dragged down to nothing looks like a
        /// rendering fault. A pane dragged across the whole window leaves no notebook AND no
        /// visible edge to drag back with, so the only way out would be the rail icon that closes
        /// the pane entirely — a resize that can only be undone by giving up is not a resize.
        /// </para>
        /// </summary>
        private void ApplyWidth(int wanted)
        {
            Panel pane = Parent as Panel;
            if (pane == null) return;

            int room = _workspace.ClientSize.Width - _workspace.RailWidth - MinimumPageWidth;
            int maximum = Math.Max(MinimumPaneWidth, room);
            int clamped = Math.Max(MinimumPaneWidth, Math.Min(wanted, maximum));

            if (pane.Width != clamped) pane.Width = clamped;
        }
    }
}
