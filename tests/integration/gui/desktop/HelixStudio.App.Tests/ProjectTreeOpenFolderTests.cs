using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 435: a file node's context menu offers "Open Containing Folder", which reveals the file in
    /// Windows Explorer with it selected (explorer.exe /select,"path").
    /// </summary>
    [TestClass]
    public class ProjectTreeOpenFolderTests
    {
        private static ContextMenuStrip Build(ProjectTreePanel panel, TreeNode node)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("BuildContextMenuForNode", BindingFlags.Instance | BindingFlags.NonPublic);
            return (ContextMenuStrip)m.Invoke(panel, new object[] { node });
        }

        [TestMethod]
        public void FileNode_OffersOpenContainingFolder()
        {
            using (var panel = new ProjectTreePanel())
            {
                var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
                var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
                var fileNode = new TreeNode("m.py")
                {
                    Tag = new ProjectTreePanel.FileGroup { Project = proj, FilePath = @"C:\ws\src\pkg\m.py" }
                };
                ContextMenuStrip menu = Build(panel, fileNode);
                var texts = menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                CollectionAssert.Contains(texts, "Open Containing Folder");
                CollectionAssert.Contains(texts, "Copy Full Path");
            }
        }

        [TestMethod]
        public void ExplorerSelectArgs_SelectsTheFile()
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("ExplorerSelectArgs", BindingFlags.Static | BindingFlags.NonPublic);
            string args = (string)m.Invoke(null, new object[] { @"C:\ws\src\pkg\m.py" });
            Assert.AreEqual("/select,\"C:\\ws\\src\\pkg\\m.py\"", args);
        }

        [TestMethod]
        public void ExplorerSelectArgs_NormalizesForwardSlashes()
        {
            // Regression: CMake-derived paths carry forward slashes; explorer's /select
            // silently rejects them and opens the DESKTOP instead of the folder.
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("ExplorerSelectArgs", BindingFlags.Static | BindingFlags.NonPublic);
            string args = (string)m.Invoke(null, new object[] { "C:/ws/gpu/main.cu" });
            Assert.AreEqual("/select,\"C:\\ws\\gpu\\main.cu\"", args);
        }
    }
}
