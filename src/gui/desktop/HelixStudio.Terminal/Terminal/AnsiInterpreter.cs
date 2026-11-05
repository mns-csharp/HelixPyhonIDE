using System.Drawing;
using System.Text;

namespace HelixPythonNamespace.Terminal.App.Terminal;

/// <summary>
/// Feeds text into a <see cref="TerminalBuffer"/>, interpreting the escape sequences the shell
/// and common tools emit: SGR colours and attributes, cursor movement, and erase commands.
/// Anything else is parsed and discarded so it never leaks onto the screen as garbage.
/// </summary>
public sealed class AnsiInterpreter
{
    private enum State
    {
        Text,
        Escape,
        ControlSequence,
        OperatingSystemCommand,
    }

    private readonly TerminalBuffer _buffer;
    private readonly StringBuilder _parameters = new();
    private State _state = State.Text;

    public AnsiInterpreter(TerminalBuffer buffer) => _buffer = buffer;

    /// <summary>Raised when an OSC 0/2 sequence sets the window title.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>
    /// Where an answer to a query goes. A program may ask where the cursor is or what kind of
    /// terminal this is, and then wait for the reply before drawing anything, so this has to
    /// reach the program's input.
    /// </summary>
    public Action<string>? Respond { get; set; }

    /// <summary>
    /// Set by <c>ESC[?2004h</c>. A program that turns this on wants pasted text wrapped in
    /// markers so it can tell a paste from someone typing very quickly.
    /// </summary>
    public bool BracketedPaste { get; private set; }

    // Mouse tracking as asked for by ESC[?1000h, ?1002h or ?1003h, and the SGR encoding ?1006h.
    private readonly HashSet<int> _mouseTracking = new();
    private bool _sgrMouse;

    /// <summary>
    /// True while the program wants the wheel reported to it in SGR form. Claude Code asks for
    /// this on every frame: it keeps its whole transcript itself and scrolls it on the wheel, the
    /// way it does in Windows Terminal, so the wheel belongs to it rather than to the scrollback.
    /// </summary>
    public bool ReportsWheel => _mouseTracking.Count > 0 && _sgrMouse;

    /// <summary>
    /// Forgets the input modes the program asked for. A program does not switch mouse reporting
    /// off on its way out — a trace of Claude Code has no ESC[?1000l in it — so without this the
    /// wheel would go on being sent to a program that has gone.
    /// </summary>
    public void ProgramEnded()
    {
        _mouseTracking.Clear();
        _sgrMouse = false;
    }

    /// <summary>One notch of the wheel at a 1-based cell, as xterm's SGR mouse protocol reports it.</summary>
    public static string WheelReport(bool up, int column, int row) =>
        $"\x1b[<{(up ? 64 : 65)};{column};{row}M";

    public void Write(string text)
    {
        foreach (var c in text) Write(c);
    }

    public void Write(char c)
    {
        switch (_state)
        {
            case State.Text:
                WriteText(c);
                break;

            case State.Escape:
                if (c == '[') { _state = State.ControlSequence; _parameters.Clear(); }
                else if (c == ']') { _state = State.OperatingSystemCommand; _parameters.Clear(); }
                else if (c == 'c') { _buffer.Clear(); _state = State.Text; }
                else if (c == '7') { _buffer.SaveCursor(); _state = State.Text; }
                else if (c == '8') { _buffer.RestoreCursor(); _state = State.Text; }
                else if (c == 'M') { _buffer.ReverseLineFeed(); _state = State.Text; }
                else _state = State.Text; // single-character escapes we do not act on
                break;

            case State.ControlSequence:
                if (char.IsAsciiLetter(c) || c == '@' || c == '`')
                {
                    Execute(c, _parameters.ToString());
                    _state = State.Text;
                }
                else
                {
                    _parameters.Append(c);
                }

                break;

            case State.OperatingSystemCommand:
                // Terminated by BEL or ESC \ ; we act on the title forms and on OSC 8 links.
                if (c == '\a' || c == '\x9c')
                {
                    FinishOperatingSystemCommand();
                }
                else if (c == '\x1b')
                {
                    FinishOperatingSystemCommand();
                    _state = State.Escape;
                    return;
                }
                else
                {
                    _parameters.Append(c);
                }

                break;
        }
    }

    private void FinishOperatingSystemCommand()
    {
        var text = _parameters.ToString();
        _parameters.Clear();
        _state = State.Text;

        var separator = text.IndexOf(';');
        if (separator < 0) return;

        var code = text[..separator];

        if (code is "0" or "2")
        {
            TitleChanged?.Invoke(text[(separator + 1)..]);
        }
        else if (code == "8")
        {
            // 8;params;uri. A URI opens a link and an empty one closes it.
            var rest = text[(separator + 1)..];
            var uriStart = rest.IndexOf(';');
            if (uriStart < 0) return;

            var uri = rest[(uriStart + 1)..];
            if (uri.Length == 0) _buffer.EndLink();
            else _buffer.BeginLink(uri);
        }
    }

