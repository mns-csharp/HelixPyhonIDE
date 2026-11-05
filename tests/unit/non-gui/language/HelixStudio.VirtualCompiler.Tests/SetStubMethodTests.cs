using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 364 (click stress-test): the bundled set/frozenset stubs were incomplete, so real
    /// methods — set.update/difference/issubset/issuperset/copy and frozenset's non-mutating set
    /// algebra — false-flagged PY6001 "'set' has no attribute '...'" (click/core.py:1391,2165).
    /// </summary>
    [TestClass]
    public class SetStubMethodTests
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
        public void SetMethods_AreAllDeclared()
        {
            var r = VirtualCompiler.CompileSource(
                "def f() -> None:\n" +
                "    s: set[int] = set()\n" +
                "    s.update({1})\n" +
                "    s.difference({1})\n" +
                "    s.difference_update({1})\n" +
                "    s.intersection_update({1})\n" +
                "    s.symmetric_difference({1})\n" +
                "    s.issubset({1})\n" +
                "    s.issuperset({1})\n" +
                "    s.isdisjoint({1})\n" +
                "    s.copy()\n");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                Dump(r));
        }

        [TestMethod]
        public void FrozensetNonMutatingMethods_AreDeclared()
        {
            var r = VirtualCompiler.CompileSource(
                "def f() -> None:\n" +
                "    s: frozenset[int] = frozenset()\n" +
                "    s.union({1})\n" +
                "    s.difference({1})\n" +
                "    s.issubset({1})\n" +
                "    s.copy()\n");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                Dump(r));
        }

        /// <summary>The stub is completed, not made permissive: a non-method still reports.</summary>
        [TestMethod]
        public void AMadeUpSetMethod_StillReports()
        {
            var r = VirtualCompiler.CompileSource(
                "def f() -> None:\n" +
                "    s: set[int] = set()\n" +
                "    s.frobnicate()\n");
            Assert.IsTrue(NoAttr(r, "frobnicate"), Dump(r));
        }
    }
}
