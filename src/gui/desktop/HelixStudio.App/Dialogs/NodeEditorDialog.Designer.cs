namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    public partial class NodeEditorDialog
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
        private Button _cancelButton;
        private Button _okButton;
        private Button _removeGenericButton;
        private Button _removeMemberButton;
        private ComboBox _typeKindCombo;
        private DataGridView _membersGrid;
        private DataGridViewComboBoxColumn accessColumn;
        private DataGridViewComboBoxColumn kindColumn;
        private DataGridViewTextBoxColumn memberNameColumn;
        private DataGridViewTextBoxColumn returnTypeColumn;
        private Label _nameLabel;
        private Label _namespaceLabel;
        private Label _typeKindLabel;
        private ListView _genericsListView;
        private TextBox _namespaceTextBox;
        private TextBox _nameTextBox;

        private GroupBox _membersGroup;
        private GroupBox _genericsGroup;

        private void InitializeComponent()
        {
        Text = "Edit Type Node";
        Size = new Size(680, 640);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        
        int labelWidth = 90;
        int leftMargin = 12;
        int controlLeft = leftMargin + labelWidth + 4;
        int controlWidth = 540;
        int currentY = 16;
        
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
        
        currentY += 36;
        
        // Members group
        _membersGroup = new GroupBox();
            this._membersGroup.Name = "membersGroup";
        _membersGroup.Text = "Members";
        _membersGroup.Location = new Point(leftMargin, currentY);
        _membersGroup.Size = new Size(controlWidth + labelWidth + 4, 280);
        
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
        
        // MemberKind column
        this.kindColumn = new DataGridViewComboBoxColumn();
        kindColumn.HeaderText = "Kind";
        kindColumn.Name = "Kind";
        kindColumn.Items.Add("Method");
        kindColumn.Items.Add("Property");
        kindColumn.FillWeight = 20;
        _membersGrid.Columns.Add(kindColumn);
        
        // Name column
        this.memberNameColumn = new DataGridViewTextBoxColumn();
        memberNameColumn.HeaderText = "Name";
        memberNameColumn.Name = "MemberName";
        memberNameColumn.FillWeight = 30;
        _membersGrid.Columns.Add(memberNameColumn);
        
        // ReturnType column
        this.returnTypeColumn = new DataGridViewTextBoxColumn();
        returnTypeColumn.HeaderText = "Return Type";
        returnTypeColumn.Name = "ReturnType";
        returnTypeColumn.FillWeight = 25;
        _membersGrid.Columns.Add(returnTypeColumn);
        
        // Access column
        this.accessColumn = new DataGridViewComboBoxColumn();
        accessColumn.HeaderText = "Access";
        accessColumn.Name = "Access";
        accessColumn.Items.Add("Public");
        accessColumn.Items.Add("Protected");
        accessColumn.Items.Add("Internal");
        accessColumn.Items.Add("Private");
        accessColumn.FillWeight = 25;
        _membersGrid.Columns.Add(accessColumn);
        
        // Member buttons
        int btnLeft = _membersGrid.Right + 8;
        int btnWidth = 95;
        
        _addMethodButton = new Button();
            this._addMethodButton.Name = "addMethodButton";
        _addMethodButton.Text = "Add Method";
        _addMethodButton.Location = new Point(btnLeft, 20);
        _addMethodButton.Size = new Size(btnWidth, 28);
        _addMethodButton.Click += OnAddMethod;
        
        _addPropertyButton = new Button();
            this._addPropertyButton.Name = "addPropertyButton";
        _addPropertyButton.Text = "Add Property";
        _addPropertyButton.Location = new Point(btnLeft, 54);
        _addPropertyButton.Size = new Size(btnWidth, 28);
        _addPropertyButton.Click += OnAddProperty;
        
        _removeMemberButton = new Button();
            this._removeMemberButton.Name = "removeMemberButton";
        _removeMemberButton.Text = "Remove";
        _removeMemberButton.Location = new Point(btnLeft, 88);
        _removeMemberButton.Size = new Size(btnWidth, 28);
        _removeMemberButton.Click += OnRemoveMember;
        
        _membersGroup.Controls.Add(_membersGrid);
        _membersGroup.Controls.Add(_addMethodButton);
        _membersGroup.Controls.Add(_addPropertyButton);
        _membersGroup.Controls.Add(_removeMemberButton);
        
        currentY += _membersGroup.Height + 8;
        
        // Generic parameters group
        _genericsGroup = new GroupBox();
            this._genericsGroup.Name = "genericsGroup";
        _genericsGroup.Text = "Generic Parameters";
        _genericsGroup.Location = new Point(leftMargin, currentY);
        _genericsGroup.Size = new Size(controlWidth + labelWidth + 4, 150);
        
        _genericsListView = new ListView();
            this._genericsListView.Name = "genericsListView";
        _genericsListView.Location = new Point(8, 20);
        _genericsListView.Size = new Size(_genericsGroup.Width - 120, 118);
        _genericsListView.View = View.Details;
        _genericsListView.FullRowSelect = true;
        _genericsListView.MultiSelect = false;
        _genericsListView.GridLines = true;
        _genericsListView.LabelEdit = true;
        _genericsListView.Columns.Add("Name", 120);
        _genericsListView.Columns.Add("Constraints", _genericsListView.Width - 130);
        
        int genBtnLeft = _genericsListView.Right + 8;
        int genBtnWidth = 95;
        
        _addGenericButton = new Button();
            this._addGenericButton.Name = "addGenericButton";
        _addGenericButton.Text = "Add Param";
        _addGenericButton.Location = new Point(genBtnLeft, 20);
        _addGenericButton.Size = new Size(genBtnWidth, 28);
        _addGenericButton.Click += OnAddGenericParam;
        
        _removeGenericButton = new Button();
            this._removeGenericButton.Name = "removeGenericButton";
        _removeGenericButton.Text = "Remove";
        _removeGenericButton.Location = new Point(genBtnLeft, 54);
        _removeGenericButton.Size = new Size(genBtnWidth, 28);
        _removeGenericButton.Click += OnRemoveGenericParam;
        
        _genericsGroup.Controls.Add(_genericsListView);
        _genericsGroup.Controls.Add(_addGenericButton);
        _genericsGroup.Controls.Add(_removeGenericButton);
        
        currentY += _genericsGroup.Height + 12;
        
        // OK and Cancel buttons
        _okButton = new Button();
            this._okButton.Name = "okButton";
        _okButton.Text = "OK";
        _okButton.DialogResult = DialogResult.OK;
        _okButton.Size = new Size(80, 28);
        _okButton.Location = new Point(Width - 200, currentY);
        _okButton.Click += OnOkClick;
        
        _cancelButton = new Button();
            this._cancelButton.Name = "cancelButton";
        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.Size = new Size(80, 28);
        _cancelButton.Location = new Point(Width - 108, currentY);
        
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        
        // Add all controls to form
        Controls.Add(_nameLabel);
        Controls.Add(_nameTextBox);
        Controls.Add(_namespaceLabel);
        Controls.Add(_namespaceTextBox);
        Controls.Add(_typeKindLabel);
        Controls.Add(_typeKindCombo);
        Controls.Add(_membersGroup);
        Controls.Add(_genericsGroup);
        Controls.Add(_okButton);
        Controls.Add(_cancelButton);
        }
    }
}
