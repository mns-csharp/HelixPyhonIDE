using System.Globalization;
using System.Text;
using HelixPythonNamespace.Terminal.Shell.Execution;
using HelixPythonNamespace.Terminal.Shell.Expansion;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Builtins;

/// <summary>The commands that have to live inside the shell because they change its state.</summary>
public static class ShellBuiltins
{
    public static void Register(BuiltinRegistry registry)
    {
        registry.Register(":", ": [args]", (_, _, _) => 0);
        registry.Register("true", "true", (_, _, _) => 0);
        registry.Register("false", "false", (_, _, _) => 1);

        registry.Register("cd", "cd [-|dir]", Cd);
        registry.Register("pwd", "pwd [-P]", Pwd);
        registry.Register("echo", "echo [-neE] [args...]", Echo);
        registry.Register("printf", "printf format [args...]", Printf);
        registry.Register("export", "export [-n] [name[=value] ...]", Export);
        registry.Register("unset", "unset [-fv] name ...", Unset);
        registry.Register("set", "set [-abefnuvxC] [-o option] [--] [args...]", Set);
        registry.Register("shopt", "shopt [-su] [optname ...]", Shopt);
        registry.Register("alias", "alias [name[=value] ...]", Alias);
        registry.Register("unalias", "unalias [-a] name ...", Unalias);
        registry.Register("source", "source filename [args]", Source);
        registry.Register(".", ". filename [args]", Source);
        registry.Register("eval", "eval [args ...]", Eval);
        registry.Register("exec", "exec command [args]", Exec);
        registry.Register("exit", "exit [n]", Exit);
        registry.Register("logout", "logout [n]", Exit);
        registry.Register("return", "return [n]", Return);
        registry.Register("break", "break [n]", Break);
        registry.Register("continue", "continue [n]", Continue);
        registry.Register("shift", "shift [n]", Shift);
        registry.Register("local", "local name[=value] ...", Local);
        registry.Register("declare", "declare [-airx] [name[=value] ...]", Declare);
        registry.Register("typeset", "typeset [-airx] [name[=value] ...]", Declare);
        registry.Register("readonly", "readonly [name[=value] ...]", ReadOnly);
        registry.Register("read", "read [-rs] [-p prompt] [-a array] [-d delim] [-n count] [name ...]", Read);
        registry.Register("test", "test expression", Test);
        registry.Register("[", "[ expression ]", Test);
        registry.Register("type", "type [-t] name ...", Type);
        registry.Register("which", "which name ...", Which);
        registry.Register("command", "command [-v] name [args]", CommandBuiltin);
        registry.Register("builtin", "builtin name [args]", BuiltinBuiltin);
        registry.Register("help", "help [pattern]", Help);
        registry.Register("history", "history [-c] [n]", History);
        registry.Register("jobs", "jobs", Jobs);
        registry.Register("fg", "fg [%n]", Foreground);
        registry.Register("bg", "bg [%n]", Background);
        registry.Register("wait", "wait [%n]", Wait);
        registry.Register("kill", "kill [-signal] pid | %job", Kill);
        registry.Register("trap", "trap [action] [signal ...]", Trap);
        registry.Register("let", "let expression [expression ...]", Let);
        registry.Register("dirs", "dirs [-c]", Dirs);
        registry.Register("pushd", "pushd [dir]", Pushd);
        registry.Register("popd", "popd", Popd);
        registry.Register("getopts", "getopts optstring name [args]", GetOpts);
        registry.Register("mapfile", "mapfile [-t] array", MapFile);
        registry.Register("readarray", "readarray [-t] array", MapFile);
        registry.Register("times", "times", Times);
        registry.Register("umask", "umask [mask]", (_, _, _) => 0);
        registry.Register("hash", "hash", (_, _, _) => 0);
        registry.Register("ulimit", "ulimit", (_, _, _) => 0);
    }

    // ------------------------------------------------------------ navigation

    private static int Cd(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var target = args.Count > 1 ? args[1] : ShellContext.HomeDirectory;

        if (target == "-")
        {
            target = context.GetValue("OLDPWD") ?? context.WorkingDirectory;
            context.Io.Out.WriteLine(ShellContext.DisplayPath(context.ResolvePath(target)));
        }

        context.ChangeDirectory(target);
        context.SetValue("PWD", context.WorkingDirectory, exported: true);
        return 0;
    }

    private static int Pwd(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var path = context.WorkingDirectory;
        context.Io.Out.WriteLine(CommandLine.Parse(args).Has('W') ? path : path.Replace('\\', '/'));
        return 0;
    }

    // ---------------------------------------------------------------- output

