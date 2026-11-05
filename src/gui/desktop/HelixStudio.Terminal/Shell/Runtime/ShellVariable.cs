namespace HelixPythonNamespace.Terminal.Shell.Runtime;

public sealed class ShellVariable
{
    public ShellVariable(string name) => Name = name;

    public string Name { get; }

    /// <summary>Scalar value. Null means declared-but-unset.</summary>
    public string? Value { get; set; }

    /// <summary>Non-null when the variable is an indexed array; <see cref="Value"/> then mirrors element 0.</summary>
    public List<string>? Elements { get; set; }

    public bool Exported { get; set; }

    public bool ReadOnly { get; set; }

    /// <summary>Declared with <c>declare -i</c>: assignments are evaluated arithmetically.</summary>
    public bool Integer { get; set; }

    public bool IsSet => Value is not null || Elements is not null;

    public bool IsArray => Elements is not null;

    public string AsScalar() => Elements is not null
        ? Elements.Count > 0 ? Elements[0] : string.Empty
        : Value ?? string.Empty;

    public IReadOnlyList<string> AsList()
    {
        if (Elements is not null) return Elements;
        return Value is null ? Array.Empty<string>() : new[] { Value };
    }
}
