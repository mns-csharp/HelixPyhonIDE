using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.ImportEngine.Analysis;
using HelixPythonNamespace.ImportEngine.Execution;
using HelixPythonNamespace.ImportEngine.Planning;

namespace HelixPythonNamespace.ImportEngine.Tests
{
    // End-to-end: copy the real "deformed" fixture into a disposable temp working copy (Execute
    // mutates files, unlike the read-only CudaKernelScaffoldAnalyzerTests, so this never touches
    // the checked-in fixture), then run the full Analyze -> Plan -> Execute pipeline and assert
    // the generated cuda_kernel_debug/ scaffold + rewritten Python loader match the
    // hand-authored g1m_ccxtsri_pae_gpu__helix_import_v2_cuda_separated reference shape.
    [TestClass]
    public class CudaKernelScaffoldExecutorTests
    {
        private string _root;

        [TestInitialize]
        public void Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "helix_iecuda_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private static string FixtureRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && Directory.GetFiles(d.FullName, "*.sln").Length == 0)
                d = d.Parent;
            Assert.IsNotNull(d, "could not locate repo root");
            return Path.Combine(d.FullName, "tests", "fixtures", "python", "g1m_ccxtsri_pae_gpu__deformed_codebase");
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string name = dir.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar);
                if (name.Split(Path.DirectorySeparatorChar).Any(p => p == "__pycache__" || p == ".pytest_cache"))
                    continue;
                Directory.CreateDirectory(Path.Combine(destDir, name));
            }
            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar);
                if (relative.Split(Path.DirectorySeparatorChar).Any(p => p == "__pycache__" || p == ".pytest_cache"))
                    continue;
                string target = Path.Combine(destDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: true);
            }
        }

        [TestMethod]
        public void Execute_GeneratesCudaDebugScaffold_FromDeformedFixture()
        {
            if (!Directory.Exists(FixtureRoot()))
                Assert.Inconclusive("Optional test fixture is unavailable: " + FixtureRoot());
            CopyDirectory(FixtureRoot(), _root);

            ImportAnalysis analysis = new ImportAnalyzer().Analyze(_root);
            Assert.IsTrue(analysis.Success, analysis.ErrorMessage);
            Assert.IsTrue(analysis.DetectedCudaScaffold.HasKernels);

            ImportPlan plan = ImportPlanFactory.CreateDefault(analysis, "g1m_test");
            Assert.IsTrue(plan.GenerateCudaDebugScaffold, "scaffold generation should default to on when kernels are found");
            // This test pins the CUDA scaffold + loader-rewrite shape of the deformed
            // fixture; the strict-OOP guard would additionally encapsulate the loader's
            // module-level helpers and change the exact text asserted below.
            plan.ConvertToObjectOriented = false;

            ImportExecutionResult result = new ImportPlanExecutor().Execute(plan);
            Assert.IsTrue(result.Success, result.ErrorMessage);

            string diagCopy = Environment.GetEnvironmentVariable("HELIX_CUDA_SCAFFOLD_DIAG_COPY");
            if (!string.IsNullOrEmpty(diagCopy))
            {
                if (Directory.Exists(diagCopy)) Directory.Delete(diagCopy, true);
                CopyDirectory(_root, diagCopy);
            }

            string scaffoldDir = Path.Combine(_root, "cuda_kernel_debug");
            Assert.IsTrue(Directory.Exists(scaffoldDir), "cuda_kernel_debug/ was not generated");
            Assert.IsTrue(File.Exists(Path.Combine(scaffoldDir, "CMakeLists.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(scaffoldDir, "common", "debug_common.h")));

            string[] expectedKernels =
            {
                "propose_batch_kernel", "delta_e_segment_kernel", "accept_segment_kernel",
                "corr_bounds_kernel", "correction_pairs_kernel", "causal_accept_kernel",
                "correction_cell_list_kernel", "causal_accept_cell_list_kernel", "bonded_delta_e_kernel",
            };
            foreach (string k in expectedKernels)
            {
                Assert.IsTrue(File.Exists(Path.Combine(scaffoldDir, "kernels", "launch_" + k + ".cu")), "missing launcher for " + k);
                Assert.IsTrue(File.Exists(Path.Combine(scaffoldDir, "apps", "main_" + k + ".cpp")), "missing host app for " + k);
            }

            string cmakeLists = File.ReadAllText(Path.Combine(scaffoldDir, "CMakeLists.txt"));
            StringAssert.Contains(cmakeLists, "project(");
            StringAssert.Contains(cmakeLists, "LANGUAGES CXX CUDA");
            StringAssert.Contains(cmakeLists, "CMAKE_CUDA_ARCHITECTURES");
            StringAssert.Contains(cmakeLists, "-real"); // SASS-only default, avoids the PTX-JIT driver rejection
            StringAssert.Contains(cmakeLists, "function(helix_add_kernel_debug_app");
            foreach (string k in expectedKernels)
                StringAssert.Contains(cmakeLists, "helix_add_kernel_debug_app(" + k + ")");

            // The launcher forward-declares the real kernel and wraps it — a real host/device
            // separation, not a copy of the whole kernel body.
            string launcher = File.ReadAllText(Path.Combine(scaffoldDir, "kernels", "launch_propose_batch_kernel.cu"));
            StringAssert.Contains(launcher, "#include \"propose_batch_kernel.cu\"");
            StringAssert.Contains(launcher, "extern \"C\" cudaError_t helix_launch_propose_batch_kernel(");
            StringAssert.Contains(launcher, "propose_batch_kernel<<<grid, block, shmemBytes, stream>>>(");

            string hostApp = File.ReadAllText(Path.Combine(scaffoldDir, "apps", "main_propose_batch_kernel.cpp"));
            StringAssert.Contains(hostApp, "#include \"debug_common.h\"");
            StringAssert.Contains(hostApp, "helix_launch_propose_batch_kernel(");
            StringAssert.Contains(hostApp, "CUDA_CHECK(");

            // The kernel source library: extracted .cu files + a common.cuh with the shared prelude.
            // Phase 1.5 splits this (originally flat, multi-main) fixture into several packages;
            // the one that ends up holding cuda_kernels.py gets a "..._lib" name derived from the
            // temp root's own (test-run-unique) folder name, not a fixed one.
            string libDir = Directory.GetDirectories(_root).FirstOrDefault(d => Path.GetFileName(d).EndsWith("_lib"));
            Assert.IsNotNull(libDir, "no *_lib package directory found under: " +
                string.Join(", ", Directory.GetDirectories(_root).Select(Path.GetFileName)));
            Assert.IsTrue(Directory.Exists(Path.Combine(libDir, "cuda")), "package cuda/ dir not created: " + libDir);
            foreach (string k in expectedKernels)
                Assert.IsTrue(File.Exists(Path.Combine(libDir, "cuda", k + ".cu")), "missing extracted kernel: " + k);
            Assert.IsTrue(File.Exists(Path.Combine(libDir, "cuda", "common.cuh")));
            string commonCuh = File.ReadAllText(Path.Combine(libDir, "cuda", "common.cuh"));
            StringAssert.Contains(commonCuh, "ns_mic_i");
            StringAssert.Contains(commonCuh, "ns_quant");

            string extractedPropose = File.ReadAllText(Path.Combine(libDir, "cuda", "propose_batch_kernel.cu"));
            StringAssert.Contains(extractedPropose, "#include \"common.cuh\"");
            StringAssert.Contains(extractedPropose, "extern \"C\" __global__ void propose_batch_kernel(");

            // The Python loader was rewritten to read from disk, not embed the kernel text.
            string loaderPy = File.ReadAllText(Path.Combine(libDir, "cuda_kernels.py"));
            StringAssert.Contains(loaderPy, "_read_cuda_source");
            StringAssert.Contains(loaderPy, "_PROPOSE_SRC = _read_cuda_source(\"propose_batch_kernel.cu\")");
            Assert.IsFalse(loaderPy.Contains("extern \"C\" __global__"),
                "the loader should no longer embed kernel source inline after extraction");

            // The root TOML now declares the linked native project.
            string rootToml = File.ReadAllText(result.PyProjectTomlPath);
            StringAssert.Contains(rootToml, "[tool.helixstudio]");
            StringAssert.Contains(rootToml, "linked-cmake-project = \"cuda_kernel_debug\"");
        }
    }
}