    private static int Echo(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var noNewline = false;
        var interpretEscapes = false;
        var index = 1;

        while (index < args.Count && args[index].Length > 1 && args[index][0] == '-' &&
               args[index].Skip(1).All(c => c is 'n' or 'e' or 'E'))
        {
            foreach (var c in args[index].Skip(1))
            {
                if (c == 'n') noNewline = true;
                else if (c == 'e') interpretEscapes = true;
                else if (c == 'E') interpretEscapes = false;
            }

            index++;
        }

        var text = string.Join(' ', args.Skip(index));

        if (interpretEscapes)
        {
            text = Unescape(text, out var suppressNewline);
            if (suppressNewline) noNewline = true;
        }

        context.Io.Out.Write(text);
        if (!noNewline) context.Io.Out.Write('\n');
        context.Io.Out.Flush();
        return 0;
    }

    /// <summary>Interprets the backslash escapes <c>echo -e</c> and <c>printf %b</c> understand.</summary>
    internal static string Unescape(string text, out bool suppressNewline)
    {
        suppressNewline = false;
        var sb = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length) { sb.Append(text[i]); continue; }

            i++;

            switch (text[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case 'e' or 'E': sb.Append('\x1b'); break;
                case '\\': sb.Append('\\'); break;
                case 'c': suppressNewline = true; return sb.ToString();
                case '0':
                {
                    var oct = string.Empty;
                    while (oct.Length < 3 && i + 1 < text.Length && text[i + 1] is >= '0' and <= '7') oct += text[++i];
                    sb.Append(oct.Length == 0 ? '\0' : (char)Convert.ToInt32(oct, 8));
                    break;
                }
                case 'x':
                {
                    var hex = string.Empty;
                    while (hex.Length < 2 && i + 1 < text.Length && Uri.IsHexDigit(text[i + 1])) hex += text[++i];
                    if (hex.Length > 0) sb.Append((char)Convert.ToInt32(hex, 16));
                    else sb.Append("\\x");
                    break;
                }
                case 'u' or 'U':
                {
                    var max = text[i] == 'u' ? 4 : 8;
                    var hex = string.Empty;
                    while (hex.Length < max && i + 1 < text.Length && Uri.IsHexDigit(text[i + 1])) hex += text[++i];
                    if (hex.Length > 0) sb.Append(char.ConvertFromUtf32(Convert.ToInt32(hex, 16)));
                    break;
                }
                default:
                    sb.Append('\\').Append(text[i]);
                    break;
            }
        }

