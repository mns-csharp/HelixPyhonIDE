using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 421 (rich stress-test): a bare class name passed as an ARGUMENT is a class object, so it
    /// satisfies a `type` / `type[X]` parameter (and a union containing one). `_safe_isinstance(obj,
    /// Node)` with param `type | Tuple[type, ...]` false-flagged PY6003 (rich pretty.py:906). Passing
    /// an INSTANCE, or an unrelated class where `type[Base]` is required, is still reported.
    /// </summary>
    [TestClass]
    public class ClassObjectArgumentTests
    {
        private static bool HasArgMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void ClassObject_SatisfiesTypeUnionParam()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Union, Tuple\n" +
                "class Node: ...\n" +
                "def safe_isinstance(obj: object, class_or_tuple: Union[type, Tuple[type, ...]]) -> bool:\n" +
                "    return True\n" +
                "def f(x: object) -> None:\n" +
                "    safe_isinstance(x, Node)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ClassObject_SatisfiesTypeSubscriptParam_Keyword()
        {
            var r = VirtualCompiler.CompileSource(
                "class Base: ...\n" +
                "class Derived(Base): ...\n" +
                "def register(*, cls: type[Base]) -> None: ...\n" +
                "def f() -> None:\n" +
                "    register(cls=Derived)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void Instance_WhereTypeExpected_StillFlagged()
        {
            // A concrete instance is not a class object, so a `type`-typed param rejects it.
            var r = VirtualCompiler.CompileSource(
                "def want_type(t: type) -> None: ...\n" +
                "def f() -> None:\n" +
                "    want_type('a string')\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnrelatedClass_WhereTypeSubscriptExpected_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Base: ...\n" +
                "class Unrelated: ...\n" +
                "def register(cls: type[Base]) -> None: ...\n" +
                "def f() -> None:\n" +
                "    register(Unrelated)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
