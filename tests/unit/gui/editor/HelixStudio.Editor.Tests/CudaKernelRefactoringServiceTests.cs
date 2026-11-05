using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class CudaKernelRefactoringServiceTests
    {
        [TestMethod]
        public void IndependentMapLoopConvertsAndRoundTripsExactly()
        {
            const string source = "void saxpy(float* y, const float* x, float a, int n) {\n    for (int i = 0; i < n; ++i) {\n        y[i] = a * x[i] + y[i];\n    }\n}\n";
            var service = new CudaKernelRefactoringService();
            CudaRefactoringResult gpu = service.ConvertToCuda(source, 0, 0);
            Assert.IsTrue(gpu.Succeeded, gpu.Message);
            StringAssert.Contains(gpu.Source, "thrust::for_each_n");
            StringAssert.Contains(gpu.Source, "#if defined(__CUDACC__)");
            StringAssert.Contains(gpu.Source, "#else");

            CudaRefactoringResult cpu = service.ConvertToCpu(gpu.Source);
            Assert.IsTrue(cpu.Succeeded, cpu.Message);
            Assert.AreEqual(source, cpu.Source);
        }

        [TestMethod]
        public void ReductionIsRefusedWithoutChangingSource()
        {
            const string source = "for (int i = 0; i < n; ++i) { total += values[i]; }";
            CudaRefactoringResult result = new CudaKernelRefactoringService().ConvertToCuda(source, 0, source.Length);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(source, result.Source);
            StringAssert.Contains(result.Message, "reduction");
        }

        [TestMethod]
        public void CrossIterationWriteIsRefused()
        {
            const string source = "for (int i = 0; i < n; ++i) { output[0] = input[i]; }";
            CudaRefactoringResult result = new CudaKernelRefactoringService().ConvertToCuda(source, 0, 0);
            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains(result.Message, "own [i]");
        }

        [TestMethod]
        public void DamagedProvenanceNeverDestroysSource()
        {
            const string source = "// PYOPTOOL-CUDA-BEGIN:not-base64\ncode\n// PYOPTOOL-CUDA-END\n";
            CudaRefactoringResult result = new CudaKernelRefactoringService().ConvertToCpu(source);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(source, result.Source);
        }
    }
}
