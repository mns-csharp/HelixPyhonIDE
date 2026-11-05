using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 255: the Solution Explorer supports F2 = rename and Del = delete, but the context menu did
    /// not advertise those shortcuts. This guards that Rename shows "F2" and Delete shows "Del" so the
    /// hint stays in sync with the (separately tested) keyboard handling.
    /// </summary>
    [TestClass]
    public class TreeContextMenuShortcutTests
    {
        private static ContextMenuStrip Build(ProjectTreePanel panel, TreeNode node)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod(
                "BuildContextMenuForNode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "BuildContextMenuForNode expected");
            return (ContextMenuStrip)m.Invoke(panel, new object[] { node });
        }

        private static string Hint(ContextMenuStrip menu, string text)
        {
            foreach (ToolStripItem it in menu.Items)
                if (it is ToolStripMenuItem mi && mi.Text == text) return mi.ShortcutKeyDisplayString;
            return "<no such item>";
        }

        [TestMethod]
        public void ProjectNode_AdvertisesF2Rename_AndDelDelete()
        {
            using (var panel = new ProjectTreePanel())
            {
                var sol = new SolutionModel(@"C:\proj\pyproject.toml", "Proj");
                var proj = new ProjectModel(@"C:\proj\pyproject.toml", "Proj", sol);
                var node = new TreeNode("Proj") { Tag = proj };

                ContextMenuStrip menu = Build(panel, node);
                Assert.AreEqual("F2", Hint(menu, "Rename"), "Rename must advertise F2");
                Assert.AreEqual("Del", Hint(menu, "Delete from Disk"), "Delete from Disk must advertise Del");
            }
        }

        [TestMethod]
        public void TypeNode_AdvertisesF2Rename_AndDelDelete()
        {
            using (var panel = new ProjectTreePanel())
            {
                var sol = new SolutionModel(@"C:\proj\pyproject.toml", "Proj");
                var proj = new ProjectModel(@"C:\proj\pyproject.toml", "Proj", sol);
                var type = new TypeModel("Widget", "app", TypeKind.ConcreteClass, proj);
                var node = new TreeNode("Widget") { Tag = type };

                ContextMenuStrip menu = Build(panel, node);
                Assert.AreEqual("F2", Hint(menu, "Rename"), "Rename must advertise F2");
                Assert.AreEqual("Del", Hint(menu, "Delete"), "Delete must advertise Del");
            }
        }
    }
}
