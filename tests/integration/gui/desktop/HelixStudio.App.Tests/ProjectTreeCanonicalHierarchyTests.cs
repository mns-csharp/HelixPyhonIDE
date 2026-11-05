using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.UI.Controls;

namespace HelixPythonNamespace.App.Tests
{
    [TestClass]
    public class ProjectTreeCanonicalHierarchyTests
    {
        private sealed class CustomNodeProvider : ProjectTreePanel.IProjectNodeProvider
        {
            public bool CanBuild(ProjectModel project) { return project != null; }

            public void AppendNodes(ProjectTreePanel owner, TreeNode projectNode, ProjectModel project)
            {
                projectNode.Nodes.Add(new TreeNode("Extension Contribution") { Tag = project });
            }
        }

        private static TreeView Tree(ProjectTreePanel panel)
        {
            FieldInfo field = typeof(ProjectTreePanel).GetField(
                "_treeView",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (TreeView)field.GetValue(panel);
        }

        [TestMethod]
        public void PythonProjectShowsKindAndCanonicalStructuralNodes()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var library = new ProjectModel(
                @"C:\ws\lib\pyproject.toml", "lib", solution)
            {
                Kind = ProjectKind.ClassLibrary
            };
            var app = new ProjectModel(
                @"C:\ws\app\pyproject.toml", "app", solution)
            {
                Kind = ProjectKind.App,
                IsStartup = true
            };
            var library2 = new ProjectModel(
                @"C:\ws\lib2\pyproject.toml", "lib2", solution)
            {
                Kind = ProjectKind.ClassLibrary
            };
            app.ProjectReferences.Add(library);
            app.ProjectReferences.Add(library2);
            solution.Projects.Add(app);
            solution.Projects.Add(library);
            solution.Projects.Add(library2);

            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode appNode = Tree(panel).Nodes[0].Nodes
                    .Cast<TreeNode>()
                    .Single(n => ReferenceEquals(n.Tag, app));
                var labels = appNode.Nodes.Cast<TreeNode>().Select(n => n.Text).ToList();

                // ProjectKind.App renders as "Console App" (ProjectTreePanel.ProjectKindLabel),
                // matching the "Python Console App" template it comes from. "[Executable]" is a
                // different kind (ProjectKind.Executable, used by the manifest languages).
                StringAssert.Contains(appNode.Text, "[Console App]");
                StringAssert.Contains(appNode.Text, "(Startup)");

                // Two structural nodes, both of which say something. The four that used to sit here
                // permanently empty — Tests, Documentation, Generated Files — and the redundant
                // Project Configuration are gone; Resources appears only when the package has
                // non-source files, and this in-memory project has none.
                CollectionAssert.IsSubsetOf(new[] { "Python Environment", "Dependencies (2)" }, labels);
                foreach (string gone in new[]
                    { "Tests", "Documentation", "Generated Files", "Project Configuration", "Resources" })
                    Assert.IsFalse(labels.Contains(gone), gone + " must not be shown for a Python package");

                TreeNode dependencies = appNode.Nodes.Cast<TreeNode>()
                    .Single(n => n.Text == "Dependencies (2)");
                Assert.AreEqual("lib", dependencies.Nodes[0].Text);
                Assert.AreEqual("lib2", dependencies.Nodes[1].Text);
            }
        }

