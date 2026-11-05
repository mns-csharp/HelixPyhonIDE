using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Iter 224: the Solution Explorer tree now supports the standard F2 (rename) and Delete keys
    /// on the selected node, mirroring what the context menu already offers. Delete removes a
    /// selected project or type (host confirms) but leaves non-deletable nodes (the solution) alone.
    /// </summary>
    [TestClass]
    public class ProjectTreeKeyboardTests
    {
        private static TreeView TreeOf(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_treeView field expected.");
            return (TreeView)f.GetValue(panel);
        }

        private static void PressKey(ProjectTreePanel panel, Keys key)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("OnTreeKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "OnTreeKeyDown expected.");
            m.Invoke(panel, new object[] { null, new KeyEventArgs(key) });
        }

        [TestMethod]
        public void DeleteKey_RemovesProject_ButNotSolution()
        {
            using (var panel = new ProjectTreePanel())
            {
                var tv = TreeOf(panel);
                var sln = new SolutionModel("s.sln", "sol");
                var proj = new ProjectModel("p.py", "proj", sln);

                var projNode = new TreeNode("proj") { Tag = proj };
                var slnNode = new TreeNode("sol") { Tag = sln };
                tv.Nodes.Add(slnNode);
                tv.Nodes.Add(projNode);

                int fired = 0;
                panel.DeleteRequested += (s, e) => fired++;

                tv.SelectedNode = projNode;
                PressKey(panel, Keys.Delete);
                Assert.AreEqual(1, fired, "Delete on a project node must raise DeleteRequested.");

                tv.SelectedNode = slnNode;
                PressKey(panel, Keys.Delete);
                Assert.AreEqual(1, fired, "Delete on the solution node must be a no-op (not deletable).");
            }
        }

        [TestMethod]
        public void DeleteKey_RemovesFolderGroup_CarryingItAsTheEventItem()
        {
            using (var panel = new ProjectTreePanel())
            {
                var tv = TreeOf(panel);
                var sln = new SolutionModel("s.sln", "sol");
                var proj = new ProjectModel("p.py", "proj", sln);
                var folder = new ProjectTreePanel.FolderGroup { Project = proj, RelativePath = "src/pkg" };
                var folderNode = new TreeNode("pkg") { Tag = folder };
                tv.Nodes.Add(folderNode);

                object deleted = null;
                panel.DeleteRequested += (s, e) => deleted = e.Item;

                tv.SelectedNode = folderNode;
                PressKey(panel, Keys.Delete);

                Assert.AreSame(folder, deleted, "Delete on a package folder must raise DeleteRequested with the FolderGroup.");
            }
        }

        [TestMethod]
        public void F2_OnFunction_StartsInlineRename_NotRenameSymbol()
        {
            // §5 (loop 533) changed F2 on a function to the inline label edit, matching VS —
            // "Rename Symbol..." stays a context-menu verb for picking a narrower scope. This
            // test originally pinned the pre-533 behavior (F2 → RenameSymbolRequested) and had
            // been failing ever since; it now pins the documented current behavior.
            using (var panel = new ProjectTreePanel())
            {
                var tv = TreeOf(panel);
                var fn = new FunctionModel { Name = "bar" };

                var fnNode = new TreeNode("bar") { Tag = fn };
                tv.Nodes.Add(fnNode);
                tv.SelectedNode = fnNode;

                int fired = 0;
                panel.RenameSymbolRequested += (s, e) => fired++;
                PressKey(panel, Keys.F2);
                Assert.AreEqual(0, fired, "F2 must not raise Rename Symbol — it starts the inline rename.");
                Assert.IsTrue(tv.LabelEdit, "F2 on a function arms the tree's inline label edit (loop 533).");
            }
        }
    }
}
