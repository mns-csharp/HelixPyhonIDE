using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.Markdown;

namespace HelixPythonNamespace.Editor.Markdown
{
    /// <summary>Raised when the user clicks a link in the preview.</summary>
    public sealed class MarkdownLinkClickedEventArgs : EventArgs
    {
        public MarkdownLinkClickedEventArgs(string target) { Target = target; }

        /// <summary>The raw destination from the document — may be a URL, or a relative file path.</summary>
        public string Target { get; }

        /// <summary>Set to true by a handler that dealt with it; otherwise the control does nothing.</summary>
        public bool Handled { get; set; }
    }

    /// <summary>
    /// A scrollable, GDI-rendered Markdown preview.
    ///
    /// <b>Why not a <see cref="WebBrowser"/>.</b> The obvious shortcut is to render the HTML this
    /// engine can already produce into the WinForms browser control. That control is the legacy IE
    /// engine: it defaults to an emulation mode that gets modern CSS wrong, it puts an ActiveX host
    /// and a script engine in the process for the sake of showing a README, it navigates on any
    /// click by default, and it makes a network request the moment a document references a remote
    /// image. Drawing the document ourselves is more code and no surprises — and it is the same
    /// choice this repository already made for its editor and its canvas.
    ///
    /// Layout runs on a (document, width) change; painting only walks the visuals intersecting the
    /// clip rectangle, so scrolling a long README does not re-measure anything.
    /// </summary>
    public class MarkdownViewControl : Control
    {
        private readonly MarkdownStyle _style = new MarkdownStyle();
        private MarkdownDocument _document;
        private List<MarkdownVisual> _visuals = new List<MarkdownVisual>();
        private Size _contentSize;
        private int _scrollX;
        private int _scrollY;
        private int _layoutWidth = -1;
        private MarkdownVisual _hotLink;
        private readonly VScrollBar _vertical = new VScrollBar();
        private readonly HScrollBar _horizontal = new HScrollBar();

        public event EventHandler<MarkdownLinkClickedEventArgs> LinkClicked;

        public MarkdownViewControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = _style.Background;
            // Focusable so the arrow keys and Page Up/Down reach OnKeyDown once the user clicks in.
            TabStop = true;

            _vertical.Dock = DockStyle.Right;
            _vertical.Scroll += (s, e) => { _scrollY = e.NewValue; Invalidate(); };
            _vertical.ValueChanged += (s, e) => { _scrollY = _vertical.Value; Invalidate(); };

            _horizontal.Dock = DockStyle.Bottom;
            _horizontal.Scroll += (s, e) => { _scrollX = e.NewValue; Invalidate(); };
            _horizontal.ValueChanged += (s, e) => { _scrollX = _horizontal.Value; Invalidate(); };

            Controls.Add(_vertical);
            Controls.Add(_horizontal);
        }

        /// <summary>The rendered document. Setting it re-lays out and scrolls back to the top.</summary>
        public MarkdownDocument Document
        {
            get { return _document; }
            set
            {
                _document = value;
                _layoutWidth = -1;
                _scrollX = _scrollY = 0;
                Relayout();
                Invalidate();
            }
        }

        /// <summary>Parses and displays <paramref name="markdown"/>.</summary>
        public void SetMarkdown(string markdown)
        {
            Document = MarkdownParser.Parse(markdown ?? string.Empty);
        }

        /// <summary>The document's H1 text, or null — for a tab caption.</summary>
        public string DocumentTitle
        {
            get { return _document == null ? null : _document.Title; }
        }

        /// <summary>The laid-out pieces. Exposed for tests, which assert on layout without painting.</summary>
        internal IList<MarkdownVisual> Visuals { get { return _visuals; } }

        /// <summary>The full extent of the laid-out document, which drives the scroll range.</summary>
        internal Size ContentSize { get { return _contentSize; } }

        private Rectangle Viewport
        {
            get
            {
                int w = Math.Max(0, ClientSize.Width - (_vertical.Visible ? _vertical.Width : 0));
                int h = Math.Max(0, ClientSize.Height - (_horizontal.Visible ? _horizontal.Height : 0));
                return new Rectangle(0, 0, w, h);
            }
        }

        // ---- layout ------------------------------------------------------------------------------

        /// <summary>
        /// Re-lays the document out for the current width, if the width actually changed.
        ///
        /// The width guard is what keeps this cheap: WinForms raises a resize for vertical-only
        /// changes and for every scrollbar appearing, and re-measuring a long document on each of
        /// those is visible as lag while dragging a splitter.
        /// </summary>
        private void Relayout()
        {
            int width = Math.Max(80, ClientSize.Width - _vertical.Width);
            if (_document == null)
            {
                _visuals = new List<MarkdownVisual>();
                _contentSize = Size.Empty;
                _layoutWidth = width;
                UpdateScrollBars();
                return;
            }
            if (width == _layoutWidth) return;

            using (Graphics g = CreateGraphics())
            {
                Size total;
                _visuals = new MarkdownLayoutEngine(_style).Layout(_document, g, width, out total);
                _contentSize = total;
            }
            _layoutWidth = width;
            UpdateScrollBars();
        }

