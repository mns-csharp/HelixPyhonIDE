using System;
using System.Drawing;
using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>
    /// Dialog for handling file conflicts between local and external changes.
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class FileConflictDialog : LunaForm
    {
        public FileConflictDialog()
        {
            InitializeComponent();
        }

        private readonly string _filePath;
        private readonly string _message;





        // Populate the path/message labels; null args fall back to generic text.
        public FileConflictDialog(string filePath, string message) : this()
        {
            _filePath = filePath ?? "Unknown file";
            _message = message ?? "A file conflict has been detected.";
            _filePathLabel.Text = _filePath;
            _messageLabel.Text = _message;
        }

        /// <summary>
        /// User chose to keep local changes.
        /// </summary>
        public bool KeepLocalChanges { get; private set; }

        /// <summary>
        /// User chose to load external changes.
        /// </summary>
        public bool LoadExternalChanges { get; private set; }


        // Three mutually exclusive outcomes, each also mapped to a distinct DialogResult so
        // callers can branch on either the bool flags or the returned result.
        private void KeepLocalButton_Click(object sender, EventArgs e)
        {
            KeepLocalChanges = true;
            DialogResult = DialogResult.Yes;
            Close();
        }

        private void LoadExternalButton_Click(object sender, EventArgs e)
        {
            LoadExternalChanges = true;
            DialogResult = DialogResult.No;
            Close();
        }

        private void CancelButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
