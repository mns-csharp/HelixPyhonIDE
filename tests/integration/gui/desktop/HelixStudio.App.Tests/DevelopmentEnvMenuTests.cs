using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Controls;
using HelixPythonNamespace.Workspace.Toolchains;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Tools ▸ Development Env — the machine's installed language kits, as a tree in a workbench
    /// tab. It was a modal dialog before the fix; the inventory is something you consult WHILE
    /// working, and a dialog has to be dismissed before you can look at the code that raised the
    /// question.
    ///
    /// The panel is asserted through <see cref="DevelopmentEnvironmentPanel.ShowReport"/> with a
    /// known report rather than a real scan: a test that ran the real scan would be asserting what
    /// happens to be installed on the machine running the suite, and would go red when someone
    /// uninstalls Go.
    /// </summary>
    [TestClass]
    public class DevelopmentEnvMenuTests
    {
        private static ToolStripMenuItem ToolsMenu(MainForm form)
        {
            foreach (ToolStripItem item in form.MainMenuStrip.Items)
            {
                var menu = item as ToolStripMenuItem;
                if (menu == null) continue;
                if ((menu.Text ?? "").Replace("&", "").TrimStart().StartsWith("Tools")) return menu;
            }
            return null;
        }

        private static ToolStripMenuItem Item(ToolStripMenuItem menu, string textFragment)
        {
            foreach (ToolStripItem item in menu.DropDownItems)
            {
                var entry = item as ToolStripMenuItem;
                if (entry == null) continue;
                if ((entry.Text ?? "").Replace("&", "").IndexOf(
                        textFragment, System.StringComparison.OrdinalIgnoreCase) >= 0) return entry;
            }
            return null;
        }

        [TestMethod]
        public void TheToolsMenu_OffersDevelopmentEnv()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                ToolStripMenuItem tools = ToolsMenu(form);
                Assert.IsNotNull(tools, "the shell must have a Tools menu");

                ToolStripMenuItem devEnv = Item(tools, "Development Env");
                Assert.IsNotNull(devEnv, "Tools ▸ Development Env is missing");
                Assert.IsTrue(devEnv.Available,
                    "the machine's toolchains are not solution-scoped — the item stays available");
                Assert.IsFalse(string.IsNullOrEmpty(devEnv.ToolTipText),
                    "every Tools entry explains itself on hover");
            }
        }

        [TestMethod]
        public void DevelopmentEnv_KeepsItsOwnAltKey_WithinTheToolsMenu()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                ToolStripMenuItem tools = ToolsMenu(form);
                var mnemonics = new List<char>();
                foreach (ToolStripItem item in tools.DropDownItems)
                {
                    string text = item.Text ?? "";
                    int amp = text.IndexOf('&');
                    if (amp >= 0 && amp + 1 < text.Length) mnemonics.Add(char.ToUpperInvariant(text[amp + 1]));
                }
                Assert.AreEqual(1, mnemonics.Count(m => m == 'D'),
                    "Alt+D must reach Development Env alone, or it cycles instead of activating");
            }
        }

        // ------------------------------------------------------------------ the tab

        [TestMethod]
        public void ClickingIt_OpensAWorkbenchTab_AndNotAWindow()
        {
            Assert.AreEqual("1", System.Environment.GetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE"),
                "run with /Settings:HelixStudio.runsettings");

            using (MainForm form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                int formsBefore = Application.OpenForms.Count;

                Item(ToolsMenu(form), "Development Env").PerformClick();
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);

                Assert.AreEqual(formsBefore, Application.OpenForms.Count,
                    "the inventory is a tab now — no window may be opened, modal or otherwise");

                TabPage tab = form._mainTabControl.TabPages.Cast<TabPage>()
                    .FirstOrDefault(t => t.Text == "Development Env");
                Assert.IsNotNull(tab, "no Development Env tab was added to the workspace");
                Assert.AreSame(tab, form._mainTabControl.SelectedTab,
                    "the command brings its tab forward");

                DevelopmentEnvironmentPanel panel = tab.Controls.OfType<DevelopmentEnvironmentPanel>()
                    .FirstOrDefault();
                Assert.IsNotNull(panel, "the tab must host the panel, not be an empty page");
                Assert.IsFalse(panel.HasScanned,
                    "a non-interactive host must not spawn a child process per catalog entry");
                Assert.AreEqual(0, panel.Tree.Nodes.Count,
                    "and with the scan suppressed there is nothing to show yet — an empty tree, "
                    + "not an invented one");

                form.Close();
            }
        }

        [TestMethod]
        public void AskingTwice_ReusesTheSameTab()
        {
            using (MainForm form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                Item(ToolsMenu(form), "Development Env").PerformClick();
                TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);

                // Move away first, so the second ask has something to undo. Without this the
                // assertion below passes on a tab that was merely restored from user settings —
                // the whole suite shares the real %APPDATA% settings file.
                TabPage elsewhere = form._mainTabControl.TabPages.Cast<TabPage>()
                    .FirstOrDefault(t => t.Text != "Development Env");
                Assert.IsNotNull(elsewhere, "the shell always has another tab to move to");
                form._mainTabControl.SelectedTab = elsewhere;

                Item(ToolsMenu(form), "Development Env").PerformClick();
                TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);

                Assert.AreEqual(1, form._mainTabControl.TabPages.Cast<TabPage>()
                        .Count(t => t.Text == "Development Env"),
                    "a second ask must select the tab, not add another one");
                Assert.AreEqual("Development Env", form._mainTabControl.SelectedTab.Text,
                    "and it must bring the existing tab forward, not quietly do nothing");

                form.Close();
            }
        }

        [TestMethod]
        public void TheTabSurvivesClosingTheSolution()
        {
            // What is installed on the machine has nothing to do with what is open in the IDE, so
            // the tab is registered NOT solution-scoped: the solution gate must leave it alone.
            using (MainForm form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                Item(ToolsMenu(form), "Development Env").PerformClick();
                TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);
                Assert.AreEqual("Development Env", form._mainTabControl.SelectedTab.Text,
                    "precondition: the tab is open and forward before the gate runs");

                // The production route: this runs on every menu-bar activation and is what applies
                // the solution gate to the tab strip.
                form.UpdateMenuWorkflowVisibility();
                TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);

                Assert.IsTrue(form._mainTabControl.TabPages.Cast<TabPage>()
                        .Any(t => t.Text == "Development Env"),
                    "closing a solution must not take the machine's inventory away with it");

                form.Close();
            }
        }

        // ------------------------------------------------------------------ nothing is probed unasked

        [TestMethod]
        public void MakingThePanelVisible_DoesNotProbeTheMachine()
        {
            // Reported: the IDE booted onto the Welcome page with the status bar already
            // reading "Probing installed language kits…". The tab had been restored from last
            // session's visibility, ShowTab selects the tab it inserts, and the panel took becoming
            // visible as an instruction to scan. Being SHOWN is not the user asking.
            using (var host = new Form { StartPosition = FormStartPosition.Manual,
                                         Location = new System.Drawing.Point(-4000, -4000),
                                         Size = new System.Drawing.Size(900, 600) })
            using (var panel = new DevelopmentEnvironmentPanel(
                () => new DevelopmentEnvironmentScanner(new OneTool())))
            {
                bool dispatched = false;
                panel.RunScan = (work, onFinished, onCancelled) => { dispatched = true; return true; };

                host.Controls.Add(panel);
                TestWindow.ShowOffScreen(host);
                Application.DoEvents();
                panel.Visible = false;
                panel.Visible = true;   // offscreen-guard: a child panel inside the shown host, not a window
                Application.DoEvents();

                Assert.IsFalse(dispatched, "showing the panel must not start a scan");
                Assert.IsFalse(panel.HasScanned);
                Assert.AreEqual(0, panel.Tree.Nodes.Count);

                // The one thing that does: the user pressing the button.
                Assert.IsTrue(panel.RescanButton.CanSelect, "the button must be clickable to be clicked");
                panel.RescanButton.PerformClick();

                Assert.IsTrue(dispatched, "the refresh button is what asks the machine");

                host.Close();
            }
        }

        [TestMethod]
        public void RestoringTheTabAtStartup_LeavesTheMachineAlone()
        {
            // EnsureDevelopmentEnvironmentTab is the startup restore path as well as the menu's
            // first step, so it is the method that must not scan.
            using (MainForm form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                DevelopmentEnvironmentPanel panel = form.EnsureDevelopmentEnvironmentTab();
                form.ShowTab("devEnv");
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);

                Assert.IsFalse(panel.HasScanned,
                    "putting the tab back is not the user asking what is installed");
                Assert.IsFalse(form._jobRunner.IsBusy,
                    "and the IDE must not be holding its foreground job slot at startup");

                form.Close();
            }
        }

        // ------------------------------------------------------------------ layout

        [TestMethod]
        public void ItsCommandsAreIconButtonsOnAToolbarAboveTheTree()
        {
            // The Terminal idiom: a Luna toolbar band across the top of the tab carrying flat
            // 28-wide icon buttons that explain themselves on hover. They used to be captioned
            // buttons in the bottom-right corner — the far corner from the tree they act on.
            using (var panel = new DevelopmentEnvironmentPanel())
            {
                foreach (Button button in new[] { panel.RescanButton, panel.CopyPathButton })
                {
                    Assert.AreEqual(string.Empty, button.Text, "an icon button carries no caption");
                    Assert.IsNotNull(button.Image, "…so it needs an icon");
                    Assert.AreEqual(28, button.Width);
                    Assert.AreEqual(FlatStyle.Flat, button.FlatStyle);
                    Assert.AreEqual(DockStyle.Left, button.Dock);
                }

                Panel toolbar = panel.RescanButton.Parent as Panel;
                Assert.IsNotNull(toolbar, "the buttons live on a toolbar strip");
                Assert.AreEqual(DockStyle.Top, toolbar.Dock, "which sits above the tree, not below it");
                Assert.AreSame(toolbar, panel.CopyPathButton.Parent, "both commands on the same strip");

                // Last-added Top control claims the topmost band; anything else there would push
                // the toolbar down the tab.
                Assert.AreSame(toolbar, panel.Controls[panel.Controls.Count - 1],
                    "the toolbar is the topmost band of the tab");

                Assert.IsFalse(panel.CopyPathButton.Enabled,
                    "there is nothing to copy until a scan has found a tool and one is selected");
            }
        }

        [TestMethod]
        public void SelectingATool_EnablesCopyPath()
        {
            // Shown, not just constructed: TreeView.SelectedNode raises AfterSelect from the native
            // TVN_SELCHANGED notification, so on an unrealized tree it assigns silently and the
            // button-enabling this asserts never runs.
            using (var host = new Form { StartPosition = FormStartPosition.Manual,
                                         Location = new System.Drawing.Point(-4000, -4000),
                                         Size = new System.Drawing.Size(900, 600) })
            using (var panel = new DevelopmentEnvironmentPanel())
            {
                host.Controls.Add(panel);
                TestWindow.ShowOffScreen(host);
                Application.DoEvents();

                panel.ShowReport(Report());

                Assert.IsTrue(panel.CopyPathButton.Enabled,
                    "the scan selects the first tool it found, and that row has a path to copy");

                panel.Tree.SelectedNode = Root(panel, "Not found").Nodes[0];
                Application.DoEvents();
                Assert.IsFalse(panel.CopyPathButton.Enabled,
                    "a kit that was never found has no path, so the button must not offer one");

                host.Close();
            }
        }

        [TestMethod]
        public void TheDetailsPane_SitsBesideTheTree_NotUnderIt()
        {
            // The tree is the tall thing — forty-odd kits with their tools under them — while the
            // details pane is eight short lines that never grow. Stacked, the details ate a third
            // of the window's height and the list scrolled inside what was left.
            using (var panel = new DevelopmentEnvironmentPanel())
            {
                Assert.AreEqual(Orientation.Vertical, panel.Split.Orientation,
                    "a vertical split puts the panes side by side, which is what gives the list its rows");
                Assert.IsTrue(panel.Split.Panel1.Controls.Contains(panel.Tree),
                    "the tree takes the left, wider pane");
            }
        }

        [TestMethod]
        public void ThePanelHasNoStatusLineOfItsOwn_ItReportsToTheShellsStatusBar()
        {
            // The summary was printed twice, thirty pixels apart: once inside the panel and once on
            // the status bar right below it, which already carries this scan's progress and Cancel.
            using (MainForm form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                Item(ToolsMenu(form), "Development Env").PerformClick();
                TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);

                TabPage tab = form._mainTabControl.TabPages.Cast<TabPage>()
                    .First(t => t.Text == "Development Env");
                DevelopmentEnvironmentPanel panel = tab.Controls.OfType<DevelopmentEnvironmentPanel>().First();

                Assert.IsNotNull(panel.ReportStatus,
                    "with no label of its own the panel needs somewhere to say 'Scanning…'");

                foreach (Control child in panel.Controls)
                    Assert.IsFalse(child is Label && ((Label)child).Dock == DockStyle.Bottom,
                        "the panel's own bottom status label is what was removed: " + child.Text);

                panel.ReportStatus("kits: 3");
                Assert.AreEqual("kits: 3", form._statusLabel.Text,
                    "what the panel has to say goes to the one status line the window has");

                form.Close();
            }
        }

        [TestMethod]
        public void TheTreeGetsMostOfTheWidth_OnceThePanelHasARealSize()
        {
            // SplitterDistance validates against the CURRENT size, so it can only be set after the
            // panel is realized — the reason it is applied in OnHandleCreated and not the ctor.
            using (var host = new Form { StartPosition = FormStartPosition.Manual,
                                         Location = new System.Drawing.Point(-4000, -4000),
                                         Size = new System.Drawing.Size(1000, 700) })
            using (var panel = new DevelopmentEnvironmentPanel())
            {
                host.Controls.Add(panel);
                TestWindow.ShowOffScreen(host);
                Application.DoEvents();

                Assert.IsTrue(panel.Split.SplitterDistance > panel.Split.Width / 2,
                    "the list is what the user came for: it gets more than half the width, not less");
                Assert.IsTrue(panel.Split.Panel2MinSize >= 200,
                    "and the details pane keeps enough room to read a path in");

                host.Close();
            }
        }

        // ------------------------------------------------------------------ progress

        [TestMethod]
        public void TheScanRunsAsAJob_SoTheStatusBarShowsProgressAndACancelButton()
        {
            // A 20-second probe of the whole machine with nothing moving reads as a frozen IDE.
            // The scan goes through the same JobRunner as Code Review, which owns the status text,
            // the marquee bar and the Cancel button.
            using (MainForm form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                Item(ToolsMenu(form), "Development Env").PerformClick();
                TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);

                TabPage tab = form._mainTabControl.TabPages.Cast<TabPage>()
                    .First(t => t.Text == "Development Env");
                DevelopmentEnvironmentPanel panel = tab.Controls.OfType<DevelopmentEnvironmentPanel>().First();
                Assert.IsNotNull(panel.RunScan,
                    "the panel must hand its scan to the host, or it runs invisibly on its own thread");

                // Drive one scan through the real wiring with a scanner that reports nothing, so
                // the assertion is about the progress affordances and not about this machine.
                var gate = new ManualResetEventSlim(false);
                bool accepted = panel.RunScan(
                    token => { gate.Wait(2000); return null; },
                    delegate { },
                    delegate { });

                Assert.IsTrue(accepted, "an idle IDE must accept the scan");
                Assert.IsTrue(form._jobRunner.IsBusy, "the scan holds the foreground job slot");
                Assert.AreEqual("Probing installed language kits", form._jobRunner.CurrentTitle);
                Assert.IsNotNull(form._jobRunner.BusyIndicator, "the shell wires a busy indicator");
                Assert.IsTrue(form._jobRunner.BusyIndicator.Available,
                    "the status bar's progress bar is showing while the machine is probed");
                Assert.IsTrue(form._cancelJobButton.Available,
                    "and a long job the user cannot stop is worse than no job");
                StringAssert.Contains(form._statusLabel.Text, "Probing installed language kits",
                    "the status bar names what is taking the time");

                gate.Set();
                TestMainForm.PumpUntilIdle(iterations: 40, delayMs: 25);

                Assert.IsFalse(form._jobRunner.IsBusy, "the job ends when the scan does");
                Assert.IsFalse(form._jobRunner.BusyIndicator.Available,
                    "and the progress bar goes away with it");
                Assert.IsFalse(form._cancelJobButton.Available, "as does Cancel");

                form.Close();
            }
        }

        /// <summary>A machine with one tool on it, answering immediately.</summary>
        private sealed class OneTool : IToolProbeRunner
        {
            public string Resolve(ToolProbe probe)
            {
                return probe.Executable == "python" ? @"C:\Python312\python.exe" : null;
            }

            public ProbeRun Run(ToolProbe probe, string path, CancellationToken token)
            {
                return ProbeRun.Ran("Python 3.12.1");
            }
        }

        [TestMethod]
        public void AFinishedScan_ReplacesTheProgressTextWithItsSummary()
        {
            // JobRunner.EndJob leaves the status bar untouched unless it is handed a final message,
            // so a scan that reported nothing on completion would leave the bar reading "Probing
            // installed language kits…" for the rest of the session. Reporting the summary is what
            // retires it — this test owns that half of the chain, and
            // ThePanelHasNoStatusLineOfItsOwn_ItReportsToTheShellsStatusBar owns the other (that
            // what the panel reports is what the shell's status bar shows).
            using (var panel = new DevelopmentEnvironmentPanel(
                () => new DevelopmentEnvironmentScanner(new OneTool())))
            {
                var reported = new List<string>();
                panel.ReportStatus = text => reported.Add(text);
                // Stands in for JobRunner.Run: work on the caller's thread, then the success callback.
                panel.RunScan = (work, onFinished, onCancelled) =>
                {
                    onFinished(work(CancellationToken.None));
                    return true;
                };

                panel.StartScan();

                Assert.IsTrue(reported.Count >= 2, "the panel says 'Scanning…' and then what it found");
                StringAssert.Contains(reported[0], "Scanning");
                StringAssert.Contains(reported[reported.Count - 1], "language kit",
                    "the last word on the status bar is the result, not the operation that has ended");
                StringAssert.DoesNotMatch(reported[reported.Count - 1],
                    new System.Text.RegularExpressions.Regex("Scanning"),
                    "a status bar still announcing a finished operation is the one thing it must never do");
            }
        }

        [TestMethod]
        public void WhileAnotherJobOwnsTheIde_TheTabSaysSoInsteadOfHangingOnScanning()
        {
            using (var panel = new DevelopmentEnvironmentPanel())
            {
                string reported = null;
                panel.ReportStatus = text => reported = text;
                panel.RunScan = (work, onFinished, onCancelled) => false;   // host busy

                panel.StartScan();

                StringAssert.Contains(reported, "Rescan",
                    "a refused scan must tell the user what to do, not sit on 'Scanning…' forever");
            }
        }

        /// <summary>A machine with one tool on it, which the user cancels the scan in the middle of.</summary>
        private sealed class OneToolThenCancel : IToolProbeRunner
        {
            private readonly System.Threading.CancellationTokenSource _cts;
            public OneToolThenCancel(System.Threading.CancellationTokenSource cts) { _cts = cts; }

            public string Resolve(ToolProbe probe)
            {
                return probe.Executable == "python" ? @"C:\Python312\python.exe" : null;
            }

            public ProbeRun Run(ToolProbe probe, string path, System.Threading.CancellationToken token)
            {
                _cts.Cancel();   // the user presses Cancel while the scan is mid-flight
                return ProbeRun.Ran("Python 3.12.1");
            }
        }

        [TestMethod]
        public void ACancelledScan_StillShowsWhatItManagedToSee()
        {
            // A cancelled job delivers no result to onSuccess, so the panel has to keep the partial
            // report itself. Throwing it away would blank the tab on Cancel; presenting it without
            // saying it is partial would turn "we stopped looking" into "it is not installed".
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                var runner = new OneToolThenCancel(cts);
                using (var panel = new DevelopmentEnvironmentPanel(
                    () => new DevelopmentEnvironmentScanner(runner)))
                {
                    string reported = null;
                    panel.ReportStatus = text => reported = text;
                    panel.RunScan = (work, onFinished, onCancelled) =>
                    {
                        work(cts.Token);   // runs until the stub trips the token
                        onCancelled();
                        return true;
                    };

                    panel.StartScan();

                    TreeNode python = Root(panel, "Python");
                    Assert.IsNotNull(python, "what was found before the Cancel stays on screen");
                    Assert.AreEqual(1, python.Nodes.Count);
                    StringAssert.Contains(reported, "cancelled",
                        "and the list must not read as the machine's full inventory");
                }
            }
        }

        // ------------------------------------------------------------------ the tree itself

        private static InstalledKit Kit(string language, string component, string version, string path,
            KitOutcome outcome = KitOutcome.Installed)
        {
            return new InstalledKit(language, component, version, path, outcome, component + " " + version);
        }

        private static DevelopmentEnvironmentReport Report(bool complete = true)
        {
            var groups = new List<KitGroup>
            {
                new KitGroup("Python", new List<InstalledKit>
                {
                    Kit("Python", "CPython interpreter", "3.12.1", @"C:\Python312\python.exe"),
                    Kit("Python", "pip", "24.0", @"C:\Python312\Scripts\pip.exe"),
                }),
                new KitGroup("CUDA", new List<InstalledKit>
                {
                    Kit("CUDA", "nvcc", "12.4", @"C:\CUDA\bin\nvcc.exe"),
                }),
                // Present on disk, but it never said what it was.
                new KitGroup("Java", new List<InstalledKit>
                {
                    Kit("Java", "Maven", null, @"C:\tools\mvn.cmd", KitOutcome.TimedOut),
                }),
                // Genuinely absent.
                new KitGroup("Go", new List<InstalledKit>
                {
                    Kit("Go", "Go toolchain", null, null, KitOutcome.NotFound),
                }),
            };
            return new DevelopmentEnvironmentReport(groups, complete);
        }

        private static TreeNode Root(DevelopmentEnvironmentPanel panel, string textFragment)
        {
            foreach (TreeNode node in panel.Tree.Nodes)
                if (node.Text.IndexOf(textFragment, System.StringComparison.OrdinalIgnoreCase) >= 0) return node;
            return null;
        }

        [TestMethod]
        public void ThePanel_ShowsOneBranchPerInstalledKit_WithItsToolsUnderneath()
        {
            using (var panel = new DevelopmentEnvironmentPanel())
            {
                Assert.AreEqual(0, panel.Tree.Nodes.Count,
                    "constructing the panel must not scan — the scan starts when the tab is looked at");

                panel.ShowReport(Report());

                TreeNode python = Root(panel, "Python");
                Assert.IsNotNull(python, "an installed kit gets its own branch");
                Assert.AreEqual(2, python.Nodes.Count, "both Python tools are listed");
                Assert.IsTrue(python.IsExpanded, "what was found is visible without a click");
                StringAssert.Contains(python.Nodes[0].Text, "3.12.1", "the version is on the row");
                Assert.IsInstanceOfType(python.Nodes[0].Tag, typeof(InstalledKit),
                    "each row carries its kit, so the details pane and Copy Path have something to read");

                Assert.IsNotNull(Root(panel, "CUDA"));
            }
        }

        [TestMethod]
        public void AToolThatCouldNotBeIdentified_IsNotFiledUnderNotFound()
        {
            using (var panel = new DevelopmentEnvironmentPanel())
            {
                panel.ShowReport(Report());

                TreeNode unknown = Root(panel, "Could not be identified");
                Assert.IsNotNull(unknown, "a tool that is on disk but silent needs its own answer");
                Assert.AreEqual(1, unknown.Nodes.Count);
                StringAssert.Contains(unknown.Nodes[0].Text, "Maven");

                TreeNode missing = Root(panel, "Not found");
                Assert.IsNotNull(missing);
                Assert.AreEqual(1, missing.Nodes.Count, "only Go was actually absent");
                StringAssert.Contains(missing.Nodes[0].Text, "Go");
                Assert.IsFalse(missing.IsExpanded, "absence is the part the user did not come for");
            }
        }

        [TestMethod]
        public void ARowNeverPrintsItsVersionTwice()
        {
            // Anything enumerated per version names itself with that version, and a row reading
            // "MSVC toolset 14.50.35717  14.50.35717" reads as a rendering bug.
            Assert.AreEqual("MSVC toolset 14.50.35717",
                DevelopmentEnvironmentPanel.Label(
                    Kit("C / C++", "MSVC toolset 14.50.35717", "14.50.35717", @"C:\cl.exe")));
            Assert.AreEqual("Python 3.11 (default)",
                DevelopmentEnvironmentPanel.Label(
                    Kit("Python", "Python 3.11 (default)", "3.11", @"C:\python.exe")));
            Assert.AreEqual("CPython interpreter  3.12.1",
                DevelopmentEnvironmentPanel.Label(
                    Kit("Python", "CPython interpreter", "3.12.1", @"C:\python.exe")),
                "a tool whose name carries no version still shows one");
            Assert.AreEqual("Store",
                DevelopmentEnvironmentPanel.Label(
                    Kit("Python", "Store", null, @"C:\python.exe")),
                "and a tool that reported none shows none — not an empty gap");
        }

        [TestMethod]
        public void ACancelledScan_SaysSoInsteadOfPresentingAPartialList()
        {
            string complete = DevelopmentEnvironmentPanel.StatusFor(Report());
            string cancelled = DevelopmentEnvironmentPanel.StatusFor(Report(false));

            StringAssert.Contains(complete, "3 tools", "two Python tools plus nvcc actually ran");
            StringAssert.Contains(complete, "2 language kits");
            StringAssert.Contains(cancelled, "cancelled",
                "an interrupted scan must not let its list read as the machine's full inventory");
        }
    }
}
