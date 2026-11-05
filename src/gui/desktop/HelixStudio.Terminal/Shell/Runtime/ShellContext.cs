using System.Text;
using HelixPythonNamespace.Terminal.Shell.Execution;
using HelixPythonNamespace.Terminal.Shell.Parsing;

namespace HelixPythonNamespace.Terminal.Shell.Runtime;

/// <summary>Everything one running shell knows: variables, functions, aliases, cwd, options and jobs.</summary>
public sealed class ShellContext
{
    public const string Version = "1.0.0";

    private readonly List<Dictionary<string, ShellVariable>> _scopes = new();
    private readonly Random _random = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public ShellContext(string? workingDirectory = null)
    {
        _scopes.Add(new Dictionary<string, ShellVariable>(StringComparer.Ordinal));
        WorkingDirectory = NormalizeDirectory(workingDirectory ?? HomeDirectory);
        RememberDriveFolder();
        ImportEnvironment();
        DiscardInheritedMarkers();
        SetDefaults();
    }

    // ------------------------------------------------------------- identity

    public static string HomeDirectory =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string WorkingDirectory { get; private set; }

    public List<string> PositionalParameters { get; set; } = new();

    public string ShellName { get; set; } = "stt";

    public int LastExitStatus { get; set; }

    public int? LastBackgroundPid { get; set; }

    public JobTable Jobs { get; } = new();

    public List<string> DirectoryStack { get; } = new();

