namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// Find All References results, shown in a top-level dockable tab (like Compile / Tests) rather
    /// than a floating dialog — the user asked for a tab so the results stay put while they read the
    /// code, sort alongside the other panels, and don't cover the editor (iter 367).
    ///
    /// A header row names the symbol and the reference/file counts; the grid lists File / Line / Col /
    /// Code, and double-click (or Enter) raises <see cref="NavigateRequested"/> with the exact
    /// path+line+column so the host lands the caret on the token (reuses the column-aware navigation
    /// from iter 366).
    /// </summary>
    public class ReferencesPanel : UserControl
    {
        private ListView _listView;
        private Label _emptyLabel;
        private Label _header;

        /// <summary>Raised with (path, line, column) — all 1-based — when a reference is activated.</summary>
        public event Action<string, int, int> NavigateRequested;

        public ReferencesPanel()
        {
            InitializeControls();
        }

        /// <summary>Populate the panel with the references for <paramref name="symbol"/>.</summary>
        public void ShowReferences(string symbol, IList<ReferenceHit> hits)
        {
            _listView.BeginUpdate();
            try
            {
                _listView.Items.Clear();
                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (hits != null)
                {
                    foreach (ReferenceHit h in hits)
                    {
                        files.Add(h.Path ?? string.Empty);
                        var item = new ListViewItem(Path.GetFileName(h.Path ?? string.Empty));
                        item.SubItems.Add(h.Line.ToString());
                        item.SubItems.Add(h.Column.ToString());
                        item.SubItems.Add(h.Preview ?? string.Empty);
                        item.ToolTipText = h.Path;
                        item.Tag = h;
                        _listView.Items.Add(item);
                    }
                }

                int count = hits != null ? hits.Count : 0;
                _header.Text = string.Format("'{0}'  —  {1} reference{2} in {3} file{4}",
                    symbol ?? string.Empty, count, count == 1 ? "" : "s",
                    files.Count, files.Count == 1 ? "" : "s");
            }
            finally
            {
                _listView.EndUpdate();
            }
            SortableListView.ReapplySort(_listView);
            if (_listView.Items.Count > 0) _listView.Items[0].Selected = true;
            ListEmptyState.Refresh(_listView, _emptyLabel);
        }

        public void Clear()
        {
            _listView.Items.Clear();
            _header.Text = "Find All References";
            SortableListView.ResetSort(_listView);
            ListEmptyState.Refresh(_listView, _emptyLabel);
        }

        private void InitializeControls()
        {
            SuspendLayout();

            _listView = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true,
            };
            _listView.DoubleClick += (s, e) => ActivateSelected();
            _listView.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { ActivateSelected(); e.Handled = true; } };

            _listView.Columns.Add(new ColumnHeader { Text = "File", Width = 220 });
            _listView.Columns.Add(new ColumnHeader { Text = "Line", Width = 55, TextAlign = HorizontalAlignment.Right });
            _listView.Columns.Add(new ColumnHeader { Text = "Col", Width = 50, TextAlign = HorizontalAlignment.Right });
            _listView.Columns.Add(new ColumnHeader { Text = "Code", Width = 520 });

            SortableListView.Enable(_listView, 1, 2); // Line and Col are numeric
            ListViewClipboard.AttachCopyMenu(_listView);

            _header = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Text = "Find All References",
            };

            Controls.Add(_listView);
            Controls.Add(_header);
            ResultsToolbar.Attach(this, "references",
                () => HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_listView, false), Clear);

            _emptyLabel = ListEmptyState.Attach(_listView,
                "No references." + Environment.NewLine + Environment.NewLine +
                "Right-click a symbol and choose Find All References, or press Shift+F12.");

            ResumeLayout(false);
        }

        private void ActivateSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            if (!(_listView.SelectedItems[0].Tag is ReferenceHit h)) return;
            Action<string, int, int> nav = NavigateRequested;
            if (nav != null) nav(h.Path, h.Line, h.Column);
        }
    }
}
