using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 454 (pydantic stress-test): a parameter annotated <c>NoReturn</c>/<c>Never</c> is the
    /// assert_never() exhaustiveness idiom — the caller passes the value whose type should already be
    /// narrowed to Never. Gradual analysis under-narrows aliased / forward-ref unions, so a Never
    /// target must not raise PY6003 (pydantic v1/utils.py:609). A real, checkable parameter type
    /// still flags a genuine mismatch.
    /// </summary>
    [TestClass]
    public class AssertNeverArgTests
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
        public void ArgumentAgainstNoReturnParam_IsNotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import NoReturn, Union\n" +
                "def assert_never(obj: NoReturn, msg: str) -> NoReturn:\n" +
                "    raise AssertionError(msg)\n" +
                "def handle(items: Union[set, dict]) -> None:\n" +
                "    if isinstance(items, dict):\n" +
                "        pass\n" +
                "    elif isinstance(items, set):\n" +
                "        pass\n" +
                "    else:\n" +
                "        assert_never(items, 'unexpected')\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ArgumentAgainstRealParam_StillFlagged()
        {
            // A checkable parameter type must still catch a genuine mismatch, proving we only relaxed
            // the Never case and not argument checking wholesale.
            var r = VirtualCompiler.CompileSource(
                "def take_int(n: int) -> None:\n" +
                "    pass\n" +
                "def f() -> None:\n" +
                "    take_int('not an int')\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