    private void WriteText(char c)
    {
        switch (c)
        {
            case '\x1b': _state = State.Escape; break;

            // A tty in cooked mode turns LF into CR+LF, and the shell's own output and anything
            // arriving on a plain pipe is written as though one had. Without it, output written
            // with a bare '\n' leaves the column where it was and every following line steps
            // further right.
            //
            // A pseudo-console has already done that translation on its child's behalf, so an LF
            // coming from one means what it says: down a row, same column. It sends a carriage
            // return of its own whenever it wants column zero, and it uses the bare form
            // deliberately — `CSI r;3H`, `CSI K`, LF, then the text is how it starts a body line
            // two columns in without re-addressing. Taking the column back to zero there put
            // every one of those lines two columns to the left of where the console believed it
            // was; from then on it stepped over cells that no longer held what it thought they
            // held, and the old frame showed through the new one as scrambled text.
            case '\n':
                if (!_buffer.ChildOwnsScreen) _buffer.CarriageReturn();
                _buffer.NewLine();
                break;
            case '\r': _buffer.CarriageReturn(); break;
            case '\b': _buffer.Backspace(); break;
            case '\t': _buffer.Tab(); break;
            case '\a': break;      // bell: nothing audible, deliberately
            case '\0': break;
            default:
                if (!char.IsControl(c)) _buffer.PutChar(c);
                break;
        }
    }

    private void Execute(char command, string parameterText)
    {
        if (parameterText.StartsWith('?'))
        {
            SetPrivateMode(parameterText[1..], command);
            return;
        }

        // Sequences with an intermediate byte, such as the ESC[>0q version query, are asking
        // about the terminal rather than telling it to do something.
        if (parameterText.StartsWith('>') || parameterText.StartsWith('<') || parameterText.StartsWith('=')) return;

        var numbers = ParseParameters(parameterText);
        int At(int index, int fallback) => index < numbers.Count && numbers[index] > 0 ? numbers[index] : fallback;
        int AtRaw(int index, int fallback) => index < numbers.Count ? numbers[index] : fallback;

        switch (command)
        {
            case 'A': _buffer.MoveCursor(-At(0, 1), 0); break;
            case 'B': _buffer.MoveCursor(At(0, 1), 0); break;
            case 'C': _buffer.MoveCursor(0, At(0, 1)); break;
            case 'D': _buffer.MoveCursor(0, -At(0, 1)); break;
            case 'E': _buffer.MoveCursor(At(0, 1), 0); _buffer.CarriageReturn(); break;
            case 'F': _buffer.MoveCursor(-At(0, 1), 0); _buffer.CarriageReturn(); break;
            case 'G' or '`': _buffer.CursorColumn = Math.Clamp(At(0, 1) - 1, 0, _buffer.Columns - 1); break;
            case 'd': _buffer.SetCursorRowInViewport(At(0, 1) - 1); break;
            case 'H' or 'f': _buffer.SetCursorInViewport(At(0, 1) - 1, At(1, 1) - 1); break;
            case 'J': _buffer.EraseInDisplay(AtRaw(0, 0)); break;
            case 'K': _buffer.EraseInLine(AtRaw(0, 0)); break;

            // The repainting vocabulary: blank cells in place, open or close gaps, move whole
            // lines within the scrolling region.
            case 'X': _buffer.EraseCharacters(At(0, 1)); break;
            case '@': _buffer.InsertCharacters(At(0, 1)); break;
            case 'P': _buffer.DeleteCharacters(At(0, 1)); break;
            case 'L': _buffer.InsertLines(At(0, 1)); break;
            case 'M': _buffer.DeleteLines(At(0, 1)); break;
            case 'S': _buffer.ScrollUp(At(0, 1)); break;
            case 'T': _buffer.ScrollDown(At(0, 1)); break;
            case 'r': _buffer.SetScrollRegion(At(0, 1) - 1, At(1, _buffer.Rows) - 1); break;

            case 'm': ApplyGraphics(numbers); break;
            case 's': _buffer.SaveCursor(); break;
            case 'u': _buffer.RestoreCursor(); break;
            case 'n': ReportStatus(AtRaw(0, 0)); break;
            case 'c': Respond?.Invoke("\x1b[?1;2c"); break;   // "a VT100 with an advanced video option"
        }
    }

