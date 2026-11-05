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
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// Dialog for editing TypeModel properties including name, namespace, members, and generics.
    /// Changes are applied through ITypeService.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class TypeModelEditorDialog : LunaForm
    {
        public TypeModelEditorDialog()
        {
            InitializeComponent();
        }

        private readonly TypeModel _originalType;
        private readonly SolutionModel _solution;
        private readonly ITypeService _typeService;

        // Basic properties








        // Members section





        // Generic parameters section



        // Dialog buttons


        // Menu strip for undo/redo



        // Undo/redo stacks
        private readonly Stack<DialogSnapshot> _undoStack;
        private readonly Stack<DialogSnapshot> _redoStack;
        private bool _suppressSnapshot;

        // Change tracking
        private bool _hasChanges;
        private string _originalName;
        private string _originalNamespace;
        private TypeKind _originalKind;
        private List<MemberModel> _originalMembers;
        private Dictionary<MemberModel, MemberModel> _originalMemberSnapshots;

        // Editing constructor. Captures the original name/namespace/kind and a deep snapshot of
        // each member so OnApplyClick can later diff current-vs-original and translate the edits
        // into the minimal set of ITypeService calls (rename, move, add/remove/modify member).
        // Note: the grids edit the live _originalType.Members list directly, so we must snapshot
        // originals up front before any UI change mutates them.
        public TypeModelEditorDialog(
            TypeModel typeModel,
            SolutionModel solution,
            ITypeService typeService) : this()
        {
            _originalType = typeModel ?? throw new ArgumentNullException("typeModel");
            _solution = solution ?? throw new ArgumentNullException("solution");
            _typeService = typeService ?? throw new ArgumentNullException("typeService");

            _originalName = typeModel.Name;
            _originalNamespace = typeModel.Namespace;
            _originalKind = typeModel.TypeKind;
            _originalMembers = typeModel.Members != null
                ? new List<MemberModel>(typeModel.Members)
                : new List<MemberModel>();
            // Per-member value snapshots keyed by the live member instance, used to detect
            // in-place edits (name/return-type/access changes) at apply time.
            _originalMemberSnapshots = new Dictionary<MemberModel, MemberModel>();
            if (typeModel.Members != null)
            {
                foreach (var m in typeModel.Members)
                {
                    var snap = new MemberModel(m.Name, m.MemberKind);
                    snap.ReturnType = m.ReturnType;
                    snap.AccessModifier = m.AccessModifier;
                    snap.HasGetter = m.HasGetter;
                    snap.HasSetter = m.HasSetter;
                    snap.IsAbstract = m.IsAbstract;
                    snap.IsVirtual = m.IsVirtual;
                    snap.IsStatic = m.IsStatic;
                    snap.IsOverride = m.IsOverride;
                    _originalMemberSnapshots[m] = snap;
                }
            }
            _hasChanges = false;
            _undoStack = new Stack<DialogSnapshot>();
            _redoStack = new Stack<DialogSnapshot>();

            PopulateFromType();
            RefreshReturnTypeItems();
        }

        /// <summary>
        /// Whether changes were applied successfully.
        /// </summary>
        public bool ChangesApplied { get; private set; }


        // Loads the type's state into all controls. _suppressSnapshot is raised for the duration
        // so the programmatic control changes here don't push spurious undo snapshots or flip the
        // _hasChanges flag; it is cleared (and the flag reset) at the end.
        private void PopulateFromType()
        {
            _suppressSnapshot = true;
            _nameTextBox.Text = _originalType.Name ?? string.Empty;
            _namespaceTextBox.Text = _originalType.Namespace ?? string.Empty;
            _sourceFileTextBox.Text = _originalType.SourceFilePath ?? string.Empty;

            // Set TypeKind combo
            switch (_originalType.TypeKind)
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

            // Populate members grid
            _membersGrid.Rows.Clear();
            foreach (var member in _originalType.Members)
            {
                AddMemberRow(member);
            }

            // Populate generic parameters grid
            _genericsGrid.Rows.Clear();
            foreach (var gp in _originalType.GenericParameters)
            {
                AddGenericParamRow(gp);
            }

            _hasChanges = false;
            _suppressSnapshot = false;
            UpdateApplyButtonState();
        }

        // Adds one grid row for a member. The live MemberModel is stashed in row.Tag so later
        // handlers can map a grid row back to its model. Complex fields (access, modifiers,
        // parameters) are rendered as display strings via the Format* helpers.
        private void AddMemberRow(MemberModel member)
        {
            int rowIndex = _membersGrid.Rows.Add();
            var row = _membersGrid.Rows[rowIndex];
            row.Tag = member;

            row.Cells["Kind"].Value = member.MemberKind == MemberKind.Method ? "Method" : "Property";
            row.Cells["MemberName"].Value = member.Name ?? string.Empty;
            row.Cells["ReturnType"].Value = member.ReturnType ?? string.Empty;
            row.Cells["Access"].Value = FormatAccessModifier(member.AccessModifier);
            row.Cells["Modifiers"].Value = FormatModifiers(member);
            row.Cells["Parameters"].Value = FormatParameters(member);
        }

        // Adds one grid row for a generic parameter, stashing the model in row.Tag. Variance is
        // rendered as a human-readable label (Covariant/Contravariant/Invariant).
        private void AddGenericParamRow(GenericParameterModel gp)
        {
            int rowIndex = _genericsGrid.Rows.Add();
            var row = _genericsGrid.Rows[rowIndex];
            row.Tag = gp;

            row.Cells["GPName"].Value = gp.Name ?? string.Empty;

            if (gp.IsCovariant)
                row.Cells["Variance"].Value = "Covariant (out)";
            else if (gp.IsContravariant)
                row.Cells["Variance"].Value = "Contravariant (in)";
            else
                row.Cells["Variance"].Value = "Invariant";

            row.Cells["Constraints"].Value = string.Join(", ", gp.Constraints);
        }

        // Display-string mappers for the read-only grid columns (access, modifier flags, and the
        // parameter signature). ParseAccessModifier below is the inverse used when the user edits.
        private static string FormatAccessModifier(AccessModifier modifier)
        {
            switch (modifier)
            {
                case AccessModifier.Public: return "Public";
                case AccessModifier.Protected: return "Protected";
                case AccessModifier.Internal: return "Internal";
                case AccessModifier.Private: return "Private";
                case AccessModifier.ProtectedInternal: return "Protected Internal";
                case AccessModifier.PrivateProtected: return "Private Protected";
                case AccessModifier.File: return "File";
                default: return "Public";
            }
        }

        private static string FormatModifiers(MemberModel member)
        {
            var modifiers = new List<string>();
            if (member.IsStatic) modifiers.Add("static");
            if (member.IsAbstract) modifiers.Add("abstract");
            if (member.IsVirtual) modifiers.Add("virtual");
            if (member.IsOverride) modifiers.Add("override");
            return string.Join(" ", modifiers);
        }

        // Renders a method's parameters as "type name, type name, ..."; empty for non-methods
        // or parameterless methods.
        private static string FormatParameters(MemberModel member)
        {
            if (member.MemberKind != MemberKind.Method || member.Parameters.Count == 0)
                return string.Empty;

            var parts = new List<string>();
            foreach (var param in member.Parameters)
            {
                parts.Add(string.Format("{0} {1}", param.TypeName, param.Name));
            }
            return string.Join(", ", parts);
        }

        // Inverse of FormatAccessModifier: turns the grid's display text back into the enum,
        // defaulting to Public for any unrecognized value.
        private static AccessModifier ParseAccessModifier(string value)
        {
            switch (value)
            {
                case "Protected": return AccessModifier.Protected;
                case "Internal": return AccessModifier.Internal;
                case "Private": return AccessModifier.Private;
                case "Protected Internal": return AccessModifier.ProtectedInternal;
                case "Private Protected": return AccessModifier.PrivateProtected;
                case "File": return AccessModifier.File;
                default: return AccessModifier.Public;
            }
        }

        // Builds the candidate type names for the ReturnType dropdown: C# built-ins (with nullable
        // forms), this type's own generic parameters, and every type visible in the solution.
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

            // Generic parameters from current type
            foreach (var gp in _originalType.GenericParameters)
            {
                if (!string.IsNullOrEmpty(gp.Name) && !types.Contains(gp.Name))
                {
                    types.Add(gp.Name);
                }
            }

            // Project-visible types (classes, structs, interfaces)
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

        // Repopulates the ReturnType combo column and, crucially, re-adds any value already sitting
        // in an existing cell that isn't in the fresh item list — a combo cell would otherwise blank
        // out a value not present in its items. Called whenever the visible type set may have changed
        // (e.g. after a generic parameter is added/renamed/removed).
        private void RefreshReturnTypeItems()
        {
            var typeList = BuildTypeList();
            var column = _membersGrid.Columns["ReturnType"] as DataGridViewComboBoxColumn;
            if (column == null) return;

            column.Items.Clear();
            foreach (var t in typeList)
            {
                column.Items.Add(t);
            }

            // Ensure existing cell values are in the items list
            foreach (DataGridViewRow row in _membersGrid.Rows)
            {
                if (row.IsNewRow) continue;
                var cell = row.Cells["ReturnType"] as DataGridViewComboBoxCell;
                if (cell != null && cell.Value != null)
                {
                    string val = cell.Value.ToString();
                    if (!string.IsNullOrEmpty(val) && !column.Items.Contains(val))
                    {
                        column.Items.Add(val);
                    }
                }
            }
        }

        // Make the ReturnType cell editable-combo while editing so free-typed types are allowed.
        private void OnMembersGridEditingControlShowing(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            if (_membersGrid.CurrentCell != null &&
                _membersGrid.CurrentCell.ColumnIndex == _membersGrid.Columns["ReturnType"].Index)
            {
                var combo = e.Control as ComboBox;
                if (combo != null)
                {
                    combo.DropDownStyle = ComboBoxStyle.DropDown;
                }
            }
        }

        // Suppress the combo-column data error raised when a cell holds a free-typed value that
        // isn't in the item list, so custom return types are accepted rather than throwing.
        private void OnMembersGridDataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            if (e.ColumnIndex == _membersGrid.Columns["ReturnType"].Index)
            {
                e.ThrowException = false;
            }
        }

        // Generic "something changed" handler (name/namespace/kind controls): record an undo
        // snapshot, mark dirty, and refresh the Apply button.
        private void OnValueChanged(object sender, EventArgs e)
        {
            PushUndo();
            _hasChanges = true;
            UpdateApplyButtonState();
        }

        // Members-grid edit committed: snapshot for undo, mark dirty, then write the edited cell
        // value back onto the underlying MemberModel (found via row.Tag) by column name.
        private void OnMembersGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            PushUndo();
            _hasChanges = true;
            UpdateApplyButtonState();

            // Sync changes back to member
            var row = _membersGrid.Rows[e.RowIndex];
            var member = row.Tag as MemberModel;
            if (member == null) return;

            var columnName = _membersGrid.Columns[e.ColumnIndex].Name;
            var value = row.Cells[e.ColumnIndex].Value?.ToString() ?? string.Empty;

            switch (columnName)
            {
                case "Kind":
                    member.MemberKind = value == "Property" ? MemberKind.Property : MemberKind.Method;
                    break;
                case "MemberName":
                    member.Name = value;
                    break;
                case "ReturnType":
                    member.ReturnType = value;
                    break;
                case "Access":
                    member.AccessModifier = ParseAccessModifier(value);
                    break;
            }
        }

        // Generics-grid edit committed: snapshot, mark dirty, and write the value back onto the
        // GenericParameterModel. Renaming a parameter also refreshes the return-type dropdown
        // (the name is a valid type); the Variance column maps text to the two variance flags,
        // and Constraints is parsed from a comma-separated list.
        private void OnGenericsGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            PushUndo();
            _hasChanges = true;
            UpdateApplyButtonState();

            // Sync changes back to generic parameter
            var row = _genericsGrid.Rows[e.RowIndex];
            var gp = row.Tag as GenericParameterModel;
            if (gp == null) return;

            var columnName = _genericsGrid.Columns[e.ColumnIndex].Name;
            var value = row.Cells[e.ColumnIndex].Value?.ToString() ?? string.Empty;

            switch (columnName)
            {
                case "GPName":
                    gp.Name = value;
                    RefreshReturnTypeItems();
                    break;
                case "Variance":
                    gp.IsCovariant = value == "Covariant (out)";
                    gp.IsContravariant = value == "Contravariant (in)";
                    break;
                case "Constraints":
                    gp.Constraints.Clear();
                    if (!string.IsNullOrEmpty(value))
                    {
                        var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            gp.Constraints.Add(part.Trim());
                        }
                    }
                    break;
            }
        }

        // Apply is only enabled when there are pending, unsaved changes.
        private void UpdateApplyButtonState()
        {
            _applyButton.Enabled = _hasChanges;
        }

        // ---- Event handlers ----

        // Adds a new void method (unique name) both to the model and the grid, marking dirty.
        private void OnAddMethod(object sender, EventArgs e)
        {
            PushUndo();
            string name = GetUniqueMemberName("NewMethod");
            var member = new MemberModel(name, MemberKind.Method)
            {
                ReturnType = "void",
                AccessModifier = AccessModifier.Public
            };
            _originalType.Members.Add(member);
            AddMemberRow(member);
            _hasChanges = true;
            UpdateApplyButtonState();
        }

        // Adds a new read/write string property (unique name) to the model and grid, marking dirty.
        private void OnAddProperty(object sender, EventArgs e)
        {
            PushUndo();
            string name = GetUniqueMemberName("NewProperty");
            var member = new MemberModel(name, MemberKind.Property)
            {
                ReturnType = "string",
                AccessModifier = AccessModifier.Public,
                HasGetter = true,
                HasSetter = true
            };
            _originalType.Members.Add(member);
            AddMemberRow(member);
            _hasChanges = true;
            UpdateApplyButtonState();
        }

        // Returns baseName if free, else appends 2..999, falling back to a random 4-char suffix,
        // so newly added members never collide with an existing member name.
        private string GetUniqueMemberName(string baseName)
        {
            if (_originalType.Members == null || !_originalType.Members.Exists(m => m.Name == baseName))
            {
                return baseName;
            }

            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseName + i;
                if (!_originalType.Members.Exists(m => m.Name == candidate))
                {
                    return candidate;
                }
            }

            return baseName + System.Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        // Opens the richer MemberEditorDialog on the selected member; on OK, re-renders that grid
        // row from the (now-mutated) model and marks dirty.
        private void OnEditMember(object sender, EventArgs e)
        {
            if (_membersGrid.CurrentRow == null) return;

            var member = _membersGrid.CurrentRow.Tag as MemberModel;
            if (member == null) return;

            using (var dialog = new MemberEditorDialog(member, _originalType, _solution))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    PushUndo();
                    // Refresh the row
                    var row = _membersGrid.CurrentRow;
                    row.Cells["Kind"].Value = member.MemberKind == MemberKind.Method ? "Method" : "Property";
                    row.Cells["MemberName"].Value = member.Name ?? string.Empty;
                    row.Cells["ReturnType"].Value = member.ReturnType ?? string.Empty;
                    row.Cells["Access"].Value = FormatAccessModifier(member.AccessModifier);
                    row.Cells["Modifiers"].Value = FormatModifiers(member);
                    row.Cells["Parameters"].Value = FormatParameters(member);

                    _hasChanges = true;
                    UpdateApplyButtonState();
                }
            }
        }

        // Removes the selected member from both the model and the grid, marking dirty.
        private void OnRemoveMember(object sender, EventArgs e)
        {
            if (_membersGrid.CurrentRow == null || _membersGrid.CurrentRow.IsNewRow) return;

            PushUndo();
            var member = _membersGrid.CurrentRow.Tag as MemberModel;
            if (member != null)
            {
                _originalType.Members.Remove(member);
            }
            _membersGrid.Rows.RemoveAt(_membersGrid.CurrentRow.Index);
            _hasChanges = true;
            UpdateApplyButtonState();
        }

        // Adds a new generic parameter (named "T" first, then "T2", "T3", ...) to the model and
        // grid, then refreshes the return-type dropdown since the new name is a usable type.
        private void OnAddGenericParam(object sender, EventArgs e)
        {
            PushUndo();
            string paramName = _originalType.GenericParameters.Count == 0
                ? "T"
                : string.Format("T{0}", _originalType.GenericParameters.Count + 1);

            var gp = new GenericParameterModel(paramName);
            _originalType.GenericParameters.Add(gp);
            AddGenericParamRow(gp);
            _hasChanges = true;
            UpdateApplyButtonState();
            RefreshReturnTypeItems();
        }

        // Removes the selected generic parameter from the model and grid, then refreshes the
        // return-type dropdown so its name is no longer offered as a type.
        private void OnRemoveGenericParam(object sender, EventArgs e)
        {
            if (_genericsGrid.CurrentRow == null || _genericsGrid.CurrentRow.IsNewRow) return;

            PushUndo();
            var gp = _genericsGrid.CurrentRow.Tag as GenericParameterModel;
            if (gp != null)
            {
                _originalType.GenericParameters.Remove(gp);
            }
            _genericsGrid.Rows.RemoveAt(_genericsGrid.CurrentRow.Index);
            _hasChanges = true;
            UpdateApplyButtonState();
            RefreshReturnTypeItems();
        }

        // Commit handler: validates the name, then translates the accumulated UI edits into the
        // minimal set of ITypeService operations by diffing current state against the originals
        // captured in the constructor (rename, move namespace, change kind, update generics, and
        // add/remove/modify members). Setting DialogResult.None on validation failure or error
        // keeps the dialog open. After a successful apply it runs a compliance pass to auto-fix
        // (or report) downstream types affected by the change.
        private void OnApplyClick(object sender, EventArgs e)
        {
            // Validate name
            string newName = _nameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                MessageBox.Show("Name cannot be empty.", "Validation Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _nameTextBox.Focus();
                return;
            }

            // Apply changes through ITypeService
            try
            {
                // Handle name change
                if (newName != _originalName)
                {
                    _typeService.RenameType(_solution, _originalType, newName);
                }

                // Handle namespace change
                string newNamespace = _namespaceTextBox.Text.Trim();
                if (newNamespace != _originalNamespace)
                {
                    _typeService.MoveToNamespace(_solution, _originalType, newNamespace);
                }

                // Handle type kind change
                TypeKind newKind = GetSelectedTypeKind();
                if (newKind != _originalKind)
                {
                    _typeService.ChangeTypeKind(_originalType, newKind);
                }

                // Persist generic parameter changes to source file and propagate to derived types
                _typeService.UpdateGenericParameters(_originalType, _solution);

                // Persist member changes to source file
                var currentMembers = _originalType.Members ?? new List<MemberModel>();

                // Find removed members
                foreach (var m in _originalMembers)
                {
                    if (!currentMembers.Contains(m))
                    {
                        _typeService.RemoveMember(_originalType, m, false);
                    }
                }

                // Find added members
                foreach (var m in currentMembers)
                {
                    if (!_originalMembers.Contains(m))
                    {
                        _typeService.AddMember(_originalType, m, false);
                    }
                }

                // Handle modified members (properties changed in-place)
                foreach (var m in currentMembers)
                {
                    MemberModel snapshot;
                    if (_originalMemberSnapshots.TryGetValue(m, out snapshot))
                    {
                        bool modified = m.Name != snapshot.Name
                            || m.ReturnType != snapshot.ReturnType
                            || m.AccessModifier != snapshot.AccessModifier;
                        if (modified)
                        {
                            _typeService.RemoveMember(_originalType, snapshot, false);
                            _typeService.AddMember(_originalType, m, false);
                        }
                    }
                }

                ChangesApplied = true;

                // R4-R7: Validate and propagate changes to downstream types
                try
                {
                    var graph = new HelixPythonNamespace.Core.Services.InheritanceGraph(_solution);

                    var engine = new HelixPythonNamespace.Core.Services.ImpactAnalysisEngine(graph);
                    var report = engine.AnalyzeCompliance(_originalType);

                    if (report != null && report.Gaps != null && report.Gaps.Count > 0)
                    {
                        // Apply auto-fixes
                        var complianceService = new HelixPythonNamespace.Workspace.Services.ComplianceUpdateService();
                        int fixCount = complianceService.ApplyAutoFixes(report);

                        // Check for remaining unfixed gaps
                        var remaining = new List<HelixPythonNamespace.Core.Model.ComplianceGap>();
                        foreach (var gap in report.Gaps)
                        {
                            if (!gap.IsResolved)
                            {
                                remaining.Add(gap);
                            }
                        }

                        if (remaining.Count > 0)
                        {
                            var sb = new System.Text.StringBuilder();
                            sb.AppendLine("Some downstream types may need manual attention:");
                            sb.AppendLine();
                            foreach (var gap in remaining)
                            {
                                sb.AppendFormat("- {0}: {1}", gap.DownstreamType.Name, gap.Description);
                                sb.AppendLine();
                            }

                            MessageBox.Show(sb.ToString(), "Compliance Notes",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else if (fixCount > 0)
                        {
                            // All gaps were auto-fixed, just notify
                            MessageBox.Show(
                                string.Format("{0} downstream type(s) were automatically updated.", fixCount),
                                "Auto-Update Complete",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                    }
                }
                catch (Exception compEx)
                {
                    // Don't block the apply for compliance issues
                    System.Diagnostics.Debug.WriteLine("Compliance check error: " + compEx.Message);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to apply changes: {0}", ex.Message),
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                DialogResult = DialogResult.None;
            }
        }

        // Maps the TypeKind combo's selected index back to the enum (0=Interface, 1=AbstractClass,
        // 2=ConcreteClass), defaulting to ConcreteClass.
        private TypeKind GetSelectedTypeKind()
        {
            switch (_typeKindCombo.SelectedIndex)
            {
                case 0: return TypeKind.Interface;
                case 1: return TypeKind.AbstractClass;
                case 2: return TypeKind.ConcreteClass;
                default: return TypeKind.ConcreteClass;
            }
        }

        // ---- Undo/Redo support ----

        // Captures a deep, value-only copy of the entire editable dialog state (name/namespace/kind
        // plus every member and generic-parameter row) into a DialogSnapshot for the undo stack.
        private DialogSnapshot CaptureSnapshot()
        {
            var snap = new DialogSnapshot();
            snap.Name = _nameTextBox.Text;
            snap.Namespace = _namespaceTextBox.Text;
            snap.TypeKindIndex = _typeKindCombo.SelectedIndex;

            snap.Members = new List<MemberSnapshot>();
            foreach (DataGridViewRow row in _membersGrid.Rows)
            {
                if (row.IsNewRow) continue;
                var member = row.Tag as MemberModel;
                if (member == null) continue;
                snap.Members.Add(new MemberSnapshot(member));
            }

            snap.Generics = new List<GenericSnapshot>();
            foreach (DataGridViewRow row in _genericsGrid.Rows)
            {
                if (row.IsNewRow) continue;
                var gp = row.Tag as GenericParameterModel;
                if (gp == null) continue;
                snap.Generics.Add(new GenericSnapshot(gp));
            }

            return snap;
        }

        // Rebuilds the model and grids from a snapshot (inverse of CaptureSnapshot). Runs with
        // _suppressSnapshot raised so the wholesale control repopulation doesn't push new undo
        // entries, and refreshes the return-type dropdown since the generic set may have changed.
        private void RestoreSnapshot(DialogSnapshot snap)
        {
            _suppressSnapshot = true;
            try
            {
                _nameTextBox.Text = snap.Name;
                _namespaceTextBox.Text = snap.Namespace;
                _typeKindCombo.SelectedIndex = snap.TypeKindIndex;

                // Restore members
                _originalType.Members.Clear();
                _membersGrid.Rows.Clear();
                foreach (var ms in snap.Members)
                {
                    var member = ms.ToMemberModel();
                    _originalType.Members.Add(member);
                    AddMemberRow(member);
                }

                // Restore generics
                _originalType.GenericParameters.Clear();
                _genericsGrid.Rows.Clear();
                foreach (var gs in snap.Generics)
                {
                    var gp = gs.ToGenericParameterModel();
                    _originalType.GenericParameters.Add(gp);
                    AddGenericParamRow(gp);
                }

                _hasChanges = true;
                UpdateApplyButtonState();
                RefreshReturnTypeItems();
            }
            finally
            {
                _suppressSnapshot = false;
            }
        }

        // Called before every mutation to record the pre-change state for undo. A new user action
        // invalidates the redo history. No-ops while _suppressSnapshot is set (programmatic loads).
        private void PushUndo()
        {
            if (_suppressSnapshot) return;
            _undoStack.Push(CaptureSnapshot());
            _redoStack.Clear();
            UpdateUndoRedoState();
        }

        // Enable/disable the Undo and Redo menu items to reflect whether their stacks have entries.
        private void UpdateUndoRedoState()
        {
            _undoMenuItem.Enabled = _undoStack.Count > 0;
            _redoMenuItem.Enabled = _redoStack.Count > 0;
        }

        // Undo: push current state onto redo, then restore the most recent undo snapshot.
        private void OnUndoClick(object sender, EventArgs e)
        {
            if (_undoStack.Count == 0) return;

            _redoStack.Push(CaptureSnapshot());
            var snap = _undoStack.Pop();
            RestoreSnapshot(snap);
            UpdateUndoRedoState();
        }

        // Redo: push current state back onto undo, then restore the most recent redo snapshot.
        private void OnRedoClick(object sender, EventArgs e)
        {
            if (_redoStack.Count == 0) return;

            _undoStack.Push(CaptureSnapshot());
            var snap = _redoStack.Pop();
            RestoreSnapshot(snap);
            UpdateUndoRedoState();
        }
    }

    // Immutable value-snapshot of the whole dialog used as one entry on the undo/redo stacks.
    internal sealed class DialogSnapshot
    {
        public string Name;
        public string Namespace;
        public int TypeKindIndex;
        public List<MemberSnapshot> Members;
        public List<GenericSnapshot> Generics;
    }

    // Value copy of a MemberModel (all flags + parameters). The ctor copies from a live model and
    // ToMemberModel() rebuilds an independent model, so snapshots stay decoupled from live edits.
    internal sealed class MemberSnapshot
    {
        public string Name;
        public MemberKind MemberKind;
        public string ReturnType;
        public AccessModifier AccessModifier;
        public bool IsStatic;
        public bool IsAbstract;
        public bool IsVirtual;
        public bool IsOverride;
        public bool HasGetter;
        public bool HasSetter;
        public List<MethodParamSnapshot> Parameters;

        public MemberSnapshot(MemberModel m)
        {
            Name = m.Name;
            MemberKind = m.MemberKind;
            ReturnType = m.ReturnType;
            AccessModifier = m.AccessModifier;
            IsStatic = m.IsStatic;
            IsAbstract = m.IsAbstract;
            IsVirtual = m.IsVirtual;
            IsOverride = m.IsOverride;
            HasGetter = m.HasGetter;
            HasSetter = m.HasSetter;
            Parameters = new List<MethodParamSnapshot>();
            if (m.Parameters != null)
            {
                foreach (var p in m.Parameters)
                {
                    Parameters.Add(new MethodParamSnapshot { Name = p.Name, TypeName = p.TypeName });
                }
            }
        }

        public MemberModel ToMemberModel()
        {
            var m = new MemberModel(Name, MemberKind);
            m.ReturnType = ReturnType;
            m.AccessModifier = AccessModifier;
            m.IsStatic = IsStatic;
            m.IsAbstract = IsAbstract;
            m.IsVirtual = IsVirtual;
            m.IsOverride = IsOverride;
            m.HasGetter = HasGetter;
            m.HasSetter = HasSetter;
            foreach (var p in Parameters)
            {
                m.Parameters.Add(new MethodParameterModel(p.Name, p.TypeName));
            }
            return m;
        }
    }

    // Value copy of a single method parameter (name + type) held inside a MemberSnapshot.
    internal sealed class MethodParamSnapshot
    {
        public string Name;
        public string TypeName;
    }

    // Value copy of a GenericParameterModel (name, variance flags, constraints). Same round-trip
    // pattern as MemberSnapshot: copy in via ctor, rebuild an independent model via ToGeneric...().
    internal sealed class GenericSnapshot
    {
        public string Name;
        public bool IsCovariant;
        public bool IsContravariant;
        public List<string> Constraints;

        public GenericSnapshot(GenericParameterModel gp)
        {
            Name = gp.Name;
            IsCovariant = gp.IsCovariant;
            IsContravariant = gp.IsContravariant;
            Constraints = new List<string>(gp.Constraints);
        }

        public GenericParameterModel ToGenericParameterModel()
        {
            var gp = new GenericParameterModel(Name);
            gp.IsCovariant = IsCovariant;
            gp.IsContravariant = IsContravariant;
            gp.Constraints.Clear();
            gp.Constraints.AddRange(Constraints);
            return gp;
        }
    }
}
