using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 369: References and Call Hierarchy became top-level tabs (iters 367/368) but were not on
    /// the View ▸ Panels menu, so a user who closed one had no way to bring it back. Every togglable
    /// result tab must have a Panels-menu entry, tagged with its tab key (OnPanelMenuItemClick toggles
    /// by Tag). This pins the designer menu's inventory so a new tab isn't shipped without its toggle.
    /// </summary>
    [TestClass]
    public class PanelMenuInventoryTests
    {
        private static HashSet<string> PanelMenuTags(MainForm form)
        {
            ToolStripMenuItem panels = (ToolStripMenuItem)typeof(MainForm)
                .GetField("panelsMenuItem", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(form);
            Assert.IsNotNull(panels, "the designer View ▸ Panels menu item must exist");

            var tags = new HashSet<string>();
            foreach (ToolStripItem it in panels.DropDownItems)
                if (it.Tag is string s) tags.Add(s);
            return tags;
        }

        [TestMethod]
        public void EveryResultTab_HasAPanelsMenuToggle()
        {
            using (var form = new MainForm())
            {
                HashSet<string> tags = PanelMenuTags(form);
                string[] required =
                {
                    "problems", "analysis", "buildResults", "lint", "deadcode",
                    "metrics", "profile", "tests", "references", "callHierarchy",
                };
                foreach (string key in required)
                    Assert.IsTrue(tags.Contains(key),
                        "View ▸ Panels is missing a toggle tagged '" + key + "'");
            }
        }

        [TestMethod]
        public void ReferencesAndCallHierarchy_AreOnThePanelsMenu()
        {
            using (var form = new MainForm())
            {
                HashSet<string> tags = PanelMenuTags(form);
                Assert.IsTrue(tags.Contains("references"), "References toggle missing");
                Assert.IsTrue(tags.Contains("callHierarchy"), "Call Hierarchy toggle missing");
            }
        }
    }
}
