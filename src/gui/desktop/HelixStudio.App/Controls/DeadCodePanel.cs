namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.DeadCode;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>Displays a <see cref="DeadCodeReport"/> (unused functions/classes/imports, etc.).</summary>
    public sealed class DeadCodePanel : UserControl
    {
        private readonly ListView _listView;
        private readonly Label _emptyLabel;

        /// <summary>Raised when a row is activated (double-click / Enter): file path + line.</summary>
        public event Action<string, int> ItemActivated;

        public DeadCodePanel()
        {
            // Single details-view ListView fills the panel. HeaderStyle is set
            // Nonclickable here but SortableListView.Enable below flips it to
            // Clickable and wires up column-header sorting.
            _listView = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true
            };
            _listView.Columns.Add(new ColumnHeader { Text = "Kind", Width = 130 });
            _listView.Columns.Add(new ColumnHeader { Text = "File", Width = 160 });
            _listView.Columns.Add(new ColumnHeader { Text = "Line", Width = 55 });
            _listView.Columns.Add(new ColumnHeader { Text = "Symbol", Width = 160 });
            _listView.Columns.Add(new ColumnHeader { Text = "Message", Width = 460 });
            SortableListView.Enable(_listView, 2); // Line is numeric
            // Right-click / Ctrl+C copies findings as TSV, like the Compile panel.
            ListViewClipboard.AttachCopyMenu(_listView);
            // Double-click or Enter jumps the host to the finding's source location.
            _listView.DoubleClick += (s, e) => ActivateSelected();
            _listView.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ActivateSelected(); e.Handled = true; } };
            Controls.Add(_listView);
            ResultsToolbar.Attach(this, "dead-code findings",
                () => HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_listView, false), Clear);
            // Empty results read as a broken grid without this — see ListEmptyState.
            _emptyLabel = ListEmptyState.Attach(_listView,
                "No dead-code findings." + Environment.NewLine + Environment.NewLine +
                "Run Analysis → Find Dead Code (Ctrl+Shift+D) to scan the solution.");
        }

        // Replace the whole list with the findings from a fresh report.
        public void Show(DeadCodeReport report)
        {
            // BeginUpdate/EndUpdate suppresses per-item repaints during a bulk rebuild.
            _listView.BeginUpdate();
            try
            {
                _listView.Items.Clear();
                if (report == null || report.Findings == null) return;
                foreach (DeadCodeFinding f in report.Findings)
                {
                    // Column 0 is the item text; remaining columns are sub-items in order.
                    var item = new ListViewItem(DeadCodeKindLabels.Label(f.Kind));
                    // Show only the file name; full path is kept for the tooltip and the Tag.
                    item.SubItems.Add(string.IsNullOrEmpty(f.FilePath) ? "" : Path.GetFileName(f.FilePath));
                    item.SubItems.Add(f.Line.ToString());
                    item.SubItems.Add(f.SymbolName);
                    item.SubItems.Add(f.Message);
                    // Stash the finding on the row so activation can recover its location.
                    item.Tag = f;
                    item.ToolTipText = f.FilePath;
                    // Dead code is greyed out to signal it is unused/removable.
                    item.ForeColor = Color.DimGray;
                    _listView.Items.Add(item);
                }
            }
            finally { _listView.EndUpdate(); }
            // Re-apply any sticky user sort so the header glyph stays truthful after repopulate.
            SortableListView.ReapplySort(_listView);
            ListEmptyState.Refresh(_listView, _emptyLabel);
        }

        public void Clear() { _listView.Items.Clear(); SortableListView.ResetSort(_listView); ListEmptyState.Refresh(_listView, _emptyLabel); }

        // Fire ItemActivated with the selected finding's file/line so the host can navigate.
        private void ActivateSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            var f = _listView.SelectedItems[0].Tag as DeadCodeFinding;
            if (f == null) return;
            // Copy the event to a local before the null check to avoid a race with unsubscribe.
            Action<string, int> h = ItemActivated;
            if (h != null) h(f.FilePath, f.Line);
        }
    }
}
