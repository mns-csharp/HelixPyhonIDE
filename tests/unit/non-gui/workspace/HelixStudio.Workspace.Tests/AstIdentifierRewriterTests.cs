using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class AstIdentifierRewriterTests
    {
        [TestMethod]
        public void Rewrite_ModuleDefAndCall()
        {
            string src = "def foo():\n    return 1\n\nfoo()\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            // def site + call site.
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("def bar():\n    return 1\n\nbar()\n", r.Text);
        }

        [TestMethod]
        public void FindOccurrences_ReturnsDefAndCallSpans()
        {
            // Same binding logic as Rewrite: the def-name span and the call span, sorted ascending.
            string src = "def foo():\n    return 1\n\nfoo()\n";
            var occ = AstIdentifierRewriter.FindOccurrences(src, "foo");
            Assert.AreEqual(2, occ.Count);
            // 1) `foo` in `def foo` at offset 4..7
            Assert.AreEqual(4, occ[0].start);
            Assert.AreEqual(7, occ[0].end);
            Assert.AreEqual("foo", src.Substring(occ[0].start, occ[0].end - occ[0].start));
            // 2) the trailing `foo()` call — its span text is also exactly `foo`
            Assert.AreEqual("foo", src.Substring(occ[1].start, occ[1].end - occ[1].start));
            Assert.IsTrue(occ[1].start > occ[0].start, "occurrences sorted ascending");
        }

        [TestMethod]
        public void FindOccurrences_AgreesWithRenameHitCount_AndSkipsShadowed()
        {
            // Reference finding must exclude the shadowing parameter, exactly like rename.
            string src = "def foo():\n    return 1\n\ndef g(foo):\n    return foo + 1\n\nfoo()\n";
            var occ = AstIdentifierRewriter.FindOccurrences(src, "foo");
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(r.Count, occ.Count, "FindOccurrences and Rewrite must agree");
            Assert.AreEqual(2, occ.Count); // def foo + trailing call; NOT the parameter or its uses
            foreach (var s in occ)
                Assert.AreEqual("foo", src.Substring(s.start, s.end - s.start));
        }

        [TestMethod]
        public void Rewrite_SkipsStringsAndComments()
        {
            string src = "# foo lives here\nx = \"foo\"\nfoo = 1\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("# foo lives here\nx = \"foo\"\nbar = 1\n", r.Text);
        }

        [TestMethod]
        public void Rewrite_SkipsAttributeAccessName()
        {
            // `obj.foo` must NOT be rewritten when renaming the module-level `foo`.
            string src = "def foo():\n    return 1\n\nobj.foo = 1\nfoo()\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(2, r.Count); // def + the top-level call
            Assert.AreEqual("def bar():\n    return 1\n\nobj.foo = 1\nbar()\n", r.Text);
        }

        [TestMethod]
        public void Rewrite_SkipsShadowingParameter()
        {
            // def g(foo): … — `foo` inside g is a parameter, not the global.
            string src = "def foo():\n    return 1\n\ndef g(foo):\n    return foo + 1\n\nfoo()\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(2, r.Count); // def foo → def bar; trailing foo() call
            Assert.AreEqual(
                "def bar():\n    return 1\n\ndef g(foo):\n    return foo + 1\n\nbar()\n",
                r.Text);
        }

        [TestMethod]
        public void Rewrite_SkipsLocalAssignment()
        {
            // A local `foo = …` inside a function shadows the global.
            string src = "def foo():\n    return 1\n\ndef other():\n    foo = 99\n    return foo\n\nfoo()\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual(
                "def bar():\n    return 1\n\ndef other():\n    foo = 99\n    return foo\n\nbar()\n",
                r.Text);
        }

        [TestMethod]
        public void Rewrite_RenamesFromImportBindingName()
        {
            // `from pkg import foo` binds `foo` locally. Renaming the module-level
            // foo (in another file) should rewrite the imported-name in this file.
            string src = "from pkg import foo\nfoo()\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(2, r.Count); // import name + call site
            Assert.AreEqual("from pkg import bar\nbar()\n", r.Text);
        }

        [TestMethod]
        public void Rewrite_FromImport_DoesNotRenameAlias()
        {
            // `from pkg import foo as alias` — the original name `foo` renames,
            // the alias stays.
            string src = "from pkg import foo as alias\nalias()\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("from pkg import bar as alias\nalias()\n", r.Text);
        }

        [TestMethod]
        public void Rewrite_DoesNotTouchNestedDefShadowing()
        {
            // An inner `def foo()` inside another function is a NESTED definition;
            // it's NOT the module-level foo and should not be renamed.
            string src = "def foo():\n    return 1\n\ndef outer():\n    def foo():\n        return 2\n    return foo()\n\nfoo()\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(2, r.Count); // top-level def + trailing call
            Assert.AreEqual(
                "def bar():\n    return 1\n\ndef outer():\n    def foo():\n        return 2\n    return foo()\n\nbar()\n",
                r.Text);
        }

        [TestMethod]
        public void Rewrite_ComprehensionTargetShadows()
        {
            // [foo for foo in items] — `foo` inside the comprehension is bound by
            // the comprehension target and must not be renamed.
            string src = "def foo():\n    return 1\n\nresult = [foo for foo in items]\nfoo()\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(2, r.Count); // def + call
            Assert.AreEqual(
                "def bar():\n    return 1\n\nresult = [foo for foo in items]\nbar()\n",
                r.Text);
        }

        [TestMethod]
        public void Rewrite_NoMatches_ReturnsOriginal()
        {
            string src = "def other():\n    return 1\n";
            var r = AstIdentifierRewriter.Rewrite(src, "foo", "bar");
            Assert.AreEqual(0, r.Count);
            Assert.AreEqual(src, r.Text);
        }

        [TestMethod]
        public void Rewrite_RenamesClassDeclaration()
        {
            string src = "class Foo:\n    pass\n\nFoo()\n";
            var r = AstIdentifierRewriter.Rewrite(src, "Foo", "Bar");
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("class Bar:\n    pass\n\nBar()\n", r.Text);
        }
    }
}
