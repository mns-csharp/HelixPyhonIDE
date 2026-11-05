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
    /// Iter 409 (SPPFE Symbol Presentation): each member node carries a leading UML visibility
    /// marker matching the designer canvas — + public, # protected, ~ internal, - private — so the
    /// access modifier is visible at a glance in the Solution Explorer.
    /// </summary>
    [TestClass]
    public class ProjectTreeVisibilityMarkerTests
    {
        private static ProjectTreePanel LoadTypeWithMixedVisibility()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Vehicle", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\vehicle.py" };
            t.Members.Add(new MemberModel("start_engine", MemberKind.Method) { AccessModifier = AccessModifier.Public, SourceLineNumber = 1 });
            t.Members.Add(new MemberModel("_reset_trip", MemberKind.Method) { AccessModifier = AccessModifier.Protected, SourceLineNumber = 2 });
            t.Members.Add(new MemberModel("__compute_vin", MemberKind.Method) { AccessModifier = AccessModifier.Private, SourceLineNumber = 3 });
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

        private static TreeNode FindMember(TreeView tree, string name)
        {
            TreeNode found = null;
            void Walk(TreeNode n)
            {
                if (n.Tag is MemberModel m && m.Name == name) found = n;
                foreach (TreeNode c in n.Nodes) Walk(c);
            }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return found;
        }

        [TestMethod]
        public void MemberNodes_ShowUmlVisibilityMarkers()
        {
            using (var panel = LoadTypeWithMixedVisibility())
            {
                TreeView tree = Tree(panel);
                StringAssert.StartsWith(FindMember(tree, "start_engine").Text, "+ ");
                StringAssert.StartsWith(FindMember(tree, "_reset_trip").Text, "# ");
                StringAssert.StartsWith(FindMember(tree, "__compute_vin").Text, "- ");
            }
        }

        [TestMethod]
        public void DeprecatedMember_ShowsIndicator_AndStrikethroughFont()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Vehicle", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\vehicle.py" };
            t.Members.Add(new MemberModel("crank", MemberKind.Method) { AccessModifier = AccessModifier.Public, IsDeprecated = true, SourceLineNumber = 1 });
            t.Members.Add(new MemberModel("start_engine", MemberKind.Method) { AccessModifier = AccessModifier.Public, SourceLineNumber = 2 });
            proj.Types.Add(t);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(sln);
                TreeNode crank = FindMember(Tree(panel), "crank");
                TreeNode start = FindMember(Tree(panel), "start_engine");
                StringAssert.Contains(crank.Text, "(deprecated)");
                Assert.IsNotNull(crank.NodeFont, "deprecated member should carry a font override");
                Assert.IsTrue(crank.NodeFont.Strikeout, "deprecated member should be struck through");
                Assert.IsFalse(start.Text.Contains("(deprecated)"), "a non-deprecated member is unmarked");
            }
        }

        [TestMethod]
        public void VisibilityMarker_PrependsButKeepsIndicatorSuffix()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Svc", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\svc.py" };
            t.Members.Add(new MemberModel("_connect", MemberKind.Method) { AccessModifier = AccessModifier.Protected, IsAsync = true, SourceLineNumber = 1 });
            proj.Types.Add(t);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(sln);
                TreeNode n = FindMember(Tree(panel), "_connect");
                StringAssert.StartsWith(n.Text, "# ");           // protected marker
                StringAssert.Contains(n.Text, "(async)");        // indicator suffix preserved
            }
        }
    }
}
