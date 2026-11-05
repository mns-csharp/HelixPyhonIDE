using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.ImportEngine.Analysis;

namespace HelixPythonNamespace.ImportEngine.Tests
{
    // Points directly at the real fixture (Analyze never mutates the filesystem, so there's
    // nothing to copy-protect against here, unlike ImportPlanExecutorTests which writes files).
    [TestClass]
    public class CudaKernelScaffoldAnalyzerTests
    {
        private static string FixtureRoot()
        {
            // tests/integration/non-gui/workspace/HelixStudio.ImportEngine.Tests/bin/.../*.dll
            // -> walk up to the repo root, then down into tests/fixtures.
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            DirectoryInfo d = new DirectoryInfo(dir);
            while (d != null && Directory.GetFiles(d.FullName, "*.sln").Length == 0)
                d = d.Parent;
            Assert.IsNotNull(d, "could not locate repo root (the product .sln) from " + dir);
            return Path.Combine(d.FullName, "tests", "fixtures", "python", "g1m_ccxtsri_pae_gpu__deformed_codebase");
        }

        [TestMethod]
        public void DetectsAllNineKernelsInDeformedFixture()
        {
            string root = FixtureRoot();
            if (!Directory.Exists(root))
                Assert.Inconclusive("Optional test fixture is unavailable: " + root);

            ImportAnalysis analysis = new ImportAnalyzer().Analyze(root);
            Assert.IsTrue(analysis.Success, analysis.ErrorMessage);
            Assert.IsNotNull(analysis.DetectedCudaScaffold);
            Assert.IsTrue(analysis.DetectedCudaScaffold.HasKernels);

            string[] expectedNames =
            {
                "propose_batch_kernel", "delta_e_segment_kernel", "accept_segment_kernel",
                "corr_bounds_kernel", "correction_pairs_kernel", "causal_accept_kernel",
                "correction_cell_list_kernel", "causal_accept_cell_list_kernel", "bonded_delta_e_kernel",
            };
            Assert.AreEqual(expectedNames.Length, analysis.DetectedCudaScaffold.Kernels.Count,
                "kernel count mismatch: found " +
                string.Join(", ", analysis.DetectedCudaScaffold.Kernels.Select(k => k.KernelName)));
            foreach (string name in expectedNames)
                Assert.IsTrue(analysis.DetectedCudaScaffold.Kernels.Any(k => k.KernelName == name), "missing kernel: " + name);

            // The shared device-code prelude (NumberSpaceGPU.DEVICE_SRC, aliased as COMMON in
            // cuda_kernels.py) must resolve, and every kernel must be flagged as using it.
            Assert.IsFalse(string.IsNullOrEmpty(analysis.DetectedCudaScaffold.CommonPreludeSource));
            StringAssert.Contains(analysis.DetectedCudaScaffold.CommonPreludeSource, "ns_mic_i");
            StringAssert.Contains(analysis.DetectedCudaScaffold.CommonPreludeSource, "ns_quant");
            Assert.IsTrue(analysis.DetectedCudaScaffold.Kernels.All(k => k.UsesCommonPrelude));

            // Spot-check propose_batch_kernel's parsed signature against the real source.
            DetectedCudaKernel propose = analysis.DetectedCudaScaffold.Kernels.First(k => k.KernelName == "propose_batch_kernel");
            Assert.AreEqual(20, propose.Parameters.Count, "param count: " +
                string.Join(", ", propose.Parameters.Select(p => p.TypeText + " " + p.Name)));
            Assert.AreEqual("ca", propose.Parameters[0].Name);
            Assert.IsTrue(propose.Parameters[0].IsPointer);
            Assert.AreEqual("N", propose.Parameters[12].Name);
            Assert.IsFalse(propose.Parameters[12].IsPointer);
            StringAssert.Contains(propose.Body, "extern \"C\" __global__ void propose_batch_kernel");
            StringAssert.Contains(propose.Body, "n_moved_out[b] = 0");
            Assert.AreEqual("_PROPOSE_SRC", propose.AssignmentVariableName);
            Assert.AreEqual("propose_batch_kernel.cu", propose.CuFileName);
        }
    }
}
