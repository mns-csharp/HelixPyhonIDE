using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor.Completion;

namespace HelixPythonNamespace.Tests
{
    /// <summary>FR-IC-02: CUDA completion is sourced from the real CudaVirtualCompiler symbol table
    /// (user-defined helpers/kernels/params + seeded built-ins), not hardcoded lists + regex.</summary>
    [TestClass]
    public class CudaCompletionAstTests
    {
        private const string Src =
            "__device__ int square(int v) { return v * v; }\n" +
            "\n" +
            "__global__ void compute(int* out, int n) {\n" +
            "    int idx = threadIdx.x;\n" +
            "    out[idx] = square(idx);\n" +
            "}\n";

        // The symbol-table sourcing these tests exercise compiles the TU, which
        // §8.2 gates behind the explicit background-analysis opt-in.
        [TestInitialize]
        public void EnableBackgroundAnalysis()
        {
            AnalysisGate.BackgroundAnalysisEnabled = true;
        }

        [TestCleanup]
        public void DisableBackgroundAnalysis()
        {
            AnalysisGate.BackgroundAnalysisEnabled = false;
        }

        private static System.Collections.Generic.List<CompletionItem> Complete(int caret)
        {
            return new CudaCompletionEngine().GetCompletions(Src, caret, "k.cu", null);
        }

        [TestMethod]
        public void IncludesUserDefinedDeviceFunctionAndKernel()
        {
            var items = Complete(Src.Length);
            Assert.IsTrue(items.Any(i => i.Label == "square"), "user __device__ helper 'square' should be offered");
            Assert.IsTrue(items.Any(i => i.Label == "compute"), "user kernel 'compute' should be offered");
        }

        [TestMethod]
        public void UserFunction_HasSpaceQualifiedDetail()
        {
            var items = Complete(Src.Length);
            var sq = items.First(i => i.Label == "square");
            StringAssert.Contains(sq.Detail, "__device__", "square's detail names its function space");
        }

        [TestMethod]
        public void IncludesSeededBuiltins_FromTheSameTable()
        {
            var items = Complete(Src.Length);
            // These come from Builtins.SeedBuiltins in the symbol table — not a second hardcoded copy.
            Assert.IsTrue(items.Any(i => i.Label == "cudaMalloc"), "seeded runtime builtin present");
            Assert.IsTrue(items.Any(i => i.Label == "threadIdx"), "seeded coordinate builtin present");
        }

        [TestMethod]
        public void StillOffersKeywords()
        {
            var items = Complete(Src.Length);
            Assert.IsTrue(items.Any(i => i.Label == "__global__" && i.Kind == CompletionKind.Keyword));
        }

        // §10 gap 7: member completion resolves through the symbol table — struct
        // members via a typedef alias, not the hardcoded x/y/z/w vector list.
        [TestMethod]
        public void MemberCompletion_ResolvesStructFieldsThroughTypedef()
        {
            string src =
                "struct Particle { float mass; float velocity; int id; };\n" +
                "typedef Particle Body;\n" +
                "__global__ void step(int n) {\n" +
                "    Body b;\n" +
                "    b.\n" +
                "}\n";
            int caret = src.IndexOf("b.") + 2;
            var items = new CudaCompletionEngine().GetCompletions(src, caret, "k.cu", null);
            Assert.IsTrue(items.Any(i => i.Label == "mass"), "struct field via typedef");
            Assert.IsTrue(items.Any(i => i.Label == "velocity"));
            Assert.IsTrue(items.Any(i => i.Label == "id"));
            Assert.IsFalse(items.Any(i => i.Label == "w"),
                "the vector x/y/z/w fallback must not fire for a resolved record");
        }

        [TestMethod]
        public void MemberCompletion_VectorBuiltins_KeepTheCoordinateFallback()
        {
            string src = "__global__ void k() { int i = threadIdx. }\n";
            int caret = src.IndexOf("threadIdx.") + "threadIdx.".Length;
            var items = new CudaCompletionEngine().GetCompletions(src, caret, "k.cu", null);
            Assert.IsTrue(items.Any(i => i.Label == "x"));
            Assert.IsFalse(items.Any(i => i.Label == "w"), "threadIdx is a uint3 — no w");
        }
    }
}