        return sb.ToString();
    }

    private static int Printf(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) throw new ShellException("usage: printf format [arguments]", 2);

        var format = args[1];
        var operands = args.Skip(2).ToList();
        var output = new StringBuilder();
        var index = 0;

        // The format string is reused until every operand has been consumed.
        while (true)
        {
            var before = index;
            FormatOnce(format, operands, ref index, output);
            if (index >= operands.Count || index == before) break;
        }

        context.Io.Out.Write(output.ToString());
        context.Io.Out.Flush();
        return 0;
    }

    /// <summary>Applies the format string once, consuming operands as conversions require them.</summary>
    private static void FormatOnce(string format, List<string> operands, ref int index, StringBuilder output)
    {
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '\\' && i + 1 < format.Length)
            {
                var escaped = Unescape(format.Substring(i, 2), out _);
                output.Append(escaped);
                i++;
                continue;
            }

            if (format[i] != '%') { output.Append(format[i]); continue; }

            if (i + 1 < format.Length && format[i + 1] == '%') { output.Append('%'); i++; continue; }

            // %[flags][width][.precision]conversion
            var start = i++;
            while (i < format.Length && "-+ #0".Contains(format[i])) i++;
            while (i < format.Length && (char.IsAsciiDigit(format[i]) || format[i] == '*')) i++;
            if (i < format.Length && format[i] == '.') { i++; while (i < format.Length && (char.IsAsciiDigit(format[i]) || format[i] == '*')) i++; }
            if (i >= format.Length) { output.Append(format[start..]); break; }

            var specification = format[start..(i + 1)];
            var conversion = format[i];
            var argument = index < operands.Count ? operands[index++] : string.Empty;

            output.Append(ApplyConversion(specification, conversion, argument));
        }
    }

    private static string ApplyConversion(string specification, char conversion, string argument)
    {
        var body = specification[1..^1];
        var leftAlign = body.Contains('-');
        var zeroPad = body.TrimStart('-', '+', ' ', '#').StartsWith('0');
        var cleaned = body.Trim('-', '+', ' ', '#');
        var dot = cleaned.IndexOf('.');
        var widthText = dot < 0 ? cleaned : cleaned[..dot];
        var precisionText = dot < 0 ? null : cleaned[(dot + 1)..];

        int.TryParse(widthText.TrimStart('0'), out var width);
        int? precision = int.TryParse(precisionText, out var p) ? p : null;

        string text;

        switch (conversion)
        {
            case 's':
                text = argument;
                if (precision is { } limit && text.Length > limit) text = text[..limit];
                break;

            case 'b':
                text = Unescape(argument, out _);
                break;

            case 'q':
                text = Interpreter.SingleQuote(argument);
                break;

            case 'c':
                text = argument.Length > 0 ? argument[0].ToString() : string.Empty;
                break;

            case 'd' or 'i':
                text = ParseLong(argument).ToString(CultureInfo.InvariantCulture);
                break;

            case 'u':
                text = ((ulong)ParseLong(argument)).ToString(CultureInfo.InvariantCulture);
                break;

            case 'x':
                text = ParseLong(argument).ToString("x", CultureInfo.InvariantCulture);
                break;

            case 'X':
                text = ParseLong(argument).ToString("X", CultureInfo.InvariantCulture);
                break;

            case 'o':
                text = System.Convert.ToString(ParseLong(argument), 8);
                break;

            case 'f' or 'F':
                text = ParseDouble(argument).ToString("F" + (precision ?? 6), CultureInfo.InvariantCulture);
                break;

            case 'e' or 'E':
                text = ParseDouble(argument).ToString((conversion == 'e' ? "e" : "E") + (precision ?? 6), CultureInfo.InvariantCulture);
                break;

            case 'g' or 'G':
                text = ParseDouble(argument).ToString("G" + (precision ?? 6), CultureInfo.InvariantCulture);
                break;

            default:
                return specification;
        }

        if (width <= 0 || text.Length >= width) return text;

        var padding = new string(zeroPad && !leftAlign ? '0' : ' ', width - text.Length);
        return leftAlign ? text + padding : padding + text;
    }

    private static long ParseLong(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return System.Convert.ToInt64(text[2..], 16);
        if (text.StartsWith('\'') && text.Length > 1) return text[1];
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    // ------------------------------------------------------------ variables

    private static int Export(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);

        if (options.Operands.Count == 0)
        {
            foreach (var variable in context.AllVariables().Where(v => v.Exported).OrderBy(v => v.Name, StringComparer.Ordinal))
                context.Io.Out.WriteLine($"declare -x {variable.Name}=\"{variable.AsScalar()}\"");

            context.Io.Out.Flush();
            return 0;
        }

        foreach (var operand in options.Operands)
        {
            var equals = operand.IndexOf('=');
            var name = equals < 0 ? operand : operand[..equals];

            if (!ShellContext.IsValidName(name))
            {
                Interpreter.WriteError(context, $"export: `{operand}': not a valid identifier");
                return 1;
            }

            if (equals >= 0) context.SetValue(name, operand[(equals + 1)..]);

            var variable = context.Lookup(name) ?? context.DeclareLocal(name);
            variable.Exported = !options.Has('n');
        }

        return 0;
    }

    private static int Unset(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);

        foreach (var name in options.Operands)
        {
            if (options.Has('f')) context.Functions.Remove(name);
            else context.Unset(name);
        }

        return 0;
    }

    private static int Set(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count == 1)
        {
            foreach (var variable in context.AllVariables().Where(v => v.IsSet).OrderBy(v => v.Name, StringComparer.Ordinal))
                context.Io.Out.WriteLine($"{variable.Name}={variable.AsScalar()}");

            context.Io.Out.Flush();
            return 0;
        }

        var i = 1;
        var sawEndOfOptions = false;

        for (; i < args.Count; i++)
        {
            var argument = args[i];

            if (argument == "--") { sawEndOfOptions = true; i++; break; }
            if (argument.Length < 2 || (argument[0] != '-' && argument[0] != '+')) break;

            var enable = argument[0] == '-';

            if (argument.Length > 1 && argument[1] == 'o')
            {
                if (i + 1 >= args.Count)
                {
                    PrintOptions(context);
                    continue;
                }

                SetNamedOption(context, args[++i], enable);
                continue;
            }

            foreach (var flag in argument.Skip(1))
            {
                switch (flag)
                {
                    case 'e': context.Options.ExitOnError = enable; break;
                    case 'u': context.Options.ErrorOnUnset = enable; break;
                    case 'x': context.Options.Trace = enable; break;
                    case 'f': context.Options.NoGlob = enable; break;
                    case 'v': context.Options.Verbose = enable; break;
                    case 'C': context.Options.NoClobber = enable; break;
                    case 'n': context.Options.NoExec = enable; break;
                    case 'a': context.Options.AllExport = enable; break;
                    case 'b' or 'm' or 'h' or 'B' or 'H': break; // accepted and ignored
                    default: throw new ShellException($"-{flag}: invalid option", 2);
                }
            }
        }

        // Only replace the positional parameters when the caller actually supplied some,
        // so `set -e` leaves "$@" alone while `set -- a b` and `set a b` replace it.
        if (sawEndOfOptions || i < args.Count) context.PositionalParameters = args.Skip(i).ToList();

        return 0;
    }

    private static void SetNamedOption(ShellContext context, string name, bool enable)
    {
        switch (name)
        {
            case "errexit": context.Options.ExitOnError = enable; break;
            case "nounset": context.Options.ErrorOnUnset = enable; break;
            case "xtrace": context.Options.Trace = enable; break;
            case "noglob": context.Options.NoGlob = enable; break;
            case "verbose": context.Options.Verbose = enable; break;
            case "noclobber": context.Options.NoClobber = enable; break;
            case "pipefail": context.Options.PipeFail = enable; break;
            case "allexport": context.Options.AllExport = enable; break;
            case "noexec": context.Options.NoExec = enable; break;
            case "vi" or "emacs" or "posix" or "monitor" or "histexpand" or "history": break;
            default: throw new ShellException($"{name}: invalid option name", 2);
        }
    }

    private static void PrintOptions(ShellContext context)
    {
        void Line(string name, bool value) => context.Io.Out.WriteLine($"{name,-16}{(value ? "on" : "off")}");

        Line("allexport", context.Options.AllExport);
        Line("errexit", context.Options.ExitOnError);
        Line("noclobber", context.Options.NoClobber);
        Line("noexec", context.Options.NoExec);
        Line("noglob", context.Options.NoGlob);
        Line("nounset", context.Options.ErrorOnUnset);
        Line("pipefail", context.Options.PipeFail);
        Line("verbose", context.Options.Verbose);
        Line("xtrace", context.Options.Trace);
        context.Io.Out.Flush();
    }

    private static int Shopt(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);

        foreach (var name in options.Operands)
        {
            if (options.Has('s') || options.Has('u'))
            {
                try { SetNamedOption(context, name, options.Has('s')); }
                catch (ShellException) { /* unknown shopt names are ignored, as bash does for many */ }
            }
            else
            {
                context.Io.Out.WriteLine($"{name,-20}off");
            }
        }

        context.Io.Out.Flush();
        return 0;
    }

    private static int Local(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (context.ScopeDepth <= 1) throw new ShellException("can only be used in a function", 1);

        foreach (var operand in args.Skip(1))
        {
            var equals = operand.IndexOf('=');
            var name = equals < 0 ? operand : operand[..equals];
            if (!ShellContext.IsValidName(name)) throw new ShellException($"`{operand}': not a valid identifier", 2);

            context.DeclareLocal(name);
            if (equals >= 0) context.SetValue(name, operand[(equals + 1)..], local: true);
        }

        return 0;
    }

    private static int Declare(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);

        if (options.Operands.Count == 0)
        {
            if (options.Has('f'))
            {
                foreach (var name in context.Functions.Keys.OrderBy(n => n, StringComparer.Ordinal))
                    context.Io.Out.WriteLine($"{name} ()");
            }
            else
            {
                foreach (var variable in context.AllVariables().Where(v => v.IsSet).OrderBy(v => v.Name, StringComparer.Ordinal))
                    context.Io.Out.WriteLine($"declare -- {variable.Name}=\"{variable.AsScalar()}\"");
            }

            context.Io.Out.Flush();
            return 0;
        }

        foreach (var operand in options.Operands)
        {
            var equals = operand.IndexOf('=');
            var name = equals < 0 ? operand : operand[..equals];
            if (!ShellContext.IsValidName(name)) throw new ShellException($"`{operand}': not a valid identifier", 2);

            var local = context.ScopeDepth > 1 && !options.Has('g');
            if (local) context.DeclareLocal(name);

            if (equals >= 0)
            {
                var value = operand[(equals + 1)..];
                if (options.Has('i')) value = ArithmeticEvaluator.Evaluate(value, context).ToString(CultureInfo.InvariantCulture);
                context.SetValue(name, value, local: local);
            }

            var variable = context.Lookup(name) ?? context.DeclareLocal(name);
            if (options.Has('x')) variable.Exported = true;
            if (options.Has('i')) variable.Integer = true;
            if (options.Has('a') && variable.Elements is null) variable.Elements = new List<string>();
            if (options.Has('r')) variable.ReadOnly = true;
        }

        return 0;
    }

    private static int ReadOnly(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var list = new List<string> { "declare", "-r" };
        list.AddRange(args.Skip(1));
        return Declare(context, list, interpreter);
    }

    private static int Read(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "pandt");
        var names = options.Operands;

        if (options.Value('p') is { } prompt)
        {
            context.Io.ErrorOut.Write(prompt);
            context.Io.ErrorOut.Flush();
        }

        var delimiter = options.Value('d') is { Length: > 0 } d ? d[0] : '\n';
        var maximum = int.TryParse(options.Value('n'), out var n) ? n : -1;

        var line = ReadDelimited(context.Io.In, delimiter, maximum);
        if (line is null) return 1;

        if (!options.Has('r')) line = RemoveLineContinuations(line);

        if (options.Value('a') is { Length: > 0 } arrayName)
        {
            context.SetArray(arrayName, SplitFields(line, context.Ifs));
            return 0;
        }

        if (names.Count == 0)
        {
            context.SetValue("REPLY", line);
            return 0;
        }

        var fields = SplitFields(line, context.Ifs);

        for (var i = 0; i < names.Count; i++)
        {
            // The last name soaks up whatever is left, separators included.
            if (i == names.Count - 1 && fields.Count > names.Count)
                context.SetValue(names[i], string.Join(' ', fields.Skip(i)));
            else
                context.SetValue(names[i], i < fields.Count ? fields[i] : string.Empty);
        }

        return 0;
    }

    private static string? ReadDelimited(TextReader reader, char delimiter, int maximum)
    {
        var sb = new StringBuilder();

        while (true)
        {
            var next = reader.Read();
            if (next < 0) return sb.Length > 0 ? sb.ToString() : null;

            var c = (char)next;
            if (c == delimiter) return sb.ToString();
            if (c == '\r' && delimiter == '\n') continue;

            sb.Append(c);
            if (maximum > 0 && sb.Length >= maximum) return sb.ToString();
        }
    }

    private static string RemoveLineContinuations(string text)
    {
        var sb = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[++i]); continue; }
            if (text[i] == '\\') continue;
            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    internal static List<string> SplitFields(string text, string ifs)
    {
        if (ifs.Length == 0) return new List<string> { text };

        var whitespace = ifs.Where(char.IsWhiteSpace).ToHashSet();
        var others = ifs.Where(c => !char.IsWhiteSpace(c)).ToHashSet();
        var fields = new List<string>();
        var current = new StringBuilder();
        var i = 0;

        while (i < text.Length && whitespace.Contains(text[i])) i++;

        for (; i < text.Length; i++)
        {
            if (whitespace.Contains(text[i]))
            {
                fields.Add(current.ToString());
                current.Clear();
                while (i + 1 < text.Length && whitespace.Contains(text[i + 1])) i++;
                continue;
            }

            if (others.Contains(text[i]))
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(text[i]);
        }

        if (current.Length > 0 || fields.Count == 0) fields.Add(current.ToString());
        return fields;
    }

    // -------------------------------------------------------------- aliases

    private static int Alias(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count == 1)
        {
            foreach (var pair in context.Aliases.OrderBy(p => p.Key, StringComparer.Ordinal))
                context.Io.Out.WriteLine($"alias {pair.Key}='{pair.Value.Replace("'", "'\\''")}'");

            context.Io.Out.Flush();
            return 0;
        }

        var status = 0;

        foreach (var operand in args.Skip(1))
        {
            var equals = operand.IndexOf('=');

            if (equals < 0)
            {
                if (context.Aliases.TryGetValue(operand, out var value))
                {
                    context.Io.Out.WriteLine($"alias {operand}='{value.Replace("'", "'\\''")}'");
                }
                else
                {
                    Interpreter.WriteError(context, $"alias: {operand}: not found");
                    status = 1;
                }

                continue;
            }

            context.Aliases[operand[..equals]] = operand[(equals + 1)..];
        }

        context.Io.Out.Flush();
        return status;
    }

    private static int Unalias(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);

        if (options.Has('a')) { context.Aliases.Clear(); return 0; }

        var status = 0;

        foreach (var name in options.Operands)
        {
            if (!context.Aliases.Remove(name))
            {
                Interpreter.WriteError(context, $"unalias: {name}: not found");
                status = 1;
            }
        }

        return status;
    }

    // ------------------------------------------------------- script control

    private static int Source(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) throw new ShellException("filename argument required", 2);

        var path = context.ResolvePath(args[1]);
        if (!File.Exists(path))
        {
            var found = ProcessRunner.Resolve(args[1], context);
            if (found is null) throw new ShellException($"{args[1]}: No such file or directory", 1);
            path = found;
        }

        var savedPositional = context.PositionalParameters;
        if (args.Count > 2) context.PositionalParameters = args.Skip(2).ToList();

        try
        {
            return interpreter.Run(File.ReadAllText(path), context);
        }
        finally
        {
            context.PositionalParameters = savedPositional;
        }
    }

    private static int Eval(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) return 0;
        return interpreter.Run(string.Join(' ', args.Skip(1)), context);
    }

    private static int Exec(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) return 0;

        // There is no exec(2) here; run the command and leave with its status, which is what
        // `exec cmd` means from the caller's point of view.
        var status = interpreter.Run(string.Join(' ', args.Skip(1).Select(Interpreter.SingleQuote)), context);
        throw new ShellExitException(status);
    }

    private static int Exit(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var code = args.Count > 1 && int.TryParse(args[1], out var parsed) ? parsed : context.LastExitStatus;
        throw new ShellExitException(code & 0xFF);
    }

    private static int Return(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var code = args.Count > 1 && int.TryParse(args[1], out var parsed) ? parsed : context.LastExitStatus;
        throw new FunctionReturnException(code & 0xFF);
    }

    private static int Break(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var levels = args.Count > 1 && int.TryParse(args[1], out var parsed) ? Math.Max(1, parsed) : 1;
        throw new LoopBreakException(levels);
    }

    private static int Continue(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var levels = args.Count > 1 && int.TryParse(args[1], out var parsed) ? Math.Max(1, parsed) : 1;
        throw new LoopContinueException(levels);
    }

    private static int Shift(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var count = args.Count > 1 && int.TryParse(args[1], out var parsed) ? parsed : 1;
        if (count < 0 || count > context.PositionalParameters.Count) return 1;

        context.PositionalParameters = context.PositionalParameters.Skip(count).ToList();
        return 0;
    }

    private static int Let(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) throw new ShellException("expression expected", 2);

        long last = 0;
        foreach (var expression in args.Skip(1)) last = ArithmeticEvaluator.Evaluate(expression, context);
        return last != 0 ? 0 : 1;
    }

    private static int Test(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var operands = args.Skip(1).ToList();

        if (args[0] == "[")
        {
            if (operands.Count == 0 || operands[^1] != "]")
                throw new ShellException("missing `]'", 2);

            operands.RemoveAt(operands.Count - 1);
        }

        if (operands.Count == 0) return 1;
        return ConditionalEvaluator.EvaluateTest(operands, context) ? 0 : 1;
    }

    // ---------------------------------------------------------- information

    private static int Type(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var status = 0;

        foreach (var name in options.Operands)
        {
            var kind = Classify(name, context, interpreter, out var detail);

            if (kind is null)
            {
                if (!options.Has('t')) Interpreter.WriteError(context, $"type: {name}: not found");
                status = 1;
                continue;
            }

            context.Io.Out.WriteLine(options.Has('t') ? kind : detail);
        }

        context.Io.Out.Flush();
        return status;
    }

    private static string? Classify(string name, ShellContext context, Interpreter interpreter, out string detail)
    {
        if (context.Aliases.TryGetValue(name, out var alias))
        {
            detail = $"{name} is aliased to `{alias}'";
            return "alias";
        }

        if (context.Functions.ContainsKey(name))
        {
            detail = $"{name} is a function";
            return "function";
        }

        if (interpreter.Builtins.Contains(name))
        {
            detail = $"{name} is a shell builtin";
            return "builtin";
        }

        var path = ProcessRunner.Resolve(name, context);
        if (path is not null)
        {
            detail = $"{name} is {path.Replace('\\', '/')}";
            return "file";
        }

        detail = string.Empty;
        return null;
    }

    private static int Which(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var status = 0;

        foreach (var name in options.Operands)
        {
            if (context.Aliases.TryGetValue(name, out var alias))
            {
                context.Io.Out.WriteLine($"{name}: aliased to {alias}");
                continue;
            }

            if (context.Functions.ContainsKey(name) || interpreter.Builtins.Contains(name))
            {
                context.Io.Out.WriteLine($"{name}: shell built-in command");
                continue;
            }

            var path = ProcessRunner.Resolve(name, context);

            if (path is null) status = 1;
            else context.Io.Out.WriteLine(path.Replace('\\', '/'));
        }

        context.Io.Out.Flush();
        return status;
    }

    private static int CommandBuiltin(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, string.Empty, stopAtFirstOperand: true);

        if (options.Operands.Count == 0) return 0;

        if (options.Has('v') || options.Has('V'))
        {
            var list = new List<string> { "type" };
            if (options.Has('v')) list.Add("-t");
            list.AddRange(options.Operands);
            return Type(context, list, interpreter);
        }

        // `command name` skips functions and aliases but still finds builtins and files.
        var name = options.Operands[0];

        if (interpreter.Builtins.TryGet(name, out var builtin)) return builtin!(context, options.Operands, interpreter);

        var executable = ProcessRunner.Resolve(name, context);
        if (executable is null)
        {
            Interpreter.WriteError(context, $"command: {name}: command not found");
            return 127;
        }

        return ProcessRunner.Run(executable, options.Operands.Skip(1).ToList(), context, context.Io, null, null);
    }

    private static int BuiltinBuiltin(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) return 0;

        var operands = args.Skip(1).ToList();
        if (!interpreter.Builtins.TryGet(operands[0], out var builtin))
        {
            Interpreter.WriteError(context, $"builtin: {operands[0]}: not a shell builtin");
            return 1;
        }

        return builtin!(context, operands, interpreter);
    }

    private static int Help(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var pattern = args.Count > 1 ? args[1] : null;
        var names = interpreter.Builtins.Names
            .Where(n => pattern is null || ShellPattern.IsMatch(n, pattern + "*", ignoreCase: true))
            .ToList();

        if (names.Count == 0)
        {
            Interpreter.WriteError(context, $"help: no help topics match `{pattern}'");
            return 1;
        }

        context.Io.Out.WriteLine($"HelixPython Terminal {ShellContext.Version} — built-in commands");
        context.Io.Out.WriteLine();

        foreach (var name in names)
            context.Io.Out.WriteLine("  " + interpreter.Builtins.HelpFor(name));

        context.Io.Out.Flush();
        return 0;
    }

    private static int History(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);

        if (options.Has('c')) { context.History.Clear(); return 0; }

        var count = options.Operands.Count > 0 && int.TryParse(options.Operands[0], out var n)
            ? n
            : context.History.Count;

        var start = Math.Max(0, context.History.Count - count);

        for (var i = start; i < context.History.Count; i++)
            context.Io.Out.WriteLine($"{i + 1,5}  {context.History[i]}");

        context.Io.Out.Flush();
        return 0;
    }

    private static int Times(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        context.Io.Out.WriteLine($"{(int)cpu.TotalMinutes}m{cpu.Seconds}.{cpu.Milliseconds:000}s");
        context.Io.Out.Flush();
        return 0;
    }

    // ----------------------------------------------------------------- jobs

    private static int Jobs(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        foreach (var job in context.Jobs.All())
        {
            var state = job.State switch
            {
                JobState.Running => "Running",
                JobState.Done => "Done",
                JobState.Failed => $"Exit {job.ExitStatus}",
                _ => "Stopped",
            };

            context.Io.Out.WriteLine($"[{job.Id}]  {state,-12}{job.Command}");
        }

        context.Io.Out.Flush();
        context.Jobs.Reap();
        return 0;
    }

    private static Job? FindJob(ShellContext context, string? specification)
    {
        if (string.IsNullOrEmpty(specification)) return context.Jobs.Current();

        var text = specification.TrimStart('%');
        if (int.TryParse(text, out var id)) return context.Jobs.Find(id);

        return context.Jobs.All().FirstOrDefault(j => j.Command.StartsWith(text, StringComparison.Ordinal));
    }

    private static int Foreground(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var job = FindJob(context, args.Count > 1 ? args[1] : null);
        if (job is null) { Interpreter.WriteError(context, "fg: no such job"); return 1; }

        context.Io.Out.WriteLine(job.Command);
        context.Io.Out.Flush();
        job.Worker?.Join();
        context.Jobs.Reap();
        return job.ExitStatus;
    }

    private static int Background(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var job = FindJob(context, args.Count > 1 ? args[1] : null);
        if (job is null) { Interpreter.WriteError(context, "bg: no such job"); return 1; }

        // Jobs already run on their own thread here, so there is nothing to resume.
        context.Io.Out.WriteLine($"[{job.Id}] {job.Command} &");
        context.Io.Out.Flush();
        return 0;
    }

    private static int Wait(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count > 1)
        {
            var job = FindJob(context, args[1]);
            if (job is null) return 127;
            job.Worker?.Join();
            context.Jobs.Reap();
            return job.ExitStatus;
        }

        foreach (var job in context.Jobs.All()) job.Worker?.Join();
        context.Jobs.Reap();
        return 0;
    }

    private static int Kill(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var targets = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();

        if (args.Skip(1).Any(a => a is "-l" or "--list"))
        {
            context.Io.Out.WriteLine("HUP INT QUIT KILL TERM STOP CONT");
            context.Io.Out.Flush();
            return 0;
        }

        var status = 0;

        foreach (var target in targets)
        {
            if (target.StartsWith('%'))
            {
                var job = FindJob(context, target);
                if (job is null) { Interpreter.WriteError(context, $"kill: {target}: no such job"); status = 1; continue; }

                job.Cancellation.Cancel();

                foreach (var process in job.Processes.ToList())
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (Exception) { /* already gone */ }
                }

                continue;
            }

            if (!int.TryParse(target, out var pid))
            {
                Interpreter.WriteError(context, $"kill: {target}: arguments must be process or job IDs");
                status = 1;
                continue;
            }

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Interpreter.WriteError(context, $"kill: ({pid}) - {ex.Message}");
                status = 1;
            }
        }

        return status;
    }

    private static int Trap(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count == 1)
        {
            foreach (var pair in context.Traps)
                context.Io.Out.WriteLine($"trap -- '{pair.Value}' {pair.Key}");

            context.Io.Out.Flush();
            return 0;
        }

        var action = args[1];
        var signals = args.Skip(2).ToList();

        if (signals.Count == 0) return 0;

        foreach (var signal in signals)
        {
            if (action is "-" or "") context.Traps.Remove(signal);
            else context.Traps[signal] = action;
        }

        return 0;
    }

    // ---------------------------------------------------------- directories

    private static int Dirs(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        if (options.Has('c')) { context.DirectoryStack.Clear(); return 0; }

        var entries = new List<string> { ShellContext.DisplayPath(context.WorkingDirectory) };
        entries.AddRange(context.DirectoryStack.Select(ShellContext.DisplayPath));
        context.Io.Out.WriteLine(string.Join(' ', entries));
        context.Io.Out.Flush();
        return 0;
    }

    private static int Pushd(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2)
        {
            if (context.DirectoryStack.Count == 0) { Interpreter.WriteError(context, "pushd: no other directory"); return 1; }

            var top = context.DirectoryStack[0];
            context.DirectoryStack[0] = context.WorkingDirectory;
            context.ChangeDirectory(top);
            return Dirs(context, new[] { "dirs" }, interpreter);
        }

        context.DirectoryStack.Insert(0, context.WorkingDirectory);
        context.ChangeDirectory(args[1]);
        return Dirs(context, new[] { "dirs" }, interpreter);
    }

    private static int Popd(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (context.DirectoryStack.Count == 0)
        {
            Interpreter.WriteError(context, "popd: directory stack empty");
            return 1;
        }

        var target = context.DirectoryStack[0];
        context.DirectoryStack.RemoveAt(0);
        context.ChangeDirectory(target);
        return Dirs(context, new[] { "dirs" }, interpreter);
    }

    // -------------------------------------------------------------- getopts

    private static int GetOpts(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 3) throw new ShellException("usage: getopts optstring name [args]", 2);

        var optionString = args[1];
        var name = args[2];
        var operands = args.Count > 3 ? args.Skip(3).ToList() : context.PositionalParameters.ToList();

        var index = int.TryParse(context.GetValue("OPTIND"), out var parsed) ? parsed : 1;
        var silent = optionString.StartsWith(':');

        if (index - 1 >= operands.Count)
        {
            context.SetValue("OPTIND", index.ToString());
            return 1;
        }

        var argument = operands[index - 1];

        if (argument.Length < 2 || argument[0] != '-' || argument == "--")
        {
            if (argument == "--") index++;
            context.SetValue("OPTIND", index.ToString());
            return 1;
        }

        var subIndex = int.TryParse(context.GetValue("STT_OPTSUB"), out var sub) ? sub : 1;
        var flag = argument[subIndex];
        var position = optionString.IndexOf(flag);

        if (position < 0)
        {
            context.SetValue(name, "?");
            if (!silent) Interpreter.WriteError(context, $"getopts: illegal option -- {flag}");
            else context.SetValue("OPTARG", flag.ToString());
            AdvanceGetOpts(context, argument, ref index, ref subIndex);
            return 0;
        }

        if (position + 1 < optionString.Length && optionString[position + 1] == ':')
        {
            string value;

            if (subIndex + 1 < argument.Length)
            {
                value = argument[(subIndex + 1)..];
                index++;
                subIndex = 1;
            }
            else if (index < operands.Count)
            {
                value = operands[index];
                index += 2;
                subIndex = 1;
            }
            else
            {
                context.SetValue(name, silent ? ":" : "?");
                context.SetValue("OPTARG", flag.ToString());
                if (!silent) Interpreter.WriteError(context, $"getopts: option requires an argument -- {flag}");
                index++;
                context.SetValue("OPTIND", index.ToString());
                context.SetValue("STT_OPTSUB", "1");
                return 0;
            }

            context.SetValue("OPTARG", value);
            context.SetValue(name, flag.ToString());
            context.SetValue("OPTIND", index.ToString());
            context.SetValue("STT_OPTSUB", subIndex.ToString());
            return 0;
        }

        context.SetValue(name, flag.ToString());
        context.Unset("OPTARG");
        AdvanceGetOpts(context, argument, ref index, ref subIndex);
        return 0;
    }

    private static void AdvanceGetOpts(ShellContext context, string argument, ref int index, ref int subIndex)
    {
        if (subIndex + 1 < argument.Length)
        {
            subIndex++;
        }
        else
        {
            index++;
            subIndex = 1;
        }

        context.SetValue("OPTIND", index.ToString());
        context.SetValue("STT_OPTSUB", subIndex.ToString());
    }

    private static int MapFile(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var name = options.Operands.Count > 0 ? options.Operands[0] : "MAPFILE";
        var lines = new List<string>();

        while (context.Io.In.ReadLine() is { } line)
            lines.Add(options.Has('t') ? line : line + "\n");

        context.SetArray(name, lines);
        return 0;
    }
}
