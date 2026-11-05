using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler;
using Cvc = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS §10 (loop 522): try/catch and C++11 lambdas parse cleanly instead of
    /// producing cascade diagnostics. try/catch lowers to sequential blocks with the
    /// catch parameter declared inside its handler; lambda bodies are parsed (syntax
    /// still checked) but the analyses do not descend into them (captures make name
    /// resolution there unprovable).
    /// </summary>
    [TestClass]
    public class CudaModernSyntaxTests
    {
        private static CudaCompileResult Compile(string source)
        {
            return Cvc.CompileSource(source, "k.cu");
        }

        private static int Errors(CudaCompileResult r)
        {
            return r.Diagnostics.Count(d => d.Severity == global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error);
        }

        private static int Cu2101(CudaCompileResult r)
        {
            return r.Diagnostics.Count(d => d.Code == CudaDiagnosticCodes.SemUndefinedName);
        }

        [TestMethod]
        public void TryCatch_ParsesClean_AndCatchParamResolves()
        {
            var result = Compile(
                "int risky();\n" +
                "int host_fn(int* o) {\n" +
                "    try {\n" +
                "        int v = risky();\n" +
                "        o[0] = v;\n" +
                "    } catch (const MyError& e) {\n" +
                "        o[1] = e.code;\n" +
                "    } catch (...) {\n" +
                "        o[2] = 0;\n" +
                "    }\n" +
                "    return 0;\n" +
                "}\n");
            Assert.AreEqual(0, Errors(result), "try/catch must not produce parse errors");
            Assert.AreEqual(0, Cu2101(result), "the catch parameter must resolve inside its handler");
        }

        [TestMethod]
        public void TryBlockLocals_DoNotLeakIntoHandlers()
        {
            var result = Compile(
                "int host_fn(int* o, int n) {\n" +
                "    try { int t = n; o[0] = t; }\n" +
                "    catch (...) { o[1] = t; }\n" +
                "    return 0;\n" +
                "}\n");
            Assert.AreEqual(1, Cu2101(result), "the try block's local is not visible in the handler");
        }

        [TestMethod]
        public void Lambda_ParsesClean_AndCallsResolve()
        {
            var result = Compile(
                "int host_fn(int* o) {\n" +
                "    auto add_one = [](int a) { return a + 1; };\n" +
                "    o[0] = add_one(2);\n" +
                "    return 0;\n" +
                "}\n");
            Assert.AreEqual(0, Errors(result), "a lambda initializer must not produce parse errors");
            Assert.AreEqual(0, Cu2101(result), "the lambda-holding local resolves at the call");
        }

        [TestMethod]
        public void CapturingLambda_AsCallArgument_ParsesClean()
        {
            var result = Compile(
                "void apply(int x);\n" +
                "int host_fn(int total) {\n" +
                "    apply([&total](int v) { total += v; } (3));\n" +
                "    return total;\n" +
                "}\n");
            Assert.AreEqual(0, Errors(result));
        }

        [TestMethod]
        public void LambdaBody_IsNotAnalyzed()
        {
            var result = Compile(
                "int host_fn() {\n" +
                "    auto f = [](int a) { return a + not_declared_anywhere; };\n" +
                "    return 0;\n" +
                "}\n");
            Assert.AreEqual(0, Cu2101(result),
                "captured names make lambda bodies unprovable — no CU2101 inside them");
        }
    }
}
