using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.CudaVirtualCompiler;
using Cvc = HelixPythonNamespace.CudaVirtualCompiler.CudaVirtualCompiler;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// CU2101 (SRS §10 semantic analysis, loop 520): use of an undefined name in a
    /// function body is a WARNING — conservative by design: qualified names,
    /// member accesses, ALL_CAPS macro-convention identifiers, __-intrinsics, and
    /// any TU with unresolved system includes are all skipped.
    /// </summary>
    [TestClass]
    public class CudaNameResolutionTests
    {
        private static CudaCompileResult Compile(string source)
        {
            return Cvc.CompileSource(source, "k.cu");
        }

        private static int CountCu2101(CudaCompileResult result)
        {
            return result.Diagnostics.Count(d => d.Code == CudaDiagnosticCodes.SemUndefinedName);
        }

        [TestMethod]
        public void UndefinedName_ReportsCu2101Warning()
        {
            var result = Compile(
                "__global__ void k(int* o) { o[0] = missing_value; }\n");
            Assert.AreEqual(1, CountCu2101(result), "one undefined name, one warning");
            var d = result.Diagnostics.First(x => x.Code == CudaDiagnosticCodes.SemUndefinedName);
            StringAssert.Contains(d.Message, "missing_value");
            Assert.AreEqual(global::HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Warning, d.Severity,
                "conservative: a warning, never an error");
        }

        [TestMethod]
        public void DefinedNames_ProduceNoCu2101()
        {
            var result = Compile(
                "enum Mode { Fast, Slow };\n" +
                "__device__ int helper(int v) { return v + Fast; }\n" +
                "__global__ void k(int* o, int n) {\n" +
                "    int idx = threadIdx.x;\n" +
                "    int doubled = idx * 2;\n" +
                "    o[idx] = helper(doubled) + n;\n" +
                "}\n");
            Assert.AreEqual(0, CountCu2101(result),
                "params, locals, functions, builtins, and enum constants all resolve");
        }

        [TestMethod]
        public void MacroConvention_Qualified_AndIntrinsics_AreSkipped()
        {
            var result = Compile(
                "__global__ void k(int* o) {\n" +
                "    o[0] = MY_MACRO_VALUE;\n" +          // ALL_CAPS: function-like macros not expanded
                "    o[1] = std::size_val;\n" +           // qualified: namespaces unprovable
                "    o[2] = __special_intrinsic(1);\n" +  // __-prefix: intrinsic convention
                "}\n");
            Assert.AreEqual(0, CountCu2101(result));
        }

        [TestMethod]
        public void UnresolvedSystemInclude_DisablesThePass()
        {
            var result = Compile(
                "#include <vector>\n" +
                "__global__ void k(int* o) { o[0] = anything_at_all; }\n");
            Assert.AreEqual(0, CountCu2101(result),
                "absent std headers make absence unprovable — the pass must stand down");
        }

        [TestMethod]
        public void DuplicateUses_ReportOnce_PerFunction()
        {
            var result = Compile(
                "__global__ void k(int* o) { o[0] = ghost; o[1] = ghost + ghost; }\n");
            Assert.AreEqual(1, CountCu2101(result), "one name, one report");
        }

        // ------------------------------------------------- ordered block scoping

        [TestMethod]
        public void UseBeforeDeclaration_Warns()
        {
            var result = Compile(
                "__global__ void k(int* o) {\n" +
                "    o[0] = late;\n" +
                "    int late = 1;\n" +
                "}\n");
            Assert.AreEqual(1, CountCu2101(result));
            var d = result.Diagnostics.First(x => x.Code == CudaDiagnosticCodes.SemUndefinedName);
            StringAssert.Contains(d.Message, "before its declaration");
        }

        [TestMethod]
        public void SiblingBlockLeakage_Warns()
        {
            var result = Compile(
                "__global__ void k(int* o, int n) {\n" +
                "    if (n > 0) { int t = 1; o[0] = t; }\n" +
                "    else { o[1] = t; }\n" +
                "}\n");
            Assert.AreEqual(1, CountCu2101(result), "t does not leak into the sibling else block");
        }

        [TestMethod]
        public void EnclosingBlockDecls_AndForInit_Resolve()
        {
            var result = Compile(
                "__global__ void k(int* o, int n) {\n" +
                "    int base_val = 2;\n" +
                "    for (int i = 0; i < n; ++i) {\n" +
                "        if (i > 0) { o[i] = base_val + i; }\n" +
                "    }\n" +
                "    while (n > 0) { o[0] = base_val; n = n - 1; }\n" +
                "}\n");
            Assert.AreEqual(0, CountCu2101(result),
                "outer locals and the for-init variable are visible in nested blocks");
        }

        [TestMethod]
        public void SelfReferencingInitializer_DoesNotWarn()
        {
            var result = Compile(
                "__global__ void k(int* o) { int x = x; o[0] = x; }\n");
            Assert.AreEqual(0, CountCu2101(result),
                "C++ puts the name in scope inside its own initializer");
        }
    }
}
