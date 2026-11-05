using System;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Floating the Solution Explorer and putting it back.
    ///
    /// The panel is REPARENTED, not rebuilt — the same control moves between the splitter and a
    /// floating window and back. That makes the whole round trip one long opportunity to dispose
    /// the thing still being used, and an ObjectDisposedException here does not just fail the
    /// redock: the explorer is gone for the rest of the session with no way to get it back.
    /// </summary>
    [TestClass]
    public class ExplorerFloatRedockTests
    {
        [TestMethod]
        public void TheExplorerSurvivesAFloatAndRedock()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                Control panel = form._leftDockPanel;

                form.OnFloatExplorer(null, EventArgs.Empty);
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);
                Assert.IsFalse(panel.IsDisposed, "floating disposed the explorer panel");

                form.OnDockExplorerLeft(null, EventArgs.Empty);
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);

                Assert.IsFalse(panel.IsDisposed, "redocking disposed the explorer panel");
                Assert.IsNotNull(panel.Parent, "the explorer was never put back into the layout");
                form.Close();
            }
        }

        /// <summary>
        /// Float and redock repeatedly. Once was survivable while the second pass was not, because
        /// the first left the floating window's teardown half-finished.
        /// </summary>
        [TestMethod]
        public void TheExplorerSurvivesRepeatedFloatAndRedock()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                Control panel = form._leftDockPanel;

                for (int i = 0; i < 3; i++)
                {
                    form.OnFloatExplorer(null, EventArgs.Empty);
                    TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);
                    form.OnDockExplorerLeft(null, EventArgs.Empty);
                    TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);
                    Assert.IsFalse(panel.IsDisposed, "the explorer was disposed on round " + (i + 1));
                    Assert.IsNotNull(panel.Parent, "the explorer was lost on round " + (i + 1));
                }
                form.Close();
            }
        }

        /// <summary>
        /// Closing the floating window with its own ✕ redocks. If that path leaves the window
        /// half-torn-down, the NEXT redock is the one that crashes — which is how this reached a
        /// user: the failure and its cause were separated by an interaction.
        /// </summary>
        [TestMethod]
        public void ClosingTheFloatingWindowRedocksInsteadOfDestroying()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();
                Control panel = form._leftDockPanel;

                form.OnFloatExplorer(null, EventArgs.Empty);
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);
                Form floating = panel.FindForm();
                Assert.IsNotNull(floating);
                Assert.AreNotSame(form, floating, "the panel never left the main window");

                floating.Close();
                TestMainForm.PumpUntilIdle(iterations: 6, delayMs: 20);

                Assert.IsFalse(panel.IsDisposed, "closing the floating window disposed the explorer");
                Assert.AreSame(form, panel.FindForm(), "the explorer did not come back to the main window");

                // And the panel is usable afterwards, which is what the crash actually broke.
                form.OnFloatExplorer(null, EventArgs.Empty);
                TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);
                form.OnDockExplorerLeft(null, EventArgs.Empty);
                TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);
                Assert.IsFalse(panel.IsDisposed, "the explorer was disposed on the next float/redock");

                form.Close();
            }
        }
        /// <summary>
        /// The Output pane comes back from a float as ITSELF, not wrapped in the floating window's
        /// chrome.
        ///
        /// LunaForm keeps its children inside a host panel that clears the hand-drawn caption, so
        /// taking Controls[0] off the form hands back that panel. Redocking it put the chrome on
        /// the tab and the next undock wrapped the result again — one more tan border per cycle.
        /// </summary>
        [TestMethod]
        public void AFloatedPaneComesBackAsItselfNotAsTheWindowsChrome()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                TabPage tab = BuildResultsTab(form);
                Assert.IsNotNull(tab, "there is no Output tab to float");
                Assert.IsTrue(tab.Controls.Count > 0, "the Output tab is empty");
                Control content = tab.Controls[0];

                for (int i = 0; i < 2; i++)
                {
                    Pane(form, "UndockPane", "BuildResults");
                    TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);
                    Pane(form, "RedockPane", "BuildResults");
                    TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);

                    Assert.AreSame(content, tab.Controls[0],
                        "round " + (i + 1) + ": the Output tab holds "
                        + tab.Controls[0].GetType().Name + " instead of its own content");
                }
                form.Close();
            }
        }

        private static TabPage BuildResultsTab(MainForm form)
        {
            var f = typeof(MainForm).GetField("_buildResultsTab",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            return f == null ? null : f.GetValue(form) as TabPage;
        }

        // UndockPane is public on the coordinator; RedockPane is private, and both are reached the
        // same way the menu reaches them.
        private static void Pane(MainForm form, string method, string paneId)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Public;
            object coordinator = typeof(MainForm)
                .GetField("_buildAnalysisCoordinator", flags).GetValue(form);
            coordinator.GetType().GetMethod(method, flags)
                .Invoke(coordinator, new object[] { paneId });
        }

    }
}
