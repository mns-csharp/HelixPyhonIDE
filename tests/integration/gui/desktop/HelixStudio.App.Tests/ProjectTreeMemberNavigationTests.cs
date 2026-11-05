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
    /// Iter 402 (SPPFE): double-clicking a member node navigates to that member's exact declaration
    /// line (in its declaring type's file), not merely to the type. It reuses the function-open path,
    /// which does OpenInEditor(file, line).
    /// </summary>
    [TestClass]
    public class ProjectTreeMemberNavigationTests
    {
        private static ProjectTreePanel LoadType()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Widget", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\widget.py", SourceLineNumber = 1 };
            t.Members.Add(new MemberModel("render", MemberKind.Method) { SourceLineNumber = 17 });
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

        private static TreeNode FindMemberNode(TreeView tree, string name)
        {
            TreeNode found = null;
            void Walk(TreeNode n) { if (n.Tag is MemberModel m && m.Name == name) found = n; foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return found;
        }

        [TestMethod]
        public void DoubleClickMember_NavigatesToItsLine_InTheTypesFile()
        {
            using (var panel = LoadType())
            {
                TreeNode memberNode = FindMemberNode(Tree(panel), "render");
                Assert.IsNotNull(memberNode, "the render member node should exist");

                FunctionModel navigated = null;
                panel.FunctionDoubleClicked += (s, e) => navigated = e.Function;

                MethodInfo dbl = typeof(ProjectTreePanel).GetMethod("OnTreeNodeDoubleClick",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                dbl.Invoke(panel, new object[] { panel, new TreeNodeMouseClickEventArgs(memberNode, MouseButtons.Left, 2, 0, 0) });

                Assert.IsNotNull(navigated, "member double-click should raise a navigation");
                Assert.AreEqual(17, navigated.SourceLineNumber, "navigate to the member's line, not the type's");
                Assert.AreEqual(@"C:\ws\src\pkg\widget.py", navigated.SourceFilePath);
                Assert.AreEqual("render", navigated.Name);
            }
        }
    }
}
