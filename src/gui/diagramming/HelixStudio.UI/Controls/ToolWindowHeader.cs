namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    /// <summary>
    /// A tool-window title bar: the name on the left, a ✕ on the right.
    ///
    /// This is a control of its own rather than a <see cref="Panel"/> assembled at the call site
    /// because <see cref="LunaForm"/> auto-styles the exact types it recognises — every plain Panel
    /// becomes tan, every plain Button gets the Luna face — and a bar built out of those two came
    /// out repainted to match the form instead of matching the tool windows it sits beside. Being a
    /// distinct type is how a control says "I have already decided how I look"; the styler skips
    /// anything it does not recognise by exact type, which is exactly the contract wanted here.
    /// </summary>
    public sealed class ToolWindowHeader : Panel
    {
        private readonly Label _title;
        private readonly GlyphButton _close;
        private readonly ToolTip _tips = new ToolTip();

        /// <summary>Raised when the ✕ is clicked.</summary>
        public event EventHandler CloseClicked;

        public ToolWindowHeader(string title, Font titleFont)
        {
            // Painted from its own bounds, so a resize has to repaint the whole bar and not just the
            // strip it newly exposed. Same reason DockablePanel's caption asks for it.
            SetStyle(ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Top;
            Height = ToolWindowChrome.HeaderHeight;
            BackColor = ToolWindowChrome.HeaderBack;
            Padding = new Padding(4, 2, 2, 2);

            _title = new Label
            {
                Text = title ?? string.Empty,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ToolWindowChrome.HeaderText,
                Font = titleFont ?? LunaFonts.UiBold,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
            };

            _close = new GlyphButton
            {
                Dock = DockStyle.Right,
                Width = ToolWindowChrome.ButtonWidth,
                FlatStyle = FlatStyle.Flat,
                BackColor = ToolWindowChrome.HeaderBack,
                ForeColor = ToolWindowChrome.HeaderText,
                Font = ToolWindowChrome.GlyphFont,
                Text = ToolWindowChrome.CloseGlyph,
                TabStop = false,
                UseVisualStyleBackColor = false,
            };
            _close.FlatAppearance.BorderSize = 0;
            _close.FlatAppearance.MouseOverBackColor = ToolWindowChrome.ButtonHover;
            _close.Click += delegate { if (CloseClicked != null) CloseClicked(this, EventArgs.Empty); };
            _tips.SetToolTip(_close, "Close");

            Controls.Add(_title);
            Controls.Add(_close);
        }

        /// <summary>The bar's caption.</summary>
        public string Title
        {
            get { return _title.Text; }
            set { _title.Text = value ?? string.Empty; }
        }

        /// <summary>The ✕ itself, so a host can reach it without walking the tree.</summary>
        public Button CloseButton { get { return _close; } }

        /// <summary>
        /// The face is painted rather than left to <see cref="Control.BackColor"/> so it tracks the
        /// ACTIVE accent. A colour assigned in the constructor is the theme as it stood at that
        /// instant: switching skin mid-session left this bar on the old accent, and any host that
        /// walks its children recolouring plain Panels could overwrite it outright.
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var face = new SolidBrush(ToolWindowChrome.HeaderBack))
                e.Graphics.FillRectangle(face, ClientRectangle);

            _title.ForeColor = ToolWindowChrome.HeaderText;
            _close.BackColor = ToolWindowChrome.HeaderBack;
            _close.ForeColor = ToolWindowChrome.HeaderText;
        }

        /// <summary>A Button the Luna auto-styler leaves alone. See the type comment above.</summary>
        private sealed class GlyphButton : Button
        {
        }
    }
}
