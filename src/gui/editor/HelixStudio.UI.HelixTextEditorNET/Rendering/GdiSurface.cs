using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace HelixTextEditorNET.Rendering
{
    // Font + measurement cache shared by paint and hit-testing. Fonts and per-font
    // ASCII advance tables live for the control's lifetime (fonts are expensive to
    // create and metrics are hot in XFromPosition loops). Port of the platform
    // Surface, backed by GDI (TextRenderer) for crisp, pixel-snapped text.
    internal sealed class GdiSurface : IDisposable
    {
        private struct FontKey : IEquatable<FontKey>
        {
            public string Name;
            public float Size;
            public bool Bold;
            public bool Italic;

            public bool Equals(FontKey other)
            {
                return Size == other.Size && Bold == other.Bold && Italic == other.Italic &&
                       string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is FontKey && Equals((FontKey)obj);
            }

            public override int GetHashCode()
            {
                return (Name != null ? Name.GetHashCode() : 0) ^ Size.GetHashCode() ^
                       (Bold ? 2 : 0) ^ (Italic ? 1 : 0);
            }
        }

        private sealed class FontEntry
        {
            public Font Font;
            public double[] AsciiWidths;  // advance of chars 32..126; null until measured
            public double Ascent;
            public double Height;
        }

        private readonly Dictionary<FontKey, FontEntry> _fonts = new Dictionary<FontKey, FontEntry>();
        private static readonly TextFormatFlags MeasureFlags =
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        public Font GetFont(StyleSpec spec)
        {
            return GetEntry(spec).Font;
        }

        private FontEntry GetEntry(StyleSpec spec)
        {
            var key = new FontKey
            {
                Name = spec.FontName,
                Size = spec.SizeF,
                Bold = spec.Bold,
                Italic = spec.Italic
            };
            FontEntry entry;
            if (_fonts.TryGetValue(key, out entry)) return entry;
            FontStyle fs = FontStyle.Regular;
            if (spec.Bold) fs |= FontStyle.Bold;
            if (spec.Italic) fs |= FontStyle.Italic;
            Font font;
            try
            {
                font = new Font(spec.FontName, Math.Max(1f, spec.SizeF), fs);
            }
            catch (ArgumentException)
            {
                font = new Font(FontFamily.GenericMonospace, Math.Max(1f, spec.SizeF), fs);
            }
            entry = new FontEntry { Font = font };
            // GetHeight is the line spacing; the ascent is its proportional share of
            // the font family's design metrics.
            FontFamily fam = font.FontFamily;
            entry.Ascent = font.GetHeight() * fam.GetCellAscent(fs) / fam.GetLineSpacing(fs);
            entry.Height = Math.Ceiling(font.GetHeight());
            _fonts[key] = entry;
            return entry;
        }

        public double WidthText(StyleSpec spec, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            FontEntry entry = GetEntry(spec);
            EnsureAscii(entry);
            // Fast path: pure printable-ASCII runs sum the cached advances.
            double w = 0;
            bool allAscii = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < ' ' || c > '~')
                {
                    allAscii = false;
                    break;
                }
                w += entry.AsciiWidths[c - ' '];
            }
            if (allAscii) return w;
            Size sz = TextRenderer.MeasureText(text, entry.Font, new Size(int.MaxValue, int.MaxValue),
                                               MeasureFlags);
            return sz.Width;
        }

        public double WidthChar(StyleSpec spec, char c)
        {
            FontEntry entry = GetEntry(spec);
            if (c >= ' ' && c <= '~')
            {
                EnsureAscii(entry);
                return entry.AsciiWidths[c - ' '];
            }
            if (c == '\t')
            {
                EnsureAscii(entry);
                return entry.AsciiWidths[0];  // resolved by the caller via tab logic
            }
            Size sz = TextRenderer.MeasureText(c.ToString(), entry.Font,
                                               new Size(int.MaxValue, int.MaxValue), MeasureFlags);
            return sz.Width;
        }

        private static void EnsureAscii(FontEntry entry)
        {
            if (entry.AsciiWidths != null) return;
            var widths = new double[95];
            // Measure via a doubled string to strip constant overhead: w = m(cc) - m(c).
            for (int i = 0; i < 95; i++)
            {
                string one = ((char)(' ' + i)).ToString();
                Size sz1 = TextRenderer.MeasureText(one, entry.Font,
                                                    new Size(int.MaxValue, int.MaxValue), MeasureFlags);
                Size sz2 = TextRenderer.MeasureText(one + one, entry.Font,
                                                    new Size(int.MaxValue, int.MaxValue), MeasureFlags);
                widths[i] = sz2.Width - sz1.Width;
                if (widths[i] <= 0) widths[i] = sz1.Width;
            }
            entry.AsciiWidths = widths;
        }

        public double Ascent(StyleSpec spec)
        {
            return GetEntry(spec).Ascent;
        }

        public double Height(StyleSpec spec)
        {
            return GetEntry(spec).Height;
        }

        public void DrawText(Graphics g, StyleSpec spec, double x, double yTop, string text, Color fore)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(g, text, GetEntry(spec).Font,
                                  new Point((int)Math.Round(x), (int)Math.Round(yTop)), fore,
                                  MeasureFlags);
        }

        // Invalidate all cached fonts (style table changed).
        public void Reset()
        {
            foreach (var kv in _fonts) kv.Value.Font.Dispose();
            _fonts.Clear();
        }

        public void Dispose()
        {
            Reset();
        }
    }
}
