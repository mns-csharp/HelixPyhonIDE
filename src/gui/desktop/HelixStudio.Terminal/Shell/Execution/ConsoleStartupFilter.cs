using System.Text;

namespace HelixPythonNamespace.Terminal.Shell.Execution;

/// <summary>
/// Removes the screen-clearing a pseudo-console does before the program it hosts has written
/// anything.
///
/// A pseudo-console believes it is opening on a blank screen, so it starts every session by
/// making one: erase the display, move to the top left. Delivered to a screen that already has a
/// prompt and a session's history on it, that wipes the lot — running <c>git --version</c> would
/// cost you everything above it, and the output would then be written over the top of whatever
/// survived.
///
/// So until the program writes its first character, erases and moves-to-home are dropped and
/// everything else — colours, cursor visibility, the window title — is passed along untouched.
/// The program's own setup is mixed into that opening frame (node sets its output colour there,
/// between the clear and the home), which is why this reads the sequences properly rather than
/// matching a fixed list. From the first character onwards the filter is out of the way for good,
/// so a full-screen program that clears and repaints on its own terms still does exactly that.
/// </summary>
public sealed class ConsoleStartupFilter : Stream
{
    private const byte Escape = 0x1b;
    private const byte Bell = 0x07;

    /// <summary>An unterminated sequence this long is not one; stop waiting and let it through.</summary>
    private const int LongestPlausibleSequence = 256;

    private readonly Stream _inner;
    private readonly List<byte> _pending = new();
    private bool _opening = true;

    public ConsoleStartupFilter(Stream inner) => _inner = inner;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;

        if (!_opening)
        {
            _inner.Write(buffer, offset, count);
            return;
        }

        for (var i = 0; i < count; i++) _pending.Add(buffer[offset + i]);
        Consume();
    }

    /// <summary>
    /// Releases anything still held back. The opening frame can arrive split across reads, so a
    /// part-formed sequence may be waiting when the program ends; it is written out rather than
    /// swallowed, because something unrecognised is somebody's content.
    /// </summary>
    public void Complete()
    {
        if (_opening) StopFiltering();
    }

    private void Consume()
    {
        while (_opening && _pending.Count > 0)
        {
            // Anything that is not an escape sequence is the program's output, and its arrival
            // is what ends the opening frame.
            if (_pending[0] != Escape)
            {
                StopFiltering();
                return;
            }

            if (!TryMeasureSequence(out var length, out var clearsTheScreen))
            {
                if (_pending.Count <= LongestPlausibleSequence) return;

                StopFiltering();
                return;
            }

            // The console's own opening never erases the scrollback. ESC[3J is the program wiping
            // its whole buffer — cmd's cls is ScrollConsoleScreenBufferW over all of it, which the
            // console sends as ESC[3J ESC[2J ESC[H — and it is only here because it happened before
            // the first frame was drawn. The frame is the program's, so it goes through whole.
            if (length == 4 && _pending[2] == (byte)'3' && _pending[3] == (byte)'J')
            {
                StopFiltering();
                return;
            }

            if (clearsTheScreen) _pending.RemoveRange(0, length);
            else WriteFront(length);
        }
    }

    /// <summary>
    /// Measures the escape sequence at the front of the buffer and says whether it is one of the
    /// two that would destroy what is already on screen. False when not all of it has arrived.
    /// </summary>
    private bool TryMeasureSequence(out int length, out bool clearsTheScreen)
    {
        length = 0;
        clearsTheScreen = false;

        if (_pending.Count < 2) return false;

        switch ((char)_pending[1])
        {
            case '[':
                return TryMeasureControlSequence(out length, out clearsTheScreen);

            case ']':
                return TryMeasureOperatingSystemCommand(out length);

            default:
                // A two-byte escape such as ESC = or ESC >; nothing that touches the screen.
                length = 2;
                return true;
        }
    }

    private bool TryMeasureControlSequence(out int length, out bool clearsTheScreen)
    {
        length = 0;
        clearsTheScreen = false;

        for (var i = 2; i < _pending.Count; i++)
        {
            var b = _pending[i];
            if (b is < 0x40 or > 0x7e) continue;   // still in the parameters

            length = i + 1;
            var parameters = Encoding.ASCII.GetString(_pending.GetRange(2, i - 2).ToArray());

            clearsTheScreen = (char)b switch
            {
                // Erase in display. Erasing the scrollback is caught before this is acted on.
                'J' => !parameters.StartsWith('?'),

                // A move to the top left. Anywhere else is the program placing its own output.
                'H' or 'f' => IsOrigin(parameters),

                _ => false,
            };

            return true;
        }

        return false;
    }

    /// <summary>Cursor addressing with every coordinate absent, zero or one means the top left.</summary>
    private static bool IsOrigin(string parameters)
    {
        if (parameters.Length == 0) return true;
        if (parameters.StartsWith('?')) return false;

        foreach (var part in parameters.Split(';'))
            if (part.Length > 0 && part != "0" && part != "1") return false;

        return true;
    }

    private bool TryMeasureOperatingSystemCommand(out int length)
    {
        length = 0;

        for (var i = 2; i < _pending.Count; i++)
        {
            if (_pending[i] == Bell)
            {
                length = i + 1;
                return true;
            }

            // The other terminator is ESC backslash.
            if (_pending[i] == Escape && i + 1 < _pending.Count && _pending[i + 1] == (byte)'\\')
            {
                length = i + 2;
                return true;
            }
        }

        return false;
    }

    private void WriteFront(int length)
    {
        _inner.Write(_pending.GetRange(0, length).ToArray(), 0, length);
        _pending.RemoveRange(0, length);
    }

    private void StopFiltering()
    {
        if (_pending.Count > 0)
        {
            var remainder = _pending.ToArray();
            _inner.Write(remainder, 0, remainder.Length);
            _pending.Clear();
        }

        _opening = false;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
