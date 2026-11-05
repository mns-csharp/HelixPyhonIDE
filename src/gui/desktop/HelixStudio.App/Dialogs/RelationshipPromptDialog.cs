namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;

    /// <summary>
    /// Prompts the user to disambiguate a relationship between two types when the Link
    /// auto-detection cannot decide (two concrete classes, two interfaces, etc.).
    /// Returns the chosen base type, derived type, and relationship kind.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class RelationshipPromptDialog : LunaForm
    {
        public RelationshipPromptDialog()
        {
            InitializeComponent();
        }

        private readonly TypeModel _a;
        private readonly TypeModel _b;





        // Outputs read by the caller after OK: the base/derived roles and the chosen relationship kind.
        public TypeModel ChosenBase { get; private set; }
        public TypeModel ChosenDerived { get; private set; }
        public RelationshipKind ChosenKind { get; private set; }

        public RelationshipPromptDialog(TypeModel a, TypeModel b) : this()
        {
            if (a == null) throw new ArgumentNullException("a");
            if (b == null) throw new ArgumentNullException("b");
            _a = a;
            _b = b;
        }


        private void OnOk(object sender, EventArgs e)
        {
            // Radio selection decides which type is the base; the other becomes the derived type.
            if (_radioA.Checked) { ChosenBase = _a; ChosenDerived = _b; }
            else { ChosenBase = _b; ChosenDerived = _a; }
            // Combo selects the relationship kind: Implements for index 1, else Inherits.
            ChosenKind = _kindCombo.SelectedIndex == 1
                ? RelationshipKind.Implements
                : RelationshipKind.Inherits;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
