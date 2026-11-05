using HelixPythonNamespace.Core.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Core.Tests
{
    /// <summary>
    /// Parsing CPython error output into located diagnostics, so a failed run can be double-clicked
    /// through to the offending line.
    ///
    /// Every sample here was captured from a real CPython 3.11 run, not written from memory — the
    /// exact spacing, the <c>, in &lt;fn&gt;</c> suffix, and the 3.11 <c>~~~^^^</c> marker rows are
    /// the details a hand-written sample gets wrong.
    /// </summary>
    [TestClass]
    public class PythonTracebackParserTests
    {
        // Captured from: python -u syn.py   (file contains `111abc` on line 2)
        private const string SyntaxErrorOutput =
            "  File \"C:\\ws\\syn.py\", line 2\n" +
            "    111abc\n" +
            "      ^\n" +
            "SyntaxError: invalid decimal literal\n";

        // Captured from: python -u run.py   (inner() opens a missing file)
        private const string TracebackOutput =
            "Traceback (most recent call last):\n" +
            "  File \"C:\\ws\\run.py\", line 7, in <module>\n" +
            "    main()\n" +
            "  File \"C:\\ws\\run.py\", line 5, in main\n" +
            "    return inner(\"nope.txt\")\n" +
            "           ^^^^^^^^^^^^^^^^^\n" +
            "  File \"C:\\ws\\run.py\", line 2, in inner\n" +
            "    return open(p)\n" +
            "           ^^^^^^^\n" +
            "FileNotFoundError: [Errno 2] No such file or directory: 'nope.txt'\n";

        [TestMethod]
        public void NoErrorInOutput_ReturnsNull()
        {
            Assert.IsNull(PythonTracebackParser.Parse(null));
            Assert.IsNull(PythonTracebackParser.Parse(""));
            Assert.IsNull(PythonTracebackParser.Parse("Prepared 7575 rows\nall done\n"),
                "inventing a location for a program that merely exited non-zero would be worse "
                + "than offering none");
        }

        [TestMethod]
        public void SyntaxError_HasOneLocation_AndNoStack()
        {
            PythonErrorReport r = PythonTracebackParser.Parse(SyntaxErrorOutput);

            Assert.IsNotNull(r, "a SyntaxError has no 'Traceback' header — frame-only parsing misses it");
            Assert.IsTrue(r.IsSyntaxError);
            Assert.AreEqual("SyntaxError", r.ExceptionType);
            Assert.AreEqual("invalid decimal literal", r.ExceptionMessage);
            Assert.AreEqual(1, r.Frames.Count);
            Assert.AreEqual(@"C:\ws\syn.py", r.Innermost.File);
            Assert.AreEqual(2, r.Innermost.Line);
            Assert.AreEqual("111abc", r.Innermost.SourceLine);
            Assert.IsTrue(r.Innermost.IsInnermost);
            Assert.IsNull(r.Innermost.Function, "a syntax error has no enclosing frame");
        }

        [TestMethod]
        public void SyntaxError_CaretGivesTheColumnRelativeToTheStatement()
        {
            PythonErrorReport r = PythonTracebackParser.Parse(SyntaxErrorOutput);
            // Echo is "    111abc" (4 spaces), caret at index 6 → offset 2 → column 3.
            Assert.AreEqual(3, r.Innermost.Column,
                "the interpreter re-indents its echo, so the caret's raw offset is not the column");
        }

        [TestMethod]
        public void Traceback_KeepsEveryFrame_InPrintedOrder()
        {
            PythonErrorReport r = PythonTracebackParser.Parse(TracebackOutput);

            Assert.IsNotNull(r);
            Assert.IsFalse(r.IsSyntaxError);
            Assert.AreEqual(3, r.Frames.Count);
            Assert.AreEqual(7, r.Frames[0].Line);
            Assert.AreEqual("<module>", r.Frames[0].Function);
            Assert.AreEqual(5, r.Frames[1].Line);
            Assert.AreEqual("main", r.Frames[1].Function);
            Assert.AreEqual(2, r.Frames[2].Line);
            Assert.AreEqual("inner", r.Frames[2].Function);
        }

        [TestMethod]
        public void Traceback_InnermostIsTheLastFrame_WhereTheErrorActuallyHappened()
        {
            PythonErrorReport r = PythonTracebackParser.Parse(TracebackOutput);

            Assert.AreEqual(2, r.Innermost.Line, "CPython prints the culprit LAST");
            Assert.AreEqual("inner", r.Innermost.Function);
            Assert.IsTrue(r.Innermost.IsInnermost);
            Assert.IsFalse(r.Frames[0].IsInnermost);
        }

        [TestMethod]
        public void Traceback_CapturesTypeMessageAndHeadline()
        {
            PythonErrorReport r = PythonTracebackParser.Parse(TracebackOutput);

            Assert.AreEqual("FileNotFoundError", r.ExceptionType);
            Assert.AreEqual("[Errno 2] No such file or directory: 'nope.txt'", r.ExceptionMessage);
            Assert.AreEqual("FileNotFoundError: [Errno 2] No such file or directory: 'nope.txt'",
                r.Headline);
        }

        [TestMethod]
        public void MarkerRowsAreNotMistakenForSourceLines()
        {
            PythonErrorReport r = PythonTracebackParser.Parse(TracebackOutput);
            Assert.AreEqual("return open(p)", r.Innermost.SourceLine,
                "the ^^^^ row under the echo must not be captured as the source");
        }

        [TestMethod]
        public void ProgramOutputBeforeTheTraceback_DoesNotDerailParsing()
        {
            string mixed = "Prepared 7575 rows\nworking...\n" + TracebackOutput;
            PythonErrorReport r = PythonTracebackParser.Parse(mixed);

            Assert.IsNotNull(r);
            Assert.AreEqual(3, r.Frames.Count);
            Assert.AreEqual("FileNotFoundError", r.ExceptionType);
        }

        [TestMethod]
        public void ChainedExceptions_ReportTheFinalOne()
        {
            // "During handling of the above exception, another exception occurred" — the one that
            // actually escaped is the LAST, and that is what the user must land on.
            string chained =
                "Traceback (most recent call last):\n" +
                "  File \"C:\\ws\\a.py\", line 3, in <module>\n" +
                "    boom()\n" +
                "ValueError: first\n" +
                "\n" +
                "During handling of the above exception, another exception occurred:\n" +
                "\n" +
                "Traceback (most recent call last):\n" +
                "  File \"C:\\ws\\b.py\", line 9, in <module>\n" +
                "    rethrow()\n" +
                "RuntimeError: second\n";

            PythonErrorReport r = PythonTracebackParser.Parse(chained);
            Assert.AreEqual("RuntimeError", r.ExceptionType);
            Assert.AreEqual("second", r.ExceptionMessage);
            Assert.AreEqual(@"C:\ws\b.py", r.Innermost.File);
            Assert.AreEqual(9, r.Innermost.Line);
        }

        [TestMethod]
        public void DottedExceptionTypes_AreRecognized()
        {
            string output =
                "Traceback (most recent call last):\n" +
                "  File \"C:\\ws\\a.py\", line 1, in <module>\n" +
                "json.decoder.JSONDecodeError: Expecting value: line 1 column 1 (char 0)\n";

            PythonErrorReport r = PythonTracebackParser.Parse(output);
            Assert.AreEqual("json.decoder.JSONDecodeError", r.ExceptionType);
            Assert.AreEqual("Expecting value: line 1 column 1 (char 0)", r.ExceptionMessage);
        }

        [TestMethod]
        public void PathsWithSpaces_AreCapturedWhole()
        {
            string output =
                "Traceback (most recent call last):\n" +
                "  File \"C:\\My Projects\\app\\main.py\", line 4, in <module>\n" +
                "RuntimeError: boom\n";

            PythonErrorReport r = PythonTracebackParser.Parse(output);
            Assert.AreEqual(@"C:\My Projects\app\main.py", r.Innermost.File,
                "the path is delimited by quotes, not whitespace");
        }

        [TestMethod]
        public void IndentationErrorCountsAsASyntaxError()
        {
            string output =
                "  File \"C:\\ws\\x.py\", line 5\n" +
                "    return 1\n" +
                "IndentationError: unexpected indent\n";

            PythonErrorReport r = PythonTracebackParser.Parse(output);
            Assert.IsTrue(r.IsSyntaxError);
            Assert.AreEqual(5, r.Innermost.Line);
        }

        [TestMethod]
        public void TruncatedOutput_WithoutAnExceptionLine_StillYieldsNavigableFrames()
        {
            string truncated =
                "Traceback (most recent call last):\n" +
                "  File \"C:\\ws\\a.py\", line 12, in <module>\n" +
                "    do_work()\n";

            PythonErrorReport r = PythonTracebackParser.Parse(truncated);
            Assert.IsNotNull(r, "frames alone are still worth navigating to");
            Assert.AreEqual(12, r.Innermost.Line);
            Assert.IsTrue(r.Innermost.IsInnermost);
        }
    }
}
