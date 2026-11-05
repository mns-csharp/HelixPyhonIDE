namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    partial class ChordCaptureDialog
    {
        private IContainer components = null;
        private Label displayLabel;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.displayLabel = new Label();
            this.SuspendLayout();
            this.displayLabel.Dock = DockStyle.Fill;
            this.displayLabel.Font = new Font("Consolas", 12F, FontStyle.Bold);
            this.displayLabel.Location = new Point(0, 0);
            this.displayLabel.Name = "displayLabel";
            this.displayLabel.Size = new Size(372, 111);
            this.displayLabel.TabIndex = 0;
            this.displayLabel.Text = "(press a key combination...)";
            this.displayLabel.TextAlign = ContentAlignment.MiddleCenter;
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(372, 111);
            this.Controls.Add(this.displayLabel);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.KeyPreview = true;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "ChordCaptureDialog";
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Press the desired shortcut";
            this.KeyDown += new KeyEventHandler(this.OnCaptureKeyDown);
            this.ResumeLayout(false);
        }
    }
}
