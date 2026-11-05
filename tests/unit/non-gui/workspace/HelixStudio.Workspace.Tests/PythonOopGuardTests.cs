using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Unit coverage for the strict-OOP guard trio: PythonOopAuditor (detects),
    /// PythonOopConverter (encapsulates + requalifies same-module references),
    /// PythonOopReferenceRewriter (requalifies cross-module references).
    /// </summary>
    [TestClass]
    public class PythonOopGuardTests
    {
        // ---- auditor ----

        [TestMethod]
        public void Auditor_CompliantModule_HasNoViolations()
        {
            string src =
                "\"\"\"Docstring.\"\"\"\n" +
                "import os\n" +
                "from typing import List\n" +
                "\n" +
                "__all__ = [\"Point\"]\n" +
                "__version__ = \"1.0\"\n" +
                "MAX_SIZE = 100\n" +
                "\n" +
                "class Point:\n" +
                "    def __init__(self, x):\n" +
                "        self.x = x\n";
            var v = PythonOopAuditor.AuditSource(src, "point.py", false);
            Assert.AreEqual(0, v.Count, string.Join("; ", v.Select(x => x.Message)));
        }

        [TestMethod]
        public void Auditor_ModuleLevelFunction_IsFlagged()
        {
            var v = PythonOopAuditor.AuditSource("def helper():\n    return 1\n", "util.py", false);
            Assert.AreEqual(1, v.Count);
            Assert.AreEqual(OopViolationKind.ModuleLevelFunction, v[0].Kind);
            Assert.AreEqual("helper", v[0].Name);
        }

        [TestMethod]
        public void Auditor_EntryModule_AllowsMainAndGuardOnly()
        {
            string src =
                "def main():\n" +
                "    pass\n" +
                "\n" +
                "if __name__ == \"__main__\":\n" +
                "    main()\n";
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(src, "main.py", true).Count);
            // The same shape in a NON-entry module is two violations.
            Assert.AreEqual(2, PythonOopAuditor.AuditSource(src, "not_entry.py", false).Count);
        }

        [TestMethod]
        public void Auditor_ExecutableStatements_AndLooseState_AreFlagged()
        {
            string src =
                "import logging\n" +
                "logger = logging.getLogger(__name__)\n" +   // non-constant state
                "print(\"hi\")\n" +                          // executable statement
                "for i in range(3):\n    pass\n";            // executable statement
            var v = PythonOopAuditor.AuditSource(src, "m.py", false);
            Assert.AreEqual(3, v.Count);
            Assert.AreEqual(OopViolationKind.NonConstantAssignment, v[0].Kind);
            Assert.AreEqual(OopViolationKind.ModuleLevelStatement, v[1].Kind);
            Assert.AreEqual(OopViolationKind.ModuleLevelStatement, v[2].Kind);
        }

        [TestMethod]
        public void Auditor_TypeCheckingImportGuard_IsAllowed()
        {
            string src =
                "from typing import TYPE_CHECKING\n" +
                "if TYPE_CHECKING:\n" +
                "    from foo import Bar\n";
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(src, "m.py", false).Count);
        }

        // ---- converter ----

        [TestMethod]
        public void Converter_WrapsFunctions_AndQualifiesSameModuleReferences()
        {
            string src =
                "import math\n" +
                "\n" +
                "def area(r):\n" +
                "    return math.pi * r * r\n" +
                "\n" +
                "def ring(r1, r2):\n" +
                "    return area(r2) - area(r1)\n";
            var r = PythonOopConverter.ConvertModule(src, "shapes", false);
            Assert.IsTrue(r.Changed);
            Assert.AreEqual("Shapes", r.ClassName);
            CollectionAssert.AreEqual(new[] { "area", "ring" }, r.MovedFunctions.ToArray());
            StringAssert.Contains(r.Text, "class Shapes:");
            StringAssert.Contains(r.Text, "    @staticmethod");
            StringAssert.Contains(r.Text, "    def area(r):");
            StringAssert.Contains(r.Text, "Shapes.area(r2) - Shapes.area(r1)");
            // The converted module itself audits clean.
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(r.Text, "shapes.py", false).Count,
                            "converted module must be strictly OOP");
        }

        [TestMethod]
        public void Converter_PreservesEntryShape_AndQualifiesMainBody()
        {
            string src =
                "def helper():\n" +
                "    return 41\n" +
                "\n" +
                "def main():\n" +
                "    print(helper() + 1)\n" +
                "\n" +
                "if __name__ == \"__main__\":\n" +
                "    main()\n";
            var r = PythonOopConverter.ConvertModule(src, "main", true);
            Assert.IsTrue(r.Changed);
            CollectionAssert.AreEqual(new[] { "helper" }, r.MovedFunctions.ToArray());
            StringAssert.Contains(r.Text, "def main():");                 // stays module-level
            StringAssert.Contains(r.Text, "Main.helper()");               // body requalified
            StringAssert.Contains(r.Text, "if __name__ == \"__main__\":");
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(r.Text, "main.py", true).Count);
        }

        [TestMethod]
        public void Converter_RewritesDunderAll()
        {
            string src =
                "__all__ = [\"greet\", \"Point\"]\n" +
                "\n" +
                "class Point:\n" +
                "    pass\n" +
                "\n" +
                "def greet():\n" +
                "    return \"hi\"\n";
            var r = PythonOopConverter.ConvertModule(src, "api", false);
            Assert.IsTrue(r.Changed);
            StringAssert.Contains(r.Text, "__all__ = [\"Api\", \"Point\"]");
        }

        [TestMethod]
        public void Converter_LeavesAlreadyOopModules_Alone()
        {
            var oop = PythonOopConverter.ConvertModule(
                "class Ready:\n    def go(self):\n        pass\n", "ready", false);
            Assert.IsFalse(oop.Changed);
        }

        [TestMethod]
        public void Converter_AConftestHelper_IsEncapsulated_NotSkippedWithTheWholeFile()
        {
            // conftest used to be skipped wholesale, which protected the fixtures but left this
            // helper unconverted — and the auditor then failed the whole import over it. Now the
            // helper moves and the fixture stays.
            var r = PythonOopConverter.ConvertModule(
                "def _load_sample_data():\n    return [1, 2, 3]\n", "conftest", false);
            Assert.IsTrue(r.Changed, "a plain helper in a conftest is ordinary procedural code");
            Assert.AreEqual("Conftest", r.ClassName,
                "NOT TestConftest — pytest would try to collect a Test-prefixed class");
            CollectionAssert.AreEquivalent(new[] { "_load_sample_data" }, r.MovedFunctions.ToArray());
        }

        [TestMethod]
        public void Converter_ParameterShadowing_IsNotRequalified()
        {
            string src =
                "def f():\n" +
                "    return 1\n" +
                "\n" +
                "def g(f):\n" +
                "    return f(1)\n";
            var r = PythonOopConverter.ConvertModule(src, "shadow", false);
            Assert.IsTrue(r.Changed);
            StringAssert.Contains(r.Text, "    def g(f):");
            StringAssert.Contains(r.Text, "        return f(1)");  // param use untouched
        }

        [TestMethod]
        public void Converter_TestModule_GetsPytestCollectableClassName()
        {
            // The wrapper class a test module's HELPERS move into keeps its Test-prefixed name.
            // The test function itself is no longer moved into it — TestProtocolFunctionRule keeps
            // it at module level, which is where pytest collects it.
            var r = PythonOopConverter.ConvertModule(
                "def test_add():\n    assert build() == 2\n\n\ndef build():\n    return 2\n",
                "test_math", false);
            Assert.IsTrue(r.Changed);
            Assert.AreEqual("TestMath", r.ClassName);
            StringAssert.Contains(r.Text, "class TestMath:");
            CollectionAssert.AreEquivalent(new[] { "build" }, r.MovedFunctions.ToArray(),
                "only the helper moves");
            StringAssert.Contains(r.Text, "def test_add():");
        }

        // ---- cross-module rewriter ----

        private static OopModuleConversion Conv(string tempDir, string rel, string dotted,
                                                string className, params string[] moved)
        {
            return new OopModuleConversion(Path.Combine(tempDir, rel), dotted, className, moved);
        }

        [TestMethod]
        public void Rewriter_FromImport_BecomesClassImport_WithQualifiedUses()
        {
            string dir = Path.Combine(Path.GetTempPath(), "oop_rw_" + Guid.NewGuid().ToString("N"));
            string consumer =
                "from pkg.utils import greet, farewell as bye, Point\n" +
                "\n" +
                "class App:\n" +
                "    def run(self):\n" +
                "        return greet() + bye() + str(Point())\n";
            var conv = Conv(dir, Path.Combine("pkg", "utils.py"), "pkg.utils", "Utils", "greet", "farewell");
            var r = PythonOopReferenceRewriter.RewriteConsumer(
                consumer, Path.Combine(dir, "app", "app.py"), conv);
            Assert.IsTrue(r.Count > 0);
            StringAssert.Contains(r.Text, "from pkg.utils import Utils, Point");
            StringAssert.Contains(r.Text, "Utils.greet()");
            StringAssert.Contains(r.Text, "Utils.farewell()");   // alias 'bye' resolved
            Assert.IsFalse(r.Text.Contains("bye()"), "alias use sites must be requalified");
        }

        [TestMethod]
        public void Rewriter_ModuleObjectImports_GainTheClassSegment()
        {
            string dir = Path.Combine(Path.GetTempPath(), "oop_rw_" + Guid.NewGuid().ToString("N"));
            string consumer =
                "import pkg.utils\n" +
                "import pkg.utils as u\n" +
                "from pkg import utils\n" +
                "\n" +
                "class App:\n" +
                "    def run(self):\n" +
                "        return pkg.utils.greet() + u.greet() + utils.greet()\n";
            var conv = Conv(dir, Path.Combine("pkg", "utils.py"), "pkg.utils", "Utils", "greet");
            var r = PythonOopReferenceRewriter.RewriteConsumer(
                consumer, Path.Combine(dir, "app", "app.py"), conv);
            StringAssert.Contains(r.Text, "pkg.utils.Utils.greet()");
            StringAssert.Contains(r.Text, "u.Utils.greet()");
            StringAssert.Contains(r.Text, "utils.Utils.greet()");
        }

        [TestMethod]
        public void Rewriter_RelativeFromImport_ResolvesAgainstConsumerPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "oop_rw_" + Guid.NewGuid().ToString("N"));
            string consumer =
                "from .utils import greet\n" +
                "\n" +
                "class App:\n" +
                "    def run(self):\n" +
                "        return greet()\n";
            var conv = Conv(dir, Path.Combine("pkg", "utils.py"), "pkg.utils", "Utils", "greet");
            var r = PythonOopReferenceRewriter.RewriteConsumer(
                consumer, Path.Combine(dir, "pkg", "app.py"), conv);
            Assert.IsTrue(r.Count > 0, "relative import must resolve to the converted sibling");
            StringAssert.Contains(r.Text, "from .utils import Utils");
            StringAssert.Contains(r.Text, "Utils.greet()");
        }

        [TestMethod]
        public void Rewriter_UnrelatedModule_IsUntouched()
        {
            string dir = Path.Combine(Path.GetTempPath(), "oop_rw_" + Guid.NewGuid().ToString("N"));
            string consumer =
                "from other.utils import greet\n" +
                "\n" +
                "class App:\n" +
                "    def run(self):\n" +
                "        return greet()\n";
            var conv = Conv(dir, Path.Combine("pkg", "utils.py"), "pkg.utils", "Utils", "greet");
            var r = PythonOopReferenceRewriter.RewriteConsumer(
                consumer, Path.Combine(dir, "app", "app.py"), conv);
            Assert.AreEqual(0, r.Count);
            Assert.AreEqual(consumer, r.Text);
        }

        // ---- the test-protocol carve-out (shared with the IDE's PY8002 pass) ----

        // Same carve-out as HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule applies inside a
        // test project, applied here per FILE because the Import Engine has no project model yet.
        // Before it, importing any project with a conftest.py failed outright: the converter
        // refused to touch the file (rightly — encapsulating a fixture disables it silently) and
        // the auditor then reported every fixture as a violation, which fails the whole import.

        [TestMethod]
        public void Auditor_TestProtocolFunctions_InATestModule_AreNotViolations()
        {
            string src =
                "import pytest\n" +
                "\n" +
                "def test_add():\n" +
                "    assert True\n" +
                "\n" +
                "async def test_add_async():\n" +
                "    assert True\n" +
                "\n" +
                "@pytest.fixture(scope=\"module\")\n" +
                "def db():\n" +
                "    return None\n" +
                "\n" +
                "def pytest_addoption(parser):\n" +
                "    pass\n" +
                "\n" +
                "def setup_module(module):\n" +
                "    pass\n";
            var v = PythonOopAuditor.AuditSource(src, "test_calc.py", false);
            Assert.AreEqual(0, v.Count, string.Join("; ", v.Select(x => x.Message)));
        }

        [TestMethod]
        public void Auditor_AConftestOfFixtures_PassesTheGuard()
        {
            string src =
                "import pytest\n" +
                "\n" +
                "@pytest.fixture\n" +
                "def client():\n" +
                "    return None\n" +
                "\n" +
                "def pytest_collection_modifyitems(items):\n" +
                "    pass\n";
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(src, "conftest.py", false).Count,
                "a conftest of fixtures and hooks must not fail the import");
        }

        [TestMethod]
        public void Auditor_AnOrdinaryHelper_InATestModule_IsStillAViolation()
        {
            var v = PythonOopAuditor.AuditSource(
                "def build_payload():\n    return True\n", "test_calc.py", false);
            Assert.AreEqual(1, v.Count, "the carve-out is only as wide as the runner protocol");
            Assert.AreEqual("build_payload", v[0].Name);
        }

        [TestMethod]
        public void Auditor_ATestNamedFunction_InAProductionModule_IsStillAViolation()
        {
            // Keyed on the file being test infrastructure, not on the function name — otherwise
            // any module could opt out of the strict-OOP guard by renaming its functions.
            var v = PythonOopAuditor.AuditSource(
                "def test_connection():\n    return True\n", "svc.py", false);
            Assert.AreEqual(1, v.Count);
            Assert.AreEqual(OopViolationKind.ModuleLevelFunction, v[0].Kind);
        }

        [TestMethod]
        public void Converter_InATestModule_MovesHelpersButLeavesTheProtocolAlone()
        {
            string src =
                "import pytest\n" +
                "\n" +
                "@pytest.fixture\n" +
                "def db():\n" +
                "    return None\n" +
                "\n" +
                "def build_payload():\n" +
                "    return True\n" +
                "\n" +
                "def test_add(db):\n" +
                "    assert build_payload()\n";
            var r = PythonOopConverter.ConvertModule(src, "test_calc", false);
            Assert.IsTrue(r.Changed, "the ordinary helper must still be encapsulated");
            CollectionAssert.AreEquivalent(new[] { "build_payload" }, r.MovedFunctions.ToArray());
            StringAssert.Contains(r.Text, "@pytest.fixture\ndef db():",
                "the fixture must stay at module level or pytest never applies it");
            StringAssert.Contains(r.Text, "def test_add(db):",
                "the test must stay at module level for pytest to collect it");
            // The moved helper is requalified through the generated class everywhere, including
            // inside the test that calls it.
            StringAssert.Contains(r.Text, ".build_payload()");
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(r.Text, "test_calc.py", false).Count,
                "converter output must satisfy the auditor — otherwise the import still fails");
        }

        [TestMethod]
        public void Converter_AConftestOfFixturesAndHelpers_MovesOnlyTheHelpers()
        {
            // The gap that used to make this file unimportable, end to end: the helper must move,
            // both fixtures must stay where pytest can see them, the call from inside the KEPT
            // fixture body must be requalified through the generated class, and the result must
            // satisfy the auditor — otherwise the import still fails.
            string src =
                "import pytest\n" +
                "\n" +
                "collect_ignore = [\"legacy_setup.py\"]\n" +
                "pytestmark = pytest.mark.slow\n" +
                "\n" +
                "def _load_sample_data():\n" +
                "    return [1, 2, 3]\n" +
                "\n" +
                "@pytest.fixture\n" +
                "def dataset():\n" +
                "    return _load_sample_data()\n" +
                "\n" +
                "@pytest.fixture(scope=\"session\")\n" +
                "def doubled(dataset):\n" +
                "    return [x * 2 for x in dataset]\n" +
                "\n" +
                "def pytest_addoption(parser):\n" +
                "    pass\n";
            var r = PythonOopConverter.ConvertModule(src, "conftest", false);

            Assert.IsTrue(r.Changed);
            CollectionAssert.AreEquivalent(new[] { "_load_sample_data" }, r.MovedFunctions.ToArray(),
                "only the plain helper moves: " + string.Join(", ", r.MovedFunctions));
            StringAssert.Contains(r.Text, "@pytest.fixture\ndef dataset():");
            StringAssert.Contains(r.Text, "def doubled(dataset):");
            StringAssert.Contains(r.Text, "def pytest_addoption(parser):");
            StringAssert.Contains(r.Text, "Conftest._load_sample_data()",
                "the call inside the kept fixture body must follow the helper into the class");
            Assert.IsFalse(r.Text.Contains("class TestConftest"));
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(r.Text, "conftest.py", false).Count,
                "converter output must satisfy the auditor, or the import still fails");
        }

        [TestMethod]
        public void Converter_AFixtureRequestedByParameterName_IsNeverRequalified()
        {
            // The precise hazard of converting conftest at all: pytest passes fixtures BY
            // PARAMETER NAME. If the reference rewriter touched a parameter that happens to share
            // a moved helper's name, the fixture would stop being injected — silently, with the
            // suite still green-ish. QualifyBareReferences honours parameter shadowing; this pins it.
            string src =
                "import pytest\n" +
                "\n" +
                "def payload():\n" +                       // a moved helper...
                "    return 1\n" +
                "\n" +
                "@pytest.fixture\n" +
                "def payload_fixture():\n" +
                "    return payload()\n" +
                "\n" +
                "def test_uses_it(payload):\n" +           // ...whose name is also a parameter here
                "    assert payload == 1\n";
            var r = PythonOopConverter.ConvertModule(src, "conftest", false);

            StringAssert.Contains(r.Text, "def test_uses_it(payload):",
                "the parameter declaration must not be rewritten");
            StringAssert.Contains(r.Text, "assert payload == 1",
                "a use shadowed by the enclosing parameter must not be rewritten");
            StringAssert.Contains(r.Text, "return Conftest.payload()",
                "the unshadowed call inside the fixture must be");
        }

        [TestMethod]
        public void Auditor_PytestModuleVariables_AreLegalInTestInfrastructure()
        {
            string src =
                "import pytest\n" +
                "\n" +
                "collect_ignore = [\"legacy.py\"]\n" +
                "collect_ignore_glob = [\"*_old.py\"]\n" +
                "pytest_plugins = [\"helpers\"]\n" +
                "pytestmark = pytest.mark.asyncio\n";
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(src, "conftest.py", false).Count,
                "pytest reads these off the module by name; they cannot be ALL_CAPS or class members");

            // Same names in production code are still ordinary non-constant module state.
            Assert.AreEqual(1, PythonOopAuditor.AuditSource(
                "collect_ignore = [\"legacy.py\"]\n", "svc.py", false).Count);
        }

        [TestMethod]
        public void Auditor_ImportTimePytestCalls_AreLegalInTestInfrastructure()
        {
            // register_assert_rewrite must run before the plugin it names is imported, and
            // importorskip must skip the module before collection — neither can be deferred into
            // a class, so both are protocol, not user code.
            string src =
                "import pytest\n" +
                "\n" +
                "pytest.register_assert_rewrite(\"myplugin.helpers\")\n" +
                "pytest.importorskip(\"numpy\")\n";
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(src, "conftest.py", false).Count,
                "these are import-time pytest APIs, not executable user code");

            // The bare `from pytest import register_assert_rewrite` form too.
            Assert.AreEqual(0, PythonOopAuditor.AuditSource(
                "from pytest import register_assert_rewrite\n\nregister_assert_rewrite(\"p\")\n",
                "conftest.py", false).Count);
        }

        [TestMethod]
        public void Auditor_OtherModuleLevelCallsInAConftest_AreStillViolations()
        {
            // The allow-list is named APIs, not "anything called in a conftest". sys.path.insert
            // is the exact anti-pattern PY8001 exists to kill; it must not be laundered by living
            // in a conftest.
            foreach (string call in new[]
                     {
                         "import sys\nsys.path.insert(0, \"src\")\n",
                         "import warnings\nwarnings.filterwarnings(\"ignore\")\n",
                         "import pytest\npytest.fail(\"nope\")\n",
                     })
                Assert.AreEqual(1, PythonOopAuditor.AuditSource(call, "conftest.py", false).Count, call);

            // And the exempt calls are exempt only in test infrastructure.
            Assert.AreEqual(1, PythonOopAuditor.AuditSource(
                "import pytest\npytest.register_assert_rewrite(\"p\")\n", "svc.py", false).Count);
        }

        [TestMethod]
        public void Converter_ATestModuleOfNothingButProtocol_IsLeftUnchanged()
        {
            string src =
                "import pytest\n" +
                "\n" +
                "@pytest.fixture\n" +
                "def db():\n" +
                "    return None\n" +
                "\n" +
                "def test_add(db):\n" +
                "    assert True\n";
            var r = PythonOopConverter.ConvertModule(src, "test_calc", false);
            Assert.IsFalse(r.Changed);
            Assert.IsTrue(r.Notes.Any(n => n.Contains("module-level")),
                "the reason must be stated, not silently skipped: " + string.Join("; ", r.Notes));
        }
    }
}
