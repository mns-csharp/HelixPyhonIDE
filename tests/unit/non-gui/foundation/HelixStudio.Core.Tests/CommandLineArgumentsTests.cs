using HelixPythonNamespace.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Core.Tests
{
    /// <summary>
    /// Windows CRT command-line rules — the same ones <c>CommandLineToArgvW</c> and therefore
    /// Python's <c>sys.argv</c> apply. This exists because run arguments are almost always file
    /// paths, and on Windows those routinely contain spaces: getting the quoting wrong silently
    /// splits one argument into several.
    /// </summary>
    [TestClass]
    public class CommandLineArgumentsTests
    {
        [TestMethod]
        public void Split_NullOrBlank_YieldsNoArguments()
        {
            Assert.AreEqual(0, CommandLineArguments.Split(null).Length);
            Assert.AreEqual(0, CommandLineArguments.Split("").Length);
            Assert.AreEqual(0, CommandLineArguments.Split("   \t ").Length);
        }

        [TestMethod]
        public void Split_SeparatesOnWhitespaceRuns()
        {
            CollectionAssert.AreEqual(new[] { "a", "b", "c" },
                CommandLineArguments.Split("  a \t b   c "));
        }

        [TestMethod]
        public void Split_KeepsQuotedPathWithSpacesAsOneArgument()
        {
            CollectionAssert.AreEqual(
                new[] { "--out", @"C:\My Data\rows.tsv" },
                CommandLineArguments.Split("--out \"C:\\My Data\\rows.tsv\""));
        }

        [TestMethod]
        public void Split_BackslashesAreLiteralUnlessTheyPrecedeAQuote()
        {
            // 2n backslashes + quote → n backslashes, quote delimits.
            CollectionAssert.AreEqual(new[] { @"C:\dir\", "next" },
                CommandLineArguments.Split(@"""C:\dir\\"" next"));
            // 2n+1 backslashes + quote → n backslashes and a LITERAL quote.
            CollectionAssert.AreEqual(new[] { "a\"b" },
                CommandLineArguments.Split(@"a\""b"));
            // A lone trailing backslash is just a backslash.
            CollectionAssert.AreEqual(new[] { @"C:\dir\" },
                CommandLineArguments.Split(@"C:\dir\"));
        }

        [TestMethod]
        public void Split_DoubledQuoteInsideQuotedRunIsALiteralQuote()
        {
            CollectionAssert.AreEqual(new[] { "say \"hi\"" },
                CommandLineArguments.Split("\"say \"\"hi\"\"\""));
        }

        [TestMethod]
        public void Split_EmptyQuotedStringIsAnArgument()
        {
            CollectionAssert.AreEqual(new[] { "a", "", "b" },
                CommandLineArguments.Split("a \"\" b"));
        }

        [TestMethod]
        public void Split_UnbalancedQuote_DoesNotThrow_AndKeepsTheTail()
        {
            // Typed into a text box mid-edit; must degrade, never throw.
            CollectionAssert.AreEqual(new[] { "--out", "C:\\My Data\\x" },
                CommandLineArguments.Split("--out \"C:\\My Data\\x"));
        }

        [TestMethod]
        public void Quote_LeavesSimpleArgumentsAlone()
        {
            Assert.AreEqual("--verbose", CommandLineArguments.Quote("--verbose"));
            Assert.AreEqual(@"C:\data\rows.tsv", CommandLineArguments.Quote(@"C:\data\rows.tsv"));
        }

        [TestMethod]
        public void Quote_WrapsWhitespaceAndEscapesQuotes()
        {
            Assert.AreEqual("\"C:\\My Data\\rows.tsv\"", CommandLineArguments.Quote(@"C:\My Data\rows.tsv"));
            Assert.AreEqual("\"\"", CommandLineArguments.Quote(""));
            Assert.AreEqual("\"a\\\"b\"", CommandLineArguments.Quote("a\"b"));
        }

        [TestMethod]
        public void Quote_DoublesATrailingBackslashRunSoItCannotEscapeTheClosingQuote()
        {
            // "C:\My Dir\" would end the quoted run at the backslash-quote pair.
            Assert.AreEqual("\"C:\\My Dir\\\\\"", CommandLineArguments.Quote(@"C:\My Dir\"));
        }

        [TestMethod]
        public void QuoteThenSplit_RoundTripsEveryAwkwardShape()
        {
            string[] cases =
            {
                "plain",
                "with space",
                @"C:\My Data\rows.tsv",
                @"trailing\",
                @"C:\dir\",
                "quote\"inside",
                "",
                "tab\there",
            };
            foreach (string original in cases)
            {
                string[] round = CommandLineArguments.Split(CommandLineArguments.Quote(original));
                Assert.AreEqual(1, round.Length, "expected exactly one argument back for: " + original);
                Assert.AreEqual(original, round[0], "round-trip failed for: " + original);
            }
        }

        [TestMethod]
        public void Join_ProducesACommandLineThatSplitsBackToTheSameVector()
        {
            string[] argv = { "-u", @"C:\My Projects\app\main.py", "in.tsv", @"C:\My Data\out.tsv" };
            CollectionAssert.AreEqual(argv, CommandLineArguments.Split(CommandLineArguments.Join(argv)));
        }
    }
}
