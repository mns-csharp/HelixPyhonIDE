using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 443 ("Sync with Active Document"): SelectFileNode finds and selects the tree node for a
    /// file path, so the Solution Explorer can track the active editor document. Path matching is
    /// case/separator-insensitive; an unknown path is a no-op.
    /// </summary>
    [TestClass]
    public class ProjectTreeSyncFileTests
    {
        private static ProjectTreePanel Load()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Vehicle", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\main.py" };
            proj.Types.Add(t);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(sln);
            return panel;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            return (TreeView)typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        [TestMethod]
        public void SelectFileNode_SelectsTheFile()
        {
            using (var panel = Load())
            {
                bool ok = panel.SelectFileNode(@"C:\ws\src\pkg\main.py");
                Assert.IsTrue(ok, "the file node should be found");
                TreeNode sel = Tree(panel).SelectedNode;
                Assert.IsNotNull(sel);
                Assert.IsInstanceOfType(sel.Tag, typeof(ProjectTreePanel.FileGroup));
            }
        }

        [TestMethod]
        public void SelectFileNode_IsPathNormalised()
        {
            using (var panel = Load())
            {
                // Forward slashes and different casing still match.
                Assert.IsTrue(panel.SelectFileNode(@"c:/ws/src/pkg/MAIN.py"));
            }
        }

        [TestMethod]
        public void SelectFileNode_UnknownPath_IsNoOp()
        {
            using (var panel = Load())
            {
                Assert.IsFalse(panel.SelectFileNode(@"C:\ws\src\pkg\other.py"));
            }
        }
    }
}
