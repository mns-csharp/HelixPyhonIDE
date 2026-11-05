namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.Profiler;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>Displays per-function timing rows from a cProfile run (<see cref="ProfileRecord"/>).</summary>
    public sealed class ProfilePanel : UserControl
    {
        private readonly ListView _listView;
        private readonly Label _emptyLabel;

        /// <summary>Raised when a row is activated: file path + line.</summary>
        public event Action<string, int> ItemActivated;

        public ProfilePanel()
        {
            // Details ListView filling the panel; sorting is enabled on the numeric
            // columns below so the user can rank functions by cost.
            _listView = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true
            };
            _listView.Columns.Add(new ColumnHeader { Text = "Function", Width = 220 });
            _listView.Columns.Add(new ColumnHeader { Text = "File", Width = 150 });
            _listView.Columns.Add(new ColumnHeader { Text = "Line", Width = 55 });
            _listView.Columns.Add(new ColumnHeader { Text = "Calls", Width = 70 });
            _listView.Columns.Add(new ColumnHeader { Text = "Total (s)", Width = 90 });
            _listView.Columns.Add(new ColumnHeader { Text = "Cumulative (s)", Width = 110 });
            // Line, Calls, Total (s), Cumulative (s) are numeric — the whole point of a
            // profiler is sorting by the hottest function.
            SortableListView.Enable(_listView, 2, 3, 4, 5);
            ListViewClipboard.AttachCopyMenu(_listView);
            _listView.DoubleClick += (s, e) => ActivateSelected();
            _listView.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ActivateSelected(); e.Handled = true; } };
            Controls.Add(_listView);
            ResultsToolbar.Attach(this, "profile results",
                () => HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_listView, false), Clear);
            // Empty results read as a broken grid without this — see ListEmptyState.
            _emptyLabel = ListEmptyState.Attach(_listView,
                "No profile data." + Environment.NewLine + Environment.NewLine +
                "Run Profiler → Profile Active File (Ctrl+Shift+P) to collect a profile.");
        }

        // Replace the list with the records from a profiler run.
        public void Show(IReadOnlyList<ProfileRecord> records)
        {
            _listView.BeginUpdate();
            try
            {
                _listView.Items.Clear();
                if (records == null) return;
                foreach (ProfileRecord r in records)
                {
                    var it = new ListViewItem(r.Name);
                    // SafeFileName tolerates cProfile's pseudo-paths (see below).
                    it.SubItems.Add(SafeFileName(r.FilePath));
                    it.SubItems.Add(r.Line.ToString());
                    it.SubItems.Add(r.CallCount.ToString());
                    // Format timings with fixed 4 decimals under invariant culture so the
                    // numeric sort parses them back consistently regardless of locale.
                    it.SubItems.Add(r.TotalSeconds.ToString("F4", CultureInfo.InvariantCulture));
                    it.SubItems.Add(r.CumulativeSeconds.ToString("F4", CultureInfo.InvariantCulture));
                    it.Tag = r;
                    it.ToolTipText = r.FilePath;
                    _listView.Items.Add(it);
                }
            }
            finally { _listView.EndUpdate(); }
            SortableListView.ReapplySort(_listView);
            ListEmptyState.Refresh(_listView, _emptyLabel);
        }

        // cProfile reports frozen/builtin frames with pseudo-filenames like
        // "<frozen importlib._bootstrap>", "<string>", "~" — which contain characters illegal in a
        // Windows path, so Path.GetFileName throws. Strip the directory part safely and never throw.
        private static string SafeFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileName(path); }
            catch
            {
                int sep = path.LastIndexOfAny(new[] { '/', '\\' });
                return sep >= 0 && sep < path.Length - 1 ? path.Substring(sep + 1) : path;
            }
        }

        /// <summary>One kernel's static resources + theoretical occupancy (FR-PROF-02). App-local DTO so the
        /// panel needn't reference the CUDA workspace engine — the harness/coordinator maps into it.</summary>
        public struct CudaKernelRow
        {
            public string Kernel;
            public string Arch;
            public int Registers;
            public int SharedMemBytes;
            public int Spills;
            public int OccupancyPercent;
            public int ActiveWarps;
            public int MaxWarps;
            public string Limiter;
        }

        // Reconfigure the columns for a CUDA kernel-occupancy profile and render one row per kernel. The
        // Profiler panel is otherwise cProfile-shaped; occupancy/registers have their own honest columns
        // rather than being crammed into the timing columns.
        public void ShowCudaKernels(IReadOnlyList<CudaKernelRow> rows)
        {
            _listView.BeginUpdate();
            try
            {
                _listView.Items.Clear();
                _listView.Columns.Clear();
                _listView.Columns.Add(new ColumnHeader { Text = "Kernel", Width = 200 });
                _listView.Columns.Add(new ColumnHeader { Text = "Arch", Width = 70 });
                _listView.Columns.Add(new ColumnHeader { Text = "Registers", Width = 80 });
                _listView.Columns.Add(new ColumnHeader { Text = "Shared Mem (B)", Width = 110 });
                _listView.Columns.Add(new ColumnHeader { Text = "Spills (B)", Width = 70 });
                _listView.Columns.Add(new ColumnHeader { Text = "Occupancy %", Width = 95 });
                _listView.Columns.Add(new ColumnHeader { Text = "Warps/SM", Width = 80 });
                _listView.Columns.Add(new ColumnHeader { Text = "Limiter", Width = 110 });
                // Registers, Shared Mem, Spills, Occupancy, Warps are numeric-sortable — rank by the metric that matters.
                SortableListView.Enable(_listView, 2, 3, 4, 5, 6);
                if (rows != null)
                {
                    foreach (CudaKernelRow k in rows)
                    {
                        var it = new ListViewItem(k.Kernel);
                        it.SubItems.Add(k.Arch);
                        it.SubItems.Add(k.Registers.ToString());
                        it.SubItems.Add(k.SharedMemBytes.ToString());
                        it.SubItems.Add(k.Spills.ToString());
                        it.SubItems.Add(k.OccupancyPercent.ToString() + "%");
                        it.SubItems.Add(k.ActiveWarps + "/" + k.MaxWarps);
                        it.SubItems.Add(k.Limiter);
                        _listView.Items.Add(it);
                    }
                }
            }
            finally { _listView.EndUpdate(); }
            SortableListView.ReapplySort(_listView);
            ListEmptyState.Refresh(_listView, _emptyLabel);
        }

        public void Clear() { _listView.Items.Clear(); SortableListView.ResetSort(_listView); ListEmptyState.Refresh(_listView, _emptyLabel); }

        // Navigate to the selected record's source location via ItemActivated.
        private void ActivateSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            var r = _listView.SelectedItems[0].Tag as ProfileRecord;
            if (r == null) return;
            Action<string, int> h = ItemActivated;
            if (h != null) h(r.FilePath, r.Line);
        }
    }
}
