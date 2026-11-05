namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;

    /// <summary>
    /// The look of a tool-window title bar, in one place.
    ///
    /// Two surfaces draw this bar — <see cref="DockablePanel"/> for the docked panels, and the GUI
    /// Designer for its own Toolbox and Properties — and they sit on screen at the same time. A bar
    /// that is a few shades or a couple of pixels off from its neighbour reads as a different
    /// product, so the numbers live here rather than being typed out twice and drifting the first
    /// time either is touched.
    /// </summary>
    public static class ToolWindowChrome
    {
        /// <summary>Height of the title bar.</summary>
        public const int HeaderHeight = 26;

        /// <summary>Width of each square button on the bar.</summary>
        public const int ButtonWidth = 22;

        /// <summary>Marlett's ✕. The font is what makes it the system close glyph rather than an x.</summary>
        public const string CloseGlyph = "r";

        /// <summary>Marlett's restore/maximize pair, used by the float/dock toggle.</summary>
        public const string RestoreGlyph = "1";
        public const string FloatGlyph = "2";

        /// <summary>Marlett's down arrow, used to collapse.</summary>
        public const string CollapseGlyph = "5";

        /// <summary>
        /// The caption face. A docked tool window is a WINDOW, so its title bar carries the Luna
        /// caption accent rather than a pale strip — the same accent the application's own caption
        /// wears, which is what makes the two read as the same chrome at the same depth.
        ///
        /// Read from the ACTIVE palette, not written down: these three were the Luna Blue literals,
        /// so switching the skin to Bottle Green left the Solution Explorer's caption the only blue
        /// thing on a green window. Every value here derives from one palette entry, so a theme can
        /// never again recolor the frame and miss the tool windows inside it.
        /// </summary>
        public static Color HeaderBack { get { return LunaThemes.Current.TabActive; } }

        /// <summary>The 1px inner light along the top and left, which is what gives the bar its lift.</summary>
        public static Color HeaderHighlight { get { return Blend(HeaderBack, Color.White, 0.47f); } }

        /// <summary>The line under the bar, separating it from the content.</summary>
        public static Color HeaderEdge { get { return Blend(HeaderBack, Color.Black, 0.22f); } }

        /// <summary>Moves <paramref name="from"/> the given fraction of the way toward <paramref name="to"/>.</summary>
        private static Color Blend(Color from, Color to, float amount)
        {
            return Color.FromArgb(
                from.R + (int)((to.R - from.R) * amount),
                from.G + (int)((to.G - from.G) * amount),
                from.B + (int)((to.B - from.B) * amount));
        }

        public static Color HeaderText { get { return Color.White; } }
        public static Color ButtonHover { get { return Color.FromArgb(80, 255, 255, 255); } }

        /// <summary>The round close button's face, and the darker ring around it.</summary>
        public static Color CloseFace { get { return Color.FromArgb(228, 71, 30); } }
        public static Color CloseFaceHot { get { return Color.FromArgb(246, 110, 66); } }
        public static Color CloseRing { get { return Color.FromArgb(150, 34, 12); } }

        /// <summary>Radius of the caption's top corners.</summary>
        public const int CornerRadius = 4;

        /// <summary>
        /// The side of every chrome button, everywhere.
        ///
        /// The caption's buttons, the document tabs' ✕ and the editor sub-tabs' ✕ are the same
        /// affordance and appear within a few pixels of each other; three sizes read as three
        /// different controls. One constant, and every hit-test rect derives from it, so the box
        /// that is painted is the box that is clicked.
        /// </summary>
        public const int ButtonSize = 14;

        /// <summary>Which mark a chrome button carries.</summary>
        /// <summary>
        /// The marks a tool window's caption can carry.
        ///
        /// A tool window is not a document window, and the three buttons it inherited said so
        /// wrongly: MINIMIZE has no meaning for a panel that cannot be restored from a taskbar, and
        /// RESTORE none for one that never maximized. What a panel actually does is auto-hide to
        /// the edge and undock into a window, so those are the marks — <see cref="PinOpen"/> /
        /// <see cref="PinHidden"/> for the first, <see cref="Undock"/> / <see cref="Dock"/> for the
        /// second. Each pair is one button in its two states.
        /// </summary>
        public enum ChromeGlyph { Close, Minimize, Restore, Tray, PinOpen, PinHidden, Undock, Dock, PanelClose }

        /// <summary>
        /// A square chrome button: a white mark engraved into a coloured face.
        ///
        /// The colour says which action it is before the mark is even read — red closes, amber
        /// minimizes, green restores — so the three are told apart at a glance and at a size where
        /// an 8px glyph alone is not much to go on.
        ///
        /// The mark is drawn twice a pixel apart: a darker shade of the button's own colour up and
        /// to the left, then white on top. That is what makes it read as cut INTO the face. The
        /// shadow is a shade of the FACE rather than black, so it deepens the mark without
        /// competing with it — the white has to stay the thing you actually see.
        /// </summary>
        public static void DrawChromeButton(Graphics g, Rectangle r, ChromeGlyph glyph, bool hot)
        {
            DrawChromeButton(g, r, glyph, hot, false);
        }

        /// <param name="mirrored">
        /// Flips the auto-hide marks for a panel docked on the RIGHT: the bar belongs against the
        /// edge the panel actually collapses into, and an icon pointing the wrong way describes
        /// somebody else's dock.
        /// </param>
        public static void DrawChromeButton(Graphics g, Rectangle r, ChromeGlyph glyph, bool hot, bool mirrored)
        {
            if (r.Width <= 0 || r.Height <= 0) return;

            // Artwork first. These buttons are whole faces, not marks on a face, so a loaded icon
            // REPLACES the drawn button rather than sitting on it.
            if (DrawCaptionArt(g, r, glyph, hot, mirrored)) return;

            SmoothingMode saved = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;

            Color face, border, shadow;
            PaletteFor(glyph == ChromeGlyph.PanelClose ? ChromeGlyph.Close : glyph,
                       hot, out face, out border, out shadow);

            using (var fill = new SolidBrush(face)) g.FillRectangle(fill, r);
            using (var edge = new Pen(border)) g.DrawRectangle(edge, r.X, r.Y, r.Width - 1, r.Height - 1);

            // The auto-hide pair is drawn in ITS OWN colours rather than engraved in white: it is a
            // two-colour mark — the edge it collapses into, and the panel moving — and a single
            // white silhouette of it is just a blob with a spike. Everything else keeps the engrave.
            if (glyph == ChromeGlyph.PinOpen || glyph == ChromeGlyph.PinHidden)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawAutoHideMark(g, r, glyph == ChromeGlyph.PinOpen, mirrored);
                g.SmoothingMode = saved;
                return;
            }

            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            ChromeGlyph drawn = glyph == ChromeGlyph.PanelClose ? ChromeGlyph.Close : glyph;
            DrawGlyph(g, drawn, cx - 1, cy - 1, shadow);
            DrawGlyph(g, drawn, cx, cy, Color.White);

            g.SmoothingMode = saved;
        }

        /// <summary>The dock edge, and the panel going into it — or coming back out.</summary>
        private static readonly Color EdgeBar = Color.FromArgb(16, 104, 240);
        private static readonly Color MovingArrow = Color.FromArgb(247, 134, 11);

        /// <summary>
        /// A bar against one side and an arrow at it: the panel folding away into the edge it is
        /// docked against (<paramref name="intoEdge"/>), or stepping back out of it.
        /// </summary>
        private static void DrawAutoHideMark(Graphics g, Rectangle r, bool intoEdge, bool mirrored)
        {
            int inset = Math.Max(1, r.Width / 8);
            var body = Rectangle.Inflate(r, -inset, -inset);
            if (body.Width < 6 || body.Height < 6) return;

            // The bar sits on the edge this panel collapses into.
            int barWidth = Math.Max(2, body.Width / 4);
            var bar = mirrored
                ? new Rectangle(body.Right - barWidth, body.Top, barWidth, body.Height)
                : new Rectangle(body.Left, body.Top, barWidth, body.Height);
            using (var brush = new SolidBrush(EdgeBar)) g.FillRectangle(brush, bar);

            // The arrow travels TOWARDS the bar to hide, away from it to come back.
            bool pointsLeft = mirrored ? !intoEdge : intoEdge;
            int cy = body.Top + body.Height / 2;
            int lane = mirrored ? body.Left : body.Left + barWidth + 1;
            int laneEnd = mirrored ? body.Right - barWidth - 1 : body.Right;
            int head = Math.Max(3, body.Height / 3);
            int thickness = Math.Max(2, body.Height / 6);

            int tipX = pointsLeft ? lane : laneEnd;
            int tailX = pointsLeft ? laneEnd : lane;
            using (var brush = new SolidBrush(MovingArrow))
            {
                int shaftLeft = Math.Min(tipX, tailX);
                int shaftRight = Math.Max(tipX, tailX);
                g.FillRectangle(brush, shaftLeft, cy - thickness / 2, Math.Max(1, shaftRight - shaftLeft), thickness);
                g.FillPolygon(brush, new[]
                {
                    new Point(tipX, cy),
                    new Point(tipX + (pointsLeft ? head : -head), cy - head),
                    new Point(tipX + (pointsLeft ? head : -head), cy + head),
                });
            }
        }

        private static void PaletteFor(ChromeGlyph glyph, bool hot,
                                       out Color face, out Color border, out Color shadow)
        {
            if (glyph == ChromeGlyph.Tray)
            {
                // Blue, because the other three are already spoken for and the colour is how these
                // are told apart before the mark is read. Minimize-to-tray sits next to
                // minimize-to-taskbar, so of the four it is the one that most needs to not be
                // mistaken for its neighbour.
                face = hot ? Color.FromArgb(82, 150, 226) : Color.FromArgb(46, 114, 194);
                border = Color.FromArgb(22, 70, 130);
                shadow = Color.FromArgb(26, 78, 142);
            }
            // The auto-hide pair is a two-colour mark of its own, so its face steps back: a pale
            // surface and a grey edge, with the blue bar and the orange arrow doing the talking. A
            // saturated face behind them would leave three colours fighting in fourteen pixels.
            else if (glyph == ChromeGlyph.PinOpen || glyph == ChromeGlyph.PinHidden)
            {
                face = hot ? Color.White : Color.FromArgb(242, 245, 250);
                border = Color.FromArgb(138, 147, 166);
                shadow = Color.FromArgb(120, 130, 150);
            }
            else if (glyph == ChromeGlyph.Minimize)
            {
                face = hot ? Color.FromArgb(246, 186, 46) : Color.FromArgb(222, 158, 12);
                border = Color.FromArgb(146, 100, 4);
                shadow = Color.FromArgb(158, 110, 6);
            }
            else if (glyph == ChromeGlyph.Restore || glyph == ChromeGlyph.Undock
                     || glyph == ChromeGlyph.Dock)
            {
                face = hot ? Color.FromArgb(72, 176, 86) : Color.FromArgb(42, 142, 58);
                border = Color.FromArgb(20, 92, 34);
                shadow = Color.FromArgb(24, 102, 38);
            }
            else
            {
                face = hot ? Color.FromArgb(240, 92, 58) : Color.FromArgb(214, 62, 32);
                border = Color.FromArgb(150, 34, 12);
                shadow = Color.FromArgb(158, 38, 14);
            }
        }

        /// <summary>
        /// The mark itself. Drawn heavy on purpose: at 14px a hairline ✕ is a smudge, and the two
        /// glyphs that are mostly diagonal or mostly horizontal are exactly the ones a thin stroke
        /// loses.
        /// </summary>
        private static void DrawGlyph(Graphics g, ChromeGlyph glyph, int cx, int cy, Color color)
        {
            if (glyph == ChromeGlyph.Tray)
            {
                // An arrow going DOWN INTO a tray. The bar alone is minimize; the bar with
                // something dropping into it is minimize-somewhere-else, which is the whole
                // difference between this button and the one beside it.
                using (var bar = new SolidBrush(color))
                    g.FillRectangle(bar, cx - 4, cy + 3, 9, 2);          // the tray
                using (var stem = new SolidBrush(color))
                    g.FillRectangle(stem, cx - 1, cy - 5, 2, 5);         // the shaft
                using (var head = new SolidBrush(color))
                    g.FillPolygon(head, new[]                            // the arrowhead
                    {
                        new Point(cx - 3, cy - 1), new Point(cx + 3, cy - 1), new Point(cx, cy + 2),
                    });
                return;
            }

            if (glyph == ChromeGlyph.Minimize)
            {
                // A solid bar, not a line: a 1px rule at this size reads as a scratch.
                using (var bar = new SolidBrush(color))
                    g.FillRectangle(bar, cx - 4, cy + 2, 9, 3);
                return;
            }

            if (glyph == ChromeGlyph.PinOpen)
            {
                // A pin pushed IN: head across the top, shaft down, point at the bottom. This is
                // the panel held open — pressing it lets go, and the panel slides to the edge.
                using (var head = new SolidBrush(color)) g.FillRectangle(head, cx - 4, cy - 5, 9, 3);
                using (var shaft = new SolidBrush(color)) g.FillRectangle(shaft, cx - 1, cy - 2, 2, 5);
                using (var tip = new SolidBrush(color))
                    g.FillPolygon(tip, new[] { new Point(cx - 2, cy + 3), new Point(cx + 2, cy + 3), new Point(cx, cy + 6) });
                return;
            }

            if (glyph == ChromeGlyph.PinHidden)
            {
                // The same pin LYING DOWN: the panel is auto-hidden and this puts it back. Turning
                // the pin rather than swapping the symbol is what says the two are one control.
                using (var head = new SolidBrush(color)) g.FillRectangle(head, cx - 5, cy - 4, 3, 9);
                using (var shaft = new SolidBrush(color)) g.FillRectangle(shaft, cx - 2, cy - 1, 5, 2);
                using (var tip = new SolidBrush(color))
                    g.FillPolygon(tip, new[] { new Point(cx + 3, cy - 2), new Point(cx + 3, cy + 2), new Point(cx + 6, cy) });
                return;
            }

            if (glyph == ChromeGlyph.Undock)
            {
                // A pane lifting OUT of its edge: the edge stays, a small window steps away from it.
                using (var edge = new SolidBrush(color)) g.FillRectangle(edge, cx - 5, cy - 5, 2, 10);
                using (var pen = new Pen(color, 1.4f)) g.DrawRectangle(pen, cx - 1, cy - 3, 6, 6);
                return;
            }

            if (glyph == ChromeGlyph.Dock)
            {
                // The same two shapes, the window going back: an arrow INTO the edge it came from.
                using (var edge = new SolidBrush(color)) g.FillRectangle(edge, cx - 5, cy - 5, 2, 10);
                using (var shaft = new SolidBrush(color)) g.FillRectangle(shaft, cx, cy - 1, 5, 2);
                using (var tip = new SolidBrush(color))
                    g.FillPolygon(tip, new[] { new Point(cx - 1, cy - 4), new Point(cx - 1, cy + 4), new Point(cx - 4, cy) });
                return;
            }

            if (glyph == ChromeGlyph.Restore)
            {
                using (var pen = new Pen(color, 1.6f))
                    g.DrawRectangle(pen, cx - 4, cy - 3, 8, 7);
                using (var cap = new SolidBrush(color))
                    g.FillRectangle(cap, cx - 4, cy - 3, 9, 2);   // the little title bar
                return;
            }

            SmoothingMode saved = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, 2.2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - 3.5f, cy - 3.5f, cx + 3.5f, cy + 3.5f);
                g.DrawLine(pen, cx + 3.5f, cy - 3.5f, cx - 3.5f, cy + 3.5f);
            }
            g.SmoothingMode = saved;
        }

        /// <summary>Centres a <see cref="ButtonSize"/> square in the space a caller has for it.</summary>
        public static Rectangle ButtonRectIn(Rectangle host)
        {
            return new Rectangle(
                host.X + (host.Width - ButtonSize) / 2,
                host.Y + (host.Height - ButtonSize) / 2,
                ButtonSize, ButtonSize);
        }



        /// <summary>The Marlett font the glyph constants above are drawn in.</summary>
        public static Font GlyphFont { get { return new Font("Marlett", 8f); } }

        // ---- caption artwork -----------------------------------------------------------------

        private const string CaptionSubfolder = "product\\caption";

        /// <summary>
        /// The file each button is drawn from. Two sheets, for two places: the window's own title
        /// bar and a docked panel's. They overlap in subject — both have a close — and are still
        /// different drawings, so the panel's close has its own glyph rather than borrowing the
        /// window's.
        /// </summary>
        private static string CaptionSlot(ChromeGlyph glyph)
        {
            switch (glyph)
            {
                case ChromeGlyph.Tray: return "caption-tray";
                case ChromeGlyph.Minimize: return "caption-minimize";
                case ChromeGlyph.Restore: return "caption-restore";
                case ChromeGlyph.Close: return "caption-close";
                case ChromeGlyph.PanelClose: return "panel-close";
                case ChromeGlyph.PinOpen:
                case ChromeGlyph.PinHidden: return "panel-autohide";
                case ChromeGlyph.Undock:
                case ChromeGlyph.Dock: return "panel-dock";
                default: return null;
            }
        }

        /// <summary>
        /// Which way the auto-hide arrow points.
        ///
        /// One drawing serves all four states, flipped. The arrow always points the way the panel
        /// is about to MOVE: an open panel on the left folds left, the same panel on the right
        /// folds right, and a hidden one comes back out the other way. Drawing it one fixed
        /// direction would describe somebody else's dock three times out of four.
        /// </summary>
        private static bool CaptionArtIsFlipped(ChromeGlyph glyph, bool mirrored)
        {
            if (glyph != ChromeGlyph.PinOpen && glyph != ChromeGlyph.PinHidden) return false;
            bool pointsLeft = glyph == ChromeGlyph.PinOpen ? !mirrored : mirrored;
            return !pointsLeft;            // the file is drawn pointing left
        }

        private static readonly object ArtLock = new object();
        private static readonly System.Collections.Generic.Dictionary<string, Image> Art =
            new System.Collections.Generic.Dictionary<string, Image>(StringComparer.Ordinal);

        /// <summary>
        /// Paints the button from its file, or reports that there is none and leaves the drawn
        /// version to it.
        ///
        /// Resampled once per (icon, size, direction) and kept. The source art is ~600px and a
        /// caption button is 14: resampling that on every repaint, for every button, on every
        /// panel, is the kind of cost that only shows up as a window that feels slow to drag.
        /// </summary>
        private static bool DrawCaptionArt(Graphics g, Rectangle r, ChromeGlyph glyph, bool hot, bool mirrored)
        {
            string slot = CaptionSlot(glyph);
            if (slot == null) return false;

            bool flipped = CaptionArtIsFlipped(glyph, mirrored);
            string key = slot + "@" + r.Width + "x" + r.Height + (flipped ? "|flip" : "");

            Image art;
            lock (ArtLock)
            {
                if (!Art.TryGetValue(key, out art))
                {
                    art = BuildCaptionArt(slot, r.Size, flipped);
                    // A MISS IS CACHED TOO, as null: without it a missing file means walking the
                    // loader's directory list again on every single repaint.
                    Art[key] = art;
                }
            }
            if (art == null) return false;

            if (hot)
            {
                // Lifted rather than tinted: the artwork already carries the colour that says which
                // button this is, and washing it with an accent would take that away.
                var lift = new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new[] { 1f, 0f, 0f, 0f, 0f },
                    new[] { 0f, 1f, 0f, 0f, 0f },
                    new[] { 0f, 0f, 1f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 1f, 0f },
                    new[] { 0.16f, 0.16f, 0.16f, 0f, 1f },
                });
                using (var attrs = new System.Drawing.Imaging.ImageAttributes())
                {
                    attrs.SetColorMatrix(lift);
                    g.DrawImage(art, r, 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, attrs);
                }
            }
            else
            {
                g.DrawImage(art, r);
            }
            return true;
        }

        private static Image BuildCaptionArt(string slot, Size size, bool flipped)
        {
            Image source;
            try { source = ToolbarIconDiskLoader.Load(CaptionSubfolder, slot + ".png"); }
            catch (System.IO.IOException) { return null; }
            if (source == null) return null;

            using (source)
            {
                var scaled = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height),
                                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (Graphics gg = Graphics.FromImage(scaled))
                {
                    gg.Clear(Color.Transparent);
                    gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    gg.CompositingQuality = CompositingQuality.HighQuality;
                    gg.DrawImage(source, new Rectangle(0, 0, scaled.Width, scaled.Height));
                }
                if (flipped) scaled.RotateFlip(RotateFlipType.RotateNoneFlipX);
                return scaled;
            }
        }
    }
}
