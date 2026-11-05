using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.UI.Projection;
using HelixPythonNamespace.TestSupport;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Regression test for a user-reported multi-second UI freeze after renaming a class in the
    /// small python-test-sample fixture (37 files, ~166 types, 43 inheritance edges). Renaming a
    /// type triggers ExecuteScopedRename -> ReloadSolutionAsync -> SolutionProjectionBuilder.
    /// RebuildFromSolution with every project checked/visible — empirically measured at 75+
    /// SECONDS before the fix (see EdgeRouter.RouteInheritanceTrunks: its obstacle-aware fallback
    /// called the full-grid A* FindPath with no size cap at all, unlike the main per-edge routing
    /// loop right next to it, which already gates FindPath behind AStarGridCellCap). This asserts a
    /// generous bound that's still ~100x below the original bug, so it fails loudly if the same
    /// class of regression reappears, without being flaky on a slow CI machine.
    /// </summary>
    [TestClass]
    public class RebuildFromSolutionPerformanceTests
    {
        private static string FindFixtureRoot()
        {
            string dir = System.AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                // "python-test-sample" was split into -oop / -non_oop variants; use the OOP one.
                string candidate = Path.Combine(dir, "tests", "fixtures", "python", "python-test-sample-oop");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        [TestMethod]
        public void RebuildFromSolution_WithEveryProjectVisible_CompletesInUnderFiveSeconds()
        {
            string fixtureRoot = FindFixtureRoot();
            Assert.IsNotNull(fixtureRoot, "fixture tests/fixtures/python/python-test-sample-oop not found");
            string pyproject = Path.Combine(fixtureRoot, "pyproject.toml");

            MainForm form = TestMainForm.CreateComposed();
            try
            {
                TestWindow.ShowOffScreen(form);
                form.OpenSolutionAsync(pyproject);
                TestMainForm.PumpUntilIdle();
                Assert.IsNotNull(form._currentSolution, "the solution must have loaded");

                var checkedProjects = new List<string>();
                foreach (var p in form._currentSolution.Projects) checkedProjects.Add(p.Name);

                var autoLayout = new GridAutoLayoutStrategy();
                var builder = new SolutionProjectionBuilder(form._canvas.SceneGraph, form._layoutStore, autoLayout);
                builder.SetVisibleProjects(checkedProjects);

                var sw = Stopwatch.StartNew();
                builder.RebuildFromSolution(form._currentSolution);
                sw.Stop();

                Assert.IsTrue(sw.ElapsedMilliseconds < 5000,
                    "RebuildFromSolution took " + sw.ElapsedMilliseconds +
                    "ms for this small fixture (166 types / 43 edges) — was 75000+ms before the " +
                    "EdgeRouter.RouteInheritanceTrunks A*-size-cap fix; this bound is still ~100x " +
                    "the fixed behavior, so a regression here means the cap regressed, not that the " +
                    "machine is merely slow.");
            }
            finally
            {
                form.Dispose();
            }
        }
    }
}
