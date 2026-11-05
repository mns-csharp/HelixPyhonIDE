namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel; using System.Drawing; using System.Windows.Forms;
    partial class EditSignatureDialog
    {
        private IContainer components = null; private Panel topPanel; private Label returnLabel; private TextBox returnTextBox; private Label scopeLabel; private ComboBox scopeComboBox;
        private DataGridView parametersGrid; private DataGridViewTextBoxColumn nameColumn; private DataGridViewComboBoxColumn kindColumn; private DataGridViewTextBoxColumn annotationColumn; private DataGridViewTextBoxColumn defaultColumn;
        private FlowLayoutPanel buttonPanel; private Button addButton; private Button removeButton; private Button moveUpButton; private Button moveDownButton; private Button applyButton; private Button cancelButton;
        protected override void Dispose(bool disposing) { if (disposing && components != null) components.Dispose(); base.Dispose(disposing); }
        private void InitializeComponent()
        {
            this.topPanel = new Panel(); this.returnLabel = new Label(); this.returnTextBox = new TextBox(); this.scopeLabel = new Label(); this.scopeComboBox = new ComboBox();
            this.parametersGrid = new DataGridView(); this.nameColumn = new DataGridViewTextBoxColumn(); this.kindColumn = new DataGridViewComboBoxColumn(); this.annotationColumn = new DataGridViewTextBoxColumn(); this.defaultColumn = new DataGridViewTextBoxColumn();
            this.buttonPanel = new FlowLayoutPanel(); this.addButton = new Button(); this.removeButton = new Button(); this.moveUpButton = new Button(); this.moveDownButton = new Button(); this.applyButton = new Button(); this.cancelButton = new Button();
            this.topPanel.SuspendLayout(); ((ISupportInitialize)this.parametersGrid).BeginInit(); this.buttonPanel.SuspendLayout(); this.SuspendLayout();
            this.topPanel.Controls.Add(this.scopeComboBox); this.topPanel.Controls.Add(this.scopeLabel); this.topPanel.Controls.Add(this.returnTextBox); this.topPanel.Controls.Add(this.returnLabel); this.topPanel.Dock = DockStyle.Top; this.topPanel.Name = "topPanel"; this.topPanel.Size = new Size(704, 64);
            this.returnLabel.Location = new Point(8, 14); this.returnLabel.Name = "returnLabel"; this.returnLabel.Size = new Size(120, 20); this.returnLabel.Text = "Return annotation:";
            this.returnTextBox.Location = new Point(128, 12); this.returnTextBox.Name = "returnTextBox"; this.returnTextBox.Size = new Size(280, 20);
            this.scopeLabel.Location = new Point(420, 14); this.scopeLabel.Name = "scopeLabel"; this.scopeLabel.Size = new Size(50, 20); this.scopeLabel.Text = "Scope:";
            this.scopeComboBox.DropDownStyle = ComboBoxStyle.DropDownList; this.scopeComboBox.Items.AddRange(new object[] { "Solution", "Project", "Package", "File" }); this.scopeComboBox.Location = new Point(470, 12); this.scopeComboBox.Name = "scopeComboBox"; this.scopeComboBox.Size = new Size(160, 21); this.scopeComboBox.SelectedIndex = 0;
            this.parametersGrid.AllowUserToAddRows = false; this.parametersGrid.AllowUserToDeleteRows = false; this.parametersGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; this.parametersGrid.Columns.AddRange(new DataGridViewColumn[] { this.nameColumn, this.kindColumn, this.annotationColumn, this.defaultColumn }); this.parametersGrid.Dock = DockStyle.Fill; this.parametersGrid.Name = "parametersGrid"; this.parametersGrid.RowHeadersVisible = false; this.parametersGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            this.nameColumn.HeaderText = "Name"; this.nameColumn.Name = "nameColumn"; this.kindColumn.HeaderText = "Kind"; this.kindColumn.Items.AddRange("Regular", "PositionalOnly", "KeywordOnly", "VarPositional", "VarKeyword"); this.kindColumn.Name = "kindColumn"; this.annotationColumn.HeaderText = "Annotation"; this.annotationColumn.Name = "annotationColumn"; this.defaultColumn.HeaderText = "Default"; this.defaultColumn.Name = "defaultColumn";
            this.buttonPanel.Controls.Add(this.addButton); this.buttonPanel.Controls.Add(this.removeButton); this.buttonPanel.Controls.Add(this.moveUpButton); this.buttonPanel.Controls.Add(this.moveDownButton); this.buttonPanel.Controls.Add(this.applyButton); this.buttonPanel.Controls.Add(this.cancelButton); this.buttonPanel.Dock = DockStyle.Bottom; this.buttonPanel.Name = "buttonPanel"; this.buttonPanel.Padding = new Padding(8); this.buttonPanel.Size = new Size(704, 46);
            this.addButton.Name = "addButton"; this.addButton.Size = new Size(80, 28); this.addButton.Text = "Add"; this.addButton.Click += new System.EventHandler(this.OnAddClick);
            this.removeButton.Name = "removeButton"; this.removeButton.Size = new Size(80, 28); this.removeButton.Text = "Remove"; this.removeButton.Click += new System.EventHandler(this.OnRemoveClick);
            this.moveUpButton.Name = "moveUpButton"; this.moveUpButton.Size = new Size(80, 28); this.moveUpButton.Text = "Move Up"; this.moveUpButton.Click += new System.EventHandler(this.OnMoveUpClick);
            this.moveDownButton.Name = "moveDownButton"; this.moveDownButton.Size = new Size(80, 28); this.moveDownButton.Text = "Move Down"; this.moveDownButton.Click += new System.EventHandler(this.OnMoveDownClick);
            this.applyButton.DialogResult = DialogResult.OK; this.applyButton.Name = "applyButton"; this.applyButton.Size = new Size(80, 28); this.applyButton.Text = "Apply"; this.applyButton.Click += new System.EventHandler(this.OnApplyClick);
            this.cancelButton.DialogResult = DialogResult.Cancel; this.cancelButton.Name = "cancelButton"; this.cancelButton.Size = new Size(80, 28); this.cancelButton.Text = "Cancel";
            this.AcceptButton = this.applyButton; this.AutoScaleDimensions = new SizeF(6F, 13F); this.AutoScaleMode = AutoScaleMode.Font; this.CancelButton = this.cancelButton; this.ClientSize = new Size(704, 401); this.Controls.Add(this.parametersGrid); this.Controls.Add(this.buttonPanel); this.Controls.Add(this.topPanel);
            this.MaximizeBox = false; this.MinimizeBox = false; this.MinimumSize = new Size(620, 400); this.Name = "EditSignatureDialog"; this.ShowInTaskbar = false; this.StartPosition = FormStartPosition.CenterParent; this.Text = "Edit Signature";
            this.topPanel.ResumeLayout(false); this.topPanel.PerformLayout(); ((ISupportInitialize)this.parametersGrid).EndInit(); this.buttonPanel.ResumeLayout(false); this.ResumeLayout(false);
        }
    }
}
