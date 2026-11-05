using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using HelixPythonNamespace.Core.Extensibility;
using HelixPythonNamespace.Core.Languages;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Dialogs
{
    public sealed class ProjectPropertyActions
    {
        public Action OpenManifest { get; set; }
        public Action OpenBuildSettings { get; set; }
        public Action RestorePackages { get; set; }
        public Action ManagePackages { get; set; }
    }

    public interface IProjectPropertyPage
    {
        bool Supports(ProjectModel project);
        string Title { get; }
        Control CreateControl(ProjectModel project, ProjectPropertyActions actions);
    }

    /// <summary>
    /// Language-aware project property pages. Pages are ordered contributions, mirroring the
    /// useful part of SharpDevelop's property-page extension path while remaining ordinary
    /// WinForms controls owned by HelixPythonNamespace.
    /// </summary>
    public sealed class ProjectPropertiesDialog : LunaForm
    {
        private readonly TabControl _pages = new TabControl { Dock = DockStyle.Fill };
        private readonly ContributionRegistry<IProjectPropertyPage> _registry =
            new ContributionRegistry<IProjectPropertyPage>();

        public ProjectPropertiesDialog(ProjectModel project, ProjectPropertyActions actions,
            IEnumerable<Tuple<string, IProjectPropertyPage, int>> additionalPages = null)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            Text = project.Name + " — Project Properties";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(680, 480);
            MinimumSize = new Size(560, 360);
            Controls.Add(_pages);

            _registry.Register("general", new GeneralPropertyPage(), 100);
            _registry.Register("build", new BuildPropertyPage(), 50);
            _registry.Register("dependencies", new DependenciesPropertyPage(), 25);
            if (additionalPages != null)
                foreach (Tuple<string, IProjectPropertyPage, int> page in additionalPages)
                    _registry.Register(page.Item1, page.Item2, page.Item3);

            foreach (IProjectPropertyPage page in _registry.Snapshot())
            {
                if (!page.Supports(project)) continue;
                var tab = new TabPage(page.Title);
                Control content = page.CreateControl(project, actions ?? new ProjectPropertyActions());
                if (content != null) { content.Dock = DockStyle.Fill; tab.Controls.Add(content); }
                _pages.TabPages.Add(tab);
            }
        }

        public IReadOnlyList<string> PageTitles
        {
            get { return _pages.TabPages.Cast<TabPage>().Select(p => p.Text).ToList(); }
        }

        private sealed class GeneralPropertyPage : IProjectPropertyPage
        {
            public bool Supports(ProjectModel project) { return true; }
            public string Title { get { return "General"; } }
            public Control CreateControl(ProjectModel project, ProjectPropertyActions actions)
            {
                var panel = PagePanel();
                AddValue(panel, "Name", project.Name, 12);
                AddValue(panel, "Language", LanguageDescriptors.For(project.Language).DisplayName, 66);
                AddValue(panel, "Kind", project.Kind.ToString(), 120);
                AddValue(panel, "Project file", project.FilePath, 174);
                var open = Button("Open Project File", 228, actions.OpenManifest);
                panel.Controls.Add(open);
                return panel;
            }
        }

        private sealed class BuildPropertyPage : IProjectPropertyPage
        {
            public bool Supports(ProjectModel project) { return true; }
            public string Title { get { return "Build & Toolchain"; } }
            public Control CreateControl(ProjectModel project, ProjectPropertyActions actions)
            {
                var panel = PagePanel();
                AddValue(panel, "Target", string.IsNullOrEmpty(project.TargetFramework) ? "Default" : project.TargetFramework, 12);
                AddValue(panel, "Startup", project.IsStartup ? "Yes" : "No", 66);
                Label note = new Label
                {
                    AutoSize = true, Location = new Point(16, 126), MaximumSize = new Size(590, 0),
                    Text = project.Language == HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp
                        ? "Configure CMake generator and native compiler discovery for this workspace."
                        : "Build commands and tool availability are supplied by the registered language connector."
                };
                panel.Controls.Add(note);
                panel.Controls.Add(Button("Build Tool Settings...", 174, actions.OpenBuildSettings));
                return panel;
            }
        }

        private sealed class DependenciesPropertyPage : IProjectPropertyPage
        {
            public bool Supports(ProjectModel project) { return true; }
            public string Title { get { return "Dependencies"; } }
            public Control CreateControl(ProjectModel project, ProjectPropertyActions actions)
            {
                var panel = PagePanel();
                var list = new ListBox { Left = 16, Top = 16, Width = 610, Height = 260, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
                foreach (ProjectModel reference in project.ProjectReferences) list.Items.Add(reference.Name);
                foreach (string unresolved in project.UnresolvedProjectReferences) list.Items.Add(unresolved + " (unresolved)");
                if (list.Items.Count == 0) list.Items.Add("(no project references)");
                panel.Controls.Add(list);
                var descriptor = LanguageDescriptors.For(project.Language);
                var restore = Button(descriptor.RestorePackagesMenuLabel ?? "Restore Packages", 292, actions.RestorePackages);
                restore.Enabled = descriptor.SupportsPackageManager && actions.RestorePackages != null;
                panel.Controls.Add(restore);
                if (descriptor.ManagePackagesMenuLabel != null)
                    panel.Controls.Add(Button(descriptor.ManagePackagesMenuLabel, 332, actions.ManagePackages));
                return panel;
            }
        }

        private static Panel PagePanel() { return new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) }; }

        private static void AddValue(Control parent, string name, string value, int top)
        {
            parent.Controls.Add(new Label { Text = name, Left = 16, Top = top, Width = 120 });
            parent.Controls.Add(new TextBox { Text = value ?? string.Empty, Left = 142, Top = top - 3, Width = 484, ReadOnly = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right });
        }

        private static Button Button(string text, int top, Action action)
        {
            var button = new Button { Text = text, Left = 16, Top = top, Width = 210, Height = 30, Enabled = action != null };
            if (action != null) button.Click += delegate { action(); };
            return button;
        }
    }
}
