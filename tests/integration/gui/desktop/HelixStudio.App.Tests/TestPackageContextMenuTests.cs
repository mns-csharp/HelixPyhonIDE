using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The new-IDE-rule test-package verbs (python-solution-model.md: tests live as separate
    /// sibling projects, and a project holding test_*.py files gets VS2022 MSTest-runner-style
    /// per-project "Run Tests"/"Debug Tests" context-menu items instead of only the whole-solution
    /// Tests menu). Gated by <c>ProjectTreePanel.HasTestFiles</c> — a real directory scan, so these
    /// use real temp directories (like <c>SolutionExplorerSrs5Tests.DoubleClick_...</c>), not the
    /// fake in-memory paths the rest of that suite uses for pure tree-shape assertions.
    /// </summary>
    [TestClass]
    public class TestPackageContextMenuTests
    {
        [TestMethod]
        public void ProjectNode_WithTestFiles_ShowsRunAndDebugTestsVerbs()
        {
            string root = MakeTempDir();
            try
            {
                File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"p_tests\"\n");
                File.WriteAllText(Path.Combine(root, "test_sample.py"), "def test_add():\n    assert 1 + 1 == 2\n");

                using (ContextMenuStrip menu = BuildProjectMenu(root, "p_tests"))
                {
                    List<string> items = ItemTexts(menu);
                    CollectionAssert.Contains(items, "Run Tests",
                        "a project holding test_*.py should offer the per-project Run Tests verb");
                    CollectionAssert.Contains(items, "Debug Tests",
                        "a project holding test_*.py should offer the per-project Debug Tests verb");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void ProjectNode_WithoutTestFiles_HidesRunAndDebugTestsVerbs()
        {
            string root = MakeTempDir();
            try
            {
                File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"p_lib\"\n");
                File.WriteAllText(Path.Combine(root, "helper.py"), "def helper():\n    return 1\n");

                using (ContextMenuStrip menu = BuildProjectMenu(root, "p_lib"))
                {
                    List<string> items = ItemTexts(menu);
                    Assert.IsFalse(items.Contains("Run Tests"),
                        "a project with no test_*.py must not show the Run Tests verb");
                    Assert.IsFalse(items.Contains("Debug Tests"),
                        "a project with no test_*.py must not show the Debug Tests verb");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void ProjectNode_WithUnderscoreTestSuffixFiles_ShowsRunAndDebugTestsVerbs()
        {
            // The *_test.py naming variant (pytest also collects this convention).
            string root = MakeTempDir();
            try
            {
                File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"p_tests2\"\n");
                File.WriteAllText(Path.Combine(root, "sample_test.py"), "def test_ok():\n    assert True\n");

                using (ContextMenuStrip menu = BuildProjectMenu(root, "p_tests2"))
                {
                    List<string> items = ItemTexts(menu);
                    CollectionAssert.Contains(items, "Run Tests");
                    CollectionAssert.Contains(items, "Debug Tests");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void ProjectNode_WithTestFiles_LabeledUnitTestNotLibrary()
        {
            string root = MakeTempDir();
            try
            {
                File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"p_tests\"\n");
                File.WriteAllText(Path.Combine(root, "test_sample.py"), "def test_add():\n    assert 1 + 1 == 2\n");

                string text = ProjectNodeText(root, "p_tests");
                StringAssert.Contains(text, "[Unit Test]",
                    "a project holding test_*.py should be labeled Unit Test, not Library");
                Assert.IsFalse(text.Contains("[Library]"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void ProjectNode_WithoutTestFiles_LabeledLibrary()
        {
            string root = MakeTempDir();
            try
            {
                File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"p_lib\"\n");
                File.WriteAllText(Path.Combine(root, "helper.py"), "def helper():\n    return 1\n");

                string text = ProjectNodeText(root, "p_lib");
                StringAssert.Contains(text, "[Library]");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static string ProjectNodeText(string root, string projectName)
        {
            string toml = Path.Combine(root, "pyproject.toml");
            var solution = new SolutionModel(toml, projectName);
            var project = new ProjectModel(toml, projectName, solution);
            solution.Projects.Add(project);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(solution);
            TreeNode projectNode = Tree(panel).Nodes[0].Nodes[0];
            return projectNode.Text;
        }

        private static ContextMenuStrip BuildProjectMenu(string root, string projectName)
        {
            string toml = Path.Combine(root, "pyproject.toml");
            var solution = new SolutionModel(toml, projectName);
            var project = new ProjectModel(toml, projectName, solution);
            solution.Projects.Add(project);
            var panel = new ProjectTreePanel();
            panel.LoadSolution(solution);
            TreeNode projectNode = Tree(panel).Nodes[0].Nodes[0];
            return panel.BuildContextMenuForNode(projectNode);
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            System.Reflection.FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (TreeView)f.GetValue(panel);
        }

        private static List<string> ItemTexts(ContextMenuStrip menu)
        {
            var texts = new List<string>();
            foreach (ToolStripItem item in menu.Items)
                if (!(item is ToolStripSeparator)) texts.Add(item.Text);
            return texts;
        }

        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_testpkg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
