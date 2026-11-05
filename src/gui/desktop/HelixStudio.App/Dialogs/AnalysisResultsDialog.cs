namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Text;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.App;

    [System.ComponentModel.DesignerCategory("Form")]

    public sealed partial class AnalysisResultsDialog : LunaForm
    {
        public AnalysisResultsDialog()
        {
            InitializeComponent();
        }






        // Read-only inputs captured at construction: the rule violations to display,
        // plus the solution and diagram projection used to resolve each violation's
        // affected node id back to a concrete type (for the "Type" column and jump-to-source).
        private readonly AnalysisReport _report;
        private readonly SolutionModel _solution;
        private readonly DiagramProjection _projection;

        // Real constructor: chains to the parameterless one (which runs InitializeComponent),
        // then stores the report/solution/projection and renders the list immediately.
        // _report is required; solution/projection are optional (type resolution is skipped when null).
        public AnalysisResultsDialog(AnalysisReport report, SolutionModel solution, DiagramProjection projection) : this()
        {
            _report = report ?? throw new ArgumentNullException("report");
            _solution = solution;
            _projection = projection;
            PopulateResults();
        }

        // Toolbar/menu handlers — thin wrappers so the designer can wire buttons to the copy/select actions.
        private void OnCopySelectedClick(object sender, EventArgs e) { CopySelected(); }
        private void OnCopyAllClick(object sender, EventArgs e) { CopyAll(); }
        private void OnSelectAllClick(object sender, EventArgs e) { SelectAll(); }


        // Fills both the ListView (one row per violation) and the plain-text output box
        // (a copy-friendly rendering of the same report), and computes the severity tallies
        // shown in the summary label.
        private void PopulateResults()
        {
            // Suspend redraw while bulk-loading rows to avoid per-item flicker.
            _listView.BeginUpdate();
            try
            {
                _listView.Items.Clear();
                var outputSb = new StringBuilder();

                int errorCount = 0;
                int warningCount = 0;
                int infoCount = 0;

                outputSb.AppendLine("=== Analysis Report ===");
                outputSb.AppendLine();

                for (int i = 0; i < _report.Violations.Count; i++)
                {
                    var result = _report.Violations[i];

                    // Column 0 is the severity text; the remaining columns are added as sub-items.
                    var item = new ListViewItem(GetSeverityText(result.Severity));
                    item.SubItems.Add(result.RuleId ?? string.Empty);
                    item.SubItems.Add(result.Message ?? string.Empty);

                    // Resolve the violation's affected node id to a real type so we can show its
                    // fully-qualified name and stash the source path (in Tag) for double-click navigation.
                    // Only possible when both the solution and its diagram projection were supplied.
                    string typeName = string.Empty;
                    string sourceFile = null;
                    if (result.AffectedNodeId.HasValue && _projection != null && _solution != null)
                    {
                        var typeModel = FindTypeByNodeId(result.AffectedNodeId.Value);
                        if (typeModel != null)
                        {
                            typeName = typeModel.FullyQualifiedName;
                            sourceFile = typeModel.SourceFilePath;
                        }
                    }
                    item.SubItems.Add(typeName);
                    item.Tag = sourceFile;

                    // Colour-code each row by severity and bump the matching counter.
                    switch (result.Severity)
                    {
                        case AnalysisSeverity.Error:
                            item.ForeColor = Color.DarkRed;
                            errorCount++;
                            break;
                        case AnalysisSeverity.Warning:
                            item.ForeColor = Color.DarkOrange;
                            warningCount++;
                            break;
                        case AnalysisSeverity.Info:
                            item.ForeColor = Color.DarkBlue;
                            infoCount++;
                            break;
                    }

                    _listView.Items.Add(item);

                    // Mirror the same row into the plain-text output for the "Copy All"/output tab.
                    outputSb.AppendLine(string.Format("[{0}] {1}: {2}{3}",
                        GetSeverityText(result.Severity),
                        result.RuleId ?? "???",
                        result.Message ?? "",
                        string.IsNullOrEmpty(typeName) ? "" : string.Format(" (Type: {0})", typeName)));
                }

                // Trailing summary line in the text output, mirroring the summary label below the grid.
                outputSb.AppendLine();
                outputSb.AppendLine(string.Format("Summary: {0} error(s), {1} warning(s), {2} info(s)",
                    errorCount, warningCount, infoCount));

                _summaryLabel.Text = string.Format(
                    "  {0} error(s), {1} warning(s), {2} info(s)",
                    errorCount, warningCount, infoCount);

                _outputTextBox.Text = outputSb.ToString();
            }
            finally
            {
                _listView.EndUpdate();
            }
        }

        // Reverse-lookup: walk every type in the solution, project it to its diagram node,
        // and return the type whose node matches the violation's affected node id. Linear scan,
        // acceptable because it only runs while populating the (bounded) violation list.
        private TypeModel FindTypeByNodeId(Guid nodeId)
        {
            foreach (var typeModel in _solution.GetAllTypes())
            {
                var node = _projection.FindNode(typeModel);
                if (node != null && node.Id == nodeId)
                {
                    return typeModel;
                }
            }
            return null;
        }

        // Double-clicking a violation opens its source in the shared editor.
        private void OnItemDoubleClick(object sender, EventArgs e)
        {
            if (_listView.SelectedItems.Count == 0)
            {
                return;
            }

            // The source path was stashed in Tag during PopulateResults (null when unresolved).
            var selectedItem = _listView.SelectedItems[0];
            var sourceFile = selectedItem.Tag as string;

            // Guard against violations with no resolvable/existing file.
            if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
            {
                MessageBox.Show(
                    "Source file not available for this item.",
                    "Info",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var mainForm = Owner as MainForm;
            if (mainForm != null) mainForm.OpenInEditor(sourceFile);
        }

        // Enter jumps to the selected violation's source (same as double-click); Ctrl+C copies the
        // selected rows, Ctrl+A selects all — SuppressKeyPress stops the ListView's default beep.
        private void OnListViewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                OnItemDoubleClick(sender, EventArgs.Empty);
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                CopySelected();
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                e.SuppressKeyPress = true;
                SelectAll();
            }
        }

        // Copy the currently selected rows to the clipboard as tab-separated text with a header
        // row, so it pastes cleanly into a spreadsheet or issue tracker.
        private void CopySelected()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Severity\tRule\tMessage\tType");
            foreach (ListViewItem item in _listView.SelectedItems)
            {
                sb.AppendLine(FormatRow(item));
            }
            if (_listView.SelectedItems.Count > 0)
            {
                Clipboard.SetText(sb.ToString());
            }
        }

        // Same as CopySelected but copies every row regardless of selection.
        private void CopyAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Severity\tRule\tMessage\tType");
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
        private void SelectAll()
        {
            for (int i = 0; i < _listView.Items.Count; i++)
            {
                _listView.Items[i].Selected = true;
            }
        }

        // Renders one ListView row as tab-joined cell text (used by both copy paths).
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

        // Maps the severity enum to its display string (also used as the text-report tag).
        private static string GetSeverityText(AnalysisSeverity severity)
        {
            switch (severity)
            {
                case AnalysisSeverity.Error:
                    return "Error";
                case AnalysisSeverity.Warning:
                    return "Warning";
                case AnalysisSeverity.Info:
                    return "Info";
                default:
                    return "Unknown";
            }
        }
    }
}
