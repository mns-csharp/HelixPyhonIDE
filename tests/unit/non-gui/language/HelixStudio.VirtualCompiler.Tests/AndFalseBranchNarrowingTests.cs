using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 412 (click stress-test): the FALSE branch of `a and b` narrows a single tested name by
    /// De Morgan — not(a and b) = (not a) or (not b) — to the union of each operand's false branch.
    /// So after a guard whose body returns, the fall-through sees the narrowed type: click
    /// exceptions.py:23 `if x is not None and not isinstance(x, str): return ...; return x` returns
    /// `str | None`, assignable to the `str | None` annotation (was PY6005).
    /// </summary>
    [TestClass]
    public class AndFalseBranchNarrowingTests
    {
        private static bool Has(CompileResult r, string code)
        {
            return r.Diagnostics.Any(d => d.Code == code);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void FallThroughAfterTerminatingAndGuard_IsNarrowed()
        {
            var r = VirtualCompiler.CompileSource(
                "from collections.abc import Sequence\n" +
                "def join(x: Sequence[str] | str | None) -> str | None:\n" +
                "    if x is not None and not isinstance(x, str):\n" +
                "        return \" / \".join(repr(e) for e in x)\n" +
                "    return x\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch), Dump(r));
        }

        [TestMethod]
        public void ElseBranchOfAndGuard_IsNarrowed()
        {
            // Same narrowing, but reached through an explicit else rather than a fall-through.
            var r = VirtualCompiler.CompileSource(
                "from collections.abc import Sequence\n" +
                "def join(x: Sequence[str] | str | None) -> str | None:\n" +
                "    if x is not None and not isinstance(x, str):\n" +
                "        return \" / \".join(repr(e) for e in x)\n" +
                "    else:\n" +
                "        return x\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch), Dump(r));
        }

        [TestMethod]
        public void GenuineReturnMismatch_IsStillFlagged()
        {
            // The narrowed fall-through type is `str | None`; returning it where `int` is annotated
            // must still be reported — the narrowing is precise, not a blanket suppression.
            var r = VirtualCompiler.CompileSource(
                "from collections.abc import Sequence\n" +
                "def join(x: Sequence[str] | str | None) -> int:\n" +
                "    if x is not None and not isinstance(x, str):\n" +
                "        return 0\n" +
                "    return x\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch), Dump(r));
        }
    }
}
