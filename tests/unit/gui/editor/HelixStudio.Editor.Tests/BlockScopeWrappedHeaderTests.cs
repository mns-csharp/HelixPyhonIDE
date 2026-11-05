using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Regression for "the virtual braces are mispositioned when a function's parameter list
    /// wraps". A block's header can span several physical lines; BlockScopeAnalyzer found it by
    /// stepping one line back from the body's first statement, which lands on the header's
    /// CLOSING line (<c>) -> R:</c>). That line's leading whitespace is the continuation indent,
    /// so the '{' / '}' glyphs and the header tint aligned to the body instead of to the `def`.
    ///
    /// Driven against the real fixture file the bug was reported on, not synthetic snippets.
    /// </summary>
    [TestClass]
    public class BlockScopeWrappedHeaderTests
    {
        private static string FixturePath()
        {
            // tests\unit\gui\editor\<proj> → repo root is six levels up from the test binary's
            // source tree; walk up from the assembly location until the fixture resolves.
            string dir = Path.GetDirectoryName(new Uri(typeof(BlockScopeWrappedHeaderTests).Assembly.CodeBase).LocalPath);
            for (int i = 0; i < 10 && dir != null; i++)
            {
                string candidate = Path.Combine(dir,
                    @"tests\fixtures\python\python-test-sample-oop\lib_utils\async_ops.py");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static List<BlockRange> AnalyzeFixture(out string src)
        {
            string path = FixturePath();
            if (path == null)
            {
                Assert.Inconclusive("async_ops.py fixture not found from the test binary location");
            }
            src = File.ReadAllText(path);
            var parsed = HelixPythonNamespace.Python.Python.ParseModule(src);
            Assert.IsFalse(parsed.HasErrors, "the fixture must parse cleanly");
            return BlockScopeAnalyzer.Analyze(parsed.Module, src);
        }

        private static BlockRange FindByHeaderLine(List<BlockRange> blocks, BlockKind kind, int headerLine)
        {
            foreach (BlockRange b in blocks)
                if (b.Kind == kind && b.HeaderLine == headerLine) return b;
            Assert.Fail("no " + kind + " block whose header ends on line " + headerLine);
            return default(BlockRange);
        }

        // `async def retry_async(` starts on line 106 and its header closes on line 113
        // ("    ) -> R:", indented four spaces). The brace row belongs on 113 — that is where a
        // C# programmer puts '{' after a wrapped signature — but it must align to column 0.
        [TestMethod]
        public void WrappedDefHeader_BracesAlignToTheDefLine_NotTheClosingParenLine()
        {
            string src;
            List<BlockRange> blocks = AnalyzeFixture(out src);

            BlockRange retry = FindByHeaderLine(blocks, BlockKind.Def, 113);
            Assert.AreEqual(106, retry.HeaderStartLine,
                "the logical header starts at 'async def retry_async(' on line 106");
            Assert.AreEqual(0, retry.ParentIndentColumn,
                "'async def retry_async(' is at column 0 — the '{' and '}' align there, not to the " +
                "four-space indent of the wrapped '    ) -> R:' line");
        }

        [TestMethod]
        public void WrappedDefHeader_AtColumnZero_IsAlsoCorrect()
        {
            string src;
            List<BlockRange> blocks = AnalyzeFixture(out src);

            // `async def gather_with_limit(` (line 43) closes on line 47: ") -> list[R]:".
            BlockRange gather = FindByHeaderLine(blocks, BlockKind.Def, 47);
            Assert.AreEqual(43, gather.HeaderStartLine);
            Assert.AreEqual(0, gather.ParentIndentColumn);
        }

        // The nested `async def guarded(...)` on line 57 fits on one line: HeaderStartLine must
        // equal HeaderLine so single-line headers keep their existing (correct) behavior.
        [TestMethod]
        public void SingleLineHeader_IsUnchanged()
        {
            string src;
            List<BlockRange> blocks = AnalyzeFixture(out src);

            BlockRange guarded = FindByHeaderLine(blocks, BlockKind.Def, 57);
            Assert.AreEqual(57, guarded.HeaderStartLine, "a one-line header starts where it ends");
            Assert.AreEqual(4, guarded.ParentIndentColumn, "'async def guarded' is indented four spaces");
        }

        // Sweep: for EVERY block in the fixture the recorded indent column must be the actual
        // leading whitespace of HeaderStartLine, and the header must never start after it ends.
        [TestMethod]
        public void EveryBlockInTheFixture_HasAConsistentHeaderSpanAndIndent()
        {
            string src;
            List<BlockRange> blocks = AnalyzeFixture(out src);
            string[] lines = src.Split('\n');
            Assert.IsTrue(blocks.Count > 10, "the fixture should yield a meaningful number of blocks");

            foreach (BlockRange b in blocks)
            {
                Assert.IsTrue(b.HeaderStartLine >= 1 && b.HeaderStartLine <= b.HeaderLine,
                    "header start " + b.HeaderStartLine + " must not follow header end " + b.HeaderLine);

                string headerStart = lines[b.HeaderStartLine - 1].TrimEnd('\r');
                int indent = 0;
                while (indent < headerStart.Length && (headerStart[indent] == ' ' || headerStart[indent] == '\t')) indent++;
                Assert.AreEqual(indent, b.ParentIndentColumn,
                    "ParentIndentColumn must be the leading whitespace of line " + b.HeaderStartLine +
                    " (\"" + headerStart + "\")");
            }
        }

        // A `def` is not the only header that wraps: a parenthesized condition does too, and an
        // unbalanced bracket inside a STRING must not leak a bracket depth that swallows every
        // later line into one logical line.
        [TestMethod]
        public void WrappedIfCondition_AndBracketsInStrings_AreHandled()
        {
            string src =
"class C:\n" +                                     // 1
"    def go(self):\n" +                             // 2
"        label = \"a ( bracket in a string\"\n" +   // 3 — must NOT open a bracket
"        if (label and\n" +                         // 4 — header starts here
"                len(label) > 2):\n" +              // 5 — header ends here
"            print(label)  # trailing ) comment\n"; // 6

            var parsed = HelixPythonNamespace.Python.Python.ParseModule(src);
            Assert.IsFalse(parsed.HasErrors, "source must parse cleanly");
            List<BlockRange> blocks = BlockScopeAnalyzer.Analyze(parsed.Module, src);

            BlockRange ifBlock = FindByHeaderLine(blocks, BlockKind.If, 5);
            Assert.AreEqual(4, ifBlock.HeaderStartLine, "the '(' on line 4 continues onto line 5");
            Assert.AreEqual(8, ifBlock.ParentIndentColumn, "'if' is indented eight spaces on line 4");

            // If the string's '(' had leaked, every block below line 3 would report a header
            // starting at line 3.
            BlockRange def = FindByHeaderLine(blocks, BlockKind.Def, 2);
            Assert.AreEqual(2, def.HeaderStartLine);
            Assert.AreEqual(4, def.ParentIndentColumn);
            BlockRange cls = FindByHeaderLine(blocks, BlockKind.Class, 1);
            Assert.AreEqual(1, cls.HeaderStartLine);
            Assert.AreEqual(0, cls.ParentIndentColumn);
        }

        // The continuation scan itself, exercised directly. Backslash continuation is covered
        // here rather than end-to-end because HelixPythonNamespace.Python's parser does not currently
        // implement it (`x = 1 + \` + newline + `2` parses as two statements), so no BlockRange
        // would ever reach the scanner with a backslash-continued header.
        [TestMethod]
        public void ContinuationScan_HandlesBrackets_Backslash_TripleQuotes_AndComments()
        {
            string[] lines =
            {
                "def f(",                    // 0 — opens a bracket
                "    a,",                    // 1 — continuation
                "):",                        // 2 — continuation, closes it
                "    s = \"\"\"text",        // 3 — opens a triple-quoted string
                "    ( unbalanced",          // 4 — continuation; bracket is inside the string
                "    \"\"\"",                // 5 — continuation, closes the string
                "    t = 1 + \\",            // 6 — trailing backslash
                "        2",                 // 7 — continuation
                "    u = 2  # a ( comment",  // 8 — bracket is inside a comment
                "    v = 3",                 // 9
            };

            MethodInfo scan = typeof(BlockScopeAnalyzer).GetMethod(
                "ComputeContinuationFlags", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(scan, "ComputeContinuationFlags must exist");
            bool[] flags = (bool[])scan.Invoke(null, new object[] { lines });

            bool[] expected = { false, true, true, false, true, true, false, true, false, false };
            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], flags[i], "line index " + i + ": \"" + lines[i] + "\"");
        }
    }
}
