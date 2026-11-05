using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 418 (click stress-test): a user-defined type predicate — a function returning
    /// TypeGuard[T] (PEP 647) or TypeIs[T] (PEP 742) — narrows its argument to T on the true branch;
    /// TypeIs additionally narrows to (type - T) on the false branch. So after
    /// `if _is_file_like(value): return`, value drops the IO member and the fall-through call type-checks
    /// (click types.py:983). TypeGuard does NOT narrow the negative branch.
    /// </summary>
    [TestClass]
    public class TypePredicateNarrowingTests
    {
        private static bool HasArgMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch);
        }

        private static bool HasReturnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void TypeIs_NarrowsFalseBranch_AfterTerminatingGuard()
        {
            // Mirrors click types.py: IO is removed on the fall-through, so the str|PathLike-only
            // helper accepts `value`.
            var r = VirtualCompiler.CompileSource(
                "from typing import Any\n" +
                "from typing_extensions import TypeIs\n" +
                "class PathLike: ...\n" +
                "class IO: ...\n" +
                "def _is_io(v: Any) -> TypeIs[IO]:\n" +
                "    return hasattr(v, 'read')\n" +
                "def sink(v: str | PathLike) -> None: ...\n" +
                "def convert(value: str | PathLike | IO) -> None:\n" +
                "    if _is_io(value):\n" +
                "        return\n" +
                "    sink(value)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void TypeIs_NarrowsTrueBranch_ToGuardedType()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Any\n" +
                "from typing_extensions import TypeIs\n" +
                "class IO:\n" +
                "    def read(self) -> str: ...\n" +
                "def _is_io(v: Any) -> TypeIs[IO]:\n" +
                "    return hasattr(v, 'read')\n" +
                "def want_io(v: IO) -> str:\n" +
                "    return v.read()\n" +
                "def convert(value: str | IO) -> None:\n" +
                "    if _is_io(value):\n" +
                "        want_io(value)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void TypeGuard_DoesNotNarrowNegativeBranch()
        {
            // TypeGuard narrows only the positive branch, so on the fall-through `value` is still the
            // full union and the str|PathLike-only sink is a genuine mismatch that must be reported.
            var r = VirtualCompiler.CompileSource(
                "from typing import Any, TypeGuard\n" +
                "class PathLike: ...\n" +
                "class IO: ...\n" +
                "def _is_io(v: Any) -> TypeGuard[IO]:\n" +
                "    return hasattr(v, 'read')\n" +
                "def sink(v: str | PathLike) -> None: ...\n" +
                "def convert(value: str | PathLike | IO) -> None:\n" +
                "    if _is_io(value):\n" +
                "        return\n" +
                "    sink(value)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
