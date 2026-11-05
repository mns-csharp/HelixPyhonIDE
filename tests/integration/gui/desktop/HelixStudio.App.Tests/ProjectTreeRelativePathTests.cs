using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 447: file nodes offer "Copy Relative Path" (relative to the solution folder), and the
    /// RelativePath helper computes it with backslashes, falling back to the full path when the file is
    /// outside the solution tree.
    /// </summary>
    [TestClass]
    public class ProjectTreeRelativePathTests
    {
        private static string Rel(string baseDir, string file)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("RelativePath", BindingFlags.Static | BindingFlags.NonPublic);
            return (string)m.Invoke(null, new object[] { baseDir, file });
        }

        [TestMethod]
        public void RelativePath_UnderBase_IsBackslashed()
        {
            Assert.AreEqual(@"src\pkg\main.py", Rel(@"C:\ws", @"C:\ws\src\pkg\main.py"));
        }

        [TestMethod]
        public void RelativePath_OutsideBase_FallsBackToFull()
        {
            Assert.AreEqual(@"D:\other\x.py", Rel(@"C:\ws", @"D:\other\x.py"));
        }

        [TestMethod]
        public void RelativePath_NoBase_ReturnsFull()
        {
            Assert.AreEqual(@"C:\ws\a.py", Rel(null, @"C:\ws\a.py"));
        }

        [TestMethod]
        public void FileNode_OffersCopyRelativePath()
        {
            using (var panel = new ProjectTreePanel())
            {
                var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
                var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
                sln.Projects.Add(proj);
                panel.LoadSolution(sln);   // sets _currentSolution
                var fileNode = new TreeNode("m.py")
                {
                    Tag = new ProjectTreePanel.FileGroup { Project = proj, FilePath = @"C:\ws\src\pkg\m.py" }
                };
                MethodInfo build = typeof(ProjectTreePanel).GetMethod("BuildContextMenuForNode", BindingFlags.Instance | BindingFlags.NonPublic);
                ContextMenuStrip menu = (ContextMenuStrip)build.Invoke(panel, new object[] { fileNode });
                var texts = menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                CollectionAssert.Contains(texts, "Copy Relative Path");
            }
        }

        [TestMethod]
        public void SolutionNode_OffersCopyFullPath()
        {
            using (var panel = new ProjectTreePanel())
            {
                var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
                panel.LoadSolution(sln);
                var solutionNode = new TreeNode("ws") { Tag = sln };

                MethodInfo build = typeof(ProjectTreePanel).GetMethod("BuildContextMenuForNode", BindingFlags.Instance | BindingFlags.NonPublic);
                ContextMenuStrip menu = (ContextMenuStrip)build.Invoke(panel, new object[] { solutionNode });
                var items = menu.Items.Cast<ToolStripItem>().ToList();
                var copyPathItem = items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "Copy Full Path");

                Assert.IsNotNull(copyPathItem, "solution context menu should offer Copy Full Path");
                Assert.IsTrue(copyPathItem.Enabled);

                var revealItem = items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "Open Containing Folder");
                Assert.IsNotNull(revealItem, "solution context menu should offer Open Containing Folder");
                Assert.IsTrue(revealItem.Enabled);
            }
        }
    }
}
