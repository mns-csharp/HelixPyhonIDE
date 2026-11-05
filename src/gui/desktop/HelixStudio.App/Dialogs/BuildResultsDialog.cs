namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Text;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.App;

    // One parsed diagnostic from the build/run output (compiler or tool message).
    // Populated by the caller and displayed as a row; File/Line drive jump-to-source.
    public sealed class BuildResultItem
    {
        public string Source { get; set; }
        public string Severity { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }

    [System.ComponentModel.DesignerCategory("Form")]

    public sealed partial class BuildResultsDialog : LunaForm
    {






        public BuildResultsDialog()
        {
            InitializeComponent();
        }

        // Toolbar/menu handlers delegating to the copy/select helpers.
        private void OnCopySelectedRowsClick(object sender, EventArgs e) { CopySelectedRows(); }
        private void OnCopyAllRowsClick(object sender, EventArgs e) { CopyAllRows(); }
        private void OnSelectAllRowsClick(object sender, EventArgs e) { SelectAllRows(); }

        // Streams a line into the raw output tab as the build runs. Called from the build worker
        // thread, so it marshals back onto the UI thread via BeginInvoke; a no-op once disposed.
        public void AppendOutputLine(string line)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendOutputLine), line);
                return;
            }

            _outputTextBox.AppendText(line + Environment.NewLine);
        }

        // Called when the build finishes to load the parsed diagnostics grid. Also thread-safe
        // (marshals to UI thread) and switches focus to the Errors tab so problems are seen first.
        public void SetResults(List<BuildResultItem> results, bool buildSucceeded)
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action<List<BuildResultItem>, bool>(SetResults), results, buildSucceeded);
                return;
            }

            PopulateResults(results ?? new List<BuildResultItem>(), buildSucceeded);
            _tabControl.SelectedTab = _errorsTab;
        }


        // Builds the diagnostics grid, colour-coding and tallying error/warning rows, and updates
        // the summary label with the pass/fail status.
        private void PopulateResults(List<BuildResultItem> results, bool buildSucceeded)
        {
            // Suspend redraw during bulk load to avoid flicker.
            _listView.BeginUpdate();
            try
            {
                _listView.Items.Clear();

                int errorCount = 0;
                int warningCount = 0;

                for (int i = 0; i < results.Count; i++)
                {
                    var result = results[i];

                    // Column 0 severity, then code/message/filename/line as sub-items.
                    // Only the file's leaf name is shown; the full BuildResultItem lives in Tag
                    // so double-click can open the original path at the right line.
                    var item = new ListViewItem(result.Severity ?? string.Empty);
                    item.SubItems.Add(result.Code ?? string.Empty);
                    item.SubItems.Add(result.Message ?? string.Empty);
                    item.SubItems.Add(string.IsNullOrEmpty(result.File) ? string.Empty : Path.GetFileName(result.File));
                    item.SubItems.Add(result.Line > 0 ? result.Line.ToString() : string.Empty);
                    item.Tag = result;

                    // Severity string comparison is case-insensitive to tolerate tool variance.
                    if (string.Equals(result.Severity, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        item.ForeColor = Color.DarkRed;
                        errorCount++;
                    }
                    else if (string.Equals(result.Severity, "warning", StringComparison.OrdinalIgnoreCase))
                    {
                        item.ForeColor = Color.DarkOrange;
                        warningCount++;
                    }

                    _listView.Items.Add(item);
                }

                // Green summary on success, red on failure, with the diagnostic tallies.
                string status = buildSucceeded ? "Run succeeded" : "Run failed";
                _summaryLabel.Text = string.Format("  {0} - {1} error(s), {2} warning(s)",
                    status, errorCount, warningCount);
                _summaryLabel.ForeColor = buildSucceeded ? Color.DarkGreen : Color.DarkRed;
            }
            finally
            {
                _listView.EndUpdate();
            }
        }

        // Ctrl+C copies selected rows, Ctrl+A selects all; SuppressKeyPress stops the default beep.
        private void OnListViewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                // Enter jumps to the selected error/warning's source, same as double-click.
                e.SuppressKeyPress = true;
                OnItemDoubleClick(sender, EventArgs.Empty);
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                CopySelectedRows();
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                e.SuppressKeyPress = true;
                SelectAllRows();
            }
        }

        // Copy selected rows to the clipboard as tab-separated text with a header row.
        private void CopySelectedRows()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Severity\tCode\tMessage\tFile\tLine");
            foreach (ListViewItem item in _listView.SelectedItems)
            {
                sb.AppendLine(FormatRow(item));
            }
            if (_listView.SelectedItems.Count > 0)
            {
                Clipboard.SetText(sb.ToString());
            }
        }

        // Same as CopySelectedRows but for every row.
        private void CopyAllRows()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Severity\tCode\tMessage\tFile\tLine");
            foreach (ListViewItem item in _listView.Items)
            {
                sb.AppendLine(FormatRow(item));
            }
            if (_listView.Items.Count > 0)
            {
                Clipboard.SetText(sb.ToString());
            }
        }

        // Marks every row selected (Ctrl+A / toolbar "Select All").
        private void SelectAllRows()
        {
            for (int i = 0; i < _listView.Items.Count; i++)
            {
                _listView.Items[i].Selected = true;
            }
        }

        // Renders one ListView row as tab-joined cell text (shared by both copy paths).
        private static string FormatRow(ListViewItem item)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < item.SubItems.Count; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(item.SubItems[i].Text);
            }
            return sb.ToString();
        }

        // Double-clicking a diagnostic opens its source file (at the reported line if known)
        // in the shared editor.
        private void OnItemDoubleClick(object sender, EventArgs e)
        {
            if (_listView.SelectedItems.Count == 0)
            {
                return;
            }

            // Recover the full BuildResultItem from Tag; bail if it has no associated file.
            var selectedItem = _listView.SelectedItems[0];
            var result = selectedItem.Tag as BuildResultItem;
            if (result == null || string.IsNullOrEmpty(result.File))
            {
                return;
            }

            // Guard against a path that no longer exists on disk.
            if (!System.IO.File.Exists(result.File))
            {
                MessageBox.Show(
                    "Source file not available for this item.",
                    "Info",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var mainForm = Owner as MainForm;
            if (mainForm != null) mainForm.OpenInEditor(result.File, result.Line > 0 ? result.Line : -1);
        }
    }
}
