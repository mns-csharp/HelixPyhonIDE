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
    /// Pull-Up dialog. User picks a base class (from the source's existing base list,
    /// class/abstract only) and a set of members to move up.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class PullUpMemberDialog : LunaForm
    {
        public PullUpMemberDialog()
        {
            InitializeComponent();
        }

        private readonly TypeModel _source;
        // Base classes the members may be pulled up into (resolved, in-solution class/abstract only).
        private readonly List<TypeModel> _candidateBases;
        // Instance members of the source that are eligible to move up.
        private readonly List<MemberModel> _eligible;




        // Outputs read by the caller after OK: the target base and the members to lift up.
        public TypeModel SelectedBase { get; private set; }
        public List<MemberModel> SelectedMembers { get; private set; }

        public PullUpMemberDialog(TypeModel source, SolutionModel solution) : this()
        {
            if (source == null) throw new ArgumentNullException("source");
            if (solution == null) throw new ArgumentNullException("solution");
            _source = source;

            // Build the base-class candidate list from the source's declared base types,
            // keeping only ones resolvable within the solution.
            _candidateBases = new List<TypeModel>();
            foreach (var bt in source.BaseTypes)
            {
                if (bt.IsExternal) continue;
                TypeModel resolved = solution.FindType(bt.FullyQualifiedName);
                if (resolved == null) continue;
                // Only class / abstract class make sense for pull-up.
                if (resolved.TypeKind == Core.Model.Enums.TypeKind.ConcreteClass
                    || resolved.TypeKind == Core.Model.Enums.TypeKind.AbstractClass)
                {
                    _candidateBases.Add(resolved);
                }
            }

            // Eligible members: non-static methods, properties and fields (instance state/behaviour).
            _eligible = source.Members
                .Where(m => !m.IsStatic)
                .Where(m => m.MemberKind == MemberKind.Method
                            || m.MemberKind == MemberKind.Property
                            || m.MemberKind == MemberKind.Field)
                .ToList();
            PopulateDialogData();
        }

        // Fill the header, base-class combo and member checklist; warn and disable OK when
        // there is no eligible base or no eligible members.
        private void PopulateDialogData()
        {
            sourceLabel.Text = string.Format("Derived class: {0}", _source.FullyQualifiedName);
            _baseCombo.Items.Clear();
            foreach (TypeModel candidateBase in _candidateBases)
                _baseCombo.Items.Add(candidateBase.FullyQualifiedName);
            if (_baseCombo.Items.Count > 0) _baseCombo.SelectedIndex = 0;

            _memberList.Items.Clear();
            _memberList.Enabled = _eligible.Count > 0;
            foreach (MemberModel member in _eligible)
                _memberList.Items.Add(FormatMember(member), false);
            if (_eligible.Count == 0)
                _memberList.Items.Add("(no eligible instance members on this class)");

            bool hasBase = _candidateBases.Count > 0;
            warn.Visible = !hasBase;
            _ok.Enabled = hasBase && _eligible.Count > 0;
        }


        private void OnOk(object sender, EventArgs e)
        {
            // Require a base-class selection; keep the dialog open otherwise.
            int idx = _baseCombo.SelectedIndex;
            if (idx < 0 || idx >= _candidateBases.Count)
            {
                MessageBox.Show(this, "Select a base class.", "No Base",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Map checked indices back to member models (guarding the placeholder row).
            var picked = new List<MemberModel>();
            for (int i = 0; i < _memberList.CheckedIndices.Count; i++)
            {
                int ci = _memberList.CheckedIndices[i];
                if (ci >= 0 && ci < _eligible.Count) picked.Add(_eligible[ci]);
            }
            // Require at least one member to move up.
            if (picked.Count == 0)
            {
                MessageBox.Show(this, "Pick at least one member to pull up.",
                    "No Members", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SelectedBase = _candidateBases[idx];
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
