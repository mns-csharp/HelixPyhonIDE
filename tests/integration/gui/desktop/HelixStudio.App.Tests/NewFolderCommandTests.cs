using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Phase 4 of the Solution Explorer CRUD-coverage sweep: there was no explicit "New Folder" /
    /// "New Package" command — creating a package directory was only possible implicitly, by typing
    /// a nested path into "Add File..."'s save dialog. The tree-menu wiring test here follows the
    /// same dialog-boundary convention as the rest of this suite (see AddExistingCommandsTests); the
    /// second test covers the actual folder-scaffolding mechanism (EnsurePackageMarkersForNewFile,
    /// reused via a synthetic "__init__.py inside the new folder" path) directly, since that part
    /// needs no dialog and is where the real risk of a bug lives.
    /// </summary>
    [TestClass]
    public class NewFolderCommandTests
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
            var f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
            return (TreeView)f.GetValue(panel);
        }

        [TestMethod]
        public void ProjectNode_NewFolder_RaisesNewFolderRequested_WithTheProject()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode project = Tree(panel).Nodes[0].Nodes[0];
                object seen = null;
                panel.NewFolderRequested += (s, e) => seen = e.Item;
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(project))
                {
                    ToolStripItem item = menu.Items.Cast<ToolStripItem>().First(i => i.Text == "New Folder");
                    item.PerformClick();
                }
                Assert.IsInstanceOfType(seen, typeof(ProjectModel));
            }
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
        public void FolderNode_Menu_OffersNewFolder()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode folder = FindFolderNode(Tree(panel).Nodes[0].Nodes[0]);
                Assert.IsNotNull(folder);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(folder))
                {
                    CollectionAssert.Contains(
                        menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList(),
                        "New Folder");
                }
            }
        }

        [TestMethod]
        public void EnsurePackageMarkersForNewFile_ScaffoldsInitPy_InTheNewFolder_AndItsAncestors()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_newfolder_" + Guid.NewGuid().ToString("N"));
            string projectDir = Path.Combine(root, "proj");
            string existingPkg = Path.Combine(projectDir, "src", "pkg");
            Directory.CreateDirectory(existingPkg);
            File.WriteAllText(Path.Combine(existingPkg, "__init__.py"), string.Empty);
            try
            {
                var sln = new SolutionModel(Path.Combine(projectDir, "pyproject.toml"), "proj");
                var project = new ProjectModel(Path.Combine(projectDir, "pyproject.toml"), "proj", sln);
                sln.Projects.Add(project);

                // The exact new-folder scenario: an empty sub-package created under an existing one.
                string newDir = Path.Combine(existingPkg, "sub");
                Directory.CreateDirectory(newDir);

                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    MethodInfo m = typeof(MainForm).GetMethod("EnsurePackageMarkersForNewFile",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(m, "EnsurePackageMarkersForNewFile expected");
                    m.Invoke(form, new object[] { project, Path.Combine(newDir, "__init__.py") });

                    Assert.IsTrue(File.Exists(Path.Combine(newDir, "__init__.py")),
                        "the new folder itself must become a real Python package");
                    // The ancestor ("pkg") already had its marker — must survive untouched, not be
                    // required to be recreated, and the walk must not throw discovering it pre-exists.
                    Assert.IsTrue(File.Exists(Path.Combine(existingPkg, "__init__.py")));

                    form.Close();
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
