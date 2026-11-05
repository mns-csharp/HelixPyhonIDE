using System;
using System.Drawing;
using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.UI.Services;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// Dialog for confirming deletion operations with impact summary.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class DeletionConfirmationDialog : LunaForm
    {
        public DeletionConfirmationDialog()
        {
            InitializeComponent();
        }

        // Inputs captured at construction: the human-readable name of the item being deleted
        // (shown in the header) and the computed impact summary that drives the warning UI.
        private readonly DeletionImpactSummary _impact;
        private readonly string _itemName;






        // Real constructor: chains to the parameterless one (InitializeComponent), stores the inputs
        // with null-safe fallbacks, and configures the header/gating from the impact severity:
        //  - critical impacts tint the header red, reveal the "I understand" confirm checkbox, and
        //    start with Delete disabled (the user must tick the box to enable it);
        //  - otherwise a softer amber header and Delete is enabled immediately.
        public DeletionConfirmationDialog(string itemName, DeletionImpactSummary impact) : this()
        {
            _itemName = itemName ?? "item";
            _impact = impact ?? new DeletionImpactSummary();
            _headerLabel.Text = string.Format("Delete '{0}'?", _itemName);
            _headerPanel.BackColor = _impact.HasCriticalImpacts ? Color.FromArgb(255, 235, 235) : Color.FromArgb(255, 250, 235);
            _confirmCheckBox.Visible = _impact.HasCriticalImpacts;
            _deleteButton.Enabled = !_impact.HasCriticalImpacts;
            PopulateImpacts();
        }

        // True only if the user confirmed via Delete; caller reads this after DialogResult.OK.
        public bool Confirmed { get; private set; }


        // Loads the owner-drawn impact list and colours the header label to match the worst severity.
        private void PopulateImpacts()
        {
            // Show an explicit "nothing else affected" info row rather than an empty list.
            if (_impact.Impacts.Count == 0)
            {
                _impactList.Items.Add(new ImpactItem(ImpactSeverity.Info, "No additional impact detected."));
            }
            else
            {
                foreach (var item in _impact.Impacts)
                {
                    _impactList.Items.Add(item);
                }
            }

            // Update header based on impact
            if (_impact.HasCriticalImpacts)
            {
                _headerLabel.ForeColor = Color.FromArgb(180, 0, 0);
            }
            else if (_impact.HasWarnings)
            {
                _headerLabel.ForeColor = Color.FromArgb(180, 100, 0);
            }
        }

        // Owner-draw handler for the impact ListBox (DrawMode set in the designer): renders a
        // severity glyph plus the description, colour-coded per severity. Runs once per visible row.
        private void ImpactList_DrawItem(object sender, DrawItemEventArgs e)
        {
            // -1 index is raised for an empty list; nothing to paint.
            if (e.Index < 0)
            {
                return;
            }

            e.DrawBackground();

            var item = _impactList.Items[e.Index] as ImpactItem;
            if (item == null)
            {
                return;
            }

            // Icon based on severity
            string icon;
            Color textColor;
            switch (item.Severity)
            {
                case ImpactSeverity.Critical:
                    icon = "\u2716"; // X mark
                    textColor = Color.FromArgb(180, 0, 0);
                    break;
                case ImpactSeverity.Warning:
                    icon = "\u26A0"; // Warning sign
                    textColor = Color.FromArgb(180, 100, 0);
                    break;
                default:
                    icon = "\u2139"; // Info
                    textColor = Color.FromArgb(0, 100, 180);
                    break;
            }

            // Fonts/brushes are per-paint disposables; icon in one column, text indented beside it.
            using (var iconFont = new Font("Segoe UI Symbol", 10f))
            using (var textBrush = new SolidBrush(textColor))
            {
                e.Graphics.DrawString(icon, iconFont, textBrush, e.Bounds.X + 4, e.Bounds.Y + 2);
                e.Graphics.DrawString(item.Description, e.Font, textBrush, e.Bounds.X + 24, e.Bounds.Y + 4);
            }

            e.DrawFocusRectangle();
        }

        // For critical deletions Delete stays disabled until the user ticks the confirm checkbox.
        private void ConfirmCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            _deleteButton.Enabled = _confirmCheckBox.Checked;
        }

        // Delete: record confirmation and close with OK so the caller proceeds with the deletion.
        private void DeleteButton_Click(object sender, EventArgs e)
        {
            Confirmed = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        // Cancel: leave Confirmed false and close with Cancel so the caller aborts.
        private void CancelButton_Click(object sender, EventArgs e)
        {
            Confirmed = false;
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
