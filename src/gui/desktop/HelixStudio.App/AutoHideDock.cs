namespace HelixPythonNamespace.App
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// Auto-hide that OVERLAYS rather than pushes.
    ///
    /// A hidden panel leaves a rail at the edge and nothing else; peeking floats the panel back out
    /// ON TOP of whatever is beside it, and puts it away again. The document, the tabs, the toolbar
    /// and the status bar do not move a pixel through any of it.
    ///
    /// That is the whole reason this class exists. The first version of auto-hide moved the
    /// SPLITTER: hiding shrank the dock to a rail and peeking widened it again, so every peek
    /// re-laid-out the entire workspace — the editor jumped left, the canvas re-flowed, and a panel
    /// sliding in and out made the whole IDE twitch. A tool window appearing over the document is
    /// the only arrangement in which nothing else has to move.
    ///
    /// The splitter pane stays at rail width for as long as the panel is auto-hidden, so the layout
    /// underneath is settled once — when the user presses the button — and never again.
    /// </summary>
    internal sealed class AutoHideDock
    {
        /// <summary>How wide the panel comes out, when nothing better is remembered.</summary>
        private const int DefaultPeekWidth = 280;

        private readonly Form _shell;
        private readonly DockablePanel _panel;
        private readonly Func<Control> _slot;        // the splitter pane the panel lives in
        private readonly Func<int> _peekWidth;
        private readonly Action<int> _setSlotWidth;
        private readonly bool _onLeft;               // which edge it waits at, and slides out from

        private Rail _rail;
        private Control _dockedParent;
        private ClickAwayFilter _filter;

        internal AutoHideDock(Form shell, DockablePanel panel, Func<Control> slot,
                              Func<int> peekWidth, Action<int> setSlotWidth, bool onLeft)
        {
            _shell = shell;
            _panel = panel;
            _slot = slot;
            _peekWidth = peekWidth;
            _setSlotWidth = setSlotWidth;
            _onLeft = onLeft;
            // The click-away filter is APPLICATION-wide; it must not outlive the panel. A shell
            // closed while its panel was peeking left the filter registered, and the next click in
            // any other window threw ObjectDisposedException out of Owns (measured: six App.Tests
            // failed that way, each in whichever test pumped next after one that closed a form
            // with a panel out).
            _panel.Disposed += delegate
            {
                Peeking = false;
                if (_filter != null) Application.RemoveMessageFilter(_filter);
            };
        }

        /// <summary>True while the panel is out over the document rather than at the edge.</summary>
        internal bool Peeking { get; private set; }

        /// <summary>True while the panel is in auto-hide mode at all.</summary>
        internal bool Hidden { get { return _rail != null && _rail.Parent != null; } }

        /// <summary>The rail, for tests and for anything that needs to know where the edge is.</summary>
        internal Control RailControl { get { return _rail; } }

        /// <summary>
        /// Puts the panel at the edge: a rail takes its place in the splitter pane, and the panel
        /// itself becomes an overlay on the shell, waiting out of sight.
        /// </summary>
        internal void Enter()
        {
            if (Hidden) return;
            Control slot = _slot();
            if (slot == null) return;

            _dockedParent = _panel.Parent;
            if (_rail == null)
            {
                _rail = new Rail(_panel.Title) { Mirrored = !_onLeft };
                _rail.MouseEnter += delegate { Peek(); };
                _rail.Click += delegate { Peek(); };
            }
            _rail.Caption = _panel.Title;

            slot.Controls.Remove(_panel);
            _panel.Dock = DockStyle.None;
            _panel.Visible = false;
            _shell.Controls.Add(_panel);          // an overlay child of the form, positioned by hand

            _rail.Dock = DockStyle.Fill;
            slot.Controls.Add(_rail);
            _setSlotWidth(DockablePanel.RailWidth);
        }

        /// <summary>Takes the panel back out of auto-hide and returns it to its pane.</summary>
        internal void Leave()
        {
            if (!Hidden) return;
            Unpeek();
            Control slot = _rail.Parent;
            slot.Controls.Remove(_rail);

            _shell.Controls.Remove(_panel);
            _panel.Dock = DockStyle.Fill;
            _panel.Visible = true;
            (_dockedParent ?? slot).Controls.Add(_panel);
            _dockedParent = null;
            _setSlotWidth(Math.Max(DefaultPeekWidth, _peekWidth()));
        }

        /// <summary>Slides the panel out over the document. Nothing underneath it moves.</summary>
        internal void Peek()
        {
            if (!Hidden || Peeking) return;
            Control slot = _rail.Parent;
            if (slot == null) return;

            Rectangle edge = slot.RectangleToScreen(slot.ClientRectangle);
            int width = Math.Max(DefaultPeekWidth, _peekWidth());
            // Out of the edge it waits at: a left-hand panel slides right off its rail, a
            // right-hand one slides LEFT off its own. Placing both at the rail's right edge put the
            // right-hand panel half off the window.
            Point at = _shell.PointToClient(_onLeft
                ? new Point(edge.Right, edge.Top)
                : new Point(edge.Left - width, edge.Top));

            // EXPANDED, or the panel comes out as a rail the size of a panel: while it is waiting at
            // the edge it is collapsed, and a collapsed DockablePanel hides its content and paints
            // its whole body as the edge strip. Peeking without this produced a 366px slab of Luna
            // blue where the tree should have been — the panel was out, and drawing itself as if it
            // were still hiding.
            _panel.IsExpanded = true;
            _panel.Bounds = new Rectangle(at.X, at.Y, width, edge.Height);
            _panel.Visible = true;
            _panel.BringToFront();
            Peeking = true;

            // A click anywhere else puts it away — including on a menu, a tab, or the editor, which
            // is why this is a message filter and not a MouseDown handler on some parent: the click
            // that dismisses it is delivered to whatever the user actually clicked, and that control
            // has no idea a panel is peeking.
            if (_filter == null) _filter = new ClickAwayFilter(this);
            Application.AddMessageFilter(_filter);
        }

        /// <summary>Puts the panel back at the edge.</summary>
        internal void Unpeek()
        {
            if (!Peeking) return;
            Peeking = false;
            _panel.Visible = false;
            _panel.IsExpanded = false;      // back to waiting: collapsed, out of sight
            if (_filter != null) Application.RemoveMessageFilter(_filter);
        }

        /// <summary>True when <paramref name="screenPoint"/> is on the panel or its rail.</summary>
        internal bool Owns(Point screenPoint)
        {
            // Keyed off OUR state, not Control.Visible: a panel reports itself invisible whenever
            // any ancestor is, so "is the click on the panel" would answer no for reasons that have
            // nothing to do with where the click was.
            if (_panel.IsDisposed) return false;
            if (Peeking && _panel.RectangleToScreen(_panel.ClientRectangle).Contains(screenPoint)) return true;
            if (_rail != null && _rail.Parent != null
                && _rail.RectangleToScreen(_rail.ClientRectangle).Contains(screenPoint)) return true;
            return false;
        }

        /// <summary>
        /// Watches for a press anywhere outside the peeked panel.
        ///
        /// It does not swallow the message: the click the user made is theirs, and the panel going
        /// away is a side effect of it rather than a replacement for it.
        /// </summary>
        private sealed class ClickAwayFilter : IMessageFilter
        {
            private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204,
                              WM_MBUTTONDOWN = 0x0207, WM_NCLBUTTONDOWN = 0x00A1;

            private readonly AutoHideDock _owner;

            internal ClickAwayFilter(AutoHideDock owner) { _owner = owner; }

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_LBUTTONDOWN && m.Msg != WM_RBUTTONDOWN
                    && m.Msg != WM_MBUTTONDOWN && m.Msg != WM_NCLBUTTONDOWN) return false;
                if (!_owner.Owns(Cursor.Position)) _owner.Unpeek();
                return false;
            }
        }

        /// <summary>
        /// The strip left behind at the edge: the panel's name on its side, and a pin.
        ///
        /// Its own control rather than the panel drawing itself narrow, because the panel is not
        /// there any more — it is over the document, waiting — and something has to hold its place
        /// in the splitter so the rest of the layout stays exactly where it was.
        /// </summary>
        internal sealed class Rail : Control
        {
            private string _caption;

            /// <summary>True when this rail is the RIGHT-hand edge, so its mark points that way.</summary>
            internal bool Mirrored { get; set; }

            internal Rail(string caption)
            {
                _caption = caption ?? string.Empty;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
                BackColor = ToolWindowChrome.HeaderBack;
            }

            internal string Caption
            {
                get { return _caption; }
                set { _caption = value ?? string.Empty; Invalidate(); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var body = new Rectangle(0, 0, Width, Height);
                using (var back = new SolidBrush(ToolWindowChrome.HeaderBack))
                    e.Graphics.FillRectangle(back, body);
                ToolWindowChrome.DrawChromeButton(e.Graphics,
                    new Rectangle(body.X + 5, body.Y + 5, 15, 15),
                    ToolWindowChrome.ChromeGlyph.PinHidden, false, Mirrored);

                using (var ink = new SolidBrush(ToolWindowChrome.HeaderText))
                {
                    System.Drawing.Drawing2D.GraphicsState state = e.Graphics.Save();
                    e.Graphics.TranslateTransform(body.X + Width - 5, body.Y + Height - 8);
                    e.Graphics.RotateTransform(-90f);
                    e.Graphics.DrawString(_caption, LunaFonts.UiBold, ink, 0, 0);
                    e.Graphics.Restore(state);
                }
            }
        }
    }
}
