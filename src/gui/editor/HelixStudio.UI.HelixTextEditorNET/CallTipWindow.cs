using System;
using System.Drawing;
using System.Windows.Forms;

namespace HelixTextEditorNET
{
    // The call-tip popup: a borderless, non-activating window drawing multi-line
    // text with an optional highlighted span (CallTipSetHlt).
    internal sealed class CallTipWindow : Form
    {
        private string _text = string.Empty;
        private int _hlStart = -1;
        private int _hlEnd = -1;
        private Color _foreHlt = Color.RoyalBlue;
        private Font _font;

        public CallTipWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // Deliberately NOT TopMost — see the identical note in AutoCListWindow's constructor.
            BackColor = Color.FromArgb(255, 255, 225);  // classic tooltip yellow
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
                cp.ExStyle = HelixPythonNamespace.Core.TestEnvironment.ExStyle(cp.Style, cp.ExStyle);   // test runs only
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HelixPythonNamespace.Core.TestEnvironment.HandleCreated(Handle, CreateParams.Style);   // alpha 0 in a test run
        }

        public void SetTip(string text, Font font)
        {
            _text = text ?? string.Empty;
            Font replacement = (Font)(font ?? SystemFonts.DefaultFont).Clone();
            Font previous = _font;
            _font = replacement;
            if (previous != null) previous.Dispose();
            _hlStart = _hlEnd = -1;
            Size sz = TextRenderer.MeasureText(_text, _font,
                                               new Size(int.MaxValue, int.MaxValue),
                                               TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            Size = new Size(sz.Width + 12, sz.Height + 8);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && _font != null)
            {
                _font.Dispose();
                _font = null;
            }
        }

        public void SetHighlight(int start, int end)
        {
            _hlStart = start;
            _hlEnd = end;
            Invalidate();
        }

        public void SetHighlightColor(Color color)
        {
            _foreHlt = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Font font = _font ?? SystemFonts.DefaultFont;
            e.Graphics.DrawRectangle(Pens.Gray, 0, 0, Width - 1, Height - 1);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            if (_hlStart >= 0 && _hlEnd > _hlStart && _hlEnd <= _text.Length)
            {
                // Draw pre / highlighted / post segments in sequence (single line
                // call tips; multi-line tips highlight within their line).
                string pre = _text.Substring(0, _hlStart);
                string mid = _text.Substring(_hlStart, _hlEnd - _hlStart);
                string post = _text.Substring(_hlEnd);
                int x = 6;
                foreach (var seg in new[] { new { T = pre, C = SystemColors.InfoText, B = false },
                                            new { T = mid, C = _foreHlt, B = true },
                                            new { T = post, C = SystemColors.InfoText, B = false } })
                {
                    if (seg.T.Length == 0) continue;
                    using (Font f = seg.B ? new Font(font, FontStyle.Bold) : (Font)font.Clone())
                    {
                        TextRenderer.DrawText(e.Graphics, seg.T, f, new Point(x, 4), seg.C, flags);
                        x += TextRenderer.MeasureText(seg.T, f, new Size(int.MaxValue, int.MaxValue),
                                                      flags).Width;
                    }
                }
            }
            else
            {
                TextRenderer.DrawText(e.Graphics, _text, font, new Point(6, 4), SystemColors.InfoText,
                                      flags);
            }
        }
    }
}
