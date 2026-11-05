namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    /// <summary>
    /// Dialog for editing a single member (method or property) with full detail.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class MemberEditorDialog : LunaForm
    {
        public MemberEditorDialog()
        {
            InitializeComponent();
            // A member name must be a legal identifier — keep OK disabled until it is, so a method or
            // field can't be named "123" or "my method".
            _nameTextBox.TextChanged += delegate { UpdateOkEnabled(); };
            UpdateOkEnabled();
        }

        private void UpdateOkEnabled()
        {
            _okButton.Enabled = Identifiers.IsValid(_nameTextBox.Text.Trim());
        }

        private readonly MemberModel _member;
        private readonly TypeModel _parentType;
        private readonly SolutionModel _solution;

        // Basic properties








        // Modifiers




        // Property accessors


        // Parameters (for methods)



        // Dialog buttons


        // Primary constructor: edits the given member in place. parentType/solution are optional
        // context used only to populate the return/parameter type dropdowns with visible type names.
        // The member reference is retained and mutated directly on OK (see OnOkClick).
        public MemberEditorDialog(MemberModel member, TypeModel parentType, SolutionModel solution) : this()
        {
            _member = member ?? throw new ArgumentNullException("member");
            _parentType = parentType;
            _solution = solution;
            PopulateFromMember();
        }

        // Convenience overload for callers that have no surrounding type/solution context.
        public MemberEditorDialog(MemberModel member) : this(member, null, null)
        {
        }


        // Loads the member's current state into the controls. Enum values map to fixed combo
        // indices (must stay in sync with the Designer's item order and OnOkClick's reverse map).
        private void PopulateFromMember()
        {
            _nameTextBox.Text = _member.Name ?? string.Empty;
            _returnTypeCombo.Text = _member.ReturnType ?? string.Empty;

            // Kind
            switch (_member.MemberKind)
            {
                case MemberKind.Property: _kindCombo.SelectedIndex = 1; break;
                case MemberKind.Field: _kindCombo.SelectedIndex = 2; break;
                case MemberKind.Event: _kindCombo.SelectedIndex = 3; break;
                default: _kindCombo.SelectedIndex = 0; break;
            }

            // Access
            switch (_member.AccessModifier)
            {
                case AccessModifier.Public: _accessCombo.SelectedIndex = 0; break;
                case AccessModifier.Protected: _accessCombo.SelectedIndex = 1; break;
                case AccessModifier.Internal: _accessCombo.SelectedIndex = 2; break;
                case AccessModifier.Private: _accessCombo.SelectedIndex = 3; break;
                case AccessModifier.ProtectedInternal: _accessCombo.SelectedIndex = 4; break;
                case AccessModifier.PrivateProtected: _accessCombo.SelectedIndex = 5; break;
                case AccessModifier.File: _accessCombo.SelectedIndex = 6; break;
                default: _accessCombo.SelectedIndex = 0; break;
            }

            // Modifiers
            _isStaticCheck.Checked = _member.IsStatic;
            _isAbstractCheck.Checked = _member.IsAbstract;
            _isVirtualCheck.Checked = _member.IsVirtual;
            _isOverrideCheck.Checked = _member.IsOverride;

            // Property accessors
            _hasGetterCheck.Checked = _member.HasGetter;
            _hasSetterCheck.Checked = _member.HasSetter;

            // Fill the parameters grid. A combo cell rejects any value not already in its item
            // list, so each existing param type is injected into the column's items before it is
            // assigned to the cell (otherwise the value would silently blank out).
            _parametersGrid.Rows.Clear();
            var paramTypeCol = _parametersGrid.Columns["ParamType"] as DataGridViewComboBoxColumn;
            foreach (var param in _member.Parameters)
            {
                // Ensure the param type value is in the combo items
                string paramTypeName = param.TypeName ?? string.Empty;
                if (paramTypeCol != null && !string.IsNullOrEmpty(paramTypeName) && !paramTypeCol.Items.Contains(paramTypeName))
                {
                    paramTypeCol.Items.Add(paramTypeName);
                }
                int rowIndex = _parametersGrid.Rows.Add();
                var row = _parametersGrid.Rows[rowIndex];
                row.Cells["ParamName"].Value = param.Name ?? string.Empty;
                row.Cells["ParamType"].Value = paramTypeName;
            }

            UpdateVisibility();
        }

        // Changing the member kind re-evaluates which section (accessors vs. parameters) applies.
        private void OnKindChanged(object sender, EventArgs e)
        {
            UpdateVisibility();
        }

        // Properties (kind index 1) get getter/setter accessors; everything else (methods, etc.)
        // gets the parameter grid. Only one group is enabled at a time to reflect what's relevant.
        private void UpdateVisibility()
        {
            bool isProperty = _kindCombo.SelectedIndex == 1;
            _accessorsGroup.Enabled = isProperty;
            _parametersGroup.Enabled = !isProperty;
        }

        // Adds a new parameter row with placeholder name/type for the user to edit.
        private void OnAddParameter(object sender, EventArgs e)
        {
            int rowIndex = _parametersGrid.Rows.Add();
            var row = _parametersGrid.Rows[rowIndex];
            row.Cells["ParamName"].Value = "param" + (_parametersGrid.Rows.Count);
            row.Cells["ParamType"].Value = "object";
        }

        // Removes the selected parameter row, ignoring the grid's trailing "new row" placeholder.
        private void OnRemoveParameter(object sender, EventArgs e)
        {
            if (_parametersGrid.CurrentRow != null && !_parametersGrid.CurrentRow.IsNewRow)
            {
                _parametersGrid.Rows.RemoveAt(_parametersGrid.CurrentRow.Index);
            }
        }

        // OK handler: validates, then writes every control's value back onto the shared _member.
        // The OK button carries DialogResult.OK; on validation failure we set DialogResult.None
        // to cancel the close. On success the mutated _member is visible to the caller by reference.
        private void OnOkClick(object sender, EventArgs e)
        {
            // Validate name
            string name = _nameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Name cannot be empty.", "Validation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _nameTextBox.Focus();
                return;
            }

            // Apply changes to member
            _member.Name = name;
            _member.ReturnType = _returnTypeCombo.Text.Trim();
            switch (_kindCombo.SelectedIndex)
            {
                case 1: _member.MemberKind = MemberKind.Property; break;
                case 2: _member.MemberKind = MemberKind.Field; break;
                case 3: _member.MemberKind = MemberKind.Event; break;
                default: _member.MemberKind = MemberKind.Method; break;
            }

            // Access
            switch (_accessCombo.SelectedIndex)
            {
                case 0: _member.AccessModifier = AccessModifier.Public; break;
                case 1: _member.AccessModifier = AccessModifier.Protected; break;
                case 2: _member.AccessModifier = AccessModifier.Internal; break;
                case 3: _member.AccessModifier = AccessModifier.Private; break;
                case 4: _member.AccessModifier = AccessModifier.ProtectedInternal; break;
                case 5: _member.AccessModifier = AccessModifier.PrivateProtected; break;
                case 6: _member.AccessModifier = AccessModifier.File; break;
            }

            // Modifiers
            _member.IsStatic = _isStaticCheck.Checked;
            _member.IsAbstract = _isAbstractCheck.Checked;
            _member.IsVirtual = _isVirtualCheck.Checked;
            _member.IsOverride = _isOverrideCheck.Checked;

            // Property accessors
            _member.HasGetter = _hasGetterCheck.Checked;
            _member.HasSetter = _hasSetterCheck.Checked;

            // Rebuild the parameter list from scratch from the grid rows. Rows with a blank name
            // are dropped; a missing type defaults to "object". The trailing new-row is skipped.
            _member.Parameters.Clear();
            foreach (DataGridViewRow row in _parametersGrid.Rows)
            {
                if (row.IsNewRow) continue;

                string paramName = row.Cells["ParamName"].Value?.ToString() ?? string.Empty;
                string paramType = row.Cells["ParamType"].Value?.ToString() ?? "object";

                if (!string.IsNullOrWhiteSpace(paramName))
                {
                    _member.Parameters.Add(new MethodParameterModel(paramName.Trim(), paramType.Trim()));
                }
            }
        }

        // Assembles the candidate type names offered in the return-type and parameter-type dropdowns:
        // C# built-ins (plus nullable forms), the parent type's generic parameters, and every
        // type visible in the solution. Deduplicated as it goes.
        private List<string> BuildTypeList()
        {
            var types = new List<string>();

            // Fundamental C# types
            types.AddRange(new[]
            {
                "bool", "byte", "sbyte", "char", "decimal", "double", "float",
                "int", "uint", "long", "ulong", "short", "ushort",
                "object", "string", "void", "dynamic",
                "bool?", "byte?", "sbyte?", "char?", "decimal?", "double?", "float?",
                "int?", "uint?", "long?", "ulong?", "short?", "ushort?"
            });

            // Generic parameters from parent type
            if (_parentType != null)
            {
                foreach (var gp in _parentType.GenericParameters)
                {
                    if (!string.IsNullOrEmpty(gp.Name) && !types.Contains(gp.Name))
                    {
                        types.Add(gp.Name);
                    }
                }
            }

            // Project-visible types
            if (_solution != null)
            {
                foreach (var t in _solution.GetAllTypes())
                {
                    string typeName = t.Name;
                    if (!string.IsNullOrEmpty(typeName) && !types.Contains(typeName))
                    {
                        types.Add(typeName);
                    }
                }
            }

            return types;
        }

        // Refills the return-type combo from the current type list.
        private void PopulateReturnTypeCombo()
        {
            var typeList = BuildTypeList();
            _returnTypeCombo.Items.Clear();
            foreach (var t in typeList)
            {
                _returnTypeCombo.Items.Add(t);
            }
        }

        // Refills the parameter-grid's type column with the current type list.
        private void PopulateParamTypeColumn(DataGridViewComboBoxColumn column)
        {
            var typeList = BuildTypeList();
            column.Items.Clear();
            foreach (var t in typeList)
            {
                column.Items.Add(t);
            }
        }

        // When the ParamType cell enters edit mode, switch its combo to DropDown (editable) style
        // so users can type arbitrary type names not present in the predefined item list.
        private void OnParametersGridEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            if (_parametersGrid.CurrentCell != null &&
                _parametersGrid.CurrentCell.ColumnIndex == _parametersGrid.Columns["ParamType"].Index)
            {
                var combo = e.Control as ComboBox;
                if (combo != null)
                {
                    combo.DropDownStyle = ComboBoxStyle.DropDown;
                }
            }
        }

        // Because the type column allows free-typed values not in its item list, the grid would
        // otherwise throw a data error. Swallow it for the ParamType column to permit custom types.
        private void OnParametersGridDataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            if (e.ColumnIndex == _parametersGrid.Columns["ParamType"].Index)
            {
                e.ThrowException = false;
            }
        }
    }
}
