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

    public sealed partial class PullUpMemberDialog
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
        private CheckedListBox _memberList;
        private ComboBox _baseCombo;
        private Label baseLabel;
        private Label memberLabel;
        private Label sourceLabel;
        private Label warn;

        private void InitializeComponent()
        {
        Text = "Pull Up Member";
        Size = new Size(460, 420);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(360, 320);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        
        this.sourceLabel = new Label();
            this.sourceLabel.Name = "sourceLabel";
        sourceLabel.Text = "Derived class: Derived.Type";
        sourceLabel.Location = new Point(12, 10);
        sourceLabel.Size = new Size(420, 18);
        sourceLabel.Font = LunaFonts.Ui;
        
        this.baseLabel = new Label();
            this.baseLabel.Name = "baseLabel";
        baseLabel.Text = "Pull up into:";
        baseLabel.Location = new Point(12, 38);
        baseLabel.Size = new Size(120, 20);
        baseLabel.Font = LunaFonts.Ui;
        
        _baseCombo = new ComboBox();
            this._baseCombo.Name = "baseCombo";
        _baseCombo.Location = new Point(130, 36);
        _baseCombo.Size = new Size(300, 22);
        _baseCombo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _baseCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _baseCombo.Font = LunaFonts.Sized(0.5f);
        
        this.memberLabel = new Label();
            this.memberLabel.Name = "memberLabel";
        memberLabel.Text = "Members to pull up:";
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
        
        _memberList.Items.Add("(no eligible instance members on this class)");
        _memberList.Enabled = false;
        
        _ok = new Button();
            this._ok.Name = "ok";
        _ok.Text = "OK";
        _ok.DialogResult = DialogResult.None;
        _ok.Size = new Size(80, 28);
        _ok.Location = new Point(262, 338);
        _ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _ok.Click += OnOk;
        _ok.Enabled = false;
        
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
        Controls.Add(baseLabel);
        Controls.Add(_baseCombo);
        Controls.Add(memberLabel);
        Controls.Add(_memberList);
        Controls.Add(_ok);
        Controls.Add(_cancel);
        
        this.warn = new Label();
            this.warn.Name = "warn";
        warn.Text = "This class has no base class in the solution to pull members into.";
        warn.ForeColor = Color.FromArgb(180, 60, 60);
        warn.Location = new Point(12, 338);
        warn.Size = new Size(240, 28);
        warn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(warn);
        }
    }
}
