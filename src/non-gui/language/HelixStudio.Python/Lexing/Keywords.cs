using System.Collections.Generic;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Python.Lexing
{
    internal static class Keywords
    {
        public static readonly IReadOnlyDictionary<string, TokenKind> Map = BuildMap();

        private static Dictionary<string, TokenKind> BuildMap()
        {
            return new Dictionary<string, TokenKind>
            {
                { "False",    TokenKind.KwFalse },
                { "None",     TokenKind.KwNone },
                { "True",     TokenKind.KwTrue },
                { "and",      TokenKind.KwAnd },
                { "as",       TokenKind.KwAs },
                { "assert",   TokenKind.KwAssert },
                { "async",    TokenKind.KwAsync },
                { "await",    TokenKind.KwAwait },
                { "break",    TokenKind.KwBreak },
                { "class",    TokenKind.KwClass },
                { "continue", TokenKind.KwContinue },
                { "def",      TokenKind.KwDef },
                { "del",      TokenKind.KwDel },
                { "elif",     TokenKind.KwElif },
                { "else",     TokenKind.KwElse },
                { "except",   TokenKind.KwExcept },
                { "finally",  TokenKind.KwFinally },
                { "for",      TokenKind.KwFor },
                { "from",     TokenKind.KwFrom },
                { "global",   TokenKind.KwGlobal },
                { "if",       TokenKind.KwIf },
                { "import",   TokenKind.KwImport },
                { "in",       TokenKind.KwIn },
                { "is",       TokenKind.KwIs },
                { "lambda",   TokenKind.KwLambda },
                { "nonlocal", TokenKind.KwNonlocal },
                { "not",      TokenKind.KwNot },
                { "or",       TokenKind.KwOr },
                { "pass",     TokenKind.KwPass },
                { "raise",    TokenKind.KwRaise },
                { "return",   TokenKind.KwReturn },
                { "try",      TokenKind.KwTry },
                { "while",    TokenKind.KwWhile },
                { "with",     TokenKind.KwWith },
                { "yield",    TokenKind.KwYield }
            };
        }
    }
}
