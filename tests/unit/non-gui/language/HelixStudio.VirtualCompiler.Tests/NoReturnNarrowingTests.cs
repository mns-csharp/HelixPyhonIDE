using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 403 (click stress-test): a call to a function/method annotated `-> NoReturn` / `-> Never`
    /// never returns, so a branch ending in it terminates. `if x is None: self.fail(...)` therefore
    /// narrows x to non-None in the fall-through (click ParamType.convert). Recognized even when the
    /// NoReturn method is inherited through a subscripted generic base (ParamType[_T]).
    /// </summary>
    [TestClass]
    public class NoReturnNarrowingTests
    {
        private static bool HasReturnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void NoReturnCall_InGuard_NarrowsFallThrough()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional, NoReturn\n" +
                "class C:\n" +
                "    def fail(self, m: str) -> NoReturn:\n" +
                "        raise ValueError(m)\n" +
                "    def conv(self, x: Optional[bool]) -> bool:\n" +
                "        if x is None:\n" +
                "            self.fail('bad')\n" +
                "        return x\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void InheritedNoReturn_ThroughGenericBase_StillNarrows()
        {
            // The click shape: fail() lives on the base ParamType[_T]; convert() is on a subclass.
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional, NoReturn, Generic, TypeVar\n" +
                "_T = TypeVar('_T')\n" +
                "class ParamType(Generic[_T]):\n" +
                "    def fail(self, m: str) -> NoReturn:\n" +
                "        raise ValueError(m)\n" +
                "class BoolType(ParamType[bool]):\n" +
                "    def convert(self, x: Optional[bool]) -> bool:\n" +
                "        if x is None:\n" +
                "            self.fail('bad')\n" +
                "        return x\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void OrdinaryCall_InGuard_DoesNotNarrow()
        {
            // A guard whose body is a NORMAL (returning) call does not terminate, so the Optional
            // survives and the return is still flagged.
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional\n" +
                "class C:\n" +
                "    def log(self, m: str) -> None: ...\n" +
                "    def conv(self, x: Optional[bool]) -> bool:\n" +
                "        if x is None:\n" +
                "            self.log('bad')\n" +
                "        return x\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
