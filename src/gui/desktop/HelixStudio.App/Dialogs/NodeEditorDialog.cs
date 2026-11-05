namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    [System.ComponentModel.DesignerCategory("Form")]

    public partial class NodeEditorDialog : LunaForm
    {
        public NodeEditorDialog()
        {
            InitializeComponent();
        }

        private readonly TypeNode _node;

        // Basic properties






        // Members section




        // Generic parameters section



        // Dialog buttons


        // Editing constructor: keeps a reference to the node being edited and loads its
        // current state into the controls. The node is mutated in place by WriteToNode on OK.
        public NodeEditorDialog(TypeNode node) : this()
        {
            if (node == null)
            {
                throw new ArgumentNullException("node");
            }

            _node = node;
            PopulateFromNode();
        }


        // Copies the node's fields into the UI controls. Enum-to-combo-index mappings here must
        // mirror WriteToNode's reverse mapping and the Designer's combo item order.
        private void PopulateFromNode()
        {
            _nameTextBox.Text = _node.Name ?? string.Empty;
            _namespaceTextBox.Text = _node.Namespace ?? string.Empty;

            // Set TypeKind combo
            switch (_node.TypeKind)
            {
                case TypeKind.Interface:
                    _typeKindCombo.SelectedIndex = 0;
                    break;
                case TypeKind.AbstractClass:
                    _typeKindCombo.SelectedIndex = 1;
                    break;
                case TypeKind.ConcreteClass:
                    _typeKindCombo.SelectedIndex = 2;
                    break;
                default:
                    _typeKindCombo.SelectedIndex = 2;
                    break;
            }

            // Populate members grid: one row per member, mapping kind/access enums to display text.
            _membersGrid.Rows.Clear();
            foreach (MemberDefinition member in _node.Members)
            {
                int rowIndex = _membersGrid.Rows.Add();
                DataGridViewRow row = _membersGrid.Rows[rowIndex];

                row.Cells["Kind"].Value = member.MemberKind == MemberKind.Method ? "Method" : "Property";
                row.Cells["MemberName"].Value = member.Name ?? string.Empty;
                row.Cells["ReturnType"].Value = member.ReturnType ?? string.Empty;

                string accessStr;
                switch (member.AccessModifier)
                {
                    case AccessModifier.Public:
                        accessStr = "Public";
                        break;
                    case AccessModifier.Protected:
                        accessStr = "Protected";
                        break;
                    case AccessModifier.Internal:
                        accessStr = "Internal";
                        break;
                    case AccessModifier.Private:
                        accessStr = "Private";
                        break;
                    default:
                        accessStr = "Public";
                        break;
                }
                row.Cells["Access"].Value = accessStr;
            }

            // Populate generic parameters list: name in the main column, comma-joined constraints
            // in a sub-item.
            _genericsListView.Items.Clear();
            foreach (GenericParameter gp in _node.GenericParameters)
            {
                string constraintsText = string.Join(", ", gp.Constraints.ToArray());
                ListViewItem item = new ListViewItem(gp.Name);
                item.SubItems.Add(constraintsText);
                _genericsListView.Items.Add(item);
            }
        }

        // Commits the UI state back onto the node. Called only after OnOkClick validation passes.
        // Members and generic parameters are fully rebuilt from the grid/list contents.
        private void WriteToNode()
        {
            _node.Name = _nameTextBox.Text.Trim();
            _node.Namespace = _namespaceTextBox.Text.Trim();

            // Set TypeKind
            switch (_typeKindCombo.SelectedIndex)
            {
                case 0:
                    _node.TypeKind = TypeKind.Interface;
                    break;
                case 1:
                    _node.TypeKind = TypeKind.AbstractClass;
                    break;
                case 2:
                    _node.TypeKind = TypeKind.ConcreteClass;
                    break;
                default:
                    _node.TypeKind = TypeKind.ConcreteClass;
                    break;
            }

            // Write members: rebuild the list from grid rows, skipping any row with no name
            // (including the grid's trailing empty new-row). Properties get default get/set accessors.
            _node.Members.Clear();
            for (int i = 0; i < _membersGrid.Rows.Count; i++)
            {
                DataGridViewRow row = _membersGrid.Rows[i];

                string kindStr = row.Cells["Kind"].Value as string;
                string memberName = row.Cells["MemberName"].Value as string;
                string returnType = row.Cells["ReturnType"].Value as string;
                string accessStr = row.Cells["Access"].Value as string;

                if (string.IsNullOrEmpty(memberName))
                {
                    continue;
                }

                MemberKind memberKind = MemberKind.Method;
                if (kindStr == "Property")
                {
                    memberKind = MemberKind.Property;
                }

                MemberDefinition member = new MemberDefinition(memberName.Trim(), memberKind);
                member.ReturnType = (returnType ?? string.Empty).Trim();
                member.AccessModifier = ParseAccessModifier(accessStr);

                if (memberKind == MemberKind.Property)
                {
                    member.HasGetter = true;
                    member.HasSetter = true;
                }

                _node.Members.Add(member);
            }

            // Write generic parameters: split the comma-separated constraints sub-item back into
            // a trimmed, non-empty list for each entry.
            _node.GenericParameters.Clear();
            for (int i = 0; i < _genericsListView.Items.Count; i++)
            {
                ListViewItem item = _genericsListView.Items[i];
                string name = item.Text;
                string constraintsText = string.Empty;
                if (item.SubItems.Count > 1)
                {
                    constraintsText = item.SubItems[1].Text;
                }

                List<string> constraints = new List<string>();
                if (!string.IsNullOrEmpty(constraintsText))
                {
                    string[] parts = constraintsText.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int j = 0; j < parts.Length; j++)
                    {
                        string trimmed = parts[j].Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            constraints.Add(trimmed);
                        }
                    }
                }

                GenericParameter gp = new GenericParameter(name, constraints);
                _node.GenericParameters.Add(gp);
            }
        }

        // Reverse of the access-modifier display mapping; defaults to Public for unknown/empty text.
        private static AccessModifier ParseAccessModifier(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return AccessModifier.Public;
            }

            switch (value)
            {
                case "Protected":
                    return AccessModifier.Protected;
                case "Internal":
                    return AccessModifier.Internal;
                case "Private":
                    return AccessModifier.Private;
                default:
                    return AccessModifier.Public;
            }
        }

        // ---- Event handlers ----

        // Adds a default method row and immediately puts the name cell into edit mode so the
        // user can rename it right away.
        private void OnAddMethod(object sender, EventArgs e)
        {
            int rowIndex = _membersGrid.Rows.Add();
            DataGridViewRow row = _membersGrid.Rows[rowIndex];
            row.Cells["Kind"].Value = "Method";
            row.Cells["MemberName"].Value = "NewMethod";
            row.Cells["ReturnType"].Value = "void";
            row.Cells["Access"].Value = "Public";
            _membersGrid.CurrentCell = row.Cells["MemberName"];
            _membersGrid.BeginEdit(true);
        }

        // Same as OnAddMethod but seeds a property row (string type) for immediate editing.
        private void OnAddProperty(object sender, EventArgs e)
        {
            int rowIndex = _membersGrid.Rows.Add();
            DataGridViewRow row = _membersGrid.Rows[rowIndex];
            row.Cells["Kind"].Value = "Property";
            row.Cells["MemberName"].Value = "NewProperty";
            row.Cells["ReturnType"].Value = "string";
            row.Cells["Access"].Value = "Public";
            _membersGrid.CurrentCell = row.Cells["MemberName"];
            _membersGrid.BeginEdit(true);
        }

        // Deletes the selected member row, ignoring the trailing new-row placeholder.
        private void OnRemoveMember(object sender, EventArgs e)
        {
            if (_membersGrid.CurrentRow != null && !_membersGrid.CurrentRow.IsNewRow)
            {
                _membersGrid.Rows.RemoveAt(_membersGrid.CurrentRow.Index);
            }
        }

        // Appends a generic parameter with an auto-generated name and starts inline label editing.
        private void OnAddGenericParam(object sender, EventArgs e)
        {
            // Determine a default name: T, T2, T3, etc.
            string paramName;
            if (_genericsListView.Items.Count == 0)
            {
                paramName = "T";
            }
            else
            {
                paramName = string.Format("T{0}", _genericsListView.Items.Count + 1);
            }

            ListViewItem item = new ListViewItem(paramName);
            item.SubItems.Add(string.Empty);
            _genericsListView.Items.Add(item);
            item.BeginEdit();
        }

        // Removes the selected generic parameter from the list.
        private void OnRemoveGenericParam(object sender, EventArgs e)
        {
            if (_genericsListView.SelectedItems.Count > 0)
            {
                _genericsListView.Items.Remove(_genericsListView.SelectedItems[0]);
            }
        }

        // OK handler: rejects an empty name (setting DialogResult.None vetoes the close),
        // otherwise commits all edits to the node via WriteToNode and lets the OK result stand.
        private void OnOkClick(object sender, EventArgs e)
        {
            // Validate name is not empty
            string typedName = _nameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(typedName))
            {
                MessageBox.Show("Name cannot be empty.", "Validation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _nameTextBox.Focus();
                return;
            }

            // A type name must be a legal identifier — reject "123", "my class", etc. so the
            // designer can't produce an uncompilable type.
            if (!Identifiers.IsValid(typedName))
            {
                MessageBox.Show("'" + typedName + "' is not a valid type name. Use a letter or underscore "
                    + "followed by letters, digits, or underscores.", "Validation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _nameTextBox.Focus();
                return;
            }

            WriteToNode();
        }
    }
}
