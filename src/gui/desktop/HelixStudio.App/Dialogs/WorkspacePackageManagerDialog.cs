using System;
using System.Drawing;
using System.Windows.Forms;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.Workspace.LanguageServices;

namespace HelixPythonNamespace.App.Dialogs
{
    /// <summary>Manifest-native dependency editor for a workspace whose package manager edits the manifest in place.</summary>
    public sealed class WorkspacePackageManagerDialog : LunaForm
    {
        private readonly ProjectModel _project;
        private readonly IPackageManagerAdapter _adapter;
        private readonly ListView _items = new ListView();
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _value = new TextBox();

        public WorkspacePackageManagerDialog(ProjectModel project, IPackageManagerAdapter adapter)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            Text = "Manage Packages — " + project.Name + " (" + adapter.Id + ")";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(680, 470);

            _items.View = View.Details; _items.FullRowSelect = true; _items.HideSelection = false;
            _items.Columns.Add("Package", 330); _items.Columns.Add(adapter.ValueLabel, 240);
            _items.SetBounds(12, 12, 640, 300); _items.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(_items);

            Controls.Add(new Label { Text = "Package", Left = 12, Top = 330, Width = 110 });
            _name.SetBounds(120, 326, 220, 25); Controls.Add(_name);
            Controls.Add(new Label { Text = adapter.ValueLabel, Left = 352, Top = 330, Width = 100 });
            _value.SetBounds(452, 326, 200, 25); Controls.Add(_value);
            var add = new Button { Text = "Add / Update", Left = 120, Top = 365, Width = 130 };
            var remove = new Button { Text = "Remove Selected", Left = 260, Top = 365, Width = 140 };
            var refresh = new Button { Text = "Refresh", Left = 410, Top = 365, Width = 100 };
            add.Click += delegate { if (_adapter.Add(_project.FilePath, _name.Text.Trim(), _value.Text.Trim())) RefreshList(); };
            remove.Click += delegate { if (_items.SelectedItems.Count > 0 && _adapter.Remove(_project.FilePath, (string)_items.SelectedItems[0].Tag)) RefreshList(); };
            refresh.Click += delegate { RefreshList(); };
            Controls.Add(add); Controls.Add(remove); Controls.Add(refresh);
            RefreshList();
        }

        internal void RefreshList()
        {
            _items.Items.Clear();
            foreach (PackageDependencyInfo dependency in _adapter.List(_project.FilePath))
            {
                var item = new ListViewItem(dependency.Name ?? string.Empty) { Tag = dependency.Identity };
                item.SubItems.Add(dependency.Value ?? string.Empty);
                _items.Items.Add(item);
            }
        }

        internal int DependencyCount { get { return _items.Items.Count; } }
    }
}
