using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using HelixTextEditorNET;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>
    /// Builds one 16×16 icon per <see cref="CompletionKind"/> and registers them
    /// with a Scintilla instance. The integer type returned by <see cref="TypeFor"/>
    /// is appended to each AutoCShow entry as <c>label?N</c> so Scintilla paints
    /// the icon next to the row.
    /// </summary>
    public static class CompletionIcons
    {
        private static readonly Dictionary<CompletionKind, int> _typeByKind =
            new Dictionary<CompletionKind, int>
            {
                { CompletionKind.Keyword,   1 },
                { CompletionKind.Builtin,   2 },
                { CompletionKind.Class,     3 },
                { CompletionKind.Method,    4 },
                { CompletionKind.Function,  5 },
                { CompletionKind.Property,  6 },
                { CompletionKind.Variable,  7 },
                { CompletionKind.Parameter, 8 },
                { CompletionKind.Module,    9 },
                { CompletionKind.Field,     10 },
                { CompletionKind.Snippet,   11 },
            };

        public static int TypeFor(CompletionKind k)
            => _typeByKind.TryGetValue(k, out int v) ? v : 0;

        /// <summary>Register all icons with <paramref name="editor"/>. Safe to call once at editor init.</summary>
        public static void RegisterAll(Scintilla editor)
        {
            if (editor == null) return;
            foreach (var kv in _typeByKind)
            {
                Bitmap bmp = BuildIcon(kv.Key);
                editor.RegisterRgbaImage(kv.Value, bmp);
            }
        }

        // ------------------------------------------------------------- icon factory

        private static Bitmap BuildIcon(CompletionKind kind)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                DrawShape(g, kind);
            }
            return bmp;
        }

        private static void DrawShape(Graphics g, CompletionKind kind)
        {
            // Palette loosely inspired by VS 2017 IntelliSense icons.
            Color purple = Color.FromArgb(160, 90, 160);
            Color green  = Color.FromArgb(90, 160, 90);
            Color blue   = Color.FromArgb(70, 130, 200);
            Color teal   = Color.FromArgb(70, 170, 170);
            Color amber  = Color.FromArgb(200, 150, 60);
            Color grey   = Color.FromArgb(130, 130, 130);

            switch (kind)
            {
                case CompletionKind.Class:
                    FillRoundedSquare(g, purple);
                    DrawInitial(g, "C", Color.White);
                    break;
                case CompletionKind.Method:
                    FillDiamond(g, purple);
                    break;
                case CompletionKind.Function:
                    FillDiamond(g, green);
                    break;
                case CompletionKind.Property:
                    FillRoundedSquare(g, grey);
                    DrawInitial(g, "P", Color.White);
                    break;
                case CompletionKind.Field:
                    FillRoundedSquare(g, blue);
                    DrawInitial(g, "F", Color.White);
                    break;
                case CompletionKind.Variable:
                    FillCircle(g, blue);
                    break;
                case CompletionKind.Parameter:
                    FillCircle(g, blue);
                    using (var pen = new Pen(blue, 2)) g.DrawLine(pen, 3, 13, 13, 13);
                    break;
                case CompletionKind.Module:
                    FillRoundedSquare(g, amber);
                    DrawInitial(g, "M", Color.White);
                    break;
                case CompletionKind.Keyword:
                    FillRoundedSquare(g, teal);
                    DrawInitial(g, "K", Color.White);
                    break;
                case CompletionKind.Builtin:
                    FillRoundedSquare(g, teal);
                    DrawInitial(g, "B", Color.White);
                    break;
                case CompletionKind.Snippet:
                    FillRoundedSquare(g, green);
                    DrawInitial(g, "S", Color.White);
                    break;
            }
        }

        private static void FillRoundedSquare(Graphics g, Color c)
        {
            using (var path = RoundRect(new Rectangle(2, 2, 12, 12), 3))
            using (var brush = new SolidBrush(c))
            {
                g.FillPath(brush, path);
            }
        }

        private static void FillCircle(Graphics g, Color c)
        {
            using (var brush = new SolidBrush(c)) g.FillEllipse(brush, 3, 3, 10, 10);
        }

        private static void FillDiamond(Graphics g, Color c)
        {
            Point[] pts = { new Point(8, 2), new Point(14, 8), new Point(8, 14), new Point(2, 8) };
            using (var brush = new SolidBrush(c)) g.FillPolygon(brush, pts);
        }

        private static void DrawInitial(Graphics g, string ch, Color color)
        {
            using (var font = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point))
            using (var brush = new SolidBrush(color))
            {
                var sz = g.MeasureString(ch, font);
                g.DrawString(ch, font, brush, (16 - sz.Width) / 2f, (16 - sz.Height) / 2f);
            }
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
