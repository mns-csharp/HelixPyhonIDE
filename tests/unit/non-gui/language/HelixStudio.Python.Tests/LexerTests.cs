using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Tests
{
    [TestClass]
    public class LexerTests
    {
        private static List<Token> TokenizeSignificant(string source)
        {
            IReadOnlyList<Token> all = Python.Tokenize(source);
            List<Token> list = new List<Token>();
            foreach (Token t in all)
            {
                if (t.Kind == TokenKind.Whitespace || t.Kind == TokenKind.Comment) continue;
                list.Add(t);
            }
            return list;
        }

        [TestMethod]
        public void TokenizesKeywords()
        {
            List<Token> toks = TokenizeSignificant("class def return if else while for True False None");
            TokenKind[] keywords = toks
                .Where(t => t.Kind != TokenKind.NewLine && t.Kind != TokenKind.EndOfFile)
                .Select(t => t.Kind)
                .ToArray();
            CollectionAssert.AreEqual(new[]
            {
                TokenKind.KwClass, TokenKind.KwDef, TokenKind.KwReturn,
                TokenKind.KwIf, TokenKind.KwElse, TokenKind.KwWhile, TokenKind.KwFor,
                TokenKind.KwTrue, TokenKind.KwFalse, TokenKind.KwNone
            }, keywords);
        }

        [TestMethod]
        public void TokenizesIdentifiers()
        {
            List<Token> toks = TokenizeSignificant("foo _bar __baz CamelCase _123");
            Assert.AreEqual(TokenKind.Name, toks[0].Kind);
            Assert.AreEqual("foo", toks[0].Text);
            Assert.AreEqual("_bar", toks[1].Text);
            Assert.AreEqual("__baz", toks[2].Text);
            Assert.AreEqual("CamelCase", toks[3].Text);
            Assert.AreEqual("_123", toks[4].Text);
        }

        [TestMethod]
        public void TokenizesNumbers()
        {
            List<Token> toks = TokenizeSignificant("0 1 42 3.14 .5 1e10 1_000_000 0x1F 0o755 0b1010 1j");
            Assert.IsTrue(toks.All(t => t.Kind == TokenKind.Number || t.Kind == TokenKind.NewLine || t.Kind == TokenKind.EndOfFile));
            Assert.AreEqual("1_000_000", toks.First(t => t.Text.Contains("_")).Text);
            Assert.AreEqual("0x1F", toks.First(t => t.Text.StartsWith("0x")).Text);
            Assert.AreEqual("0o755", toks.First(t => t.Text.StartsWith("0o")).Text);
            Assert.AreEqual("0b1010", toks.First(t => t.Text.StartsWith("0b")).Text);
        }

        [TestMethod]
        public void TokenizesOperatorsWithGreedyMatching()
        {
            List<Token> toks = TokenizeSignificant("a += b; x **= 2; y //= 3; z <<= 1; q >>= 1");
            TokenKind[] ops = toks.Where(t => t.Kind != TokenKind.Name && t.Kind != TokenKind.Number
                && t.Kind != TokenKind.Semicolon && t.Kind != TokenKind.NewLine && t.Kind != TokenKind.EndOfFile)
                .Select(t => t.Kind).ToArray();
            CollectionAssert.AreEqual(new[]
            {
                TokenKind.PlusAssign, TokenKind.DoubleStarAssign, TokenKind.DoubleSlashAssign,
                TokenKind.LeftShiftAssign, TokenKind.RightShiftAssign
            }, ops);
        }

        [TestMethod]
        public void TokenizesArrowAndWalrus()
        {
            List<Token> toks = TokenizeSignificant("def f() -> int: x := 1");
            Assert.IsTrue(toks.Any(t => t.Kind == TokenKind.Arrow));
            Assert.IsTrue(toks.Any(t => t.Kind == TokenKind.Walrus));
        }

        [TestMethod]
        public void TokenizesEllipsis()
        {
            List<Token> toks = TokenizeSignificant("...");
            Assert.AreEqual(TokenKind.Ellipsis, toks[0].Kind);
        }

        [TestMethod]
        public void TokenizesStrings()
        {
            List<Token> toks = TokenizeSignificant("\"hello\" 'world' \"\"\"triple\"\"\" r\"raw\" b'bytes'");
            Token[] strs = toks.Where(t => t.Kind == TokenKind.String).ToArray();
            Assert.AreEqual(5, strs.Length);
        }

        /// <summary>
        /// Iter 358 (requests stress-test): a backslash before a quote does not close a raw string —
        /// r'x\'y' is one string whose content is x\'y, per CPython ("even in a raw literal, quotes
        /// can be escaped with a backslash, but the backslash remains in the result"). The lexer
        /// gated escape handling on !isRaw, so it closed at the \' and the rest of the line became
        /// garbage tokens — ~128 phantom parse errors on requests/utils.py's three regex lines.
        /// </summary>
        [TestMethod]
        public void RawString_BackslashEscapedQuote_DoesNotTerminate()
        {
            // Source characters:  a = r'x\'y'
            List<Token> toks = TokenizeSignificant("a = r'x\\'y'\n");
            Token[] strs = toks.Where(t => t.Kind == TokenKind.String).ToArray();

            Assert.AreEqual(1, strs.Length, "the whole r'x\\'y' is a single string token");
            Assert.AreEqual("x\\'y", strs[0].Value as string, "content keeps the backslash and the quote");
            // No stray Name 'y' leaked out, and no second (unterminated) string opened.
            Assert.IsFalse(toks.Any(t => t.Kind == TokenKind.Name && t.Text == "y"),
                "nothing after the escaped quote should leak into the token stream");
        }

        /// <summary>
        /// Iter 482 (django stress-test): a backslash before the closing quotes of a TRIPLE string does
        /// not close it one quote early — `"""... \""""` (staticfiles/storage.py:78) is one string whose
        /// content ends with an escaped quote, not a triple-quote followed by a stray quote.
        /// </summary>
        [TestMethod]
        public void TripleString_BackslashEscapedQuoteBeforeClose_IsOneString()
        {
            // Source characters:  x = """ab\"""" then  y = 1  (the \" is content, then """ closes).
            List<Token> toks = TokenizeSignificant("x = \"\"\"ab\\\"\"\"\"\ny = 1\n");
            Token[] strs = toks.Where(t => t.Kind == TokenKind.String).ToArray();

            Assert.AreEqual(1, strs.Length, "the whole triple literal is a single string token");
            Assert.AreEqual("ab\\\"", strs[0].Value as string, "content keeps the escaped quote");
            // The following `y = 1` must survive as real tokens, not be swallowed by a runaway string.
            Assert.IsTrue(toks.Any(t => t.Kind == TokenKind.Name && t.Text == "y"),
                "code after the triple string must not be swallowed");
        }

        [TestMethod]
        public void TripleString_EscapedQuoteInMiddle_TokenizesCleanly()
        {
            List<Token> toks = TokenizeSignificant("s = \"\"\"a \\\" b\"\"\"\n");
            Token[] strs = toks.Where(t => t.Kind == TokenKind.String).ToArray();
            Assert.AreEqual(1, strs.Length);
            Assert.AreEqual("a \\\" b", strs[0].Value as string);
        }

        /// <summary>The idiomatic requests regex that triggered the cascade tokenizes cleanly.</summary>
        [TestMethod]
        public void RawString_CharClassWithEscapedQuote_TokenizesAsOneString()
        {
            // r'<meta.*?charset=["\']*(.+?)'
            List<Token> toks = TokenizeSignificant("x = r'<meta.*?charset=[\"\\']*(.+?)'\n");
            Token[] strs = toks.Where(t => t.Kind == TokenKind.String).ToArray();
            Assert.AreEqual(1, strs.Length);
        }

        /// <summary>Non-raw escaped quotes were already correct — guard against a regression.</summary>
        [TestMethod]
        public void NonRawString_EscapedQuote_StillDoesNotTerminate()
        {
            List<Token> toks = TokenizeSignificant("a = 'x\\'y'\n");
            Token[] strs = toks.Where(t => t.Kind == TokenKind.String).ToArray();
            Assert.AreEqual(1, strs.Length);
        }

        [TestMethod]
        public void TokenizesFString()
        {
            List<Token> toks = TokenizeSignificant("f\"hello {name}!\"");
            Assert.AreEqual(TokenKind.FStringStart, toks[0].Kind);
            Assert.IsTrue(toks.Any(t => t.Kind == TokenKind.FStringMiddle));
            Assert.IsTrue(toks.Any(t => t.Kind == TokenKind.LeftBrace));
            Assert.IsTrue(toks.Any(t => t.Kind == TokenKind.Name && t.Text == "name"));
            Assert.IsTrue(toks.Any(t => t.Kind == TokenKind.RightBrace));
            Assert.IsTrue(toks.Any(t => t.Kind == TokenKind.FStringEnd));
        }

        [TestMethod]
        public void IndentationProducesIndentAndDedent()
        {
            string src = "class A:\n    def foo(self):\n        pass\n";
            List<Token> toks = TokenizeSignificant(src);
            int indents = toks.Count(t => t.Kind == TokenKind.Indent);
            int dedents = toks.Count(t => t.Kind == TokenKind.Dedent);
            Assert.AreEqual(2, indents);
            Assert.AreEqual(2, dedents);
        }

        [TestMethod]
        public void BracketsSuppressImplicitNewline()
        {
            string src = "x = (\n    1,\n    2,\n)\n";
            List<Token> toks = TokenizeSignificant(src);
            // Only one NEWLINE (the one after the closing paren).
            int newlines = toks.Count(t => t.Kind == TokenKind.NewLine);
            Assert.AreEqual(1, newlines);
        }

        [TestMethod]
        public void LineContinuationIsTrivia()
        {
            string src = "x = 1 + \\\n    2\n";
            IReadOnlyList<Token> all = Python.Tokenize(src);
            Assert.IsTrue(all.Any(t => t.Kind == TokenKind.LineContinuation));
        }

        [TestMethod]
        public void CommentsArePreservedAsTrivia()
        {
            string src = "# header\nx = 1  # tail\n";
            IReadOnlyList<Token> all = Python.Tokenize(src);
            int comments = all.Count(t => t.Kind == TokenKind.Comment);
            Assert.AreEqual(2, comments);
        }

        [TestMethod]
        public void SoftKeywordsAreNames()
        {
            // match / case / type / _ are soft keywords in 3.12; the lexer must emit Name.
            List<Token> toks = TokenizeSignificant("match case type _");
            foreach (Token t in toks.Take(4)) Assert.AreEqual(TokenKind.Name, t.Kind);
        }
    }
}
