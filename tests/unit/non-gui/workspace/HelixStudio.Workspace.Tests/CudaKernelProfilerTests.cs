using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cuda;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// FR-PROF-02: verifies the <c>nvcc -Xptxas -v</c> resource-usage parser and the native occupancy
    /// calculator against verbatim ptxas output. The live run is exercised by the CLI + cuprofile harness.
    /// </summary>
    [TestClass]
    public class CudaKernelProfilerTests
    {
        // Verbatim two-kernel `ptxas -v` output (Ada, sm_89): a light kernel and a register-heavy one with smem + spills.
        private const string Ptxas =
            "ptxas info    : 0 bytes gmem\n" +
            "ptxas info    : Compiling entry function '_Z6vecAddPKfS0_Pfi' for 'sm_89'\n" +
            "ptxas info    : Function properties for _Z6vecAddPKfS0_Pfi\n" +
            "    0 bytes stack frame, 0 bytes spill stores, 0 bytes spill loads\n" +
            "ptxas info    : Used 32 registers, used 0 barriers, 380 bytes cmem[0]\n" +   // CUDA 13 'used N barriers' token
            "ptxas info    : Compiling entry function '_Z11heavyKernelPfi' for 'sm_89'\n" +
            "ptxas info    : Function properties for _Z11heavyKernelPfi\n" +
            "    24 bytes stack frame, 16 bytes spill stores, 12 bytes spill loads\n" +
            "ptxas info    : Used 96 registers, used 1 barriers, 8192 bytes smem, 380 bytes cmem[0]\n";

        [TestMethod]
        public void ParsePtxas_ExtractsPerKernelResources()
        {
            var r = CudaKernelProfiler.ParsePtxas(Ptxas, 256);
            Assert.IsTrue(r.Available);
            Assert.AreEqual(2, r.Kernels.Count);
            Assert.AreEqual("sm_89", r.Arch);

            var k0 = r.Kernels[0];
            Assert.AreEqual("vecAdd", k0.Name);          // demangled
            Assert.AreEqual(32, k0.Registers);
            Assert.AreEqual(0, k0.SharedMemBytes);
            Assert.AreEqual(380, k0.ConstMemBytes);
            Assert.AreEqual(0, k0.SpillStores);

            var k1 = r.Kernels[1];
            Assert.AreEqual("heavyKernel", k1.Name);
            Assert.AreEqual(96, k1.Registers);
            Assert.AreEqual(8192, k1.SharedMemBytes);
            Assert.AreEqual(24, k1.StackFrameBytes);
            Assert.AreEqual(16, k1.SpillStores);
            Assert.AreEqual(12, k1.SpillLoads);
            Assert.AreEqual(380, k1.ConstMemBytes);
        }

        [TestMethod]
        public void ParsePtxas_ComputesOccupancy_LightKernelIsFull_HeavyIsRegisterLimited()
        {
            var r = CudaKernelProfiler.ParsePtxas(Ptxas, 256);
            var light = r.Kernels[0];
            var heavy = r.Kernels[1];

            // 32 regs @ 256 threads on sm_89: warp-count limited, fully occupied.
            Assert.AreEqual(100, light.OccupancyPercent);
            Assert.AreEqual("warps", light.Limiter);
            Assert.AreEqual(48, light.MaxWarpsPerSm);

            // 96 regs caps active blocks to 2 -> 16/48 warps -> 33%, register-limited.
            Assert.AreEqual(33, heavy.OccupancyPercent);
            Assert.AreEqual("registers", heavy.Limiter);
            Assert.AreEqual(2, heavy.ActiveBlocksPerSm);
        }

        [TestMethod]
        public void ComputeOccupancy_SharedMemoryLimited()
        {
            // 32 regs (not the binding constraint) but 48 KB static smem/block on sm_89 (102400 B/SM) -> 2 blocks.
            var k = new CudaKernelProfile { Arch = "sm_89", Registers = 32, SharedMemBytes = 49152, ThreadsPerBlock = 256 };
            CudaKernelProfiler.ComputeOccupancy(k);
            Assert.AreEqual("shared-memory", k.Limiter);
            Assert.AreEqual(2, k.ActiveBlocksPerSm);
            Assert.AreEqual(33, k.OccupancyPercent);
        }

        [TestMethod]
        public void Demangle_ReadsItaniumLengthPrefixedName()
        {
            Assert.AreEqual("vecAdd", CudaKernelProfiler.Demangle("_Z6vecAddPKfS0_Pfi"));
            Assert.AreEqual("plainName", CudaKernelProfiler.Demangle("plainName"));  // not mangled -> unchanged
        }

        [TestMethod]
        public void ParsePtxas_EmptyYieldsNoKernels()
        {
            Assert.AreEqual(0, CudaKernelProfiler.ParsePtxas("").Kernels.Count);
            Assert.AreEqual(0, CudaKernelProfiler.ParsePtxas("nvcc fatal   : no input files\n").Kernels.Count);
        }
    }
}
