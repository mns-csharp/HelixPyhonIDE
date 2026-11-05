using System.Collections.Generic;
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
    /// Iter 420 (SPPFE Sorting): the sort toggle cycles Declaration -> Alphabetical -> Access -> Kind.
    /// Access orders public before protected/private; Kind groups constructor, method, property, field.
    /// Every mode breaks ties by declaration order.
    /// </summary>
    [TestClass]
    public class ProjectTreeSortModesTests
    {
        private static ProjectTreePanel LoadMixed()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Vehicle", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\v.py" };
            t.Members.Add(new MemberModel("start_engine", MemberKind.Method) { AccessModifier = AccessModifier.Public, SourceLineNumber = 1 });
            t.Members.Add(new MemberModel("_reset", MemberKind.Method) { AccessModifier = AccessModifier.Protected, SourceLineNumber = 2 });
            t.Members.Add(new MemberModel("is_moving", MemberKind.Property) { AccessModifier = AccessModifier.Public, SourceLineNumber = 3 });
            t.Members.Add(new MemberModel("__init__", MemberKind.Constructor) { AccessModifier = AccessModifier.Public, SourceLineNumber = 4 });
            t.Members.Add(new MemberModel("name", MemberKind.Field) { AccessModifier = AccessModifier.Public, SourceLineNumber = 5 });
            proj.Types.Add(t);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(sln);
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            return (TreeView)typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static Button SortButton(ProjectTreePanel panel)
        {
            return (Button)typeof(ProjectTreePanel).GetField("_sortButton", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static List<string> MemberNames(TreeView tree)
        {
            TreeNode type = null;
            void Walk(TreeNode n) { if (n.Tag is TypeModel) type = n; foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return type.Nodes.Cast<TreeNode>().Where(n => n.Tag is MemberModel).Select(n => ((MemberModel)n.Tag).Name).ToList();
        }

        [TestMethod]
        public void AccessSort_PublicBeforeProtected_TieByDeclaration()
        {
            using (var panel = LoadMixed())
            {
                SortButton(panel).PerformClick(); // Alphabetical
                SortButton(panel).PerformClick(); // Access
                StringAssert.Contains(SortButton(panel).Text, "Access");
                // All public in declaration order, then the protected member.
                CollectionAssert.AreEqual(
                    new[] { "start_engine", "is_moving", "__init__", "name", "_reset" },
                    MemberNames(Tree(panel)));
            }
        }

        [TestMethod]
        public void KindSort_ConstructorMethodPropertyField()
        {
            using (var panel = LoadMixed())
            {
                SortButton(panel).PerformClick(); // Alphabetical
                SortButton(panel).PerformClick(); // Access
                SortButton(panel).PerformClick(); // Kind
                StringAssert.Contains(SortButton(panel).Text, "Kind");
                CollectionAssert.AreEqual(
                    new[] { "__init__", "start_engine", "_reset", "is_moving", "name" },
                    MemberNames(Tree(panel)));
            }
        }
    }
}
