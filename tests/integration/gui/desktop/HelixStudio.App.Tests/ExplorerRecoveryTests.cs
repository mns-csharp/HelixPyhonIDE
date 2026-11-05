using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using HelixPythonNamespace.UI.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The Solution Explorer can always be got back.
    ///
    /// Reported from the running IDE: "solution explorer has vanished and I cannot recover it" —
    /// an empty left edge, a narrow strip against the window frame, and a View menu whose Panels
    /// list did not mention it. Two independent defects, either of which is enough to lose it:
    ///
    /// 1. Auto-hide saved the CURRENT splitter distance every time the pointer left the panel,
    ///    collapsed or not. The second firing recorded the ~26px collapsed strip as the panel's
    ///    width, and from then on every expand, redock and dock-side change restored a sliver.
    /// 2. The explorer only became a tool PANEL when something else claimed a tab — so with no
    ///    designer ever opened, View ▸ Panels had no entry for it and there was no menu route back.
    /// </summary>
    [TestClass]
    public class ExplorerRecoveryTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private static SplitContainer Splitter(MainForm form)
        {
            return (SplitContainer)typeof(MainForm).GetField("_leftSplitter", Private).GetValue(form);
        }

        private static DockablePanel Dock(MainForm form)
        {
            return (DockablePanel)typeof(MainForm).GetField("_leftDockPanel", Private).GetValue(form);
        }

        private static Coordinators.ExplorerCoordinator Explorer(MainForm form)
        {
            return (Coordinators.ExplorerCoordinator)typeof(MainForm)
                .GetField("_explorerCoordinator", Private).GetValue(form);
        }

        private static List<string> MenuTexts(ToolStripMenuItem root)
        {
            var found = new List<string>();
            Collect(root, found);
            return found;
        }

        private static void Collect(ToolStripItem item, List<string> into)
        {
            into.Add(item.Text);
            var menu = item as ToolStripMenuItem;
            if (menu == null) return;
            foreach (ToolStripItem child in menu.DropDownItems) Collect(child, into);
        }

        private static MainForm Shell()
        {
            MainForm form = TestMainForm.CreateComposed();
            form.ClientSize = new System.Drawing.Size(1280, 900);
            FixtureSolution.RealizeHeadless(form);
            TestMainForm.PumpUntilIdle(iterations: 4, delayMs: 20);
            return form;
        }

        /// <summary>Defect 2: with no designer ever opened, the menu still names it.</summary>
        [TestMethod]
        public void TheSolutionExplorerIsAToolPanelFromStartupAndHasAMenuEntry()
        {
            using (MainForm form = Shell())
            {
                CollectionAssert.Contains(form.ToolPanelsOnForTests(MainForm.ToolDockSide.Left), "Solution Explorer",
                    "the explorer is not a tool panel until a designer builds the group");
                CollectionAssert.Contains(MenuTexts(form.ViewMenuForTests), "Solution Explorer",
                    "View ▸ Panels has no entry for the Solution Explorer, so a user who loses it "
                    + "has no way to ask for it back");

                form.Close();
            }
        }

        /// <summary>
        /// Defect 1: collapsing twice must not teach the IDE that the panel is 26 pixels wide.
        /// </summary>
        [TestMethod]
        public void CollapsingTwiceDoesNotMakeTheRememberedWidthTheCollapsedStrip()
        {
            using (MainForm form = Shell())
            {
                SplitContainer splitter = Splitter(form);
                DockablePanel dock = Dock(form);
                splitter.SplitterDistance = 240;
                dock.IsPinned = false;

                // The pointer leaves twice — which is all it takes, and is what a user does.
                form.OnLeftPanelAutoHideForTests();
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);
                form.OnLeftPanelAutoHideForTests();
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);

                form.OnLeftPanelAutoShowForTests();
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);

                // Measured on the PANEL, not the splitter: auto-hide overlays now, so the pane it
                // came from stays a rail on purpose and the width that matters is the one the panel
                // comes out at. The defect this guards is unchanged — a remembered width that has
                // decayed to the collapsed strip brings the panel back as a sliver.
                Assert.IsTrue(dock.Width >= 160,
                    "the explorer came back " + dock.Width + "px wide, which is a sliver "
                    + "against the window edge and reads as a missing panel");

                form.Close();
            }
        }

        /// <summary>
        /// Whatever state it got into — floating, collapsed, or squeezed to nothing — one menu
        /// command brings it back to a state the user can work in.
        /// </summary>
        [TestMethod]
        public void DockExplorerLeftRecoversItFromEveryStateItCanGetInto()
        {
            using (MainForm form = Shell())
            {
                SplitContainer splitter = Splitter(form);
                DockablePanel dock = Dock(form);

                // Squeezed to nothing.
                splitter.SplitterDistance = 0;
                form.OnDockExplorerLeft(null, EventArgs.Empty);
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);
                Assert.IsTrue(splitter.SplitterDistance >= 160, "a zero-width explorer was not restored");

                // Collapsed and unpinned.
                dock.IsPinned = false;
                dock.IsExpanded = false;
                splitter.SplitterDistance = dock.CollapsedHeight;
                form.OnDockExplorerLeft(null, EventArgs.Empty);
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);
                Assert.IsTrue(dock.IsExpanded, "a collapsed explorer was not expanded");
                Assert.IsTrue(splitter.SplitterDistance >= 160, "a collapsed explorer was not widened");

                // Floating.
                form.OnFloatExplorer(null, EventArgs.Empty);
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);
                form.OnDockExplorerLeft(null, EventArgs.Empty);
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);
                Assert.IsTrue(splitter.Panel1.Controls.Contains(dock), "a floating explorer was not redocked");
                Assert.IsTrue(splitter.SplitterDistance >= 160, "a redocked explorer came back as a sliver");

                form.Close();
            }
        }

        /// <summary>
        /// And the Panels entry is a real route back, not just a label: reopening restores a dock
        /// the user can read the tree in.
        /// </summary>
        [TestMethod]
        public void ReopeningFromTheMenuRestoresAUsableDock()
        {
            using (MainForm form = Shell())
            {
                SplitContainer splitter = Splitter(form);

                form.HideToolPanel("Solution Explorer");
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);
                CollectionAssert.DoesNotContain(form.ToolPanelsOnForTests(MainForm.ToolDockSide.Left), "Solution Explorer");

                splitter.SplitterDistance = 4;            // and the dock was left squeezed as well
                form.ShowToolPanel("Solution Explorer");
                TestMainForm.PumpUntilIdle(iterations: 2, delayMs: 10);

                CollectionAssert.Contains(form.ToolPanelsOnForTests(MainForm.ToolDockSide.Left), "Solution Explorer");
                Assert.IsTrue(splitter.SplitterDistance >= 160,
                    "the panel came back into a dock too narrow to see it in");

                form.Close();
            }
        }
    }
}