        private void UpdateScrollBars()
        {
            Rectangle view = Viewport;

            bool needVertical = _contentSize.Height > view.Height;
            bool needHorizontal = _contentSize.Width > view.Width;
            _vertical.Visible = needVertical;
            _horizontal.Visible = needHorizontal;

            int viewHeight = Math.Max(1, ClientSize.Height - (needHorizontal ? _horizontal.Height : 0));
            int viewWidth = Math.Max(1, ClientSize.Width - (needVertical ? _vertical.Width : 0));

            // LargeChange counts toward Maximum in WinForms, so the usable maximum is
            // (range - LargeChange + 1). Setting Maximum to the raw content height is the classic
            // off-by-a-page bug that leaves the last screenful unreachable.
            _vertical.LargeChange = viewHeight;
            _vertical.SmallChange = 24;
            _vertical.Maximum = Math.Max(0, _contentSize.Height);
            _vertical.Value = Clamp(_scrollY, 0, Math.Max(0, _vertical.Maximum - viewHeight + 1));

            _horizontal.LargeChange = viewWidth;
            _horizontal.SmallChange = 24;
            _horizontal.Maximum = Math.Max(0, _contentSize.Width);
            _horizontal.Value = Clamp(_scrollX, 0, Math.Max(0, _horizontal.Maximum - viewWidth + 1));

            _scrollY = _vertical.Value;
            _scrollX = _horizontal.Value;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (max < min) return min;
            return value < min ? min : (value > max ? max : value);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
            Invalidate();
        }

        // ---- painting ------------------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_style.Background);
            if (_visuals.Count == 0) return;

            Rectangle clip = e.ClipRectangle;
            clip.Offset(_scrollX, _scrollY);

            foreach (MarkdownVisual visual in _visuals)
            {
                if (!visual.Bounds.IntersectsWith(clip)) continue;
                Rectangle bounds = visual.Bounds;
                bounds.Offset(-_scrollX, -_scrollY);

                switch (visual.Kind)
                {
                    case MarkdownVisualKind.Fill:
                    case MarkdownVisualKind.Rule:
                        using (var brush = new SolidBrush(visual.BackColor)) e.Graphics.FillRectangle(brush, bounds);
                        break;

                    case MarkdownVisualKind.ImageBox:
                        using (var pen = new Pen(visual.BackColor))
                            e.Graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
                        TextRenderer.DrawText(e.Graphics, "🖼 " + visual.Text, visual.Font,
                            new Rectangle(bounds.X + 3, bounds.Y + 3, bounds.Width - 6, bounds.Height - 6),
                            visual.ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        break;

                    case MarkdownVisualKind.Text:
                        if (visual.BackColor != Color.Empty && visual.BackColor.A != 0)
                            using (var brush = new SolidBrush(visual.BackColor)) e.Graphics.FillRectangle(brush, bounds);
                        TextRenderer.DrawText(e.Graphics, visual.Text, visual.Font, bounds.Location,
                            visual.ForeColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        // Links are underlined at paint time rather than by an underlined font, so
                        // the style cache does not need a variant per link state.
                        if (visual.LinkTarget != null)
                            using (var pen = new Pen(visual.ForeColor))
                                e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
                        break;
                }
            }
        }

        // ---- interaction ---------------------------------------------------------------------------

        private MarkdownVisual HitTest(Point client)
        {
            Point document = new Point(client.X + _scrollX, client.Y + _scrollY);
            for (int i = _visuals.Count - 1; i >= 0; i--)
            {
                MarkdownVisual visual = _visuals[i];
                if (visual.LinkTarget == null) continue;
                if (visual.Bounds.Contains(document)) return visual;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            MarkdownVisual hit = HitTest(e.Location);
            if (ReferenceEquals(hit, _hotLink)) return;
            _hotLink = hit;
            Cursor = hit != null ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;

            MarkdownVisual hit = HitTest(e.Location);
            if (hit == null) return;

            // The control NEVER opens the target itself. Following a link means launching a browser
            // or opening a file, and a preview pane is not the right place to decide either — the
            // host knows what a relative path means for this document and whether the user is in a
            // context where launching something is appropriate.
            var handler = LinkClicked;
            if (handler != null) handler(this, new MarkdownLinkClickedEventArgs(hit.LinkTarget));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_vertical.Visible) return;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            int delta = -(e.Delta / 120) * lines * _vertical.SmallChange;
            ScrollBy(0, delta);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
                case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End:
                    return true;
                default: return base.IsInputKey(keyData);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Up: ScrollBy(0, -_vertical.SmallChange); break;
                case Keys.Down: ScrollBy(0, _vertical.SmallChange); break;
                case Keys.Left: ScrollBy(-_horizontal.SmallChange, 0); break;
                case Keys.Right: ScrollBy(_horizontal.SmallChange, 0); break;
                case Keys.PageUp: ScrollBy(0, -_vertical.LargeChange); break;
                case Keys.PageDown: ScrollBy(0, _vertical.LargeChange); break;
                case Keys.Home: _scrollY = 0; UpdateScrollBars(); Invalidate(); break;
                case Keys.End: _scrollY = int.MaxValue; UpdateScrollBars(); Invalidate(); break;
                default: return;
            }
            e.Handled = true;
        }

        /// <summary>
        /// Invokes the real click handler at a client point, for tests.
        ///
        /// A seam rather than a synthesised mouse message, for the reason this suite already
        /// learned the hard way with <c>SendKeys.SendWait</c>: anything routed through the OS lands
        /// in whatever window currently has focus, and test controls are never focused. This calls
        /// the same code path the real click does, with no OS involvement.
        /// </summary>
        internal void TestClickAt(int x, int y)
        {
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        }

        internal void ScrollBy(int dx, int dy)
        {
            _scrollX += dx;
            _scrollY += dy;
            UpdateScrollBars();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _style.Dispose();
            base.Dispose(disposing);
        }
    }
}
