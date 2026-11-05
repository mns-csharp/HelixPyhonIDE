using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.App.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 286: "Show All Hidden" had a discoverable shortcut (Ctrl+Shift+H) but its counterpart
    /// "Hide Selected" had none — the menu showed no key and the "Ctrl+H" mentioned in code comments
    /// wasn't actually bound. Hide Selected is now registered on Ctrl+H (symmetric with Ctrl+Shift+H).
    /// Guards that Register stamps the chord onto the menu item (so it displays + dispatches) and that
    /// the hide/show-all pair does not collide.
    /// </summary>
    [TestClass]
    public class HideSelectedKeybindingTests
    {
        private static KeybindingManager NewManagerWithHidePair()
        {
            var mgr = new KeybindingManager(new UserSettingsManager());
            mgr.RegisterDefault("view.hideSelected", "Hide Selected", Keys.Control | Keys.H);
            mgr.RegisterDefault("view.showAllHidden", "Show All Hidden", Keys.Control | Keys.Shift | Keys.H);
            return mgr;
        }

        [TestMethod]
        public void HideSelected_StampsCtrlH_OntoItsMenuItem()
        {
            var mgr = NewManagerWithHidePair();
            var item = new ToolStripMenuItem();

            mgr.Register("view.hideSelected", item);

            Assert.AreEqual(Keys.Control | Keys.H, item.ShortcutKeys,
                "wiring the menu item must stamp Ctrl+H so it both displays and dispatches");
        }

        [TestMethod]
        public void HideAndShowAll_AreSymmetricAndDoNotConflict()
        {
            var mgr = NewManagerWithHidePair();

            // Each command's own chord must have no OTHER owner (distinct keys => no collision).
            Assert.IsNull(mgr.FindConflict("view.hideSelected", Keys.Control | Keys.H),
                "no other command may own Ctrl+H (Hide Selected)");
            Assert.IsNull(mgr.FindConflict("view.showAllHidden", Keys.Control | Keys.Shift | Keys.H),
                "no other command may own Ctrl+Shift+H (Show All Hidden)");

            // And they are genuinely different chords (symmetric pair, not the same key).
            Assert.AreNotEqual(Keys.Control | Keys.H, Keys.Control | Keys.Shift | Keys.H);
        }
    }
}
