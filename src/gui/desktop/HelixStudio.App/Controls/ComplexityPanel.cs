namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.Complexity;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// Displays a <see cref="ComplexityReport"/>: one row per analyzed function/method with its
    /// heuristic best/worst/average-case notation, little-o comparison, and the AST evidence that
    /// drove the classification.
    /// </summary>
    public sealed class ComplexityPanel : UserControl
    {
        private readonly ListView _list;
        private readonly TextBox _searchBox;
        private Label _empty;

        // The unfiltered report from the last analysis run — the search box filters a VIEW over
        // this, so clearing the search restores every row without re-running the engine.
        private ComplexityReport _lastReport;

        /// <summary>Raised when a row is activated: file path + line.</summary>
        public event Action<string, int> ItemActivated;

        public ComplexityPanel()
        {
            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true,
                MultiSelect = true,
            };
            _list.Columns.Add(new ColumnHeader { Text = "Function", Width = 200 });
            _list.Columns.Add(new ColumnHeader { Text = "File", Width = 140 });
            _list.Columns.Add(new ColumnHeader { Text = "Line", Width = 50 });
            _list.Columns.Add(new ColumnHeader { Text = "Best (Ω)", Width = 90 });
            _list.Columns.Add(new ColumnHeader { Text = "Worst (O)", Width = 90 });
            _list.Columns.Add(new ColumnHeader { Text = "Average (Θ)", Width = 90 });
            _list.Columns.Add(new ColumnHeader { Text = "Little-o", Width = 110 });
            _list.Columns.Add(new ColumnHeader { Text = "Evidence", Width = 420 });
            SortableListView.Enable(_list, 2);
            // Keys only: the panel has its own "Plot" right-click menu below, so this must not
            // overwrite ContextMenuStrip the way AttachCopyMenu would.
            ListViewClipboard.AttachKeyboardShortcuts(_list);

            _list.DoubleClick += (s, e) => Activate();
            _list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Activate(); e.Handled = true; } };

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem plotItem = new ToolStripMenuItem("Plot");
            plotItem.Click += (s, e) => ShowPlotForSelection();
            menu.Items.Add(plotItem);
            // Right-clicking follows the standard multi-select convention (Explorer, etc.):
            // right-clicking inside the CURRENT selection keeps it (so a multi-row pick survives
            // the right-click that opens "Plot" on all of them); right-clicking a row outside it
            // replaces the selection with just that row.
            menu.Opening += (s, e) =>
            {
                Point pt = _list.PointToClient(Cursor.Position);
                ListViewHitTestInfo hit = _list.HitTest(pt);
                if (hit.Item != null && !hit.Item.Selected)
                {
                    foreach (ListViewItem it in _list.SelectedItems) it.Selected = false;
                    hit.Item.Selected = true;
                }
                plotItem.Text = _list.SelectedItems.Count > 1 ? "Plot (" + _list.SelectedItems.Count + " selected)" : "Plot";
                e.Cancel = _list.SelectedItems.Count == 0;
            };
            _list.ContextMenuStrip = menu;

            // Incremental search: narrows the grid to functions whose name contains the typed
            // text (case-insensitive) so a large solution's results don't require scrolling
            // through everything to find one function.
            _searchBox = new TextBox { Dock = DockStyle.Fill };
            _searchBox.TextChanged += (s, e) => ApplyFilter();
            SetCue(_searchBox, "Search function names…");
            Panel header = new Panel { Dock = DockStyle.Top, Height = 24 };
            header.Controls.Add(_searchBox);

            Controls.Add(_list);
            Controls.Add(header);
            ResultsToolbar.Attach(this, "complexity analysis",
                () => HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_list, false), Clear);

            const string How = "Run Analysis ▸ Asymptotic Complexity to measure the solution.";
            _empty = ListEmptyState.Attach(_list,
                "No complexity findings yet." + Environment.NewLine + Environment.NewLine + How);
        }

        public void Show(ComplexityReport report)
        {
            _lastReport = report;
            ApplyFilter();
        }

        public void Clear()
        {
            _lastReport = null;
            _searchBox.Text = string.Empty;
            PopulateRows(Enumerable.Empty<ComplexityFinding>());
            SortableListView.ResetSort(_list);
        }

        // Re-populates the grid from _lastReport, narrowed to rows whose function name contains
        // the current search text. Called on every keystroke and whenever a fresh report arrives.
        private void ApplyFilter()
        {
            IEnumerable<ComplexityFinding> findings = _lastReport != null
                ? _lastReport.Findings
                : Enumerable.Empty<ComplexityFinding>();

            string term = _searchBox.Text.Trim();
            if (term.Length > 0)
                findings = findings.Where(f => f.MemberName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

            PopulateRows(findings);

            bool noMatchesForSearch = term.Length > 0 && _lastReport != null && !findings.Any();
            const string How = "Run Analysis ▸ Asymptotic Complexity to measure the solution.";
            _empty.Text = noMatchesForSearch
                ? "No functions match \"" + term + "\"."
                : "No complexity findings yet." + Environment.NewLine + Environment.NewLine + How;
        }

        private void PopulateRows(IEnumerable<ComplexityFinding> findings)
        {
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (ComplexityFinding f in findings)
                {
                    ListViewItem it = new ListViewItem(f.MemberName);
                    it.SubItems.Add(Path.GetFileName(f.FilePath));
                    it.SubItems.Add(f.Line.ToString());
                    it.SubItems.Add(f.BestCase);
                    it.SubItems.Add(f.WorstCase);
                    it.SubItems.Add(f.AverageCase);
                    it.SubItems.Add(f.LittleO);
                    it.SubItems.Add(f.Evidence);
                    it.Tag = new RowData(f);
                    it.ToolTipText = f.Explanation;
                    _list.Items.Add(it);
                }
            }
            finally { _list.EndUpdate(); }
            SortableListView.ReapplySort(_list);
            ListEmptyState.Refresh(_list, _empty);
        }

        private void Activate()
        {
            if (_list.SelectedItems.Count == 0) return;
            RowData row = _list.SelectedItems[0].Tag as RowData;
            if (row == null) return;
            Action<string, int> h = ItemActivated;
            if (h != null) h(row.Finding.FilePath, row.Finding.Line);
        }

        // Analysis ▸ Asymptotic Complexity's seventh doorway: a visual, not just a notation
        // string. Non-modal (Show, not ShowDialog) so the user can keep browsing other rows and
        // pop up several plots side by side to compare shapes. One selected row plots that
        // function's best+worst case; multiple selected rows overlay each function's worst-case
        // curve on one shared plot so their growth is directly comparable.
        private void ShowPlotForSelection()
        {
            List<ComplexityFinding> findings = _list.SelectedItems
                .Cast<ListViewItem>()
                .Select(it => (it.Tag as RowData)?.Finding)
                .Where(f => f != null)
                .ToList();
            if (findings.Count == 0) return;
            AsymptoticPlotForm form = new AsymptoticPlotForm(findings);
            form.Show(FindForm());
        }

        private sealed class RowData
        {
            public readonly ComplexityFinding Finding;
            public RowData(ComplexityFinding finding) { Finding = finding; }
        }

        // Show a grey placeholder in the search box while it is empty. A TextBox may not have its
        // native handle yet at construction time, so (re)apply the cue once it is created.
        private const int EM_SETCUEBANNER = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCue(TextBox box, string cue)
        {
            if (box == null) return;
            EventHandler apply = delegate
            {
                try { SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, cue); }
                catch { /* cosmetic only */ }
            };
            if (box.IsHandleCreated) apply(box, EventArgs.Empty);
            box.HandleCreated += apply;
        }
    }
}
