using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 425 (black stress-test): in `X if cond else Y`, the X branch sees cond's true-narrowing and
    /// the Y branch the false-narrowing, so a call nested in a branch checks its arguments against the
    /// narrowed type. `f(value) if value is not None else None` passes `str`, not `str | None` (black
    /// __init__.py:267). A genuinely bad call in a branch is still reported.
    /// </summary>
    [TestClass]
    public class TernaryNarrowingTests
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
        public void TrueBranch_SeesNonNoneNarrowing()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional\n" +
                "def needs_str(s: str) -> str:\n" +
                "    return s\n" +
                "def f(value: Optional[str]) -> Optional[str]:\n" +
                "    return needs_str(value) if value is not None else None\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void FalseBranch_SeesNarrowing()
        {
            // `needs_str(value)` sits in the ELSE branch, reached when `value is None` is false,
            // i.e. value is str there.
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional\n" +
                "def needs_str(s: str) -> str:\n" +
                "    return s\n" +
                "def f(value: Optional[str]) -> str:\n" +
                "    return 'none' if value is None else needs_str(value)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void GenuineMismatchInBranch_StillFlagged()
        {
            // No narrowing applies to `n`, so passing the int where str is required is a real error.
            var r = VirtualCompiler.CompileSource(
                "def needs_str(s: str) -> str:\n" +
                "    return s\n" +
                "def f(n: int, flag: bool) -> str:\n" +
                "    return needs_str(n) if flag else 'x'\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
