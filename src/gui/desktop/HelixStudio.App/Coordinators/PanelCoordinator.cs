namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;

    /// <summary>
    /// Owns the top-level tab registry and panel visibility behaviour: registering
    /// closable tabs, showing/hiding them, restoring them to their default position,
    /// keeping the View &gt; Panels menu/toolbar toggles in sync, and persisting
    /// visibility to user settings. <see cref="MainForm"/> keeps thin delegating
    /// wrappers so existing callers and other coordinators are unaffected.
    /// </summary>
    public sealed class PanelCoordinator : ICoordinator
    {
        public sealed class WorkbenchPadDescriptor
        {
            public string Key { get; set; }
            public bool SolutionScoped { get; set; }
            public bool ResultScoped { get; set; }
            public int Order { get; set; }
        }

        private MainFormContext _context;
        private MainForm host;
        private readonly Dictionary<string, WorkbenchPadDescriptor> pads =
            new Dictionary<string, WorkbenchPadDescriptor>(StringComparer.Ordinal);

        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        public void Detach()
        {
            _context = null;
            host = null;
        }

        // Record a tab in the registry the first time it's seen. The three parallel maps
        // are: key->page (lookup), page->key (reverse lookup on close), and key->order
        // (its stable "default" index, captured as insertion order so ComputeInsertIndex
        // can restore a re-shown tab to roughly its original slot).
        public void RegisterTopLevelTab(string key, TabPage page)
        {
            bool result = DefaultResultTabs.Contains(key);
            RegisterTopLevelTab(key, page, !string.Equals(key, "welcome", StringComparison.Ordinal) &&
                !string.Equals(key, "terminal", StringComparison.Ordinal), result);
        }

        public void RegisterTopLevelTab(string key, TabPage page, bool solutionScoped, bool resultScoped)
        {
            if (string.IsNullOrEmpty(key) || page == null) return;
            if (!host._knownTabs.ContainsKey(key))
            {
                host._knownTabs[key] = page;
                host._tabKeysByPage[page] = key;
                host._tabDefaultOrder[key] = host._tabDefaultOrder.Count;
                pads[key] = new WorkbenchPadDescriptor
                {
                    Key = key,
                    SolutionScoped = solutionScoped,
                    ResultScoped = resultScoped,
                    Order = host._tabDefaultOrder[key]
                };
            }
        }

        /// <summary>Forgets a tab (an add-in was removed). The caller takes it off the strip first.</summary>
        public void UnregisterTopLevelTab(string key)
        {
            TabPage page;
            if (string.IsNullOrEmpty(key) || !host._knownTabs.TryGetValue(key, out page)) return;
            host._knownTabs.Remove(key);
            if (page != null) host._tabKeysByPage.Remove(page);
            host._tabDefaultOrder.Remove(key);
            pads.Remove(key);
        }

        public IReadOnlyList<WorkbenchPadDescriptor> RegisteredPads =>
            new List<WorkbenchPadDescriptor>(pads.Values);

        // A tab counts as visible if it's known AND currently in the tab control — OR it's one of
        // the two panes that can be pulled into a floating window (BuildAnalysisCoordinator).
        // While floating, the TabPage itself is removed from the strip (its content lives in the
        // floating form instead), so without this a Panels-menu toggle would see "not visible" and
        // re-insert the now-empty TabPage alongside the still-open floating window — the state
        // desync behind the duplicate/blank "Output" tab bug. Treating "floating" as "visible"
        // makes the toggle a safe no-op instead.
        private bool IsTabVisible(string key)
        {
            if (key == "analysis" && host._analysisResultsUndocked) return true;
            if (key == "buildResults" && host._buildResultsUndocked) return true;
            return host._knownTabs.TryGetValue(key, out TabPage p) && host._mainTabControl != null
                && host._mainTabControl.TabPages.Contains(p);
        }

        /// <summary>
        /// Top-level tabs that only mean anything once a solution is open. With nothing loaded there
        /// is no code to design, edit, debug, analyse, compile, measure, test or profile, so these
        /// are removed from the strip entirely rather than left as a row of dead tabs.
        ///
        /// Welcome stays as the entry workflow. The standalone Terminal popup is not a top-level IDE
        /// tab and has no solution-scoped lifecycle.
        /// </summary>
        /// <summary>
        /// The subset of <see cref="SolutionScopedTabs"/> whose CONTENT is produced by running
        /// something against one specific solution, and is therefore wiped by
        /// <c>SolutionCoordinator.NewEmptyWorkspace</c> when that solution closes. Exactly the panels
        /// cleared there — keep the two lists in step.
        ///
        /// The rule is symmetric: if closing a solution clears the panel, opening the NEXT solution
        /// must not bring its tab back. These appear only when their engine actually produces results
        /// for the open solution (the command calls <see cref="ShowTab"/>), or when the user toggles
        /// one on from View ▸ Panels while that solution is open — never by restore.
        ///
        /// Without this they leaked across solutions two ways: <see cref="ShowTab"/> stamped a GLOBAL
        /// <c>VisibleTopLevelTabs[key] = true</c> that <see cref="ApplySolutionGate"/> then replayed for
        /// every later solution, and an unknown key defaulted to visible, so a first-ever solution open
        /// showed the whole row. Reported: a Python session's Output/Lint/Dead Code/Metrics/
        /// Complexity tabs reappeared — empty — over an unrelated C++/CUDA solution.
        /// </summary>
        private static readonly HashSet<string> DefaultResultTabs = new HashSet<string>(StringComparer.Ordinal)
        {
            "analysis", "buildResults", "problems", "lint", "deadcode", "metrics",
            "complexity", "codeReview", "tests", "profile", "references", "callHierarchy",
        };

        /// <summary>
        /// Applies the solution gate to the tab strip.
        ///
        /// Crucially this does NOT touch the persisted VisibleTopLevelTabs preferences: gating is a
        /// consequence of the workflow, not a choice the user made, so hiding a tab here must not be
        /// remembered as "the user closed it". When a solution opens, each gated tab returns only if
        /// the user's own preference says it should be visible — and <see cref="ResultTabs"/> never
        /// return at all, since their results died with the solution that produced them.
        /// </summary>
        /// <summary>
        /// The solution-scoped pads a file opened on its own still needs: the document itself,
        /// the rules it broke, the output of running it, and the debugger.
        ///
        /// Deliberately not all of them. The canvas, the designers and the analysis results are
        /// views OF A SOLUTION'S STRUCTURE — a loose file gives them nothing to draw, and a tab
        /// that opens onto an empty surface is exactly what the gate exists to prevent.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> SingleFilePads =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            { "editor", "problems", "buildResults", "debug" };

        public void ApplySolutionGate(bool hasSolution)
        {
            ApplySolutionGate(hasSolution, false);
        }

        public void ApplySolutionGate(bool hasSolution, bool singleFile)
        {
            if (host._mainTabControl == null) return;

            foreach (WorkbenchPadDescriptor descriptor in pads.Values)
            {
                if (!descriptor.SolutionScoped) continue;
                string key = descriptor.Key;
                if (!host._knownTabs.TryGetValue(key, out TabPage tab) || tab == null) continue;
                bool present = host._mainTabControl.TabPages.Contains(tab);
                bool wanted = hasSolution || (singleFile && SingleFilePads.Contains(key));

                if (!wanted)
                {
                    if (present) host._mainTabControl.TabPages.Remove(tab);   // no PersistTabVisibility
                    continue;
                }

                if (present) continue;
                if (descriptor.ResultScoped) continue;                      // results belong to one solution
                if (!UserWantsVisible(key)) continue;                        // respect an explicit close

                // TabPages.Insert silently drops the page when the control has no handle (the trap
                // fixed for the editor in iter 315). This gate runs during layout restore, before
                // the window is shown, so without this the tabs would never come back.
                EditorCoordinator.EnsureRealized(host._mainTabControl);
                host._mainTabControl.TabPages.Insert(ComputeInsertIndex(key), tab);
            }

            SyncPanelToggleState();
            host._mainTabControl.Invalidate();
        }

        /// <summary>
        /// Take every <see cref="ResultTabs"/> entry off the strip without touching user preferences —
        /// the tab-strip half of dropping a solution's engine results (the panel half is
        /// SolutionCoordinator.ResetEngineResultPanels). File ▸ Close gets this for free from
        /// <see cref="ApplySolutionGate"/>; opening a solution straight over an open one does its own
        /// teardown and calls this explicitly.
        /// </summary>
        public void HideResultTabs()
        {
            if (host._mainTabControl == null) return;
            foreach (WorkbenchPadDescriptor descriptor in pads.Values)
            {
                if (!descriptor.ResultScoped) continue;
                string key = descriptor.Key;
                if (!host._knownTabs.TryGetValue(key, out TabPage tab) || tab == null) continue;
                if (host._mainTabControl.TabPages.Contains(tab)) host._mainTabControl.TabPages.Remove(tab);
            }
            SyncPanelToggleState();
            host._mainTabControl.Invalidate();
        }

        /// <summary>The user's saved preference for a tab; unknown keys default to visible.</summary>
        private bool UserWantsVisible(string key)
        {
            var prefs = host._settingsManager != null && host._settingsManager.Settings != null
                ? host._settingsManager.Settings.VisibleTopLevelTabs
                : null;
            bool wanted;
            return prefs == null || !prefs.TryGetValue(key, out wanted) || wanted;
        }

        // Show (or focus) a panel's tab. If already present, just select it; otherwise
        // insert it at its computed default position, then persist + resync the toggles.
        public void ShowTab(string key)
        {
            if (!host._knownTabs.TryGetValue(key, out TabPage tab)) return;
            if (host._mainTabControl.TabPages.Contains(tab))
            {
                host._mainTabControl.SelectedTab = tab;
                return;
            }
            int insertAt = ComputeInsertIndex(key);
            EditorCoordinator.EnsureRealized(host._mainTabControl);   // see ApplySolutionGate
            host._mainTabControl.TabPages.Insert(insertAt, tab);
            host._mainTabControl.SelectedTab = tab;
            // Remember the user closed/opened it, and keep menu/toolbar checks in sync.
            PersistTabVisibility(key, true);
            SyncPanelToggleState();
            host._mainTabControl.Invalidate();
        }

        // Remove a tab from the control (hide, not dispose), persisting the hidden state.
        public void CloseTab(TabPage tab)
        {
            if (tab == null || host._mainTabControl == null) return;
            if (!host._mainTabControl.TabPages.Contains(tab)) return;
            host._mainTabControl.TabPages.Remove(tab);
            // Reverse-map back to the key so we can record it as no longer visible.
            if (host._tabKeysByPage.TryGetValue(tab, out string key)) PersistTabVisibility(key, false);
            SyncPanelToggleState();
            host._mainTabControl.Invalidate();
        }

        /// <summary>
        /// Adds/removes a tab from the strip WITHOUT persisting to user settings — for automatic,
        /// lifecycle-driven visibility (e.g. the Welcome tab disappearing when a solution opens and
        /// reappearing when it closes) that must NOT be remembered as a deliberate user close/open the
        /// way <see cref="ShowTab"/>/<see cref="CloseTab"/> record. Mirrors <see cref="ApplySolutionGate"/>'s
        /// own persistence-free Insert/Remove — same principle, just keyed to one specific tab instead
        /// of the whole <see cref="SolutionScopedTabs"/> set.
        /// </summary>
        public void SetTabVisibleWithoutPersisting(string key, bool visible)
        {
            if (!host._knownTabs.TryGetValue(key, out TabPage tab) || host._mainTabControl == null) return;
            bool present = host._mainTabControl.TabPages.Contains(tab);
            if (visible && !present)
            {
                EditorCoordinator.EnsureRealized(host._mainTabControl); // see ApplySolutionGate
                host._mainTabControl.TabPages.Insert(ComputeInsertIndex(key), tab);
            }
            else if (!visible && present)
            {
                host._mainTabControl.TabPages.Remove(tab);
            }
            SyncPanelToggleState();
            host._mainTabControl.Invalidate();
        }

        // Flip a panel: hide it if showing, show it if hidden.
        private void TogglePanel(string key)
        {
            if (IsTabVisible(key))
            {
                if (host._knownTabs.TryGetValue(key, out TabPage tab)) CloseTab(tab);
            }
            else ShowTab(key);
        }

        // Public entry point (e.g. keybindings) that routes to the private toggle.
        public void TogglePanelByKey(string key)
        {
            TogglePanel(key);
        }

        /// <summary>
        /// Restore-to-original-position logic: find the first currently-visible tab whose
        /// default order is greater than the target's, and insert there. Falls back to end.
        /// </summary>
        private int ComputeInsertIndex(string key)
        {
            if (!host._tabDefaultOrder.TryGetValue(key, out int targetOrder)) return host._mainTabControl.TabPages.Count;
            for (int i = 0; i < host._mainTabControl.TabPages.Count; i++)
            {
                TabPage current = host._mainTabControl.TabPages[i];
                if (!host._tabKeysByPage.TryGetValue(current, out string k)) continue;
                if (!host._tabDefaultOrder.TryGetValue(k, out int order)) continue;
                if (order > targetOrder) return i;
            }
            return host._mainTabControl.TabPages.Count;
        }

        // Build a "View > Panels" checkbox item wired to toggle the panel. CheckOnClick is
        // off because the Checked state is driven by SyncPanelToggleState (real tab
        // visibility), not by the click itself. Registered with the keybinding system.
        public void AddPanelMenuItem(ToolStripMenuItem parent, string key, string text, string bindingName)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.CheckOnClick = false;
            mi.Checked = true;
            mi.Click += (s, e) => TogglePanel(key);
            host._keybindings.Register(bindingName, mi);
            parent.DropDownItems.Add(mi);
            host._panelMenuItemsByKey[key] = mi;
        }

        // Toolbar counterpart to AddPanelMenuItem: an icon button (or text-only fallback
        // when no image) that toggles the same panel. Checked reflects visibility, so
        // CheckOnClick is off here too.
        public void AddPanelToolbarButton(string key, string text, string tooltip, Image image = null)
        {
            ToolStripButton btn = new ToolStripButton(text);
            if (image != null)
            {
                btn.Image = image;
                btn.DisplayStyle = ToolStripItemDisplayStyle.Image;
            }
            else
            {
                btn.DisplayStyle = ToolStripItemDisplayStyle.Text;
            }
            btn.CheckOnClick = false;
            btn.Checked = true;
            // Keep the panel name visible on hover even for icon-only buttons.
            btn.ToolTipText = string.IsNullOrEmpty(tooltip) ? text : tooltip;
            btn.Click += (s, e) => TogglePanel(key);
            host._toolStrip.Items.Add(btn);
            host._panelToolbarButtonsByKey[key] = btn;
        }

        // Single source of truth for the checkmarks: push actual tab visibility onto every
        // registered menu item and toolbar button. Called after any show/hide.
        public void SyncPanelToggleState()
        {
            foreach (var kv in host._panelMenuItemsByKey)
                kv.Value.Checked = IsTabVisible(kv.Key);
            foreach (var kv in host._panelToolbarButtonsByKey)
                kv.Value.Checked = IsTabVisible(kv.Key);
        }

        // Write one tab's visible/hidden flag into user settings, lazily creating the map.
        // Save is best-effort — a settings write failure must not break the UI toggle.
        private void PersistTabVisibility(string key, bool visible)
        {
            if (host._settingsManager == null) return;
            // A result tab's visibility says "this solution produced results", not "the user prefers
            // this tab" — and VisibleTopLevelTabs is global. Recording it here is what carried one
            // solution's tabs into the next; see ResultTabs.
            if (pads.TryGetValue(key, out WorkbenchPadDescriptor descriptor) && descriptor.ResultScoped) return;
            if (host._settingsManager.Settings.VisibleTopLevelTabs == null)
                host._settingsManager.Settings.VisibleTopLevelTabs = new Dictionary<string, bool>(StringComparer.Ordinal);
            host._settingsManager.Settings.VisibleTopLevelTabs[key] = visible;
            try { host._settingsManager.Save(); } catch { }
        }

        // On startup, replay saved visibility: show tabs marked visible, hide ones marked
        // hidden, leaving matching ones alone.
        public void ApplyTabVisibilityFromSettings()
        {
            if (host._settingsManager == null || host._settingsManager.Settings.VisibleTopLevelTabs == null) return;
            // Snapshot before iterating: ShowTab/CloseTab call PersistTabVisibility which
            // writes back into VisibleTopLevelTabs. Dictionary's version counter bumps on
            // every indexer set (even updates), so iterating the live dict would throw
            // "Collection was modified" on the very first transition.
            var snapshot = new List<KeyValuePair<string, bool>>(host._settingsManager.Settings.VisibleTopLevelTabs);
            bool pruned = false;
            foreach (var kv in snapshot)
            {
                // Result tabs are no longer a preference at all. Older settings files still carry the
                // entries an engine run stamped in — drop them so a restart can't resurrect a previous
                // solution's result tabs, and so the map stops growing stale keys.
                if (pads.TryGetValue(kv.Key, out WorkbenchPadDescriptor descriptor) && descriptor.ResultScoped)
                {
                    host._settingsManager.Settings.VisibleTopLevelTabs.Remove(kv.Key);
                    pruned = true;
                    continue;
                }
                if (!host._knownTabs.TryGetValue(kv.Key, out TabPage tab)) continue;
                bool currentlyVisible = host._mainTabControl.TabPages.Contains(tab);
                if (kv.Value && !currentlyVisible) ShowTab(kv.Key);
                else if (!kv.Value && currentlyVisible) CloseTab(tab);
            }
            if (pruned) { try { host._settingsManager.Save(); } catch { } }
            SyncPanelToggleState();
        }
    }
}
