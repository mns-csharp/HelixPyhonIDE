using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 388 (rich stress-test): an early-return guard with an `or` condition narrows the
    /// fall-through. `if style is None or style._null: return self` leaves `style` non-None
    /// afterward, so `return style` against `-> Style` is valid (rich style.py:737). The narrowing
    /// engine only handled `and`; `or` is now its De Morgan dual (false branch chains the negations).
    /// </summary>
    [TestClass]
    public class OrGuardNarrowingTests
    {
        private static bool HasReturnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private const string Prelude =
            "from typing import Optional\n" +
            "class Style:\n" +
            "    _null: bool\n";

        [TestMethod]
        public void OrGuardEarlyReturn_NarrowsFallThrough()
        {
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "def add(style: Optional[Style]) -> Style:\n" +
                "    if style is None or style._null:\n" +
                "        return Style()\n" +
                "    return style\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void OrGuardWithMultipleNoneChecks_Narrows()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional\n" +
                "def f(a: Optional[int], b: Optional[int]) -> int:\n" +
                "    if a is None or b is None:\n" +
                "        return 0\n" +
                "    return a + b\n");   // both a and b non-None here
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void NoGuard_StillFlagsOptionalReturn()
        {
            // Regression guard: the `or` handling didn't blanket-suppress — an unguarded Optional
            // return is still a genuine mismatch.
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "def bad(style: Optional[Style]) -> Style:\n" +
                "    return style\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void AndGuardTrueBranch_StillNarrows()
        {
            // The pre-existing `and` true-branch narrowing must keep working.
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional\n" +
                "def f(x: Optional[int]) -> int:\n" +
                "    if x is not None and x > 0:\n" +
                "        return x\n" +
                "    return 0\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }
    }
}
