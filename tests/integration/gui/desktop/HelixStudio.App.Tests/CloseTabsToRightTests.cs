using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Coordinators;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 461 (editor tab workflow): "Close Tabs to the Right" trims the tab strip back to the
    /// clicked tab — a VS / VS Code staple. Only unpinned tabs positioned AFTER the anchor are
    /// victims; pinned tabs and everything to the left survive.
    /// </summary>
    [TestClass]
    public class CloseTabsToRightTests
    {
        private static TabControl RealizedTabs(out List<EditorTabPage> pages, params string[] names)
        {
            var tabs = new TabControl();
            EditorCoordinator.EnsureRealized(tabs); // force the native handle so Insert/Add sticks
            pages = new List<EditorTabPage>();
            foreach (string n in names)
            {
                var p = new EditorTabPage { Text = n };
                tabs.TabPages.Add(p);
                pages.Add(p);
            }
            return tabs;
        }

        [TestMethod]
        public void ClosesEveryUnpinnedTabAfterTheAnchor()
        {
            List<EditorTabPage> p;
            using (TabControl tabs = RealizedTabs(out p, "a", "b", "c", "d"))
            {
                var victims = EditorCoordinator.TabsToRight(tabs, p[1]); // anchor = "b"
                CollectionAssert.AreEquivalent(new[] { p[2], p[3] }, victims,
                    "tabs c and d (to the right of b) are the victims");
                CollectionAssert.DoesNotContain(victims, p[0], "the tab to the LEFT must survive");
                CollectionAssert.DoesNotContain(victims, p[1], "the anchor itself must survive");
            }
        }

        [TestMethod]
        public void PinnedTabsToTheRightSurvive()
        {
            List<EditorTabPage> p;
            using (TabControl tabs = RealizedTabs(out p, "a", "b", "c", "d"))
            {
                p[2].IsPinned = true; // pin "c"
                var victims = EditorCoordinator.TabsToRight(tabs, p[0]); // anchor = "a"
                CollectionAssert.Contains(victims, p[1], "b is an unpinned tab to the right");
                CollectionAssert.Contains(victims, p[3], "d is an unpinned tab to the right");
                CollectionAssert.DoesNotContain(victims, p[2], "the pinned tab c must survive");
            }
        }

        [TestMethod]
        public void RightmostAnchor_HasNoVictims()
        {
            List<EditorTabPage> p;
            using (TabControl tabs = RealizedTabs(out p, "a", "b", "c"))
            {
                Assert.AreEqual(0, EditorCoordinator.TabsToRight(tabs, p[2]).Count,
                    "the rightmost tab has nothing to its right — the menu item should be disabled");
            }
        }
    }
}
