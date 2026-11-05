namespace HelixPythonNamespace.UI.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    public class PropertyPanel : UserControl
    {
        private Label _lblName;
        private TextBox _txtName;
        private Label _lblNamespace;
        private TextBox _txtNamespace;
        private Label _lblTypeKind;
        private ComboBox _cboTypeKind;
        private Label _lblMembers;
        private ListBox _lstMembers;
        private Button _btnAddMember;
        private Button _btnRemoveMember;
        private Button _btnEditMember;
        private Label _lblGenericParams;
        private ListBox _lstGenericParams;
        private Button _btnAddGenericParam;
        private Button _btnRemoveGenericParam;
        private Button _btnApply;

        private TypeNode _currentNode;
        private TypeModel _currentTypeModel;
        private List<MemberModel> _originalMembers;
        private bool _isLoading;
        private bool _isDirty;

        public PropertyPanel()
        {
            _isLoading = false;
            _isDirty = false;
            InitializeControls();
        }

        public event EventHandler NodePropertyChanged;

        public event EventHandler<TypeModelChangedEventArgs> TypeModelChanged;

        public void LoadNode(TypeNode node)
        {
            _isLoading = true;
            _currentTypeModel = null;

            try
            {
                _currentNode = node;
                _isDirty = false;

                if (node == null)
                {
                    ClearControls();
                    return;
                }

                Enabled = true;
                _txtName.Text = node.Name ?? string.Empty;
                _txtNamespace.Text = node.Namespace ?? string.Empty;
                _txtNamespace.ReadOnly = true; // Read-only in legacy mode

                // Set TypeKind combo
                SelectTypeKind(node.TypeKind);

                // Populate members
                _lstMembers.Items.Clear();
                if (node.Members != null)
                {
                    for (int i = 0; i < node.Members.Count; i++)
                    {
                        MemberDefinition member = node.Members[i];
                        string display = FormatMember(member);
                        _lstMembers.Items.Add(display);
                    }
                }

                // Populate generic parameters
                _lstGenericParams.Items.Clear();
                if (node.GenericParameters != null)
                {
                    for (int i = 0; i < node.GenericParameters.Count; i++)
                    {
                        GenericParameter gp = node.GenericParameters[i];
                        _lstGenericParams.Items.Add(gp.Name);
                    }
                }

                _btnApply.Visible = false;
            }
            finally
            {
                _isLoading = false;
            }
        }

        public void LoadTypeModel(TypeModel typeModel)
        {
            _isLoading = true;
            _currentNode = null;

            try
            {
                _currentTypeModel = typeModel;
                _isDirty = false;

                // Snapshot original members for change tracking
                _originalMembers = typeModel != null && typeModel.Members != null
                    ? new List<MemberModel>(typeModel.Members)
                    : new List<MemberModel>();

                if (typeModel == null)
                {
                    ClearControls();
                    return;
                }

                Enabled = true;
                _txtName.Text = typeModel.Name ?? string.Empty;
                _txtNamespace.Text = typeModel.Namespace ?? string.Empty;
                _txtNamespace.ReadOnly = false;

                // Set TypeKind combo
                SelectTypeKind(typeModel.TypeKind);

                // Populate members
                _lstMembers.Items.Clear();
                if (typeModel.Members != null)
                {
                    for (int i = 0; i < typeModel.Members.Count; i++)
                    {
                        MemberModel member = typeModel.Members[i];
                        string display = FormatMemberModel(member);
                        _lstMembers.Items.Add(display);
                    }
                }

                // Populate generic parameters
                _lstGenericParams.Items.Clear();
                if (typeModel.GenericParameters != null)
                {
                    for (int i = 0; i < typeModel.GenericParameters.Count; i++)
                    {
                        GenericParameterModel gp = typeModel.GenericParameters[i];
                        _lstGenericParams.Items.Add(gp.Name);
                    }
                }

                _btnApply.Visible = true;
                _btnApply.Enabled = false;
            }
            finally
            {
                _isLoading = false;
            }
        }

        public TypeModel CurrentTypeModel
        {
            get { return _currentTypeModel; }
        }

        private void ClearControls()
        {
            _txtName.Text = string.Empty;
            _txtNamespace.Text = string.Empty;
            _cboTypeKind.SelectedIndex = -1;
            _lstMembers.Items.Clear();
            _lstGenericParams.Items.Clear();
            _btnApply.Visible = false;
            Enabled = false;
        }

        private void SelectTypeKind(TypeKind kind)
        {
            for (int i = 0; i < _cboTypeKind.Items.Count; i++)
            {
                if (_cboTypeKind.Items[i].ToString() == kind.ToString())
                {
                    _cboTypeKind.SelectedIndex = i;
                    break;
                }
            }
        }

        public TypeNode SaveToNode()
        {
            if (_currentNode == null)
            {
                return null;
            }

            _currentNode.Name = _txtName.Text;

            if (_cboTypeKind.SelectedIndex >= 0)
            {
                string selected = _cboTypeKind.SelectedItem.ToString();
                if (selected == TypeKind.Interface.ToString())
                {
                    _currentNode.TypeKind = TypeKind.Interface;
                }
                else if (selected == TypeKind.AbstractClass.ToString())
                {
                    _currentNode.TypeKind = TypeKind.AbstractClass;
                }
                else if (selected == TypeKind.ConcreteClass.ToString())
                {
                    _currentNode.TypeKind = TypeKind.ConcreteClass;
                }
            }

            return _currentNode;
        }

        public void SaveToNode(TypeNode node)
        {
            if (node != null)
            {
                _currentNode = node;
            }
            SaveToNode();
        }

        public void ClearNode()
        {
            LoadNode(null);
        }

        private void InitializeControls()
        {
            SuspendLayout();

            int labelWidth = 100;
            int controlLeft = 105;
            int controlWidth = 170;
            int currentY = 8;
            int rowHeight = 28;

            // Name label and text box
            _lblName = new Label();
            _lblName.Text = "Name:";
            _lblName.Location = new Point(4, currentY + 3);
            _lblName.Size = new Size(labelWidth, 16);
            Controls.Add(_lblName);

            _txtName = new TextBox();
            _txtName.Location = new Point(controlLeft, currentY);
            _txtName.Size = new Size(controlWidth, 20);
            _txtName.TextChanged += OnTextChanged;
            Controls.Add(_txtName);

            currentY += rowHeight;

            // Namespace label and text box
            _lblNamespace = new Label();
            _lblNamespace.Text = "Namespace:";
            _lblNamespace.Location = new Point(4, currentY + 3);
            _lblNamespace.Size = new Size(labelWidth, 16);
            Controls.Add(_lblNamespace);

            _txtNamespace = new TextBox();
            _txtNamespace.Location = new Point(controlLeft, currentY);
            _txtNamespace.Size = new Size(controlWidth, 20);
            _txtNamespace.TextChanged += OnTextChanged;
            Controls.Add(_txtNamespace);

            currentY += rowHeight;

            // TypeKind label and combo box
            _lblTypeKind = new Label();
            _lblTypeKind.Text = "Type Kind:";
            _lblTypeKind.Location = new Point(4, currentY + 3);
            _lblTypeKind.Size = new Size(labelWidth, 16);
            Controls.Add(_lblTypeKind);

            _cboTypeKind = new ComboBox();
            _cboTypeKind.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboTypeKind.Location = new Point(controlLeft, currentY);
            _cboTypeKind.Size = new Size(controlWidth, 21);
            _cboTypeKind.Items.Add(TypeKind.Interface.ToString());
            _cboTypeKind.Items.Add(TypeKind.AbstractClass.ToString());
            _cboTypeKind.Items.Add(TypeKind.ConcreteClass.ToString());
            _cboTypeKind.SelectedIndexChanged += OnComboChanged;
            Controls.Add(_cboTypeKind);

            currentY += rowHeight;

            // Members label and list box
            _lblMembers = new Label();
            _lblMembers.Text = "Members:";
            _lblMembers.Location = new Point(4, currentY);
            _lblMembers.Size = new Size(labelWidth, 16);
            Controls.Add(_lblMembers);

            currentY += 18;

            _lstMembers = new ListBox();
            _lstMembers.Location = new Point(4, currentY);
            _lstMembers.Size = new Size(controlLeft + controlWidth - 4, 100);
            Controls.Add(_lstMembers);

            currentY += 104;

            _btnAddMember = new Button();
            _btnAddMember.Text = "Add";
            _btnAddMember.Location = new Point(4, currentY);
            _btnAddMember.Size = new Size(60, 24);
            _btnAddMember.Click += OnAddMemberClick;
            Controls.Add(_btnAddMember);

            _btnEditMember = new Button();
            _btnEditMember.Text = "Edit";
            _btnEditMember.Location = new Point(68, currentY);
            _btnEditMember.Size = new Size(55, 24);
            _btnEditMember.Click += OnEditMemberClick;
            Controls.Add(_btnEditMember);

            _btnRemoveMember = new Button();
            _btnRemoveMember.Text = "Remove";
            _btnRemoveMember.Location = new Point(127, currentY);
            _btnRemoveMember.Size = new Size(70, 24);
            _btnRemoveMember.Click += OnRemoveMemberClick;
            Controls.Add(_btnRemoveMember);

            currentY += 30;

            // Generic parameters label and list box
            _lblGenericParams = new Label();
            _lblGenericParams.Text = "Generic Params:";
            _lblGenericParams.Location = new Point(4, currentY);
            _lblGenericParams.Size = new Size(labelWidth, 16);
            Controls.Add(_lblGenericParams);

            currentY += 18;

            _lstGenericParams = new ListBox();
            _lstGenericParams.Location = new Point(4, currentY);
            _lstGenericParams.Size = new Size(controlLeft + controlWidth - 4, 70);
            Controls.Add(_lstGenericParams);

            currentY += 74;

            _btnAddGenericParam = new Button();
            _btnAddGenericParam.Text = "Add Param";
            _btnAddGenericParam.Location = new Point(4, currentY);
            _btnAddGenericParam.Size = new Size(90, 24);
            _btnAddGenericParam.Click += OnAddGenericParamClick;
            Controls.Add(_btnAddGenericParam);

            _btnRemoveGenericParam = new Button();
            _btnRemoveGenericParam.Text = "Remove";
            _btnRemoveGenericParam.Location = new Point(100, currentY);
            _btnRemoveGenericParam.Size = new Size(75, 24);
            _btnRemoveGenericParam.Click += OnRemoveGenericParamClick;
            Controls.Add(_btnRemoveGenericParam);

            currentY += 34;

            // Apply button (for TypeModel mode)
            _btnApply = new Button();
            _btnApply.Text = "Apply Changes";
            _btnApply.Location = new Point(4, currentY);
            _btnApply.Size = new Size(controlLeft + controlWidth - 4, 28);
            _btnApply.Visible = false;
            _btnApply.Enabled = false;
            _btnApply.Click += OnApplyClick;
            Controls.Add(_btnApply);

            Size = new Size(controlLeft + controlWidth + 8, currentY + 40);
            ResumeLayout(false);
        }

        private void OnTextChanged(object sender, EventArgs e)
        {
            if (!_isLoading)
            {
                MarkDirty();
                RaiseNodePropertyChanged();
            }
        }

        private void OnComboChanged(object sender, EventArgs e)
        {
            if (!_isLoading)
            {
                MarkDirty();
                RaiseNodePropertyChanged();
            }
        }

        private void MarkDirty()
        {
            _isDirty = true;
            if (_currentTypeModel != null)
            {
                _btnApply.Enabled = true;
            }
        }

        private void OnAddMemberClick(object sender, EventArgs e)
        {
            if (_currentTypeModel != null)
            {
                // TypeModel mode
                string name = GetUniqueMemberName(_currentTypeModel.Members, "NewProperty");
                var newMember = new MemberModel(name, MemberKind.Property);
                newMember.ReturnType = "string";
                newMember.AccessModifier = AccessModifier.Public;
                newMember.HasGetter = true;
                newMember.HasSetter = true;
                _currentTypeModel.Members.Add(newMember);

                string display = FormatMemberModel(newMember);
                _lstMembers.Items.Add(display);
                MarkDirty();
                return;
            }

            if (_currentNode == null)
            {
                return;
            }

            string defName = GetUniqueMemberDefName(_currentNode.Members, "NewProperty");
            MemberDefinition newDef = new MemberDefinition(defName, MemberKind.Property);
            newDef.ReturnType = "string";
            _currentNode.Members.Add(newDef);

            string displayStr = FormatMember(newDef);
            _lstMembers.Items.Add(displayStr);
            RaiseNodePropertyChanged();
        }

        private void OnEditMemberClick(object sender, EventArgs e)
        {
            int selectedIndex = _lstMembers.SelectedIndex;
            if (selectedIndex < 0)
            {
                return;
            }

            if (_currentTypeModel != null && selectedIndex < _currentTypeModel.Members.Count)
            {
                // TypeModel mode - raise event to let MainForm handle member editing dialog
                RaiseTypeModelMemberEdit(selectedIndex);
                return;
            }

            if (_currentNode != null && selectedIndex < _currentNode.Members.Count)
            {
                // Legacy mode - just show message
                MessageBox.Show(
                    "Member editing is available when a solution is loaded.",
                    "Info",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private void OnRemoveMemberClick(object sender, EventArgs e)
        {
            int selectedIndex = _lstMembers.SelectedIndex;

            if (_currentTypeModel != null)
            {
                if (selectedIndex < 0 || selectedIndex >= _currentTypeModel.Members.Count)
                {
                    return;
                }

                _currentTypeModel.Members.RemoveAt(selectedIndex);
                _lstMembers.Items.RemoveAt(selectedIndex);
                MarkDirty();
                return;
            }

            if (_currentNode == null)
            {
                return;
            }

            if (selectedIndex < 0 || selectedIndex >= _currentNode.Members.Count)
            {
                return;
            }

            _currentNode.Members.RemoveAt(selectedIndex);
            _lstMembers.Items.RemoveAt(selectedIndex);
            RaiseNodePropertyChanged();
        }

        private void OnAddGenericParamClick(object sender, EventArgs e)
        {
            if (_currentTypeModel != null)
            {
                string paramName = string.Format("T{0}", _currentTypeModel.GenericParameters.Count + 1);
                var gp = new GenericParameterModel(paramName);
                _currentTypeModel.GenericParameters.Add(gp);
                _lstGenericParams.Items.Add(gp.Name);
                MarkDirty();
                return;
            }

            if (_currentNode == null)
            {
                return;
            }

            string paramNameNode = string.Format("T{0}", _currentNode.GenericParameters.Count + 1);
            GenericParameter gpNode = new GenericParameter(paramNameNode);
            _currentNode.GenericParameters.Add(gpNode);
            _lstGenericParams.Items.Add(gpNode.Name);
            RaiseNodePropertyChanged();
        }

        private void OnRemoveGenericParamClick(object sender, EventArgs e)
        {
            int selectedIndex = _lstGenericParams.SelectedIndex;

            if (_currentTypeModel != null)
            {
                if (selectedIndex < 0 || selectedIndex >= _currentTypeModel.GenericParameters.Count)
                {
                    return;
                }

                _currentTypeModel.GenericParameters.RemoveAt(selectedIndex);
                _lstGenericParams.Items.RemoveAt(selectedIndex);
                MarkDirty();
                return;
            }

            if (_currentNode == null)
            {
                return;
            }

            if (selectedIndex < 0 || selectedIndex >= _currentNode.GenericParameters.Count)
            {
                return;
            }

            _currentNode.GenericParameters.RemoveAt(selectedIndex);
            _lstGenericParams.Items.RemoveAt(selectedIndex);
            RaiseNodePropertyChanged();
        }

        private void OnApplyClick(object sender, EventArgs e)
        {
            if (_currentTypeModel == null || !_isDirty)
            {
                return;
            }

            // Apply basic property changes to the TypeModel
            _currentTypeModel.Name = _txtName.Text.Trim();
            _currentTypeModel.Namespace = _txtNamespace.Text.Trim();

            if (_cboTypeKind.SelectedIndex >= 0)
            {
                string selected = _cboTypeKind.SelectedItem.ToString();
                if (selected == TypeKind.Interface.ToString())
                {
                    _currentTypeModel.TypeKind = TypeKind.Interface;
                }
                else if (selected == TypeKind.AbstractClass.ToString())
                {
                    _currentTypeModel.TypeKind = TypeKind.AbstractClass;
                }
                else if (selected == TypeKind.ConcreteClass.ToString())
                {
                    _currentTypeModel.TypeKind = TypeKind.ConcreteClass;
                }
            }

            _isDirty = false;
            _btnApply.Enabled = false;

            // Compute member changes
            var addedMembers = new List<MemberModel>();
            var removedMembers = new List<MemberModel>();

            if (_currentTypeModel.Members != null)
            {
                foreach (var m in _currentTypeModel.Members)
                {
                    if (!_originalMembers.Contains(m))
                    {
                        addedMembers.Add(m);
                    }
                }
            }

            foreach (var m in _originalMembers)
            {
                if (_currentTypeModel.Members == null || !_currentTypeModel.Members.Contains(m))
                {
                    removedMembers.Add(m);
                }
            }

            // Update snapshot
            _originalMembers = _currentTypeModel.Members != null
                ? new List<MemberModel>(_currentTypeModel.Members)
                : new List<MemberModel>();

            RaiseTypeModelChanged(addedMembers, removedMembers);
        }

        private static string FormatMember(MemberDefinition member)
        {
            if (member == null)
            {
                return string.Empty;
            }

            string prefix = GetAccessPrefix(member.AccessModifier);
            string returnType = member.ReturnType ?? "void";

            if (member.MemberKind == MemberKind.Property)
            {
                return string.Format("{0} {1} : {2}", prefix, member.Name, returnType);
            }

            return string.Format("{0} {1}() : {2}", prefix, member.Name, returnType);
        }

        private static string FormatMemberModel(MemberModel member)
        {
            if (member == null)
            {
                return string.Empty;
            }

            string prefix = GetAccessPrefix(member.AccessModifier);
            string returnType = member.ReturnType ?? "void";

            if (member.MemberKind == MemberKind.Property)
            {
                return string.Format("{0} {1} : {2}", prefix, member.Name, returnType);
            }

            return string.Format("{0} {1}() : {2}", prefix, member.Name, returnType);
        }

        private static string GetAccessPrefix(AccessModifier modifier)
        {
            switch (modifier)
            {
                case AccessModifier.Public:
                    return "+";
                case AccessModifier.Protected:
                    return "#";
                case AccessModifier.Private:
                    return "-";
                case AccessModifier.Internal:
                    return "~";
                default:
                    return "+";
            }
        }

        private void RaiseNodePropertyChanged()
        {
            EventHandler handler = NodePropertyChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void RaiseTypeModelChanged()
        {
            RaiseTypeModelChanged(null, null);
        }

        private void RaiseTypeModelChanged(List<MemberModel> addedMembers, List<MemberModel> removedMembers)
        {
            EventHandler<TypeModelChangedEventArgs> handler = TypeModelChanged;
            if (handler != null)
            {
                var args = new TypeModelChangedEventArgs(_currentTypeModel);
                args.AddedMembers = addedMembers;
                args.RemovedMembers = removedMembers;
                handler(this, args);
            }
        }

        private void RaiseTypeModelMemberEdit(int memberIndex)
        {
            EventHandler<TypeModelChangedEventArgs> handler = TypeModelChanged;
            if (handler != null)
            {
                var args = new TypeModelChangedEventArgs(_currentTypeModel);
                args.EditMemberIndex = memberIndex;
                handler(this, args);
            }
        }

        public void RefreshMembersList()
        {
            if (_currentTypeModel == null)
            {
                return;
            }

            _lstMembers.Items.Clear();
            foreach (var member in _currentTypeModel.Members)
            {
                _lstMembers.Items.Add(FormatMemberModel(member));
            }
        }

        public void MarkDirtyFromExternal()
        {
            _isDirty = true;
            if (_currentTypeModel != null)
            {
                _btnApply.Enabled = true;
            }
        }

        private static string GetUniqueMemberName(List<MemberModel> members, string baseName)
        {
            if (members == null || !members.Exists(m => m.Name == baseName))
            {
                return baseName;
            }

            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseName + i;
                if (!members.Exists(m => m.Name == candidate))
                {
                    return candidate;
                }
            }

            return baseName + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        private static string GetUniqueMemberDefName(List<MemberDefinition> members, string baseName)
        {
            if (members == null || !members.Exists(m => m.Name == baseName))
            {
                return baseName;
            }

            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseName + i;
                if (!members.Exists(m => m.Name == candidate))
                {
                    return candidate;
                }
            }

            return baseName + Guid.NewGuid().ToString("N").Substring(0, 4);
        }
    }

    public class TypeModelChangedEventArgs : EventArgs
    {
        public TypeModelChangedEventArgs(TypeModel typeModel)
        {
            TypeModel = typeModel;
            EditMemberIndex = -1;
        }

        public TypeModel TypeModel { get; private set; }

        public int EditMemberIndex { get; set; }

        public List<MemberModel> AddedMembers { get; set; }

        public List<MemberModel> RemovedMembers { get; set; }
    }
}
