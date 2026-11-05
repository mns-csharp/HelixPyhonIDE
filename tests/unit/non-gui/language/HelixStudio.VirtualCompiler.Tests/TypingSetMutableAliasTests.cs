using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 386: `typing.Set` is a deprecated alias for the MUTABLE builtin `set`, so
    /// `x: Set[int]` must support add/remove/discard (rich pretty.py/protocol.py/traceback.py:
    /// `visited_ids: Set[int] = set(); visited_ids.add(...)`). Iter 375 had removed the `Set = set`
    /// alias to make the ABC resolvable as a base, which regressed Set[X] to the read-only
    /// collections.abc set ABC. The ABC now keeps its real name (AbstractSet); `Set` aliases `set`.
    /// </summary>
    [TestClass]
    public class TypingSetMutableAliasTests
    {
        private static bool NoAttr(CompileResult r, string member)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound
                                          && d.Message.Contains("'" + member + "'"));
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void TypingSetAnnotation_SupportsMutatingMethods()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Set\n" +
                "def f() -> None:\n" +
                "    seen: Set[int] = set()\n" +
                "    seen.add(1)\n" +
                "    seen.remove(1)\n" +
                "    seen.discard(2)\n" +
                "    seen.clear()\n");
            Assert.IsFalse(NoAttr(r, "add"), Dump(r));
            Assert.IsFalse(NoAttr(r, "remove"), Dump(r));
            Assert.IsFalse(NoAttr(r, "discard"), Dump(r));
        }

        [TestMethod]
        public void TypingMutableSetAnnotation_SupportsAdd()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import MutableSet\n" +
                "def f(s: MutableSet[int]) -> None:\n" +
                "    s.add(1)\n" +
                "    s.discard(1)\n");
            Assert.IsFalse(NoAttr(r, "add"), Dump(r));
        }

        [TestMethod]
        public void BoundMethodOfSetAdd_IsAssignableTarget()
        {
            // The exact rich shape: bind the .add method to a local.
            var r = VirtualCompiler.CompileSource(
                "from typing import Set\n" +
                "def f() -> None:\n" +
                "    visited: Set[int] = set()\n" +
                "    push = visited.add\n" +
                "    pop = visited.remove\n");
            Assert.IsFalse(NoAttr(r, "add"), Dump(r));
            Assert.IsFalse(NoAttr(r, "remove"), Dump(r));
        }

        [TestMethod]
        public void MadeUpMethodOnBuiltinSet_StillReports()
        {
            // Regression guard: the mutable-alias restore didn't turn the builtin `set`'s
            // attribute-checking off. (typing-alias annotations like `Set[int]` are intentionally
            // lenient about unknown members — same as `List[int]` — so this uses the builtin `set`.)
            var r = VirtualCompiler.CompileSource(
                "def f() -> None:\n" +
                "    seen: set[int] = set()\n" +
                "    seen.frobnicate()\n");
            Assert.IsTrue(NoAttr(r, "frobnicate"), Dump(r));
        }
    }
}
