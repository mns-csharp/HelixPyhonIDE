using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Phase 7 (final) of the Solution Explorer CRUD-coverage sweep: solution rename now also moves
    /// the solution's root folder on disk (same-parent, renamed leaf), not just the display name —
    /// the highest-blast-radius rename in the tree. The happy path is gated behind a real
    /// MessageBox.Show confirm (moving a real folder), so — matching this suite's established
    /// boundary for dialog-gated destructive commands (see SolutionExplorerSrs5Tests' Delete tests)
    /// — it is not driven end-to-end here. What IS covered directly, with real disk state and no
    /// dialog involved: the up-front "destination already exists" refusal (returns before the
    /// confirm dialog, so it's fully synchronous) and the two supporting helpers
    /// (RebaseUnderMovedRoot, RekeySolutionSettings) that the happy path depends on for correctness.
    /// </summary>
    [TestClass]
    public class SolutionRenameFolderMoveTests
    {
        [TestMethod]
        public void RenameSolution_DestinationAlreadyExists_RefusesWithNoSideEffects()
        {
            string parent = Path.Combine(Path.GetTempPath(), "helix_slnrename_" + Guid.NewGuid().ToString("N"));
            string oldDir = Path.Combine(parent, "OldName");
            string collidingDir = Path.Combine(parent, "NewName");
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(collidingDir);
            string oldToml = Path.Combine(oldDir, "pyproject.toml");
            const string originalToml = "[project]\nname = \"OldName\"\n";
            File.WriteAllText(oldToml, originalToml);
            File.WriteAllText(Path.Combine(collidingDir, "marker.txt"), "already here");
            try
            {
                var sln = new SolutionModel(oldToml, "OldName");
                var proj = new ProjectModel(oldToml, "OldName", sln);
                sln.Projects.Add(proj);

                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    form._currentSolution = sln;

                    // The SolutionModel branch never touches e.Node, so a null Node is fine here.
                    form.OnTreeRename(form, new TreeRenameEventArgs(null, sln, "NewName"));

                    Assert.IsTrue(Directory.Exists(oldDir), "the original folder must be untouched");
                    Assert.AreEqual(originalToml, File.ReadAllText(oldToml), "the original TOML must be untouched");
                    Assert.IsTrue(File.Exists(Path.Combine(collidingDir, "marker.txt")),
                        "the colliding destination folder must be left exactly as it was");
                    Assert.AreEqual("OldName", sln.Name, "the in-memory model must not have been renamed either");

                    form.Close();
                }
            }
            finally { try { Directory.Delete(parent, true); } catch { } }
        }

        [TestMethod]
        public void RenameRootProject_DestinationAlreadyExists_UsesSolutionTransactionAndPreservesMetadata()
        {
            string parent = Path.Combine(Path.GetTempPath(), "helix_rootprojrename_" + Guid.NewGuid().ToString("N"));
            string oldDir = Path.Combine(parent, "OldName");
            string collidingDir = Path.Combine(parent, "NewName");
            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(collidingDir);
            string oldToml = Path.Combine(oldDir, "pyproject.toml");
            const string originalToml = "[project]\nname = \"OldName\"\n\n[tool.helixstudio]\nkind = \"app\"\n";
            File.WriteAllText(oldToml, originalToml);
            File.WriteAllText(Path.Combine(collidingDir, "marker.txt"), "already here");
            try
            {
                var sln = new SolutionModel(oldToml, "OldName");
                var rootProject = new ProjectModel(oldToml, "OldName", sln);
                sln.Projects.Add(rootProject);

                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();
                    form._currentSolution = sln;

                    form.OnTreeRename(form, new TreeRenameEventArgs(null, rootProject, "NewName"));

                    Assert.IsTrue(Directory.Exists(oldDir));
                    Assert.AreEqual(originalToml, File.ReadAllText(oldToml));
                    Assert.IsTrue(File.Exists(Path.Combine(collidingDir, "marker.txt")));
                    Assert.AreEqual("OldName", sln.Name);
                    form.Close();
                }
            }
            finally { try { Directory.Delete(parent, true); } catch { } }
        }

        [TestMethod]
        public void RebaseUnderMovedRoot_HandlesExactRoot_NestedPath_AndUnrelatedPath()
        {
            MethodInfo m = typeof(MainForm).GetMethod("RebaseUnderMovedRoot", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "RebaseUnderMovedRoot expected");

            string oldRoot = @"C:\ws\OldName";
            string newRoot = @"C:\ws\NewName";

            // A project directory that IS the root itself (single-project solution shape).
            Assert.AreEqual(newRoot, (string)m.Invoke(null, new object[] { oldRoot, oldRoot, newRoot }));

            // A nested project/file path under the root.
            Assert.AreEqual(@"C:\ws\NewName\src\pkg",
                (string)m.Invoke(null, new object[] { @"C:\ws\OldName\src\pkg", oldRoot, newRoot }));

            // A path outside the root entirely is returned unchanged.
            Assert.AreEqual(@"C:\elsewhere\thing",
                (string)m.Invoke(null, new object[] { @"C:\elsewhere\thing", oldRoot, newRoot }));
        }

        [TestMethod]
        public void RekeySolutionSettings_MovesEntries_FromOldPathToNewPath()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false;
                TestWindow.ShowOffScreen(form);
                Application.DoEvents();

                const string oldToml = @"C:\ws\OldName\pyproject.toml";
                const string newToml = @"C:\ws\NewName\pyproject.toml";

                form._settingsManager.Settings.SolutionStartupProject[oldToml] = "myproj";
                form._settingsManager.Settings.SolutionDockingLayouts[oldToml] =
                    new DockingLayoutState { ActiveTopLevelTab = "editor" };

                MethodInfo m = typeof(MainForm).GetMethod("RekeySolutionSettings", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(m, "RekeySolutionSettings expected");
                m.Invoke(form, new object[] { oldToml, newToml });

                Assert.IsFalse(form._settingsManager.Settings.SolutionStartupProject.ContainsKey(oldToml));
                Assert.AreEqual("myproj", form._settingsManager.Settings.SolutionStartupProject[newToml]);

                Assert.IsFalse(form._settingsManager.Settings.SolutionDockingLayouts.ContainsKey(oldToml));
                Assert.AreEqual("editor", form._settingsManager.Settings.SolutionDockingLayouts[newToml].ActiveTopLevelTab);

                form.Close();
            }
        }
    }
}
