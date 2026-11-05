using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
using System.Windows.Forms;
using HelixPythonNamespace.Terminal.Shell.Suggestions;

namespace HelixPythonNamespace.Terminal.App.Terminal;

/// <summary>
/// A screen plus a line editor. It draws a <see cref="TerminalBuffer"/>, turns keystrokes into
/// edited input, and hands finished lines to whoever called <see cref="ReadLine"/> from a
/// worker thread.
/// </summary>
public sealed partial class TerminalControl : Control
{
    private readonly TerminalBuffer _buffer;
    private readonly AnsiInterpreter _interpreter;
    private readonly VScrollBar _scrollBar = new() { Dock = DockStyle.Right, Width = 14 };
    private readonly System.Windows.Forms.Timer _cursorTimer = new() { Interval = 530 };

    private readonly StringBuilder _pending = new();
    private readonly object _pendingGate = new();
    private bool _flushScheduled;

    // Line editing.
    private readonly ManualResetEventSlim _lineReady = new(false);
    private readonly StringBuilder _edit = new();
    private string? _lineResult;
    private bool _editing;
    private int _caret;
    private int _anchorRow;
    private int _anchorColumn;
    private int _caretRow;
    private int _caretColumn;
    private bool _cursorVisible = true;
    private Point _drawnCaret = new(-1, -1);
    private (int Row, int Column) _blinkAnchor = (-1, -1);
    private bool _shuttingDown;

    // History navigation.
    private List<string> _history = new();
    private int _historyIndex;
    private string _historyDraft = string.Empty;

    // Suggestions.
    private SuggestionPopup? _popup;
    private int _suggestGeneration;
    private CancellationTokenSource? _suggestCancel;
    private bool _tabOnce;
    private Form? _form;

    // Text pasted as several lines, replayed one command at a time.
    private readonly Queue<string> _pastedLines = new();

    // A half-typed line set aside while SubmitLine runs something else, given back at the next prompt.
    private string? _draftAfterSubmit;

    // Selection.
    private bool _selecting;
    private int _selectionStartRow = -1;
    private int _selectionStartColumn;
    private int _selectionEndRow = -1;
    private int _selectionEndColumn;

    private Font _font = new("Cascadia Mono", 11f);
    private SizeF _cellSize;

