namespace HelixPythonNamespace.Editor.Tests
{
    using System.Collections.Generic;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Editor.Navigation;
    using HelixPythonNamespace.Python.Analysis;

    [TestClass]
    public class TodoScannerTests
    {
        [TestMethod]
        public void ScansLeadingTodoComment()
        {
            List<TodoItem> items = TodoScanner.ScanText("# TODO: fix this\n", "f.py");
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual("TODO", items[0].Tag);
            Assert.AreEqual("fix this", items[0].Message);
            Assert.AreEqual(1, items[0].Line);
            Assert.AreEqual(3, items[0].Column); // 'T' is at index 2 (1-based 3)
        }

        [TestMethod]
        public void ScansTrailingComment()
        {
            List<TodoItem> items = TodoScanner.ScanText("x = 1  # FIXME later\n", "f.py");
            Assert.AreEqual(1, items.Count);
            Assert.AreEqual("FIXME", items[0].Tag);
            Assert.AreEqual("later", items[0].Message);
        }

        [TestMethod]
        public void IgnoresTagInsideStringLiteral()
        {
            List<TodoItem> items = TodoScanner.ScanText("s = \"# TODO not a comment\"\n", "f.py");
            Assert.AreEqual(0, items.Count);
        }

        [TestMethod]
        public void RequiresWordBoundaryAfterTag()
        {
            // "TODOS" must not match the TODO tag.
            Assert.AreEqual(0, TodoScanner.ScanText("# TODOS are great\n", "f.py").Count);
        }

        [TestMethod]
        public void RecognisesAllTags()
        {
            string src = "# TODO a\n# FIXME b\n# HACK c\n# XXX d\n# NOTE e\n# regular f\n";
            List<TodoItem> items = TodoScanner.ScanText(src, "f.py");
            Assert.AreEqual(5, items.Count);
            Assert.AreEqual("NOTE", items[4].Tag);
            Assert.AreEqual(5, items[4].Line);
        }
    }

    [TestClass]
    public class OccurrenceMatcherTests
    {
        [TestMethod]
        public void FindsWholeWordOccurrencesOnly()
        {
            List<OccurrenceMatcher.Match> m = OccurrenceMatcher.FindAll("foo = foo + foobar", "foo");
            Assert.AreEqual(2, m.Count);
            Assert.AreEqual(0, m[0].Start);
            Assert.AreEqual(3, m[0].End);
            Assert.AreEqual(6, m[1].Start);
        }

        [TestMethod]
        public void RejectsNonIdentifierNeedle()
        {
            Assert.AreEqual(0, OccurrenceMatcher.FindAll("1 + 1", "1").Count);
            Assert.AreEqual(0, OccurrenceMatcher.FindAll("", "x").Count);
        }

        [TestMethod]
        public void MatchesUnderscoreAndDigitIdentifiers()
        {
            List<OccurrenceMatcher.Match> m = OccurrenceMatcher.FindAll("_x1 = _x1 + _x12", "_x1");
            Assert.AreEqual(2, m.Count);
        }
    }
}
