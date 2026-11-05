using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 405 (black/click stress-test): `Callable[[P], R]` / `Callable[..., R]` annotations now
    /// convert to a real CallableRef (were collapsed to GenericRef(Callable, [Unknown, R])), and the
    /// callable subtype rule accepts a function invocable with the target's arguments. So a concrete
    /// predicate satisfies a `Callable[..., bool] | None` param (black lines.py), and an all-optional
    /// function satisfies a zero-arg Callable (click _compat.py); a function needing MORE args does not.
    /// </summary>
    [TestClass]
    public class CallableAnnotationTests
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
        public void ConcreteFunction_SatisfiesOptionalCallableParam()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Callable, Optional\n" +
                "class Leaf: ...\n" +
                "def use(cb: Optional[Callable[[Leaf], bool]]) -> None: ...\n" +
                "def pred(leaf: Leaf) -> bool:\n" +
                "    return True\n" +
                "def f() -> None:\n" +
                "    use(pred)\n" +
                "    use(None)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ConcreteFunction_SatisfiesEllipsisCallable()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Callable\n" +
                "class Leaf: ...\n" +
                "def use(cb: Callable[..., bool]) -> None: ...\n" +
                "def pred(leaf: Leaf) -> bool:\n" +
                "    return True\n" +
                "def f() -> None:\n" +
                "    use(pred)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void AllOptionalParams_SatisfyZeroArgCallable()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Callable\n" +
                "def run(factory: Callable[[], int]) -> None: ...\n" +
                "def make(a: int = 0, b: int = 0) -> int:\n" +
                "    return a + b\n" +
                "def f() -> None:\n" +
                "    run(make)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void FunctionNeedingMoreArgs_DoesNotSatisfy()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Callable\n" +
                "def run(cb: Callable[[int], int]) -> None: ...\n" +
                "def needs_two(a: int, b: int) -> int:\n" +
                "    return a + b\n" +
                "def f() -> None:\n" +
                "    run(needs_two)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
