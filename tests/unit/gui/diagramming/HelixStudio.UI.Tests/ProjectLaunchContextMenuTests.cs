using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.UI.Tests
{
    /// <summary>
    /// An executable package can be launched from its own Solution Explorer node, without first
    /// being made the startup package. Before this, running a package that was not the startup one
    /// meant a detour through "Set as Startup Package" — the Run button had no other target.
    ///
    /// A library must NOT offer them: there is nothing to launch, and an enabled verb that fails
    /// on click is worse than no verb.
    /// </summary>
    [TestClass]
    public class ProjectLaunchContextMenuTests
    {
        private static List<string> MenuItemsFor(ProjectModel project)
        {
            using (var panel = new ProjectTreePanel())
            {
                var node = new TreeNode(project.Name) { Tag = project };
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(node))
                {
                    var captions = new List<string>();
                    foreach (ToolStripItem item in menu.Items)
                        if (!(item is ToolStripSeparator)) captions.Add(item.Text);
                    return captions;
                }
            }
        }

        // IsAppProject decides runnability from the package on DISK (a resolvable entry file),
        // not from a flag, so the fixture is a real directory with a real main.py.
        private static ProjectModel Executable()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix-launchmenu-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "main.py"),
                "def main():\r\n    return 0\r\n");
            string manifest = System.IO.Path.Combine(dir, "pyproject.toml");
            System.IO.File.WriteAllText(manifest, "[project]\r\nname = \"train-sample\"\r\n");
            var p = new ProjectModel(manifest, "train-sample", new SolutionModel(manifest, "fixture"));
            p.Kind = ProjectKind.App;
            p.Language = ProjectLanguage.Python;
            return p;
        }

        [TestMethod]
        public void AnExecutablePackageOffersRunAndStartDebugging()
        {
            List<string> items = MenuItemsFor(Executable());
            CollectionAssert.Contains(items, "Run",
                "an executable package must be launchable from its own node; items were: "
                + string.Join(" | ", items.ToArray()));
            CollectionAssert.Contains(items, "Start Debugging",
                "items were: " + string.Join(" | ", items.ToArray()));
        }

        /// <summary>They come first: launching what you pointed at is the common intent.</summary>
        [TestMethod]
        public void TheLaunchVerbsComeBeforeTheStartupVerb()
        {
            List<string> items = MenuItemsFor(Executable());
            int run = items.IndexOf("Run");
            int startup = items.FindIndex(t => t != null && t.Contains("Startup"));
            Assert.IsTrue(run >= 0, "no Run item");
            if (startup >= 0)
                Assert.IsTrue(run < startup,
                    "Run should precede the startup verb, but the order was: "
                    + string.Join(" | ", items.ToArray()));
        }

        [TestMethod]
        public void ALibraryOffersNeither()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "helix-launchmenu-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string manifest = System.IO.Path.Combine(dir, "pyproject.toml");
            System.IO.File.WriteAllText(manifest, "[project]\r\nname = \"sample-common\"\r\n");
            var library = new ProjectModel(manifest, "sample-common", new SolutionModel(manifest, "fixture"));
            library.Kind = ProjectKind.ClassLibrary;
            library.Language = ProjectLanguage.Python;
            List<string> items = MenuItemsFor(library);
            CollectionAssert.DoesNotContain(items, "Run",
                "a library has no entry point, so offering to run it would fail on click");
            CollectionAssert.DoesNotContain(items, "Start Debugging");
        }

        /// <summary>The menu items must actually be wired to the events the host listens on.</summary>
        [TestMethod]
        public void ClickingThemRaisesTheHostEvents()
        {
            using (var panel = new ProjectTreePanel())
            {
                ProjectModel project = Executable();
                var node = new TreeNode(project.Name) { Tag = project };

                object ranFor = null, debuggedFor = null;
                panel.RunProjectRequested += (s, e) => ranFor = e.Item;
                panel.DebugProjectRequested += (s, e) => debuggedFor = e.Item;

                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(node))
                {
                    foreach (ToolStripItem item in menu.Items)
                    {
                        if (item.Text == "Run") item.PerformClick();
                        if (item.Text == "Start Debugging") item.PerformClick();
                    }
                }

                Assert.AreSame(project, ranFor, "'Run' did not raise RunProjectRequested for the node's package");
                Assert.AreSame(project, debuggedFor, "'Start Debugging' did not raise DebugProjectRequested");
            }
        }
    }
}
