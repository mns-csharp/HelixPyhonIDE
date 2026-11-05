namespace HelixPythonNamespace.Cli.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Cli;

    [TestClass]
    public class CudaCliTests
    {
        private sealed class CliResult
        {
            public int ExitCode;
            public string Out;
            public string Err;
        }

        private static CliResult Run(params string[] args)
        {
            StringWriter o = new StringWriter();
            StringWriter e = new StringWriter();
            int rc = CliRunner.Run(new List<string>(args), o, e);
            return new CliResult { ExitCode = rc, Out = o.ToString(), Err = e.ToString() };
        }

        private static string WriteCu(string dir, string name, string src)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, src);
            return path;
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cuda_cli_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        public void CudaCompileReportsKernelReturnTypeError()
        {
            string dir = TempDir();
            try
            {
                string path = WriteCu(dir, "bad.cu", "__global__ int bad() { return 0; }\n");
                CliResult r = Run("cuda-compile", path);
                StringAssert.Contains(r.Out, "CU5030");
                Assert.IsTrue(r.ExitCode >= 1);
            }
            finally { TryDelete(dir); }
        }

        [TestMethod]
        public void CudaCompileCleanKernelHasNoErrors()
        {
            string dir = TempDir();
            try
            {
                // A device-only kernel with no host code — no memory-space diagnostics.
                string path = WriteCu(dir, "clean.cu",
                    "__global__ void scale(float* a, int n) {\n" +
                    "    int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                    "    if (i < n) { a[i] = a[i] * 2.0f; }\n" +
                    "}\n");
                CliResult r = Run("cuda-compile", path, "--quiet");
                Assert.AreEqual(0, r.ExitCode, "out: " + r.Out);
            }
            finally { TryDelete(dir); }
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
