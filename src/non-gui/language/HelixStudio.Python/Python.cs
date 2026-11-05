using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Python.Lexing;
using HelixPythonNamespace.Python.Parsing;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python
{
    /// <summary>
    /// Public facade for the HelixPythonNamespace.Python parser library. Entry points are
    /// deliberately static to keep callers stateless; internal services are
    /// instantiated per call.
    /// </summary>
    public static class Python
    {
        public static ParseResult ParseModule(string source, string fileName = null,
            System.Threading.CancellationToken cancel = default(System.Threading.CancellationToken))
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            PythonLexer lexer = new PythonLexer(source, fileName);
            IReadOnlyList<Token> tokens = lexer.Tokenize(cancel);
            PythonParser parser = new PythonParser(tokens, fileName);
            ModuleSyntax module = parser.ParseModule(cancel);
            List<Diagnostics.ParseDiagnostic> diags = new List<Diagnostics.ParseDiagnostic>();
            diags.AddRange(lexer.Diagnostics);
            diags.AddRange(parser.Diagnostics);
            return new ParseResult(module, diags);
        }

        public static ParseResult ParseModule(Stream source, string fileName = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            using (StreamReader reader = new StreamReader(source))
            {
                return ParseModule(reader.ReadToEnd(), fileName);
            }
        }

        public static IReadOnlyList<Token> Tokenize(string source, string fileName = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            PythonLexer lexer = new PythonLexer(source, fileName);
            return lexer.Tokenize();
        }
    }
}
