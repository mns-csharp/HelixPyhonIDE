using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Tests for the symtable port (<see cref="VirtualCompiler.Symtable.SymbolTableBuilder"/>
    /// and <see cref="VirtualCompiler.Symtable.NameResolver"/>). Each test exercises a
    /// specific scoping or context rule CPython enforces at compile time.
    /// </summary>
    [TestClass]
    public class SymtableTests
    {
        private static bool Has(CompileResult r, string code)
        {
            return r.Diagnostics.Any(d => d.Code == code);
        }

        [TestMethod]
        public void Builtins_Are_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource("print(len(range(10)))\n");
            Assert.IsFalse(r.HasErrors, "builtins should resolve; got: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void Undefined_Global_Name_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("x = undef_name\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName),
                "expected undefined-name; got: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void Locally_Bound_Name_IsNot_Flagged()
        {
            var r = VirtualCompiler.CompileSource("x = 1\nprint(x)\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void Function_Parameter_Resolves()
        {
            var r = VirtualCompiler.CompileSource("def f(x): return x\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void Function_Undefined_Local_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("def f(): return y\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void Closure_From_Enclosing_Function_Resolves()
        {
            const string src = "def outer():\n    z = 1\n    def inner(): return z\n    return inner\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName),
                "closure capture across enclosing function should resolve; got: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void Class_Body_Name_Not_Visible_In_Method()
        {
            // Python's class-scope rule: names in class body are NOT inherited by methods.
            const string src = "class C:\n    helper = 10\n    def m(self):\n        return helper\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName),
                "methods cannot see names defined in the enclosing class body");
        }

        [TestMethod]
        public void Class_Body_Can_See_Its_Own_Names()
        {
            const string src = "class C:\n    a = 1\n    b = a + 1\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void Return_Outside_Function_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("return 42\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompReturnOutsideFunction));
        }

        [TestMethod]
        public void Yield_Outside_Function_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("yield 42\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompYieldOutsideFunction));
        }

        [TestMethod]
        public void Break_Outside_Loop_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("def f():\n    break\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompBreakOutsideLoop));
        }

        [TestMethod]
        public void Break_Inside_Loop_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource("while True:\n    break\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.CompBreakOutsideLoop));
        }

        [TestMethod]
        public void Continue_Outside_Loop_IsFlagged()
        {
            var r = VirtualCompiler.CompileSource("def f():\n    continue\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.CompContinueOutsideLoop));
        }

        [TestMethod]
        public void Nonlocal_Without_Binding_IsFlagged()
        {
            const string src = "def f():\n    nonlocal nosuch\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.SymNonlocalWithoutBinding));
        }

        [TestMethod]
        public void Nonlocal_With_Enclosing_Binding_NotFlagged()
        {
            const string src =
                "def outer():\n" +
                "    x = 1\n" +
                "    def inner():\n" +
                "        nonlocal x\n" +
                "        x = 2\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymNonlocalWithoutBinding));
        }

        [TestMethod]
        public void Global_Before_Assign_Is_NotFlagged()
        {
            // `global x` followed by `x = 1` is the canonical way to declare a global
            // write in a function. CPython accepts this without error.
            const string src =
                "def f():\n" +
                "    global x\n" +
                "    x = 1\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymGlobalAndLocal));
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.CompGlobalAfterAssign));
        }

        [TestMethod]
        public void Import_Binds_Name()
        {
            var r = VirtualCompiler.CompileSource("import os\nprint(os)\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void Dotted_Import_Binds_TopLevel_Only()
        {
            // `import a.b` binds `a`, not `b`.
            var r = VirtualCompiler.CompileSource("import os.path\nprint(os)\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));

            var r2 = VirtualCompiler.CompileSource("import os.path\nprint(path)\n");
            Assert.IsTrue(Has(r2, VirtualCompilerDiagnosticCodes.SymUndefinedName),
                "`import os.path` should NOT bind `path` at module level");
        }

        [TestMethod]
        public void FromImport_With_Alias_Binds_AsName()
        {
            var r = VirtualCompiler.CompileSource("from os import path as p\nprint(p)\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void Comprehension_Target_IsLocalTo_Comprehension()
        {
            // `[x for x in range(3)]` — the `x` is local to the comprehension.
            // Using `x` after doesn't leak.
            const string src = "result = [x for x in range(3)]\nprint(x)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName),
                "Python 3 comprehensions do not leak their iteration variable");
        }

        [TestMethod]
        public void Lambda_Parameter_Resolves()
        {
            var r = VirtualCompiler.CompileSource("f = lambda x: x + 1\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void Duplicate_Parameter_Flagged_Once()
        {
            var r = VirtualCompiler.CompileSource("def f(x, x): pass\n");
            int count = r.Diagnostics.Count(d => d.Code == VirtualCompilerDiagnosticCodes.SymDuplicateParameter);
            Assert.IsTrue(count >= 1);
        }

        [TestMethod]
        public void Except_As_Binds_Name_In_Handler()
        {
            const string src = "try:\n    pass\nexcept Exception as e:\n    print(e)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void With_As_Binds_Name_In_Body()
        {
            const string src = "with open('f') as fh:\n    fh.read()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void For_Loop_Target_Binds()
        {
            const string src = "for i in range(3):\n    print(i)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void AugAssign_NotFlagged_AtCompileTime()
        {
            // CPython raises UnboundLocalError at runtime for augmented-assign with no prior
            // binding — compile-time passes accept it. Our symtable mirrors that behavior:
            // the target counts as a local binding (DefLocal) despite the implicit read.
            var r = VirtualCompiler.CompileSource("x += 1\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.SymUndefinedName),
                "augmented-assign isn't a compile-time error; runtime handles it");
        }

        [TestMethod]
        public void LazyGlobalAssignmentCreatesModuleBinding()
        {
            const string src =
                "def initialize():\n" +
                "    global _cache\n" +
                "    _cache = []\n" +
                "def use():\n" +
                "    return _cache[0]\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(r.Diagnostics.Any(d =>
                    d.Code == VirtualCompilerDiagnosticCodes.SymUndefinedName
                    && d.Message.Contains("_cache")),
                string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        }
    }
}
