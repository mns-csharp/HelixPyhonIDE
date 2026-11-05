using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler;
using Cvc = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS §10 (loop 524): six previously declared-but-never-emitted diagnostic
    /// codes now fire — CU1004 invalid numeric literal, CU2002 unexpected EOF,
    /// CU3021/CU3022 dynamic-shared-memory/extern-__shared__ pairing, CU5011 bad
    /// __shared__ array size, CU5031 kernel reference parameter.
    /// </summary>
    [TestClass]
    public class CudaRevivedCodesTests
    {
        private static CudaCompileResult Compile(string source)
        {
            return Cvc.CompileSource(source, "k.cu");
        }

        private static int Count(CudaCompileResult r, string code)
        {
            return r.Diagnostics.Count(d => d.Code == code);
        }

        // ------------------------------------------------------------- CU1004

        [TestMethod]
        public void InvalidNumericLiterals_ReportCu1004()
        {
            Assert.AreEqual(1, Count(Compile("int a = 123abc;\n"), CudaDiagnosticCodes.LexInvalidNumber),
                "bad suffix");
            Assert.AreEqual(1, Count(Compile("int b = 0x;\n"), CudaDiagnosticCodes.LexInvalidNumber),
                "hex without digits");
            Assert.AreEqual(1, Count(Compile("double c = 1e;\n"), CudaDiagnosticCodes.LexInvalidNumber),
                "empty exponent");
        }

        [TestMethod]
        public void ValidNumericLiterals_AreSilent()
        {
            var result = Compile(
                "unsigned long a = 42ull;\n" +
                "float b = 1.5f;\n" +
                "double c = 2e10;\n" +
                "int d = 0xFF;\n" +
                "int e = 1'000'000;\n");
            Assert.AreEqual(0, Count(result, CudaDiagnosticCodes.LexInvalidNumber));
        }

        // ------------------------------------------------------------- CU2002

        [TestMethod]
        public void TruncatedSource_ReportsCu2002_UnexpectedEof()
        {
            var result = Compile("__global__ void k(int* o) { if (o[0] > 1\n");
            Assert.IsTrue(Count(result, CudaDiagnosticCodes.ParseUnexpectedEof) >= 1,
                "EOF where a token was required must say so, not just 'expected X'");
        }

        // ------------------------------------------------------- CU3021/CU3022

        [TestMethod]
        public void DynamicSharedPassed_ButKernelHasNoExternShared_Cu3021()
        {
            var result = Compile(
                "__global__ void k(float* o) { o[0] = 1.0f; }\n" +
                "void host_fn(float* o) { k<<<1, 32, 1024>>>(o); }\n");
            Assert.AreEqual(1, Count(result, CudaDiagnosticCodes.LaunchSharedMemUnused));
        }

        [TestMethod]
        public void ExternSharedKernel_LaunchedWithoutSharedBytes_Cu3022()
        {
            var result = Compile(
                "__global__ void k(float* o) {\n" +
                "    extern __shared__ float buf[];\n" +
                "    buf[0] = o[0];\n" +
                "}\n" +
                "void host_fn(float* o) { k<<<1, 32>>>(o); }\n");
            Assert.AreEqual(1, Count(result, CudaDiagnosticCodes.LaunchSharedMemMissing));
        }

        [TestMethod]
        public void MatchedSharedPairing_IsSilent()
        {
            var result = Compile(
                "__global__ void k(float* o) {\n" +
                "    extern __shared__ float buf[];\n" +
                "    buf[0] = o[0];\n" +
                "}\n" +
                "void host_fn(float* o) { k<<<1, 32, 256>>>(o); }\n");
            Assert.AreEqual(0, Count(result, CudaDiagnosticCodes.LaunchSharedMemUnused));
            Assert.AreEqual(0, Count(result, CudaDiagnosticCodes.LaunchSharedMemMissing));
        }

        // ------------------------------------------------------------- CU5011

        [TestMethod]
        public void SharedArrayWithNonPositiveSize_Cu5011()
        {
            var result = Compile(
                "__global__ void k(float* o) { __shared__ float t[0]; o[0] = 1.0f; }\n");
            Assert.AreEqual(1, Count(result, CudaDiagnosticCodes.QualSharedBadSize));

            var ok = Compile(
                "__global__ void k2(float* o) { __shared__ float t[64]; t[0] = o[0]; }\n");
            Assert.AreEqual(0, Count(ok, CudaDiagnosticCodes.QualSharedBadSize));
        }

        // ------------------------------------------------------------- CU5031

        [TestMethod]
        public void KernelReferenceParameter_Cu5031()
        {
            var result = Compile(
                "__global__ void k(int& bad, int* ok) { *ok = bad; }\n");
            Assert.AreEqual(1, Count(result, CudaDiagnosticCodes.QualKernelBadParam));

            var ok = Compile(
                "__global__ void k2(int good, int* out_p) { *out_p = good; }\n" +
                "__device__ int helper(const int& fine) { return fine; }\n");
            Assert.AreEqual(0, Count(ok, CudaDiagnosticCodes.QualKernelBadParam),
                "by-value kernel params and device-function references stay legal");
        }
    }
}
