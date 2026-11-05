namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed partial class ExtractInterfaceDialog
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
        private Button _selectAll;
        private Button _selectNone;
        private CheckedListBox _memberList;
        private Label memberLabel;
        private Label nameLabel;
        private Label sourceLabel;
        private TextBox _nameBox;

        private void InitializeComponent()
        {
        Text = "Extract Interface";
        Size = new Size(460, 420);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(360, 320);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        
        this.sourceLabel = new Label();
            this.sourceLabel.Name = "sourceLabel";
        sourceLabel.Text = "Source class: Source.Type";
        sourceLabel.Location = new Point(12, 10);
        sourceLabel.Size = new Size(420, 18);
        sourceLabel.Font = LunaFonts.Ui;
        
        this.nameLabel = new Label();
            this.nameLabel.Name = "nameLabel";
        nameLabel.Text = "Interface name:";
        nameLabel.Location = new Point(12, 38);
        nameLabel.Size = new Size(120, 20);
        nameLabel.Font = LunaFonts.Ui;
        
        _nameBox = new TextBox();
            this._nameBox.Name = "nameBox";
        _nameBox.Location = new Point(130, 36);
        _nameBox.Size = new Size(300, 22);
        _nameBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _nameBox.Font = LunaFonts.Sized(0.5f);
        _nameBox.Text = "ISource";
        
        this.memberLabel = new Label();
            this.memberLabel.Name = "memberLabel";
        memberLabel.Text = "Members to expose in the interface:";
        memberLabel.Location = new Point(12, 68);
        memberLabel.Size = new Size(400, 18);
        memberLabel.Font = LunaFonts.Ui;
        
        _memberList = new CheckedListBox();
            this._memberList.Name = "memberList";
        _memberList.Location = new Point(12, 90);
        _memberList.Size = new Size(420, 240);
        _memberList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _memberList.CheckOnClick = true;
        _memberList.Font = LunaFonts.Ui;
        _memberList.Items.Add("(no public, non-static methods or properties on this class)");
        _memberList.Enabled = false;
        
        _selectAll = new Button();
            this._selectAll.Name = "selectAll";
        _selectAll.Text = "Select All";
        _selectAll.Size = new Size(80, 28);
        _selectAll.Location = new Point(12, 338);
        _selectAll.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _selectAll.Click += new EventHandler(this.OnSelectAllClick);
        
        _selectNone = new Button();
            this._selectNone.Name = "selectNone";
        _selectNone.Text = "Select None";
        _selectNone.Size = new Size(90, 28);
        _selectNone.Location = new Point(98, 338);
        _selectNone.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _selectNone.Click += new EventHandler(this.OnSelectNoneClick);
        
        _ok = new Button();
            this._ok.Name = "ok";
        _ok.Text = "OK";
        _ok.DialogResult = DialogResult.None;
        _ok.Size = new Size(80, 28);
        _ok.Location = new Point(262, 338);
        _ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _ok.Click += OnOk;
        
        _cancel = new Button();
            this._cancel.Name = "cancel";
        _cancel.Text = "Cancel";
        _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Size = new Size(80, 28);
        _cancel.Location = new Point(350, 338);
        _cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        
        AcceptButton = _ok;
        CancelButton = _cancel;
        
        Controls.Add(sourceLabel);
        Controls.Add(nameLabel);
        Controls.Add(_nameBox);
        Controls.Add(memberLabel);
        Controls.Add(_memberList);
        Controls.Add(_selectAll);
        Controls.Add(_selectNone);
        Controls.Add(_ok);
        Controls.Add(_cancel);
        }
    }
}
