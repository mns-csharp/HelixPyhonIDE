using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 257: the three Luna themes were only reachable via Preferences. A View ▸ Theme submenu now
    /// surfaces them for quick, discoverable switching. Guards that the submenu is built with the three
    /// themes and exactly one (the active one) checked.
    /// </summary>
    [TestClass]
    public class ThemeMenuTests
    {
        [TestMethod]
        public void ViewMenu_HasThemeSubmenu_ThreeThemes_ExactlyOneChecked()
        {
            using (var form = new MainForm())
            {
                // The submenu is normally added during full init; invoke the builder directly (it only
                // needs the designer's viewMenuItem, which exists after InitializeComponent).
                typeof(MainForm).GetMethod("EnsureThemeMenuItems", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(form, null);

                var view = (ToolStripMenuItem)typeof(MainForm)
                    .GetField("viewMenuItem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                Assert.IsNotNull(view, "viewMenuItem expected");

                ToolStripMenuItem theme = null;
                foreach (ToolStripItem it in view.DropDownItems)
                    if (it is ToolStripMenuItem mi && (mi.Text ?? "").Replace("&", "") == "Theme") { theme = mi; break; }
                Assert.IsNotNull(theme, "View should have a Theme submenu");

                Assert.AreEqual(3, theme.DropDownItems.Count, "three themes");
                var names = new List<string>();
                int checkedCount = 0;
                foreach (ToolStripMenuItem mi in theme.DropDownItems)
                {
                    names.Add(mi.Text);
                    if (mi.Checked) checkedCount++;
                }
                Assert.AreEqual(1, checkedCount, "exactly one theme (the active one) must be checked");
                CollectionAssert.Contains(names, "Luna Blue");
                CollectionAssert.Contains(names, "Bottle Green");
                CollectionAssert.Contains(names, "Crimson Red");
            }
        }
    }
}
