using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Python;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Phase 6 of the Solution Explorer CRUD-coverage sweep: member (method/property/field/
    /// constructor) rename. PythonMemberRewriter is deliberately scoped to the OWNING CLASS BODY
    /// ONLY (not solution/file-wide like AstIdentifierRewriter), since member names collide far
    /// more often than class/function names — these tests exercise that scoping directly, not just
    /// the happy path.
    /// </summary>
    [TestClass]
    public class PythonMemberRewriterTests
    {
        [TestMethod]
        public void RenamesTheMethodDeclaration_AndItsSelfCallSitesInOtherMethods()
        {
            const string source =
                "class Widget:\n" +
                "    def render(self):\n" +
                "        return 'r'\n" +
                "\n" +
                "    def refresh(self):\n" +
                "        return self.render()\n";
            var result = PythonMemberRewriter.Rename(source, "Widget", "render", "draw");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.NewSource, "def draw(self):");
            StringAssert.Contains(result.NewSource, "return self.draw()");
            Assert.IsFalse(result.NewSource.Contains("render"));
            Assert.AreEqual(2, result.Count, "declaration + one self-call site");
        }

        [TestMethod]
        public void RenamesAFieldsClassLevelDeclaration_AndEverySelfAssignment()
        {
            const string source =
                "class Widget:\n" +
                "    count = 0\n" +
                "\n" +
                "    def bump(self):\n" +
                "        self.count = self.count + 1\n" +
                "        return self.count\n";
            var result = PythonMemberRewriter.Rename(source, "Widget", "count", "total");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.NewSource, "total = 0");
            StringAssert.Contains(result.NewSource, "self.total = self.total + 1");
            StringAssert.Contains(result.NewSource, "return self.total");
            Assert.IsFalse(result.NewSource.Contains("count"));
        }

        [TestMethod]
        public void DoesNotTouchAMethodOfTheSameName_OnADifferentClass()
        {
            const string source =
                "class Alpha:\n" +
                "    def go(self):\n" +
                "        return 'alpha'\n" +
                "\n" +
                "\n" +
                "class Beta:\n" +
                "    def go(self):\n" +
                "        return 'beta'\n";
            var result = PythonMemberRewriter.Rename(source, "Beta", "go", "run");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.NewSource, "class Alpha:\n    def go(self):",
                "renaming Beta.go must leave Alpha.go completely untouched");
            StringAssert.Contains(result.NewSource, "class Beta:\n    def run(self):");
        }

        [TestMethod]
        public void DoesNotTouchAPlainVariableOfTheSameName_NotPrefixedWithSelf()
        {
            // `render` here is a bare local, not `self.render` — a member rename must never
            // reach for a bare-name occurrence the way AstIdentifierRewriter (module scope) would.
            const string source =
                "class Widget:\n" +
                "    def render(self):\n" +
                "        pass\n" +
                "\n" +
                "    def other(self):\n" +
                "        render = 5\n" +
                "        return render\n";
            var result = PythonMemberRewriter.Rename(source, "Widget", "render", "draw");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.NewSource, "def draw(self):");
            StringAssert.Contains(result.NewSource, "render = 5",
                "an unrelated local variable of the same name must not be renamed");
            StringAssert.Contains(result.NewSource, "return render");
        }

        [TestMethod]
        public void RenamesASelfCall_ThatIsABareExpressionStatement()
        {
            // Regression guard for the exact traversal gap AstIdentifierRewriter's own comments
            // warn about: a bare `self.reset()` statement is an ExpressionStatement wrapping a
            // CallExpression wrapping the AttributeExpression — nodes a naive Descendants() walk
            // (rather than AstIdentifierRewriter.EnumerateAll) can silently fail to reach.
            const string source =
                "class Widget:\n" +
                "    def reset(self):\n" +
                "        pass\n" +
                "\n" +
                "    def go(self):\n" +
                "        self.reset()\n";
            var result = PythonMemberRewriter.Rename(source, "Widget", "reset", "clear");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            StringAssert.Contains(result.NewSource, "def clear(self):");
            StringAssert.Contains(result.NewSource, "self.clear()");
            Assert.IsFalse(result.NewSource.Contains("reset"));
        }

        [TestMethod]
        public void UnknownClass_Fails()
        {
            var result = PythonMemberRewriter.Rename("class Widget:\n    pass\n", "Ghost", "x", "y");
            Assert.IsFalse(result.Success);
        }

        [TestMethod]
        public void MemberNotPresent_SucceedsAsANoOp()
        {
            // Nothing to rename is not an error — mirrors AstIdentifierRewriter.Rewrite's own
            // "0 hits, source unchanged" behavior for a name that isn't there.
            const string source = "class Widget:\n    def go(self):\n        pass\n";
            var result = PythonMemberRewriter.Rename(source, "Widget", "ghost", "y");
            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, result.Count);
            Assert.AreEqual(source, result.NewSource);
        }

        [TestMethod]
        public void SameNameAsNewName_SucceedsAsANoOp()
        {
            const string source = "class Widget:\n    def go(self):\n        pass\n";
            var result = PythonMemberRewriter.Rename(source, "Widget", "go", "go");
            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, result.Count);
        }
    }
}
