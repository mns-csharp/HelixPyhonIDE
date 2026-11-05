namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Drawing.Text;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.Complexity;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// Non-modal viewer for one or more Complexity-panel rows' growth curves, opened via the
    /// "Plot" context-menu action (ComplexityPanel). With a single row selected, draws its
    /// worst-case (O) curve, and — when a conditional early exit gave the row a genuinely
    /// different best case — the best-case (Ω) curve too, so the shape of the case split (e.g.
    /// linear search's flat Ω(1) against its rising O(n)) is visible at a glance. With multiple
    /// rows selected, draws one worst-case curve per function (the standard basis for comparing
    /// algorithms) overlaid on a single shared plot, so their relative growth is directly
    /// comparable instead of eyeballed across separate windows.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed class AsymptoticPlotForm : LunaForm
    {
        public AsymptoticPlotForm(ComplexityFinding finding)
            : this(new[] { finding ?? throw new ArgumentNullException(nameof(finding)) })
        {
        }

        public AsymptoticPlotForm(IReadOnlyList<ComplexityFinding> findings)
        {
            if (findings == null || findings.Count == 0) throw new ArgumentException("At least one finding is required.", nameof(findings));

            Text = "Asymptotic Growth — " + (findings.Count == 1
                ? findings[0].MemberName
                : "comparing " + findings.Count + " functions");
            ClientSize = new Size(680, 480);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = true;
            Icon = LunaForm.GetAppIcon();

            PlotCanvas canvas = new PlotCanvas(findings) { Dock = DockStyle.Fill };
            Controls.Add(canvas);

            Button close = new Button { Text = "Close", Dock = DockStyle.Bottom, Height = 32 };
            close.Click += delegate { Close(); };
            Controls.Add(close);
            CancelButton = close;
        }

        private sealed class PlotCanvas : Panel
        {
            private const int SampleCount = 24;

            // A qualitative palette (color-blind-friendlier than pure RGB primaries), cycled if
            // more functions are compared than colors — a plot with that many overlapping curves
            // is already hard to read regardless of palette size.
            private static readonly Color[] Palette =
            {
                Color.FromArgb(200, 40, 40),   // red
                Color.FromArgb(40, 110, 190),  // blue
                Color.FromArgb(40, 140, 70),   // green
                Color.FromArgb(210, 140, 20),  // orange
                Color.FromArgb(130, 70, 180),  // purple
                Color.FromArgb(30, 150, 150),  // teal
                Color.FromArgb(160, 90, 50),   // brown
                Color.FromArgb(190, 60, 140),  // magenta
            };

            private readonly IReadOnlyList<ComplexityFinding> _findings;
            private readonly bool _multiMode;

            // Single-finding mode data.
            private readonly double[] _worstY;
            private readonly double[] _bestY;
            private readonly bool _sameShape;

            // Multi-finding mode data: one worst-case curve per finding, same order as _findings.
            private readonly double[][] _curves;

            public PlotCanvas(IReadOnlyList<ComplexityFinding> findings)
            {
                _findings = findings;
                _multiMode = findings.Count > 1;
                DoubleBuffered = true;
                BackColor = Color.White;

                if (_multiMode)
                {
                    _curves = findings.Select(f => SamplePoints(f.WorstCaseKind, f.WorstCaseDegree)).ToArray();
                }
                else
                {
                    ComplexityFinding f = findings[0];
                    _worstY = SamplePoints(f.WorstCaseKind, f.WorstCaseDegree);
                    _bestY = SamplePoints(f.BestCaseKind, f.BestCaseDegree);
                    _sameShape = f.BestCaseKind == f.WorstCaseKind && f.BestCaseDegree == f.WorstCaseDegree;
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                int legendBottom = _multiMode ? DrawMultiHeader(g) : DrawSingleHeader(g);

                int marginLeft = 55, marginRight = 25, marginTop = legendBottom + 10, marginBottom = 45;
                int plotWidth = Width - marginLeft - marginRight;
                int plotHeight = Height - marginTop - marginBottom;
                if (plotWidth <= 10 || plotHeight <= 10) return;
                Rectangle plotRect = new Rectangle(marginLeft, marginTop, plotWidth, plotHeight);

                double maxY = _multiMode ? _curves.Max(Max) : Math.Max(Max(_worstY), Max(_bestY));
                if (maxY <= 0) maxY = 1;

                using (Pen axisPen = new Pen(Color.Black, 1.5f))
                {
                    g.DrawLine(axisPen, plotRect.Left, plotRect.Bottom, plotRect.Right, plotRect.Bottom);
                    g.DrawLine(axisPen, plotRect.Left, plotRect.Top, plotRect.Left, plotRect.Bottom);
                }
                using (Font axisFont = new Font("Segoe UI", 8f))
                using (Brush axisBrush = new SolidBrush(Color.DimGray))
                {
                    g.DrawString("n (input size) →", axisFont, axisBrush, plotRect.Right - 95, plotRect.Bottom + 8);
                    GraphicsState state = g.Save();
                    g.TranslateTransform(marginLeft - 42, plotRect.Top + plotHeight / 2 + 35);
                    g.RotateTransform(-90);
                    g.DrawString("steps →", axisFont, axisBrush, 0, 0);
                    g.Restore(state);
                }

                if (_multiMode)
                {
                    for (int i = 0; i < _curves.Length; i++)
                        DrawCurve(g, plotRect, _curves[i], maxY, Palette[i % Palette.Length], 2.5f);
                }
                else
                {
                    Color worstColor = Palette[0], bestColor = Palette[2];
                    DrawCurve(g, plotRect, _worstY, maxY, worstColor, 2.5f);
                    if (!_sameShape) DrawCurve(g, plotRect, _bestY, maxY, bestColor, 2.5f);
                }
            }

            // Title + "Worst case O(...)" / "Best case Ω(...)" legend for a single function.
            // Returns the Y coordinate the plot area should start below.
            private int DrawSingleHeader(Graphics g)
            {
                ComplexityFinding f = _findings[0];
                Color worstColor = Palette[0], bestColor = Palette[2];
                using (Font titleFont = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (Font legendFont = new Font("Segoe UI", 9f))
                using (Brush titleBrush = new SolidBrush(Color.Black))
                using (Brush worstBrush = new SolidBrush(worstColor))
                using (Brush bestBrush = new SolidBrush(bestColor))
                {
                    g.DrawString(f.MemberName, titleFont, titleBrush, 10, 8);
                    g.DrawString("Worst case " + f.WorstCase, legendFont, worstBrush, 10, 30);
                    if (!_sameShape)
                        g.DrawString("Best case " + f.BestCase, legendFont, bestBrush, 220, 30);
                }
                return 55;
            }

            // Title + one "<name>  O(...)" legend row per compared function, stacked vertically
            // since a handful of functions won't fit side by side the way the two-curve single
            // view does.
            private int DrawMultiHeader(Graphics g)
            {
                using (Font titleFont = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (Brush titleBrush = new SolidBrush(Color.Black))
                    g.DrawString("Comparing " + _findings.Count + " functions (worst case)", titleFont, titleBrush, 10, 8);

                const int rowHeight = 16;
                using (Font legendFont = new Font("Segoe UI", 9f))
                {
                    for (int i = 0; i < _findings.Count; i++)
                    {
                        Color c = Palette[i % Palette.Length];
                        using (Brush b = new SolidBrush(c))
                            g.DrawString(_findings[i].MemberName + "   " + _findings[i].WorstCase, legendFont, b, 10, 30 + i * rowHeight);
                    }
                }
                return 30 + _findings.Count * rowHeight + 5;
            }

            private static void DrawCurve(Graphics g, Rectangle plotRect, double[] ys, double maxY, Color color, float width)
            {
                if (ys.Length < 2) return;
                PointF[] pts = new PointF[ys.Length];
                for (int i = 0; i < ys.Length; i++)
                {
                    float x = plotRect.Left + (float)i / (ys.Length - 1) * plotRect.Width;
                    float yNorm = (float)(ys[i] / maxY);
                    float y = plotRect.Bottom - yNorm * plotRect.Height;
                    pts[i] = new PointF(x, y);
                }
                using (Pen pen = new Pen(color, width) { LineJoin = LineJoin.Round })
                    g.DrawLines(pen, pts);
            }

            private static double Max(double[] arr)
            {
                double m = 0;
                foreach (double v in arr) if (v > m) m = v;
                return m;
            }

            private static double[] SamplePoints(GrowthOrderKind kind, int degree)
            {
                double[] result = new double[SampleCount];
                for (int n = 1; n <= SampleCount; n++)
                    result[n - 1] = Evaluate(kind, degree, n);
                return result;
            }

            // Mirrors ComplexityEngine's own growth-rate ladder — see
            // .claude/skills/helixstudio/chapters/19-asymptotic-complexity.md.
            private static double Evaluate(GrowthOrderKind kind, int degree, int n)
            {
                switch (kind)
                {
                    case GrowthOrderKind.Constant: return 1.0;
                    case GrowthOrderKind.Log: return Math.Log(Math.Max(n, 2), 2);
                    case GrowthOrderKind.Linear: return n;
                    case GrowthOrderKind.LinearLog: return n * Math.Log(Math.Max(n, 2), 2);
                    case GrowthOrderKind.Poly: return Math.Pow(n, degree <= 0 ? 2 : degree);
                    case GrowthOrderKind.Exponential: return Math.Pow(2, n);
                    default: return n;
                }
            }
        }
    }
}
