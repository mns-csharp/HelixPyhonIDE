using System;
using System.Collections.Generic;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Compliance
{
    /// <summary>
    /// Recognizes a Python test file by what it CONTAINS, for solution policy rule 3 (PY8003).
    ///
    /// The name/directory conventions in <c>HelixPythonNamespace.Core.Policy.TestFilePlacementRule</c> are
    /// the primary signal, but they are only conventions: a file called <c>verification.py</c> that
    /// imports pytest and declares a <c>TestCalculator</c> class is plainly test material, and it
    /// slipped past a name-only rule entirely. It also escaped rule 2 (PY8002), because a
    /// <c>unittest.TestCase</c> subclass is perfectly object-oriented.
    ///
    /// Worth knowing when reading this: neither runner would COLLECT such a file by default —
    /// pytest keys on <c>python_files = test_*.py *_test.py</c> and unittest discovery on
    /// <c>test*.py</c>, both file-name patterns. So this catches test code by intent, not a suite
    /// that silently runs. That is the point: a class library must not carry tests whether or not
    /// today's runner configuration happens to execute them.
    ///
    /// A file is a test file by content when ANY of these holds:
    /// <list type="number">
    /// <item>a class derives from <c>unittest.TestCase</c> (or a bare <c>TestCase</c>);</item>
    /// <item>a class named <c>Test*</c> declares at least one <c>test_*</c> method — pytest's own
    /// convention, which needs no import at all, so no import check can see it;</item>
    /// <item>the file imports pytest or unittest AND declares a <c>test_*</c> function/method or
    /// carries a <c>@pytest.fixture</c> / <c>@pytest.mark.*</c> decorator.</item>
    /// </list>
    ///
    /// Rule 3 is a HARD blocker with no Preferences toggle, so the shape of rule 2 above is a
    /// deliberate, accepted trade: a production class named e.g. <c>TestRunner</c> with a method
    /// <c>test_connection()</c> WILL be reported, and the only way out is to rename it or move it.
    /// That was chosen over leaving import-free pytest classes undetectable. Rule 3 above is the
    /// guard that keeps the merely-adjacent cases legal: importing <c>unittest.mock</c> in
    /// production code, or <c>if TYPE_CHECKING: import pytest</c> for type hints, declares no tests
    /// and stays clean.
    /// </summary>
    internal static class PythonTestContentDetector
    {
        public static bool IsTestModule(Module module)
        {
            if (module == null || module.Body == null) return false;

            bool importsFramework = false;
            bool hasTestNamedCallable = false;
            bool hasPytestDecorator = false;

            foreach (Stmt statement in module.Body)
            {
                if (ImportsTestFramework(statement)) importsFramework = true;

                ClassDef cls = statement as ClassDef;
                if (cls != null)
                {
                    if (DerivesFromTestCase(cls)) return true;                     // (1)
                    if (IsPytestStyleTestClass(cls)) return true;                  // (2)
                    if (ClassHasTestNamedMethod(cls)) hasTestNamedCallable = true;
                    if (HasPytestDecorator(cls.DecoratorList)) hasPytestDecorator = true;
                    foreach (Stmt member in Body(cls))
                        if (HasPytestDecorator(Decorators(member))) hasPytestDecorator = true;
                    continue;
                }

                string fnName = FunctionName(statement);
                if (fnName != null)
                {
                    if (IsTestName(fnName)) hasTestNamedCallable = true;
                    if (HasPytestDecorator(Decorators(statement))) hasPytestDecorator = true;
                }
            }

            return importsFramework && (hasTestNamedCallable || hasPytestDecorator);   // (3)
        }

        // ---- (1) unittest.TestCase ------------------------------------------

        private static bool DerivesFromTestCase(ClassDef cls)
        {
            if (cls.Bases == null) return false;
            foreach (Expr b in cls.Bases)
            {
                // `TestCase`, `unittest.TestCase`, `unittest.case.TestCase`, and the async variant.
                string tail = TrailingName(b);
                if (string.Equals(tail, "TestCase", StringComparison.Ordinal)
                    || string.Equals(tail, "IsolatedAsyncioTestCase", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ---- (2) pytest's import-free convention -----------------------------

        private static bool IsPytestStyleTestClass(ClassDef cls)
        {
            return cls.Name != null
                && cls.Name.StartsWith("Test", StringComparison.Ordinal)
                && ClassHasTestNamedMethod(cls);
        }

        private static bool ClassHasTestNamedMethod(ClassDef cls)
        {
            foreach (Stmt member in Body(cls))
            {
                string name = FunctionName(member);
                if (name != null && IsTestName(name)) return true;
            }
            return false;
        }

        // ---- (3) framework import + a test declaration ------------------------

        private static bool ImportsTestFramework(Stmt statement)
        {
            Import imp = statement as Import;
            if (imp != null && imp.Names != null)
            {
                foreach (Alias a in imp.Names)
                    if (IsFrameworkModule(a?.Name)) return true;
                return false;
            }

            ImportFrom from = statement as ImportFrom;
            if (from != null) return IsFrameworkModule(from.Module);

            return false;
        }

        // `pytest`, `unittest`, and any submodule of either (`unittest.mock`, `pytest.approx`).
        // A submodule import alone is NOT enough to condemn a file — it only sets the flag, and
        // rule 3 still requires a test declaration alongside it.
        private static bool IsFrameworkModule(string module)
        {
            if (string.IsNullOrEmpty(module)) return false;
            return Equals(module, "pytest") || module.StartsWith("pytest.", StringComparison.Ordinal)
                || Equals(module, "unittest") || module.StartsWith("unittest.", StringComparison.Ordinal);
        }

        private static bool Equals(string a, string b)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        // @pytest.fixture, @pytest.mark.parametrize(...), @fixture — the decorator forms that mean
        // "this module is test material" even when nothing here is named test_*.
        private static bool HasPytestDecorator(IReadOnlyList<Expr> decorators)
        {
            if (decorators == null) return false;
            foreach (Expr d in decorators)
            {
                Expr target = d;
                Call call = target as Call;
                if (call != null) target = call.Func;

                if (RootName(target) == "pytest") return true;

                string tail = TrailingName(target);
                if (string.Equals(tail, "fixture", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // ---- rule 2's test-protocol carve-out ---------------------------------

        /// <summary>
        /// True when a module-level statement is a function the test runner can only find at module
        /// level — rule 2's (PY8002) carve-out for test projects, decided by
        /// <see cref="HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule"/>.
        ///
        /// This adapter exists only to reduce this assembly's AST to the name + dotted decorators
        /// that the shared rule takes; the Import Engine's strict-OOP guard does the same from its
        /// own, different syntax tree.
        /// </summary>
        public static bool IsTestProtocolFunction(Stmt statement)
        {
            string name = FunctionName(statement);
            if (name == null) return false;
            return HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule.IsTestProtocolFunction(
                name, DottedDecorators(statement));
        }

        private static List<string> DottedDecorators(Stmt statement)
        {
            var names = new List<string>();
            IReadOnlyList<Expr> decorators = Decorators(statement);
            if (decorators == null) return names;
            foreach (Expr d in decorators)
            {
                string dotted = DottedName(d);
                if (!string.IsNullOrEmpty(dotted)) names.Add(dotted);
            }
            return names;
        }

        // `@pytest.fixture` and `@pytest.fixture(scope="module")` alike -> "pytest.fixture".
        private static string DottedName(Expr e)
        {
            Call call = e as Call;
            if (call != null) e = call.Func;

            var parts = new List<string>();
            AttributeExpr attr = e as AttributeExpr;
            while (attr != null)
            {
                parts.Insert(0, attr.Attr);
                e = attr.Value;
                attr = e as AttributeExpr;
            }
            Name n = e as Name;
            if (n == null) return null;               // a subscript/lambda decorator: not a name
            parts.Insert(0, n.Id);
            return string.Join(".", parts);
        }

        // ---- shared helpers ---------------------------------------------------

        // pytest and unittest both collect on the `test` prefix; `test_x` and `testX` alike. The
        // same predicate rule 2's carve-out uses, so a function this recognizes as a test is
        // exactly one the carve-out lets stay at module level.
        private static bool IsTestName(string name)
        {
            return HelixPythonNamespace.Core.Policy.TestProtocolFunctionRule.IsCollectedTestName(name);
        }

        private static string FunctionName(Stmt statement)
        {
            FunctionDef fn = statement as FunctionDef;
            if (fn != null) return fn.Name;
            AsyncFunctionDef afn = statement as AsyncFunctionDef;
            return afn?.Name;
        }

        private static IReadOnlyList<Expr> Decorators(Stmt statement)
        {
            FunctionDef fn = statement as FunctionDef;
            if (fn != null) return fn.DecoratorList;
            AsyncFunctionDef afn = statement as AsyncFunctionDef;
            if (afn != null) return afn.DecoratorList;
            ClassDef cls = statement as ClassDef;
            return cls?.DecoratorList;
        }

        private static IReadOnlyList<Stmt> Body(ClassDef cls)
        {
            return cls.Body ?? (IReadOnlyList<Stmt>)new Stmt[0];
        }

        // The last identifier of a dotted expression: `unittest.case.TestCase` -> "TestCase".
        private static string TrailingName(Expr e)
        {
            AttributeExpr attr = e as AttributeExpr;
            if (attr != null) return attr.Attr;
            Name n = e as Name;
            return n?.Id;
        }

        // The leftmost identifier: `pytest.mark.parametrize` -> "pytest".
        private static string RootName(Expr e)
        {
            AttributeExpr attr = e as AttributeExpr;
            while (attr != null)
            {
                e = attr.Value;
                attr = e as AttributeExpr;
            }
            Name n = e as Name;
            return n?.Id;
        }
    }
}
