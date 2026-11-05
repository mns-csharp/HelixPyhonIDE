using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 452 (pydantic stress-test): the class argument of <c>isinstance</c> may be a runtime
    /// variable (a <c>tuple[type[...], ...]</c>), not a static class — pydantic's
    /// <c>isinstance(alias, allowed_types)</c>. Narrowing must not fabricate a <c>ClassRef</c> named
    /// after that snake_case variable, which would poison the value's type and make the return
    /// unassignable (a false PY6005). A real class name in isinstance still narrows.
    /// </summary>
    [TestClass]
    public class IsinstanceRuntimeVarNarrowingTests
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
        public void IsinstanceAgainstSnakeCaseVariable_DoesNotPoisonTheReturnType()
        {
            // allowed_types is a tuple parameter, not a class. The `not isinstance` guard used to
            // narrow `alias` to a bogus ClassRef("allowed_types"), so the return was flagged.
            var r = VirtualCompiler.CompileSource(
                "from typing import Any\n" +
                "class AliasPath: ...\n" +
                "def gen(alias_gen: Any, allowed_types: tuple, field: str) -> str | AliasPath | None:\n" +
                "    alias = None\n" +
                "    if alias_gen is not None:\n" +
                "        alias = alias_gen(field)\n" +
                "        if alias is not None and not isinstance(alias, allowed_types):\n" +
                "            raise TypeError('bad')\n" +
                "    return alias\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void IsinstanceAgainstRealClass_StillNarrows()
        {
            // A genuine class name must still narrow: returning the isinstance-narrowed value under a
            // mismatched annotation is still a real error, proving we didn't disable narrowing wholesale.
            var r = VirtualCompiler.CompileSource(
                "def f(x: object) -> int:\n" +
                "    if isinstance(x, str):\n" +
                "        return x\n" +
                "    return 0\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
