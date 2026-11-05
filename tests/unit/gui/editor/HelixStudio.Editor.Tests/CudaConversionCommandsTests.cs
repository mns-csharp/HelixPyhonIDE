using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// The four context-menu conversions. These pin the SURFACE — which command appears for which
    /// file, what the replacement text looks like, and that a refusal names the dependence — while
    /// CudaTranslationTests pins the analysis itself against the book corpus.
    /// </summary>
    [TestClass]
    public class CudaConversionCommandsTests
    {
        private const string CLoop =
            "void scale(float* out, const float* in, float a, int n) {\n" +
            "    for (int i = 0; i < n; ++i) {\n" +
            "        out[i] = a * in[i];\n" +
            "    }\n" +
            "}\n";

        private const string Kernel =
            "__global__ void scale(float* out, const float* in, float a, int n) {\n" +
            "    int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
            "    if (i < n) {\n" +
            "        out[i] = a * in[i];\n" +
            "    }\n" +
            "}\n";

        [TestMethod]
        public void ForwardCommandsOfferedOnCAndCppFilesOnly_ReverseOnCudaFilesOnly()
        {
            Assert.IsTrue(CudaConversionCommands.AppliesTo(CudaConversionKind.ToCudaKernel, "a.c"));
            Assert.IsTrue(CudaConversionCommands.AppliesTo(CudaConversionKind.ToCudaKernel, "a.cpp"));
            Assert.IsTrue(CudaConversionCommands.AppliesTo(CudaConversionKind.ToCudaFramework, "a.cpp"));

            // "Convert to C"/"Convert to C++" would be meaningless in a file that has no kernels.
            Assert.IsFalse(CudaConversionCommands.AppliesTo(CudaConversionKind.ToC, "a.c"));
            Assert.IsFalse(CudaConversionCommands.AppliesTo(CudaConversionKind.ToCpp, "a.cpp"));
            Assert.IsTrue(CudaConversionCommands.AppliesTo(CudaConversionKind.ToC, "a.cu"));
            Assert.IsTrue(CudaConversionCommands.AppliesTo(CudaConversionKind.ToCpp, "a.cuh"));

            Assert.IsFalse(CudaConversionCommands.AppliesTo(CudaConversionKind.ToCudaKernel, "a.py"));
        }

        [TestMethod]
        public void CSelection_ConvertsToAKernelWithTheBodyPreserved()
        {
            CudaConversionOutcome r = CudaConversionCommands.Convert(
                CudaConversionKind.ToCudaKernel, CLoop, 0, CLoop.Length);

            Assert.IsTrue(r.Succeeded, r.Message);
            StringAssert.Contains(r.Replacement, "__global__");
            StringAssert.Contains(r.Replacement, "blockIdx.x * blockDim.x + threadIdx.x");
            StringAssert.Contains(r.Replacement, "out[i] = a * in[i];");
            Assert.AreEqual("Map", r.Shape);
        }

        [TestMethod]
        public void CppSelection_ConvertsToTheFrameworkForm_WithNoLaunchGeometryForTheUserToGetRight()
        {
            CudaConversionOutcome r = CudaConversionCommands.Convert(
                CudaConversionKind.ToCudaFramework, CLoop, 0, CLoop.Length);

            Assert.IsTrue(r.Succeeded, r.Message);
            StringAssert.Contains(r.Replacement, "gpu::for_each(n,");
            StringAssert.Contains(r.Replacement, "HELIX_GPU (int i)");
            StringAssert.Contains(r.Replacement, "out[i] = a * in[i];");

            // The whole point of the framework form: none of CUDA's ceremony survives into it.
            Assert.IsFalse(r.Replacement.Contains("__global__"));
            Assert.IsFalse(r.Replacement.Contains("blockIdx"));
            Assert.IsFalse(r.Replacement.Contains("cudaMalloc"));
            Assert.IsFalse(r.Replacement.Contains("<<<"));
        }

        [TestMethod]
        public void CudaSelection_ConvertsBackToC_AndToCpp()
        {
            CudaConversionOutcome c = CudaConversionCommands.Convert(
                CudaConversionKind.ToC, Kernel, 0, Kernel.Length);
            Assert.IsTrue(c.Succeeded, c.Message);
            StringAssert.Contains(c.Replacement, "for (int i = 0; i < n; ++i)");
            StringAssert.Contains(c.Replacement, "out[i] = a * in[i];");
            Assert.IsFalse(c.Replacement.Contains("threadIdx"));

            CudaConversionOutcome cpp = CudaConversionCommands.Convert(
                CudaConversionKind.ToCpp, Kernel, 0, Kernel.Length);
            Assert.IsTrue(cpp.Succeeded, cpp.Message);
            StringAssert.Contains(cpp.Replacement, "for (int i = 0; i < n; ++i)");
        }

        [TestMethod]
        public void RoundTripThroughTheMenuCommandsPreservesTheWork()
        {
            CudaConversionOutcome toC = CudaConversionCommands.Convert(
                CudaConversionKind.ToC, Kernel, 0, Kernel.Length);
            Assert.IsTrue(toC.Succeeded, toC.Message);

            CudaConversionOutcome back = CudaConversionCommands.Convert(
                CudaConversionKind.ToCudaKernel, toC.Replacement, 0, toC.Replacement.Length);
            Assert.IsTrue(back.Succeeded, back.Message);
            StringAssert.Contains(back.Replacement, "out[i] = a * in[i];");
            StringAssert.Contains(back.Replacement, "blockIdx.x * blockDim.x + threadIdx.x");
        }

        [TestMethod]
        public void AReductionIsRefusedAndTheReasonNamesTheDependence()
        {
            const string reduction =
                "void total(const float* v, float* acc, int n) {\n" +
                "    for (int i = 0; i < n; ++i) {\n" +
                "        *acc += v[i];\n" +
                "    }\n" +
                "}\n";

            CudaConversionOutcome r = CudaConversionCommands.Convert(
                CudaConversionKind.ToCudaKernel, reduction, 0, reduction.Length);

            Assert.IsFalse(r.Succeeded);
            Assert.IsNull(r.Replacement, "a refused conversion must not hand back text to paste in");
            Assert.IsTrue(r.Message.Length > 0);
        }

        [TestMethod]
        public void ACooperativeKernelIsRefusedRatherThanSerialisedIntoADifferentAlgorithm()
        {
            const string tiled =
                "__global__ void reduce(float* out, const float* in, int n) {\n" +
                "    __shared__ float buf[256];\n" +
                "    int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    buf[threadIdx.x] = i < n ? in[i] : 0.0f;\n" +
                "    __syncthreads();\n" +
                "    if (threadIdx.x == 0) out[blockIdx.x] = buf[0];\n" +
                "}\n";

            CudaConversionOutcome r = CudaConversionCommands.Convert(
                CudaConversionKind.ToC, tiled, 0, tiled.Length);

            Assert.IsFalse(r.Succeeded);
            StringAssert.Contains(r.Message, "shared");
        }

        [TestMethod]
        public void AnInvalidSelectionRangeIsReportedInsteadOfThrowing()
        {
            CudaConversionOutcome r = CudaConversionCommands.Convert(
                CudaConversionKind.ToCudaKernel, CLoop, 5, CLoop.Length);
            Assert.IsFalse(r.Succeeded);
            StringAssert.Contains(r.Message, "invalid");
        }
    }
}
