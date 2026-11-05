namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel; using System.Drawing; using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    partial class MemberPickerDialog
    {
        private IContainer components = null; private Label hintLabel; private CheckedListBox membersListBox;
        private Button selectAllButton; private Button selectNoneButton; private Button okButton; private Button cancelButton;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this.hintLabel = new Label(); this.membersListBox = new CheckedListBox(); this.selectAllButton = new Button();
            this.selectNoneButton = new Button(); this.okButton = new Button(); this.cancelButton = new Button(); this.SuspendLayout();
            this.hintLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; this.hintLabel.Font = LunaFonts.Ui;
            this.hintLabel.Location = new Point(12, 10); this.hintLabel.Name = "hintLabel"; this.hintLabel.Size = new Size(400, 20); this.hintLabel.Text = "Choose members from the list.";
            this.membersListBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; this.membersListBox.CheckOnClick = true;
            this.membersListBox.Font = LunaFonts.Ui; this.membersListBox.FormattingEnabled = true; this.membersListBox.Location = new Point(12, 34);
            this.membersListBox.Name = "membersListBox"; this.membersListBox.Size = new Size(400, 259);
            this.selectAllButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; this.selectAllButton.Location = new Point(12, 302); this.selectAllButton.Name = "selectAllButton";
            this.selectAllButton.Size = new Size(80, 28); this.selectAllButton.Text = "Select All"; this.selectAllButton.Click += new System.EventHandler(this.OnSelectAllClick);
            this.selectNoneButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left; this.selectNoneButton.Location = new Point(98, 302); this.selectNoneButton.Name = "selectNoneButton";
            this.selectNoneButton.Size = new Size(90, 28); this.selectNoneButton.Text = "Select None"; this.selectNoneButton.Click += new System.EventHandler(this.OnSelectNoneClick);
            this.okButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; this.okButton.Location = new Point(242, 302); this.okButton.Name = "okButton";
            this.okButton.Size = new Size(80, 28); this.okButton.Text = "OK"; this.okButton.Click += new System.EventHandler(this.OnOkClick);
            this.cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; this.cancelButton.DialogResult = DialogResult.Cancel; this.cancelButton.Location = new Point(330, 302);
            this.cancelButton.Name = "cancelButton"; this.cancelButton.Size = new Size(80, 28); this.cancelButton.Text = "Cancel";
            this.AcceptButton = this.okButton; this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font; this.CancelButton = this.cancelButton;
            this.ClientSize = new Size(424, 341); this.Controls.Add(this.cancelButton); this.Controls.Add(this.okButton); this.Controls.Add(this.selectNoneButton);
            this.Controls.Add(this.selectAllButton); this.Controls.Add(this.membersListBox); this.Controls.Add(this.hintLabel); this.MaximizeBox = false;
            this.MinimizeBox = false; this.MinimumSize = new Size(340, 280); this.Name = "MemberPickerDialog"; this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent; this.Text = "Select Members"; this.ResumeLayout(false);
        }
    }
}
