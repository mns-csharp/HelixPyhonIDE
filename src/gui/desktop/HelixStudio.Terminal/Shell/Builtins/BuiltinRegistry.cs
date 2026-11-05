using HelixPythonNamespace.Terminal.Shell.Execution;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Builtins;

/// <summary>A command implemented in-process. <c>arguments[0]</c> is the name it was invoked under.</summary>
public delegate int BuiltinCommand(ShellContext context, IReadOnlyList<string> arguments, Interpreter interpreter);

public sealed class BuiltinRegistry
{
    private readonly Dictionary<string, BuiltinCommand> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _help = new(StringComparer.Ordinal);

    public IEnumerable<string> Names => _commands.Keys.OrderBy(n => n, StringComparer.Ordinal);

    public bool Contains(string name) => _commands.ContainsKey(name);

    public bool TryGet(string name, out BuiltinCommand? command)
    {
        var found = _commands.TryGetValue(name, out var value);
        command = value;
        return found;
    }

    public string HelpFor(string name) => _help.TryGetValue(name, out var text) ? text : name;

    public void Register(string name, string synopsis, BuiltinCommand command)
    {
        _commands[name] = command;
        _help[name] = synopsis;
    }

    public static BuiltinRegistry CreateDefault()
    {
        var registry = new BuiltinRegistry();
        ShellBuiltins.Register(registry);
        FileCommands.Register(registry);
        TextCommands.Register(registry);
        SystemCommands.Register(registry);
        return registry;
    }
}

/// <summary>Minimal getopt-style parsing shared by the built-in commands.</summary>
public sealed class CommandLine
{
    private CommandLine() { }

    public HashSet<char> Flags { get; } = new();

    public Dictionary<string, string?> LongOptions { get; } = new(StringComparer.Ordinal);

    public Dictionary<char, string> Values { get; } = new();

    public List<string> Operands { get; } = new();

    public bool Has(char flag) => Flags.Contains(flag);

    public bool HasLong(string name) => LongOptions.ContainsKey(name);

    public string? Value(char flag) => Values.TryGetValue(flag, out var value) ? value : null;

    /// <summary>
    /// Splits <paramref name="arguments"/> (excluding the command name) into flags and operands.
    /// Characters in <paramref name="flagsTakingValues"/> consume the rest of the argument, or the
    /// next one. Everything after <c>--</c> is an operand.
    /// </summary>
    public static CommandLine Parse(IReadOnlyList<string> arguments, string flagsTakingValues = "", bool stopAtFirstOperand = false)
    {
        var result = new CommandLine();
        var i = 1; // arguments[0] is the command name
        var noMoreOptions = false;

        for (; i < arguments.Count; i++)
        {
            var argument = arguments[i];

            if (noMoreOptions || argument.Length < 2 || argument[0] != '-')
            {
                result.Operands.Add(argument);
                if (stopAtFirstOperand) { noMoreOptions = true; }
                continue;
            }

            if (argument == "--") { noMoreOptions = true; continue; }

            if (argument.StartsWith("--"))
            {
                var body = argument[2..];
                var equals = body.IndexOf('=');
                if (equals >= 0) result.LongOptions[body[..equals]] = body[(equals + 1)..];
                else result.LongOptions[body] = null;
                continue;
            }

            for (var j = 1; j < argument.Length; j++)
            {
                var flag = argument[j];

                if (flagsTakingValues.Contains(flag))
                {
                    if (j + 1 < argument.Length)
                    {
                        result.Values[flag] = argument[(j + 1)..];
                    }
                    else if (i + 1 < arguments.Count)
                    {
                        result.Values[flag] = arguments[++i];
                    }
                    else
                    {
                        throw new ShellException($"-{flag}: option requires an argument");
                    }

                    result.Flags.Add(flag);
                    break;
                }

                result.Flags.Add(flag);
            }
        }

        return result;
    }
}
