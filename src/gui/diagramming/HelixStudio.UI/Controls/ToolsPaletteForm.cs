namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.UI.Interaction;

    /// <summary>
    /// Event args carrying a requested TypeKind when the user clicks a
    /// type-creation button (Interface / Abstract / Concrete) on the palette.
    /// </summary>
    public sealed class TypeRequestedEventArgs : EventArgs
    {
        public TypeRequestedEventArgs(TypeKind kind)
        {
            Kind = kind;
        }

        public TypeKind Kind { get; private set; }
    }

    /// <summary>
    /// Floating Paint.NET-style tools palette.
    ///   Row 1: Select / Pan — tool-mode selection (mutually exclusive).
    ///   Row 2: Interface / Abstract Class / Concrete Class — type-creation shortcuts.
    /// Icons are loaded from checked-in PNG assets in both the designer and runtime.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class ToolsPaletteForm : LunaForm
    {
        public ToolsPaletteForm()
        {
            InitializeComponent();
            ApplyToolbarIcons();
            ApplyCanvasToolboxLayout();
            Paint += OnCanvasToolboxPaint;
        }

        private const int ButtonSize = 22;

        private readonly SharedInteractionPreferences _preferences;






        private bool _syncing;

        public ToolsPaletteForm(SharedInteractionPreferences preferences) : this()
        {
            if (preferences == null) throw new ArgumentNullException("preferences");
            _preferences = preferences;

            SyncFromPreferences();

            _preferences.ActiveToolChanged += OnPreferencesChanged;
        }

        private void OnSelectToolClick(object sender, EventArgs e) { SetActive(CanvasTool.Select); }
        private void OnPanToolClick(object sender, EventArgs e) { SetActive(CanvasTool.Pan); }
        private void OnAddInterfaceClick(object sender, EventArgs e) { RaiseTypeRequested(TypeKind.Interface); }
        private void OnAddAbstractClick(object sender, EventArgs e) { RaiseTypeRequested(TypeKind.AbstractClass); }
        private void OnAddConcreteClick(object sender, EventArgs e) { RaiseTypeRequested(TypeKind.ConcreteClass); }

        /// <summary>
        /// Raised when the user clicks Interface / Abstract / Concrete button.
        /// </summary>
        public event EventHandler<TypeRequestedEventArgs> TypeRequested;

        /// <summary>
        /// Raised when the user closes the palette with its title-bar X. The host uses this to
        /// clear the "palette enabled" intent — otherwise the palette reappears on the next tab
        /// switch (visibility = enabled AND canvas-tab-active), so the X would appear to do nothing.
        /// </summary>
        public event EventHandler UserClosed;

        /// <summary>
        /// Host-supplied icons. Call once after construction before first show.
        /// </summary>
        public void SetIcons(Image selectIcon, Image panIcon,
            Image interfaceIcon, Image abstractIcon, Image concreteIcon)
        {
            if (_selectButton != null) _selectButton.Image = ScaleIcon(selectIcon);
            if (_panButton != null) _panButton.Image = ScaleIcon(panIcon);
            if (_interfaceButton != null) _interfaceButton.Image = ScaleIcon(interfaceIcon);
            if (_abstractButton != null) _abstractButton.Image = ScaleIcon(abstractIcon);
            if (_concreteButton != null) _concreteButton.Image = ScaleIcon(concreteIcon);
        }

        private static Image ScaleIcon(Image source)
        {
            if (source == null) return null;
            // Keep an inset around the opaque I/A/C tile assets: drawn edge to edge they cover the
            // whole 22px button and hide the FlatAppearance hover and pressed backgrounds. The
            // transparent 16px Select/Pan glyphs are already smaller and keep their native size.
            int size = Math.Min(source.Width, 18);

            // The interpolation is chosen, not fixed. This used to be NearestNeighbor always, on
            // the stated assumption that the I/A/C assets were "native 24px" -- they are 256 x 256.
            // Nearest-neighbour from 256 to 18 keeps 324 pixels out of 65,536 on a 14px stride, so
            // what the button showed depended on where those sample points happened to land on the
            // glyph: thin strokes drop out, edges alias hard, and a small change to the artwork
            // changes the result completely. Anything more than a slight reduction is resampled
            // properly; a 1:1 or near-1:1 draw still goes through NearestNeighbor, which keeps a
            // 16px pixel-art glyph crisp instead of blurring it.
            bool significantReduction = source.Width > size * 3 / 2;
            var scaled = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(scaled))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = significantReduction
                    ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic
                    : System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, size, size));
            }
            return scaled;
        }

        private void ApplyToolbarIcons()
        {
            SetIcons(
                ToolbarIconDiskLoader.Load("select_cursor.png"),
                ToolbarIconDiskLoader.Load("pan_hand.png"),
                ToolbarIconDiskLoader.Load("product\\app", "app_I.png"),
                ToolbarIconDiskLoader.Load("product\\app", "app_A.png"),
                ToolbarIconDiskLoader.Load("product\\app", "app_C.png"));
        }

        private void ApplyCanvasToolboxLayout()
        {
            MinimumSize = Size.Empty;
            ClientSize = new Size(122, 24);
            BackColor = Color.FromArgb(246, 246, 246);
            Text = "Designer Tools";

            _selectButton.Location = new Point(0, 1);
            _panButton.Location = new Point(24, 1);
            _separator.Visible = false;
            _interfaceButton.Location = new Point(48, 1);
            _abstractButton.Location = new Point(72, 1);
            _concreteButton.Location = new Point(96, 1);
            foreach (Button button in new[] { _selectButton, _panButton, _interfaceButton, _abstractButton, _concreteButton })
            {
                button.Size = new Size(ButtonSize, ButtonSize);
                button.BackColor = Color.Transparent;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(204, 228, 247);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(153, 209, 255);
                button.Cursor = Cursors.Hand;
            }
        }

        private void OnCanvasToolboxPaint(object sender, PaintEventArgs e)
        {
            ControlPaint.DrawBorder3D(e.Graphics, ClientRectangle, Border3DStyle.Flat);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // LunaForm clears padding for embedded surfaces during handle creation. Restore a
            // small inset so the raised frame remains visible instead of being covered by children.
            Padding = new Padding(2);
        }


        private static Button CreateIconButton(Point location, string tooltip)
        {
            Button b = new Button();
            b.Size = new Size(ButtonSize, ButtonSize);
            b.Location = location;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.ImageAlign = ContentAlignment.MiddleCenter;
            b.BackColor = SystemColors.Control;
            if (!string.IsNullOrEmpty(tooltip))
            {
                ToolTip tip = new ToolTip();
                tip.SetToolTip(b, tooltip);
            }
            return b;
        }

        private void SetActive(CanvasTool tool)
        {
            if (_syncing) return;
            _preferences.ActiveTool = tool;
            SyncFromPreferences();
        }

        private void RaiseTypeRequested(TypeKind kind)
        {
            EventHandler<TypeRequestedEventArgs> handler = TypeRequested;
            if (handler != null)
            {
                handler(this, new TypeRequestedEventArgs(kind));
            }
        }

        private void OnPreferencesChanged()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(SyncFromPreferences)); } catch { }
            }
            else
            {
                SyncFromPreferences();
            }
        }

        private void SyncFromPreferences()
        {
            _syncing = true;
            try
            {
                bool sel = _preferences.ActiveTool == CanvasTool.Select;
                Color activeBg = Color.FromArgb(187, 215, 253);
                Color activeBorder = Color.FromArgb(0, 120, 215);
                _selectButton.BackColor = sel ? activeBg : Color.Transparent;
                _panButton.BackColor = !sel ? activeBg : Color.Transparent;
                _selectButton.FlatAppearance.BorderColor = sel ? activeBorder : Color.Gray;
                _panButton.FlatAppearance.BorderColor = !sel ? activeBorder : Color.Gray;
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnKeyDownShortcut(object sender, KeyEventArgs e)
        {
            if (e == null) return;
            if (e.KeyCode == Keys.S) { SetActive(CanvasTool.Select); e.Handled = true; }
            else if (e.KeyCode == Keys.H) { SetActive(CanvasTool.Pan); e.Handled = true; }
            else if (e.KeyCode == Keys.I) { RaiseTypeRequested(TypeKind.Interface); e.Handled = true; }
            else if (e.KeyCode == Keys.A) { RaiseTypeRequested(TypeKind.AbstractClass); e.Handled = true; }
            else if (e.KeyCode == Keys.C) { RaiseTypeRequested(TypeKind.ConcreteClass); e.Handled = true; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Hide instead of close when the user clicks the X, and tell the host so it can drop the
            // "enabled" intent (and its menu checkmark) — otherwise the palette pops back on the next
            // canvas-tab activation and the X appears to do nothing.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                EventHandler h = UserClosed;
                if (h != null) h(this, EventArgs.Empty);
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_preferences != null)
                {
                    _preferences.ActiveToolChanged -= OnPreferencesChanged;
                }
                if (components != null)
                {
                    components.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
