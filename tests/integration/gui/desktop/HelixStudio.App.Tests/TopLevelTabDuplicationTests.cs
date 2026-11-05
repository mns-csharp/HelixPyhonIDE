using System;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// User-reported bug: after undocking the Output (Build Results) pane into its floating
    /// window, toggling it from View &gt; Panels while floating, then closing the floating window
    /// (redock), the main tab strip ended up with two "Output" tabs. Root cause was two-fold:
    ///   1. MainForm.InsertTabInOrder (used by BuildAnalysisCoordinator.RedockPane) inserted the
    ///      pane's TabPage unconditionally — TabPages.Insert on an already-present page adds a
    ///      second, visually duplicate header rather than deduping like a plain Controls collection.
    ///   2. PanelCoordinator.IsTabVisible didn't know a pane could be floating, so a Panels-menu
    ///      toggle while undocked treated the pane as "hidden" and re-inserted an empty copy of the
    ///      TabPage (its real content was still parented to the floating window) — the state desync
    ///      that produced the duplicate in the first place.
    /// Covers both undockable panes (Analysis Results, Build Results/"Output") since both route
    /// through the same InsertTabInOrder/IsTabVisible logic.
    /// </summary>
    [TestClass]
    public class TopLevelTabDuplicationTests
    {
        // Both undockable panes are result tabs, so opening a solution no longer puts them on the
        // strip on its own — they arrive when a build/analysis run shows them (PanelCoordinator
        // .ResultTabs). Show them here so each test starts from "the user has this pane open".
        private static void SetSolution(MainForm form)
        {
            typeof(MainForm).GetField("_currentSolution",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(form, new SolutionModel(@"C:\p\pyproject.toml", "P"));
            form.UpdateMenuWorkflowVisibility();
            form.ShowTab("buildResults");
            form.ShowTab("analysis");
        }

        private static int CountTabsWithText(TabControl main, string text)
        {
            int n = 0;
            foreach (TabPage tp in main.TabPages) if (tp.Text == text) n++;
            return n;
        }

        [TestMethod]
        public void UndockToggleRedock_NeverDuplicatesTheOutputTab()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form);
                form.CreateControl();

                Assert.AreEqual(1, CountTabsWithText(form._mainTabControl, "Output"), "sanity: starts with exactly one Output tab");

                form.OnToggleBuildResultsDocking(form, EventArgs.Empty);   // undock -> floating
                Assert.AreEqual(0, CountTabsWithText(form._mainTabControl, "Output"), "the tab leaves the strip while floating");
                Assert.IsTrue(form._buildResultsUndocked);

                form._panelMenuItemsByKey["buildResults"].PerformClick();  // View > Panels toggle while floating
                Assert.AreEqual(0, CountTabsWithText(form._mainTabControl, "Output"),
                    "toggling Panels while floating must not insert a second, empty copy");
                Assert.IsTrue(form._buildResultsUndocked, "still floating — the toggle must not have redocked it");

                form.OnToggleBuildResultsDocking(form, EventArgs.Empty);   // redock
                Assert.AreEqual(1, CountTabsWithText(form._mainTabControl, "Output"),
                    "back to exactly one Output tab after redocking — this was the reported duplicate");
                Assert.IsFalse(form._buildResultsUndocked);
            }
        }

        [TestMethod]
        public void UndockToggleRedock_NeverDuplicatesTheAnalysisResultsTab()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form);
                form.CreateControl();

                Assert.AreEqual(1, CountTabsWithText(form._mainTabControl, "Analysis Results"));

                form.OnToggleAnalysisResultsDocking(form, EventArgs.Empty);
                Assert.AreEqual(0, CountTabsWithText(form._mainTabControl, "Analysis Results"));

                form._panelMenuItemsByKey["analysis"].PerformClick();
                Assert.AreEqual(0, CountTabsWithText(form._mainTabControl, "Analysis Results"),
                    "toggling Panels while floating must not insert a second, empty copy");

                form.OnToggleAnalysisResultsDocking(form, EventArgs.Empty);
                Assert.AreEqual(1, CountTabsWithText(form._mainTabControl, "Analysis Results"),
                    "back to exactly one Analysis Results tab after redocking");
            }
        }

        // General invariant: no matter what sequence of registered top-level tabs get shown/hidden,
        // the strip must never carry the same TabPage object twice (which is what a raw
        // TabPages.Insert-without-Contains-check produces).
        [TestMethod]
        public void MainTabStrip_NeverContainsTheSameTabPageTwice()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                SetSolution(form);
                form.CreateControl();

                form.OnToggleBuildResultsDocking(form, EventArgs.Empty);
                form._panelMenuItemsByKey["buildResults"].PerformClick();
                form.OnToggleBuildResultsDocking(form, EventArgs.Empty);
                form.OnToggleAnalysisResultsDocking(form, EventArgs.Empty);
                form.OnToggleAnalysisResultsDocking(form, EventArgs.Empty);

                var seen = new System.Collections.Generic.HashSet<TabPage>();
                foreach (TabPage tp in form._mainTabControl.TabPages)
                    Assert.IsTrue(seen.Add(tp), "TabPage '" + tp.Text + "' appears more than once in the top-level strip");
            }
        }
    }
}
