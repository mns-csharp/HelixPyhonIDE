using System;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler.Translation;

namespace HelixPythonNamespace.CudaVirtualCompiler.Tests
{
    /// <summary>
    /// Unit coverage for the CPU↔CUDA translation engine, including supported kernel shapes,
    /// round trips, and safe refusal of constructs the translator cannot preserve.
    ///
    /// The headline property is the ROUND TRIP: a kernel translated to sequential C and back
    /// must yield the same work. Because the engine splices the author's own body text rather
    /// than pretty-printing an AST, "the same work" is a real character-level comparison
    /// (comments and layout normalised away), not a comparison of two printers.
    /// </summary>
    [TestClass]
    public class CudaTranslationTests
    {
        // ===================== unit behaviour =====================

        [TestMethod]
        public void Map_CudaToCpu_BecomesALoopOverTheGuardBound()
        {
            const string cu =
                "__global__ void scale(const float* in, float* out, int n, float k) {\n" +
                "    int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    if (i < n) { out[i] = k * in[i]; }\n" +
                "}\n";
            TranslationResult r = CudaTranslator.CudaToCpu(cu, "scale");
            Assert.IsTrue(r.Success, r.RefusalReason);
            Assert.AreEqual(KernelShape.Map, r.Shape);
            StringAssert.Contains(r.Code, "for (int i = 0; i < n; ++i)");
            StringAssert.Contains(r.Code, "out[i] = k * in[i];");
            Assert.IsFalse(r.Code.Contains("threadIdx"), "the CPU form must not mention thread indices");
        }

        [TestMethod]
        public void Map_CpuToCuda_BecomesAGuardedKernel()
        {
            const string cpp =
                "void scale(const float* in, float* out, int n, float k) {\n" +
                "    for (int i = 0; i < n; ++i) { out[i] = k * in[i]; }\n" +
                "}\n";
            TranslationResult r = CudaTranslator.CpuToCuda(cpp, "scale");
            Assert.IsTrue(r.Success, r.RefusalReason);
            Assert.AreEqual(KernelShape.Map, r.Shape);
            StringAssert.Contains(r.Code, "__global__ void scale_kernel(");
            StringAssert.Contains(r.Code, "int i = blockIdx.x * blockDim.x + threadIdx.x;");
            StringAssert.Contains(r.Code, "if (i < n)");
            StringAssert.Contains(r.Code, "<<<blocks, threads>>>");
        }

        [TestMethod]
        public void TwoDimensionalKernel_RoundTripsThroughNestedLoops()
        {
            const string cu =
                "__global__ void copy2d(const float* in, float* out, int W, int H) {\n" +
                "    int x = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    int y = blockIdx.y * blockDim.y + threadIdx.y;\n" +
                "    if (x >= W || y >= H) return;\n" +
                "    out[y * W + x] = in[y * W + x];\n" +
                "}\n";
            TranslationResult down = CudaTranslator.CudaToCpu(cu, "copy2d");
            Assert.IsTrue(down.Success, down.RefusalReason);
            StringAssert.Contains(down.Code, "for (int y = 0; y < H; ++y)");
            StringAssert.Contains(down.Code, "for (int x = 0; x < W; ++x)");

            TranslationResult up = CudaTranslator.CpuToCuda(down.Code, "copy2d_cpu", false, 2);
            Assert.IsTrue(up.Success, up.RefusalReason);
            StringAssert.Contains(up.Code, "blockIdx.x");
            StringAssert.Contains(up.Code, "blockIdx.y");
            StringAssert.Contains(CudaTranslator.Normalize(up.Code), "out[y * W + x] = in[y * W + x];");
        }

        [TestMethod]
        public void PerThreadEarlyReturn_BecomesContinue_NotAWholeFunctionExit()
        {
            // In a kernel `return;` means "this thread does nothing". Spliced verbatim into a
            // sequential loop it would abandon every remaining element — a silent wrong answer.
            const string cu =
                "__global__ void label_pass(int* label, int W, int H) {\n" +
                "    int x = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    int y = blockIdx.y * blockDim.y + threadIdx.y;\n" +
                "    if (x >= W || y >= H) return;\n" +
                "    int i = y * W + x;\n" +
                "    int me = label[i];\n" +
                "    if (me < 0) return;\n" +
                "    label[i] = me + 1;\n" +
                "}\n";
            TranslationResult r = CudaTranslator.CudaToCpu(cu, "label_pass");
            Assert.IsTrue(r.Success, r.RefusalReason);

            string body = r.Code.Substring(r.Code.IndexOf("for (int", StringComparison.Ordinal));
            StringAssert.Contains(body, "continue;", "the per-thread early exit must become 'continue'");
            Assert.IsFalse(Regex.IsMatch(body, @"\breturn\s*;"),
                "a bare 'return;' inside the loop would abandon the remaining elements");
            Assert.IsTrue(r.Notes.Exists(n => n.Contains("continue")),
                "the rewrite must be reported, not silent");

            // And the CPU form must convert back, which it could not while it held a `return`.
            TranslationResult back = CudaTranslator.CpuToCuda(r.Code, "label_pass_cpu", false, 2);
            Assert.IsTrue(back.Success, back.RefusalReason);
        }

