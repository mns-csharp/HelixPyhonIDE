namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    using HelixPythonNamespace.UI.Controls;
    public sealed class DockablePanel : UserControl
    {
        /// <summary>
        /// The caption's surface: a Panel that repaints in FULL whenever its size changes.
        ///
        /// <see cref="OnHeaderPaint"/> draws from the bar's own width — the rounded top corners and
        /// the 1px light edge both sit at the right-hand end. A plain Panel invalidates only the
        /// strip a resize newly exposes, so widening the dock left the PREVIOUS right edge painted
        /// where it was: a bright vertical hairline stranded in the middle of the caption, at
        /// whatever width the bar was last drawn at. Any control whose painting is a function of its
        /// size has to ask for this; the default is for controls whose painting is not.
        /// </summary>
        private sealed class CaptionPanel : Panel
        {
            public CaptionPanel()
            {
                SetStyle(ControlStyles.ResizeRedraw, true);
            }
        }

        private Panel _headerPanel;
        private Label _titleLabel;
        private readonly ToolTip _toolTip = new ToolTip();
        private Button _pinButton;
        private Button _collapseButton;
        private Button _floatButton;
        private Button _closeButton;
        private Panel _contentPanel;
        private bool _isPinned = true;
        private bool _isExpanded = true;
        private Timer _autoHideTimer;
        private const int HeaderHeight = ToolWindowChrome.HeaderHeight;
        private const int AutoHideDelayMs = 600;

        public DockablePanel()
        {
            InitializeControls();
        }

        public event EventHandler CollapseStateChanged;
        public event EventHandler PinStateChanged;
        public event EventHandler AutoHideRequested;
        public event EventHandler AutoShowRequested;
        public event EventHandler FloatRequested;

        /// <summary>Raised when the panel should return to its dock.</summary>
        public event EventHandler DockRequested;

        /// <summary>
        /// Raised by the header's ✕. Closes whatever the panel is currently SHOWING, which for a
        /// tabbed group is the active tab — the panel itself only goes away once nothing is left in
        /// it. Visual Studio puts this at the top with the title and leaves the tabs along the
        /// bottom carrying nothing but their labels; a ✕ on every tab is the document-tab idiom, and
        /// borrowing it here made a tool pane read as a row of documents.
        /// </summary>
        public event EventHandler CloseRequested;

        private bool _isFloating;

        /// <summary>
        /// Whether this panel is currently in a floating window. Set by the host when it floats or
        /// redocks the panel, so the header button can offer the OPPOSITE action.
        ///
        /// Without this the button only ever floated: once floating it re-raised float, which did
        /// nothing, and the only way back was a context menu nobody knew to right-click for. A
        /// one-way door on a tool window is a trap, not a feature.
        /// </summary>
        public bool IsFloating
        {
            get { return _isFloating; }
            set
            {
                if (_isFloating == value) return;
                _isFloating = value;
                if (_floatButton != null)
                {
                    _floatButton.Invalidate();
                    _toolTip.SetToolTip(_floatButton, _captions == CaptionStyle.ToolWindow
                        ? (_isFloating ? "Dock" : "Undock") : (_isFloating ? "Dock" : "Float"));
                }
                // A FLOATING panel wears the window's caption, not its own. Undocked, the panel
                // kept drawing its title bar inside a window that already had one — two bars, two
                // copies of the name, one inside the other, which reads as a panel nested in a
                // panel and is the first thing anyone notices about it.
                ApplyRailMode();
            }
        }
        public event EventHandler DockLeftRequested;
        public event EventHandler DockRightRequested;

        public string Title
        {
            get { return _titleLabel.Text; }
            set
            {
                _titleLabel.Text = value;
                if (_headerPanel != null) _headerPanel.Invalidate();
            }
        }

        public bool IsPinned
        {
            get { return _isPinned; }
            set
            {
                if (_isPinned == value) return;
                _isPinned = value;
                UpdatePinButton();
                ApplyRailMode();
                if (_collapseButton != null)
                {
                    _collapseButton.Invalidate();
                    if (_captions == CaptionStyle.ToolWindow)
                        _toolTip.SetToolTip(_collapseButton, _isPinned ? "Auto Hide" : "Dock (stop auto-hiding)");
                }
                if (PinStateChanged != null)
                {
                    PinStateChanged(this, EventArgs.Empty);
                }
            }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                _contentPanel.Visible = _isExpanded;
                ApplyRailMode();
                UpdateCollapseButton();
                if (CollapseStateChanged != null)
                {
                    CollapseStateChanged(this, EventArgs.Empty);
                }
            }
        }

        public int CollapsedHeight
        {
            get { return HeaderHeight + 2; }
        }

        /// <summary>How wide the panel is while it waits at the edge.</summary>
        public const int RailWidth = 26;

        /// <summary>
        /// True when this panel is docked against the RIGHT edge, so its auto-hide mark points that
        /// way. An icon of a panel folding into the left edge, on a panel docked right, describes
        /// somebody else's dock.
        /// </summary>
        public bool DockedOnRight { get; set; }

        /// <summary>What this panel's caption buttons mean.</summary>
        public enum CaptionStyle
        {
            /// <summary>Minimize / maximize / close — for a pane that really does both.</summary>
            Document = 0,

            /// <summary>Auto-hide / undock / close — what a TOOL window does.</summary>
            ToolWindow = 1,
        }

        private CaptionStyle _captions = CaptionStyle.Document;

        /// <summary>
        /// Which pair of verbs the two left caption buttons carry.
        ///
        /// Not a global change, because this control is not only used by tool windows: the AI
        /// Terminal is docked in one of these too, and there minimize and maximize are exactly what
        /// the buttons do — it collapses to its header and it maximizes into a tab. Repurposing
        /// them for everyone broke that in the same edit that fixed the Solution Explorer, which is
        /// what this property exists to stop.
        /// </summary>
        public CaptionStyle Captions
        {
            get { return _captions; }
            set
            {
                if (_captions == value) return;
                _captions = value;
                if (_collapseButton != null)
                {
                    _collapseButton.Invalidate();
                    _toolTip.SetToolTip(_collapseButton, _captions == CaptionStyle.ToolWindow
                        ? (_isPinned ? "Auto Hide" : "Dock (stop auto-hiding)") : "Minimize");
                }
                if (_floatButton != null)
                {
                    _floatButton.Invalidate();
                    _toolTip.SetToolTip(_floatButton, _captions == CaptionStyle.ToolWindow
                        ? (IsFloating ? "Dock" : "Undock") : "Float");
                }
            }
        }

        /// <summary>
        /// The panel is in auto-hide mode: it lives at the edge and comes out on hover.
        ///
        /// The same fact as "not pinned", named the way the user's button names it. Pinned is the
        /// stored state because everything already written against it — the hover machinery, the
        /// host's restore path — reads that.
        /// </summary>
        public bool IsAutoHidden
        {
            get { return !_isPinned; }
            set { IsPinned = !value; if (value) IsExpanded = false; }
        }

        /// <summary>True while the panel is sitting at the edge as a rail rather than open.</summary>
        public bool ShowingRail
        {
            get { return _captions == CaptionStyle.ToolWindow && !_isPinned && !_isExpanded; }
        }

        /// <summary>
        /// The title bar, so a host can make the panel DRAGGABLE.
        ///
        /// Docking by dragging is the gesture people arrive with — a tool window is moved by taking
        /// hold of its bar — and the buttons are the discoverable fallback rather than the only
        /// way. The bar is exposed rather than the drag being implemented here because only the
        /// host knows what the drop targets are.
        /// </summary>
        public Control TitleBar
        {
            get { return _headerPanel; }
        }

        /// <summary>
        /// Enables or disables the middle header button (the one that raises
        /// <see cref="FloatRequested"/>).
        ///
        /// A host that uses it as "maximize" must be able to grey it out once the panel already
        /// IS maximized — otherwise the button stays lit and does nothing, which reads as broken.
        /// </summary>
        public bool FloatButtonEnabled
        {
            get { return _floatButton != null && _floatButton.Enabled; }
            set { if (_floatButton != null) _floatButton.Enabled = value; }
        }

        /// <summary>
        /// Retitle the middle header button's tooltip. Hosts differ in what floating means to them
        /// — for a bottom-docked console it is "Maximize" — and a wrong tooltip is worse than none.
        /// </summary>
        public void SetFloatTooltip(string text)
        {
            if (_floatButton != null) _toolTip.SetToolTip(_floatButton, text);
        }

        public void SetContent(Control content)
        {
            if (content == null) return;
            _contentPanel.Controls.Clear();
            content.Dock = DockStyle.Fill;
            _contentPanel.Controls.Add(content);
        }

        /// <summary>
        /// Gets the content control hosted in this panel.
        /// </summary>
        public Control GetContent()
        {
            if (_contentPanel.Controls.Count > 0)
            {
                return _contentPanel.Controls[0];
            }
            return null;
        }

        private void InitializeControls()
        {
            SuspendLayout();

            // Header panel
            _headerPanel = new CaptionPanel();
            _headerPanel.Dock = DockStyle.Top;
            _headerPanel.Height = HeaderHeight;
            _headerPanel.BackColor = ToolWindowChrome.HeaderBack;
            _headerPanel.Padding = new Padding(6, 2, 2, 2);
            // Painted, not just filled: BackColor alone cannot give the bar its rounded top or its
            // inner light, and — as this panel found out — a plain Panel is exactly what LunaForm's
            // auto-styler repaints tan on its way past.
            _headerPanel.Paint += OnHeaderPaint;

            // Title label
            _titleLabel = new Label();
            _titleLabel.Dock = DockStyle.Fill;
            _titleLabel.Text = "Panel";
            _titleLabel.ForeColor = ToolWindowChrome.HeaderText;
            _titleLabel.Font = LunaFonts.UiBold;
            _titleLabel.BackColor = Color.Transparent;
            _titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            _titleLabel.AutoEllipsis = true;

            // Float button (rightmost)
            _floatButton = new Button();
            _floatButton.Dock = DockStyle.Right;
            _floatButton.Width = ToolWindowChrome.ButtonWidth;
            _floatButton.FlatStyle = FlatStyle.Flat;
            _floatButton.FlatAppearance.BorderSize = 0;
            _floatButton.FlatAppearance.MouseOverBackColor = ToolWindowChrome.ButtonHover;
            _floatButton.BackColor = Color.Transparent;
            _floatButton.ForeColor = ToolWindowChrome.HeaderText;
            _floatButton.Text = string.Empty;
            _floatButton.Paint += OnFloatButtonPaint;
            _floatButton.Click += OnFloatClick;
            _toolTip.SetToolTip(_floatButton, "Float");

            // Close button. Added LAST of the docked-right buttons so it lands on the outer edge:
            // WinForms lays docked children out from the end of the collection, so the last one
            // added takes the rightmost slot — where Visual Studio puts it.
            _closeButton = new Button();
            _closeButton.Dock = DockStyle.Right;
            _closeButton.Width = ToolWindowChrome.ButtonWidth;
            _closeButton.FlatStyle = FlatStyle.Flat;
            _closeButton.FlatAppearance.BorderSize = 0;
            _closeButton.FlatAppearance.MouseOverBackColor = ToolWindowChrome.ButtonHover;
            _closeButton.BackColor = Color.Transparent;
            _closeButton.ForeColor = ToolWindowChrome.HeaderText;
            _closeButton.Text = string.Empty;
            _closeButton.Paint += OnCloseButtonPaint;
            _closeButton.Click += OnCloseClick;
            _toolTip.SetToolTip(_closeButton, "Close");

            // Collapse button
            _collapseButton = new Button();
            _collapseButton.Dock = DockStyle.Right;
            _collapseButton.Width = ToolWindowChrome.ButtonWidth;
            _collapseButton.FlatStyle = FlatStyle.Flat;
            _collapseButton.FlatAppearance.BorderSize = 0;
            _collapseButton.FlatAppearance.MouseOverBackColor = ToolWindowChrome.ButtonHover;
            _collapseButton.BackColor = Color.Transparent;
            _collapseButton.ForeColor = ToolWindowChrome.HeaderText;
            _collapseButton.Text = string.Empty;
            _collapseButton.Paint += OnCollapseButtonPaint;
            _collapseButton.Click += OnAutoHideClick;
            _toolTip.SetToolTip(_collapseButton, "Minimize");

            // Pin button
            _pinButton = new Button();
            _pinButton.Dock = DockStyle.Right;
            _pinButton.Width = ToolWindowChrome.ButtonWidth;
            _pinButton.FlatStyle = FlatStyle.Flat;
            _pinButton.FlatAppearance.BorderSize = 0;
            _pinButton.FlatAppearance.MouseOverBackColor = ToolWindowChrome.ButtonHover;
            _pinButton.BackColor = Color.Transparent;
            _pinButton.ForeColor = ToolWindowChrome.HeaderText;
            _pinButton.Font = new Font("Wingdings", 8f);
            _pinButton.Click += OnPinClick;
            UpdatePinButton();

            // The title is PAINTED by OnHeaderPaint, not hosted as a Label: a Label on the header
            // gets its ForeColor reset to near-black by the application-wide theme pass, and white
            // text is not optional on a caption this dark. _titleLabel stays as the store behind
            // the Title property so callers and tests are unaffected.
            //
            // Added minimize, then float, then close: WinForms lays docked children out from the
            // end of the collection, so this is the order they appear in left to right.
            _headerPanel.Controls.Add(_collapseButton);
            _headerPanel.Controls.Add(_floatButton);
            _headerPanel.Controls.Add(_closeButton);
            foreach (Button captionButton in new[] { _floatButton, _collapseButton, _closeButton })
            {
                Button b = captionButton;
                b.MouseEnter += delegate { b.Invalidate(); };
                b.MouseLeave += delegate { b.Invalidate(); };
            }
            // The pin is built but NOT shown. Auto-hide was never wired to anything a user could
            // discover, so the tick only ever toggled its own glyph — a control that appears to do
            // something and does nothing is worse than no control. The button and its event stay so
            // IsPinned keeps working for callers until auto-hide is real.

            // Header context menu for dock position
            ContextMenuStrip headerMenu = new ContextMenuStrip();
            headerMenu.Items.Add("Dock Left", null, OnDockLeftClick);
            headerMenu.Items.Add("Dock Right", null, OnDockRightClick);
            headerMenu.Items.Add(new ToolStripSeparator());
            headerMenu.Items.Add("Float", null, OnFloatClick);
            _headerPanel.ContextMenuStrip = headerMenu;
            _titleLabel.ContextMenuStrip = headerMenu;

            // Content panel
            _contentPanel = new Panel();
            _contentPanel.Dock = DockStyle.Fill;

            Controls.Add(_contentPanel);
            Controls.Add(_headerPanel);

            // Auto-hide timer
            _autoHideTimer = new Timer();
            _autoHideTimer.Interval = AutoHideDelayMs;
            _autoHideTimer.Tick += OnAutoHideTimerTick;

            // Clicking the rail pins the panel back open — the pin glyph painted on it is a
            // promise that something happens there, and a promise a click has to keep.
            Click += delegate { if (ShowingRail) { IsPinned = true; IsExpanded = true; } };

            // Mouse tracking for auto-hide
            MouseLeave += OnPanelMouseLeave;
            MouseEnter += OnPanelMouseEnter;
            _headerPanel.MouseEnter += OnPanelMouseEnter;
            _headerPanel.MouseLeave += OnPanelMouseLeave;
            _titleLabel.MouseEnter += OnPanelMouseEnter;
            _titleLabel.MouseLeave += OnPanelMouseLeave;
            _contentPanel.MouseEnter += OnPanelMouseEnter;
            _contentPanel.MouseLeave += OnPanelMouseLeave;

            ResumeLayout(false);
        }

        /// <summary>
        /// The left caption button: AUTO HIDE, not minimize.
        ///
        /// Minimize is a document window's verb — it means "go to the taskbar and wait to be
        /// restored", and a tool panel has no taskbar to go to, which is why the button did
        /// nothing a user could name. What a panel does instead is slide to the edge it is docked
        /// against and come back when the pointer visits that edge; pressing this is how it is put
        /// into that state, and pressing it again (on the rail, or from the peeked panel) pins it
        /// back open.
        /// </summary>
        private void OnAutoHideClick(object sender, EventArgs e)
        {
            if (_captions != CaptionStyle.ToolWindow)
            {
                IsExpanded = !IsExpanded;     // a document pane really does minimize
                return;
            }
            IsPinned = !IsPinned;
            // Unpinning means "go to the edge now", not "go when the pointer next leaves".
            IsExpanded = IsPinned;
        }

        private void OnPinClick(object sender, EventArgs e)
        {
            IsPinned = !IsPinned;
        }

        /// <summary>
        /// The caption face: Luna blue, its top corners rounded, with a light inner line along the
        /// top and left. That inner line is the whole 3D effect — without it the bar is a flat
        /// rectangle of colour, and a flat rectangle does not read as a window's title bar.
        /// </summary>
        private void OnHeaderPaint(object sender, PaintEventArgs e)
        {
            Rectangle r = _headerPanel.ClientRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int radius = ToolWindowChrome.CornerRadius;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                // Rounded on top, square at the bottom — the bar meets the content there.
                path.AddArc(r.X, r.Y, radius * 2, radius * 2, 180, 90);
                path.AddArc(r.Right - radius * 2 - 1, r.Y, radius * 2, radius * 2, 270, 90);
                path.AddLine(r.Right - 1, r.Bottom, r.X, r.Bottom);
                path.CloseFigure();
                using (var face = new SolidBrush(ToolWindowChrome.HeaderBack)) g.FillPath(face, path);
                using (var light = new Pen(ToolWindowChrome.HeaderHighlight)) g.DrawPath(light, path);
            }

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            using (var edge = new Pen(ToolWindowChrome.HeaderEdge))
                g.DrawLine(edge, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);

            int right = _closeButton != null ? _closeButton.Left : r.Right;
            if (_floatButton != null && _floatButton.Left < right) right = _floatButton.Left;
            if (_collapseButton != null && _collapseButton.Left < right) right = _collapseButton.Left;
            var textRect = new Rectangle(r.X + 7, r.Y, Math.Max(0, right - r.X - 11), r.Height);
            using (var sf = new StringFormat {
                       LineAlignment = StringAlignment.Center,
                       Trimming = StringTrimming.EllipsisCharacter,
                       FormatFlags = StringFormatFlags.NoWrap })
            using (var text = new SolidBrush(ToolWindowChrome.HeaderText))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.DrawString(_titleLabel.Text ?? string.Empty, _titleLabel.Font, text, textRect, sf);
            }
        }

        // All three caption buttons are the same square sunken red button, differing only in the
        // mark they carry. They sit a few pixels apart, so anything else about them differing —
        // size, shape, treatment — reads as three unrelated controls rather than one row.
        private void OnCollapseButtonPaint(object sender, PaintEventArgs e)
        {
            PaintCaptionButton(e, _collapseButton,
                _captions != CaptionStyle.ToolWindow ? ToolWindowChrome.ChromeGlyph.Minimize
                : IsAutoHidden ? ToolWindowChrome.ChromeGlyph.PinHidden
                : ToolWindowChrome.ChromeGlyph.PinOpen,
                DockedOnRight);
        }

        private void OnFloatButtonPaint(object sender, PaintEventArgs e)
        {
            PaintCaptionButton(e, _floatButton,
                _captions != CaptionStyle.ToolWindow ? ToolWindowChrome.ChromeGlyph.Restore
                : IsFloating ? ToolWindowChrome.ChromeGlyph.Dock
                : ToolWindowChrome.ChromeGlyph.Undock);
        }

        private void OnCloseButtonPaint(object sender, PaintEventArgs e)
        {
            PaintCaptionButton(e, _closeButton, ToolWindowChrome.ChromeGlyph.PanelClose);
        }

        private void PaintCaptionButton(PaintEventArgs e, Button button, ToolWindowChrome.ChromeGlyph glyph)
        {
            PaintCaptionButton(e, button, glyph, false);
        }

        private void PaintCaptionButton(PaintEventArgs e, Button button,
                                        ToolWindowChrome.ChromeGlyph glyph, bool mirrored)
        {
            using (var back = new SolidBrush(ToolWindowChrome.HeaderBack))
                e.Graphics.FillRectangle(back, button.ClientRectangle);

            bool hot = button.ClientRectangle.Contains(button.PointToClient(Control.MousePosition));
            ToolWindowChrome.DrawChromeButton(
                e.Graphics, ToolWindowChrome.ButtonRectIn(button.ClientRectangle), glyph, hot, mirrored);
        }

        /// <summary>The header's ✕, so callers need not identify it by its glyph.</summary>
        public Button CloseButton { get { return _closeButton; } }

        private void OnCloseClick(object sender, EventArgs e)
        {
            if (CloseRequested != null) CloseRequested(this, EventArgs.Empty);
        }

        private void OnFloatClick(object sender, EventArgs e)
        {
            // Toggle: the same button that undocked is the one that docks again.
            if (_isFloating)
            {
                if (DockRequested != null) DockRequested(this, EventArgs.Empty);
                return;
            }
            if (FloatRequested != null)
            {
                FloatRequested(this, EventArgs.Empty);
            }
        }

        private void OnDockLeftClick(object sender, EventArgs e)
        {
            if (DockLeftRequested != null)
            {
                DockLeftRequested(this, EventArgs.Empty);
            }
        }

        private void OnDockRightClick(object sender, EventArgs e)
        {
            if (DockRightRequested != null)
            {
                DockRightRequested(this, EventArgs.Empty);
            }
        }

        private void UpdateCollapseButton()
        {
            // Marlett: 5 = down arrow (collapse), 6 = up arrow (expand)
            _collapseButton.Text = _isExpanded ? "5" : "6";
        }

        private void UpdatePinButton()
        {
            // Visual feedback for pin state
            if (_isPinned)
            {
                _pinButton.ForeColor = Color.FromArgb(0, 122, 204);
                _pinButton.Text = "ü"; // Wingdings pushpin (pinned)
            }
            else
            {
                _pinButton.ForeColor = Color.FromArgb(160, 160, 160);
                _pinButton.Text = "ü"; // Same icon, dimmed
            }
        }

        /// <summary>
        /// Hides the panel's chrome while it is a rail, and paints the rail itself.
        ///
        /// A 26px-wide caption bar with three buttons crushed into it is not a rail — it is a
        /// broken title bar. What belongs at the edge is the panel's NAME, turned on its side, the
        /// way every IDE that has this feature draws it.
        /// </summary>
        private void ApplyRailMode()
        {
            bool rail = ShowingRail;
            // Hidden while floating too: the window's own caption is the panel's caption then, and
            // the panel's buttons have nothing left to say — auto-hide is meaningless for a window,
            // and docking it again is what closing the window does.
            if (_headerPanel != null) _headerPanel.Visible = !rail && !_isFloating;
            if (_contentPanel != null) _contentPanel.Visible = _isExpanded;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!ShowingRail) return;

            var body = new Rectangle(0, 0, Width, Height);
            using (var back = new SolidBrush(ToolWindowChrome.HeaderBack)) e.Graphics.FillRectangle(back, body);

            // The pin, lying down, at the top of the rail: the same mark the caption carries, so the
            // way back is where the way out was.
            ToolWindowChrome.DrawChromeButton(e.Graphics,
                new Rectangle(body.X + 5, body.Y + 5, 15, 15), ToolWindowChrome.ChromeGlyph.PinHidden, false);

            // The name, reading bottom-to-top like a book spine.
            using (var ink = new SolidBrush(ToolWindowChrome.HeaderText))
            {
                var state = e.Graphics.Save();
                e.Graphics.TranslateTransform(body.X + Width - 5, body.Y + Height - 8);
                e.Graphics.RotateTransform(-90f);
                e.Graphics.DrawString(Title, LunaFonts.UiBold, ink, 0, 0);
                e.Graphics.Restore(state);
            }
        }

        private void OnPanelMouseEnter(object sender, EventArgs e)
        {
            _autoHideTimer.Stop();

            if (!_isPinned && !_isExpanded)
            {
                if (AutoShowRequested != null)
                {
                    AutoShowRequested(this, EventArgs.Empty);
                }
            }
        }

        private void OnPanelMouseLeave(object sender, EventArgs e)
        {
            if (!_isPinned && _isExpanded)
            {
                // Check if mouse is still within bounds (might have moved to child control)
                Point mousePos = PointToClient(Cursor.Position);
                if (!ClientRectangle.Contains(mousePos))
                {
                    _autoHideTimer.Start();
                }
            }
        }

        private void OnAutoHideTimerTick(object sender, EventArgs e)
        {
            _autoHideTimer.Stop();

            if (!_isPinned)
            {
                // Double-check mouse isn't inside
                Point mousePos = PointToClient(Cursor.Position);
                if (!ClientRectangle.Contains(mousePos))
                {
                    if (AutoHideRequested != null)
                    {
                        AutoHideRequested(this, EventArgs.Empty);
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_autoHideTimer != null)
                {
                    _autoHideTimer.Stop();
                    _autoHideTimer.Dispose();
                    _autoHideTimer = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
