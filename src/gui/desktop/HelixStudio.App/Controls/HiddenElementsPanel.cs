namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.UI.Projection;

    using HelixPythonNamespace.UI.Controls;
    /// <summary>
    /// Dockable panel listing every type currently hidden from the canvas projection.
    /// Unhide-selected / Unhide-all controls let the user restore them. Events surface
    /// the user's intent to MainForm without owning hide-state directly.
    /// </summary>
    public sealed class HiddenElementsPanel : UserControl
    {
        private ListView _list;
        private Button _unhideSelected;
        private Button _unhideAll;

        /// <summary>User asked to unhide one or more specific types.</summary>
        public event EventHandler<IList<string>> UnhideRequested;

        /// <summary>User asked to unhide everything.</summary>
        public event EventHandler UnhideAllRequested;

        public HiddenElementsPanel()
        {
            BackColor = Color.FromArgb(245, 246, 248);
            BuildControls();
        }

        // Rebuild the list from the projection's current set of hidden type FQNs.
        // Called by MainForm whenever hide-state changes (hide/unhide, solution reload).
        public void Refresh(SolutionModel solution, SolutionProjectionBuilder projection)
        {
            // Suppress intermediate repaints while the list is torn down and rebuilt.
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                if (solution == null || projection == null) return;
                foreach (var fqn in projection.GetHiddenFqns())
                {
                    // Resolve the FQN back to a model so we can show friendly name/kind/namespace.
                    // The type may no longer exist (e.g. deleted after being hidden), in which
                    // case fall back to the raw FQN and leave the other columns blank.
                    TypeModel t = solution.FindType(fqn);
                    var item = new ListViewItem(t != null ? t.Name : fqn);
                    item.SubItems.Add(t != null ? t.TypeKind.ToString() : string.Empty);
                    item.SubItems.Add(t != null ? (t.Namespace ?? string.Empty) : string.Empty);
                    // The FQN is the stable identity used when raising UnhideRequested.
                    item.Tag = fqn;
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
            }
            // Nothing to unhide when the list is empty — disable both buttons.
            _unhideSelected.Enabled = _list.Items.Count > 0;
            _unhideAll.Enabled = _list.Items.Count > 0;
        }

        // Compose the panel: a top header label, a fill ListView, and a bottom button bar.
        // Docked children are added last-to-first in Z-order, so add order matters (see end).
        private void BuildControls()
        {
            Label header = new Label();
            header.Text = "Hidden types — select and Unhide to restore.";
            header.Dock = DockStyle.Top;
            header.Height = 28;
            header.Padding = new Padding(8, 6, 0, 0);
            header.Font = LunaFonts.Sized(0.5f);

            // MultiSelect lets the user pick several types to unhide at once.
            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = true;
            _list.HideSelection = false;
            _list.Font = LunaFonts.Ui;
            _list.Columns.Add("Name", 180);
            _list.Columns.Add("Kind", 110);
            _list.Columns.Add("Namespace", 260);
            ListViewClipboard.AttachCopyMenu(_list);

            var btnPanel = new Panel();
            btnPanel.Dock = DockStyle.Bottom;
            btnPanel.Height = 42;

            _unhideSelected = new Button();
            _unhideSelected.Text = "Unhide Selected";
            _unhideSelected.Size = new Size(140, 28);
            _unhideSelected.Location = new Point(8, 6);
            // Collect the FQNs of the selected rows and raise UnhideRequested. The panel
            // does not mutate hide-state itself — MainForm owns that and will call Refresh.
            _unhideSelected.Click += delegate
            {
                var fqns = new List<string>();
                foreach (ListViewItem it in _list.SelectedItems)
                {
                    var fqn = it.Tag as string;
                    if (!string.IsNullOrEmpty(fqn)) fqns.Add(fqn);
                }
                if (fqns.Count == 0) return;
                var handler = UnhideRequested;
                if (handler != null) handler(this, fqns);
            };

            _unhideAll = new Button();
            _unhideAll.Text = "Unhide All";
            _unhideAll.Size = new Size(120, 28);
            _unhideAll.Location = new Point(156, 6);
            _unhideAll.Click += delegate
            {
                var handler = UnhideAllRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };

            btnPanel.Controls.Add(_unhideSelected);
            btnPanel.Controls.Add(_unhideAll);

            // Add the Fill control first, then the docked edges, so docking resolves with
            // the fill claiming the space left between the top header and bottom button bar.
            Controls.Add(_list);
            Controls.Add(header);
            Controls.Add(btnPanel);
        }
    }
}
