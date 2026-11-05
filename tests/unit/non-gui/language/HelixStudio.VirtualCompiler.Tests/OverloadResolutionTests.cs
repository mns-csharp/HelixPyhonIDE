using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 416 (click stress-test): an @overload-decorated def is a type-only signature; the real
    /// body is the trailing same-named implementation. Argument checking must use the implementation's
    /// (permissive) signature, not the first overload's narrow Literal params — otherwise
    /// `lookup_default(name, call=False)` false-flags against the `call: Literal[True]` overload
    /// (click core.py:2461). A genuine bad call to the implementation is still reported.
    /// </summary>
    [TestClass]
    public class OverloadResolutionTests
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
        public void OverloadedMethod_ChecksAgainstImplementation_NotFirstOverload()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal, overload, Any\n" +
                "class Ctx:\n" +
                "    @overload\n" +
                "    def lookup(self, name: str, call: Literal[True] = True) -> Any: ...\n" +
                "    @overload\n" +
                "    def lookup(self, name: str, call: Literal[False] = ...) -> Any: ...\n" +
                "    def lookup(self, name: str, call: bool = True) -> Any:\n" +
                "        return None\n" +
                "def use(ctx: Ctx) -> None:\n" +
                "    ctx.lookup('x', call=False)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void OverloadedMethod_PositionalCall_Accepted()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal, overload, Any\n" +
                "class Ctx:\n" +
                "    @overload\n" +
                "    def lookup(self, name: str, call: Literal[True] = True) -> Any: ...\n" +
                "    @overload\n" +
                "    def lookup(self, name: str, call: Literal[False] = ...) -> Any: ...\n" +
                "    def lookup(self, name: str, call: bool = True) -> Any:\n" +
                "        return None\n" +
                "def use(ctx: Ctx) -> None:\n" +
                "    ctx.lookup('x', False)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void OverloadedMethod_GenuinelyBadArg_StillFlagged()
        {
            // The implementation's first param is `str`; passing an int is a real error.
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal, overload, Any\n" +
                "class Ctx:\n" +
                "    @overload\n" +
                "    def lookup(self, name: str, call: Literal[True] = True) -> Any: ...\n" +
                "    def lookup(self, name: str, call: bool = True) -> Any:\n" +
                "        return None\n" +
                "def use(ctx: Ctx) -> None:\n" +
                "    ctx.lookup(123)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
