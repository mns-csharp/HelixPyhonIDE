using System;
using System.Drawing;
using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// Ask the user for a new name and a rename scope (Solution / Project / Package / File).
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class RenameSymbolDialog : LunaForm
    {
        public RenameSymbolDialog()
        {
            InitializeComponent();
            // Block renaming to a non-identifier (empty, starts with a digit, contains spaces/punctuation)
            // — it would produce uncompilable code. Rename stays disabled until the name is valid.
            _nameBox.TextChanged += delegate { UpdateOkEnabled(); };
            UpdateOkEnabled();
        }

        private void UpdateOkEnabled()
        {
            renameButton.Enabled = IsValidIdentifier(NewName);
        }

        /// <summary>
        /// A syntactically valid identifier: non-empty, starts with a letter or underscore, and
        /// otherwise contains only letters, digits, or underscores (Unicode-aware, matching Python 3).
        /// </summary>
        internal static bool IsValidIdentifier(string name) => Identifiers.IsValid(name);





        // The trimmed replacement name entered by the user; read by the caller after OK.
        public string NewName { get { return _nameBox.Text.Trim(); } }

        // Map the checked radio button to the rename scope; Solution-wide is the default.
        public RenameScope SelectedScope
        {
            get
            {
                if (_projectRadio.Checked) return RenameScope.Project;
                if (_packageRadio.Checked) return RenameScope.Package;
                if (_fileRadio.Checked) return RenameScope.File;
                return RenameScope.Solution;
            }
        }

        // Set the title/prompt from the symbol kind and seed the box with the current name (all selected).
        public RenameSymbolDialog(string symbolKind, string oldName) : this()
        {
            Text = "Rename " + (string.IsNullOrEmpty(symbolKind) ? "Symbol" : symbolKind);
            promptLabel.Text = "Rename '" + (oldName ?? string.Empty) + "' to:";
            _nameBox.Text = oldName ?? string.Empty;
            _nameBox.SelectAll();
        }
    }
}
