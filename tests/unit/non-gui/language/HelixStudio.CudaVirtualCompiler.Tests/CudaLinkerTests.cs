using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler;
using Cvc = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS §10 (loop 527): symbol-level per-TU linking. Cross-TU redefinitions warn
    /// (linkage is unmodeled, so never an error); cross-TU kernel launches — the
    /// case the per-TU LaunchAnalyzer skips — validate kernel-ness and argument
    /// count against the sibling TU's definition; header-land callees stay silent.
    /// </summary>
    [TestClass]
    public class CudaLinkerTests
    {
        private static List<CompileFileResult> CompileTus(params (string path, string source)[] tus)
        {
            var results = new List<CompileFileResult>();
            foreach (var tu in tus)
            {
                results.Add(new CompileFileResult(tu.path, tu.path, Cvc.CompileSource(tu.source, tu.path)));
            }
            return results;
        }

        [TestMethod]
        public void CrossTuRedefinition_Warns_NamingBothFiles()
        {
            var diags = CudaLinker.Link(CompileTus(
                ("a.cu", "__device__ int helper(int v) { return v + 1; }\n"),
                ("b.cu", "__device__ int helper(int v) { return v + 2; }\n")));
            var odr = diags.Where(d => d.Code == CudaDiagnosticCodes.SemDuplicateDefinition).ToList();
            Assert.AreEqual(1, odr.Count);
            Assert.AreEqual("b.cu", odr[0].FilePath, "reported on the later TU");
            StringAssert.Contains(odr[0].Message, "a.cu");
            Assert.AreEqual(global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Warning, odr[0].Severity,
                "static definitions in two TUs are legal — never an error");
        }

        [TestMethod]
        public void CrossTuLaunch_ArgCountMismatch_IsCaught()
        {
            var diags = CudaLinker.Link(CompileTus(
                ("kernels.cu", "__global__ void scale(int* d, int n) { d[threadIdx.x] *= n; }\n"),
                ("main.cu", "void run(int* d) { scale<<<1, 32>>>(d); }\n")));
            var mismatch = diags.Where(d => d.Code == CudaDiagnosticCodes.LaunchArgCountMismatch).ToList();
            Assert.AreEqual(1, mismatch.Count, "per-TU analysis skips this; the linker must not");
            Assert.AreEqual("main.cu", mismatch[0].FilePath);
            StringAssert.Contains(mismatch[0].Message, "kernels.cu");
        }

        [TestMethod]
        public void CrossTuLaunch_OfNonKernel_IsCaught()
        {
            var diags = CudaLinker.Link(CompileTus(
                ("lib.cu", "__device__ int helper(int v) { return v; }\n"),
                ("main.cu", "void run(int* d) { helper<<<1, 32>>>(d); }\n")));
            Assert.AreEqual(1, diags.Count(d => d.Code == CudaDiagnosticCodes.LaunchOfNonKernel));
        }

        [TestMethod]
        public void CorrectCrossTuLaunch_AndHeaderCallees_AreSilent()
        {
            var diags = CudaLinker.Link(CompileTus(
                ("kernels.cu", "__global__ void scale(int* d, int n) { d[threadIdx.x] *= n; }\n"),
                ("main.cu",
                 "void run(int* d) {\n" +
                 "    scale<<<1, 32>>>(d, 2);\n" +          // resolves in kernels.cu, arity OK
                 "    from_header<<<1, 32>>>(d);\n" +       // defined nowhere we can see
                 "}\n")));
            Assert.AreEqual(0, diags.Count(d => d.Code == CudaDiagnosticCodes.LaunchArgCountMismatch));
            Assert.AreEqual(0, diags.Count(d => d.Code == CudaDiagnosticCodes.LaunchOfNonKernel));
        }

        [TestMethod]
        public void SameTuLaunch_IsNotDoubleReported()
        {
            // The per-TU LaunchAnalyzer already errors on this arity mismatch; the
            // linker must stay out of it or every mistake shows up twice.
            var diags = CudaLinker.Link(CompileTus(
                ("solo.cu",
                 "__global__ void k(int* d, int n) { d[threadIdx.x] = n; }\n" +
                 "void run(int* d) { k<<<1, 32>>>(d); }\n")));
            Assert.AreEqual(0, diags.Count, "linker adds nothing for same-TU launches");
        }
    }
}
