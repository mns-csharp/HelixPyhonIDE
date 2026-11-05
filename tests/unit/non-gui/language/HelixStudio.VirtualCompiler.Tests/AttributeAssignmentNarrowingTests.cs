using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 389 (rich stress-test): after `self._hash = hash(...)` a read of `self._hash` sees the
    /// assigned type, not the declared union (int | None). rich style.py __hash__:
    /// `if self._hash is not None: return self._hash; self._hash = hash(...); return self._hash` —
    /// the final return false-flagged PY6005 because assignment did not narrow the attribute path.
    /// </summary>
    [TestClass]
    public class AttributeAssignmentNarrowingTests
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
            "class C:\n" +
            "    _hash: Optional[int]\n" +
            "    def __init__(self) -> None:\n" +
            "        self._hash: Optional[int] = None\n";

        [TestMethod]
        public void ConcreteAssignment_NarrowsAttributeRead()
        {
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "    def h(self) -> int:\n" +
                "        self._hash = 12345\n" +
                "        return self._hash\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void GuardThenAssignment_BothPaths_AreNarrowed()
        {
            // The exact style.py __hash__ shape.
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "    def h(self) -> int:\n" +
                "        if self._hash is not None:\n" +
                "            return self._hash\n" +
                "        self._hash = hash('x')\n" +
                "        return self._hash\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnguardedOptionalAttributeReturn_StillFlags()
        {
            // Regression guard: without an assignment or guard, the declared Optional is still checked.
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "    def bad(self) -> int:\n" +
                "        return self._hash\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