        // ===================== the refusal policy =====================

        [TestMethod]
        public void LoopCarriedDependence_IsRefusedWithTheDependenceNamed()
        {
            const string cpp =
                "void prefix(float* a, int n) {\n" +
                "    for (int i = 1; i < n; ++i) { a[i] = a[i - 1] + a[i]; }\n" +
                "}\n";
            TranslationResult r = CudaTranslator.CpuToCuda(cpp, "prefix");
            Assert.IsFalse(r.Success, "a prefix recurrence must not become a one-thread-per-element kernel");
            Assert.AreEqual(KernelShape.Scan, r.Shape);
            StringAssert.Contains(r.RefusalReason, "loop-carried");
            StringAssert.Contains(r.RefusalReason, "scan");
        }

        [TestMethod]
        public void DataDependentWrite_IsRefusedAsACollisionRisk()
        {
            const string cpp =
                "void scatter(const int* perm, const float* src, float* dst, int n) {\n" +
                "    for (int i = 0; i < n; ++i) { dst[perm[i]] = src[i]; }\n" +
                "}\n";
            TranslationResult r = CudaTranslator.CpuToCuda(cpp, "scatter");
            Assert.IsFalse(r.Success, "a data-dependent write index is a collision risk");
            Assert.AreEqual(KernelShape.Scatter, r.Shape);
            StringAssert.Contains(r.RefusalReason, "collision");
        }

        [TestMethod]
        public void AffineLinearisedWrite_IsNotMistakenForAScatter()
        {
            // `out[row * W + col]` is a linearised 2-D index, NOT a scatter: distinct
            // iterations address distinct elements.
            const string cpp =
                "void fill(float* out, int H, int W) {\n" +
                "    for (int row = 0; row < H; ++row)\n" +
                "        for (int col = 0; col < W; ++col)\n" +
                "            out[row * W + col] = 1.0f;\n" +
                "}\n";
            TranslationResult r = CudaTranslator.CpuToCuda(cpp, "fill", false);
            Assert.IsTrue(r.Success, r.RefusalReason);
            Assert.AreNotEqual(KernelShape.Scatter, r.Shape);
        }

        [TestMethod]
        public void SharedMemoryKernel_IsRefusedRatherThanSerialisedWrongly()
        {
            const string cu =
                "__global__ void reduce(const float* in, float* out, int n) {\n" +
                "    extern __shared__ float sdata[];\n" +
                "    int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    if (i < n) { sdata[threadIdx.x] = in[i]; __syncthreads(); out[blockIdx.x] = sdata[0]; }\n" +
                "}\n";
            TranslationResult r = CudaTranslator.CudaToCpu(cu, "reduce");
            Assert.IsFalse(r.Success, "a cooperating kernel must not be silently serialised");
            StringAssert.Contains(r.RefusalReason, "__shared__");
        }

        [TestMethod]
        public void KernelWithoutBoundsGuard_IsRefusedRatherThanGuessingTheCount()
        {
            const string cu =
                "__global__ void touch(float* out) {\n" +
                "    int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    out[i] = 1.0f;\n" +
                "}\n";
            TranslationResult r = CudaTranslator.CudaToCpu(cu, "touch");
            Assert.IsFalse(r.Success, "without a guard the element count is unknown");
            StringAssert.Contains(r.RefusalReason, "bounds guard");
        }

