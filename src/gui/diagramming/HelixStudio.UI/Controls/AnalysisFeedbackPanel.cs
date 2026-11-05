namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public class AnalysisFeedbackPanel : UserControl
    {
        private ListView _listView;
        private ColumnHeader _colSeverity;
        private ColumnHeader _colRule;
        private ColumnHeader _colMessage;
        private ColumnHeader _colAffectedType;

        public AnalysisFeedbackPanel()
        {
            InitializeControls();
        }

        public void ShowReport(AnalysisReport report)
        {
            if (report == null)
            {
                Clear();
                return;
            }

            _listView.BeginUpdate();

            try
            {
                _listView.Items.Clear();

                for (int i = 0; i < report.Violations.Count; i++)
                {
                    AnalysisResult result = report.Violations[i];

                    ListViewItem item = new ListViewItem(GetSeverityText(result.Severity));
                    item.SubItems.Add(result.RuleId ?? string.Empty);
                    item.SubItems.Add(result.Message ?? string.Empty);

                    string affectedType = string.Empty;
                    if (result.AffectedNodeId.HasValue)
                    {
                        affectedType = result.AffectedNodeId.Value.ToString();
                    }
                    item.SubItems.Add(affectedType);

                    // Color code by severity
                    switch (result.Severity)
                    {
                        case AnalysisSeverity.Error:
                            item.ForeColor = Color.DarkRed;
                            break;
                        case AnalysisSeverity.Warning:
                            item.ForeColor = Color.DarkOrange;
                            break;
                        case AnalysisSeverity.Info:
                            item.ForeColor = Color.DarkBlue;
                            break;
                    }

                    _listView.Items.Add(item);
                }
            }
            finally
            {
                _listView.EndUpdate();
            }
        }

        public void Clear()
        {
            _listView.Items.Clear();
        }

        private void InitializeControls()
        {
            SuspendLayout();

            _listView = new ListView();
            _listView.View = View.Details;
            _listView.FullRowSelect = true;
            _listView.GridLines = true;
            _listView.Dock = DockStyle.Fill;
            _listView.HeaderStyle = ColumnHeaderStyle.Nonclickable;

            _colSeverity = new ColumnHeader();
            _colSeverity.Text = "Severity";
            _colSeverity.Width = 80;

            _colRule = new ColumnHeader();
            _colRule.Text = "Rule";
            _colRule.Width = 150;

            _colMessage = new ColumnHeader();
            _colMessage.Text = "Message";
            _colMessage.Width = 350;

            _colAffectedType = new ColumnHeader();
            _colAffectedType.Text = "Affected Type";
            _colAffectedType.Width = 200;

            _listView.Columns.Add(_colSeverity);
            _listView.Columns.Add(_colRule);
            _listView.Columns.Add(_colMessage);
            _listView.Columns.Add(_colAffectedType);
            ListViewClipboard.AttachCopyMenu(_listView);

            Controls.Add(_listView);

            ResumeLayout(false);
        }

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
