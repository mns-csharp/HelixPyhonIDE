using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 265 (user bug: double-clicking main.py in the tree doesn't open it). The double-click opens
    /// a file only when the node carries a FileGroup whose FilePath is File.Exists-able. This drives the
    /// actual GUI tree builder (ProjectTreePanel.LoadSolution) for a root-level console app and asserts
    /// the main.py node is a FileGroup with an openable path — closing the non-interactive chain.
    /// </summary>
    [TestClass]
    public class ProjectTreeFileNodeTests
    {
        [TestMethod]
        public async System.Threading.Tasks.Task ConsoleApp_MainPyNode_IsFileGroup_WithOpenablePath()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_tree_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "pyproject.toml"),
                    "[project]\nname = \"myapp\"\nversion = \"0.1.0\"\nrequires-python = \">=3.11\"\n");
                File.WriteAllText(Path.Combine(root, "main.py"),
                    "def main():\n    print(\"hello\")\n\n\nif __name__ == \"__main__\":\n    main()\n");

                var result = await new SolutionService().LoadAsync(Path.Combine(root, "pyproject.toml"));
                Assert.IsTrue(result.Success, result.ErrorMessage);

                using (var panel = new ProjectTreePanel())
                {
                    panel.LoadSolution(result.Solution);

                    var treeView = (TreeView)typeof(ProjectTreePanel)
                        .GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
                    Assert.IsNotNull(treeView, "_treeView expected");

                    TreeNode mainNode = FindNode(treeView.Nodes,
                        n => (n.Text ?? "").StartsWith("main.py"));
                    Assert.IsNotNull(mainNode, "a 'main.py' file node should exist in the tree");

                    var fg = mainNode.Tag as ProjectTreePanel.FileGroup;
                    Assert.IsNotNull(fg, "the main.py node's Tag must be a FileGroup (so double-click opens it)");
                    Assert.IsFalse(string.IsNullOrEmpty(fg.FilePath), "FileGroup.FilePath must be set");
                    Assert.IsTrue(File.Exists(fg.FilePath),
                        "the file node's path must be File.Exists-able or double-click no-ops: " + fg.FilePath);
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static TreeNode FindNode(TreeNodeCollection nodes, Func<TreeNode, bool> pred)
        {
            foreach (TreeNode n in nodes)
            {
                if (pred(n)) return n;
                TreeNode found = FindNode(n.Nodes, pred);
                if (found != null) return found;
            }
            return null;
        }
    }
}
