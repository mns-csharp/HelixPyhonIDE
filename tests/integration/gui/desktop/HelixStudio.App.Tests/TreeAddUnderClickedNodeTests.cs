using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// SRS §5.7 (context-menu bug): Add Class / Add Interface must create the element under the
    /// EXACT node the command was invoked from. The file-node menu used to re-target node.Parent
    /// (the project), so the new type landed in the project's default package regardless of which
    /// file was clicked. It now passes the FileGroup itself, and the host resolves the clicked
    /// file's own package (MainForm.TryResolveAddTypeTarget).
    /// </summary>
    [TestClass]
    public class TreeAddUnderClickedNodeTests
    {
        private static ContextMenuStrip BuildMenu(ProjectTreePanel panel, TreeNode node)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod(
                "BuildContextMenuForNode", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "BuildContextMenuForNode expected");
            return (ContextMenuStrip)m.Invoke(panel, new object[] { node });
        }

        private static ToolStripMenuItem FindItem(ContextMenuStrip menu, string text)
        {
            foreach (ToolStripItem item in menu.Items)
            {
                var mi = item as ToolStripMenuItem;
                if (mi != null && mi.Text == text) return mi;
            }
            return null;
        }

        private static ProjectTreePanel.FileGroup FileGroupUnderProject(out ProjectModel project)
        {
            var sln = new SolutionModel(@"D:\r\sol.toml", "sol");
            project = new ProjectModel(@"D:\r\app\pyproject.toml", "app", sln);
            return new ProjectTreePanel.FileGroup
            {
                Project = project,
                FilePath = @"D:\r\app\src\pkg\mod.py",
            };
        }

        [TestMethod]
        public void FileNode_AddClass_RaisesWithTheFileGroupItself()
        {
            using (var panel = new ProjectTreePanel())
            {
                ProjectModel project;
                var fg = FileGroupUnderProject(out project);
                var node = new TreeNode("mod.py") { Tag = fg };

                object received = null;
                panel.AddClassRequested += (s, e) => received = e.Item;

                using (var menu = BuildMenu(panel, node))
                {
                    var add = FindItem(menu, "Add Class");
                    Assert.IsNotNull(add, "a file node must offer Add Class");
                    add.PerformClick();
                }

                Assert.AreSame(fg, received,
                    "Add Class must pass the CLICKED file node's FileGroup, not the parent project");
            }
        }

        [TestMethod]
        public void FileNode_AddInterface_RaisesWithTheFileGroupItself()
        {
            using (var panel = new ProjectTreePanel())
            {
                ProjectModel project;
                var fg = FileGroupUnderProject(out project);
                var node = new TreeNode("mod.py") { Tag = fg };

                object received = null;
                panel.AddInterfaceRequested += (s, e) => received = e.Item;

                using (var menu = BuildMenu(panel, node))
                {
                    var add = FindItem(menu, "Add Interface");
                    Assert.IsNotNull(add, "a file node must offer Add Interface");
                    add.PerformClick();
                }

                Assert.AreSame(fg, received,
                    "Add Interface must pass the CLICKED file node's FileGroup, not the parent project");
            }
        }

        [TestMethod]
        public void ResolveTarget_ProjectNode_UsesDefaultNamespace()
        {
            var sln = new SolutionModel(@"D:\r\sol.toml", "sol");
            var project = new ProjectModel(@"D:\r\app\pyproject.toml", "app", sln);

            ProjectModel resolved;
            string ns;
            Assert.IsTrue(MainForm.TryResolveAddTypeTarget(project, out resolved, out ns));
            Assert.AreSame(project, resolved);
            Assert.IsNull(ns, "a project node keeps the project's default namespace (ns null)");
        }

        [TestMethod]
        public void ResolveTarget_FileNode_UsesTheFilesOwnPackage()
        {
            ProjectModel project;
            var fg = FileGroupUnderProject(out project);

            ProjectModel resolved;
            string ns;
            Assert.IsTrue(MainForm.TryResolveAddTypeTarget(fg, out resolved, out ns));
            Assert.AreSame(project, resolved);
            Assert.AreEqual("pkg", ns, "the new type joins the clicked file's package");
        }

        [TestMethod]
        public void ResolveTarget_UnknownItem_Fails()
        {
            ProjectModel resolved;
            string ns;
            Assert.IsFalse(MainForm.TryResolveAddTypeTarget("not-a-node-tag", out resolved, out ns));
            Assert.IsFalse(MainForm.TryResolveAddTypeTarget(null, out resolved, out ns));
        }
    }
}
