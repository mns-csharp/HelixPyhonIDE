using System;
using System.Linq;
using System.Windows.Forms;

using HelixPythonNamespace.UI.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The AI Terminal's three header buttons.
    ///
    /// They were inert on arrival: <see cref="DockablePanel"/> raises CollapseStateChanged /
    /// FloatRequested / CloseRequested but takes no action itself, and nothing was subscribed — so
    /// all three looked broken. These pin the contract:
    ///
    /// <list type="bullet">
    /// <item><b>Maximize</b> — becomes a top-level tab, and the button greys out once there.</item>
    /// <item><b>Minimize</b> — one step smaller: Maximized → Docked, Docked → Collapsed. Never
    /// Maximized → Collapsed, which would hide the console the user was just working in.</item>
    /// <item><b>Close</b> — hides it; View ▸ AI Terminal is the way back.</item>
    /// </list>
    ///
    /// Driven through the real header buttons rather than the state methods, so a regression that
    /// unsubscribes an event fails here.
    /// </summary>
    [TestClass]
    public class AiTerminalPlacementTests
    {
        /// <summary>
        /// Click one of the dock's header buttons, identified by its private field name.
        ///
        /// By field rather than by tooltip because the collapse button has none. Going through the
        /// actual <see cref="Button"/> is the point: calling MainForm's handler directly would
        /// still pass even with the events unsubscribed, which is precisely the bug these exist to
        /// catch.
        /// </summary>
        private static void ClickHeaderButton(DockablePanel dock, string fieldName)
        {
            System.Reflection.FieldInfo field = typeof(DockablePanel).GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, "DockablePanel has no field " + fieldName);
            var button = field.GetValue(dock) as Button;
            Assert.IsNotNull(button, fieldName + " is not a Button");
            button.PerformClick();
            Application.DoEvents();
        }

        private const string MinimizeButton = "_collapseButton";
        private const string MaximizeButton = "_floatButton";
        private const string CloseButton = "_closeButton";

        /// <summary>
        /// A composed, REALIZED MainForm.
        ///
        /// <c>Show()</c> matters: <c>Button.PerformClick()</c> is a no-op while the control cannot
        /// be selected, and a control on a form that was never shown reports Visible == false — so
        /// a test that only calls CreateControl() clicks nothing and then asserts against an
        /// unchanged state. Positioned off every monitor so it never appears on the desktop.
        /// </summary>
        private static MainForm Composed()
        {
            MainForm form = TestMainForm.CreateComposed();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-4000, -4000);
            form.Size = new System.Drawing.Size(1280, 900);
            TestWindow.ShowOffScreen(form);
            Application.DoEvents();
            // This suite is about PLACEMENT -- maximize, collapse, the tick that follows them --
            // and every one of those starts from a terminal that is on screen. While
            // MainForm.AiTerminalSuppressed holds the panel back at startup, open it first, the way
            // a user now would. The startup default itself is owned by
            // AiTerminalVisibilityPersistenceTests, which asserts the suppression contract directly;
            // duplicating it here would make eight placement tests fail for a reason that has
            // nothing to do with placement.
            if (MainForm.AiTerminalSuppressed)
            {
                form.ShowAiTerminal();
                Application.DoEvents();
            }
            return form;
        }

        // ---- maximize -------------------------------------------------------------

        [TestMethod]
        public void Maximize_MovesTheTerminalIntoATopLevelTab()
        {
            using (MainForm form = Composed())
            {
                ClickHeaderButton(form._aiTerminalDock, MaximizeButton);

                Assert.AreEqual(MainForm.AiTerminalPlacement.Maximized, form.AiTerminalState);
                Assert.IsNotNull(form._aiTerminalTab, "a tab is created for it");
                Assert.AreSame(form._aiTerminalTab, form._mainTabControl.SelectedTab,
                    "and selected, or maximizing would appear to do nothing");
                Assert.IsTrue(form._aiTerminalTab.Controls.Contains(form._aiTerminalDock),
                    "the whole dock moves — its header carries the way back");
                Assert.IsTrue(form._editorAiSplitter.Panel2Collapsed,
                    "the bottom pane is given back to the editor");
            }
        }

        [TestMethod]
        public void WhileMaximized_TheMaximizeButtonIsDisabled()
        {
            using (MainForm form = Composed())
            {
                Assert.IsTrue(form._aiTerminalDock.FloatButtonEnabled, "enabled while docked");
                ClickHeaderButton(form._aiTerminalDock, MaximizeButton);
                Assert.IsFalse(form._aiTerminalDock.FloatButtonEnabled,
                    "a lit button that does nothing reads as broken");
            }
        }

        [TestMethod]
        public void Maximize_Twice_IsANoOp()
        {
            using (MainForm form = Composed())
            {
                ClickHeaderButton(form._aiTerminalDock, MaximizeButton);
                TabPage first = form._aiTerminalTab;
                form._aiTerminalDock.FloatButtonEnabled = true;   // simulate a stray re-raise
                ClickHeaderButton(form._aiTerminalDock, MaximizeButton);
                Assert.AreSame(first, form._aiTerminalTab, "no second tab is created");
            }
        }

        // ---- minimize --------------------------------------------------------------

        [TestMethod]
        public void Minimize_WhileDocked_CollapsesToTheTitleBar()
        {
            using (MainForm form = Composed())
            {
                int before = form._editorAiSplitter.Panel2.Height;
                ClickHeaderButton(form._aiTerminalDock, MinimizeButton);

                Assert.AreEqual(MainForm.AiTerminalPlacement.Collapsed, form.AiTerminalState);
                Assert.IsTrue(form._editorAiSplitter.Panel2.Height < before,
                    "the editor gets the height back");
                Assert.IsTrue(form._editorAiSplitter.Panel2.Height <= form._aiTerminalDock.CollapsedHeight + 8,
                    "only the title bar remains");
            }
        }

        [TestMethod]
        public void Minimize_WhileMaximized_ReturnsToDocked_NotCollapsed()
        {
            // The specified behaviour: from full screen the useful step is back to the dock.
            // Collapsing would hide the console the user was just working in.
            using (MainForm form = Composed())
            {
                ClickHeaderButton(form._aiTerminalDock, MaximizeButton);
                Assert.AreEqual(MainForm.AiTerminalPlacement.Maximized, form.AiTerminalState);

                ClickHeaderButton(form._aiTerminalDock, MinimizeButton);

                Assert.AreEqual(MainForm.AiTerminalPlacement.Docked, form.AiTerminalState);
                Assert.IsNull(form._aiTerminalTab, "the tab is gone");
                Assert.IsTrue(form._editorAiSplitter.Panel2.Controls.Contains(form._aiTerminalDock),
                    "it is back in the bottom pane");
                Assert.IsFalse(form._editorAiSplitter.Panel2Collapsed);
                Assert.IsTrue(form._aiTerminalDock.IsExpanded,
                    "restored showing its content, not collapsed");
                Assert.IsTrue(form._aiTerminalDock.FloatButtonEnabled,
                    "maximize becomes available again");
            }
        }

        [TestMethod]
        public void Minimize_FromCollapsed_ExpandsBackToDocked()
        {
            using (MainForm form = Composed())
            {
                ClickHeaderButton(form._aiTerminalDock, MinimizeButton);
                Assert.AreEqual(MainForm.AiTerminalPlacement.Collapsed, form.AiTerminalState);

                ClickHeaderButton(form._aiTerminalDock, MinimizeButton);   // the button toggles
                Assert.AreEqual(MainForm.AiTerminalPlacement.Docked, form.AiTerminalState);
                Assert.IsTrue(form._editorAiSplitter.Panel2.Height > form._aiTerminalDock.CollapsedHeight + 8);
            }
        }

        // ---- close ------------------------------------------------------------------

        [TestMethod]
        public void Close_HidesTheTerminal()
        {
            using (MainForm form = Composed())
            {
                ClickHeaderButton(form._aiTerminalDock, CloseButton);
                Assert.IsTrue(form._editorAiSplitter.Panel2Collapsed, "the pane is gone");
                Assert.IsTrue(form._aiTerminalHidden);
            }
        }

        [TestMethod]
        public void Close_WhileMaximized_AlsoRemovesTheTab()
        {
            // Otherwise closing from the maximized state would leave an orphaned empty tab.
            using (MainForm form = Composed())
            {
                ClickHeaderButton(form._aiTerminalDock, MaximizeButton);
                ClickHeaderButton(form._aiTerminalDock, CloseButton);

                Assert.IsNull(form._aiTerminalTab);
                Assert.IsFalse(form._mainTabControl.TabPages.Cast<TabPage>()
                    .Any(p => p.Text.IndexOf("AI Terminal", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.IsTrue(form._editorAiSplitter.Panel2Collapsed);
            }
        }

        [TestMethod]
        public void ViewMenu_BringsItBackAfterClose()
        {
            // Close must not be a one-way door costing the user a restart.
            using (MainForm form = Composed())
            {
                ClickHeaderButton(form._aiTerminalDock, CloseButton);
                Assert.IsTrue(form._editorAiSplitter.Panel2Collapsed);

                Assert.IsNotNull(form._aiTerminalMenuItem, "View ▸ AI Terminal must exist");
                form._aiTerminalMenuItem.PerformClick();
                Application.DoEvents();

                Assert.IsFalse(form._editorAiSplitter.Panel2Collapsed, "it is showing again");
                Assert.AreEqual(MainForm.AiTerminalPlacement.Docked, form.AiTerminalState);
                Assert.IsFalse(form._aiTerminalHidden);
            }
        }
        // ---- surviving the explorer's re-parenting -----------------------------------

        [TestMethod]
        public void TheTerminalSurvivesAnExplorerDockChange()
        {
            // ExplorerCoordinator CLEARS both _leftSplitter panels and repopulates them on every
            // dock/undock/float/restore — which happens on solution open. It used to re-add
            // _mainTabControl directly, evicting the editor/AI splitter from the control tree: the
            // terminal vanished and View > AI Terminal then acted on a detached splitter and did
            // nothing. It must place MainForm.EditorHost instead.
            using (MainForm form = Composed())
            {
                DockExplorer(form, "OnDockExplorerRight");
                Application.DoEvents();
                Assert.IsTrue(ContainsDeep(form._leftSplitter, form._editorAiSplitter),
                    "the editor/AI splitter must still be in the tree after docking right");
                Assert.IsTrue(ContainsDeep(form._leftSplitter, form._aiTerminalDock),
                    "and the terminal with it");

                DockExplorer(form, "OnDockExplorerLeft");
                Application.DoEvents();
                Assert.IsTrue(ContainsDeep(form._leftSplitter, form._editorAiSplitter),
                    "and again after docking back left");
                Assert.IsTrue(ContainsDeep(form._leftSplitter, form._aiTerminalPanel),
                    "the console itself is still reachable");
            }
        }

        [TestMethod]
        public void ViewMenu_StillWorksAfterAnExplorerDockChange()
        {
            // The reported symptom: the menu item existed and clicking it did nothing.
            using (MainForm form = Composed())
            {
                DockExplorer(form, "OnDockExplorerRight");
                DockExplorer(form, "OnDockExplorerLeft");
                Application.DoEvents();

                ClickHeaderButton(form._aiTerminalDock, CloseButton);
                Assert.IsTrue(form._editorAiSplitter.Panel2Collapsed);

                form._aiTerminalMenuItem.PerformClick();
                Application.DoEvents();

                Assert.IsFalse(form._editorAiSplitter.Panel2Collapsed, "the menu reopens it");
                Assert.IsTrue(ContainsDeep(form, form._aiTerminalPanel),
                    "and it is actually on screen, not on a detached splitter");
            }
        }

        /// <summary>
        /// Drive the real ExplorerCoordinator handler (an instance event handler on MainForm's
        /// private coordinator), so the test exercises the same re-parenting the app performs.
        /// </summary>
        private static void DockExplorer(MainForm form, string handlerName)
        {
            object coordinator = typeof(MainForm)
                .GetField("_explorerCoordinator",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(form);
            Assert.IsNotNull(coordinator, "MainForm._explorerCoordinator expected");
            coordinator.GetType().GetMethod(handlerName)
                .Invoke(coordinator, new object[] { form, EventArgs.Empty });
            Application.DoEvents();
        }

        // ---- the View menu item is a checked toggle ----------------------------------

        [TestMethod]
        public void TheMenuItemIsTicked_WhileTheTerminalIsShowing()
        {
            // Its neighbours in the View menu are checked toggles; an unticked item next to them
            // reads as "off" even with the panel plainly on screen.
            using (MainForm form = Composed())
            {
                Assert.IsTrue(form._aiTerminalMenuItem.Checked,
                    "the terminal is docked and visible, so the item is ticked");
            }
        }

        [TestMethod]
        public void TheTickClears_WhenTheTerminalIsClosed()
        {
            using (MainForm form = Composed())
            {
                ClickHeaderButton(form._aiTerminalDock, CloseButton);
                Assert.IsFalse(form._aiTerminalMenuItem.Checked,
                    "the header's X must move the tick too, not just the menu");
            }
        }

        [TestMethod]
        public void TheMenuItemToggles_RatherThanOnlyShowing()
        {
            // A ticked item that only ever shows is a lie: clicking it must also hide.
            using (MainForm form = Composed())
            {
                form._aiTerminalMenuItem.PerformClick();
                Application.DoEvents();
                Assert.IsFalse(form.IsAiTerminalVisible, "clicking while ticked hides it");
                Assert.IsFalse(form._aiTerminalMenuItem.Checked);

                form._aiTerminalMenuItem.PerformClick();
                Application.DoEvents();
                Assert.IsTrue(form.IsAiTerminalVisible, "clicking again brings it back");
                Assert.IsTrue(form._aiTerminalMenuItem.Checked);
                Assert.IsFalse(form._editorAiSplitter.Panel2Collapsed);
            }
        }

        [TestMethod]
        public void TheTickStaysSet_WhileMaximizedOrCollapsed()
        {
            // Both are still "showing" — collapsed leaves the header on screen, maximized fills a
            // tab. Only Close clears the tick.
            using (MainForm form = Composed())
            {
                ClickHeaderButton(form._aiTerminalDock, MaximizeButton);
                Assert.IsTrue(form._aiTerminalMenuItem.Checked, "maximized is still showing");

                ClickHeaderButton(form._aiTerminalDock, MinimizeButton);   // back to docked
                ClickHeaderButton(form._aiTerminalDock, MinimizeButton);   // collapse
                Assert.AreEqual(MainForm.AiTerminalPlacement.Collapsed, form.AiTerminalState);
                Assert.IsTrue(form._aiTerminalMenuItem.Checked, "collapsed is still showing");
            }
        }

        private static bool ContainsDeep(Control root, Control target)
        {
            if (root == null || target == null) return false;
            foreach (Control child in root.Controls)
            {
                if (ReferenceEquals(child, target)) return true;
                if (ContainsDeep(child, target)) return true;
            }
            return false;
        }
    }
}
