namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.Debugger;
    using HelixPythonNamespace.UI.Controls;

    // Lets the user discover, browse for, and pick a Python interpreter for the debugger.
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class SelectPythonInterpreterDialog : LunaForm
    {
        // Discovers installed interpreters across the machine.
        private readonly PythonInterpreterManager _manager = new PythonInterpreterManager();
        public SelectPythonInterpreterDialog() { InitializeComponent(); }
        // Show the currently-selected interpreter path (if any) at the top of the dialog.
        public SelectPythonInterpreterDialog(string currentSelectedPath) : this()
        {
            selectedLabel.Text = string.IsNullOrEmpty(currentSelectedPath) ? "No interpreter selected." : "Current: " + currentSelectedPath;
        }
        // The interpreter the user picked; read by the caller after OK.
        public PythonInterpreter SelectedInterpreter { get; private set; }
        // On load and Refresh: (re)discover interpreters into the list.
        private void OnDialogLoad(object sender, EventArgs e) { Rescan(); }
        private void OnRefreshClick(object sender, EventArgs e) { Rescan(); }
        // Browse: add a manually-located python.exe. Apply: record selection. Save: record and close OK.
        private void OnBrowseClick(object sender, EventArgs e) { BrowseForExe(); }
        private void OnApplyClick(object sender, EventArgs e) { ApplySelection(); }
        private void OnSaveClick(object sender, EventArgs e) { ApplySelection(); DialogResult = DialogResult.OK; }
        // Repopulate the list from a fresh discovery pass; each row's Tag holds its interpreter model.
        private void Rescan()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                interpretersListView.Items.Clear();
                foreach (PythonInterpreter interpreter in _manager.DiscoverAll())
                {
                    var row = new ListViewItem(new[] { interpreter.ExecutablePath, interpreter.Version ?? "", interpreter.Flavor ?? "", interpreter.Architecture ?? "", interpreter.Source ?? "", interpreter.HasPythonNet ? "yes" : "" });
                    row.Tag = interpreter; interpretersListView.Items.Add(row);
                }
                // Pre-select the first interpreter so Apply/Save works without an extra click.
                if (interpretersListView.Items.Count > 0) interpretersListView.Items[0].Selected = true;
            }
            finally { Cursor = Cursors.Default; }
        }
        // Let the user point at a python.exe by hand; add it as a "browse"-sourced row and select it.
        private void BrowseForExe()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Python executable (python.exe)|python.exe|All files (*.*)|*.*"; dialog.Title = "Browse for a Python interpreter";
                if (dialog.ShowDialog(this) != DialogResult.OK || !File.Exists(dialog.FileName)) return;
                var interpreter = new PythonInterpreter { ExecutablePath = dialog.FileName, Flavor = "Other", Source = "browse" };
                var row = new ListViewItem(new[] { interpreter.ExecutablePath, "", interpreter.Flavor, "", interpreter.Source, "" });
                row.Tag = interpreter; interpretersListView.Items.Add(row); row.Selected = true;
            }
        }
        // Record the highlighted row's interpreter as the result and echo it in the header label.
        private void ApplySelection()
        {
            if (interpretersListView.SelectedItems.Count == 0) return;
            SelectedInterpreter = interpretersListView.SelectedItems[0].Tag as PythonInterpreter;
            if (SelectedInterpreter != null) selectedLabel.Text = "Selected: " + SelectedInterpreter.ExecutablePath;
        }
    }
}
