namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    /// <summary>
    /// A tab strip this product draws, lays out and scrolls itself.
    ///
    /// It derives from <see cref="TabControl"/> for one reason only: <see cref="TabPage"/> refuses
    /// every other parent — <c>TabPage.AssignParent</c> throws "TabPages can only be added to
    /// TabControls" — so a page host that is not a TabControl cannot exist. Nothing of the native
    /// strip is reused. The native tab row is reduced to one pixel per tab (see
    /// <see cref="NeutraliseNativeStrip"/>), which is what stops the common control from ever
    /// deciding it has overflowed and creating its own <c>msctls_updown32</c> scroller: that
    /// scroller is a single control carrying BOTH arrows, always at the right-hand end, and no
    /// amount of owner-drawing moves it. The band the strip is drawn in is reserved through
    /// <c>TCM_ADJUSTRECT</c> instead, so the pages sit below (or above) whatever we claim.
    ///
    /// Everything visible — tab widths, the scroll position, where the arrows sit, hit-testing —
    /// is this class's own. Inheriting is a parenting requirement, not a layout one.
    /// </summary>
    public class HelixTabControl : TabControl
    {
        private const int TCM_ADJUSTRECT = 0x1328;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        private const int HTCLIENT = 1;

        /// <summary>Width of each scroll arrow's reserved lane at the ends of the strip.</summary>
        public const int ArrowWidth = 20;

        /// <summary>Gap between neighbouring tab heads.</summary>
        private const int TabGap = 2;

        private const int TextPadLeft = 8;
        private const int TextPadRight = 6;
        private const int CloseGap = 6;
        private const int MinTabWidth = 60;
        private const int MaxTabWidth = 240;

        private int _scrollOffset;
        private int _hotCloseIndex = -1;
        private int _hotArrow;            // -1 left, +1 right, 0 none
        private readonly List<Rectangle> _tabRects = new List<Rectangle>();
        private int _stripHeight = ToolWindowChrome.HeaderHeight;

        // Drag-reorder. A press only ARMS the drag; it becomes active once the pointer passes the
        // system drag threshold, so an ordinary click on a tab is never mistaken for a tiny drag.
        private int _dragSource = -1;
        private Point _dragStart;
        private bool _dragActive;
        private int _dragCursorX;
        private int _dragGrabOffset;

        public HelixTabControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
            UpdateStyles();
            NeutraliseNativeStrip();
        }

        /// <summary>Raised when a tab's ✕ is clicked, or a tab is middle-clicked.</summary>
        public event EventHandler<TabEventArgs> TabCloseRequested;

        /// <summary>
        /// Raised AFTER a drag has reordered the strip, so a host can persist the new order. The
        /// move itself is already done — a handler that does nothing still leaves a working reorder.
        /// </summary>
        public event EventHandler<TabReorderedEventArgs> TabReordered;

        /// <summary>Whether each head carries a ✕.</summary>
        public bool ShowCloseButtons { get; set; } = true;

        /// <summary>Whether heads can be dragged into a new order.</summary>
        public bool AllowTabReorder { get; set; } = true;

        /// <summary>Height of the band the strip is drawn in.</summary>
        public int StripHeight
        {
            get { return _stripHeight; }
            set
            {
                int h = Math.Max(8, value);
                if (h == _stripHeight) return;
                _stripHeight = h;
                UpdateTabPageBounds();
                Invalidate();
            }
        }

        /// <summary>True when the heads do not all fit and the arrows are therefore showing.</summary>
        public bool TabsOverflow
        {
            get { return MeasureTotalWidth() > ClientSize.Width; }
        }

        /// <summary>How far the strip is scrolled, in pixels. Clamped to what there is to scroll.</summary>
        public int ScrollOffset
        {
            get { return _scrollOffset; }
            set
            {
                int clamped = ClampScroll(value);
                if (clamped == _scrollOffset) return;
                _scrollOffset = clamped;
                Invalidate();
            }
        }

        /// <summary>The strip's band, at whichever end <see cref="TabControl.Alignment"/> puts it.</summary>
        public Rectangle StripBounds
        {
            get
            {
                Rectangle c = ClientRectangle;
                return StripOnBottom
                    ? new Rectangle(c.Left, c.Bottom - _stripHeight, c.Width, _stripHeight)
                    : new Rectangle(c.Left, c.Top, c.Width, _stripHeight);
            }
        }

        /// <summary>The lane heads are laid out in — inset by an arrow at each end when overflowing.</summary>
        public Rectangle TabLane
        {
            get
            {
                Rectangle s = StripBounds;
                if (!TabsOverflow) return s;
                return new Rectangle(s.Left + ArrowWidth, s.Top,
                                     Math.Max(0, s.Width - ArrowWidth * 2), s.Height);
            }
        }

        /// <summary>The left arrow's rect, or <see cref="Rectangle.Empty"/> when not overflowing.</summary>
        public Rectangle LeftArrowBounds
        {
            get
            {
                if (!TabsOverflow) return Rectangle.Empty;
                Rectangle s = StripBounds;
                return new Rectangle(s.Left, s.Top, ArrowWidth, s.Height);
            }
        }

        /// <summary>The right arrow's rect, or <see cref="Rectangle.Empty"/> when not overflowing.</summary>
        public Rectangle RightArrowBounds
        {
            get
            {
                if (!TabsOverflow) return Rectangle.Empty;
                Rectangle s = StripBounds;
                return new Rectangle(s.Right - ArrowWidth, s.Top, ArrowWidth, s.Height);
            }
        }

        private bool StripOnBottom
        {
            get { return Alignment == TabAlignment.Bottom; }
        }

        /// <summary>
        /// The head's rectangle as DRAWN, which is not what the base returns.
        ///
        /// <see cref="TabControl.GetTabRect"/> reports the native row's geometry, and that row has
        /// been shrunk to a pixel per tab so it can never overflow. Callers that ask where a tab is
        /// mean the thing on screen, so this shadows it and answers with that.
        /// </summary>
        public new Rectangle GetTabRect(int index)
        {
            EnsureLayout();
            if (index < 0 || index >= _tabRects.Count) return Rectangle.Empty;
            return _tabRects[index];
        }

        /// <summary>The tab at a point in client coordinates, or -1.</summary>
        public int TabIndexAt(Point p)
        {
            if (!TabLane.Contains(p)) return -1;
            EnsureLayout();
            for (int i = 0; i < _tabRects.Count; i++)
                if (_tabRects[i].Contains(p)) return i;
            return -1;
        }

        /// <summary>The ✕ rect inside a head, right-aligned and centred.</summary>
        public Rectangle GetCloseButtonRect(int index)
        {
            Rectangle r = GetTabRect(index);
            if (r.IsEmpty || !ShowCloseButtons) return Rectangle.Empty;
            int size = ToolWindowChrome.ButtonSize;
            return new Rectangle(r.Right - size - TextPadRight,
                                 r.Top + (r.Height - size) / 2, size, size);
        }

        /// <summary>Scroll one head towards the start.</summary>
        public void ScrollTabsLeft()
        {
            EnsureLayout();
            int lane = TabLane.Left;
            for (int i = _tabRects.Count - 1; i >= 0; i--)
            {
                int delta = lane - _tabRects[i].Left;
                if (delta > 0) { ScrollOffset = _scrollOffset - delta; return; }
            }
            ScrollOffset = 0;
        }

        /// <summary>Scroll one head towards the end.</summary>
        public void ScrollTabsRight()
        {
            EnsureLayout();
            int lane = TabLane.Left;
            for (int i = 0; i < _tabRects.Count; i++)
            {
                int delta = _tabRects[i].Left - lane;
                if (delta > 0) { ScrollOffset = _scrollOffset + delta; return; }
            }
        }

        /// <summary>Scroll just enough to bring a head fully into the lane.</summary>
        public void EnsureTabVisible(int index)
        {
            EnsureLayout();
            if (index < 0 || index >= _tabRects.Count) return;
            Rectangle lane = TabLane;
            Rectangle r = _tabRects[index];
            if (r.Left < lane.Left) ScrollOffset = _scrollOffset - (lane.Left - r.Left);
            else if (r.Right > lane.Right) ScrollOffset = _scrollOffset + (r.Right - lane.Right);
        }

        /// <summary>
        /// Shrinks the native tab row to nothing that can overflow.
        ///
        /// Fixed sizing at one pixel per tab keeps the common control's own total far below any
        /// width, so it never reaches the state in which it would create its scroller. That is the
        /// whole trick: there is no native scroller to hide, reposition or fight.
        /// </summary>
        private void NeutraliseNativeStrip()
        {
            base.SizeMode = TabSizeMode.Fixed;
            base.ItemSize = new Size(1, 1);
            base.Multiline = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                // The native control answers HTTRANSPARENT for any part of the strip its own tab
                // items do not cover — and those items have been shrunk to a pixel each, so that is
                // very nearly the whole band. Windows takes it at its word and routes the click to
                // the PARENT, which is why the heads painted here were dead to the mouse while
                // posting the same message to this handle selected them perfectly: a posted message
                // never goes through hit-testing. Claim the band back.
                base.WndProc(ref m);
                if (m.Result == (IntPtr)HTTRANSPARENT)
                {
                    Point p = PointToClient(new Point(unchecked((int)(long)m.LParam)));
                    if (ClientRectangle.Contains(p)) m.Result = (IntPtr)HTCLIENT;
                }
                return;
            }

            if (m.Msg == TCM_ADJUSTRECT && m.LParam != IntPtr.Zero)
            {
                // "Given this client rect, where do the pages go?" — answer with everything except
                // the band the strip is drawn in, so the native row reserves nothing for itself.
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                Rectangle c = ClientRectangle;
                r.Left = c.Left;
                r.Right = c.Right;
                r.Top = StripOnBottom ? c.Top : c.Top + _stripHeight;
                r.Bottom = StripOnBottom ? c.Bottom - _stripHeight : c.Bottom;
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private void UpdateTabPageBounds()
        {
            if (!IsHandleCreated) return;
            // Re-asks the control to lay its pages out against the new display rect.
            try { UpdateBounds(); Invalidate(true); } catch { }
        }

        private int MeasureTabWidth(int index)
        {
            if (index < 0 || index >= TabCount) return MinTabWidth;
            string text = TabPages[index].Text ?? string.Empty;
            int text_w = TextRenderer.MeasureText(text, Font).Width;
            int w = TextPadLeft + text_w + TextPadRight;
            if (ShowCloseButtons) w += ToolWindowChrome.ButtonSize + CloseGap;
            if (w < MinTabWidth) w = MinTabWidth;
            if (w > MaxTabWidth) w = MaxTabWidth;
            return w;
        }

        private int MeasureTotalWidth()
        {
            int total = 0;
            for (int i = 0; i < TabCount; i++) total += MeasureTabWidth(i) + TabGap;
            return total > 0 ? total - TabGap : 0;
        }

        private int ClampScroll(int value)
        {
            int max = Math.Max(0, MeasureTotalWidth() - TabLane.Width);
            if (value < 0) return 0;
            return value > max ? max : value;
        }

        // Recomputes every head's on-screen rect from the current scroll position. Cheap enough to
        // run per paint and per hit-test, which keeps it impossible for the two to disagree.
        private void EnsureLayout()
        {
            _tabRects.Clear();
            Rectangle lane = TabLane;
            _scrollOffset = ClampScroll(_scrollOffset);
            int x = lane.Left - _scrollOffset;
            for (int i = 0; i < TabCount; i++)
            {
                int w = MeasureTabWidth(i);
                _tabRects.Add(new Rectangle(x, lane.Top + 2, w, lane.Height - 2));
                x += w + TabGap;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            // AllPaintingInWmPaint routes the erase through OnPaint; a second fill here only flashes.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, ClientRectangle);

            EnsureLayout();
            Rectangle lane = TabLane;

            // Heads are clipped to the lane so a partly-scrolled tab is cut at the lane edge rather
            // than drawn over the arrow beside it.
            Region saved = g.Clip;
            g.SetClip(lane);
            int dragged = _dragActive ? _dragSource : -1;
            for (int i = 0; i < _tabRects.Count; i++)
            {
                if (i == dragged) continue;   // drawn last, on top of the rest
                Rectangle r = EffectiveTabRect(i);
                if (!r.IntersectsWith(lane)) continue;
                PaintTab(g, i, r, i == SelectedIndex, lane);
            }
            if (dragged >= 0 && dragged < _tabRects.Count)
                PaintTab(g, dragged, EffectiveTabRect(dragged), dragged == SelectedIndex, lane);
            g.Clip = saved;

            if (TabsOverflow)
            {
                PaintArrow(g, LeftArrowBounds, true, _hotArrow < 0, _scrollOffset > 0);
                PaintArrow(g, RightArrowBounds, false, _hotArrow > 0,
                           _scrollOffset < Math.Max(0, MeasureTotalWidth() - lane.Width));
            }

            base.OnPaint(e);
        }

        /// <summary>
        /// One head: rounded on the side facing away from the pages, square where it meets them.
        ///
        /// <paramref name="clip"/> is passed explicitly and applied to the text rectangle by hand
        /// because <see cref="TextRenderer"/> is GDI and does NOT honour <see cref="Graphics.Clip"/>.
        /// The shapes obey the caller's clip; the label would sail straight through it and print a
        /// half-scrolled tab's caption across the scroll arrow beside it.
        /// </summary>
        protected virtual void PaintTab(Graphics g, int index, Rectangle rect, bool selected, Rectangle clip)
        {
            Color face = selected ? LunaThemes.Current.TabActive : Color.FromArgb(219, 216, 201);
            SmoothingMode prev = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = BuildTabPath(rect, ToolWindowChrome.CornerRadius, StripOnBottom))
            using (var brush = new SolidBrush(face))
            {
                g.FillPath(brush, path);
            }
            g.SmoothingMode = prev;

            int reserve = ShowCloseButtons ? ToolWindowChrome.ButtonSize + CloseGap + TextPadRight : TextPadRight;
            var textRect = new Rectangle(rect.Left + TextPadLeft, rect.Top,
                                         Math.Max(0, rect.Width - TextPadLeft - reserve), rect.Height);
            textRect = Rectangle.Intersect(textRect, clip);
            if (textRect.Width > 0)
            {
                TextRenderer.DrawText(g, TabPages[index].Text, Font, textRect,
                                      selected ? Color.White : Color.FromArgb(64, 64, 64),
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            if (ShowCloseButtons)
            {
                ToolWindowChrome.DrawChromeButton(g, GetCloseButtonRect(index),
                                                  ToolWindowChrome.ChromeGlyph.Close, index == _hotCloseIndex);
            }
        }

        /// <summary>A scroll arrow in its reserved lane. Greyed when there is nothing that way to reach.</summary>
        protected virtual void PaintArrow(Graphics g, Rectangle r, bool pointsLeft, bool hot, bool enabled)
        {
            if (r.IsEmpty) return;
            if (hot && enabled)
            {
                using (var hover = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
                    g.FillRectangle(hover, r);
            }

            Color ink = enabled ? LunaThemes.Current.Accent : Color.FromArgb(160, 160, 160);
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            const int h = 4;   // half-height of the triangle
            Point[] tri = pointsLeft
                ? new[] { new Point(cx + h - 1, cy - h), new Point(cx + h - 1, cy + h), new Point(cx - h + 1, cy) }
                : new[] { new Point(cx - h + 1, cy - h), new Point(cx - h + 1, cy + h), new Point(cx + h - 1, cy) };

            SmoothingMode prev = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(ink)) g.FillPolygon(brush, tri);
            g.SmoothingMode = prev;
        }

        private static GraphicsPath BuildTabPath(Rectangle r, int radius, bool roundedAtBottom)
        {
            if (radius < 1) radius = 1;
            if (radius > r.Height / 2) radius = r.Height / 2;
            int d = radius * 2;
            var path = new GraphicsPath();
            if (roundedAtBottom)
            {
                path.AddLine(r.Left, r.Top, r.Right, r.Top);
                path.AddLine(r.Right, r.Top, r.Right, r.Bottom - radius);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            }
            else
            {
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddLine(r.Right, r.Top + radius, r.Right, r.Bottom);
                path.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
            }
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Where a head is drawn WHILE a drag is in flight: the dragged one follows the cursor, and
        /// the tabs it would displace slide one slot out of its way so the drop target is visible
        /// before the button is released.
        /// </summary>
        private Rectangle EffectiveTabRect(int index)
        {
            Rectangle rect = _tabRects[index];
            if (!_dragActive || _dragSource < 0) return rect;

            if (index == _dragSource)
            {
                Rectangle lane = TabLane;
                int left = _dragCursorX - _dragGrabOffset;
                if (left < lane.Left) left = lane.Left;
                int maxLeft = Math.Max(lane.Left, lane.Right - rect.Width);
                if (left > maxLeft) left = maxLeft;
                return new Rectangle(left, rect.Top, rect.Width, rect.Height);
            }

            int drop = ComputeDropInsertIndex(_dragCursorX, _dragSource);
            int width = rect.Width + TabGap;
            if (drop < _dragSource && index >= drop && index < _dragSource)
                return new Rectangle(rect.X + width, rect.Y, rect.Width, rect.Height);
            if (drop > _dragSource && index > _dragSource && index <= drop)
                return new Rectangle(rect.X - width, rect.Y, rect.Width, rect.Height);
            return rect;
        }

        /// <summary>
        /// The slot a drop at <paramref name="cursorX"/> lands in, expressed as an index in the strip
        /// as it will look AFTER the dragged tab is lifted out of it.
        /// </summary>
        public int ComputeDropInsertIndex(int cursorX, int sourceIndex)
        {
            EnsureLayout();
            int count = _tabRects.Count;
            int target = count;
            for (int i = 0; i < count; i++)
            {
                if (i == sourceIndex) continue;
                int mid = _tabRects[i].Left + _tabRects[i].Width / 2;
                if (cursorX < mid) { target = i; break; }
            }
            if (sourceIndex < target) target--;
            if (target < 0) target = 0;
            if (target >= count) target = count - 1;
            return target;
        }

        /// <summary>Moves a tab to a new slot, keeping whatever was selected selected.</summary>
        public void MoveTab(int from, int to)
        {
            if (from < 0 || from >= TabCount) return;
            if (to < 0) to = 0;
            if (to >= TabCount) to = TabCount - 1;
            if (from == to) return;

            TabPage moving = TabPages[from];
            TabPage wasSelected = SelectedTab;

            SuspendLayout();
            try
            {
                TabPages.Remove(moving);
                TabPages.Insert(to, moving);
                SelectedTab = wasSelected != null && TabPages.Contains(wasSelected) ? wasSelected : moving;
            }
            finally
            {
                ResumeLayout();
            }

            Invalidate();
            EventHandler<TabReorderedEventArgs> handler = TabReordered;
            if (handler != null) handler(this, new TabReorderedEventArgs(from, to, moving));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && TabsOverflow)
            {
                if (LeftArrowBounds.Contains(e.Location)) { ScrollTabsLeft(); return; }
                if (RightArrowBounds.Contains(e.Location)) { ScrollTabsRight(); return; }
            }

            int index = TabIndexAt(e.Location);
            if (index >= 0)
            {
                if (e.Button == MouseButtons.Left && ShowCloseButtons
                    && GetCloseButtonRect(index).Contains(e.Location))
                {
                    RaiseCloseRequested(index);
                    return;
                }
                if (e.Button == MouseButtons.Middle)
                {
                    RaiseCloseRequested(index);
                    return;
                }
                if (e.Button == MouseButtons.Left)
                {
                    SelectedIndex = index;
                    if (AllowTabReorder)
                    {
                        _dragSource = index;
                        _dragStart = e.Location;
                        _dragActive = false;
                        _dragGrabOffset = e.X - _tabRects[index].Left;
                        _dragCursorX = e.X;
                    }
                    return;
                }
            }

            // Not on the strip — let the base have it, so anything hosted below behaves normally.
            if (!StripBounds.Contains(e.Location)) base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragSource >= 0)
            {
                if ((MouseButtons & MouseButtons.Left) == 0)
                {
                    CancelDrag();
                }
                else
                {
                    if (!_dragActive
                        && Math.Abs(e.X - _dragStart.X) >= Math.Max(2, SystemInformation.DragSize.Width / 2))
                    {
                        _dragActive = true;
                        Cursor = Cursors.SizeWE;
                    }
                    if (_dragActive)
                    {
                        _dragCursorX = e.X;
                        Invalidate(StripBounds);
                    }
                }
            }

            int close = -1;
            int index = TabIndexAt(e.Location);
            if (index >= 0 && ShowCloseButtons && GetCloseButtonRect(index).Contains(e.Location)) close = index;

            int arrow = 0;
            if (TabsOverflow)
            {
                if (LeftArrowBounds.Contains(e.Location)) arrow = -1;
                else if (RightArrowBounds.Contains(e.Location)) arrow = 1;
            }

            if (close != _hotCloseIndex || arrow != _hotArrow)
            {
                _hotCloseIndex = close;
                _hotArrow = arrow;
                Invalidate(StripBounds);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int source = _dragSource;
            bool wasActive = _dragActive;
            CancelDrag();

            if (wasActive && source >= 0 && source < TabCount)
                MoveTab(source, ComputeDropInsertIndex(e.X, source));

            base.OnMouseUp(e);
        }

        private void CancelDrag()
        {
            if (_dragSource < 0 && !_dragActive) return;
            _dragSource = -1;
            _dragActive = false;
            Cursor = Cursors.Default;
            Invalidate(StripBounds);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotCloseIndex == -1 && _hotArrow == 0) return;
            _hotCloseIndex = -1;
            _hotArrow = 0;
            Invalidate(StripBounds);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (TabsOverflow && StripBounds.Contains(e.Location))
            {
                if (e.Delta > 0) ScrollTabsLeft(); else ScrollTabsRight();
                return;
            }
            base.OnMouseWheel(e);
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            EnsureTabVisible(SelectedIndex);
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _scrollOffset = ClampScroll(_scrollOffset);
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Invalidate();
        }

        private void RaiseCloseRequested(int index)
        {
            EventHandler<TabEventArgs> handler = TabCloseRequested;
            if (handler != null && index >= 0 && index < TabCount)
                handler(this, new TabEventArgs(index, TabPages[index]));
        }
    }

    /// <summary>Identifies the tab an event is about, by index and by page.</summary>
    public sealed class TabEventArgs : EventArgs
    {
        public TabEventArgs(int index, TabPage page)
        {
            Index = index;
            Page = page;
        }

        public int Index { get; private set; }
        public TabPage Page { get; private set; }
    }

    /// <summary>Reports a completed drag-reorder: where the tab came from and where it landed.</summary>
    public sealed class TabReorderedEventArgs : EventArgs
    {
        public TabReorderedEventArgs(int fromIndex, int toIndex, TabPage page)
        {
            FromIndex = fromIndex;
            ToIndex = toIndex;
            Page = page;
        }

        public int FromIndex { get; private set; }
        public int ToIndex { get; private set; }
        public TabPage Page { get; private set; }
    }
}
