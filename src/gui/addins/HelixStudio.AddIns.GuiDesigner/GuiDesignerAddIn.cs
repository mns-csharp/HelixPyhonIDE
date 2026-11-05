namespace HelixPythonNamespace.AddIns.GuiDesigner
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.AddIns;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.GuiDesigner.Model;
    using HelixPythonNamespace.UI.Controls;

    /// <summary>
    /// The GUI Designer Canvas as an add-in (spec 004, Task B): its tab of form documents, its menu
    /// item, the Solution Explorer's "GUI Design View" on form types, and what it knows about the
    /// solution it was opened on (<c>GuiDesignerAddIn.Awareness.cs</c>). Talks to the IDE only
    /// through <see cref="IAddInHost"/>; this used to be <c>MainForm</c> code.
    /// </summary>
    public sealed partial class GuiDesignerAddIn : IHelixAddIn
    {
        internal const string GuiDesignerTabKey = "guiDesigner";

        internal const string MenuCaption = "&GUI Designer Canvas...";

        internal const string GuiDesignViewCaption = "GUI Design View";

        /// <summary>The add-in setting holding the folded toolbox categories, one per line. Same
        /// name and format the host seeds from the legacy <c>ToolboxCollapsed["gui"]</c>.</summary>
        internal const string ToolboxCollapsedSetting = "ToolboxCollapsed";

        private IAddInHost _host;

        /// <summary>The one top-level tab, and the strip of form documents inside it.</summary>
        internal TabPage _guiDesignerTab;
        internal TabControl _guiDesignerTabs;

        /// <summary>The designer whose Toolbox and Properties are in the shell's docks now.</summary>
        private GuiDesignerForm _guiDesignerWithDockedPanels;
        private Panel _guiToolbox;
        private Panel _guiProperties;

        public string Id { get { return "gui-designer"; } }

        public string DisplayName { get { return "GUI Designer Canvas"; } }

        public void Initialize(IAddInHost host)
        {
            _host = host;

            // The Tools item it used to be: same caption, tooltip and visibility rule (§6.2).
            host.AddMenuItem(MenuCaption,
                "Design Python (Tkinter, PyQt6, PySide6, wxPython) or C++ Win32 forms visually.",
                OpenFromMenu, IsMenuVisible);

            // Distinct from "Open in Class Designer Canvas" (the class hierarchy): this opens the
            // form design surface, and only makes sense for a class that subclasses a known GUI
            // framework base (Tkinter/PyQt6/PySide6/wxPython) or a file with a design beside it.
            host.RegisterExplorerTypeCommand(GuiDesignViewCaption, CanDesign, OpenGuiDesignView);

            // Closing a solution closes its designers (SRS solution_explorer_rules 9), on the same
            // close route the shell used. An open raises the same event with a solution set.
            host.SolutionChanged += delegate
            {
                if (_host.SolutionDirectory == null) CloseGuiDesignerDocuments();
            };
            // ponytail: the tab is built on first use, as it always was: it is solution-scoped, and
            // registering it here would let the solution gate open an empty canvas uninvited
            // (Chapter 20, §20.6).
        }

        public void Shutdown()
        {
        }

        // ---------------- menu and Solution Explorer ----------------

        /// <summary>Visible in a Python workspace, or with a source it can bind to in front (§6.3:
        /// not for a native workspace on its own; its C++ Win32 binding is reached through an
        /// active .cpp/.h document).</summary>
        private bool IsMenuVisible()
        {
            if (_host.IsPythonSolutionOpen) return true;
            string ext;
            try { ext = (Path.GetExtension(_host.ActiveDocumentPath ?? string.Empty) ?? string.Empty).ToLowerInvariant(); }
            catch (ArgumentException) { return false; }
            return ext == ".py" || ext == ".cpp" || ext == ".cc" || ext == ".cxx" || ext == ".h" || ext == ".hpp";
        }

        /// <summary>The active document wins when there IS one: opening the designer while looking
        /// at a form means that form. With nothing open it is the SOLUTION's forms rather than a
        /// fabricated empty one.</summary>
        internal void OpenFromMenu()
        {
            string active = _host.ActiveDocumentPath;
            if (string.IsNullOrEmpty(active)) ShowGuiDesignerOnSolution();
            else ShowForSource(active);
        }

        /// <summary>Opens <paramref name="sourcePath"/> with the binding its extension implies; no
        /// path opens the sample form.</summary>
        internal void ShowForSource(string sourcePath)
        {
            OpenGuiDesignerDocument(GuiDesignerForm.FormForSource(sourcePath),
                string.IsNullOrEmpty(sourcePath) ? null : sourcePath);
        }

        /// <summary>"GUI Design View" is enabled for a form type, or for a file (a stand-in type,
        /// see <see cref="IAddInHost.RegisterExplorerTypeCommand"/>) with a <c>.designer.json</c>
        /// beside it.</summary>
        internal static bool CanDesign(TypeModel type)
        {
            return type != null && (GuiFormBridge.IsGuiFormType(type) || HasDesignerSidecar(type.SourceFilePath));
        }

        internal void OpenGuiDesignView(TypeModel type)
        {
            if (type == null) return;
            if (GuiFormBridge.IsGuiFormType(type))
            {
                OpenGuiDesignerDocument(GuiFormBridge.BuildFormFor(type), type.SourceFilePath);
                return;
            }
            if (HasDesignerSidecar(type.SourceFilePath)) ShowForSource(type.SourceFilePath);
        }

        private static bool HasDesignerSidecar(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return false;
            try
            {
                string directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                return File.Exists(Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath) + ".designer.json"));
            }
            catch (ArgumentException) { return false; }
        }

        // ---------------- the tab and its documents ----------------

        /// <summary>
        /// The GUI Designer Canvas's own top-level tab, built on first use.
        ///
        /// It used to be an inner page of the Class Designer Canvas, and that grouping was wrong: the
        /// Class Designer Canvas is the CLASS DIAGRAM of the solution — a view OF code — while a GUI
        /// Designer is a document the user authors, sitting beside the Editor and other design surfaces.
        ///
        /// It keeps an inner strip of its own, because several forms can be open at once.
        /// </summary>
        private TabControl GuiDesignerDocuments()
        {
            if (_guiDesignerTabs == null)
            {
                _guiDesignerTabs = new TabControl { Dock = DockStyle.Fill, Font = LunaFonts.Sized(-0.5f) };
                _guiDesignerTab = new TabPage("GUI Designer Canvas");
                _guiDesignerTab.ToolTipText = "GUI Designer Canvas — form design surfaces, one page per form";
                _guiDesignerTab.Controls.Add(_guiDesignerTabs);
                // Solution-scoped. Activated hands the panes to the form in front; Removed runs only
                // for a real close of the tab (not for the gate's reorder churn) and takes them back.
                _host.RegisterTab(GuiDesignerTabKey, _guiDesignerTab, true, new AddInTabCallbacks
                {
                    Activated = ClaimGuiPanes,
                    Removed = UndockGuiDesignerPanels,
                });
                // Hooked on the INNER strip, not on the page: removing a TabPage does not dispose
                // it, so a Disposed handler would never run and the panels would outlive their designer.
                _guiDesignerTabs.ControlRemoved += OnDesignerTabRemoved;
                // Switching between two forms inside this strip changes which designer owns the
                // shared panes without the top-level selection changing at all, so the host's
                // router never hears of it.
                _guiDesignerTabs.SelectedIndexChanged += delegate { if (IsInFront()) ClaimGuiPanes(); };
            }
            return _guiDesignerTabs;
        }

        private bool IsInFront()
        {
            var strip = _guiDesignerTab != null ? _guiDesignerTab.Parent as TabControl : null;
            return strip != null && ReferenceEquals(strip.SelectedTab, _guiDesignerTab);
        }

        internal void OpenGuiDesignerDocument(GuiForm form, string sourcePath)
        {
            if (form == null) return;
            TabControl documents = GuiDesignerDocuments();
            string key = Path.GetFullPath(sourcePath ?? form.FormClassName);
            foreach (TabPage existing in documents.TabPages)
            {
                if (string.Equals(existing.Tag as string, key, StringComparison.OrdinalIgnoreCase))
                {
                    documents.SelectedTab = existing;
                    // Re-claim the Toolbox and Properties. This is NOT redundant with the docking
                    // done for a freshly created designer: closing the top-level tab hands the
                    // panels back to the designer but leaves this INNER page in place, so reopening
                    // the same form comes through here. Without this, the designer docked its
                    // panels correctly exactly once per session and every reopen after that showed
                    // them un-tabbed. DockGuiDesignerPanels is a no-op when they are already docked.
                    GuiDesignerForm reopened = existing.Controls.Count > 0
                        ? existing.Controls[0] as GuiDesignerForm
                        : null;
                    if (reopened != null) DockGuiDesignerPanels(reopened);
                    // The tab is user-hideable: re-show it, or this command appears to do nothing
                    // after the user hid that tab.
                    _host.ShowTab(GuiDesignerTabKey);
                    return;
                }
            }

            var designer = new GuiDesignerForm(form, sourcePath)
            {
                TopLevel = false,
                FormBorderStyle = FormBorderStyle.None,
                Dock = DockStyle.Fill,
                EmbeddedSurface = true,
            };
            PersistToolboxCollapse(designer);
            // Save As and Open Design open the result as its own document rather than repointing
            // this window at it — a designer window is bound to one file for its life.
            designer.OpenDocumentRequested = OpenGuiDesignerDocument;
            var tab = new TabPage(Path.GetFileName(sourcePath ?? form.FormClassName) + " [Design]") { Tag = key };
            tab.Controls.Add(designer);
            documents.TabPages.Add(tab);
            documents.SelectedTab = tab;
            DockGuiDesignerPanels(designer);
            // Re-show the host tab before selecting it; direct SelectedTab assignment cannot
            // resurrect a page removed by View ▸ Panels.
            _host.ShowTab(GuiDesignerTabKey);
            designer.Show();
        }

        /// <summary>
        /// Closes every open GUI Designer Canvas document and gives back the panels they borrowed. Used
        /// when a solution closes, so a designer for a file that is no longer open cannot be left
        /// holding the Solution Explorer's dock.
        /// </summary>
        internal void CloseGuiDesignerDocuments()
        {
            if (_guiDesignerTabs == null) return;
            var doomed = new List<TabPage>();
            foreach (TabPage page in _guiDesignerTabs.TabPages)
                if (page.Controls.Count > 0 && page.Controls[0] is GuiDesignerForm) doomed.Add(page);
            foreach (TabPage page in doomed)
            {
                _guiDesignerTabs.TabPages.Remove(page);
                page.Dispose();
            }
            UndockGuiDesignerPanels();

            // The host tab goes with the last document in it: an empty "GUI Designer Canvas" tab is a
            // surface with nothing on it, and §9 says a closed solution leaves no designer behind.
            if (_guiDesignerTab != null && _guiDesignerTab.Parent != null) _host.CloseTab(GuiDesignerTabKey);
        }

        /// <summary>A designer document went away; its docked panels must not outlive it.</summary>
        private void OnDesignerTabRemoved(object sender, ControlEventArgs e)
        {
            var page = e.Control as TabPage;
            if (page == null || _guiDesignerWithDockedPanels == null) return;
            if (!ContainsDesigner(page, _guiDesignerWithDockedPanels)) return;
            UndockGuiDesignerPanels();
        }

        private static bool ContainsDesigner(Control root, Control designer)
        {
            if (ReferenceEquals(root, designer)) return true;
            foreach (Control child in root.Controls)
                if (ContainsDesigner(child, designer)) return true;
            return false;
        }

        // ---------------- Toolbox and Properties ----------------

        /// <summary>Gives the Toolbox and Properties to the form that is actually on screen: the
        /// selected inner page, not whichever one happened to dock its panels first.</summary>
        private void ClaimGuiPanes()
        {
            GuiDesignerForm designer = ActiveGuiDesigner();
            if (designer != null) DockGuiDesignerPanels(designer);
        }

        /// <summary>The GUI form on screen right now, or null when the designer tab is empty.</summary>
        internal GuiDesignerForm ActiveGuiDesigner()
        {
            if (_guiDesignerTabs == null) return null;
            TabPage page = _guiDesignerTabs.SelectedTab;
            if (page == null) return null;
            GuiDesignerForm designer = FindGuiDesigner(page);
            return designer != null && !designer.IsDisposed ? designer : null;
        }

        private static GuiDesignerForm FindGuiDesigner(Control root)
        {
            var designer = root as GuiDesignerForm;
            if (designer != null) return designer;
            foreach (Control child in root.Controls)
            {
                GuiDesignerForm found = FindGuiDesigner(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Moves a GUI Designer Canvas's Toolbox into the Solution Explorer's group, and its
        /// PROPERTIES pane to the right-hand dock: an inspector and the thing it inspects have to be
        /// on screen together. The host claims a pane only when it is not already the one on screen
        /// (another canvas may have taken the shared panels in between), so this asks the panel what
        /// it shows rather than trusting a flag.
        /// </summary>
        private void DockGuiDesignerPanels(GuiDesignerForm designer)
        {
            if (designer == null) return;
            if (ReferenceEquals(_guiDesignerWithDockedPanels, designer))
            {
                if (_guiToolbox != null) _host.ClaimToolPanel("Toolbox", _guiToolbox, DockSide.Left);
                if (_guiProperties != null) _host.ClaimToolPanel("Properties", _guiProperties, DockSide.Right);
                return;
            }
            UndockGuiDesignerPanels();

            Panel toolbox = designer.DetachToolbox();
            Panel properties = designer.DetachProperties();
            if (toolbox == null && properties == null) return;

            // Toolbox first, then Properties: the order the shell always docked them in.
            _guiDesignerWithDockedPanels = designer;
            if (toolbox != null) { _guiToolbox = toolbox; _host.ClaimToolPanel("Toolbox", toolbox, DockSide.Left); }
            if (properties != null) { _guiProperties = properties; _host.ClaimToolPanel("Properties", properties, DockSide.Right); }
        }

        /// <summary>Gives a GUI Designer Canvas its Toolbox and Properties back.</summary>
        internal void UndockGuiDesignerPanels()
        {
            GuiDesignerForm designer = _guiDesignerWithDockedPanels;
            _guiDesignerWithDockedPanels = null;
            Panel toolbox = _guiToolbox, properties = _guiProperties;
            _guiToolbox = null;
            _guiProperties = null;

            if (toolbox != null) _host.ReleaseToolPanel("Toolbox", toolbox);
            if (properties != null) _host.ReleaseToolPanel("Properties", properties);

            if (designer != null && !designer.IsDisposed)
            {
                if (toolbox != null) designer.ReattachToolbox(toolbox);
                if (properties != null) designer.ReattachProperties(properties);
            }
        }

        /// <summary>Restores the designer's folds and keeps them saved from then on. Restore first,
        /// subscribe second, so the restore does not write back what was just read.</summary>
        private void PersistToolboxCollapse(GuiDesignerForm designer)
        {
            string saved = _host.GetSetting(ToolboxCollapsedSetting);
            designer.CollapsedToolboxCategories = string.IsNullOrEmpty(saved)
                ? new List<string>()
                : saved.Split('\n').Where(s => s.Length > 0).ToList();
            designer.ToolboxCollapseChanged += delegate
            {
                IList<string> folded = designer.CollapsedToolboxCategories;
                _host.SetSetting(ToolboxCollapsedSetting, folded == null ? string.Empty : string.Join("\n", folded));
            };
        }
    }
}
