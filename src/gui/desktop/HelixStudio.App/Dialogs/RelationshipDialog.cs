namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model.Enums;

    [System.ComponentModel.DesignerCategory("Form")]

    // Radio-button prompt letting the user pick the kind of relationship to create between two types.
    public partial class RelationshipDialog : LunaForm
    {





        public RelationshipDialog()
        {
            InitializeComponent();
        }

        // Map the checked radio button to the relationship enum; Implements is the default.
        public RelationshipKind SelectedKind
        {
            get
            {
                if (_inheritsRadio.Checked)
                {
                    return RelationshipKind.Inherits;
                }
                if (_dependsOnRadio.Checked)
                {
                    return RelationshipKind.DependsOn;
                }
                return RelationshipKind.Implements;
            }
        }

    }
}
