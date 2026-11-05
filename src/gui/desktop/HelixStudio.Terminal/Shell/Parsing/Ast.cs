using HelixPythonNamespace.Terminal.Shell.Lexing;

namespace HelixPythonNamespace.Terminal.Shell.Parsing;

public abstract class Node;

// ------------------------------------------------------------------ lists

/// <summary>A sequence of and-or lists separated by <c>;</c>, <c>&amp;</c> or newlines.</summary>
public sealed class CompoundList : Node
{
    public List<AndOrList> Items { get; } = new();
}

public enum AndOrOperator
{
    And, // &&
    Or,  // ||
}

public sealed class AndOrList : Node
{
    public Pipeline First { get; init; } = null!;

    public List<(AndOrOperator Operator, Pipeline Pipeline)> Rest { get; } = new();

    /// <summary>Terminated with <c>&amp;</c>: run without waiting.</summary>
    public bool Background { get; set; }
}

public sealed class Pipeline : Node
{
    public bool Negated { get; set; }

    /// <summary>Prefixed with the <c>time</c> keyword.</summary>
    public bool Timed { get; set; }

    public List<PipelineElement> Elements { get; } = new();
}

public sealed class PipelineElement
{
    public Command Command { get; init; } = null!;

    /// <summary>True when the connection to the *next* element was <c>|&amp;</c>, i.e. stderr is piped too.</summary>
    public bool PipeStandardError { get; set; }
}

// --------------------------------------------------------------- commands

public abstract class Command : Node
{
    public List<Redirection> Redirections { get; } = new();
}

public sealed class Assignment
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Null for a bare <c>NAME=</c>.</summary>
    public Word? Value { get; init; }

    /// <summary><c>NAME+=value</c>.</summary>
    public bool Append { get; init; }

    /// <summary><c>NAME[i]=value</c>.</summary>
    public string? Subscript { get; init; }

    /// <summary><c>NAME=(a b c)</c>.</summary>
    public List<Word>? ArrayItems { get; init; }
}

public sealed class SimpleCommand : Command
{
    public List<Assignment> Assignments { get; } = new();

    public List<Word> Words { get; } = new();
}

public sealed class SubshellCommand : Command
{
    public CompoundList Body { get; init; } = null!;
}

public sealed class GroupCommand : Command
{
    public CompoundList Body { get; init; } = null!;
}

public sealed class IfCommand : Command
{
    public List<(CompoundList Condition, CompoundList Body)> Branches { get; } = new();

    public CompoundList? ElseBody { get; set; }
}

public sealed class WhileCommand : Command
{
    public CompoundList Condition { get; init; } = null!;

    public CompoundList Body { get; init; } = null!;

    /// <summary>True for <c>until</c>, which inverts the condition.</summary>
    public bool Until { get; init; }
}

public sealed class ForCommand : Command
{
    public string Variable { get; init; } = string.Empty;

    /// <summary>Null means "iterate over the positional parameters".</summary>
    public List<Word>? Items { get; init; }

    public CompoundList Body { get; init; } = null!;
}

/// <summary><c>for ((init; cond; update)); do ...; done</c>.</summary>
public sealed class ForArithmeticCommand : Command
{
    public string Initialiser { get; init; } = string.Empty;

    public string Condition { get; init; } = string.Empty;

    public string Update { get; init; } = string.Empty;

    public CompoundList Body { get; init; } = null!;
}

public enum CaseTerminator
{
    /// <summary><c>;;</c> — stop after this clause.</summary>
    Break,

    /// <summary><c>;;&amp;</c> — keep testing the remaining patterns.</summary>
    ContinueTesting,

    /// <summary><c>;&amp;</c> — fall through into the next clause's body.</summary>
    FallThrough,
}

public sealed class CaseClause
{
    public List<Word> Patterns { get; } = new();

    public CompoundList Body { get; init; } = null!;

    public CaseTerminator Terminator { get; set; } = CaseTerminator.Break;
}

public sealed class CaseCommand : Command
{
    public Word Subject { get; init; } = null!;

    public List<CaseClause> Clauses { get; } = new();
}

public sealed class FunctionDefinition : Command
{
    public string Name { get; init; } = string.Empty;

    public Command Body { get; init; } = null!;
}

/// <summary><c>((expression))</c>: exit status 0 when the value is non-zero.</summary>
public sealed class ArithmeticCommand : Command
{
    public string Expression { get; init; } = string.Empty;
}

/// <summary><c>[[ ... ]]</c>.</summary>
public sealed class ConditionalCommand : Command
{
    public ConditionalNode Expression { get; init; } = null!;
}

// ------------------------------------------------------- [[ ]] expressions

public abstract class ConditionalNode;

public sealed class ConditionalWord : ConditionalNode
{
    public Word Word { get; init; } = null!;
}

public sealed class ConditionalUnary : ConditionalNode
{
    public string Operator { get; init; } = string.Empty;

    public Word Operand { get; init; } = null!;
}

public sealed class ConditionalBinary : ConditionalNode
{
    public string Operator { get; init; } = string.Empty;

    public Word Left { get; init; } = null!;

    public Word Right { get; init; } = null!;

    /// <summary>For <c>==</c>/<c>!=</c>: whether the right side was quoted, which makes it a literal instead of a pattern.</summary>
    public bool RightIsLiteral { get; init; }
}

public sealed class ConditionalNot : ConditionalNode
{
    public ConditionalNode Operand { get; init; } = null!;
}

public sealed class ConditionalAndOr : ConditionalNode
{
    public bool IsAnd { get; init; }

    public ConditionalNode Left { get; init; } = null!;

    public ConditionalNode Right { get; init; } = null!;
}

// ----------------------------------------------------------- redirections

public enum RedirectionType
{
    Input,              // <
    Output,             // >
    Append,             // >>
    Clobber,            // >|
    ReadWrite,          // <>
    DuplicateInput,     // <&n
    DuplicateOutput,    // >&n
    CloseDescriptor,    // <&- / >&-
    HereDocument,       // << / <<-
    HereString,         // <<<
    OutputAndError,     // &> / >&file
    AppendOutputAndError, // &>>
}

public sealed class Redirection
{
    /// <summary>Explicit file descriptor number, or null for the operator's default (0 for input, 1 for output).</summary>
    public int? FileDescriptor { get; init; }

    public RedirectionType Type { get; init; }

    /// <summary>Target filename, here-string text, or null for descriptor duplication.</summary>
    public Word? Target { get; init; }

    /// <summary>The source descriptor for <c>&gt;&amp;n</c>.</summary>
    public int? TargetDescriptor { get; init; }

    public HereDocument? HereDocument { get; init; }
}
