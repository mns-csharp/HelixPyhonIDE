using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.ImportEngine.Analysis;
using HelixPythonNamespace.ImportEngine.Execution;
using HelixPythonNamespace.ImportEngine.Planning;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.ImportEngine.Tests
{
    /// <summary>
    /// The strict-OOP guard end to end: an imported solution is either fully
    /// object-oriented (every project/package audits clean) or the import fails
    /// with the residual violations listed.
    /// </summary>
    [TestClass]
    public class OopGuardImportTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_oop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void W(string relPath, string content = "")
        {
            string full = Path.Combine(_root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        private ImportPlan DefaultPlan(string solutionName = "demo")
        {
            ImportAnalysis analysis = new ImportAnalyzer().Analyze(_root);
            Assert.IsTrue(analysis.Success, analysis.ErrorMessage);
            return ImportPlanFactory.CreateDefault(analysis, solutionName);
        }

        [TestMethod]
        public void Import_ProceduralTree_ProducesStrictlyOopSolution()
        {
            W(@"mylib\__init__.py");
            W(@"mylib\utils.py",
              "def greet(name):\n" +
              "    return \"hi \" + name\n" +
              "\n" +
              "def shout(name):\n" +
              "    return greet(name).upper()\n");
            W(@"app\__init__.py");
            W(@"app\main.py",
              "from mylib.utils import greet\n" +
              "\n" +
              "def main():\n" +
              "    print(greet(\"world\"))\n" +
              "\n" +
              "if __name__ == \"__main__\":\n" +
              "    main()\n");

            ImportExecutionResult result = new ImportPlanExecutor().Execute(DefaultPlan());
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(result.OopConvertedModuleCount >= 1, "utils.py must have been converted");
            Assert.AreEqual(0, result.OopViolations.Count);

            // The library module was encapsulated and requalified.
            string utils = File.ReadAllText(Path.Combine(_root, "mylib", "utils.py"));
            StringAssert.Contains(utils, "class Utils:");
            StringAssert.Contains(utils, "@staticmethod");
            StringAssert.Contains(utils, "Utils.greet(name).upper()");

            // The consumer imports the class and calls through it; the entry shape survives.
            string main = File.ReadAllText(Path.Combine(_root, "app", "main.py"));
            StringAssert.Contains(main, "from mylib.utils import Utils");
            StringAssert.Contains(main, "Utils.greet(\"world\")");
            StringAssert.Contains(main, "def main():");
            StringAssert.Contains(main, "if __name__ == \"__main__\":");

            // The ENTIRE imported solution audits strictly OOP.
            Assert.AreEqual(0, PythonOopAuditor.AuditTree(Path.Combine(_root, "mylib")).Count);
            Assert.AreEqual(0, PythonOopAuditor.AuditTree(Path.Combine(_root, "app")).Count);
        }

        [TestMethod]
        public void Import_UnconvertibleModuleLevelCode_FailsWithViolationsListed()
        {
            W(@"pkg\__init__.py");
            W(@"pkg\bootstrap.py",
              "import logging\n" +
              "logger = logging.getLogger(__name__)\n" +   // loose module state
              "logger.info(\"loaded\")\n");                // executable statement

            ImportExecutionResult result = new ImportPlanExecutor().Execute(DefaultPlan());
            Assert.IsFalse(result.Success, "guarded import must fail on residual violations");
            StringAssert.Contains(result.ErrorMessage, "Strict OOP guard");
            Assert.IsTrue(result.OopViolations.Count >= 2);
            Assert.IsTrue(result.Diagnostics.Any(d => d.StartsWith("OOP violation:")),
                          "violations must be itemized in the diagnostics");
        }

        [TestMethod]
        public void Import_GuardDisabled_LeavesProceduralCodeAlone()
        {
            W(@"pkg\__init__.py");
            W(@"pkg\loose.py", "def f():\n    return 1\n\nx = f()\n");

            ImportPlan plan = DefaultPlan();
            plan.ConvertToObjectOriented = false;
            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(0, result.OopConvertedModuleCount);
            StringAssert.Contains(File.ReadAllText(Path.Combine(_root, "pkg", "loose.py")), "def f():");
        }

        [TestMethod]
        public void Analyzer_ForecastsConversionAndResiduals()
        {
            W(@"pkg\__init__.py");
            W(@"pkg\a.py", "def f():\n    return 1\n");     // convertible
            W(@"pkg\b.py", "print(\"boot\")\n");            // residual

            ImportAnalysis analysis = new ImportAnalyzer().Analyze(_root);
            Assert.IsTrue(analysis.Success, analysis.ErrorMessage);
            Assert.IsTrue(analysis.OopViolations.Any(v => v.Kind == OopViolationKind.ModuleLevelFunction));
            Assert.IsTrue(analysis.OopViolations.Any(v => v.Kind == OopViolationKind.ModuleLevelStatement));
            Assert.IsTrue(analysis.Diagnostics.Any(d => d.StartsWith("Strict-OOP:")),
                          "analysis must forecast the guard's behavior");
        }

        [TestMethod]
        public void Import_TestPackage_KeepsTestsCollectable_AndRequalifiesTheirCalls()
        {
            // The import converts the LIBRARY to strict OOP and requalifies the test's calls to
            // follow it, but leaves the test function itself at module level — that is where
            // pytest collects it, and rewriting the user's suite into classes buys nothing.
            // See HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule.
            W(@"mylib\__init__.py");
            W(@"mylib\calc.py", "def add(a, b):\n    return a + b\n");
            W(@"mylib_tests\__init__.py");
            W(@"mylib_tests\test_calc.py",
              "from mylib.calc import add\n" +
              "\n" +
              "def test_add():\n" +
              "    assert add(1, 1) == 2\n");

            ImportExecutionResult result = new ImportPlanExecutor().Execute(DefaultPlan());
            Assert.IsTrue(result.Success, result.ErrorMessage);

            string test = File.ReadAllText(Path.Combine(_root, "mylib_tests", "test_calc.py"));
            StringAssert.Contains(test, "def test_add():");
            Assert.IsFalse(test.Contains("class TestCalc"),
                "encapsulating the test would also encapsulate any fixture beside it");
            StringAssert.Contains(test, "from mylib.calc import Calc");
            StringAssert.Contains(test, "Calc.add(1, 1)");
        }

        [TestMethod]
        public void Import_TestPackageWithAConftest_Succeeds()
        {
            // The case that used to fail the whole import: the converter refuses to touch a
            // conftest (encapsulating a fixture disables it silently) and the auditor then
            // reported every fixture as an unresolved strict-OOP violation.
            W(@"mylib\__init__.py");
            W(@"mylib\calc.py", "def add(a, b):\n    return a + b\n");
            W(@"mylib_tests\__init__.py");
            W(@"mylib_tests\conftest.py",
              "import pytest\n" +
              "\n" +
              "@pytest.fixture\n" +
              "def operands():\n" +
              "    return (1, 1)\n" +
              "\n" +
              "def pytest_addoption(parser):\n" +
              "    pass\n");
            W(@"mylib_tests\test_calc.py",
              "from mylib.calc import add\n" +
              "\n" +
              "def test_add(operands):\n" +
              "    assert add(*operands) == 2\n");

            ImportExecutionResult result = new ImportPlanExecutor().Execute(DefaultPlan());
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(0, result.OopViolations.Count,
                string.Join("; ", result.OopViolations.Select(v => v.Message)));

            string conftest = File.ReadAllText(Path.Combine(_root, "mylib_tests", "conftest.py"));
            StringAssert.Contains(conftest, "def operands():");
            StringAssert.Contains(conftest, "def pytest_addoption(parser):");
        }

        [TestMethod]
        public void Import_ConftestWithAHelperAndModuleVariables_Succeeds()
        {
            // The last conftest hole: a helper beside the fixtures, plus pytest's module-level
            // protocol variables. The converter used to skip conftest wholesale, so the helper was
            // never encapsulated and the auditor failed the entire import over it.
            W(@"mylib\__init__.py");
            W(@"mylib\calc.py", "def add(a, b):\n    return a + b\n");
            W(@"mylib_tests\__init__.py");
            W(@"mylib_tests\conftest.py",
              "import pytest\n" +
              "\n" +
              "collect_ignore = [\"legacy_setup.py\"]\n" +
              "\n" +
              "def _sample():\n" +                        // the plain helper
              "    return (1, 1)\n" +
              "\n" +
              "@pytest.fixture\n" +
              "def operands():\n" +
              "    return _sample()\n");
            W(@"mylib_tests\test_calc.py",
              "from mylib.calc import add\n" +
              "\n" +
              "def test_add(operands):\n" +
              "    assert add(*operands) == 2\n");

            ImportExecutionResult result = new ImportPlanExecutor().Execute(DefaultPlan());
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.AreEqual(0, result.OopViolations.Count,
                string.Join("; ", result.OopViolations.Select(v => v.Message)));

            string conftest = File.ReadAllText(Path.Combine(_root, "mylib_tests", "conftest.py"));
            StringAssert.Contains(conftest, "class Conftest:", "the helper is encapsulated...");
            StringAssert.Contains(conftest, "@pytest.fixture\ndef operands():", "...the fixture is not");
            StringAssert.Contains(conftest, "Conftest._sample()", "and the fixture's call follows it");
            StringAssert.Contains(conftest, "collect_ignore = [\"legacy_setup.py\"]");

            // The test file still requests the fixture by parameter name, untouched.
            string test = File.ReadAllText(Path.Combine(_root, "mylib_tests", "test_calc.py"));
            StringAssert.Contains(test, "def test_add(operands):");
        }
    }
}
