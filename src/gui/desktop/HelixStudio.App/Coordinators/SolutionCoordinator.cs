namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.UI.Projection;
    using HelixPythonNamespace.UI.Rendering;
    using HelixPythonNamespace.UI.Services;
    using HelixPythonNamespace.Workspace.Services;
    using HelixPythonNamespace.Workspace.Structure;

    /// <summary>
    /// Owns the solution lifecycle: new/empty workspace, open (.sln async), import a
    /// Python directory, close, save layout, reload, recent-files menu, and the form's
    /// .sln drag-and-drop. References the host form via <c>host</c>.
    /// </summary>
    public sealed class SolutionCoordinator : ICoordinator
    {
        private MainFormContext _context;
        private MainForm host;

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

        // Rebuild the File > Recent Files submenu from scratch. Prune dead paths first,
        // then either show a disabled "(No recent files)" placeholder or one accelerator-
        // numbered item per path plus a separator and "Clear Recent Files".
        internal void RefreshRecentFilesMenu()
        {
            host._recentFilesMenu.DropDownItems.Clear();

            host._recentFilesManager.PruneInvalidPaths();

            var recentFiles = host._recentFilesManager.GetRecentFiles();
            if (recentFiles.Count == 0)
            {
                var emptyItem = new ToolStripMenuItem("(No recent files)");
                emptyItem.Enabled = false;
                host._recentFilesMenu.DropDownItems.Add(emptyItem);
            }
            else
            {
                // "&{n} filename" gives Alt-n mnemonics; full path lives on Tag (used by
                // the click handler) and in the tooltip so the menu stays readable.
                for (int i = 0; i < recentFiles.Count; i++)
                {
                    string path = recentFiles[i];
                    string displayName = string.Format("&{0} {1}", i + 1, RecentFilesManager.DisplayName(path));
                    var item = new ToolStripMenuItem(displayName);
                    item.Tag = path;
                    item.ToolTipText = path;
                    item.Click += host.OnRecentFileClick;
                    host._recentFilesMenu.DropDownItems.Add(item);
                }

                host._recentFilesMenu.DropDownItems.Add(new ToolStripSeparator());
                host._recentFilesMenu.DropDownItems.Add("Clear Recent Files", null, host.OnClearRecentFiles);
            }
        }


        /// <summary>
        /// Opt-in load tracing: set HELIXSTUDIO_TRACE_LOAD=1 to print how long each phase of opening a
        /// solution takes. Added after SymPy (700K LOC) failed to reach the load-complete status
        /// within 25 seconds and there was no way to tell which phase was responsible.
        /// </summary>
        private static readonly bool TraceLoad =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HELIXSTUDIO_TRACE_LOAD"));

        private static System.Diagnostics.Stopwatch _phaseWatch;

        private static void Phase(string name)
        {
            if (!TraceLoad) return;
            if (_phaseWatch == null) { _phaseWatch = System.Diagnostics.Stopwatch.StartNew(); }
            else
            {
                Console.WriteLine("[load] {0,-34} {1,6} ms", name, _phaseWatch.ElapsedMilliseconds);
                _phaseWatch.Restart();
            }
        }

        /// <summary>
        /// Drop every engine result and restore each panel's empty state. Every one of these results
        /// belongs to the solution that produced it: left in place they are still on screen under the
        /// NEXT solution — diagnostics, dead-code hits, metrics and reference rows pointing at files
        /// that are no longer even open (and still clickable, resurrecting tabs for dead files).
        ///
        /// Shared by BOTH ways a solution goes away: File ▸ Close (<see cref="NewEmptyWorkspace"/>)
        /// and opening another solution straight over an open one, which performs its own teardown
        /// rather than routing through close. Mirrors PanelCoordinator.ResultTabs — the panels listed
        /// here are exactly the tabs that must not survive into the next solution, so keep them in step.
        /// </summary>
        internal void ResetEngineResultPanels()
        {
            if (host._analysisResultsListView != null) host._analysisResultsListView.Items.Clear();
            if (host._analysisOutputTextBox != null) host._analysisOutputTextBox.Clear();
            if (host._analysisSummaryLabel != null)
                host._analysisSummaryLabel.Text = "No analysis results yet — run Analysis ▸ Run Analysis to check your code.";
            if (host._buildErrorsListView != null) host._buildErrorsListView.Items.Clear();
            if (host._buildOutputTextBox != null) host._buildOutputTextBox.Clear();
            // Blank, not an empty-state sentence: the Output summary is a live status line, and the
            // old hint named Python-only shortcuts that a C++/CUDA workspace does not even show.
            if (host._buildSummaryLabel != null) host._buildSummaryLabel.Text = string.Empty;

            if (host._problemsPanel != null) host._problemsPanel.Clear();
            if (host._lintPanel != null) host._lintPanel.Clear();
            if (host._deadCodePanel != null) host._deadCodePanel.Clear();
            if (host._metricsPanel != null) host._metricsPanel.Clear();
            if (host._complexityPanel != null) host._complexityPanel.Clear();
            // A verdict belongs to the solution it was given; carrying it over would be a claim
            // about code the review never read.
            if (host._codeReviewPanel != null) host._codeReviewPanel.Clear();
            if (host._profilePanel != null) host._profilePanel.Clear();
            if (host._testRunnerPanel != null) host._testRunnerPanel.Clear();
            if (host._referencesPanel != null) host._referencesPanel.Clear();
            if (host._callHierarchyPanel != null) host._callHierarchyPanel.Clear();
        }

        // Tear the app down to a blank slate: drop the solution and every derived object
        // (layout store, projection builders per category, navigation state), stop watching
        // files, and clear every canvas/tree/panel. Used both by File > New and File > Close.
        public void NewEmptyWorkspace()
        {
            host.CloseAnalysisWindows();
            // §9: an active debug session holds the debuggee process (and through it
            // file handles inside the solution) — stop it before the model goes away.
            try { host._debugCoordinator?.Controller?.Stop(); } catch { /* already stopped */ }
            host._currentSolution = null;
            host._currentCMakeFolder = null;
            host._currentCMakeProject = null;
            // Dispose the linked-cmake-project secondary node too (Clear() below wipes the tree
            // anyway, but this also drops the tracking fields so a stale descriptor can't survive).
            host.DisposeLinkedCMakeProject();
            // §9: no editor tab may survive its solution — pinned or not. Closing disposes each
            // document (native editor handle, autosave + diagnostics timers, cached scope ranges)
            // and drops its strong reference to the old SolutionModel, which otherwise leaks
            // across an open→close→open cycle. Runs after _currentSolution is nulled so each
            // tab's close-time autosave flush skips the solution-wide FileSaved rebind, and
            // before StopFileWatcher below so those flushes still enter the watcher guard.
            host.CloseAllEditorTabs(includePinned: true);
            // Closing the solution takes the solution-scoped chrome away again, clears the stale
            // breadcrumb (the status bar otherwise read "No solution loaded" next to the name of the
            // solution just closed), and returns the user to the entry page rather than leaving them
            // on an empty designer canvas.
            host.UpdateMenuWorkflowVisibility();
            host._layoutStore = null;
            host._projectionBuilder = null;
            host._navigationState = null;
            host.StopFileWatcher();
            host._canvas.Clear();
            if (host._designerCategoryCombo != null) host._designerCategoryCombo.SelectedIndex = 0;
            host._projectTreePanel.Clear();
            host._analysisPanel.Clear();

            // Must run AFTER _navigationState is cleared above: UpdateBreadcrumb reads it, so calling
            // it any earlier redisplays the solution that was just closed. The status bar otherwise
            // read "No solution loaded" next to the closed solution's name.
            host.UpdateBreadcrumb();
            host.SelectEntryTabAfterSolutionClose();

            // Welcome is a standalone top-level tab, but closing a solution always makes it reappear
            // (SelectEntryTabAfterSolutionClose re-adds it if the user had previously closed it) — the
            // start page is exactly what you want to see with no solution loaded.

            // Reset docking state
            // Analysis/Build results dock back into the main window (undo any float-out).
            host._analysisResultsUndocked = false;
            host._buildResultsUndocked = false;

            // Reverse promotion if active
            // "Promoted mode" expands a tab to fill the window; collapse it back.
            if (host._isPromotedMode)
            {
                host._isPromotedMode = false;
                host.UpdateTabPromotion();
            }

            ResetEngineResultPanels();
            // §9: "closing a solution shall immediately close every graphical
            // designer" — every singleton designer this product ships, plus every
            // OOP-canvas window. The GUI designer add-in closes its forms on the
            // SolutionChanged raised just below.
            CMakeDesignerForm.CloseIfOpen();
            OopClassCanvasForm.CloseAllOpen();
            host.RaiseAddInSolutionChanged();

            // Remove Manage Global Namespace tab if present
            // This tab is created on demand, so remove and null out its whole trio.
            if (host._manageGlobalNsTab != null && host._mainTabControl.TabPages.Contains(host._manageGlobalNsTab))
            {
                host._mainTabControl.TabPages.Remove(host._manageGlobalNsTab);
            }
            host._manageGlobalNsTab = null;
            host._manageGlobalNsCanvas = null;
            host._manageGlobalNsBuilder = null;

            host.UpdateTitle();
            host._statusLabel.Text = "Ready - No solution loaded";
        }

        // File > Close: persist everything worth keeping (checked projects, open editor
        // files, docking + canvas layout) before wiping state via NewEmptyWorkspace
        // (which also closes every editor tab belonging to the solution — §9).
        // "1 compliance issue (10 files)" / "2 compliance issues (11 files)" / "1 compliance issue".
        // Pluralises honestly and only mentions the file count when it differs from the issue count,
        // so a one-file, one-rule violation reads simply as "1 compliance issue".
        internal static string ComplianceIssuePhrase(int issues, int files)
        {
            string head = issues + (issues == 1 ? " compliance issue" : " compliance issues");
            if (files > issues)
                head += " (" + files + (files == 1 ? " file)" : " files)");
            return head;
        }

        internal void OnFileClose(object sender, EventArgs e)
        {
            // File ▸ Close is the one command that means "put away whatever is open", and in
            // single-file mode that is the loose file. Leaving the mode standing here would leave
            // the status bar, the title and the Run target all pointing at a file the user just
            // closed out of.
            host.CloseSingleFile();

            if (host._currentSolution == null)
            {
                return;
            }

            // R4: Save docking state before closing
            host.SaveDockingState();

            // Persist per-solution UI state keyed by the solution's file path.
            if (host._settingsManager != null)
            {
                var checkedProjects = host._projectTreePanel.GetCheckedProjects();
                host._settingsManager.Settings.SetVisibleProjects(host._currentSolution.FilePath, checkedProjects);
                host.SaveOpenEditorFilesForSolution(host._currentSolution.FilePath);
                host._settingsManager.Settings.SetExpandedTreePaths(
                    host._currentSolution.FilePath, host._projectTreePanel.GetExpandedPaths());
                host._settingsManager.Save();
            }

            // Flush the current node positions from the canvas back into the layout store.
            if (host._layoutStore != null)
            {
                try
                {
                    host.SyncLayoutFromCanvas();
                    host._layoutStore.Save();
                }
                catch
                {
                    // Best effort save
                }
            }

            NewEmptyWorkspace();
        }

        // File > New retired (iter 246): creation now goes through the single template-driven wizard
        // (MainForm.OnNewProjectWizard), which scaffolds and then reopens via OpenSolutionAsync — the
        // same canvas/tree/recents wiring that used to live here. See NewProjectWizardForm.

        // File > Open also accepts a root CMakeLists.txt. Selecting it opens the containing
        // CUDA/C++ folder directly; users do not need to manufacture a Visual Studio solution.
        internal void OnFileOpen(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Projects and Solutions (CMakeLists.txt;*.toml;*.sln;*.slnx)|CMakeLists.txt;*.toml;*.sln;*.slnx|CMake CUDA/C++ Project (CMakeLists.txt)|CMakeLists.txt|Python Project (pyproject.toml)|pyproject.toml|Visual Studio Solutions (*.sln;*.slnx)|*.sln;*.slnx|All Files (*.*)|*.*";
                dlg.Title = "Open Project or Solution";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    OpenSolutionAsync(dlg.FileName);
                }
            }
        }

        // File > Import Directory: no seed path, so the wizard starts from a blank picker.
        internal void OnFileImportDirectory(object sender, EventArgs e)
        {
            host.RunImportDirectoryWizard(initialPath: null);
        }

        // Drag-over the form: light up the copy cursor only if at least one dragged path is
        // a project file or an importable directory; otherwise leave the drop rejected.
        public void OnFormDragEnter(object sender, DragEventArgs e)
        {
            if (e == null || e.Data == null) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;
            foreach (string p in paths)
            {
                if (MainForm.IsProjectPath(p) || MainForm.IsImportableDirectory(p))
                {
                    e.Effect = DragDropEffects.Copy;
                    return;
                }
            }
        }

        // Drop onto the form: act on the first usable path — open a project directly, or
        // launch the import wizard for a plain directory. First match wins, then return.
        public void OnFormDragDrop(object sender, DragEventArgs e)
        {
            if (e == null || e.Data == null) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;
            foreach (string p in paths)
            {
                if (MainForm.IsProjectPath(p))
                {
                    OpenSolutionAsync(p);
                    return;
                }
                // A directory some language claims as a workspace (today: a CMakeLists.txt-
                // bearing folder → C++ provider) opens directly instead of importing.
                if (Directory.Exists(p) &&
                    HelixPythonNamespace.Workspace.LanguageServices.LanguageWorkspaceRegistry.TryDetect(p) != null)
                {
                    OpenSolutionAsync(p);
                    return;
                }
                if (MainForm.IsImportableDirectory(p))
                {
                    host.RunImportDirectoryWizard(p);
                    return;
                }
            }
        }

        // Central solution-load path (menu, drag-drop, recent files all funnel here).
        // async void because it's a top-level UI event handler; parse happens off-thread
        // via LoadAsync, then all UI mutation resumes on the captured UI context.
        /// <summary>
        /// Report a solution-load failure. Same guard SolutionAuditPresenter already applies: a
        /// non-interactive host (test harness / CI / headless) must never raise a modal, because
        /// there is nobody to click it — the dialog blocks the STA thread forever and, because
        /// OpenSolutionAsync is `async void`, it can surface long after the test that triggered it
        /// finished, stranding an "Error" box on the developer's desktop. Observed exactly that:
        /// a disposed-form continuation popped "Failed to open solution: Cannot access a disposed
        /// object" over a live desktop session.
        /// </summary>
        private void ReportLoadError(string message, string caption)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1",
                              StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine("[noninteractive] " + caption + ": " + message);
                return;
            }
            MessageBox.Show(message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>
        /// True when there is nobody to click a modal. Same guard <see cref="ReportLoadError"/> and
        /// SolutionAuditPresenter already apply, and it matters more here than anywhere: this dialog
        /// gates a load, so in a test host it would block the STA thread forever, and the action
        /// behind it MOVES the user's directories.
        /// </summary>
        private static bool IsNonInteractive()
        {
            return string.Equals(Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"), "1",
                                 StringComparison.Ordinal);
        }

        /// <summary>
        /// The load gate for solution policy rule 4: verify the tree can be drawn as the disk
        /// actually is, offer the repair, and re-load from the repaired disk.
        ///
        /// Returns the load result the rest of the open should proceed with — the original one when
        /// nothing was repaired, or a fresh load when it was. Never returns a failed result in place
        /// of a good one: if the repair or the re-load goes wrong the user keeps the solution they
        /// asked for, marked unverified.
        ///
        /// <b>The dialog is raised only when there is disk work to do.</b> A plan whose operations
        /// are all <c>RenderNode</c> cannot be executed at all — showing a path the tree omits is the
        /// RENDERER's half of the contract and that half is not built yet — so prompting "the IDE
        /// will now restructure your directory structure" and then restructuring nothing would be a
        /// lie, and it would fire on nearly every real solution (every symbol-free
        /// <c>__init__.py</c> is an orphan today). Those solutions are still marked unverified and
        /// still say so in the status bar; they just are not offered an action the IDE will not take.
        /// </summary>
        /// <summary>
        /// Records BOTH halves of the blocked state: what is wrong, and what the user can do about
        /// it. Always together — a reason with no remedy is what produced the dead end this pair
        /// replaces, so they are never set independently.
        /// </summary>
        private void MarkUnverified(string reason, string remedy)
        {
            host.StructureUnverifiedReason = reason;
            host.StructureUnverifiedRemedy = remedy;
        }

        private async Task<SolutionLoadResult> EnsureStructureParityAsync(string slnPath, SolutionLoadResult result)
        {
            host.StructureUnverifiedReason = null;
            host.StructureUnverifiedRemedy = null;

            StructureParityReport report = result.StructureParity;
            // A null report means the scan did not run or threw — "not verified", never "clean".
            // Nothing to offer the user in that case, so the solution opens as before.
            if (report == null) return result;

            var engine = new DirectoryStructureCorrectionEngine();

            // Stages 1-5 of the gate need no window, so they live in StructureParityGate and the
            // headless `helixstudio` launcher evaluates the very same code. Only stage 6 is this
            // host's: it has someone to confirm a restructure, and can then execute it.
            StructureParityVerdict verdict = StructureParityGate.Evaluate(report, engine);
            if (verdict.IsVerified) return result;

            if (!verdict.RepairAvailable || IsNonInteractive())
            {
                MarkUnverified(verdict.Reason, verdict.Remedy);
                return result;
            }

            StructureRepairPlan plan = verdict.Plan;
            string unverified = verdict.Summary;

            using (var dialog = new StructureRepairDialog(report, plan))
            {
                if (dialog.ShowDialog(host) != DialogResult.OK)
                {
                    // Cancel is not "proceed anyway". The solution opens so the user can look at it
                    // and decide, but it stays flagged and Run/Debug/Compile stay blocked — the
                    // point of the rule is that the IDE does not vouch for a structure it has not
                    // verified. This is the ONE state in which "reopen and accept the prompt" is
                    // true, because this is the one state that raised a prompt.
                    MarkUnverified(unverified + " (restructure declined)",
                        "Reopen the solution and accept the restructure when prompted.");
                    return result;
                }
            }

            host.Cursor = Cursors.WaitCursor;
            StructureRepairResult repair;
            try
            {
                // Off the UI thread: this moves directories and rewrites manifests. The re-verify
                // delegate re-loads from the repaired disk through the same production path, so
                // stage 6 judges what the IDE will actually see next.
                repair = await Task.Run(() => engine.Repair(plan, delegate
                {
                    SolutionLoadResult reloaded =
                        host._solutionService.LoadAsync(slnPath).GetAwaiter().GetResult();
                    return reloaded.Success ? reloaded.StructureParity : null;
                }));
            }
            catch (Exception ex)
            {
                repair = null;
                System.Diagnostics.Debug.WriteLine("[structure] repair threw: " + ex.Message);
            }
            finally { host.Cursor = Cursors.Default; }

            if (repair == null || !repair.Succeeded)
            {
                string detail = repair == null ? "The repair could not be started."
                    : (repair.FailureReason ?? "The repair did not complete.")
                      + (repair.RolledBack ? " Your folders were put back the way they were."
                                           : " Some changes may remain; see .helixstudio\\structure-repair.journal.");
                ReportLoadError("The directory structure could not be corrected." + Environment.NewLine
                    + Environment.NewLine + detail, "Directory Structure");
                MarkUnverified(unverified + " (repair failed)",
                    repair != null && repair.RolledBack
                        ? "Your folders were put back the way they were. " + detail
                        : "See .helixstudio\\structure-repair.journal for what was applied before it stopped.");
                return result;
            }

            // Re-load: the model in `result` describes directories that have moved.
            SolutionLoadResult after = await host._solutionService.LoadAsync(slnPath);
            return after.Success ? after : result;
        }

        public async void OpenSolutionAsync(string slnPath, bool openStartupEntry = false,
            string fileToOpen = null)
        {
            try
            {
                // A solution and a loose file are never open together. Ended here rather than at
                // each of the half-dozen callers (Open, Recent, Welcome, drag-drop, the shell
                // command line, the New-Project wizard's reopen), because every one of them ends
                // with a solution on screen and any that forgot would leave single-file mode
                // running underneath it — a Run target and a rule set with no window to name them.
                host.CloseSingleFile();

                // Connector layer: ask the registry which language claims this path instead of
                // sniffing manifest names inline. C++ adopts a workspace through the native
                // CMake path; a manifest-anchored workspace loads through its
                // registered provider and then adopt through the SAME language-neutral pipeline
                // below that Python uses; everything else (including unrecognized/nonexistent
                // paths) falls through to the Python load, preserving its exact error messages.
                var detected = HelixPythonNamespace.Workspace.LanguageServices.LanguageWorkspaceRegistry.TryDetect(slnPath);
                if (detected != null && detected.Language == HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp)
                {
                    string cmakeFolder = Directory.Exists(slnPath) ? slnPath : Path.GetDirectoryName(slnPath);
                    host.Cursor = Cursors.WaitCursor;
                    object imported = host.LoadCMakeFolderAsNativeProject(cmakeFolder);
                    host.Cursor = Cursors.Default;
                    if (imported != null)
                    {
                        host.UpdateMenuWorkflowVisibility();
                        host.SelectWorkspaceTabAfterSolutionOpen();
                    }
                    return;
                }
                bool providerLoad = detected != null &&
                    detected.Language != HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Python;

                host.Cursor = Cursors.WaitCursor;
                string loadingName = SolutionDisplayNameFor(slnPath);
                host._statusLabel.Text = "Loading " + loadingName + "…";
                // Say it in the Solution Explorer too. It is the panel the user is watching, and
                // while a load ran it used to read "No solution open. Open one from the Welcome
                // page…" — inviting them to start the very load they are waiting on.
                if (host._projectTreePanel != null) host._projectTreePanel.BeginLoading(loadingName);

                HelixPythonNamespace.Workspace.Services.SolutionLoadResult result;
                if (providerLoad)
                {
                    var opened = await detected.OpenAsync(slnPath);
                    result = opened.Success
                        ? new HelixPythonNamespace.Workspace.Services.SolutionLoadResult(opened.Solution)
                        : new HelixPythonNamespace.Workspace.Services.SolutionLoadResult(
                            opened.ErrorMessage ?? "The workspace could not be loaded.");
                }
                else
                {
                    result = await host._solutionService.LoadAsync(slnPath);
                }

                host.Cursor = Cursors.Default;

                // Structure parity gate (PY8004, rules 2/3/5). Runs on the way in, before the tree
                // is drawn, on the ONE path every route into a solution funnels through — menu,
                // Recent Files, drag-and-drop, command-line open. Python/TOML solutions only: the
                // rule is scoped to TOML solutions, and a provider-loaded (CMake) workspace
                // has no canonical member layout to compare against.
                if (result.Success && !providerLoad)
                    result = await EnsureStructureParityAsync(slnPath, result);

                if (result.Success)
                {
                    // Opening over an already-open solution: persist the outgoing solution's
                    // open-file list, then close its tabs (pinned included — §9: no editor tab
                    // survives its solution). Same teardown File ▸ Close performs; without it the
                    // old documents linger under the new solution holding the old model alive.
                    if (host._currentSolution != null)
                    {
                        try
                        {
                            if (host._settingsManager != null)
                            {
                                host.SaveOpenEditorFilesForSolution(host._currentSolution.FilePath);
                                host._settingsManager.Save();
                            }
                        }
                        catch { /* best-effort persistence */ }
                        host._currentSolution = null;   // close-time flushes skip the solution-wide rebind
                        host.CloseAllEditorTabs(includePinned: true);
                        // …and the outgoing solution's engine results, which this path never dropped:
                        // its Lint/Metrics/Complexity/Output rows stayed on screen, and their tabs on
                        // the strip, under the newly opened solution. File ▸ Close already does both
                        // (NewEmptyWorkspace + the solution gate); opening straight over an open
                        // solution skips that teardown, so do the same two steps here.
                        ResetEngineResultPanels();
                        host.HideResultTabs();
                    }

                    // Adopt the loaded model, then repair known data issues (duplicate base
                    // types) and reopen the editor files that were open last session.
                    Phase("load: service");
                    host._currentSolution = result.Solution;
                    // Opening a solution unlocks the solution-scoped menus and toolbar groups, and
                    // moves the user off the Welcome page onto the workspace.
                    host.UpdateMenuWorkflowVisibility();
                    host.SelectWorkspaceTabAfterSolutionOpen();
                    Phase("workflow chrome");
                    host.RepairDuplicateBaseTypes(host._currentSolution);
                    host.RestoreLastOpenEditorFiles(host._currentSolution.FilePath);

                    // Python projects commonly group multiple classes per module, so the old
                    // "one-type-per-file" separation prompt is obsolete — no reload needed here.

                    // Load persisted node positions for this solution from its layout file.
                    Phase("repair + restore editors");
                    host._layoutStore = new JsonLayoutMetadataStore(host._currentSolution.FilePath);
                    host._layoutStore.Load();

                    Phase("layout store");
                    host._projectTreePanel.LoadSolution(host._currentSolution);
                    // Session restore: put the tree back the way the user left it last time.
                    if (host._settingsManager != null)
                        host._projectTreePanel.ApplyExpandedPaths(
                            host._settingsManager.Settings.GetExpandedTreePaths(host._currentSolution.FilePath));
                    // [tool.helixstudio] linked-cmake-project: attach a secondary Solution Explorer
                    // node for a linked native CMake project (if the root TOML declares one) —
                    // additive, does not touch IsNativeWorkspaceActive / replace this Python tree.
                    // Python-format solutions only: the manifest languages' anchors (pom.xml,
                    // Cargo.toml, …) are not helixstudio TOMLs.
                    if (!providerLoad) host.AttachLinkedCMakeProjectIfPresent(host._currentSolution);
                    // Restore the persisted startup-package selection (bold node + play icon) so the
                    // choice survives closing/reopening the solution — VS2017 startup-project parity.
                    host.RefreshStartupProjectStylingFromSettings();

                    // Restore which projects were checked (shown on canvas) last time.
                    var savedVisibility = host._settingsManager.Settings.GetVisibleProjects(slnPath);
                    // Sanitize saved names: strip stale "[C#]"/"[C++]" suffixes for backward compat
                    var sanitizedVisibility = MainForm.SanitizeProjectNames(savedVisibility, host._currentSolution);
                    if (sanitizedVisibility.Count > 0)
                    {
                        host._projectTreePanel.SetCheckedProjects(sanitizedVisibility);
                    }
                    else
                    {
                        // First time opening or empty saved state: check all projects by default
                        var allProjectNames = new List<string>();
                        foreach (var p in host._currentSolution.Projects)
                        {
                            allProjectNames.Add(p.Name);
                        }
                        host._projectTreePanel.SetCheckedProjects(allProjectNames);
                    }

                    Phase("tree load");
                    var checkedProjects = host._projectTreePanel.GetCheckedProjects();

                    // Build the main-canvas projection, limited to the checked projects.
                    var autoLayout = new GridAutoLayoutStrategy();
                    host._projectionBuilder = new SolutionProjectionBuilder(host._canvas.SceneGraph, host._layoutStore, autoLayout);
                    host._projectionBuilder.SetVisibleProjects(checkedProjects);

                    // Start at solution-level hierarchical view
                    Phase("projection builder");
                    host._navigationState = new NavigationViewState(host._currentSolution.Name);
                    host._projectionBuilder.BuildSolutionLevel(host._currentSolution);

                    // Reset viewport, then fit on the first paint. Node bounds aren't measured until
                    // the canvas actually renders, so an immediate ZoomToFit would run against
                    // un-measured bounds and leave a sparse diagram top-left; RequestFitOnFirstPaint
                    // fits once the content has really been drawn (centred).
                    Phase("build solution level");
                    host._canvas.Viewport.Reset();
                    host._canvas.RequestFitOnFirstPaint(80f);

                    host._canvas.Invalidate();

                    host.UpdateBreadcrumb();

                    // NO Application.DoEvents() here. It was added to "let the UI catch up", but it
                    // re-enters the message loop in the middle of the load: on a large real-world
                    // solution (SymPy, 2083 types) it pumped paints for 47 SECONDS before returning,
                    // so the sequence below — including the "Loaded: N project(s)" status — was never
                    // reached. This method is async; the UI repaints on its own once it returns.
                    // Measured with HELIXSTUDIO_TRACE_LOAD=1.

                    // Reset the "Show:" filter back to All for the newly opened solution.
                    if (host._designerCategoryCombo != null) host._designerCategoryCombo.SelectedIndex = 0;

                    Phase("canvas + breadcrumb");
                    host.RebuildCategoryTabs(checkedProjects);

                    Phase("category tabs");
                    host._recentFilesManager.AddRecent(slnPath);
                    RefreshRecentFilesMenu();

                    host.StartFileWatcher();

                    host.UpdateTitle();
                    host._analysisPanel.Clear();
                    host.RaiseAddInSolutionChanged();

                    // R4: Restore docking state for host solution
                    Phase("watcher + title");
                    host.RestoreDockingState();

                    // Freshly created via the wizard: open the startup package's entry file (main.py /
                    // __main__.py / *_main.py) and focus it, so a new project lands on editable code —
                    // VS opens Program.cs. Done last so this tab wins over the restored active tab.
                    if (openStartupEntry)
                    {
                        string entry = host.ResolveStartupEntryFile(host._currentSolution);
                        if (!string.IsNullOrEmpty(entry) && System.IO.File.Exists(entry))
                            host.OpenInEditor(entry);
                    }
                    if (!string.IsNullOrEmpty(fileToOpen) && System.IO.File.Exists(fileToOpen))
                        host.OpenInEditor(fileToOpen);

                    // Report project/type counts in the status bar.
                    Phase("docking restore");
                    var typeCount = host._currentSolution.GetAllTypes().Count();
                    Phase("count types");
                    host._statusLabel.Text = string.Format(
                        "Loaded: {0} project(s), {1} type(s) - Check projects in tree to show on canvas",
                        host._currentSolution.Projects.Count,
                        typeCount);

                    // Audit each PACKAGE against the per-package rules of the Python solution model
                    // (docs/python-solution-model.md: PSM-03/04/05/06/12/13). Independent per package
                    // (PSM-07), so a solution with several valid executable packages is compliant —
                    // unlike the old per-solution "single main.py / single def main()" check, which
                    // mis-flagged multi-executable solutions. Non-blocking: findings land in the
                    // Problems panel and the status bar, and the solution still opens so the user can
                    // fix them. The scan reads files, so it runs off the UI thread.
                    // Python-solution rule set — auditing a non-Python tree against
                    // PSM main.py/package rules would flag every project falsely.
                    SolutionModel auditedSolution = providerLoad ? null : host._currentSolution;
                    var pkgFindings = auditedSolution == null ? null : await Task.Run(() =>
                        HelixPythonNamespace.Workspace.Python.PackageComplianceValidator.Validate(auditedSolution));
                    if (pkgFindings != null && pkgFindings.Count > 0)
                    {
                        var rows = HelixPythonNamespace.App.Services.PackageCompliancePresenter.ToDiagnostics(pkgFindings);
                        if (host._problemsPanel != null) host._problemsPanel.ShowDiagnostics(rows);

                        string diagTab = host._problemsTab != null ? host._problemsTab.Text : "Compile";
                        int issueCount = HelixPythonNamespace.App.Services.PackageCompliancePresenter.CountAffectedPackages(pkgFindings);
                        string issuePhrase = ComplianceIssuePhrase(issueCount, rows.Count);
                        host._statusLabel.Text = string.Format(
                            "Loaded: {0} project(s), {1} type(s) - {2} in the {3} tab",
                            host._currentSolution.Projects.Count, typeCount, issuePhrase, diagTab);
                    }

                    // Structure parity, last so it survives whichever status text ran above. A
                    // solution the IDE could not verify must SAY so — silently drawing a tree that
                    // disagrees with the disk is the exact failure PY8004 exists to end.
                    if (!string.IsNullOrEmpty(host.StructureUnverifiedReason))
                        host._statusLabel.Text += "  -  Structure not verified: "
                            + host.StructureUnverifiedReason;
                }
                // Loader reported a graceful failure (bad file, parse error) — show it.
                else
                {
                    ReportLoadError(result.ErrorMessage, "Error Loading Solution");
                    host._statusLabel.Text = "Failed to load solution";
                }
            }
            // Catch-all for unexpected exceptions; reset cursor so the UI isn't left busy.
            catch (Exception ex)
            {
                host.Cursor = Cursors.Default;
                ReportLoadError(string.Format("Failed to open solution: {0}", ex.Message), "Error");
                host._statusLabel.Text = "Error opening solution";
            }
            // A failed or crashed load must not leave the Solution Explorer claiming forever that
            // something is on its way. The success path already cleared it via LoadSolution; this
            // catches every other exit.
            finally
            {
                if (host._projectTreePanel != null && host._projectTreePanel.IsShowingLoading)
                    host._projectTreePanel.EndLoading();
            }
        }

        /// <summary>
        /// Human-readable name for a solution being loaded, taken from its folder — every project
        /// file is called "pyproject.toml", so the folder is the part the user recognises.
        /// </summary>
        internal static string SolutionDisplayNameFor(string solutionPath)
        {
            if (string.IsNullOrEmpty(solutionPath)) return "solution";
            try
            {
                string dir = System.IO.Path.GetDirectoryName(solutionPath);
                string name = string.IsNullOrEmpty(dir) ? null : System.IO.Path.GetFileName(dir);
                return string.IsNullOrEmpty(name) ? "solution" : name;
            }
            catch { return "solution"; }
        }

        // File > Save Layout menu handler — thin wrapper over SaveLayout.
        internal void OnFileSaveLayout(object sender, EventArgs e)
        {
            SaveLayout();
        }

        // Flush canvas node positions to the layout store and clear the dirty flag.
        public void SaveLayout()
        {
            if (host._currentSolution == null || host._layoutStore == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return;
            }

            try
            {
                host.SyncLayoutFromCanvas();
                host._layoutStore.Save();
                host.ClearSolutionDirty();
                host._statusLabel.Text = "Layout saved";
            }
            catch (Exception ex)
            {
                ReportLoadError(string.Format("Failed to save layout: {0}", ex.Message), "Error");
            }
        }

        // Re-parse the solution from disk (e.g. after external edits), preserving the user's
        // current checked-project selection and layout store, and propagating any contract
        // changes to downstream types afterward.
        internal async void ReloadSolutionAsync(string fileToOpen = null)
        {
            if (host._currentSolution == null)
            {
                return;
            }

            // Consume this immediately so an unrelated later watcher reload cannot inherit it.
            var renamedExpansion = host._expandedTreePathsAfterRename;
            host._expandedTreePathsAfterRename = null;

            try
            {
                host.Cursor = Cursors.WaitCursor;
                host._statusLabel.Text = "Reloading solution...";

                // Snapshot old contract types before reload for change detection
                var oldContractTypes = host.SnapshotContractTypes(host._currentSolution);

                bool reloadSucceeded;
                SolutionModel reloadedSolution = null;
                string reloadError = null;

                // A native CMake solution has no pyproject.toml/.sln for SolutionService to
                // re-parse — re-import via CMakeProjectImporter + CMakeSolutionLoader instead,
                // the same pipeline LoadCMakeFolderAsNativeProject used to build the model the
                // first time. Without this branch every reload of a C++ solution (e.g. right
                // after a Rename Symbol) hit SolutionService.ReloadAsync's "Expected a
                // pyproject.toml, .sln, or .slnx file" rejection unconditionally — an unowned
                // "Error Reloading" message box, and the tree never refreshed to show the
                // rename (found during GUI verification of the C++ context-menu feature).
                if (!string.IsNullOrEmpty(host._currentCMakeFolder))
                {
                    // Language-services layer: same provider pipeline the initial open used
                    // (CMakeProjectImporter → CMakeSolutionLoader), so open and reload can never
                    // drift apart again.
                    var reopened = await HelixPythonNamespace.Workspace.LanguageServices.LanguageWorkspaceRegistry
                        .For(HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Cpp)
                        .OpenAsync(host._currentCMakeFolder);
                    if (reopened != null && reopened.Success)
                    {
                        host._currentCMakeProject = (HelixPythonNamespace.Workspace.Cuda.CMakeProjectDescriptor)reopened.NativeProject;
                        reloadedSolution = reopened.Solution;
                        reloadSucceeded = reloadedSolution != null;
                    }
                    else
                    {
                        reloadSucceeded = false;
                        reloadError = reopened != null ? reopened.ErrorMessage : "CMake import failed.";
                    }
                }
                else
                {
                    // Connector layer: a manifest-language solution (CMake) reloads
                    // through the same provider pipeline its initial open used — the Python
                    // ReloadAsync would reject its anchor file ("Expected a pyproject.toml,
                    // .sln, or .slnx"), the exact open-vs-reload drift the C++ branch above
                    // fixed for CMake.
                    var reloadProvider = HelixPythonNamespace.Workspace.LanguageServices.LanguageWorkspaceRegistry
                        .TryDetect(host._currentSolution.FilePath);
                    if (reloadProvider != null &&
                        reloadProvider.Language != HelixPythonNamespace.Core.Model.Enums.ProjectLanguage.Python)
                    {
                        var reopened = await reloadProvider.OpenAsync(host._currentSolution.FilePath);
                        if (reopened != null && reopened.Success)
                        {
                            reloadedSolution = reopened.Solution;
                            reloadSucceeded = reloadedSolution != null;
                        }
                        else
                        {
                            reloadSucceeded = false;
                            reloadError = reopened != null ? reopened.ErrorMessage : "Workspace reload failed.";
                        }
                    }
                    else
                    {
                        var result = await host._solutionService.ReloadAsync(host._currentSolution);
                        reloadSucceeded = result.Success;
                        reloadedSolution = result.Solution;
                        reloadError = result.ErrorMessage;
                    }
                }

                host.Cursor = Cursors.Default;

                if (reloadSucceeded)
                {
                    // Capture checked projects before swapping the model so the same set
                    // stays visible on the rebuilt canvas.
                    var checkedProjects = host._projectTreePanel.GetCheckedProjects();

                    host._currentSolution = reloadedSolution;

                    // Re-bind each base-type reference to the freshly loaded solution so
                    // inheritance edges resolve against the new object graph.
                    foreach (var type in host._currentSolution.GetAllTypes())
                    {
                        foreach (var baseType in type.BaseTypes)
                        {
                            baseType.Bind(host._currentSolution);
                        }
                    }

                    host._projectTreePanel.LoadSolutionPreservingExpansion(host._currentSolution);
                    if (renamedExpansion != null)
                        host._projectTreePanel.ApplyExpandedPaths(renamedExpansion);
                    host._projectTreePanel.SetCheckedProjects(checkedProjects);
                    // Keep the startup-package styling across an in-place reload (file-watcher refresh).
                    host.RefreshStartupProjectStylingFromSettings();

                    // Reuse the existing layout store (positions persist across reload).
                    var autoLayout = new GridAutoLayoutStrategy();
                    host._projectionBuilder = new SolutionProjectionBuilder(host._canvas.SceneGraph, host._layoutStore, autoLayout);
                    host._projectionBuilder.SetVisibleProjects(checkedProjects);
                    host._projectionBuilder.RebuildFromSolution(host._currentSolution);
                    host._canvas.Invalidate();

                    // Same reason as the open path: a DoEvents here re-enters the message loop
                    // mid-reload and, on a large solution, does not return for tens of seconds —
                    // which would strand a reload triggered by an external file change.
                    // Rebuild category tabs
                    host.RebuildCategoryTabs(checkedProjects);

                    host._statusLabel.Text = "Solution reloaded";

                    if (!string.IsNullOrEmpty(fileToOpen) && System.IO.File.Exists(fileToOpen))
                        host.OpenInEditor(fileToOpen);

                    // Detect upstream changes and propagate to downstream types
                    host.DetectAndPropagateUpstreamChanges(oldContractTypes);
                }
                else
                {
                    // Same non-interactive guard as the open path: this fires from an `async void`
                    // reload, so a modal here can appear long after whatever triggered it and block
                    // a thread with nobody to dismiss it.
                    ReportLoadError(reloadError, "Error Reloading");
                }
            }
            catch (Exception ex)
            {
                host.Cursor = Cursors.Default;
                ReportLoadError(string.Format("Failed to reload: {0}", ex.Message), "Error");
            }
        }
    }
}
