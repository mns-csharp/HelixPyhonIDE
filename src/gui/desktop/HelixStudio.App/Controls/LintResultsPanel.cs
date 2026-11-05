namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Lint;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// Displays <see cref="LintFinding"/>s produced by <see cref="PythonLinter"/>.
    /// Mirrors <see cref="ProblemsPanel"/>'s column/colour convention. Because
    /// <see cref="LintFinding"/> carries only a span (no file path), rows are added
    /// together with the owning file path via <see cref="LintRow"/>.
    /// </summary>
    public sealed class LintResultsPanel : UserControl
    {
        /// <summary>A lint finding paired with its source file (the finding itself has no path).</summary>
        public sealed class LintRow
        {
            public string FilePath;
            public LintFinding Finding;
            public LintRow(string filePath, LintFinding finding) { FilePath = filePath; Finding = finding; }
        }

        private readonly ListView _listView;
        private readonly Label _emptyLabel;

        /// <summary>Raised when a row is activated (double-click / Enter): file path + line.</summary>
        public event Action<string, int> ItemActivated;

        public LintResultsPanel()
        {
            // Details ListView identical in shape to ProblemsPanel; SortableListView.Enable
            // below promotes the header to Clickable and adds sorting.
            _listView = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true
            };
            _listView.Columns.Add(new ColumnHeader { Text = "Severity", Width = 70 });
            _listView.Columns.Add(new ColumnHeader { Text = "File", Width = 160 });
            _listView.Columns.Add(new ColumnHeader { Text = "Line", Width = 55 });
            _listView.Columns.Add(new ColumnHeader { Text = "Code", Width = 70 });
            _listView.Columns.Add(new ColumnHeader { Text = "Message", Width = 520 });
            SortableListView.Enable(_listView, 2); // Line is numeric
            // The Severity column carries no icon here, so rank it by text — otherwise A-Z sorting
            // puts Info before Warning. Order matches the Problems panel: Error -> Warning -> Info.
            SortableListView.SetTextRankColumn(_listView, 0, new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "Error", 0 }, { "Warning", 1 }, { "Info", 2 },
            });
            // Right-click / Ctrl+C copies findings as TSV, like the Compile panel.
            ListViewClipboard.AttachCopyMenu(_listView);
            // Double-click or Enter navigates to the finding's source line.
            _listView.DoubleClick += (s, e) => ActivateSelected();
            _listView.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ActivateSelected(); e.Handled = true; } };
            Controls.Add(_listView);
            ResultsToolbar.Attach(this, "lint findings",
                () => HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_listView, false), Clear);
            // Empty results read as a broken grid without this — see ListEmptyState.
            _emptyLabel = ListEmptyState.Attach(_listView,
                "No lint findings." + Environment.NewLine + Environment.NewLine +
                "Run Analysis → Lint Python Sources (Ctrl+Shift+L) to check the solution.");
        }

        // Replace all rows with the supplied findings. Each row pairs a finding with its
        // file path because the finding carries only a span, not the owning file.
        public void Show(IReadOnlyList<LintRow> rows)
        {
            _listView.BeginUpdate();
            try
            {
                _listView.Items.Clear();
                if (rows == null) return;
                foreach (LintRow row in rows)
                {
                    LintFinding f = row.Finding;
                    var item = new ListViewItem(SeverityText(f.Severity));
                    // File column shows the leaf name; full path lives in the tooltip.
                    item.SubItems.Add(string.IsNullOrEmpty(row.FilePath) ? "" : Path.GetFileName(row.FilePath));
                    item.SubItems.Add(f.Span.Start.Line.ToString());
                    item.SubItems.Add(f.Code);
                    item.SubItems.Add(f.Message);
                    // Keep the whole row (finding + path) for activation.
                    item.Tag = row;
                    item.ToolTipText = row.FilePath;
                    // Colour-code by severity to match the Problems panel convention.
                    item.ForeColor = SeverityColor(f.Severity);
                    _listView.Items.Add(item);
                }
            }
            finally { _listView.EndUpdate(); }
            // Keep the user's chosen sort sticky across repopulate.
            SortableListView.ReapplySort(_listView);
            ListEmptyState.Refresh(_listView, _emptyLabel);
        }

        public void Clear() { _listView.Items.Clear(); SortableListView.ResetSort(_listView); ListEmptyState.Refresh(_listView, _emptyLabel); }

        // Surface the selected row's file/line to the host via ItemActivated.
        private void ActivateSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            var row = _listView.SelectedItems[0].Tag as LintRow;
            if (row == null) return;
            Action<string, int> h = ItemActivated;
            if (h != null) h(row.FilePath, row.Finding.Span.Start.Line);
        }

        // Severity enum -> display label.
        private static string SeverityText(AnalysisSeverity s)
        {
            switch (s)
            {
                case AnalysisSeverity.Error: return "Error";
                case AnalysisSeverity.Warning: return "Warning";
                default: return "Info";
            }
        }

        // Severity enum -> row foreground colour (red/orange/blue).
        private static Color SeverityColor(AnalysisSeverity s)
        {
            switch (s)
            {
                case AnalysisSeverity.Error: return Color.DarkRed;
                case AnalysisSeverity.Warning: return Color.DarkOrange;
                default: return Color.DarkBlue;
            }
        }
    }
}
