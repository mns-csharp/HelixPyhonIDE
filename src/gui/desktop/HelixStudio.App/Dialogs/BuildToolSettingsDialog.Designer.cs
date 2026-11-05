namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.App.Services;

    public sealed partial class BuildToolSettingsDialog
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
        private Button _refreshButton;
        private Label _infoLabel;
        private ListView _sdkListView;
        private Panel buttonPanel;
        private Panel _nativeToolchainPanel;
        private ComboBox _nativeToolchainCombo;
        private Label _nativeToolchainLabel;
        private TextBox _gccPathTextBox;
        private Button _gccBrowseButton;
        private Label _nativeStatusLabel;
        private Label _gccPathLabel;
        private Label _msvcPathLabel;
        private TextBox _msvcPathTextBox;
        private Button _msvcBrowseButton;
        private Label _cxxStandardLabel;
        private ComboBox _cxxStandardCombo;
        private Label _cudaStandardLabel;
        private ComboBox _cudaStandardCombo;

        private void InitializeComponent()
        {
        Text = "Run Tool Settings";
        Size = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(400, 250);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        
        _infoLabel = new Label();
            this._infoLabel.Name = "infoLabel";
        _infoLabel.Text = "Select the default tool / interpreter used when Run executes a project:";
        _infoLabel.Dock = DockStyle.Top;
        _infoLabel.Height = 30;
        _infoLabel.Padding = new Padding(8, 8, 0, 0);
        _infoLabel.Font = LunaFonts.Ui;

        _nativeToolchainPanel = new Panel();
        _nativeToolchainPanel.Name = "nativeToolchainPanel";
        _nativeToolchainPanel.Dock = DockStyle.Top;
        _nativeToolchainPanel.Height = 205;
        _nativeToolchainPanel.Padding = new Padding(8, 5, 8, 5);
        _nativeToolchainLabel = new Label();
        _nativeToolchainLabel.Name = "nativeToolchainLabel";
        _nativeToolchainLabel.AutoSize = false;
        _nativeToolchainLabel.Dock = DockStyle.Top;
        _nativeToolchainLabel.Height = 25;
        _nativeToolchainLabel.Text = "Native toolchain";
        _nativeToolchainCombo = new ComboBox();
        _nativeToolchainCombo.Name = "nativeToolchainCombo";
        _nativeToolchainCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _nativeToolchainCombo.Items.AddRange(new object[] {
            "Auto", "MSVC", "GCC", "Helix CUDA/C++ Emulator" });
        _nativeToolchainCombo.Dock = DockStyle.Top;
        _nativeToolchainCombo.AccessibleName = "Native CMake toolchain";
        _nativeToolchainCombo.Width = 220;
        _nativeToolchainCombo.Dock = DockStyle.None;
        _nativeToolchainCombo.Location = new Point(8, 28);
        _nativeToolchainCombo.SelectedIndexChanged += OnNativeToolchainChanged;
        _gccPathTextBox = new TextBox();
        _gccPathTextBox.Name = "gccPathTextBox";
        _gccPathLabel = new Label();
        _gccPathLabel.Name = "gccPathLabel";
        _gccPathLabel.Text = "GNU C++ compiler";
        _gccPathLabel.Location = new Point(240, 7);
        _gccPathLabel.Size = new Size(250, 20);
        _gccPathTextBox.Location = new Point(240, 28);
        _gccPathTextBox.Width = 335;
        _gccPathTextBox.AccessibleName = "GNU C++ compiler path";
        _gccBrowseButton = new Button();
        _gccBrowseButton.Name = "gccBrowseButton";
        _gccBrowseButton.Text = "Browse...";
        _gccBrowseButton.Location = new Point(582, 26);
        _gccBrowseButton.Width = 75;
        _gccBrowseButton.Click += OnBrowseGcc;
        _nativeStatusLabel = new Label();
        _nativeStatusLabel.Name = "nativeStatusLabel";
        _msvcPathLabel = new Label();
        _msvcPathLabel.Name = "msvcPathLabel";
        _msvcPathLabel.Text = "MSVC/CUDA x64 environment";
        _msvcPathLabel.Location = new Point(8, 60);
        _msvcPathLabel.Size = new Size(155, 20);
        _msvcPathTextBox = new TextBox();
        _msvcPathTextBox.Name = "msvcPathTextBox";
        _msvcPathTextBox.Location = new Point(170, 58);
        _msvcPathTextBox.Width = 405;
        _msvcPathTextBox.AccessibleName = "MSVC x64 environment script";
        _msvcBrowseButton = new Button();
        _msvcBrowseButton.Name = "msvcBrowseButton";
        _msvcBrowseButton.Text = "Browse...";
        _msvcBrowseButton.Location = new Point(582, 56);
        _msvcBrowseButton.Width = 75;
        _msvcBrowseButton.Click += OnBrowseMsvc;
        _cxxStandardLabel = new Label();
        _cxxStandardLabel.Name = "cxxStandardLabel";
        _cxxStandardLabel.Text = "C++ dialect";
        _cxxStandardLabel.Location = new Point(8, 91);
        _cxxStandardLabel.Size = new Size(100, 20);
        _cxxStandardCombo = new ComboBox();
        _cxxStandardCombo.Name = "cxxStandardCombo";
        _cxxStandardCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _cxxStandardCombo.Items.AddRange(new object[] {
            "C++98", "C++11", "C++14", "C++17", "C++20", "C++23", "C++26" });
        _cxxStandardCombo.Location = new Point(8, 111);
        _cxxStandardCombo.Width = 150;
        _cudaStandardLabel = new Label();
        _cudaStandardLabel.Name = "cudaStandardLabel";
        _cudaStandardLabel.Text = "CUDA C++ dialect";
        _cudaStandardLabel.Location = new Point(170, 91);
        _cudaStandardLabel.Size = new Size(130, 20);
        _cudaStandardCombo = new ComboBox();
        _cudaStandardCombo.Name = "cudaStandardCombo";
        _cudaStandardCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _cudaStandardCombo.Items.AddRange(new object[] {
            "CUDA C++03", "CUDA C++11", "CUDA C++14", "CUDA C++17",
            "CUDA C++20", "CUDA C++23", "CUDA C++26" });
        _cudaStandardCombo.Location = new Point(170, 111);
        _cudaStandardCombo.Width = 150;
        _nativeStatusLabel.Location = new Point(8, 143);
        _nativeStatusLabel.Size = new Size(650, 54);
        _nativeStatusLabel.AutoEllipsis = true;
        _nativeStatusLabel.Text = "Detected native compilers";
        _nativeStatusLabel.AccessibleName = "Detected native compiler status";
        _nativeToolchainPanel.Controls.Add(_msvcBrowseButton);
        _nativeToolchainPanel.Controls.Add(_cudaStandardCombo);
        _nativeToolchainPanel.Controls.Add(_cudaStandardLabel);
        _nativeToolchainPanel.Controls.Add(_cxxStandardCombo);
        _nativeToolchainPanel.Controls.Add(_cxxStandardLabel);
        _nativeToolchainPanel.Controls.Add(_msvcPathTextBox);
        _nativeToolchainPanel.Controls.Add(_msvcPathLabel);
        _nativeToolchainPanel.Controls.Add(_nativeStatusLabel);
        _nativeToolchainPanel.Controls.Add(_gccBrowseButton);
        _nativeToolchainPanel.Controls.Add(_gccPathTextBox);
        _nativeToolchainPanel.Controls.Add(_gccPathLabel);
        _nativeToolchainPanel.Controls.Add(_nativeToolchainCombo);
        _nativeToolchainPanel.Controls.Add(_nativeToolchainLabel);
        
        _sdkListView = new ListView();
            this._sdkListView.Name = "sdkListView";
        _sdkListView.View = View.Details;
        _sdkListView.FullRowSelect = true;
        _sdkListView.MultiSelect = false;
        _sdkListView.CheckBoxes = true;
        _sdkListView.Dock = DockStyle.Fill;
        _sdkListView.Font = LunaFonts.Ui;
        
        _sdkListView.Columns.Add("Version", 150);
        _sdkListView.Columns.Add("Path", 300);
        
        _sdkListView.ItemCheck += OnItemCheck;
        
        this.buttonPanel = new Panel();
            this.buttonPanel.Name = "buttonPanel";
        buttonPanel.Dock = DockStyle.Bottom;
        buttonPanel.Height = 45;
        
        _refreshButton = new Button();
            this._refreshButton.Name = "refreshButton";
        _refreshButton.Text = "Refresh";
        _refreshButton.Size = new Size(80, 28);
        _refreshButton.Location = new Point(10, 8);
        _refreshButton.Click += OnRefresh;
        
        _okButton = new Button();
            this._okButton.Name = "okButton";
        _okButton.Text = "OK";
        _okButton.DialogResult = DialogResult.OK;
        _okButton.Size = new Size(80, 28);
        _okButton.Location = new Point(Size.Width - 190, 8);
        
        _cancelButton = new Button();
            this._cancelButton.Name = "cancelButton";
        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.Size = new Size(80, 28);
        _cancelButton.Location = new Point(Size.Width - 100, 8);
        
        buttonPanel.Controls.Add(_refreshButton);
        buttonPanel.Controls.Add(_okButton);
        buttonPanel.Controls.Add(_cancelButton);
        
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        
        Controls.Add(_sdkListView);
        Controls.Add(_nativeToolchainPanel);
        Controls.Add(_infoLabel);
        Controls.Add(buttonPanel);
        }
    }
}
