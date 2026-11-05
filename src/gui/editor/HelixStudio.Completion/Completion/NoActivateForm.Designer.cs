namespace HelixPythonNamespace.Editor.Completion
{
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    partial class NoActivateForm
    {
        private IContainer components = null;
        private Label titleLabel;
        private Label bodyLabel;
        private Panel bodyPanel;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this.titleLabel = new Label(); this.bodyLabel = new Label(); this.bodyPanel = new Panel(); this.SuspendLayout();
            this.titleLabel.AutoEllipsis = true; this.titleLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            this.titleLabel.ForeColor = Color.FromArgb(30, 30, 30); this.titleLabel.Location = new Point(8, 6);
            this.titleLabel.Name = "titleLabel"; this.titleLabel.Size = new Size(344, 20); this.titleLabel.Text = "Completion item";
            // Scrollable body: the label auto-sizes (wraps at the panel width and grows in
            // height); the AutoScroll panel shows a vertical scrollbar when a long docstring
            // overflows, instead of clipping it.
            this.bodyPanel.Location = new Point(8, 28); this.bodyPanel.Name = "bodyPanel";
            this.bodyPanel.Size = new Size(344, 86); this.bodyPanel.AutoScroll = true;
            this.bodyLabel.Font = new Font("Segoe UI", 9F); this.bodyLabel.ForeColor = Color.FromArgb(70, 70, 70);
            this.bodyLabel.Location = new Point(0, 0); this.bodyLabel.Name = "bodyLabel";
            this.bodyLabel.AutoSize = true; this.bodyLabel.MaximumSize = new Size(324, 0);
            this.bodyLabel.Text = "Signature and documentation appear here.";
            this.bodyPanel.Controls.Add(this.bodyLabel);
            this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font;
            this.BackColor = Color.FromArgb(252, 252, 252); this.ClientSize = new Size(360, 120);
            this.Controls.Add(this.bodyPanel); this.Controls.Add(this.titleLabel); this.FormBorderStyle = FormBorderStyle.None;
            this.Name = "NoActivateForm"; this.ShowInTaskbar = false; this.StartPosition = FormStartPosition.Manual;
            this.Paint += new PaintEventHandler(this.OnBorderPaint); this.ResumeLayout(false);
        }
    }
}