    public Dictionary<string, FunctionDefinition> Functions { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Traps { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ShellOptions Options { get; } = new();

    /// <summary>Set when <c>exit</c> runs, so enclosing loops and lists unwind.</summary>
    public bool ExitRequested { get; set; }

    public int ExitCode { get; set; }

    /// <summary>Cancelled when the user presses Ctrl+C.</summary>
    public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

    /// <summary>
    /// Set for a command started with <c>&amp;</c>. A background job shares the screen but must not
    /// take it over: it gets pipes, so it cannot swallow the keystrokes meant for the prompt.
    /// </summary>
    public bool IsBackgroundJob { get; set; }

    /// <summary>
    /// The size of the window, shared with every subshell forked from this context so that a
    /// resize reaches a child however deeply nested the command that started it was.
    /// </summary>
    public TerminalSize Screen { get; private set; } = new();

    public int TerminalColumns => Screen.Columns;

    public int TerminalRows => Screen.Rows;

    /// <summary>Rows in the console a hosted program is given, which is the window plus history.</summary>
    public int ConsoleRows => Screen.ConsoleRows;

    /// <summary>
    /// Whether <paramref name="executable"/> is one of the programs named in
    /// <c>STT_CONSOLE_HISTORY</c>, and so is given a console with room to keep its history in.
    /// </summary>
    public bool KeepsConsoleHistory(string executable)
    {
        var named = Lookup("STT_CONSOLE_HISTORY")?.Value;
        if (string.IsNullOrWhiteSpace(named)) return false;

        var name = Path.GetFileNameWithoutExtension(executable);
        if (name.Length == 0) return false;

        foreach (var candidate in named.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>Records the screen size and passes it on to whatever is currently running.</summary>
    public void SetTerminalSize(int columns, int rows)
    {
        Screen.Set(columns, rows);
        SetValue("COLUMNS", Screen.Columns.ToString());
        SetValue("LINES", Screen.ConsoleRows.ToString());
    }

    /// <summary>
    /// Re-sizes the console under a running program without touching the window.
    ///
    /// A pseudo-console keeps no history behind the rows it was created with, so the only place a
    /// program's own scrollback can live is in rows it still has. The window asks for more of them
    /// while the program is filling them, and gives them back when it is not.
    /// </summary>
    public void SetConsoleRows(int rows)
    {
        Screen.SetConsoleRows(rows);
        SetValue("LINES", Screen.ConsoleRows.ToString());
    }

    /// <summary>Set by the host so the shell can run scripts for <c>$(...)</c>, <c>eval</c> and <c>source</c>.</summary>
    public Interpreter? Interpreter { get; set; }

    /// <summary>The streams the currently executing command should use.</summary>
    public ShellIo Io { get; set; } = null!;

    /// <summary>Command history, newest last. Owned by the host but read by the <c>history</c> builtin.</summary>
    public List<string> History { get; } = new();

    /// <summary>Temporary files created for process substitution; deleted when the command finishes.</summary>
    public List<string> TemporaryFiles { get; } = new();

    /// <summary>Work queued by <c>&gt;(cmd)</c>, run once the command that produced the output finishes.</summary>
    public List<Action> DeferredActions { get; } = new();

    /// <summary>Deletes process-substitution temporaries and runs anything queued by <c>&gt;(cmd)</c>.</summary>
    public void RunDeferredActions()
    {
        foreach (var action in DeferredActions.ToList())
        {
            try { action(); } catch (Exception) { /* a failed >(cmd) must not break the caller */ }
        }

        DeferredActions.Clear();

        foreach (var path in TemporaryFiles.ToList())
        {
            try { File.Delete(path); } catch (Exception) { /* best effort */ }
        }

        TemporaryFiles.Clear();
    }

    /// <summary>
    /// Copies the context for a subshell: <c>(...)</c>, a background job, a command substitution
    /// or a non-final pipeline stage. Changes made to the copy are not visible to the parent,
    /// which is exactly the isolation those constructs are defined to have.
    /// </summary>
    public ShellContext Fork()
    {
        var clone = new ShellContext(WorkingDirectory)
        {
            ShellName = ShellName,
            LastExitStatus = LastExitStatus,
            LastBackgroundPid = LastBackgroundPid,
            PositionalParameters = PositionalParameters.ToList(),
            Interpreter = Interpreter,
            CancellationToken = CancellationToken,
            Io = Io,
            Screen = Screen,
            IsBackgroundJob = IsBackgroundJob,
        };

        clone._scopes.Clear();

        foreach (var scope in _scopes)
        {
            var copy = new Dictionary<string, ShellVariable>(StringComparer.Ordinal);

            foreach (var pair in scope)
            {
                copy[pair.Key] = new ShellVariable(pair.Key)
                {
                    Value = pair.Value.Value,
                    Elements = pair.Value.Elements?.ToList(),
                    Exported = pair.Value.Exported,
                    ReadOnly = pair.Value.ReadOnly,
                    Integer = pair.Value.Integer,
                };
            }

            clone._scopes.Add(copy);
        }

        foreach (var pair in Functions) clone.Functions[pair.Key] = pair.Value;
        foreach (var pair in Aliases) clone.Aliases[pair.Key] = pair.Value;
        foreach (var pair in Traps) clone.Traps[pair.Key] = pair.Value;
        foreach (var pair in _driveFolders) clone._driveFolders[pair.Key] = pair.Value;

        clone.Options.ExitOnError = Options.ExitOnError;
        clone.Options.ErrorOnUnset = Options.ErrorOnUnset;
        clone.Options.Trace = Options.Trace;
        clone.Options.NoGlob = Options.NoGlob;
        clone.Options.Verbose = Options.Verbose;
        clone.Options.NoClobber = Options.NoClobber;
        clone.Options.PipeFail = Options.PipeFail;
        clone.Options.AllExport = Options.AllExport;

        return clone;
    }

    // ------------------------------------------------------------- scoping

    public void PushScope() => _scopes.Add(new Dictionary<string, ShellVariable>(StringComparer.Ordinal));

    public void PopScope()
    {
        if (_scopes.Count > 1) _scopes.RemoveAt(_scopes.Count - 1);
    }

    public int ScopeDepth => _scopes.Count;

    private Dictionary<string, ShellVariable> GlobalScope => _scopes[0];

    private Dictionary<string, ShellVariable> CurrentScope => _scopes[^1];

    // ----------------------------------------------------------- variables

    public ShellVariable? Lookup(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var variable)) return variable;

        return null;
    }

    /// <summary>Reads a variable, including the shell's dynamic specials such as <c>$?</c> and <c>$RANDOM</c>.</summary>
    public string? GetValue(string name)
    {
        switch (name)
        {
            case "?": return LastExitStatus.ToString();
            case "$": return Environment.ProcessId.ToString();
            case "!": return LastBackgroundPid?.ToString() ?? string.Empty;
            case "#": return PositionalParameters.Count.ToString();
            case "0": return ShellName;
            case "-": return Options.FlagString;
            case "RANDOM": return _random.Next(0, 32768).ToString();
            case "SECONDS": return ((int)(DateTime.UtcNow - _startedAt).TotalSeconds).ToString();
            case "EPOCHSECONDS": return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            case "EPOCHREALTIME": return (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0).ToString("F6");
            case "PWD": return WorkingDirectory;
        }

        if (name.Length > 0 && name.All(char.IsAsciiDigit))
        {
            var index = int.Parse(name);
            return index >= 1 && index <= PositionalParameters.Count ? PositionalParameters[index - 1] : null;
        }

        var variable = Lookup(name);
        return variable is { IsSet: true } ? variable.AsScalar() : null;
    }

    public bool IsSet(string name) =>
        name is "?" or "$" or "#" or "0" or "-" or "RANDOM" or "SECONDS" or "PWD" or "EPOCHSECONDS"
        || (name.Length > 0 && name.All(char.IsAsciiDigit) && int.Parse(name) <= PositionalParameters.Count && int.Parse(name) >= 1)
        || Lookup(name) is { IsSet: true };

    public void SetValue(string name, string value, bool exported = false, bool local = false)
    {
        if (name == "PWD") { ChangeDirectory(value); return; }

        var scope = local ? CurrentScope : FindScopeFor(name);
        if (!scope.TryGetValue(name, out var variable))
        {
            variable = new ShellVariable(name);
            scope[name] = variable;
        }

        if (variable.ReadOnly) throw new ShellException($"{name}: readonly variable");

        variable.Value = value;
        variable.Elements = null;
        if (exported) variable.Exported = true;

        if (name == "HOME" || name == "PATH") { /* used directly on lookup; nothing cached */ }
    }

    public void SetArray(string name, IEnumerable<string> values, bool local = false)
    {
        var scope = local ? CurrentScope : FindScopeFor(name);
        if (!scope.TryGetValue(name, out var variable))
        {
            variable = new ShellVariable(name);
            scope[name] = variable;
        }

        if (variable.ReadOnly) throw new ShellException($"{name}: readonly variable");

        variable.Elements = values.ToList();
        variable.Value = null;
    }

    public void SetArrayElement(string name, int index, string value)
    {
        var scope = FindScopeFor(name);
        if (!scope.TryGetValue(name, out var variable))
        {
            variable = new ShellVariable(name);
            scope[name] = variable;
        }

        if (variable.ReadOnly) throw new ShellException($"{name}: readonly variable");

        variable.Elements ??= variable.Value is null ? new List<string>() : new List<string> { variable.Value };
        variable.Value = null;
        while (variable.Elements.Count <= index) variable.Elements.Add(string.Empty);
        variable.Elements[index] = value;
    }

    /// <summary>Declares a name in the innermost scope so a later assignment stays local to the function.</summary>
    public ShellVariable DeclareLocal(string name)
    {
        if (!CurrentScope.TryGetValue(name, out var variable))
        {
            variable = new ShellVariable(name);
            CurrentScope[name] = variable;
        }

        return variable;
    }

    public void Unset(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (!_scopes[i].TryGetValue(name, out var variable)) continue;
            if (variable.ReadOnly) throw new ShellException($"{name}: cannot unset: readonly variable");
            _scopes[i].Remove(name);
            return;
        }
    }

    private Dictionary<string, ShellVariable> FindScopeFor(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].ContainsKey(name)) return _scopes[i];

