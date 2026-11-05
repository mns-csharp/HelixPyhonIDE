using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.UI.Tests
{
    /// <summary>
    /// "Add Jupyter Notebook..." on a Python project node.
    ///
    /// There was no way to create a notebook anywhere in the IDE. "Add File..." looks like the
    /// place, but it runs every typed name through <c>MainForm.NormalizeNewFilePath</c>, which
    /// forces a `.py` extension — so a user who typed <c>analysis.ipynb</c> got
    /// <c>analysis.py</c>. The verb is separate for that reason, not for tidiness.
    ///
    /// It is Python-only: the notebook kernel is a Python process, so offering it on a C++ project
    /// would be an offer the IDE cannot keep — the same rule the scaffolding verbs already follow.
    /// </summary>
    [TestClass]
    public class NotebookContextMenuTests
    {
        private const string Verb = "Add Jupyter Notebook...";

        private static ProjectModel Project(ProjectLanguage language)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-nbmenu-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string manifest = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(manifest, "[project]\r\nname = \"analysis\"\r\n");
            var project = new ProjectModel(manifest, "analysis", new SolutionModel(manifest, "fixture"));
            project.Kind = ProjectKind.App;
            project.Language = language;
            return project;
        }

        private static List<string> MenuItemsFor(TreeNode node)
        {
            using (var panel = new ProjectTreePanel())
            using (ContextMenuStrip menu = panel.BuildContextMenuForNode(node))
            {
                var captions = new List<string>();
                foreach (ToolStripItem item in menu.Items)
                    if (!(item is ToolStripSeparator)) captions.Add(item.Text);
                return captions;
            }
        }

        [TestMethod]
        public void APythonProjectNode_OffersIt()
        {
            ProjectModel project = Project(ProjectLanguage.Python);
            List<string> items = MenuItemsFor(new TreeNode(project.Name) { Tag = project });

            CollectionAssert.Contains(items, Verb, "items were: " + string.Join(" | ", items.ToArray()));
        }

        /// <summary>
        /// Beside "Add File...", because that is where a user goes looking for it — and where they
        /// went looking before, and got a `.py` file.
        /// </summary>
        [TestMethod]
        public void ItSitsNextToAddFile()
        {
            ProjectModel project = Project(ProjectLanguage.Python);
            List<string> items = MenuItemsFor(new TreeNode(project.Name) { Tag = project });

            int addFile = items.IndexOf("Add File...");
            Assert.IsTrue(addFile >= 0, "no Add File... item: " + string.Join(" | ", items.ToArray()));
            Assert.AreEqual(addFile + 1, items.IndexOf(Verb),
                "items were: " + string.Join(" | ", items.ToArray()));
        }

        [TestMethod]
        public void ACppProjectNode_DoesNot()
        {
            ProjectModel project = Project(ProjectLanguage.Cpp);
            List<string> items = MenuItemsFor(new TreeNode(project.Name) { Tag = project });

            CollectionAssert.DoesNotContain(items, Verb,
                "the kernel is Python; items were: " + string.Join(" | ", items.ToArray()));
        }

        /// <summary>
        /// A folder node gets it too, and targets that folder — a notebook usually belongs beside
        /// the data it reads, not at the package root.
        /// </summary>
        [TestMethod]
        public void AFolderNodeOffersIt_AndRaisesTheRequestWithTheFolder()
        {
            ProjectModel project = Project(ProjectLanguage.Python);
            var folder = new ProjectTreePanel.FolderGroup { Project = project, RelativePath = "notebooks" };
            var node = new TreeNode("notebooks") { Tag = folder };

            object seen = null;
            using (var panel = new ProjectTreePanel())
            using (ContextMenuStrip menu = panel.BuildContextMenuForNode(node))
            {
                panel.AddNotebookRequested += (s, e) => seen = e.Item;
                ToolStripItem item = null;
                foreach (ToolStripItem candidate in menu.Items)
                    if (candidate.Text == Verb) { item = candidate; break; }

                Assert.IsNotNull(item, "a folder node must offer the verb too");
                item.PerformClick();
            }

            Assert.IsInstanceOfType(seen, typeof(ProjectTreePanel.FolderGroup),
                "the request must carry the clicked folder, so the file lands in it");
        }
    }
}
