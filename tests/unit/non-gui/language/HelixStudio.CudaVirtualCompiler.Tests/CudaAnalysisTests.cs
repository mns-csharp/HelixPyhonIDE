namespace HelixPythonNamespace.CudaVirtualCompiler.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.CudaVirtualCompiler;

    [TestClass]
    public class CudaAnalysisTests
    {
        private static CudaCompileResult Compile(string src)
        {
            return CudaVirtualCompiler.CompileSource(src, "test.cu");
        }

        private static bool Has(CudaCompileResult r, string code)
        {
            for (int i = 0; i < r.Diagnostics.Count; ++i)
                if (r.Diagnostics[i].Code == code) return true;
            return false;
        }

        private static bool HasAnyPrefix(CudaCompileResult r, string prefix)
        {
            for (int i = 0; i < r.Diagnostics.Count; ++i)
                if (r.Diagnostics[i].Code.StartsWith(prefix)) return true;
            return false;
        }

        private const string CleanKernel =
            "__global__ void add(float* a, float* b, int n) {\n" +
            "  int i = blockIdx.x * blockDim.x + threadIdx.x;\n" +
            "  if (i < n) { a[i] = a[i] + b[i]; }\n" +
            "}\n" +
            "void run() {\n" +
            "  float* d_a; float* d_b;\n" +
            "  cudaMalloc(&d_a, 100);\n" +
            "  cudaMalloc(&d_b, 100);\n" +
            "  add<<<1, 256>>>(d_a, d_b, 100);\n" +
            "  cudaDeviceSynchronize();\n" +
            "  cudaFree(d_a);\n" +
            "  cudaFree(d_b);\n" +
            "}\n";

        [TestMethod]
        public void CleanKernelHasNoErrorSeverityDiagnostics()
        {
            CudaCompileResult r = Compile(CleanKernel);
            Assert.IsFalse(r.HasErrors(), "expected no error-severity diagnostics for a well-formed kernel");
            Assert.IsNotNull(r.Unit);
            Assert.IsNotNull(r.SymbolTable);
        }

        [TestMethod]
        public void BlockTooLargeReportsCu3010()
        {
            CudaCompileResult r = Compile(
                "__global__ void k(int* a) { }\n" +
                "void run() { int* d; cudaMalloc(&d, 4); k<<<1, 2048>>>(d); }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.LaunchBlockTooLarge));
            Assert.IsTrue(r.HasErrors());
            Assert.IsTrue(r.ErrorCount() >= 1);
        }

        [TestMethod]
        public void LaunchOfNonKernelReportsCu3001()
        {
            CudaCompileResult r = Compile(
                "void notkernel() { }\n" +
                "void run() { notkernel<<<1, 1>>>(); }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.LaunchOfNonKernel));
        }

        [TestMethod]
        public void ArgCountMismatchReportsCu3030()
        {
            CudaCompileResult r = Compile(
                "__global__ void k(int a) { }\n" +
                "void run() { k<<<1, 1>>>(1, 2); }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.LaunchArgCountMismatch));
        }

        [TestMethod]
        public void HostPointerPassedToKernelReportsCu4030()
        {
            CudaCompileResult r = Compile(
                "__global__ void k(float* p) { }\n" +
                "void run() { float* h = (float*)malloc(100); k<<<1, 1>>>(h); }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.MemHostPtrToKernel));
        }

        [TestMethod]
        public void FreeOfHostPointerReportsCu4021()
        {
            CudaCompileResult r = Compile(
                "void run() { float* h = (float*)malloc(100); cudaFree(h); }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.MemFreeOfHostPointer));
        }

        [TestMethod]
        public void BuiltinVarInHostReportsCu5003()
        {
            CudaCompileResult r = Compile("void run() { int i = threadIdx.x; (void)i; }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.QualBuiltinVarInHost));
        }

        [TestMethod]
        public void SyncInDivergentBranchReportsCu5020()
        {
            CudaCompileResult r = Compile(
                "__global__ void k(int* a) { if (threadIdx.x < 5) { __syncthreads(); } }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.QualSyncInDivergentBranch));
        }

        [TestMethod]
        public void NonVoidKernelReturnReportsCu5030()
        {
            CudaCompileResult r = Compile("__global__ int k() { return 0; }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.QualKernelBadReturnType));
        }

        [TestMethod]
        public void UncheckedCudaErrorReportsCu6001()
        {
            CudaCompileResult r = Compile("void run() { cudaMalloc(0, 100); }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.LintUncheckedCudaError));
        }

        [TestMethod]
        public void PostLaunchCheckInsideIfSuppressesCu6002()
        {
            CudaCompileResult r = Compile(
                "__global__ void k(){}\n" +
                "int main(){ k<<<1,1>>>(); " +
                "if(cudaGetLastError()!=cudaSuccess)return 1; return 0; }\n");
            Assert.IsFalse(Has(r, CudaDiagnosticCodes.LintMissingPostLaunchCheck));
        }

        [TestMethod]
        public void PostLaunchCheckInitializerSuppressesCu6002()
        {
            CudaCompileResult r = Compile(
                "__global__ void k(){}\n" +
                "int main(){ k<<<1,1>>>(); cudaError_t e=cudaDeviceSynchronize(); " +
                "if(e!=cudaSuccess)return 1; return 0; }\n");
            Assert.IsFalse(Has(r, CudaDiagnosticCodes.LintMissingPostLaunchCheck));
        }

        [TestMethod]
        public void Cxx98RejectsNullptrWithCu7001()
        {
            CudaCompileOptions options = CudaCompileOptions.Default();
            options.LanguageStandard = "98";
            CudaCompileResult r = CudaVirtualCompiler.CompileSource(
                "int main(){ int* p=nullptr; return p==nullptr; }\n", "main.cpp", options);
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.StandardFeatureUnavailable));
        }

        [TestMethod]
        public void Cxx11AcceptsNullptrDialectFeature()
        {
            CudaCompileOptions options = CudaCompileOptions.Default();
            options.LanguageStandard = "11";
            CudaCompileResult r = CudaVirtualCompiler.CompileSource(
                "int main(){ int* p=nullptr; return p==nullptr; }\n", "main.cpp", options);
            Assert.IsFalse(Has(r, CudaDiagnosticCodes.StandardFeatureUnavailable));
        }

        [TestMethod]
        public void Cxx17RejectsConceptAndCxx20AcceptsDialectFeature()
        {
            CudaCompileOptions oldOptions = CudaCompileOptions.Default();
            oldOptions.LanguageStandard = "17";
            CudaCompileResult old = CudaVirtualCompiler.CompileSource(
                "template<class T> concept Number = true;\n", "feature.cpp", oldOptions);
            Assert.IsTrue(Has(old, CudaDiagnosticCodes.StandardFeatureUnavailable));

            CudaCompileOptions modernOptions = CudaCompileOptions.Default();
            modernOptions.LanguageStandard = "20";
            CudaCompileResult modern = CudaVirtualCompiler.CompileSource(
                "template<class T> concept Number = true;\n", "feature.cpp", modernOptions);
            Assert.IsFalse(Has(modern, CudaDiagnosticCodes.StandardFeatureUnavailable));
        }

        [TestMethod]
        public void StaticArrayOutOfBoundsReportsCu6030()
        {
            CudaCompileResult r = Compile("__global__ void k() { int arr[8]; arr[10] = 1; }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.LintArrayOob));
        }

        [TestMethod]
        public void BreakOutsideLoopReportsCu2110()
        {
            CudaCompileResult r = Compile("void run() { break; }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.SemBreakOutsideLoop));
        }

        [TestMethod]
        public void DisablingLintSuppressesCu6xxx()
        {
            CudaCompileOptions opts = CudaCompileOptions.Default();
            opts.EnableLint = false;
            CudaCompileResult r = CudaVirtualCompiler.CompileSource(
                "void run() { cudaMalloc(0, 100); }\n", "test.cu", opts);
            Assert.IsFalse(HasAnyPrefix(r, "CU6"));
        }

        [TestMethod]
        public void MacroDefinedBlockSizeIsConstantFoldedThroughDim3()
        {
            // #define BLOCK is folded when reached through a dim3 variable
            // (EvalDim resolves one level of local-variable indirection, then
            // EvalConstInt resolves the object-like macro). Note: a bare macro used
            // directly as the block dim (k<<<1, BLOCK>>>) is deliberately NOT folded
            // by the C++ EvalDim (its NameExpr branch returns Unknown early) — this
            // test exercises the path the engine actually folds.
            CudaCompileResult r = Compile(
                "#define BLOCK 2048\n" +
                "__global__ void k(int* a) { }\n" +
                "void run() { int* d; cudaMalloc(&d, 4); dim3 block(BLOCK); k<<<1, block>>>(d); }\n");
            Assert.IsTrue(Has(r, CudaDiagnosticCodes.LaunchBlockTooLarge));
        }

        [TestMethod]
        public void CompileSolutionReadsFilesAndReportsPerFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cuda_vc_test_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string f = Path.Combine(dir, "k.cu");
                File.WriteAllText(f, "__global__ int bad() { return 0; }\n");
                List<WorkspaceFile> files = new List<WorkspaceFile>();
                WorkspaceFile wf = new WorkspaceFile();
                wf.FilePath = f;
                files.Add(wf);
                List<CompileFileResult> results = CudaVirtualCompiler.CompileSolution(files);
                Assert.AreEqual(1, results.Count);
                Assert.AreEqual(f, results[0].FilePath);
                Assert.IsTrue(Has(results[0].Result, CudaDiagnosticCodes.QualKernelBadReturnType));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
