namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    partial class FileConflictDialog
    {
        private IContainer components = null;
        private Panel warningPanel;
        private Label warningIconLabel;
        private Label _messageLabel;
        private Label _filePathLabel;
        private Label descriptionLabel;
        private Panel optionsPanel;
        private Button _keepLocalButton;
        private Button _loadExternalButton;
        private Button _cancelButton;

        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }

        private void InitializeComponent()
        {
            this.warningPanel = new Panel(); this.warningIconLabel = new Label(); this._messageLabel = new Label();
            this._filePathLabel = new Label(); this.descriptionLabel = new Label(); this.optionsPanel = new Panel();
            this._keepLocalButton = new Button(); this._loadExternalButton = new Button(); this._cancelButton = new Button();
            this.warningPanel.SuspendLayout(); this.optionsPanel.SuspendLayout(); this.SuspendLayout();
            this.warningPanel.BackColor = Color.FromArgb(255, 248, 220); this.warningPanel.Controls.Add(this._filePathLabel);
            this.warningPanel.Controls.Add(this._messageLabel); this.warningPanel.Controls.Add(this.warningIconLabel);
            this.warningPanel.Dock = DockStyle.Top; this.warningPanel.Name = "warningPanel"; this.warningPanel.Size = new Size(484, 60);
            this.warningIconLabel.AutoSize = true; this.warningIconLabel.Font = new Font("Segoe UI Symbol", 20F);
            this.warningIconLabel.ForeColor = Color.FromArgb(200, 140, 0); this.warningIconLabel.Location = new Point(16, 10);
            this.warningIconLabel.Name = "warningIconLabel"; this.warningIconLabel.Text = "⚠";
            this._messageLabel.AutoEllipsis = true; this._messageLabel.Location = new Point(60, 10); this._messageLabel.Name = "messageLabel";
            this._messageLabel.Size = new Size(400, 20); this._messageLabel.Text = "A file conflict has been detected.";
            this._filePathLabel.AutoEllipsis = true; this._filePathLabel.Font = new Font(this.Font, FontStyle.Italic);
            this._filePathLabel.ForeColor = Color.Gray; this._filePathLabel.Location = new Point(60, 32); this._filePathLabel.Name = "filePathLabel";
            this._filePathLabel.Size = new Size(400, 20); this._filePathLabel.Text = "Unknown file";
            this.descriptionLabel.Location = new Point(16, 75); this.descriptionLabel.Name = "descriptionLabel";
            this.descriptionLabel.Size = new Size(460, 20); this.descriptionLabel.Text = "How would you like to resolve this conflict?";
            this.optionsPanel.Controls.Add(this._cancelButton); this.optionsPanel.Controls.Add(this._loadExternalButton);
            this.optionsPanel.Controls.Add(this._keepLocalButton); this.optionsPanel.Location = new Point(16, 105);
            this.optionsPanel.Name = "optionsPanel"; this.optionsPanel.Size = new Size(460, 40);
            this._keepLocalButton.Location = new Point(0, 0); this._keepLocalButton.Name = "keepLocalButton";
            this._keepLocalButton.Size = new Size(140, 35); this._keepLocalButton.Text = "Keep Local Changes";
            this._keepLocalButton.Click += new System.EventHandler(this.KeepLocalButton_Click);
            this._loadExternalButton.Location = new Point(150, 0); this._loadExternalButton.Name = "loadExternalButton";
            this._loadExternalButton.Size = new Size(150, 35); this._loadExternalButton.Text = "Load External Changes";
            this._loadExternalButton.Click += new System.EventHandler(this.LoadExternalButton_Click);
            this._cancelButton.Location = new Point(310, 0); this._cancelButton.Name = "cancelButton";
            this._cancelButton.Size = new Size(80, 35); this._cancelButton.Text = "Cancel";
            this._cancelButton.Click += new System.EventHandler(this.CancelButton_Click);
            this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this._cancelButton; this.ClientSize = new Size(484, 181); this.Controls.Add(this.optionsPanel);
            this.Controls.Add(this.descriptionLabel); this.Controls.Add(this.warningPanel); this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false; this.MinimizeBox = false; this.Name = "FileConflictDialog"; this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent; this.Text = "File Conflict Detected";
            this.warningPanel.ResumeLayout(false); this.warningPanel.PerformLayout(); this.optionsPanel.ResumeLayout(false); this.ResumeLayout(false);
        }
    }
}
