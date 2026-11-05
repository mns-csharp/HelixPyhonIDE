namespace HelixPythonNamespace.App.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// VS2022-style Reference Manager: categories on the left ("Projects", "Packages"), the
    /// active category's page on the right, OK applies everything at once. Projects lists the
    /// sibling projects of the target's solution; Packages collects language-appropriate
    /// package references (NuGet id / crates.io crate / Maven group:artifact / PEP 508
    /// requirement). C++/CUDA projects have no package ecosystem, so only
    /// the Projects category shows for them (target_link_libraries covers their references).
    /// </summary>
    public sealed class ReferenceManagerForm : LunaForm
    {
        private readonly ProjectModel _target;
        private readonly List<ProjectModel> _candidates = new List<ProjectModel>();
        private readonly List<KeyValuePair<string, string>> _pendingPackages = new List<KeyValuePair<string, string>>();

        private readonly ListBox _categories;
        private readonly Panel _projectsPage;
        private readonly CheckedListBox _projectsList;
        private readonly Panel _packagesPage;
        private readonly TextBox _pkgName;
        private readonly TextBox _pkgVersion;
        private readonly ListBox _pkgPending;

        public ReferenceManagerForm(SolutionModel solution, ProjectModel target)
        {
            if (solution == null) throw new ArgumentNullException("solution");
            if (target == null) throw new ArgumentNullException("target");
            _target = target;

            Text = "Reference Manager — " + target.Name;
            Size = new Size(640, 460);
            MinimumSize = new Size(540, 380);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            Font = LunaFonts.Ui;

            var nav = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10) };
            var cancel = new Button { Text = "Cancel", Width = 92, Height = 28, Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
            var ok = new Button { Text = "OK", Width = 92, Height = 28, Dock = DockStyle.Right, DialogResult = DialogResult.OK };
            nav.Controls.Add(ok); nav.Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            _categories = new ListBox { Dock = DockStyle.Left, Width = 140, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
            _categories.Items.Add("Projects");
            bool hasPackages = PackageEcosystemLabel(target.Language) != null;
            if (hasPackages) _categories.Items.Add("Packages");
            _categories.SelectedIndexChanged += delegate { ShowCategory(); };

            // ---- Projects page ----
            _projectsPage = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            _projectsList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            foreach (ProjectModel project in solution.Projects)
            {
                if (ReferenceEquals(project, target)) continue;
                if (target.ProjectReferences.Contains(project)) continue;
                _candidates.Add(project);
                _projectsList.Items.Add(project.Name);
            }
            var projectsHeader = new Label
            {
                Dock = DockStyle.Top, Height = 30,
                Text = _candidates.Count > 0
                    ? "Projects \"" + target.Name + "\" will reference:"
                    : "No other projects available to reference.",
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _projectsPage.Controls.Add(_projectsList);
            _projectsPage.Controls.Add(projectsHeader);

            // ---- Packages page ----
            _packagesPage = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), Visible = false };
            var pkgHeader = new Label
            {
                Dock = DockStyle.Top, Height = 30,
                Text = hasPackages ? PackageEcosystemLabel(target.Language) : string.Empty,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var entryRow = new Panel { Dock = DockStyle.Top, Height = 32 };
            _pkgName = new TextBox { Dock = DockStyle.Fill };
            _pkgVersion = new TextBox { Dock = DockStyle.Right, Width = 110 };
            var addBtn = new Button { Text = "Add", Dock = DockStyle.Right, Width = 60 };
            addBtn.Click += delegate { AddPendingPackage(); };
            entryRow.Controls.Add(_pkgName);
            entryRow.Controls.Add(_pkgVersion);
            entryRow.Controls.Add(addBtn);
            var versionHint = new Label
            {
                Dock = DockStyle.Top, Height = 22, ForeColor = Color.FromArgb(120, 124, 132),
                Text = "Name on the left, version (optional) on the right — Add queues it; OK applies.",
                TextAlign = ContentAlignment.MiddleLeft,
            };
            _pkgPending = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _packagesPage.Controls.Add(_pkgPending);
            _packagesPage.Controls.Add(versionHint);
            _packagesPage.Controls.Add(entryRow);
            _packagesPage.Controls.Add(pkgHeader);

            var content = new Panel { Dock = DockStyle.Fill };
            content.Controls.Add(_projectsPage);
            content.Controls.Add(_packagesPage);

            Controls.Add(content);
            Controls.Add(_categories);
            Controls.Add(nav);
            _categories.SelectedIndex = 0;
        }

        internal static string PackageEcosystemLabel(ProjectLanguage language)
        {
            switch (language)
            {
                case ProjectLanguage.Python: return "pip requirement, PEP 508 (e.g. requests or requests>=2.31):";
                default: return null;   // C++/CUDA: no package ecosystem — Projects only
            }
        }

        private void ShowCategory()
        {
            bool projects = _categories.SelectedIndex <= 0;
            _projectsPage.Visible = projects;
            _packagesPage.Visible = !projects;
        }

        private void AddPendingPackage()
        {
            string name = _pkgName.Text.Trim();
            if (name.Length == 0) return;
            string version = _pkgVersion.Text.Trim();
            _pendingPackages.Add(new KeyValuePair<string, string>(name, version));
            _pkgPending.Items.Add(version.Length > 0 ? name + "  (" + version + ")" : name);
            _pkgName.Clear();
            _pkgVersion.Clear();
            _pkgName.Focus();
        }

        /// <summary>Checked sibling projects to reference.</summary>
        public List<ProjectModel> SelectedProjects
        {
            get
            {
                var result = new List<ProjectModel>();
                foreach (int idx in _projectsList.CheckedIndices) result.Add(_candidates[idx]);
                return result;
            }
        }

        /// <summary>Queued package references as (name, version-or-empty).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> PendingPackages
        {
            get { return _pendingPackages; }
        }

        // ---- automation hooks (headless tests) ----
        internal int CategoryCount { get { return _categories.Items.Count; } }
        internal void SelectCategory(int index) { _categories.SelectedIndex = index; ShowCategory(); }
        internal bool CheckProject(string name)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (!string.Equals(_candidates[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                _projectsList.SetItemChecked(i, true);
                return true;
            }
            return false;
        }
        internal void QueuePackage(string name, string version)
        {
            _pkgName.Text = name;
            _pkgVersion.Text = version ?? string.Empty;
            AddPendingPackage();
        }
    }
}
