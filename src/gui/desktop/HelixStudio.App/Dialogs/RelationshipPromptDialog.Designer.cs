namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed partial class RelationshipPromptDialog
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
        private Label header;
        private Label kindLabel;
        private RadioButton _radioA;
        private RadioButton _radioB;

        private void InitializeComponent()
        {
        Text = "Link Types";
        Size = new Size(440, 240);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        
        this.header = new Label();
            this.header.Name = "header";
        header.Text = "Which is the base type?";
        header.Location = new Point(12, 12);
        header.Size = new Size(400, 20);
        header.Font = LunaFonts.UiBold;
        
        _radioA = new RadioButton();
            this._radioA.Name = "radioA";
        _radioA.Text = (_a == null ? "Source.Type" : _a.FullyQualifiedName);
        _radioA.Location = new Point(24, 38);
        _radioA.Size = new Size(380, 22);
        _radioA.Font = LunaFonts.Ui;
        _radioA.Checked = true;
        
        _radioB = new RadioButton();
            this._radioB.Name = "radioB";
        _radioB.Text = (_b == null ? "Target.Type" : _b.FullyQualifiedName);
        _radioB.Location = new Point(24, 62);
        _radioB.Size = new Size(380, 22);
        _radioB.Font = LunaFonts.Ui;
        
        this.kindLabel = new Label();
            this.kindLabel.Name = "kindLabel";
        kindLabel.Text = "Relationship:";
        kindLabel.Location = new Point(12, 98);
        kindLabel.Size = new Size(100, 20);
        kindLabel.Font = LunaFonts.Ui;
        
        _kindCombo = new ComboBox();
            this._kindCombo.Name = "kindCombo";
        _kindCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _kindCombo.Location = new Point(120, 96);
        _kindCombo.Size = new Size(280, 22);
        _kindCombo.Font = LunaFonts.Sized(0.5f);
        _kindCombo.Items.Add("Inherits");
        _kindCombo.Items.Add("Implements");
        // Default based on the kinds:
        //   interface + anything            → Implements
        //   two interfaces / two classes    → Inherits
        bool eitherIsInterface = (_a != null && _a.TypeKind == TypeKind.Interface) || (_b != null && _b.TypeKind == TypeKind.Interface);
        bool bothInterfaces = _a != null && _b != null && _a.TypeKind == TypeKind.Interface && _b.TypeKind == TypeKind.Interface;
        _kindCombo.SelectedIndex = (eitherIsInterface && !bothInterfaces) ? 1 : 0;
        
        _ok = new Button();
            this._ok.Name = "ok";
        _ok.Text = "OK";
        _ok.DialogResult = DialogResult.None;
        _ok.Size = new Size(80, 28);
        _ok.Location = new Point(240, 160);
        _ok.Click += OnOk;
        
        _cancel = new Button();
            this._cancel.Name = "cancel";
        _cancel.Text = "Cancel";
        _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Size = new Size(80, 28);
        _cancel.Location = new Point(330, 160);
        
        AcceptButton = _ok;
        CancelButton = _cancel;
        
        Controls.Add(header);
        Controls.Add(_radioA);
        Controls.Add(_radioB);
        Controls.Add(kindLabel);
        Controls.Add(_kindCombo);
        Controls.Add(_ok);
        Controls.Add(_cancel);
        }
    }
}
