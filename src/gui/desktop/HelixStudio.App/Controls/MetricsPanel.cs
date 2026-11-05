namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Windows.Forms;
    using HelixPythonNamespace.Metrics;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// Displays a <see cref="MetricsReport"/> across three tabs: per-file size metrics,
    /// per-class metrics, and per-function complexity metrics.
    /// </summary>
    public sealed class MetricsPanel : UserControl
    {
        private readonly ListView _files;
        private readonly ListView _classes;
        private readonly ListView _functions;
        private Label _filesEmpty, _classesEmpty, _functionsEmpty;

        /// <summary>Raised when a class/function row is activated: file path + line.</summary>
        public event Action<string, int> ItemActivated;

        public MetricsPanel()
        {
            var tabs = new TabControl { Dock = DockStyle.Fill };

            // One ListView per tab; column 0 is the entity name (widened by MakeList).
            _files = MakeList("File", "Total", "Code", "Comment", "Blank", "Funcs", "Classes");
            _classes = MakeList("Class", "File", "Line", "Methods", "LOC");
            _functions = MakeList("Function", "File", "Line", "LOC", "Stmts", "Params", "Complexity", "MaxNest");

            // Mark the numeric columns so sorting compares magnitudes, not text.
            SortableListView.Enable(_files, 1, 2, 3, 4, 5, 6);
            SortableListView.Enable(_classes, 2, 3, 4);
            SortableListView.Enable(_functions, 2, 3, 4, 5, 6, 7);

            // Only class/function rows carry a source location (Loc Tag), so only they
            // are navigable on double-click or Enter; the Files tab has no per-row location.
            _classes.DoubleClick += (s, e) => Activate(_classes);
            _functions.DoubleClick += (s, e) => Activate(_functions);
            _classes.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Activate(_classes); e.Handled = true; } };
            _functions.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Activate(_functions); e.Handled = true; } };

            tabs.TabPages.Add(WrapTab("Files", _files));
            tabs.TabPages.Add(WrapTab("Classes", _classes));
            tabs.TabPages.Add(WrapTab("Functions", _functions));
            Controls.Add(tabs);
            // Three grids behind sub-tabs, so Copy/Save emit all three under section headers
            // rather than only whichever tab happens to be selected.
            ResultsToolbar.Attach(this, "metrics", () =>
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("== Files ==");
                sb.AppendLine(HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_files, false));
                sb.AppendLine("== Classes ==");
                sb.AppendLine(HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_classes, false));
                sb.AppendLine("== Functions ==");
                sb.AppendLine(HelixPythonNamespace.UI.Controls.ListViewClipboard.BuildText(_functions, false));
                return sb.ToString();
            }, Clear);

            // All three tabs open as blank ruled grids until Compute Metrics runs — say so, and say
            // how to fill them, rather than looking broken. Attached after WrapTab so each label has
            // the list's parent to sit in.
            const string How = "Run Metrics → Compute Metrics (Ctrl+Shift+M) to measure the solution.";
            _filesEmpty = ListEmptyState.Attach(_files,
                "No file metrics yet." + Environment.NewLine + Environment.NewLine + How);
            _classesEmpty = ListEmptyState.Attach(_classes,
                "No class metrics yet." + Environment.NewLine + Environment.NewLine + How);
            _functionsEmpty = ListEmptyState.Attach(_functions,
                "No function metrics yet." + Environment.NewLine + Environment.NewLine + How);
        }

        // Refresh all three tabs from one report.
        public void Show(MetricsReport report)
        {
            PopulateFiles(report);
            PopulateClasses(report);
            PopulateFunctions(report);
            RefreshEmptyStates();
        }

        public void Clear()
        {
            _files.Items.Clear();
            _classes.Items.Clear();
            _functions.Items.Clear();
            SortableListView.ResetSort(_files);
            SortableListView.ResetSort(_classes);
            SortableListView.ResetSort(_functions);
            RefreshEmptyStates();
        }

        private void RefreshEmptyStates()
        {
            ListEmptyState.Refresh(_files, _filesEmpty);
            ListEmptyState.Refresh(_classes, _classesEmpty);
            ListEmptyState.Refresh(_functions, _functionsEmpty);
        }

        // Rebuild the Files tab: one row per source file with size/count breakdown.
        private void PopulateFiles(MetricsReport report)
        {
            _files.BeginUpdate();
            try
            {
                _files.Items.Clear();
                if (report == null) return;
                foreach (FileMetric m in report.Files)
                {
                    var it = new ListViewItem(Path.GetFileName(m.FilePath));
                    it.SubItems.Add(m.TotalLines.ToString());
                    it.SubItems.Add(m.CodeLines.ToString());
                    it.SubItems.Add(m.CommentLines.ToString());
                    it.SubItems.Add(m.BlankLines.ToString());
                    it.SubItems.Add(m.FunctionCount.ToString());
                    it.SubItems.Add(m.ClassCount.ToString());
                    it.ToolTipText = m.FilePath;
                    _files.Items.Add(it);
                }
            }
            finally { _files.EndUpdate(); }
            SortableListView.ReapplySort(_files);
        }

        // Rebuild the Classes tab. Each row tags a Loc so double-click can navigate to source.
        private void PopulateClasses(MetricsReport report)
        {
            _classes.BeginUpdate();
            try
            {
                _classes.Items.Clear();
                if (report == null) return;
                foreach (ClassMetric m in report.Classes)
                {
                    var it = new ListViewItem(m.Name);
                    it.SubItems.Add(Path.GetFileName(m.FilePath));
                    it.SubItems.Add(m.Line.ToString());
                    it.SubItems.Add(m.MethodCount.ToString());
                    it.SubItems.Add(m.Loc.ToString());
                    it.Tag = new Loc(m.FilePath, m.Line);
                    it.ToolTipText = m.FilePath;
                    _classes.Items.Add(it);
                }
            }
            finally { _classes.EndUpdate(); }
            SortableListView.ReapplySort(_classes);
        }

        // Rebuild the Functions tab: complexity metrics per function, each tagged with its Loc.
        private void PopulateFunctions(MetricsReport report)
        {
            _functions.BeginUpdate();
            try
            {
                _functions.Items.Clear();
                if (report == null) return;
                foreach (FunctionMetric m in report.Functions)
                {
                    var it = new ListViewItem(m.Name);
                    it.SubItems.Add(Path.GetFileName(m.FilePath));
                    it.SubItems.Add(m.Line.ToString());
                    it.SubItems.Add(m.Loc.ToString());
                    it.SubItems.Add(m.StatementCount.ToString());
                    it.SubItems.Add(m.ParameterCount.ToString());
                    it.SubItems.Add(m.CyclomaticComplexity.ToString());
                    it.SubItems.Add(m.MaxNestingDepth.ToString());
                    it.Tag = new Loc(m.FilePath, m.Line);
                    it.ToolTipText = m.FilePath;
                    _functions.Items.Add(it);
                }
            }
            finally { _functions.EndUpdate(); }
            SortableListView.ReapplySort(_functions);
        }

        // Raise ItemActivated for the selected row of the given list (Classes or Functions).
        private void Activate(ListView lv)
        {
            if (lv.SelectedItems.Count == 0) return;
            var loc = lv.SelectedItems[0].Tag as Loc;
            if (loc == null) return;
            Action<string, int> h = ItemActivated;
            if (h != null) h(loc.File, loc.Line);
        }

        // Factory for a details ListView. The first column (entity name) gets a wider
        // 180px width; the rest are narrow 80px numeric/short columns.
        private static ListView MakeList(params string[] columns)
        {
            var lv = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Dock = DockStyle.Fill,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true
            };
            foreach (string c in columns)
                lv.Columns.Add(new ColumnHeader { Text = c, Width = c == columns[0] ? 180 : 80 });
            ListViewClipboard.AttachCopyMenu(lv);
            return lv;
        }

        // Wrap a content control in a filled TabPage.
        private static TabPage WrapTab(string title, Control content)
        {
            var tab = new TabPage(title);
            content.Dock = DockStyle.Fill;
            tab.Controls.Add(content);
            return tab;
        }

        // Immutable file+line pair stashed on class/function rows for navigation.
        private sealed class Loc
        {
            public readonly string File;
            public readonly int Line;
            public Loc(string file, int line) { File = file; Line = line; }
        }
    }
}
