namespace HelixPythonNamespace.Terminal.Shell.Lexing;

public enum TokenType
{
    Word,

    /// <summary>A bare integer immediately in front of a redirection operator, as in <c>2&gt;file</c>.</summary>
    IoNumber,

    Newline,
    EndOfInput,

    /// <summary><c>((expression))</c> in command position; <see cref="Token.Text"/> holds the expression.</summary>
    ArithmeticCommand,

    /// <summary><c>[[ expression ]]</c>; <see cref="Token.Text"/> holds the raw inner text.</summary>
    ConditionalExpression,

    Semicolon,      // ;
    DoubleSemi,     // ;;
    Ampersand,      // &
    AndIf,          // &&
    Pipe,           // |
    OrIf,           // ||
    PipeAmp,        // |&
    LeftParen,      // (
    RightParen,     // )

    Less,           // <
    Great,          // >
    DGreat,         // >>
    Clobber,        // >|
    LessAnd,        // <&
    GreatAnd,       // >&
    LessGreat,      // <>
    DLess,          // <<
    DLessDash,      // <<-
    TripleLess,     // <<<
    AmpGreat,       // &>
    AmpDGreat,      // &>>
}

public sealed class Token
{
    public TokenType Type { get; init; }

    /// <summary>Raw source spelling; for operators this is the operator itself.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Set for <see cref="TokenType.Word"/>; null otherwise.</summary>
    public Word? Word { get; init; }

    /// <summary>Byte offset of the token start in the source, for error messages.</summary>
    public int Position { get; init; }

    /// <summary>
    /// True when no whitespace separated this token from the previous one. The parser needs
    /// this to tell <c>2&gt;file</c> (an io-number redirect) from <c>echo 2 &gt;file</c>.
    /// </summary>
    public bool AdjacentToPrevious { get; init; }

    /// <summary>Heredoc body, attached by the lexer once the following lines have been read.</summary>
    public HereDocument? HereDoc { get; set; }

    public bool IsOperator => Type is not (TokenType.Word or TokenType.IoNumber or TokenType.Newline or TokenType.EndOfInput);

    public override string ToString() => Type == TokenType.Word ? $"Word({Word})" : Type.ToString();
}

public sealed class HereDocument
{
    /// <summary>The delimiter word, as written (quoting matters).</summary>
    public string Delimiter { get; init; } = string.Empty;

    /// <summary>When the delimiter was quoted the body is taken literally, with no expansion.</summary>
    public bool Quoted { get; init; }

    /// <summary><c>&lt;&lt;-</c> strips leading tabs from the body and the delimiter line.</summary>
    public bool StripTabs { get; init; }

    /// <summary>Raw body text, still unexpanded.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// False when the input ended before the delimiter line appeared. An interactive front end
    /// uses this to know it should keep prompting for the rest of the here-document.
    /// </summary>
    public bool Terminated { get; set; }
}
