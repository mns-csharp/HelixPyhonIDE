namespace HelixPythonNamespace.App.Controls
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Forms;
    using HelixPythonNamespace.Ai.Abstractions;
    using HelixPythonNamespace.Ai.Agent;
    using HelixPythonNamespace.Ai.Tools;

    /// <summary>
    /// The AI Terminal: a keyboard-only console for an agent that reads, edits and runs commands
    /// in the open solution.
    ///
    /// <b>It is a terminal, not a form.</b> There are no Send/Stop/Undo buttons and no modal
    /// approval dialogs — every one of those would force a hand off the keyboard mid-thought,
    /// which is the whole reason a console exists. Typing lands directly in the console buffer with
    /// real local echo (the same model as <see cref="TerminalPanel"/>), Enter submits, Esc
    /// interrupts, and everything else is a slash command. Tool approvals are answered with a
    /// single keystroke inline.
    ///
    /// It is a thin view over the headless engine in <c>HelixPythonNamespace.Ai</c> (NFR-03): no provider
    /// logic, no tool logic, no approval policy. Its three jobs are collecting input, marshalling
    /// engine callbacks onto the UI thread, and rendering.
    ///
    /// The editable region is everything at or after <see cref="_inputStart"/>, which every append
    /// advances to "now" — so output can never be typed into or deleted.
    /// </summary>
    public sealed class AiTerminalPanel : UserControl
    {
        private readonly RichTextBox _console;
        private readonly AiProviderRegistry _registry;
        private readonly List<string> _history = new List<string>();

        private AgentSession _session;
        private EditJournal _journal;
        private CancellationTokenSource _cancellation;
        private IAiProvider _provider;
        private string _solutionRoot;
        private string _solutionName;
        private int _inputStart;
        private int _historyIndex = -1;

        /// <summary>
        /// Set while an approval keystroke is awaited. Non-null means the next y/n/a keypress is a
        /// decision rather than input, which is what lets the console stay usable during a run
        /// without a modal dialog.
        /// </summary>
        private TaskCompletionSource<AgentApproval> _pendingApproval;

        private static readonly Color Background = Color.FromArgb(12, 12, 12);
        private static readonly Color Foreground = Color.FromArgb(220, 220, 220);
        private static readonly Color PromptColor = Color.FromArgb(120, 200, 255);
        private static readonly Color ToolColor = Color.FromArgb(190, 170, 110);
        private static readonly Color AskColor = Color.FromArgb(255, 200, 90);
        private static readonly Color ErrorColor = Color.FromArgb(240, 120, 120);
        private static readonly Color DimColor = Color.FromArgb(120, 120, 120);

        // Exposed for tests and the GUI verification harness, mirroring TerminalPanel's pattern.
        internal RichTextBox ConsoleBox { get { return _console; } }
        internal bool IsRunning { get { return _cancellation != null; } }
        internal bool IsAwaitingApproval { get { return _pendingApproval != null; } }
        internal string CurrentLine { get { return ReadLiveLine(); } }

        /// <summary>
        /// Put the caret back on the live input line.
        ///
        /// Called after the panel is re-placed (docked, maximized, reopened): a console the user
        /// just summoned should accept typing immediately rather than requiring a click, which is
        /// the same keyboard-first rule the rest of the panel follows.
        /// </summary>
        public void FocusConsole()
        {
            if (!_console.IsHandleCreated) return;
            _console.Focus();
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
        }

        /// <summary>
        /// Drive the real key handler, for tests and the GUI harness.
        ///
        /// The alternative — <c>SendKeys.SendWait</c> — routes through the OS to whatever window
        /// currently has focus. A test form parked off-screen never has focus, so in a shared test
        /// host those keystrokes land in an unrelated window (in the worst case, the developer's
        /// own desktop) and the test passes or fails depending on what else is running. This calls
        /// the same handler a real keystroke reaches, with no focus dependency.
        /// </summary>
        internal void SimulateKeyDown(Keys key)
        {
            OnConsoleKeyDown(_console, new KeyEventArgs(key));
        }

        /// <summary>Type a line into the live region and submit it, exactly as a user would.</summary>
        internal void SimulateTypeLine(string text)
        {
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _console.SelectedText = text ?? string.Empty;
            SimulateKeyDown(Keys.Enter);
        }

        public AiTerminalPanel(AiProviderRegistry registry)
        {
            _registry = registry ?? new AiProviderRegistry();

            _console = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = false,           // typing goes straight into the buffer
                BackColor = Background,
                ForeColor = Foreground,
                Font = new Font("Consolas", 9.75f),
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
                WordWrap = true,
                HideSelection = false,
                ShortcutsEnabled = true,
            };
            _console.KeyDown += OnConsoleKeyDown;
            _console.KeyPress += OnConsoleKeyPress;
            Controls.Add(_console);

            _provider = _registry.Resolve(null);
            WriteBanner();
        }

        // ---- composition --------------------------------------------------------

        private void WriteBanner()
        {
            AppendLine("HelixPython AI agent — reads, edits and runs commands in this solution.", DimColor);
            AppendLine("Enter sends · Esc interrupts · /help for commands · approvals answer y/n/a inline.", DimColor);
            AppendPrompt();
        }

        private string PromptText()
        {
            string name = _provider == null ? "ai" : _provider.Id;
            return name + " # ";
        }

        private void AppendPrompt()
        {
            Append(Environment.NewLine, Foreground);
            Append(PromptText(), PromptColor);
            _inputStart = _console.TextLength;
            _console.SelectionStart = _inputStart;
            _console.SelectionLength = 0;
        }

        /// <summary>
        /// Point the agent at a solution. The agent's sandbox IS this directory, so there is
        /// nothing meaningful to root it at before a solution is open.
        /// </summary>
        public void SetSolution(string solutionRoot, string solutionName)
        {
            _solutionRoot = solutionRoot;
            _solutionName = solutionName;
            RebuildSession();
            if (!string.IsNullOrWhiteSpace(solutionRoot))
                WriteOutOfBand("workspace: " + solutionRoot, DimColor);
        }

        private void RebuildSession()
        {
            _session = null;
            _journal = null;
            if (string.IsNullOrWhiteSpace(_solutionRoot) || _provider == null) return;

            EditJournal journal;
            _session = AgentFactory.Create(_provider, _solutionRoot, _solutionName,
                new AskForWritesPolicy(AskAsync), out journal);
            _journal = journal;
        }

        // ---- input --------------------------------------------------------------

        private string ReadLiveLine()
        {
            if (_inputStart > _console.TextLength) return string.Empty;
            return _console.Text.Substring(_inputStart);
        }

        private void SetLiveLine(string text)
        {
            _console.SelectionStart = _inputStart;
            _console.SelectionLength = _console.TextLength - _inputStart;
            _console.SelectedText = text ?? string.Empty;
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
        }

        private void OnConsoleKeyPress(object sender, KeyPressEventArgs e)
        {
            // A printable key while a decision is pending IS the decision — swallow it so the
            // letter never lands in the buffer.
            if (_pendingApproval != null) { e.Handled = true; return; }

            // Typing anywhere in the scrollback jumps to the live line first, so a stray click
            // never results in characters appearing in the middle of old output.
            if (_console.SelectionStart < _inputStart)
            {
                _console.SelectionStart = _console.TextLength;
                _console.SelectionLength = 0;
            }
        }

        private void OnConsoleKeyDown(object sender, KeyEventArgs e)
        {
            // An approval is answered with one keystroke and nothing else is accepted, so a
            // half-typed prompt cannot be mistaken for consent.
            if (_pendingApproval != null)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                switch (e.KeyCode)
                {
                    case Keys.Y: CompleteApproval(AgentApproval.Allow, "yes"); break;
                    case Keys.A: CompleteApproval(AgentApproval.AllowForSession, "always"); break;
                    case Keys.N:
                    case Keys.Escape: CompleteApproval(AgentApproval.Deny, "no"); break;
                }
                return;
            }

            // Copy/select-all must keep working across the scrollback; clamping the selection
            // below would collapse the very selection being copied.
            if (e.Control && (e.KeyCode == Keys.A || e.KeyCode == Keys.C)) return;

            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                Interrupt();
                return;
            }

            if (_console.SelectionStart < _inputStart) _console.SelectionStart = _console.TextLength;

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    e.SuppressKeyPress = true;
                    Submit();
                    break;

                case Keys.Up:
                    e.SuppressKeyPress = true;
                    RecallHistory(-1);
                    break;
                case Keys.Down:
                    e.SuppressKeyPress = true;
                    RecallHistory(+1);
                    break;

                // Backspace/Left at the prompt would eat the prompt itself.
                case Keys.Back:
                case Keys.Left:
                    if (_console.SelectionStart <= _inputStart && _console.SelectionLength == 0)
                        e.SuppressKeyPress = true;
                    break;

                case Keys.Home:
                    e.SuppressKeyPress = true;
                    _console.SelectionStart = _inputStart;
                    _console.SelectionLength = 0;
                    break;

                case Keys.U:
                    // Ctrl+U clears the line, as in bash/readline.
                    if (e.Control) { e.SuppressKeyPress = true; SetLiveLine(string.Empty); }
                    break;
                case Keys.L:
                    // Ctrl+L clears the screen, as in bash.
                    if (e.Control) { e.SuppressKeyPress = true; ClearScreen(); }
                    break;
            }
        }

        private void RecallHistory(int direction)
        {
            if (_history.Count == 0) return;
            if (_historyIndex < 0) _historyIndex = _history.Count;
            _historyIndex += direction;
            if (_historyIndex < 0) _historyIndex = 0;
            if (_historyIndex >= _history.Count) { _historyIndex = _history.Count; SetLiveLine(string.Empty); return; }
            SetLiveLine(_history[_historyIndex]);
        }

        private void Submit()
        {
            string line = ReadLiveLine().Trim();
            Append(Environment.NewLine, Foreground);

            if (line.Length == 0) { AppendPrompt(); return; }

            _history.Add(line);
            _historyIndex = -1;

            if (line.StartsWith("/", StringComparison.Ordinal)) { RunCommand(line); return; }

            if (_cancellation != null)
            {
                AppendLine("busy — Esc interrupts the current turn.", ErrorColor);
                AppendPrompt();
                return;
            }
            if (_session == null)
            {
                AppendLine(string.IsNullOrWhiteSpace(_solutionRoot)
                    ? "no solution open — the agent works inside the solution directory."
                    : "no AI provider available.", ErrorColor);
                AppendPrompt();
                return;
            }
            if (_provider != null && !_provider.IsConfigured)
            {
                AppendLine(_provider.DisplayName + " has no API key. " + _provider.CredentialHint, ErrorColor);
                AppendPrompt();
                return;
            }

            StartTurn(line);
        }

        // ---- slash commands ------------------------------------------------------

        private void RunCommand(string line)
        {
            string[] parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();
            string argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            switch (verb)
            {
                case "/help":
                    AppendLine("/provider [id]   switch or list providers", DimColor);
                    AppendLine("/model [id]      switch or list models", DimColor);
                    AppendLine("/undo            roll back every file the agent changed", DimColor);
                    AppendLine("/reset           forget the conversation", DimColor);
                    AppendLine("/clear           clear the screen  (Ctrl+L)", DimColor);
                    AppendLine("Esc              interrupt the running turn", DimColor);
                    break;

                case "/provider":
                    if (argument.Length == 0)
                    {
                        foreach (IAiProvider p in _registry.Providers)
                            AppendLine((p == _provider ? "* " : "  ") + p.Id.PadRight(12)
                                + (p.IsConfigured ? "ready" : "no key"), DimColor);
                    }
                    else
                    {
                        IAiProvider found = _registry.Find(argument);
                        if (found == null) AppendLine("no such provider: " + argument, ErrorColor);
                        else
                        {
                            _provider = found;
                            RebuildSession();
                            AppendLine("provider: " + found.DisplayName
                                + (found.IsConfigured ? string.Empty : "  (no key — " + found.CredentialHint + ")"),
                                found.IsConfigured ? DimColor : ErrorColor);
                        }
                    }
                    break;

                case "/model":
                    if (_provider == null) { AppendLine("no provider selected.", ErrorColor); break; }
                    if (argument.Length == 0)
                    {
                        foreach (string m in _provider.Models)
                            AppendLine((_session != null && m == _session.Model ? "* " : "  ") + m, DimColor);
                    }
                    else if (_session == null) AppendLine("open a solution first.", ErrorColor);
                    else { _session.Model = argument; AppendLine("model: " + argument, DimColor); }
                    break;

                case "/undo":
                    if (_journal == null || _journal.Count == 0) AppendLine("nothing to undo.", DimColor);
                    else AppendLine(_journal.UndoAll(), ToolColor);
                    break;

                case "/reset":
                    if (_session != null) { _session.Reset(); AppendLine("conversation cleared.", DimColor); }
                    break;

                case "/clear":
                    ClearScreen();
                    return;   // ClearScreen writes its own prompt

                default:
                    AppendLine("unknown command: " + verb + "  (/help)", ErrorColor);
                    break;
            }
            AppendPrompt();
        }

        private void ClearScreen()
        {
            _console.Clear();
            AppendPrompt();
        }

        // ---- running a turn ------------------------------------------------------

        private void StartTurn(string prompt)
        {
            _cancellation = new CancellationTokenSource();
            AgentSession session = _session;
            CancellationToken token = _cancellation.Token;

            // Explicit continuation rather than async void: an unobserved exception would
            // otherwise take the process down on the finalizer thread.
            Task.Run(delegate { return session.RunAsync(prompt, OnAgentEvent, token); })
                .ContinueWith(delegate (Task t)
                {
                    Marshal(delegate
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            Exception inner = t.Exception.GetBaseException();
                            if (!(inner is OperationCanceledException))
                                AppendLine("agent failed: " + inner.Message, ErrorColor);
                        }
                        else if (t.IsCanceled)
                        {
                            AppendLine("interrupted.", DimColor);
                        }
                        _cancellation = null;
                        // A denial left pending when the turn ends must not strand the console.
                        CompleteApproval(AgentApproval.Deny, null);
                        WriteUsage(session);
                        AppendPrompt();
                    });
                });
        }

        private void Interrupt()
        {
            if (_pendingApproval != null) { CompleteApproval(AgentApproval.Deny, "no"); return; }
            CancellationTokenSource cancellation = _cancellation;
            if (cancellation == null) return;
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        private void WriteUsage(AgentSession session)
        {
            if (session == null) return;
            AiUsage usage = session.TotalUsage;
            if (usage.InputTokens == 0 && usage.OutputTokens == 0) return;
            AppendLine(string.Format("[{0:n0} in / {1:n0} out]", usage.InputTokens, usage.OutputTokens), DimColor);
        }

        private void OnAgentEvent(AgentEvent e)
        {
            Marshal(delegate
            {
                switch (e.Kind)
                {
                    case AgentEventKind.AssistantText: WriteOutOfBandRaw(e.Text, Foreground); break;
                    case AgentEventKind.Thinking: WriteOutOfBandRaw(e.Text, DimColor); break;
                    case AgentEventKind.ToolStarted: WriteOutOfBand("· " + e.Text, ToolColor); break;
                    case AgentEventKind.ToolFinished: break;
                    case AgentEventKind.ToolDenied: WriteOutOfBand("× " + e.Text, ErrorColor); break;
                    case AgentEventKind.Notice: WriteOutOfBand(e.Text, DimColor); break;
                    case AgentEventKind.Error: WriteOutOfBand(e.Text, ErrorColor); break;
                    case AgentEventKind.TurnComplete: break;
                }
            });
        }

        // ---- inline approval -----------------------------------------------------

        /// <summary>
        /// Ask for one tool call, inline.
        ///
        /// Called from the agent's background thread and awaited by it. A MessageBox here would
        /// mean reaching for the mouse on every write and every command, which defeats the point
        /// of a console — so the question is printed and a single keystroke answers it.
        /// </summary>
        private Task<AgentApproval> AskAsync(AgentApprovalRequest request, CancellationToken token)
        {
            var completion = new TaskCompletionSource<AgentApproval>();
            Marshal(delegate
            {
                if (token.IsCancellationRequested) { completion.TrySetResult(AgentApproval.Deny); return; }

                _pendingApproval = completion;
                bool execute = request.Tool.Risk == AgentToolRisk.Execute;
                WriteOutOfBand((execute ? "⚠ " : "? ") + request.Description, AskColor);
                // "always" is not offered for command execution — the policy refuses to honour it
                // there, and presenting an option that does nothing would be a lie.
                Append("  allow? " + (execute ? "[y/N]" : "[y/N/a=always]") + " ", AskColor);
                _inputStart = _console.TextLength;
                _console.SelectionStart = _inputStart;
                _console.Focus();
            });
            return completion.Task;
        }

        private void CompleteApproval(AgentApproval decision, string echo)
        {
            TaskCompletionSource<AgentApproval> pending = _pendingApproval;
            if (pending == null) return;
            _pendingApproval = null;
            if (echo != null) Append(echo + Environment.NewLine, AskColor);
            pending.TrySetResult(decision);
        }

        // ---- output --------------------------------------------------------------

        /// <summary>
        /// Write agent output above the live input line, preserving whatever the user has typed.
        ///
        /// Output arrives while the user may be mid-sentence on the next prompt; splicing it in
        /// without lifting the typed text would interleave the two.
        /// </summary>
        private void WriteOutOfBand(string text, Color color) { WriteOutOfBandRaw(text + Environment.NewLine, color); }

        private void WriteOutOfBandRaw(string text, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            string live = ReadLiveLine();
            if (live.Length > 0)
            {
                _console.SelectionStart = _inputStart;
                _console.SelectionLength = _console.TextLength - _inputStart;
                _console.SelectedText = string.Empty;
            }
            Append(text, color);
            _inputStart = _console.TextLength;
            if (live.Length > 0) Append(live, Foreground);
            _console.SelectionStart = _console.TextLength;
            _console.ScrollToCaret();
        }

        private void Marshal(Action action)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void Append(string text, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _console.SelectionColor = color;
            _console.AppendText(text);
            _console.SelectionColor = _console.ForeColor;
            _console.SelectionStart = _console.TextLength;
            _console.ScrollToCaret();
        }

        private void AppendLine(string text, Color color)
        {
            Append((text ?? string.Empty) + Environment.NewLine, color);
        }
    }
}
