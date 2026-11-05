namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Text;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;

    public sealed partial class AnalysisResultsDialog
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

        private ContextMenuStrip contextMenu;
        private Label _summaryLabel;
        private ListView _listView;
        private TabControl _tabControl;
        private TabPage _outputTab;
        private TabPage _resultsTab;
        private TextBox _outputTextBox;

        private void InitializeComponent()
        {
        Text = "Analysis Results";
        Size = new Size(800, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 300);
        
        _summaryLabel = new Label();
            this._summaryLabel.Name = "summaryLabel";
        _summaryLabel.Dock = DockStyle.Top;
        _summaryLabel.Height = 30;
        _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
        _summaryLabel.Padding = new Padding(8, 0, 0, 0);
        _summaryLabel.Font = LunaFonts.UiBold;
        _summaryLabel.BackColor = Color.FromArgb(61, 143, 240);
        
        _tabControl = new TabControl();
            this._tabControl.Name = "tabControl";
        _tabControl.Dock = DockStyle.Fill;
        _tabControl.Font = LunaFonts.Ui;
        
        // Results tab
        _resultsTab = new TabPage("Results");
            this._resultsTab.Name = "resultsTab";
        
        _listView = new ListView();
            this._listView.Name = "listView";
        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines = true;
        _listView.Dock = DockStyle.Fill;
        _listView.Font = LunaFonts.Ui;
        
        _listView.Columns.Add("Severity", 80);
        _listView.Columns.Add("Rule", 160);
        _listView.Columns.Add("Message", 320);
        _listView.Columns.Add("Type", 180);
        
        _listView.DoubleClick += OnItemDoubleClick;
        _listView.KeyDown += OnListViewKeyDown;
        
        this.contextMenu = new ContextMenuStrip();
            this.contextMenu.Name = "contextMenu";
        contextMenu.Items.Add("Copy Selected\tCtrl+C", null, this.OnCopySelectedClick);
        contextMenu.Items.Add("Copy All", null, this.OnCopyAllClick);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Select All\tCtrl+A", null, this.OnSelectAllClick);
        _listView.ContextMenuStrip = contextMenu;
        
        _resultsTab.Controls.Add(_listView);
        
        // Output tab
        _outputTab = new TabPage("Output");
            this._outputTab.Name = "outputTab";
        
        _outputTextBox = new TextBox();
            this._outputTextBox.Name = "outputTextBox";
        _outputTextBox.Dock = DockStyle.Fill;
        _outputTextBox.Multiline = true;
        _outputTextBox.ReadOnly = true;
        _outputTextBox.ScrollBars = ScrollBars.Both;
        _outputTextBox.WordWrap = false;
        _outputTextBox.Font = new Font("Consolas", 9f);
        _outputTextBox.BackColor = Color.White;
        _outputTextBox.ForeColor = Color.Black;
        
        _outputTab.Controls.Add(_outputTextBox);
        
        _tabControl.TabPages.Add(_resultsTab);
        _tabControl.TabPages.Add(_outputTab);
        _tabControl.SelectedTab = _resultsTab;
        
        Controls.Add(_tabControl);
        Controls.Add(_summaryLabel);
        }
    }
}
