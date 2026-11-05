using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 373 (rich stress-test): builtin stub gaps produced PY6001 "has no attribute" on real
    /// members — __new__ (a universal object member, so every class has it), SyntaxError's msg/offset/
    /// lineno/text/filename, and slice.indices. The stubs were completed.
    /// </summary>
    [TestClass]
    public class BuiltinStubGapTests
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
        public void DunderNew_IsAUniversalObjectMember()
        {
            var r = VirtualCompiler.CompileSource(
                "class Style:\n" +
                "    @classmethod\n" +
                "    def make(cls) -> 'Style':\n" +
                "        return cls.__new__(cls)\n");
            Assert.IsFalse(NoAttr(r, "__new__"), Dump(r));
        }

        [TestMethod]
        public void SyntaxError_Attributes_AreDeclared()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(exc: SyntaxError) -> None:\n" +
                "    print(exc.msg, exc.filename, exc.lineno, exc.offset, exc.text)\n");
            Assert.IsFalse(NoAttr(r, "msg"), Dump(r));
            Assert.IsFalse(NoAttr(r, "filename"), Dump(r));
            Assert.IsFalse(NoAttr(r, "lineno"), Dump(r));
            Assert.IsFalse(NoAttr(r, "offset"), Dump(r));
            Assert.IsFalse(NoAttr(r, "text"), Dump(r));
        }

        [TestMethod]
        public void Slice_Indices_IsDeclared()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(s: slice) -> None:\n" +
                "    s.indices(10)\n");
            Assert.IsFalse(NoAttr(r, "indices"), Dump(r));
        }

        /// <summary>Completing object does not make attribute checks permissive on unrelated members.</summary>
        [TestMethod]
        public void AMadeUpObjectMember_StillReports()
        {
            var r = VirtualCompiler.CompileSource(
                "class C: ...\n" +
                "def f(c: C) -> None:\n" +
                "    c.__totally_not_real__()\n");
            Assert.IsTrue(NoAttr(r, "__totally_not_real__"), Dump(r));
        }
    }
}
