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
    /// Phase 1 of the Solution Explorer CRUD-coverage sweep: the tree offered "Add Class" / "Add
    /// Interface" on Project/File/Folder nodes but no "Add Abstract Class", even though the canvas's
    /// own "Add &lt;Type&gt;" context menu and the underlying TypeService.AddClass(isAbstract: true)
    /// have supported abstract classes all along — the tree command was pure missing wiring.
    /// </summary>
    [TestClass]
    public class AddAbstractClassCommandTests
    {
        private static ToolStripMenuItem FindItem(ContextMenuStrip menu, string text)
        {
            foreach (ToolStripItem item in menu.Items)
                if (item is ToolStripMenuItem m && m.Text == text) return m;
            return null;
        }

        private static TreeNode FindProjectNode(ProjectTreePanel panel, string name)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
            var tree = (TreeView)f.GetValue(panel);
            TreeNode found = null;
            void Walk(TreeNode n) { if (n.Tag is ProjectModel p && p.Name == name) found = n; foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return found;
        }

        [TestMethod]
        public void AddAbstractClass_FromProjectMenu_CreatesAbcScaffoldedType()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_addabc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string toml = Path.Combine(root, "pyproject.toml");
                File.WriteAllText(toml, "[project]\nname = \"p\"\n");
                var sln = new SolutionModel(toml, "p");
                var project = new ProjectModel(toml, "p", sln);
                sln.Projects.Add(project);

                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Size = new System.Drawing.Size(900, 600);
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    form._currentSolution = sln;
                    form._projectTreePanel.LoadSolution(sln);

                    TreeNode projectNode = FindProjectNode(form._projectTreePanel, "p");
                    Assert.IsNotNull(projectNode, "the project node should exist in the tree");

                    int before = project.Types.Count;
                    using (ContextMenuStrip menu = form._projectTreePanel.BuildContextMenuForNode(projectNode))
                    {
                        ToolStripMenuItem item = FindItem(menu, "Add Abstract Class");
                        Assert.IsNotNull(item, "\"Add Abstract Class\" should be offered on a project node");
                        Assert.IsTrue(item.Enabled);

                        item.PerformClick();
                        Application.DoEvents();
                    }

                    Assert.AreEqual(before + 1, project.Types.Count, "a new type should have been added");
                    TypeModel created = project.Types.Last();
                    Assert.AreEqual(TypeKind.AbstractClass, created.TypeKind,
                        "the created type must actually be an abstract class, not a concrete one");
                    Assert.IsTrue(File.Exists(created.SourceFilePath), "the type's source file should be written to disk");

                    string src = File.ReadAllText(created.SourceFilePath);
                    StringAssert.Contains(src, "ABC");
                    StringAssert.Contains(src, "from abc import");

                    form.Close();
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void AddAbstractClass_IsAlsoOfferedOnFolderNodes()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_addabc_folder_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string toml = Path.Combine(root, "pyproject.toml");
                File.WriteAllText(toml, "[project]\nname = \"p\"\n");
                var sln = new SolutionModel(toml, "p");
                var project = new ProjectModel(toml, "p", sln);
                sln.Projects.Add(project);
                var existing = new TypeModel("Existing", "p.sub", TypeKind.ConcreteClass, project)
                {
                    SourceFilePath = Path.Combine(root, "src", "sub", "existing.py"),
                };
                project.Types.Add(existing);

                using (var panel = new ProjectTreePanel())
                {
                    panel.LoadSolution(sln);
                    FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
                    var tree = (TreeView)f.GetValue(panel);
                    TreeNode folderNode = null;
                    void Walk(TreeNode n) { if (n.Tag is ProjectTreePanel.FolderGroup) folderNode = n; foreach (TreeNode c in n.Nodes) if (folderNode == null) Walk(c); }
                    foreach (TreeNode r in tree.Nodes) Walk(r);
                    Assert.IsNotNull(folderNode, "a folder node should exist for the 'sub' package");

                    using (ContextMenuStrip menu = panel.BuildContextMenuForNode(folderNode))
                    {
                        Assert.IsNotNull(FindItem(menu, "Add Abstract Class"),
                            "\"Add Abstract Class\" should also be offered on a folder/package node");
                    }
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
