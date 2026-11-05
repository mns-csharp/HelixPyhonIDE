namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    /// <summary>
    /// Small dialog for creating a nested type inside an existing parent.
    /// User picks the kind (class/abstract/interface/struct) and the name.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class AddNestedTypeDialog : LunaForm
    {
        public AddNestedTypeDialog()
        {
            InitializeComponent();
        }

        private readonly TypeModel _parent;





        // Results the caller reads after DialogResult.OK: the chosen kind and validated name.
        public TypeKind SelectedKind { get; private set; }
        public string TypeName { get; private set; }

        // Real constructor: chains the designer ctor, then records the parent the nested type belongs to.
        public AddNestedTypeDialog(TypeModel parent) : this()
        {
            if (parent == null) throw new ArgumentNullException("parent");
            _parent = parent;
        }


        private void OnOk(object sender, EventArgs e)
        {
            // Validate the name is a legal C# identifier before accepting; keep dialog open on failure.
            string name = (_nameBox.Text ?? string.Empty).Trim();
            if (!Identifiers.IsValid(name))
            {
                MessageBox.Show(this, "Enter a valid C# identifier.", "Invalid Name",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Map the combo box selection order to the TypeKind enum; default to a concrete class.
            switch (_kindCombo.SelectedIndex)
            {
                case 1: SelectedKind = TypeKind.AbstractClass; break;
                case 2: SelectedKind = TypeKind.Interface; break;
                case 3: SelectedKind = TypeKind.Struct; break;
                default: SelectedKind = TypeKind.ConcreteClass; break;
            }
            TypeName = name;
            DialogResult = DialogResult.OK;
            Close();
        }

        // Identifier check: first char letter/underscore, remainder letters/digits/underscore.
    }
}
