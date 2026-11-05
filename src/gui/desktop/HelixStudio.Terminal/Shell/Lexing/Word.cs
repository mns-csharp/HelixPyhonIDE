namespace HelixPythonNamespace.Terminal.Shell.Lexing;

/// <summary>
/// A word is the sequence of pieces the lexer found between delimiters. Expansion happens
/// later, so each piece remembers whether it was written inside quotes: that single bit is
/// what decides, after expansion, whether the resulting text may be split on IFS and glob.
/// </summary>
public sealed class Word
{
    public List<WordPart> Parts { get; } = new();

    public Word() { }

    public Word(IEnumerable<WordPart> parts) => Parts.AddRange(parts);

    public static Word Literal(string text) => new(new[] { (WordPart)new LiteralPart(text, false) });

    public bool IsEmpty => Parts.Count == 0;

    /// <summary>True when the word is a single unquoted literal, e.g. a reserved word or an operator-ish name.</summary>
    public bool TryGetPlainLiteral(out string text)
    {
        if (Parts.Count == 1 && Parts[0] is LiteralPart { Quoted: false } lit)
        {
            text = lit.Text;
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>The literal spelling of the word ignoring quoting; used for reserved-word and assignment detection.</summary>
    public string LiteralText
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in Parts)
            {
                if (part is LiteralPart lit) sb.Append(lit.Text);
                else return string.Empty; // contains an expansion: not a plain literal
            }

            return sb.ToString();
        }
    }

    public override string ToString() => string.Concat(Parts.Select(p => p.ToString()));
}

public abstract class WordPart
{
    /// <summary>Whether this piece was written inside quotes (suppresses field splitting and globbing).</summary>
    public bool Quoted { get; init; }
}

/// <summary>Plain text. When unquoted it may still contain glob metacharacters.</summary>
public sealed class LiteralPart : WordPart
{
    public string Text { get; }

    public LiteralPart(string text, bool quoted)
    {
        Text = text;
        Quoted = quoted;
    }

    public override string ToString() => Text;
}

/// <summary>A leading <c>~</c> or <c>~user</c>.</summary>
public sealed class TildePart : WordPart
{
    public string User { get; }

    public TildePart(string user) => User = user;

    public override string ToString() => "~" + User;
}

public enum ParameterOperator
{
    None,

    /// <summary>${v:-w} / ${v-w}</summary>
    UseDefault,

    /// <summary>${v:=w} / ${v=w}</summary>
    AssignDefault,

    /// <summary>${v:?w} / ${v?w}</summary>
    ErrorIfUnset,

    /// <summary>${v:+w} / ${v+w}</summary>
    UseAlternate,

    /// <summary>${v#w}</summary>
    RemoveSmallestPrefix,

    /// <summary>${v##w}</summary>
    RemoveLargestPrefix,

    /// <summary>${v%w}</summary>
    RemoveSmallestSuffix,

    /// <summary>${v%%w}</summary>
    RemoveLargestSuffix,

    /// <summary>${v/pat/rep}</summary>
    ReplaceFirst,

    /// <summary>${v//pat/rep}</summary>
    ReplaceAll,

    /// <summary>${v:offset:length}</summary>
    Substring,

    /// <summary>${v^^} / ${v^}</summary>
    UpperCase,

    /// <summary>${v,,} / ${v,}</summary>
    LowerCase,
}

/// <summary>A <c>$name</c> or <c>${...}</c> reference.</summary>
public sealed class ParameterPart : WordPart
{
    public string Name { get; init; } = string.Empty;

    public ParameterOperator Operator { get; init; } = ParameterOperator.None;

    /// <summary>The word after the operator (the default value, pattern, ...).</summary>
    public Word? Argument { get; init; }

    /// <summary>The replacement half of ${v/pat/rep}.</summary>
    public Word? Replacement { get; init; }

    /// <summary>${#v} — expand to the length rather than the value.</summary>
    public bool Length { get; init; }

    /// <summary>True for the ':' variants, which treat an empty value like an unset one.</summary>
    public bool ColonForm { get; init; }

    /// <summary>Only applies to <see cref="ParameterOperator.UpperCase"/>/<see cref="ParameterOperator.LowerCase"/>.</summary>
    public bool AllCharacters { get; init; }

    /// <summary>${v[@]} / ${v[*]} — array subscript, or null when not an array reference.</summary>
    public string? Subscript { get; init; }

    /// <summary>${!v} — the value of <c>v</c> names the parameter to expand.</summary>
    public bool Indirect { get; init; }

    public override string ToString() => "${" + Name + "}";
}

/// <summary><c>$(script)</c> or <c>`script`</c>. The body is re-parsed at expansion time.</summary>
public sealed class CommandSubstitutionPart : WordPart
{
    public string Script { get; }

    public CommandSubstitutionPart(string script) => Script = script;

    public override string ToString() => "$(" + Script + ")";
}

/// <summary><c>$((expression))</c>.</summary>
public sealed class ArithmeticPart : WordPart
{
    public string Expression { get; }

    public ArithmeticPart(string expression) => Expression = expression;

    public override string ToString() => "$((" + Expression + "))";
}

/// <summary><c>&lt;(cmd)</c> or <c>&gt;(cmd)</c> — expands to a temp file path holding the output.</summary>
public sealed class ProcessSubstitutionPart : WordPart
{
    public string Script { get; }

    public bool Input { get; }

    public ProcessSubstitutionPart(string script, bool input)
    {
        Script = script;
        Input = input;
    }

    public override string ToString() => (Input ? "<(" : ">(") + Script + ")";
}
