namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    /// <summary>
    /// Extract-interface dialog. User picks the new interface name and the set of
    /// members to lift. Eligible members: public, non-static Methods and Properties.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class ExtractInterfaceDialog : LunaForm
    {
        // Check/uncheck-all convenience buttons for the member checklist.
        private void OnSelectAllClick(object sender, EventArgs e) { SetAll(true); }
        private void OnSelectNoneClick(object sender, EventArgs e) { SetAll(false); }
        public ExtractInterfaceDialog()
        {
            InitializeComponent();
        }

        private readonly TypeModel _source;
        // Members eligible to lift; the checklist is index-parallel with this list.
        private readonly List<MemberModel> _eligible;






        // Outputs read by the caller after OK: the interface name and the members to lift.
        public string InterfaceName { get; private set; }
        public List<MemberModel> SelectedMembers { get; private set; }

        public ExtractInterfaceDialog(TypeModel source) : this()
        {
            if (source == null) throw new ArgumentNullException("source");
            _source = source;
            // Eligible = public (or protected-internal), non-static methods/properties only —
            // the subset that makes sense on an extracted interface.
            _eligible = source.Members
                .Where(m => (m.MemberKind == MemberKind.Method || m.MemberKind == MemberKind.Property))
                .Where(m => !m.IsStatic)
                .Where(m => m.AccessModifier == AccessModifier.Public
                            || m.AccessModifier == AccessModifier.ProtectedInternal)
                .ToList();
            PopulateDialogData();
        }

        // Fill the header, suggest an "I"-prefixed name, and list all eligible members pre-checked.
        private void PopulateDialogData()
        {
            sourceLabel.Text = string.Format("Source class: {0}", _source.FullyQualifiedName);
            _nameBox.Text = "I" + _source.Name;
            _memberList.Items.Clear();
            _memberList.Enabled = _eligible.Count > 0;
            foreach (MemberModel member in _eligible)
                _memberList.Items.Add(FormatMember(member), true);
            // No eligible members: show an explanatory placeholder instead of an empty list.
            if (_eligible.Count == 0)
                _memberList.Items.Add("(no public, non-static methods or properties on this class)");
        }


        // Bulk (un)check; bounded to _eligible so the placeholder row can't be toggled.
        private void SetAll(bool check)
        {
            if (!_memberList.Enabled) return;
            for (int i = 0; i < _memberList.Items.Count && i < _eligible.Count; i++)
            {
                _memberList.SetItemChecked(i, check);
            }
        }

        private void OnOk(object sender, EventArgs e)
        {
            // Validate the interface name is a legal identifier; keep dialog open otherwise.
            string name = (_nameBox.Text ?? string.Empty).Trim();
            if (!Identifiers.IsValid(name))
            {
                MessageBox.Show(this, "Enter a valid C# identifier for the interface name.",
                    "Invalid Name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Map checked indices back to member models (skipping any out-of-range placeholder).
            var picked = new List<MemberModel>();
            for (int i = 0; i < _memberList.CheckedIndices.Count; i++)
            {
                int idx = _memberList.CheckedIndices[i];
                if (idx >= 0 && idx < _eligible.Count) picked.Add(_eligible[idx]);
            }
            // Require at least one member; an empty interface isn't useful.
            if (picked.Count == 0)
            {
                MessageBox.Show(this, "Pick at least one member to include in the interface.",
                    "No Members", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            InterfaceName = name;
            SelectedMembers = picked;
            DialogResult = DialogResult.OK;
            Close();
        }

        // Fixed-width label: "Kind   Name : ReturnType" (void when no return type).
        private static string FormatMember(MemberModel m)
        {
            string rt = string.IsNullOrEmpty(m.ReturnType) ? "void" : m.ReturnType;
            return string.Format("{0,-10} {1} : {2}", m.MemberKind, m.Name, rt);
        }
    }
}
