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
    /// Iter 404 (SPPFE Rule 2): the Solution Explorer has an incremental search box. Typing narrows
    /// the tree to nodes whose text matches (case-insensitive), keeping the ancestors of each match;
    /// clearing it restores the full tree.
    /// </summary>
    [TestClass]
    public class ProjectTreeSearchFilterTests
    {
        private static ProjectTreePanel LoadTwoTypes()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var widget = new TypeModel("Widget", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\w.py" };
            widget.Members.Add(new MemberModel("render", MemberKind.Method) { SourceLineNumber = 3 });
            var gadget = new TypeModel("Gadget", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\g.py" };
            gadget.Members.Add(new MemberModel("spin", MemberKind.Method) { SourceLineNumber = 3 });
            proj.Types.Add(widget);
            proj.Types.Add(gadget);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(sln);
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            return (TreeView)typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static void SetFilter(ProjectTreePanel panel, string text)
        {
            var box = (TextBox)typeof(ProjectTreePanel).GetField("_searchBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
            box.Text = text;   // raises TextChanged -> ApplyFilter
        }

        private static List<string> AllNodeTexts(TreeView tree)
        {
            var texts = new List<string>();
            void Walk(TreeNode n) { texts.Add(n.Text); foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return texts;
        }

        // Member nodes carry a UML visibility marker prefix ("+ render"), so match on substring.
        private static bool AnyContains(List<string> texts, string needle)
        {
            return texts.Any(t => t.Contains(needle));
        }

        [TestMethod]
        public void Filter_KeepsMatchingBranch_HidesOthers()
        {
            using (var panel = LoadTwoTypes())
            {
                SetFilter(panel, "render");
                var texts = AllNodeTexts(Tree(panel));
                Assert.IsTrue(AnyContains(texts, "render"), "the matching member should remain");
                Assert.IsTrue(texts.Contains("Widget"), "its ancestor type should remain (context)");
                Assert.IsFalse(AnyContains(texts, "spin"), "a non-matching member should be hidden");
                Assert.IsFalse(texts.Contains("Gadget"), "a type with no matching descendant should be hidden");
            }
        }

        [TestMethod]
        public void Filter_IsCaseInsensitive()
        {
            using (var panel = LoadTwoTypes())
            {
                SetFilter(panel, "GADGET");
                Assert.IsTrue(AllNodeTexts(Tree(panel)).Contains("Gadget"));
            }
        }

        [TestMethod]
        public void ClearingFilter_RestoresFullTree()
        {
            using (var panel = LoadTwoTypes())
            {
                SetFilter(panel, "render");
                SetFilter(panel, "");
                var texts = AllNodeTexts(Tree(panel));
                Assert.IsTrue(texts.Contains("Widget") && texts.Contains("Gadget"),
                    "clearing the search restores both types");
                Assert.IsTrue(AnyContains(texts, "spin"), "and their members");
            }
        }
    }
}
