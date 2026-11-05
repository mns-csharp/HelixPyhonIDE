using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HelixPythonNamespace.Core.Model.Enums;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.VirtualCompiler.Compliance;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// The three solution-policy rules, enforced as hard blockers (PY8001/PY8002/PY8003).
    ///
    /// Rule 1: user-written source must belong to a workspace member — the IDE must not execute
    /// code it cannot show, compile-check or debug.
    /// Rule 2: user-written code must be object-oriented.
    /// Rule 3: only a test project may contain test files.
    /// All exempt third-party packages.
    /// </summary>
    [TestClass]
    public class SolutionPolicyTests
    {
        private string _root;

        [TestInitialize]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix-policy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "projects", "app"));
        }

        private string Write(string relative, string text)
        {
            string p = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
            return p;
        }

        private SolutionPolicy Policy()
        {
            return new SolutionPolicy(_root, new[] { Path.Combine(_root, "projects", "app") });
        }

        private CompileDiagnostic[] Check(string file, string source, SolutionPolicy policy = null)
        {
            var options = new CompileOptions
            {
                EnableAstValidator = false, EnableSymtablePass = false,
                EnableCompileValidator = false, EnableTypeChecker = false,
                EnableImportResolution = false,
                Policy = policy ?? Policy(),
            };
            return VirtualCompiler.CompileSource(source, file, options).Diagnostics
                .Where(d => d.Code.StartsWith("PY8", StringComparison.Ordinal)).ToArray();
        }

        // ---- Rule 1: workspace membership -------------------------------------

        [TestMethod]
        public void FileInsideAMemberProject_IsAllowed()
        {
            string f = Write(@"projects\app\service.py", "class Service:\n    pass\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f)).Length);
        }

        [TestMethod]
        public void FileOutsideEveryMember_IsBlocked()
        {
            string f = Write(@"src\helper.py", "class Helper:\n    pass\n");
            var d = Check(f, File.ReadAllText(f));
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, d[0].Code);
            Assert.AreEqual(HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error, d[0].Severity,
                "must be an error so the Run preflight blocks on it");
        }

        [TestMethod]
        public void ThirdPartyOutsideTheWorkspace_IsExempt()
        {
            foreach (string dir in new[] { "_thirdparty", "site-packages", @"bioshell\external",
                                           ".venv", "node_modules", "vendor" })
            {
                string f = Write(Path.Combine(dir, "lib.py"), "def helper():\n    return 1\n");
                Assert.AreEqual(0, Check(f, File.ReadAllText(f)).Length,
                    dir + " is a dependency the user cannot restructure; it must not block Run");
            }
        }

        // ---- Rule 2: object orientation ---------------------------------------

        [TestMethod]
        public void ModuleLevelFunction_IsBlocked()
        {
            string f = Write(@"projects\app\util.py", "def compute(x):\n    return x * 2\n");
            var d = Check(f, File.ReadAllText(f));
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented, d[0].Code);
            StringAssert.Contains(d[0].Message, "compute");
        }

        [TestMethod]
        public void ClassWithMethods_IsAllowed()
        {
            string f = Write(@"projects\app\svc.py",
                "class Service:\n    @staticmethod\n    def compute(x):\n        return x * 2\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f)).Length,
                "methods inside a class are not module-level functions");
        }

        [TestMethod]
        public void EntryPointMainStaysAtModuleLevel()
        {
            // PSM-06..24 REQUIRE a top-level def main(); Python cannot express a process entry
            // point inside a class. Without this carve-out the rule blocks every executable.
            string f = Write(@"projects\app\main.py",
                "class App:\n    def run(self):\n        return 0\n\n\n"
                + "def main() -> int:\n    return App().run()\n\n\n"
                + "if __name__ == \"__main__\":\n    raise SystemExit(main())\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f)).Length);
        }

        [TestMethod]
        public void OtherEntryModuleNameVariants_AreAlsoExempt()
        {
            foreach (string name in new[] { "main", "__main__", "_main", "main_", "app_main" })
            {
                string f = Write(@"projects\app\e_" + name.Trim('_') + ".py",
                    "def " + name + "():\n    return 0\n");
                Assert.AreEqual(0, Check(f, File.ReadAllText(f)).Length, name + " should be exempt");
            }
        }

        [TestMethod]
        public void ModuleLevelConstantsAndImports_AreNotViolations()
        {
            string f = Write(@"projects\app\consts.py",
                "import os\n\nMAX = 10\nNAMES = [\"a\", \"b\"]\n\nclass C:\n    pass\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f)).Length,
                "only module-level FUNCTIONS are procedural; data and imports are not");
        }

        [TestMethod]
        public void EveryOffendingFunctionIsReported_NotJustTheFirst()
        {
            string f = Write(@"projects\app\many.py", "def a():\n    pass\n\ndef b():\n    pass\n");
            Assert.AreEqual(2, Check(f, File.ReadAllText(f)).Length);
        }

        // ---- both rules at once, and unsuppressibility --------------------------

        [TestMethod]
        public void ProceduralFileOutsideTheWorkspace_ReportsBothRules()
        {
            string f = Write(@"scripts\tool.py", "def go():\n    return 1\n");
            var d = Check(f, File.ReadAllText(f));
            CollectionAssert.AreEquivalent(
                new[] { VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace,
                        VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented },
                d.Select(x => x.Code).ToArray());
        }

        [TestMethod]
        public void NoqaCannotSilenceAHardBlocker()
        {
            // Every other diagnostic honours these comments; a policy blocker must not be
            // bypassable by a one-line comment.
            string f = Write(@"projects\app\sneaky.py", "def compute(x):  # noqa\n    return x\n");
            Assert.AreEqual(1, Check(f, File.ReadAllText(f)).Length, "# noqa must not suppress PY8002");

            string g = Write(@"projects\app\sneaky2.py", "def compute(x):  # type: ignore\n    return x\n");
            Assert.AreEqual(1, Check(g, File.ReadAllText(g)).Length, "# type: ignore must not suppress PY8002");
        }

        // ---- Rule 2 is user-configurable -------------------------------------
        //
        // Tools ▸ Preferences ▸ General ▸ "Allow non-object-oriented source" clears
        // EnforceObjectOrientation, for solutions of existing procedural code the user is not in
        // a position to restructure.

        [TestMethod]
        public void ObjectOrientationRule_CanBeTurnedOff()
        {
            string f = Write(@"projects\app\procedural.py", "def compute(x):\n    return x\n");

            SolutionPolicy relaxed = Policy();
            relaxed.EnforceObjectOrientation = false;

            Assert.AreEqual(1, Check(f, File.ReadAllText(f)).Length, "on by default");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f), relaxed).Length,
                "PY8002 must disappear entirely when the user allows procedural source");
        }

        [TestMethod]
        public void TurningOffTheObjectOrientationRule_LeavesWorkspaceMembershipEnforced()
        {
            // Rule 1 is deliberately not user-configurable: the IDE still refuses to execute
            // source it cannot show, compile-check or breakpoint.
            string f = Write(@"loose\outside.py", "def compute(x):\n    return x\n");

            SolutionPolicy relaxed = Policy();
            relaxed.EnforceObjectOrientation = false;

            CompileDiagnostic[] found = Check(f, File.ReadAllText(f), relaxed);
            Assert.AreEqual(1, found.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, found[0].Code);
        }

        // ---- Rule 3: only a test project may contain test files (PY8003) --------
        //
        // A class library, an executable app and an executable GUI app must not and cannot hold
        // unit tests: a package that ships its own tests cannot be built, packaged or referenced
        // apart from its test harness, and per-project Run Tests / Debug Tests cannot be offered
        // against a project that is simultaneously the subject and the test.

        // Solution shape for these: `projects\app` (the non-test member, as in Policy()) plus
        // `projects\app_tests`, a real test project.
        private SolutionPolicy PolicyWithTestProject()
        {
            string app = Path.Combine(_root, "projects", "app");
            string tests = Path.Combine(_root, "projects", "app_tests");
            Directory.CreateDirectory(tests);
            return new SolutionPolicy(_root, new[] { app, tests },
                new[]
                {
                    new KeyValuePair<string, ProjectKind>(app, ProjectKind.ClassLibrary),
                    new KeyValuePair<string, ProjectKind>(tests, ProjectKind.TestProject),
                });
        }

        [TestMethod]
        public void TestFileInsideANonTestProject_IsBlocked()
        {
            string f = Write(@"projects\app\test_service.py",
                "class TestService:\n    def test_it(self):\n        assert True\n");
            var d = Check(f, File.ReadAllText(f), PolicyWithTestProject());
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject, d[0].Code);
            Assert.AreEqual(HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error, d[0].Severity,
                "must be an error so Compile, Run and Debug all block on it");
            StringAssert.Contains(d[0].Message, "test_service.py");
        }

        [TestMethod]
        public void TestFileInsideATestProject_IsAllowed()
        {
            string f = Write(@"projects\app_tests\test_service.py",
                "class TestService:\n    def test_it(self):\n        assert True\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f), PolicyWithTestProject()).Length,
                "a test project is the one place test files belong");
        }

        [TestMethod]
        public void EveryTestFileNamingConvention_IsRecognized()
        {
            // test_*.py, *_test.py, pytest's conftest.py, and anything under tests/ or test/.
            foreach (string relative in new[]
                     {
                         @"projects\app\test_math.py",
                         @"projects\app\math_test.py",
                         @"projects\app\conftest.py",
                         @"projects\app\tests\helpers.py",
                         @"projects\app\test\helpers.py",
                     })
            {
                string f = Write(relative, "class C:\n    pass\n");
                var d = Check(f, File.ReadAllText(f), PolicyWithTestProject());
                Assert.AreEqual(1, d.Length, relative + " should be recognized as a test file");
                Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject, d[0].Code);
            }
        }

        [TestMethod]
        public void OrdinarySourceThatMerelyMentionsTest_IsNotATestFile()
        {
            // The rule must not fire on production code whose name happens to contain "test" —
            // a false positive here blocks Run over a file the user has no reason to move.
            foreach (string relative in new[]
                     {
                         @"projects\app\testing_utils.py",
                         @"projects\app\latest.py",
                         @"projects\app\contest.py",
                         @"projects\app\tests_helpers\thing.py",
                     })
            {
                string f = Write(relative, "class C:\n    pass\n");
                Assert.AreEqual(0, Check(f, File.ReadAllText(f), PolicyWithTestProject()).Length,
                    relative + " is ordinary source, not a test file");
            }
        }

        [TestMethod]
        public void TestFileOutsideEveryMember_ReportsMembershipOnly()
        {
            // Rule 1 already tells the user to move this file into a project. Adding rule 3 on top
            // would tell them to move the same file to two different places at once.
            string f = Write(@"loose\test_stray.py", "class TestStray:\n    pass\n");
            var d = Check(f, File.ReadAllText(f), PolicyWithTestProject());
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace, d[0].Code);
        }

        [TestMethod]
        public void ANestedNonTestMember_IsNotExemptedByItsTestProjectAncestor()
        {
            // Ownership is the DEEPEST matching member. A test project containing a nested
            // library member must not launder that library's test files.
            string tests = Path.Combine(_root, "projects", "app_tests");
            string nested = Path.Combine(tests, "nested_lib");
            Directory.CreateDirectory(nested);
            var policy = new SolutionPolicy(_root,
                new[] { Path.Combine(_root, "projects", "app"), tests, nested },
                new[]
                {
                    new KeyValuePair<string, ProjectKind>(tests, ProjectKind.TestProject),
                    new KeyValuePair<string, ProjectKind>(nested, ProjectKind.ClassLibrary),
                });

            string f = Write(@"projects\app_tests\nested_lib\test_thing.py", "class TestThing:\n    pass\n");
            var d = Check(f, File.ReadAllText(f), policy);
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject, d[0].Code);
        }

        [TestMethod]
        public void ThirdPartyTestFiles_AreExempt()
        {
            // Dependencies ship their own test suites; policing them would block Run over code
            // the user does not own and cannot restructure.
            string f = Write(@".venv\Lib\site-packages\dep\test_dep.py", "class TestDep:\n    pass\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f), PolicyWithTestProject()).Length);
        }

        [TestMethod]
        public void MisplacedTestFile_IsUnsuppressible()
        {
            string f = Write(@"projects\app\test_sneaky.py", "class TestSneaky:  # noqa\n    pass\n");
            Assert.AreEqual(1, Check(f, File.ReadAllText(f), PolicyWithTestProject()).Length,
                "# noqa must not smuggle a test file past a hard blocker");
        }

        [TestMethod]
        public void TestPlacementRule_IsSeparableFromTheOtherTwo()
        {
            string f = Write(@"projects\app\test_off.py", "class TestOff:\n    pass\n");
            SolutionPolicy relaxed = PolicyWithTestProject();
            relaxed.EnforceTestFilePlacement = false;
            Assert.AreEqual(0, Check(f, File.ReadAllText(f), relaxed).Length);
            Assert.AreEqual(1, Check(f, File.ReadAllText(f), PolicyWithTestProject()).Length,
                "enforced by default — the IDE ships with the rule on");
        }

        // ---- Rule 3, content detection ----------------------------------------
        //
        // The name/directory conventions are only conventions. A file called verification.py that
        // imports pytest and declares a TestCalculator class is test material whatever it is
        // named, and it escaped rule 2 as well — a unittest.TestCase subclass is object-oriented.
        // Neither runner would COLLECT these by default (both key on file-name patterns), so this
        // catches test code by intent, which is the point: a library must not carry tests whether
        // or not today's runner config happens to execute them.

        private CompileDiagnostic[] CheckUnnamed(string source)
        {
            // A deliberately innocuous file name, so ONLY content detection can fire.
            string f = Write(@"projects\app\verification.py", source);
            return Check(f, source, PolicyWithTestProject());
        }

        [TestMethod]
        public void PytestClass_WithFrameworkImport_IsDetected()
        {
            var d = CheckUnnamed(
                "import pytest\n\n\nclass TestCalculator:\n    def test_add(self):\n        assert True\n");
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyTestFileInNonTestProject, d[0].Code);
            StringAssert.Contains(d[0].Message, "declares unit tests",
                "the message must explain itself — nothing about 'verification.py' suggests a test");
        }

        [TestMethod]
        public void UnittestTestCaseSubclass_IsDetected()
        {
            foreach (string source in new[]
                     {
                         "import unittest\n\n\nclass Checks(unittest.TestCase):\n    def test_add(self):\n        self.assertTrue(True)\n",
                         "from unittest import TestCase\n\n\nclass Checks(TestCase):\n    def test_add(self):\n        pass\n",
                         "import unittest\n\n\nclass Checks(unittest.IsolatedAsyncioTestCase):\n    async def test_add(self):\n        pass\n",
                     })
                Assert.AreEqual(1, CheckUnnamed(source).Length, source);
        }

        [TestMethod]
        public void PytestStyleTestClass_IsDetected_WithNoImportAtAll()
        {
            // pytest's convention needs no import, so no import check could ever see this.
            Assert.AreEqual(1, CheckUnnamed(
                "class TestCalculator:\n    def test_add(self):\n        assert True\n").Length);
        }

        [TestMethod]
        public void PytestFixturesWithoutAnyTestNamedCallable_AreDetected()
        {
            Assert.AreEqual(1, CheckUnnamed(
                "import pytest\n\n\nclass Fixtures:\n    @pytest.fixture\n    def client(self):\n        return None\n").Length);
        }

        [TestMethod]
        public void ImportingAFrameworkWithoutDeclaringTests_StaysLegal()
        {
            // The guard that keeps merely-adjacent code runnable. Rule 3 is a hard blocker with no
            // toggle, so production code that touches unittest.mock must not become unshippable.
            foreach (string source in new[]
                     {
                         "from unittest.mock import Mock\n\n\nclass Client:\n    def send(self):\n        return Mock()\n",
                         "import unittest\n\n\nclass Runner:\n    def go(self):\n        return unittest.TestLoader()\n",
                         "from typing import TYPE_CHECKING\n\nif TYPE_CHECKING:\n    import pytest\n\n\nclass Helper:\n    def use(self):\n        return None\n",
                     })
                Assert.AreEqual(0, CheckUnnamed(source).Length, source);
        }

        [TestMethod]
        public void OrdinaryProductionCode_IsNotDetected()
        {
            Assert.AreEqual(0, CheckUnnamed(
                "class Calculator:\n    def add(self, a, b):\n        return a + b\n").Length);
        }

        [TestMethod]
        public void ATestNamedClassWithoutTestMethods_IsNotDetected()
        {
            // `Test*` alone is not enough — a domain class called TestPlan or TestFixtureBuilder
            // whose methods are ordinary names stays legal.
            Assert.AreEqual(0, CheckUnnamed(
                "class TestPlan:\n    def schedule(self):\n        return []\n").Length);
        }

        [TestMethod]
        public void AProductionClassNamedTestWithTestMethods_IsAnAcceptedFalsePositive()
        {
            // Documented, deliberately chosen cost of catching import-free pytest classes: this
            // shape is indistinguishable from a pytest test class by content alone. There is no
            // toggle, so the resolution is to rename the class or its method.
            Assert.AreEqual(1, CheckUnnamed(
                "class TestRunner:\n    def test_connection(self):\n        return True\n").Length,
                "known trade-off — if this ever needs to pass, the rule itself must change");
        }

        [TestMethod]
        public void ContentDetectionIsScopedToNonTestProjects()
        {
            string f = Write(@"projects\app_tests\verification.py",
                "import pytest\n\n\nclass TestCalculator:\n    def test_add(self):\n        assert True\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f), PolicyWithTestProject()).Length,
                "content detection must not fire inside the one project allowed to hold tests");
        }

        // ---- Rule 2's test-protocol carve-out ---------------------------------

        // Rule 3 requires the user's tests to live in a kind = "test" project. Without a carve-out
        // rule 2 then forbids the only shape pytest and unittest can collect, and the two rules
        // contradict each other — the only escape being the IDE-wide "Allow non-object-oriented
        // source" switch, which buys one ordinary test at the cost of the guarantee on every line
        // of production code. So inside a test project the runner protocol may stay module-level,
        // exactly as `def main()` may in an executable. Nothing wider.

        [TestMethod]
        public void ModuleLevelTestFunctions_InATestProject_AreAllowed()
        {
            string f = Write(@"projects\app_tests\test_calculator.py",
                "def test_add():\n    assert True\n\n\nasync def test_add_async():\n    assert True\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f), PolicyWithTestProject()).Length,
                "pytest collects test functions by module-level name; there is no class form");
        }

        [TestMethod]
        public void PytestFixtures_InATestProject_AreAllowed()
        {
            // The case with no workaround at all: a fixture is only visible to other modules when
            // it is declared at the top level of conftest.py. Inside a class pytest never finds
            // it, and the suite keeps running with the fixture silently unapplied.
            string f = Write(@"projects\app_tests\conftest.py",
                "import pytest\n\n\n@pytest.fixture\ndef db():\n    return None\n" +
                "\n\n@pytest.fixture(scope=\"module\")\ndef client():\n    return None\n" +
                "\n\nfrom pytest import fixture\n\n\n@fixture\ndef bare():\n    return None\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f), PolicyWithTestProject()).Length,
                "@pytest.fixture, the called form, and a bare @fixture must all be exempt");
        }

        [TestMethod]
        public void RunnerHooks_InATestProject_AreAllowed()
        {
            // pytest_* hooks are the whole conftest extension surface, and the xunit-style names
            // are fetched as module attributes — a class member of the same name is never found.
            string f = Write(@"projects\app_tests\conftest.py",
                "def pytest_addoption(parser):\n    pass\n\n\n"
                + "def pytest_collection_modifyitems(items):\n    pass\n\n\n"
                + "def setup_module(module):\n    pass\n\n\n"
                + "def teardown_module(module):\n    pass\n\n\n"
                + "def setUpModule():\n    pass\n\n\n"
                + "def load_tests(loader, tests, pattern):\n    return tests\n");
            Assert.AreEqual(0, Check(f, File.ReadAllText(f), PolicyWithTestProject()).Length);
        }

        [TestMethod]
        public void AnOrdinaryHelper_InATestProject_IsStillBlocked()
        {
            // The carve-out is exactly as wide as the runner protocol. A helper the runner never
            // looks for by name can be a class like any other, so rule 2 still applies to it.
            string f = Write(@"projects\app_tests\test_calculator.py",
                "def test_add():\n    assert build_payload()\n\n\ndef build_payload():\n    return True\n");
            var d = Check(f, File.ReadAllText(f), PolicyWithTestProject());
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented, d[0].Code);
            StringAssert.Contains(d[0].Message, "build_payload");
        }

        [TestMethod]
        public void ATestNamedFunction_InANonTestProject_IsStillBlocked()
        {
            // The carve-out is gated on the PROJECT KIND, not the function name. If it keyed on
            // the name, any production file could opt out of rule 2 by calling its functions
            // test_something — and rule 2 would be advisory rather than a blocker.
            string f = Write(@"projects\app\svc.py", "def test_connection():\n    return True\n");
            var d = Check(f, File.ReadAllText(f), PolicyWithTestProject());
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented, d[0].Code);
        }

        [TestMethod]
        public void AFixtureDecoratedFunction_InANonTestProject_IsStillBlocked()
        {
            // No `import pytest`, so rule 3's content detection cannot fire (limb 3 is a
            // conjunction) — this isolates rule 2 and proves the decorator alone exempts nothing
            // outside a test project.
            string f = Write(@"projects\app\svc.py", "@pytest.fixture\ndef db():\n    return None\n");
            var d = Check(f, File.ReadAllText(f), PolicyWithTestProject());
            Assert.AreEqual(1, d.Length);
            Assert.AreEqual(VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented, d[0].Code);
        }

        [TestMethod]
        public void TheCarveOutIsExplainedInTheMessage_InsideATestProject()
        {
            // A user who hits PY8002 on a helper in a test project must be able to tell that
            // tests themselves are fine — otherwise the obvious reading is "tests are banned
            // here" and the Preferences switch looks like the answer again.
            string f = Write(@"projects\app_tests\test_calculator.py", "def build_payload():\n    return True\n");
            var d = Check(f, File.ReadAllText(f), PolicyWithTestProject());
            Assert.AreEqual(1, d.Length);
            StringAssert.Contains(d[0].Message, "@pytest.fixture");
        }

        [TestMethod]
        public void TheCarveOutDoesNotReachAFileNoTestProjectOwns()
        {
            // A loose test file outside every member is rule 1's business. The carve-out must not
            // quietly make it acceptable, or PY8001 would be the only thing left holding it.
            string f = Write(@"scripts\test_loose.py", "def test_add():\n    assert True\n");
            var d = Check(f, File.ReadAllText(f), PolicyWithTestProject());
            CollectionAssert.AreEquivalent(
                new[] { VirtualCompilerDiagnosticCodes.PolicyFileOutsideWorkspace,
                        VirtualCompilerDiagnosticCodes.PolicyNonObjectOriented },
                d.Select(x => x.Code).ToArray());
        }

        [TestMethod]
        public void PolicyIsInert_WhenNoneIsSupplied()
        {
            // Callers with no solution context (single-file editor compiles) must not start
            // reporting policy errors they have no basis to judge.
            string f = Write(@"anywhere\loose.py", "def go():\n    return 1\n");
            var options = new CompileOptions
            {
                EnableAstValidator = false, EnableSymtablePass = false,
                EnableCompileValidator = false, EnableTypeChecker = false,
                EnableImportResolution = false,
            };
            Assert.AreEqual(0, VirtualCompiler.CompileSource(File.ReadAllText(f), f, options)
                .Diagnostics.Count(d => d.Code.StartsWith("PY8", StringComparison.Ordinal)));
        }
    }
}
