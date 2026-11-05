using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 445: View ▸ Sync with Active Document is a checkable toggle that turns the Solution
    /// Explorer's track-selection on/off. It defaults on; unchecking it sets _syncTreeWithEditor false
    /// so SyncTreeToActiveDocument becomes a no-op.
    /// </summary>
    [TestClass]
    public class SyncDocumentMenuTests
    {
        private static ToolStripMenuItem SyncItem(MainForm form)
        {
            typeof(MainForm).GetMethod("EnsureSyncDocMenuItem", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(form, null);
            return (ToolStripMenuItem)typeof(MainForm)
                .GetField("_syncDocMenuItem", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }

        private static bool SyncFlag(MainForm form)
        {
            return (bool)typeof(MainForm)
                .GetField("_syncTreeWithEditor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }

        [TestMethod]
        public void ViewMenu_HasCheckedSyncToggle_ByDefault()
        {
            using (var form = new MainForm())
            {
                ToolStripMenuItem item = SyncItem(form);
                Assert.IsNotNull(item, "Sync with Active Document menu item expected");
                Assert.IsTrue(item.Checked, "on by default");
                Assert.IsTrue(item.CheckOnClick, "it is a toggle");
                Assert.IsTrue(SyncFlag(form), "the backing flag is on");
            }
        }

        [TestMethod]
        public void Unchecking_DisablesTracking()
        {
            using (var form = new MainForm())
            {
                ToolStripMenuItem item = SyncItem(form);
                item.Checked = false;   // raises CheckedChanged
                Assert.IsFalse(SyncFlag(form), "tracking is off after unchecking");
                item.Checked = true;
                Assert.IsTrue(SyncFlag(form), "tracking is on again");
            }
        }
    }
}
