namespace HelixPythonNamespace.DeadCode.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.DeadCode;
    using HelixPythonNamespace.Linker;
    using HelixPythonNamespace.Python;
    using AstModule = HelixPythonNamespace.VirtualCompiler.Ast.Module;
    using CstToAst = HelixPythonNamespace.VirtualCompiler.Ast.CstToAst;

    [TestClass]
    public class DeadCodeTests
    {
        private static CompileFileResult F(string fqn, string path, string src)
        {
            AstModule mod = CstToAst.Lower(Python.ParseModule(src, path).Module);
            return new CompileFileResult(mod, fqn, path);
        }

        private static DeadCodeReport Run(params CompileFileResult[] files)
        {
            List<CompileFileResult> list = new List<CompileFileResult>(files);
            LinkResult link = PythonLinker.Link(list);
            return new DeadCodeEngine().Analyze(list, link);
        }

        private static bool Has(DeadCodeReport r, DeadCodeKind kind, string symbol)
        {
            foreach (DeadCodeFinding f in r.Findings)
            {
                if (f.Kind == kind && f.SymbolName == symbol) return true;
            }
            return false;
        }

        private static bool HasKind(DeadCodeReport r, DeadCodeKind kind)
        {
            foreach (DeadCodeFinding f in r.Findings)
            {
                if (f.Kind == kind) return true;
            }
            return false;
        }

        [TestMethod]
        public void UnusedTopLevelFunctionFlagged()
        {
            DeadCodeReport r = Run(F("m", "m.py", "def orphan():\n    pass\n"));
            Assert.IsTrue(Has(r, DeadCodeKind.UnusedFunction, "orphan"));
        }

        [TestMethod]
        public void UsedCrossModuleFunctionNotFlagged()
        {
            DeadCodeReport r = Run(
                F("a", "a.py", "def foo():\n    pass\n"),
                F("b", "b.py", "from a import foo\n\nfoo()\n"));
            Assert.IsFalse(Has(r, DeadCodeKind.UnusedFunction, "foo"));
        }

        [TestMethod]
        public void FunctionUsedViaModuleAttribute_NotFlagged()
        {
            // `import a as _layout; _layout.child_env()` — reached through module ATTRIBUTE
            // access, not a direct name import, so it's absent from the linker's name
            // reference index. Any `.NAME` access in the solution must count as a use
            // (child_env / insert_sys_path were false-flagged on fixture (a)).
            DeadCodeReport r = Run(
                F("a", "a.py", "def child_env():\n    return 1\n"),
                F("b", "b.py", "import a as _layout\n\n_layout.child_env()\n"));
            Assert.IsFalse(Has(r, DeadCodeKind.UnusedFunction, "child_env"),
                "a function reached via module.attr must not be flagged dead");
        }

        [TestMethod]
        public void TrulyOrphanFunction_StillFlagged_AfterAttrRule()
        {
            // Guard: the attribute-use rule must not silence a genuinely dead function
            // whose name never appears as any `.NAME` access.
            DeadCodeReport r = Run(
                F("a", "a.py", "def never_referenced_anywhere_xyz():\n    return 1\n"),
                F("b", "b.py", "x = 1\nprint(x)\n"));
            Assert.IsTrue(Has(r, DeadCodeKind.UnusedFunction, "never_referenced_anywhere_xyz"),
                "a truly orphan function must still be flagged");
        }

        [TestMethod]
        public void UnusedImportFlagged()
        {
            DeadCodeReport r = Run(F("m", "m.py", "import os\n\nx = 1\nprint(x)\n"));
            Assert.IsTrue(Has(r, DeadCodeKind.UnusedImport, "os"));
        }

        [TestMethod]
        public void UsedImportNotFlagged()
        {
            DeadCodeReport r = Run(F("m", "m.py", "import os\n\nprint(os.getcwd())\n"));
            Assert.IsFalse(Has(r, DeadCodeKind.UnusedImport, "os"));
        }

        // Regression: a name used ONLY in a PEP 695 type-parameter bound counts as used. The
        // DeadWalker didn't walk FunctionDef/ClassDef type-param bounds, so the import looked unused.
        [TestMethod]
        public void ImportUsedOnlyInTypeParamBoundNotFlagged()
        {
            DeadCodeReport r = Run(F("m", "m.py",
                "from typing import Sequence\n\n\ndef first[T: Sequence](items: T):\n    return items[0]\n"));
            Assert.IsFalse(Has(r, DeadCodeKind.UnusedImport, "Sequence"),
                "import used only in a type-param bound must not be flagged unused");
        }

        [TestMethod]
        public void TestPrefixedFunctionIsLive()
        {
            DeadCodeReport r = Run(F("m", "m.py", "def test_something():\n    pass\n"));
            Assert.IsFalse(Has(r, DeadCodeKind.UnusedFunction, "test_something"));
        }

        [TestMethod]
        public void AllExportKeepsSymbolLive()
        {
            DeadCodeReport r = Run(F("m", "m.py", "def api():\n    pass\n__all__ = ['api']\n"));
            Assert.IsFalse(Has(r, DeadCodeKind.UnusedFunction, "api"));
        }

        [TestMethod]
        public void UnreachableAfterReturnFlagged()
        {
            DeadCodeReport r = Run(F("m", "m.py", "def f():\n    return 1\n    x = 2\n"));
            Assert.IsTrue(HasKind(r, DeadCodeKind.UnreachableCode));
        }

        [TestMethod]
        public void UnusedLocalFlagged()
        {
            DeadCodeReport r = Run(F("m", "m.py", "def f():\n    unusedvar = 1\n    return 2\n"));
            Assert.IsTrue(Has(r, DeadCodeKind.UnusedVariable, "unusedvar"));
        }

        [TestMethod]
        public void UsedLocalNotFlagged()
        {
            DeadCodeReport r = Run(F("m", "m.py", "def f():\n    used = 1\n    return used\n"));
            Assert.IsFalse(Has(r, DeadCodeKind.UnusedVariable, "used"));
        }

        [TestMethod]
        public void UnusedMethodFlaggedOnLiveClass()
        {
            // Class kept live via __all__; its method is never called anywhere.
            DeadCodeReport r = Run(F("m", "m.py",
                "class Service:\n" +
                "    def ghost(self):\n" +
                "        pass\n" +
                "__all__ = ['Service']\n"));
            Assert.IsTrue(Has(r, DeadCodeKind.UnusedMethod, "Service.ghost"));
        }

        [TestMethod]
        public void DunderMethodNotFlagged()
        {
            DeadCodeReport r = Run(F("m", "m.py",
                "class Service:\n" +
                "    def __init__(self):\n" +
                "        pass\n" +
                "__all__ = ['Service']\n"));
            Assert.IsFalse(Has(r, DeadCodeKind.UnusedMethod, "Service.__init__"));
        }

        [TestMethod]
        public void ProtocolClassNotFlaggedAsUnused()
        {
            DeadCodeReport r = Run(F("m", "m.py",
                "from typing import Protocol\n" +
                "class Drawable(Protocol):\n" +
                "    def draw(self):\n" +
                "        ...\n"));
            Assert.IsFalse(Has(r, DeadCodeKind.UnusedClass, "Drawable"));
        }
    }
}
