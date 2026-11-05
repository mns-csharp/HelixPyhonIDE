using System;
using System.Linq;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor.Completion;
using HelixPythonNamespace.Core.Model;

namespace HelixPythonNamespace.Tests
{
    /// <summary>FR-KB-06: completion/Quick Info carries real CUDA documentation from CudaCatalog.</summary>
    [TestClass]
    public class CudaCatalogTests
    {
        [TestMethod]
        public void Catalog_HasParameterDescriptions_ForCudaMemcpy()
        {
            string doc = CudaCatalog.GetDoc("cudaMemcpy");
            Assert.IsNotNull(doc);
            StringAssert.Contains(doc, "Parameters:");
            StringAssert.Contains(doc, "kind", "documents the direction parameter");
            StringAssert.Contains(doc, "cudaMemcpyHostToDevice", "explains the memcpy-kind enum meaning");
        }

        [TestMethod]
        public void CompletionItem_ForBuiltin_CarriesCatalogDocumentation()
        {
            const string src =
                "int main() {\n" +
                "    void* d;\n" +
                "    cudaMalloc(&d, 16);\n" +
                "}\n";
            var items = new CudaCompletionEngine().GetCompletions(src, src.Length, "k.cu", null);
            var malloc = items.FirstOrDefault(i => i.Label == "cudaMalloc");
            Assert.IsNotNull(malloc, "cudaMalloc offered");
            Assert.IsFalse(string.IsNullOrEmpty(malloc.Documentation), "cudaMalloc has catalog documentation");
            StringAssert.Contains(malloc.Documentation, "device memory");
        }

        [TestMethod]
        public void SyncThreads_HasDivergenceWarningDoc()
        {
            string doc = CudaCatalog.GetDoc("__syncthreads");
            Assert.IsNotNull(doc);
            StringAssert.Contains(doc, "Barrier");
        }

        [TestMethod]
        public void UnknownSymbol_HasNoDoc()
        {
            Assert.IsNull(CudaCatalog.GetDoc("not_a_cuda_symbol"));
        }

        [TestMethod]
        public void PreprocessorLineOffersCudaDirectives()
        {
            const string src = "#def";
            var items = new CudaCompletionEngine().GetCompletions(src, src.Length, "kernel.cu", null);
            Assert.IsTrue(items.Any(i => i.Label == "define"));
            Assert.IsTrue(items.All(i => i.Detail.StartsWith("preprocessor directive #")));
        }

        [TestMethod]
        public void IncludeQuotesOfferHeadersFromLoadedCudaFixture()
        {
            string cmake = FindFixtureCMake();
            string sourceFile = Path.Combine(Path.GetDirectoryName(cmake), "app_cli", "src", "main.cu");
            var solution = new SolutionModel(cmake, "CudaCppMultiPackageWorkspace");
            const string src = "#include \"lib_core/a";
            var engine = new CudaCompletionEngine();
            CompletionContext context = engine.Classify(src, src.Length);
            var items = engine.GetCompletions(src, src.Length, sourceFile, solution, context);

            Assert.AreEqual(CompletionContextKind.PreprocessorInclude, context.Kind);
            Assert.AreEqual("lib_core/a", context.Prefix);
            Assert.IsTrue(items.Any(i => i.Label == "lib_core/animals.cuh"));
            Assert.IsFalse(items.Any(i => i.Label == "vector"), "quoted includes show project headers, not standard headers");
        }

        [TestMethod]
        public void IncludeAnglesOfferStandardAndCudaHeaders()
        {
            const string src = "#include <cuda_";
            var items = new CudaCompletionEngine().GetCompletions(src, src.Length, "kernel.cu", null);
            Assert.IsTrue(items.Any(i => i.Label == "cuda_runtime.h"));
            Assert.IsTrue(items.Any(i => i.Label == "vector"));
        }

        private static string FindFixtureCMake()
        {
            foreach (string start in new[] { Environment.CurrentDirectory, AppDomain.CurrentDomain.BaseDirectory })
            {
                string dir = Path.GetFullPath(start);
                while (!string.IsNullOrEmpty(dir))
                {
                    string candidate = Path.Combine(dir, "tests", "fixtures", "cuda",
                        "CudaCppMultiPackageWorkspace", "CMakeLists.txt");
                    if (File.Exists(candidate)) return candidate;
                    DirectoryInfo parent = Directory.GetParent(dir);
                    dir = parent == null ? null : parent.FullName;
                }
            }
            Assert.Fail("CudaCppMultiPackageWorkspace fixture was not found");
            return null;
        }
    }
}
