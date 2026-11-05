namespace HelixPythonNamespace.UI.Controls
{
    using System.Collections;
    using System.Text;
    using System.Windows.Forms;

    /// <summary>
    /// Adds standard grid affordances to a details <see cref="ListView"/>: Ctrl+A selects every row,
    /// Ctrl+C (or the Copy / Copy All context-menu entries) copies rows as tab-separated text with a
    /// header line — the form that pastes cleanly into a bug report, a spreadsheet or a Markdown
    /// table. Lives in the UI assembly (rather than App) so every results/findings panel across all
    /// three GUI assemblies (Output, Problems, Complexity, Metrics, Profiler, Tests, References, Call
    /// Hierarchy, Dead Code, Lint, Analysis Feedback, CUDA Validation, …) can share it without
    /// duplicating the formatting.
    /// </summary>
    public static class ListViewClipboard
    {
        /// <summary>
        /// Full affordance: a Select All / Copy / Copy All context menu, plus the same Ctrl+A/Ctrl+C
        /// keyboard shortcuts. For a list that already owns its own <see cref="ListView.ContextMenuStrip"/>
        /// (e.g. a "Plot"/"Unhide" action menu), use <see cref="AttachKeyboardShortcuts"/> instead so this
        /// doesn't clobber it.
        /// </summary>
        public static void AttachCopyMenu(ListView list)
        {
            if (list == null) return;

            var menu = new ContextMenuStrip();
            var selectAll = new ToolStripMenuItem("Select All", null, delegate { SelectAll(list); });
            selectAll.ShortcutKeyDisplayString = "Ctrl+A";
            menu.Items.Add(selectAll);
            var copy = new ToolStripMenuItem("Copy", null, delegate { CopyRows(list, selectedOnly: true); });
            copy.ShortcutKeyDisplayString = "Ctrl+C";
            menu.Items.Add(copy);
            menu.Items.Add(new ToolStripMenuItem("Copy All", null, delegate { CopyRows(list, selectedOnly: false); }));
            // Select All / Copy are meaningful only with rows present / a selection; Copy All only with any rows.
            menu.Opening += delegate
            {
                selectAll.Enabled = list.MultiSelect && list.Items.Count > 0;
                copy.Enabled = list.SelectedItems.Count > 0;
                ((ToolStripMenuItem)menu.Items[2]).Enabled = list.Items.Count > 0;
            };
            list.ContextMenuStrip = menu;

            AttachKeyboardShortcuts(list);
        }

        /// <summary>
        /// Ctrl+A selects every row; Ctrl+C copies the selection (or the whole list when nothing is
        /// selected) — wired without touching <see cref="ListView.ContextMenuStrip"/>, for lists that
        /// already have their own right-click menu.
        /// </summary>
        /// <remarks>
        /// This <see cref="ListView.KeyDown"/> subscription is a fallback only: a <see cref="ListView"/>
        /// never claims Ctrl+A/Ctrl+C for itself the way a writable <see cref="TextBoxBase"/> does (see
        /// <see cref="TextBoxBase.ShortcutsEnabled"/>), so when the host form also has a MenuStrip item
        /// bound to the same chord (e.g. Edit ▸ Copy = Ctrl+C), <c>Form.ProcessCmdKey</c> hands the key to
        /// that global menu shortcut FIRST and this KeyDown handler never fires. The host form's
        /// <c>ProcessCmdKey</c> override must call <see cref="TryHandleShortcut"/> on the focused list
        /// before falling through to its menu shortcuts for this to actually take effect.
        /// </remarks>
        public static void AttachKeyboardShortcuts(ListView list)
        {
            if (list == null) return;
            list.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (TryHandleShortcut(list, e.KeyData)) e.Handled = true;
            };
        }

        /// <summary>
        /// If <paramref name="keyData"/> is Ctrl+A or Ctrl+C, performs it on <paramref name="list"/> and
        /// returns true. Meant to be called from a host form's <c>ProcessCmdKey</c> override — ahead of
        /// any global menu shortcut bound to the same chord — for whichever <see cref="ListView"/>
        /// currently has focus; see the remarks on <see cref="AttachKeyboardShortcuts"/> for why that's
        /// necessary.
        /// </summary>
        public static bool TryHandleShortcut(ListView list, Keys keyData)
        {
            if (list == null) return false;
            if (keyData == (Keys.Control | Keys.A)) { SelectAll(list); return true; }
            if (keyData == (Keys.Control | Keys.C)) { CopyRows(list, selectedOnly: list.SelectedItems.Count > 0); return true; }
            return false;
        }

        /// <summary>Selects every row. A no-op on a single-select list, where "select all" is meaningless.</summary>
        public static void SelectAll(ListView list)
        {
            if (list == null || !list.MultiSelect) return;
            list.BeginUpdate();
            try
            {
                foreach (ListViewItem item in list.Items) item.Selected = true;
            }
            finally { list.EndUpdate(); }
        }

        /// <summary>
        /// Tab-separated text of the list: a header line from the column captions, then one line per
        /// row. Column captions are stripped of the ▲/▼ sort glyph so the header stays clean.
        /// </summary>
        public static string BuildText(ListView list, bool selectedOnly)
        {
            if (list == null) return string.Empty;
            var sb = new StringBuilder();

            var cols = new string[list.Columns.Count];
            for (int i = 0; i < list.Columns.Count; i++) cols[i] = StripGlyph(list.Columns[i].Text);
            sb.AppendLine(string.Join("\t", cols));

            IList rows = selectedOnly ? (IList)list.SelectedItems : list.Items;
            foreach (ListViewItem it in rows)
            {
                var cells = new string[it.SubItems.Count];
                for (int i = 0; i < it.SubItems.Count; i++) cells[i] = it.SubItems[i].Text;
                sb.AppendLine(string.Join("\t", cells));
            }
            return sb.ToString();
        }

        private static string StripGlyph(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int idx = text.IndexOf('▲'); // ▲
            if (idx < 0) idx = text.IndexOf('▼'); // ▼
            return idx >= 0 ? text.Substring(0, idx).TrimEnd() : text;
        }

        private static void CopyRows(ListView list, bool selectedOnly)
        {
            string text = BuildText(list, selectedOnly);
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch { /* clipboard access can fail transiently */ }
        }
    }
}
