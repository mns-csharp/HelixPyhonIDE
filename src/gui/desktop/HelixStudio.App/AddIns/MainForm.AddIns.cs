namespace HelixPythonNamespace.App
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Windows.Forms;
    using HelixPythonNamespace.AddIns;

    /// <summary>
    /// The shell side of add-ins (spec 001, §4.4, §4.7): the list of loaded add-ins, the
    /// top-level Add-in menu, the file-opener registry and the tab callbacks. Each add-in talks to
    /// the shell only through its own <see cref="AddInHost"/>, which records what it registered so
    /// <see cref="UnloadAddIn"/> can take all of it away again.
    /// </summary>
    public partial class MainForm
    {
        /// <summary>The add-in setting the legacy toolbox folds are seeded into (categories, one per line).</summary>
        internal const string AddInToolboxCollapsedSetting = "ToolboxCollapsed";

        private readonly List<LoadedAddIn> _addIns = new List<LoadedAddIn>();
        private readonly Dictionary<TabPage, AddInTabCallbacks> _addInTabs = new Dictionary<TabPage, AddInTabCallbacks>();
        private ToolStripMenuItem _addInMenu;

        /// <summary>The add-in tab that was last activated and has not been deactivated since.</summary>
        private TabPage _selectedAddInTab;

        /// <summary>Every add-in loaded now, in load order.</summary>
        internal IReadOnlyList<LoadedAddIn> LoadedAddIns
        {
            get { return _addIns.AsReadOnly(); }
        }

        internal UserSettings AddInUserSettings
        {
            get { return _settingsManager == null ? null : _settingsManager.Settings; }
        }

        internal void SaveAddInSettings()
        {
            try { if (_settingsManager != null) _settingsManager.Save(); }
            catch (Exception) { }
        }

        /// <summary>Startup: seed, build the menu, scan both roots. Runs before the Load event's
        /// tab-visibility replay, so add-in tabs are known by then (Chapter 3, §3.6.2).</summary>
        private void LoadAddIns()
        {
            SeedAddInSettingsFromLegacyToolbox(AddInUserSettings);
            EnsureAddInMenu();
            try { AddInLoader.LoadAll(this); }
            catch (Exception ex) { AppendBuildOutputLine("Add-ins: loading stopped: " + AddInLoader.Describe(ex)); }
        }

        /// <summary>
        /// Loads one add-in: the scan's path without the scan (tests use it). Refuses a second add-in
        /// with an Id already loaded. An add-in that throws in Initialize is reported, whatever it
        /// registered before throwing is taken away again, and the IDE carries on.
        /// </summary>
        internal LoadedAddIn LoadAddIn(IHelixAddIn instance, AddInKind kind, string folder, string source = null)
        {
            if (instance == null) return null;
            string from = source ?? instance.GetType().FullName;

            string id;
            try { id = instance.Id; }
            catch (Exception ex)
            {
                AppendBuildOutputLine("Add-in " + from + " could not be loaded: " + AddInLoader.Describe(ex));
                return null;
            }
            if (string.IsNullOrEmpty(id))
            {
                AppendBuildOutputLine("Add-in " + from + " was not loaded: it has no Id.");
                return null;
            }
            // The user removed it (Preferences ▸ Install Add-in): not loaded, from either root.
            if (AddInLoader.IsRemoved(AddInUserSettings, id)) return null;
            LoadedAddIn existing = FindAddIn(id);
            if (existing != null)
            {
                AppendBuildOutputLine("Add-in " + from + " was not loaded: an add-in with Id '" + id
                    + "' is already loaded (" + (existing.Folder ?? existing.Instance.GetType().FullName) + ").");
                return null;
            }

            EnsureAddInMenu();
            var host = new AddInHost(this, id);
            LoadedAddIn loaded;
            try
            {
                loaded = new LoadedAddIn(instance, kind, folder, host);
                instance.Initialize(host);
            }
            catch (Exception ex)
            {
                AppendBuildOutputLine("Add-in " + from + " failed to initialize: " + AddInLoader.Describe(ex));
                host.UndoAll();
                return null;
            }

            _addIns.Add(loaded);
            RebuildAddInMenu();
            return loaded;
        }

        /// <summary>
        /// Takes a loaded add-in out of the running IDE: Shutdown, close its tabs (their Removed
        /// callbacks run), give back its panes, remove its menu items, file openers and explorer
        /// commands. Its assembly stays in memory until restart (spec 001, §4.6 ruling).
        /// </summary>
        internal bool UnloadAddIn(string id)
        {
            LoadedAddIn addIn = FindAddIn(id);
            if (addIn == null) return false;
            try { addIn.Instance.Shutdown(); }
            catch (Exception ex) { AppendBuildOutputLine("Add-in " + id + ": Shutdown failed: " + AddInLoader.Describe(ex)); }
            addIn.Host.UndoAll();
            _addIns.Remove(addIn);
            RebuildAddInMenu();
            return true;
        }

        /// <summary>
        /// Preferences ▸ Install Add-in ▸ Remove: <see cref="UnloadAddIn"/>, then remember it. The Id
        /// goes on <c>RemovedAddIns</c> so no later start loads it; a user-installed folder is
        /// queued for deletion at the next start (Windows locks a loaded DLL). Only a folder under
        /// the user root is ever queued: Remove never deletes a bundled or foreign folder.
        /// </summary>
        internal bool RemoveAddIn(string id)
        {
            // An installed add-in that failed to start is listed too, and can be removed as well.
            AddInCatalogEntry addIn = AddInCatalog.List(this).FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (addIn == null) return false;
            if (addIn.Loaded) UnloadAddIn(id);

            UserSettings settings = AddInUserSettings;
            if (settings != null)
            {
                if (settings.RemovedAddIns == null) settings.RemovedAddIns = new List<string>();
                if (!settings.RemovedAddIns.Contains(id)) settings.RemovedAddIns.Add(id);
                if (addIn.Kind == AddInKind.User && AddInInstaller.IsInUserRoot(addIn.Folder))
                {
                    if (settings.PendingAddInDeletes == null) settings.PendingAddInDeletes = new List<string>();
                    if (!settings.PendingAddInDeletes.Contains(addIn.Folder, StringComparer.OrdinalIgnoreCase))
                        settings.PendingAddInDeletes.Add(addIn.Folder);
                }
                SaveAddInSettings();
            }
            return true;
        }

        /// <summary>Add-in ▸ Manage Add-ins...: Preferences, opened on the Install Add-in tab.</summary>
        private void OnManageAddIns(object sender, EventArgs e)
        {
            ShowPreferences(Dialogs.PreferencesDialog.InstallAddInTabText);
        }

        /// <summary>The Preferences dialog, wired to this shell's add-ins and opened on
        /// <paramref name="tab"/> (a tab's text; null keeps the first tab).</summary>
        internal Dialogs.PreferencesDialog CreatePreferencesDialog(string tab, Services.BuildToolDetector detector)
        {
            var dialog = new Dialogs.PreferencesDialog(_settingsManager, detector);
            dialog.AttachAddInShell(this);
            if (tab != null) dialog.SelectTab(tab);
            return dialog;
        }

        /// <summary>The loaded add-in of type <typeparamref name="T"/>, or null. For tests.</summary>
        internal T AddIn<T>() where T : class, IHelixAddIn
        {
            return _addIns.Select(a => a.Instance).OfType<T>().FirstOrDefault();
        }

        private LoadedAddIn FindAddIn(string id)
        {
            return _addIns.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
        }

        /// <summary>OnFormClosing: every add-in's Shutdown, each isolated (Chapter 3, §3.7).</summary>
        private void ShutdownAddIns()
        {
            foreach (LoadedAddIn addIn in _addIns.ToArray())
            {
                try { addIn.Instance.Shutdown(); }
                catch { }
            }
        }

        /// <summary>A solution opened or closed, or a new workspace started.</summary>
        internal void RaiseAddInSolutionChanged()
        {
            foreach (LoadedAddIn addIn in _addIns.ToArray())
                addIn.Host.RaiseSolutionChanged();
        }

        /// <summary>File ▸ Open rows the loaded add-ins contributed, in load order.</summary>
        internal IEnumerable<KeyValuePair<string, string>> AddInOpenFileFilters()
        {
            return _addIns.ToArray().SelectMany(a => a.Host.OpenFileFilters.ToArray());
        }

        /// <summary>The add-ins' file openers come first; true when one claimed the path.</summary>
        private bool OpenWithAddIn(string path)
        {
            foreach (LoadedAddIn addIn in _addIns.ToArray())
                if (addIn.Host.TryOpenFile(path)) return true;
            return false;
        }

        // ---------------- tabs ----------------

        internal void RegisterAddInTab(TabPage page, AddInTabCallbacks callbacks)
        {
            _addInTabs[page] = callbacks ?? new AddInTabCallbacks();
            if (_mainTabControl != null)
            {
                _mainTabControl.ControlRemoved -= OnMainTabRemoved;
                _mainTabControl.ControlRemoved += OnMainTabRemoved;
            }
            EnsureToolboxRouting();
        }

        /// <summary>Stops dispatching to the tab, takes it off the strip and out of the registry,
        /// then tells the add-in it is gone.</summary>
        internal void UnregisterAddInTab(string key, TabPage page)
        {
            AddInTabCallbacks callbacks;
            if (!_addInTabs.TryGetValue(page, out callbacks)) return;
            _addInTabs.Remove(page);
            if (ReferenceEquals(_selectedAddInTab, page)) _selectedAddInTab = null;
            if (_mainTabControl != null && _mainTabControl.TabPages.Contains(page)) CloseTab(page);
            _panelCoordinator.UnregisterTopLevelTab(key);
            InvokeAddInCallback(callbacks.Removed, "Removed");
        }

        /// <summary>
        /// The deferred re-check the CG designer's tab used to do itself, for every add-in tab: a
        /// reorder removes and re-inserts a page, so only a page still missing at the end of the
        /// message queue was closed.
        /// </summary>
        private void NotifyAddInTabRemovedIfGone(TabPage page)
        {
            if (IsDisposed || !IsHandleCreated) { FireAddInTabRemoved(page); return; }
            BeginInvoke((Action)delegate
            {
                if (IsDisposed) return;
                if (_mainTabControl != null && _mainTabControl.TabPages.Contains(page)) return;
                FireAddInTabRemoved(page);
            });
        }

        private void FireAddInTabRemoved(TabPage page)
        {
            AddInTabCallbacks callbacks;
            if (_addInTabs.TryGetValue(page, out callbacks)) InvokeAddInCallback(callbacks.Removed, "Removed");
        }

        private void ActivateAddInTab(TabPage page)
        {
            DeactivateLeftAddInTab();
            AddInTabCallbacks callbacks;
            if (page != null && _addInTabs.TryGetValue(page, out callbacks))
            {
                _selectedAddInTab = page;
                InvokeAddInCallback(callbacks.Activated, "Activated");
            }
        }

        /// <summary>
        /// Deactivated for the add-in tab that was in front and is not any more (another tab was
        /// selected, or it left the strip). Idempotent. The main strip's selection handler calls it
        /// BEFORE <c>ApplyNotebookMode</c>: notebook mode snapshots the explorer on entry, so a tab
        /// that put the explorer aside must have brought it back first (the PDB Viewer's structure
        /// mode; the order the shell kept before the viewer became an add-in).
        /// </summary>
        internal void DeactivateLeftAddInTab()
        {
            TabPage was = _selectedAddInTab;
            if (was == null || _mainTabControl == null) return;
            if (_mainTabControl.SelectedTab == was && _mainTabControl.TabPages.Contains(was)) return;
            _selectedAddInTab = null;
            AddInTabCallbacks callbacks;
            if (_addInTabs.TryGetValue(was, out callbacks)) InvokeAddInCallback(callbacks.Deactivated, "Deactivated");
        }

        private bool IsAddInTab(TabPage page)
        {
            return page != null && _addInTabs.ContainsKey(page);
        }

        private void InvokeAddInCallback(Action callback, string what)
        {
            if (callback == null) return;
            try { callback(); }
            catch (Exception ex) { AppendBuildOutputLine("Add-in tab " + what + " failed: " + AddInLoader.Describe(ex)); }
        }

        // ---------------- the Add-in menu (§4.7) ----------------

        /// <summary>Top-level "Add-in", right after Tools. Built in code: its content is whatever
        /// is loaded, so the designer file has nothing to describe.</summary>
        private void EnsureAddInMenu()
        {
            if (_addInMenu != null || _menuStrip == null) return;
            _addInMenu = new ToolStripMenuItem("Add-i&n") { Name = "addInMenuItem" };
            _addInMenu.DropDownOpening += OnAddInMenuOpening;
            int at = toolsMenuItem != null ? _menuStrip.Items.IndexOf(toolsMenuItem) : -1;
            if (at >= 0) _menuStrip.Items.Insert(at + 1, _addInMenu);
            else _menuStrip.Items.Add(_addInMenu);
            RebuildAddInMenu();
        }

        internal ToolStripMenuItem AddInMenuForTests
        {
            get { return _addInMenu; }
        }

        internal void RebuildAddInMenu()
        {
            if (_addInMenu == null) return;
            _addInMenu.DropDownItems.Clear();
            foreach (LoadedAddIn addIn in _addIns)
            {
                var sub = new ToolStripMenuItem((addIn.DisplayName ?? addIn.Id).Replace("&", "&&"));
                foreach (AddInHost.MenuEntry entry in addIn.Host.MenuEntries)
                {
                    AddInHost.MenuEntry e = entry;
                    string id = addIn.Id;
                    var item = new ToolStripMenuItem(e.Caption) { Tag = e, ToolTipText = e.ToolTip };
                    item.Click += delegate
                    {
                        try { if (e.Run != null) e.Run(); }
                        catch (Exception ex)
                        {
                            AppendBuildOutputLine("Add-in " + id + ": \"" + e.Caption.Replace("&", "")
                                + "\" failed: " + AddInLoader.Describe(ex));
                        }
                    };
                    sub.DropDownItems.Add(item);
                }
                _addInMenu.DropDownItems.Add(sub);
            }
            if (_addIns.Count == 0)
                _addInMenu.DropDownItems.Add(new ToolStripMenuItem("(no add-ins installed)") { Enabled = false });
            _addInMenu.DropDownItems.Add(new ToolStripSeparator());
            _addInMenu.DropDownItems.Add(new ToolStripMenuItem(ManageAddInsCaption, null, OnManageAddIns) { Name = "manageAddInsMenuItem" });
        }

        internal const string ManageAddInsCaption = "Manage Add-ins...";

        /// <summary>Applies each item's visible predicate; a submenu with nothing visible is hidden.</summary>
        internal void OnAddInMenuOpening(object sender, EventArgs e)
        {
            if (_addInMenu == null) return;
            foreach (ToolStripItem top in _addInMenu.DropDownItems)
            {
                var sub = top as ToolStripMenuItem;
                if (sub == null || sub.DropDownItems.Count == 0) continue;
                bool any = false;
                foreach (ToolStripItem child in sub.DropDownItems)
                {
                    var entry = child.Tag as AddInHost.MenuEntry;
                    bool visible = true;
                    if (entry != null && entry.Visible != null)
                    {
                        try { visible = entry.Visible(); }
                        catch (Exception) { visible = false; }
                    }
                    child.Available = visible;
                    any |= visible;
                }
                sub.Available = any;
            }
        }

        // ---------------- settings ----------------

        /// <summary>
        /// One-time migration (spec 004, §4.4): the CG and GUI toolbox folds move from
        /// <c>ToolboxCollapsed["cg"/"gui"]</c> to <c>AddInSettings["cg-designer"/"gui-designer"]</c>.
        /// Never overwrites: an add-in that already has the setting keeps its own value.
        /// </summary>
        internal static void SeedAddInSettingsFromLegacyToolbox(UserSettings settings)
        {
            if (settings == null || settings.ToolboxCollapsed == null) return;
            if (settings.AddInSettings == null)
                settings.AddInSettings = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            Seed(settings, ToolboxKeyCg, "cg-designer");
            Seed(settings, ToolboxKeyGui, "gui-designer");
        }

        private static void Seed(UserSettings settings, string legacyKey, string addInId)
        {
            List<string> folded;
            if (!settings.ToolboxCollapsed.TryGetValue(legacyKey, out folded) || folded == null) return;

            Dictionary<string, string> values;
            if (!settings.AddInSettings.TryGetValue(addInId, out values) || values == null)
                settings.AddInSettings[addInId] = values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (values.ContainsKey(AddInToolboxCollapsedSetting)) return;
            values[AddInToolboxCollapsedSetting] = string.Join("\n", folded);
        }
    }
}
