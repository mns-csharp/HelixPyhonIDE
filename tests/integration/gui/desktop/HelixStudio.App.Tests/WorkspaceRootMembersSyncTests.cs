using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Phase 2 of the Solution Explorer CRUD-coverage sweep: deleting a project previously left a
    /// dangling entry in the workspace-root pyproject.toml's [tool.uv.workspace].members list —
    /// SolutionService silently skipped the missing directory on the next load rather than erroring,
    /// which is how the gap went unnoticed. MainForm.RewriteWorkspaceRootMembers (called after
    /// project add/delete) keeps that list in sync with the in-memory project set. Invoked directly
    /// via reflection so this test never needs to click through OnTreeDelete's confirmation
    /// MessageBox (the established pattern in this suite — see SolutionExplorerSrs5Tests, which
    /// stops at the tree-menu wiring layer for the same reason).
    /// </summary>
    [TestClass]
    public class WorkspaceRootMembersSyncTests
    {
        private static void Invoke(MainForm form, bool forceWorkspaceSection = false)
        {
            MethodInfo m = typeof(MainForm).GetMethod("RewriteWorkspaceRootMembers", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "RewriteWorkspaceRootMembers expected");
            m.Invoke(form, new object[] { forceWorkspaceSection });
        }

        [TestMethod]
        public void RemovingAProject_DropsItFromTheRootTomlMembersList()
        {
            string root = Path.Combine(Path.GetTempPath(), "helix_wsroot_" + Guid.NewGuid().ToString("N"));
            string projADir = Path.Combine(root, "proj_a");
            string projBDir = Path.Combine(root, "proj_b");
            Directory.CreateDirectory(projADir);
            Directory.CreateDirectory(projBDir);
            try
            {
                string rootToml = Path.Combine(root, "pyproject.toml");
                File.WriteAllText(rootToml,
                    "[project]\nname = \"ws\"\n\n[tool.uv.workspace]\nmembers = [\"proj_a\", \"proj_b\"]\n");

                var sln = new SolutionModel(rootToml, "ws");
                var projA = new ProjectModel(Path.Combine(projADir, "pyproject.toml"), "proj_a", sln);
                var projB = new ProjectModel(Path.Combine(projBDir, "pyproject.toml"), "proj_b", sln);
                sln.Projects.Add(projA);
                sln.Projects.Add(projB);

                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    form._currentSolution = sln;

                    // Simulate what OnTreeDelete's ProjectModel branch does before calling this helper.
                    sln.Projects.Remove(projB);
                    Invoke(form);

                    string updated = File.ReadAllText(rootToml);
                    StringAssert.Contains(updated, "proj_a");
                    Assert.IsFalse(updated.Contains("proj_b"),
                        "the deleted project must be dropped from the workspace members list, not left dangling");

                    form.Close();
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void SingleProjectSolution_WithNoExistingWorkspaceSection_IsLeftUntouched()
        {
            // A single-project solution whose root TOML doubles as its one project's own TOML never
            // declared [tool.uv.workspace] — RewriteWorkspaceRootMembers must not invent one.
            string root = Path.Combine(Path.GetTempPath(), "helix_wsroot_single_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string toml = Path.Combine(root, "pyproject.toml");
                const string original = "[project]\nname = \"solo\"\n";
                File.WriteAllText(toml, original);

                var sln = new SolutionModel(toml, "solo");
                var proj = new ProjectModel(toml, "solo", sln);
                sln.Projects.Add(proj);

                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    form._currentSolution = sln;
                    Invoke(form);

                    Assert.AreEqual(original, File.ReadAllText(toml),
                        "no [tool.uv.workspace] section should be invented for a single-project solution");

                    form.Close();
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void ForceWorkspaceSection_CreatesTheSectionWhenAddingASecondProject()
        {
            // Mirrors what OnTreeAddProject / OnTreeAddExistingProject do: registering a second
            // project genuinely does need [tool.uv.workspace] created, unlike a plain delete.
            string root = Path.Combine(Path.GetTempPath(), "helix_wsroot_force_" + Guid.NewGuid().ToString("N"));
            string projBDir = Path.Combine(root, "proj_b");
            Directory.CreateDirectory(projBDir);
            try
            {
                string toml = Path.Combine(root, "pyproject.toml");
                File.WriteAllText(toml, "[project]\nname = \"solo\"\n");

                var sln = new SolutionModel(toml, "solo");
                var projA = new ProjectModel(toml, "solo", sln);
                var projB = new ProjectModel(Path.Combine(projBDir, "pyproject.toml"), "proj_b", sln);
                sln.Projects.Add(projA);
                sln.Projects.Add(projB);

                using (var form = TestMainForm.CreateComposed())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    TestWindow.ShowOffScreen(form);
                    Application.DoEvents();

                    form._currentSolution = sln;
                    Invoke(form, forceWorkspaceSection: true);

                    string updated = File.ReadAllText(toml);
                    StringAssert.Contains(updated, "[tool.uv.workspace]");
                    StringAssert.Contains(updated, "proj_b");

                    form.Close();
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
