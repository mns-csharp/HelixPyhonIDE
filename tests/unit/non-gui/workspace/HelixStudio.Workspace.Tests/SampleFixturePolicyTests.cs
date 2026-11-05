using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.VirtualCompiler.Compliance;
using HelixPythonNamespace.Workspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Solution policy against the two standing manual-QA fixtures,
    /// <c>tests/fixtures/python/python-test-sample-oop</c> and <c>…-non_oop</c>.
    ///
    /// These are the solutions a human opens to exercise the IDE by hand, and until now nothing
    /// asserted anything about them — so they had drifted into being unopenable: both declared
    /// their <c>my_unit_test_package</c> as <c>kind = "library"</c>, which made all four of its
    /// test files PY8003 errors and every module-level <c>@pytest.fixture</c> in them a PY8002
    /// error. Compile, Run and Debug all refused.
    ///
    /// The pair is now a deliberate contrast, and these tests pin it:
    /// <list type="bullet">
    /// <item><b>-oop</b> — <c>kind = "test"</c>. Its test package must be completely silent, which
    /// is rule 2's test-protocol carve-out working on real pre-existing content (the fixtures and
    /// module-level test function that were already in those files) plus the two files added to
    /// cover the shapes the originals lacked.</item>
    /// <item><b>-non_oop</b> — deliberately still <c>kind = "library"</c>, so it keeps reporting
    /// PY8003 and the module-level-function PY8002s. Open the two side by side to see the rule.</item>
    /// </list>
    ///
    /// Loaded through the production <see cref="SolutionService.LoadAsync"/> path. No
    /// <c>MainForm</c> — the policy object is rebuilt from the loaded model exactly as
    /// <c>MainForm.BuildSolutionPolicy</c> does, so this runs in the fast non-GUI suite instead of
    /// the message-pumping one. READ-ONLY: the fixtures are used in place, never copied or mutated.
    /// </summary>
    [TestClass]
    public class SampleFixturePolicyTests
    {
        private const string FixtureRoot = @"tests\fixtures\python";

        private static string Fixture(string which)
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, FixtureRoot)))
                dir = dir.Parent;
            Assert.IsNotNull(dir, "fixture tree not found above " + AppDomain.CurrentDomain.BaseDirectory);
            string path = Path.Combine(dir.FullName, FixtureRoot, which);
            if (!Directory.Exists(path)) Assert.Inconclusive("Optional workspace test data is unavailable.");
            return path;
        }

        private sealed class Scan
        {
            public SolutionModel Solution;
            public string Root;
            public List<CompileDiagnostic> Violations = new List<CompileDiagnostic>();

            public List<CompileDiagnostic> OfCode(string code)
            {
                return Violations.Where(d => d.Code == code).ToList();
            }

            public List<string> FilesOfCode(string code)
            {
                return OfCode(code).Select(d => Path.GetFileName(d.FilePath)).Distinct().ToList();
            }

            public List<CompileDiagnostic> Under(string relativeDirectory)
            {
                string prefix = Path.Combine(Root, relativeDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Violations.Where(d =>
                    d.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }

        private static Scan ScanFixture(string which)
        {
            string root = Fixture(which);
            var loaded = new SolutionService()
                .LoadAsync(Path.Combine(root, "pyproject.toml")).GetAwaiter().GetResult();
            Assert.IsNotNull(loaded?.Solution, which + " must load");

            var dirs = new List<string>();
            var kinds = new List<KeyValuePair<string, ProjectKind>>();
            foreach (ProjectModel p in loaded.Solution.Projects)
            {
                if (p == null || string.IsNullOrEmpty(p.FilePath)) continue;
                string dir = Path.GetDirectoryName(Path.GetFullPath(p.FilePath));
                dirs.Add(dir);
                kinds.Add(new KeyValuePair<string, ProjectKind>(dir, p.Kind));
            }

            var policy = new SolutionPolicy(root, dirs, kinds);
            var options = new CompileOptions
            {
                EnableAstValidator = false, EnableSymtablePass = false,
                EnableCompileValidator = false, EnableTypeChecker = false,
                EnableImportResolution = false, EnableCompliancePass = true,
                Policy = policy,
            };

            var scan = new Scan { Solution = loaded.Solution, Root = root };
            foreach (string file in Directory.GetFiles(root, "*.py", SearchOption.AllDirectories))
            {
                if (!policy.IsUserWritten(file)) continue;   // skips __pycache__ / .pytest_cache
                string text;
                try { text = File.ReadAllText(file); } catch { continue; }
                try
                {
                    // Fully qualified: `VirtualCompiler` is both a namespace and the class in it.
                    scan.Violations.AddRange(
                        HelixPythonNamespace.VirtualCompiler.VirtualCompiler
                            .CompileSource(text, file, options).Diagnostics
                            .Where(d => d.Code.StartsWith("PY8", StringComparison.Ordinal)));
                }
                catch { /* an unparseable file is a syntax problem, not a policy one */ }
            }
            return scan;
        }

        // ---- -oop : the test package must be completely clean --------------------

        [TestMethod]
        public void OopSample_TestPackageIsATestProject_AndIsNotExecutable()
        {
            ProjectModel tests = ScanFixture("python-test-sample-oop").Solution
                .Projects.First(p => p.Name == "my_unit_test_package");

            Assert.AreEqual(ProjectKind.TestProject, tests.Kind,
                "kind = \"test\" is what makes its test files legal (rule 3) AND enables rule 2's "
                + "carve-out for their module-level fixtures");
            Assert.IsFalse(tests.IsExecutable,
                "a test project is launched by the test runner, never by Run/Debug");
            Assert.IsFalse(tests.IsStartup);
        }

        [TestMethod]
        public void OopSample_TestPackageHasNoPolicyViolationAtAll()
        {
            var offenders = ScanFixture("python-test-sample-oop").Under("my_unit_test_package");
            Assert.AreEqual(0, offenders.Count,
                "the test package must be silent; got: " + string.Join("; ",
                    offenders.Select(d => d.Code + " " + Path.GetFileName(d.FilePath) + " — " + d.Message)));
        }

        [TestMethod]
        public void OopSample_NoTestFileIsMisplaced()
        {
            var scan = ScanFixture("python-test-sample-oop");
            Assert.AreEqual(0, scan.OfCode(VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject).Count,
                "every test file lives in the kind = \"test\" project");
        }

        [TestMethod]
        public void OopSample_TheCarveOutCoversRealPreExistingContent_NotJustTheAddedFiles()
        {
            // The strongest form of the claim. These fixtures and this module-level test function
            // were in the fixture BEFORE the carve-out existed and were PY8002 errors then; they
            // are untouched and now legal purely because the package kind changed.
            string pkg = Path.Combine(Fixture("python-test-sample-oop"), "my_unit_test_package", "tests");

            string events = File.ReadAllText(Path.Combine(pkg, "test_events.py"));
            StringAssert.Contains(events, "def bus(", "precondition: a module-level fixture");
            StringAssert.Contains(events, "def listener(", "precondition: a second one");

            string sample = File.ReadAllText(Path.Combine(pkg, "test_sample.py"));
            StringAssert.Contains(sample, "def test_pi_constant_is_close_to_math_pi(",
                "precondition: a module-level test function");

            var scan = ScanFixture("python-test-sample-oop");
            Assert.AreEqual(0, scan.Under(Path.Combine("my_unit_test_package", "tests")).Count);
        }

        [TestMethod]
        public void OopSample_ShowcaseFilesCoverEveryCarveOutShape()
        {
            // Guards the fixture's own coverage: if someone trims these files, the fixture stops
            // demonstrating what it claims to and this test says so rather than quietly passing.
            string pkg = Path.Combine(Fixture("python-test-sample-oop"), "my_unit_test_package", "tests");
            string conftest = File.ReadAllText(Path.Combine(pkg, "conftest.py"));
            string showcase = File.ReadAllText(Path.Combine(pkg, "test_policy_showcase.py"));

            foreach (string shape in new[]
                     {
                         "@pytest.fixture\n",                  // bare decorator
                         "@pytest.fixture(scope=",             // called form
                         "@fixture\n",                         // from pytest import fixture
                         "def pytest_addoption(",              // hook
                         "def pytest_collection_modifyitems(", // hook
                         "collect_ignore = ",                  // module variable
                         "collect_ignore_glob = ",             // module variable
                         "pytest.register_assert_rewrite(",    // import-time call
                     })
                StringAssert.Contains(conftest, shape, "conftest must still demonstrate: " + shape);

            foreach (string shape in new[]
                     {
                         "pytestmark = ",                      // module variable
                         "pytest.importorskip(",               // import-time call
                         "def setup_module(", "def teardown_module(",
                         "def setup_function(", "def teardown_function(",
                         "def test_",                          // module-level test
                         "async def test_",                    // async module-level test
                         "unittest.TestCase",                  // the class form still legal
                     })
                StringAssert.Contains(showcase, shape, "showcase must still demonstrate: " + shape);
        }

        [TestMethod]
        public void OopSample_AnOrdinaryHelperInTheTestPackage_IsStillAClass()
        {
            // The other half of the carve-out: it is exactly as wide as the runner protocol.
            // A helper written as a module-level def here would be a PY8002 error, and the fixture
            // must keep showing the compliant shape rather than quietly relying on an exemption.
            string conftest = File.ReadAllText(Path.Combine(
                Fixture("python-test-sample-oop"), "my_unit_test_package", "tests", "conftest.py"));
            StringAssert.Contains(conftest, "class TestDataBuilder:");
        }

        // ---- -non_oop : the deliberate counterexample ----------------------------

        [TestMethod]
        public void NonOopSample_KeepsTheViolations_SoThePairIsAContrast()
        {
            var scan = ScanFixture("python-test-sample-non_oop");

            ProjectModel tests = scan.Solution.Projects.First(p => p.Name == "my_unit_test_package");
            Assert.AreEqual(ProjectKind.ClassLibrary, tests.Kind,
                "deliberately NOT a test project — this fixture is the 'before'");

            Assert.AreEqual(4, scan.OfCode(VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject).Count,
                "four test files in a class library");

            // Rule 1: two loose scripts at the solution root belong to no member.
            CollectionAssert.AreEquivalent(
                new[] { "debug_target.py", "exception_target.py" },
                scan.FilesOfCode(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace).ToArray());

            // Rule 2 fires on the same module-level fixtures that are legal in -oop. That
            // difference is the carve-out, isolated: same code, different project kind.
            var inTests = scan.Under(Path.Combine("my_unit_test_package", "tests"))
                .Where(d => d.Code == VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented)
                .ToList();
            Assert.IsTrue(inTests.Count > 0,
                "module-level fixtures in a class library must still be PY8002");
        }

        [TestMethod]
        public void TheTwoSamplesDifferOnlyByTheTestPackageKind_InTheirTestTrees()
        {
            // Pins the contrast itself. If someone "fixes" -non_oop, the pair stops teaching
            // anything and this fails rather than both fixtures silently going green.
            var oop = ScanFixture("python-test-sample-oop");
            var nonOop = ScanFixture("python-test-sample-non_oop");

            string rel = Path.Combine("my_unit_test_package", "tests");
            Assert.AreEqual(0, oop.Under(rel).Count, "-oop test tree silent");
            Assert.IsTrue(nonOop.Under(rel).Count > 0, "-non_oop test tree still reporting");
        }
    }
}