        [TestMethod]
        public void DependenciesMenu_OffersAddReferenceForItsOwningProject()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var project = new ProjectModel(@"C:\ws\app\pyproject.toml", "app", solution)
            {
                Kind = ProjectKind.App
            };
            solution.Projects.Add(project);

            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode dependencies = Tree(panel).Nodes[0].Nodes[0].Nodes
                    .Cast<TreeNode>().Single(n => n.Text == "Dependencies");
                object requested = null;
                panel.AddProjectReferenceRequested += (s, e) => requested = e.Item;

                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(dependencies))
                {
                    ToolStripItem add = menu.Items.Cast<ToolStripItem>()
                        .Single(i => i.Text == "Add Reference...");
                    add.PerformClick();
                }

                var group = requested as ProjectTreePanel.StructuralGroup;
                Assert.IsNotNull(group, "Dependencies must route the command with its structural context");
                Assert.AreSame(project, group.Project);
            }
        }

        [TestMethod]
        public void ReferenceRefresh_PreservesExpandedSolutionAndProjectRows()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var app = new ProjectModel(@"C:\ws\app\pyproject.toml", "app", solution);
            var library = new ProjectModel(@"C:\ws\lib\pyproject.toml", "lib", solution);
            solution.Projects.Add(app);
            solution.Projects.Add(library);

            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode root = Tree(panel).Nodes[0];
                TreeNode appNode = root.Nodes.Cast<TreeNode>().Single(n => ReferenceEquals(n.Tag, app));
                root.Expand();
                appNode.Expand();
                Assert.IsTrue(appNode.IsExpanded, "precondition: project row is expanded");

                app.ProjectReferences.Add(library);
                panel.LoadSolutionPreservingExpansion(solution);

                TreeNode refreshedRoot = Tree(panel).Nodes[0];
                TreeNode refreshedApp = refreshedRoot.Nodes.Cast<TreeNode>()
                    .Single(n => ReferenceEquals(n.Tag, app));
                Assert.IsTrue(refreshedRoot.IsExpanded, "solution expansion must survive reference refresh");
                Assert.IsTrue(refreshedApp.IsExpanded, "project expansion must survive reference refresh");
                TreeNode dependencies = refreshedApp.Nodes.Cast<TreeNode>()
                    .Single(n => n.Text == "Dependencies (1)");
                Assert.IsTrue(dependencies.Nodes.Cast<TreeNode>().Any(n => n.Text == "lib"));
            }
        }

        [TestMethod]
        public void RenameReplay_PreservesExpandedAndCollapsedProjectRows()
        {
            var before = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var renamed = new ProjectModel(@"C:\ws\old_name\pyproject.toml", "old_name", before);
            var collapsed = new ProjectModel(@"C:\ws\other\pyproject.toml", "other", before);
            before.Projects.Add(renamed);
            before.Projects.Add(collapsed);

            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(before);
                TreeNode oldRoot = Tree(panel).Nodes[0];
                oldRoot.Expand();
                oldRoot.Nodes.Cast<TreeNode>().Single(n => ReferenceEquals(n.Tag, renamed)).Expand();

                var mapped = panel.GetExpandedPathsAfterRename("old_name", "new_name");

                var after = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
                var renamedAfter = new ProjectModel(@"C:\ws\new_name\pyproject.toml", "new_name", after);
                var collapsedAfter = new ProjectModel(@"C:\ws\other\pyproject.toml", "other", after);
                after.Projects.Add(renamedAfter);
                after.Projects.Add(collapsedAfter);
                panel.LoadSolution(after);
                panel.ApplyExpandedPaths(mapped);

                TreeNode newRoot = Tree(panel).Nodes[0];
                TreeNode renamedNode = newRoot.Nodes.Cast<TreeNode>()
                    .Single(n => ReferenceEquals(n.Tag, renamedAfter));
                TreeNode collapsedNode = newRoot.Nodes.Cast<TreeNode>()
                    .Single(n => ReferenceEquals(n.Tag, collapsedAfter));
                Assert.IsTrue(newRoot.IsExpanded, "solution state must survive project rename");
                Assert.IsTrue(renamedNode.IsExpanded, "renamed project must remain expanded");
                Assert.IsFalse(collapsedNode.IsExpanded, "collapsed sibling must remain collapsed");
                Assert.IsTrue(mapped.Any(p => p.IndexOf("new_name", StringComparison.Ordinal) >= 0));
                Assert.IsFalse(mapped.Any(p => p.IndexOf("old_name", StringComparison.Ordinal) >= 0));
            }
        }

        [TestMethod]
        public void ProjectMenus_OfferBoundaryAwareMoveCommands()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var first = new ProjectModel(@"C:\ws\first\pyproject.toml", "first", solution);
            var last = new ProjectModel(@"C:\ws\last\pyproject.toml", "last", solution);
            solution.Projects.Add(first);
            solution.Projects.Add(last);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode firstNode = Tree(panel).Nodes[0].Nodes[0];
                TreeNode lastNode = Tree(panel).Nodes[0].Nodes[1];
                using (ContextMenuStrip firstMenu = panel.BuildContextMenuForNode(firstNode))
                using (ContextMenuStrip lastMenu = panel.BuildContextMenuForNode(lastNode))
                {
                    Assert.IsFalse(firstMenu.Items.Cast<ToolStripItem>().Single(i => i.Text == "Move Up").Enabled);
                    Assert.IsTrue(firstMenu.Items.Cast<ToolStripItem>().Single(i => i.Text == "Move Down").Enabled);
                    Assert.IsTrue(lastMenu.Items.Cast<ToolStripItem>().Single(i => i.Text == "Move Up").Enabled);
                    Assert.IsFalse(lastMenu.Items.Cast<ToolStripItem>().Single(i => i.Text == "Move Down").Enabled);
                }
            }
        }

        [TestMethod]
        public void ProjectF2_EditsOnlyName_AndCancelRestoresMarkers()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var project = new ProjectModel(@"C:\ws\pyproject.toml", "app", solution)
            {
                Kind = ProjectKind.App,
                IsStartup = true
            };
            solution.Projects.Add(project);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode node = Tree(panel).Nodes[0].Nodes[0];
                Tree(panel).SelectedNode = node;
                StringAssert.Contains(node.Text, "[Console App]");
                StringAssert.Contains(node.Text, "(Startup)");

                panel.BeginRenameSelectedNode();
                Assert.AreEqual("app", node.Text,
                    "the native edit box must receive only the real project name");

                MethodInfo afterEdit = typeof(ProjectTreePanel).GetMethod(
                    "OnTreeAfterLabelEdit", BindingFlags.Instance | BindingFlags.NonPublic);
                afterEdit.Invoke(panel, new object[] { panel,
                    new NodeLabelEditEventArgs(node, null) });
                StringAssert.Contains(node.Text, "[Console App]");
                StringAssert.Contains(node.Text, "(Startup)");
            }
        }

        [TestMethod]
        public void ProjectRenameCommit_RestoresMarkersAroundTheNewName()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var project = new ProjectModel(@"C:\ws\pyproject.toml", "old_name", solution)
            {
                Kind = ProjectKind.App,
                IsStartup = true
            };
            solution.Projects.Add(project);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode node = Tree(panel).Nodes[0].Nodes[0];
                Tree(panel).SelectedNode = node;
                panel.RenameRequested += (s, e) =>
                    panel.UpdateProjectNodeDisplayName(e.Node, e.NewName);

                panel.BeginRenameSelectedNode();
                MethodInfo afterEdit = typeof(ProjectTreePanel).GetMethod(
                    "OnTreeAfterLabelEdit", BindingFlags.Instance | BindingFlags.NonPublic);
                afterEdit.Invoke(panel, new object[] { panel,
                    new NodeLabelEditEventArgs(node, "new_name") });

                Assert.AreEqual("new_name  [Console App]  (Startup)", node.Text);
            }
        }

        /// <summary>
        /// Double-clicking the project row opens its pyproject.toml (§5.5) — which is why the
        /// separate "Project Configuration" row was removable rather than merely unwanted.
        /// </summary>
        [TestMethod]
        public void DoubleClickingTheProjectRow_OpensItsManifest()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix-tree-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string toml = Path.Combine(dir, "pyproject.toml");
            File.WriteAllText(toml, "[project]\nname = \"lib\"\n");

            var solution = new SolutionModel(Path.Combine(dir, "solution.toml"), "ws");
            var project = new ProjectModel(toml, "lib", solution) { Kind = ProjectKind.ClassLibrary };
            solution.Projects.Add(project);

            using (var panel = new ProjectTreePanel())
            {
                string opened = null;
                panel.FileDoubleClicked += (s, path) => opened = path;
                panel.LoadSolution(solution);

                TreeNode projectNode = Tree(panel).Nodes[0].Nodes[0];
                MethodInfo handler = typeof(ProjectTreePanel).GetMethod("OnTreeNodeDoubleClick",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(handler, "ProjectTreePanel.OnTreeNodeDoubleClick expected");
                handler.Invoke(panel, new object[]
                {
                    null,
                    new TreeNodeMouseClickEventArgs(projectNode, MouseButtons.Left, 2, 0, 0),
                });

                Assert.AreEqual(toml, opened, "the project row opens the manifest itself");
            }
        }

        [TestMethod]
        public void RegisteredProjectNodeProvider_CanOwnStructureWithoutEditingTreeControl()
        {
            var solution = new SolutionModel(@"C:\ws\pyproject.toml", "ws");
            var project = new ProjectModel(@"C:\ws\lib\pyproject.toml", "lib", solution);
            solution.Projects.Add(project);

            using (var panel = new ProjectTreePanel())
            {
                panel.RegisterProjectNodeProvider("test.extension", new CustomNodeProvider(), 1000);
                panel.LoadSolution(solution);

                TreeNode projectNode = Tree(panel).Nodes[0].Nodes[0];
                Assert.AreEqual(1, projectNode.Nodes.Count);
                Assert.AreEqual("Extension Contribution", projectNode.Nodes[0].Text);
            }
        }

        [TestMethod]
        public void NativeProjectShowsOnlyBackedStructuralNodes_AndNoTestsGroup()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix-native-tree-" + Guid.NewGuid().ToString("N"));
            string package = Path.Combine(root, "lib_core");
            Directory.CreateDirectory(Path.Combine(package, "resources"));
            Directory.CreateDirectory(Path.Combine(package, "docs"));
            Directory.CreateDirectory(Path.Combine(package, "generated"));
            Directory.CreateDirectory(Path.Combine(root, "cmake"));
            File.WriteAllText(Path.Combine(root, "CMakeLists.txt"), "add_subdirectory(lib_core)");
            File.WriteAllText(Path.Combine(package, "CMakeLists.txt"), "add_library(lib_core)");
            File.WriteAllText(Path.Combine(root, "cmake", "warnings.cmake"), "# warnings");
            File.WriteAllText(Path.Combine(package, "resources", "kernel.bin"), "resource");
            File.WriteAllText(Path.Combine(package, "docs", "README.md"), "docs");
            File.WriteAllText(Path.Combine(package, "generated", "version.h"), "#define VERSION 1");

            try
            {
                string rootCMake = Path.Combine(root, "CMakeLists.txt");
                var solution = new SolutionModel(rootCMake, "native");
                var project = new ProjectModel(Path.Combine(package, "lib_core.cmake-target"), "lib_core", solution)
                {
                    Language = ProjectLanguage.Cpp,
                    Kind = ProjectKind.CudaLibrary,
                    HasCudaSources = true
                };
                solution.Projects.Add(project);

                using (var panel = new ProjectTreePanel())
                {
                    panel.LoadSolution(solution);
                    TreeNode projectNode = Tree(panel).Nodes[0].Nodes[0];
                    var labels = projectNode.Nodes.Cast<TreeNode>().Select(n => n.Text).ToList();

                    Assert.IsFalse(labels.Contains("Tests"), "native tests are separate target packages");
                    CollectionAssert.IsSubsetOf(new[]
                    {
                        "Native Toolchain", "Resources", "Documentation", "Generated Files", "Project Configuration"
                    }, labels);
                    Assert.IsFalse(labels.Contains("Dependencies"), "empty native dependency groups stay hidden");

                    TreeNode toolchain = projectNode.Nodes.Cast<TreeNode>().Single(n => n.Text == "Native Toolchain");
                    Assert.IsTrue(toolchain.Nodes.Cast<TreeNode>().Any(n => n.Text == "Workspace CMakeLists.txt"));
                    Assert.IsTrue(toolchain.Nodes.Cast<TreeNode>().Any(n => n.Text == "warnings.cmake"));

                    TreeNode resources = projectNode.Nodes.Cast<TreeNode>().Single(n => n.Text == "Resources");
                    Assert.IsTrue(resources.Nodes[0].Tag is ProjectTreePanel.FileGroup);
                    TreeNode config = projectNode.Nodes.Cast<TreeNode>().Single(n => n.Text == "Project Configuration");
                    var configFile = (ProjectTreePanel.FileGroup)config.Tag;
                    Assert.AreEqual(Path.Combine(package, "CMakeLists.txt"), configFile.FilePath);
                    Assert.IsTrue(File.Exists(configFile.FilePath), "configuration must open a real file");
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
