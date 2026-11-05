namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Core.Model;

    /// <summary>
    /// Shown when era-specific codegen policy detects syntax that would not compile
    /// against the target project's LangVersion. Lists the required downgrades; user
    /// accepts (apply + emit) or cancels (no-op).
    /// </summary>
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class CodegenDowngradeDialog : LunaForm
    {
        public CodegenDowngradeDialog()
        {
            InitializeComponent();
        }

        // True only after the user clicks Proceed; caller uses this to decide whether to emit code.
        public bool Accepted { get; private set; }

        public CodegenDowngradeDialog(ProjectModel project, IList<string> downgradeDescriptions) : this()
        {
            // Default to rejected, then fill the UI from the project's TFM and the list of forced downgrades.
            Accepted = false;
            LoadDowngrades(
                project != null ? project.Name : "<project>",
                project != null ? project.TargetFramework : "unknown",
                downgradeDescriptions ?? new List<string>());
        }

        // Render the explanatory heading and the bullet list of downgrades the user must accept.
        private void LoadDowngrades(string projectName, string tfm, IList<string> downgrades)
        {
            headingLabel.Text = string.Format(
                "Project '{0}' targets {1} and cannot compile some of the C# features you're about to emit.",
                projectName, string.IsNullOrEmpty(tfm) ? "an unknown framework" : tfm);
            downgradeListBox.Items.Clear();
            foreach (string downgrade in downgrades) downgradeListBox.Items.Add(downgrade);
        }

        // Proceed: mark accepted and close with OK so the caller applies the downgrades and emits.
        private void OnProceedClick(object sender, EventArgs e)
        {
            Accepted = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