        return GlobalScope;
    }

    public IEnumerable<ShellVariable> AllVariables()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            foreach (var pair in _scopes[i])
                if (seen.Add(pair.Key)) yield return pair.Value;
        }
    }

    /// <summary>Builds the environment block for a child process from the exported variables.</summary>
    public Dictionary<string, string> BuildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in AllVariables())
            if (variable.Exported && variable.IsSet) env[variable.Name] = variable.AsScalar();

        env["PWD"] = WorkingDirectory;
        return env;
    }

    // --------------------------------------------------------- directories

    public void ChangeDirectory(string path)
    {
        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved)) throw new ShellException($"cd: {path}: No such file or directory");

        var previous = WorkingDirectory;
        WorkingDirectory = NormalizeDirectory(resolved);
        RememberDriveFolder();
        SetValue("OLDPWD", previous, exported: true);
    }

    /// <summary>
    /// The folder this shell was last in on each drive, so that <c>D:</c> goes back there as it
    /// does in cmd. Windows keeps the same thing per process, but the process is the app, and
    /// what it has for a drive is wherever the executable was started from.
    /// </summary>
    private readonly Dictionary<char, string> _driveFolders = new();

    private void RememberDriveFolder()
    {
        if (WorkingDirectory.Length >= 2 && WorkingDirectory[1] == ':')
            _driveFolders[char.ToUpperInvariant(WorkingDirectory[0])] = WorkingDirectory;
    }

    /// <summary>A letter and a colon with no slash after it: <c>D:</c> or <c>D:sub</c>.</summary>
    public static bool IsDriveRelative(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
        (path.Length == 2 || path[2] is not ('/' or '\\'));

    private static string NormalizeDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.Length > 3 && (full.EndsWith('\\') || full.EndsWith('/'))) full = full[..^1];

            // The drive letter keeps whatever case was typed, so `cd /d` and `cd D:/` would
            // otherwise show up differently in the prompt. Settle on upper case.
            if (full.Length >= 2 && full[1] == ':') full = char.ToUpperInvariant(full[0]) + full[1..];

            return full;
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>Turns a shell path (relative, <c>~</c>-prefixed, forward or backslashes) into a full Windows path.</summary>
    public string ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return WorkingDirectory;

        if (path == "~") return HomeDirectory;
        if (path.StartsWith("~/") || path.StartsWith("~\\")) path = Path.Combine(HomeDirectory, path[2..]);

        // "/c/Users/..." and "/mnt/c/..." are the shapes people type out of habit on Windows.
        if ((path.StartsWith('/') || path.StartsWith('\\')) && path.Length >= 2)
        {
            var unixDrive = TryTranslateUnixStyleDrive(path);
            if (unixDrive is not null) path = unixDrive;
        }

        if (IsDriveRelative(path))
        {
            var drive = char.ToUpperInvariant(path[0]);
            var folder = _driveFolders.TryGetValue(drive, out var remembered) ? remembered : drive + @":\";
            return Path.GetFullPath(Path.Combine(folder, path[2..]));
        }

        if (Path.IsPathRooted(path))
        {
            // A leading slash with no drive letter means "root of the current drive".
            if ((path[0] == '/' || path[0] == '\\') && (path.Length < 2 || path[1] != '/' && path[1] != '\\'))
            {
                var root = Path.GetPathRoot(WorkingDirectory) ?? "C:\\";
                path = Path.Combine(root, path[1..]);
            }

            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(WorkingDirectory, path));
    }

    private static string? TryTranslateUnixStyleDrive(string path)
    {
        var trimmed = path.Replace('\\', '/');

        if (trimmed.StartsWith("/mnt/") && trimmed.Length >= 6 && char.IsLetter(trimmed[5]) &&
            (trimmed.Length == 6 || trimmed[6] == '/'))
            return trimmed[5] + ":" + (trimmed.Length > 6 ? trimmed[6..] : "/");

        if (trimmed.Length >= 2 && char.IsLetter(trimmed[1]) && (trimmed.Length == 2 || trimmed[2] == '/'))
        {
            var candidate = trimmed[1] + ":" + (trimmed.Length > 2 ? trimmed[2..] : "/");
            if (Directory.Exists(trimmed[1] + ":\\")) return candidate;
        }

        return null;
    }

    /// <summary>Renders a full path the way the prompt should show it, with <c>~</c> and forward slashes.</summary>
    public static string DisplayPath(string path)
    {
        var home = HomeDirectory;
        var display = path;

        if (display.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            display = "~" + display[home.Length..];

        return display.Replace('\\', '/');
    }

    // ------------------------------------------------------------- startup

    private void ImportEnvironment()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            if (!IsValidName(name)) continue;

            GlobalScope[name] = new ShellVariable(name)
            {
                Value = entry.Value?.ToString() ?? string.Empty,
                Exported = true,
            };
        }
    }

    /// <summary>
    /// Variables that describe the process that launched this window rather than the window
    /// itself, and so mean nothing — or the wrong thing — to a command typed at its prompt.
    /// </summary>
    private static readonly string[] InheritedMarkers =
    {
        // Asks programs to leave their output uncoloured. This screen does colour.
        "NO_COLOR",

        // Claude Code marks the environment of everything it runs. Passed on, they tell a
        // session started here that it is a nested child of a session it has nothing to do
        // with, and it turns off transcript saving accordingly.
        "CLAUDECODE",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_PID",
    };

    /// <summary>
    /// Forgets the markers above when they were inherited rather than chosen.
    ///
    /// A tray application is handed whatever environment the thing that launched it happened to
    /// have — Explorer, the autostart entry, or a terminal that was open at the time — and that
    /// environment then reaches every command run in this window. Something set once in a passing
    /// shell should not quietly change how commands behave here, with nothing on screen to
    /// explain it. A preference the user actually holds is recorded against their account or the
    /// machine and is left alone, and so is setting one in <c>~/.sttrc</c>, which runs later.
    /// </summary>
    private void DiscardInheritedMarkers()
    {
        foreach (var name in InheritedMarkers)
        {
            if (Lookup(name) is not { IsSet: true }) continue;

            string? chosen;

            try
            {
                chosen = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                         ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            }
            catch (Exception)
            {
                // No registry to consult; leave the variable as it arrived.
                continue;
            }

            if (string.IsNullOrEmpty(chosen)) Unset(name);
        }
    }

    private void SetDefaults()
    {
        void Default(string name, string value, bool exported)
        {
            if (Lookup(name) is { IsSet: true }) return;
            GlobalScope[name] = new ShellVariable(name) { Value = value, Exported = exported };
        }

        Default("HOME", HomeDirectory, true);
        Default("IFS", " \t\n", false);
        Default("PS1", @"\[\e[1;32m\]\u@\h\[\e[0m\]:\[\e[1;34m\]\w\[\e[0m\]\$ ", false);
        Default("PS2", "> ", false);
        Default("SHELL", Environment.ProcessPath ?? "stt", true);
        Default("HOSTNAME", Environment.MachineName, true);
        Default("USER", Environment.UserName, true);
        Default("LOGNAME", Environment.UserName, true);
        Default("TERM", "xterm-256color", true);

        // The screen renders 24-bit colour, so say so: programs that check this use the full
        // range instead of quantising to the 256-colour cube.
        Default("COLORTERM", "truecolor", true);

        // Programs whose console is given rows to keep their own history in. A pseudo-console
        // holds no scrollback, so what a program writes past the top of it is lost before this
        // terminal is told anything; a taller console is the only place that history can live, and
        // the window then shows the bottom of it. It is right for a program that keeps a
        // transcript and wrong for one that paints a display — `vim` handed a console taller than
        // the window would draw the bottom half of itself below where anyone can see it — and the
        // console cannot tell the two apart, because it does not pass on the alternate-screen
        // switch that would say which this is. So it is named rather than guessed at.
        Default("STT_CONSOLE_HISTORY", "claude", false);

        Default("OSTYPE", "windows", false);
        Default("MACHTYPE", RuntimeArchitecture(), false);
        Default("BASH_VERSION", Version + "(1)-release", false);
        Default("STT_VERSION", Version, false);
        Default("HISTSIZE", "5000", false);
        Default("TMPDIR", Path.GetTempPath(), true);
        Default("OLDPWD", WorkingDirectory, true);
    }

    private static string RuntimeArchitecture() =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() + "-pc-windows";

    public static bool IsValidName(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    public string Ifs => GetValue("IFS") ?? " \t\n";
}

