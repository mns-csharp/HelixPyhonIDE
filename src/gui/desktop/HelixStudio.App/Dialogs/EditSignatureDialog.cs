namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Workspace.Services;

    // Grid-based editor for a function's signature: parameters (name/kind/annotation/default),
    // return annotation, and the rename scope. Used by the refactoring "change signature" flow.
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class EditSignatureDialog : LunaForm
    {
        public EditSignatureDialog() { InitializeComponent(); }
        // Seed the grid from the existing parameters. Each row's Tag holds the ORIGINAL name so the
        // refactor can match edited rows back to the parameters they came from (rename vs. add).
        public EditSignatureDialog(string functionName, IList<ParameterEdit> initial, string returnAnnotation) : this()
        {
            Text = "Edit Signature — " + (functionName ?? string.Empty); returnTextBox.Text = returnAnnotation ?? string.Empty;
            if (initial != null) foreach (ParameterEdit parameter in initial)
            {
                int index = parametersGrid.Rows.Add(parameter.NewName ?? string.Empty, parameter.Kind.ToString(), parameter.Annotation ?? string.Empty, parameter.DefaultValue ?? string.Empty);
                parametersGrid.Rows[index].Tag = parameter.OldName;
            }
        }
        // Populated on Apply; null until then. The caller reads these three outputs after OK.
        public List<ParameterEdit> EditedParams { get; private set; }
        public string NewReturnAnnotation { get { return returnTextBox.Text.Trim(); } }
        // Map the scope combo text back onto the RenameScope enum; default to the whole solution.
        public RenameScope SelectedScope
        {
            get { switch ((string)scopeComboBox.SelectedItem) { case "Project": return RenameScope.Project; case "Package": return RenameScope.Package; case "File": return RenameScope.File; default: return RenameScope.Solution; } }
        }
        // Add a new (untagged, hence brand-new) parameter row with placeholder defaults.
        private void OnAddClick(object sender, EventArgs e) { int index = parametersGrid.Rows.Add("param", "Regular", "", ""); parametersGrid.Rows[index].Tag = null; }
        private void OnRemoveClick(object sender, EventArgs e) { if (parametersGrid.CurrentRow != null) parametersGrid.Rows.Remove(parametersGrid.CurrentRow); }
        // Up/Down reorder the selected parameter row (order becomes the emitted parameter order).
        private void OnMoveUpClick(object sender, EventArgs e) { MoveRow(-1); }
        private void OnMoveDownClick(object sender, EventArgs e) { MoveRow(1); }
        // Apply snapshots the grid into EditedParams for the caller to consume — but first rejects any
        // parameter renamed to a non-identifier ("123", "my param"), which would break the signature.
        private void OnApplyClick(object sender, EventArgs e)
        {
            List<ParameterEdit> rows = ReadRows();
            string bad = FirstInvalidParamName(rows);
            if (bad != null)
            {
                MessageBox.Show(this, "'" + bad + "' is not a valid parameter name. Use a letter or "
                    + "underscore followed by letters, digits, or underscores.", "Invalid Signature",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;   // veto the close so the user can fix it
                return;
            }
            EditedParams = rows;
        }

        // The first parameter name that isn't a legal identifier, or null when all are valid.
        // Extracted so the validation is unit-testable without the modal dialog.
        internal static string FirstInvalidParamName(IEnumerable<ParameterEdit> rows)
        {
            if (rows == null) return null;
            foreach (ParameterEdit p in rows)
                if (!Identifiers.IsValid(p.NewName)) return p.NewName;
            return null;
        }
        // Move the current row by delta, keeping it selected at its new position.
        private void MoveRow(int delta)
        {
            if (parametersGrid.CurrentRow == null) return; int index = parametersGrid.CurrentRow.Index; int target = index + delta;
            if (target < 0 || target >= parametersGrid.Rows.Count) return; DataGridViewRow row = parametersGrid.Rows[index];
            parametersGrid.Rows.RemoveAt(index); parametersGrid.Rows.Insert(target, row); parametersGrid.CurrentCell = parametersGrid.Rows[target].Cells[0];
        }
        // Read the grid into ParameterEdit records; blank-named rows (e.g. the new-row placeholder)
        // are skipped. Tag carries the old name so downstream can tell renames from additions.
        private List<ParameterEdit> ReadRows()
        {
            var result = new List<ParameterEdit>(); foreach (DataGridViewRow row in parametersGrid.Rows)
            {
                string name = Convert.ToString(row.Cells["nameColumn"].Value).Trim(); if (name.Length == 0) continue;
                ParamKind kind; Enum.TryParse(Convert.ToString(row.Cells["kindColumn"].Value), out kind);
                result.Add(new ParameterEdit { OldName = row.Tag as string, NewName = name, Annotation = Convert.ToString(row.Cells["annotationColumn"].Value).Trim(), DefaultValue = Convert.ToString(row.Cells["defaultColumn"].Value).Trim(), Kind = kind });
            }
            return result;
        }
    }
}
