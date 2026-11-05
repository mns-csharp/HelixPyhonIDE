namespace HelixPythonNamespace.Python.Syntax
{
    public enum TokenKind
    {
        // Special / structural
        EndOfFile,
        NewLine,
        Indent,
        Dedent,
        Comment,
        Whitespace,
        LineContinuation,

        // Literals
        Name,
        Number,
        String,
        FStringStart,
        FStringMiddle,
        FStringEnd,

        // Keywords (Python 3.12 complete set)
        KwFalse, KwNone, KwTrue,
        KwAnd, KwAs, KwAssert, KwAsync, KwAwait,
        KwBreak, KwClass, KwContinue,
        KwDef, KwDel,
        KwElif, KwElse, KwExcept,
        KwFinally, KwFor, KwFrom,
        KwGlobal, KwIf, KwImport, KwIn, KwIs,
        KwLambda, KwNonlocal, KwNot,
        KwOr, KwPass,
        KwRaise, KwReturn,
        KwTry, KwWhile, KwWith, KwYield,
        // Soft keywords (contextual; lexer emits as Name and parser promotes)
        // match, case, _, type — NOT dedicated token kinds.

        // Operators and punctuation
        Plus, Minus, Star, DoubleStar, Slash, DoubleSlash, Percent, At,
        Amp, Pipe, Caret, Tilde, LeftShift, RightShift,
        Less, Greater, LessEqual, GreaterEqual, EqualEqual, NotEqual,
        Assign, PlusAssign, MinusAssign, StarAssign, DoubleStarAssign,
        SlashAssign, DoubleSlashAssign, PercentAssign, AtAssign,
        AmpAssign, PipeAssign, CaretAssign,
        LeftShiftAssign, RightShiftAssign,
        LeftParen, RightParen, LeftBracket, RightBracket, LeftBrace, RightBrace,
        Comma, Colon, Semicolon, Dot, Ellipsis, Arrow, Walrus,

        // Error
        Unknown
    }

    internal static class TokenKinds
    {
        public static bool IsKeyword(TokenKind kind)
        {
            return kind >= TokenKind.KwFalse && kind <= TokenKind.KwYield;
        }

        public static bool IsAugmentedAssign(TokenKind kind)
        {
            switch (kind)
            {
                case TokenKind.PlusAssign:
                case TokenKind.MinusAssign:
                case TokenKind.StarAssign:
                case TokenKind.DoubleStarAssign:
                case TokenKind.SlashAssign:
                case TokenKind.DoubleSlashAssign:
                case TokenKind.PercentAssign:
                case TokenKind.AtAssign:
                case TokenKind.AmpAssign:
                case TokenKind.PipeAssign:
                case TokenKind.CaretAssign:
                case TokenKind.LeftShiftAssign:
                case TokenKind.RightShiftAssign:
                    return true;
                default:
                    return false;
            }
        }
    }
}
