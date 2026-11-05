namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// Call Hierarchy (incoming calls) results, shown in a top-level dockable tab rather than a
    /// floating dialog — the sibling of <see cref="ReferencesPanel"/>, converted for the same reason
    /// (iter 368): the caller list stays put while the user reads the code and navigates, sits beside
    /// Compile/Tests/References, and never covers the editor.
    ///
    /// A header names the target and the call-site count; the grid lists Caller / File / Line / Col /
    /// Code, and double-click (or Enter) raises <see cref="NavigateRequested"/> with the exact
    /// path+line+column so the host lands the caret on the call site (column-aware nav from iter 366).
    /// </summary>
    public class CallHierarchyPanel : UserControl
    {
        private ListView _listView;
        private Label _emptyLabel;
        private Label _header;

        /// <summary>Raised with (path, line, column) — all 1-based — when a caller row is activated.</summary>
        public event Action<string, int, int> NavigateRequested;

        public CallHierarchyPanel()
        {
            InitializeControls();
        }

        /// <summary>Populate the panel with the incoming calls to <paramref name="targetName"/>.</summary>
        public void ShowCallers(string targetName, IList<CallerHit> callers)
        {
            _listView.BeginUpdate();
            try
            {
                _listView.Items.Clear();
                if (callers != null)
                {
                    foreach (CallerHit c in callers)
                    {
                        var item = new ListViewItem(c.CallerLabel ?? "<module>");
                        item.SubItems.Add(Path.GetFileName(c.Path ?? string.Empty));
                        item.SubItems.Add(c.Line.ToString());
                        item.SubItems.Add(c.Column.ToString());
                        item.SubItems.Add(c.Preview ?? string.Empty);
                        item.ToolTipText = c.Path;
                        item.Tag = c;
                        _listView.Items.Add(item);
                    }
                }

                int count = callers != null ? callers.Count : 0;
                _header.Text = string.Format("Callers of '{0}'  —  {1} call site{2}",
                    targetName ?? string.Empty, count, count == 1 ? "" : "s");
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
            _header.Text = "Call Hierarchy — Incoming Calls";
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

            _listView.Columns.Add(new ColumnHeader { Text = "Caller", Width = 220 });
            _listView.Columns.Add(new ColumnHeader { Text = "File", Width = 180 });
            _listView.Columns.Add(new ColumnHeader { Text = "Line", Width = 55, TextAlign = HorizontalAlignment.Right });
            _listView.Columns.Add(new ColumnHeader { Text = "Col", Width = 50, TextAlign = HorizontalAlignment.Right });
            _listView.Columns.Add(new ColumnHeader { Text = "Code", Width = 420 });

            SortableListView.Enable(_listView, 2, 3); // Line and Col are numeric
            ListViewClipboard.AttachCopyMenu(_listView);

            _header = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Text = "Call Hierarchy — Incoming Calls",
            };

            Controls.Add(_listView);
            Controls.Add(_header);
            ResultsToolbar.Attach(this, "call hierarchy",
                () => HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_listView, false), Clear);

            _emptyLabel = ListEmptyState.Attach(_listView,
                "No incoming calls." + Environment.NewLine + Environment.NewLine +
                "Right-click a function or method and choose Call Hierarchy.");

            ResumeLayout(false);
        }

        private void ActivateSelected()
        {
            if (_listView.SelectedItems.Count == 0) return;
            if (!(_listView.SelectedItems[0].Tag is CallerHit c)) return;
            Action<string, int, int> nav = NavigateRequested;
            if (nav != null) nav(c.Path, c.Line, c.Column);
        }
    }
}
