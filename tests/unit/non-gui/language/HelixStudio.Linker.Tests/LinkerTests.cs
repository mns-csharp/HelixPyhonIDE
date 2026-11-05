namespace HelixPythonNamespace.Linker.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Linker;
    using HelixPythonNamespace.Python;
    using AstModule = HelixPythonNamespace.VirtualCompiler.Ast.Module;
    using CstToAst = HelixPythonNamespace.VirtualCompiler.Ast.CstToAst;

    [TestClass]
    public class LinkerTests
    {
        private static CompileFileResult File(string fqn, string path, string src)
        {
            AstModule mod = CstToAst.Lower(Python.ParseModule(src, path).Module);
            return new CompileFileResult(mod, fqn, path);
        }

        private static HashSet<string> Codes(LinkResult r)
        {
            HashSet<string> codes = new HashSet<string>();
            foreach (LinkDiagnostic d in r.Diagnostics) codes.Add(d.Code);
            return codes;
        }

        // ---- module table ----

        [TestMethod]
        public void ModuleTableRecordsDefsAndAll()
        {
            LinkResult r = PythonLinker.Link(new List<CompileFileResult>
            {
                File("m", "m.py",
                    "import os\n" +
                    "def foo():\n    pass\n" +
                    "class Bar:\n    pass\n" +
                    "baz = 1\n" +
                    "__all__ = ['foo', 'Bar']\n"),
            });
            ModuleSummary m = r.Modules["m"];
            Assert.IsTrue(m.Bindings.ContainsKey("foo"));
            Assert.AreEqual(BindingKind.Function, m.Bindings["foo"].Kind);
            Assert.IsTrue(m.Bindings.ContainsKey("Bar"));
            Assert.AreEqual(BindingKind.Class, m.Bindings["Bar"].Kind);
            Assert.IsTrue(m.Bindings.ContainsKey("baz"));
            Assert.IsTrue(m.Bindings.ContainsKey("os")); // import alias
            Assert.IsTrue(m.HasExplicitAll);
            CollectionAssert.AreEquivalent(new[] { "foo", "Bar" }, m.All);
        }

        // ---- import resolution ----

        [TestMethod]
        public void StdlibImportResolves()
        {
            LinkResult r = PythonLinker.Link(new List<CompileFileResult> { File("m", "m.py", "import os\n") });
            Assert.IsFalse(Codes(r).Contains("PY7001"));
        }

        [TestMethod]
        public void UnknownModuleEmitsPY7001()
        {
            LinkResult r = PythonLinker.Link(new List<CompileFileResult> { File("m", "m.py", "import totally_not_a_module_xyz\n") });
            Assert.IsTrue(Codes(r).Contains("PY7001"));
        }

        [TestMethod]
        public void NamespacePackageImport_ResolvesByPathSuffix()
        {
            // Namespace-package (PEP 420) layout: dataset.py's __init__-chain FQN is only
            // 'data.dataset' (its sample_project/ parent has no __init__.py), yet it is imported as
            // 'sample_project.data.dataset'. The linker must resolve via the file-path suffix rather
            // than emit a phantom PY7001 (fixture (b): sample_project.data.dataset etc.).
            LinkResult r = PythonLinker.Link(new List<CompileFileResult> {
                File("data.dataset", "root/sample_project/data/dataset.py", "FEATURE_COLS = [1, 2, 3]\n"),
                File("app", "root/app.py", "from sample_project.data.dataset import FEATURE_COLS\n") });
            Assert.IsFalse(Codes(r).Contains("PY7001"),
                "namespace-package import must resolve by path suffix; codes: " + string.Join(",", Codes(r)));
        }

        [TestMethod]
        public void UbiquitousThirdPartyImportsResolve()
        {
            // numpy/torch/etc. are installed & importable by the real interpreter, so the
            // linker must NOT hard-error PY7001 'no module named numpy' on them (iter 32 —
            // 75+126 phantom errors on the scientific fixtures).
            LinkResult r = PythonLinker.Link(new List<CompileFileResult> {
                File("m", "m.py",
                    "import numpy as np\n" +
                    "import torch\n" +
                    "import cupy\n" +
                    "import matplotlib.pyplot as plt\n" +
                    "import pandas as pd\n" +
                    "from scipy import stats\n") });
            Assert.IsFalse(Codes(r).Contains("PY7001"),
                "ubiquitous third-party imports must not emit PY7001; codes: " +
                string.Join(",", Codes(r)));
        }

        [TestMethod]
        public void CrossModuleImportResolvesAndUnknownNameEmitsPY7002()
        {
            List<CompileFileResult> files = new List<CompileFileResult>
            {
                File("a", "a.py", "def foo():\n    pass\n"),
                File("b", "b.py", "from a import foo, missing\n"),
            };
            HashSet<string> codes = Codes(PythonLinker.Link(files));
            Assert.IsTrue(codes.Contains("PY7002"), "missing name should emit PY7002");
        }

        [TestMethod]
        public void CrossModuleReferenceIsIndexed()
        {
            List<CompileFileResult> files = new List<CompileFileResult>
            {
                File("a", "a.py", "def foo():\n    pass\n"),
                File("b", "b.py", "from a import foo\n\nfoo()\n"),
            };
            LinkResult r = PythonLinker.Link(files);
            IReadOnlyList<UseSite> uses = r.References.Lookup(new DefId { ModuleFqn = "a", Name = "foo" });
            Assert.IsNotNull(uses, "expected a recorded use of a.foo");
            Assert.IsTrue(uses.Count >= 1);
            Assert.AreEqual("b", uses[0].ModuleFqn);
        }

        // ---- cycles ----

        [TestMethod]
        public void CircularImportEmitsPY7004()
        {
            List<CompileFileResult> files = new List<CompileFileResult>
            {
                File("a", "a.py", "import b\n"),
                File("b", "b.py", "import a\n"),
            };
            Assert.IsTrue(Codes(PythonLinker.Link(files)).Contains("PY7004"));
        }

        [TestMethod]
        public void NoCycleForAcyclicGraph()
        {
            List<CompileFileResult> files = new List<CompileFileResult>
            {
                File("a", "a.py", "def x():\n    pass\n"),
                File("b", "b.py", "import a\n"),
            };
            Assert.IsFalse(Codes(PythonLinker.Link(files)).Contains("PY7004"));
        }

        // ---- __all__ ----

        [TestMethod]
        public void UnusedReExportEmitsPY7008()
        {
            LinkResult r = PythonLinker.Link(new List<CompileFileResult>
            {
                File("m", "m.py", "def foo():\n    pass\n__all__ = ['foo', 'ghost']\n"),
            });
            Assert.IsTrue(Codes(r).Contains("PY7008"));
        }

        // ---- relative import semantics (pure) ----

        [TestMethod]
        public void ResolveRelativeBasics()
        {
            bool escapes;
            // from . import x  in pkg/sub.py  => base package "pkg"
            Assert.AreEqual("pkg", ImportResolver.ResolveRelative("pkg.sub", "", 1, false, out escapes));
            Assert.IsFalse(escapes);
            // from .mod import x in pkg/sub.py => "pkg.mod"
            Assert.AreEqual("pkg.mod", ImportResolver.ResolveRelative("pkg.sub", "mod", 1, false, out escapes));
            Assert.IsFalse(escapes);
            // from ..mod import x in pkg.sub.leaf => "pkg.mod"
            Assert.AreEqual("pkg.mod", ImportResolver.ResolveRelative("pkg.sub.leaf", "mod", 2, false, out escapes));
            Assert.IsFalse(escapes);
            // escapes top-level package
            ImportResolver.ResolveRelative("top", "x", 2, false, out escapes);
            Assert.IsTrue(escapes);
        }

        [TestMethod]
        public void RelativeImportInitPyTreatedAsPackage()
        {
            bool escapes;
            // In pkg/__init__.py, "from . import x" stays at "pkg".
            Assert.AreEqual("pkg", ImportResolver.ResolveRelative("pkg", "", 1, true, out escapes));
            Assert.IsFalse(escapes);
        }
    }
}
