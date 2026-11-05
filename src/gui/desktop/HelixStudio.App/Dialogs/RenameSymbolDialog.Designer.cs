namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel; using System.Drawing; using System.Windows.Forms;
    partial class RenameSymbolDialog
    {
        private IContainer components = null; private Label promptLabel; private TextBox _nameBox; private GroupBox scopeGroupBox;
        private RadioButton _solutionRadio; private RadioButton _projectRadio; private RadioButton _packageRadio; private RadioButton _fileRadio;
        private Button renameButton; private Button cancelButton;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this.promptLabel = new Label(); this._nameBox = new TextBox(); this.scopeGroupBox = new GroupBox(); this._solutionRadio = new RadioButton();
            this._projectRadio = new RadioButton(); this._packageRadio = new RadioButton(); this._fileRadio = new RadioButton(); this.renameButton = new Button(); this.cancelButton = new Button(); this.scopeGroupBox.SuspendLayout(); this.SuspendLayout();
            this.promptLabel.AutoEllipsis = true; this.promptLabel.Location = new Point(12, 12); this.promptLabel.Name = "promptLabel"; this.promptLabel.Size = new Size(396, 20); this.promptLabel.Text = "Rename 'OldName' to:";
            this._nameBox.Location = new Point(12, 38); this._nameBox.Name = "nameTextBox"; this._nameBox.Size = new Size(396, 20); this._nameBox.Text = "NewName";
            this.scopeGroupBox.Controls.Add(this._fileRadio); this.scopeGroupBox.Controls.Add(this._packageRadio); this.scopeGroupBox.Controls.Add(this._projectRadio); this.scopeGroupBox.Controls.Add(this._solutionRadio); this.scopeGroupBox.Location = new Point(12, 72); this.scopeGroupBox.Name = "scopeGroupBox"; this.scopeGroupBox.Size = new Size(396, 120); this.scopeGroupBox.Text = "Scope";
            this._solutionRadio.Checked = true; this._solutionRadio.Location = new Point(12, 22); this._solutionRadio.Name = "solutionRadio"; this._solutionRadio.Size = new Size(360, 20); this._solutionRadio.Text = "Solution (all projects)";
            this._projectRadio.Location = new Point(12, 44); this._projectRadio.Name = "projectRadio"; this._projectRadio.Size = new Size(360, 20); this._projectRadio.Text = "Project (the symbol's owning project)";
            this._packageRadio.Location = new Point(12, 66); this._packageRadio.Name = "packageRadio"; this._packageRadio.Size = new Size(360, 20); this._packageRadio.Text = "Package (the first-segment package directory)";
            this._fileRadio.Location = new Point(12, 88); this._fileRadio.Name = "fileRadio"; this._fileRadio.Size = new Size(360, 20); this._fileRadio.Text = "File (declaring file only)";
            this.renameButton.DialogResult = DialogResult.OK; this.renameButton.Location = new Point(244, 208); this.renameButton.Name = "renameButton"; this.renameButton.Size = new Size(80, 28); this.renameButton.Text = "Rename";
            this.cancelButton.DialogResult = DialogResult.Cancel; this.cancelButton.Location = new Point(330, 208); this.cancelButton.Name = "cancelButton"; this.cancelButton.Size = new Size(80, 28); this.cancelButton.Text = "Cancel";
            this.AcceptButton = this.renameButton; this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font; this.CancelButton = this.cancelButton; this.ClientSize = new Size(420, 250);
            this.Controls.Add(this.cancelButton); this.Controls.Add(this.renameButton); this.Controls.Add(this.scopeGroupBox); this.Controls.Add(this._nameBox); this.Controls.Add(this.promptLabel);
            this.FormBorderStyle = FormBorderStyle.FixedDialog; this.MaximizeBox = false; this.MinimizeBox = false; this.Name = "RenameSymbolDialog"; this.ShowInTaskbar = false; this.StartPosition = FormStartPosition.CenterParent; this.Text = "Rename Symbol";
            this.scopeGroupBox.ResumeLayout(false); this.ResumeLayout(false); this.PerformLayout();
        }
    }
}
