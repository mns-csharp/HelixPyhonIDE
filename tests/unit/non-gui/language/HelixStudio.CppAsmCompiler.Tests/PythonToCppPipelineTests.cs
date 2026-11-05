using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.PythonAsmCompiler;

namespace HelixPythonNamespace.CppAsmCompiler.Tests
{
    /// <summary>
    /// The Python -> ASM -> C++ pipeline: the Python translator emits the assembly, and
    /// <see cref="NasmToCpp"/> reads it back as C++. Both emitters write the same frame layout and
    /// the same '; helix.' symbol map, which is what makes the hand-off possible at all.
    ///
    /// Integer slots map to <c>long long</c>, not <c>int</c>: the Python side computes in 64-bit
    /// RAX, and as <c>int</c> a Python int larger than 2^31 would silently truncate.
    /// </summary>
    [TestClass]
    public class PythonToCppPipelineTests
    {
        private static string ToCpp(string python)
        {
            HelixPythonNamespace.PythonAsmCompiler.TranslationResult asm = PythonToNasm.Translate(python, "probe.py");
            Assert.IsTrue(asm.Succeeded, "the Python translator refused: " + asm.RefusalReason);
            DecompileResult cpp = NasmToCpp.Decompile(asm.Assembly);
            Assert.IsTrue(cpp.Succeeded,
                "NasmToCpp could not read Python-emitted assembly: " + cpp.RefusalReason
                + (cpp.Line > 0 ? " (asm line " + cpp.Line + ")" : ""));
            return cpp.Source;
        }

        [TestMethod]
        public void ArithmeticFunction()
        {
            string cpp = ToCpp("def f(a: int, b: int) -> int:\n    return a * b + a\n");
            StringAssert.Contains(cpp, "long long f(long long a, long long b)");
        }

        [TestMethod]
        public void FunctionWithALoop()
        {
            string cpp = ToCpp("def total(n: int) -> int:\n    t = 0\n    i = 0\n    while i < n:\n        t = t + i\n        i = i + 1\n    return t\n");
            StringAssert.Contains(cpp, "while");
        }
    }
}
