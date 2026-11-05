using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Metrics;

namespace HelixPythonNamespace.Tests
{
    /// <summary>FR-METR-02..07: native LOC + per-function complexity/nesting metrics for the native (CUDA/C++) sources this product analyses.</summary>
    [TestClass]
    public class LanguageMetricsEngineTests
    {
        private static MetricsReport Run(string code, string file, LanguageProfile p)
        {
            return LanguageMetricsEngine.Analyze(new MetricsSource(file, code), p);
        }

        [TestMethod]
        public void Cuda_KernelDecisions_NoWhileFunctions()
        {
            string code =
                "__global__ void addKernel(int* c, const int* a, const int* b) {\n" +
                "    int i = threadIdx.x;\n" +
                "    while (i < 10) {\n" +
                "        if (a[i] > 0) c[i] = a[i] + b[i];\n" +
                "        i++;\n" +
                "    }\n" +
                "}\n";
            var r = Run(code, "k.cu", LanguageMetricsEngine.Cuda);
            Assert.AreEqual(1, r.Functions.Count, "only addKernel; the while is not a function");
            Assert.AreEqual("addKernel", r.Functions[0].Name);
            Assert.AreEqual(3, r.Functions[0].CyclomaticComplexity, "1 + while + if");
        }

    }
}
