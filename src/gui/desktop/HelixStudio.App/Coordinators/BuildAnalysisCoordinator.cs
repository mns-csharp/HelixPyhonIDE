namespace HelixPythonNamespace.App.Coordinators
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;
    using HelixPythonNamespace.App;
    using HelixPythonNamespace.UI.Controls;
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.App.Dialogs;
    using HelixPythonNamespace.Core.Model;
    using HelixPythonNamespace.Core.Model.Enums;
    using HelixPythonNamespace.Core.Services;
    using HelixPythonNamespace.UI.Rendering;
    using HelixPythonNamespace.AddIns.Kit;

    /// <summary>
    /// Owns the Analysis Results and Build/Output result panels: docking/undocking
    /// them into floating windows, populating them from analysis/build runs, running
    /// the analysis engine, and reflecting violations as canvas badges. Cross-cutting
    /// tab-promotion/order helpers remain on <see cref="MainForm"/> and are called via
    /// the host. The View menu's toggle entries call the thin wrappers on MainForm.
    /// </summary>
    public sealed class BuildAnalysisCoordinator : ICoordinator
    {
        private MainFormContext _context;
        private MainForm host;

        // ICoordinator lifecycle: cache the shared context and a direct MainForm reference
        // (host) so the many host.<field> accesses below stay terse.
        public void Attach(MainFormContext context)
        {
            _context = context;
            host = context.Host;
        }

        // Drop references so the coordinator doesn't pin the form after teardown.
        public void Detach()
        {
            _context = null;
            host = null;
        }

        // View-menu / title-bar toggle: flip the Analysis Results pane between docked
        // (a tab in the main tab control) and floating (its own window).
        public void OnToggleAnalysisResultsDocking(object sender, EventArgs e)
        {
            if (host._analysisResultsUndocked)
            {
                RedockPane("AnalysisResults");
            }
            else
            {
                UndockPane("AnalysisResults");
            }
        }

        // Same toggle for the Build/Output pane.
        public void OnToggleBuildResultsDocking(object sender, EventArgs e)
        {
            if (host._buildResultsUndocked)
            {
                RedockPane("BuildResults");
            }
            else
            {
                UndockPane("BuildResults");
            }
        }

        // Pull the named pane out of the main tab control and reparent its content into a
        // new floating LunaForm window. Guards against double-undocking and records the
        // undocked flag + floating-form reference on the host so the toggles/redock can find it.
        public void UndockPane(string paneId)
        {
            TabPage tab = null;
            Form floatingForm = null;
            string title = "";

            // Resolve which host tab/flag this paneId maps to; bail if it's already undocked.
            if (paneId == "AnalysisResults")
            {
                if (host._analysisResultsUndocked) return;
                tab = host._analysisResultsTab;
                title = "Analysis Results";
                host._analysisResultsUndocked = true;
            }
            else if (paneId == "BuildResults")
            {
                if (host._buildResultsUndocked) return;
                tab = host._buildResultsTab;
                title = "Output";
                host._buildResultsUndocked = true;
            }

            if (tab == null) return;

            // Remove the tab from main tab control
            host._mainTabControl.TabPages.Remove(tab);

            // Create floating form: a LunaForm so the XP/Luna theming (title bar, borders,
            // colors) matches the rest of the app rather than the default WinForms chrome.
            // Owned by the host so it minimizes/closes with the main window; Tag carries the
            // paneId so OnFloatingPaneClosing can redock the right pane.
            floatingForm = new LunaForm();
            floatingForm.Text = title;
            floatingForm.Size = new Size(700, 500);
            floatingForm.StartPosition = FormStartPosition.CenterScreen;
            floatingForm.ShowInTaskbar = true;
            floatingForm.Owner = host;
            floatingForm.BackColor = LunaColors.MainBackground;
            floatingForm.ForeColor = LunaColors.TextPrimary;
            floatingForm.Tag = paneId;
            floatingForm.FormClosing += OnFloatingPaneClosing;

            // Move the tab's content to the floating form
            Control content = null;
            if (tab.Controls.Count > 0)
            {
                content = tab.Controls[0];
                tab.Controls.Remove(content);
                content.Dock = DockStyle.Fill;
                floatingForm.Controls.Add(content);
            }

            // Remember the floating form on the host so redock / cleanup can reach it.
            if (paneId == "AnalysisResults") host._floatingAnalysisResults = floatingForm;
            else if (paneId == "BuildResults") host._floatingBuildResults = floatingForm;

            floatingForm.Show();

            // Removing a tab can change whether the designer sub-tabs should be promoted to
            // top level; let MainForm re-evaluate that after the layout changed.
            host.UpdateTabPromotion();

            host._statusLabel.Text = string.Format("{0} undocked", title);
        }

        // Inverse of UndockPane: move the content back out of the floating window into its
        // original tab and re-insert that tab in the correct order in the main tab control.
        private void RedockPane(string paneId)
        {
            Form floatingForm = null;
            TabPage tab = null;

            // Resolve the floating form + target tab and clear the undocked state up front.
            if (paneId == "AnalysisResults")
            {
                if (!host._analysisResultsUndocked) return;
                floatingForm = host._floatingAnalysisResults;
                tab = host._analysisResultsTab;
                host._analysisResultsUndocked = false;
                host._floatingAnalysisResults = null;
            }
            else if (paneId == "BuildResults")
            {
                if (!host._buildResultsUndocked) return;
                floatingForm = host._floatingBuildResults;
                tab = host._buildResultsTab;
                host._buildResultsUndocked = false;
                host._floatingBuildResults = null;
            }

            if (floatingForm == null || tab == null) return;

            // Detach the closing handler first so closing the form below doesn't re-enter
            // the redock path, then reparent the single content control back onto the tab.
            floatingForm.FormClosing -= OnFloatingPaneClosing;

            // ContentHost, not Controls[0]: a LunaForm keeps its children one level down inside the
            // panel that clears its hand-drawn caption, so Controls[0] is the chrome. Taking that
            // instead wrapped the pane in another host on every undock/redock cycle.
            LunaForm luna = floatingForm as LunaForm;
            Control container = luna != null ? luna.ContentHost : floatingForm;
            if (container.Controls.Count > 0)
            {
                Control content = container.Controls[0];
                content.Dock = DockStyle.Fill;
                tab.Controls.Add(content);   // reparents out of the floating window
            }
            floatingForm.Close();
            floatingForm.Dispose();

            // Layout changed again — re-evaluate designer sub-tab promotion (may now demote).
            host.UpdateTabPromotion();

            // Re-insert at its canonical position rather than appending, so tab order is stable.
            host.InsertTabInOrder(tab);

            host._statusLabel.Text = string.Format("{0} docked", tab.Text);
        }

        // Clicking the floating window's [X] should redock the pane, not destroy its content.
        // Cancel the user-initiated close and route through RedockPane instead (which re-adds
        // the content to the tab). Non-user closes, e.g. app shutdown, fall through normally.
        public void OnFloatingPaneClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Form form = sender as Form;
                if (form != null)
                {
                    string paneId = form.Tag as string;
                    if (!string.IsNullOrEmpty(paneId))
                    {
                        RedockPane(paneId);
                    }
                }
            }
        }

        // Tear down all analysis/build result surfaces and reset the embedded panes to their
        // empty state. Called when the solution is closed so stale results don't linger.
        public void CloseAnalysisWindows()
        {
            if (host._analysisResultsDialog != null && !host._analysisResultsDialog.IsDisposed)
            {
                host._analysisResultsDialog.Close();
                host._analysisResultsDialog = null;
            }

            if (host._buildResultsDialog != null && !host._buildResultsDialog.IsDisposed)
            {
                host._buildResultsDialog.Close();
                host._buildResultsDialog = null;
            }

            // Close floating panes if open
            CloseFloatingPanes();

            // Clear embedded analysis/run results
            host._analysisResultsListView.Items.Clear();
            host._analysisOutputTextBox.Clear();
            host._analysisSummaryLabel.Text = "No analysis results";
            host._buildErrorsListView.Items.Clear();
            host._buildOutputTextBox.Clear();
            host._buildSummaryLabel.Text = "No run results";
        }

        // Force-close any floating panes without the redock-on-close dance: unhook the closing
        // handler, salvage the content back onto its tab (so it isn't disposed with the form),
        // then close/dispose the window and clear the undocked state. Used during shutdown/reset.
        public void CloseFloatingPanes()
        {
            if (host._floatingAnalysisResults != null)
            {
                host._floatingAnalysisResults.FormClosing -= OnFloatingPaneClosing;
                if (host._floatingAnalysisResults.Controls.Count > 0)
                {
                    Control content = host._floatingAnalysisResults.Controls[0];
                    host._floatingAnalysisResults.Controls.Remove(content);
                    content.Dock = DockStyle.Fill;
                    host._analysisResultsTab.Controls.Add(content);
                }
                host._floatingAnalysisResults.Close();
                host._floatingAnalysisResults.Dispose();
                host._floatingAnalysisResults = null;
                host._analysisResultsUndocked = false;
            }
            if (host._floatingBuildResults != null)
            {
                host._floatingBuildResults.FormClosing -= OnFloatingPaneClosing;
                if (host._floatingBuildResults.Controls.Count > 0)
                {
                    Control content = host._floatingBuildResults.Controls[0];
                    host._floatingBuildResults.Controls.Remove(content);
                    content.Dock = DockStyle.Fill;
                    host._buildResultsTab.Controls.Add(content);
                }
                host._floatingBuildResults.Close();
                host._floatingBuildResults.Dispose();
                host._floatingBuildResults = null;
                host._buildResultsUndocked = false;
            }

            // Tabs came back into the main control while promoted — re-evaluate promotion.
            if (host._isPromotedMode)
            {
                host.UpdateTabPromotion();
            }
        }

        // Append a line to the embedded Output text box. Build/run output arrives on background
        // process-reader threads, so marshal onto the UI thread via BeginInvoke when needed;
        // also guard against a disposed form (output can trickle in during shutdown).
        public void AppendBuildOutputLine(string line)
        {
            if (host.IsDisposed) return;

            if (host.InvokeRequired)
            {
                host.BeginInvoke(new Action<string>(AppendBuildOutputLine), line);
                return;
            }

            host._buildOutputTextBox.AppendText(line + Environment.NewLine);
        }

        // Fill the embedded Errors/Warnings list from parsed build results, coloring error rows
        // red and warning rows amber, and tally counts for the summary label.
        public void PopulateEmbeddedBuildResults(List<BuildResultItem> results, bool buildSucceeded)
        {
            results = results ?? new List<BuildResultItem>();
            host._diagnosticStore.Replace("Build", results);
            RenderDiagnosticStore();
            
            int errorCount = 0;
            int warningCount = 0;
            foreach (var r in results)
            {
                if (string.Equals(r.Severity, "error", StringComparison.OrdinalIgnoreCase)) errorCount++;
                else if (string.Equals(r.Severity, "warning", StringComparison.OrdinalIgnoreCase)) warningCount++;
            }

            host._buildSummaryLabel.Text = buildSucceeded
                ? string.Format("Run succeeded - {0} warning(s)", warningCount)
                : string.Format("Run failed - {0} error(s), {1} warning(s)", errorCount, warningCount);

            if (results.Count > 0) host._buildInnerTabControl.SelectedTab = host._buildErrorsSubTab;
        }

        public void RenderDiagnosticStore()
        {
            if (host == null || host._buildErrorsListView == null || host._diagnosticStore == null) return;
            host._buildErrorsListView.Items.Clear();

            // One ListViewItem per diagnostic; keep the source BuildResultItem in Tag for later use.
            foreach (var r in host._diagnosticStore.Snapshot(host._diagnosticSourceFilter, host._diagnosticSeverityFilter))
            {
                var item = new ListViewItem(r.Severity ?? "");
                item.SubItems.Add(r.Source ?? "Build");
                item.SubItems.Add(r.Code ?? "");
                item.SubItems.Add(r.File ?? "");
                item.SubItems.Add(r.Line > 0 ? r.Line.ToString() : "");
                item.SubItems.Add(r.Message ?? "");
                item.Tag = r;

                if (string.Equals(r.Severity, "error", StringComparison.OrdinalIgnoreCase))
                {
                    item.ForeColor = Color.FromArgb(255, 100, 100);
                }
                else if (string.Equals(r.Severity, "warning", StringComparison.OrdinalIgnoreCase))
                {
                    item.ForeColor = Color.FromArgb(255, 200, 80);
                }

                host._buildErrorsListView.Items.Add(item);
            }

        }

        // Parse a single MSBuild/dotnet output line into a structured BuildResultItem. Called from
        // the build process's stdout/stderr reader threads, so appends to the shared list are
        // locked. Non-matching lines (plain build chatter) are ignored.
        public static void ParseBuildLine(string line, List<BuildResultItem> results)
        {
            if (string.IsNullOrEmpty(line)) return;

            // MSBuild format: path(line,col): severity code: message
            var match = System.Text.RegularExpressions.Regex.Match(line,
                @"^(.+?)\((\d+),(\d+)\)\s*:\s*(error|warning)\s+(\w+)\s*:\s*(.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                lock (results)
                {
                    results.Add(new BuildResultItem
                    {
                        File = match.Groups[1].Value.Trim(),
                        Line = int.Parse(match.Groups[2].Value),
                        Column = int.Parse(match.Groups[3].Value),
                        Severity = match.Groups[4].Value.ToLower(),
                        Code = match.Groups[5].Value,
                        Message = match.Groups[6].Value.Trim()
                    });
                }
            }
        }

        // Null out the cached dialog reference when the standalone Build Results dialog closes,
        // so a later request opens a fresh one instead of touching a disposed form.
        public void OnBuildResultsDialogClosed(object sender, FormClosedEventArgs e)
        {
            if (sender == host._buildResultsDialog)
            {
                host._buildResultsDialog = null;
            }
        }

        // Carries both outputs of an analysis run from the background worker to the UI
        // continuation: the report itself and the projection needed to map violations to types.
        private sealed class AnalysisRun
        {
            public AnalysisReport Report;
            public DiagramProjection Projection;
        }

        // Menu/toolbar entry point for "Run Analysis". Requires a loaded solution and an idle
        // job runner (single background job at a time, sharing the Cancel button).
        public void OnRunAnalysis(object sender, EventArgs e)
        {
            if (host._currentSolution == null)
            {
                host._statusLabel.Text = "No solution loaded";
                return;
            }
            if (host._jobRunner.IsBusy) return;

            // Build the diagram + run analysis on a background thread so the UI stays
            // responsive and the shared Cancel button can discard the run.
            host._jobRunner.Run(
                "Analyzing",
                token =>
                {
                    LayoutModel layout = host._layoutStore?.LayoutModel ?? new LayoutModel(host._currentSolution.FilePath);
                    DiagramProjection projection = new DiagramProjection(host._currentSolution, layout);
                    Diagram diagram = projection.Build();
                    token.ThrowIfCancellationRequested();
                    AnalysisReport report = host._analysisEngine.Analyze(diagram, token);
                    return new AnalysisRun { Report = report, Projection = projection };
                },
                // Success continuation (runs on the UI thread): show the report in the panel,
                // populate the embedded list, and bring the results into view — either by
                // selecting the docked tab or bringing the floating window to the front.
                run =>
                {
                    host._analysisPanel.ShowReport(run.Report);
                    host._statusLabel.Text = string.Format("Analysis complete: {0} violation(s) found", run.Report.Violations.Count);
                    PopulateEmbeddedAnalysisResults(run.Report, run.Projection);
                    // Select the docked tab (re-inserting it via ShowTab if the user had closed
                    // it — a bare SelectedTab assignment to a page no longer in TabPages silently
                    // does nothing), or bring the floating window to the front.
                    if (!host._analysisResultsUndocked)
                    {
                        host.ShowTab("analysis");
                    }
                    else if (host._floatingAnalysisResults != null)
                    {
                        host._floatingAnalysisResults.BringToFront();
                    }
                },
                // Failure continuation: report the error to the user.
                ex => MessageBox.Show(string.Format("Analysis failed: {0}", ex.Message), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error));
        }

        // Render the analysis report into both the results ListView (one row per violation) and
        // the plain-text output box, then reflect violation counts onto the canvas nodes.
        private void PopulateEmbeddedAnalysisResults(AnalysisReport report, DiagramProjection projection)
        {
            host._analysisResultsListView.Items.Clear();
            host._analysisOutputTextBox.Clear();
            if (report == null || report.Violations == null)
            {
                host._analysisSummaryLabel.Text = "No analysis results";
                return;
            }
            host._analysisSummaryLabel.Text = string.Format("Analysis: {0} violation(s) found", report.Violations.Count);
            // Build the plain-text report in parallel with the list rows.
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== HelixStudio Analysis Report ===");
            sb.AppendLine(string.Format("Violations: {0}", report.Violations.Count));
            sb.AppendLine();
            foreach (AnalysisResult violation in report.Violations)
            {
                string sev = violation.Severity.ToString();
                string rule = violation.RuleId ?? "";
                string msg = violation.Message ?? "";
                // A violation references a diagram node by id; resolve it back to the owning
                // type's FQN by scanning the projection so the row can name the offending type.
                string typeName = "";
                if (violation.AffectedNodeId.HasValue && projection != null && host._currentSolution != null)
                {
                    foreach (TypeModel allType in host._currentSolution.GetAllTypes())
                    {
                        TypeNode typeNode = projection.FindNode(allType);
                        if (typeNode != null && typeNode.Id == violation.AffectedNodeId.Value)
                        {
                            typeName = allType.FullyQualifiedName;
                            break;
                        }
                    }
                }
                // Severity drives the row color: red = error, amber = warning, green = info.
                ListViewItem item = new ListViewItem(sev);
                item.SubItems.Add(rule);
                item.SubItems.Add(typeName);
                item.SubItems.Add(msg);
                if (violation.Severity == AnalysisSeverity.Error)
                {
                    item.ForeColor = Color.FromArgb(255, 100, 100);
                }
                else if (violation.Severity == AnalysisSeverity.Warning)
                {
                    item.ForeColor = Color.FromArgb(255, 200, 80);
                }
                else
                {
                    item.ForeColor = Color.FromArgb(100, 200, 100);
                }
                host._analysisResultsListView.Items.Add(item);
                sb.AppendLine(string.Format("[{0}] {1}: {2} - {3}", sev, rule, typeName, msg));
            }
            host._analysisOutputTextBox.Text = sb.ToString();
            host._analysisInnerTabControl.SelectedTab = host._analysisResultsSubTab;
            UpdateNodeViolationBadges(report, projection);
        }

        // Reflect analysis violations as per-node badges on the canvas: reset all counts, then
        // group violations by owning type and stamp each matching visual with a count + tooltip
        // summary, and invalidate so the canvas repaints the badges.
        private void UpdateNodeViolationBadges(AnalysisReport report, DiagramProjection projection)
        {
            if (host._projectionBuilder == null || host._canvas == null)
            {
                return;
            }
            // Clear any badges left over from a previous run before recomputing.
            foreach (NodeVisual node in host._canvas.SceneGraph.Nodes)
            {
                if (node != null)
                {
                    node.ViolationCount = 0;
                    node.ViolationSummary = null;
                }
            }
            if (report == null || report.Violations == null || host._currentSolution == null)
            {
                host._canvas.Invalidate();
                return;
            }
            // Accumulate the violation summary strings per type FQN.
            Dictionary<string, List<string>> byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (AnalysisResult violation in report.Violations)
            {
                if (!violation.AffectedNodeId.HasValue || projection == null)
                {
                    continue;
                }
                // Same node-id → type-FQN resolution as above.
                string typeName = null;
                foreach (TypeModel allType in host._currentSolution.GetAllTypes())
                {
                    TypeNode typeNode = projection.FindNode(allType);
                    if (typeNode != null && typeNode.Id == violation.AffectedNodeId.Value)
                    {
                        typeName = allType.FullyQualifiedName;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(typeName))
                {
                    if (!byType.TryGetValue(typeName, out var list))
                    {
                        list = (byType[typeName] = new List<string>());
                    }
                    list.Add(string.Format("[{0}] {1}", violation.RuleId ?? "", violation.Message ?? ""));
                }
            }
            // Stamp each type's visual with its violation count + newline-joined tooltip text.
            foreach (KeyValuePair<string, List<string>> item in byType)
            {
                NodeVisual nodeVisual = host._projectionBuilder.FindVisual(item.Key);
                if (nodeVisual != null)
                {
                    nodeVisual.ViolationCount = item.Value.Count;
                    nodeVisual.ViolationSummary = string.Join("\n", item.Value.ToArray());
                }
            }
            host._canvas.Invalidate();
        }

        // Null out the cached analysis dialog reference when it closes (see build variant above).
        public void OnAnalysisDialogClosed(object sender, FormClosedEventArgs e)
        {
            if (sender == host._analysisResultsDialog)
            {
                host._analysisResultsDialog = null;
            }
        }
    }
}
