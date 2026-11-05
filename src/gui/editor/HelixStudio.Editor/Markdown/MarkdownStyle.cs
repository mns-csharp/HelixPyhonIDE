using System;
using System.Collections.Generic;
using System.Drawing;

namespace HelixPythonNamespace.Editor.Markdown
{
    /// <summary>What a laid-out piece of the document is.</summary>
    public enum MarkdownVisualKind
    {
        /// <summary>A run of text drawn with one font and colour.</summary>
        Text,
        /// <summary>A filled rectangle — code-block background, table header shading, quote bar.</summary>
        Fill,
        /// <summary>A one-pixel line — thematic break, table gridline, heading underline.</summary>
        Rule,
        /// <summary>The placeholder box drawn where an image would be.</summary>
        ImageBox,
    }

    /// <summary>
    /// One positioned, ready-to-paint piece of the document.
    ///
    /// Layout and painting are separate passes over this list, and that split is what makes the
    /// control usable: layout needs a <see cref="Graphics"/> to measure text and is the expensive
    /// half, so it runs once per (document, width) and the paint handler — which fires on every
    /// scroll tick — only walks the visuals that intersect the clip rectangle.
    /// </summary>
    public sealed class MarkdownVisual
    {
        public MarkdownVisualKind Kind;
        public Rectangle Bounds;
        public string Text;
        public Font Font;
        public Color ForeColor;
        public Color BackColor;

        /// <summary>Non-null when clicking this run should follow a link.</summary>
        public string LinkTarget;

        /// <summary>1-based source line, so a future preview↔source sync has something to key on.</summary>
        public int SourceLine;
    }

    /// <summary>
    /// Fonts, colours and spacing for the Markdown preview, with a font cache.
    ///
    /// <b>The cache is not an optimisation, it is a correctness requirement.</b> A GDI+
    /// <see cref="Font"/> is an unmanaged handle; creating one per text run during layout would
    /// leak a handle per word on every re-layout, and re-layout runs on every resize. Every font
    /// this class hands out is owned by the instance and disposed with it — callers must never
    /// dispose a font they got from here.
    /// </summary>
    public sealed class MarkdownStyle : IDisposable
    {
        private readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>(StringComparer.Ordinal);
        private bool _disposed;

        public MarkdownStyle() : this("Segoe UI", 9.75f, "Consolas", 9f) { }

        public MarkdownStyle(string bodyFamily, float bodySize, string codeFamily, float codeSize)
        {
            BodyFamily = bodyFamily;
            BodySize = bodySize;
            CodeFamily = codeFamily;
            CodeSize = codeSize;
        }

        public string BodyFamily { get; }
        public float BodySize { get; }
        public string CodeFamily { get; }
        public float CodeSize { get; }

        // Colours chosen to sit on the Luna-era white document background the editor already uses,
        // and to match the syntax palette PythonEditorControl applies (link blue 0,102,204; the same
        // red-brown 163,21,21 the editor uses for strings is reused for inline code).
        public Color Background = Color.White;
        public Color Foreground = Color.FromArgb(32, 32, 32);
        public Color HeadingForeground = Color.FromArgb(16, 16, 16);
        public Color LinkForeground = Color.FromArgb(0, 102, 204);
        public Color CodeForeground = Color.FromArgb(163, 21, 21);
        public Color CodeBackground = Color.FromArgb(246, 246, 246);
        public Color CodeBlockForeground = Color.FromArgb(32, 32, 32);
        public Color BorderColor = Color.FromArgb(214, 214, 214);
        public Color QuoteBar = Color.FromArgb(196, 196, 196);
        public Color QuoteForeground = Color.FromArgb(96, 96, 96);
        public Color TableHeaderBackground = Color.FromArgb(242, 242, 242);

        public int PagePadding = 16;
        public int ParagraphSpacing = 10;
        public int BlockSpacing = 8;
        public int ListIndent = 24;
        public int QuoteIndent = 16;
        public int CodePadding = 8;
        public int TableCellPadding = 6;

        /// <summary>Point size for a heading level, 1–6.</summary>
        public float HeadingSize(int level)
        {
            switch (level)
            {
                case 1: return BodySize * 1.9f;
                case 2: return BodySize * 1.55f;
                case 3: return BodySize * 1.3f;
                case 4: return BodySize * 1.15f;
                case 5: return BodySize * 1.05f;
                default: return BodySize;
            }
        }

        /// <summary>
        /// A cached font. Never dispose the result — <see cref="MarkdownStyle"/> owns it.
        /// </summary>
        public Font Get(bool bold, bool italic, bool strikeout, bool monospace, float size)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MarkdownStyle));

            FontStyle style = FontStyle.Regular;
            if (bold) style |= FontStyle.Bold;
            if (italic) style |= FontStyle.Italic;
            if (strikeout) style |= FontStyle.Strikeout;

            string family = monospace ? CodeFamily : BodyFamily;
            string key = family + "|" + size.ToString("0.##") + "|" + ((int)style).ToString();

            Font font;
            if (_fonts.TryGetValue(key, out font)) return font;

            try { font = new Font(family, size, style); }
            catch
            {
                // A missing family is not a reason to fail a render. Fall back to the stock UI font
                // at the requested size and style rather than throwing out of a paint handler.
                try { font = new Font(FontFamily.GenericSansSerif, size, style); }
                catch { font = SystemFonts.DefaultFont; }
            }
            _fonts[key] = font;
            return font;
        }

        public Font Body { get { return Get(false, false, false, false, BodySize); } }
        public Font Code { get { return Get(false, false, false, true, CodeSize); } }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Font font in _fonts.Values)
            {
                // SystemFonts.DefaultFont is shared and must outlive this object.
                if (font != null && !ReferenceEquals(font, SystemFonts.DefaultFont)) font.Dispose();
            }
            _fonts.Clear();
        }
    }
}
