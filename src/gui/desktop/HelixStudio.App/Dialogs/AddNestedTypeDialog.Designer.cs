namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed partial class AddNestedTypeDialog
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

        private Button _cancel;
        private Button _ok;
        private ComboBox _kindCombo;
        private Label _parentLabel;
        private Label kindLabel;
        private Label nameLabel;
        private TextBox _nameBox;

        private void InitializeComponent()
        {
        Text = "Add Nested Type";
        Size = new Size(420, 220);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        
        _parentLabel = new Label();
            this._parentLabel.Name = "parentLabel";
        _parentLabel.Text = string.Format("Parent: {0}", (_parent == null ? "Parent.Type" : _parent.FullyQualifiedName));
        _parentLabel.Location = new Point(12, 12);
        _parentLabel.Size = new Size(380, 20);
        _parentLabel.Font = LunaFonts.Ui;
        
        this.kindLabel = new Label();
            this.kindLabel.Name = "kindLabel";
        kindLabel.Text = "Kind:";
        kindLabel.Location = new Point(12, 42);
        kindLabel.Size = new Size(80, 20);
        kindLabel.Font = LunaFonts.Ui;
        
        _kindCombo = new ComboBox();
            this._kindCombo.Name = "kindCombo";
        _kindCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _kindCombo.Location = new Point(100, 40);
        _kindCombo.Size = new Size(290, 22);
        _kindCombo.Font = LunaFonts.Sized(0.5f);
        _kindCombo.Items.Add("Class");
        _kindCombo.Items.Add("Abstract Class");
        _kindCombo.Items.Add("Interface");
        _kindCombo.Items.Add("Struct");
        _kindCombo.SelectedIndex = 0;
        
        this.nameLabel = new Label();
            this.nameLabel.Name = "nameLabel";
        nameLabel.Text = "Name:";
        nameLabel.Location = new Point(12, 76);
        nameLabel.Size = new Size(80, 20);
        nameLabel.Font = LunaFonts.Ui;
        
        _nameBox = new TextBox();
            this._nameBox.Name = "nameBox";
        _nameBox.Location = new Point(100, 74);
        _nameBox.Size = new Size(290, 22);
        _nameBox.Font = LunaFonts.Sized(0.5f);
        _nameBox.Text = "Nested";
        
        _ok = new Button();
            this._ok.Name = "ok";
        _ok.Text = "OK";
        _ok.DialogResult = DialogResult.None;
        _ok.Size = new Size(80, 28);
        _ok.Location = new Point(220, 140);
        _ok.Click += OnOk;
        
        _cancel = new Button();
            this._cancel.Name = "cancel";
        _cancel.Text = "Cancel";
        _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Size = new Size(80, 28);
        _cancel.Location = new Point(310, 140);
        
        AcceptButton = _ok;
        CancelButton = _cancel;
        
        Controls.Add(_parentLabel);
        Controls.Add(kindLabel);
        Controls.Add(_kindCombo);
        Controls.Add(nameLabel);
        Controls.Add(_nameBox);
        Controls.Add(_ok);
        Controls.Add(_cancel);
        }
    }
}