/// <summary>How big the window is, and a way to hear about it changing.</summary>
public sealed class TerminalSize
{
    public int Columns { get; private set; } = 80;

    public int Rows { get; private set; } = 25;

    /// <summary>
    /// Rows in the console a hosted program is given.
    ///
    /// It starts as the window's rows and is the window's rows for everything that does not need
    /// more: a pseudo-console has no scrollback of its own, so a program with history to keep is
    /// given the rows to keep it in, and the window shows the bottom of them.
    /// </summary>
    public int ConsoleRows { get; private set; } = 25;

    /// <summary>
    /// True while the program on the console is one that keeps a transcript, and so is given rows
    /// to keep it in. Off for everything else, because a console taller than the window is wrong
    /// for a program that paints a display rather than scrolling one.
    /// </summary>
    public bool ProgramKeepsHistory { get; private set; }

    public void SetProgramKeepsHistory(bool keeps) => ProgramKeepsHistory = keeps;

    /// <summary>Carries the size of the console, which is what a running program is resized to.</summary>
    public event Action<int, int>? Changed;

    public void Set(int columns, int rows)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);

        // A window resize starts the console over at the window's height. Whatever history the
        // program had room for is laid out again from there as it fills it back up, and keeping
        // the old height instead would leave a hole under a window that had just grown.
        ConsoleRows = Rows;
        Changed?.Invoke(Columns, ConsoleRows);
    }

    public void SetConsoleRows(int rows)
    {
        var wanted = Math.Max(Rows, rows);
        if (wanted == ConsoleRows) return;

        ConsoleRows = wanted;
        Changed?.Invoke(Columns, ConsoleRows);
    }
}

