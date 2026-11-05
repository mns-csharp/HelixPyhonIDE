using System;
using System.Drawing;
using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// Modal prompt shown once per existing per-package <c>pyproject.toml</c>.
    /// Lets the user pick Keep / Merge / Overwrite and optionally latch that
    /// choice for every remaining conflict in the current import.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class PyProjectConflictDialog : LunaForm
    {
        public PyProjectConflictDialog()
        {
            InitializeComponent();
        }

        private readonly string _existingPath;






        // Show the path of the existing pyproject.toml this prompt is about.
        public PyProjectConflictDialog(string existingPath) : this()
        {
            _existingPath = existingPath ?? string.Empty;
            pathLabel.Text = _existingPath;
        }

        // Map the selected radio button to the resolution enum; Keep is the default.
        public PyProjectConflictResolution Resolution
        {
            get
            {
                if (_overwriteRadio.Checked) return PyProjectConflictResolution.Overwrite;
                if (_mergeRadio.Checked) return PyProjectConflictResolution.Merge;
                return PyProjectConflictResolution.Keep;
            }
        }

        // When checked, the caller latches this resolution for every remaining conflict.
        public bool ApplyToAll => _applyAllCheck.Checked;

    }
}
