namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    using HelixPythonNamespace.UI.Controls;
    partial class ComposeConfirmDialog
    {
        private IContainer components = null;
        private Label promptLabel;
        private ListBox sourceTypesListBox;
        private Button createFieldsButton;
        private Button justMoveButton;
        private Button cancelButton;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.promptLabel = new Label();
            this.sourceTypesListBox = new ListBox();
            this.createFieldsButton = new Button();
            this.justMoveButton = new Button();
            this.cancelButton = new Button();
            this.SuspendLayout();
            this.promptLabel.Font = LunaFonts.Sized(0.5f);
            this.promptLabel.Location = new Point(12, 12);
            this.promptLabel.Name = "promptLabel";
            this.promptLabel.Size = new Size(420, 36);
            this.promptLabel.TabIndex = 0;
            this.promptLabel.Text = "Create private fields on the target from the dropped types?";
            this.sourceTypesListBox.Font = LunaFonts.Ui;
            this.sourceTypesListBox.FormattingEnabled = true;
            this.sourceTypesListBox.ItemHeight = 14;
            this.sourceTypesListBox.Location = new Point(12, 54);
            this.sourceTypesListBox.Name = "sourceTypesListBox";
            this.sourceTypesListBox.SelectionMode = SelectionMode.None;
            this.sourceTypesListBox.Size = new Size(420, 130);
            this.sourceTypesListBox.TabIndex = 1;
            this.createFieldsButton.Location = new Point(12, 200);
            this.createFieldsButton.Name = "createFieldsButton";
            this.createFieldsButton.Size = new Size(120, 28);
            this.createFieldsButton.TabIndex = 2;
            this.createFieldsButton.Text = "Create Fields";
            this.createFieldsButton.UseVisualStyleBackColor = true;
            this.createFieldsButton.Click += new System.EventHandler(this.OnCreateFieldsClick);
            this.justMoveButton.Location = new Point(224, 200);
            this.justMoveButton.Name = "justMoveButton";
            this.justMoveButton.Size = new Size(100, 28);
            this.justMoveButton.TabIndex = 3;
            this.justMoveButton.Text = "Just Move";
            this.justMoveButton.UseVisualStyleBackColor = true;
            this.justMoveButton.Click += new System.EventHandler(this.OnJustMoveClick);
            this.cancelButton.DialogResult = DialogResult.Cancel;
            this.cancelButton.Location = new Point(344, 200);
            this.cancelButton.Name = "cancelButton";
            this.cancelButton.Size = new Size(88, 28);
            this.cancelButton.TabIndex = 4;
            this.cancelButton.Text = "Cancel";
            this.cancelButton.UseVisualStyleBackColor = true;
            this.cancelButton.Click += new System.EventHandler(this.OnCancelClick);
            this.AcceptButton = this.createFieldsButton;
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.CancelButton = this.cancelButton;
            this.ClientSize = new Size(452, 241);
            this.Controls.Add(this.cancelButton);
            this.Controls.Add(this.justMoveButton);
            this.Controls.Add(this.createFieldsButton);
            this.Controls.Add(this.sourceTypesListBox);
            this.Controls.Add(this.promptLabel);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "ComposeConfirmDialog";
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Compose on target?";
            this.ResumeLayout(false);
        }
    }
}
