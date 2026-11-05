using System.Text;
using HelixPythonNamespace.Terminal.App.Terminal;
using HelixPythonNamespace.Terminal.Shell.Runtime;
using HelixPythonNamespace.Terminal.Shell.Session;
using HelixPythonNamespace.Terminal.Shell.Suggestions;

namespace HelixPythonNamespace.Terminal.App;

/// <summary>
/// Drives the read-evaluate-print loop on a worker thread: it asks the terminal for a line,
/// keeps asking while the input is unfinished, runs it, and repeats.
/// </summary>
public sealed class ShellHost : IDisposable
{
    private readonly TerminalControl _terminal;
    private readonly TerminalInputStream _input = new();
    private readonly ShellSession _session;
    private Thread? _worker;
    private CancellationTokenSource? _commandCancellation;
    private volatile bool _stopping;
    private string _currentPrompt = "$ ";

    public ShellHost(TerminalControl terminal, string? startupDirectory)
    {
        _terminal = terminal;

        var output = new TerminalOutputStream(terminal.Write);
        _session = new ShellSession(_input, output, output, startupDirectory);

        terminal.StandardInput = _input;
        terminal.InterruptRequested += Interrupt;
        terminal.PromptRedrawRequested += () => terminal.Write(_currentPrompt);
        terminal.SizeChangedInCells += (columns, rows) => _session.SetTerminalSize(columns, rows);
        terminal.ConsoleRowsChanged += rows => _session.SetConsoleRows(rows);
        terminal.SuggestionProvider = ProvideSuggestions;
        _input.RawInputChanged += SetRawInput;
    }

    /// <summary>
    /// Hands the screen and the keyboard to a child that has taken the console, and takes them
    /// back afterwards. The shell signals this from its worker thread, so it has to cross to the
    /// UI one — and synchronously, because the child's first output follows immediately and has
    /// to be placed against the screen it was given.
    /// </summary>
    private void SetRawInput(bool raw)
    {
        // Stop is joining the worker thread that reaches here, on the UI thread; a synchronous
        // Invoke back onto that same thread would deadlock the two of them against each other
        // until the join times out. Nobody will see this tab's screen again once it is closing,
        // so there is nothing worth crossing threads for.
        if (_stopping) return;

        try
        {
            // The console the child was actually given, which is the window plus whatever room
            // the last program was left needing. The screen has to be told the same number or its
            // rows and the console's are a different length from the first frame.
            var consoleRows = _session.Context.ConsoleRows;
            var keepsHistory = _session.Context.Screen.ProgramKeepsHistory;

            if (_terminal.IsHandleCreated && _terminal.InvokeRequired)
                _terminal.Invoke(() => _terminal.SetChildScreen(raw, consoleRows, keepsHistory));
            else
                _terminal.SetChildScreen(raw, consoleRows, keepsHistory);
        }
        catch (Exception)
        {
            // The window went away mid-command; there is no keyboard left to switch.
        }
    }

    /// <summary>Raised when the shell exits, so the application can quit.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised on the worker thread as a command line starts to run, with its text.</summary>
    public event Action<string>? CommandStarted;

    /// <summary>Raised on the worker thread when that command line has finished.</summary>
    public event Action? CommandFinished;

    /// <summary>
    /// Raised on the worker thread when the loop itself failed and the shell has stopped. The
    /// reason has already been written into the terminal.
    /// </summary>
    public event Action? Failed;

    public ShellSession Session => _session;

    public void Start()
    {
        _session.LoadHistory();
        _terminal.SetHistory(_session.Context.History);
        foreach (var error in _session.Suggestions.Specs.LoadErrors) _terminal.Write($"stt: spec {error}\n");
        _session.SetTerminalSize(_terminal.ColumnCount, _terminal.RowCount);

        _worker = new Thread(RunLoop) { IsBackground = true, Name = "stt-shell" };
        _worker.Start();
    }

    private void RunLoop()
    {
        try
        {
            _session.LoadStartupFile();
            WriteBanner();

            while (!_stopping)
            {
                var accumulated = new StringBuilder();
                _currentPrompt = _session.RenderPrompt();

                while (true)
                {
                    var line = _terminal.ReadLine(_currentPrompt);
                    if (line is null) return;

                    accumulated.Append(line);

                    if (!ShellSession.NeedsMoreInput(accumulated.ToString())) break;

                    accumulated.Append('\n');
                    _currentPrompt = _session.RenderPrompt(continuation: true);
                }

                var script = accumulated.ToString();
                if (script.Trim().Length == 0) continue;

                RunCommand(script);

                if (_session.ExitRequested)
                {
                    ExitRequested?.Invoke();
                    return;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The window closed while we were mid-loop.
        }
        catch (Exception ex)
        {
            // A tab being closed may fail on its way out; that is not worth reporting. Otherwise
            // one tab's shell failing is that tab's business, and the other tabs carry on.
            if (_stopping) return;

            _terminal.Write($"\nstt: shell stopped: {ex.Message}\n");
            Failed?.Invoke();
        }
        finally
        {
            _session.SaveHistory();
        }
    }

    private void RunCommand(string script)
    {
        _input.Reset();
        _commandCancellation = new CancellationTokenSource();

        try
        {
            CommandStarted?.Invoke(script);
            _session.Execute(script, _commandCancellation.Token);
        }
        catch (Exception ex)
        {
            _terminal.Write($"stt: {ex.Message}\n");
        }
        finally
        {
            _commandCancellation.Dispose();
            _commandCancellation = null;
            CommandFinished?.Invoke();
        }
    }

    private void Interrupt()
    {
        _commandCancellation?.Cancel();
        _session.Context.Jobs.KillAll();
        _input.UnblockPendingReads();
    }

    private SuggestionSet ProvideSuggestions(string line, int caret, bool includeGenerators, CancellationToken token) =>
        _session.Suggestions.Suggest(line, caret, _session.Context, includeGenerators, token);

    private void WriteBanner()
    {
        _terminal.Write(
            $"\x1b[1;36mHelixPython Terminal {ShellContext.Version}\x1b[0m — a bash-like shell for Windows\n" +
            "Type \x1b[1mhelp\x1b[0m for built-in commands, \x1b[1mexit\x1b[0m to close the shell.\n" +
            $"Startup file: {ShellSession.StartupFilePath.Replace('\\', '/')}\n\n");
    }

    public void Stop()
    {
        _stopping = true;
        _commandCancellation?.Cancel();
        _session.Context.Jobs.KillAll();
        _session.Context.ExitRequested = true;
        _input.UnblockPendingReads();
        _terminal.CancelReadLine();
        _worker?.Join(1500);
        _session.SaveHistory();
    }

    public void Dispose()
    {
        Stop();
        _input.Dispose();
    }
}
