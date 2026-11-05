namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.ComponentModel;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    using HelixPythonNamespace.Core;
    using HelixPythonNamespace.UI.Controls;
    /// <summary>
    /// Base form that replaces the OS-native title bar with a hand-drawn Windows XP
    /// "Luna" (Blue) caption + frame, while keeping native window behaviour (drag,
    /// edge-resize, Aero Snap, maximize) via WM_NCHITTEST. Every window in the app
    /// derives from this so the XP look is consistent top to bottom.
    ///
    /// The colors here mirror HelixPythonNamespace.App.LunaColors; they are duplicated rather
    /// than referenced because LunaColors lives in the App assembly, which this
    /// (lower-level) UI assembly cannot reference.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public partial class LunaForm : Form
    {
        /// <summary>Disables window chrome when a form is hosted as an IDE document surface.</summary>
        public bool EmbeddedSurface { get; set; }
        // ----- Win32 -----
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCACTIVATE = 0x0086;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_WINDOWPOSCHANGED = 0x0047;
        private const int WM_SETTEXT = 0x000C;
        private const int WM_SETICON = 0x0080;

        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        // ----- Accent colors: read live from the active Luna theme (Blue / Green / Crimson) so a
        // theme switch recolors every window's caption + frame. Neutral tones below stay constant. -----
        private static Color CaptionActiveLeft { get { return LunaThemes.Current.CaptionLeft; } }
        private static Color CaptionActiveMid { get { return LunaThemes.Current.CaptionMid; } }
        private static Color CaptionActiveRight { get { return LunaThemes.Current.CaptionRight; } }
        private static Color CaptionActiveHighlight { get { return LunaThemes.Current.CaptionHighlight; } }
        private static Color CaptionInactiveLeft { get { return LunaThemes.Current.CaptionInactiveLeft; } }
        private static Color CaptionInactiveRight { get { return LunaThemes.Current.CaptionInactiveRight; } }
        private static Color FrameColor { get { return LunaThemes.Current.Frame; } }
        private static readonly Color TanFace = Color.FromArgb(236, 233, 216);
        private static readonly Color CaptionText = Color.White;
        private static readonly Color CaptionTextInactive = Color.FromArgb(228, 235, 250);
        private static readonly Color CloseHover = Color.FromArgb(232, 80, 70);
        private static readonly Color ButtonHover = Color.FromArgb(120, 255, 255, 255);

        // ----- Layout -----
        private int _captionH = 30;
        private int _frame = 4;
        private int _cornerRadius = 6;   // XP Luna rounds the TOP two corners; bottom stays square
        private const int ResizeGrip = 6;

        private bool _isActive = true;
        private bool _toolWindow;
        private int _hoverButton = -1;   // 0=min 1=max 2=close
        private int _pressedButton = -1;

        // Cached once per process: every LunaForm shares the same app icon, and loading
        // from the embedded resource is wasted work past the first construction.
        private static Icon _appIcon;

        public LunaForm()
        {
            InitializeComponent();
            // Reduce caption flicker on activate/resize. We deliberately do NOT flip
            // FormBorderStyle to None: keeping it (Sizable / FixedDialog / ToolWindow)
            // preserves the right window styles (WS_THICKFRAME etc.) so WM_NCHITTEST
            // resize and Aero Snap keep working; WM_NCCALCSIZE just hides the frame.
            DoubleBuffered = true;
            ResizeRedraw = true;
            // The shell UI font, so every child control inherits the OS typeface/size.
            Font = LunaFonts.Ui;
            Icon = GetAppIcon();
        }

        /// <summary>
        /// Loads the app icon from the resource embedded in this assembly (rather than,
        /// say, <c>Application.ExecutablePath</c>) so it resolves the same way whether
        /// this form is hosted inside HelixPythonNamespace.exe or a test harness process. Public so
        /// the few Form-derived (non-LunaForm) windows elsewhere in the app can share it.
        /// </summary>
        public static Icon GetAppIcon()
        {
            if (_appIcon == null)
            {
                try
                {
                    var asm = typeof(LunaForm).Assembly;
                    // Resolved by SEARCH, not by a literal. The literal used to be
                    // "HelixPythonNamespace.UI.app_main.ico" -- but the csproj declares
                    // <LogicalName>HelixStudio.UI.app_main.ico</LogicalName>, and the namespace
                    // migration renamed the code without renaming the resource. The lookup returned
                    // null, silently: every window fell back to the default WinForms glyph and the
                    // tray fell back to SystemIcons.Application, for as long as that mismatch stood.
                    // Nothing failed, so nothing reported it. Matching on the file name instead
                    // means the next rename cannot un-brand the product the same quiet way.
                    string resourceName = null;
                    foreach (string candidate in asm.GetManifestResourceNames())
                    {
                        if (candidate.EndsWith("app_main.ico", StringComparison.OrdinalIgnoreCase))
                        {
                            resourceName = candidate;
                            break;
                        }
                    }

                    if (resourceName != null)
                    {
                        using (var stream = asm.GetManifestResourceStream(resourceName))
                        {
                            if (stream != null)
                            {
                                _appIcon = new Icon(stream);
                            }
                        }
                    }
                }
                catch
                {
                    // Icon branding is best-effort; never block a form from opening over it.
                }
            }
            return _appIcon;
        }

        private static Icon _traySizedIcon;

        /// <summary>
        /// The application icon at the shell's small-icon size, for a <c>NotifyIcon</c>.
        ///
        /// <para>
        /// <see cref="GetAppIcon"/> hands back whichever frame the .ico nominates as its default,
        /// which is 32 px. A <c>NotifyIcon</c> passes the handle to the shell as-is, so a 32 px
        /// icon in a 16 px tray is reduced by the shell with a box filter -- and the tile's thin
        /// white glyph is exactly the detail that survives that worst. Selecting the frame here
        /// hands the tray the 16 px art that was reduced properly when the .ico was built.
        /// </para>
        /// <para>
        /// <see cref="Form.Icon"/> needs no such help: WinForms already asks the icon for a
        /// small-icon-sized copy when it sets the window's WM_SETICON pair.
        /// </para>
        /// </summary>
        public static Icon GetTrayIcon()
        {
            if (_traySizedIcon == null)
            {
                Icon source = GetAppIcon();
                if (source == null) return null;
                try { _traySizedIcon = new Icon(source, SystemInformation.SmallIconSize); }
                catch { _traySizedIcon = source; }
            }

            return _traySizedIcon;
        }

        private bool IsDesignerHosted
        {
            get { return DesignMode || LicenseManager.UsageMode == LicenseUsageMode.Designtime; }
        }

        /// <summary>
        /// Forms that already theme their own descendants (e.g. MainForm) override this
        /// to opt out of the conservative auto-styler.
        /// </summary>
        protected virtual bool AutoStyleChildControls { get { return true; } }

        /// <summary>
        /// Because WM_NCCALCSIZE gives the client area the whole window and we hand-draw the caption
        /// in the top <see cref="_captionH"/> pixels, a control positioned by absolute Location (the
        /// bulk of designer dialogs, e.g. a label at 12,12) renders UNDER the caption — Padding only
        /// offsets docked/anchored children, not absolutely-positioned ones. To fix every dialog at
        /// once we reparent all children into a Dock=Fill host that sits below the caption (inside the
        /// form's Padding), which preserves each form's exact relative layout. MainForm manages its own
        /// coordinator-driven docking and opts out.
        /// </summary>
        protected virtual bool UseLunaClientHost { get { return true; } }

        private Panel _lunaClientHost;

        /// <summary>
        /// The container a caller's children actually end up in.
        ///
        /// This form draws its own caption, so it quietly moves every child into a Dock=Fill host
        /// panel (see <see cref="EnsureClientHost"/>) — which means <c>form.Controls</c> holds the
        /// host, not what the caller added. Code that reparents content back out has to know that:
        /// <c>Controls.Remove(theContent)</c> matches nothing and silently succeeds, and
        /// <c>Controls[0]</c> hands back the chrome instead of the content. One caller hit the
        /// first form of that and disposed the Solution Explorer along with the window.
        /// </summary>
        public Control ContentHost
        {
            get { return _lunaClientHost != null ? (Control)_lunaClientHost : this; }
        }

        private bool IsResizable
        {
            get
            {
                return WindowState != FormWindowState.Maximized &&
                       (FormBorderStyle == FormBorderStyle.Sizable ||
                        FormBorderStyle == FormBorderStyle.SizableToolWindow);
            }
        }

        private bool HasMaxButton
        {
            get
            {
                return MaximizeBox && !_toolWindow &&
                       (FormBorderStyle == FormBorderStyle.Sizable ||
                        FormBorderStyle == FormBorderStyle.SizableToolWindow);
            }
        }

        private bool HasMinButton { get { return MinimizeBox && !_toolWindow; } }

        /// <summary>
        /// Show a fourth caption button that hides the window to the notification area.
        ///
        /// <para>Opt-in, and off by default, because LunaForm is the base of every dialog in the
        /// application and only the main window has anywhere to hide TO. A modal dialog with a
        /// "minimize to tray" button would be offering something that cannot work.</para>
        /// </summary>
        public bool ShowTrayButton { get; set; }

        /// <summary>Raised when the tray caption button is clicked. The host decides what "hide to
        /// the tray" means; this class only owns the button.</summary>
        public event EventHandler TrayRequested;

        private bool HasTrayButton { get { return ShowTrayButton && !_toolWindow; } }

        // In a test run only (TestEnvironment.OffScreen): a top-level LunaForm is layered at alpha 0
        // and never activated, so it can neither be seen nor take the user's mouse or keyboard
        // (book Chapter 4, 4.6.5). Unset, these return exactly what WinForms returns.
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle = TestEnvironment.ExStyle(cp.Style, cp.ExStyle);
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return !TestEnvironment.MayActivate || base.ShowWithoutActivation; }
        }

        /// <summary><see cref="Form.Activate"/>, except that a test run never takes the foreground.</summary>
        public new void Activate()
        {
            if (TestEnvironment.MayActivate) base.Activate();
        }

        /// <summary><see cref="Control.BringToFront"/>; in a test run a top-level window is raised
        /// without being activated.</summary>
        public new void BringToFront()
        {
            if (TestEnvironment.MayActivate || !TopLevel || !IsHandleCreated) { base.BringToFront(); return; }
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        /// <summary><see cref="Form.WindowState"/>; in a test run a minimized window is restored
        /// without being activated (the WinForms setter uses an activating SW_ command).</summary>
        public new FormWindowState WindowState
        {
            get { return base.WindowState; }
            set
            {
                if (!TestEnvironment.MayActivate && value == FormWindowState.Normal && IsHandleCreated && Visible
                    && base.WindowState == FormWindowState.Minimized)
                {
                    ShowWindow(Handle, SW_SHOWNOACTIVATE);
                    return;
                }
                base.WindowState = value;
            }
        }

        private const int SW_SHOWNOACTIVATE = 4;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TestEnvironment.HandleCreated(Handle, CreateParams.Style);
            if (IsDesignerHosted || EmbeddedSurface) { Padding = Padding.Empty; return; }
            _toolWindow = FormBorderStyle == FormBorderStyle.FixedToolWindow ||
                          FormBorderStyle == FormBorderStyle.SizableToolWindow;
            _captionH = (int)Math.Round((_toolWindow ? 20 : 28) * (DeviceDpi / 96f)) + 2;
            _frame = _toolWindow ? 3 : 4;
            _cornerRadius = Math.Max(3, (int)Math.Round(6 * (DeviceDpi / 96f)));
            UpdateChromePadding();
            UpdateRegion();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (IsDesignerHosted || EmbeddedSurface) return;
            BackColor = TanFace;
            EnsureClientHost();
            if (AutoStyleChildControls)
            {
                foreach (Control c in Controls) StyleTree(c);
            }
            UpdateChromePadding();
            Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (IsDesignerHosted || EmbeddedSurface) return;
            if (!TestEnvironment.ClampOnShown)
            {
                // A test run: off every screen instead (see CreateParams above).
                if (WindowState == FormWindowState.Normal && TopLevel && Visible)   // a laid-out, unshown form stays put
                    foreach (Screen screen in Screen.AllScreens)
                        if (screen.Bounds.IntersectsWith(Bounds))
                        {
                            Left = TestEnvironment.OffScreenLeft(Left, SystemInformation.VirtualScreen.Width);
                            break;
                        }
                return;
            }
            // Keep the whole window on the visible desktop — a dialog centered on a parent near a
            // screen edge, or simply taller than the work area, must not spill off-screen where its
            // (hand-drawn) caption can't be reached.
            if (WindowState == FormWindowState.Normal)
            {
                Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
                Rectangle clamped = ClampToArea(Bounds, wa);
                if (clamped != Bounds) Bounds = clamped;
            }
        }

        /// <summary>
        /// Fit <paramref name="window"/> inside <paramref name="area"/>: shrink it if larger, then
        /// slide it fully inside. Pure geometry so it can be unit-tested without a screen.
        /// </summary>
        public static Rectangle ClampToArea(Rectangle window, Rectangle area)
        {
            int w = Math.Min(window.Width, area.Width);
            int h = Math.Min(window.Height, area.Height);
            int x = window.X;
            int y = window.Y;
            if (x + w > area.Right) x = area.Right - w;
            if (y + h > area.Bottom) y = area.Bottom - h;
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            return new Rectangle(x, y, w, h);
        }

        // Move every existing child into a Dock=Fill host so absolutely-positioned controls land
        // below the hand-drawn caption (see UseLunaClientHost). Children keep their Location / Dock /
        // Anchor and z-order, so each form's layout is reproduced exactly, just shifted into the
        // content area. Runs once.
        private void EnsureClientHost()
        {
            if (_lunaClientHost != null || !UseLunaClientHost) return;
            if (Controls.Count == 0) return;

            var host = new Panel { Dock = DockStyle.Fill, BackColor = TanFace };

            var existing = new Control[Controls.Count];
            Controls.CopyTo(existing, 0);

            SuspendLayout();
            host.SuspendLayout();
            // Reparent in original order to preserve dock z-order (Fill vs Top/Bottom layering).
            foreach (Control c in existing)
            {
                Controls.Remove(c);
                host.Controls.Add(c);
            }
            Controls.Add(host);
            host.ResumeLayout(false);
            ResumeLayout(true);

            _lunaClientHost = host;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsDesignerHosted) return;
            UpdateChromePadding();
            UpdateRegion();
            Invalidate();
        }

        // Round only the top two corners (authentic XP "Luna"): a rounded-rect region whose bottom
        // edge is square. A maximized window fills the monitor edge-to-edge, so it uses no region.
        private void UpdateRegion()
        {
            if (IsDisposed || !IsHandleCreated) return;

            Region previous = Region;
            if (WindowState == FormWindowState.Maximized)
            {
                if (previous != null) { Region = null; previous.Dispose(); }
                return;
            }

            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;
            int d = Math.Min(_cornerRadius * 2, Math.Min(w, h));

            using (var path = new GraphicsPath())
            {
                path.StartFigure();
                path.AddArc(0, 0, d, d, 180f, 90f);          // top-left
                path.AddArc(w - d, 0, d, d, 270f, 90f);      // top-right
                path.AddLine(w, h, 0, h);                    // straight bottom edge (square corners)
                path.CloseFigure();
                Region = new Region(path);
            }
            if (previous != null) previous.Dispose();
        }

        private void UpdateChromePadding()
        {
            if (EmbeddedSurface) { Padding = Padding.Empty; return; }
            int side = WindowState == FormWindowState.Maximized ? 0 : _frame;
            int bottom = WindowState == FormWindowState.Maximized ? 0 : _frame;
            Padding = new Padding(side, _captionH, side, bottom);
        }

        // ----- Conservative auto-styler (exact-type only, never custom controls) -----
        private void StyleTree(Control c)
        {
            Type t = c.GetType();
            if (t == typeof(Button))
            {
                StyleButton((Button)c);
            }
            else if (t == typeof(Panel) || t == typeof(FlowLayoutPanel) ||
                     t == typeof(TableLayoutPanel) || t == typeof(GroupBox) ||
                     t == typeof(TabPage))
            {
                c.BackColor = TanFace;
            }
            foreach (Control child in c.Controls) StyleTree(child);
        }

        private static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(240, 238, 228);
            b.UseVisualStyleBackColor = false;
            b.FlatAppearance.BorderColor = Color.FromArgb(127, 157, 185);
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(210, 224, 245);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 203, 236);
            b.ForeColor = Color.FromArgb(30, 30, 30);
        }

        // ----- Painting -----
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (IsDesignerHosted || EmbeddedSurface) return;
            Graphics g = e.Graphics;
            int w = ClientSize.Width;
            // A zero-width/height client area happens transiently (minimize, early layout) and every
            // gradient brush below would throw on an empty rectangle. WinForms latches a paint
            // exception permanently (STATE_EXCEPTIONWHILEPAINTING), so one bad frame would replace
            // the whole window with the red-X placeholder — bail out instead.
            if (w <= 0 || ClientSize.Height <= 0) return;
            bool maximized = WindowState == FormWindowState.Maximized;
            int frame = maximized ? 0 : _frame;

            // Frame (sides + bottom) — a solid blue XP border.
            if (frame > 0)
            {
                using (var fb = new SolidBrush(FrameColor))
                {
                    g.FillRectangle(fb, 0, _captionH, frame, ClientSize.Height - _captionH);                      // left
                    g.FillRectangle(fb, w - frame, _captionH, frame, ClientSize.Height - _captionH);              // right
                    g.FillRectangle(fb, 0, ClientSize.Height - frame, w, frame);                                  // bottom
                }
            }

            // Caption gradient.
            Rectangle cap = new Rectangle(0, 0, w, _captionH);
            using (var brush = new LinearGradientBrush(
                cap,
                _isActive ? CaptionActiveLeft : CaptionInactiveLeft,
                _isActive ? CaptionActiveRight : CaptionInactiveRight,
                LinearGradientMode.Horizontal))
            {
                if (_isActive)
                {
                    var blend = new ColorBlend(3);
                    blend.Colors = new[] { CaptionActiveLeft, CaptionActiveMid, CaptionActiveRight };
                    blend.Positions = new[] { 0f, 0.5f, 1f };
                    brush.InterpolationColors = blend;
                }
                g.FillRectangle(brush, cap);
            }

            // Glossy highlight band near the top of the caption.
            if (_isActive)
            {
                int bandH = Math.Max(2, _captionH / 3);
                // The default WrapMode.Tile makes the gradient's last row wrap back to the bright
                // start colour, drawing a hard highlight line across the caption (it looked like a
                // strike-through through the title). WrapMode.Clamp is NOT a legal wrap mode for a
                // LinearGradientBrush — GDI+ rejects it with "Parameter is not valid", which threw out
                // of OnPaint and left WinForms painting the red-X exception placeholder over the whole
                // caption (plus a crash dialog on every launch). Oversize the gradient by one row and
                // fill only bandH rows instead, so the wrap seam falls outside what we draw.
                using (var band = new LinearGradientBrush(
                    new Rectangle(0, 0, w, bandH + 1),
                    Color.FromArgb(140, CaptionActiveHighlight),
                    Color.FromArgb(0, CaptionActiveHighlight),
                    LinearGradientMode.Vertical))
                {
                    g.FillRectangle(band, 0, 0, w, bandH);
                }
            }

            // App icon.
            int textLeft = 8;
            if (Icon != null)
            {
                int isz = _captionH - 10;
                try
                {
                    // Select the frame at the size we are about to draw, rather than reducing
                    // whichever frame the .ico nominates as its default (32 px). app_main.ico
                    // carries 16, 20 and 24 px frames that were reduced properly when it was built;
                    // squeezing the 32 into an 18 px caption with a Bitmap copy throws that away.
                    // Icon.ToBitmap() allocates; without the outer using it leaked one GDI bitmap per
                    // caption repaint.
                    using (var sized = new Icon(Icon, isz, isz))
                    using (var raw = sized.ToBitmap())
                    using (var bmp = new Bitmap(raw, new Size(isz, isz)))
                    {
                        g.DrawImage(bmp, 6, (_captionH - isz) / 2, isz, isz);
                    }
                    textLeft = 6 + isz + 6;
                }
                catch { /* icon rendering is best-effort */ }
            }

            // Title text.
            ComputeButtonRects(out Rectangle trayR, out Rectangle minR, out Rectangle maxR, out Rectangle closeR);
            int textRight = !trayR.IsEmpty ? trayR.Left - 6
                          : closeR.IsEmpty ? w - 8 : closeR.Left - 6;
            var titleRect = new Rectangle(textLeft, 0, Math.Max(0, textRight - textLeft), _captionH);
            using (var tf = new Font(LunaFonts.UiBold, FontStyle.Bold))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            using (var tb = new SolidBrush(_isActive ? CaptionText : CaptionTextInactive))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.DrawString(Text ?? string.Empty, tf, tb, titleRect, sf);
            }

            // Caption buttons.
            if (HasMinButton) DrawCaptionButton(g, minR, 0);
            if (HasMaxButton) DrawCaptionButton(g, maxR, 1);
            DrawCaptionButton(g, closeR, 2);
            if (HasTrayButton) DrawCaptionButton(g, trayR, 3);
        }

        /// <summary>
        /// The window's own caption buttons, drawn as the same red chrome button the docked panels
        /// and the document tabs wear. One treatment for minimize, restore and close across the
        /// whole application: they are the same affordance at every level, and three different
        /// looks for it was three chances to read them as three different things.
        /// </summary>
        private void DrawCaptionButton(Graphics g, Rectangle r, int kind)
        {
            if (r.IsEmpty) return;

            bool hover = _hoverButton == kind;
            bool pressed = _pressedButton == kind && hover;

            ToolWindowChrome.ChromeGlyph glyph =
                kind == 0 ? ToolWindowChrome.ChromeGlyph.Minimize
              : kind == 1 ? ToolWindowChrome.ChromeGlyph.Restore
              : kind == 3 ? ToolWindowChrome.ChromeGlyph.Tray
              : ToolWindowChrome.ChromeGlyph.Close;

            ToolWindowChrome.DrawChromeButton(
                g, ToolWindowChrome.ButtonRectIn(r), glyph, hover || pressed);
        }

        private void ComputeButtonRects(out Rectangle minR, out Rectangle maxR, out Rectangle closeR)
        {
            Rectangle ignored;
            ComputeButtonRects(out ignored, out minR, out maxR, out closeR);
        }

        private void ComputeButtonRects(out Rectangle trayR, out Rectangle minR,
                                        out Rectangle maxR, out Rectangle closeR)
        {
            trayR = minR = maxR = closeR = Rectangle.Empty;
            int bw = (int)Math.Round(21 * (DeviceDpi / 96f));
            int bh = (int)Math.Round((_toolWindow ? 16 : 18) * (DeviceDpi / 96f));
            int top = (_captionH - bh) / 2;
            int x = ClientSize.Width - (WindowState == FormWindowState.Maximized ? 2 : _frame) - bw - 2;

            closeR = new Rectangle(x, top, bw, bh);
            x -= bw + 1;
            if (HasMaxButton) { maxR = new Rectangle(x, top, bw, bh); x -= bw + 1; }
            if (HasMinButton) { minR = new Rectangle(x, top, bw, bh); x -= bw + 1; }
            // Outermost of the four, so the three Windows buttons keep the positions muscle memory
            // expects: close on the corner, then maximize, then minimize.
            if (HasTrayButton) { trayR = new Rectangle(x, top, bw, bh); }
        }

        // ----- Hit testing & native window behaviour -----
        protected override void WndProc(ref Message m)
        {
            if (EmbeddedSurface) { base.WndProc(ref m); return; }
            if (IsDesignerHosted)
            {
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == WM_WINDOWPOSCHANGED)
            {
                FormWindowState before = _lastWindowState;
                base.WndProc(ref m);
                KeepSizeAcrossRestore(before);
                return;
            }

            switch (m.Msg)
            {
                case WM_NCCALCSIZE:
                    if (m.WParam != IntPtr.Zero)
                    {
                        if (WindowState == FormWindowState.Maximized)
                        {
                            // A maximized frameless window is oversized by the resize border and
                            // positioned at -border,-border, so a client == whole window would spill
                            // off every screen edge (content lost under the taskbar / off-desktop).
                            // Pin the client rectangle straight to the monitor work area instead.
                            NCCALCSIZE_PARAMS p = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
                            Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
                            p.rgrc0.left = wa.Left;
                            p.rgrc0.top = wa.Top;
                            p.rgrc0.right = wa.Right;
                            p.rgrc0.bottom = wa.Bottom;
                            Marshal.StructureToPtr(p, m.LParam, false);
                        }
                        // Otherwise remove the entire non-client area; client fills the window.
                        m.Result = IntPtr.Zero;
                        return;
                    }
                    break;

                case WM_NCACTIVATE:
                    _isActive = m.WParam != IntPtr.Zero;
                    Invalidate();
                    // Returning TRUE prevents the default (now-nonexistent) caption flicker.
                    m.Result = new IntPtr(1);
                    return;

                case WM_NCHITTEST:
                    m.Result = (IntPtr)HitTest(m.LParam);
                    return;

                case WM_GETMINMAXINFO:
                    // Let DefWindowProc fill its default max size/position first, THEN override with
                    // the work-area constraint. Doing it the other way (as before) let the default
                    // clobber our values, so a maximized frameless window spilled past the work area
                    // (over the taskbar / off-screen). base-first + return is the correct order.
                    base.WndProc(ref m);
                    AdjustMaxInfo(m.LParam);
                    return;

                case WM_SETTEXT:
                case WM_SETICON:
                    base.WndProc(ref m);
                    Invalidate(new Rectangle(0, 0, ClientSize.Width, _captionH));
                    return;
            }
            base.WndProc(ref m);
        }

        private FormWindowState _lastWindowState = FormWindowState.Normal;
        private Size _normalSize;

        /// <summary>
        /// A restore (from minimized or maximized) gives back the size the window went away with.
        /// When a Form leaves Normal, WinForms saves its CLIENT size, and on the way back
        /// (Form.RestoreWindowBoundsIfNecessary) turns it into a window size by adding a standard
        /// frame (AdjustWindowRectEx). This window has no such frame: WM_NCCALCSIZE gives the whole
        /// window to the client. So the window came back larger by the frame: 800x600 -> 816x639,
        /// measured (WindowLifecycleTests.RestoringFromTheTaskbar_GivesTheWindowItsGeometryBack).
        /// The real window size is kept from the moment it left Normal.
        /// </summary>
        private void KeepSizeAcrossRestore(FormWindowState before)
        {
            FormWindowState now = base.WindowState;
            _lastWindowState = now;
            // Leaving Normal: RestoreBounds holds the real window size now (it is reset by the time
            // the window is back, so keep it).
            if (before == FormWindowState.Normal && now != FormWindowState.Normal) _normalSize = RestoreBounds.Size;
            if (before == FormWindowState.Normal || now != FormWindowState.Normal) return;
            Size wanted = _normalSize;
            if (wanted.Width <= 0 || wanted.Height <= 0) return;
            // After the restore has finished: WinForms applies its own size inside this message,
            // after any size set here.
            BeginInvoke((MethodInvoker)delegate
            {
                if (!IsDisposed && base.WindowState == FormWindowState.Normal && Size != wanted) Size = wanted;
            });
        }

        private int HitTest(IntPtr lParam)
        {
            int x = unchecked((short)(long)lParam);
            int y = unchecked((short)((long)lParam >> 16));
            Point p = PointToClient(new Point(x, y));
            int w = ClientSize.Width;
            int h = ClientSize.Height;

            // Caption buttons must be client so our mouse handlers receive them.
            ComputeButtonRects(out Rectangle trayR, out Rectangle minR, out Rectangle maxR, out Rectangle closeR);
            if ((HasTrayButton && trayR.Contains(p)) ||
                (HasMinButton && minR.Contains(p)) ||
                (HasMaxButton && maxR.Contains(p)) ||
                closeR.Contains(p))
            {
                return HTCLIENT;
            }

            if (IsResizable)
            {
                bool left = p.X <= ResizeGrip;
                bool right = p.X >= w - ResizeGrip;
                bool top = p.Y <= ResizeGrip;
                bool bottom = p.Y >= h - ResizeGrip;
                if (top && left) return HTTOPLEFT;
                if (top && right) return HTTOPRIGHT;
                if (bottom && left) return HTBOTTOMLEFT;
                if (bottom && right) return HTBOTTOMRIGHT;
                if (left) return HTLEFT;
                if (right) return HTRIGHT;
                if (top) return HTTOP;
                if (bottom) return HTBOTTOM;
            }

            // The caption strip: HTCAPTION gives native drag + double-click-maximize + snap.
            if (p.Y < _captionH) return HTCAPTION;

            return HTCLIENT;
        }

        private void AdjustMaxInfo(IntPtr lParam)
        {
            MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
            Screen scr = Screen.FromHandle(Handle);
            Rectangle wa = scr.WorkingArea;
            Rectangle mb = scr.Bounds;
            mmi.ptMaxPosition.x = wa.Left - mb.Left;
            mmi.ptMaxPosition.y = wa.Top - mb.Top;
            mmi.ptMaxSize.x = wa.Width;
            mmi.ptMaxSize.y = wa.Height;
            if (MaximumSize.IsEmpty)
            {
                mmi.ptMaxTrackSize.x = wa.Width;
                mmi.ptMaxTrackSize.y = wa.Height;
            }
            if (!MinimumSize.IsEmpty)
            {
                mmi.ptMinTrackSize.x = MinimumSize.Width;
                mmi.ptMinTrackSize.y = MinimumSize.Height;
            }
            Marshal.StructureToPtr(mmi, lParam, true);
        }

        // ----- Caption button mouse interaction (these regions are HTCLIENT) -----
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = ButtonAt(e.Location);
            if (hit != _hoverButton)
            {
                _hoverButton = hit;
                Invalidate(new Rectangle(0, 0, ClientSize.Width, _captionH));
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _pressedButton = ButtonAt(e.Location);
                if (_pressedButton >= 0) Invalidate(new Rectangle(0, 0, ClientSize.Width, _captionH));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _pressedButton >= 0)
            {
                int released = ButtonAt(e.Location);
                int was = _pressedButton;
                _pressedButton = -1;
                Invalidate(new Rectangle(0, 0, ClientSize.Width, _captionH));
                if (released == was) PerformCaptionAction(was);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverButton != -1 || _pressedButton != -1)
            {
                _hoverButton = -1;
                _pressedButton = -1;
                Invalidate(new Rectangle(0, 0, ClientSize.Width, _captionH));
            }
        }

        private int ButtonAt(Point p)
        {
            ComputeButtonRects(out Rectangle trayR, out Rectangle minR, out Rectangle maxR, out Rectangle closeR);
            if (HasTrayButton && trayR.Contains(p)) return 3;
            if (HasMinButton && minR.Contains(p)) return 0;
            if (HasMaxButton && maxR.Contains(p)) return 1;
            if (closeR.Contains(p)) return 2;
            return -1;
        }

        private void PerformCaptionAction(int kind)
        {
            switch (kind)
            {
                case 0: WindowState = FormWindowState.Minimized; break;
                case 1:
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                    UpdateChromePadding();
                    Invalidate();
                    break;
                case 2: Close(); break;
                case 3:
                    {
                        // The form does not hide itself: the tray icon, its menu and the restore
                        // path belong to the host, and a LunaForm that hid itself with no icon up
                        // would be a window the user cannot get back.
                        EventHandler handler = TrayRequested;
                        if (handler != null) handler(this, EventArgs.Empty);
                    }
                    break;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        // WM_NCCALCSIZE payload (wParam==TRUE). Only rgrc0 (the proposed window/new client rect) is
        // adjusted; the trailing rectangles + WINDOWPOS pointer are declared so the marshalled size
        // matches the native struct.
        [StructLayout(LayoutKind.Sequential)]
        private struct NCCALCSIZE_PARAMS
        {
            public RECT rgrc0;
            public RECT rgrc1;
            public RECT rgrc2;
            public IntPtr lppos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }
    }
}