/// <summary><c>set -e</c> and friends.</summary>
public sealed class ShellOptions
{
    public bool ExitOnError { get; set; }        // -e

    public bool ErrorOnUnset { get; set; }       // -u

    public bool Trace { get; set; }              // -x

    public bool NoGlob { get; set; }             // -f

    public bool Verbose { get; set; }            // -v

    public bool NoClobber { get; set; }          // -C

    public bool PipeFail { get; set; }           // -o pipefail

    public bool NoExec { get; set; }             // -n

    public bool AllExport { get; set; }          // -a

    public string FlagString
    {
        get
        {
            var sb = new StringBuilder("i");
            if (ExitOnError) sb.Append('e');
            if (ErrorOnUnset) sb.Append('u');
            if (Trace) sb.Append('x');
            if (NoGlob) sb.Append('f');
            if (Verbose) sb.Append('v');
            if (NoClobber) sb.Append('C');
            if (NoExec) sb.Append('n');
            if (AllExport) sb.Append('a');
            return sb.ToString();
        }
    }
}

/// <summary>A shell-level error that should be reported to stderr and set a non-zero status.</summary>
public class ShellException : Exception
{
    public ShellException(string message, int exitCode = 1) : base(message) => ExitCode = exitCode;

    public int ExitCode { get; }
}
