using System.Text;
using HelixPythonNamespace.Terminal.Shell.Execution;
using HelixPythonNamespace.Terminal.Shell.Runtime;

namespace HelixPythonNamespace.Terminal.App.Terminal;

/// <summary>
/// The shell's standard output and error. Bytes arrive on shell threads, are decoded as UTF-8
/// (tolerating multi-byte characters split across writes) and handed to the screen.
/// </summary>
public sealed class TerminalOutputStream : Stream, ITerminalStream
{
    private readonly Action<string> _sink;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly object _gate = new();

    public TerminalOutputStream(Action<string> sink) => _sink = sink;

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

        // The screen is handed the decoded text inside the lock, not after it. A command's output
        // and its errors are the same stream object written from two pump threads, and decoding
        // under the lock but delivering outside it lets the second thread reach the screen first —
        // so two lines that were written in order arrive in the other one, or interleaved.
        lock (_gate)
        {
            var chars = new char[_decoder.GetCharCount(buffer, offset, count, flush: false)];
            var written = _decoder.GetChars(buffer, offset, count, chars, 0, flush: false);
            if (written == 0) return;

            _sink(new string(chars, 0, written));
        }
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// The shell's standard input. The UI pushes typed text in; commands block on <see cref="Read"/>
/// until a line arrives, Ctrl+D signals end of input, and Ctrl+C releases anyone waiting.
/// </summary>
public sealed class TerminalInputStream : Stream, IUnblockableInput, ITerminalStream, IRawInputSource
{
    private readonly object _gate = new();
    private readonly Queue<byte> _queue = new();
    private bool _endOfInput;
    private bool _unblocked;

    /// <summary>Raised the first time a command blocks waiting for input, so the UI can show a cursor.</summary>
    public event Action? InputRequested;

    /// <summary>Raised when a child takes the keyboard over, and again when it gives it back.</summary>
    public event Action<bool>? RawInputChanged;

    public void BeginRawInput() => RawInputChanged?.Invoke(true);

    public void EndRawInput() => RawInputChanged?.Invoke(false);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>True while a command is blocked waiting for the user to type something.</summary>
    public bool IsWaiting { get; private set; }

    public void Feed(string text)
    {
        if (text.Length == 0) return;

        lock (_gate)
        {
            foreach (var b in Encoding.UTF8.GetBytes(text)) _queue.Enqueue(b);
            _endOfInput = false;
            _unblocked = false;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Ctrl+D: the next read returns end-of-stream.</summary>
    public void SignalEndOfInput()
    {
        lock (_gate)
        {
            _endOfInput = true;
            Monitor.PulseAll(_gate);
        }
    }

    public void UnblockPendingReads()
    {
        lock (_gate)
        {
            _unblocked = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Throws away anything typed but not yet consumed, used when a command is interrupted.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _queue.Clear();
            _endOfInput = false;
            _unblocked = false;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;

        var raised = false;

        lock (_gate)
        {
            while (_queue.Count == 0)
            {
                if (_endOfInput) { _endOfInput = false; return 0; }
                if (_unblocked) { _unblocked = false; return 0; }

                if (!raised)
                {
                    raised = true;
                    IsWaiting = true;
                    Monitor.Exit(_gate);
                    try { InputRequested?.Invoke(); }
                    finally { Monitor.Enter(_gate); }
                    continue;
                }

                Monitor.Wait(_gate);
            }

            IsWaiting = false;

            var taken = 0;
            while (taken < count && _queue.Count > 0) buffer[offset + taken++] = _queue.Dequeue();
            return taken;
        }
    }

    public override void Flush() { }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
