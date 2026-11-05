namespace HelixPythonNamespace.App.Dialogs
{
    using System.Drawing;
    using System.Windows.Forms;
    public sealed partial class SimpleInputDialog
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private Label promptLabel;
        private Button cancelButton;
        private Button _okButton;
        private TextBox _textBox;

        private void InitializeComponent()
        {
            this.promptLabel = new Label();
            this.promptLabel.Name = "promptLabel"; this._textBox = new TextBox();
            this._textBox.Name = "textBox"; this._okButton = new Button();
            this._okButton.Name = "okButton"; this.cancelButton = new Button();
            this.cancelButton.Name = "cancelButton";
            this.SuspendLayout();
            this.promptLabel.Location = new Point(12, 12); this.promptLabel.Size = new Size(396, 20); this.promptLabel.Text = "Enter value:";
            this._textBox.Location = new Point(12, 38); this._textBox.Size = new Size(396, 22); this._textBox.Text = "Sample value";
            this._okButton.DialogResult = DialogResult.OK; this._okButton.Location = new Point(244, 78); this._okButton.Size = new Size(80, 28); this._okButton.Text = "OK";
            this.cancelButton.DialogResult = DialogResult.Cancel; this.cancelButton.Location = new Point(330, 78); this.cancelButton.Size = new Size(80, 28); this.cancelButton.Text = "Cancel";
            this.AcceptButton = this._okButton; this.CancelButton = this.cancelButton; this.ClientSize = new Size(420, 120);
            this.Controls.AddRange(new Control[] { this.promptLabel, this._textBox, this._okButton, this.cancelButton });
            this.FormBorderStyle = FormBorderStyle.FixedDialog; this.Name = "SimpleInputDialog"; this.Text = "Input"; this.ResumeLayout(false); this.PerformLayout();
        }
    }
}
