namespace HelixPythonNamespace.App.Dialogs
{
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// Shown when a user drops one or more dragged type nodes onto another type node.
    /// Offers three outcomes:
    ///   - CreateFields: add private fields on the target; dragged nodes return to origin.
    ///   - JustMove: treat as a normal drag — nodes stay at their new positions.
    ///   - Cancel: roll drag back; no changes.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class ComposeConfirmDialog : LunaForm
    {
        public ComposeConfirmDialog()
        {
            InitializeComponent();
        }

        // Three-way choice returned to the drag/drop handler.
        public enum Result { Cancel, JustMove, CreateFields }

        // The user's decision; read by the caller after the dialog closes.
        public Result Outcome { get; private set; }

        public ComposeConfirmDialog(string targetName, IList<string> sourceNames) : this()
        {
            // Default to Cancel so an unexpected close (X button) is treated as no-op.
            Outcome = Result.Cancel;
            LoadComposition(targetName ?? "<target>", sourceNames ?? new List<string>());
        }

        // Fill the prompt (count + target) and list the dropped source type names.
        private void LoadComposition(string targetName, IList<string> sourceNames)
        {
            promptLabel.Text = string.Format("Create {0} private field(s) on '{1}' from the dropped type(s)?",
                sourceNames.Count, targetName);
            sourceTypesListBox.Items.Clear();
            foreach (string sourceName in sourceNames) sourceTypesListBox.Items.Add(sourceName);
        }

        // Compose: add private fields on the target for each dropped type.
        private void OnCreateFieldsClick(object sender, System.EventArgs e)
        { Outcome = Result.CreateFields; DialogResult = DialogResult.OK; Close(); }

        // Plain move: leave nodes at their dropped positions, create nothing.
        private void OnJustMoveClick(object sender, System.EventArgs e)
        { Outcome = Result.JustMove; DialogResult = DialogResult.OK; Close(); }

        // Cancel: record the rollback intent; DialogResult.Cancel is wired by the button itself.
        private void OnCancelClick(object sender, System.EventArgs e)
        { Outcome = Result.Cancel; }
    }
}
