using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 424: the Solution Explorer context menu offers tree-wide Expand All / Collapse All (a
    /// standard Solution Explorer action). Collapse All folds everything but keeps the solution root
    /// open, matching Visual Studio.
    /// </summary>
    [TestClass]
    public class ProjectTreeExpandCollapseTests
    {
        private static ProjectTreePanel Load()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Vehicle", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\m.py" };
            t.Members.Add(new MemberModel("start_engine", MemberKind.Method) { SourceLineNumber = 1 });
            proj.Types.Add(t);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(sln);
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            return (TreeView)typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static ContextMenuStrip Build(ProjectTreePanel panel, TreeNode node)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("BuildContextMenuForNode", BindingFlags.Instance | BindingFlags.NonPublic);
            return (ContextMenuStrip)m.Invoke(panel, new object[] { node });
        }

        private static ToolStripItem Item(ContextMenuStrip menu, string text)
        {
            return menu.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Text == text);
        }

        private static int ExpandedCount(TreeView tree)
        {
            int n = 0;
            void Walk(TreeNode t) { if (t.IsExpanded) n++; foreach (TreeNode c in t.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return n;
        }

        [TestMethod]
        public void ContextMenu_OffersExpandAndCollapseAll()
        {
            using (var panel = Load())
            {
                ContextMenuStrip menu = Build(panel, Tree(panel).Nodes[0]);
                Assert.IsNotNull(Item(menu, "Expand All"), "Expand All should be offered");
                Assert.IsNotNull(Item(menu, "Collapse All"), "Collapse All should be offered");
            }
        }

        [TestMethod]
        public void ExpandAll_OpensEveryContainer_ThenCollapseAll_KeepsOnlyRoot()
        {
            using (var panel = Load())
            {
                TreeView tree = Tree(panel);
                ContextMenuStrip menu = Build(panel, tree.Nodes[0]);

                Item(menu, "Expand All").PerformClick();
                int expandedAll = ExpandedCount(tree);
                Assert.IsTrue(expandedAll >= 4, "expand all opens the solution/project/folders/type ("+expandedAll+")");

                Item(menu, "Collapse All").PerformClick();
                Assert.AreEqual(1, ExpandedCount(tree), "collapse all leaves only the solution root open");
                Assert.IsTrue(tree.Nodes[0].IsExpanded, "the solution root stays visible");
            }
        }

        [TestMethod]
        public void NoSolution_DoesNotOfferTreeActions()
        {
            using (var panel = new ProjectTreePanel())
            {
                // An empty tree (nothing loaded) has no hierarchy to fold.
                var detached = new TreeNode("x");
                ContextMenuStrip menu = Build(panel, detached);
                Assert.IsNull(Item(menu, "Expand All"), "no Expand All when the tree is empty");
            }
        }
    }
}
