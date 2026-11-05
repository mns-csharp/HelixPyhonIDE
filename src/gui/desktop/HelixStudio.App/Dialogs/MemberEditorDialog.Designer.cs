namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    public sealed partial class MemberEditorDialog
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

        private Button _addParamButton;
        private Button _cancelButton;
        private Button _okButton;
        private Button _removeParamButton;
        private CheckBox _hasGetterCheck;
        private CheckBox _hasSetterCheck;
        private CheckBox _isAbstractCheck;
        private CheckBox _isOverrideCheck;
        private CheckBox _isStaticCheck;
        private CheckBox _isVirtualCheck;
        private ComboBox _accessCombo;
        private ComboBox _kindCombo;
        private ComboBox _returnTypeCombo;
        private DataGridView _parametersGrid;
        private Label _accessLabel;
        private Label _kindLabel;
        private Label _nameLabel;
        private Label _returnTypeLabel;
        private TextBox _nameTextBox;

        private DataGridViewComboBoxColumn paramTypeColumn;
        private DataGridViewTextBoxColumn paramNameColumn;

        private GroupBox _modifiersGroup;
        private GroupBox _accessorsGroup;
        private GroupBox _parametersGroup;

        private void InitializeComponent()
        {
        Text = "Edit Member";
        Size = new Size(500, 520);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        
        int labelWidth = 90;
        int leftMargin = 12;
        int controlLeft = leftMargin + labelWidth + 4;
        int controlWidth = 360;
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
        
        // Kind
        _kindLabel = new Label();
            this._kindLabel.Name = "kindLabel";
        _kindLabel.Text = "Kind:";
        _kindLabel.Location = new Point(leftMargin, currentY + 3);
        _kindLabel.Size = new Size(labelWidth, 20);
        
        _kindCombo = new ComboBox();
            this._kindCombo.Name = "kindCombo";
        
        _kindCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        
        _kindCombo.Location = new Point(controlLeft, currentY);
        
        _kindCombo.Size = new Size(150, 22);
        _kindCombo.Items.Add("Method");
        _kindCombo.Items.Add("Property");
        _kindCombo.Items.Add("Field");
        _kindCombo.Items.Add("Event");
        _kindCombo.SelectedIndexChanged += OnKindChanged;
        
        currentY += 30;
        
        // Return Type
        _returnTypeLabel = new Label();
            this._returnTypeLabel.Name = "returnTypeLabel";
        _returnTypeLabel.Text = "Type:";
        _returnTypeLabel.Location = new Point(leftMargin, currentY + 3);
        _returnTypeLabel.Size = new Size(labelWidth, 20);
        
        _returnTypeCombo = new ComboBox();
            this._returnTypeCombo.Name = "returnTypeCombo";
        
        _returnTypeCombo.DropDownStyle = ComboBoxStyle.DropDown;
        
        _returnTypeCombo.Location = new Point(controlLeft, currentY);
        
        _returnTypeCombo.Size = new Size(controlWidth, 22);
        PopulateReturnTypeCombo();
        
        currentY += 30;
        
        // Access
        _accessLabel = new Label();
            this._accessLabel.Name = "accessLabel";
        _accessLabel.Text = "Access:";
        _accessLabel.Location = new Point(leftMargin, currentY + 3);
        _accessLabel.Size = new Size(labelWidth, 20);
        
        _accessCombo = new ComboBox();
            this._accessCombo.Name = "accessCombo";
        
        _accessCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        
        _accessCombo.Location = new Point(controlLeft, currentY);
        
        _accessCombo.Size = new Size(150, 22);
        _accessCombo.Items.Add("Public");
        _accessCombo.Items.Add("Protected");
        _accessCombo.Items.Add("Internal");
        _accessCombo.Items.Add("Private");
        _accessCombo.Items.Add("Protected Internal");
        _accessCombo.Items.Add("Private Protected");
        _accessCombo.Items.Add("File");
        
        currentY += 36;
        
        // Modifiers group
        _modifiersGroup = new GroupBox();
            this._modifiersGroup.Name = "modifiersGroup";
        _modifiersGroup.Text = "Modifiers";
        _modifiersGroup.Location = new Point(leftMargin, currentY);
        _modifiersGroup.Size = new Size(220, 80);
        
        _isStaticCheck = new CheckBox();
            this._isStaticCheck.Name = "isStaticCheck";
        
        _isStaticCheck.Text = "Static";
        
        _isStaticCheck.Location = new Point(10, 20);
        
        _isStaticCheck.Size = new Size(80, 20);
        
        _isAbstractCheck = new CheckBox();
            this._isAbstractCheck.Name = "isAbstractCheck";
        
        _isAbstractCheck.Text = "Abstract";
        
        _isAbstractCheck.Location = new Point(100, 20);
        
        _isAbstractCheck.Size = new Size(100, 20);
        
        _isVirtualCheck = new CheckBox();
            this._isVirtualCheck.Name = "isVirtualCheck";
        
        _isVirtualCheck.Text = "Virtual";
        
        _isVirtualCheck.Location = new Point(10, 45);
        
        _isVirtualCheck.Size = new Size(80, 20);
        
        _isOverrideCheck = new CheckBox();
            this._isOverrideCheck.Name = "isOverrideCheck";
        
        _isOverrideCheck.Text = "Override";
        
        _isOverrideCheck.Location = new Point(100, 45);
        
        _isOverrideCheck.Size = new Size(100, 20);
        
        _modifiersGroup.Controls.Add(_isStaticCheck);
        _modifiersGroup.Controls.Add(_isAbstractCheck);
        _modifiersGroup.Controls.Add(_isVirtualCheck);
        _modifiersGroup.Controls.Add(_isOverrideCheck);
        
        // Accessors group (for properties)
        _accessorsGroup = new GroupBox();
            this._accessorsGroup.Name = "accessorsGroup";
        _accessorsGroup.Text = "Property Accessors";
        _accessorsGroup.Location = new Point(250, currentY);
        _accessorsGroup.Size = new Size(220, 80);
        
        _hasGetterCheck = new CheckBox();
            this._hasGetterCheck.Name = "hasGetterCheck";
        
        _hasGetterCheck.Text = "Has Getter";
        
        _hasGetterCheck.Location = new Point(10, 20);
        
        _hasGetterCheck.Size = new Size(100, 20);
        
        _hasSetterCheck = new CheckBox();
            this._hasSetterCheck.Name = "hasSetterCheck";
        
        _hasSetterCheck.Text = "Has Setter";
        
        _hasSetterCheck.Location = new Point(10, 45);
        
        _hasSetterCheck.Size = new Size(100, 20);
        
        _accessorsGroup.Controls.Add(_hasGetterCheck);
        _accessorsGroup.Controls.Add(_hasSetterCheck);
        
        currentY += 90;
        
        // Parameters group (for methods)
        _parametersGroup = new GroupBox();
            this._parametersGroup.Name = "parametersGroup";
        _parametersGroup.Text = "Parameters (Methods Only)";
        _parametersGroup.Location = new Point(leftMargin, currentY);
        _parametersGroup.Size = new Size(460, 150);
        
        _parametersGrid = new DataGridView();
            this._parametersGrid.Name = "parametersGrid";
        
        _parametersGrid.Location = new Point(8, 20);
        
        _parametersGrid.Size = new Size(350, 118);
        
        _parametersGrid.AllowUserToAddRows = false;
        
        _parametersGrid.AllowUserToDeleteRows = false;
        
        _parametersGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        
        _parametersGrid.MultiSelect = false;
        
        _parametersGrid.RowHeadersVisible = false;
        
        _parametersGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        
        _parametersGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        
        this.paramNameColumn = new DataGridViewTextBoxColumn();
        
        this.paramNameColumn.HeaderText = "Name";
        
        this.paramNameColumn.Name = "ParamName";
        
        this.paramNameColumn.FillWeight = 40;
        _parametersGrid.Columns.Add(paramNameColumn);
        
        this.paramTypeColumn = new DataGridViewComboBoxColumn();
        
        this.paramTypeColumn.HeaderText = "Type";
        
        this.paramTypeColumn.Name = "ParamType";
        
        this.paramTypeColumn.FillWeight = 60;
        
        this.paramTypeColumn.FlatStyle = FlatStyle.Flat;
        
        this.paramTypeColumn.DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox;
        PopulateParamTypeColumn(paramTypeColumn);
        _parametersGrid.Columns.Add(paramTypeColumn);
        _parametersGrid.EditingControlShowing += OnParametersGridEditingControlShowing;
        _parametersGrid.DataError += OnParametersGridDataError;
        
        _addParamButton = new Button();
            this._addParamButton.Name = "addParamButton";
        
        _addParamButton.Text = "Add";
        
        _addParamButton.Location = new Point(365, 20);
        
        _addParamButton.Size = new Size(80, 28);
        _addParamButton.Click += OnAddParameter;
        
        _removeParamButton = new Button();
            this._removeParamButton.Name = "removeParamButton";
        
        _removeParamButton.Text = "Remove";
        
        _removeParamButton.Location = new Point(365, 54);
        
        _removeParamButton.Size = new Size(80, 28);
        _removeParamButton.Click += OnRemoveParameter;
        
        _parametersGroup.Controls.Add(_parametersGrid);
        _parametersGroup.Controls.Add(_addParamButton);
        _parametersGroup.Controls.Add(_removeParamButton);
        
        currentY += 160;
        
        // OK and Cancel buttons
        _okButton = new Button();
            this._okButton.Name = "okButton";
        _okButton.Text = "OK";
        _okButton.DialogResult = DialogResult.OK;
        _okButton.Size = new Size(80, 28);
        _okButton.Location = new Point(Width - 190, currentY);
        _okButton.Click += OnOkClick;
        
        _cancelButton = new Button();
            this._cancelButton.Name = "cancelButton";
        
        _cancelButton.Text = "Cancel";
        
        _cancelButton.DialogResult = DialogResult.Cancel;
        
        _cancelButton.Size = new Size(80, 28);
        
        _cancelButton.Location = new Point(Width - 100, currentY);
        
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
        
        // Add all controls to form
        Controls.Add(_nameLabel);
        Controls.Add(_nameTextBox);
        Controls.Add(_kindLabel);
        Controls.Add(_kindCombo);
        Controls.Add(_returnTypeLabel);
        Controls.Add(_returnTypeCombo);
        Controls.Add(_accessLabel);
        Controls.Add(_accessCombo);
        Controls.Add(_modifiersGroup);
        Controls.Add(_accessorsGroup);
        Controls.Add(_parametersGroup);
        Controls.Add(_okButton);
        Controls.Add(_cancelButton);
        }
    }
}
