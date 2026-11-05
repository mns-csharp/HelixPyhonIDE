using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    // With no solution loaded, the Solution Explorer used to render as a blank white void — no hint
    // that anything could be opened there. It now carries an empty-state placeholder, exactly like
    // the Editor tab's "No file open" label, and swaps back to the tree once a solution loads.
    [TestClass]
    public class ProjectTreeEmptyStateTests
    {
        private static Label Placeholder(ProjectTreePanel panel)
        {
            foreach (Control c in panel.Controls)
            {
                if (c is Label label) return label;
            }
            return null;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            foreach (Control c in panel.Controls)
            {
                if (c is TreeView tree) return tree;
            }
            return null;
        }

        [TestMethod]
        public void WithNothingLoaded_ThePlaceholderIsShownInsteadOfTheTree()
        {
            using (var panel = new ProjectTreePanel())
            {
                Label placeholder = Placeholder(panel);
                TreeView tree = Tree(panel);

                Assert.IsNotNull(placeholder, "Solution Explorer has no empty-state placeholder");
                Assert.IsNotNull(tree);
                StringAssert.Contains(placeholder.Text, "No solution open");
                StringAssert.Contains(placeholder.Text, "Ctrl+O", "the placeholder should name the way out");
                Assert.IsTrue(placeholder.Visible, "placeholder must be visible while the tree is empty");
                Assert.IsFalse(tree.Visible,
                    "the tree and the placeholder both Dock=Fill — only one may be visible");
            }
        }

        [TestMethod]
        public void LoadingASolution_SwapsThePlaceholderForTheTree()
        {
            using (var panel = new ProjectTreePanel())
            {
                var solution = new SolutionModel(@"C:\proj\pyproject.toml", "Proj");
                solution.Projects.Add(new ProjectModel(@"C:\proj\pyproject.toml", "Proj", solution));

                panel.LoadSolution(solution);

                Assert.IsFalse(Placeholder(panel).Visible, "placeholder must hide once the tree has nodes");
                Assert.IsTrue(Tree(panel).Visible);
            }
        }

        [TestMethod]
        public void ClosingTheSolution_BringsThePlaceholderBack()
        {
            using (var panel = new ProjectTreePanel())
            {
                var solution = new SolutionModel(@"C:\proj\pyproject.toml", "Proj");
                solution.Projects.Add(new ProjectModel(@"C:\proj\pyproject.toml", "Proj", solution));
                panel.LoadSolution(solution);

                panel.Clear();

                Assert.IsTrue(Placeholder(panel).Visible, "closing a solution must restore the empty state");
                Assert.IsFalse(Tree(panel).Visible);
            }
        }

        [TestMethod]
        public void LoadingANullSolution_AlsoRestoresThePlaceholder()
        {
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(null);
                Assert.IsTrue(Placeholder(panel).Visible);
            }
        }
    }
}
