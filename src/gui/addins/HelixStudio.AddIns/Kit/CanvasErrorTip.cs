namespace HelixPythonNamespace.AddIns.Kit
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    /// <summary>What a canvas is reporting, and therefore how it is drawn.</summary>
    public enum CanvasTipSeverity { Error, Warning }

    /// <summary>
    /// The way a design canvas in this IDE says something is wrong: once, on the canvas, beside the
    /// thing it is about, behind a mark, wrapped to a readable measure.
    ///
    /// <para>Tooltips keep each diagnostic beside the canvas item it describes without reserving
    /// permanent space for an empty diagnostics panel.</para>
    /// </summary>
    public sealed class CanvasErrorTip : IDisposable
    {
        /// <summary>The widest a message is allowed to get before it wraps.</summary>
        public const int MaxWidth = 420;

        private const int Pad = 8;
        private const int IconSize = 16;

        private static readonly Color ErrorInk = Color.FromArgb(178, 34, 34);      // firebrick
        private static readonly Color ErrorBack = Color.FromArgb(255, 246, 246);
        private static readonly Color ErrorEdge = Color.FromArgb(214, 150, 150);

        private static readonly Color WarnInk = Color.FromArgb(124, 90, 10);
        private static readonly Color WarnBack = Color.FromArgb(255, 252, 240);
        private static readonly Color WarnEdge = Color.FromArgb(214, 186, 120);

        private readonly ToolTip _tip;
        private Image _errorMark;
        private Image _warnMark;

        public CanvasErrorTip(Font font)
        {
            Font = font ?? SystemFonts.DefaultFont;
            _tip = new ToolTip
            {
                OwnerDraw = true,
                ShowAlways = true,
                AutoPopDelay = 20000,
                InitialDelay = 0,
                ReshowDelay = 0,
            };
            _tip.Popup += OnPopup;
            _tip.Draw += OnDraw;
        }

        public Font Font { get; set; }

        /// <summary>The WinForms tooltip underneath. Exposed because a test asserts that a refusal
        /// is owner-drawn and is NOT the same object as the canvas's ordinary hover text — two
        /// facts about the control itself that no wrapper method can stand in for.</summary>
        public ToolTip Tip { get { return _tip; } }

        /// <summary>What it is currently showing. WinForms tells the owner-draw handlers nothing
        /// about the text, and tests read it to say WHY something was refused.</summary>
        public string Text { get; private set; }

        public CanvasTipSeverity Severity { get; private set; }

        /// <summary>Shows <paramref name="message"/> at a point on <paramref name="host"/>.</summary>
        public void Show(Control host, string message, CanvasTipSeverity severity,
                         int x, int y, int durationMs = 6000)
        {
            if (host == null || string.IsNullOrEmpty(message)) return;
            Text = message;
            Severity = severity;
            // NOT Hide() first. WinForms leaves a tooltip hidden when Hide and Show land in the same
            // message turn, so "tidy up before re-showing" is what stops it ever appearing. Show
            // alone raises Popup again, which re-measures for the new text.
            _tip.Show(message, host, x, y, durationMs);
        }

        public void Hide(Control host)
        {
            if (host != null) _tip.Hide(host);
            Text = null;
        }

        public Size Measure(string text)
        {
            Size measured = TextRenderer.MeasureText(text ?? string.Empty, Font,
                new Size(MaxWidth, int.MaxValue), TextFlags);
            return new Size(Pad * 3 + IconSize + measured.Width,
                            Math.Max(measured.Height, IconSize) + Pad * 2);
        }

        public static TextFormatFlags TextFlags
        {
            get { return TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding; }
        }

        public void Draw(DrawToolTipEventArgs e, string text, CanvasTipSeverity severity)
        {
            bool error = severity == CanvasTipSeverity.Error;
            using (var back = new SolidBrush(error ? ErrorBack : WarnBack))
                e.Graphics.FillRectangle(back, e.Bounds);
            using (var edge = new Pen(error ? ErrorEdge : WarnEdge))
                e.Graphics.DrawRectangle(edge, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);

            Image mark = Mark(error);
            if (mark != null) e.Graphics.DrawImage(mark, Pad, Pad, IconSize, IconSize);

            var textArea = new Rectangle(
                Pad * 2 + IconSize, Pad,
                Math.Max(1, e.Bounds.Width - (Pad * 3 + IconSize)),
                Math.Max(1, e.Bounds.Height - Pad * 2));
            TextRenderer.DrawText(e.Graphics, text ?? string.Empty, Font, textArea,
                error ? ErrorInk : WarnInk, TextFlags);
        }

        private Image Mark(bool error)
        {
            if (error)
            {
                // The red cross from the shared toolbar set: named for the analysis it was drawn
                // for, but it is the mark this product already means "error" by.
                if (_errorMark == null)
                    try { _errorMark = ToolbarIcons.DeadCode(); }
                    catch (System.IO.IOException) { _errorMark = null; }
                return _errorMark;
            }
            if (_warnMark == null) _warnMark = DrawWarningMark();
            return _warnMark;
        }

        /// <summary>An amber triangle with an exclamation. Drawn rather than loaded because the
        /// shared set has no warning glyph, and a missing file must not mean a blank square.</summary>
        private static Image DrawWarningMark()
        {
            var bitmap = new Bitmap(IconSize, IconSize);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                PointF[] triangle =
                {
                    new PointF(IconSize / 2f, 1.5f),
                    new PointF(IconSize - 1.5f, IconSize - 2f),
                    new PointF(1.5f, IconSize - 2f),
                };
                using (var fill = new SolidBrush(Color.FromArgb(240, 186, 60)))
                    g.FillPolygon(fill, triangle);
                using (var edge = new Pen(Color.FromArgb(170, 120, 20), 1.2f))
                    g.DrawPolygon(edge, triangle);
                using (var ink = new SolidBrush(Color.FromArgb(70, 50, 10)))
                using (var font = new Font(SystemFonts.DefaultFont.FontFamily, 8f, FontStyle.Bold))
                using (var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                })
                    g.DrawString("!", font, ink,
                        new RectangleF(0, 3, IconSize, IconSize - 3), format);
            }
            return bitmap;
        }

        private void OnPopup(object sender, PopupEventArgs e)
        {
            e.ToolTipSize = Measure(Text);
        }

        private void OnDraw(object sender, DrawToolTipEventArgs e)
        {
            Draw(e, Text, Severity);
        }

        public void Dispose()
        {
            _tip.Popup -= OnPopup;
            _tip.Draw -= OnDraw;
            _tip.Dispose();
            if (_warnMark != null) { _warnMark.Dispose(); _warnMark = null; }
            if (_errorMark != null) { _errorMark.Dispose(); _errorMark = null; }
        }
    }
}