    /// <summary>Answers the questions a program asks before it will draw.</summary>
    private void ReportStatus(int request)
    {
        switch (request)
        {
            case 5:
                Respond?.Invoke("\x1b[0n");   // in good working order
                break;

            case 6:
                var row = _buffer.CursorRow - _buffer.AddressingTop + 1;
                var column = _buffer.CursorColumn + 1;
                Respond?.Invoke($"\x1b[{Math.Max(1, row)};{Math.Max(1, column)}R");
                break;
        }
    }

    /// <summary>
    /// The <c>ESC[?…h</c> and <c>ESC[?…l</c> switches. Most are about input the screen does not
    /// send — focus events, clicks — and turning them on costs nothing because nothing then sends
    /// those events. The ones that matter are whether to draw the caret, whether a paste should be
    /// marked as one, and whether the wheel is the program's.
    /// </summary>
    private void SetPrivateMode(string parameterText, char command)
    {
        if (command is not ('h' or 'l')) return;

        var enabled = command == 'h';

        foreach (var mode in ParseParameters(parameterText))
        {
            switch (mode)
            {
                case 25: _buffer.CursorVisible = enabled; break;
                case 2004: BracketedPaste = enabled; break;

                case 1000 or 1002 or 1003:
                    if (enabled) _mouseTracking.Add(mode);
                    else _mouseTracking.Remove(mode);
                    break;

                case 1006: _sgrMouse = enabled; break;

                case 47 or 1047:
                    if (enabled) _buffer.EnterAlternateScreen();
                    else _buffer.LeaveAlternateScreen();
                    break;

                case 1048:
                    if (enabled) _buffer.SaveCursor();
                    else _buffer.RestoreCursor();
                    break;

                // The usual one: save the cursor and switch in a single request, so the prompt
                // is exactly where it was when the program lets go of the screen.
                case 1049:
                    if (enabled) { _buffer.SaveCursor(); _buffer.EnterAlternateScreen(); }
                    else { _buffer.LeaveAlternateScreen(); _buffer.RestoreCursor(); }
                    break;
            }
        }
    }

    private static List<int> ParseParameters(string text)
    {
        var numbers = new List<int>();

        foreach (var part in text.Split(';'))
            numbers.Add(int.TryParse(part, out var value) ? value : 0);

        if (numbers.Count == 0) numbers.Add(0);
        return numbers;
    }

    private void ApplyGraphics(List<int> parameters)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var code = parameters[i];

            switch (code)
            {
                case 0:
                    _buffer.CurrentForeground = _buffer.DefaultForeground;
                    _buffer.CurrentBackground = Color.Empty;
                    _buffer.CurrentFlags = CellFlags.None;
                    break;

                case 1: _buffer.CurrentFlags |= CellFlags.Bold; break;
                case 2: _buffer.CurrentFlags |= CellFlags.Dim; break;
                case 4: _buffer.CurrentFlags |= CellFlags.Underline; break;
                case 7: _buffer.CurrentFlags |= CellFlags.Inverse; break;
                case 21 or 22: _buffer.CurrentFlags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 24: _buffer.CurrentFlags &= ~CellFlags.Underline; break;
                case 27: _buffer.CurrentFlags &= ~CellFlags.Inverse; break;

                case >= 30 and <= 37:
                    _buffer.CurrentForeground = TerminalPalette.Standard(code - 30, _buffer.CurrentFlags.HasFlag(CellFlags.Bold));
                    break;

                case 39: _buffer.CurrentForeground = _buffer.DefaultForeground; break;

                case >= 40 and <= 47:
                    _buffer.CurrentBackground = TerminalPalette.Standard(code - 40, false);
                    break;

                case 49: _buffer.CurrentBackground = Color.Empty; break;

                case >= 90 and <= 97:
                    _buffer.CurrentForeground = TerminalPalette.Standard(code - 90, true);
                    break;

                case >= 100 and <= 107:
                    _buffer.CurrentBackground = TerminalPalette.Standard(code - 100, true);
                    break;

                case 38 or 48:
                {
                    var isForeground = code == 38;
                    if (i + 1 >= parameters.Count) break;

                    if (parameters[i + 1] == 5 && i + 2 < parameters.Count)
                    {
                        var colour = TerminalPalette.Extended(parameters[i + 2]);
                        if (isForeground) _buffer.CurrentForeground = colour;
                        else _buffer.CurrentBackground = colour;
                        i += 2;
                    }
                    else if (parameters[i + 1] == 2 && i + 4 < parameters.Count)
                    {
                        var colour = Color.FromArgb(parameters[i + 2], parameters[i + 3], parameters[i + 4]);
                        if (isForeground) _buffer.CurrentForeground = colour;
                        else _buffer.CurrentBackground = colour;
                        i += 4;
                    }

                    break;
                }
            }
        }
    }
}
