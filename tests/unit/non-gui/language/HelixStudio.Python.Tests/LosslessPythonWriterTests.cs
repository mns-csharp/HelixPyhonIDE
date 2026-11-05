using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Emit;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Tests
{
    /// <summary>
    /// FR-GEN-02: the lossless, trivia-preserving Python round-trip. Unlike <see cref="PythonWriter"/> (which
    /// normalizes), <see cref="LosslessPythonWriter"/> must reproduce the exact source from the CST spans —
    /// comments, blank lines, docstrings, trailing whitespace, and mixed indentation all preserved — and must
    /// splice targeted edits without disturbing surrounding trivia.
    /// </summary>
    [TestClass]
    public class LosslessPythonWriterTests
    {
        private static ModuleSyntax Parse(string src)
        {
            ParseResult r = Python.ParseModule(src);
            Assert.IsFalse(r.HasErrors, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            return r.Module;
        }

        private static void AssertRoundTrips(string src)
        {
            var m = Parse(src);
            string outp = LosslessPythonWriter.Write(src, m);
            Assert.AreEqual(src, outp, "lossless writer must reproduce the source byte-for-byte");
            Assert.IsTrue(LosslessPythonWriter.RoundTripsExactly(src, m));
        }

        [TestMethod]
        public void RoundTrips_CommentsAndBlankLines()
        {
            AssertRoundTrips(
                "# leading module comment\n" +
                "import os  # trailing comment on import\n" +
                "\n" +
                "\n" +
                "def greet(name):  # inline\n" +
                "    # body comment\n" +
                "    return 'hi ' + name\n");
        }

        [TestMethod]
        public void RoundTrips_DocstringsAndDecorators()
        {
            AssertRoundTrips(
                "class Widget:\n" +
                "    \"\"\"A widget.\n\n    Multi-line docstring.\n    \"\"\"\n" +
                "\n" +
                "    @property\n" +
                "    def size(self):\n" +
                "        return self._size\n");
        }

        [TestMethod]
        public void RoundTrips_MixedIndentationAndTrailingWhitespace()
        {
            // Deliberately irregular spacing a normalizing writer would 'fix'.
            AssertRoundTrips(
                "if True:\n" +
                "\tx = 1\n" +           // tab indent
                "\tif x:\n" +
                "\t\ty = 2   \n" +      // trailing spaces
                "z = 3\n");
        }

        [TestMethod]
        public void RoundTrips_NoTrailingNewline()
        {
            AssertRoundTrips("x = 1\ny = 2");
        }

        [TestMethod]
        public void RoundTrips_LeadingAndTrailingBlankLines()
        {
            AssertRoundTrips("\n\n# header\n\nvalue = 42\n\n\n");
        }

        [TestMethod]
        public void Edit_ReplacesOneStatement_PreservesSurroundingTrivia()
        {
            string src =
                "# keep me\n" +
                "x = 1  # and this trailing comment\n" +
                "y = 2\n";
            var m = Parse(src);
            // Replace the first top-level statement (x = 1) with a new assignment.
            StatementSyntax first = m.Statements[0];
            string outp = LosslessPythonWriter.Write(src, m, new[] { new PythonEdit(first, "x = 1000") });

            Assert.IsTrue(outp.Contains("# keep me"), "leading comment preserved");
            Assert.IsTrue(outp.Contains("x = 1000"), "edit applied");
            Assert.IsTrue(outp.Contains("# and this trailing comment"), "trailing comment preserved");
            Assert.IsTrue(outp.Contains("y = 2"), "later statement preserved");
            Assert.IsFalse(outp.Contains("x = 1 "), "old text replaced");
        }

        [TestMethod]
        public void Edit_EmptyEditSet_IsIdentity()
        {
            string src = "a = 1\n# c\nb = 2\n";
            var m = Parse(src);
            Assert.AreEqual(src, LosslessPythonWriter.Write(src, m, new PythonEdit[0]));
        }
    }
}
