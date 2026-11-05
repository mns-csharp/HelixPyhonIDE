namespace HelixPythonNamespace.Editor.Tests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Editor.Navigation;

    [TestClass]
    public class ScopeIndexTests
    {
        private const string Src =
            "import os\n" +          // 1
            "x = 1\n" +              // 2
            "def foo():\n" +         // 3
            "    y = 2\n" +          // 4
            "    return y\n" +       // 5
            "class Bar:\n" +         // 6
            "    def m(self):\n" +   // 7
            "        return 3\n";    // 8

        [TestMethod]
        public void EnclosingScopeResolvesFunctionBody()
        {
            ScopeIndex idx = new ScopeIndex(Src);
            Scope s = idx.EnclosingScope(Src.IndexOf("y = 2", System.StringComparison.Ordinal));
            Assert.AreEqual(ScopeKind.Function, s.Kind);
            Assert.AreEqual("foo", s.Name);
        }

        [TestMethod]
        public void EnclosingScopeResolvesNestedMethod()
        {
            ScopeIndex idx = new ScopeIndex(Src);
            Scope s = idx.EnclosingScope(Src.IndexOf("return 3", System.StringComparison.Ordinal));
            Assert.AreEqual(ScopeKind.Function, s.Kind);
            Assert.AreEqual("m", s.Name);
        }

        [TestMethod]
        public void ModuleLevelStatementIsModuleScope()
        {
            ScopeIndex idx = new ScopeIndex(Src);
            Scope s = idx.EnclosingScope(Src.IndexOf("x = 1", System.StringComparison.Ordinal));
            Assert.AreEqual(ScopeKind.Module, s.Kind);
        }

        [TestMethod]
        public void CollectsModuleScopeNames()
        {
            ScopeIndex idx = new ScopeIndex(Src);
            Assert.IsTrue(idx.IsModuleScopeName("os"));   // import
            Assert.IsTrue(idx.IsModuleScopeName("x"));    // assignment
            Assert.IsTrue(idx.IsModuleScopeName("foo"));  // def
            Assert.IsTrue(idx.IsModuleScopeName("Bar"));  // class
            Assert.IsFalse(idx.IsModuleScopeName("y"));   // local
            Assert.IsFalse(idx.IsModuleScopeName("m"));   // method, not module-level
        }

        [TestMethod]
        public void ImportAliasIsModuleName()
        {
            ScopeIndex idx = new ScopeIndex("import numpy as np\nfrom os import path as p\n");
            Assert.IsTrue(idx.IsModuleScopeName("np"));
            Assert.IsTrue(idx.IsModuleScopeName("p"));
            Assert.IsFalse(idx.IsModuleScopeName("numpy")); // aliased away
        }

        [TestMethod]
        public void WildcardImportMakesEveryNameModuleScope()
        {
            ScopeIndex idx = new ScopeIndex("from os import *\n");
            Assert.IsTrue(idx.IsModuleScopeName("anything_at_all"));
        }

        [TestMethod]
        public void TripleQuotedStringContentIsNotAScope()
        {
            string src =
                "s = \"\"\"\n" +
                "def not_real():\n" +
                "    pass\n" +
                "\"\"\"\n" +
                "def real():\n" +
                "    pass\n";
            ScopeIndex idx = new ScopeIndex(src);
            Assert.IsFalse(idx.IsModuleScopeName("not_real"));
            Assert.IsTrue(idx.IsModuleScopeName("real"));
            Assert.IsTrue(idx.IsModuleScopeName("s"));
        }
    }
}
