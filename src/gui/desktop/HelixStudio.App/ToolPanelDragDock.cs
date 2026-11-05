namespace HelixPythonNamespace.App
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    /// <summary>
    /// Docking by DRAGGING: take hold of a tool window's tab or its title bar, pull it to the side
    /// of the shell you want it on, and drop.
    ///
    /// The buttons and the View menu still do it, and they remain the discoverable route — but they
    /// are not the gesture anyone arrives with. A tool window is moved the way a window is moved:
    /// by picking it up. This is a deliberately small implementation of that idea rather than a
    /// general docking framework — there are exactly two sites, so the only question a drop has to
    /// answer is "which half of the workspace did it land in".
    ///
    /// What the user sees while dragging is the PANEL ITSELF, translucent, the size it will be:
    /// its title bar, its name, its frame. Not a label chip — a chip says "a drag is happening" and
    /// leaves the two questions that matter unanswered, which are what is moving and how big it
    /// lands. Over open ground the frame floats under the pointer at the size the panel is now;
    /// over a dock site it SNAPS to that site, so the rectangle on screen is the rectangle you get.
    /// </summary>
    internal sealed class ToolPanelDragDock
    {
        /// <summary>How far the pointer must travel before a click becomes a drag.</summary>
        private const int DragThreshold = 6;

        private readonly Form _shell;
        private readonly Func<Point, Rectangle> _zoneOf;
        private readonly Func<Point, bool> _zoneIsLeft;
        private readonly Action<string, bool> _drop;
        private readonly Func<Point, string> _panelAt;
        private readonly Func<string, Rectangle> _frameOf;
        private readonly Action<string> _say;

        private DragGhost _ghost;
        private Point _origin;
        private Size _frame;
        private Size _grabOffset;
        private string _dragging;
        private bool _armed;
        private Control _source;
        private Cursor _cursorBefore;

        /// <param name="shell">The window the drop zones are measured in.</param>
        /// <param name="panelAt">Which panel the gesture started on, given a screen point.</param>
        /// <param name="zoneOf">The rectangle (screen coords) that would take a drop at this point.</param>
        /// <param name="zoneIsLeft">Whether a drop at this point means the left-hand dock.</param>
        /// <param name="drop">Apply the move: panel title, and true for the left dock.</param>
        /// <param name="frameOf">The panel's rectangle on screen, so the trail is its real size.</param>
        /// <param name="say">Status-bar line while the drag is live; null to say nothing.</param>
        internal ToolPanelDragDock(Form shell, Func<Point, string> panelAt, Func<Point, Rectangle> zoneOf,
                                   Func<Point, bool> zoneIsLeft, Action<string, bool> drop,
                                   Func<string, Rectangle> frameOf, Action<string> say)
        {
            _shell = shell;
            _panelAt = panelAt;
            _zoneOf = zoneOf;
            _zoneIsLeft = zoneIsLeft;
            _drop = drop;
            _frameOf = frameOf;
            _say = say;
        }

        /// <summary>
        /// Whether the drag may put windows on screen.
        ///
        /// A test host runs the real gesture — that is the point of testing it — and must not paint
        /// a translucent blue rectangle across the developer's desktop while doing so. The same
        /// switch the IDE uses to stay out of a non-interactive session's way governs this.
        /// </summary>
        private static bool VisualsAllowed
        {
            get { return Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE") != "1"; }
        }

        /// <summary>The panel this drag is carrying, or null when no drag is in progress.</summary>
        internal string Carrying { get { return _armed ? _dragging : null; } }

        /// <summary>Makes <paramref name="handle"/> a place the user can pick a tool window up by.</summary>
        internal void MakeDraggable(Control handle)
        {
            if (handle == null) return;
            handle.MouseDown += OnMouseDown;
            handle.MouseMove += OnMouseMove;
            handle.MouseUp += OnMouseUp;
            // A drag that ends outside any control — released over another window, or interrupted —
            // must still put the trail and the highlight away.
            handle.MouseCaptureChanged += delegate { if (!handle.Capture) Cancel(); };
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _source = (Control)sender;
            _origin = _source.PointToScreen(e.Location);
            _dragging = _panelAt(_origin);
            _armed = _dragging != null;
            if (!_armed) return;

            // TAKE THE MOUSE. A drag is only a drag if the moves that follow keep arriving after
            // the pointer has left the control it started on — and the tab strips here are native
            // TabControls, which do their own thing with the button and do not hold it for us. With
            // no capture the gesture works exactly as far as the edge of the strip and then stops
            // dead: no trail, and a release that reaches nothing. Synthesised mouse events cannot
            // see this, because they are delivered to the control by hand.
            _source.Capture = true;

            // The frame is measured once, at the moment it is picked up: the panel is about to be
            // taken out of its dock, and a size read after that is the size of whatever is left.
            Rectangle frame = _frameOf != null ? _frameOf(_dragging) : Rectangle.Empty;
            if (frame.Width < 80 || frame.Height < 60) frame = new Rectangle(_origin.X - 60, _origin.Y - 12, 300, 460);
            _frame = new Size(Math.Min(frame.Width, MaxGhostWidth), Math.Min(frame.Height, MaxGhostHeight));
            // Held where it was grabbed, the way a window is: the pointer keeps its place on the
            // title bar instead of the frame jumping so its corner sits under the cursor.
            _grabOffset = new Size(
                Math.Max(0, Math.Min(_frame.Width - 12, _origin.X - frame.X)),
                Math.Max(0, Math.Min(_frame.Height - 6, _origin.Y - frame.Y)));
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_armed || e.Button != MouseButtons.Left) return;
            var source = (Control)sender;
            Point at = source.PointToScreen(e.Location);
            if (Math.Abs(at.X - _origin.X) < DragThreshold && Math.Abs(at.Y - _origin.Y) < DragThreshold) return;

            Rectangle zone = _zoneOf(at);
            bool lands = zone.Width > 0 && zone.Height > 0;

            // Over a site the frame IS the site — what you see is the shape it will take. Over open
            // ground it floats at the size it is now, held where you grabbed it.
            Rectangle where = lands
                ? zone
                : new Rectangle(at.X - _grabOffset.Width, at.Y - _grabOffset.Height, _frame.Width, _frame.Height);
            ShowGhost(where, lands ? (_zoneIsLeft(at) ? "Dock left" : "Dock right") : null, lands);
            Say(lands
                ? "Drop to dock " + _dragging + (_zoneIsLeft(at) ? " on the left" : " on the right")
                : "Moving " + _dragging + " — drop near the left or right edge to dock it");

            if (_cursorBefore == null) { _cursorBefore = source.Cursor; }
            source.Cursor = lands ? Cursors.SizeAll : Cursors.No;
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (!_armed) { Cancel(); return; }
            var source = (Control)sender;
            Point at = source.PointToScreen(e.Location);
            bool moved = Math.Abs(at.X - _origin.X) >= DragThreshold || Math.Abs(at.Y - _origin.Y) >= DragThreshold;
            string panel = _dragging;
            source.Capture = false;
            Cancel();

            if (!moved || panel == null) return;
            Rectangle zone = _zoneOf(at);
            if (zone.Width <= 0 || zone.Height <= 0) return;   // dropped somewhere that takes nothing
            _drop(panel, _zoneIsLeft(at));
        }

        private void Cancel()
        {
            if (_source != null && _source.Capture) _source.Capture = false;
            _armed = false;
            _dragging = null;
            if (_source != null && _cursorBefore != null) _source.Cursor = _cursorBefore;
            _cursorBefore = null;
            _source = null;
            HideGhost();
        }

        private void Say(string line)
        {
            if (_say != null) _say(line);
        }

        private void ShowGhost(Rectangle where, string action, bool landing)
        {
            ShowDropPreview(where, _dragging, action, landing);
        }

        /// <summary>
        /// The same trail, for a drag this class is not running: a FLOATING panel is moved by its
        /// window's own title bar, which Windows drags for us, and the preview has to come from
        /// somewhere. Shared rather than copied so the two gestures cannot drift apart.
        /// </summary>
        internal void ShowDropPreview(Rectangle where, string title, string action, bool landing)
        {
            if (!VisualsAllowed) return;
            if (_ghost == null || _ghost.IsDisposed) _ghost = new DragGhost(_shell);
            _ghost.Follow(where, title, action, landing);
        }

        internal void HideDropPreview()
        {
            HideGhost();
        }

        private void HideGhost()
        {
            if (_ghost != null && !_ghost.IsDisposed) _ghost.Hide();
        }

        private const int MaxGhostWidth = 460, MaxGhostHeight = 760;

        /// <summary>
        /// The panel being dragged, drawn as itself: a translucent frame with its title bar and its
        /// name, at the size it is now — or at the size of the dock site it is about to land in.
        ///
        /// A window rather than a rubber band on the shell: the ground it crosses is live controls
        /// that repaint themselves, and an XOR band over a repainting tree leaves streaks behind.
        /// </summary>
        private sealed class DragGhost : Form
        {
            private static readonly Color Accent = Color.FromArgb(51, 110, 180);
            private string _title = string.Empty;
            private string _action;
            private bool _landing;

            internal DragGhost(Form owner)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(246, 248, 252);
                Opacity = 0.5;
                Enabled = false;                 // never takes the mouse away from the drag
                if (owner != null && !owner.IsDisposed) Owner = owner;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            internal void Follow(Rectangle where, string title, string action, bool landing)
            {
                _title = title ?? string.Empty;
                _action = action;
                _landing = landing;
                Opacity = landing ? 0.45 : 0.55;
                Bounds = where;
                Invalidate();
                if (!Visible) Show();
                else BringToFront();
            }

            private string Caption()
            {
                return string.IsNullOrEmpty(_action) ? _title : _title + "  ·  " + _action;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var body = new Rectangle(0, 0, Width - 1, Height - 1);
                int header = Math.Min(ToolWindowChromeHeaderHeight, Math.Max(18, Height / 8));

                // The body: the panel's own surface, tinted where it is about to dock so the
                // rectangle reads as a landing site rather than as a window in flight.
                using (var face = new SolidBrush(_landing ? Color.FromArgb(206, 222, 244) : BackColor))
                    e.Graphics.FillRectangle(face, body);

                // Its title bar, because that is what makes a tool window recognisable at a glance.
                using (var bar = new SolidBrush(Accent))
                    e.Graphics.FillRectangle(bar, body.Left, body.Top, body.Width, header);
                TextRenderer.DrawText(e.Graphics, Caption(), Font,
                    new Rectangle(body.Left + 7, body.Top, body.Width - 10, header), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                using (var edge = new Pen(Accent, 2f)) e.Graphics.DrawRectangle(edge, body);
            }

            /// <summary>Matches the real panels' title bars; kept local so UI is not referenced here.</summary>
            private const int ToolWindowChromeHeaderHeight = 24;
        }
    }
}
