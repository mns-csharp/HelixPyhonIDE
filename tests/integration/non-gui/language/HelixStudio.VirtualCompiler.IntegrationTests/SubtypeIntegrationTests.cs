using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Phase-3.0 finish: end-to-end verification that SubtypeChecker is wired
    /// into the TypeInferencer — arg/return type mismatches produce PY6003 /
    /// PY6005 with the right leniency (Any / Unknown / imports → silent).
    /// </summary>
    [TestClass]
    public class SubtypeIntegrationTests
    {
        private static bool Has(CompileResult r, string code)
        {
            return r.Diagnostics.Any(d => d.Code == code);
        }

        // ---------- PY6003 arg-type mismatch ----------

        [TestMethod]
        public void ArgType_Mismatch_IntParam_GivenStr_Flagged()
        {
            const string src = "def f(x: int): pass\nf(\"hello\")\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void ArgType_Compatible_NotFlagged()
        {
            const string src = "def f(x: int): pass\nf(42)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch));
        }

        [TestMethod]
        public void ArgType_BoolPassedAsInt_NotFlagged()
        {
            // bool is-a int in Python — base chain in bundled builtins stub.
            const string src = "def f(x: int): pass\nf(True)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch),
                "bool is a subclass of int; should not flag");
        }

        [TestMethod]
        public void ArgType_AnyParam_AcceptsAnything()
        {
            const string src = "def f(x): pass\nf(\"anything\")\nf(42)\nf(None)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch));
        }

        [TestMethod]
        public void KeywordArg_Mismatch_Flagged()
        {
            const string src = "def f(x: int, y: str): pass\nf(1, y=99)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch));
        }

        [TestMethod]
        public void UnionParam_AcceptsEitherMember()
        {
            const string src = "def f(x: Union[int, str]): pass\nf(1)\nf(\"hi\")\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch));
        }

        [TestMethod]
        public void OptionalParam_AcceptsNone()
        {
            const string src = "def f(x: Optional[int]): pass\nf(None)\nf(42)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch));
        }

        [TestMethod]
        public void ImportedFunctionCall_DoesNotFlag_ArgType()
        {
            // Imported names are Any-typed → any-arg flow
            const string src = "from os import getcwd\ngetcwd(\"unexpected\")\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch));
        }

        // ---------- PY6005 return-type mismatch ----------

        [TestMethod]
        public void ReturnType_Mismatch_Flagged()
        {
            const string src = "def f() -> int:\n    return \"hello\"\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch));
        }

        [TestMethod]
        public void ReturnType_Match_NotFlagged()
        {
            const string src = "def f() -> int:\n    return 42\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch));
        }

        [TestMethod]
        public void ReturnType_UnannotatedFunction_NotChecked()
        {
            const string src = "def f():\n    return \"anything\"\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch));
        }

        [TestMethod]
        public void ReturnType_NoneAnnotated_AllowsBareReturn()
        {
            const string src = "def f() -> None:\n    return\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch));
        }

        [TestMethod]
        public void ReturnType_NoneAnnotated_ValueReturn_Flagged()
        {
            const string src = "def f() -> None:\n    return 42\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch));
        }

        [TestMethod]
        public void ReturnType_UnionAnnotated_AcceptsAllMembers()
        {
            const string src =
                "def f(x: int) -> Union[int, str]:\n" +
                "    if x > 0:\n" +
                "        return 1\n" +
                "    return \"neg\"\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch));
        }
    }
}
