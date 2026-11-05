namespace HelixPythonNamespace.AddIns.Kit
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// The palette every design surface in this IDE puts its tools in.
    ///
    /// <para><b>One shared control.</b> Designer palettes use the same control, so their typeface,
    /// color, and category layout stay consistent.</para>
    ///
    /// <para><b>Categories collapse.</b> Users can hide tools they are not using and keep the
    /// active categories in view.</para>
    ///
    /// <para>The control owns no domain knowledge: callers hand it categories, labels, icons and an
    /// opaque tag, and it raises <see cref="ItemActivated"/> and <see cref="ItemDragStarted"/> back.
    /// This keeps the control independent of the tools each designer provides.</para>
    ///
    /// <para><b>The rows flow like a newspaper when there is room</b> (user requests,
    /// Tasks 3d and 10d). A user who drags the splitter to widen the toolbox gets as many columns
    /// as fit at <see cref="MinColumnWidth"/>. Headers and tools keep their order, top to bottom and
    /// on into the next column, and there is no gap: a group may continue in the next column
    /// (without its header again), and an open header never ends a column. A narrow toolbox is one
    /// column, as it always was.</para>
    /// </summary>
    public sealed class ToolboxPalette : UserControl, IToolboxCollapseState
    {
        /// <summary>One entry the caller wants shown.</summary>
        public sealed class Entry
        {
            public readonly string Category;
            public readonly string Label;
            public readonly Image Icon;
            public readonly object Tag;
            public readonly string Tooltip;

            /// <summary>An optional sub-group inside the category (Task 10g), drawn as its own
            /// foldable sub-header. Null means no sub-header.</summary>
            public readonly string Subgroup;

            public Entry(string category, string label, Image icon, object tag, string tooltip = null,
                         string subgroup = null)
            {
                Category = category ?? string.Empty;
                Label = label ?? string.Empty;
                Icon = icon;
                Tag = tag;
                Tooltip = tooltip;
                Subgroup = string.IsNullOrEmpty(subgroup) ? null : subgroup;
            }
        }

        // A plain scrolling panel, not a FlowLayoutPanel: LayoutGroups places the groups in
        // columns itself (user request, "groups side by side").
        private readonly Panel _flow;

        /// <summary>The narrowest column: a 24 px icon and a whole card name.</summary>
        public const int MinColumnWidth = 190;

        /// <summary>The space between two columns.</summary>
        public const int ColumnGap = 8;

        private readonly ToolTip _tips = new ToolTip();
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Control>> _rowsByCategory =
            new Dictionary<string, List<Control>>(StringComparer.Ordinal);
        private readonly List<ItemRow> _items = new List<ItemRow>();
        private ItemRow _selected;

        /// <summary>Raised on a click, and again on a double-click. The tag is the caller's.</summary>
        public event EventHandler<ToolboxItemEventArgs> ItemActivated;

        /// <summary>Raised when a row is dragged, so the caller can start a drag-and-drop of its
        /// own. The palette does not know what a drop means.</summary>
        public event EventHandler<ToolboxItemEventArgs> ItemDragStarted;

        /// <summary>Accent used for hover and selection. Set per designer so a palette still reads
        /// as belonging to its canvas.</summary>
        public Color Accent { get; set; }

        public ToolboxPalette()
        {
            Accent = LunaColors.Accent;
            BackColor = LunaColors.PanelBackground;
            _flow = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(4, 4, 4, 6),
                BackColor = LunaColors.PanelBackground,
            };
            Controls.Add(_flow);
        }

        /// <summary>The tag of the row the user last picked, or null.</summary>
        public object SelectedTag { get { return _selected == null ? null : _selected.Tag; } }

        /// <summary>
        /// Replaces everything in the palette.
        ///
        /// <para>Rebuild rather than patch: a palette's contents change when a framework combo
        /// moves or a search box is typed in, and working out the delta is more code than throwing
        /// the rows away. Collapsed categories survive the rebuild, because a user who folded the
        /// containers away did not mean "until the next keystroke".</para>
        /// </summary>
        public void SetEntries(IEnumerable<Entry> entries)
        {
            _flow.SuspendLayout();
            try
            {
                foreach (Control child in new List<Control>(_flow.Controls.Count > 0
                    ? ToList(_flow.Controls) : new List<Control>()))
                {
                    _flow.Controls.Remove(child);
                    child.Dispose();
                }
                _rowsByCategory.Clear();
                _items.Clear();
                _selected = null;

                string current = null, currentSubgroup = null;
                foreach (Entry entry in entries)
                {
                    if (!string.Equals(entry.Category, current, StringComparison.Ordinal))
                    {
                        current = entry.Category;
                        currentSubgroup = null;
                        if (!_rowsByCategory.ContainsKey(current))
                            _rowsByCategory[current] = new List<Control>();
                        _flow.Controls.Add(BuildHeader(current));
                    }
                    if (entry.Subgroup != null && !string.Equals(entry.Subgroup, currentSubgroup, StringComparison.Ordinal))
                    {
                        currentSubgroup = entry.Subgroup;
                        _flow.Controls.Add(BuildSubgroupHeader(current, currentSubgroup));
                    }
                    ItemRow row = BuildRow(entry);
                    row.SubgroupKey = entry.Subgroup == null ? null : KeyFor(current, entry.Subgroup);
                    _flow.Controls.Add(row);
                    _rowsByCategory[current].Add(row);
                    _items.Add(row);
                }

                ApplyCollapse();
            }
            finally
            {
                _flow.ResumeLayout(true);
            }
            LayoutGroups();
        }

        private static List<Control> ToList(Control.ControlCollection controls)
        {
            var list = new List<Control>(controls.Count);
            foreach (Control c in controls) list.Add(c);
            return list;
        }

        private CategoryHeader BuildHeader(string category)
        {
            var header = new CategoryHeader(category, this) { Width = PreferredRowWidth() };
            _tips.SetToolTip(header, "Click to collapse or expand "
                + ToolboxStyle.CategoryText(category));
            return header;
        }

        /// <summary>The fold key of a sub-group (Task 10g): kept in the same set as the categories,
        /// so the shell saves it through <see cref="IToolboxCollapseState"/> with no new setting.</summary>
        public static string KeyFor(string category, string subgroup)
        {
            return category + "/" + subgroup;
        }

        private SubgroupHeader BuildSubgroupHeader(string category, string subgroup)
        {
            var header = new SubgroupHeader(category, subgroup, this) { Width = PreferredRowWidth() };
            _tips.SetToolTip(header, "Click to collapse or expand " + ToolboxStyle.CategoryText(subgroup));
            return header;
        }

        private ItemRow BuildRow(Entry entry)
        {
            var row = new ItemRow(entry, this) { Width = PreferredRowWidth() };
            if (!string.IsNullOrEmpty(entry.Tooltip)) _tips.SetToolTip(row, entry.Tooltip);
            return row;
        }

        private int PreferredRowWidth()
        {
            int available = _flow.ClientSize.Width - _flow.Padding.Horizontal - 18;
            return Math.Max(150, available);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutGroups();
        }

        /// <summary>
        /// Flows the rows into columns like a newspaper: as many columns as fit at
        /// <see cref="MinColumnWidth"/>, headers and tools in toolbox order down one column and on
        /// into the next (user request, Task 10d: "the next group's icons should start
        /// where the earlier group ended"). A group may continue in the next column, without its
        /// header again; an open header never ends a column; a folded group is only its header.
        /// One column is exactly the old single list. Runs on resize (the splitter), on fold and
        /// unfold, and on every <see cref="SetEntries"/>.
        /// </summary>
        private void LayoutGroups()
        {
            // The rows that take space, in order, and the folded rows parked out of the way.
            var shown = new List<Control>();
            var keep = new List<bool>();
            var parked = new List<Control>();
            string category = null;
            foreach (Control child in _flow.Controls)
            {
                var header = child as CategoryHeader;
                if (header != null) category = header.Category;
                bool folded = category != null && IsCollapsed(category);
                var sub = child as SubgroupHeader;
                var row = child as ItemRow;
                bool subFolded = (sub != null && IsCollapsed(sub.Key))
                              || (row != null && row.SubgroupKey != null && IsCollapsed(row.SubgroupKey));
                if (header == null && (folded || (row != null && subFolded))) { parked.Add(child); continue; }
                shown.Add(child);
                // An open header or sub-header stays with its first tool (Task 10g: sub-headers
                // too). A folded one is a whole group or sub-group.
                keep.Add((header != null && !folded) || (sub != null && !subFolded));
            }

            var heights = new List<int>(shown.Count);
            foreach (Control c in shown) heights.Add(c.Margin.Top + c.Height + c.Margin.Bottom);

            // The same 18 px the rows always kept for the vertical scroll bar, so its arrival
            // never pushes a column under the edge, and a horizontal bar never appears.
            int available = _flow.ClientSize.Width - _flow.Padding.Horizontal - 18;
            int columnWidth;
            int[] column = ColumnsFor(heights, keep, available, MinColumnWidth, out columnWidth);
            int width = Math.Max(150, columnWidth);

            // Children of a scrolled panel are placed relative to the scroll origin.
            Point origin = _flow.AutoScrollPosition;
            int current = -1, y = 0, bottom = 0;
            _flow.SuspendLayout();
            for (int i = 0; i < shown.Count; i++)
            {
                Control c = shown[i];
                if (column[i] != current) { current = column[i]; y = _flow.Padding.Top; }
                int x = _flow.Padding.Left + current * (width + ColumnGap);
                c.Width = width;
                int top = y + c.Margin.Top;
                c.Location = new Point(origin.X + x + c.Margin.Left, origin.Y + top);
                y = top + c.Height + c.Margin.Bottom;
                bottom = Math.Max(bottom, y);
            }
            // A folded row is parked, hidden, at the top of the first column, so it adds nothing
            // to the scroll height. Its Visible flag is not read: that is false on any form not
            // yet shown.
            foreach (Control c in parked)
            {
                c.Width = width;
                c.Location = new Point(origin.X + _flow.Padding.Left + c.Margin.Left, origin.Y + _flow.Padding.Top);
            }
            _flow.AutoScrollMinSize = new Size(0, bottom + _flow.Padding.Bottom);
            _flow.ResumeLayout(true);
        }

        /// <summary>
        /// Which column each ROW goes in, and how wide a column is. Pure, so it is tested without
        /// a window. Rows stay in order. A row moves to the next column when its middle is past
        /// that column's even share of the total height (Task 3d's rule, per row since Task 10d).
        /// A row flagged <paramref name="keepWithNext"/> (an open header, a sub-header) never ends
        /// a column: it moves on with the row after it, unless that would empty its column. Never
        /// more columns than rows; the columns used share the whole width.
        /// </summary>
        public static int[] ColumnsFor(IList<int> rowHeights, IList<bool> keepWithNext, int availableWidth,
                                         int minColumnWidth, out int columnWidth)
        {
            int count = rowHeights.Count;
            int columns = Math.Max(1, Math.Min(count,
                (availableWidth + ColumnGap) / (minColumnWidth + ColumnGap)));
            double total = 0;
            foreach (int h in rowHeights) total += h;

            var column = new int[count];
            int current = 0, start = 0;          // start: the first row of the current column
            double top = 0;                       // where row i starts, down the whole list
            for (int i = 0; i < count; i++)
            {
                if (current < columns - 1 && i > start
                    && top + rowHeights[i] / 2.0 > total * (current + 1) / columns)
                {
                    int breakAt = i;
                    while (breakAt > start && keepWithNext[breakAt - 1]) breakAt--;
                    if (breakAt > start)
                    {
                        current++;
                        for (int j = breakAt; j < i; j++) column[j] = current;
                        start = breakAt;
                    }
                }
                column[i] = current;
                top += rowHeights[i];
            }

            int used = current + 1;
            columnWidth = (availableWidth - ColumnGap * (used - 1)) / used;
            return column;
        }

        public bool IsCollapsed(string category) { return _collapsed.Contains(category); }

        /// <summary>
        /// Which categories are folded, as names.
        ///
        /// <para>The setter applies the whole set rather than merging, so restoring a saved state
        /// also OPENS anything the user had opened. A merge would make unfolding a category
        /// un-rememberable -- it would come back folded on the next start.</para>
        /// </summary>
        public IList<string> CollapsedToolboxCategories
        {
            get { return new List<string>(_collapsed); }
            set
            {
                _collapsed.Clear();
                if (value != null)
                    foreach (string category in value)
                        if (!string.IsNullOrEmpty(category)) _collapsed.Add(category);

                // Every category, not just the named ones: one that is no longer in the set has to
                // be opened, or a restore could only ever fold things.
                ApplyCollapse();
                foreach (Control child in _flow.Controls)
                    if (child is CategoryHeader || child is SubgroupHeader) child.Invalidate();
                LayoutGroups();
            }
        }

        public event EventHandler ToolboxCollapseChanged;

        public void ToggleCategory(string category)
        {
            if (!_collapsed.Remove(category)) _collapsed.Add(category);
            ApplyCollapse();
            LayoutGroups();
            foreach (Control child in _flow.Controls)
            {
                var header = child as CategoryHeader;
                if (header != null && header.Category == category) header.Invalidate();
                var sub = child as SubgroupHeader;
                if (sub != null && sub.Key == category) sub.Invalidate();
            }

            EventHandler handler = ToolboxCollapseChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>Shows and hides every row from the folded set: a tool is hidden when its
        /// category or its sub-group (Task 10g) is folded, a sub-header when its category is.</summary>
        private void ApplyCollapse()
        {
            _flow.SuspendLayout();
            string category = null;
            foreach (Control child in _flow.Controls)
            {
                var header = child as CategoryHeader;
                if (header != null) { category = header.Category; continue; }
                bool folded = category != null && IsCollapsed(category);
                var sub = child as SubgroupHeader;
                if (sub != null) { sub.Visible = !folded; continue; }
                var row = child as ItemRow;
                if (row != null)
                    row.Visible = !folded && !(row.SubgroupKey != null && IsCollapsed(row.SubgroupKey));
            }
            _flow.ResumeLayout(true);
        }

        public void Pick(ItemRow row)
        {
            if (_selected != null && !ReferenceEquals(_selected, row))
            {
                ItemRow previous = _selected;
                _selected = null;
                previous.Invalidate();
            }
            _selected = row;
            row.Invalidate();
            EventHandler<ToolboxItemEventArgs> handler = ItemActivated;
            if (handler != null) handler(this, new ToolboxItemEventArgs(row.Tag));
        }

        /// <summary>
        /// Raised once per double-click, AFTER the ItemActivated of each of its two clicks (user
        /// ruling). Designers can handle a double-click separately from a single click.
        /// </summary>
        public event EventHandler<ToolboxItemEventArgs> ItemDoubleClicked;

        public void DoubleClicked(ItemRow row)
        {
            EventHandler<ToolboxItemEventArgs> handler = ItemDoubleClicked;
            if (handler != null) handler(this, new ToolboxItemEventArgs(row.Tag));
        }

        public void BeginDrag(ItemRow row)
        {
            EventHandler<ToolboxItemEventArgs> handler = ItemDragStarted;
            if (handler != null) handler(this, new ToolboxItemEventArgs(row.Tag));
        }

        public bool IsSelected(ItemRow row) { return ReferenceEquals(_selected, row); }

        /// <summary>Every tool row on the palette, in order. For tests and for keyboard search.</summary>
        public IList<ItemRow> Rows { get { return _items; } }

        /// <summary>Category names in the order they appear.</summary>
        public IList<string> Categories
        {
            get
            {
                var names = new List<string>();
                foreach (Control child in _flow.Controls)
                {
                    var header = child as CategoryHeader;
                    if (header != null) names.Add(header.Category);
                }
                return names;
            }
        }

        // ---- the two row types ---------------------------------------------------------------

        /// <summary>A category header: an upper-case name and the collapsible's chevron.</summary>
        public sealed class CategoryHeader : Control
        {
            private readonly ToolboxPalette _owner;
            private bool _hot;

            public CategoryHeader(string category, ToolboxPalette owner)
            {
                Category = category;
                _owner = owner;
                Height = ToolboxStyle.CategoryHeight;
                Margin = new Padding(0, 6, 0, 1);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                         | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            public string Category { get; private set; }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnClick(EventArgs e) { _owner.ToggleCategory(Category); base.OnClick(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                if (_hot)
                    using (var brush = new SolidBrush(ToolboxStyle.RowWash(_owner.Accent, false, true)))
                    using (GraphicsPath path = ToolboxStyle.RoundedRect(
                        new Rectangle(0, 0, Width - 1, Height - 1), 3))
                        g.FillPath(brush, path);

                ToolboxStyle.DrawChevron(g,
                    new Rectangle(0, 0, ToolboxStyle.ChevronWidth, Height),
                    !_owner.IsCollapsed(Category), ToolboxStyle.ChevronColor);

                var text = new Rectangle(ToolboxStyle.ChevronWidth, 0,
                                         Width - ToolboxStyle.ChevronWidth - 2, Height);
                TextRenderer.DrawText(g, ToolboxStyle.CategoryText(Category),
                    ToolboxStyle.CategoryFont, text, ToolboxStyle.CategoryForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>
        /// A sub-header inside a category (Task 10g, user request: "Create subgroups
        /// under the OBSERVERS group"). Indented under its header, upper case like it, with its own
        /// chevron; a click folds only its sub-group.
        /// </summary>
        public sealed class SubgroupHeader : Control
        {
            public const int Indent = 14;
            private readonly ToolboxPalette _owner;
            private bool _hot;

            public SubgroupHeader(string category, string subgroup, ToolboxPalette owner)
            {
                Category = category;
                Subgroup = subgroup;
                Key = KeyFor(category, subgroup);
                _owner = owner;
                Height = ToolboxStyle.CategoryHeight - 4;
                Margin = new Padding(0, 2, 0, 1);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                         | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            public string Category { get; private set; }
            public string Subgroup { get; private set; }
            public string Key { get; private set; }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnClick(EventArgs e) { _owner.ToggleCategory(Key); base.OnClick(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                if (_hot)
                    using (var brush = new SolidBrush(ToolboxStyle.RowWash(_owner.Accent, false, true)))
                    using (GraphicsPath path = ToolboxStyle.RoundedRect(
                        new Rectangle(Indent, 0, Width - Indent - 1, Height - 1), 3))
                        g.FillPath(brush, path);

                ToolboxStyle.DrawChevron(g, new Rectangle(Indent, 0, ToolboxStyle.ChevronWidth, Height),
                    !_owner.IsCollapsed(Key), ToolboxStyle.ChevronColor);

                var text = new Rectangle(Indent + ToolboxStyle.ChevronWidth, 0,
                                         Width - Indent - ToolboxStyle.ChevronWidth - 2, Height);
                TextRenderer.DrawText(g, ToolboxStyle.CategoryText(Subgroup),
                    ToolboxStyle.CategoryFont, text, ToolboxStyle.CategoryForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>One tool: its icon and its label, and no enclosure around either.</summary>
        public sealed class ItemRow : Control
        {
            private readonly ToolboxPalette _owner;
            private readonly Image _icon;
            private bool _hot;
            private bool _pressed;
            private Point _pressedAt;

            public ItemRow(Entry entry, ToolboxPalette owner)
            {
                _owner = owner;
                _icon = entry.Icon;
                Tag = entry.Tag;
                RawLabel = entry.Label;
                Text = ToolboxStyle.ItemText(entry.Label);
                Height = ToolboxStyle.ItemHeight;
                Margin = new Padding(0, 0, 0, 1);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                         | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
                BackColor = Color.Transparent;
                TabStop = true;
            }

            /// <summary>The caller's label before <see cref="ToolboxStyle.ItemText"/> touched it.
            /// Search matches against this, so typing what the model calls a thing still finds
            /// it.</summary>
            public string RawLabel { get; private set; }

            public new object Tag { get; private set; }

            /// <summary>The fold key of the row's sub-group (<see cref="KeyFor"/>), or null.</summary>
            public string SubgroupKey { get; internal set; }

            /// <summary>Raises the palette's activation exactly as a click does.</summary>
            public void PerformClick() { _owner.Pick(this); }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hot = false; _pressed = false; Invalidate(); base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                _pressed = e.Button == MouseButtons.Left;
                _pressedAt = e.Location;
                if (CanSelect) Focus();
                Invalidate();
                base.OnMouseDown(e);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                // A press that travels is a drag, not a click. SystemInformation.DragSize is the
                // platform's own threshold, so the palette feels like every other draggable list.
                if (_pressed
                    && (Math.Abs(e.X - _pressedAt.X) > SystemInformation.DragSize.Width
                        || Math.Abs(e.Y - _pressedAt.Y) > SystemInformation.DragSize.Height))
                {
                    _pressed = false;
                    Invalidate();
                    _owner.BeginDrag(this);
                }
                base.OnMouseMove(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                bool wasPressed = _pressed;
                _pressed = false;
                Invalidate();
                if (wasPressed && e.Button == MouseButtons.Left) _owner.Pick(this);
                base.OnMouseUp(e);
            }

            // WinForms raises this on the second mouse-up of a double-click, after OnMouseUp, so
            // the click's own Pick has already run. ItemActivated is left exactly as it was.
            protected override void OnDoubleClick(EventArgs e) { _owner.DoubleClicked(this); base.OnDoubleClick(e); }

            protected override bool IsInputKey(Keys keyData)
            {
                return keyData == Keys.Space || keyData == Keys.Enter || base.IsInputKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
                {
                    PerformClick();
                    e.Handled = true;
                }
                base.OnKeyDown(e);
            }

            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                bool selected = _owner.IsSelected(this);
                if (_pressed || _hot || selected || Focused)
                {
                    Color wash = ToolboxStyle.RowWash(_owner.Accent, _pressed, _hot || selected);
                    using (var brush = new SolidBrush(wash))
                    using (GraphicsPath path = ToolboxStyle.RoundedRect(
                        new Rectangle(0, 0, Width - 1, Height - 1), 4))
                        g.FillPath(brush, path);
                }

                if (_icon != null)
                {
                    int top = (Height - ToolboxStyle.IconSize) / 2;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_icon, new Rectangle(ToolboxStyle.ChevronWidth - 6, top,
                        ToolboxStyle.IconSize, ToolboxStyle.IconSize));
                }

                int textLeft = ToolboxStyle.ChevronWidth - 6 + ToolboxStyle.IconSize + 7;
                var text = new Rectangle(textLeft, 0, Width - textLeft - 4, Height);
                TextRenderer.DrawText(g, Text, ToolboxStyle.ItemFont, text, ToolboxStyle.ItemForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    public sealed class ToolboxItemEventArgs : EventArgs
    {
        public readonly object Tag;
        public ToolboxItemEventArgs(object tag) { Tag = tag; }
    }
}
