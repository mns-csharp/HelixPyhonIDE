using System.Drawing;
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
    /// Iter 415 (SPPFE error/warning indicator): ApplyDiagnostics colours the symbol that owns each
    /// diagnostic (red = error, amber = warning), matching to the nearest enclosing declaration in the
    /// file, and propagates the colour up to ancestors so a collapsed project still signals problems.
    /// Errors outrank warnings on shared ancestors, and a fresh call clears prior markers.
    /// </summary>
    [TestClass]
    public class ProjectTreeDiagnosticBadgeTests
    {
        private const string File1 = @"C:\ws\src\pkg\vehicle.py";
        private static readonly Color ErrorColor = Color.FromArgb(200, 32, 32);
        private static readonly Color WarningColor = Color.FromArgb(150, 100, 0);

        private static ProjectTreePanel Load()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Vehicle", "pkg", TypeKind.ConcreteClass, proj)
            { SourceFilePath = File1, SourceLineNumber = 3 };
            t.Members.Add(new MemberModel("start_engine", MemberKind.Method) { SourceLineNumber = 5 });
            t.Members.Add(new MemberModel("wheel_count", MemberKind.Method) { SourceLineNumber = 11 });
            proj.Types.Add(t);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(sln);
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            return (TreeView)typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static TreeNode Find(TreeView tree, string predText)
        {
            TreeNode found = null;
            void Walk(TreeNode n) { if (n.Text.Contains(predText)) found = n; foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
            return found;
        }

        private static void ExpandAll(TreeView tree)
        {
            void Walk(TreeNode n) { n.Expand(); foreach (TreeNode c in n.Nodes) Walk(c); }
            foreach (TreeNode r in tree.Nodes) Walk(r);
        }

        [TestMethod]
        public void Error_ColoursOwningMember_ByEnclosingLine()
        {
            using (var panel = Load())
            {
                // Line 12 is inside wheel_count (declared line 11), not start_engine (line 5).
                panel.ApplyDiagnostics(new[]
                {
                    new ProjectTreePanel.SymbolDiagnostic { FilePath = File1, Line = 12, IsError = true }
                });
                TreeView tree = Tree(panel);
                Assert.AreEqual(ErrorColor, Find(tree, "wheel_count").ForeColor);
                Assert.AreNotEqual(ErrorColor, Find(tree, "start_engine").ForeColor);
            }
        }

        [TestMethod]
        public void Error_PropagatesToAncestors()
        {
            using (var panel = Load())
            {
                panel.ApplyDiagnostics(new[]
                {
                    new ProjectTreePanel.SymbolDiagnostic { FilePath = File1, Line = 12, IsError = true }
                });
                TreeView tree = Tree(panel);
                Assert.AreEqual(ErrorColor, Find(tree, "Vehicle").ForeColor, "the containing type is marked");
                Assert.AreEqual(ErrorColor, Find(tree, "vehicle.py").ForeColor, "the containing file is marked");
            }
        }

        [TestMethod]
        public void Warning_UsesAmber_AndErrorOutranksOnSharedAncestor()
        {
            using (var panel = Load())
            {
                // A warning on start_engine (line 5) and an error on wheel_count (line 11).
                panel.ApplyDiagnostics(new[]
                {
                    new ProjectTreePanel.SymbolDiagnostic { FilePath = File1, Line = 5, IsError = false },
                    new ProjectTreePanel.SymbolDiagnostic { FilePath = File1, Line = 11, IsError = true }
                });
                TreeView tree = Tree(panel);
                Assert.AreEqual(WarningColor, Find(tree, "start_engine").ForeColor, "warning member is amber");
                Assert.AreEqual(ErrorColor, Find(tree, "wheel_count").ForeColor, "error member is red");
                // Vehicle is an ancestor of both — the error wins.
                Assert.AreEqual(ErrorColor, Find(tree, "Vehicle").ForeColor);
            }
        }

        [TestMethod]
        public void ReApplying_ClearsPriorMarkers()
        {
            using (var panel = Load())
            {
                panel.ApplyDiagnostics(new[]
                {
                    new ProjectTreePanel.SymbolDiagnostic { FilePath = File1, Line = 11, IsError = true }
                });
                // Recompile came back clean.
                panel.ApplyDiagnostics(new ProjectTreePanel.SymbolDiagnostic[0]);
                TreeView tree = Tree(panel);
                Assert.AreNotEqual(ErrorColor, Find(tree, "wheel_count").ForeColor);
                Assert.AreNotEqual(ErrorColor, Find(tree, "Vehicle").ForeColor);
            }
        }
    }
}
