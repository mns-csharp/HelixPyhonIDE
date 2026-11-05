using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Core.Model;
using HelixPythonNamespace.Workspace.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Solution policy rule 3 (PY8003) end-to-end on a REAL fixture solution: class library,
    /// executable app and executable GUI app packages must not and cannot contain unit test files,
    /// and the IDE raises a hard error on compilation, run execution and debug execution.
    ///
    /// Driven through the production path — <see cref="SolutionService.LoadAsync"/> to load the
    /// solution exactly as File ▸ Open does, then <c>MainForm.FindPolicyViolations</c> (what
    /// Compile appends to the Problems panel) and <c>MainForm.ComputeRunPreflight</c> /
    /// <c>ComputeRunPreflightFor</c> (what Run and Debug both gate on). The fixture is copied to
    /// %TEMP% first, per this suite's convention, so the fault is introduced into a COPY and the
    /// tracked fixture stays clean.
    ///
    /// The temp directories are deliberately NOT deleted: MainForm's solution-reload paths are
    /// async void, and a continuation firing after the directory is gone pops a real modal error
    /// box with no test left running to dismiss it.
    /// </summary>
    [TestClass]
    public class TestFilePlacementPolicyTests
    {
        // A private, minimal workspace keeps these policy tests independent of removed corpora.
        private static string CopyFixture()
        {
            string dest = Path.Combine(Path.GetTempPath(), "helix_py8003_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dest);
            Directory.CreateDirectory(Path.Combine(dest, "src", "pkg_alpha"));
            Directory.CreateDirectory(Path.Combine(dest, "src", "pkg_beta"));
            File.WriteAllText(Path.Combine(dest, "pyproject.toml"),
                "[project]\nname = \"policy-fixture\"\nversion = \"0.1.0\"\nrequires-python = \">=3.12\"\n\n"
              + "[tool.uv.workspace]\nmembers = [\"src/pkg_alpha\", \"src/pkg_beta\"]\n");
            foreach (string name in new[] { "pkg_alpha", "pkg_beta" })
            {
                string package = Path.Combine(dest, "src", name);
                File.WriteAllText(Path.Combine(package, "pyproject.toml"),
                    "[project]\nname = \"" + name + "\"\nversion = \"0.1.0\"\nrequires-python = \">=3.12\"\n\n"
                  + "[tool.helixstudio]\nkind = \"library\"\n");
                File.WriteAllText(Path.Combine(package, "__init__.py"), "");
                File.WriteAllText(Path.Combine(package, "models.py"), "class Model:\n    pass\n");
            }
            return dest;
        }

        private static SolutionModel Load(string root)
        {
            var result = new SolutionService().LoadAsync(Path.Combine(root, "pyproject.toml"))
                .GetAwaiter().GetResult();
            Assert.IsNotNull(result?.Solution, "the fixture solution must load");
            return result.Solution;
        }

        private static List<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> PolicyViolations(SolutionModel sln)
        {
            using (var form = new MainForm())
                return form.FindPolicyViolations(sln);
        }

        private static IEnumerable<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> MisplacedTests(
            IEnumerable<HelixPythonNamespace.VirtualCompiler.CompileDiagnostic> all)
        {
            return all.Where(d => d.Code ==
                HelixPythonNamespace.VirtualCompiler.VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject);
        }

        // Adds a real sibling test project to the workspace: its own pyproject.toml declaring
        // kind = "test", plus the [tool.uv.workspace].members entry that makes it a member.
        private static string AddTestProject(string root, string name)
        {
            string dir = Path.Combine(root, "src", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "pyproject.toml"),
                "[project]\nname = \"" + name + "\"\nversion = \"0.1.0\"\n"
                + "requires-python = \">=3.12\"\n\n[tool.helixstudio]\nkind = \"test\"\n");

            string rootToml = Path.Combine(root, "pyproject.toml");
            File.WriteAllText(rootToml, File.ReadAllText(rootToml).Replace(
                "members = [\"src/pkg_alpha\", \"src/pkg_beta\"]",
                "members = [\"src/pkg_alpha\", \"src/pkg_beta\", \"src/" + name + "\"]"));
            return dir;
        }

        [TestMethod]
        public void CleanFixture_HasNoTestPlacementViolation()
        {
            // The baseline the other tests are measured against: a real solution of two library
            // packages, no test files, nothing for rule 3 to report.
            string root = CopyFixture();
            Assert.AreEqual(0, MisplacedTests(PolicyViolations(Load(root))).Count(),
                "the untouched fixture must be clean — otherwise every assertion below is noise");
        }

        [TestMethod]
        public void TestFileInAClassLibrary_IsAHardErrorOnCompile()
        {
            string root = CopyFixture();
            string offender = Path.Combine(root, "src", "pkg_beta", "test_shapes.py");
            File.WriteAllText(offender,
                "class TestShapes:\n    def test_area(self):\n        assert True\n");

            var violations = MisplacedTests(PolicyViolations(Load(root))).ToList();
            Assert.AreEqual(1, violations.Count, "pkg_beta is a class library and must not hold tests");
            Assert.AreEqual(HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error, violations[0].Severity,
                "an error, not a warning — Compile must fail, not merely mention it");
            Assert.IsTrue(string.Equals(offender, violations[0].FilePath, StringComparison.OrdinalIgnoreCase),
                "reported against the offending file, got: " + violations[0].FilePath);
            StringAssert.Contains(violations[0].Message, "test_shapes.py");
            StringAssert.Contains(violations[0].Message, "class library");
        }

        [TestMethod]
        public void TestFileInAClassLibrary_BlocksRunAndDebug()
        {
            string root = CopyFixture();
            File.WriteAllText(Path.Combine(root, "src", "pkg_beta", "test_shapes.py"),
                "class TestShapes:\n    def test_area(self):\n        assert True\n");
            SolutionModel sln = Load(root);

            using (var form = new MainForm())
            {
                // Run: the whole-solution launch.
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan run = form.ComputeRunPreflight(sln);
                Assert.IsFalse(run.CanRun, "Run must refuse to launch while rule 3 is violated");
                StringAssert.Contains(run.BlockReason, "PY8003");

                // Debug: the per-package launch the tree's "Start Debugging" uses. A package the
                // IDE refuses to run must not become debuggable by a different route.
                ProjectModel beta = sln.Projects.First(p => p.Name == "pkg_beta");
                HelixPythonNamespace.Workspace.Execution.SolutionRunPlan debug = form.ComputeRunPreflightFor(sln, beta);
                Assert.IsFalse(debug.CanRun, "Debug must refuse to launch while rule 3 is violated");
                StringAssert.Contains(debug.BlockReason, "PY8003");
                Assert.IsTrue(debug.PolicyViolations != null && debug.PolicyViolations.Count > 0,
                    "the blocked launch must carry the violations so they reach the Problems panel");
            }
        }

        [TestMethod]
        public void EveryTestFileConvention_IsCaughtInTheRealSolution()
        {
            // Names AND pytest's directory/conftest conventions, on real fixture files.
            string root = CopyFixture();
            string pkg = Path.Combine(root, "src", "pkg_beta");
            File.WriteAllText(Path.Combine(pkg, "test_a.py"), "class TestA:\n    pass\n");
            File.WriteAllText(Path.Combine(pkg, "b_test.py"), "class BTest:\n    pass\n");
            File.WriteAllText(Path.Combine(pkg, "conftest.py"), "class Fixtures:\n    pass\n");
            Directory.CreateDirectory(Path.Combine(pkg, "tests"));
            File.WriteAllText(Path.Combine(pkg, "tests", "helpers.py"), "class Helpers:\n    pass\n");

            Assert.AreEqual(4, MisplacedTests(PolicyViolations(Load(root))).Count(),
                "all four conventions must be caught, not just the test_ prefix");
        }

        [TestMethod]
        public void MovingTheTestsToASiblingTestProject_ClearsTheViolation()
        {
            // The resolution the message prescribes actually resolves it — the point of the rule
            // is a layout the user can reach, not a wall.
            string root = CopyFixture();
            string offender = Path.Combine(root, "src", "pkg_beta", "test_shapes.py");
            File.WriteAllText(offender, "class TestShapes:\n    def test_area(self):\n        assert True\n");
            Assert.AreEqual(1, MisplacedTests(PolicyViolations(Load(root))).Count(), "precondition");

            string testProject = AddTestProject(root, "pkg_beta_tests");
            File.Move(offender, Path.Combine(testProject, "test_shapes.py"));

            SolutionModel sln = Load(root);
            Assert.AreEqual(0, MisplacedTests(PolicyViolations(sln)).Count(),
                "the same file is legal inside a kind = \"test\" project");
            Assert.AreEqual(HelixPythonNamespace.Core.Model.Enums.ProjectKind.TestProject,
                sln.Projects.First(p => p.Name == "pkg_beta_tests").Kind);
        }

        [TestMethod]
        public void PreExistingTestsProject_IsRecognizedByName_WithoutAKindToken()
        {
            // Back-compat: test packages predating ProjectKind.TestProject were stored as
            // kind = "library" and identified only by the <project>_tests naming convention.
            // Without the load-time inference, shipping rule 3 would block Run and Debug on
            // solutions the user never changed.
            string root = CopyFixture();
            string dir = Path.Combine(root, "src", "pkg_beta_tests");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "pyproject.toml"),
                "[project]\nname = \"pkg_beta_tests\"\nversion = \"0.1.0\"\n"
                + "requires-python = \">=3.12\"\n\n[tool.helixstudio]\nkind = \"library\"\n");
            File.WriteAllText(Path.Combine(dir, "test_shapes.py"), "class TestShapes:\n    pass\n");

            string rootToml = Path.Combine(root, "pyproject.toml");
            File.WriteAllText(rootToml, File.ReadAllText(rootToml).Replace(
                "members = [\"src/pkg_alpha\", \"src/pkg_beta\"]",
                "members = [\"src/pkg_alpha\", \"src/pkg_beta\", \"src/pkg_beta_tests\"]"));

            SolutionModel sln = Load(root);
            Assert.AreEqual(HelixPythonNamespace.Core.Model.Enums.ProjectKind.TestProject,
                sln.Projects.First(p => p.Name == "pkg_beta_tests").Kind,
                "a *_tests library must be inferred as a test project at load time");
            Assert.AreEqual(0, MisplacedTests(PolicyViolations(sln)).Count(),
                "an existing solution must not start failing the moment the rule ships");
        }
    }
}
