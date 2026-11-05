namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;

    /// <summary>
    /// Dialog showing compliance analysis results after an upstream type change.
    /// Displays auto-fixes applied, items needing attention, and ambiguous cases.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public partial class ComplianceResultsDialog : LunaForm
    {
        // Close button simply dismisses the (non-modal review) dialog.
        private void OnCloseButtonClick(object sender, EventArgs e) { Close(); }
        public ComplianceResultsDialog()
        {
            InitializeComponent();
        }




        private ComplianceReport _report;
        // Callback that performs the auto-fixes; invoked when the user clicks Apply.
        private Action _onApply;

        public ComplianceResultsDialog(ComplianceReport report, Action onApply) : this()
        {
            _report = report;
            _onApply = onApply;
            PopulateResults();
        }


        // Rebuild the summary line and the results grid from the current report state.
        private void PopulateResults()
        {
            if (_report == null) return;

            // Bucket the gaps by fix kind for the summary counts.
            int autoFix = _report.AutoFixCount;
            int needsAttention = _report.Gaps.Count(g => g.FixKind == ComplianceFixKind.NeedsAttention);
            int ambiguous = _report.Gaps.Count(g => g.FixKind == ComplianceFixKind.Ambiguous);

            _summaryLabel.Text = string.Format(
                "Upstream type '{0}' changed.\n" +
                "Auto-fixable: {1}  |  Needs attention: {2}  |  Ambiguous: {3}",
                _report.UpstreamType.Name,
                autoFix, needsAttention, ambiguous);

            // Apply is only meaningful when there is at least one auto-fixable gap.
            _applyButton.Enabled = autoFix > 0;

            // Rebuild one row per gap; status column shows "Fixed" or the pending fix kind.
            _resultsListView.Items.Clear();
            foreach (var gap in _report.Gaps)
            {
                var statusText = gap.IsResolved ? "Fixed" : gap.FixKind.ToString();
                var item = new ListViewItem(statusText);

                // Color-code by fix kind
                switch (gap.FixKind)
                {
                    case ComplianceFixKind.AutoFix:
                        item.ForeColor = gap.IsResolved ? Color.FromArgb(0, 200, 83) : Color.FromArgb(100, 200, 255);
                        break;
                    case ComplianceFixKind.NeedsAttention:
                        item.ForeColor = Color.FromArgb(255, 190, 60);
                        break;
                    case ComplianceFixKind.Ambiguous:
                        item.ForeColor = Color.FromArgb(255, 80, 80);
                        break;
                }

                item.SubItems.Add(gap.ChangeKind.ToString());
                item.SubItems.Add(gap.DownstreamType != null ? gap.DownstreamType.Name : "-");
                item.SubItems.Add(gap.UpstreamMember != null ? gap.UpstreamMember.Name : "-");
                item.SubItems.Add(gap.Description);

                _resultsListView.Items.Add(item);
            }
        }

        // Run the fixes then re-render so newly-resolved gaps flip to "Fixed" without reopening.
        private void OnApplyClicked(object sender, EventArgs e)
        {
            if (_onApply != null)
            {
                _onApply();
                PopulateResults(); // Refresh to show resolved items
            }
        }
    }
}
