using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Coordinators;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Two reported defects in the editor tab strip, both traceable to one decision: the strip was
    /// TabSizeMode.Fixed, so every tab shared ONE width and the longest filename set it.
    ///
    ///   1. A 96-character test filename gave <c>animals.py</c> a 538px tab.
    ///   2. With those widths the strip overflows and scrolls; closing the long tab left the
    ///      remaining tabs scrolled out of view, so the strip looked empty.
    ///
    /// Driven on the real control with the real production sizing, so the numbers are the ones the
    /// IDE actually renders.
    /// </summary>
    [TestClass]
    public class EditorTabStripSizingTests
    {
        // The real fixture file whose name triggered the report.
        private const string LongName =
            "test_advanced______________________________________geometry_____________________________hello.py";

        private static TabControl NewStrip(Form host)
        {
            var tabs = new CompositedTabControl { Dock = DockStyle.Fill, Font = LunaFonts.Sized(-0.5f) };
            EditorCoordinator.ConfigureEditorTabStrip(tabs);
            host.Controls.Add(tabs);
            TestWindow.ShowOffScreen(host);
            return tabs;
        }

        private static Form HiddenHost()
        {
            // Realised but never on screen: -4000 keeps it off every monitor.
            return new Form
            {
                ClientSize = new Size(940, 300),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000),
                ShowInTaskbar = false,
            };
        }

        [TestMethod]
        public void AShortNamedTab_IsNotWidenedByALongNamedSibling()
        {
            using (Form host = HiddenHost())
            {
                TabControl tabs = NewStrip(host);
                tabs.TabPages.Add(new TabPage("animals.py"));
                EditorCoordinator.ApplyEditorTabSizing(tabs);
                Application.DoEvents();
                int aloneWidth = tabs.GetTabRect(0).Width;

                tabs.TabPages.Add(new TabPage(LongName));
                EditorCoordinator.ApplyEditorTabSizing(tabs);
                Application.DoEvents();

                int withSibling = tabs.GetTabRect(0).Width;
                Assert.AreEqual(aloneWidth, withSibling,
                    "opening a long-named file must not change the width of animals.py's tab");
                Assert.IsTrue(withSibling < 200,
                    "a 10-character name needs nowhere near 200px; got " + withSibling);
                Assert.IsTrue(tabs.GetTabRect(1).Width > withSibling,
                    "and the long name gets the wide tab, because it is the one that needs it");
            }
        }

        [TestMethod]
        public void EveryTab_IsWideEnoughForItsOwnLabelAndCloseIcon()
        {
            using (Form host = HiddenHost())
            {
                TabControl tabs = NewStrip(host);
                foreach (string name in new[] { "a.py", "animals.py", "some_longer_module_name.py", LongName })
                    tabs.TabPages.Add(new TabPage(name));
                EditorCoordinator.ApplyEditorTabSizing(tabs);
                Application.DoEvents();

                using (Graphics g = tabs.CreateGraphics())
                {
                    for (int i = 0; i < tabs.TabPages.Count; i++)
                    {
                        int needed = EditorCoordinator.RequiredEditorTabWidth(g, tabs.TabPages[i].Text, tabs.Font);
                        int have = tabs.GetTabRect(i).Width;

                        // Not an exact-fit assertion, deliberately. WinForms sizes each tab from
                        // its OWN measurement of the label, which runs a few pixels short of
                        // TextRenderer's and diverges further as the label grows; the painter's
                        // EndEllipsis is what makes that safe. What must hold is that a tab is
                        // never short by more than that measurement gap — anything worse means the
                        // per-tab padding is wrong, or the strip went back to one shared width.
                        Assert.IsTrue(have >= needed - EditorCoordinator.TabMeasurementAllowance,
                            "tab " + i + " ('" + tabs.TabPages[i].Text + "') is " + have
                            + "px for a label needing " + needed + "px");
                    }
                }
            }
        }

        [TestMethod]
        public void ClosingTheLongTabWhileScrolled_LeavesTheRemainingTabsOnScreen()
        {
            using (Form host = HiddenHost())
            {
                TabControl tabs = NewStrip(host);
                tabs.TabPages.Add(new TabPage("animals.py"));
                tabs.TabPages.Add(new TabPage(LongName));
                tabs.TabPages.Add(new TabPage("errors.py"));
                EditorCoordinator.ApplyEditorTabSizing(tabs);
                Application.DoEvents();

                // Select the last tab: with a very wide sibling the strip scrolls to reach it.
                tabs.SelectedIndex = tabs.TabPages.Count - 1;
                Application.DoEvents();

                TabPage longTab = tabs.TabPages[1];
                tabs.TabPages.Remove(longTab);
                longTab.Dispose();
                EditorCoordinator.ApplyEditorTabSizing(tabs);
                Application.DoEvents();

                for (int i = 0; i < tabs.TabPages.Count; i++)
                {
                    Rectangle r = tabs.GetTabRect(i);
                    Assert.IsTrue(r.Right > 0 && r.Left < tabs.ClientSize.Width,
                        "tab '" + tabs.TabPages[i].Text + "' is off the strip after the close: " + r);
                }
            }
        }

        /// <summary>
        /// The strip must never be left scrolled past its own content — that is what made it look
        /// empty. Checked directly: the first tab starts at the left edge once everything fits.
        /// </summary>
        [TestMethod]
        public void AfterAClose_TheStripIsScrolledBackToTheStartWhenEverythingFits()
        {
            using (Form host = HiddenHost())
            {
                TabControl tabs = NewStrip(host);
                tabs.TabPages.Add(new TabPage("animals.py"));
                tabs.TabPages.Add(new TabPage(LongName));
                EditorCoordinator.ApplyEditorTabSizing(tabs);
                Application.DoEvents();
                tabs.SelectedIndex = 1;
                Application.DoEvents();

                TabPage longTab = tabs.TabPages[1];
                tabs.TabPages.Remove(longTab);
                longTab.Dispose();
                EditorCoordinator.ApplyEditorTabSizing(tabs);
                Application.DoEvents();

                Assert.IsTrue(tabs.GetTabRect(0).Left >= 0,
                    "the one remaining tab must be back at the left edge, not scrolled off it: "
                    + tabs.GetTabRect(0));
            }
        }
    }
}
