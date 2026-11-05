namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using HelixPythonNamespace.UI.Controls;
    using HelixPythonNamespace.Workspace.Toolchains;
    using HelixPythonNamespace.AddIns.Kit;

    /// <summary>
    /// The Development Environment workbench tab — what programming-language kits are installed on
    /// THIS machine, as a tree of languages and the tools that make up each kit.
    ///
    /// It is a tab and not a dialog on purpose: the machine's inventory is something you consult
    /// WHILE working (which Python is Run about to use? is the CUDA toolkit even here?), and a
    /// modal window is exactly the wrong shape for that — it has to be dismissed before you can
    /// look at the code the question came from.
    ///
    /// Three things this panel is careful about, because all three are ways a list like this lies:
    /// <list type="bullet">
    /// <item>a tool that could not be identified is NOT reported as missing — it gets its own
    /// branch, since "we could not tell" and "it is not installed" are different answers;</item>
    /// <item>a scan the user cancelled says so on the status bar instead of presenting a partial
    /// list as the machine's full inventory;</item>
    /// <item>every version shown is the tool's own words, kept verbatim in the details pane.</item>
    /// </list>
    /// </summary>
    public sealed class DevelopmentEnvironmentPanel : UserControl
    {
        private readonly TreeView _tree;
        private readonly TextBox _details;
        private readonly Button _rescan;
        private readonly Button _copyPath;
        private readonly Func<DevelopmentEnvironmentScanner> _scannerFactory;
        private readonly SplitContainer _split;
        private readonly ToolTip _tips = new ToolTip();

        private CancellationTokenSource _cancellation;
        private DevelopmentEnvironmentReport _report;
        private bool _scannedOnce;
        private bool _splitLaidOut;

        /// <param name="scannerFactory">
        /// Makes the scanner when a scan starts. A factory rather than an instance because Rescan
        /// must be able to re-run a scan that was cancelled, and because nothing here may launch a
        /// process on its own — scanning is something the USER starts, never the panel.
        /// </param>
        public DevelopmentEnvironmentPanel(Func<DevelopmentEnvironmentScanner> scannerFactory = null)
        {
            _scannerFactory = scannerFactory ?? DefaultScanner;

            Dock = DockStyle.Fill;
            BackColor = SystemColors.Control;

            var header = new Label
            {
                Dock = DockStyle.Top,
                Height = 34,
                Padding = new Padding(12, 9, 12, 0),
                Text = "Programming language kits installed on " + SafeMachineName() + ".",
            };

            // The panel's own toolbar, in the Terminal's idiom: normal Luna toolbar chrome across
            // the top of the tab, 28x24 flat icon buttons with tooltips instead of captions. The
            // two commands used to be text buttons parked in the bottom-right corner — the far
            // corner from the tree they act on, and the one place in the tab nothing else lives.
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = LunaColors.ToolbarBackground };

            _rescan = MakeIconButton(ToolbarIcons.Refresh(), "Ask this machine again what it has");
            _rescan.Click += delegate { StartScan(); };

            _copyPath = MakeIconButton(ToolbarIcons.Copy(), "Copy the selected tool's full path to the clipboard");
            _copyPath.Enabled = false;   // nothing is selected until a scan has found something
            _copyPath.Click += delegate { CopySelectedPath(); };

            // Added right-to-left: each docks Left, so the last one added sits first.
            toolbar.Controls.Add(_copyPath);
            toolbar.Controls.Add(_rescan);

            // Side by side, not stacked: the tree is the tall thing — forty-odd kits, each with its
            // tools under it — and a details pane across the bottom spends the panel's height on
            // eight short lines that never change size. A vertical split gives the list every row
            // the window has.
            //
            // Only the orientation is set here. The minimum sizes and the splitter position are
            // ALL validated against the container's current width, which on a control that has not
            // been laid out yet is the 150px designer default — `Panel2MinSize = 200` throws
            // "SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize" from the
            // constructor. See ApplySplitLayout.
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
            };
            _split = split;

            _tree = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                ShowLines = true,
                ShowRootLines = true,
                ShowPlusMinus = true,
            };
            _tree.AfterSelect += OnNodeSelected;
            LunaColors.ApplyTo(_tree);

            _details = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                Text = NotScannedYet,
            };

            split.Panel1.Controls.Add(_tree);
            split.Panel2.Controls.Add(_details);

            // Docked children are laid out from the END of the collection backwards, so the LAST
            // Top control added claims the topmost band: the toolbar sits above the header line,
            // and Fill goes in first so it takes what is left.
            Controls.Add(split);
            Controls.Add(header);
            Controls.Add(toolbar);
        }

        // 28x24 flat icon-only button, tooltip-only label — matches TerminalPanel.MakeIconButton and
        // ResultsToolbar so every toolbar in the IDE reads as the same control.
        private Button MakeIconButton(Image icon, string tooltip)
        {
            var button = new Button
            {
                Dock = DockStyle.Left,
                Width = 28,
                FlatStyle = FlatStyle.Flat,
                Image = icon,
                ImageAlign = ContentAlignment.MiddleCenter,
                Text = string.Empty,
                TabStop = false,
            };
            button.FlatAppearance.BorderSize = 0;
            _tips.SetToolTip(button, tooltip);
            return button;
        }

        /// <summary>
        /// Whether a scan has run in this panel's lifetime. The shell reads it to decide whether
        /// opening the tab is the user's FIRST ask, and so worth a scan.
        /// </summary>
        public bool HasScanned { get { return _scannedOnce; } }

        /// <summary>
        /// Where this panel's one line of status goes. The shell points it at the status bar, which
        /// is where the summary, the progress and the Cancel button for this scan already are —
        /// a second copy of "46 tools across 19 language kits" inside the panel was the same
        /// sentence twice, thirty pixels apart.
        ///
        /// It carries more than the summary: "Scanning…", a refused scan, a cancelled one, and the
        /// Copy Path acknowledgement all come through here. A host that leaves it null gets a panel
        /// that works and says nothing.
        /// </summary>
        public Action<string> ReportStatus { get; set; }

        private void Status(string text)
        {
            Action<string> report = ReportStatus;
            if (report != null) report(text);
        }

        /// <summary>
        /// Hands one scan to whoever owns long-running work, returning false if that owner is
        /// already busy. <c>onFinished</c> and <c>onCancelled</c> are called on the UI thread.
        /// </summary>
        public delegate bool ScanDispatcher(Func<CancellationToken, DevelopmentEnvironmentReport> work,
                                            Action<DevelopmentEnvironmentReport> onFinished,
                                            Action onCancelled);

        /// <summary>
        /// How a scan is run. The IDE sets this to its <c>JobRunner</c>, which is what puts the
        /// scan on the status bar with the same marquee progress bar and Cancel button as Code
        /// Review, Compile and the rest — a 20-second probe of the whole machine with no visible
        /// sign of work reads as a frozen IDE.
        ///
        /// Left null the panel runs the scan on its own <see cref="Task"/>, so it still works
        /// standalone (and in tests) without a host to borrow a status bar from.
        /// </summary>
        public ScanDispatcher RunScan { get; set; }

        /// <summary>
        /// The scan the IDE runs: the PATH catalog, plus the two kits PATH cannot see — every
        /// registered Python interpreter, and the Visual C++ toolsets (cl.exe lives outside PATH
        /// until a developer prompt puts it there).
        /// </summary>
        private static DevelopmentEnvironmentScanner DefaultScanner()
        {
            var providers = new List<IKitProvider>
            {
                new Services.PythonInterpreterKitProvider(),
                new MsvcKitProvider(),
            };
            return new DevelopmentEnvironmentScanner(new ProcessToolProbeRunner(), null, providers);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplySplitLayout();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplySplitLayout();   // whichever of the two happens first once there is a real width
        }

        /// <summary>
        /// Give the tree most of the width, once the panel HAS a width. The list is what the user
        /// came for — forty-odd kits with their tools under them — while the details pane holds
        /// eight short lines whatever happens, so it gets the smaller share and keeps it.
        ///
        /// Runs once, and only above a width where the minimums fit: every setter here validates
        /// against the current width and throws otherwise. Below that the WinForms defaults stand,
        /// which is still a side-by-side split — just an evenly divided one.
        /// </summary>
        private void ApplySplitLayout()
        {
            const int treeMinimum = 220;
            const int detailsMinimum = 200;
            if (_splitLaidOut || _split.Width < treeMinimum + detailsMinimum + 10) return;
            try
            {
                _split.Panel1MinSize = treeMinimum;
                _split.Panel2MinSize = detailsMinimum;
                _split.SplitterDistance = Math.Max(treeMinimum, (int)(_split.Width * 0.6));
                _splitLaidOut = true;
            }
            catch (InvalidOperationException) { }   // mid-layout: the next size change tries again
            catch (ArgumentException) { }
        }

        protected override void Dispose(bool disposing)
        {
            // A scan in flight owns a child process; leaving it running past the tab would keep
            // probing for a panel nobody can see.
            if (disposing) { CancelScan(); _tips.Dispose(); }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ scanning

        public void StartScan()
        {
            if (RunScan != null) { StartHostedScan(); return; }

            CancelScan();
            _scannedOnce = true;
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;

            BeginScanUi();

            DevelopmentEnvironmentScanner scanner = _scannerFactory();
            Task.Factory.StartNew(delegate { return scanner.Scan(token); }, token)
                .ContinueWith(delegate (Task<DevelopmentEnvironmentReport> finished)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    DevelopmentEnvironmentReport result = finished.Status == TaskStatus.RanToCompletion
                        ? finished.Result
                        : null;
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate { EndScanUi(result); });
                    }
                    catch (ObjectDisposedException) { }     // the tab went away mid-scan
                    catch (InvalidOperationException) { }   // ... or lost its handle first
                });
        }

        /// <summary>
        /// Run the scan through the host's job scheduler, so it gets the status text, the marquee
        /// progress bar and the Cancel button that every other long operation in the IDE gets.
        ///
        /// The report is captured on the way out as well as returned: a cancelled job delivers no
        /// result, and the partial list IS the answer to show — with the status line saying what
        /// was never looked at. Throwing it away would leave the tab blank after a Cancel.
        /// </summary>
        private void StartHostedScan()
        {
            DevelopmentEnvironmentScanner scanner = _scannerFactory();
            DevelopmentEnvironmentReport partial = null;

            BeginScanUi();
            bool accepted = RunScan(
                delegate (CancellationToken token) { partial = scanner.Scan(token); return partial; },
                delegate (DevelopmentEnvironmentReport report) { if (!IsDisposed) EndScanUi(report); },
                delegate { if (!IsDisposed) EndScanUi(partial); });

            if (accepted) { _scannedOnce = true; return; }

            // Another operation owns the scheduler. Say so instead of sitting on "Scanning…"
            // forever, and leave _scannedOnce false so opening the tab again tries again.
            _rescan.Enabled = true;
            Status("Another operation is running — press Rescan when it has finished.");
        }

        private void BeginScanUi()
        {
            _rescan.Enabled = false;
            Status("Scanning…");
            _tree.Nodes.Clear();
            _details.Text = Prompt;
        }

        private void EndScanUi(DevelopmentEnvironmentReport report)
        {
            _rescan.Enabled = true;
            if (report == null) { Status("The scan did not complete."); return; }
            ShowReport(report);
        }

        private void CancelScan()
        {
            if (_cancellation == null) return;
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            _cancellation = null;
        }

        // ------------------------------------------------------------------ rendering

        /// <summary>
        /// Render a finished scan. Separated from the scan itself so the tree it builds can be
        /// asserted against a known report instead of against whatever happens to be installed on
        /// the machine running the tests.
        /// </summary>
        internal void ShowReport(DevelopmentEnvironmentReport report)
        {
            _report = report;
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                if (report == null) return;

                foreach (KitGroup group in report.InstalledGroups)
                {
                    TreeNode languageNode = new TreeNode(group.Language) { Tag = group };
                    languageNode.NodeFont = new Font(_tree.Font, FontStyle.Bold);
                    foreach (InstalledKit kit in group.Kits)
                    {
                        if (kit.Outcome != KitOutcome.Installed) continue;
                        languageNode.Nodes.Add(new TreeNode(Label(kit)) { Tag = kit });
                    }
                    languageNode.Text = group.Language + "  (" + languageNode.Nodes.Count + ")";
                    _tree.Nodes.Add(languageNode);
                    languageNode.Expand();
                }

                // Present but unidentifiable. Kept apart from both lists above: counting these as
                // installed would invent a version, and counting them as missing would deny a tool
                // that is sitting right there on disk.
                IList<InstalledKit> unresolved = report.Unresolved;
                if (unresolved.Count > 0)
                {
                    var node = new TreeNode("Could not be identified  (" + unresolved.Count + ")");
                    node.NodeFont = new Font(_tree.Font, FontStyle.Bold);
                    foreach (InstalledKit kit in unresolved)
                        node.Nodes.Add(new TreeNode(kit.Language + " — " + kit.Component) { Tag = kit });
                    _tree.Nodes.Add(node);
                }

                IList<KitGroup> missing = report.MissingGroups;
                if (missing.Count > 0)
                {
                    var node = new TreeNode("Not found on this machine  (" + missing.Count + ")");
                    node.NodeFont = new Font(_tree.Font, FontStyle.Bold);
                    foreach (KitGroup group in missing)
                        node.Nodes.Add(new TreeNode(group.Language) { Tag = group });
                    _tree.Nodes.Add(node);   // deliberately left collapsed
                }
            }
            finally { _tree.EndUpdate(); }

            Status(StatusFor(report));
            SelectFirstTool();
        }

        /// <summary>
        /// Put the selection on the first tool found, so the details pane shows a real row rather
        /// than opening onto a blank half-panel.
        /// </summary>
        private void SelectFirstTool()
        {
            foreach (TreeNode language in _tree.Nodes)
                foreach (TreeNode kit in language.Nodes)
                    if (kit.Tag is InstalledKit) { _tree.SelectedNode = kit; return; }
        }

        /// <summary>
        /// The status line carries the completeness of the scan, not just its yield — a cancelled
        /// scan's list is a sample, and saying otherwise would turn "not looked at" into "not there".
        /// </summary>
        internal static string StatusFor(DevelopmentEnvironmentReport report)
        {
            if (report == null) return "The scan did not complete.";
            int languages = report.InstalledGroups.Count;
            string summary = report.InstalledCount + " tool" + (report.InstalledCount == 1 ? "" : "s")
                           + " across " + languages + " language kit" + (languages == 1 ? "" : "s") + ".";
            if (!report.IsCompleteScan)
                return summary + " Scan was cancelled — anything not listed was not checked.";
            int unresolved = report.Unresolved.Count;
            if (unresolved > 0)
                return summary + " " + unresolved + " tool" + (unresolved == 1 ? " was" : "s were")
                     + " found but could not be identified.";
            return summary;
        }

        /// <summary>
        /// The row's text: the tool, then its version — unless the tool's own name already carries
        /// it, which is the normal case for anything enumerated per version ("MSVC toolset
        /// 14.50.35717", "Python 3.11 (default)"). Printing it twice reads like a rendering bug.
        /// </summary>
        internal static string Label(InstalledKit kit)
        {
            if (kit == null) return string.Empty;
            if (string.IsNullOrEmpty(kit.Version)) return kit.Component;
            if (kit.Component != null &&
                kit.Component.IndexOf(kit.Version, StringComparison.Ordinal) >= 0) return kit.Component;
            return kit.Component + "  " + kit.Version;
        }

        private void OnNodeSelected(object sender, TreeViewEventArgs e)
        {
            InstalledKit kit = e.Node == null ? null : e.Node.Tag as InstalledKit;
            KitGroup group = e.Node == null ? null : e.Node.Tag as KitGroup;

            if (kit != null)
            {
                _copyPath.Enabled = !string.IsNullOrEmpty(kit.Path);
                _details.Text = string.Join(Environment.NewLine, new[]
                {
                    "Kit:       " + kit.Language,
                    "Tool:      " + kit.Component,
                    "Version:   " + (string.IsNullOrEmpty(kit.Version) ? "(not reported)" : kit.Version),
                    "Path:      " + (string.IsNullOrEmpty(kit.Path) ? "(not found)" : kit.Path),
                    "Status:    " + Describe(kit.Outcome),
                    string.Empty,
                    "Reported by the tool itself:",
                    kit.Detail ?? string.Empty,
                });
                return;
            }

            _copyPath.Enabled = false;
            if (group != null)
            {
                _details.Text = group.IsInstalled
                    ? group.Language + ": " + group.Kits.Count + " tool(s) probed."
                    : group.Language + " was not found on this machine." + Environment.NewLine
                      + "Nothing from this kit is on PATH or in the locations the IDE knows about.";
                return;
            }
            _details.Text = Prompt;
        }

        private const string Prompt = "Select a tool to see its version, its full path, and the "
                                    + "banner it printed.";

        /// <summary>
        /// What the panel says before anyone has asked it anything. Nothing is probed until the
        /// user asks — becoming visible is NOT an ask, which is what made an IDE that merely
        /// restored this tab launch eighty child processes while the user was still looking at the
        /// Welcome page.
        /// </summary>
        private static readonly string NotScannedYet =
            "Nothing has been scanned yet." + Environment.NewLine + Environment.NewLine +
            "Press the refresh button above to ask this machine which language kits it has.";

        private static string Describe(KitOutcome outcome)
        {
            switch (outcome)
            {
                case KitOutcome.Installed: return "installed";
                case KitOutcome.NotFound: return "not found";
                case KitOutcome.TimedOut: return "present, but did not answer in time";
                default: return "present, but could not be identified";
            }
        }

        private void CopySelectedPath()
        {
            TreeNode node = _tree.SelectedNode;
            InstalledKit kit = node == null ? null : node.Tag as InstalledKit;
            if (kit == null || string.IsNullOrEmpty(kit.Path)) return;
            try { Clipboard.SetText(kit.Path); Status("Copied " + kit.Path); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Status("The clipboard is held by another program — path not copied.");
            }
        }

        private static string SafeMachineName()
        {
            try { return Environment.MachineName; }
            catch (InvalidOperationException) { return "this machine"; }
        }

        /// <summary>The last rendered scan — for tests and for the caller's status line.</summary>
        internal DevelopmentEnvironmentReport Report { get { return _report; } }

        internal TreeView Tree { get { return _tree; } }

        internal SplitContainer Split { get { return _split; } }

        internal Button RescanButton { get { return _rescan; } }

        internal Button CopyPathButton { get { return _copyPath; } }
    }
}
