using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler;
using HelixPythonNamespace.VirtualCompiler.Ast;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class PipelineSmokeTests
    {
        [TestMethod]
        public void EmptyModule_Produces_NoDiagnostics()
        {
            var result = VirtualCompiler.CompileSource("", "empty.py");
            Assert.IsNotNull(result.AstModule);
            Assert.IsFalse(result.HasErrors);
        }

        [TestMethod]
        public void TrivialPassModule_Lowers_ToPassStmt()
        {
            var result = VirtualCompiler.CompileSource("pass\n", "t.py");
            Assert.IsNotNull(result.AstModule);
            var mod = (Module)result.AstModule;
            Assert.AreEqual(1, mod.Body.Count);
            Assert.IsInstanceOfType(mod.Body[0], typeof(Pass));
            Assert.IsFalse(result.HasErrors);
        }

        [TestMethod]
        public void SimpleFunction_Lowers_ToFunctionDef()
        {
            const string src = "def f(x):\n    return x\n";
            var result = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsNotNull(result.AstModule);
            var mod = (Module)result.AstModule;
            Assert.AreEqual(1, mod.Body.Count);
            var fd = mod.Body[0] as FunctionDef;
            Assert.IsNotNull(fd, "Expected FunctionDef, got " + mod.Body[0].GetType().Name);
            Assert.AreEqual("f", fd.Name);
            Assert.AreEqual(1, fd.Args.Args.Count);
            Assert.AreEqual("x", fd.Args.Args[0].Name);
            Assert.AreEqual(1, fd.Body.Count);
            Assert.IsInstanceOfType(fd.Body[0], typeof(Return));
        }

        [TestMethod]
        public void SimpleClass_Lowers_ToClassDef()
        {
            const string src = "class C(Base):\n    def m(self): pass\n";
            var result = VirtualCompiler.CompileSource(src, "t.py");
            var mod = (Module)result.AstModule;
            Assert.AreEqual(1, mod.Body.Count);
            var cd = mod.Body[0] as ClassDef;
            Assert.IsNotNull(cd);
            Assert.AreEqual("C", cd.Name);
            Assert.AreEqual(1, cd.Bases.Count);
            Assert.IsInstanceOfType(cd.Bases[0], typeof(Name));
            Assert.AreEqual("Base", ((Name)cd.Bases[0]).Id);
            Assert.AreEqual(1, cd.Body.Count);
            Assert.IsInstanceOfType(cd.Body[0], typeof(FunctionDef));
        }

        [TestMethod]
        public void Import_Lowers_ToImport()
        {
            var result = VirtualCompiler.CompileSource("import os\n", "t.py");
            var mod = (Module)result.AstModule;
            Assert.AreEqual(1, mod.Body.Count);
            var imp = mod.Body[0] as Import;
            Assert.IsNotNull(imp);
            Assert.AreEqual(1, imp.Names.Count);
            Assert.AreEqual("os", imp.Names[0].Name);
        }

        [TestMethod]
        public void FromImport_Lowers_ToImportFrom()
        {
            var result = VirtualCompiler.CompileSource("from pkg.sub import foo as f, bar\n", "t.py");
            var mod = (Module)result.AstModule;
            var imp = (ImportFrom)mod.Body[0];
            Assert.AreEqual("pkg.sub", imp.Module);
            Assert.AreEqual(0, imp.Level);
            Assert.AreEqual(2, imp.Names.Count);
            Assert.AreEqual("foo", imp.Names[0].Name);
            Assert.AreEqual("f", imp.Names[0].AsName);
            Assert.AreEqual("bar", imp.Names[1].Name);
            Assert.IsNull(imp.Names[1].AsName);
        }

        [TestMethod]
        public void ElifChain_Flattens_ToNestedIf_InOrElse()
        {
            const string src = "if a:\n    x=1\nelif b:\n    x=2\nelse:\n    x=3\n";
            var result = VirtualCompiler.CompileSource(src, "t.py");
            var mod = (Module)result.AstModule;
            var outerIf = (If)mod.Body[0];
            Assert.AreEqual(1, outerIf.OrElse.Count);
            var elif = outerIf.OrElse[0] as If;
            Assert.IsNotNull(elif, "elif should lower to nested If inside orelse");
            Assert.AreEqual(1, elif.OrElse.Count);  // final else body
        }

        [TestMethod]
        public void AnnotatedAssignment_LowersTo_AnnAssign()
        {
            var result = VirtualCompiler.CompileSource("x: int = 1\n", "t.py");
            var mod = (Module)result.AstModule;
            Assert.IsInstanceOfType(mod.Body[0], typeof(AnnAssign));
        }

        [TestMethod]
        public void AugmentedAssignment_LowersTo_AugAssign_WithCorrectOp()
        {
            var result = VirtualCompiler.CompileSource("x += 1\n", "t.py");
            var aa = (AugAssign)((Module)result.AstModule).Body[0];
            Assert.AreEqual(BinOp.Add, aa.Op);
        }

        [TestMethod]
        public void AssignToCallExpr_FlaggedBy_AstValidator()
        {
            // `f() = 1` is a classic invalid-target case that CPython flags.
            const string src = "f() = 1\n";
            var result = VirtualCompiler.CompileSource(src, "t.py");
            // Either the parser emits a syntax error or the validator does. We accept either,
            // but require at least one diagnostic with severity >= Error.
            Assert.IsTrue(result.HasErrors,
                "Expected at least one error for assigning to a call expression; got none.");
        }

        [TestMethod]
        public void DuplicateParameter_Flagged()
        {
            const string src = "def f(x, x): pass\n";
            var result = VirtualCompiler.CompileSource(src, "t.py");
            bool hasDup = result.Diagnostics.Any(d =>
                d.Code == VirtualCompilerDiagnosticCodes.SymDuplicateParameter);
            Assert.IsTrue(hasDup,
                "Expected duplicate-parameter diagnostic; got: " +
                string.Join(", ", result.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        }

        [TestMethod]
        public void SyntaxError_Propagates_AsCompileDiagnostic()
        {
            // Unterminated string should cause the lexer to emit a diagnostic that propagates
            // through to the CompileResult.
            const string src = "x = \"unterminated\n";
            var result = VirtualCompiler.CompileSource(src, "t.py");
            Assert.IsTrue(result.Diagnostics.Count > 0);
        }
    }
}
