namespace HelixPythonNamespace.Python.Tests
{
    using System;
    using System.Text;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Python.Lexing;

    /// <summary>
    /// Regression tests for cooperative cancellation in the lexer and parser. These guard the
    /// "Cancel does nothing" bug: a long tokenize/parse of a single large file must observe an
    /// already-tripped token and abort with <see cref="OperationCanceledException"/> rather than
    /// running the whole file to completion.
    /// </summary>
    [TestClass]
    public class CancellationTests
    {
        // A source large enough to cross the lexer's periodic cancellation check (every ~8192
        // loop iterations) — so a cancelled token is observed mid-lex, not only at end-of-file.
        private static string BigSource(int lines)
        {
            StringBuilder sb = new StringBuilder(lines * 12);
            for (int i = 0; i < lines; i++) sb.Append("x").Append(i).Append(" = ").Append(i).Append("\n");
            return sb.ToString();
        }

        private static CancellationToken AlreadyCancelled()
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();
            return cts.Token;
        }

        [TestMethod]
        public void Lexer_HonorsAlreadyCancelledToken()
        {
            string source = BigSource(5000); // ~50k chars → well past the 8192-iteration check
            PythonLexer lexer = new PythonLexer(source, "big.py");
            Assert.ThrowsException<OperationCanceledException>(() => lexer.Tokenize(AlreadyCancelled()));
        }

        [TestMethod]
        public void Lexer_NoToken_TokenizesFully()
        {
            // The default (no token) overload must remain non-cancelling for existing callers.
            PythonLexer lexer = new PythonLexer(BigSource(200), "ok.py");
            Assert.IsTrue(lexer.Tokenize().Count > 0);
        }

        [TestMethod]
        public void Parser_HonorsAlreadyCancelledToken()
        {
            Assert.ThrowsException<OperationCanceledException>(
                () => Python.ParseModule(BigSource(2000), "big.py", AlreadyCancelled()));
        }

        [TestMethod]
        public void Parser_NoToken_ParsesFully()
        {
            ParseResult result = Python.ParseModule(BigSource(100), "ok.py");
            Assert.IsNotNull(result.Module);
        }
    }
}
