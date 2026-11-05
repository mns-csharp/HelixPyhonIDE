namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel; using System.Drawing; using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    partial class GoToTypePickerForm
    {
        private IContainer components = null; private Label headerLabel; private ListView candidateListView;
        private ColumnHeader fqnColumn; private ColumnHeader kindColumn; private Button goToButton; private Button cancelButton;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this.headerLabel = new Label(); this.candidateListView = new ListView(); this.fqnColumn = new ColumnHeader();
            this.fqnColumn.Name = "fqnColumn"; this.kindColumn = new ColumnHeader();
            this.kindColumn.Name = "kindColumn";
            this.goToButton = new Button(); this.cancelButton = new Button(); this.SuspendLayout();
            this.headerLabel.Font = LunaFonts.Sized(0.5f); this.headerLabel.Location = new Point(12, 8); this.headerLabel.Name = "headerLabel";
            this.headerLabel.Size = new Size(480, 20); this.headerLabel.Text = "'TypeName' matches multiple types. Pick one:";
            this.candidateListView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            this.candidateListView.Columns.AddRange(new ColumnHeader[] { this.fqnColumn, this.kindColumn }); this.candidateListView.Font = LunaFonts.Ui;
            this.candidateListView.FullRowSelect = true; this.candidateListView.HideSelection = false; this.candidateListView.Location = new Point(12, 32);
            this.candidateListView.MultiSelect = false; this.candidateListView.Name = "candidateListView"; this.candidateListView.Size = new Size(480, 210);
            this.candidateListView.View = View.Details; this.candidateListView.DoubleClick += new System.EventHandler(this.OnAcceptClick);
            this.fqnColumn.Text = "FQN"; this.fqnColumn.Width = 340; this.kindColumn.Text = "Kind"; this.kindColumn.Width = 120;
            this.goToButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; this.goToButton.Location = new Point(316, 252); this.goToButton.Name = "goToButton";
            this.goToButton.Size = new Size(80, 28); this.goToButton.Text = "Go To"; this.goToButton.Click += new System.EventHandler(this.OnAcceptClick);
            this.cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; this.cancelButton.DialogResult = DialogResult.Cancel; this.cancelButton.Location = new Point(404, 252);
            this.cancelButton.Name = "cancelButton"; this.cancelButton.Size = new Size(80, 28); this.cancelButton.Text = "Cancel";
            this.AcceptButton = this.goToButton; this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font; this.CancelButton = this.cancelButton;
            this.ClientSize = new Size(504, 281); this.Controls.Add(this.cancelButton); this.Controls.Add(this.goToButton); this.Controls.Add(this.candidateListView);
            this.Controls.Add(this.headerLabel); this.FormBorderStyle = FormBorderStyle.FixedToolWindow; this.KeyPreview = true; this.Name = "GoToTypePickerForm";
            this.ShowInTaskbar = false; this.StartPosition = FormStartPosition.CenterParent; this.Text = "Go to Type — pick candidate";
            this.KeyDown += new KeyEventHandler(this.OnFormKeyDown); this.ResumeLayout(false);
        }
    }
}
