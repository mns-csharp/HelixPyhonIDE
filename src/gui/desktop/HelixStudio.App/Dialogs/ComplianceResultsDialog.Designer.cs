namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;

    public partial class ComplianceResultsDialog
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

        private Button _applyButton;
        private Button _closeButton;
        private FlowLayoutPanel buttonPanel;
        private Label _summaryLabel;
        private ListView _resultsListView;

        private void InitializeComponent()
        {
        Text = "Compliance Update Results";
        Size = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(220, 220, 220);
        
        _summaryLabel = new Label();
            this._summaryLabel.Name = "summaryLabel";
        _summaryLabel.Dock = DockStyle.Top;
        _summaryLabel.Height = 50;
        _summaryLabel.Padding = new Padding(10);
        _summaryLabel.Font = LunaFonts.Sized(1f);
        _summaryLabel.ForeColor = Color.FromArgb(220, 220, 220);
        
        _resultsListView = new ListView();
            this._resultsListView.Name = "resultsListView";
        _resultsListView.Dock = DockStyle.Fill;
        _resultsListView.View = View.Details;
        _resultsListView.FullRowSelect = true;
        _resultsListView.GridLines = true;
        _resultsListView.BackColor = Color.FromArgb(37, 37, 38);
        _resultsListView.ForeColor = Color.FromArgb(220, 220, 220);
        
        _resultsListView.Columns.Add("Status", 80);
        _resultsListView.Columns.Add("Type", 100);
        _resultsListView.Columns.Add("Downstream Type", 150);
        _resultsListView.Columns.Add("Change", 120);
        _resultsListView.Columns.Add("Description", 220);
        
        this.buttonPanel = new FlowLayoutPanel();
            this.buttonPanel.Name = "buttonPanel";
        buttonPanel.Dock = DockStyle.Bottom;
        buttonPanel.Height = 45;
        buttonPanel.FlowDirection = FlowDirection.RightToLeft;
        buttonPanel.Padding = new Padding(5);
        buttonPanel.BackColor = Color.FromArgb(45, 45, 48);
        
        _closeButton = new Button();
            this._closeButton.Name = "closeButton";
        _closeButton.Text = "Close";
        _closeButton.Size = new Size(80, 30);
        _closeButton.FlatStyle = FlatStyle.Flat;
        _closeButton.BackColor = Color.FromArgb(63, 63, 70);
        _closeButton.ForeColor = Color.White;
        _closeButton.Click += new EventHandler(this.OnCloseButtonClick);
        
        _applyButton = new Button();
            this._applyButton.Name = "applyButton";
        _applyButton.Text = "Apply Auto-Fixes";
        _applyButton.Size = new Size(120, 30);
        _applyButton.FlatStyle = FlatStyle.Flat;
        _applyButton.BackColor = Color.FromArgb(0, 122, 204);
        _applyButton.ForeColor = Color.White;
        _applyButton.Click += OnApplyClicked;
        
        buttonPanel.Controls.Add(_closeButton);
        buttonPanel.Controls.Add(_applyButton);
        
        Controls.Add(_resultsListView);
        Controls.Add(_summaryLabel);
        Controls.Add(buttonPanel);
        }
    }
}
