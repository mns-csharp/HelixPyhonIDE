using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.Tests
{
    /// <summary>FR-KB-03: the parameter-info highlight span for the current argument.</summary>
    [TestClass]
    public class CallTipHighlighterTests
    {
        private const string Sig = "cudaError_t cudaMemcpy(void* dst, const void* src, size_t count, cudaMemcpyKind kind)";

        private static string Param(string sig, int argIndex)
        {
            Assert.IsTrue(CallTipHighlighter.TryGetParamSpan(sig, argIndex, out int s, out int e), "span found");
            return sig.Substring(s, e - s);
        }

        [TestMethod]
        public void FirstArgument_IsHighlighted()
        {
            Assert.AreEqual("void* dst", Param(Sig, 0));
        }

        [TestMethod]
        public void SecondArgument_IsHighlighted()
        {
            Assert.AreEqual("const void* src", Param(Sig, 1));
        }

        [TestMethod]
        public void LastArgument_IsHighlighted()
        {
            Assert.AreEqual("cudaMemcpyKind kind", Param(Sig, 3));
        }

        [TestMethod]
        public void CommaInsideTemplate_DoesNotSplitParam()
        {
            // A comma inside std::map<int,int> must not create an extra parameter.
            string sig = "void f(std::map<int, int> m, int n)";
            Assert.AreEqual("std::map<int, int> m", Param(sig, 0));
            Assert.AreEqual("int n", Param(sig, 1));
        }

        [TestMethod]
        public void OutOfRange_ReturnsFalse()
        {
            Assert.IsFalse(CallTipHighlighter.TryGetParamSpan(Sig, 9, out _, out _));
        }

        [TestMethod]
        public void NoParens_ReturnsFalse()
        {
            Assert.IsFalse(CallTipHighlighter.TryGetParamSpan("not a signature", 0, out _, out _));
        }
    }
}
