using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 451 (SPPFE Filtering polish): when a search or kind filter matches nothing but a solution
    /// IS loaded, the tree used to go blank — indistinguishable from an empty solution. It now shows a
    /// "No … match …" placeholder naming the term (and kind), and restores the tree when the filter
    /// is cleared.
    /// </summary>
    [TestClass]
    public class ProjectTreeNoMatchStateTests
    {
        private static ProjectTreePanel Load()
        {
            var sln = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var proj = new ProjectModel(@"C:\ws\src\pkg\pyproject.toml", "pkg", sln);
            sln.Projects.Add(proj);
            var t = new TypeModel("Vehicle", "pkg", TypeKind.ConcreteClass, proj) { SourceFilePath = @"C:\ws\src\pkg\m.py" };
            t.Members.Add(new MemberModel("start_engine", MemberKind.Method) { SourceLineNumber = 4 });
            proj.Types.Add(t);
            proj.Types.Add(new TypeModel("Drivable", "pkg", TypeKind.Interface, proj) { SourceFilePath = @"C:\ws\src\pkg\m.py", SourceLineNumber = 20 });
            var panel = new ProjectTreePanel();
            panel.LoadSolution(sln);
            return panel;
        }

        private static Label Placeholder(ProjectTreePanel panel)
        {
            foreach (Control c in panel.Controls)
                if (c is Label label) return label;
            return null;
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            return (TreeView)typeof(ProjectTreePanel).GetField("_treeView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static TextBox SearchBox(ProjectTreePanel panel)
        {
            return (TextBox)typeof(ProjectTreePanel).GetField("_searchBox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        private static ComboBox Combo(ProjectTreePanel panel)
        {
            return (ComboBox)typeof(ProjectTreePanel).GetField("_kindCombo", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
        }

        [TestMethod]
        public void SearchWithNoMatches_ShowsNoMatchPlaceholderNamingTheTerm()
        {
            using (var panel = Load())
            {
                SearchBox(panel).Text = "zzznotathing";

                Label ph = Placeholder(panel);
                Assert.IsTrue(ph.Visible, "the no-match placeholder must be shown when a filter empties the tree");
                Assert.IsFalse(Tree(panel).Visible, "the empty tree must yield to the placeholder");
                StringAssert.Contains(ph.Text, "zzznotathing", "the message must quote the search term");
                StringAssert.Contains(ph.Text, "No ", "the message must read as a no-results state");
                Assert.IsFalse(ph.Text.Contains("No solution open"),
                    "a solution IS open — the idle placeholder would be misleading");
            }
        }

        [TestMethod]
        public void ClearingTheSearch_RestoresTheTree()
        {
            using (var panel = Load())
            {
                SearchBox(panel).Text = "zzznotathing";
                Assert.IsTrue(Placeholder(panel).Visible);

                SearchBox(panel).Text = string.Empty;

                Assert.IsFalse(Placeholder(panel).Visible, "clearing the search restores the tree");
                Assert.IsTrue(Tree(panel).Visible);
            }
        }

        [TestMethod]
        public void SearchThatMatches_KeepsTheTreeVisible()
        {
            using (var panel = Load())
            {
                SearchBox(panel).Text = "Vehicle";

                Assert.IsFalse(Placeholder(panel).Visible, "a matching search must not show the no-match placeholder");
                Assert.IsTrue(Tree(panel).Visible);
            }
        }

        [TestMethod]
        public void KindFilterWithNoMatches_NamesTheKind()
        {
            using (var panel = Load())
            {
                // No enums exist in this solution, so the Enums filter empties the tree.
                Combo(panel).SelectedIndex = Combo(panel).FindStringExact("Enums");

                Label ph = Placeholder(panel);
                Assert.IsTrue(ph.Visible);
                StringAssert.Contains(ph.Text, "enums", "the message should name the filtered kind");
            }
        }
    }
}
