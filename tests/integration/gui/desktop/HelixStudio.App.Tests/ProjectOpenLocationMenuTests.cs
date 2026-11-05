using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// "Open the package/project directory location" on a project node.
    ///
    /// The verb hands off to Explorer, which a test cannot follow, so what is pinned here is
    /// everything on this side of that boundary: the item exists on a project node, it is enabled
    /// only when there is a directory to open, it resolves to the PACKAGE's own folder rather than
    /// the solution's, and it does not sit directly beneath "Delete from Disk" where a mis-click
    /// would be expensive.
    ///
    /// The fixture deliberately puts the member in its own child directory — the shape PY8004
    /// requires of every workspace member — because a fixture whose project manifest sits at the
    /// solution root cannot tell the two directories apart, and would pass whichever one the code
    /// resolved.
    /// </summary>
    [TestClass]
    public class ProjectOpenLocationMenuTests
    {
        /// <summary>
        /// The same label the solution node uses. The node that was right-clicked is what makes
        /// "containing" unambiguous, so the assertions below check the RESOLVED DIRECTORY rather
        /// than trusting the text to distinguish them.
        /// </summary>
        private const string ItemText = "Open Containing Folder";
        private const string SolutionRoot = @"D:\repos\ws";
        private const string PackageDirectory = SolutionRoot + @"\mypkg";

        private static ProjectTreePanel Load()
        {
            var solution = new SolutionModel(SolutionRoot + @"\pyproject.toml", "ws");
            var project = new ProjectModel(PackageDirectory + @"\pyproject.toml", "mypkg", solution);
            solution.Projects.Add(project);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(solution);
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            System.Reflection.FieldInfo field = typeof(ProjectTreePanel).GetField("_treeView",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, "ProjectTreePanel no longer has a _treeView field");
            return (TreeView)field.GetValue(panel);
        }

        private static TreeNode ProjectNode(ProjectTreePanel panel)
        {
            TreeView tree = Tree(panel);
            Assert.IsTrue(tree.Nodes.Count > 0 && tree.Nodes[0].Nodes.Count > 0, "the tree has no project node");
            return tree.Nodes[0].Nodes[0];
        }

        [TestMethod]
        public void AProjectNode_OffersTheVerb()
        {
            using (ProjectTreePanel panel = Load())
            using (ContextMenuStrip menu = panel.BuildContextMenuForNode(ProjectNode(panel)))
            {
                CollectionAssert.Contains(Texts(menu), ItemText);
            }
        }

        [TestMethod]
        public void TheVerbIsEnabled_AndPointsAtThePackageFolder_NotTheSolutionFolder()
        {
            using (ProjectTreePanel panel = Load())
            using (ContextMenuStrip menu = panel.BuildContextMenuForNode(ProjectNode(panel)))
            {
                ToolStripItem item = Item(menu, ItemText);
                Assert.IsTrue(item.Enabled, "a project with a manifest on disk has a folder to open");

                // The tooltip carries the resolved directory — the only observable available
                // without launching a shell.
                string shown = item.ToolTipText;
                Assert.IsFalse(string.IsNullOrEmpty(shown), "the item should say which folder it opens");
                Assert.AreEqual(PackageDirectory, shown.TrimEnd(Path.DirectorySeparatorChar),
                    "it must resolve to the package's own directory, not the solution's");
            }
        }

        [TestMethod]
        public void TheVerbOpensItsOwnGroup_NotSittingUnderDeleteFromDisk()
        {
            using (ProjectTreePanel panel = Load())
            using (ContextMenuStrip menu = panel.BuildContextMenuForNode(ProjectNode(panel)))
            {
                List<string> texts = Texts(menu);
                int delete = texts.IndexOf("Delete from Disk");
                int open = texts.IndexOf(ItemText);
                Assert.IsTrue(delete >= 0, "Delete from Disk is missing from the project menu");
                Assert.IsTrue(open >= 0, "the open-location verb is missing from the project menu");
                Assert.AreNotEqual(delete + 1, open,
                    "a harmless navigation verb must not sit directly beneath a destructive one");
                Assert.IsTrue(menu.Items[open - 1] is ToolStripSeparator,
                    "the verb should open its own group");
            }
        }

        [TestMethod]
        public void AManifestWithNoDirectoryPart_StillResolvesToSomethingShowable()
        {
            // An empty tooltip would read as a broken menu item.
            Assert.AreEqual("", ProjectTreePanel.ProjectDirectoryOf(null));
            Assert.AreEqual("pyproject.toml", ProjectTreePanel.ProjectDirectoryOf("pyproject.toml"));
            Assert.AreEqual(PackageDirectory,
                ProjectTreePanel.ProjectDirectoryOf(PackageDirectory + @"\pyproject.toml"));
        }

        private static List<string> Texts(ContextMenuStrip menu)
        {
            return menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
        }

        private static ToolStripItem Item(ContextMenuStrip menu, string text)
        {
            foreach (ToolStripItem item in menu.Items)
                if (item.Text == text) return item;
            Assert.Fail("menu has no item '" + text + "'");
            return null;
        }
    }
}
