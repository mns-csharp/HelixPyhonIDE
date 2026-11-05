using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.UI.Controls;
using HelixPythonNamespace.UI.Rendering;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Diagnostic probe for a user-reported bug: opening the real "python-test-sample" solution
    /// fresh (no rename involved at all) and trying "Open in Designer" on AsyncRateLimiter
    /// (tests/fixtures/python/python-test-sample/src/lib_utils/async_ops.py) fails. Unlike
    /// Diag_RenameTypeOpenInDesigner (which manually assigns _currentSolution and so only ever
    /// exercises the flat RebuildFromSolution scene), this test goes through the real
    /// OpenSolutionAsync path, which starts the canvas at the hierarchical
    /// BuildSolutionLevel/NavigationViewState(Level=Solution) — the state an actual freshly
    /// opened solution is in before any manual canvas drill-down.
    /// </summary>
    [TestClass]
    public class Diag_OpenInDesignerFreshSolution
    {
        // Walk up from bin/Debug/net472 until we find the repo root (same convention as
        // Tier1Tier2LintTests.FindFixtureSrc — the exact build-output depth varies by build
        // invocation, e.g. "Debug\net472" vs "Any CPU\Debug\net472").
        private static string FindFixtureRoot()
        {
            string dir = System.AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                // The old flat "python-test-sample" fixture was split into an -oop and a -non_oop
                // variant; AsyncRateLimiter (a class) lives in the OOP one.
                string candidate = Path.Combine(dir, "tests", "fixtures", "python", "python-test-sample-oop");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        [TestMethod]
        public void FreshlyOpenedSolution_OpenInDesigner_FindsAsyncRateLimiter()
        {
            string fixtureRoot = FindFixtureRoot();
            Assert.IsNotNull(fixtureRoot, "fixture tests/fixtures/python/python-test-sample-oop not found");
            string pyproject = Path.Combine(fixtureRoot, "pyproject.toml");
            Assert.IsTrue(File.Exists(pyproject), "fixture pyproject.toml must exist at " + pyproject);

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                TestWindow.ShowOffScreen(form);
                form.OpenSolutionAsync(pyproject);
                TestMainForm.PumpUntilIdle();

                Assert.IsNotNull(form._currentSolution, "the solution must have loaded");
                TypeModel type = form._currentSolution.FindType("AsyncRateLimiter");
                if (type == null)
                {
                    foreach (var project in form._currentSolution.Projects)
                    {
                        TypeModel candidate = project.Types.Find(t => t.Name == "AsyncRateLimiter");
                        if (candidate != null) { type = candidate; break; }
                    }
                }
                Assert.IsNotNull(type, "AsyncRateLimiter must be found in the loaded solution");

                Assert.IsNotNull(form._projectionBuilder, "OpenSolutionAsync must have built a projection builder");
                form.OnTreeOpenInDesigner(form, new TreeOperationEventArgs(null, type));

                NodeVisual visual = form._projectionBuilder.FindVisual(type.FullyQualifiedName);
                Assert.IsNotNull(visual, "Open in Designer must find a visual for AsyncRateLimiter on a freshly opened solution");
            }
            finally
            {
                FixtureSolution.TearDown(form);
            }
        }
    }
}
