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
    /// Iter 398 (SPPFE Slice 2): a class node exposes its members (constructor / method / property /
    /// field) as child nodes with per-kind icons and static/abstract/async indicators, in
    /// declaration order — the logical structure the spec asks for, not just a leaf type node.
    /// </summary>
    [TestClass]
    public class ProjectTreeMemberNodeTests
    {
        private static ProjectTreePanel LoadTypeWithMembers()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Widget", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\widget.py" };
            t.Members.Add(new MemberModel("__init__", MemberKind.Constructor) { SourceLineNumber = 2 });
            t.Members.Add(new MemberModel("render", MemberKind.Method) { SourceLineNumber = 5 });
            t.Members.Add(new MemberModel("connect", MemberKind.Method) { IsAsync = true, SourceLineNumber = 8 });
            t.Members.Add(new MemberModel("area", MemberKind.Property) { SourceLineNumber = 11 });
            t.Members.Add(new MemberModel("make", MemberKind.Method) { IsStatic = true, SourceLineNumber = 14 });
            t.Members.Add(new MemberModel("_count", MemberKind.Field) { SourceLineNumber = 1 });
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

        private static TreeNode FindTypeNode(TreeView tree)
        {
            TreeNode found = null;
            void Walk(TreeNode n) { if (n.Tag is TypeModel) found = n; foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return found;
        }

        [TestMethod]
        public void TypeNode_HasMemberChildren_InDeclarationOrder()
        {
            using (var panel = LoadTypeWithMembers())
            {
                TreeNode type = FindTypeNode(Tree(panel));
                Assert.IsNotNull(type, "the Widget type node should exist");
                var members = type.Nodes.Cast<TreeNode>().Where(n => n.Tag is MemberModel).ToList();
                Assert.AreEqual(6, members.Count, "all six members should appear as children");
                // Declaration order is by source line: _count(1), __init__(2), render(5), connect(8), area(11), make(14).
                CollectionAssert.AreEqual(
                    new[] { "_count", "__init__", "render", "connect", "area", "make" },
                    members.Select(n => ((MemberModel)n.Tag).Name).ToArray());
            }
        }

        [TestMethod]
        public void MemberNodes_UsePerKindIcons_AndIndicators()
        {
            using (var panel = LoadTypeWithMembers())
            {
                TreeNode type = FindTypeNode(Tree(panel));
                TreeNode Get(string name) => type.Nodes.Cast<TreeNode>().First(n => n.Tag is MemberModel m && m.Name == name);

                Assert.AreEqual("constructor", Get("__init__").ImageKey);
                Assert.AreEqual("property", Get("area").ImageKey);
                Assert.AreEqual("memberField", Get("_count").ImageKey);
                Assert.AreEqual("method", Get("render").ImageKey);

                StringAssert.Contains(Get("connect").Text, "(async)");
                StringAssert.Contains(Get("make").Text, "(static)");
            }
        }
    }
}