        [TestMethod]
        public void RowOnY_StaysOnY_AcrossTheRoundTrip()
        {
            // Declaration order is not axis order. Lifting this kernel with the row first and
            // pushing it back must not move the row onto threadIdx.x, which would leave the
            // work correct but the memory access strided.
            const string kernel =
                "__global__ void add2d(float* c, const float* a, const float* b, int rows, int cols) {\n" +
                "    int i = blockIdx.y * blockDim.y + threadIdx.y;\n" +
                "    int j = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    if (i < rows && j < cols) {\n" +
                "        int index = i * cols + j;\n" +
                "        c[index] = a[index] + b[index];\n" +
                "    }\n" +
                "}\n";

            TranslationResult down = CudaTranslator.CudaToCpu(kernel, "add2d");
            Assert.IsTrue(down.Success, down.RefusalReason);
            // The row must be the OUTER loop, so the column varies fastest.
            int rowAt = down.Code.IndexOf("for (int i =", StringComparison.Ordinal);
            int colAt = down.Code.IndexOf("for (int j =", StringComparison.Ordinal);
            Assert.IsTrue(rowAt >= 0 && colAt > rowAt, "the row loop must enclose the column loop:\n" + down.Code);

            TranslationResult up = CudaTranslator.CpuToCuda(down.Code, "add2d_cpu", false, 2);
            Assert.IsTrue(up.Success, up.RefusalReason);
            StringAssert.Contains(up.Code, "int i = blockIdx.y * blockDim.y + threadIdx.y;");
            StringAssert.Contains(up.Code, "int j = blockIdx.x * blockDim.x + threadIdx.x;");
        }

        [TestMethod]
        public void SizeTIndex_IsNotNarrowedToInt()
        {
            const string kernel =
                "__global__ void initialize(unsigned char* data, size_t n) {\n" +
                "    size_t i = (size_t)blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    if (i < n) { data[i] = i & 0xff; }\n" +
                "}\n";

            TranslationResult down = CudaTranslator.CudaToCpu(kernel, "initialize");
            Assert.IsTrue(down.Success, down.RefusalReason);
            StringAssert.Contains(down.Code, "for (size_t i = 0;");

            TranslationResult up = CudaTranslator.CpuToCuda(down.Code, "initialize_cpu", false, 1);
            Assert.IsTrue(up.Success, up.RefusalReason);
            StringAssert.Contains(up.Code, "size_t i = blockIdx.x");
            Assert.IsFalse(up.Code.Contains("int i = blockIdx.x"),
                "a size_t index narrowed to int caps the array at 2^31 elements");
        }

        [TestMethod]
        public void PlainSetupBeforeTheGuard_IsCarriedIntoTheLoop_NotDropped()
        {
            const string kernel =
                "__global__ void kernel_func(float* out, const float* in, int N) {\n" +
                "    int dimx = N;\n" +
                "    int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    if (i < dimx) {\n" +
                "        out[i] = in[i] * 2.0f;\n" +
                "    }\n" +
                "}\n";

            TranslationResult down = CudaTranslator.CudaToCpu(kernel, "kernel_func");
            Assert.IsTrue(down.Success, down.RefusalReason);
            StringAssert.Contains(down.Code, "int dimx = N;");
            StringAssert.Contains(down.Code, "out[i] = in[i] * 2.0f;");
        }

        [TestMethod]
        public void GridStrideSetupBeforeTheGuard_IsRefused_BecauseItHasNoSequentialMeaning()
        {
            const string kernel =
                "__global__ void mark(int* arcs, int arcs_count) {\n" +
                "    int threadIndex = blockDim.x * blockIdx.x + threadIdx.x;\n" +
                "    int stride = blockDim.x * gridDim.x;\n" +
                "    if (threadIndex >= arcs_count) return;\n" +
                "    for (int i = threadIndex; i < arcs_count; i += stride) arcs[i] = 1;\n" +
                "}\n";

            TranslationResult down = CudaTranslator.CudaToCpu(kernel, "mark");
            Assert.IsFalse(down.Success, "a grid-stride computed from the thread geometry cannot be lifted");
            StringAssert.Contains(down.RefusalReason, "stride");
        }

        [TestMethod]
        public void StatementsAfterTheGuard_AreRefused_NotSilentlyDiscarded()
        {
            const string kernel =
                "__global__ void printMatrix(const int* mat, int rr, int cc) {\n" +
                "    int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
                "    int j = blockIdx.y * blockDim.y + threadIdx.y;\n" +
                "    if (i < rr && j < cc) { mat2[i * cc + j] = mat[i * cc + j]; }\n" +
                "    mat2[0] = 1;\n" +
                "}\n";

            TranslationResult down = CudaTranslator.CudaToCpu(kernel, "printMatrix");
            Assert.IsFalse(down.Success, "trailing statements run for every thread and must not vanish");
            StringAssert.Contains(down.RefusalReason, "after its bounds guard");
        }
    }
}
