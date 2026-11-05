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
    /// Iter 407 (SPPFE Sorting): the Solution Explorer header carries a sort toggle that flips a
    /// type's member layout between source declaration order (default) and alphabetical by name.
    /// Clicking it re-sorts every type's members and keeps the tree expanded.
    /// </summary>
    [TestClass]
    public class ProjectTreeMemberSortTests
    {
        private static ProjectTreePanel LoadTypeWithMembers()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Vehicle", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\vehicle.py" };
            t.Members.Add(new MemberModel("start_engine", MemberKind.Method) { SourceLineNumber = 3 });
            t.Members.Add(new MemberModel("accelerate", MemberKind.Method) { SourceLineNumber = 6 });
            t.Members.Add(new MemberModel("brake", MemberKind.Method) { SourceLineNumber = 9 });
            t.Members.Add(new MemberModel("zone_check", MemberKind.Method) { SourceLineNumber = 12 });
            t.Members.Add(new MemberModel("make_noise", MemberKind.Method) { SourceLineNumber = 15 });
            proj.Types.Add(t);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(sln);
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
            return (TreeView)f.GetValue(panel);
        }

        private static Button SortButton(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_sortButton", BindingFlags.Instance | BindingFlags.NonPublic);
            return (Button)f.GetValue(panel);
        }

        private static TreeNode FindTypeNode(TreeView tree)
        {
            TreeNode found = null;
            void Walk(TreeNode n) { if (n.Tag is TypeModel) found = n; foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return found;
        }

        private static string[] MemberNames(TreeView tree)
        {
            TreeNode type = FindTypeNode(tree);
            return type.Nodes.Cast<TreeNode>().Where(n => n.Tag is MemberModel)
                .Select(n => ((MemberModel)n.Tag).Name).ToArray();
        }

        [TestMethod]
        public void DefaultSort_IsDeclarationOrder()
        {
            using (var panel = LoadTypeWithMembers())
            {
                CollectionAssert.AreEqual(
                    new[] { "start_engine", "accelerate", "brake", "zone_check", "make_noise" },
                    MemberNames(Tree(panel)));
            }
        }

        [TestMethod]
        public void ToggleOnce_SortsAlphabetically()
        {
            using (var panel = LoadTypeWithMembers())
            {
                SortButton(panel).PerformClick();
                CollectionAssert.AreEqual(
                    new[] { "accelerate", "brake", "make_noise", "start_engine", "zone_check" },
                    MemberNames(Tree(panel)));
                StringAssert.Contains(SortButton(panel).Text, "A");
            }
        }

        [TestMethod]
        public void CyclingAllModes_ReturnsToDeclarationOrder()
        {
            using (var panel = LoadTypeWithMembers())
            {
                // Four modes: Declaration -> Alphabetical -> Access -> Kind -> Declaration.
                for (int i = 0; i < 4; i++) SortButton(panel).PerformClick();
                CollectionAssert.AreEqual(
                    new[] { "start_engine", "accelerate", "brake", "zone_check", "make_noise" },
                    MemberNames(Tree(panel)));
                StringAssert.Contains(SortButton(panel).Text, "Decl");
            }
        }

        [TestMethod]
        public void Toggle_KeepsTypeNodeExpanded()
        {
            using (var panel = LoadTypeWithMembers())
            {
                TreeView tree = Tree(panel);
                TreeNode type = FindTypeNode(tree);
                type.Expand();
                Assert.IsTrue(type.IsExpanded, "precondition: the type node is expanded");

                SortButton(panel).PerformClick();

                TreeNode typeAfter = FindTypeNode(Tree(panel));
                Assert.IsTrue(typeAfter.IsExpanded, "sorting must not collapse an expanded type node");
            }
        }
    }
}
