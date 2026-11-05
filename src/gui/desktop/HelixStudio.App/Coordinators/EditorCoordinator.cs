namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.UI.Projection;
    using HelixPythonNamespace.UI.Rendering;
    using HelixPythonNamespace.Workspace.Services;

    /// <summary>
    /// Owns the embedded Python editor: the editor tab, its owner-drawn sub-tabs
    /// (open/close/rename/pin), opening files, and save/restore of the open-file set
    /// per solution. Methods reference the host form via <c>host</c>; MainForm keeps
    /// thin wrappers for entries other code still calls.
    /// </summary>
    public sealed class EditorCoordinator : ICoordinator
    {
        private MainFormContext _context;
        // Cached reference to the host form; all editor state (tab controls, open-tab
        // maps, status bar) lives on MainForm, so every method reaches through `host`.
        private MainForm host;

        // Centered placeholder shown over the Editor tab when no file is open, so the pane
        // reads as an intentional empty state rather than a blank white void.
        private Label _editorEmptyLabel;

        // Last project-kind/def-main() violation actually surfaced per project (FilePath ->
        // "reason|offendingFile", or absent if compliant) — see OnEditorBeforeSave. Without this,
        // the check re-ran and re-announced on EVERY autosave (~400ms per keystroke) of ANY file
        // in the project, including files with nothing to do with the violation, which read as
        // the same error message "randomly" reappearing while typing something unrelated.
        private readonly System.Collections.Generic.Dictionary<string, string> _lastMainParityViolation =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Autosave fires on the UI thread after a 400 ms typing pause. Re-parsing the file,
        // rebinding the solution and rebuilding the hidden canvas there made every typing burst
        // look like an IDE freeze. Remember saved files while the Editor owns the screen and apply
        // their model/diagram refresh only when the user actually opens Class Designer Canvas.
        private readonly HashSet<string> _pendingCanvasRefreshFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Coordinators are attached once by MainForm after its fields exist; we grab
        // the host here so subsequent event handlers can touch its controls.
        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        // Drop references so the coordinator doesn't pin the form after teardown.
        public void Detach()
        {
            _pendingCanvasRefreshFiles.Clear();
            _context = null;
            host = null;
        }

        // Builds the top-level "Editor" tab and the owner-drawn inner TabControl that
        // hosts one sub-tab per open file. Called during MainForm construction.
        public void InitializeEditorTab()
        {
            // Composited: this strip is owner-drawn AND lights its close button on hover, so the
            // native hot-track repaint would otherwise show through for a frame on every mouse
            // move. Same fix, same reason, as the tool-panel strip.
            host._editorInnerTabControl = new HelixPythonNamespace.UI.Controls.CompositedTabControl();
            host._editorInnerTabControl.Dock = DockStyle.Fill;
            host._editorInnerTabControl.Font = LunaFonts.Sized(-0.5f);
            // The tab shows a bare file name; the tooltip shows where that file actually is. Two
            // projects with a main.py are indistinguishable on the strip otherwise. EditorTabPage
            // has been putting the full path in ToolTipText all along — the strip simply never had
            // tooltips turned on, so nothing was ever shown.
            host._editorInnerTabControl.ShowToolTips = true;
            ConfigureEditorTabStrip(host._editorInnerTabControl);
            // DrawItem paints each tab; MouseDown handles right-click context menu;
            // MouseClick handles the close-icon hit-test and middle-click close.
            host._editorInnerTabControl.DrawItem += OnEditorInnerTabDrawItem;
            host._editorInnerTabControl.MouseDown += OnEditorInnerTabMouseDown;
            host._editorInnerTabControl.MouseMove += OnEditorInnerTabMouseMove;
            host._editorInnerTabControl.MouseLeave += OnEditorInnerTabMouseLeave;
            host._editorInnerTabControl.MouseClick += OnEditorInnerTabMouseClick;
            // Refresh the window title bar + Ln/Col readout when the active document changes.
            host._editorInnerTabControl.SelectedIndexChanged += delegate { host.UpdateTitle(); host.UpdateCaretStatus(); host.UpdateEolStatus(); host.SyncTreeToActiveDocument(); ApplyNotebookMode(); host.UpdateCanvasScopedToolbarEnabled(); };
            host._editorTab = new TabPage("Editor");
            // No tooltip. The tab is labelled "Editor"; a tip that lists the editor's features is
            // a paragraph hanging off the strip, and it says nothing the label did not.
            host._editorTab.ToolTipText = null;
            host._editorTab.Controls.Add(host._editorInnerTabControl);

            // Empty-state placeholder: overlays the inner tab control (both Dock=Fill) and is
            // shown/hidden by UpdateEditorEmptyState based on the open-tab count, so the Editor
            // tab never reads as a blank white void when no file is open.
            _editorEmptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "No file open.\r\n\r\nDouble-click a type in the Solution Explorer, "
                     + "or use File → Open File in Editor (Ctrl+E).",
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = LunaFonts.Sized(1.5f),
                BackColor = SystemColors.Window,
            };
            host._editorTab.Controls.Add(_editorEmptyLabel);

            host._mainTabControl.TabPages.Add(host._editorTab);
            // Register in _permanentTabs so it's treated as a fixed top-level tab, and
            // under the "editor" key so host.ShowTab("editor") can re-add it if closed.
            host._permanentTabs.Add(host._editorTab);
            host.RegisterTopLevelTab("editor", host._editorTab);

            UpdateEditorEmptyState();
        }

        /// <summary>
        /// Forces a TabControl's native handle so <c>TabPages.Insert</c> actually registers pages.
        /// Reading <c>Handle</c> creates it even when the control is hidden, which
        /// <c>CreateControl()</c> refuses to do.
        /// </summary>
        internal static void EnsureRealized(TabControl tabs)
        {
            if (tabs == null || tabs.IsHandleCreated) return;
            IntPtr forceCreate = tabs.Handle;
            GC.KeepAlive(forceCreate);
        }

        // Show the "no file open" placeholder only while the editor has zero tabs. The label and the
        // inner tab control both Dock=Fill, so exactly ONE may be Visible at a time — two visible
        // Fill siblings fight over the same area and the back-most one collapses the other to 0px.
        public void UpdateEditorEmptyState()
        {
            if (_editorEmptyLabel == null || host._editorInnerTabControl == null) return;
            bool empty = host._editorInnerTabControl.TabPages.Count == 0;
            _editorEmptyLabel.Visible = empty;
            host._editorInnerTabControl.Visible = !empty;
        }

        // Right-click on an editor sub-tab opens its context menu. Left as a MouseDown
        // (not MouseClick) so the menu pops on press, matching native tab UX.
        public void OnEditorInnerTabMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || host._editorInnerTabControl == null) return;
            // Find which editor sub-tab the click hit.
            for (int i = 0; i < host._editorInnerTabControl.TabPages.Count; i++)
            {
                if (!host._editorInnerTabControl.GetTabRect(i).Contains(e.Location)) continue;
                HelixPythonNamespace.Editor.EditorTabPage tab =
                    host._editorInnerTabControl.TabPages[i] as HelixPythonNamespace.Editor.EditorTabPage;
                if (tab == null) return;
                ShowEditorTabContextMenu(tab, e.Location);
                return;
            }
        }

        // Builds and shows the per-tab context menu (path ops, rename, close family, pin).
        // Menu items are built fresh each time so their enabled-state reflects the
        // current file (e.g. Open File Location only when the file exists on disk).
        public void ShowEditorTabContextMenu(HelixPythonNamespace.Editor.EditorTabPage tab, Point at)
        {
            string path = tab.FilePath ?? string.Empty;
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem copyPath = new ToolStripMenuItem("Copy Full Path");
            copyPath.Click += delegate
            {
                try { if (!string.IsNullOrEmpty(path)) Clipboard.SetText(path); }
                catch { /* clipboard access can fail transiently */ }
            };
            menu.Items.Add(copyPath);

            // Path relative to the solution folder — the form that travels well in bug reports and
            // imports (matches the Solution Explorer's Copy Relative Path). Disabled with no solution.
            string solutionDir = host._currentSolution != null && !string.IsNullOrEmpty(host._currentSolution.FilePath)
                ? Path.GetDirectoryName(host._currentSolution.FilePath) : null;
            ToolStripMenuItem copyRel = new ToolStripMenuItem("Copy Relative Path");
            copyRel.Enabled = !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(solutionDir);
            copyRel.Click += delegate
            {
                try { Clipboard.SetText(RelativeToSolution(solutionDir, path)); }
                catch { /* clipboard access can fail transiently */ }
            };
            menu.Items.Add(copyRel);

            // "View Full Path" — no-op click; the hover tooltip reveals the path.
            ToolStripMenuItem viewPath = new ToolStripMenuItem("View Full Path");
            viewPath.ToolTipText = string.IsNullOrEmpty(path) ? "(no path)" : path;
            menu.Items.Add(viewPath);

            // Reveals the file in Explorer with it pre-selected (/select). Disabled when
            // the tab has no on-disk path yet (unsaved/renamed away).
            ToolStripMenuItem openLoc = new ToolStripMenuItem("Open File Location");
            openLoc.Enabled = !string.IsNullOrEmpty(path) && File.Exists(path);
            openLoc.Click += delegate
            {
                // Shared reveal path: shell API first (handles >MAX_PATH), /select fallback.
                HelixPythonNamespace.UI.Controls.ProjectTreePanel.RevealInExplorer(path);
            };
            menu.Items.Add(openLoc);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem rename = new ToolStripMenuItem("Rename…");
            rename.Click += delegate { RenameEditorTabFile(tab); };
            menu.Items.Add(rename);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem close = new ToolStripMenuItem("Close");
            close.Click += delegate { CloseEditorTab(tab); };
            menu.Items.Add(close);

            ToolStripMenuItem closeOthers = new ToolStripMenuItem("Close All Others");
            closeOthers.Click += delegate { CloseEditorTabsExcept(tab); };
            menu.Items.Add(closeOthers);

            // "Close Tabs to the Right" — trims the tab strip back to this tab (VS/VS Code staple).
            // Disabled when nothing unpinned sits to the right, so it never reads as a no-op.
            ToolStripMenuItem closeRight = new ToolStripMenuItem("Close Tabs to the Right");
            closeRight.Enabled = HasEditorTabsToRight(tab);
            closeRight.Click += delegate { CloseEditorTabsToRight(tab); };
            menu.Items.Add(closeRight);

            ToolStripMenuItem closeAll = new ToolStripMenuItem("Close All");
            closeAll.Click += delegate { CloseAllEditorTabs(); };
            menu.Items.Add(closeAll);

            menu.Items.Add(new ToolStripSeparator());

            // Pin/unpin toggle. Pinned tabs are protected from bulk-close and always
            // sort to the left (see the insert logic in OpenInEditor).
            ToolStripMenuItem pin = new ToolStripMenuItem(tab.IsPinned ? "Unpin" : "Pin");
            pin.Click += delegate { tab.IsPinned = !tab.IsPinned; };
            menu.Items.Add(pin);

            menu.Show(host._editorInnerTabControl, at);
        }

        // Closes a single editor sub-tab: flushes its debounced autosave, drops it from
        // the open-tab map, disposes it, and re-measures the remaining tab widths.
        public void CloseEditorTab(HelixPythonNamespace.Editor.EditorTabPage tab)
        {
            if (tab == null || host._editorInnerTabControl == null) return;
            if (!host._editorInnerTabControl.TabPages.Contains(tab)) return;
            // Flush pending autosave so edits typed within the debounce window
            // aren't lost when the user closes the tab immediately after typing.
            try { tab.FlushPendingSave(); } catch { }
            string key = tab.FilePath != null ? Path.GetFullPath(tab.FilePath) : null;
            host._editorInnerTabControl.TabPages.Remove(tab);
            if (!string.IsNullOrEmpty(key)) host._openEditorTabs.Remove(key);
            tab.Dispose();
            RecomputeEditorTabWidths();
            UpdateEditorEmptyState();

            // Closing the last sub-tab closes the whole Editor top-level tab along with it,
            // rather than leaving an empty panel behind — same mechanism as clicking its own ×
            // (PanelCoordinator.CloseTab), so it comes back the normal way (ShowTab("editor"),
            // already wired into OpenInEditor) the next time a file is opened.
            if (host._editorInnerTabControl.TabPages.Count == 0) host.CloseTab(host._editorTab);
        }

        /// <summary>Close either a code document or a notebook through one lifecycle path.</summary>
        public void CloseDocumentTab(TabPage tab)
        {
            if (tab == null || host._editorInnerTabControl == null) return;
            if (!host._editorInnerTabControl.TabPages.Contains(tab)) return;

            string path = null;
            var editor = tab as HelixPythonNamespace.Editor.EditorTabPage;
            var notebook = tab as HelixPythonNamespace.Editor.Notebook.NotebookTabPage;
            if (editor != null)
            {
                try { editor.FlushPendingSave(); } catch { }
                path = editor.FilePath;
            }
            else if (notebook != null)
            {
                try { notebook.FlushPendingSave(); } catch { }
                path = notebook.FilePath;
            }
            else return;

            host._editorInnerTabControl.TabPages.Remove(tab);
            if (!string.IsNullOrEmpty(path)) host._openEditorTabs.Remove(Path.GetFullPath(path));
            tab.Dispose();
            RecomputeEditorTabWidths();
            UpdateEditorEmptyState();
            ApplyNotebookMode();
            if (host._editorInnerTabControl.TabPages.Count == 0)
            {
                // Closing the last notebook is an empty-workspace transition. Select Welcome
                // while Editor is still the current top-level page, so the shared helper can
                // distinguish this from a user who is deliberately viewing Output/Terminal.
                // Closing Editor first leaves TabControl free to select an arbitrary neighbour.
                if (notebook != null) host.SelectEntryTabAfterSolutionClose();
                host.CloseTab(host._editorTab);
            }
        }

        // Left inset of a tab's label, and the width taken off its right for the close icon.
        // The tab control's Padding.X is derived from these (InitializeEditorTab), because under
        // TabSizeMode.Normal that padding is what makes the tab wide enough to hold both.
        /// <summary>
        /// Height of a tab strip, shared by the editor sub-tabs and the docked tool panels. One
        /// definition because the two sit a few pixels apart on the same window and read as a
        /// single component; two constants would drift the first time either was touched.
        /// </summary>
        internal const int TabStripHeight = 22;

        internal const int TabTextLeftPad = 6;
        internal const int TabCloseReservation = 22;

        /// <summary>
        /// Slack added to each tab on top of the painter's own reservations.
        ///
        /// WinForms sizes a Normal tab from its OWN measurement of the label, which runs about
        /// 10px narrower than the <see cref="TextRenderer"/> measurement the painter (and
        /// <see cref="RequiredEditorTabWidth"/>) work from — measured, not guessed. Without the
        /// allowance every tab comes out a few pixels short and the last characters end up under
        /// the close icon, which is the exact bug TabSizeMode.Fixed was originally introduced to
        /// dodge.
        /// </summary>
        internal const int TabMeasurementAllowance = 12;

        // Re-size the tab strip after the set of tabs or a title changes (the ` *` modified suffix
        // and the pin bullet both change a label's width).
        public void RecomputeEditorTabWidths()
        {
            ApplyEditorTabSizing(host._editorInnerTabControl);
        }

        /// <summary>
        /// The editor sub-tab strip's own configuration. Static and taking the control so tests
        /// exercise the same setup the IDE runs with, instead of a hand-copied approximation that
        /// can quietly stop matching it.
        /// </summary>
        internal static void ConfigureEditorTabStrip(TabControl tabs)
        {
            if (tabs == null) return;

            // OwnerDrawFixed so OnEditorInnerTabDrawItem can render the label + close icon itself
            // (the default tab painter has no per-tab close button). "Fixed" here is about the
            // owner-draw contract, not the widths.
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;

            // Normal, NOT Fixed. TabSizeMode.Fixed gives every tab ONE shared width, and with tabs
            // sized to fit the longest label that meant a single long filename set the width of all
            // of them: a 96-character test file made `animals.py` a 538px tab (measured), pushed
            // the rest off the strip, and left the strip scrolled — which is what made a remaining
            // tab look like it had vanished after the long one was closed.
            //
            // Normal sizes each tab from its own label. The reason Fixed was chosen originally was
            // that Normal measures the TEXT only and knows nothing about the close icon we paint,
            // so the label ran under the (X). That is what Padding.X below pays for: WinForms adds
            // it on BOTH sides, so half the total reservation on each side gives every tab exactly
            // the room the painter needs, per tab, with no shared width.
            tabs.SizeMode = TabSizeMode.Normal;
            tabs.Padding = new Point(
                (TabTextLeftPad + TabCloseReservation + TabMeasurementAllowance + 1) / 2, 4);
        }

        /// <summary>
        /// Keeps the strip's row height stable and the selected tab reachable after the set of tabs
        /// changes. Widths are per-tab now (see <see cref="ConfigureEditorTabStrip"/>), so there is
        /// no shared width left to compute — what still needs doing is making sure a strip that had
        /// scrolled is not left showing empty space once the tabs it scrolled past are gone.
        /// </summary>
        internal static void ApplyEditorTabSizing(TabControl tabs)
        {
            if (tabs == null || tabs.TabPages.Count == 0) return;
            try
            {
                if (tabs.ItemSize.Height != TabStripHeight)
                    tabs.ItemSize = new Size(0, TabStripHeight);
                ScrollSelectedTabIntoView(tabs);
                tabs.Invalidate();
            }
            catch { /* control not yet realized */ }
        }

        private const int TCM_SETCURFOCUS = 0x1330;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Scroll the strip so the selected tab is on screen.
        ///
        /// A tab strip that has scrolled right keeps its offset when tabs are REMOVED, so closing
        /// the tab that caused the overflow can leave the strip parked past the end of its own
        /// content — visually an empty strip with the surviving tabs off to the left. TCM_SETCURFOCUS
        /// is the documented way to bring a tab into view; passing the index that is already
        /// selected changes no state, it only forces the scroll.
        /// </summary>
        private static void ScrollSelectedTabIntoView(TabControl tabs)
        {
            if (tabs == null || !tabs.IsHandleCreated) return;
            int index = tabs.SelectedIndex;
            if (index < 0 || index >= tabs.TabPages.Count) return;
            try { SendMessage(tabs.Handle, TCM_SETCURFOCUS, (IntPtr)index, IntPtr.Zero); }
            catch { /* cosmetic — never let a scroll nudge break a close */ }
        }

        /// <summary>
        /// Width a tab needs for <paramref name="text"/>: its measured label plus everything the
        /// owner-draw painter reserves on either side of it.
        /// </summary>
        internal static int RequiredEditorTabWidth(Graphics g, string text, Font font)
        {
            Size sz = TextRenderer.MeasureText(g, text ?? string.Empty, font);
            return sz.Width + TabTextLeftPad + TabCloseReservation;
        }

        // "Close All Others" — closes every unpinned tab except `keep`. Victims are
        // gathered into a list first because we can't mutate TabPages while iterating it.
        public void CloseEditorTabsExcept(HelixPythonNamespace.Editor.EditorTabPage keep)
        {
            if (host._editorInnerTabControl == null) return;
            List<HelixPythonNamespace.Editor.EditorTabPage> victims = new List<HelixPythonNamespace.Editor.EditorTabPage>();
            foreach (TabPage p in host._editorInnerTabControl.TabPages)
            {
                HelixPythonNamespace.Editor.EditorTabPage etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp != null && etp != keep && !etp.IsPinned) victims.Add(etp);
            }
            foreach (HelixPythonNamespace.Editor.EditorTabPage v in victims) CloseEditorTab(v);
        }

        // The unpinned tabs positioned after `from` in `tabs` — the victims of "Close Tabs to the
        // Right". Pure and static so it can be unit-tested on a bare TabControl; both the close action
        // and the menu-enable gate share it, keeping the "what closes" rule in one place.
        internal static List<HelixPythonNamespace.Editor.EditorTabPage> TabsToRight(TabControl tabs, HelixPythonNamespace.Editor.EditorTabPage from)
        {
            List<HelixPythonNamespace.Editor.EditorTabPage> victims = new List<HelixPythonNamespace.Editor.EditorTabPage>();
            if (tabs == null || from == null) return victims;
            int start = tabs.TabPages.IndexOf(from);
            if (start < 0) return victims;
            for (int i = start + 1; i < tabs.TabPages.Count; i++)
            {
                HelixPythonNamespace.Editor.EditorTabPage etp = tabs.TabPages[i] as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp != null && !etp.IsPinned) victims.Add(etp);
            }
            return victims;
        }

        // "Close Tabs to the Right" — closes every unpinned tab positioned after `from` (VS/VS Code
        // staple: trim a long tab strip back to a point). Pinned tabs to the right survive by design.
        public void CloseEditorTabsToRight(HelixPythonNamespace.Editor.EditorTabPage from)
        {
            foreach (HelixPythonNamespace.Editor.EditorTabPage v in TabsToRight(host._editorInnerTabControl, from))
                CloseEditorTab(v);
        }

        // True when at least one unpinned tab sits to the right of `from` — gates the menu item so it
        // is disabled when there is nothing to close (e.g. the rightmost tab).
        public bool HasEditorTabsToRight(HelixPythonNamespace.Editor.EditorTabPage from)
        {
            return TabsToRight(host._editorInnerTabControl, from).Count > 0;
        }

        // Path relative to the solution directory, with backslashes, for Copy Relative Path. Falls
        // back to the absolute path when the file lives outside the solution tree or on another drive.
        private static string RelativeToSolution(string baseDir, string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return filePath ?? string.Empty;
            if (string.IsNullOrEmpty(baseDir)) return filePath;
            try
            {
                Uri baseUri = new Uri(baseDir.TrimEnd('\\', '/') + "\\");
                Uri fileUri = new Uri(filePath);
                if (!baseUri.IsBaseOf(fileUri)) return filePath;
                string rel = Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString());
                return rel.Replace('/', '\\');
            }
            catch { return filePath; }
        }

        // "Close All" — closes every unpinned tab; pinned tabs survive by design.
        // Solution close/switch passes includePinned: true — a closed solution must leave
        // no editor tab behind, pinned or not (its documents no longer have a solution).
        public void CloseAllEditorTabs(bool includePinned = false)
        {
            if (host._editorInnerTabControl == null) return;
            foreach (TabPage v in DocumentTabsToClose(host._editorInnerTabControl, includePinned))
                CloseDocumentTab(v);
        }

        internal static List<TabPage> DocumentTabsToClose(TabControl tabs, bool includePinned)
        {
            var victims = new List<TabPage>();
            if (tabs == null) return victims;
            foreach (TabPage page in tabs.TabPages)
            {
                var editor = page as HelixPythonNamespace.Editor.EditorTabPage;
                if (editor != null && (includePinned || !editor.IsPinned)) victims.Add(page);
                else if (page is HelixPythonNamespace.Editor.Notebook.NotebookTabPage) victims.Add(page);
            }
            return victims;
        }

        // The victims of "Close All" — pure and static so the pinned/unpinned rule is
        // unit-testable on a bare TabControl, mirroring TabsToRight.
        internal static List<HelixPythonNamespace.Editor.EditorTabPage> TabsToClose(TabControl tabs, bool includePinned)
        {
            List<HelixPythonNamespace.Editor.EditorTabPage> victims = new List<HelixPythonNamespace.Editor.EditorTabPage>();
            if (tabs == null) return victims;
            foreach (TabPage p in tabs.TabPages)
            {
                HelixPythonNamespace.Editor.EditorTabPage etp = p as HelixPythonNamespace.Editor.EditorTabPage;
                if (etp != null && (includePinned || !etp.IsPinned)) victims.Add(etp);
            }
            return victims;
        }

        // Renames the tab's backing .py file on disk and rewrites imports across the
        // solution that referenced the old module. Reloads the solution so the model
        // reflects the new path.
        public void RenameEditorTabFile(HelixPythonNamespace.Editor.EditorTabPage tab)
        {
            if (tab == null || host._editorInnerTabControl == null) return;
            string oldPath = tab.FilePath;
            if (string.IsNullOrEmpty(oldPath)) return;

            // Prompt for the new name, defaulting to the current one, kept in-folder.
            string oldFileName = Path.GetFileName(oldPath);
            string newFileName;
            using (var dlg = new HelixPythonNamespace.App.Dialogs.SimpleInputDialog(
                "Rename File",
                "New filename (kept in the same folder):",
                oldFileName))
            {
                if (dlg.ShowDialog(host) != DialogResult.OK) return;
                newFileName = (dlg.Value ?? string.Empty).Trim();
            }
            if (string.IsNullOrEmpty(newFileName) || newFileName == oldFileName) return;
            // Force a .py extension so the file stays a recognized Python module.
            if (!newFileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                newFileName = Path.ChangeExtension(newFileName, ".py");

            // Abort if the target name already exists — File.Move would throw and we'd
            // rather show a friendly warning than a stack trace.
            string dir = Path.GetDirectoryName(oldPath);
            string newPath = Path.Combine(dir, newFileName);
            if (File.Exists(newPath))
            {
                MessageBox.Show(host,
                    "A file named '" + newFileName + "' already exists in that folder.",
                    "Rename Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Capture module namespaces BEFORE the move — the file path drives both.
            string oldNs = null, newNs = null;
            try
            {
                HelixPythonNamespace.Workspace.Python.SymbolResolver resolver = host.GetSymbolResolver();
                if (resolver != null)
                {
                    oldNs = resolver.DeriveModuleNamespace(oldPath);
                    newNs = resolver.DeriveModuleNamespace(newPath);
                }
            }
            catch { /* best-effort */ }

            try
            {
                // Flush the debounced autosave first so the move carries the latest text.
                tab.FlushPendingSave();

                // Suspend the file watcher across the move so the rename doesn't fire a
                // spurious delete/create that would trigger a reload mid-operation.
                using (host._fileWatcher?.SuspendWhileEditing())
                {
                    File.Move(oldPath, newPath);
                }

                // Cross-file import rewrite across the solution.
                int filesChanged = 0, totalHits = 0;
                if (!string.IsNullOrEmpty(oldNs) && !string.IsNullOrEmpty(newNs) && oldNs != newNs)
                {
                    (filesChanged, totalHits) = host.RewriteImportsAcrossSolution(oldNs, newNs, newPath);
                }

                // Update open-tab state: re-key the map from old to new full path.
                string oldKey = Path.GetFullPath(oldPath);
                string newKey = Path.GetFullPath(newPath);
                host._openEditorTabs.Remove(oldKey);
                tab.FilePath = newPath;            // setter runs UpdateTitle
                host._openEditorTabs[newKey] = tab;
                RecomputeEditorTabWidths();

                // Rebuild the model from disk so projects/types point at the new path.
                host.ReloadSolutionAsync();

                if (totalHits > 0)
                {
                    host._statusLabel.Text = string.Format(
                        "Renamed {0} → {1} ({2} imports updated in {3} file(s))",
                        oldFileName, newFileName, totalHits, filesChanged);
                }
                else
                {
                    host._statusLabel.Text = string.Format("Renamed {0} → {1}", oldFileName, newFileName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(host,
                    "Failed to rename file: " + ex.Message,
                    "Rename Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Which sub-tab's close button the pointer is over, or -1.
        private int _hotEditorTabClose = -1;

        /// <summary>
        /// Lights the close button under the pointer. Repaints only when the answer CHANGES —
        /// invalidating on every mouse move across a tab strip is itself a source of flicker.
        /// </summary>
        private void OnEditorInnerTabMouseMove(object sender, MouseEventArgs e)
        {
            TabControl tabs = host._editorInnerTabControl;
            if (tabs == null) return;

            int hot = -1;
            for (int i = 0; i < tabs.TabPages.Count; i++)
            {
                if (!GetEditorTabCloseButtonRect(tabs.GetTabRect(i)).Contains(e.Location)) continue;
                hot = i;
                break;
            }
            if (hot == _hotEditorTabClose) return;
            // Just the two buttons involved. Invalidating the whole strip on every crossing repaints
            // every label to change one 14px square, which is work the compositor then has to hide.
            InvalidateCloseButton(tabs, _hotEditorTabClose);
            _hotEditorTabClose = hot;
            InvalidateCloseButton(tabs, hot);
        }

        private static void InvalidateCloseButton(TabControl tabs, int index)
        {
            if (index < 0 || index >= tabs.TabPages.Count) return;
            Rectangle r = GetEditorTabCloseButtonRect(tabs.GetTabRect(index));
            r.Inflate(2, 2);
            tabs.Invalidate(r);
        }

        // Leaving the strip clears the highlight, or the last button stays lit for good.
        private void OnEditorInnerTabMouseLeave(object sender, EventArgs e)
        {
            if (_hotEditorTabClose < 0) return;
            TabControl tabs = host._editorInnerTabControl;
            int was = _hotEditorTabClose;
            _hotEditorTabClose = -1;
            if (tabs != null) InvalidateCloseButton(tabs, was);
        }

        // Owner-draw callback for each inner tab: paints the background, the label, and a
        // per-tab close icon. Runs on every repaint since DrawMode is OwnerDrawFixed.
        public void OnEditorInnerTabDrawItem(object sender, DrawItemEventArgs e)
        {
            if (host._editorInnerTabControl == null) return;
            if (e.Index < 0 || e.Index >= host._editorInnerTabControl.TabPages.Count) return;

            TabPage tab = host._editorInnerTabControl.TabPages[e.Index];
            bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Rectangle rect = e.Bounds;
            // These two are what the tab control's Padding.X has to pay for — see
            // InitializeEditorTab. Keep them in step or labels start colliding with the close icon.

            // Selected tab uses the window color, others the control color — mimics the
            // native selected/unselected tab contrast.
            Color bg = isSelected ? SystemColors.Window : SystemColors.Control;
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, rect);

            // Label — reserve room on the right for the close icon.
            Rectangle textRect = new Rectangle(rect.Left + TabTextLeftPad, rect.Top,
                                               rect.Width - TabCloseReservation, rect.Height);
            Color textColor = isSelected ? SystemColors.WindowText : SystemColors.ControlText;
            // EndEllipsis is the guarantee that a label never runs under the close icon. Tabs are
            // padded to fit their own label (ConfigureEditorTabStrip), but WinForms sizes them from
            // its own text measurement, which runs a little short of this one — and by more for a
            // longer label. Trimming the last characters is the right way to lose that argument;
            // drawing them under the (X), which is what happened before, is not.
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                  | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics, tab.Text, host._editorInnerTabControl.Font, textRect, textColor, flags);

            // The same square sunken red button the document tabs and the panel captions wear, at
            // the same size. It was a bundled round .ico at 12px here and 14px there, which put two
            // shapes and three sizes of one affordance on screen together.
            Rectangle closeRect = GetEditorTabCloseButtonRect(rect);
            HelixPythonNamespace.UI.Controls.ToolWindowChrome.DrawChromeButton(
                e.Graphics, closeRect, HelixPythonNamespace.UI.Controls.ToolWindowChrome.ChromeGlyph.Close,
                e.Index == _hotEditorTabClose);
        }

        // Computes the 12px close-icon rectangle at the right edge of a tab, vertically
        // centered. Shared by the painter and the click hit-test so they stay aligned.
        public static Rectangle GetEditorTabCloseButtonRect(Rectangle tabRect)
        {
            int size = HelixPythonNamespace.UI.Controls.ToolWindowChrome.ButtonSize;
            return new Rectangle(
                tabRect.Right - size - 5,
                tabRect.Top + (tabRect.Height - size) / 2,
                size, size);
        }

        // Closes tabs via mouse: left-click only closes when the hit lands on the close
        // icon; middle-click closes the tab regardless of where on it the click landed.
        public void OnEditorInnerTabMouseClick(object sender, MouseEventArgs e)
        {
            if (host._editorInnerTabControl == null) return;

            // Left-click: close only if the pointer is inside the close-icon rect.
            if (e.Button == MouseButtons.Left)
            {
                for (int i = 0; i < host._editorInnerTabControl.TabPages.Count; i++)
                {
                    Rectangle tabRect = host._editorInnerTabControl.GetTabRect(i);
                    if (!GetEditorTabCloseButtonRect(tabRect).Contains(e.Location)) continue;
                    CloseDocumentTab(host._editorInnerTabControl.TabPages[i]);
                    return;
                }
            }
            // Middle-click: close whichever tab the pointer is anywhere within.
            else if (e.Button == MouseButtons.Middle)
            {
                for (int i = 0; i < host._editorInnerTabControl.TabPages.Count; i++)
                {
                    if (!host._editorInnerTabControl.GetTabRect(i).Contains(e.Location)) continue;
                    CloseDocumentTab(host._editorInnerTabControl.TabPages[i]);
                    return;
                }
            }
        }

        /// <summary>
        /// Paints the margin breakpoint dots for a freshly-opened editor tab from the
        /// BreakpointManager — without raising BreakpointToggled (so the manager isn't
        /// double-toggled, the #11 bug). Applies immediately if the editor's native handle exists,
        /// otherwise defers to HandleCreated (an inserted tab's handle isn't realized until shown).
        /// </summary>
        private void SyncBreakpointMarkers(HelixPythonNamespace.Editor.EditorTabPage tab, string full)
        {
            HelixPythonNamespace.Debugger.BreakpointManager mgr = host._debugCoordinator?.Controller?.Breakpoints;
            if (mgr == null) return;
            System.Action apply = delegate
            {
                foreach (int ln in mgr.GetBreakpoints(full))
                    try { tab.Editor.SetBreakpointMarker(ln, true); } catch { }
            };
            // Post via BeginInvoke rather than calling apply() directly: HelixTextEditorNET raises
            // HandleCreated BEFORE it flushes the cached Text into the document, so a direct call
            // would run with Lines.Count == 0 and SetBreakpointMarker would no-op. BeginInvoke
            // defers apply until after the text has settled.
            if (tab.Editor.IsHandleCreated)
            {
                try { tab.Editor.BeginInvoke(apply); } catch { apply(); }
            }
            else
            {
                EventHandler onCreated = null;
                onCreated = delegate
                {
                    tab.Editor.HandleCreated -= onCreated;
                    try { tab.Editor.BeginInvoke(apply); } catch { apply(); }
                };
                tab.Editor.HandleCreated += onCreated;
            }
        }

        // Central entry point for showing a file in the editor. Restores the top-level
        // Editor tab if it was closed, reuses an existing sub-tab for the file (keyed by
        // canonical full path) or builds a fresh one, optionally activates the Editor tab,
        // and jumps to a line. Used by tree double-click, go-to-type, and File ▸ Open.
        /// <summary>
        /// Open a file in a document tab.
        ///
        /// <para>
        /// <paramref name="readOnly"/> is for files the user is READING rather than working on —
        /// a package on a notebook's import path, which lives outside the notebook's folder and
        /// usually outside the solution. It applies only when this call CREATES the tab: a file
        /// already open for editing is never quietly demoted, because the user may have unsaved
        /// work in it. Asking for it editable does upgrade an existing read-only tab, which is
        /// what "Open for editing" on an already-open file has to mean.
        /// </para>
        /// </summary>
        public void OpenInEditor(string filePath, int line = -1, bool activate = true, int column = -1,
                                 bool readOnly = false)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            host.RecordNavigationTarget(filePath, line, column);

            // The Editor top-level tab is closable via its × button; if it's been
            // closed, the inner-tab additions below would succeed silently but the
            // user wouldn't see anything because the outer wrapper isn't in the
            // main tab control. Restore it via the shared host.ShowTab helper.
            if (host._editorTab != null && host._mainTabControl != null &&
                !host._mainTabControl.TabPages.Contains(host._editorTab))
            {
                host.ShowTab("editor");
            }

            // Reuse an already-open tab for this file (keyed by canonical full path) so
            // the same document is never opened twice; otherwise build a fresh tab.
            string full = Path.GetFullPath(filePath);

            // A notebook is not a text file. Loading an 11 MB .ipynb into the code editor would
            // show the user a wall of JSON with their Python quoted inside it — the same mistake
            // that lexed READMEs as Python before the Markdown work. It gets its own surface.
            if (IsNotebookPath(full)) { OpenNotebook(full, activate); return; }
            if (host._openEditorTabs.TryGetValue(full, out TabPage existing))
            {
                host._editorInnerTabControl.SelectedTab = existing;
                if (!readOnly && existing is HelixPythonNamespace.Editor.EditorTabPage reopened)
                    reopened.IsReadOnly = false;
            }
            else
            {
                // Create the editor tab and apply user font settings + the current
                // solution context (for symbol resolution / go-to-type), tolerating
                // failures since a missing font or null solution shouldn't block opening.
                // The Scintilla-backed tab can fail to initialize on some machines (its native
                // SciLexer.dll self-extract can be blocked by antivirus or a read-only app folder).
                // Surface that clearly instead of letting the open silently fail / crash.
                HelixPythonNamespace.Editor.EditorTabPage tab;
                try
                {
                    tab = new HelixPythonNamespace.Editor.EditorTabPage(full);
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show(host,
                        "Could not open \"" + Path.GetFileName(full) + "\".\n\n" +
                        "The code editor component failed to initialize. This can happen when the native " +
                        "SciLexer.dll is blocked by antivirus or the application folder is read-only.\n\n" +
                        "Details: " + ex.Message,
                        "Editor failed to open", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    if (host._statusLabel != null) host._statusLabel.Text = "Editor failed to open " + Path.GetFileName(full);
                    return;
                }
                try { tab.Editor.ApplyFontSettings(host._settingsManager.Settings.EditorFontName, host._settingsManager.Settings.EditorFontSize); } catch { }
                // Ctrl+MouseWheel zoom is a per-document gesture but a global preference: the host
                // persists the new size and mirrors it into every other open document.
                tab.Editor.EditorFontSizeChanged += (s, size) => host.OnEditorFontSizeZoomed(size);
                // §3.2–3.4 overlay preferences follow the persisted settings.
                try
                {
                    tab.Editor.VirtualBracesEnabled = host._settingsManager.Settings.EditorVirtualBraces;
                    tab.Editor.TypeMarkersEnabled = host._settingsManager.Settings.EditorTypeMarkers;
                }
                catch { }
                // View ▸ Word Wrap is solution-wide, so a tab opened while it is on must come up
                // wrapped rather than inheriting the control default — including the tabs restored
                // at startup, which is where a persisted setting would otherwise silently do nothing.
                try { tab.SetWordWrap(host.WordWrapEnabled); } catch { }
                // After the constructor has loaded the file: the buffer refuses edits, not loads,
                // but setting it here keeps the order obvious rather than relying on that.
                tab.IsReadOnly = readOnly;
                try { tab.Editor.Solution = host._currentSolution; } catch { }
                // Wire editor events back to the host: navigation, symbol resolution,
                // breakpoint toggling into the debugger, and save hooks.
                tab.Editor.GoToDefinitionRequested += host.OnEditorGoToDefinitionRequested;
                tab.Editor.ResolveSymbolRequested += host.OnEditorResolveSymbolRequested;
                tab.Editor.BreakpointToggled += (s, ln) =>
                {
                    host._debugCoordinator.Controller?.Breakpoints.ToggleBreakpoint(full, ln);
                };
                // Debug data tips: hovering an expression while paused shows its live value. Wired
                // per tab because the hover originates in this document's editor; the coordinator
                // owns the evaluation and the popup.
                try { host._debugCoordinator?.AttachDataTips(tab.Editor); } catch { }
                tab.FileSaved += (s, ev) => OnEditorFileSaved(full);
                tab.BeforeSave += OnEditorBeforeSave;
                // Every disk write this tab performs (autosave included) suspends the solution
                // file watcher, so the IDE's own saves never surface as "External Change
                // Detected". Resolved per call because StartFileWatcher rebuilds the watcher.
                tab.SaveGuard = () => host._fileWatcher != null ? host._fileWatcher.SuspendWhileEditing() : null;
                // Keep tab width in sync with the title: the ` *` modified suffix or the
                // pin bullet can extend the rendered text past the current ItemSize.
                tab.ModifiedChanged += (s, ev) => RecomputeEditorTabWidths();
                // Live status-bar Ln/Col readout as the caret moves in this document.
                tab.CaretMoved += (ln, cl) => host.OnEditorCaretMoved(ln, cl);
                // TabControl.TabPages.Insert only REGISTERS a page when the control's handle
                // exists; without one it parents the page but leaves TabPages.Count at 0, so the
                // tab never appears — and because the file is still recorded in _openEditorTabs
                // below, every later open takes the "reuse existing tab" path and the file stays
                // un-openable for the rest of the session. That is exactly the state at launch:
                // UpdateEditorEmptyState hides this control while it has no tabs, and
                // Control.CreateControl() is a no-op on an invisible control. Force the handle.
                EnsureRealized(host._editorInnerTabControl);

                // Find where the unpinned region starts (after the last pinned tab, if any) —
                // pinned tabs never shift when new files are opened — then insert alphabetically
                // by file name within that region instead of always at the front.
                int unpinnedStart = host._editorInnerTabControl.TabPages.Count;
                for (int i = host._editorInnerTabControl.TabPages.Count - 1; i >= 0; i--)
                {
                    HelixPythonNamespace.Editor.EditorTabPage earlier =
                        host._editorInnerTabControl.TabPages[i] as HelixPythonNamespace.Editor.EditorTabPage;
                    if (earlier != null && earlier.IsPinned) { unpinnedStart = i + 1; break; }
                    unpinnedStart = i;
                }
                string newFileName = Path.GetFileName(full);
                int insertIdx = host._editorInnerTabControl.TabPages.Count;
                for (int i = unpinnedStart; i < host._editorInnerTabControl.TabPages.Count; i++)
                {
                    TabPage otherTab = host._editorInnerTabControl.TabPages[i];
                    HelixPythonNamespace.Editor.EditorTabPage other = otherTab as HelixPythonNamespace.Editor.EditorTabPage;
                    string otherName = other != null && !string.IsNullOrEmpty(other.FilePath)
                        ? Path.GetFileName(other.FilePath) : otherTab.Text;
                    if (string.Compare(newFileName, otherName, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        insertIdx = i;
                        break;
                    }
                }
                // Insert at the computed slot, select it, and register in the open-tab map.
                host._editorInnerTabControl.TabPages.Insert(insertIdx, tab);
                host._editorInnerTabControl.SelectedTab = tab;
                host._openEditorTabs[full] = tab;
                RecomputeEditorTabWidths();
                UpdateEditorEmptyState();
                // Show the red margin dots for any breakpoints already registered for this file
                // (restored from disk, or set while the file was closed) — the panel + debugger
                // already honor them, but the margin only showed after a manual toggle.
                SyncBreakpointMarkers(tab, full);
            }
            // Only switch the main tab when the caller actually wants focus. Background
            // callers (e.g. tree double-click, which wants the Designer Canvas to stay
            // on screen so the user sees the selection + zoom) pass activate=false.
            if (activate) host._mainTabControl.SelectedTab = host._editorTab;
            if (line > 0 && host._editorInnerTabControl.SelectedTab is HelixPythonNamespace.Editor.EditorTabPage etp)
            {
                // GoToLine/GoToLineColumn always set the caret to the right position, but centering
                // the viewport on it (CentreCaretLine) reads the editor's LinesOnScreen, which comes
                // from the control's real height. The FIRST time this editor (or the whole Editor
                // top-level tab) becomes visible in the session, it hasn't actually been resized to
                // fill its real space yet, so LinesOnScreen reads 0/near-0 and CentreCaretLine's
                // "too small a viewport" guard silently no-ops — the caret lands on the right line
                // internally, but the viewport stays wherever it was (often the top of the file),
                // so the user visually sees the wrong place. Reused, already-visible tabs don't hit
                // this — hence "sometimes works, sometimes doesn't". PerformLayout() on just
                // _mainTabControl only resizes ITS immediate child (_editorTab) — the editor control
                // is several docked levels further down (_editorTab -> _editorInnerTabControl ->
                // the tab page -> the editor), so laying out one level isn't enough; calling it on
                // the Form itself cascades a full layout pass down through every descendant in one
                // synchronous call. Same fix as the Designer Canvas's analogous first-paint bug.
                if (activate) { host.PerformLayout(); Application.DoEvents(); }
                // When a caller supplies the column (a diagnostic, a Find hit), land the caret on the
                // exact token rather than at column 1 — VS/PyCharm parity. GoToLineColumn falls back
                // to the line start for column <= 1.
                try { if (column > 1) etp.Editor.GoToLineColumn(line, column); else etp.Editor.GoToLine(line); }
                catch { /* best effort */ }
                // The editor paints its caret only while FOCUSED (RenderState.ShowCaret = Focused).
                // A jump that starts in another panel's ListView (a failed test row, a Problems
                // entry, a Find hit) left focus in that list, so the caret just placed on the
                // target line never blinked — the file "popped open" with no visible cursor.
                // activate=false callers keep their focus (the tree double-click contract).
                if (activate) { try { etp.Editor.Focus(); } catch { /* best effort */ } }
            }
            // Seed the Ln/Col readout for the document we just opened. It was otherwise fed only by
            // caret MOVES and tab-change events, so opening the first document left the indicator
            // blank until the user happened to click in the text (iter 349 screenshot). Later opens
            // were already covered by the inner tab's SelectedIndexChanged; calling here makes the
            // readout a property of "a document is open" rather than of how it came to be open.
            host.UpdateCaretStatus();
            host.UpdateEolStatus();

            // Point the Solution Explorer at the document that is now active. This was driven only
            // by the inner strip's SelectedIndexChanged, which does NOT fire when the requested
            // file is already the selected tab — so re-opening the document you are already in
            // (Go To Definition onto the same file, a Problems row, a dependency row) left the tree
            // highlighting whatever was selected before. Doing it here makes the tree follow "which
            // document is active" rather than "did the tab index happen to change".
            host.SyncTreeToActiveDocument();
        }

        // ---- Notebooks -------------------------------------------------------------------

        /// <summary>
        /// The single authority for "is this a Jupyter notebook?" on the shell side, mirroring
        /// <c>PythonEditorControl.IsMarkdownFilePath</c>: the open path and the Solution Explorer's
        /// double-click both ask here, so a file can never open as a notebook in one place and as
        /// text in another.
        /// </summary>
        public static bool IsNotebookPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return string.Equals(Path.GetExtension(path), ".ipynb", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Open a notebook in its own tab, beside the code editor's tabs. Registered in the same
        /// open-tab map as every other document so re-opening reuses the tab, Close All finds it,
        /// and the file cannot end up open twice.
        /// </summary>
        private void OpenNotebook(string full, bool activate)
        {
            if (host._openEditorTabs.TryGetValue(full, out TabPage existing))
            {
                host._editorInnerTabControl.SelectedTab = existing;
                if (activate) host._mainTabControl.SelectedTab = host._editorTab;
                host.RememberRecentNotebook(full);
                return;
            }

            var tab = new HelixPythonNamespace.Editor.Notebook.NotebookTabPage();
            string error = tab.LoadFile(full);
            if (error != null)
            {
                // Refused with the reason, never as an empty notebook: "this file has no cells
                // array" tells the user their download is HTML, and a blank page does not.
                tab.Dispose();
                MessageBox.Show(host,
                    "Could not open " + Path.GetFileName(full) + " as a notebook."
                        + Environment.NewLine + Environment.NewLine + error,
                    "Open notebook", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (host._statusLabel != null) host._statusLabel.Text = "Not a notebook: " + Path.GetFileName(full);
                return;
            }

            // The kernel's interpreter is the same one Run uses. Resolved per start rather than
            // captured, so changing the interpreter takes effect on the next Restart Kernel.
            tab.InterpreterResolver = () => host.FindPythonInterpreter();
            tab.View.Solution = host._currentSolution;
            tab.StatusChanged += (s, message) =>
            {
                if (host._statusLabel != null) host._statusLabel.Text = message;
            };
            tab.ModifiedChanged += delegate { RecomputeEditorTabWidths(); };

            EnsureRealized(host._editorInnerTabControl);   // see the note in OpenInEditor
            host._editorInnerTabControl.TabPages.Add(tab);
            host._editorInnerTabControl.SelectedTab = tab;
            host._openEditorTabs[full] = tab;
            RecomputeEditorTabWidths();
            UpdateEditorEmptyState();
            host.RememberRecentNotebook(full);

            tab.OpenFileRequested = (path, readOnly) => OpenInEditor(path, readOnly: readOnly);
            // The notebook's own File menu is the only one visible while it is on screen (see
            // SyncShellMenusForNotebookMode), so these two are the user's only route to a new or
            // different notebook without first closing this one. Neither goes near a solution.
            tab.NewNotebookRequested = () => host.OnFileNewNotebook(null, EventArgs.Empty);
            tab.OpenNotebookRequested = PromptOpenNotebook;
            ApplyNotebookMode();

            if (activate) host._mainTabControl.SelectedTab = host._editorTab;
            if (host._statusLabel != null)
            {
                host._statusLabel.Text = Path.GetFileName(full) + ": "
                    + tab.Document.Cells.Count + " cells, "
                    + tab.Document.CodeCellCount + " of them code";
            }
        }

        /// <summary>
        /// Notebook mode: while a notebook is the active document, the IDE gets out of its way.
        ///
        /// <para>
        /// A notebook carries its own left rail, its own menus and its own page, and Colab's layout
        /// is the whole width of the window. The Solution Explorer beside it is a second tree the
        /// notebook has no relationship to, and the AI terminal is a third pane competing for the
        /// same screen. Both come back the moment the user selects a code document again — this is a
        /// MODE, not a preference, so it never touches the persisted panel settings (the same rule
        /// PanelCoordinator.ApplySolutionGate follows for the solution gate).
        /// </para>
        /// </summary>
        public void ApplyNotebookMode()
        {
            // OPEN, not ACTIVE. Reading a library file from a notebook's Files pane opens an
            // ordinary document tab beside it, and tying the mode to the ACTIVE document meant the
            // Solution Explorer, the AI terminal and every other top-level tab came flooding back
            // the moment that happened -- the window rearranging itself around a user who had only
            // asked to read a file. The notebook still owns the workbench until it is closed.
            bool notebook = AnyNotebookOpen()
                            && host._mainTabControl != null
                            && host._mainTabControl.SelectedTab == host._editorTab;

            // The menu and toolbar are the exception, and they are handled separately below: a
            // notebook carries its OWN File/Edit/View menu bar, so hiding the shell's is right
            // while the notebook is on screen and wrong while a `.py` tab is, which supplies none.
            // Without this an editor tab opened from a notebook has no menu bar at all.
            SyncShellMenusForNotebookMode(notebook && ActiveNotebook() != null);

            if (notebook == _notebookMode) return;
            _notebookMode = notebook;

            try
            {
                if (notebook)
                {
                    // A solution load selects Class Designer Canvas before the generated notebook is
                    // opened. Notebook mode owns the workbench, so hide every other top-level tab
                    // transiently and restore its exact position when the mode ends.
                    _notebookHiddenTabs.Clear();
                    for (int i = host._mainTabControl.TabPages.Count - 1; i >= 0; i--)
                    {
                        TabPage page = host._mainTabControl.TabPages[i];
                        if (page == host._editorTab) continue;
                        _notebookHiddenTabs.Add(new KeyValuePair<TabPage, int>(page, i));
                        host._mainTabControl.TabPages.RemoveAt(i);
                    }
                    _notebookHiddenTabs.Sort((a, b) => a.Value.CompareTo(b.Value));

                    _explorerWasVisible = host._leftDockPanel != null && host._leftDockPanel.Visible;
                    _aiTerminalWasVisible = host._aiTerminalDock != null && host._aiTerminalDock.Visible;
                    // Hiding the panel is not enough: it lives in a SplitContainer panel, and a
                    // hidden child leaves the splitter's 200 px reserved — a blank column beside a
                    // notebook that is meant to have the window. Collapsing the panel is what
                    // actually gives the page the width.
                    if (host._leftSplitter != null)
                    {
                        _savedSplitterDistance = host._leftSplitter.SplitterDistance;
                        host._leftSplitter.Panel1Collapsed = true;
                    }
                    if (host._leftDockPanel != null) host._leftDockPanel.Visible = false;
                    if (host._aiTerminalDock != null) host._aiTerminalDock.Visible = false;
                }
                else
                {
                    foreach (KeyValuePair<TabPage, int> hidden in _notebookHiddenTabs)
                    {
                        if (hidden.Key == null || hidden.Key.IsDisposed ||
                            host._mainTabControl.TabPages.Contains(hidden.Key)) continue;
                        int index = Math.Min(hidden.Value, host._mainTabControl.TabPages.Count);
                        host._mainTabControl.TabPages.Insert(index, hidden.Key);
                    }
                    _notebookHiddenTabs.Clear();

                    if (host._leftDockPanel != null) host._leftDockPanel.Visible = _explorerWasVisible;
                    if (host._leftSplitter != null)
                    {
                        host._leftSplitter.Panel1Collapsed = !_explorerWasVisible;
                        if (_explorerWasVisible && _savedSplitterDistance > 0)
                        {
                            try { host._leftSplitter.SplitterDistance = _savedSplitterDistance; }
                            catch (InvalidOperationException) { }
                            catch (ArgumentException) { }
                        }
                    }
                    if (host._aiTerminalDock != null) host._aiTerminalDock.Visible = _aiTerminalWasVisible;
                }
            }
            catch (Exception) { /* a layout failure must never block switching documents */ }

            if (host._statusLabel != null && notebook)
                host._statusLabel.Text = "Notebook mode — the runtime, the cells and the panes are the notebook's own.";
        }

        private bool _notebookMode;

        /// <summary>True while notebook mode owns the workbench (the explorer column is its to restore).</summary>
        internal bool NotebookModeActive { get { return _notebookMode; } }
        private bool _explorerWasVisible = true;
        private bool _aiTerminalWasVisible = true;
        private int _savedSplitterDistance;
        private bool _menuWasVisible = true;
        private bool _shellMenusHidden;
        private readonly List<KeyValuePair<TabPage, int>> _notebookHiddenTabs =
            new List<KeyValuePair<TabPage, int>>();

        /// <summary>
        /// Hide the shell's own MENU BAR — and only that — while a notebook is the document on
        /// screen.
        ///
        /// <para>
        /// <b>The menu is hidden because it is REPLACED, not because chrome is unwelcome.</b> A
        /// notebook carries its own File / Edit / View / Insert / Runtime / Tools / Help bar, and
        /// showing both would stack two menu bars with near-identical names and different meanings
        /// — two "File ▸ Save" a few pixels apart, one saving the notebook and one the layout. That
        /// is the whole of the argument, and it does not carry any further than the menu.
        /// </para>
        ///
        /// <para>
        /// <b>The toolbar is NOT hidden any more, because nothing replaces it.</b> It used to be,
        /// for no better reason than that it sat beside the menu. The notebook's action row
        /// (<c>+ Code</c>, <c>Run all</c>, <c>Interrupt</c>) lives inside the notebook page and is
        /// about cells; it is not a substitute for New / Open / Import / Run / Build / Debug.
        /// Hiding it removed capability and offered nothing in its place — which is exactly the
        /// argument used to justify KEEPING the toolbar over a read-only `.py`, and it applies here
        /// unchanged. What the toolbar shows is already decided by
        /// <see cref="MainForm.UpdateToolbarWorkflowVisibility"/> (solution-scoped commands
        /// disappear without a solution) and <c>UpdateCanvasScopedToolbarEnabled</c> (canvas
        /// commands grey out off the canvas), so in notebook mode it settles to the entry points
        /// that still mean something.
        /// </para>
        ///
        /// <para>
        /// Kept out of the mode transition because it tracks a different question. The mode lasts
        /// as long as a notebook is OPEN; the menu bar depends on which document is SHOWING, and
        /// both can change without the other. Idempotent, so it is safe on every selection.
        /// </para>
        /// </summary>
        private void SyncShellMenusForNotebookMode(bool notebookOnScreen)
        {
            try
            {
                if (notebookOnScreen)
                {
                    if (!_shellMenusHidden)
                    {
                        _menuWasVisible = host._menuStrip != null && host._menuStrip.Visible;
                        _shellMenusHidden = true;
                    }
                    if (host._menuStrip != null) host._menuStrip.Visible = false;
                }
                else if (_shellMenusHidden)
                {
                    _shellMenusHidden = false;
                    if (host._menuStrip != null) host._menuStrip.Visible = _menuWasVisible;
                }

                // Whatever the mode, the toolbar's own scoping decides its contents.
                host.UpdateCanvasScopedToolbarEnabled();
            }
            catch (Exception) { /* a layout failure must never block switching documents */ }
        }

        /// <summary>True while ANY open document tab is a notebook, selected or not.</summary>
        internal bool AnyNotebookOpen()
        {
            if (host._editorInnerTabControl == null) return false;
            foreach (TabPage page in host._editorInnerTabControl.TabPages)
                if (page is HelixPythonNamespace.Editor.Notebook.NotebookTabPage) return true;
            return false;
        }

        /// <summary>The active notebook tab, or null when the active document is not one.</summary>
        public HelixPythonNamespace.Editor.Notebook.NotebookTabPage ActiveNotebook()
        {
            if (host._editorInnerTabControl == null) return null;
            return host._editorInnerTabControl.SelectedTab as HelixPythonNamespace.Editor.Notebook.NotebookTabPage;
        }

        // Fires just before an editor autosave. Enforces project-kind rules about
        // def main() (apps must have one, libraries/Flask apps must not) and reports any
        // violation to the Problems panel WITHOUT cancelling the save (autosave runs on
        // every keystroke, so blocking or popping modals would be intolerable).
        public void OnEditorBeforeSave(object sender, System.ComponentModel.CancelEventArgs e)
        {
            HelixPythonNamespace.Editor.EditorTabPage tab = sender as HelixPythonNamespace.Editor.EditorTabPage;
            if (tab == null || host._currentSolution == null) return;
            string filePath = tab.FilePath;
            if (string.IsNullOrEmpty(filePath)) return;

            ProjectModel owner = FindOwningProject(host._currentSolution.Projects, filePath);
            if (owner == null) return;
            string ownerKey = owner.FilePath ?? owner.Name ?? filePath;

            // Count def main() in project.Functions EXCLUDING the file being saved —
            // its contribution is replaced by the in-memory parse below (the on-disk
            // copy may be stale vs. the editor text). Also remember the FIRST file that
            // actually defines one, so a "must not define main()" violation can be attributed
            // to the real offender rather than unconditionally to whatever file is being saved
            // (which is very often a different, unrelated file in the same project).
            int mainCount = 0;
            string firstExternalMainFile = null;
            foreach (FunctionModel fn in owner.Functions)
            {
                if (fn == null) continue;
                if (string.Equals(fn.SourceFilePath, filePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(fn.Name, "main", StringComparison.Ordinal)) continue;
                mainCount++;
                if (firstExternalMainFile == null) firstExternalMainFile = fn.SourceFilePath;
            }

            // Do not infer a missing main() from the parser's recovery tree. While a user is
            // typing an incomplete statement, that tree can omit a later, valid def main().
            int inMemoryMainLine = -1;
            HelixPythonNamespace.Workspace.Python.PythonEntryPointFacts entryPoint =
                HelixPythonNamespace.Workspace.Python.PythonEntryPointFacts.Analyze(tab.Editor.Text, filePath);
            if (!entryPoint.Trustworthy)
            {
                _lastMainParityViolation.Remove(ownerKey);
                RemoveResolvedMainParityDiagnostics(owner, host._problemsPanel);
                return;
            }
            if (entryPoint.HasModuleLevelMainFunction)
            {
                mainCount++;
                inMemoryMainLine = entryPoint.MainFunctionLine;
            }

            // Map project kind + main() count to a parity rule. Only one can trip per save.
            bool violation = false;
            string reason = null;
            if (owner.Kind == HelixPythonNamespace.Core.Model.Enums.ProjectKind.App && mainCount == 0)
            {
                violation = true;
                reason = "An app must have at least one def main() function somewhere in the project.";
            }
            else if (owner.Kind == HelixPythonNamespace.Core.Model.Enums.ProjectKind.GuiApp && mainCount == 0)
            {
                violation = true;
                reason = "A GUI app must have at least one def main() function (GUI apps bootstrap the event loop from main).";
            }
            else if (owner.Kind == HelixPythonNamespace.Core.Model.Enums.ProjectKind.ClassLibrary && mainCount > 0)
            {
                violation = true;
                reason = "A class library must not define a def main() function (libraries are imported, not run).";
            }
            else if (owner.Kind == HelixPythonNamespace.Core.Model.Enums.ProjectKind.FlaskWebApp && mainCount > 0)
            {
                violation = true;
                reason = "A Flask web app must not define a def main() function (the Flask instance is the WSGI entry point).";
            }

            // The file that actually has the extra def main() — this file if it contributed one,
            // else whichever other file did (attribution fix: previously this was unconditionally
            // `filePath`, so saving an unrelated file kept blaming IT for a different file's
            // stray main()).
            string offenderPath = inMemoryMainLine >= 0 ? filePath : firstExternalMainFile ?? filePath;

            // De-dup: only actually surface this when the (reason, offending file) pair changed
            // since the last check for this project — otherwise the SAME already-known violation
            // re-announces itself on every autosave (~400ms/keystroke) of every file in the
            // project, including ones with nothing to do with it ("shows up out of nowhere,
            // repeatedly" while typing something unrelated).
            string violationKey = violation ? reason + "|" + offenderPath : null;
            string lastKey;
            bool sameAsLast = _lastMainParityViolation.TryGetValue(ownerKey, out lastKey) && lastKey == violationKey;
            if (!violation)
            {
                _lastMainParityViolation.Remove(ownerKey);
                RemoveResolvedMainParityDiagnostics(owner, host._problemsPanel);
                return;
            }
            _lastMainParityViolation[ownerKey] = violationKey;
            if (sameAsLast && HasMainParityDiagnostic(host._problemsPanel, offenderPath)) return;

            // Report the project-kind violation to the Problems panel only — do
            // not cancel the save and do not pop a modal. Autosave runs on every
            // keystroke, so a modal would fire repeatedly; instead the user sees
            // the issue in the Problems panel and fixes it when ready.
            if (host._problemsPanel != null)
            {
                var span = (inMemoryMainLine > 0 && string.Equals(offenderPath, filePath, StringComparison.OrdinalIgnoreCase))
                    ? new HelixPythonNamespace.Python.Text.SourceSpan(
                        new HelixPythonNamespace.Python.Text.SourcePosition(0, inMemoryMainLine, 1),
                        new HelixPythonNamespace.Python.Text.SourcePosition(0, inMemoryMainLine, 2))
                    : new HelixPythonNamespace.Python.Text.SourceSpan();
                var diag = new HelixPythonNamespace.VirtualCompiler.CompileDiagnostic(
                    offenderPath, "PY9001",
                    HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error,
                    span, reason);
                host._problemsPanel.ShowDiagnostics(
                    new System.Collections.Generic.List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> { diag });
            }
            // Surface the tab if it's been closed, but don't change the selected
            // tab — host handler fires on every autosave (~400 ms of typing), so
            // switching selection would yank keyboard focus away from the editor
            // mid-keystroke.
            if (host._mainTabControl != null && host._problemsTab != null &&
                !host._mainTabControl.TabPages.Contains(host._problemsTab))
            {
                host.ShowTab("problems");
            }
        }

        private static bool HasMainParityDiagnostic(
            HelixPythonNamespace.App.Controls.ProblemsPanel panel, string filePath)
        {
            if (panel == null) return false;
            return panel.Diagnostics.Any(d => d.Code == "PY9001" &&
                string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        }

        private static void RemoveResolvedMainParityDiagnostics(
            ProjectModel owner, HelixPythonNamespace.App.Controls.ProblemsPanel panel)
        {
            if (owner == null || panel == null) return;
            string directory;
            try { directory = Path.GetFullPath(Path.GetDirectoryName(owner.FilePath) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return; }
            if (directory.Length == 0) return;

            foreach (var diagnostic in panel.Diagnostics.ToArray())
            {
                if (diagnostic.Code != "PY9001" || string.IsNullOrEmpty(diagnostic.FilePath)) continue;
                string diagnosticPath;
                try { diagnosticPath = Path.GetFullPath(diagnostic.FilePath); }
                catch { continue; }
                if (diagnosticPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
                    (diagnosticPath.Length == directory.Length ||
                     diagnosticPath[directory.Length] == Path.DirectorySeparatorChar ||
                     diagnosticPath[directory.Length] == Path.AltDirectorySeparatorChar))
                    panel.RemoveDiagnostic(diagnostic);
            }
        }

        internal static ProjectModel FindOwningProject(IEnumerable<ProjectModel> projects, string filePath)
        {
            if (projects == null || string.IsNullOrEmpty(filePath)) return null;
            string fullFile;
            try { fullFile = Path.GetFullPath(filePath); }
            catch { return null; }

            ProjectModel best = null;
            int bestLength = -1;
            foreach (ProjectModel project in projects)
            {
                string directory;
                try { directory = Path.GetFullPath(Path.GetDirectoryName(project.FilePath) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
                catch { continue; }
                if (directory.Length == 0 || directory.Length <= bestLength) continue;
                if (!fullFile.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) continue;
                if (fullFile.Length > directory.Length && fullFile[directory.Length] != Path.DirectorySeparatorChar
                    && fullFile[directory.Length] != Path.AltDirectorySeparatorChar) continue;
                best = project;
                bestLength = directory.Length;
            }
            return best;
        }

        // Fires after a file is saved to disk. Re-parses just that file, replaces the
        // types it contributed in the model, rebinds base-type references solution-wide,
        // and refreshes the diagram so the canvas reflects the edit.
        public void OnEditorFileSaved(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || host._currentSolution == null) return;

            if (host._mainTabControl != null && host._mainTabControl.SelectedTab == host._editorTab)
            {
                _pendingCanvasRefreshFiles.Add(filePath);
                if (host._statusLabel != null)
                    host._statusLabel.Text = "Saved " + Path.GetFileName(filePath);
                return;
            }

            try
            {
                // Find the project that owns host file (match by file path prefix).
                ProjectModel owner = null;
                foreach (ProjectModel p in host._currentSolution.Projects)
                {
                    string dir = Path.GetDirectoryName(p.FilePath);
                    if (!string.IsNullOrEmpty(dir) && filePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    {
                        owner = p; break;
                    }
                }
                if (owner == null) return;

                // Language dispatch by extension: this refresh used to run EVERY saved file
                // through the PYTHON parser — for a .cu/.cuh it produced a nameless type whose
                // model constructor threw, so each autosave surfaced "Warning: refresh failed
                // (Name cannot be null or empty)" in the status bar while editing C++/CUDA.
                string ext = (Path.GetExtension(filePath) ?? string.Empty).ToLowerInvariant();
                bool isPython = ext == ".py" || ext == ".pyw" || ext == ".pyi";
                bool isNative = ext == ".cu" || ext == ".cuh" || ext == ".cpp" || ext == ".cc" ||
                                ext == ".cxx" || ext == ".c" || ext == ".h" || ext == ".hpp" ||
                                ext == ".hxx" || ext == ".hh";
                if (!isPython && !isNative) return;   // other languages: their loaders own the model

                // Drop all types that came from host file.
                for (int i = owner.Types.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(owner.Types[i].SourceFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                        owner.Types.RemoveAt(i);
                }

                if (isPython)
                {
                    // Re-parse and re-extract types from the saved file.
                    string source = File.ReadAllText(filePath);
                    var parsed = global::HelixPythonNamespace.Python.Python.ParseModule(source, filePath);
                    HelixPythonNamespace.Workspace.Python.PythonTypeExtractor.ExtractInto(owner, parsed.Module, filePath);
                }
                else
                {
                    // Native: the C++/CUDA extractor owns this file's types AND free functions.
                    for (int i = owner.Functions.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(owner.Functions[i].SourceFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                            owner.Functions.RemoveAt(i);
                    }
                    HelixPythonNamespace.Workspace.Cuda.CppTypeExtractor.ExtractFromFile(owner, filePath);
                }

                // Rebind type references across the solution so the new types resolve.
                foreach (TypeModel t in host._currentSolution.GetAllTypes())
                    foreach (TypeReference r in t.BaseTypes) r.Bind(host._currentSolution);

                // Refresh the canvas projection.
                host.OnRefreshCanvas(host, EventArgs.Empty);
                host._statusLabel.Text = "Diagram refreshed after save: " + Path.GetFileName(filePath);
            }
            catch (Exception ex)
            {
                host._statusLabel.Text = "Warning: refresh failed for " + Path.GetFileName(filePath) + " (" + ex.Message + ")";
            }
        }

        /// <summary>
        /// Apply editor saves that were deliberately deferred while the canvas was hidden.
        /// Called only when Class Designer Canvas becomes active, so expensive parsing/projection work
        /// cannot interrupt typing in the source editor.
        /// </summary>
        public void RefreshPendingCanvasChanges()
        {
            if (_pendingCanvasRefreshFiles.Count == 0) return;
            string[] files = _pendingCanvasRefreshFiles.ToArray();
            _pendingCanvasRefreshFiles.Clear();
            foreach (string file in files) OnEditorFileSaved(file);
        }

        // Persists the set of currently-open editor files against the solution so they
        // can be reopened next time it loads (see RestoreLastOpenEditorFiles).
        public void SaveOpenEditorFilesForSolution(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath) || host._settingsManager == null || host._editorInnerTabControl == null) return;
            var files = new List<string>();
            foreach (TabPage tab in host._editorInnerTabControl.TabPages)
            {
                if (tab is HelixPythonNamespace.Editor.EditorTabPage etp && !string.IsNullOrEmpty(etp.FilePath))
                    files.Add(etp.FilePath);
            }
            host._settingsManager.Settings.SetLastOpenEditorFiles(solutionPath, files);
        }

        // Reopens the editor files remembered for this solution, skipping any that no
        // longer exist on disk. Best-effort: a single failure won't abort the rest.
        public void RestoreLastOpenEditorFiles(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath) || host._settingsManager == null) return;
            List<string> files = host._settingsManager.Settings.GetLastOpenEditorFiles(solutionPath);
            foreach (string f in files)
            {
                if (!string.IsNullOrEmpty(f) && File.Exists(f))
                {
                    try { OpenInEditor(f); } catch { /* best effort */ }
                }
            }
        }

        /// <summary>
        /// Prompt for an existing notebook and open it. Reached from the notebook's own
        /// File ▸ "Open notebook…", which is the only File menu on screen while a notebook is
        /// showing. Filtered to <c>.ipynb</c> alone because the command names one thing; a user
        /// who wants a different file type has Open File in Editor on the shell menu.
        ///
        /// <para>
        /// No solution is required or created. <c>OpenInEditor</c> routes an <c>.ipynb</c> to the
        /// notebook surface through <c>IsNotebookPath</c>, and a notebook tab has never needed a
        /// solution — <c>View.Solution</c> is allowed to be null.
        /// </para>
        /// </summary>
        public void PromptOpenNotebook()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Jupyter Notebooks (*.ipynb)|*.ipynb|All Files (*.*)|*.*";
                dlg.Title = "Open Notebook";
                if (dlg.ShowDialog() == DialogResult.OK) OpenInEditor(dlg.FileName);
            }
        }

        // File ▸ Open File in Editor… — prompts for a Python file or a notebook and opens it.
        // This route never needed a solution, and a notebook picked here lands on the Colab
        // surface via OpenInEditor's IsNotebookPath branch. It just could not be SEEN: the filter
        // offered .py/.pyi and "All Files", so opening a notebook meant knowing to switch the
        // dropdown to a filter that shows every file on the disk. A capability the user cannot
        // find is one the IDE does not have, so .ipynb is named — and named FIRST, because this
        // is the one document type here that has nowhere else to be opened from.
        public void OnFileOpenInEditor(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Python and Notebook Files (*.py;*.pyi;*.ipynb)|*.py;*.pyi;*.ipynb"
                    + "|Jupyter Notebooks (*.ipynb)|*.ipynb"
                    + "|Python Files (*.py;*.pyi)|*.py;*.pyi"
                    + "|All Files (*.*)|*.*";
                dlg.Title = "Open File in Editor";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    OpenInEditor(dlg.FileName);
                }
            }
        }
    }
}
