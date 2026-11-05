using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// SRS §10 IntelliSense (loop 525): symbol-table-backed Go to Definition for
    /// CUDA-C. Functions prefer the definition over a prototype; types, globals and
    /// enum constants resolve by scope walk; seeded builtins are never targets.
    /// </summary>
    [TestClass]
    public class CudaDefinitionLocatorTests
    {
        private const string Source =
            "struct Params { int n; };\n" +               // line 1
            "__constant__ int scale_factor = 2;\n" +      // line 2
            "enum Mode { Fast, Slow };\n" +               // line 3
            "__global__ void scale_kernel(int* data);\n" + // line 4 — prototype
            "__global__ void scale_kernel(int* data) {\n" + // line 5 — definition
            "    data[threadIdx.x] *= scale_factor;\n" +
            "}\n";

        [TestMethod]
        public void Function_PrefersDefinitionOverPrototype()
        {
            var loc = CudaDefinitionLocator.Locate(Source, "k.cu", "scale_kernel");
            Assert.IsNotNull(loc);
            Assert.AreEqual(5, loc.Line, "the body-bearing declaration wins");
            Assert.AreEqual("function", loc.SymbolKind);
        }

        [TestMethod]
        public void Type_Global_AndEnumConstant_Resolve()
        {
            var type = CudaDefinitionLocator.Locate(Source, "k.cu", "Params");
            Assert.IsNotNull(type);
            Assert.AreEqual(1, type.Line);

            var global = CudaDefinitionLocator.Locate(Source, "k.cu", "scale_factor");
            Assert.IsNotNull(global);
            Assert.AreEqual(2, global.Line);

            var enumConst = CudaDefinitionLocator.Locate(Source, "k.cu", "Fast");
            Assert.IsNotNull(enumConst);
            Assert.AreEqual(3, enumConst.Line);
        }

        [TestMethod]
        public void SeededBuiltins_AreNotNavigationTargets()
        {
            Assert.IsNull(CudaDefinitionLocator.Locate(Source, "k.cu", "threadIdx"));
            Assert.IsNull(CudaDefinitionLocator.Locate(Source, "k.cu", "printf"));
            Assert.IsNull(CudaDefinitionLocator.Locate(Source, "k.cu", "cudaMalloc"));
        }

        [TestMethod]
        public void UnknownToken_ReturnsNull()
        {
            Assert.IsNull(CudaDefinitionLocator.Locate(Source, "k.cu", "no_such_thing"));
            Assert.IsNull(CudaDefinitionLocator.Locate("", "k.cu", "x"));
            Assert.IsNull(CudaDefinitionLocator.Locate(Source, "k.cu", ""));
        }
    }
}
