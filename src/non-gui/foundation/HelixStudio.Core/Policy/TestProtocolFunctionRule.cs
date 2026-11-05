using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.Core.Policy
{
    /// <summary>
    /// The carve-out in solution policy rule 2 (<c>PY8002</c>) for <b>test-protocol functions</b>:
    /// the module-level functions pytest and unittest can only discover at module level.
    ///
    /// Rule 2 requires user-written code to live in classes, because the structural views the IDE
    /// is built around (type/member trees, hierarchy, designers) have nothing to show for a bare
    /// function. Rule 3 (<see cref="TestFilePlacementRule"/>) separately requires every test file to
    /// live in a <see cref="Model.Enums.ProjectKind.TestProject"/>. Together, and without this
    /// carve-out, the two rules contradict each other: rule 3 marches the user's tests into a
    /// dedicated project, and rule 2 then forbids the only shape in which pytest can see them.
    ///
    /// This is the same kind of carve-out — and exists for exactly the same reason — as rule 2's
    /// existing allowance for <c>def main()</c>. That one is not a style concession either: PSM-06…24
    /// define an executable package's entry point as a top-level <c>def main()</c>, and Python has no
    /// way to express a process entry point inside a class. Here the constraint comes from the test
    /// runners rather than from the language, but it binds just as hard:
    /// <list type="bullet">
    /// <item>pytest collects test functions by NAME at module scope (<c>python_functions = test*</c>)
    /// and unittest's loader does the same;</item>
    /// <item>a <c>@pytest.fixture</c> is only visible to other modules when it is declared at the top
    /// level of <c>conftest.py</c> — moved into a class it is silently never applied, which is worse
    /// than a compile error because the suite still runs;</item>
    /// <item><c>pytest_*</c> hooks and the xunit-style <c>setup_module</c>/<c>teardown_module</c>
    /// family are looked up as module attributes by the runner, never as class members.</item>
    /// </list>
    ///
    /// Deliberately NOT exempt: an ordinary helper in a test file. <c>def build_payload()</c> next to
    /// the tests is code the runner never looks for by name, so it can be a class like any other, and
    /// rule 2 still applies to it. The carve-out is exactly as wide as the runner protocol, no wider.
    ///
    /// This class is the ONE authority for the predicate, and lives in the foundation assembly so
    /// both enforcement points can share it rather than drifting apart:
    /// <list type="bullet">
    /// <item>the Python compliance pass (<c>ComplianceChecker</c>), which reports PY8002 and blocks
    /// Compile/Run/Debug — applied only inside a test project, so production code cannot smuggle a
    /// procedural function past the rule by naming it <c>test_something</c>;</item>
    /// <item>the Import Engine's strict-OOP guard (<c>PythonOopAuditor</c> /
    /// <c>PythonOopConverter</c>), which would otherwise rewrite an imported suite into classes and
    /// fail the import over the fixtures it cannot rewrite.</item>
    /// </list>
    ///
    /// It is name-and-decorator based rather than AST based on purpose: the two callers parse Python
    /// with two different syntax trees (<c>VirtualCompiler.Ast</c> and <c>Python.Syntax</c>), and the
    /// foundation assembly has no project references to either. Each caller reduces its own nodes to
    /// a function name plus dotted decorator names and asks here.
    /// </summary>
    public static class TestProtocolFunctionRule
    {
        // unittest's module-level protocol hooks, and pytest's xunit-style module/function fixtures.
        // Every one of these is fetched by the runner as an attribute of the MODULE; a class member
        // of the same name is simply never found.
        private static readonly string[] ModuleHookNames =
        {
            "setup_module", "teardown_module",
            "setup_function", "teardown_function",
            "setUpModule", "tearDownModule",
            "load_tests",
        };

        /// <summary>
        /// True when a module-level function is part of the test-runner protocol and must therefore
        /// stay at module level.
        /// </summary>
        /// <param name="functionName">The function's own name.</param>
        /// <param name="decorators">
        /// Its decorators as dotted names, with any call syntax already stripped:
        /// <c>@pytest.fixture</c> and <c>@pytest.fixture(scope="module")</c> both arrive as
        /// <c>"pytest.fixture"</c>. Null or empty is fine.
        /// </param>
        public static bool IsTestProtocolFunction(string functionName, IEnumerable<string> decorators)
        {
            return IsCollectedTestName(functionName)
                || IsRunnerHookName(functionName)
                || HasTestProtocolDecorator(decorators);
        }

        /// <summary>
        /// True for a name the runners collect as a test. Both pytest (<c>python_functions = test*</c>)
        /// and unittest (<c>testMethodPrefix = "test"</c>) key on the bare <c>test</c> prefix, so this
        /// is deliberately as loose as they are — <c>test_add</c>, <c>testAdd</c> and even
        /// <c>testing_helper</c> are all collected, and a rule stricter than the runner would reject
        /// functions that really do run as tests.
        /// </summary>
        public static bool IsCollectedTestName(string functionName)
        {
            return !string.IsNullOrEmpty(functionName)
                && functionName.StartsWith("test", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(functionName, "test", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True for a hook the runner looks up on the module: any <c>pytest_*</c> plugin hook (the
        /// whole conftest.py extension surface — <c>pytest_addoption</c>,
        /// <c>pytest_collection_modifyitems</c>, <c>pytest_runtest_setup</c>, …) or one of the
        /// fixed xunit-style names.
        /// </summary>
        public static bool IsRunnerHookName(string functionName)
        {
            if (string.IsNullOrEmpty(functionName)) return false;
            if (functionName.StartsWith("pytest_", StringComparison.Ordinal)) return true;
            foreach (string hook in ModuleHookNames)
                if (string.Equals(functionName, hook, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// True when a decorator marks the function as test infrastructure: anything rooted at
        /// <c>pytest</c> (<c>@pytest.fixture</c>, <c>@pytest.mark.parametrize</c>), or a bare
        /// <c>@fixture</c> from <c>from pytest import fixture</c>.
        /// </summary>
        public static bool HasTestProtocolDecorator(IEnumerable<string> decorators)
        {
            if (decorators == null) return false;
            foreach (string d in decorators)
            {
                if (string.IsNullOrEmpty(d)) continue;
                if (string.Equals(d, "pytest", StringComparison.Ordinal)
                    || d.StartsWith("pytest.", StringComparison.Ordinal)) return true;

                int dot = d.LastIndexOf('.');
                string tail = dot >= 0 ? d.Substring(dot + 1) : d;
                if (string.Equals(tail, "fixture", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // pytest's module-level protocol VARIABLES. Like the hooks these are read off the module
        // by name, so they cannot move into a class either — and unlike the hooks they are
        // assignments, which the strict-OOP auditor judges by a separate rule (dunder or ALL_CAPS).
        // `pytestmark = pytest.mark.asyncio` and a conftest's `collect_ignore` are ordinary,
        // idiomatic lines that would otherwise fail an import.
        private static readonly string[] ModuleVariableNames =
        {
            "collect_ignore", "collect_ignore_glob", "pytest_plugins", "pytestmark",
        };

        /// <summary>
        /// True for a module-level assignment target the test runner reads off the module by name,
        /// and which therefore may stay module-level in test infrastructure despite being neither a
        /// dunder nor an ALL_CAPS constant.
        /// </summary>
        public static bool IsRunnerModuleVariable(string targetName)
        {
            if (string.IsNullOrEmpty(targetName)) return false;
            foreach (string v in ModuleVariableNames)
                if (string.Equals(targetName, v, StringComparison.Ordinal)) return true;
            return false;
        }

        // The pytest APIs whose documented contract is "call this at module level". They are bare
        // calls, so the auditor would otherwise class them as executable module-level code — which
        // is normally exactly right, and is why this is a short allow-list of named APIs rather
        // than a blanket "any pytest.* call".
        //
        //   register_assert_rewrite  must run in conftest BEFORE the named plugin is imported,
        //                            so it cannot be deferred into a class or a fixture;
        //   importorskip             skips the whole module when a dependency is absent, which
        //                            only works if it executes at import time.
        private static readonly string[] ModuleCallNames =
        {
            "register_assert_rewrite", "importorskip",
        };

        /// <summary>
        /// True for a module-level call the test runner requires at import time.
        /// <paramref name="dottedCallee"/> is the callee as a dotted name with the call stripped:
        /// <c>pytest.register_assert_rewrite("x")</c> arrives as <c>"pytest.register_assert_rewrite"</c>.
        /// Both the qualified and the <c>from pytest import ...</c> bare form are accepted.
        /// </summary>
        public static bool IsRunnerModuleCall(string dottedCallee)
        {
            if (string.IsNullOrEmpty(dottedCallee)) return false;
            int dot = dottedCallee.LastIndexOf('.');
            string root = dot >= 0 ? dottedCallee.Substring(0, dot) : null;
            string tail = dot >= 0 ? dottedCallee.Substring(dot + 1) : dottedCallee;

            // Qualified form must be rooted at pytest; a bare form is accepted on the name alone,
            // since `from pytest import importorskip` leaves no root to check.
            if (root != null && !string.Equals(root, "pytest", StringComparison.Ordinal)) return false;

            foreach (string c in ModuleCallNames)
                if (string.Equals(tail, c, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>
        /// True when a file is test infrastructure by NAME — a module the runners load looking for
        /// the protocol above (<c>test_*.py</c>, <c>*_test.py</c>, <c>conftest.py</c>).
        ///
        /// Used by the Import Engine, which has no project model to consult and must decide per
        /// file. The compliance pass does NOT use this: inside the IDE the far stronger signal is
        /// available — whether the owning project is a test project — and gating on the file name
        /// there would let any production file opt out of rule 2 by renaming itself.
        /// </summary>
        public static bool IsTestInfrastructureModuleName(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return false;
            return string.Equals(moduleName, "conftest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(moduleName, "test", StringComparison.OrdinalIgnoreCase)
                || moduleName.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
                || moduleName.EndsWith("_test", StringComparison.OrdinalIgnoreCase);
        }
    }
}
