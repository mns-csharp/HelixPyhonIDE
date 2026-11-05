using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler;
using Cvc = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS §10 (loop 526): CU7001 dialect checks that need syntax context — a token
    /// scan cannot see a lambda ('[' is also indexing) or auto type deduction
    /// ('auto' was a C++98 storage-class specifier); digit separators are a
    /// per-token text predicate. Each feature reports once, like the keyword pass.
    /// </summary>
    [TestClass]
    public class CudaDialectAstTests
    {
        private static CudaCompileResult Compile(string source, string standard)
        {
            CudaCompileOptions options = CudaCompileOptions.Default();
            options.LanguageStandard = standard;
            return Cvc.CompileSource(source, "k.cu", options);
        }

        private static int Cu7001(CudaCompileResult r)
        {
            return r.Diagnostics.Count(d => d.Code == CudaDiagnosticCodes.StandardFeatureUnavailable);
        }

        private const string LambdaSource =
            "int host_fn() {\n" +
            "    auto f = [](int a) { return a + 1; };\n" +
            "    return f(2);\n" +
            "}\n";

        [TestMethod]
        public void Cxx98_RejectsLambda_AndAuto_Cxx11Accepts()
        {
            var old = Compile(LambdaSource, "98");
            Assert.AreEqual(2, Cu7001(old), "one report for the lambda, one for auto");
            Assert.IsTrue(old.Diagnostics.Any(d =>
                d.Code == CudaDiagnosticCodes.StandardFeatureUnavailable &&
                d.Message.Contains("lambda")), "lambda must be named");
            Assert.IsTrue(old.Diagnostics.Any(d =>
                d.Code == CudaDiagnosticCodes.StandardFeatureUnavailable &&
                d.Message.Contains("auto")), "auto must be named");

            Assert.AreEqual(0, Cu7001(Compile(LambdaSource, "11")));
        }

        [TestMethod]
        public void Cxx98_AutoAlone_ReportsOnce()
        {
            var result = Compile(
                "int host_fn(int n) {\n" +
                "    auto a = n + 1;\n" +
                "    auto b = a * 2;\n" +      // same feature — still one report
                "    return b;\n" +
                "}\n", "98");
            Assert.AreEqual(1, Cu7001(result));
        }

        [TestMethod]
        public void DigitSeparators_RequireCxx14()
        {
            const string src = "int host_fn() { int big = 1'000'000; return big; }\n";
            Assert.AreEqual(1, Cu7001(Compile(src, "11")));
            Assert.AreEqual(0, Cu7001(Compile(src, "14")));
        }

        [TestMethod]
        public void PlainCxx98Code_IsSilent()
        {
            var result = Compile(
                "int host_fn(int n) { int total = 0; for (int i = 0; i < n; ++i) total += i; return total; }\n",
                "98");
            Assert.AreEqual(0, Cu7001(result));
        }
    }
}
