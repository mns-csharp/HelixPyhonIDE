namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel; using System.Drawing; using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    partial class AboutDialog
    {
        private IContainer components = null; private Label titleLabel; private Label subtitleLabel; private Label versionLabel;
        private Label buildLabel; private Label runtimeLabel; private Label copyrightLabel; private LinkLabel repositoryLinkLabel; private Button okButton;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this.titleLabel = new Label(); this.subtitleLabel = new Label(); this.versionLabel = new Label(); this.buildLabel = new Label();
            this.runtimeLabel = new Label(); this.copyrightLabel = new Label(); this.repositoryLinkLabel = new LinkLabel(); this.okButton = new Button(); this.SuspendLayout();
            this.titleLabel.Font = LunaFonts.SizedBold(7f); this.titleLabel.Location = new Point(16, 14); this.titleLabel.Name = "titleLabel"; this.titleLabel.Size = new Size(400, 32); this.titleLabel.Text = "HelixPython";
            this.subtitleLabel.Font = LunaFonts.Ui; this.subtitleLabel.ForeColor = Color.FromArgb(90, 90, 90); this.subtitleLabel.Location = new Point(16, 50); this.subtitleLabel.Name = "subtitleLabel"; this.subtitleLabel.Size = new Size(400, 18); this.subtitleLabel.UseMnemonic = false; this.subtitleLabel.Text = "Visual OOP architecture designer & Python IDE";
            this.versionLabel.Font = LunaFonts.Ui; this.versionLabel.Location = new Point(16, 78); this.versionLabel.Name = "versionLabel"; this.versionLabel.Size = new Size(400, 18); this.versionLabel.Text = "Version";
            this.buildLabel.Font = LunaFonts.Ui; this.buildLabel.Location = new Point(16, 100); this.buildLabel.Name = "buildLabel"; this.buildLabel.Size = new Size(400, 18); this.buildLabel.Text = "Build:";
            this.runtimeLabel.Font = LunaFonts.Ui; this.runtimeLabel.Location = new Point(16, 122); this.runtimeLabel.Name = "runtimeLabel"; this.runtimeLabel.Size = new Size(400, 18); this.runtimeLabel.Text = ".NET Framework";
            this.copyrightLabel.Font = LunaFonts.Sized(-0.75f); this.copyrightLabel.ForeColor = Color.FromArgb(110, 110, 110); this.copyrightLabel.Location = new Point(16, 148); this.copyrightLabel.Name = "copyrightLabel"; this.copyrightLabel.Size = new Size(400, 18); this.copyrightLabel.UseMnemonic = false; this.copyrightLabel.Text = "Copyright";
            this.repositoryLinkLabel.Font = LunaFonts.Ui; this.repositoryLinkLabel.Location = new Point(16, 172); this.repositoryLinkLabel.Name = "repositoryLinkLabel"; this.repositoryLinkLabel.Size = new Size(400, 18); this.repositoryLinkLabel.Text = "https://github.com/"; this.repositoryLinkLabel.LinkClicked += new LinkLabelLinkClickedEventHandler(this.OnRepositoryLinkClicked);
            this.okButton.DialogResult = DialogResult.OK; this.okButton.Location = new Point(330, 205); this.okButton.Name = "okButton"; this.okButton.Size = new Size(84, 28); this.okButton.Text = "OK";
            this.AcceptButton = this.okButton; this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font; this.CancelButton = this.okButton; this.ClientSize = new Size(424, 245);
            this.Controls.Add(this.okButton); this.Controls.Add(this.repositoryLinkLabel); this.Controls.Add(this.copyrightLabel); this.Controls.Add(this.runtimeLabel); this.Controls.Add(this.buildLabel); this.Controls.Add(this.versionLabel); this.Controls.Add(this.subtitleLabel); this.Controls.Add(this.titleLabel);
            this.FormBorderStyle = FormBorderStyle.FixedDialog; this.MaximizeBox = false; this.MinimizeBox = false; this.Name = "AboutDialog"; this.ShowInTaskbar = false; this.StartPosition = FormStartPosition.CenterParent; this.Text = "About HelixStudio"; this.ResumeLayout(false);
        }
    }
}
