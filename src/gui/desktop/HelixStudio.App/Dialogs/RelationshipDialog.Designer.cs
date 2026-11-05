namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model.Enums;

    public partial class RelationshipDialog
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

        private Button _cancelButton;
        private Button _okButton;
        private RadioButton _dependsOnRadio;
        private RadioButton _implementsRadio;
        private RadioButton _inheritsRadio;

        private GroupBox _kindGroup;

        private void InitializeComponent()
        {
        Text = "Select Relationship Kind";
        Size = new Size(320, 220);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        
        _kindGroup = new GroupBox();
            this._kindGroup.Name = "kindGroup";
        _kindGroup.Text = "Relationship Kind";
        _kindGroup.Location = new Point(12, 12);
        _kindGroup.Size = new Size(280, 110);
        
        _implementsRadio = new RadioButton();
            this._implementsRadio.Name = "implementsRadio";
        _implementsRadio.Text = "Implements (interface realization)";
        _implementsRadio.Location = new Point(16, 24);
        _implementsRadio.Size = new Size(250, 22);
        _implementsRadio.Checked = true;
        
        _inheritsRadio = new RadioButton();
            this._inheritsRadio.Name = "inheritsRadio";
        _inheritsRadio.Text = "Inherits (class inheritance)";
        _inheritsRadio.Location = new Point(16, 50);
        _inheritsRadio.Size = new Size(250, 22);
        
        _dependsOnRadio = new RadioButton();
            this._dependsOnRadio.Name = "dependsOnRadio";
        _dependsOnRadio.Text = "Depends On (dependency)";
        _dependsOnRadio.Location = new Point(16, 76);
        _dependsOnRadio.Size = new Size(250, 22);
        
        _kindGroup.Controls.Add(_implementsRadio);
        _kindGroup.Controls.Add(_inheritsRadio);
        _kindGroup.Controls.Add(_dependsOnRadio);
        
        _okButton = new Button();
            this._okButton.Name = "okButton";
        _okButton.Text = "OK";
        _okButton.DialogResult = DialogResult.OK;
        _okButton.Size = new Size(80, 28);
        _okButton.Location = new Point(120, 132);
        
        _cancelButton = new Button();
            this._cancelButton.Name = "cancelButton";
        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.Size = new Size(80, 28);
        _cancelButton.Location = new Point(208, 132);
        
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        
        Controls.Add(_kindGroup);
        Controls.Add(_okButton);
        Controls.Add(_cancelButton);
        }
    }
}
