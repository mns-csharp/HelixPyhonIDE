namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;

    public sealed partial class AddProjectReferenceDialog
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
        private CheckedListBox _list;
        private Label _header;

        private void InitializeComponent()
        {
        Text = "Add Project Reference";
        Size = new Size(460, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        
        _header = new Label();
        _header.Text = "Select projects to reference from \"TargetProject\":";
        _header.Location = new Point(16, 12);
        _header.Name = "headerLabel";
        _header.Size = new Size(420, 20);
        
        _list = new CheckedListBox();
            this._list.Name = "list";
        
        _list.Location = new Point(16, 40);
        
        _list.Size = new Size(420, 290);
        
        _list.CheckOnClick = true;
        
        _list.IntegralHeight = false;
        
        
        _okButton = new Button();
            this._okButton.Name = "okButton";
        
        _okButton.Text = "Add";
        
        _okButton.Size = new Size(90, 28);
        
        _okButton.Location = new Point(246, 346);
        
        _okButton.DialogResult = DialogResult.OK;
        _okButton.Click += OnOkClick;
        
        _cancelButton = new Button();
            this._cancelButton.Name = "cancelButton";
        
        _cancelButton.Text = "Cancel";
        
        _cancelButton.Size = new Size(90, 28);
        
        _cancelButton.Location = new Point(346, 346);
        
        _cancelButton.DialogResult = DialogResult.Cancel;
        
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        
        Controls.Add(_header);
        Controls.Add(_list);
        Controls.Add(_okButton);
        Controls.Add(_cancelButton);
        
        }
    }
}
