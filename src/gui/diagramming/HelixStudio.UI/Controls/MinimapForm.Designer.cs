namespace HelixPythonNamespace.UI.Controls
{
    using System.Drawing;
    using System.Windows.Forms;

    public sealed partial class MinimapForm
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
            this._minimap = new MinimapControl();
            this.SuspendLayout();
            this._minimap.Dock = DockStyle.Fill;
            this._minimap.Location = new Point(0, 0);
            this._minimap.Name = "_minimap";
            this._minimap.Size = new Size(220, 160);
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(220, 160);
            this.Controls.Add(this._minimap);
            this.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.MinimumSize = new Size(150, 110);
            this.Name = "MinimapForm";
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.Manual;
            this.Text = "Minimap";
            this.TopMost = true;
            this.ResumeLayout(false);
        }
    }
}
