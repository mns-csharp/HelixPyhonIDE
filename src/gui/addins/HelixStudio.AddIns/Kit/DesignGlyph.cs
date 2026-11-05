namespace HelixPythonNamespace.AddIns.Kit
{
    using System.Drawing;
    using System.Drawing.Drawing2D;

    /// <summary>
    /// Fallback glyphs for designer toolbar commands when artwork is unavailable.
    ///
    /// Bundled artwork remains the primary source; these drawn shapes are used when a file is
    /// genuinely not on disk.
    ///
    /// They exist because a button whose image failed to load is a blank cell with nothing to say
    /// why, and a file can go missing for reasons that have nothing to do with this code — a
    /// partial checkout, a packaging step that skipped a folder. An earlier version of this comment
    /// claimed the whole <c>assets/</c> tree was deleted in this working tree; that was true of
    /// <c>assets/Python_icons/</c> only, and it is why these were drawn in the first place.
    ///
    /// They are deliberately flat and two-colour — blue for the thing, orange for the action on it
    /// — so they read at 16px on a pale strip.
    /// </summary>
    public static class DesignGlyph
    {
        private static readonly Color Ink = Color.FromArgb(52, 120, 200);
        private static readonly Color Edge = Color.FromArgb(28, 70, 130);
        private static readonly Color Action = Color.FromArgb(232, 126, 18);

        private const int Size = 16;

        private static Bitmap Draw(System.Action<Graphics> paint)
        {
            var bmp = new Bitmap(Size, Size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                paint(g);
            }
            return bmp;
        }

        /// <summary>A fresh sheet with a plus on it.</summary>
        public static Image NewDesign()
        {
            return Draw(delegate(Graphics g)
            {
                using (var page = new SolidBrush(Color.White))
                using (var edge = new Pen(Edge, 1.2f))
                {
                    g.FillRectangle(page, 2, 1, 9, 12);
                    g.DrawRectangle(edge, 2, 1, 9, 12);
                }
                using (var plus = new SolidBrush(Action))
                {
                    g.FillRectangle(plus, 9, 8, 6, 2);
                    g.FillRectangle(plus, 11, 6, 2, 6);
                }
            });
        }

        /// <summary>A magnifier with a plus (zoom in), a minus (zoom out) or a frame (fit).</summary>
        public static Image Plus() { return Magnifier(1); }
        public static Image Minus() { return Magnifier(-1); }
        public static Image Fit() { return Magnifier(0); }

        private static Image Magnifier(int sign)
        {
            return Draw(delegate(Graphics g)
            {
                using (var glass = new SolidBrush(Color.White))
                using (var edge = new Pen(Edge, 1.6f))
                {
                    g.FillEllipse(glass, 1, 1, 10, 10);
                    g.DrawEllipse(edge, 1, 1, 10, 10);
                    g.DrawLine(edge, 10, 10, 14, 14);
                }
                using (var mark = new Pen(Ink, 1.6f))
                {
                    if (sign == 0) g.DrawRectangle(mark, 4, 4, 4, 4);
                    else g.DrawLine(mark, 3.5f, 6, 8.5f, 6);
                    if (sign > 0) g.DrawLine(mark, 6, 3.5f, 6, 8.5f);
                }
            });
        }

        /// <summary>The pointer, for the Select tool.</summary>
        public static Image Arrow()
        {
            return Draw(delegate(Graphics g)
            {
                var head = new[]
                {
                    new Point(3, 1), new Point(3, 13), new Point(6, 10),
                    new Point(8, 15), new Point(10, 14), new Point(8, 9), new Point(12, 9),
                };
                using (var fill = new SolidBrush(Color.FromArgb(40, 44, 54)))
                using (var edge = new Pen(Color.White, 1f))
                {
                    g.FillPolygon(fill, head);
                    g.DrawPolygon(edge, head);
                }
            });
        }

        /// <summary>
        /// The hand that moves the sheet. Four fingers and a thumb rather than an arrow cross,
        /// because the cross reads as "move this node" and this moves the paper under all of them.
        /// </summary>
        public static Image Hand()
        {
            return Draw(delegate(Graphics g)
            {
                using (var skin = new SolidBrush(Action))
                using (var edge = new Pen(Color.FromArgb(150, 78, 8), 1f))
                {
                    // Palm.
                    var palm = new Rectangle(4, 7, 9, 8);
                    g.FillRectangle(skin, palm);
                    g.DrawRectangle(edge, palm);
                    // Three raised fingers and a thumb down the left.
                    for (int i = 0; i < 3; i++)
                    {
                        var finger = new Rectangle(6 + i * 3, 2 + i % 2, 2, 6);
                        g.FillRectangle(skin, finger);
                        g.DrawRectangle(edge, finger);
                    }
                    var thumb = new Rectangle(2, 8, 3, 4);
                    g.FillRectangle(skin, thumb);
                    g.DrawRectangle(edge, thumb);
                }
            });
        }

        /// <summary>An arrow curving back the way it came — or forward again.</summary>
        public static Image Undo(bool back)
        {
            return Draw(delegate(Graphics g)
            {
                using (var pen = new Pen(Ink, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, 2, 4, 12, 10, back ? 200 : -20, back ? 140 : -140);
                using (var head = new SolidBrush(Action))
                    g.FillPolygon(head, back
                        ? new[] { new Point(2, 4), new Point(8, 4), new Point(2, 10) }
                        : new[] { new Point(14, 4), new Point(8, 4), new Point(14, 10) });
            });
        }

        /// <summary>Pieces coming together inside a frame, or leaving it.</summary>
        public static Image Group(bool grouping)
        {
            return Draw(delegate(Graphics g)
            {
                using (var frame = new Pen(Color.FromArgb(70, 90, 130), 1.2f) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(frame, 1, 1, 13, 13);

                Rectangle a = grouping ? new Rectangle(3, 3, 5, 5) : new Rectangle(0, 0, 5, 5);
                Rectangle b = grouping ? new Rectangle(8, 8, 5, 5) : new Rectangle(11, 11, 5, 5);
                using (var fill = new SolidBrush(Ink))
                using (var edge = new Pen(Edge))
                {
                    g.FillRectangle(fill, a); g.DrawRectangle(edge, a);
                    g.FillRectangle(fill, b); g.DrawRectangle(edge, b);
                }
            });
        }

        /// <summary>A disk, with the shutter every save icon has had for forty years.</summary>
        public static Image Save()
        {
            return Draw(delegate(Graphics g)
            {
                using (var body = new SolidBrush(Ink))
                using (var edge = new Pen(Edge, 1.2f))
                {
                    g.FillRectangle(body, 1, 2, 13, 12);
                    g.DrawRectangle(edge, 1, 2, 13, 12);
                }
                using (var shutter = new SolidBrush(Color.White)) g.FillRectangle(shutter, 4, 3, 7, 4);
                using (var label = new SolidBrush(Color.FromArgb(226, 234, 246))) g.FillRectangle(label, 3, 9, 9, 5);
            });
        }

        /// <summary>A folder, open at the front.</summary>
        public static Image Open()
        {
            return Draw(delegate(Graphics g)
            {
                using (var back = new SolidBrush(Color.FromArgb(238, 186, 86)))
                using (var edge = new Pen(Color.FromArgb(150, 104, 24), 1.1f))
                {
                    g.FillRectangle(back, 1, 3, 13, 10);
                    g.DrawRectangle(edge, 1, 3, 13, 10);
                    g.FillRectangle(back, 1, 2, 6, 2);
                }
                using (var front = new SolidBrush(Color.FromArgb(252, 214, 130)))
                using (var edge = new Pen(Color.FromArgb(150, 104, 24), 1.1f))
                {
                    var flap = new[] { new Point(1, 13), new Point(4, 7), new Point(15, 7), new Point(12, 13) };
                    g.FillPolygon(front, flap);
                    g.DrawPolygon(edge, flap);
                }
            });
        }
    }
}
