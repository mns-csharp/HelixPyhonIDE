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

    public sealed partial class PushDownMemberDialog
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
        private CheckedListBox _derivedList;
        private CheckedListBox _memberList;
        private Label derivedLabel;
        private Label memberLabel;
        private Label sourceLabel;

        private void InitializeComponent()
        {
        Text = "Push Down Member";
        Size = new Size(520, 460);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(400, 360);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        
        this.sourceLabel = new Label();
            this.sourceLabel.Name = "sourceLabel";
        sourceLabel.Text = "Base class: Base.Type";
        sourceLabel.Location = new Point(12, 10);
        sourceLabel.Size = new Size(480, 18);
        sourceLabel.Font = LunaFonts.Ui;
        
        this.derivedLabel = new Label();
            this.derivedLabel.Name = "derivedLabel";
        derivedLabel.Text = "Target derived classes:";
        derivedLabel.Location = new Point(12, 38);
        derivedLabel.Size = new Size(240, 20);
        derivedLabel.Font = LunaFonts.Ui;
        
        _derivedList = new CheckedListBox();
            this._derivedList.Name = "derivedList";
        _derivedList.Location = new Point(12, 60);
        _derivedList.Size = new Size(480, 130);
        _derivedList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _derivedList.CheckOnClick = true;
        _derivedList.Font = LunaFonts.Ui;
        _derivedList.Items.Add("(no derived classes found in this solution)");
        _derivedList.Enabled = false;
        
        this.memberLabel = new Label();
            this.memberLabel.Name = "memberLabel";
        memberLabel.Text = "Members to push down:";
        memberLabel.Location = new Point(12, 200);
        memberLabel.Size = new Size(240, 18);
        memberLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        memberLabel.Font = LunaFonts.Ui;
        
        _memberList = new CheckedListBox();
            this._memberList.Name = "memberList";
        _memberList.Location = new Point(12, 222);
        _memberList.Size = new Size(480, 150);
        _memberList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _memberList.CheckOnClick = true;
        _memberList.Font = LunaFonts.Ui;
        _memberList.Items.Add("(no eligible instance members on this class)");
        _memberList.Enabled = false;
        
        _ok = new Button();
            this._ok.Name = "ok";
        _ok.Text = "OK";
        _ok.DialogResult = DialogResult.None;
        _ok.Size = new Size(80, 28);
        _ok.Location = new Point(322, 380);
        _ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _ok.Click += OnOk;
        _ok.Enabled = false;
        
        _cancel = new Button();
            this._cancel.Name = "cancel";
        _cancel.Text = "Cancel";
        _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Size = new Size(80, 28);
        _cancel.Location = new Point(410, 380);
        _cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        
        AcceptButton = _ok;
        CancelButton = _cancel;
        
        Controls.Add(sourceLabel);
        Controls.Add(derivedLabel);
        Controls.Add(_derivedList);
        Controls.Add(memberLabel);
        Controls.Add(_memberList);
        Controls.Add(_ok);
        Controls.Add(_cancel);
        }
    }
}
