using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HelixPythonNamespace.Terminal.Shell.Execution;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.Shell.Builtins;

/// <summary>Utilities that report on or interact with the machine rather than the file system.</summary>
public static class SystemCommands
{
    public static void Register(BuiltinRegistry registry)
    {
        registry.Register("date", "date [+format]", Date);
        registry.Register("sleep", "sleep seconds", Sleep);
        registry.Register("env", "env [name=value ...] [command]", Env);
        registry.Register("printenv", "printenv [name]", PrintEnv);
        registry.Register("seq", "seq [first [step]] last", Seq);
        registry.Register("clear", "clear", Clear);
        registry.Register("cls", "cls", ClearScreenAndScrollback);
        registry.Register("cmd", "cmd [command ...]", Cmd);
        registry.Register("whoami", "whoami", (context, _, _) => Write(context, Environment.UserName));
        registry.Register("hostname", "hostname", (context, _, _) => Write(context, Environment.MachineName));
        registry.Register("uname", "uname [-a]", Uname);
        registry.Register("id", "id", Id);
        registry.Register("ps", "ps [-e]", Ps);
        registry.Register("yes", "yes [string]", Yes);
        registry.Register("expr", "expr expression", Expr);
        registry.Register("md5sum", "md5sum [file ...]", (c, a, i) => Digest(c, a, MD5.Create()));
        registry.Register("sha1sum", "sha1sum [file ...]", (c, a, i) => Digest(c, a, SHA1.Create()));
        registry.Register("sha256sum", "sha256sum [file ...]", (c, a, i) => Digest(c, a, SHA256.Create()));
        registry.Register("base64", "base64 [-d] [file]", Base64);
        registry.Register("uptime", "uptime", Uptime);
        registry.Register("open", "open path", Open);
        registry.Register("start", "start path", Open);
        registry.Register("explorer", "explorer [path]", Open);
    }

    private static int Write(ShellContext context, string text)
    {
        context.Io.Out.WriteLine(text);
        context.Io.Out.Flush();
        return 0;
    }

    // ---------------------------------------------------------- time & date

    private static int Date(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args, "d");
        var now = options.Has('u') ? DateTime.UtcNow : DateTime.Now;

        if (options.Value('d') is { } text && DateTime.TryParse(text, out var parsed)) now = parsed;

        var format = args.FirstOrDefault(a => a.StartsWith('+'));

        if (format is null)
        {
            context.Io.Out.WriteLine(now.ToString("ddd MMM d HH:mm:ss ", CultureInfo.InvariantCulture) +
                TimeZoneInfo.Local.StandardName + now.ToString(" yyyy", CultureInfo.InvariantCulture));
            context.Io.Out.Flush();
            return 0;
        }

