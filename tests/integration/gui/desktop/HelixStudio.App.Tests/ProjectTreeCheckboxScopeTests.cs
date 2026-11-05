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
    /// Iter 352 (screenshot audit of the SymPy tree): every row in the Solution Explorer drew a
    /// checkbox, but the checkbox only ever meant "show this project on the Designer canvas" —
    /// visibility is project-granular through the projection builders, the category tabs and the
    /// saved per-solution setting.
    ///
    /// So both things a user would try with a class-row checkbox lied. Ticking one class walked the
    /// parents up setting each to "any child checked", which checked the project and put all 2083
    /// SymPy types on the canvas. Un-ticking one class did nothing whatsoever, because a sibling was
    /// still checked and the project stayed checked.
    ///
    /// Only the solution and project rows draw a checkbox now, and only those rows drive visibility.
    /// </summary>
    [TestClass]
    public class ProjectTreeCheckboxScopeTests
    {
        private const string Root = @"D:\repos\sympy";

        [TestMethod]
        public void OnlyTheSolutionAndProjectRowsOwnACheckbox()
        {
            using (var panel = Load(@"D:\repos\sympy\sympy\core\add.py"))
            {
                TreeNode solution = Tree(panel).Nodes[0];
                TreeNode project = solution.Nodes[0];
                TreeNode folder = project.Nodes[0];                       // sympy
                TreeNode file = folder.Nodes[0].Nodes[0];                 // core > add.py (1)
                TreeNode type = file.Nodes[0];                            // T0

                Assert.IsTrue(ProjectTreePanel.ShowsCheckbox(solution), "solution row");
                Assert.IsTrue(ProjectTreePanel.ShowsCheckbox(project), "project row");
                Assert.IsFalse(ProjectTreePanel.ShowsCheckbox(folder), "folder row");
                Assert.IsFalse(ProjectTreePanel.ShowsCheckbox(file), "file row");
                Assert.IsFalse(ProjectTreePanel.ShowsCheckbox(type), "class row");
            }
        }

        /// <summary>The defect in one assertion: ticking a class no longer shows the whole project.</summary>
        [TestMethod]
        public void CheckingAClassRow_DoesNotMakeTheProjectVisible()
        {
            using (var panel = Load(@"D:\repos\sympy\sympy\core\add.py"))
            {
                TreeNode project = Tree(panel).Nodes[0].Nodes[0];
                TreeNode type = project.Nodes[0].Nodes[0].Nodes[0].Nodes[0];

                var raised = new List<string>();
                panel.ProjectVisibilityChanged += (s, e) => raised.Add(e.ProjectName);

                type.Checked = true;
                RaiseAfterCheck(panel, type);

                CollectionAssert.AreEqual(new string[0], raised.ToArray(),
                    "a row with no checkbox must not redraw the canvas");
                Assert.IsFalse(project.Checked, "and must not tick the project on its way up");
            }
        }

        [TestMethod]
        public void CheckingTheProjectRow_StillMakesItVisible()
        {
            using (var panel = Load(@"D:\repos\sympy\sympy\core\add.py"))
            {
                TreeNode project = Tree(panel).Nodes[0].Nodes[0];

                var raised = new List<string>();
                panel.ProjectVisibilityChanged += (s, e) => raised.Add(e.ProjectName);

                project.Checked = true;
                RaiseAfterCheck(panel, project);

                CollectionAssert.AreEqual(new[] { "sympy" }, raised.ToArray());
                CollectionAssert.AreEqual(new[] { "sympy" }, panel.GetCheckedProjects().ToArray());
            }
        }

        /// <summary>
        /// The solution row still ticks its projects — and stops there. Before, one click on it set
        /// Checked on every file, class and function underneath: ~10,000 native item updates on
        /// SymPy for state nothing reads.
        /// </summary>
        [TestMethod]
        public void CheckingTheSolutionRow_TicksTheProjectsAndNothingDeeper()
        {
            using (var panel = Load(@"D:\repos\sympy\sympy\core\add.py"))
            {
                TreeNode solution = Tree(panel).Nodes[0];
                TreeNode project = solution.Nodes[0];

                solution.Checked = true;
                RaiseAfterCheck(panel, solution);

                Assert.IsTrue(project.Checked, "the project follows the solution");
                Assert.IsFalse(project.Nodes[0].Checked, "the folder underneath does not");
                CollectionAssert.AreEqual(new[] { "sympy" }, panel.GetCheckedProjects().ToArray());
            }
        }

        // ------------------------------------------------------------------ helpers

        // AfterCheck fires with TreeViewAction.Unknown for a programmatic assignment, which the panel
        // deliberately ignores, so the user gesture is replayed directly onto the handler.
        private static void RaiseAfterCheck(ProjectTreePanel panel, TreeNode node)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("OnTreeAfterCheck",
                BindingFlags.Instance | BindingFlags.NonPublic);
            m.Invoke(panel, new object[] { null, new TreeViewEventArgs(node, TreeViewAction.ByMouse) });
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_treeView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (TreeView)f.GetValue(panel);
        }

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
    }
}
