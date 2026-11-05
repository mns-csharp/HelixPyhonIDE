using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    [TestClass]
    public class PythonIdentifierRewriterTests
    {
        // ---------------- RewriteIdentifier ----------------

        [TestMethod]
        public void RewriteIdentifier_RenamesDefSiteAndCallSite()
        {
            string src = "def foo():\n    return 1\n\nfoo()\n";
            var r = PythonIdentifierRewriter.RewriteIdentifier(src, "foo", "bar");
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("def bar():\n    return 1\n\nbar()\n", r.Text);
        }

        [TestMethod]
        public void RewriteIdentifier_DoesNotTouchStringLiterals()
        {
            string src = "x = \"foo\"\nfoo = 1\n";
            var r = PythonIdentifierRewriter.RewriteIdentifier(src, "foo", "bar");
            Assert.AreEqual(1, r.Count);
            // Only the identifier assignment was renamed; the string is intact.
            Assert.AreEqual("x = \"foo\"\nbar = 1\n", r.Text);
        }

        [TestMethod]
        public void RewriteIdentifier_DoesNotTouchComments()
        {
            string src = "# foo is important\nfoo = 1\n";
            var r = PythonIdentifierRewriter.RewriteIdentifier(src, "foo", "bar");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("# foo is important\nbar = 1\n", r.Text);
        }

        [TestMethod]
        public void RewriteIdentifier_HandlesAttributeAccessRightSide()
        {
            // `obj.foo` — the token is a Name token even when used as an attribute.
            // The rewriter renames ALL lexical occurrences (documented v1 limitation).
            string src = "obj.foo = 1\nfoo()\n";
            var r = PythonIdentifierRewriter.RewriteIdentifier(src, "foo", "bar");
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("obj.bar = 1\nbar()\n", r.Text);
        }

        [TestMethod]
        public void RewriteIdentifier_DoesNotTouchSubstringMatches()
        {
            // "foobar" and "foo_baz" are distinct Name tokens — they must not match "foo".
            string src = "foobar = 1\nfoo_baz = 2\nfoo = 3\n";
            var r = PythonIdentifierRewriter.RewriteIdentifier(src, "foo", "renamed");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("foobar = 1\nfoo_baz = 2\nrenamed = 3\n", r.Text);
        }

        [TestMethod]
        public void RewriteIdentifier_NoMatches_ReturnsOriginalText()
        {
            string src = "x = 1\n";
            var r = PythonIdentifierRewriter.RewriteIdentifier(src, "foo", "bar");
            Assert.AreEqual(0, r.Count);
            Assert.AreEqual(src, r.Text);
        }

        [TestMethod]
        public void RewriteIdentifier_DoesNotTouchKeywords()
        {
            // "class" is a keyword token, not a Name token — renaming "class" should
            // find zero matches in "class Foo: pass".
            string src = "class Foo:\n    pass\n";
            var r = PythonIdentifierRewriter.RewriteIdentifier(src, "class", "CLASS");
            Assert.AreEqual(0, r.Count);
            Assert.AreEqual(src, r.Text);
        }

        [TestMethod]
        public void RewriteIdentifier_UpdatesFromImportAliases()
        {
            // Rename the function name — the import clause itself imports it by that name.
            string src = "from pkg import foo\nfoo()\n";
            var r = PythonIdentifierRewriter.RewriteIdentifier(src, "foo", "bar");
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("from pkg import bar\nbar()\n", r.Text);
        }

        // ---------------- RewriteModuleImports ----------------

        [TestMethod]
        public void RewriteModuleImports_ExactMatch()
        {
            string src = "from lib_utils.geometry import total_area\n";
            var r = PythonIdentifierRewriter.RewriteModuleImports(
                src, "lib_utils.geometry", "lib_utils.geom");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("from lib_utils.geom import total_area\n", r.Text);
        }

        [TestMethod]
        public void RewriteModuleImports_PrefixMatch_WalksDottedSuffix()
        {
            string src = "from pkg_old.sub.leaf import X\n";
            var r = PythonIdentifierRewriter.RewriteModuleImports(
                src, "pkg_old", "pkg_new");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("from pkg_new.sub.leaf import X\n", r.Text);
        }

        [TestMethod]
        public void RewriteModuleImports_BareImportStatement()
        {
            string src = "import pkg_old.tools\nimport other\n";
            var r = PythonIdentifierRewriter.RewriteModuleImports(
                src, "pkg_old", "pkg_new");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("import pkg_new.tools\nimport other\n", r.Text);
        }

        [TestMethod]
        public void RewriteModuleImports_DoesNotTouchUnrelatedImports()
        {
            string src = "from pkg_alpha import X\nfrom pkg_beta import Y\n";
            var r = PythonIdentifierRewriter.RewriteModuleImports(
                src, "pkg_gamma", "pkg_renamed");
            Assert.AreEqual(0, r.Count);
            Assert.AreEqual(src, r.Text);
        }

        [TestMethod]
        public void RewriteModuleImports_SimilarPrefix_NoMatch()
        {
            // "pkg_alpha" must not match "pkg_alpha2" — only exact or `+ "."` prefix.
            string src = "from pkg_alpha2 import X\n";
            var r = PythonIdentifierRewriter.RewriteModuleImports(
                src, "pkg_alpha", "pkg_renamed");
            Assert.AreEqual(0, r.Count);
            Assert.AreEqual(src, r.Text);
        }

        [TestMethod]
        public void RewriteModuleImports_MultiNameImportLine_LeavesNamesAlone()
        {
            // `from pkg_old import A, B as C` — rewrite pkg_old only; the names A, B, C are kept.
            string src = "from pkg_old import A, B as C\n";
            var r = PythonIdentifierRewriter.RewriteModuleImports(
                src, "pkg_old", "pkg_new");
            Assert.AreEqual(1, r.Count);
            Assert.AreEqual("from pkg_new import A, B as C\n", r.Text);
        }
    }
}
