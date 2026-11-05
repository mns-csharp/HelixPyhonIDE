using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 351 (screenshot audit): the Solution Explorer hung every file directly off the project
    /// node, labelled by bare file name. On SymPy that is 1599 siblings in one flat alphabetical
    /// list — and it contained entries that could not be told apart: "add.py (4)" and "add.py (10)"
    /// are sympy/core/add.py and sympy/vector/add.py, distinguishable only by hovering for a
    /// tooltip. The tree now mirrors the directory layout.
    /// </summary>
    [TestClass]
    public class ProjectTreeFolderHierarchyTests
    {
        private const string Root = @"D:\repos\sympy";

        [TestMethod]
        public void FilesAppearUnderTheirPackageFolders_NotFlatUnderTheProject()
        {
            using (var panel = Load(
                @"D:\repos\sympy\sympy\core\add.py",
                @"D:\repos\sympy\sympy\vector\add.py"))
            {
                TreeNode project = ProjectNode(panel);

                Assert.IsNull(FindChild(project, "add.py (1)"),
                    "a file must no longer sit directly under the project");

                TreeNode sympy = FindChild(project, "sympy");
                Assert.IsNotNull(sympy, "the package folder should be a node");
                Assert.IsNotNull(FindChild(FindChild(sympy, "core"), "add.py (1)"));
                Assert.IsNotNull(FindChild(FindChild(sympy, "vector"), "add.py (1)"));
            }
        }

        /// <summary>The defect in one assertion: two same-named files are now distinguishable.</summary>
        [TestMethod]
        public void TwoFilesWithTheSameName_AreNoLongerIndistinguishable()
        {
            using (var panel = Load(
                @"D:\repos\sympy\sympy\core\add.py",
                @"D:\repos\sympy\sympy\vector\add.py"))
            {
                List<string> paths = new List<string>();
                CollectFilePaths(ProjectNode(panel), string.Empty, paths);

                CollectionAssert.AreEquivalent(
                    new[] { "sympy/core/add.py (1)", "sympy/vector/add.py (1)" },
                    paths,
                    "each file must be reachable by a distinct path through the tree");
            }
        }

        [TestMethod]
        public void FoldersComeBeforeFiles_AndAreAlphabetical()
        {
            using (var panel = Load(
                @"D:\repos\sympy\zzz_top_level.py",
                @"D:\repos\sympy\sympy\core\add.py",
                @"D:\repos\sympy\abc\thing.py"))
            {
                TreeNode project = ProjectNode(panel);

                Assert.AreEqual("abc", project.Nodes[0].Text, "folders sort first, alphabetically");
                Assert.AreEqual("sympy", project.Nodes[1].Text);
                Assert.AreEqual("zzz_top_level.py (1)", project.Nodes[2].Text,
                    "a file in the project root stays at project level, after the folders");
            }
        }

        [TestMethod]
        public void AFileWithNoPath_StaysAtProjectLevel()
        {
            using (var panel = Load(new string[] { null }))
            {
                TreeNode project = ProjectNode(panel);
                Assert.IsTrue(project.Nodes.Count >= 1);
                StringAssert.Contains(project.Nodes[0].Text, "unassigned");
            }
        }

        /// <summary>
        /// A file outside the project directory has no meaningful folder chain to build, so it must
        /// stay at project level rather than growing an invented one.
        /// </summary>
        [TestMethod]
        public void AFileOutsideTheProjectDirectory_StaysAtProjectLevel()
        {
            using (var panel = Load(@"C:\elsewhere\stray.py"))
            {
                TreeNode project = ProjectNode(panel);
                Assert.IsTrue(project.Nodes.Count >= 1);
                Assert.AreEqual("stray.py (1)", project.Nodes[0].Text);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static ProjectTreePanel Load(params string[] typeSourcePaths)
        {
            var solution = new SolutionModel(Root + @"\pyproject.toml", "sympy");
            var project = new ProjectModel(Root + @"\pyproject.toml", "sympy", solution);
            solution.Projects.Add(project);

            int n = 0;
            foreach (string path in typeSourcePaths)
            {
                project.Types.Add(new TypeModel("T" + (n++), "ns", TypeKind.ConcreteClass, project)
                {
                    SourceFilePath = path,
                });
            }

            var panel = new ProjectTreePanel();
            panel.LoadSolution(solution);
            return panel;
        }

        private static TreeNode ProjectNode(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var tree = (TreeView)f.GetValue(panel);
            Assert.AreEqual(1, tree.Nodes.Count, "one solution node expected");
            Assert.AreEqual(1, tree.Nodes[0].Nodes.Count, "one project node expected");
            return tree.Nodes[0].Nodes[0];
        }

        private static TreeNode FindChild(TreeNode parent, string text)
        {
            if (parent == null) return null;
            foreach (TreeNode c in parent.Nodes)
                if (c.Text == text) return c;
            return null;
        }

        // Every FILE node, labelled by its full path through the folder nodes. Selected by Tag
        // rather than "has no children": a project also carries the canonical structural nodes
        // (Python Environment, Dependencies, Tests, Resources, Documentation, Generated Files,
        // Project Configuration), and counting those as files made this 9 paths instead of 2.
        private static void CollectFilePaths(TreeNode node, string prefix, List<string> into)
        {
            foreach (TreeNode c in node.Nodes)
            {
                string here = prefix.Length == 0 ? c.Text : prefix + "/" + c.Text;
                string tagName = c.Tag == null ? null : c.Tag.GetType().Name;
                if (c.Nodes.Count > 0 && tagName == "FolderGroup")
                {
                    CollectFilePaths(c, here, into);
                }
                else if (c.Tag is ProjectTreePanel.FileGroup file)
                {
                    // "Project Configuration" is a FileGroup too — it points at the project's own
                    // pyproject.toml. That is a real file, but not one of the SOURCE files this
                    // test is about, so leave it out rather than baking its label into the expected set.
                    if (!string.Equals(file.FilePath, Root + @"\pyproject.toml", StringComparison.OrdinalIgnoreCase))
                        into.Add(here);
                }
            }
        }
    }
}
