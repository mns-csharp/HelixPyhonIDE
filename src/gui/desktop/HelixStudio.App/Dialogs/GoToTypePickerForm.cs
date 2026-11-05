namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.UI.Controls;

    // Disambiguation picker for "Go To Type" when one identifier matches several types.
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class GoToTypePickerForm : LunaForm
    {
        private readonly List<TypeModel> _candidates = new List<TypeModel>();
        public GoToTypePickerForm() { InitializeComponent(); }
        public GoToTypePickerForm(string identifier, IList<TypeModel> candidates) : this()
        {
            if (candidates != null) _candidates.AddRange(candidates);
            headerLabel.Text = string.Format("'{0}' matches {1} types. Pick one:", identifier ?? string.Empty, _candidates.Count);
            // One row per candidate; stash the model on Tag so selection maps straight back to it.
            foreach (TypeModel candidate in _candidates)
            {
                var item = new ListViewItem(candidate.FullyQualifiedName); item.SubItems.Add(candidate.TypeKind.ToString()); item.Tag = candidate; candidateListView.Items.Add(item);
            }
            // Pre-select the first match so Enter accepts immediately.
            if (candidateListView.Items.Count > 0) candidateListView.Items[0].Selected = true;
        }
        // The chosen type; read by the caller after OK.
        public TypeModel Selected { get; private set; }
        private void OnAcceptClick(object sender, EventArgs e) { AcceptSelection(); }
        // Enter key acts as accept when a row is selected.
        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && candidateListView.SelectedItems.Count > 0) { AcceptSelection(); e.Handled = true; }
        }
        // Commit the selected row's model and close with OK.
        private void AcceptSelection()
        {
            if (candidateListView.SelectedItems.Count == 0) return;
            Selected = candidateListView.SelectedItems[0].Tag as TypeModel; DialogResult = DialogResult.OK; Close();
        }
    }
}
