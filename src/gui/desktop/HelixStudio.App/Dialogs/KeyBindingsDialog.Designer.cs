namespace HelixPythonNamespace.App.Dialogs
{
    using System.ComponentModel;
    using System.Drawing;
    using System.Windows.Forms;

    partial class KeyBindingsDialog
    {
        private IContainer components = null;
        private Panel presetPanel;
        private Label presetLabel;
        private ComboBox _presetCombo;
        private Button applyPresetButton;
        private Label filterLabel;
        private TextBox _filterBox;
        private Label _hint;
        private DataGridView _grid;
        private DataGridViewTextBoxColumn commandColumn;
        private DataGridViewTextBoxColumn shortcutColumn;
        private Panel footerPanel;
        private Button _closeButton;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.presetPanel = new Panel();
            this.presetLabel = new Label();
            this._presetCombo = new ComboBox();
            this.applyPresetButton = new Button();
            this.filterLabel = new Label();
            this._filterBox = new TextBox();
            this._hint = new Label();
            this._grid = new DataGridView();
            this.commandColumn = new DataGridViewTextBoxColumn();
            this.shortcutColumn = new DataGridViewTextBoxColumn();
            this.footerPanel = new Panel();
            this._closeButton = new Button();
            this.presetPanel.SuspendLayout();
            ((ISupportInitialize)(this._grid)).BeginInit();
            this.footerPanel.SuspendLayout();
            this.SuspendLayout();
            // presetPanel
            this.presetPanel.Controls.Add(this._filterBox);
            this.presetPanel.Controls.Add(this.filterLabel);
            this.presetPanel.Controls.Add(this.applyPresetButton);
            this.presetPanel.Controls.Add(this._presetCombo);
            this.presetPanel.Controls.Add(this.presetLabel);
            this.presetPanel.Dock = DockStyle.Top;
            this.presetPanel.Location = new Point(0, 0);
            this.presetPanel.Name = "presetPanel";
            this.presetPanel.Padding = new Padding(8, 6, 8, 6);
            this.presetPanel.Size = new Size(604, 40);
            this.presetPanel.TabIndex = 0;
            // presetLabel
            this.presetLabel.AutoSize = true;
            this.presetLabel.Location = new Point(8, 12);
            this.presetLabel.Name = "presetLabel";
            this.presetLabel.Size = new Size(40, 13);
            this.presetLabel.TabIndex = 0;
            this.presetLabel.Text = "Preset:";
            // presetCombo
            this._presetCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            this._presetCombo.FormattingEnabled = true;
            this._presetCombo.Items.AddRange(new object[] { "Windows", "Mac-style", "Linux-style" });
            this._presetCombo.Location = new Point(68, 8);
            this._presetCombo.Name = "presetCombo";
            this._presetCombo.Size = new Size(160, 21);
            this._presetCombo.TabIndex = 1;
            // applyPresetButton
            this.applyPresetButton.Location = new Point(238, 7);
            this.applyPresetButton.Name = "applyPresetButton";
            this.applyPresetButton.Size = new Size(120, 26);
            this.applyPresetButton.TabIndex = 2;
            this.applyPresetButton.Text = "Apply Preset";
            this.applyPresetButton.UseVisualStyleBackColor = true;
            this.applyPresetButton.Click += new System.EventHandler(this.OnApplyPresetClick);
            // filterLabel
            this.filterLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this.filterLabel.AutoSize = true;
            this.filterLabel.Location = new Point(378, 12);
            this.filterLabel.Name = "filterLabel";
            this.filterLabel.Size = new Size(36, 13);
            this.filterLabel.Text = "Filter:";
            // filterBox
            this._filterBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this._filterBox.Location = new Point(420, 8);
            this._filterBox.Name = "filterBox";
            this._filterBox.Size = new Size(176, 21);
            this._filterBox.TabIndex = 3;
            this._filterBox.TextChanged += new System.EventHandler(this.OnFilterChanged);
            // hint
            this._hint.BackColor = Color.FromArgb(245, 245, 245);
            this._hint.Dock = DockStyle.Top;
            this._hint.ForeColor = Color.FromArgb(90, 90, 90);
            this._hint.Location = new Point(0, 40);
            this._hint.Name = "hintLabel";
            this._hint.Padding = new Padding(8, 4, 8, 4);
            this._hint.Size = new Size(604, 36);
            this._hint.TabIndex = 1;
            this._hint.Text = "Double-click a command to change its shortcut. Right-click to restore its default.";
            this._hint.TextAlign = ContentAlignment.MiddleLeft;
            // grid
            this._grid.AllowUserToAddRows = false;
            this._grid.AllowUserToDeleteRows = false;
            this._grid.AllowUserToResizeRows = false;
            this._grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            this._grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this._grid.Columns.AddRange(new DataGridViewColumn[] { this.commandColumn, this.shortcutColumn });
            this._grid.Dock = DockStyle.Fill;
            this._grid.Location = new Point(0, 76);
            this._grid.MultiSelect = false;
            this._grid.Name = "keyBindingsGrid";
            this._grid.ReadOnly = true;
            this._grid.RowHeadersVisible = false;
            this._grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            this._grid.Size = new Size(604, 321);
            this._grid.TabIndex = 2;
            this._grid.CellDoubleClick += new DataGridViewCellEventHandler(this.OnCellDoubleClick);
            this._grid.MouseUp += new MouseEventHandler(this.OnGridMouseUp);
            // columns
            this.commandColumn.DataPropertyName = "Command";
            this.commandColumn.FillWeight = 65F;
            this.commandColumn.HeaderText = "Command";
            this.commandColumn.Name = "commandColumn";
            this.commandColumn.ReadOnly = true;
            this.shortcutColumn.DataPropertyName = "Shortcut";
            this.shortcutColumn.FillWeight = 35F;
            this.shortcutColumn.HeaderText = "Shortcut";
            this.shortcutColumn.Name = "shortcutColumn";
            this.shortcutColumn.ReadOnly = true;
            // footer
            this.footerPanel.Controls.Add(this._closeButton);
            this.footerPanel.Dock = DockStyle.Bottom;
            this.footerPanel.Location = new Point(0, 397);
            this.footerPanel.Name = "footerPanel";
            this.footerPanel.Size = new Size(604, 44);
            this.footerPanel.TabIndex = 3;
            this._closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            this._closeButton.DialogResult = DialogResult.OK;
            this._closeButton.Location = new Point(502, 8);
            this._closeButton.Name = "closeButton";
            this._closeButton.Size = new Size(90, 28);
            this._closeButton.TabIndex = 0;
            this._closeButton.Text = "Close";
            this._closeButton.UseVisualStyleBackColor = true;
            // form
            this.AcceptButton = this._closeButton;
            // Escape closes the dialog too — rebinds already persisted as they were made, so there is
            // nothing to revert; Close (DialogResult.OK) is the only outcome either way.
            this.CancelButton = this._closeButton;
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(604, 441);
            this.Controls.Add(this._grid);
            this.Controls.Add(this.footerPanel);
            this.Controls.Add(this._hint);
            this.Controls.Add(this.presetPanel);
            this.MinimumSize = new Size(480, 320);
            this.Name = "KeyBindingsDialog";
            this.ShowInTaskbar = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "Keybindings";
            this.presetPanel.ResumeLayout(false);
            this.presetPanel.PerformLayout();
            ((ISupportInitialize)(this._grid)).EndInit();
            this.footerPanel.ResumeLayout(false);
            this.ResumeLayout(false);
        }
    }
}
