using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Editor;
using HelixPythonNamespace.Python;

namespace HelixPythonNamespace.Python.Tests
{
    [TestClass]
    public class BlockBoundaryTests
    {
        private static List<BlockRange> Compute(string source)
        {
            ParseResult r = Python.ParseModule(source);
            Assert.IsFalse(r.HasErrors, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            return BlockScopeAnalyzer.Analyze(r.Module, source);
        }

        [TestMethod]
        public void ClassBlock_OneTuple()
        {
            var pairs = Compute("class Animal:\n    pass\n");
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual(BlockKind.Class, pairs[0].Kind);
            Assert.AreEqual(1, pairs[0].HeaderLine);  // header on line 1 (1-based), body starts at line 2 → headerLine = 2-1 = 1
            Assert.AreEqual(2, pairs[0].LastContentLine);
            Assert.AreEqual(4, pairs[0].IndentColumn);
            Assert.AreEqual(0, pairs[0].ParentIndentColumn);
        }

        [TestMethod]
        public void DefBlock_OneTuple()
        {
            var pairs = Compute("def foo():\n    return 42\n");
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual(BlockKind.Def, pairs[0].Kind);
            Assert.AreEqual(4, pairs[0].IndentColumn);
        }

        [TestMethod]
        public void AsyncDef_OneTuple()
        {
            var pairs = Compute("async def fetch():\n    pass\n");
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual(BlockKind.Def, pairs[0].Kind);
        }

        [TestMethod]
        public void SingleLineSuite_Skipped()
        {
            var pairs = Compute("def f(): pass\n");
            Assert.AreEqual(0, pairs.Count);
        }

        [TestMethod]
        public void NestedBlocks_TwoTuples()
        {
            var pairs = Compute("def outer():\n    def inner():\n        pass\n");
            Assert.AreEqual(2, pairs.Count);
            // inner's range is strictly inside outer's range
            var outer = pairs[0];
            var inner = pairs[1];
            Assert.IsTrue(inner.HeaderLine > outer.HeaderLine);
            Assert.IsTrue(inner.LastContentLine <= outer.LastContentLine);
            Assert.IsTrue(inner.IndentColumn > outer.IndentColumn);
        }

        [TestMethod]
        public void IfElifElse_ThreeTuples()
        {
            string src = "if x:\n    a\nelif y:\n    b\nelse:\n    c\n";
            var pairs = Compute(src);
            Assert.AreEqual(3, pairs.Count);
            Assert.IsTrue(pairs.All(p => p.Kind == BlockKind.If));
        }

        [TestMethod]
        public void WhileLoop_OneTuple()
        {
            var pairs = Compute("while x:\n    foo()\n");
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual(BlockKind.While, pairs[0].Kind);
        }

        [TestMethod]
        public void WhileElse_TwoTuples()
        {
            var pairs = Compute("while x:\n    a\nelse:\n    b\n");
            Assert.AreEqual(2, pairs.Count);
        }

        [TestMethod]
        public void ForLoop_OneTuple()
        {
            var pairs = Compute("for i in range(10):\n    print(i)\n");
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual(BlockKind.For, pairs[0].Kind);
        }

        [TestMethod]
        public void TryAllClauses_FiveTuples()
        {
            string src =
                "try:\n    a\nexcept Ex as e:\n    b\nexcept:\n    c\nelse:\n    d\nfinally:\n    e\n";
            var pairs = Compute(src);
            // body + 2 handlers + else + finally = 5
            Assert.AreEqual(5, pairs.Count);
            Assert.IsTrue(pairs.All(p => p.Kind == BlockKind.Try));
        }

        [TestMethod]
        public void WithBlock_OneTuple()
        {
            var pairs = Compute("with open('f') as f:\n    read(f)\n");
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual(BlockKind.With, pairs[0].Kind);
        }

        [TestMethod]
        public void MatchBlock_OneTuplePerCase()
        {
            string src = "match x:\n    case 1:\n        pass\n    case _:\n        pass\n";
            var pairs = Compute(src);
            // Each case body is one suite; match itself doesn't get a direct pair in our walk.
            Assert.AreEqual(2, pairs.Count);
            Assert.IsTrue(pairs.All(p => p.Kind == BlockKind.Match));
        }

        [TestMethod]
        public void IndentColumnMatchesFirstStatementColumn()
        {
            string src = "class A:\n    def f():\n        pass\n";
            var pairs = Compute(src);
            Assert.AreEqual(2, pairs.Count);
            // outer's body indent is 4 (def at column 4)
            Assert.AreEqual(4, pairs[0].IndentColumn);
            // inner's body indent is 8 (pass at column 8)
            Assert.AreEqual(8, pairs[1].IndentColumn);
        }

        [TestMethod]
        public void ParentIndentMatchesHeaderColumn()
        {
            string src = "class A:\n    def f():\n        pass\n";
            var pairs = Compute(src);
            Assert.AreEqual(0, pairs[0].ParentIndentColumn); // class at column 0
            Assert.AreEqual(4, pairs[1].ParentIndentColumn); // def at column 4
        }

        [TestMethod]
        public void ResultsSortedByHeaderLine()
        {
            string src = "if a:\n    x\nif b:\n    y\nif c:\n    z\n";
            var pairs = Compute(src);
            Assert.AreEqual(3, pairs.Count);
            Assert.IsTrue(pairs[0].HeaderLine < pairs[1].HeaderLine);
            Assert.IsTrue(pairs[1].HeaderLine < pairs[2].HeaderLine);
        }

        [TestMethod]
        public void LastContentLine_ClipsPastBlankLinesBetweenBlocks()
        {
            // Regression: the outer def's body ends at line 3 (`sim.initialize()`),
            // but the parser's Span.End for compound statements extends past blank
            // lines to where the next less-indented statement begins (DEDENT token
            // position). Without the text-based clip, the closer tick would land
            // below `def _run_phase` on line 5.
            string src =
                "class C:\n" +                   // 1
                "    def initialize(self):\n" +  // 2
                "        sim.initialize()\n" +   // 3
                "\n" +                           // 4 — blank
                "    def _run_phase(self):\n" +  // 5
                "        pass\n";                // 6
            var pairs = Compute(src);
            // We expect: class body (ends at the end of the last def which is line 6),
            // def initialize body (ends at line 3 after clipping),
            // def _run_phase body (ends at line 6).
            Assert.AreEqual(3, pairs.Count);
            var initPair = pairs.First(p => p.HeaderLine == 2);
            Assert.AreEqual(3, initPair.LastContentLine,
                "def initialize's LastContentLine must clip to the last non-blank line inside its body (3), not extend past blank line 4 into the next def.");
        }

        [TestMethod]
        public void LastContentLine_ClipsTrailingBlanksAtEndOfFile()
        {
            string src = "def f():\n    x = 1\n\n\n";
            var pairs = Compute(src);
            Assert.AreEqual(1, pairs.Count);
            Assert.AreEqual(2, pairs[0].LastContentLine,
                "Trailing blank lines past the last statement must not push the closer tick downwards.");
        }
    }
}
