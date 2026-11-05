namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Runtime.CompilerServices;
    using System.Windows.Forms;

    /// <summary>
    /// Adds clickable column-header sorting to a details-view <see cref="ListView"/>.
    /// Clicking a header sorts ascending; clicking the same header again toggles to
    /// descending. Columns listed in <c>numericColumns</c> are compared numerically
    /// (so "10" sorts after "2", not before it); all others compare case-insensitively
    /// as text.
    ///
    /// <para>Design note — the sorter is attached only transiently, around the single
    /// <see cref="ListView.Sort"/> call, and detached immediately after. Leaving a
    /// <see cref="ListView.ListViewItemSorter"/> assigned makes every subsequent
    /// <c>Items.Add</c> re-sort the whole list (O(n) per insert, O(n^2) for a bulk
    /// populate) — a well-known WinForms perf trap. Detaching keeps bulk population at
    /// O(n) while preserving the user's chosen order until the next repopulate.</para>
    ///
    /// <para>Each enabled list's sorter is remembered (keyed by the control) so a panel
    /// can <see cref="ReapplySort"/> after it repopulates: this keeps the user's chosen
    /// sort "sticky" across refreshes and — crucially — keeps the ▲/▼ header glyph
    /// truthful. Without it the glyph survived a repopulate while the rows reverted to
    /// insertion order, so the header lied about the actual ordering. Panels that fully
    /// replace their contents call <see cref="ResetSort"/> on Clear to drop the glyph.</para>
    /// </summary>
    internal static class SortableListView
    {
        // Per-control sorter. ConditionalWeakTable keeps no strong ref to the ListView,
        // so a disposed/GC'd panel doesn't leak through this static map.
        private static readonly ConditionalWeakTable<ListView, ListViewColumnSorter> Sorters =
            new ConditionalWeakTable<ListView, ListViewColumnSorter>();

        // Attach clickable-sort behaviour to a details ListView: remember its sorter in the
        // weak table, flip the header to Clickable, and wire the ColumnClick handler. The
        // numericColumns indices decide which columns compare as numbers rather than text.
        public static void Enable(ListView listView, params int[] numericColumns)
        {
            if (listView == null) return;
            ListViewColumnSorter sorter = new ListViewColumnSorter(numericColumns);
            Sorters.Remove(listView);
            Sorters.Add(listView, sorter);
            listView.HeaderStyle = ColumnHeaderStyle.Clickable;
            listView.ColumnClick += delegate (object s, ColumnClickEventArgs e)
            {
                SortByColumn(listView, e.Column);
            };
        }

        /// <summary>
        /// Sorts by a column exactly as clicking its header would: the already-sorted column toggles
        /// direction, a different column switches to it ascending. Lets a menu command — or a test —
        /// drive the same sort a header click does.
        /// </summary>
        public static void SortByColumn(ListView listView, int column)
        {
            if (listView == null) return;
            ListViewColumnSorter sorter;
            if (!Sorters.TryGetValue(listView, out sorter)) return;
            if (column == sorter.Column && sorter.Order != SortOrder.None)
            {
                sorter.Order = sorter.Order == SortOrder.Descending
                    ? SortOrder.Ascending
                    : SortOrder.Descending;
            }
            else
            {
                sorter.Column = column;
                sorter.Order = SortOrder.Ascending;
            }
            SortNow(listView, sorter);
        }

        /// <summary>
        /// Marks a column as sorting by the row's severity rank (its <see cref="ListViewItem.ImageIndex"/>)
        /// rather than by the cell text. A Severity column reads "Fatal / Error / Warning / Info", which
        /// sorts alphabetically to the wrong order (Error before Fatal, Warning last); the image index
        /// already encodes real severity rank (error=0, warning=1, info=2), so an Error List sorts the
        /// way a professional one does. Call after <see cref="Enable"/>.
        /// </summary>
        public static void SetIconRankColumn(ListView listView, int column)
        {
            if (listView == null) return;
            ListViewColumnSorter sorter;
            if (Sorters.TryGetValue(listView, out sorter)) sorter.IconRankColumn = column;
        }

        /// <summary>
        /// Marks a column as sorting by a caller-supplied rank of its cell TEXT rather than
        /// alphabetically — for a Severity column that carries no icon (e.g. the Lint panel, whose
        /// text "Error / Warning / Info" sorts A-Z to Error, Info, Warning, putting Info before
        /// Warning). Ranks maps cell text to an order key; unmapped text sorts to the end. Call after
        /// <see cref="Enable"/>.
        /// </summary>
        public static void SetTextRankColumn(ListView listView, int column, IReadOnlyDictionary<string, int> ranks)
        {
            if (listView == null || ranks == null) return;
            ListViewColumnSorter sorter;
            if (Sorters.TryGetValue(listView, out sorter))
            {
                sorter.TextRankColumn = column;
                sorter.TextRanks = ranks;
            }
        }

        /// <summary>
        /// Re-applies the user's active sort (if any) after the list has been repopulated,
        /// so the chosen ordering is sticky and the header glyph stays truthful. No-op when
        /// the user has never sorted this list. Call at the end of a panel's populate.
        /// </summary>
        public static void ReapplySort(ListView listView)
        {
            if (listView == null) return;
            ListViewColumnSorter sorter;
            if (!Sorters.TryGetValue(listView, out sorter)) return;
            if (sorter.Order == SortOrder.None) return;
            SortNow(listView, sorter);
        }

        /// <summary>Drops any active sort and strips the header glyph. Call from Clear().</summary>
        public static void ResetSort(ListView listView)
        {
            if (listView == null) return;
            ListViewColumnSorter sorter;
            if (Sorters.TryGetValue(listView, out sorter)) sorter.Order = SortOrder.None;
            for (int i = 0; i < listView.Columns.Count; i++)
                listView.Columns[i].Text = StripGlyph(listView.Columns[i].Text);
        }

        // Attach the sorter only for the duration of one Sort() call, then detach it (see
        // the class remarks on the WinForms re-sort-on-every-Add perf trap), and update the
        // ▲/▼ glyph to match.
        private static void SortNow(ListView listView, ListViewColumnSorter sorter)
        {
            listView.ListViewItemSorter = sorter;
            try { listView.Sort(); }
            finally { listView.ListViewItemSorter = null; }
            ApplyHeaderGlyph(listView, sorter.Column, sorter.Order);
        }

        // Append the direction arrow to the sorted column's header and strip any stale
        // arrow from the others (the native header has no built-in sort indicator, so we
        // fake it by editing the header text).
        private static void ApplyHeaderGlyph(ListView listView, int sortedColumn, SortOrder order)
        {
            string arrow = order == SortOrder.Ascending ? "  ▲" : "  ▼";
            for (int i = 0; i < listView.Columns.Count; i++)
            {
                string baseText = StripGlyph(listView.Columns[i].Text);
                listView.Columns[i].Text = i == sortedColumn ? baseText + arrow : baseText;
            }
        }

        private static string StripGlyph(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int idx = text.IndexOf('▲');
            if (idx < 0) idx = text.IndexOf('▼');
            return idx >= 0 ? text.Substring(0, idx).TrimEnd() : text;
        }

        // IComparer attached only for the duration of one Sort() call. Holds the active
        // column and direction plus the set of column indices to compare numerically.
        private sealed class ListViewColumnSorter : IComparer
        {
            private readonly HashSet<int> _numeric;

            public int Column;
            public SortOrder Order = SortOrder.None;
            // A column that sorts by the row's severity rank (ImageIndex) instead of cell text; -1 = none.
            public int IconRankColumn = -1;
            // A column that sorts by a rank of its cell TEXT (TextRanks) instead of alphabetically; -1 = none.
            public int TextRankColumn = -1;
            public IReadOnlyDictionary<string, int> TextRanks;

            public ListViewColumnSorter(int[] numericColumns)
            {
                _numeric = new HashSet<int>();
                if (numericColumns != null)
                {
                    foreach (int c in numericColumns) _numeric.Add(c);
                }
            }

            public int Compare(object x, object y)
            {
                // No active sort => report equal so Sort() leaves order untouched.
                if (Order == SortOrder.None) return 0;
                ListViewItem a = x as ListViewItem;
                ListViewItem b = y as ListViewItem;
                if (a == null || b == null) return 0;

                // Severity-rank column: order by the row's icon (error=0 < warning=1 < info=2), the
                // real severity order, not the alphabetical order of "Error"/"Fatal"/"Warning". Ties
                // (same severity) fall back to the File cell so equal-severity rows stay grouped stably.
                if (Column == IconRankColumn)
                {
                    int rc = a.ImageIndex.CompareTo(b.ImageIndex);
                    if (rc == 0)
                    {
                        string fa = a.SubItems.Count > 1 ? a.SubItems[1].Text : string.Empty;
                        string fb = b.SubItems.Count > 1 ? b.SubItems[1].Text : string.Empty;
                        rc = string.Compare(fa, fb, StringComparison.OrdinalIgnoreCase);
                    }
                    return Order == SortOrder.Descending ? -rc : rc;
                }

                // Text-rank column: order by the caller's rank of the cell text (Error<Warning<Info),
                // not alphabetically. Unmapped text sorts to the end; File cell breaks ties stably.
                if (Column == TextRankColumn && TextRanks != null)
                {
                    string ta = Column < a.SubItems.Count ? a.SubItems[Column].Text : string.Empty;
                    string tb = Column < b.SubItems.Count ? b.SubItems[Column].Text : string.Empty;
                    int ra, rb;
                    if (!TextRanks.TryGetValue(ta ?? string.Empty, out ra)) ra = int.MaxValue;
                    if (!TextRanks.TryGetValue(tb ?? string.Empty, out rb)) rb = int.MaxValue;
                    int rc2 = ra.CompareTo(rb);
                    if (rc2 == 0)
                    {
                        string fa = a.SubItems.Count > 1 ? a.SubItems[1].Text : string.Empty;
                        string fb = b.SubItems.Count > 1 ? b.SubItems[1].Text : string.Empty;
                        rc2 = string.Compare(fa, fb, StringComparison.OrdinalIgnoreCase);
                    }
                    return Order == SortOrder.Descending ? -rc2 : rc2;
                }

                // Guard against rows with fewer sub-items than the sort column (ragged rows).
                string sa = Column < a.SubItems.Count ? a.SubItems[Column].Text : string.Empty;
                string sb = Column < b.SubItems.Count ? b.SubItems[Column].Text : string.Empty;

                int cmp;
                // Numeric columns compare as doubles so "10" > "2". A cell that fails to
                // parse (blank/"-"/"N/A") is treated as smaller, sinking it to one end.
                if (_numeric.Contains(Column))
                {
                    double da, db;
                    bool pa = double.TryParse(sa, NumberStyles.Any, CultureInfo.InvariantCulture, out da);
                    bool pb = double.TryParse(sb, NumberStyles.Any, CultureInfo.InvariantCulture, out db);
                    if (pa && pb) cmp = da.CompareTo(db);
                    else if (pa) cmp = 1;       // numeric value sorts after non-numeric/blank
                    else if (pb) cmp = -1;
                    else cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                }

                return Order == SortOrder.Descending ? -cmp : cmp;
            }
        }
    }
}
