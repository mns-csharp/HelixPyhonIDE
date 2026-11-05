using System.Text.Json;

namespace HelixPythonNamespace.Terminal.Shell.Suggestions;

/// <summary>What an argument or an option's value is, and so what to suggest for it.</summary>
public static class ArgumentKinds
{
    public const string Folder = "folder";
    public const string File = "file";
    public const string Path = "path";
    public const string Variable = "variable";
    public const string Job = "job";
    public const string Command = "command";
    public const string Text = "text";
    public const string Choice = "choice";
    public const string GitBranch = "git-branch";
    public const string GitFile = "git-file";
    public const string GitRemote = "git-remote";

    /// <summary>Kinds whose values come from running a program, which is slow and may fail.</summary>
    public static bool IsGenerator(string kind) => kind.StartsWith("git-", StringComparison.Ordinal);
}

public sealed class ChoiceSpec
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

public sealed class OptionSpec
{
    /// <summary>Every spelling of the one option, e.g. <c>-l</c> and <c>--long</c>.</summary>
    public List<string> Names { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    /// <summary>The kind of value the option takes, or null when it takes none.</summary>
    public string? Value { get; set; }

    public List<ChoiceSpec> Choices { get; set; } = new();
}

public sealed class ArgumentSpec
{
    public string Kind { get; set; } = ArgumentKinds.Path;

    /// <summary>True when this argument repeats; only meaningful on the last one.</summary>
    public bool Many { get; set; }

    public List<ChoiceSpec> Choices { get; set; } = new();
}

/// <summary>
/// What the suggestions know about one command: its options, its arguments and its subcommands,
/// which are commands of their own so that <c>git remote add</c> nests. Specs are JSON so that
/// another program can be taught to the shell by adding a file rather than rebuilding it.
/// </summary>
public sealed class CommandSpec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string Name { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    /// <summary>A usage line for the side pane; builtins fall back to their registered synopsis.</summary>
    public string Synopsis { get; set; } = string.Empty;

    public List<OptionSpec> Options { get; set; } = new();

    public List<ArgumentSpec> Args { get; set; } = new();

    public List<CommandSpec> Subcommands { get; set; } = new();

    public static CommandSpec Parse(string json)
    {
        var spec = JsonSerializer.Deserialize<CommandSpec>(json, JsonOptions)
                   ?? throw new JsonException("the file is empty");

        if (string.IsNullOrWhiteSpace(spec.Name)) throw new JsonException("the spec has no \"name\"");
        return spec;
    }

    public CommandSpec? Subcommand(string name) =>
        Subcommands.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) ||
            s.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));

    public OptionSpec? Option(string name) =>
        Options.FirstOrDefault(o => o.Names.Contains(name, StringComparer.Ordinal));

    /// <summary>The positional argument at <paramref name="index"/>; a last argument marked Many repeats.</summary>
    public ArgumentSpec? Argument(int index)
    {
        if (Args.Count == 0) return null;
        if (index < Args.Count) return Args[index];
        return Args[^1].Many ? Args[^1] : null;
    }
}
