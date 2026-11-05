using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 460 (fastapi stress-test): an early-return guard on an Optional ATTRIBUTE
    /// (`if self.call is None: return`) narrows that attribute to non-None for the rest of the scope,
    /// so a later use no longer sees the `| None` (fastapi dependencies/models.py: _impartial(self.call)
    /// expected `(...) -> Any`, not `(...) -> Any | None`). Without the guard the attribute stays
    /// Optional and a genuine mismatch is still flagged.
    /// </summary>
    [TestClass]
    public class AttributeEarlyReturnNarrowTests
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
        public void EarlyReturnIsNone_NarrowsAttributeForTheRestOfTheScope()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Any, Callable\n" +
                "def take(f: Callable[..., Any]) -> Any:\n" +
                "    return f\n" +
                "class Dep:\n" +
                "    call: Callable[..., Any] | None = None\n" +
                "    def use(self) -> Any:\n" +
                "        if self.call is None:\n" +
                "            return None\n" +
                "        return take(self.call)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ElseReturns_AlsoNarrowsInTheFallThrough()
        {
            // Symmetric form: `if self.call is not None: ... else: return`.
            var r = VirtualCompiler.CompileSource(
                "from typing import Any, Callable\n" +
                "def take(f: Callable[..., Any]) -> Any:\n" +
                "    return f\n" +
                "class Dep:\n" +
                "    call: Callable[..., Any] | None = None\n" +
                "    def use(self) -> Any:\n" +
                "        if self.call is not None:\n" +
                "            pass\n" +
                "        else:\n" +
                "            return None\n" +
                "        return take(self.call)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void WithoutAGuard_TheOptionalAttributeIsStillFlagged()
        {
            // No guard: self.call is genuinely Optional, so passing it where a non-Optional callable is
            // required is a real mismatch and must still flag.
            var r = VirtualCompiler.CompileSource(
                "from typing import Any, Callable\n" +
                "def take(f: Callable[..., Any]) -> Any:\n" +
                "    return f\n" +
                "class Dep:\n" +
                "    call: Callable[..., Any] | None = None\n" +
                "    def use(self) -> Any:\n" +
                "        return take(self.call)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