        context.Io.Out.WriteLine(FormatDate(now, format[1..]));
        context.Io.Out.Flush();
        return 0;
    }

    /// <summary>Translates the strftime-style specifiers people actually type.</summary>
    private static string FormatDate(DateTime value, string format)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 >= format.Length) { sb.Append(format[i]); continue; }

            i++;

            sb.Append(format[i] switch
            {
                'Y' => value.Year.ToString("0000"),
                'y' => (value.Year % 100).ToString("00"),
                'm' => value.Month.ToString("00"),
                'd' => value.Day.ToString("00"),
                'e' => value.Day.ToString().PadLeft(2),
                'H' => value.Hour.ToString("00"),
                'I' => (value.Hour % 12 == 0 ? 12 : value.Hour % 12).ToString("00"),
                'M' => value.Minute.ToString("00"),
                'S' => value.Second.ToString("00"),
                'N' => (value.Millisecond * 1000000).ToString("000000000"),
                'p' => value.Hour < 12 ? "AM" : "PM",
                'A' => value.DayOfWeek.ToString(),
                'a' => value.DayOfWeek.ToString()[..3],
                'B' => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(value.Month),
                'b' or 'h' => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(value.Month),
                'j' => value.DayOfYear.ToString("000"),
                's' => new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds().ToString(),
                'F' => value.ToString("yyyy-MM-dd"),
                'T' => value.ToString("HH:mm:ss"),
                'D' => value.ToString("MM/dd/yy"),
                'R' => value.ToString("HH:mm"),
                'Z' => TimeZoneInfo.Local.StandardName,
                'n' => "\n",
                't' => "\t",
                '%' => "%",
                _ => "%" + format[i],
            });
        }

        return sb.ToString();
    }

    private static int Sleep(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) throw new ShellException("missing operand", 2);

        double total = 0;

        foreach (var operand in args.Skip(1))
        {
            var text = operand;
            var multiplier = 1.0;

            if (text.Length > 1)
            {
                switch (text[^1])
                {
                    case 's': text = text[..^1]; break;
                    case 'm': text = text[..^1]; multiplier = 60; break;
                    case 'h': text = text[..^1]; multiplier = 3600; break;
                    case 'd': text = text[..^1]; multiplier = 86400; break;
                }
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                throw new ShellException($"invalid time interval '{operand}'", 2);

            total += seconds * multiplier;
        }

        var deadline = DateTime.UtcNow.AddSeconds(total);

        while (DateTime.UtcNow < deadline)
        {
            if (context.CancellationToken.IsCancellationRequested) return 130;
            var remaining = deadline - DateTime.UtcNow;
            Thread.Sleep((int)Math.Min(100, Math.Max(1, remaining.TotalMilliseconds)));
        }

        return 0;
    }

    // ------------------------------------------------------------ environment

    private static int Env(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var index = 1;
        var assignments = new List<string>();

        while (index < args.Count && args[index].Contains('=') && !args[index].StartsWith('-'))
            assignments.Add(args[index++]);

        if (index >= args.Count)
        {
            foreach (var pair in context.BuildEnvironment().OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                context.Io.Out.WriteLine($"{pair.Key}={pair.Value}");

            context.Io.Out.Flush();
            return 0;
        }

        var script = new StringBuilder();
        foreach (var assignment in assignments) script.Append(assignment).Append(' ');
        foreach (var argument in args.Skip(index)) script.Append(Interpreter.SingleQuote(argument)).Append(' ');

        return interpreter.Run(script.ToString(), context);
    }

    private static int PrintEnv(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count == 1) return Env(context, new[] { "env" }, interpreter);

        var status = 1;

        foreach (var name in args.Skip(1))
        {
            var value = context.GetValue(name);
            if (value is null) continue;
            context.Io.Out.WriteLine(value);
            status = 0;
        }

        context.Io.Out.Flush();
        return status;
    }

    private static int Seq(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        // Parsed by hand: a negative step such as `seq 3 -1 1` must not look like a flag.
        var separator = "\n";
        var operands = new List<string>();

        for (var i = 1; i < args.Count; i++)
        {
            var argument = args[i];

            if (argument == "-s" && i + 1 < args.Count) { separator = args[++i]; continue; }
            if (argument.StartsWith("-s") && argument.Length > 2) { separator = argument[2..]; continue; }
            if (argument == "--") continue;

            operands.Add(argument);
        }

        if (operands.Count == 0) throw new ShellException("missing operand", 2);

        double first = 1, step = 1, last;

        if (operands.Count == 1)
        {
            last = ParseNumber(operands[0]);
        }
        else if (operands.Count == 2)
        {
            first = ParseNumber(operands[0]);
            last = ParseNumber(operands[1]);
        }
        else
        {
            first = ParseNumber(operands[0]);
            step = ParseNumber(operands[1]);
            last = ParseNumber(operands[2]);
        }

        if (step == 0) throw new ShellException("step must not be zero", 2);

        var values = new List<string>();
        var integral = first == Math.Floor(first) && step == Math.Floor(step) && last == Math.Floor(last);

        if (step > 0)
            for (var v = first; v <= last + 1e-9; v += step) values.Add(Format(v));
        else
            for (var v = first; v >= last - 1e-9; v += step) values.Add(Format(v));

        context.Io.Out.Write(string.Join(separator, values));
        context.Io.Out.Write('\n');
        context.Io.Out.Flush();
        return 0;

        string Format(double value) => integral
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("G15", CultureInfo.InvariantCulture);

        static double ParseNumber(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ShellException($"invalid floating point argument: {text}", 2);
    }

    /// <summary>
    /// Clears the screen and keeps the session behind it, which is what <c>clear</c> does
    /// everywhere else: the window is blank, and everything that was on it is a scroll away.
    /// </summary>
    private static int Clear(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        context.Io.Out.Write("\x1b[2J\x1b[H");
        context.Io.Out.Flush();
        return 0;
    }

    /// <summary>
    /// cmd's own <c>cls</c>, and deliberately not a second name for <c>clear</c>. A console has no
    /// scrollback of its own to keep, so clearing it there really does take everything, and that
    /// is the one people reach for when they want the window emptied rather than tidied. The extra
    /// sequence is the request to take the scrollback with it.
    /// </summary>
    private static int ClearScreenAndScrollback(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        context.Io.Out.Write("\x1b[H\x1b[2J\x1b[3J");
        context.Io.Out.Flush();
        return 0;
    }

    /// <summary>
    /// Hands a command to cmd.exe, which is the only way to reach the things cmd implements
    /// itself rather than as programs on the path — <c>copy</c>, <c>type</c>, <c>ver</c>,
    /// <c>assoc</c>, <c>ftype</c>, <c>vol</c>, <c>start</c>, <c>tree</c> and the rest of them.
    ///
    /// The line is still parsed by this shell first, so quoting, expansion, pipes and redirection
    /// are the shell's throughout and only the command itself is cmd's. To keep something cmd
    /// parses — <c>%PATH%</c>, <c>&amp;&amp;</c>, a caret escape — quote it: <c>cmd "dir &amp;&amp; ver"</c>
    /// arrives at cmd exactly as written. With no arguments it opens an interactive cmd.exe on a
    /// console of its own.
    /// </summary>
    private static int Cmd(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var comSpec = context.GetValue("ComSpec")
                      ?? Environment.GetEnvironmentVariable("ComSpec")
                      ?? "cmd.exe";

        var arguments = new List<string>(args.Skip(1));

        // A line starting with one of cmd's own switches is somebody addressing cmd directly, as
        // `cmd /c dir` or `cmd /k` — that goes through as written. Anything else is a command to
        // run, and needs the switch that says so.
        if (arguments.Count > 0 && !arguments[0].StartsWith('/')) arguments.Insert(0, "/c");

        return ProcessRunner.Run(comSpec, arguments, context, context.Io, null, null);
    }

    private static int Uname(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);
        var all = options.Has('a');
        var parts = new List<string>();

        if (all || options.Has('s') || options.Flags.Count == 0) parts.Add("STT");
        if (all || options.Has('n')) parts.Add(Environment.MachineName);
        if (all || options.Has('r')) parts.Add(Environment.OSVersion.Version.ToString());
        if (all || options.Has('v')) parts.Add(Environment.OSVersion.VersionString);
        if (all || options.Has('m')) parts.Add(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        if (all || options.Has('o')) parts.Add("Windows");

        return Write(context, string.Join(' ', parts));
    }

    private static int Id(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter) =>
        Write(context, $"uid=1000({Environment.UserName}) gid=1000({Environment.UserName}) groups=1000({Environment.UserName})");

    private static int Ps(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        context.Io.Out.WriteLine($"{"PID",8}  {"MEM",10}  COMMAND");

        foreach (var process in Process.GetProcesses().OrderBy(p => p.Id))
        {
            try
            {
                context.Io.Out.WriteLine($"{process.Id,8}  {FileCommands.HumanSize(process.WorkingSet64),10}  {process.ProcessName}");
            }
            catch (Exception)
            {
                // Processes can exit while we enumerate; skip them.
            }
            finally
            {
                process.Dispose();
            }
        }

        context.Io.Out.Flush();
        return 0;
    }

    private static int Yes(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var text = args.Count > 1 ? string.Join(' ', args.Skip(1)) : "y";

        try
        {
            while (!context.CancellationToken.IsCancellationRequested)
            {
                context.Io.Out.WriteLine(text);
                context.Io.Out.Flush();
            }
        }
        catch (IOException)
        {
            return 0; // the reader closed the pipe, which is how `yes | head` is meant to end
        }

        return 130;
    }

    private static int Expr(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        if (args.Count < 2) throw new ShellException("missing operand", 2);

        var operands = args.Skip(1).ToList();

        // String forms first: `expr LENGTH str` and `expr str : regex`.
        if (operands.Count == 2 && operands[0] == "length")
            return Write(context, operands[1].Length.ToString());

        if (operands.Count == 3 && operands[1] == ":")
        {
            var match = System.Text.RegularExpressions.Regex.Match(operands[0], "^(?:" + operands[2] + ")");
            var result = match.Success
                ? match.Groups.Count > 1 ? match.Groups[1].Value : match.Length.ToString()
                : "0";

            context.Io.Out.WriteLine(result);
            context.Io.Out.Flush();
            return result is "0" or "" ? 1 : 0;
        }

        var expression = string.Join(' ', operands);
        var value = Expansion.ArithmeticEvaluator.Evaluate(expression, context);
        context.Io.Out.WriteLine(value);
        context.Io.Out.Flush();
        return value != 0 ? 0 : 1;
    }

    private static int Digest(ShellContext context, IReadOnlyList<string> args, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            var options = CommandLine.Parse(args);

            if (options.Operands.Count == 0)
            {
                using var input = new MemoryStream(Encoding.UTF8.GetBytes(context.Io.In.ReadToEnd()));
                context.Io.Out.WriteLine($"{Convert.ToHexString(algorithm.ComputeHash(input)).ToLowerInvariant()}  -");
                context.Io.Out.Flush();
                return 0;
            }

            var status = 0;

            foreach (var operand in options.Operands)
            {
                var path = context.ResolvePath(operand);

                if (!File.Exists(path))
                {
                    Interpreter.WriteError(context, $"{operand}: No such file or directory");
                    status = 1;
                    continue;
                }

                using var stream = File.OpenRead(path);
                context.Io.Out.WriteLine($"{Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant()}  {operand}");
            }

            context.Io.Out.Flush();
            return status;
        }
    }

    private static int Base64(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var options = CommandLine.Parse(args);

        var input = options.Operands.Count > 0
            ? File.ReadAllText(context.ResolvePath(options.Operands[0]))
            : context.Io.In.ReadToEnd();

        try
        {
            if (options.Has('d') || options.HasLong("decode"))
                context.Io.Out.Write(Encoding.UTF8.GetString(Convert.FromBase64String(input.Trim())));
            else
                context.Io.Out.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(input)));
        }
        catch (FormatException)
        {
            Interpreter.WriteError(context, "base64: invalid input");
            return 1;
        }

        context.Io.Out.Flush();
        return 0;
    }

    private static int Uptime(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return Write(context,
            $" {DateTime.Now:HH:mm:ss} up {uptime.Days} days, {uptime.Hours:00}:{uptime.Minutes:00}, {Environment.ProcessorCount} cpus");
    }

    private static int Open(ShellContext context, IReadOnlyList<string> args, Interpreter interpreter)
    {
        var target = args.Count > 1 ? context.ResolvePath(args[1]) : context.WorkingDirectory;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            Interpreter.WriteError(context, $"{args[0]}: {ex.Message}");
            return 1;
        }
    }
}
