namespace HelixPythonNamespace.AddIns.Kit
{
    using System;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Globalization;
    using System.Text;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// The one description of what a toolbox looks like in this IDE.
    ///
    /// <para><b>Why it exists.</b> Designer palettes use a shared set of fonts, colors, and spacing
    /// so their controls remain visually consistent with the IDE.</para>
    ///
    /// <para>The fonts are derived from <see cref="LunaFonts"/> and the colours from
    /// <see cref="LunaColors"/>, so a theme change moves the palettes with everything else rather
    /// than leaving them behind at hard-coded values.</para>
    /// </summary>
    public static class ToolboxStyle
    {
        /// <summary>Height of a category header row.</summary>
        public const int CategoryHeight = 24;

        /// <summary>Height of one tool row.</summary>
        public const int ItemHeight = 30;

        /// <summary>Icon box inside a tool row. 24 px because the designers' artwork is drawn, not
        /// glyph-based, and 16 px loses the difference between one bead model and another.</summary>
        public const int IconSize = 24;

        /// <summary>Left inset of the chevron, and of everything measured from it.</summary>
        public const int ChevronWidth = 14;

        public static Font CategoryFont { get { return LunaFonts.SizedBold(-1f); } }

        public static Font ItemFont { get { return LunaFonts.Ui; } }

        public static Color CategoryForeColor { get { return LunaColors.TextAccent; } }

        public static Color ItemForeColor { get { return LunaColors.TextPrimary; } }

        public static Color ChevronColor { get { return LunaColors.TextAccent; } }

        /// <summary>
        /// A category name as it is shown: upper case, always.
        ///
        /// <para>Upper case rather than the author's own capitalisation because the three palettes
        /// disagreed — "Common Controls", "Inputs and outputs", "ENERGY TERMS" — and a rule that
        /// depends on who typed the string is not a rule. Invariant culture: a Turkish locale
        /// lower-cases a dotted I to a dotless one, and a category would read differently on one
        /// user's machine.</para>
        /// </summary>
        public static string CategoryText(string name)
        {
            return (name ?? string.Empty).ToUpperInvariant();
        }

        /// <summary>
        /// A tool's label as it is shown: Capitalize Each Word, leaving acronyms and camel-cased
        /// names exactly as they were written.
        ///
        /// <para><b>The rule is "a word that already carries an upper-case letter past its first is
        /// telling you something".</b> Blind title-casing turns <c>ReLU</c> into <c>Relu</c>,
        /// <c>Conv2D</c> into <c>Conv2d</c>, <c>CA H-Bond</c> into <c>Ca H-bond</c> and
        /// <c>SURPASS</c> into <c>Surpass</c> — every one of them a term of art in this product's
        /// domain, and every one of them wrong. So a word is only re-cased when it is entirely
        /// lower case, which is exactly the sentence-case leftovers this is meant to fix
        /// ("Inputs and outputs" -> "Inputs And Outputs", "Dataset file" -> "Dataset File").</para>
        /// </summary>
        public static string ItemText(string label)
        {
            if (string.IsNullOrEmpty(label)) return string.Empty;

            // A label with brackets or a comma carries notation -- units such as "nm" and "kJ/mol",
            // a Greek sigma, an index such as "i,i+4" -- and its case is part of what it says.
            // Re-casing it would print "Nm", "Σ" and "I,I+4". It is shown as written.
            if (label.IndexOfAny(new[] { '(', ',' }) >= 0) return label;

            var output = new StringBuilder(label.Length);
            int index = 0;
            while (index < label.Length)
            {
                if (!IsWordCharacter(label[index]))
                {
                    output.Append(label[index]);
                    index++;
                    continue;
                }

                int start = index;
                while (index < label.Length && IsWordCharacter(label[index])) index++;
                output.Append(CaseWord(label.Substring(start, index - start)));
            }
            return output.ToString();
        }

        // Hyphen and apostrophe end a word for capitalisation purposes -- "CA H-Bond" has to keep
        // its B upper case, and treating the hyphen as part of the word would make "H-bond" one
        // token whose second letter is lower case and therefore "re-casable".
        private static bool IsWordCharacter(char c)
        {
            return char.IsLetterOrDigit(c);
        }

        private static string CaseWord(string word)
        {
            for (int i = 1; i < word.Length; i++)
                if (char.IsUpper(word[i])) return word;      // acronym or camel case: leave it

            if (word.Length == 1) return word.ToUpperInvariant();
            return char.ToUpperInvariant(word[0])
                 + word.Substring(1).ToLowerInvariant();
        }

        /// <summary>
        /// The collapsible's glyph: one shape, one colour, drawn antialiased, in every palette.
        ///
        /// <para>A triangle rather than a plus/minus box, pointing right when the section is folded
        /// and down when it is open, so the direction says which way it will move. Drawn rather than
        /// typed, because a text glyph picks up whatever font the row happens to carry and the
        /// three palettes did not agree on that either.</para>
        /// </summary>
        public static void DrawChevron(Graphics g, Rectangle bounds, bool expanded, Color colour)
        {
            SmoothingMode previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                float cx = bounds.X + bounds.Width / 2f;
                float cy = bounds.Y + bounds.Height / 2f;
                PointF[] triangle = expanded
                    ? new[]
                      {
                          new PointF(cx - 4f, cy - 2f),
                          new PointF(cx + 4f, cy - 2f),
                          new PointF(cx, cy + 3f),
                      }
                    : new[]
                      {
                          new PointF(cx - 2f, cy - 4f),
                          new PointF(cx + 3f, cy),
                          new PointF(cx - 2f, cy + 4f),
                      };
                using (var brush = new SolidBrush(colour))
                    g.FillPolygon(brush, triangle);
            }
            finally
            {
                g.SmoothingMode = previous;
            }
        }

        /// <summary>The soft wash behind a hovered or pressed row. Alpha over the accent rather
        /// than a flat colour, so it reads the same on every Luna theme.</summary>
        public static Color RowWash(Color accent, bool pressed, bool hot)
        {
            if (pressed) return Color.FromArgb(70, accent);
            if (hot) return Color.FromArgb(38, accent);
            return Color.FromArgb(22, accent);
        }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Formats a count for a category header's right-hand side, e.g. "12".</summary>
        public static string Count(int n)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }
    }
}
