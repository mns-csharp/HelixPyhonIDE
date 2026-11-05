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
    /// Push-Down dialog. User picks one or more derived classes and the members to
    /// move into them. If the base class has no derived classes, the dialog refuses
    /// to open (caller should check first).
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class PushDownMemberDialog : LunaForm
    {
        public PushDownMemberDialog()
        {
            InitializeComponent();
        }

        private readonly TypeModel _source;
        // Classes in the solution that derive from the source (the push-down targets).
        private readonly List<TypeModel> _derived;
        // Instance members of the source eligible to move down.
        private readonly List<MemberModel> _eligible;




        // Outputs read by the caller after OK: the target derived classes and the members to push down.
        public List<TypeModel> SelectedDerivedTypes { get; private set; }
        public List<MemberModel> SelectedMembers { get; private set; }

        public PushDownMemberDialog(TypeModel source, SolutionModel solution) : this()
        {
            if (source == null) throw new ArgumentNullException("source");
            if (solution == null) throw new ArgumentNullException("solution");
            _source = source;

            // Direct subclasses: any in-solution type whose base list names the source.
            _derived = solution.GetAllTypes()
                .Where(t => t != source)
                .Where(t => t.BaseTypes.Any(bt => !bt.IsExternal
                                                  && bt.FullyQualifiedName == source.FullyQualifiedName))
                .ToList();

            // Eligible members: non-static methods, properties and fields (instance state/behaviour).
            _eligible = source.Members
                .Where(m => !m.IsStatic)
                .Where(m => m.MemberKind == MemberKind.Method
                            || m.MemberKind == MemberKind.Property
                            || m.MemberKind == MemberKind.Field)
                .ToList();
            PopulateDialogData();
        }

        // Fill the derived-class and member checklists; disable OK unless both have entries.
        private void PopulateDialogData()
        {
            sourceLabel.Text = string.Format("Base class: {0}", _source.FullyQualifiedName);
            _derivedList.Items.Clear();
            _derivedList.Enabled = _derived.Count > 0;
            foreach (TypeModel derivedType in _derived)
                _derivedList.Items.Add(derivedType.FullyQualifiedName, false);
            if (_derived.Count == 0)
                _derivedList.Items.Add("(no derived classes found in this solution)");

            _memberList.Items.Clear();
            _memberList.Enabled = _eligible.Count > 0;
            foreach (MemberModel member in _eligible)
                _memberList.Items.Add(FormatMember(member), false);
            if (_eligible.Count == 0)
                _memberList.Items.Add("(no eligible instance members on this class)");

            _ok.Enabled = _derived.Count > 0 && _eligible.Count > 0;
        }


        private void OnOk(object sender, EventArgs e)
        {
            // Map checked derived-class rows back to their models (guarding the placeholder row).
            var derivedPicked = new List<TypeModel>();
            for (int i = 0; i < _derivedList.CheckedIndices.Count; i++)
            {
                int ci = _derivedList.CheckedIndices[i];
                if (ci >= 0 && ci < _derived.Count) derivedPicked.Add(_derived[ci]);
            }
            // Require at least one destination class.
            if (derivedPicked.Count == 0)
            {
                MessageBox.Show(this, "Select at least one derived class.",
                    "No Target", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Map checked member rows back to their models.
            var memberPicked = new List<MemberModel>();
            for (int i = 0; i < _memberList.CheckedIndices.Count; i++)
            {
                int ci = _memberList.CheckedIndices[i];
                if (ci >= 0 && ci < _eligible.Count) memberPicked.Add(_eligible[ci]);
            }
            // Require at least one member to push down.
            if (memberPicked.Count == 0)
            {
                MessageBox.Show(this, "Pick at least one member to push down.",
                    "No Members", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SelectedDerivedTypes = derivedPicked;
            SelectedMembers = memberPicked;
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
