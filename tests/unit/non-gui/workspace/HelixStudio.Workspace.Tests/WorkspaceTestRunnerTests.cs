using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cuda;
using HelixPythonNamespace.Workspace.Testing;
using HelixPythonNamespace.Core.Model.Enums;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// FR-TEST-02..07: verifies the output parsers of the per-language test runners (cargo/dotnet/Pkg.test/ctest/
    /// surefire) against verbatim real tool output, so each maps cases to the shared <c>WorkspaceTestRun</c> the
    /// GUI Test Runner panel consumes. The live end-to-end runs are exercised by the CLI + wstest harness (loop 192).
    /// </summary>
    [TestClass]
    public class WorkspaceTestRunnerTests
    {
        [TestMethod]
        public void AdapterRegistry_ShipsNoBuiltInAdapters_ButStaysContributable()
        {
            // The cargo/dotnet/java/julia adapters moved to their own products, and Python has
            // always run its tests through HelixPythonNamespace.Testing (pytest/unittest child processes)
            // rather than this workspace-level registry. What must survive the split is the
            // contribution point itself.
            foreach (ProjectLanguage lang in (ProjectLanguage[])System.Enum.GetValues(typeof(ProjectLanguage)))
                Assert.IsNull(WorkspaceTestAdapterRegistry.For(lang), lang + ": no built-in adapter");

            WorkspaceTestAdapterRegistry.Register(new StubAdapter());
            Assert.AreEqual("stub", WorkspaceTestAdapterRegistry.For(ProjectLanguage.Python).Id,
                "a host-contributed adapter must still resolve");
        }

        private sealed class StubAdapter : HelixPythonNamespace.Workspace.Testing.IWorkspaceTestAdapter
        {
            public string Id { get { return "stub"; } }
            public ProjectLanguage Language { get { return ProjectLanguage.Python; } }
            public HelixPythonNamespace.Workspace.Testing.WorkspaceTestRun Run(string projectDirectory, bool wholeWorkspace)
            { return null; }
        }
        [TestMethod]
        public void CTest_Parses_NamesWithSpaces_Catch2Style()
        {
            // Verbatim ctest output shape for a Catch2 suite (catch_discover_tests): test names
            // contain spaces. The old \S+ name pattern dropped every multi-word test — a 60-test
            // suite surfaced only its two single-word-named tests in the panel.
            var r = CTestRunner.ParseOutput(
                "      Start 32: BoundingBox dimensions\n" +
                "32/60 Test #32: BoundingBox dimensions ...........................   Passed    0.02 sec\n" +
                "      Start 33: BoundingBox intersect disjoint returns nullopt\n" +
                "33/60 Test #33: BoundingBox intersect disjoint returns nullopt ...***Failed    0.03 sec\n" +
                "      Start 34: angle_between\n" +
                "34/60 Test #34: angle_between .....................................   Passed    0.02 sec\n");
            Assert.AreEqual(3, r.Tests.Count);
            Assert.AreEqual("BoundingBox dimensions", r.Tests[0].Name);
            Assert.AreEqual("passed", r.Tests[0].Outcome);
            Assert.AreEqual("BoundingBox intersect disjoint returns nullopt", r.Tests[1].Name);
            Assert.AreEqual("failed", r.Tests[1].Outcome);
            Assert.AreEqual("angle_between", r.Tests[2].Name);
        }

        [TestMethod]
        public void CTest_Parses_PassFail()
        {
            var r = CTestRunner.ParseOutput(
                "    Start 1: adds_two\n" +
                "1/3 Test #1: adds_two .................   Passed    0.01 sec\n" +
                "2/3 Test #2: adds_neg .................   Passed    0.01 sec\n" +
                "3/3 Test #3: wrong ...................***Failed   0.02 sec\n");
            Assert.AreEqual(3, r.Tests.Count);
            Assert.AreEqual(2, r.Passed);
            Assert.AreEqual(1, r.Failed);
        }

        // A root that called enable_testing() has a root CTestTestfile.cmake: one run dir, the
        // root itself — the subdirectory files are reached through its subdirs() include chain.
        [TestMethod]
        public void CTest_ResolveTestDirectories_RootFileWins_NoDescent()
        {
            string root = MakeTempDir();
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(root, "CTestTestfile.cmake"), "subdirs(\"sub\")\n");
                string sub = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "sub")).FullName;
                System.IO.File.WriteAllText(System.IO.Path.Combine(sub, "CTestTestfile.cmake"), "add_test(x \"cmd\")\n");

                var dirs = CTestRunner.ResolveTestDirectories(root);
                Assert.AreEqual(1, dirs.Count);
                Assert.AreEqual(root, dirs[0]);
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        // The wizard's GTest template enables testing only in ITS OWN directory; a root that never
        // called enable_testing() gets no root CTestTestfile.cmake. The runner must then find the
        // topmost subdirectories that do have one — skipping _deps, where FetchContent'd googletest
        // keeps its own files.
        [TestMethod]
        public void CTest_ResolveTestDirectories_FallsBackToTopmostSubdirs_SkippingDeps()
        {
            string root = MakeTempDir();
            try
            {
                string tests = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "gtests")).FullName;
                System.IO.File.WriteAllText(System.IO.Path.Combine(tests, "CTestTestfile.cmake"), "add_test(x \"cmd\")\n");
                // nested below a matched dir: must NOT be returned separately (would double-run).
                string nested = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tests, "inner")).FullName;
                System.IO.File.WriteAllText(System.IO.Path.Combine(nested, "CTestTestfile.cmake"), "add_test(y \"cmd\")\n");
                // googletest's own files under _deps: must be skipped.
                string deps = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "_deps", "googletest-build")).FullName;
                System.IO.File.WriteAllText(System.IO.Path.Combine(deps, "CTestTestfile.cmake"), "add_test(g \"cmd\")\n");

                var dirs = CTestRunner.ResolveTestDirectories(root);
                Assert.AreEqual(1, dirs.Count);
                Assert.AreEqual(tests, dirs[0]);
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        // End-to-end with the REAL ctest: a build tree whose only CTestTestfile.cmake lives in a
        // subdirectory (the exact shape a wizard-created GTest project produces) still runs and
        // reports its cases.
        [TestMethod]
        public void CTest_Run_RealCtest_FindsSubdirectoryOnlyTests()
        {
            if (!CTestRunner.IsAvailable) { Assert.Inconclusive("ctest not installed"); return; }
            string root = MakeTempDir();
            try
            {
                string tests = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "gtests")).FullName;
                // NOT cmd.exe: ctest quotes every argument and cmd.exe rejects a quoted "/c"
                // ("The syntax of the command is incorrect"). cmake ships beside ctest and its
                // -E true/false give a portable exit-0/exit-1 pair.
                System.IO.File.WriteAllText(System.IO.Path.Combine(tests, "CTestTestfile.cmake"),
                    "add_test(dummy_pass \"cmake\" \"-E\" \"true\")\n" +
                    "add_test(dummy_fail \"cmake\" \"-E\" \"false\")\n");

                var r = CTestRunner.Run(root, 60000);
                Assert.IsTrue(r.Available, r.Reason);
                Assert.AreEqual(2, r.Tests.Count, r.RawOutput);
                Assert.AreEqual(1, r.Passed);
                Assert.AreEqual(1, r.Failed);
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        private static string MakeTempDir()
        {
            string d = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "hx_ctest_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(d);
            return d;
        }

        // ctest reports names only — the Line column and double-click navigation need the macro's
        // source location, resolved by scanning the workspace for TEST_CASE/TEST definitions.
        [TestMethod]
        public void NativeTestLocator_IndexesCatch2AndGtest_SkipsBuildDirs()
        {
            string root = MakeTempDir();
            try
            {
                string tests = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "tests")).FullName;
                System.IO.File.WriteAllLines(System.IO.Path.Combine(tests, "test_geometry.cu"), new[]
                {
                    "#include <catch2/catch_test_macros.hpp>",              // 1
                    "",                                                     // 2
                    "TEST_CASE(\"BoundingBox dimensions\", \"[geometry]\") {", // 3
                    "}",                                                    // 4
                    "SCENARIO(\"vectors can be scaled\") {",                // 5
                    "}",                                                    // 6
                });
                System.IO.File.WriteAllLines(System.IO.Path.Combine(tests, "test_gtest.cpp"), new[]
                {
                    "#include <gtest/gtest.h>",                             // 1
                    "TEST(MathSuite, Addition) {",                          // 2
                    "}",                                                    // 3
                });
                // Copies inside build output / fetched deps must NOT shadow the real sources.
                string decoy = System.IO.Directory.CreateDirectory(
                    System.IO.Path.Combine(root, "cmake-build-helixstudio-x", "_deps")).FullName;
                System.IO.File.WriteAllText(System.IO.Path.Combine(decoy, "test_geometry.cu"),
                    "TEST_CASE(\"BoundingBox dimensions\") {}\n");

                var index = HelixPythonNamespace.Workspace.Cuda.NativeTestLocator.BuildIndex(root);

                Assert.IsTrue(index.ContainsKey("BoundingBox dimensions"), "Catch2 multi-word name indexed");
                StringAssert.Contains(index["BoundingBox dimensions"].FilePath, "tests");
                Assert.AreEqual(3, index["BoundingBox dimensions"].Line);
                Assert.AreEqual(5, index["Scenario: vectors can be scaled"].Line,
                    "SCENARIO registers under the 'Scenario: ' prefix catch_discover_tests uses");
                Assert.AreEqual(2, index["MathSuite.Addition"].Line, "GTest keyed Suite.Name");
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

    }
}
