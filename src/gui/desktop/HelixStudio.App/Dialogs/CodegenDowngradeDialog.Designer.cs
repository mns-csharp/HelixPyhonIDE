namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    using HelixPythonNamespace.UI.Controls;
    partial class CodegenDowngradeDialog
    {
        private IContainer components = null;
        private Label headingLabel;
        private Label subheadLabel;
        private ListBox downgradeListBox;
        private Button proceedButton;
        private Button cancelButton;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.headingLabel = new Label();
            this.subheadLabel = new Label();
            this.downgradeListBox = new ListBox();
            this.proceedButton = new Button();
            this.cancelButton = new Button();
            this.SuspendLayout();
            // headingLabel
            this.headingLabel.Font = LunaFonts.Ui;
            this.headingLabel.Location = new Point(12, 12);
            this.headingLabel.Name = "headingLabel";
            this.headingLabel.Size = new Size(480, 40);
            this.headingLabel.TabIndex = 0;
            this.headingLabel.Text = "Project '<project>' targets an unknown framework and requires compatibility changes.";
            // subheadLabel
            this.subheadLabel.Font = LunaFonts.UiBold;
            this.subheadLabel.Location = new Point(12, 56);
            this.subheadLabel.Name = "subheadLabel";
            this.subheadLabel.Size = new Size(480, 20);
            this.subheadLabel.TabIndex = 1;
            this.subheadLabel.Text = "The following downgrades will be applied if you proceed:";
            // downgradeListBox
            this.downgradeListBox.Font = LunaFonts.Ui;
            this.downgradeListBox.FormattingEnabled = true;
            this.downgradeListBox.ItemHeight = 14;
            this.downgradeListBox.Location = new Point(12, 80);
            this.downgradeListBox.Name = "downgradeListBox";
            this.downgradeListBox.SelectionMode = SelectionMode.None;
            this.downgradeListBox.Size = new Size(480, 172);
            this.downgradeListBox.TabIndex = 2;
            // proceedButton
            this.proceedButton.Location = new Point(292, 260);
            this.proceedButton.Name = "proceedButton";
            this.proceedButton.Size = new Size(100, 28);
            this.proceedButton.TabIndex = 3;
            this.proceedButton.Text = "Proceed";
            this.proceedButton.UseVisualStyleBackColor = true;
            this.proceedButton.Click += new System.EventHandler(this.OnProceedClick);
            // cancelButton
            this.cancelButton.DialogResult = DialogResult.Cancel;
            this.cancelButton.Location = new Point(400, 260);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new Size(100, 28);
            this.cancelButton.TabIndex = 4;
            this.cancelButton.Text = "Cancel";
            this.cancelButton.UseVisualStyleBackColor = true;
            // CodegenDowngradeDialog
            this.AcceptButton = this.proceedButton;
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new Size(512, 301);
            this.Controls.Add(this.cancelButton);
            this.Controls.Add(this.proceedButton);
            this.Controls.Add(this.downgradeListBox);
            this.Controls.Add(this.subheadLabel);
            this.Controls.Add(this.headingLabel);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "CodegenDowngradeDialog";
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Codegen Downgrades Required";
            this.ResumeLayout(false);
        }
    }
}
