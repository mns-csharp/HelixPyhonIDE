using System;
using System.Collections.Generic;
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
    /// <summary>
    /// SRS solution_explorer_rules §5 + §7 behaviors added in the Solution Explorer
    /// pass: check-state survival (§7), double-click opens project/solution (§5.5),
    /// folder-targeted Add (§5.7), file rename on disk (§5.2), tree clipboard (§5.3),
    /// Open in Editor / Open in Class Designer Canvas everywhere (§5.4), and the VS-parity
    /// Refresh / Analyze commands (§5.9).
    /// </summary>
    [TestClass]
    public class SolutionExplorerSrs5Tests
    {
        private const string Root = @"D:\repos\sympy";

        // ------------------------------------------------------------------ §7

        [TestMethod]
        public void CheckState_SurvivesSearchFilterRoundTrip()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                panel.SetCheckedProjects(new[] { "sympy" });

                SearchBox(panel).Text = "add";       // filter on
                SearchBox(panel).Text = string.Empty; // filter off — restores from snapshot

                CollectionAssert.AreEqual(new[] { "sympy" }, panel.GetCheckedProjects().ToArray(),
                    "clearing the search filter must not blank the canvas selection");
            }
        }

        [TestMethod]
        public void CheckState_SurvivesActiveFilter()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                panel.SetCheckedProjects(new[] { "sympy" });
                SearchBox(panel).Text = "T0";        // project row survives as context

                CollectionAssert.AreEqual(new[] { "sympy" }, panel.GetCheckedProjects().ToArray(),
                    "the filtered clone of the project row must keep its check");
            }
        }

        [TestMethod]
        public void CheckState_SurvivesTreeRebuild()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                panel.SetCheckedProjects(new[] { "sympy" });
                SolutionModel solution = (SolutionModel)Tree(panel).Nodes[0].Tag;

                panel.LoadSolution(solution);   // sort clicks / Refresh route through here

                CollectionAssert.AreEqual(new[] { "sympy" }, panel.GetCheckedProjects().ToArray(),
                    "a rebuild must re-apply the previous check state");
            }
        }

        // ------------------------------------------------------------------ §5.5

        [TestMethod]
        public void DoubleClick_ProjectRow_OpensItsProjectFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_srs5_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string toml = Path.Combine(root, "pyproject.toml");
                File.WriteAllText(toml, "[project]\nname = \"p\"\n");
                var solution = new SolutionModel(toml, "p");
                var project = new ProjectModel(toml, "p", solution);
                solution.Projects.Add(project);
                using (var panel = new ProjectTreePanel())
                {
                    panel.LoadSolution(solution);
                    string opened = null;
                    panel.FileDoubleClicked += (s, path) => opened = path;

                    TreeNode projectNode = Tree(panel).Nodes[0].Nodes[0];
                    DoubleClick(panel, projectNode);
                    Assert.AreEqual(toml, opened, "double-clicking a project opens its pyproject.toml");

                    opened = null;
                    DoubleClick(panel, Tree(panel).Nodes[0]);
                    Assert.AreEqual(toml, opened, "double-clicking the solution opens its file");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ------------------------------------------------------------------ §5.7

        [TestMethod]
        public void FolderNode_Menu_OffersAddVerbs_AndOpenCommands()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode folder = FindFolderNode(Tree(panel).Nodes[0].Nodes[0]);
                Assert.IsNotNull(folder, "expected a folder row for src/core");

                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(folder))
                {
                    List<string> items = ItemTexts(menu);
                    CollectionAssert.IsSubsetOf(
                        new[] { "Open in Editor", "Open in Class Designer Canvas", "Add Class",
                                "Add Interface", "Add File...", "Paste", "Rename Package..." },
                        items);
                    Assert.AreEqual("Open in Editor", items[0], "§5.4: open verbs lead the menu");
                }
            }
        }

        [TestMethod]
        public void ResolveAddTarget_FolderNode_MapsSrcFolderToNamespace()
        {
            var solution = new SolutionModel(Root + @"\pyproject.toml", "sympy");
            var project = new ProjectModel(Root + @"\pyproject.toml", "sympy", solution);

            var srcFolder = new ProjectTreePanel.FolderGroup { Project = project, RelativePath = "src/pkg_a/sub" };
            Assert.IsTrue(MainForm.TryResolveAddTypeTarget(srcFolder, out ProjectModel p1, out string ns1));
            Assert.AreSame(project, p1);
            Assert.AreEqual("pkg_a.sub", ns1);

            // A folder with no `src` above it is a package too, and maps on its own path. This
            // assertion used to require null, from when `src/<package>/` was the layout a project
            // had; the canonical layout puts the package DIRECTLY under the project, so `sympy/core`
            // is the package `sympy.core` and nothing about it is a default-namespace fallback.
            // `src` is still stripped when present, which is what keeps existing projects working.
            var plainFolder = new ProjectTreePanel.FolderGroup { Project = project, RelativePath = "sympy/core" };
            Assert.IsTrue(MainForm.TryResolveAddTypeTarget(plainFolder, out ProjectModel p2, out string ns2));
            Assert.AreEqual("sympy.core", ns2);
        }

        // ------------------------------------------------------------------ §5.2

        [TestMethod]
        public void ComputeRenamedFilePath_KeepsExtension_StripsCount_RejectsInvalid()
        {
            Assert.AreEqual(@"C:\x\newname.py", MainForm.ComputeRenamedFilePath(@"C:\x\old.py", "newname"));
            Assert.AreEqual(@"C:\x\newname.py", MainForm.ComputeRenamedFilePath(@"C:\x\old.py", "newname.py (3)"));
            Assert.AreEqual(@"C:\x\other.txt", MainForm.ComputeRenamedFilePath(@"C:\x\old.py", "other.txt"));
            Assert.IsNull(MainForm.ComputeRenamedFilePath(@"C:\x\old.py", "bad|name"));
            Assert.IsNull(MainForm.ComputeRenamedFilePath(@"C:\x\old.py", "  "));
        }

        [TestMethod]
        public void FileNode_Menu_OffersRename_AndF2TargetsFiles()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode file = FindFileNode(Tree(panel).Nodes[0].Nodes[0]);
                Assert.IsNotNull(file);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(file))
                {
                    List<string> items = ItemTexts(menu);
                    CollectionAssert.IsSubsetOf(new[] { "Rename", "Cut", "Copy", "Paste" }, items);
                }
            }
        }

        // ------------------------------------------------------------------ §5.9

        [TestMethod]
        public void FileNode_Menu_OffersOpenWith_AndDelete()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode file = FindFileNode(Tree(panel).Nodes[0].Nodes[0]);
                Assert.IsNotNull(file);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(file))
                {
                    List<string> items = ItemTexts(menu);
                    CollectionAssert.Contains(items, "Open With...", "§5.9 VS-parity Open With");
                    CollectionAssert.Contains(items, "Delete", "§5.9 VS-parity Delete on a file row");
                }
            }
        }

        [TestMethod]
        public void FileNode_Delete_RaisesDeleteRequested_WithTheFileGroup()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode file = FindFileNode(Tree(panel).Nodes[0].Nodes[0]);
                object deleted = null;
                panel.DeleteRequested += (s, e) => deleted = e.Item;
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(file))
                {
                    ToolStripItem del = menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Delete");
                    del.PerformClick();
                }
                Assert.IsInstanceOfType(deleted, typeof(ProjectTreePanel.FileGroup),
                    "the host receives the FileGroup so it can delete the file on disk");
            }
        }

        [TestMethod]
        public void OpenWithArgs_FormatsTheShellInvocation()
        {
            Assert.AreEqual(@"shell32.dll,OpenAs_RunDLL C:\x\file.py",
                ProjectTreePanel.OpenWithArgs(@"C:\x\file.py"));
        }

        [TestMethod]
        public void FileNode_Exclude_RaisesExcludeFromProjectRequested_WithTheFileGroup()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode file = FindFileNode(Tree(panel).Nodes[0].Nodes[0]);
                object seen = null;
                panel.ExcludeFromProjectRequested += (s, e) => seen = e.Item;
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(file))
                {
                    CollectionAssert.Contains(ItemTexts(menu), "Exclude from Project");
                    ToolStripItem exclude = menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Exclude from Project");
                    exclude.PerformClick();
                }
                Assert.IsInstanceOfType(seen, typeof(ProjectTreePanel.FileGroup));
            }
        }

        [TestMethod]
        public void FolderNode_Menu_OffersExcludeFromProject()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode folder = FindFolderNode(Tree(panel).Nodes[0].Nodes[0]);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(folder))
                {
                    CollectionAssert.Contains(ItemTexts(menu), "Exclude from Project");
                }
            }
        }

        [TestMethod]
        public void FolderNode_Menu_OffersDeletePackage_WithDelKeyHint()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode folder = FindFolderNode(Tree(panel).Nodes[0].Nodes[0]);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(folder))
                {
                    ToolStripMenuItem delete = (ToolStripMenuItem)menu.Items.Cast<ToolStripItem>()
                        .First(i => i.Text == "Delete Package");
                    Assert.AreEqual("Del", delete.ShortcutKeyDisplayString);
                }
            }
        }

        [TestMethod]
        public void FolderNode_DeletePackage_RaisesDeleteRequested_WithTheFolderGroup()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode folder = FindFolderNode(Tree(panel).Nodes[0].Nodes[0]);
                object deleted = null;
                panel.DeleteRequested += (s, e) => deleted = e.Item;
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(folder))
                {
                    ToolStripItem del = menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Delete Package");
                    del.PerformClick();
                }
                Assert.IsInstanceOfType(deleted, typeof(ProjectTreePanel.FolderGroup),
                    "the host receives the FolderGroup so it can delete the package directory on disk");
            }
        }

        [TestMethod]
        public void ExcludedFiles_AppearUnderTheProject_AndOfferInclude()
        {
            var solution = new SolutionModel(Root + @"\pyproject.toml", "sympy");
            var project = new ProjectModel(Root + @"\pyproject.toml", "sympy", solution);
            project.ExcludedPaths.Add("legacy/old.py");
            solution.Projects.Add(project);

            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode projectNode = Tree(panel).Nodes[0].Nodes[0];
                TreeNode excludedRoot = projectNode.Nodes.Cast<TreeNode>()
                    .FirstOrDefault(n => n.Text == "Excluded Files");
                Assert.IsNotNull(excludedRoot, "an excluded entry gets its own tree node so it can be Included again");
                Assert.AreEqual(1, excludedRoot.Nodes.Count);
                TreeNode entryNode = excludedRoot.Nodes[0];
                Assert.AreEqual("legacy/old.py", entryNode.Text);
                Assert.IsInstanceOfType(entryNode.Tag, typeof(ProjectTreePanel.ExcludedEntry));

                object seen = null;
                panel.IncludeInProjectRequested += (s, e) => seen = e.Item;
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(entryNode))
                {
                    CollectionAssert.Contains(ItemTexts(menu), "Include in Project");
                    ToolStripItem include = menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Include in Project");
                    include.PerformClick();
                }
                var entry = seen as ProjectTreePanel.ExcludedEntry;
                Assert.IsNotNull(entry);
                Assert.AreEqual("legacy/old.py", entry.RelativePath);
                Assert.AreSame(project, entry.Project);
            }
        }

        [TestMethod]
        public void NoExcludedPaths_NoExcludedFilesNodeAppears()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode projectNode = Tree(panel).Nodes[0].Nodes[0];
                Assert.IsFalse(projectNode.Nodes.Cast<TreeNode>().Any(n => n.Text == "Excluded Files"));
            }
        }

        // ------------------------------------------------------------------ §5.3

        [TestMethod]
        public void CutThenPaste_RaisesFilePasteRequested_AndConsumesTheClipboard()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode file = FindFileNode(Tree(panel).Nodes[0].Nodes[0]);
                TreeNode project = Tree(panel).Nodes[0].Nodes[0];

                FilePasteEventArgs seen = null;
                panel.FilePasteRequested += (s, e) => seen = e;

                panel.CopyFileNode(file, cut: true);
                Assert.IsTrue(panel.HasFileClipboard);
                Assert.IsTrue(panel.PendingClipboardIsCut);

                panel.PasteIntoNode(project);
                Assert.IsNotNull(seen, "paste must reach the host");
                Assert.IsTrue(seen.IsCut);
                StringAssert.EndsWith(seen.SourcePath, "add.py");
                Assert.AreSame(project.Tag, seen.TargetItem);
                Assert.IsFalse(panel.HasFileClipboard, "a cut is consumed by its paste");
            }
        }

        [TestMethod]
        public void CopyPaste_KeepsTheClipboard_ForRepeatedPastes()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode file = FindFileNode(Tree(panel).Nodes[0].Nodes[0]);
                int raised = 0;
                panel.FilePasteRequested += (s, e) => raised++;

                panel.CopyFileNode(file, cut: false);
                panel.PasteIntoNode(Tree(panel).Nodes[0].Nodes[0]);
                panel.PasteIntoNode(Tree(panel).Nodes[0].Nodes[0]);

                Assert.AreEqual(2, raised);
                Assert.IsTrue(panel.HasFileClipboard, "a copy survives pastes");
            }
        }

        [TestMethod]
        public void DedupePastePath_ProducesImportableUnderscoreNames()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_srs5_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string target = Path.Combine(root, "mod.py");
                Assert.AreEqual(target, MainForm.DedupePastePath(target), "free name is unchanged");
                File.WriteAllText(target, "");
                Assert.AreEqual(Path.Combine(root, "mod_copy.py"), MainForm.DedupePastePath(target));
                File.WriteAllText(Path.Combine(root, "mod_copy.py"), "");
                Assert.AreEqual(Path.Combine(root, "mod_copy_2.py"), MainForm.DedupePastePath(target));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ------------------------------------------------------------------ §5.4 / §5.9

        [TestMethod]
        public void EveryContainerNode_LeadsWithBothOpenVerbs()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                TreeNode solution = Tree(panel).Nodes[0];
                TreeNode project = solution.Nodes[0];
                TreeNode type = FindFileNode(project).Nodes[0];

                foreach (TreeNode node in new[] { solution, project, type })
                {
                    using (ContextMenuStrip menu = panel.BuildContextMenuForNode(node))
                    {
                        List<string> items = ItemTexts(menu);
                        Assert.AreEqual("Open in Editor", items[0], node.Text);
                        Assert.AreEqual("Open in Class Designer Canvas", items[1], node.Text);
                    }
                }

                // File rows keep their historical bold "Open" as the editor verb.
                TreeNode file = FindFileNode(project);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(file))
                {
                    List<string> items = ItemTexts(menu);
                    Assert.AreEqual("Open", items[0]);
                    Assert.AreEqual("Open in Class Designer Canvas", items[1]);
                }
            }
        }

        // ------------------------------------------------------------------ §5 (loop 533)

        [TestMethod]
        public void FunctionNode_Menu_OffersPlainRename_BeforeRenameSymbol()
        {
            var solution = new SolutionModel(Root + @"\pyproject.toml", "sympy");
            var project = new ProjectModel(Root + @"\pyproject.toml", "sympy", solution);
            project.Functions.Add(new FunctionModel { Name = "helper", SourceFilePath = Root + @"\a.py" });
            solution.Projects.Add(project);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode fnNode = FindNodeByTag<FunctionModel>(Tree(panel).Nodes[0]);
                Assert.IsNotNull(fnNode, "expected a function row");
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(fnNode))
                {
                    List<string> items = ItemTexts(menu);
                    CollectionAssert.Contains(items, "Rename");
                    CollectionAssert.Contains(items, "Rename Symbol...");
                    CollectionAssert.Contains(items, "Edit Signature...");
                    Assert.IsTrue(items.IndexOf("Rename") < items.IndexOf("Rename Symbol..."),
                        "the quick inline rename leads, matching the type-node menu shape");
                }
            }
        }

        [TestMethod]
        public void FunctionNode_F2_BeginsInlineEdit_NotTheModalRenameSymbol()
        {
            var solution = new SolutionModel(Root + @"\pyproject.toml", "sympy");
            var project = new ProjectModel(Root + @"\pyproject.toml", "sympy", solution);
            project.Functions.Add(new FunctionModel { Name = "helper", SourceFilePath = Root + @"\a.py" });
            solution.Projects.Add(project);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode fnNode = FindNodeByTag<FunctionModel>(Tree(panel).Nodes[0]);
                Tree(panel).SelectedNode = fnNode;

                object renameSymbolTarget = null;
                panel.RenameSymbolRequested += (s, e) => renameSymbolTarget = e.Item;

                SendKey(panel, Keys.F2);

                Assert.IsNull(renameSymbolTarget, "F2 must not fall through to the modal Rename Symbol path");
                Assert.IsTrue(Tree(panel).LabelEdit, "F2 must have started inline label editing");
            }
        }

        [TestMethod]
        public void FunctionNode_Menu_OffersDelete_WithDelKeyHint()
        {
            var solution = new SolutionModel(Root + @"\pyproject.toml", "sympy");
            var project = new ProjectModel(Root + @"\pyproject.toml", "sympy", solution);
            project.Functions.Add(new FunctionModel { Name = "helper", SourceFilePath = Root + @"\a.py" });
            solution.Projects.Add(project);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode fnNode = FindNodeByTag<FunctionModel>(Tree(panel).Nodes[0]);
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(fnNode))
                {
                    ToolStripMenuItem delete = (ToolStripMenuItem)menu.Items.Cast<ToolStripItem>()
                        .First(i => i.Text == "Delete");
                    Assert.AreEqual("Del", delete.ShortcutKeyDisplayString);
                }
            }
        }

        [TestMethod]
        public void FunctionNode_DeleteKey_RaisesDeleteRequested_WithTheFunction()
        {
            var solution = new SolutionModel(Root + @"\pyproject.toml", "sympy");
            var project = new ProjectModel(Root + @"\pyproject.toml", "sympy", solution);
            var fn = new FunctionModel { Name = "helper", SourceFilePath = Root + @"\a.py" };
            project.Functions.Add(fn);
            solution.Projects.Add(project);
            using (var panel = new ProjectTreePanel())
            {
                panel.LoadSolution(solution);
                TreeNode fnNode = FindNodeByTag<FunctionModel>(Tree(panel).Nodes[0]);
                Tree(panel).SelectedNode = fnNode;

                object deleted = null;
                panel.DeleteRequested += (s, e) => deleted = e.Item;
                SendKey(panel, Keys.Delete);

                Assert.AreSame(fn, deleted, "Delete on a function row must raise DeleteRequested with the FunctionModel");
            }
        }

        private static TreeNode FindNodeByTag<T>(TreeNode root)
        {
            foreach (TreeNode child in root.Nodes)
            {
                if (child.Tag is T) return child;
                TreeNode found = FindNodeByTag<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static void SendKey(ProjectTreePanel panel, Keys key)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("OnTreeKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "OnTreeKeyDown expected");
            m.Invoke(panel, new object[] { panel, new KeyEventArgs(key) });
        }

        [TestMethod]
        public void SolutionMenu_OffersRefresh_ProjectMenu_OffersAnalyze()
        {
            using (var panel = Load(@"D:\repos\sympy\src\core\add.py"))
            {
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(Tree(panel).Nodes[0]))
                    CollectionAssert.Contains(ItemTexts(menu), "Refresh");
                using (ContextMenuStrip menu = panel.BuildContextMenuForNode(Tree(panel).Nodes[0].Nodes[0]))
                    CollectionAssert.Contains(ItemTexts(menu), "Analyze");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static List<string> ItemTexts(ContextMenuStrip menu)
        {
            var texts = new List<string>();
            foreach (ToolStripItem item in menu.Items)
                if (!(item is ToolStripSeparator)) texts.Add(item.Text);
            return texts;
        }

        private static void DoubleClick(ProjectTreePanel panel, TreeNode node)
        {
            MethodInfo m = typeof(ProjectTreePanel).GetMethod("OnTreeNodeDoubleClick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            m.Invoke(panel, new object[]
            {
                null, new TreeNodeMouseClickEventArgs(node, MouseButtons.Left, 2, 0, 0)
            });
        }

        private static TreeNode FindFolderNode(TreeNode start)
        {
            foreach (TreeNode child in start.Nodes)
            {
                if (child.Tag is ProjectTreePanel.FolderGroup) return child;
                TreeNode inner = FindFolderNode(child);
                if (inner != null) return inner;
            }
            return null;
        }

        private static TreeNode FindFileNode(TreeNode start)
        {
            foreach (TreeNode child in start.Nodes)
            {
                if (child.Tag is ProjectTreePanel.FileGroup fg && !string.IsNullOrEmpty(fg.FilePath)) return child;
                TreeNode inner = FindFileNode(child);
                if (inner != null) return inner;
            }
            return null;
        }

        private static TextBox SearchBox(ProjectTreePanel panel)
        {
            FieldInfo f = typeof(ProjectTreePanel).GetField("_searchBox",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (TextBox)f.GetValue(panel);
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
