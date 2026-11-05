namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    partial class DeletionConfirmationDialog
    {
        private IContainer components = null;
        private Panel _headerPanel; private Label _headerLabel; private Panel listPanel; private ListBox _impactList;
        private Panel bottomPanel; private CheckBox _confirmCheckBox; private Button _deleteButton; private Button _cancelButton;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this._headerPanel = new Panel(); this._headerLabel = new Label(); this.listPanel = new Panel(); this._impactList = new ListBox();
            this.bottomPanel = new Panel(); this._confirmCheckBox = new CheckBox(); this._deleteButton = new Button(); this._cancelButton = new Button();
            this._headerPanel.SuspendLayout(); this.listPanel.SuspendLayout(); this.bottomPanel.SuspendLayout(); this.SuspendLayout();
            this._headerPanel.BackColor = Color.FromArgb(255, 250, 235); this._headerPanel.Controls.Add(this._headerLabel);
            this._headerPanel.Dock = DockStyle.Top; this._headerPanel.Name = "headerPanel"; this._headerPanel.Size = new Size(484, 60);
            this._headerLabel.AutoSize = true; this._headerLabel.Font = new Font(this.Font.FontFamily, 12F, FontStyle.Bold);
            this._headerLabel.Location = new Point(16, 18); this._headerLabel.Name = "headerLabel"; this._headerLabel.Text = "Delete 'item'?";
            this.listPanel.Controls.Add(this._impactList); this.listPanel.Dock = DockStyle.Fill; this.listPanel.Name = "listPanel";
            this.listPanel.Padding = new Padding(16); this.listPanel.Size = new Size(484, 221);
            this._impactList.Dock = DockStyle.Fill; this._impactList.DrawMode = DrawMode.OwnerDrawFixed; this._impactList.FormattingEnabled = true;
            this._impactList.IntegralHeight = false; this._impactList.ItemHeight = 24; this._impactList.Name = "impactList";
            this._impactList.DrawItem += new DrawItemEventHandler(this.ImpactList_DrawItem);
            this.bottomPanel.Controls.Add(this._cancelButton); this.bottomPanel.Controls.Add(this._deleteButton);
            this.bottomPanel.Controls.Add(this._confirmCheckBox); this.bottomPanel.Dock = DockStyle.Bottom; this.bottomPanel.Name = "bottomPanel";
            this.bottomPanel.Size = new Size(484, 80);
            this._confirmCheckBox.AutoSize = true; this._confirmCheckBox.Location = new Point(16, 10); this._confirmCheckBox.Name = "confirmCheckBox";
            this._confirmCheckBox.Text = "I understand this action cannot be undone"; this._confirmCheckBox.Visible = false;
            this._confirmCheckBox.CheckedChanged += new System.EventHandler(this.ConfirmCheckBox_CheckedChanged);
            this._deleteButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; this._deleteButton.BackColor = Color.FromArgb(220, 53, 69);
            this._deleteButton.FlatStyle = FlatStyle.Flat; this._deleteButton.ForeColor = Color.White; this._deleteButton.Location = new Point(288, 42);
            this._deleteButton.Name = "deleteButton"; this._deleteButton.Size = new Size(80, 28); this._deleteButton.Text = "Delete";
            this._deleteButton.Click += new System.EventHandler(this.DeleteButton_Click);
            this._cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; this._cancelButton.Location = new Point(378, 42);
            this._cancelButton.Name = "cancelButton"; this._cancelButton.Size = new Size(80, 28); this._cancelButton.Text = "Cancel";
            this._cancelButton.Click += new System.EventHandler(this.CancelButton_Click);
            this.AcceptButton = this._deleteButton; this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this._cancelButton; this.ClientSize = new Size(484, 361); this.Controls.Add(this.listPanel);
            this.Controls.Add(this._headerPanel); this.Controls.Add(this.bottomPanel); this.MaximizeBox = false; this.MinimizeBox = false;
            this.MinimumSize = new Size(400, 300); this.Name = "DeletionConfirmationDialog"; this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent; this.Text = "Confirm Deletion";
            this._headerPanel.ResumeLayout(false); this._headerPanel.PerformLayout(); this.listPanel.ResumeLayout(false);
            this.bottomPanel.ResumeLayout(false); this.bottomPanel.PerformLayout(); this.ResumeLayout(false);
        }
    }
}