    public TerminalControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);

        _buffer = new TerminalBuffer(80, 25, 10000);
        _interpreter = new AnsiInterpreter(_buffer);
        _interpreter.TitleChanged += title => TitleChanged?.Invoke(title);

        // A program that asks the terminal a question waits for the answer on its own input.
        _interpreter.Respond = reply => StandardInput?.Feed(reply);

        BackColor = _buffer.DefaultBackground;
        ForeColor = _buffer.DefaultForeground;
        TabStop = true;
        Cursor = Cursors.IBeam;

        Controls.Add(_scrollBar);
        _scrollBar.Scroll += (_, _) =>
        {
            lock (_buffer.SyncRoot) _buffer.ViewTop = _scrollBar.Value;
            Invalidate();
        };

        _cursorTimer.Tick += (_, _) =>
        {
            _cursorVisible = !_cursorVisible;
            InvalidateCursor();

            // Also on a tick, not only on output. A program that has finished writing still has
            // history that has not been asked for yet, and fitting the console only when something
            // arrives leaves it at whatever height the last frame happened to catch it at.
            FitConsoleToItsHistory();
        };
        _cursorTimer.Start();

        MeasureFont();
    }

    /// <summary>Raised when the running program sets the window title with an OSC sequence.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>Raised when the user presses Ctrl+C while a command is running.</summary>
    public event Action? InterruptRequested;

    /// <summary>
    /// Suggests what could be typed at the caret: the line, the caret, whether to run generators,
    /// and a token that is cancelled when a newer keystroke makes the answer stale. Set by the host.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<string, int, bool, CancellationToken, SuggestionSet>? SuggestionProvider { get; set; }

    /// <summary>Where typed characters go when no line edit is in progress.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TerminalInputStream? StandardInput { get; set; }

    /// <summary>
    /// True while a child owns the keyboard. The shell's own line editing is out of the way: keys
    /// go through as the sequences a terminal would send, and nothing is echoed here, because the
    /// child echoes what it wants us to see.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RawInputMode { get; set; }

    /// <summary>
    /// Marks a child taking the screen and giving it back. It gets the keyboard, and the rows it
    /// addresses are counted from the line it started on rather than from the top of the window —
    /// without which everything it draws by address lands above where it is writing, over the
    /// prompt and the session before it.
    /// </summary>
    public void SetChildScreen(bool active, int consoleRows, bool keepsHistory)
    {
        RawInputMode = active;

        lock (_buffer.SyncRoot)
        {
            _consoleKeepsHistory = active && keepsHistory;

            if (active) _buffer.BeginChildScreen(_buffer.CursorRow, consoleRows);
            else _buffer.EndChildScreen();

            _interpreter.ProgramEnded();

            // A program that has just started has not filled anything yet, so the console is not
            // grown out from under the one before it.
            _consoleResizedAt = Environment.TickCount64;
        }

        // A console names the window after whatever it is hosting. That is the child's to set
        // while it is running and nobody's afterwards, so the window takes its own name back
        // rather than being left called after the last program that happened to run in it.
        if (!active) TitleChanged?.Invoke(string.Empty);

        Invalidate();
    }

    public int ColumnCount => _buffer.Columns;

    public int RowCount => _buffer.Rows;

    public bool IsEditing => _editing;

    /// <summary>The monospace font the grid is measured and drawn with.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Font TerminalFont
    {
        get => _font;
        set
        {
            var previous = _font;
            _font = value;
            MeasureFont();
            UpdateDimensions();
            Invalidate();
            if (!ReferenceEquals(previous, value)) previous.Dispose();
        }
    }

    public void ApplyTheme(Color background, Color foreground)
    {
        _buffer.DefaultBackground = background;
        _buffer.DefaultForeground = foreground;
        _buffer.CurrentForeground = foreground;
        BackColor = background;
        ForeColor = foreground;
        _scrollBar.BackColor = background;
        _popup?.ApplyTheme(background, foreground);
        Invalidate();
    }

    public void SetScrollbackLimit(int lines)
    {
        lock (_buffer.SyncRoot) _buffer.ScrollbackLimit = Math.Max(_buffer.Rows, lines);
    }

    public void SetHistory(List<string> history) => _history = history;

    // ------------------------------------------------------------- output

    /// <summary>Appends text to the screen. Safe to call from any thread.</summary>
    public void Write(string text)
    {
        if (text.Length == 0 || IsDisposed) return;

        lock (_pendingGate)
        {
            _pending.Append(text);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }

        try
        {
            if (InvokeRequired) BeginInvoke(FlushPending);
            else FlushPending();
        }
        catch (Exception)
        {
            // The window went away between the check and the call; nothing to draw on.
        }
    }

    private void FlushPending()
    {
        string text;

        lock (_pendingGate)
        {
            text = _pending.ToString();
            _pending.Clear();
            _flushScheduled = false;
        }

        if (text.Length == 0) return;

        lock (_buffer.SyncRoot)
        {
            var wasAtBottom = _buffer.IsAtBottom;
            _interpreter.Write(text);
            if (wasAtBottom) _buffer.ScrollToBottom();
            _lastOutputAt = Environment.TickCount64;
        }

        FitConsoleToItsHistory();
        SyncCaretBlink();
        SyncScrollBar();
        Invalidate();
    }

    // ------------------------------------------- the console a program is given

    /// <summary>
    /// Rows added at a time, a quarter of what the console already has. Growing by a fixed step
    /// takes a visible number of seconds to reach a long transcript at one step per frame, and
    /// overshooting by a quarter costs a single frame with a gap in it before the exact shrink
    /// that follows takes the gap out again.
    /// </summary>
    private static int ConsoleStep(int current) => Math.Max(4, current / 4);

    /// <summary>
    /// Blank rows tolerated on a program's console before it counts as bigger than its history.
    /// A row or two between messages is normal; a run longer than this is room going spare.
    /// </summary>
    private const int ConsoleSlackAllowance = 3;

    /// <summary>
    /// Where a shrink aims. It has to leave more slack than growth will fire on, or the two chase
    /// each other: the console would be grown for being full, come back with a gap, be shrunk to
    /// exactly full, and be grown again on the next frame, resizing the program for ever.
    /// </summary>
    private const int ConsoleSlackTarget = ConsoleSlackAllowance + 2;

    /// <summary>How far the console may grow past the window, which is how much history it keeps.</summary>
    private const int MaxConsoleScreens = 6;

    /// <summary>A resize makes the program lay its whole display out again; not faster than this.</summary>
    private const int ConsoleResizeIntervalMilliseconds = 350;

    /// <summary>
    /// How long the program must have written nothing before its console is measured. A frame is
    /// only whole once it has stopped arriving; Claude Code idle writes nothing at all, and while
    /// it is busy its spinner keeps this from firing, so the console is fitted at the pauses.
    /// </summary>
    private const int ConsoleQuietMilliseconds = 250;

    private long _consoleResizedAt;
    private long _lastOutputAt;

    /// <summary>
    /// Whether what is on the console can be measured: not straight after a resize, whose repaint
    /// has not arrived, and not while output is still coming in, because a frame half drawn reads
    /// its undrawn rows as slack. Measured mid-repaint, a 119-row console under Claude Code read
    /// as ninety blank rows, was cut to 30 and grown back step by step, a full repaint each time,
    /// for as long as the program ran — the screen flashing on every frame.
    /// </summary>
    public static bool MayMeasureConsole(long now, long resizedAt, long lastOutputAt) =>
        now - resizedAt >= ConsoleResizeIntervalMilliseconds &&
        now - lastOutputAt >= ConsoleQuietMilliseconds;

    /// <summary>
    /// True while the program running is one that keeps a transcript, which is the only kind the
    /// console is grown for. A pseudo-console does not pass on the alternate-screen switch, so
    /// there is nothing in what arrives here that tells a program painting a display from one
    /// scrolling a session past it — and a console taller than the window is badly wrong for the
    /// first. <c>STT_CONSOLE_HISTORY</c> names the programs it is right for.
    /// </summary>
    private bool _consoleKeepsHistory;

    /// <summary>Raised when the console under a running program should be a different height.</summary>
    public event Action<int>? ConsoleRowsChanged;

    /// <summary>
    /// Keeps the console a program is running on the height of the history it has to show.
    ///
    /// A pseudo-console is a screen with nothing behind it: rows that leave the top of it are gone
    /// before this terminal is told anything, which is why a Claude Code session used to scroll
    /// back to the command that started it and no further. Rows it never lost are rows it can
    /// still draw, so the console is grown while the program is filling it and handed back when it
    /// is not — and what ends up above the window is the program's own history, laid out by the
    /// program, at the size it chose to lay it out at.
    ///
    /// It settles by itself. Growth stops as soon as the program runs out of history to fill the
    /// new rows with, because that is exactly when the blank run appears that gives them back.
    /// </summary>
    private void FitConsoleToItsHistory()
    {
        int wanted;

        lock (_buffer.SyncRoot)
        {
            if (!_buffer.ChildOwnsScreen || !_consoleKeepsHistory) return;

            // Before measuring anything, not after. A resize costs the program a full repaint, and
            // the rows just added stay blank until that arrives — measuring again before it does
            // reads them as slack and hands them straight back, which is an oscillation rather
            // than a search. It also keeps the scan off the path every write takes; the cursor
            // timer measures once the program has gone quiet.
            var now = Environment.TickCount64;
            if (!MayMeasureConsole(now, _consoleResizedAt, _lastOutputAt)) return;

            var current = _buffer.ChildRows;
            wanted = WantedConsoleRows(current);
            if (wanted == current) return;

            _consoleResizedAt = now;
            _buffer.SetChildRows(wanted);
        }

        ConsoleRowsChanged?.Invoke(wanted);
    }

    private int WantedConsoleRows(int current)
    {
        // An alternate screen is thrown away when the program exits, so it is never history; it is
        // the window and no more, or the bottom of `vim` hangs below where anyone can see it.
        if (_buffer.UsingAlternateScreen) return _buffer.Rows;

        // A program that takes the wheel keeps its history itself and scrolls it on request, so
        // the console is the window. Grown, it repaints on every resize, and the history it built
        // up stopped at six screens — the start of a long Claude Code session, its logo and first
        // replies, fell off the top and could not be scrolled back to.
        if (_interpreter.ReportsWheel) return _buffer.Rows;

        var slack = _buffer.ChildScreenSlack();

        // Filling everything it was given means there is more where that came from.
        if (slack <= ConsoleSlackAllowance)
            return Math.Min(current + ConsoleStep(current), _buffer.Rows * MaxConsoleScreens);

        // A gap is rows the program had nothing to put on, and they are worth nothing above the
        // window and a hole above its input line inside it. Enough are taken back to leave the
        // gap at the size growth will not immediately fire on again.
        if (slack > ConsoleSlackTarget + 1)
            return Math.Max(_buffer.Rows, current - (slack - ConsoleSlackTarget));

        return current;
    }

    /// <summary>Writes text and waits for it to reach the screen, so prompts appear in order.</summary>
    private void WriteSynchronously(string text)
    {
        if (InvokeRequired)
        {
            try { Invoke(() => WriteSynchronously(text)); }
            catch (Exception) { }
            return;
        }

        Write(text);
        FlushPending();
    }

    public void ClearScreen()
    {
        lock (_buffer.SyncRoot) _buffer.Clear();
        SyncScrollBar();
        Invalidate();
    }

    // -------------------------------------------------------- line editing

    /// <summary>
    /// Shows <paramref name="prompt"/> and blocks the calling (worker) thread until the user
    /// finishes a line. Returns null when the terminal is shutting down.
    /// </summary>
    public string? ReadLine(string prompt)
    {
        if (_shuttingDown) return null;

        _lineReady.Reset();
        _lineResult = null;

        try
        {
            Invoke(() => BeginEditing(prompt));
        }
        catch (Exception)
        {
            return null;
        }

        _lineReady.Wait();
        return _lineResult;
    }

    /// <summary>Releases a blocked <see cref="ReadLine"/> so the shell thread can exit.</summary>
    public void CancelReadLine()
    {
        _shuttingDown = true;
        _lineResult = null;
        _lineReady.Set();
    }

    private void BeginEditing(string prompt)
    {
        WriteSynchronously(prompt);

        lock (_buffer.SyncRoot)
        {
            _anchorRow = _buffer.CursorRow;
            _anchorColumn = _buffer.CursorColumn;
        }

        _edit.Clear();
        _caret = 0;
        _historyIndex = _history.Count;
        _historyDraft = string.Empty;
        _editing = true;
        _tabOnce = false;

        // Replay the next line of a multi-line paste instead of waiting for Enter.
        if (_pastedLines.Count > 0)
        {
            var line = _pastedLines.Dequeue();
            _edit.Append(line);
            _caret = _edit.Length;
            RedrawInput();
            CommitLine();
            return;
        }

        if (_draftAfterSubmit is { } draft)
        {
            _draftAfterSubmit = null;
            _edit.Append(draft);
            _caret = _edit.Length;
        }

        RedrawInput();
    }

    private void EndEditing(string result)
    {
        _editing = false;
        HideSuggestions();

        lock (_buffer.SyncRoot)
        {
            _buffer.CursorRow = _caretRow;
            _buffer.CursorColumn = _caretColumn;
            MoveToEndOfInput();
            _buffer.NewLine();
            _buffer.CarriageReturn();
            _buffer.ScrollToBottom();
        }

        SyncScrollBar();
        Invalidate();

        // Before the line is handed over, so the command's first output lands inside the listing.
        StartNewListing();

        _lineResult = result;
        _lineReady.Set();
    }

    private void MoveToEndOfInput()
    {
        _buffer.TruncateTo(_anchorRow, _anchorColumn);
        foreach (var c in _edit.ToString()) _buffer.PutChar(c);
    }

    private void RedrawInput()
    {
        lock (_buffer.SyncRoot)
        {
            _buffer.TruncateTo(_anchorRow, _anchorColumn);

            var text = _edit.ToString();
            for (var i = 0; i < _caret && i < text.Length; i++) _buffer.PutChar(text[i]);

            _caretRow = _buffer.CursorRow;
            _caretColumn = _buffer.CursorColumn;

            for (var i = _caret; i < text.Length; i++) _buffer.PutChar(text[i]);

            _buffer.EnsureRoom();
            _buffer.ScrollToBottom();
        }

        SyncCaretBlink();
        _cursorVisible = true;
        SyncScrollBar();
        Invalidate();
    }

    // ------------------------------------------------------------ keyboard

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Browsing's keys come first, and only while it is available: an empty command line with a
        // listing above it. Anything else ends browsing and carries on below as usual.
        if (HandleBrowsingKey(e))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        var control = e.Control;
        var shift = e.Shift;

        // Copy and paste work the same whether or not a line is being edited.
        if (control && shift && e.KeyCode == Keys.C) { CopySelection(); e.Handled = true; return; }
        if (control && shift && e.KeyCode == Keys.V) { Paste(); e.Handled = true; return; }
        if (control && e.KeyCode == Keys.V) { Paste(); e.Handled = true; return; }
        if (control && e.KeyCode == Keys.Insert) { CopySelection(); e.Handled = true; return; }

        if (control && e.KeyCode == Keys.C)
        {
            if (HasSelection) { CopySelection(); ClearSelection(); }
            else Interrupt();

            e.Handled = true;
            return;
        }

        // Ahead of the scrolling keys, because Page Up and Page Down page the list while it is open.
        if (_editing && _popup is { Visible: true } && HandlePopupKey(e.KeyCode, control, shift))
        {
            _tabOnce = false;
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_editing && control && e.KeyCode == Keys.Space)
        {
            RequestSuggestions(force: true);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        // A full-screen program wants the paging keys for itself, so scrolling the scrollback
        // moves to Shift while one is running.
        var scrollKeysAreOurs = !RawInputMode || shift;

        switch (e.KeyCode)
        {
            case Keys.PageUp when !control && scrollKeysAreOurs:
                ScrollBy(-_buffer.Rows + 1);
                e.Handled = true;
                return;

            case Keys.PageDown when !control && scrollKeysAreOurs:
                ScrollBy(_buffer.Rows - 1);
                e.Handled = true;
                return;

            case Keys.Home when control:
                ScrollTo(0);
                e.Handled = true;
                return;

            case Keys.End when control:
                ScrollTo(int.MaxValue);
                e.Handled = true;
                return;
        }

        if (!_editing)
        {
            if (RawInputMode) HandleConsoleKey(e);
            else HandleRawKey(e);

            return;
        }

        var handled = true;

        switch (e.KeyCode)
        {
            case Keys.Enter:
                CommitLine();
                break;

            case Keys.Back:
                if (control) DeleteWordBackwards();
                else if (_caret > 0) { _edit.Remove(--_caret, 1); RedrawInput(); }
                RefreshOpenSuggestions();
                break;

            case Keys.Delete:
                if (_caret < _edit.Length) { _edit.Remove(_caret, 1); RedrawInput(); }
                RefreshOpenSuggestions();
                break;

            case Keys.Left:
                if (control) _caret = PreviousWord();
                else if (_caret > 0) _caret--;
                RedrawInput();
                break;

            case Keys.Right:
                if (control) _caret = NextWord();
                else if (_caret < _edit.Length) _caret++;
                RedrawInput();
                break;

            case Keys.Home:
                _caret = 0;
                RedrawInput();
                break;

            case Keys.End:
                _caret = _edit.Length;
                RedrawInput();
                break;

            case Keys.Up:
                RecallHistory(-1);
                break;

            case Keys.Down:
                RecallHistory(1);
                break;

            case Keys.Tab:
                TabComplete();
                break;

            case Keys.Escape:
                ReplaceLine(string.Empty);
                break;

            case Keys.A when control:
                _caret = 0;
                RedrawInput();
                break;

            case Keys.E when control:
                _caret = _edit.Length;
                RedrawInput();
                break;

            case Keys.U when control:
                _edit.Remove(0, _caret);
                _caret = 0;
                RedrawInput();
                break;

            case Keys.K when control:
                _edit.Remove(_caret, _edit.Length - _caret);
                RedrawInput();
                break;

            case Keys.W when control:
                DeleteWordBackwards();
                break;

            case Keys.L when control:
                ClearScreenKeepingInput();
                break;

            case Keys.D when control:
                if (_edit.Length == 0) EndEditing("exit");
                else if (_caret < _edit.Length) { _edit.Remove(_caret, 1); RedrawInput(); }
                break;

            default:
                handled = false;
                break;
        }

        if (handled)
        {
            if (e.KeyCode != Keys.Tab) _tabOnce = false;

            // Moving the caret, recalling history or cutting text leaves the word the list was for.
            if (e.KeyCode is not (Keys.Tab or Keys.Back or Keys.Delete)) HideSuggestions();

            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    /// <summary>Keystrokes while a command is running go straight to its standard input.</summary>
    private void HandleRawKey(KeyEventArgs e)
    {
        if (StandardInput is null) return;

        switch (e.KeyCode)
        {
            case Keys.Enter:
                StandardInput.Feed("\n");
                Write("\n");
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;

            case Keys.D when e.Control:
                StandardInput.SignalEndOfInput();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
        }
    }

    /// <summary>
    /// Hands a keystroke to a child running on a pseudo-console, encoded the way a terminal sends
    /// it. Keys with no sequence of their own are left alone so <see cref="OnKeyPress"/> can
    /// deliver them as plain characters.
    /// </summary>
    private void HandleConsoleKey(KeyEventArgs e)
    {
        if (StandardInput is null) return;

        var sequence = EncodeKey(e);
        if (sequence is null) return;

        StandardInput.Feed(sequence);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private static string? EncodeKey(KeyEventArgs e)
    {
        var modifier = ModifierCode(e);

        switch (e.KeyCode)
        {
            // Enter submits; Ctrl+Enter and Shift+Enter ask for a new line inside the input the
            // program is editing. A terminal has no separate code for either — the wire carries
            // one carriage return for all three — so the convention is the meta prefix, which is
            // what a full-screen program reads as "insert a line, do not send this yet".
            case Keys.Enter: return e.Control || e.Shift ? "\x1b\r" : "\r";
            case Keys.Back: return e.Control ? "\b" : "\x7f";
            case Keys.Tab: return e.Shift ? "\x1b[Z" : "\t";
            case Keys.Escape: return "\x1b";

            case Keys.Up: return CursorKey('A', modifier);
            case Keys.Down: return CursorKey('B', modifier);
            case Keys.Right: return CursorKey('C', modifier);
            case Keys.Left: return CursorKey('D', modifier);
            case Keys.End: return CursorKey('F', modifier);
            case Keys.Home: return CursorKey('H', modifier);

            case Keys.Insert: return Tilde(2, modifier);
            case Keys.Delete: return Tilde(3, modifier);
            case Keys.PageUp: return Tilde(5, modifier);
            case Keys.PageDown: return Tilde(6, modifier);

            case Keys.F1: return "\x1bOP";
            case Keys.F2: return "\x1bOQ";
            case Keys.F3: return "\x1bOR";
            case Keys.F4: return "\x1bOS";
            case Keys.F5: return Tilde(15, modifier);
            case Keys.F6: return Tilde(17, modifier);
            case Keys.F7: return Tilde(18, modifier);
            case Keys.F8: return Tilde(19, modifier);
            case Keys.F9: return Tilde(20, modifier);
            case Keys.F10: return Tilde(21, modifier);
            case Keys.F11: return Tilde(23, modifier);
            case Keys.F12: return Tilde(24, modifier);
        }

        var letter = e.KeyCode is >= Keys.A and <= Keys.Z;

        // Ctrl and a letter is the control character of the same name; Ctrl+Space is NUL.
        if (e.Control && !e.Alt && letter) return ((char)(e.KeyCode - Keys.A + 1)).ToString();
        if (e.Control && !e.Alt && e.KeyCode == Keys.Space) return "\0";

        // Alt and a letter is that letter with an escape in front. KeyPress never sees these.
        if (e.Alt && !e.Control && letter) return "\x1b" + char.ToLowerInvariant((char)e.KeyCode);

        return null;
    }

    /// <summary>xterm's modifier encoding: 1 plus shift, alt and control as 1, 2 and 4.</summary>
    private static int ModifierCode(KeyEventArgs e)
    {
        var code = 1;
        if (e.Shift) code += 1;
        if (e.Alt) code += 2;
        if (e.Control) code += 4;
        return code;
    }

    private static string CursorKey(char final, int modifier) =>
        modifier == 1 ? "\x1b[" + final : $"\x1b[1;{modifier}{final}";

    private static string Tilde(int code, int modifier) =>
        modifier == 1 ? $"\x1b[{code}~" : $"\x1b[{code};{modifier}~";

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);

        if (char.IsControl(e.KeyChar)) { e.Handled = true; return; }

        // Ctrl+Space asks for suggestions; it does not type a space.
        if (e.KeyChar == ' ' && (ModifierKeys & Keys.Control) != 0) { e.Handled = true; return; }

        if (_editing)
        {
            _edit.Insert(_caret++, e.KeyChar);
            _tabOnce = false;
            RedrawInput();

            if (e.KeyChar == ' ') HideSuggestions();
            else RequestSuggestions(force: false);
        }
        else if (RawInputMode)
        {
            // No echo: whatever the child wants shown arrives on its output.
            StandardInput?.Feed(e.KeyChar.ToString());
        }
        else
        {
            StandardInput?.Feed(e.KeyChar.ToString());
            Write(e.KeyChar.ToString());
        }

        e.Handled = true;
    }

    private void CommitLine()
    {
        var text = _edit.ToString();
        EndEditing(text);
    }

    /// <summary>
    /// Types a line at the prompt and runs it, as though it had been entered by hand — so what
    /// happened is on the screen afterwards like any other command. False when the shell is busy
    /// and there is no prompt to type at.
    /// </summary>
    public bool SubmitLine(string text)
    {
        if (InvokeRequired)
        {
            try { return Invoke(() => SubmitLine(text)); }
            catch (Exception) { return false; }
        }

        if (!_editing) return false;

        // A command sent from the tree or the toolbar leaves what was half-typed for the next prompt.
        if (_edit.Length > 0) _draftAfterSubmit = _edit.ToString();

        _edit.Clear();
        _edit.Append(text);
        _caret = _edit.Length;
        RedrawInput();
        CommitLine();
        return true;
    }

    /// <summary>Queues the popup's initial command before the shell worker starts.</summary>
    public void QueueCommand(string text)
    {
        if (InvokeRequired)
        {
            try { Invoke(() => QueueCommand(text)); }
            catch (Exception) { }
            return;
        }

        if (!_shuttingDown && !string.IsNullOrWhiteSpace(text)) _pastedLines.Enqueue(text);
    }

    /// <summary>
    /// Types text at the caret without running it, as though it had been typed: it ends browsing a
    /// listing and asks for suggestions. False when there is no prompt to type at.
    /// </summary>
    public bool InsertAtPrompt(string text)
    {
        if (InvokeRequired)
        {
            try { return Invoke(() => InsertAtPrompt(text)); }
            catch (Exception) { return false; }
        }

        if (!_editing || RawInputMode || text.Length == 0) return false;

        EndBrowsing();
        _edit.Insert(_caret, text);
        _caret += text.Length;
        _tabOnce = false;
        RedrawInput();

        if (text.EndsWith(' ')) HideSuggestions();
        else RequestSuggestions(force: false);

        return true;
    }

    private void Interrupt()
    {
        // A child on a console decides for itself what Ctrl+C means, so it just gets the byte.
        if (RawInputMode)
        {
            StandardInput?.Feed("\x03");
            return;
        }

        if (_editing)
        {
            WriteAtInputEnd("^C");
            EndEditing(string.Empty);
            return;
        }

        Write("^C\n");
        InterruptRequested?.Invoke();
    }

    private void WriteAtInputEnd(string text)
    {
        lock (_buffer.SyncRoot)
        {
            _buffer.CursorRow = _caretRow;
            _buffer.CursorColumn = _caretColumn;
            MoveToEndOfInput();
            foreach (var c in text) _buffer.PutChar(c);
            _caretRow = _buffer.CursorRow;
            _caretColumn = _buffer.CursorColumn;
        }
    }

    private void ReplaceLine(string text)
    {
        _edit.Clear();
        _edit.Append(text);
        _caret = _edit.Length;
        RedrawInput();
    }

    private void ClearScreenKeepingInput()
    {
        var text = _edit.ToString();
        var caret = _caret;

        lock (_buffer.SyncRoot)
        {
            _buffer.Clear();
            _anchorRow = 0;
            _anchorColumn = 0;
        }

        // Ctrl+L keeps whatever was half-typed, so redraw the prompt with it.
        PromptRedrawRequested?.Invoke();
        FlushPending();

        lock (_buffer.SyncRoot)
        {
            _anchorRow = _buffer.CursorRow;
            _anchorColumn = _buffer.CursorColumn;
        }

        _edit.Clear();
        _edit.Append(text);
        _caret = caret;
        RedrawInput();
    }

    /// <summary>Asks the host to re-emit the prompt after the screen was cleared.</summary>
    public event Action? PromptRedrawRequested;

    private void RecallHistory(int direction)
    {
        if (_history.Count == 0) return;

        if (_historyIndex == _history.Count) _historyDraft = _edit.ToString();

        var index = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        if (index == _historyIndex) return;

        _historyIndex = index;
        ReplaceLine(index == _history.Count ? _historyDraft : _history[index]);
    }

    private int PreviousWord()
    {
        var i = _caret;
        while (i > 0 && char.IsWhiteSpace(_edit[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(_edit[i - 1])) i--;
        return i;
    }

    private int NextWord()
    {
        var i = _caret;
        while (i < _edit.Length && char.IsWhiteSpace(_edit[i])) i++;
        while (i < _edit.Length && !char.IsWhiteSpace(_edit[i])) i++;
        return i;
    }

    private void DeleteWordBackwards()
    {
        var start = PreviousWord();
        if (start >= _caret) return;

        _edit.Remove(start, _caret - start);
        _caret = start;
        RedrawInput();
    }

    // --------------------------------------------------------- suggestions

    private SuggestionPopup Popup => _popup ??= new SuggestionPopup(BackColor, ForeColor);

    /// <summary>
    /// Asks for suggestions off the UI thread, so typing never waits on them. An answer that
    /// arrives after a newer keystroke is dropped. A set that skipped generators is shown at once
    /// and then again with them.
    /// </summary>
    private void RequestSuggestions(bool force)
    {
        if (!_editing || RawInputMode || SuggestionProvider is not { } provider) return;

        var line = _edit.ToString();
        var caret = _caret;
        _suggestCancel?.Cancel();
        _suggestCancel = new CancellationTokenSource();
        var token = _suggestCancel.Token;
        var generation = ++_suggestGeneration;

        Task.Run(() =>
        {
            try
            {
                var set = provider(line, caret, false, token);
                BeginInvoke(() => ShowSuggestions(set, generation, force));

                if (!set.Pending || token.IsCancellationRequested) return;

                var full = provider(line, caret, true, token);
                BeginInvoke(() => ShowSuggestions(full, generation, force));
            }
            catch (Exception)
            {
                // A provider that fails, or a window that closed meanwhile, just shows nothing.
            }
        });
    }

    private void ShowSuggestions(SuggestionSet set, int generation, bool force)
    {
        // Tab or a paste can change the line without a new request; that answer's span no longer fits.
        if (generation != _suggestGeneration || !_editing || !set.IsFor(_edit.ToString(), _caret)) return;

        var word = TypedWord(set);
        if (set.Items.Count == 0 || (word.Length == 0 && !force) ||
            (set.Items.Count == 1 && set.Items[0].InsertText == word))
        {
            HideSuggestions();
            return;
        }

        Popup.ShowFor(set, WordAnchor(set.ReplaceStart));
    }

    /// <summary>Hides the list and drops any answer still on its way.</summary>
    private void HideSuggestions()
    {
        _suggestGeneration++;
        _suggestCancel?.Cancel();
        _popup?.HideIfShown();
    }

    private void RefreshOpenSuggestions()
    {
        if (_popup is not { Visible: true }) return;

        if (_caret == 0 || char.IsWhiteSpace(_edit[_caret - 1])) HideSuggestions();
        else RequestSuggestions(force: false);
    }

    private bool HandlePopupKey(Keys key, bool control, bool shift)
    {
        switch (key)
        {
            case Keys.Up: Popup.MoveHighlight(-1); return true;
            case Keys.Down: Popup.MoveHighlight(1); return true;
            case Keys.PageUp: Popup.PageHighlight(-1); return true;
            case Keys.PageDown: Popup.PageHighlight(1); return true;
            case Keys.Tab when shift: Popup.MoveHighlight(-1); return true;
            case Keys.Tab:
                if (!AcceptHighlighted()) { HideSuggestions(); TabComplete(); }
                return true;
            case Keys.Right when !control && _caret == _edit.Length:
                if (!AcceptHighlighted()) HideSuggestions();
                return true;
            case Keys.Escape: HideSuggestions(); return true;
            case Keys.Enter: HideSuggestions(); CommitLine(); return true;
            default: return false;
        }
    }

    /// <summary>False when there is nothing to accept, or the list was for a line that has since changed.</summary>
    private bool AcceptHighlighted()
    {
        if (_popup is not { Set: { } set, Highlighted: { } item } || !set.IsFor(_edit.ToString(), _caret)) return false;

        Accept(set, item.InsertText);
        return true;
    }

    /// <summary>
    /// Puts the text in place of the typed word. A folder keeps the list open on what is inside it;
    /// anything else is finished, so a space follows it.
    /// </summary>
    private void Accept(SuggestionSet set, string text)
    {
        ReplaceWord(set, text);

        if (text.EndsWith('/'))
        {
            RedrawInput();
            RequestSuggestions(force: true);
            return;
        }

        _edit.Insert(_caret++, ' ');
        HideSuggestions();
        RedrawInput();
    }

    private void ReplaceWord(SuggestionSet set, string text)
    {
        var start = Math.Clamp(set.ReplaceStart, 0, _edit.Length);
        var length = Math.Clamp(set.ReplaceLength, 0, _edit.Length - start);

        _edit.Remove(start, length);
        _edit.Insert(start, text);
        _caret = start + text.Length;
    }

    private string TypedWord(SuggestionSet set)
    {
        var start = Math.Clamp(set.ReplaceStart, 0, _edit.Length);
        return _edit.ToString(start, Math.Clamp(set.ReplaceLength, 0, _edit.Length - start));
    }

    /// <summary>
    /// With the list closed, Tab finishes a single match, then extends to what every match shares,
    /// and only then opens the list. Generators such as git never run here, on the UI thread: when
    /// the answer needs one, the list opens once it has answered.
    /// </summary>
    private void TabComplete()
    {
        if (SuggestionProvider is not { } provider) return;

        SuggestionSet set;
        try { set = provider(_edit.ToString(), _caret, false, CancellationToken.None); }
        catch (Exception) { return; }

        if (set.Pending)
        {
            _tabOnce = false;
            RequestSuggestions(force: true);
            return;
        }

        if (set.Items.Count == 0) return;

        if (set.Items.Count == 1)
        {
            _tabOnce = false;
            Accept(set, set.Items[0].InsertText);
            return;
        }

        if (!_tabOnce && set.CommonPrefix.Length > set.ReplaceLength &&
            set.CommonPrefix.StartsWith(TypedWord(set), StringComparison.OrdinalIgnoreCase))
        {
            ReplaceWord(set, set.CommonPrefix);
            _tabOnce = true;
            RedrawInput();
            return;
        }

        _tabOnce = false;
        _suggestGeneration++;
        _suggestCancel?.Cancel();
        Popup.ShowFor(set, WordAnchor(set.ReplaceStart));
    }

    /// <summary>The screen rectangle of the cell the word starts in, for the list to hang from.</summary>
    private Rectangle WordAnchor(int replaceStart)
    {
        // ponytail: ignores wide characters before the word
        int x, y;
        lock (_buffer.SyncRoot)
        {
            var c = _anchorColumn + replaceStart;
            var row = _anchorRow + c / _buffer.Columns;
            x = (int)(c % _buffer.Columns * _cellSize.Width);
            y = (int)((row - _buffer.ViewTop) * _cellSize.Height);
        }

        return new Rectangle(PointToScreen(new Point(x, y)), Size.Ceiling(_cellSize));
    }

    private void OnFormMovedOrResized(object? sender, EventArgs e) => HideSuggestions();

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);

        var form = FindForm();
        if (ReferenceEquals(form, _form)) return;

        if (_form is not null)
        {
            _form.Move -= OnFormMovedOrResized;
            _form.Resize -= OnFormMovedOrResized;
        }

        _form = form;

        if (_form is not null)
        {
            _form.Move += OnFormMovedOrResized;
            _form.Resize += OnFormMovedOrResized;
        }
    }
    // ------------------------------------------------------- mouse & scroll

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.Button == MouseButtons.Right)
        {
            if (HasSelection) { CopySelection(); ClearSelection(); }
            else Paste();

            return;
        }

        if (e.Button != MouseButtons.Left) return;

        var (row, column) = HitTest(e.Location);
        _selecting = true;
        _selectionStartRow = row;
        _selectionStartColumn = column;
        _selectionEndRow = row;
        _selectionEndColumn = column;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selecting) return;

        var (row, column) = HitTest(e.Location);
        _selectionEndRow = row;
        _selectionEndColumn = column;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _selecting = false;

        // A press that never turned into a drag is a click: highlight the name under it. A press
        // that did drag out a selection leaves it alone, or a click on a name would blank out
        // every selection the moment the button comes back up.
        if (e.Button == MouseButtons.Left && !HasSelection) ClickListing(e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && DoubleClickListing(e.Location)) return;

        var (row, column) = HitTest(e.Location);

        lock (_buffer.SyncRoot)
        {
            if (row < 0 || row >= _buffer.Lines.Count) return;

            var line = _buffer.Lines[row];
            if (column >= line.Cells.Count) return;

            var start = column;
            var end = column;
            while (start > 0 && !char.IsWhiteSpace(line.Cells[start - 1].Character)) start--;
            while (end < line.Cells.Count && !char.IsWhiteSpace(line.Cells[end].Character)) end++;

            _selectionStartRow = row;
            _selectionStartColumn = start;
            _selectionEndRow = row;
            _selectionEndColumn = end;
        }

        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // A program that asked for the wheel scrolls its own history, as it does in Windows
        // Terminal; Shift keeps the wheel for the scrollback, as it does there too.
        if (RawInputMode && _interpreter.ReportsWheel && (ModifierKeys & Keys.Shift) == 0)
        {
            int row, column;
            lock (_buffer.SyncRoot)
            {
                var (bufferRow, cellColumn) = HitTest(e.Location);
                row = Math.Clamp(bufferRow - _buffer.AddressingTop + 1, 1, _buffer.ChildRows);
                column = Math.Clamp(cellColumn + 1, 1, _buffer.Columns);
            }

            var report = AnsiInterpreter.WheelReport(e.Delta > 0, column, row);
            var notches = Math.Max(1, Math.Abs(e.Delta) / 120);
            for (var i = 0; i < notches; i++) StandardInput?.Feed(report);
            return;
        }

        ScrollBy(-Math.Sign(e.Delta) * 3);
    }

    private (int Row, int Column) HitTest(Point location)
    {
        var row = _buffer.ViewTop + (int)(location.Y / _cellSize.Height);
        var column = (int)Math.Round(location.X / _cellSize.Width);
        return (Math.Max(0, row), Math.Max(0, column));
    }

    private bool HasSelection => _selectionStartRow >= 0 &&
        (_selectionStartRow != _selectionEndRow || _selectionStartColumn != _selectionEndColumn);

    private void ClearSelection()
    {
        _selectionStartRow = -1;
        _selectionEndRow = -1;
        Invalidate();
    }

    public void CopySelection()
    {
        if (!HasSelection) return;

        string text;
        lock (_buffer.SyncRoot)
            text = _buffer.GetText(_selectionStartRow, _selectionStartColumn, _selectionEndRow, _selectionEndColumn);

        if (text.Length == 0) return;

        try { Clipboard.SetText(text); }
        catch (Exception) { /* another process owns the clipboard; nothing to do */ }
    }

    public void Paste()
    {
        string text;

        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty; }
        catch (Exception) { return; }

        if (text.Length == 0) return;

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        if (_editing)
        {
            var lines = text.Split('\n');

            if (lines.Length == 1)
            {
                _edit.Insert(_caret, lines[0]);
                _caret += lines[0].Length;
                RedrawInput();
                return;
            }

            // A multi-line paste runs one line per prompt; the rest waits its turn.
            for (var i = 1; i < lines.Length; i++) _pastedLines.Enqueue(lines[i]);

            _edit.Insert(_caret, lines[0]);
            _caret += lines[0].Length;
            RedrawInput();
            CommitLine();
            return;
        }

        if (!RawInputMode)
        {
            StandardInput?.Feed(text);
            return;
        }

        // A console expects the Enter key, which is carriage return, not a line feed. A program
        // that asked for bracketed paste wants the block marked, so it can tell a paste from
        // someone typing very fast and refuse to act on newlines inside it.
        var pasted = text.Replace('\n', '\r');
        StandardInput?.Feed(_interpreter.BracketedPaste ? "\x1b[200~" + pasted + "\x1b[201~" : pasted);
    }

    public void ScrollBy(int lines) => ScrollTo(_buffer.ViewTop + lines);

    public void ScrollTo(int line)
    {
        lock (_buffer.SyncRoot) _buffer.ViewTop = Math.Clamp(line, 0, _buffer.MaxViewTop);
        SyncScrollBar();
        Invalidate();
    }

    private void SyncScrollBar()
    {
        if (InvokeRequired) { try { BeginInvoke(SyncScrollBar); } catch (Exception) { } return; }
        if (IsDisposed || _scrollBar.IsDisposed) return;

        int maximum, value;
        lock (_buffer.SyncRoot)
        {
            maximum = _buffer.MaxViewTop;
            value = Math.Clamp(_buffer.ViewTop, 0, maximum);
        }

        _scrollBar.Minimum = 0;
        _scrollBar.Maximum = Math.Max(0, maximum + _buffer.Rows - 1);
        _scrollBar.LargeChange = Math.Max(1, _buffer.Rows);
        _scrollBar.Value = value;
        _scrollBar.Visible = maximum > 0;
    }

    // ------------------------------------------------------------ painting

    private void MeasureFont()
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var size = TextRenderer.MeasureText(graphics, "MMMMMMMMMM", _font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding);

        _cellSize = new SizeF(size.Width / 10f, size.Height);
        if (_cellSize.Width < 1) _cellSize = new SizeF(8, 16);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateDimensions();
    }

    private void UpdateDimensions()
    {
        if (_cellSize.Width <= 0 || _cellSize.Height <= 0) return;

        // The scrollbar's width comes off whether or not it is showing at the moment. Measuring
        // against the whole client area while it is hidden hands the program a column count that
        // stops fitting the second there is anything to scroll to, and the rightmost cells of
        // every line — the ones a program wraps at — are then drawn underneath it. Reserving the
        // room always also keeps the count still, rather than having it change under a running
        // program each time the scrollbar comes and goes.
        var width = Math.Max(1, ClientSize.Width - _scrollBar.Width);
        var columns = Math.Max(20, (int)(width / _cellSize.Width));
        var rows = Math.Max(4, (int)(ClientSize.Height / _cellSize.Height));

        lock (_buffer.SyncRoot)
        {
            _buffer.Resize(columns, rows);

            // The console under a running program has just been put back to the window's height,
            // and it has not had the chance to fill it yet. Growing it again before its first
            // frame arrives would measure the blank rows it has not drawn on.
            _consoleResizedAt = Environment.TickCount64;
        }

        SyncScrollBar();
        SizeChangedInCells?.Invoke(columns, rows);
    }

    /// <summary>Reports the size in character cells so the shell can set COLUMNS and LINES.</summary>
    public event Action<int, int>? SizeChangedInCells;

    /// <summary>
    /// Where the caret belongs. While the shell is editing a line that is the editor's own caret;
    /// while a program owns the screen it is wherever that program left the cursor. Taking the
    /// first for both is what stopped the caret blinking under a full-screen program: the timer
    /// kept repainting the cell the shell's prompt had last used, which is nowhere near the one
    /// the caret was being drawn in, so the caret itself was only ever redrawn by accident.
    /// </summary>
    private (int Row, int Column) CaretPosition()
    {
        if (_editing) return (_caretRow, _caretColumn);

        lock (_buffer.SyncRoot)
            return (_buffer.CursorRow, Math.Min(_buffer.CursorColumn, Math.Max(0, _buffer.Columns - 1)));
    }

    /// <summary>A program hides the caret while it repaints; the line editor always shows its own.</summary>
    private bool CaretIsShown => _editing || _buffer.CursorVisible;

    private void InvalidateCursor()
    {
        if (IsDisposed) return;

        var (row, column) = CaretPosition();
        InvalidateCell(row, column);

        // The caret may have moved since it was last drawn, and the cell it left has to be
        // repainted too or a copy of it stays on the screen.
        if (_drawnCaret.Y >= 0 && (_drawnCaret.Y != row || _drawnCaret.X != column))
            InvalidateCell(_drawnCaret.Y, _drawnCaret.X);
    }

    private void InvalidateCell(int row, int column)
    {
        var y = (int)((row - _buffer.ViewTop) * _cellSize.Height);
        var x = (int)(column * _cellSize.Width);
        Invalidate(new Rectangle(x - 1, y - 1, (int)_cellSize.Width + 3, (int)_cellSize.Height + 3));
    }

    /// <summary>
    /// Puts the caret back on and restarts its phase when it moves, so it is solid the moment it
    /// arrives somewhere new rather than possibly landing mid-blink and looking like nothing.
    /// </summary>
    private void SyncCaretBlink()
    {
        var position = CaretPosition();
        if (position == _blinkAnchor) return;

        _blinkAnchor = position;
        _cursorVisible = true;
        _cursorTimer.Stop();
        _cursorTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(_buffer.DefaultBackground);

        int viewTop, rows;
        List<TerminalLine> snapshot;

        lock (_buffer.SyncRoot)
        {
            viewTop = _buffer.ViewTop;
            rows = _buffer.Rows;
            snapshot = _buffer.Lines.Skip(viewTop).Take(rows + 1).ToList();
            RefreshHighlightForPaint(_buffer);
        }

        var (selectionTop, selectionTopColumn, selectionBottom, selectionBottomColumn) = NormalisedSelection();

        for (var i = 0; i < snapshot.Count; i++)
        {
            var absoluteRow = viewTop + i;
            var y = i * _cellSize.Height;
            if (y > ClientSize.Height) break;

            DrawLine(graphics, snapshot[i], absoluteRow, y,
                selectionTop, selectionTopColumn, selectionBottom, selectionBottomColumn);
        }

        DrawCursor(graphics, viewTop);
    }

    /// <summary>
    /// Whether a character can be trusted to advance by exactly one cell in the terminal font,
    /// and so may be drawn as part of a longer run rather than placed on the grid by itself.
    /// </summary>
    private static bool FitsTheGrid(char c) => c is >= ' ' and <= '~';

    private (int Top, int TopColumn, int Bottom, int BottomColumn) NormalisedSelection()
    {
        if (!HasSelection) return (-1, 0, -1, 0);

        if (_selectionStartRow < _selectionEndRow ||
            (_selectionStartRow == _selectionEndRow && _selectionStartColumn <= _selectionEndColumn))
            return (_selectionStartRow, _selectionStartColumn, _selectionEndRow, _selectionEndColumn);

        return (_selectionEndRow, _selectionEndColumn, _selectionStartRow, _selectionStartColumn);
    }

    private void DrawLine(Graphics graphics, TerminalLine line, int absoluteRow, float y,
        int selectionTop, int selectionTopColumn, int selectionBottom, int selectionBottomColumn)
    {
        var cells = line.Cells;
        if (cells.Count == 0) return;

        var index = 0;

        while (index < cells.Count)
        {
            var cell = cells[index];
            var selected = Marked(absoluteRow, index, selectionTop, selectionTopColumn, selectionBottom, selectionBottomColumn);

            // Group neighbouring cells that share every visual attribute into a single draw call.
            //
            // Only plain ASCII may be grouped. Drawing a run hands the spacing inside it to the
            // font, which is safe while every glyph advances by exactly one cell; a character the
            // terminal font has no glyph for is fetched from another font, advances by whatever
            // that font says, and drags the rest of the run out of its columns. Anything outside
            // ASCII is therefore placed on the grid one cell at a time.
            var run = index + 1;

            if (FitsTheGrid(cell.Character))
            {
                while (run < cells.Count &&
                       FitsTheGrid(cells[run].Character) &&
                       cells[run].Foreground == cell.Foreground &&
                       cells[run].Background == cell.Background &&
                       cells[run].Flags == cell.Flags &&
                       Marked(absoluteRow, run, selectionTop, selectionTopColumn, selectionBottom, selectionBottomColumn) == selected)
                    run++;
            }

            var text = new string(cells.Skip(index).Take(run - index).Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray());

            var foreground = cell.Foreground.IsEmpty ? _buffer.DefaultForeground : cell.Foreground;
            var background = cell.Background.IsEmpty ? _buffer.DefaultBackground : cell.Background;

            if (cell.Flags.HasFlag(CellFlags.Inverse) || selected) (foreground, background) = (background, foreground);
            if (cell.Flags.HasFlag(CellFlags.Dim)) foreground = Blend(foreground, background, 0.5f);

            var x = index * _cellSize.Width;
            var width = (run - index) * _cellSize.Width;

            if (background != _buffer.DefaultBackground)
            {
                using var brush = new SolidBrush(background);
                graphics.FillRectangle(brush, x, y, width + 0.5f, _cellSize.Height);
            }

            var font = cell.Flags.HasFlag(CellFlags.Bold)
                ? new Font(_font, _font.Style | FontStyle.Bold)
                : _font;

            TextRenderer.DrawText(graphics, text, font, new Point((int)x, (int)y), foreground, TextFormatFlags.NoPadding);

            if (cell.Flags.HasFlag(CellFlags.Underline))
            {
                using var pen = new Pen(foreground);
                graphics.DrawLine(pen, x, y + _cellSize.Height - 1, x + width, y + _cellSize.Height - 1);
            }

            if (!ReferenceEquals(font, _font)) font.Dispose();

            index = run;
        }
    }

    private static bool IsSelected(int row, int column, int top, int topColumn, int bottom, int bottomColumn)
    {
        if (top < 0 || row < top || row > bottom) return false;
        if (row == top && column < topColumn) return false;
        if (row == bottom && column >= bottomColumn) return false;
        return true;
    }

    private static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R * (1 - amount) + b.R * amount),
        (int)(a.G * (1 - amount) + b.G * amount),
        (int)(a.B * (1 - amount) + b.B * amount));

    private void DrawCursor(Graphics graphics, int viewTop)
    {
        _drawnCaret = new Point(-1, -1);

        if (!Focused || !_cursorVisible || !CaretIsShown) return;

        var (row, column) = CaretPosition();

        var y = (row - viewTop) * _cellSize.Height;
        if (y + _cellSize.Height < 0 || y > ClientSize.Height) return;

        var x = column * _cellSize.Width;

        // A bar down the leading edge of the cell, the width of a pipe character. It sits between
        // the character it is on and the one before it, so nothing is covered up and the caret
        // reads as a place to type rather than as a selected letter.
        var width = Math.Max(1f, (float)Math.Round(_cellSize.Width / 7f));

        using var brush = new SolidBrush(_buffer.DefaultForeground);
        graphics.FillRectangle(brush, x, y, width, _cellSize.Height);

        _drawnCaret = new Point(column, row);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);

        _cursorVisible = true;
        _cursorTimer.Stop();
        _cursorTimer.Start();
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        HideSuggestions();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shuttingDown = true;
            _lineReady.Set();
            _cursorTimer.Stop();
            _cursorTimer.Dispose();
            _lineReady.Dispose();
            _font.Dispose();
            _suggestCancel?.Cancel();
            _popup?.Dispose();
        }

        base.Dispose(disposing);
    }
}
