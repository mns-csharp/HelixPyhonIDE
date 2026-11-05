namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;

    public sealed partial class ManagePackagesDialog
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

        private Button _installButton;
        private Button _removeButton;
        private Button _searchButton;
        private Button refreshButton;
        private Label versionLabel;
        private ListView _installedListView;
        private ListView _searchResultsListView;
        private Panel installedButtonPanel;
        private Panel installPanel;
        private Panel searchPanel;
        private Splitter outputSplitter;
        private TabControl _tabControl;
        private TabPage _installedTab;
        private TabPage _searchTab;
        private TextBox _outputTextBox;
        private TextBox _searchBox;
        private TextBox _versionTextBox;

        private void InitializeComponent()
        {
        Text = string.Format("Manage NuGet Packages - {0}", (_project == null ? "Project" : _project.Name));
        Size = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(550, 400);
        
        _tabControl = new TabControl();
            this._tabControl.Name = "tabControl";
        _tabControl.Dock = DockStyle.Fill;
        _tabControl.Font = LunaFonts.Ui;
        
        // ---- Installed tab ----
        _installedTab = new TabPage("Installed");
            this._installedTab.Name = "installedTab";
        
        _installedListView = new ListView();
            this._installedListView.Name = "installedListView";
        _installedListView.View = View.Details;
        _installedListView.FullRowSelect = true;
        _installedListView.Dock = DockStyle.Fill;
        _installedListView.Font = LunaFonts.Ui;
        _installedListView.Columns.Add("Package", 300);
        _installedListView.Columns.Add("Version", 120);
        
        this.installedButtonPanel = new Panel();
            this.installedButtonPanel.Name = "installedButtonPanel";
        installedButtonPanel.Dock = DockStyle.Bottom;
        installedButtonPanel.Height = 38;
        
        _removeButton = new Button();
            this._removeButton.Name = "removeButton";
        _removeButton.Text = "Remove Selected";
        _removeButton.Size = new Size(130, 28);
        _removeButton.Location = new Point(6, 5);
        _removeButton.Click += OnRemovePackage;
        
        this.refreshButton = new Button();
            this.refreshButton.Name = "refreshButton";
        refreshButton.Text = "Refresh";
        refreshButton.Size = new Size(80, 28);
        refreshButton.Location = new Point(142, 5);
        refreshButton.Click += new EventHandler(this.OnRefreshInstalledClick);
        
        installedButtonPanel.Controls.Add(_removeButton);
        installedButtonPanel.Controls.Add(refreshButton);
        
        _installedTab.Controls.Add(_installedListView);
        _installedTab.Controls.Add(installedButtonPanel);
        
        // ---- Search tab ----
        _searchTab = new TabPage("Browse");
            this._searchTab.Name = "searchTab";
        
        this.searchPanel = new Panel();
            this.searchPanel.Name = "searchPanel";
        searchPanel.Dock = DockStyle.Top;
        searchPanel.Height = 38;
        
        _searchBox = new TextBox();
            this._searchBox.Name = "searchBox";
        _searchBox.Location = new Point(6, 8);
        _searchBox.Size = new Size(400, 24);
        _searchBox.Font = LunaFonts.Ui;
        _searchBox.KeyDown += OnSearchKeyDown;
        
        _searchButton = new Button();
            this._searchButton.Name = "searchButton";
        _searchButton.Text = "Search";
        _searchButton.Size = new Size(80, 24);
        _searchButton.Location = new Point(412, 7);
        _searchButton.Click += OnSearchPackages;
        
        searchPanel.Controls.Add(_searchBox);
        searchPanel.Controls.Add(_searchButton);
        
        _searchResultsListView = new ListView();
            this._searchResultsListView.Name = "searchResultsListView";
        _searchResultsListView.View = View.Details;
        _searchResultsListView.FullRowSelect = true;
        _searchResultsListView.Dock = DockStyle.Fill;
        _searchResultsListView.Font = LunaFonts.Ui;
        _searchResultsListView.Columns.Add("Package", 300);
        _searchResultsListView.Columns.Add("Version", 100);
        _searchResultsListView.Columns.Add("Description", 250);
        _searchResultsListView.SelectedIndexChanged += OnSearchResultSelected;
        
        this.installPanel = new Panel();
            this.installPanel.Name = "installPanel";
        installPanel.Dock = DockStyle.Bottom;
        installPanel.Height = 38;
        
        this.versionLabel = new Label();
            this.versionLabel.Name = "versionLabel";
        versionLabel.Text = "Version:";
        versionLabel.Location = new Point(6, 10);
        versionLabel.AutoSize = true;
        versionLabel.Font = LunaFonts.Ui;
        
        _versionTextBox = new TextBox();
            this._versionTextBox.Name = "versionTextBox";
        _versionTextBox.Location = new Point(65, 7);
        _versionTextBox.Size = new Size(120, 24);
        _versionTextBox.Font = LunaFonts.Ui;
        _versionTextBox.Text = "";
        
        _installButton = new Button();
            this._installButton.Name = "installButton";
        _installButton.Text = "Install";
        _installButton.Size = new Size(80, 28);
        _installButton.Location = new Point(195, 5);
        _installButton.Click += OnInstallPackage;
        
        installPanel.Controls.Add(versionLabel);
        installPanel.Controls.Add(_versionTextBox);
        installPanel.Controls.Add(_installButton);
        
        _searchTab.Controls.Add(_searchResultsListView);
        _searchTab.Controls.Add(installPanel);
        _searchTab.Controls.Add(searchPanel);
        
        // ---- Output area ----
        _outputTextBox = new TextBox();
            this._outputTextBox.Name = "outputTextBox";
        _outputTextBox.Dock = DockStyle.Bottom;
        _outputTextBox.Height = 100;
        _outputTextBox.Multiline = true;
        _outputTextBox.ReadOnly = true;
        _outputTextBox.ScrollBars = ScrollBars.Vertical;
        _outputTextBox.Font = new Font("Consolas", 8.5f);
        _outputTextBox.BackColor = Color.White;
        _outputTextBox.ForeColor = Color.Black;
        
        this.outputSplitter = new Splitter();
        this.outputSplitter.Name = "outputSplitter";
        outputSplitter.Dock = DockStyle.Bottom;
        outputSplitter.Height = 4;
        
        _tabControl.TabPages.Add(_installedTab);
        _tabControl.TabPages.Add(_searchTab);
        
        Controls.Add(_tabControl);
        Controls.Add(outputSplitter);
        Controls.Add(_outputTextBox);
        }
    }
}
