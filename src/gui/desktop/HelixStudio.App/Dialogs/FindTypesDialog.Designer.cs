namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;

    public sealed partial class FindTypesDialog
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
        private Label _countLabel;
        private Label filterLabel;
        private ListView _results;
        private TextBox _filterBox;

        private void InitializeComponent()
        {
        Text = "Find Type";
        Size = new Size(560, 460);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(400, 300);
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        
        this.filterLabel = new Label();
            this.filterLabel.Name = "filterLabel";
        filterLabel.Text = "Type name contains:";
        filterLabel.Location = new Point(12, 12);
        filterLabel.Size = new Size(140, 18);
        filterLabel.Font = LunaFonts.Ui;
        
        _filterBox = new TextBox();
            this._filterBox.Name = "filterBox";
        _filterBox.Location = new Point(12, 32);
        _filterBox.Size = new Size(520, 24);
        _filterBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _filterBox.Font = LunaFonts.Sized(0.5f);
        _filterBox.TextChanged += OnFilterChanged;
        _filterBox.KeyDown += OnFilterKeyDown;
        
        _results = new ListView();
            this._results.Name = "results";
        _results.Location = new Point(12, 64);
        _results.Size = new Size(520, 320);
        _results.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _results.View = View.Details;
        _results.FullRowSelect = true;
        _results.MultiSelect = false;
        _results.HideSelection = false;
        _results.Font = LunaFonts.Ui;
        _results.Columns.Add("Name", 150);
        _results.Columns.Add("Kind", 90);
        _results.Columns.Add("Namespace", 160);
        _results.Columns.Add("Project", 110);
        _results.DoubleClick += new EventHandler(this.OnAcceptSelectionClick);
        _results.KeyDown += OnResultsKeyDown;
        
        _countLabel = new Label();
            this._countLabel.Name = "countLabel";
        _countLabel.Location = new Point(12, 392);
        _countLabel.Size = new Size(360, 18);
        _countLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _countLabel.Font = LunaFonts.Sized(-0.5f);
        _countLabel.ForeColor = Color.FromArgb(120, 120, 120);
        
        _okButton = new Button();
            this._okButton.Name = "okButton";
        _okButton.Text = "Go To";
        _okButton.DialogResult = DialogResult.None;
        _okButton.Size = new Size(80, 28);
        _okButton.Location = new Point(362, 390);
        _okButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _okButton.Click += new EventHandler(this.OnAcceptSelectionClick);
        
        _cancelButton = new Button();
            this._cancelButton.Name = "cancelButton";
        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.Size = new Size(80, 28);
        _cancelButton.Location = new Point(452, 390);
        _cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        
        Controls.Add(filterLabel);
        Controls.Add(_filterBox);
        Controls.Add(_results);
        Controls.Add(_countLabel);
        Controls.Add(_okButton);
        Controls.Add(_cancelButton);
        }
    }
}
