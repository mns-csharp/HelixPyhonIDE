namespace HelixPythonNamespace.CudaVirtualCompiler.Tests
{
    using System;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.CudaVirtualCompiler;

    /// <summary>
    /// Verifies the nvcc-backed authoritative parity pass. These tests require a real nvcc install; they
    /// report Inconclusive (not failed) when nvcc / a host compiler isn't present, so CI without CUDA
    /// still passes.
    /// </summary>
    [TestClass]
    public class NvccVerifierTests
    {
        private static string WriteTemp(string name, string content)
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_nvcc_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        [TestMethod]
        public void Verify_BrokenSource_ReportsAuthoritativeError()
        {
            if (!NvccVerifier.IsAvailable)
                Assert.Inconclusive("nvcc not available on this machine");

            // Missing semicolon — the exact class of hard error the static virtual compiler can miss.
            string cu = WriteTemp("broken.cu",
                "__global__ void k(int* a) {\n" +
                "    int i = threadIdx.x\n" +   // <-- missing ';'
                "    a[i] = i;\n" +
                "}\n");
            try
            {
                NvccVerifyResult r = NvccVerifier.Verify(cu);
                if (!r.Available) Assert.Inconclusive("nvcc could not run: " + r.Reason);
                Assert.IsTrue(r.ErrorCount >= 1, "nvcc must report at least one error on broken source");
                Assert.AreNotEqual(0, r.ExitCode, "nvcc exit code should be non-zero on a compile error");
                StringAssert.Contains(r.NvccVersion, "release", "version string should be captured");
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(cu), true); } catch { } }
        }

        [TestMethod]
        public void Verify_ValidKernel_ReportsNoErrors()
        {
            if (!NvccVerifier.IsAvailable)
                Assert.Inconclusive("nvcc not available on this machine");

            string cu = WriteTemp("ok.cu",
                "__global__ void add(const float* a, const float* b, float* c, int n) {\n" +
                "    int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    if (i < n) c[i] = a[i] + b[i];\n" +
                "}\n");
            try
            {
                NvccVerifyResult r = NvccVerifier.Verify(cu);
                if (!r.Available) Assert.Inconclusive("nvcc could not run: " + r.Reason);
                Assert.AreEqual(0, r.ErrorCount, "a valid kernel must compile with no nvcc errors");
                Assert.AreEqual(0, r.ExitCode, "nvcc exit code should be 0 for a clean compile");
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(cu), true); } catch { } }
        }
    }
}
