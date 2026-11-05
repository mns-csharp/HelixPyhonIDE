namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    using HelixPythonNamespace.UI.Controls;
    partial class PyProjectConflictDialog
    {
        private IContainer components = null;
        private Label headerLabel; private Label pathLabel; private RadioButton _keepRadio; private RadioButton _mergeRadio;
        private RadioButton _overwriteRadio; private CheckBox _applyAllCheck; private Button _okButton; private Button _cancelButton;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this.headerLabel = new Label(); this.pathLabel = new Label(); this._keepRadio = new RadioButton(); this._mergeRadio = new RadioButton();
            this._overwriteRadio = new RadioButton(); this._applyAllCheck = new CheckBox(); this._okButton = new Button(); this._cancelButton = new Button(); this.SuspendLayout();
            this.headerLabel.Font = LunaFonts.UiBold; this.headerLabel.Location = new Point(16, 16);
            this.headerLabel.Name = "headerLabel"; this.headerLabel.Size = new Size(520, 20); this.headerLabel.Text = "A pyproject.toml already exists at:";
            this.pathLabel.AutoEllipsis = true; this.pathLabel.Font = new Font("Consolas", 9F); this.pathLabel.Location = new Point(16, 40);
            this.pathLabel.Name = "pathLabel"; this.pathLabel.Size = new Size(520, 20); this.pathLabel.Text = "pyproject.toml";
            this._keepRadio.Checked = true; this._keepRadio.Location = new Point(24, 72); this._keepRadio.Name = "keepRadio";
            this._keepRadio.Size = new Size(500, 22); this._keepRadio.Text = "&Keep existing — leave this file untouched";
            this._mergeRadio.Location = new Point(24, 96); this._mergeRadio.Name = "mergeRadio"; this._mergeRadio.Size = new Size(500, 22);
            this._mergeRadio.Text = "&Merge — preserve everything; only add or update [tool.helixstudio]";
            this._overwriteRadio.Location = new Point(24, 120); this._overwriteRadio.Name = "overwriteRadio"; this._overwriteRadio.Size = new Size(500, 22);
            this._overwriteRadio.Text = "&Overwrite — replace with a freshly-authored minimal pyproject.toml";
            this._applyAllCheck.Location = new Point(24, 156); this._applyAllCheck.Name = "applyAllCheck"; this._applyAllCheck.Size = new Size(400, 22);
            this._applyAllCheck.Text = "&Apply this choice to all remaining conflicts";
            this._okButton.DialogResult = DialogResult.OK; this._okButton.Location = new Point(342, 200); this._okButton.Name = "okButton";
            this._okButton.Size = new Size(90, 28); this._okButton.Text = "OK";
            this._cancelButton.DialogResult = DialogResult.Cancel; this._cancelButton.Location = new Point(442, 200); this._cancelButton.Name = "cancelButton";
            this._cancelButton.Size = new Size(90, 28); this._cancelButton.Text = "Cancel";
            this.AcceptButton = this._okButton; this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this._cancelButton; this.ClientSize = new Size(544, 241); this.Controls.Add(this._cancelButton); this.Controls.Add(this._okButton);
            this.Controls.Add(this._applyAllCheck); this.Controls.Add(this._overwriteRadio); this.Controls.Add(this._mergeRadio); this.Controls.Add(this._keepRadio);
            this.Controls.Add(this.pathLabel); this.Controls.Add(this.headerLabel); this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false; this.MinimizeBox = false; this.Name = "PyProjectConflictDialog"; this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent; this.Text = "Existing pyproject.toml"; this.ResumeLayout(false);
        }
    }
}
