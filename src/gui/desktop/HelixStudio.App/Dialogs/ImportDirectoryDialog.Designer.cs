namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel; using System.Drawing; using System.Windows.Forms;
    partial class ImportDirectoryDialog
    {
        private IContainer components = null; private Label _sourceLabel; private TextBox _sourceTextBox; private Button _sourceBrowseButton;
        private Label _nameLabel; private TextBox _nameTextBox; private GroupBox _formatGroup; private RadioButton _tomlRadio;
        private RadioButton _vsRadio; private RadioButton _bothRadio; private CheckBox _overwriteCheckBox; private Label _previewLabel;
        private TextBox _previewTextBox; private Button _importButton; private Button _cancelButton;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this._sourceLabel = new Label(); this._sourceTextBox = new TextBox(); this._sourceBrowseButton = new Button(); this._nameLabel = new Label(); this._nameTextBox = new TextBox();
            this._formatGroup = new GroupBox(); this._tomlRadio = new RadioButton(); this._vsRadio = new RadioButton(); this._bothRadio = new RadioButton(); this._overwriteCheckBox = new CheckBox();
            this._previewLabel = new Label(); this._previewTextBox = new TextBox(); this._importButton = new Button(); this._cancelButton = new Button(); this._formatGroup.SuspendLayout(); this.SuspendLayout();
            this._sourceLabel.Location = new Point(16, 23); this._sourceLabel.Name = "sourceLabel"; this._sourceLabel.Size = new Size(110, 20); this._sourceLabel.Text = "Source directory:";
            this._sourceTextBox.Location = new Point(134, 20); this._sourceTextBox.Name = "sourceTextBox"; this._sourceTextBox.Size = new Size(300, 20); this._sourceTextBox.TextChanged += new System.EventHandler(this.OnInputChanged);
            this._sourceBrowseButton.Location = new Point(444, 19); this._sourceBrowseButton.Name = "sourceBrowseButton"; this._sourceBrowseButton.Size = new Size(80, 24); this._sourceBrowseButton.Text = "Browse..."; this._sourceBrowseButton.Click += new System.EventHandler(this.OnSourceBrowseClick);
            this._nameLabel.Location = new Point(16, 55); this._nameLabel.Name = "nameLabel"; this._nameLabel.Size = new Size(110, 20); this._nameLabel.Text = "Solution name:";
            this._nameTextBox.Location = new Point(134, 52); this._nameTextBox.Name = "nameTextBox"; this._nameTextBox.Size = new Size(390, 20); this._nameTextBox.Text = "imported"; this._nameTextBox.TextChanged += new System.EventHandler(this.OnInputChanged);
            this._formatGroup.Controls.Add(this._bothRadio); this._formatGroup.Controls.Add(this._vsRadio); this._formatGroup.Controls.Add(this._tomlRadio); this._formatGroup.Location = new Point(16, 84); this._formatGroup.Name = "formatGroup"; this._formatGroup.Size = new Size(508, 100); this._formatGroup.Text = "Target format";
            this._tomlRadio.Checked = true; this._tomlRadio.Location = new Point(12, 20); this._tomlRadio.Name = "tomlRadio"; this._tomlRadio.Size = new Size(480, 20); this._tomlRadio.Text = "&TOML Workspace (uv-compatible)"; this._tomlRadio.CheckedChanged += new System.EventHandler(this.OnInputChanged);
            this._vsRadio.Location = new Point(12, 44); this._vsRadio.Name = "visualStudioRadio"; this._vsRadio.Size = new Size(480, 20); this._vsRadio.Text = "&Visual Studio (.sln + .pyproj)"; this._vsRadio.CheckedChanged += new System.EventHandler(this.OnInputChanged);
            this._bothRadio.Location = new Point(12, 68); this._bothRadio.Name = "bothRadio"; this._bothRadio.Size = new Size(480, 20); this._bothRadio.Text = "&Both formats"; this._bothRadio.CheckedChanged += new System.EventHandler(this.OnInputChanged);
            this._overwriteCheckBox.Location = new Point(134, 196); this._overwriteCheckBox.Name = "overwriteCheckBox"; this._overwriteCheckBox.Size = new Size(390, 20); this._overwriteCheckBox.Text = "Overwrite existing .sln / .pyproj files";
            this._previewLabel.Location = new Point(16, 231); this._previewLabel.Name = "previewLabel"; this._previewLabel.Size = new Size(110, 20); this._previewLabel.Text = "Will create:";
            this._previewTextBox.BackColor = Color.FromArgb(245, 245, 245); this._previewTextBox.Location = new Point(134, 228); this._previewTextBox.Multiline = true; this._previewTextBox.Name = "previewTextBox"; this._previewTextBox.ReadOnly = true; this._previewTextBox.Size = new Size(390, 44); this._previewTextBox.Text = "(select a valid source directory)";
            this._importButton.DialogResult = DialogResult.OK; this._importButton.Enabled = false; this._importButton.Location = new Point(334, 288); this._importButton.Name = "importButton"; this._importButton.Size = new Size(90, 28); this._importButton.Text = "Import"; this._importButton.Click += new System.EventHandler(this.OnImportClick);
            this._cancelButton.DialogResult = DialogResult.Cancel; this._cancelButton.Location = new Point(434, 288); this._cancelButton.Name = "cancelButton"; this._cancelButton.Size = new Size(90, 28); this._cancelButton.Text = "Cancel";
            this.AcceptButton = this._importButton; this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font; this.CancelButton = this._cancelButton; this.ClientSize = new Size(544, 331);
            this.Controls.Add(this._cancelButton); this.Controls.Add(this._importButton); this.Controls.Add(this._previewTextBox); this.Controls.Add(this._previewLabel); this.Controls.Add(this._overwriteCheckBox); this.Controls.Add(this._formatGroup);
            this.Controls.Add(this._nameTextBox); this.Controls.Add(this._nameLabel); this.Controls.Add(this._sourceBrowseButton); this.Controls.Add(this._sourceTextBox); this.Controls.Add(this._sourceLabel);
            this.FormBorderStyle = FormBorderStyle.FixedDialog; this.MaximizeBox = false; this.MinimizeBox = false; this.Name = "ImportDirectoryDialog"; this.ShowInTaskbar = false; this.StartPosition = FormStartPosition.CenterParent; this.Text = "Import Python Directory";
            this._formatGroup.ResumeLayout(false); this.ResumeLayout(false); this.PerformLayout();
        }
    }
}
