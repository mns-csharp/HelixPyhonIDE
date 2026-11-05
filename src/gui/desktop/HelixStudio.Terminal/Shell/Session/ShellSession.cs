using System.Text;
using HelixPythonNamespace.Terminal.Shell.Execution;
using HelixPythonNamespace.Terminal.Shell.Lexing;
using HelixPythonNamespace.Terminal.Shell.Parsing;
using HelixPythonNamespace.Terminal.Shell.Runtime;
using HelixPythonNamespace.Terminal.Shell.Suggestions;

namespace HelixPythonNamespace.Terminal.Shell.Session;

/// <summary>
/// One interactive shell: a context, an interpreter and the glue an interactive front end needs
/// (prompt rendering, history, continuation detection, completion). The UI owns the streams.
/// </summary>
public sealed class ShellSession
{
    private readonly Interpreter _interpreter = new();
    private readonly Lazy<SuggestionEngine> _suggestions;

    public ShellSession(Stream input, Stream output, Stream error, string? workingDirectory = null)
    {
        Context = new ShellContext(workingDirectory)
        {
            Interpreter = _interpreter,
            ShellName = "stt",
        };

        Context.Io = new ShellIo(input, output, error);

        // Built on first use, after the history has been loaded, so usage starts from it.
        _suggestions = new Lazy<SuggestionEngine>(() => new SuggestionEngine(
            SpecLibrary.Load(SpecLibrary.UserDirectory),
            UsageStats.From(Context.History.ToList()),
            new GeneratorCache(GitGenerators.All, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5))),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ShellContext Context { get; }

    public Interpreter Interpreter => _interpreter;

    /// <summary>What to suggest while a line is typed; shared by the popup and Tab.</summary>
    public SuggestionEngine Suggestions => _suggestions.Value;

    public bool ExitRequested => Context.ExitRequested;

    public int ExitCode => Context.ExitCode;

    /// <summary>The file read at start-up, the equivalent of <c>~/.bashrc</c>.</summary>
    public static string StartupFilePath => Path.Combine(ShellContext.HomeDirectory, ".sttrc");

    public static string HistoryFilePath => Path.Combine(ShellContext.HomeDirectory, ".stt_history");

    /// <summary>Runs one line (or block) of input, recording it in the history first.</summary>
    public int Execute(string source, CancellationToken cancellationToken = default)
    {
        var trimmed = source.Trim();

        if (trimmed.Length > 0 && (Context.History.Count == 0 || Context.History[^1] != trimmed))
            Context.History.Add(trimmed);

        if (trimmed.Length > 0 && _suggestions.IsValueCreated) _suggestions.Value.Usage.Record(trimmed);

        Context.CancellationToken = cancellationToken;
        Context.ExitRequested = false;

        try
        {
            var status = _interpreter.Run(source, Context);
            Context.LastExitStatus = status;
            return status;
        }
        catch (OperationCanceledException)
        {
            Context.LastExitStatus = 130;
            return 130;
        }
        finally
        {
            Context.CancellationToken = CancellationToken.None;
            ReportFinishedJobs();
        }
    }

    private void ReportFinishedJobs()
    {
        foreach (var job in Context.Jobs.Reap())
        {
            var state = job.State == JobState.Done ? "Done" : $"Exit {job.ExitStatus}";
            Context.Io.ErrorOut.WriteLine($"[{job.Id}]+  {state}\t{job.Command}");
        }

        Context.Io.ErrorOut.Flush();
    }

    /// <summary>Loads <c>~/.sttrc</c> if it exists, creating a starter file the first time.</summary>
    public void LoadStartupFile()
    {
        var path = StartupFilePath;

        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, DefaultStartupFile);
            _interpreter.Run(File.ReadAllText(path), Context);
        }
        catch (Exception ex)
        {
            Context.Io.ErrorOut.WriteLine($"stt: {Path.GetFileName(path)}: {ex.Message}");
            Context.Io.ErrorOut.Flush();
        }
    }

    public void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFilePath)) return;

            foreach (var line in File.ReadAllLines(HistoryFilePath))
                if (line.Trim().Length > 0) Context.History.Add(line);
        }
        catch (Exception)
        {
            // A damaged history file must never stop the shell from starting.
        }
    }

    public void SaveHistory()
    {
        try
        {
            var limit = int.TryParse(Context.GetValue("HISTSIZE"), out var parsed) ? parsed : 5000;
            var lines = Context.History.Skip(Math.Max(0, Context.History.Count - limit));
            File.WriteAllLines(HistoryFilePath, lines);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    /// <summary>Tells the shell how big the terminal is, so <c>ls</c> and friends lay out correctly.</summary>
    public void SetTerminalSize(int columns, int rows) => Context.SetTerminalSize(columns, rows);

    /// <summary>Re-sizes the console under a running program, leaving the window alone.</summary>
    public void SetConsoleRows(int rows) => Context.SetConsoleRows(rows);

    /// <summary>True when the input is an unfinished construct and the caller should keep reading.</summary>
    public static bool NeedsMoreInput(string source)
    {
        if (source.TrimEnd().EndsWith('\\')) return true;

        try
        {
            var tokens = Lexer.Tokenize(source);

            // A here-document whose delimiter never arrived still needs more lines.
            if (tokens.Any(t => t.HereDoc is { Terminated: false })) return true;

            new Parser(tokens).ParseInput();
            return false;
        }
        catch (ShellSyntaxException ex)
        {
            return ex.Message.Contains("EOF", StringComparison.Ordinal)
                || ex.Message.Contains("end of input", StringComparison.Ordinal);
        }
    }

    // -------------------------------------------------------------- prompt

    public string RenderPrompt(bool continuation = false)
    {
        var template = Context.GetValue(continuation ? "PS2" : "PS1") ?? (continuation ? "> " : @"\w\$ ");
        return ExpandPrompt(template);
    }

    /// <summary>Expands the backslash escapes bash defines for <c>PS1</c>.</summary>
    public string ExpandPrompt(string template)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '\\') { sb.Append(template[i]); continue; }
            if (++i >= template.Length) { sb.Append('\\'); break; }

            switch (template[i])
            {
                case 'u': sb.Append(Environment.UserName); break;
                case 'h': sb.Append(Environment.MachineName.Split('.')[0]); break;
                case 'H': sb.Append(Environment.MachineName); break;
                case 'w': sb.Append(ShellContext.DisplayPath(Context.WorkingDirectory)); break;
                case 'W': sb.Append(Path.GetFileName(Context.WorkingDirectory.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf : Context.WorkingDirectory); break;
                case 's': sb.Append(Context.ShellName); break;
                case 'v' or 'V': sb.Append(ShellContext.Version); break;
                case '$': sb.Append('$'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 'e': sb.Append('\x1b'); break;
                case 'a': sb.Append('\a'); break;
                case '\\': sb.Append('\\'); break;
                case 't': sb.Append(DateTime.Now.ToString("HH:mm:ss")); break;
                case 'T': sb.Append(DateTime.Now.ToString("hh:mm:ss")); break;
                case '@': sb.Append(DateTime.Now.ToString("hh:mm tt")); break;
                case 'A': sb.Append(DateTime.Now.ToString("HH:mm")); break;
                case 'd': sb.Append(DateTime.Now.ToString("ddd MMM dd")); break;
                case 'j': sb.Append(Context.Jobs.All().Count); break;
                case '[' or ']': break; // non-printing markers; the terminal measures widths itself
                case '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7':
                {
                    var octal = string.Empty;
                    while (octal.Length < 3 && i < template.Length && template[i] is >= '0' and <= '7') octal += template[i++];
                    i--;
                    sb.Append((char)System.Convert.ToInt32(octal, 8));
                    break;
                }
                default: sb.Append('\\').Append(template[i]); break;
            }
        }

        return sb.ToString();
    }

    private const string DefaultStartupFile = """
        # ~/.sttrc — read by HelixPython Terminal at start-up.
        # Anything valid in the shell works here.

        alias ll='ls -l'
        alias la='ls -la'
        alias l='ls'
        alias ..='cd ..'
        alias ...='cd ../..'
        alias grep='grep --color'
        alias h='history'
        alias c='clear'

        export EDITOR=notepad

        # Prompt: user@host:cwd$ with colour.
        export PS1='\[\e[1;32m\]\u@\h\[\e[0m\]:\[\e[1;34m\]\w\[\e[0m\]\$ '
        """;
}
