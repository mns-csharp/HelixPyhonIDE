using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python.Lexing;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Tests
{
    [TestClass]
    public class MultilineFStringPositionTests
    {
        [TestMethod]
        public void ReplacementFieldAtFirstColumnHasOneBasedPosition()
        {
            const string source = "message = f\"\"\"\n{value}\n\"\"\"\n";
            var tokens = new PythonLexer(source, "generated.py").Tokenize();

            Token brace = tokens.First(t => t.Kind == TokenKind.LeftBrace);
            Assert.AreEqual(2, brace.Span.Start.Line);
            Assert.AreEqual(1, brace.Span.Start.Column);
        }

        [TestMethod]
        public void ConsecutiveNewlinesBeforeFirstColumnFieldDoNotCrash()
        {
            const string source = "message = f\"\"\"\n\n{value}\n\"\"\"\n";
            var tokens = new PythonLexer(source, "generated.py").Tokenize();

            Assert.IsTrue(tokens.Any(t => t.Kind == TokenKind.FStringEnd));
        }

        [DataTestMethod]
        [DataRow("\n")]
        [DataRow("\r\n")]
        public void EscapedPhysicalNewlineInsideStringStaysInOneToken(string newline)
        {
            string source = "message = \"first \\" + newline + "second\"\n";
            var lexer = new PythonLexer(source, "continuation.py");
            var tokens = lexer.Tokenize();

            Assert.AreEqual(1, tokens.Count(t => t.Kind == TokenKind.String));
            Assert.IsFalse(lexer.Diagnostics.Any(d =>
                d.Code == HelixPythonNamespace.Python.Diagnostics.DiagnosticCodes.UnterminatedString));
        }

        [TestMethod]
        public void BmpAndNonBmpUnicodeIdentifiersAreSingleNameTokens()
        {
            const string source = "θ = 1\n𐌘 = θ\n";
            var lexer = new PythonLexer(source, "unicode_names.py");
            var tokens = lexer.Tokenize();

            CollectionAssert.AreEqual(
                new[] { "θ", "𐌘", "θ" },
                tokens.Where(t => t.Kind == TokenKind.Name).Select(t => t.Text).ToArray());
            Assert.AreEqual(0, lexer.Diagnostics.Count);
        }

        [DataTestMethod]
        [DataRow("\n")]
        [DataRow("\r\n")]
        public void EscapedPhysicalNewlineInsideFStringStaysInOneToken(string newline)
        {
            string source = "message = f\"{value} \\" + newline + "suffix\"\n";
            var lexer = new PythonLexer(source, "f_continuation.py");
            var tokens = lexer.Tokenize();

            Assert.IsTrue(tokens.Any(t => t.Kind == TokenKind.FStringEnd));
            Assert.IsFalse(lexer.Diagnostics.Any(d =>
                d.Code == HelixPythonNamespace.Python.Diagnostics.DiagnosticCodes.UnterminatedString));
        }
    }
}
