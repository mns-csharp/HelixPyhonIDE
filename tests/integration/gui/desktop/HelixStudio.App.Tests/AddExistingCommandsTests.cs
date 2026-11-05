using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Phase 3 of the Solution Explorer CRUD-coverage sweep: "Add File..." / "Add Project..." only
    /// ever created brand-new items — there was no way to register an already-existing file or
    /// project folder. These commands are gated behind a modal file/folder-picker dialog
    /// (OpenFileDialog / FolderBrowserDialog), so — matching the rest of this suite's established
    /// boundary for dialog-gated commands (see SolutionExplorerSrs5Tests' Delete tests) — coverage
    /// here stops at proving the tree wiring (menu item present, event carries the right node) is
    /// correct, since driving a real modal dialog would hang a headless test run.
    /// </summary>
    [TestClass]
    public class AddExistingCommandsTests
    {
        private const string Root = @"D:\repos\sympy";

        private static ProjectTreePanel Load(params string[] typeSourcePaths)
        {
            var solution = new SolutionModel(Root + @"\pyproject.toml", "sympy");
            var project = new ProjectModel(Root + @"\pyproject.toml", "sympy", solution);
            solution.Projects.Add(project);
            int n = 0;
            foreach (string path in typeSourcePaths)
            {
                project.Types.Add(new TypeModel("T" + (n++), "ns", TypeKind.ConcreteClass, project)
                {
                    SourceFilePath = path,
                });
            }
            var panel = new ProjectTreePanel();
            panel.LoadSolution(solution);
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            var f = typeof(ProjectTreePanel).GetField("_treeView",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (TreeView)f.GetValue(panel);
        }

        private static TreeNode FindFolderNode(TreeNode start)
        {
            foreach (TreeNode child in start.Nodes)
            {
                if (child.Tag is ProjectTreePanel.FolderGroup) return child;
                TreeNode inner = FindFolderNode(child);
                if (inner != null) return inner;
            }
            return null;
        }

        [TestMethod]
        public void ProjectNode_Menu_OffersAddExistingFile_NextToAddFile()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode project = Tree(panel).Nodes[0].Nodes[0];
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(project))
                {
                    var texts = menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                    CollectionAssert.Contains(texts, "Add File...");
                    CollectionAssert.Contains(texts, "Add Existing File...");
                }
            }
        }

        [TestMethod]
        public void FolderNode_Menu_OffersAddExistingFile()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode folder = FindFolderNode(Tree(panel).Nodes[0].Nodes[0]);
                Assert.IsNotNull(folder);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(folder))
                {
                    CollectionAssert.Contains(
                        menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList(),
                        "Add Existing File...");
                }
            }
        }

        [TestMethod]
        public void ProjectNode_AddExistingFile_RaisesAddExistingFileRequested_WithTheProject()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode project = Tree(panel).Nodes[0].Nodes[0];
                object seen = null;
                panel.AddExistingFileRequested += (s, e) => seen = e.Item;
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(project))
                {
                    ToolStripItem item = menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Add Existing File...");
                    item.PerformClick();
                }
                Assert.IsInstanceOfType(seen, typeof(ProjectModel));
            }
        }

        [TestMethod]
        public void SolutionNode_Menu_OffersAddExistingProject_NextToAddProject()
        {
            using (var panel = Load())
            {
                TreeNode solution = Tree(panel).Nodes[0];
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(solution))
                {
                    var texts = menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                    CollectionAssert.Contains(texts, "Add New Project...");
                    CollectionAssert.Contains(texts, "Add Existing Project...");
                }
            }
        }

        [TestMethod]
        public void SolutionNode_AddExistingProject_RaisesAddExistingProjectRequested()
        {
            using (var panel = Load())
            {
                TreeNode solution = Tree(panel).Nodes[0];
                object seen = null;
                bool fired = false;
                panel.AddExistingProjectRequested += (s, e) => { fired = true; seen = e.Item; };
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(solution))
                {
                    ToolStripItem item = menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Add Existing Project...");
                    item.PerformClick();
                }
                Assert.IsTrue(fired, "clicking \"Add Existing Project...\" must raise AddExistingProjectRequested");
                Assert.IsInstanceOfType(seen, typeof(SolutionModel));
            }
        }
    }
}
