namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel; using System.Drawing; using System.Windows.Forms;
    partial class SelectPythonInterpreterDialog
    {
        private IContainer components = null; private Panel toolbarPanel; private Button refreshButton; private Button browseButton;
        private ListView interpretersListView; private ColumnHeader pathColumn; private ColumnHeader versionColumn; private ColumnHeader flavorColumn;
        private ColumnHeader architectureColumn; private ColumnHeader sourceColumn; private ColumnHeader pythonNetColumn; private Label selectedLabel;
        private Panel footerPanel; private Button applyButton; private Button saveButton; private Button cancelButton;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this.toolbarPanel = new Panel(); this.refreshButton = new Button(); this.browseButton = new Button(); this.interpretersListView = new ListView();
            this.pathColumn = new ColumnHeader(); this.versionColumn = new ColumnHeader(); this.flavorColumn = new ColumnHeader(); this.architectureColumn = new ColumnHeader(); this.sourceColumn = new ColumnHeader(); this.pythonNetColumn = new ColumnHeader();
            this.selectedLabel = new Label(); this.footerPanel = new Panel(); this.applyButton = new Button(); this.saveButton = new Button(); this.cancelButton = new Button(); this.toolbarPanel.SuspendLayout(); this.footerPanel.SuspendLayout(); this.SuspendLayout();
            this.toolbarPanel.Controls.Add(this.browseButton); this.toolbarPanel.Controls.Add(this.refreshButton); this.toolbarPanel.Dock = DockStyle.Top; this.toolbarPanel.Name = "toolbarPanel"; this.toolbarPanel.Size = new Size(804, 40);
            this.refreshButton.Location = new Point(8, 6); this.refreshButton.Name = "refreshButton"; this.refreshButton.Size = new Size(90, 28); this.refreshButton.Text = "Refresh"; this.refreshButton.Click += new System.EventHandler(this.OnRefreshClick);
            this.browseButton.Location = new Point(104, 6); this.browseButton.Name = "browseButton"; this.browseButton.Size = new Size(90, 28); this.browseButton.Text = "Browse..."; this.browseButton.Click += new System.EventHandler(this.OnBrowseClick);
            this.interpretersListView.Columns.AddRange(new ColumnHeader[] { this.pathColumn, this.versionColumn, this.flavorColumn, this.architectureColumn, this.sourceColumn, this.pythonNetColumn }); this.interpretersListView.Dock = DockStyle.Fill; this.interpretersListView.FullRowSelect = true; this.interpretersListView.HideSelection = false; this.interpretersListView.Location = new Point(0, 40); this.interpretersListView.MultiSelect = false; this.interpretersListView.Name = "interpretersListView"; this.interpretersListView.Size = new Size(804, 352); this.interpretersListView.View = View.Details;
            this.pathColumn.Text = "Path"; this.pathColumn.Width = 340; this.versionColumn.Text = "Version"; this.versionColumn.Width = 80; this.flavorColumn.Text = "Flavor"; this.flavorColumn.Width = 100; this.architectureColumn.Text = "Arch"; this.architectureColumn.Width = 70; this.sourceColumn.Text = "Source"; this.sourceColumn.Width = 110; this.pythonNetColumn.Text = "Python.NET"; this.pythonNetColumn.Width = 90;
            this.selectedLabel.Dock = DockStyle.Bottom; this.selectedLabel.ForeColor = Color.FromArgb(90, 90, 90); this.selectedLabel.Name = "selectedLabel"; this.selectedLabel.Padding = new Padding(8, 4, 8, 4); this.selectedLabel.Size = new Size(804, 28); this.selectedLabel.Text = "No interpreter selected."; this.selectedLabel.TextAlign = ContentAlignment.MiddleLeft;
            this.footerPanel.Controls.Add(this.cancelButton); this.footerPanel.Controls.Add(this.saveButton); this.footerPanel.Controls.Add(this.applyButton); this.footerPanel.Dock = DockStyle.Bottom; this.footerPanel.Name = "footerPanel"; this.footerPanel.Size = new Size(804, 44);
            this.applyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; this.applyButton.Location = new Point(484, 8); this.applyButton.Name = "applyButton"; this.applyButton.Size = new Size(90, 28); this.applyButton.Text = "Apply"; this.applyButton.Click += new System.EventHandler(this.OnApplyClick);
            this.saveButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; this.saveButton.Location = new Point(584, 8); this.saveButton.Name = "saveButton"; this.saveButton.Size = new Size(120, 28); this.saveButton.Text = "Save && Close"; this.saveButton.Click += new System.EventHandler(this.OnSaveClick);
            this.cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; this.cancelButton.DialogResult = DialogResult.Cancel; this.cancelButton.Location = new Point(708, 8); this.cancelButton.Name = "cancelButton"; this.cancelButton.Size = new Size(90, 28); this.cancelButton.Text = "Cancel";
            this.AcceptButton = this.saveButton; this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font; this.CancelButton = this.cancelButton; this.ClientSize = new Size(804, 401);
            this.Controls.Add(this.interpretersListView); this.Controls.Add(this.selectedLabel); this.Controls.Add(this.footerPanel); this.Controls.Add(this.toolbarPanel); this.MaximizeBox = true; this.MinimizeBox = false; this.MinimumSize = new Size(640, 360);
            this.Name = "SelectPythonInterpreterDialog"; this.ShowInTaskbar = false; this.StartPosition = FormStartPosition.CenterParent; this.Text = "Select Python Interpreter"; this.Load += new System.EventHandler(this.OnDialogLoad);
            this.toolbarPanel.ResumeLayout(false); this.footerPanel.ResumeLayout(false); this.ResumeLayout(false);
        }
    }
}
