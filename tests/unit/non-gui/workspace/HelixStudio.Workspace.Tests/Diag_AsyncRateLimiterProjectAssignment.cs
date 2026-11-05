using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Services;

namespace HelixPythonNamespace.Workspace.Tests
{
    /// <summary>
    /// Diagnostic probe (non-GUI, no MainForm/WinForms involved — safe to run without risking the
    /// window-handle-creation dialogs the App.Tests WinForms suite triggers on this box) for a
    /// user-reported bug: "Open in Designer" on AsyncRateLimiter in the real
    /// tests/fixtures/python/python-test-sample workspace switches to the Designer tab but shows
    /// nothing. MainForm.NavigateToTypeOnAllTab's fallback path only rebuilds a type-level view
    /// when `typeModel.Project != null`; otherwise it falls back to a flat RebuildFromSolution that
    /// filters by `IsProjectVisible(type.Project?.Name)` — which excludes a type with a null
    /// Project just as silently. This dumps TypeModel.Project / .Namespace / .FullyQualifiedName
    /// for AsyncRateLimiter as actually parsed from the real multi-package ([tool.uv.workspace])
    /// fixture, to see whether the Project back-reference is even populated correctly for this
    /// workspace layout.
    /// </summary>
    [TestClass]
    public class Diag_AsyncRateLimiterProjectAssignment
    {
        private static string FindFixtureRoot()
        {
            // The fixture was renamed python-test-sample -> python-test-sample-oop when the
            // corpus split into _oop/_non_oop variants . This probe needs the OOP
            // variant: only it still contains the AsyncRateLimiter CLASS (the non_oop variant
            // decomposes it into functions, so there is no TypeModel to diagnose there).
            string dir = System.AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                foreach (string name in new[] { "python-test-sample", "python-test-sample-oop" })
                {
                    string candidate = Path.Combine(dir, "tests", "fixtures", "python", name);
                    if (Directory.Exists(candidate)) return candidate;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        [TestMethod]
        public void AsyncRateLimiter_HasNonNullProject_AndConsistentNamespace()
        {
            string fixtureRoot = FindFixtureRoot();
            Assert.IsNotNull(fixtureRoot, "fixture tests/fixtures/python/python-test-sample not found");
            string pyproject = Path.Combine(fixtureRoot, "pyproject.toml");
            Assert.IsTrue(File.Exists(pyproject), "fixture pyproject.toml must exist at " + pyproject);

            var loadTask = new SolutionService().LoadAsync(pyproject);
            loadTask.Wait();
            Assert.IsTrue(loadTask.Result.Success, loadTask.Result.ErrorMessage);
            SolutionModel solution = loadTask.Result.Solution;

            TypeModel type = null;
            ProjectModel owningProject = null;
            foreach (var project in solution.Projects)
            {
                TypeModel candidate = project.Types.Find(t => t.Name == "AsyncRateLimiter");
                if (candidate != null) { type = candidate; owningProject = project; break; }
            }
            Assert.IsNotNull(type, "AsyncRateLimiter must be found somewhere in the loaded solution");

            string report = string.Format(
                "Projects in solution: {0}\n" +
                "Owning project (by list membership): {1}\n" +
                "type.Project (back-reference): {2}\n" +
                "type.Project.Name: {3}\n" +
                "type.Namespace: '{4}'\n" +
                "type.FullyQualifiedName: {5}\n" +
                "type.SourceFilePath: {6}\n" +
                "owningProject.GetNamespaces(): {7}\n" +
                "owningProject.GetTypesByNamespace(type.Namespace).Count: {8}",
                string.Join(", ", solution.Projects.ConvertAll(p => p.Name)),
                owningProject.Name,
                type.Project == null ? "NULL" : "set",
                type.Project == null ? "N/A" : type.Project.Name,
                type.Namespace ?? "(null)",
                type.FullyQualifiedName,
                type.SourceFilePath,
                string.Join(" | ", owningProject.GetNamespaces()),
                owningProject.GetTypesByNamespace(type.Namespace ?? "").Count);

            // Fail with the full diagnostic dump as the message regardless of outcome, so the
            // values are visible in the test-run log either way.
            Assert.IsNotNull(type.Project, "DIAGNOSTIC DUMP:\n" + report);
            Assert.AreSame(owningProject, type.Project, "DIAGNOSTIC DUMP (Project reference mismatch):\n" + report);
            Assert.IsTrue(owningProject.GetTypesByNamespace(type.Namespace ?? "").Contains(type),
                "DIAGNOSTIC DUMP (namespace round-trip mismatch):\n" + report);
        }
    }
}
