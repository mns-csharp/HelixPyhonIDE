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
    using HelixPythonNamespace.Workspace.Services;

    public sealed partial class TypeModelEditorDialog
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

        private Button _addGenericButton;
        private Button _addMethodButton;
        private Button _addPropertyButton;
        private Button _applyButton;
        private Button _cancelButton;
        private Button _editMemberButton;
        private Button _removeGenericButton;
        private Button _removeMemberButton;
        private ComboBox _typeKindCombo;
        private DataGridView _genericsGrid;
        private DataGridView _membersGrid;
        private Label _nameLabel;
        private Label _namespaceLabel;
        private Label _sourceFileLabel;
        private Label _typeKindLabel;
        private MenuStrip _menuStrip;
        private TextBox _namespaceTextBox;
        private TextBox _nameTextBox;
        private TextBox _sourceFileTextBox;
        private ToolStripMenuItem _redoMenuItem;
        private ToolStripMenuItem _undoMenuItem;
        private ToolStripMenuItem editMenu;

        private DataGridViewComboBoxColumn accessColumn;
        private DataGridViewComboBoxColumn gpVarianceColumn;
        private DataGridViewComboBoxColumn kindColumn;
        private DataGridViewComboBoxColumn returnTypeColumn;
        private DataGridViewTextBoxColumn gpConstraintsColumn;
        private DataGridViewTextBoxColumn gpNameColumn;
        private DataGridViewTextBoxColumn memberNameColumn;
        private DataGridViewTextBoxColumn modifiersColumn;
        private DataGridViewTextBoxColumn paramsColumn;

        private GroupBox _membersGroup;
        private GroupBox _genericsGroup;

        private void InitializeComponent()
        {
        Text = "Edit Type";
        Size = new Size(720, 700);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(600, 500);
        
        // Menu strip with Edit menu for undo/redo
        _menuStrip = new MenuStrip();
            this._menuStrip.Name = "menuStrip";
        this.editMenu = new ToolStripMenuItem("&Edit");
            this.editMenu.Name = "editMenu";
        _undoMenuItem = new ToolStripMenuItem("&Undo", null, OnUndoClick);
            this._undoMenuItem.Name = "undoMenuItem";
        _undoMenuItem.ShortcutKeys = Keys.Control | Keys.Z;
        _undoMenuItem.Enabled = false;
        _redoMenuItem = new ToolStripMenuItem("&Redo", null, OnRedoClick);
            this._redoMenuItem.Name = "redoMenuItem";
        _redoMenuItem.ShortcutKeys = Keys.Control | Keys.Y;
        _redoMenuItem.Enabled = false;
        editMenu.DropDownItems.Add(_undoMenuItem);
        editMenu.DropDownItems.Add(_redoMenuItem);
        _menuStrip.Items.Add(editMenu);
        MainMenuStrip = _menuStrip;
        Controls.Add(_menuStrip);
        
        int labelWidth = 100;
        int leftMargin = 12;
        int controlLeft = leftMargin + labelWidth + 4;
        int controlWidth = 560;
        int currentY = 16 + _menuStrip.Height;
        
        // Name
        _nameLabel = new Label();
            this._nameLabel.Name = "nameLabel";
        _nameLabel.Text = "Name:";
        _nameLabel.Location = new Point(leftMargin, currentY + 3);
        _nameLabel.Size = new Size(labelWidth, 20);
        
        _nameTextBox = new TextBox();
            this._nameTextBox.Name = "nameTextBox";
        
        _nameTextBox.Location = new Point(controlLeft, currentY);
        
        _nameTextBox.Size = new Size(controlWidth, 22);
        _nameTextBox.TextChanged += OnValueChanged;
        
        currentY += 30;
        
        // Namespace
        _namespaceLabel = new Label();
            this._namespaceLabel.Name = "namespaceLabel";
        _namespaceLabel.Text = "Namespace:";
        _namespaceLabel.Location = new Point(leftMargin, currentY + 3);
        _namespaceLabel.Size = new Size(labelWidth, 20);
        
        _namespaceTextBox = new TextBox();
            this._namespaceTextBox.Name = "namespaceTextBox";
        
        _namespaceTextBox.Location = new Point(controlLeft, currentY);
        
        _namespaceTextBox.Size = new Size(controlWidth, 22);
        _namespaceTextBox.TextChanged += OnValueChanged;
        
        currentY += 30;
        
        // TypeKind
        _typeKindLabel = new Label();
            this._typeKindLabel.Name = "typeKindLabel";
        _typeKindLabel.Text = "Type Kind:";
        _typeKindLabel.Location = new Point(leftMargin, currentY + 3);
        _typeKindLabel.Size = new Size(labelWidth, 20);
        
        _typeKindCombo = new ComboBox();
            this._typeKindCombo.Name = "typeKindCombo";
        
        _typeKindCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        
        _typeKindCombo.Location = new Point(controlLeft, currentY);
        
        _typeKindCombo.Size = new Size(200, 22);
        _typeKindCombo.Items.Add("Interface");
        _typeKindCombo.Items.Add("Abstract Class");
        _typeKindCombo.Items.Add("Concrete Class");
        _typeKindCombo.SelectedIndexChanged += OnValueChanged;
        
        currentY += 30;
        
        // Source file (read-only)
        _sourceFileLabel = new Label();
            this._sourceFileLabel.Name = "sourceFileLabel";
        _sourceFileLabel.Text = "Source File:";
        _sourceFileLabel.Location = new Point(leftMargin, currentY + 3);
        _sourceFileLabel.Size = new Size(labelWidth, 20);
        
        _sourceFileTextBox = new TextBox();
            this._sourceFileTextBox.Name = "sourceFileTextBox";
        
        _sourceFileTextBox.Location = new Point(controlLeft, currentY);
        
        _sourceFileTextBox.Size = new Size(controlWidth, 22);
        
        _sourceFileTextBox.ReadOnly = true;
        
        _sourceFileTextBox.BackColor = Color.FromArgb(245, 245, 245);
        
        currentY += 36;
        
        // Members group
        _membersGroup = new GroupBox();
            this._membersGroup.Name = "membersGroup";
        _membersGroup.Text = "Members";
        _membersGroup.Location = new Point(leftMargin, currentY);
        _membersGroup.Size = new Size(controlWidth + labelWidth + 4, 280);
        _membersGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        
        _membersGrid = new DataGridView();
            this._membersGrid.Name = "membersGrid";
        
        _membersGrid.Location = new Point(8, 20);
        
        _membersGrid.Size = new Size(_membersGroup.Width - 120, 248);
        
        _membersGrid.AllowUserToAddRows = false;
        
        _membersGrid.AllowUserToDeleteRows = false;
        
        _membersGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        
        _membersGrid.MultiSelect = false;
        
        _membersGrid.RowHeadersVisible = false;
        
        _membersGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        
        _membersGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        
        _membersGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        
        // MemberKind column
        this.kindColumn = new DataGridViewComboBoxColumn();
        this.kindColumn.HeaderText = "Kind";
        this.kindColumn.Name = "Kind";
        this.kindColumn.FillWeight = 15;
        kindColumn.Items.Add("Method");
        kindColumn.Items.Add("Property");
        _membersGrid.Columns.Add(kindColumn);
        
        // Name column
        this.memberNameColumn = new DataGridViewTextBoxColumn();
        this.memberNameColumn.HeaderText = "Name";
        this.memberNameColumn.Name = "MemberName";
        this.memberNameColumn.FillWeight = 25;
        _membersGrid.Columns.Add(memberNameColumn);
        
        // ReturnType column (ComboBox with free typing support)
        this.returnTypeColumn = new DataGridViewComboBoxColumn();
        this.returnTypeColumn.HeaderText = "Type";
        this.returnTypeColumn.Name = "ReturnType";
        this.returnTypeColumn.FillWeight = 20;
        this.returnTypeColumn.FlatStyle = FlatStyle.Flat;
        this.returnTypeColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox;
        _membersGrid.Columns.Add(returnTypeColumn);
        
        // Access column
        this.accessColumn = new DataGridViewComboBoxColumn();
        this.accessColumn.HeaderText = "Access";
        this.accessColumn.Name = "Access";
        this.accessColumn.FillWeight = 15;
        accessColumn.Items.Add("Public");
        accessColumn.Items.Add("Protected");
        accessColumn.Items.Add("Internal");
        accessColumn.Items.Add("Private");
        _membersGrid.Columns.Add(accessColumn);
        
        // Modifiers column
        this.modifiersColumn = new DataGridViewTextBoxColumn();
        this.modifiersColumn.HeaderText = "Modifiers";
        this.modifiersColumn.Name = "Modifiers";
        this.modifiersColumn.FillWeight = 15;
        this.modifiersColumn.ReadOnly = true;
        _membersGrid.Columns.Add(modifiersColumn);
        
        // Parameters column (for display only)
        this.paramsColumn = new DataGridViewTextBoxColumn();
        this.paramsColumn.HeaderText = "Parameters";
        this.paramsColumn.Name = "Parameters";
        this.paramsColumn.FillWeight = 20;
        this.paramsColumn.ReadOnly = true;
        _membersGrid.Columns.Add(paramsColumn);
        
        _membersGrid.CellValueChanged += OnMembersGridCellValueChanged;
        _membersGrid.EditingControlShowing += OnMembersGridEditingControlShowing;
        _membersGrid.DataError += OnMembersGridDataError;
        
        // Member buttons
        int btnLeft = _membersGroup.Width - 105;
        int btnWidth = 95;
        
        _addMethodButton = new Button();
            this._addMethodButton.Name = "addMethodButton";
        
        _addMethodButton.Text = "Add Method";
        
        _addMethodButton.Location = new Point(btnLeft, 20);
        
        _addMethodButton.Size = new Size(btnWidth, 28);
        
        _addMethodButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _addMethodButton.Click += OnAddMethod;
        
        _addPropertyButton = new Button();
            this._addPropertyButton.Name = "addPropertyButton";
        
        _addPropertyButton.Text = "Add Property";
        
        _addPropertyButton.Location = new Point(btnLeft, 54);
        
        _addPropertyButton.Size = new Size(btnWidth, 28);
        
        _addPropertyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _addPropertyButton.Click += OnAddProperty;
        
        _editMemberButton = new Button();
            this._editMemberButton.Name = "editMemberButton";
        
        _editMemberButton.Text = "Edit...";
        
        _editMemberButton.Location = new Point(btnLeft, 88);
        
        _editMemberButton.Size = new Size(btnWidth, 28);
        
        _editMemberButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _editMemberButton.Click += OnEditMember;
        
        _removeMemberButton = new Button();
            this._removeMemberButton.Name = "removeMemberButton";
        
        _removeMemberButton.Text = "Remove";
        
        _removeMemberButton.Location = new Point(btnLeft, 122);
        
        _removeMemberButton.Size = new Size(btnWidth, 28);
        
        _removeMemberButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _removeMemberButton.Click += OnRemoveMember;
        
        _membersGroup.Controls.Add(_membersGrid);
        _membersGroup.Controls.Add(_addMethodButton);
        _membersGroup.Controls.Add(_addPropertyButton);
        _membersGroup.Controls.Add(_editMemberButton);
        _membersGroup.Controls.Add(_removeMemberButton);
        
        currentY += _membersGroup.Height + 8;
        
        // Generic parameters group
        _genericsGroup = new GroupBox();
            this._genericsGroup.Name = "genericsGroup";
        _genericsGroup.Text = "Generic Parameters";
        _genericsGroup.Location = new Point(leftMargin, currentY);
        _genericsGroup.Size = new Size(controlWidth + labelWidth + 4, 150);
        _genericsGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        
        _genericsGrid = new DataGridView();
            this._genericsGrid.Name = "genericsGrid";
        
        _genericsGrid.Location = new Point(8, 20);
        
        _genericsGrid.Size = new Size(_genericsGroup.Width - 120, 118);
        
        _genericsGrid.AllowUserToAddRows = false;
        
        _genericsGrid.AllowUserToDeleteRows = false;
        
        _genericsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        
        _genericsGrid.MultiSelect = false;
        
        _genericsGrid.RowHeadersVisible = false;
        
        _genericsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        
        _genericsGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        
        _genericsGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        
        this.gpNameColumn = new DataGridViewTextBoxColumn();
        
        this.gpNameColumn.HeaderText = "Name";
        
        this.gpNameColumn.Name = "GPName";
        
        this.gpNameColumn.FillWeight = 30;
        _genericsGrid.Columns.Add(gpNameColumn);
        
        this.gpVarianceColumn = new DataGridViewComboBoxColumn();
        
        this.gpVarianceColumn.HeaderText = "Variance";
        
        this.gpVarianceColumn.Name = "Variance";
        
        this.gpVarianceColumn.FillWeight = 30;
        gpVarianceColumn.Items.Add("Invariant");
        gpVarianceColumn.Items.Add("Covariant (out)");
        gpVarianceColumn.Items.Add("Contravariant (in)");
        _genericsGrid.Columns.Add(gpVarianceColumn);
        
        this.gpConstraintsColumn = new DataGridViewTextBoxColumn();
        
        this.gpConstraintsColumn.HeaderText = "Constraints";
        
        this.gpConstraintsColumn.Name = "Constraints";
        
        this.gpConstraintsColumn.FillWeight = 40;
        _genericsGrid.Columns.Add(gpConstraintsColumn);
        
        _genericsGrid.CellValueChanged += OnGenericsGridCellValueChanged;
        
        int genBtnLeft = _genericsGroup.Width - 105;
        int genBtnWidth = 95;
        
        _addGenericButton = new Button();
            this._addGenericButton.Name = "addGenericButton";
        
        _addGenericButton.Text = "Add Param";
        
        _addGenericButton.Location = new Point(genBtnLeft, 20);
        
        _addGenericButton.Size = new Size(genBtnWidth, 28);
        
        _addGenericButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _addGenericButton.Click += OnAddGenericParam;
        
        _removeGenericButton = new Button();
            this._removeGenericButton.Name = "removeGenericButton";
        
        _removeGenericButton.Text = "Remove";
        
        _removeGenericButton.Location = new Point(genBtnLeft, 54);
        
        _removeGenericButton.Size = new Size(genBtnWidth, 28);
        
        _removeGenericButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _removeGenericButton.Click += OnRemoveGenericParam;
        
        _genericsGroup.Controls.Add(_genericsGrid);
        _genericsGroup.Controls.Add(_addGenericButton);
        _genericsGroup.Controls.Add(_removeGenericButton);
        
        currentY += _genericsGroup.Height + 12;
        
        // Apply and Cancel buttons
        _applyButton = new Button();
            this._applyButton.Name = "applyButton";
        _applyButton.Text = "Apply Changes";
        _applyButton.DialogResult = DialogResult.OK;
        _applyButton.Size = new Size(110, 28);
        _applyButton.Location = new Point(Width - 240, currentY);
        _applyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _applyButton.Click += OnApplyClick;
        
        _cancelButton = new Button();
            this._cancelButton.Name = "cancelButton";
        
        _cancelButton.Text = "Cancel";
        
        _cancelButton.DialogResult = DialogResult.Cancel;
        
        _cancelButton.Size = new Size(80, 28);
        
        _cancelButton.Location = new Point(Width - 120, currentY);
        
        _cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        
        AcceptButton = _applyButton;
        CancelButton = _cancelButton;
        
        // Add all controls to form
        Controls.Add(_nameLabel);
        Controls.Add(_nameTextBox);
        Controls.Add(_namespaceLabel);
        Controls.Add(_namespaceTextBox);
        Controls.Add(_typeKindLabel);
        Controls.Add(_typeKindCombo);
        Controls.Add(_sourceFileLabel);
        Controls.Add(_sourceFileTextBox);
        Controls.Add(_membersGroup);
        Controls.Add(_genericsGroup);
        Controls.Add(_applyButton);
        Controls.Add(_cancelButton);
        }
    }
}
