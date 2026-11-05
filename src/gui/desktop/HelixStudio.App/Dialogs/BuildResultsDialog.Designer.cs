namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Text;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

    public sealed partial class BuildResultsDialog
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

        private ContextMenuStrip listContextMenu;
        private Label _summaryLabel;
        private ListView _listView;
        private TabControl _tabControl;
        private TabPage _errorsTab;
        private TabPage _outputTab;
        private TextBox _outputTextBox;

        private void InitializeComponent()
        {
        Text = "Run Results";
        Size = new Size(900, 500);
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
        _summaryLabel.Text = "  Building...";
        
        _tabControl = new TabControl();
            this._tabControl.Name = "tabControl";
        _tabControl.Dock = DockStyle.Fill;
        _tabControl.Font = LunaFonts.Ui;
        
        // Errors & Warnings tab
        _errorsTab = new TabPage("Errors && Warnings");
            this._errorsTab.Name = "errorsTab";
        
        _listView = new ListView();
            this._listView.Name = "listView";
        _listView.View = View.Details;
        _listView.FullRowSelect = true;
        _listView.GridLines = true;
        _listView.Dock = DockStyle.Fill;
        _listView.Font = LunaFonts.Ui;
        
        _listView.Columns.Add("Severity", 70);
        _listView.Columns.Add("Code", 80);
        _listView.Columns.Add("Message", 380);
        _listView.Columns.Add("File", 200);
        _listView.Columns.Add("Line", 50);
        
        _listView.DoubleClick += OnItemDoubleClick;
        _listView.KeyDown += OnListViewKeyDown;
        
        this.listContextMenu = new ContextMenuStrip();
            this.listContextMenu.Name = "listContextMenu";
        listContextMenu.Items.Add("Copy Selected\tCtrl+C", null, this.OnCopySelectedRowsClick);
        listContextMenu.Items.Add("Copy All", null, this.OnCopyAllRowsClick);
        listContextMenu.Items.Add(new ToolStripSeparator());
        listContextMenu.Items.Add("Select All\tCtrl+A", null, this.OnSelectAllRowsClick);
        _listView.ContextMenuStrip = listContextMenu;
        
        _errorsTab.Controls.Add(_listView);
        
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
        
        _tabControl.TabPages.Add(_outputTab);
        _tabControl.TabPages.Add(_errorsTab);
        _tabControl.SelectedTab = _outputTab;
        
        Controls.Add(_tabControl);
        Controls.Add(_summaryLabel);
        }
    }
}
