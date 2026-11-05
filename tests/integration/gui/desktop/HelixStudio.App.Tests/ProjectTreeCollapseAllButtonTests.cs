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
    /// Iter 467 (SPPFE): the Solution Explorer header now has a Collapse All toolbar button — the
    /// signature Solution Explorer affordance — previously only reachable from the right-click menu.
    /// </summary>
    [TestClass]
    public class ProjectTreeCollapseAllButtonTests
    {
        private static ProjectTreePanel Load()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Vehicle", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\m.py" };
            t.Members.Add(new MemberModel("start", MemberKind.Method) { SourceLineNumber = 2 });
            proj.Types.Add(t);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(sln);
            return panel;
        }

        private static Button CollapseButton(ProjectTreePanel panel)
        {
            return (Button)typeof(ProjectTreePanel).GetField("_collapseAllButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static Button ExpandButton(ProjectTreePanel panel)
        {
            return (Button)typeof(ProjectTreePanel).GetField("_expandAllButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            return (TreeView)typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static int ExpandedCount(TreeNodeCollection nodes)
        {
            int n = 0;
            foreach (TreeNode node in nodes)
            {
                if (node.IsExpanded) n++;
                n += ExpandedCount(node.Nodes);
            }
            return n;
        }

        [TestMethod]
        public void CollapseAllButton_Exists_AndAdvertisesItself()
        {
            using (var panel = Load())
            {
                Button b = CollapseButton(panel);
                Assert.IsNotNull(b, "the Solution Explorer header should have a Collapse All button");
                Assert.IsTrue(b.Visible || b.Parent != null, "the button must be placed in the header");
            }
        }

        [TestMethod]
        public void ClickingIt_CollapsesTheTree()
        {
            using (var form = new Form())
            {
                var panel = Load();
                panel.Dock = DockStyle.Fill;
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                TreeView tv = Tree(panel);
                foreach (TreeNode n in tv.Nodes) n.ExpandAll();
                Application.DoEvents();
                Assert.IsTrue(ExpandedCount(tv.Nodes) > 0, "precondition: some nodes are expanded");

                CollapseButton(panel).PerformClick();
                Application.DoEvents();

                // VS-style Collapse All folds everything, then re-opens the solution root so the
                // top-level projects stay visible — so exactly one node (the root) remains expanded.
                Assert.AreEqual(1, ExpandedCount(tv.Nodes),
                    "Collapse All must fold every node except the re-opened solution root");
                Assert.IsTrue(tv.Nodes[0].IsExpanded, "the solution root stays expanded after Collapse All");
                form.Close();
            }
        }

        [TestMethod]
        public void ExpandAllButton_UnfoldsTheWholeTree()
        {
            using (var form = new Form())
            {
                var panel = Load();
                panel.Dock = DockStyle.Fill;
                form.Controls.Add(panel);
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                TreeView tv = Tree(panel);
                tv.CollapseAll();
                Application.DoEvents();
                int beforeExpanded = ExpandedCount(tv.Nodes);

                ExpandButton(panel).PerformClick();
                Application.DoEvents();

                Assert.IsTrue(ExpandedCount(tv.Nodes) > beforeExpanded,
                    "Expand All must unfold more nodes than were open before");
                Assert.IsNotNull(ExpandButton(panel), "the header should have an Expand All button");
                form.Close();
            }
        }
    }
}
