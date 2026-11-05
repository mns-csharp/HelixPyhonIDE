namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.UI.Controls;

    // Reusable checklist for picking a subset of members; title/hint are supplied by the caller.
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class MemberPickerDialog : LunaForm
    {
        // Backing list is index-parallel with the checklist items, mapping checks back to models.
        private readonly List<MemberModel> _allMembers = new List<MemberModel>();
        public MemberPickerDialog() { InitializeComponent(); }
        public MemberPickerDialog(string title, IEnumerable<MemberModel> members, string hint) : this()
        {
            Text = title ?? "Select Members"; hintLabel.Text = hint ?? string.Empty;
            if (members != null) foreach (MemberModel member in members) { _allMembers.Add(member); membersListBox.Items.Add(FormatMember(member)); }
        }
        // Chosen members; read by the caller after OK.
        public List<MemberModel> SelectedMembers { get; private set; }
        private void OnSelectAllClick(object sender, EventArgs e) { SetAllChecked(true); }
        private void OnSelectNoneClick(object sender, EventArgs e) { SetAllChecked(false); }
        private void SetAllChecked(bool value) { for (int index = 0; index < membersListBox.Items.Count; index++) membersListBox.SetItemChecked(index, value); }
        // Collect checked members; require at least one before closing with OK.
        private void OnOkClick(object sender, EventArgs e)
        {
            SelectedMembers = new List<MemberModel>();
            foreach (int index in membersListBox.CheckedIndices) if (index >= 0 && index < _allMembers.Count) SelectedMembers.Add(_allMembers[index]);
            if (SelectedMembers.Count == 0) { System.Windows.Forms.MessageBox.Show(this, "Select at least one member.", "No Selection"); return; }
            DialogResult = System.Windows.Forms.DialogResult.OK; Close();
        }
        // Fixed-width label: "Kind   Name : ReturnType" (void when no return type).
        private static string FormatMember(MemberModel member)
        {
            string resultType = string.IsNullOrEmpty(member.ReturnType) ? "void" : member.ReturnType;
            return string.Format("{0,-10} {1} : {2}", member.MemberKind, member.Name, resultType);
        }
    }
}
