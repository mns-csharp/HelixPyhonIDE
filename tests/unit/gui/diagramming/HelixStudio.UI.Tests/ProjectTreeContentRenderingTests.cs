using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.UI.Tests
{
    /// <summary>
    /// The renderer half of solution policy rule 4 (PY8004): the Solution Explorer draws every real
    /// path the loader inventoried.
    ///
    /// <b>Why these are worth their weight.</b> The parity checker projects
    /// <see cref="ProjectModel.ContentFiles"/> and <see cref="SolutionModel.SolutionItems"/> and
    /// declares the solution clean when the disk agrees with that projection. If the panel did not
    /// actually draw them, the projection would be a lie and the report would be worse than
    /// useless: a clean verdict on a tree that still hides files. That is a silent version of
    /// exactly the failure the rule was written from, where a clean-looking five-project tree
    /// disagreed with disk for the whole life of the solution. So the projection is asserted
    /// against the disk in the workspace suite, and the NODES are asserted here.
    /// </summary>
    [TestClass]
    public class ProjectTreeContentRenderingTests
    {
        private static TreeView TreeOf(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, "_treeView field expected.");
            return (TreeView)f.GetValue(panel);
        }

        private static IEnumerable<TreeNode> Descend(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;
                foreach (TreeNode child in Descend(node.Nodes)) yield return child;
            }
        }

        private static IEnumerable<string> FilePathsIn(ProjectTreePanel panel)
        {
            return Descend(TreeOf(panel).Nodes)
                .Select(n => n.Tag as ProjectTreePanel.FileGroup)
                .Where(g => g != null && !string.IsNullOrEmpty(g.FilePath))
                .Select(g => g.FilePath.Replace('\\', '/'));
        }

        /// <summary>
        /// A model shaped the way the loader leaves one: a member holding a file that declares a
        /// type and a file that declares nothing, plus root content no member owns.
        /// </summary>
        private static SolutionModel BuildSolution(string root)
        {
            var solution = new SolutionModel(Path.Combine(root, "pyproject.toml"), "s");
            var project = new ProjectModel(Path.Combine(root, "pkg", "pyproject.toml"), "pkg", solution);
            solution.Projects.Add(project);

            project.Types.Add(new TypeModel("Square", "pkg", TypeKind.ConcreteClass, project)
            {
                SourceFilePath = Path.Combine(root, "pkg", "shapes.py"),
            });

            // What the inventory records: every real file, including the symbol-free ones.
            project.ContentFiles.Add("shapes.py");
            project.ContentFiles.Add("__init__.py");
            project.ContentFiles.Add("data/table.csv");
            project.ContentDirectories.Add("data");
            project.ContentDirectories.Add("empty_package");

            solution.SolutionItems.Add("README.md");
            solution.SolutionItems.Add("results/run.png");
            solution.ContentDirectories.Add("results");
            return solution;
        }

        /// <summary>
        /// The case that kept the motivating solution blocked: nine symbol-free <c>__init__.py</c>
        /// files with no node at all, because the tree's file spine came entirely from symbols.
        /// </summary>
        [TestMethod]
        public void ASymbolFreeFile_GetsItsOwnNode()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(BuildSolution(root));
                var paths = FilePathsIn(panel).ToList();

                CollectionAssert.Contains(paths, (root + "/pkg/__init__.py").Replace('\\', '/'),
                    string.Join("\n", paths));
                // And the file that DOES declare a type is still there exactly once — the content
                // pass must not give an already-rendered file a second node.
                Assert.AreEqual(1, paths.Count(p =>
                    p.EndsWith("/pkg/shapes.py", StringComparison.OrdinalIgnoreCase)),
                    string.Join("\n", paths));
            }
        }

        /// <summary>
        /// A file nested under the member appears at its REAL relative depth, with the folder node
        /// its path implies — not flattened to the project root and not parked in a group node that
        /// corresponds to no directory.
        /// </summary>
        [TestMethod]
        public void ANestedContentFile_AppearsUnderItsOwnFolderNode()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(BuildSolution(root));

                TreeNode data = Descend(TreeOf(panel).Nodes).FirstOrDefault(n =>
                    n.Tag is ProjectTreePanel.FolderGroup g
                    && string.Equals(g.RelativePath, "data", StringComparison.OrdinalIgnoreCase));

                Assert.IsNotNull(data, "no folder node for the member's data/ directory");
                Assert.IsTrue(data.Nodes.Cast<TreeNode>().Any(n =>
                    n.Tag is ProjectTreePanel.FileGroup f
                    && f.FilePath.Replace('\\', '/').EndsWith("/data/table.csv", StringComparison.OrdinalIgnoreCase)),
                    "table.csv is not under data/");
            }
        }

        /// <summary>
        /// An EMPTY directory gets a node too. It has no file to imply it, and a directory the user
        /// can see in Explorer and cannot see in the tree is the same disagreement as a hidden file.
        /// </summary>
        [TestMethod]
        public void AnEmptyDirectory_StillGetsAFolderNode()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(BuildSolution(root));

                bool present = Descend(TreeOf(panel).Nodes).Any(n =>
                    n.Tag is ProjectTreePanel.FolderGroup g
                    && string.Equals(g.RelativePath, "empty_package", StringComparison.OrdinalIgnoreCase));

                Assert.IsTrue(present, "an empty directory under a member has no node");
            }
        }

        /// <summary>
        /// Root content no member owns: loose files under a <c>Solution Items</c> container, and a
        /// non-member directory as a real folder node holding its own files. Five of the motivating
        /// solution's fourteen surviving defects were exactly this shape.
        /// </summary>
        [TestMethod]
        public void RootContentNoMemberOwns_IsDrawnUnderTheSolution()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(BuildSolution(root));
                var all = Descend(TreeOf(panel).Nodes).ToList();

                TreeNode items = all.FirstOrDefault(n => n.Text == "Solution Items");
                Assert.IsNotNull(items, "no Solution Items node");
                Assert.IsTrue(items.Nodes.Cast<TreeNode>().Any(n =>
                    n.Tag is ProjectTreePanel.FileGroup f
                    && f.FilePath.EndsWith("README.md", StringComparison.OrdinalIgnoreCase)),
                    "README.md is not under Solution Items");

                // results/ is a real directory, so it is a real folder node, not a Solution Item.
                TreeNode results = all.FirstOrDefault(n =>
                    n.Tag is ProjectTreePanel.FolderGroup g
                    && string.Equals(g.RelativePath, "results", StringComparison.OrdinalIgnoreCase));
                Assert.IsNotNull(results, "no folder node for the non-member results/ directory");
                Assert.IsTrue(results.Nodes.Cast<TreeNode>().Any(n =>
                    n.Tag is ProjectTreePanel.FileGroup f
                    && f.FilePath.Replace('\\', '/').EndsWith("/results/run.png", StringComparison.OrdinalIgnoreCase)),
                    "run.png is not under results/");
            }
        }

        /// <summary>
        /// A content node carries the same <see cref="ProjectTreePanel.FileGroup"/> tag a
        /// symbol-bearing row does, so double-click-to-open, the context menu and drag-drop all work
        /// on it. Drawing a node the user can see but cannot act on would only relocate the
        /// complaint from "my file is missing" to "my file does nothing".
        /// </summary>
        [TestMethod]
        public void AContentNode_IsActionableLikeAnyOtherFileRow()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(BuildSolution(root));

                TreeNode init = Descend(TreeOf(panel).Nodes).FirstOrDefault(n =>
                    n.Tag is ProjectTreePanel.FileGroup f
                    && f.FilePath.EndsWith("__init__.py", StringComparison.OrdinalIgnoreCase));

                Assert.IsNotNull(init);
                var group = (ProjectTreePanel.FileGroup)init.Tag;
                Assert.IsTrue(Path.IsPathRooted(group.FilePath), group.FilePath);
                Assert.IsNotNull(group.Project, "a file inside a member must know its member");
            }
        }
    }
}
