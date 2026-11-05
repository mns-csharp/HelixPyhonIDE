using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 293: a file node's right-click menu opened with "Copy Full Path" and had no "Open" — the
    /// primary action for a file. It now leads with a bold "Open" that raises FileDoubleClicked (the
    /// same path as double-click). Guards presence, enablement, and that it fires the open event.
    /// </summary>
    [TestClass]
    public class TreeFileOpenMenuTests
    {
        private static ContextMenuStrip BuildMenu(ProjectTreePanel panel, TreeNode node)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod(
                "BuildContextMenuForNode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "BuildContextMenuForNode expected");
            return (ContextMenuStrip)m.Invoke(panel, new object[] { node });
        }

        [TestMethod]
        public void FileNode_LeadsWithBoldOpen_ThatRaisesFileDoubleClicked()
        {
            using (var panel = new ProjectTreePanel())
            {
                var fg = new ProjectTreePanel.FileGroup { FilePath = @"C:\proj\main.py" };
                var node = new TreeNode("main.py") { Tag = fg };

                string opened = null;
                panel.FileDoubleClicked += (s, path) => opened = path;

                using (var menu = BuildMenu(panel, node))
                {
                    var first = menu.Items[0] as ToolStripMenuItem;
                    Assert.IsNotNull(first, "first item should be a menu item");
                    Assert.AreEqual("Open", first.Text, "a file node should lead with Open");
                    Assert.IsTrue(first.Enabled, "Open should be enabled for a real path");
                    Assert.IsTrue(first.Font.Bold, "the default verb should be bold");

                    first.PerformClick();
                    Assert.AreEqual(@"C:\proj\main.py", opened,
                        "Open must raise FileDoubleClicked with the file path");
                }
            }
        }

        [TestMethod]
        public void FileNode_Open_IsDisabled_WhenNoPath()
        {
            using (var panel = new ProjectTreePanel())
            {
                var node = new TreeNode("phantom") { Tag = new ProjectTreePanel.FileGroup { FilePath = "" } };
                using (var menu = BuildMenu(panel, node))
                {
                    var first = menu.Items[0] as ToolStripMenuItem;
                    Assert.AreEqual("Open", first.Text);
                    Assert.IsFalse(first.Enabled, "Open should be disabled when there is no file path");
                }
            }
        }
    }
}
