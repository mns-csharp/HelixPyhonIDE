using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Phase-1.2 coverage: AstValidator extensions (reserved-name assignment,
    /// multiple starred, cannot-delete-literal) plus the new CompileValidator
    /// (async context, global/nonlocal ordering, annotated name conflicts).
    /// </summary>
    [TestClass]
    public class CompileValidatorTests
    {
        private static bool Has(CompileResult r, string code)
        {
            return r.Diagnostics.Any(d => d.Code == code);
        }

        // ---------- Reserved-name assignment / deletion (AstValidator) ----------

        [TestMethod]
        public void Assign_To_Underscore_DebugUnderscore_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("__debug__ = 1\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.AstAssignToReserved));
        }

        [TestMethod]
        public void Assign_To_None_IsFlagged()
        {
            // `None = 1` either becomes an AST assign to Constant(None) (flagged as reserved)
            // or a parser error. Either way we expect at least one Error-severity diagnostic.
            var r = VirtualCompiler.CompileSource("None = 1\n");
            Assert.IsTrue(r.HasErrors);
        }

        [TestMethod]
        public void Assign_To_Regular_Name_IsFine()
        {
            var r = VirtualCompiler.CompileSource("x = 1\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.AstAssignToReserved));
        }

        [TestMethod]
        public void Delete_Literal_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("del 1\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.AstCannotDeleteLiteral)
                          || r.HasErrors, "expected cannot-delete-literal or other syntax error");
        }

        [TestMethod]
        public void Multiple_Starred_In_Tuple_Target_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("*a, *b = [1, 2, 3]\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.AstMultipleStarredInAssign));
        }

        [TestMethod]
        public void Single_Starred_In_Tuple_Target_IsFine()
        {
            var r = VirtualCompiler.CompileSource("a, *b, c = [1, 2, 3, 4]\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.AstMultipleStarredInAssign));
        }

        // ---------- Async context (CompileValidator) ----------

        [TestMethod]
        public void Await_Outside_Async_IsFlagged()
        {
            const string src = "def f():\n    await foo()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompAwaitOutsideAsync));
        }

        [TestMethod]
        public void Await_Inside_Async_IsFine()
        {
            const string src = "async def f():\n    await foo()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.CompAwaitOutsideAsync));
        }

        [TestMethod]
        public void Await_At_Module_Level_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("await foo()\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompAwaitOutsideAsync));
        }

        [TestMethod]
        public void AsyncFor_Outside_Async_IsFlagged()
        {
            const string src = "def f():\n    async for x in it:\n        pass\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompAsyncForOutsideAsync));
        }

        [TestMethod]
        public void AsyncFor_Inside_Async_IsFine()
        {
            const string src = "async def f():\n    async for x in it:\n        pass\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.CompAsyncForOutsideAsync));
        }

        [TestMethod]
        public void AsyncWith_Outside_Async_IsFlagged()
        {
            const string src = "def f():\n    async with cm() as x:\n        pass\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompAsyncWithOutsideAsync));
        }

        [TestMethod]
        public void AsyncWith_Inside_Async_IsFine()
        {
            const string src = "async def f():\n    async with cm() as x:\n        pass\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.CompAsyncWithOutsideAsync));
        }

        [TestMethod]
        public void Nested_Function_Breaks_AsyncContext()
        {
            // A synchronous def nested inside an async def is NOT in async context.
            const string src =
                "async def outer():\n" +
                "    def inner():\n" +
                "        await foo()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompAwaitOutsideAsync),
                "sync def nested inside async def should not inherit async context");
        }

        [TestMethod]
        public void Async_Comprehension_Outside_Async_IsFlagged()
        {
            // Either the parser rejects the syntax outright or the compile validator flags
            // the async-comprehension context. CPython itself emits "'async for' is outside
            // async function" at compile time only when the parser accepts — older PEP 530
            // behavior.  We accept either outcome as long as something trips an error.
            const string src = "def f():\n    return [x async for x in it]\n";
            var r = VirtualCompiler.CompileSource(src);
            bool flagged = Has(r, VirtualCompilerDiagnosticCodes.CompAsyncComprehensionOutsideAsync) || r.HasErrors;
            Assert.IsTrue(flagged,
                "expected either compile-validator async-comp flag or a parser error; got none");
        }

        // ---------- Global / nonlocal ordering (SymbolTableBuilder) ----------

        [TestMethod]
        public void Global_After_Assign_IsFlagged()
        {
            const string src =
                "def f():\n" +
                "    x = 1\n" +
                "    global x\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompGlobalAfterAssign));
        }

        [TestMethod]
        public void Global_Before_Assign_IsFine()
        {
            const string src =
                "def f():\n" +
                "    global x\n" +
                "    x = 1\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.CompGlobalAfterAssign));
        }

        [TestMethod]
        public void Global_After_Use_IsFlagged()
        {
            const string src =
                "def f():\n" +
                "    print(x)\n" +
                "    global x\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompGlobalAfterAssign));
        }

        [TestMethod]
        public void Nonlocal_After_Assign_IsFlagged()
        {
            const string src =
                "def outer():\n" +
                "    x = 1\n" +
                "    def inner():\n" +
                "        x = 2\n" +
                "        nonlocal x\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompNonlocalAfterAssign));
        }

        // ---------- Annotated name + global/nonlocal conflict ----------

        [TestMethod]
        public void Annotated_Global_IsFlagged()
        {
            const string src =
                "def f():\n" +
                "    global x\n" +
                "    x: int = 1\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompAnnotatedGlobal));
        }

        [TestMethod]
        public void Annotated_Nonlocal_IsFlagged()
        {
            const string src =
                "def outer():\n" +
                "    x = 1\n" +
                "    def inner():\n" +
                "        nonlocal x\n" +
                "        x: int = 2\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompAnnotatedNonlocal));
        }

        [TestMethod]
        public void Plain_Annotation_Without_Global_IsFine()
        {
            const string src = "def f():\n    x: int = 1\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.CompAnnotatedGlobal));
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.CompAnnotatedNonlocal));
        }
    }
}
