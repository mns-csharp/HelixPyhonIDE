using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 372 (rich stress-test): instance attributes assigned via TUPLE/LIST unpacking —
    /// `self.top_left, self.top, self.top_right = iter(line)` (rich's Box sets all its box-drawing
    /// glyphs this way) — were not recognised, so every later `self.top_right` false-flagged PY6001
    /// "'Box' has no attribute ...". The self-attribute scanner now walks unpacking and starred targets.
    /// </summary>
    [TestClass]
    public class UnpackedSelfAttributeTests
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
        public void TupleUnpackedSelfAttrs_AreRecognised()
        {
            var r = VirtualCompiler.CompileSource(
                "class Box:\n" +
                "    def __init__(self, line: str) -> None:\n" +
                "        self.top_left, self.top, self.top_right = iter(line)\n" +
                "    def render(self) -> str:\n" +
                "        return self.top_left + self.top + self.top_right\n");
            Assert.IsFalse(NoAttr(r, "top_left"), Dump(r));
            Assert.IsFalse(NoAttr(r, "top"), Dump(r));
            Assert.IsFalse(NoAttr(r, "top_right"), Dump(r));
        }

        [TestMethod]
        public void StarredUnpackedSelfAttr_IsRecognised()
        {
            var r = VirtualCompiler.CompileSource(
                "class C:\n" +
                "    def __init__(self) -> None:\n" +
                "        self.head, *self.rest = [1, 2, 3]\n" +
                "    def go(self) -> None:\n" +
                "        print(self.head, self.rest)\n");
            Assert.IsFalse(NoAttr(r, "head"), Dump(r));
            Assert.IsFalse(NoAttr(r, "rest"), Dump(r));
        }

        /// <summary>The scan is not made permissive: a truly-unset attribute still reports.</summary>
        [TestMethod]
        public void AnUnsetAttribute_StillReports()
        {
            var r = VirtualCompiler.CompileSource(
                "class Box:\n" +
                "    def __init__(self, line: str) -> None:\n" +
                "        self.top_left, self.top = iter(line)\n" +
                "    def go(self) -> str:\n" +
                "        return self.nonexistent\n");
            Assert.IsTrue(NoAttr(r, "nonexistent"), Dump(r));
        }
    }
}
