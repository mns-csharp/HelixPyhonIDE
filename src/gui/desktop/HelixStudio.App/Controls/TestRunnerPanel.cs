namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.Testing;

    using HelixPythonNamespace.UI.Controls;
    /// <summary>
    /// Live test-run results. Nodes arrive incrementally from <see cref="TestRunner"/>'s
    /// TestsBatch event and are upserted by <see cref="TestNode.Id"/>. A summary line shows
    /// pass/fail/skip counts.
    /// </summary>
    public sealed class TestRunnerPanel : UserControl
    {
        private readonly Label _summary;
        private readonly ListView _listView;
        private readonly Dictionary<string, ListViewItem> _byId =
            new Dictionary<string, ListViewItem>(StringComparer.Ordinal);
        // Running status per test id, so the summary can be recomputed incrementally
        // (O(1) per node) instead of rescanning the whole ListView on every batch
        // (which was O(n) per batch → O(n^2) across a large run).
        private readonly Dictionary<string, TestStatus> _statusById =
            new Dictionary<string, TestStatus>(StringComparer.Ordinal);
        private int _passed, _failed, _skipped, _other;

        /// <summary>Raised when a test row is activated: file path + line.</summary>
        public event Action<string, int> ItemActivated;

        public TestRunnerPanel()
        {
            // Summary label docked at the top, results ListView filling the rest. Duration
            // is the only numeric column, so it is the one marked sortable.
            _summary = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0),
                Font = LunaFonts.UiBold,
                Text = "No tests run"
            };
            _listView = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true
            };
            _listView.Columns.Add(new ColumnHeader { Text = "Status", Width = 70 });
            _listView.Columns.Add(new ColumnHeader { Text = "Test", Width = 430 });
            _listView.Columns.Add(new ColumnHeader { Text = "Line", Width = 50, TextAlign = HorizontalAlignment.Right });
            _listView.Columns.Add(new ColumnHeader { Text = "Duration (s)", Width = 90 });
            _listView.Columns.Add(new ColumnHeader { Text = "Message", Width = 320 });
            SortableListView.Enable(_listView, 2, 3); // Line and Duration are numeric
            ListViewClipboard.AttachCopyMenu(_listView);
            _listView.DoubleClick += (s, e) => ActivateSelected();
            _listView.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ActivateSelected(); e.Handled = true; } };
            Controls.Add(_listView);
            Controls.Add(_summary);
            ResultsToolbar.Attach(this, "test results",
                () => HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_listView, false), Clear);
        }

        public void Clear()
        {
            _listView.Items.Clear();
            _byId.Clear();
            _statusById.Clear();
            _passed = _failed = _skipped = _other = 0;
            _summary.Text = "No tests run";
            // Drop any sort glyph from a previous run. We deliberately do NOT re-apply the
            // sort on each incremental batch in UpsertNodes: re-sorting a growing list every
            // batch is O(n log n) per batch (O(n^2 log n) across a large run) and churns the
            // rows the user is watching. The user can re-click a header once the run settles.
            SortableListView.ResetSort(_listView);
        }

        /// <summary>Apply a batch of node updates (insert or update existing by Id).</summary>
        public void UpsertNodes(IEnumerable<TestNode> nodes)
        {
            if (nodes == null) return;
            _listView.BeginUpdate();
            try
            {
                foreach (TestNode n in nodes)
                {
                    ListViewItem it;
                    if (!_byId.TryGetValue(n.Id, out it))
                    {
                        it = new ListViewItem(StatusText(n.Status));
                        it.SubItems.Add(ShortTestName(n.Display));
                        it.SubItems.Add("");
                        it.SubItems.Add("");
                        it.SubItems.Add("");
                        _listView.Items.Add(it);
                        _byId[n.Id] = it;
                    }
                    else
                    {
                        // Re-counting an updated node: back out its previous status bucket.
                        TestStatus prev;
                        if (_statusById.TryGetValue(n.Id, out prev)) Bump(prev, -1);
                    }
                    _statusById[n.Id] = n.Status;
                    Bump(n.Status, +1);
                    it.Text = StatusText(n.Status);
                    it.SubItems[1].Text = ShortTestName(n.Display);
                    // Line stays blank when the runner couldn't resolve one (0) — a
                    // right-aligned 0 would read as a real location.
                    it.SubItems[2].Text = n.Line > 0 ? n.Line.ToString(CultureInfo.InvariantCulture) : "";
                    it.SubItems[3].Text = n.DurationSeconds.ToString("F3", CultureInfo.InvariantCulture);
                    it.SubItems[4].Text = n.FailureMessage ?? "";
                    it.ForeColor = StatusColor(n.Status);
                    it.Tag = new Loc(n.FilePath, n.Line);
                    // Full node id in the tooltip (hover) since the row shows only the short name.
                    it.ToolTipText = string.IsNullOrEmpty(n.Display) ? n.FilePath : n.Display;
                }
            }
            finally { _listView.EndUpdate(); }
            RefreshSummary();
        }

        // Progress line shown while a run is in flight, before the final pass/fail counts land.
        public void UpdateSummary(int pct, int completed, int totalEst)
        {
            _summary.Text = string.Format(CultureInfo.InvariantCulture,
                "Running… {0}% ({1}/{2})", pct, completed, totalEst);
        }

        // Finalize the summary line once a run terminates. Replaces the transient
        // "Running… %" progress text with the resting pass/fail totals prefixed by
        // "Done —" so the panel never appears stuck mid-run after the last test lands
        // (a late Progress event could otherwise leave "Running… 100%" on screen).
        // Safe to call repeatedly.
        public void MarkRunComplete()
        {
            _summary.Text = string.Format(CultureInfo.InvariantCulture,
                "Done — {0} passed, {1} failed, {2} skipped{3}",
                _passed, _failed, _skipped, _other > 0 ? ", " + _other + " pending" : "");
        }

        // Rebuild the summary line straight from the running counters — no ListView rescan
        // (see the _statusById field note on why the counts are maintained incrementally).
        private void RefreshSummary()
        {
            _summary.Text = string.Format("{0} passed, {1} failed, {2} skipped{3}",
                _passed, _failed, _skipped, _other > 0 ? ", " + _other + " pending" : "");
        }

        // Adjust the running status counters by delta (+1 on insert, -1 to back out a prior
        // status). Failed and Error share the "failed" bucket; Running/Pending fall into "other".
        private void Bump(TestStatus status, int delta)
        {
            switch (status)
            {
                case TestStatus.Passed: _passed += delta; break;
                case TestStatus.Failed:
                case TestStatus.Error: _failed += delta; break;
                case TestStatus.Skipped: _skipped += delta; break;
                default: _other += delta; break;
            }
        }

        // Navigate to the selected test's source location via ItemActivated.
        private void ActivateSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            var loc = _listView.SelectedItems[0].Tag as Loc;
            if (loc == null || string.IsNullOrEmpty(loc.File)) return;
            Action<string, int> h = ItemActivated;
            if (h != null) h(loc.File, loc.Line);
        }

        // Trim the shared directory prefix from a test id so rows are distinguishable instead of
        // all showing the same truncated prefix. A pytest node id like
        // "pkg/tests/test_geometry.py::TestFoo::test_bar" becomes
        // "test_geometry.py::TestFoo::test_bar" (filename + class::method). The complete id stays
        // available via the row tooltip. Names without a path separator are returned unchanged.
        private static string ShortTestName(string display)
        {
            if (string.IsNullOrEmpty(display)) return display;
            int fwd = display.LastIndexOf('/');
            int back = display.LastIndexOf('\\');
            int cut = fwd > back ? fwd : back;
            return (cut >= 0 && cut < display.Length - 1) ? display.Substring(cut + 1) : display;
        }

        // Status enum -> display label.
        private static string StatusText(TestStatus s)
        {
            switch (s)
            {
                case TestStatus.Passed: return "Passed";
                case TestStatus.Failed: return "Failed";
                case TestStatus.Error: return "Error";
                case TestStatus.Skipped: return "Skipped";
                case TestStatus.Running: return "Running";
                default: return "Pending";
            }
        }

        // Status enum -> row foreground colour (green pass, red fail/error, gold skip).
        private static Color StatusColor(TestStatus s)
        {
            switch (s)
            {
                case TestStatus.Passed: return Color.Green;
                case TestStatus.Failed:
                case TestStatus.Error: return Color.DarkRed;
                case TestStatus.Skipped: return Color.DarkGoldenrod;
                default: return Color.DimGray;
            }
        }

        // Immutable file+line pair stashed on each row for navigation.
        private sealed class Loc
        {
            public readonly string File;
            public readonly int Line;
            public Loc(string file, int line) { File = file; Line = line; }
        }
    }
}
