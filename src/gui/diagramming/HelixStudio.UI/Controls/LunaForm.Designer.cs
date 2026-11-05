namespace HelixPythonNamespace.UI.Controls
{
    using System.Drawing;
    using System.Windows.Forms;

    public partial class LunaForm
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

        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(640, 480);
            this.Name = "LunaForm";
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Luna Form";
            this.ResumeLayout(false);
        }
    }
}
