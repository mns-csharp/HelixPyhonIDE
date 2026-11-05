using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.App.Dialogs
{
    // Checked-list dialog for picking which sibling projects the target project should reference.
    [System.ComponentModel.DesignerCategory("Form")]
    public sealed partial class AddProjectReferenceDialog : LunaForm
    {
        public AddProjectReferenceDialog()
        {
            InitializeComponent();
        }

        private readonly ProjectModel _target;
        // Candidate list parallels the checked-list items by index; used to map selections back to models.
        private readonly List<ProjectModel> _candidates = new List<ProjectModel>();




        public AddProjectReferenceDialog(SolutionModel solution, ProjectModel target) : this()
        {
            if (solution == null) throw new ArgumentNullException("solution");
            if (target == null) throw new ArgumentNullException("target");

            _target = target;
            // Build the candidate set: every project except the target itself and those already referenced.
            foreach (ProjectModel project in solution.Projects)
            {
                if (project == target) continue;
                if (target.ProjectReferences.Contains(project)) continue;
                _candidates.Add(project);
            }

            _header.Text = string.Format("Select projects to reference from \"{0}\":", target.Name);
            foreach (ProjectModel project in _candidates) _list.Items.Add(project.Name);
            // Nothing to reference: explain and disable OK so the dialog can only be cancelled.
            if (_candidates.Count == 0) { _header.Text = "No other projects available to reference."; _okButton.Enabled = false; }
        }

        // Translate checked indices back into the ProjectModel instances the caller applies.
        public List<ProjectModel> SelectedProjects
        {
            get
            {
                List<ProjectModel> result = new List<ProjectModel>();
                for (int i = 0; i < _list.CheckedIndices.Count; i++)
                {
                    int idx = _list.CheckedIndices[i];
                    result.Add(_candidates[idx]);
                }
                return result;
            }
        }


        private void OnOkClick(object sender, EventArgs e)
        {
            // Guard: require at least one checked item. Suppress the OK close by resetting DialogResult.
            if (_list.CheckedIndices.Count == 0)
            {
                DialogResult = DialogResult.None;
                MessageBox.Show("Select at least one project.", "Nothing Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
