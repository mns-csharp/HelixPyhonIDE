using System.Text;

namespace HelixPythonNamespace.Terminal.Shell.Runtime;

/// <summary>
/// The three standard streams a command runs with. Redirection and pipelines work by handing a
/// command a different <see cref="ShellIo"/>, so nothing in the shell has to know about real
/// file descriptors.
/// </summary>
/// <summary>
/// Marker for a stream that is attached to the interactive screen. Commands use it the way a
/// Unix tool uses isatty: <c>ls</c> only uses columns and colour when a person will see them.
/// </summary>
public interface ITerminalStream
{
}

/// <summary>
/// Implemented by an interactive input stream that can stop editing lines on the child's behalf.
/// A program on a pseudo-console does its own echoing and line editing, so while one is running
/// the screen must hand over every keystroke untouched — arrows and Ctrl combinations included —
/// and print none of them itself.
/// </summary>
public interface IRawInputSource
{
    void BeginRawInput();

    void EndRawInput();
}

public sealed class ShellIo : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private TextWriter? _out;
    private TextWriter? _error;
    private TextReader? _in;

    public ShellIo(Stream input, Stream output, Stream error)
    {
        Input = input;
        Output = output;
        Error = error;
    }

    public Stream Input { get; }

    public Stream Output { get; }

    public Stream Error { get; }

    /// <summary>Streams this object owns and must close when the command finishes.</summary>
    public List<IDisposable> Owned { get; } = new();

    /// <summary>True when standard output goes to the screen rather than a pipe or a file.</summary>
    public bool IsOutputTerminal => Output is ITerminalStream;

    /// <summary>
    /// True when all three streams are the screen, so a child can be given the terminal whole.
    /// Any redirection or pipe at all takes this away: <c>foo &gt; log</c> has to keep writing
    /// bytes to the file rather than to a console the file cannot represent.
    /// </summary>
    public bool IsFullyTerminal => Input is ITerminalStream && Output is ITerminalStream && Error is ITerminalStream;

    public TextWriter Out => _out ??= new StreamWriter(Output, Utf8NoBom, 4096, leaveOpen: true) { AutoFlush = false };

    public TextWriter ErrorOut => _error ??= new StreamWriter(Error, Utf8NoBom, 4096, leaveOpen: true) { AutoFlush = false };

    public TextReader In => _in ??= new StreamReader(Input, Utf8NoBom, false, 4096, leaveOpen: true);

    /// <summary>Replaces one stream, keeping the others; used when applying a redirection.</summary>
    public ShellIo With(Stream? input = null, Stream? output = null, Stream? error = null) =>
        new(input ?? Input, output ?? Output, error ?? Error);

    /// <summary>
    /// Pushes buffered text out. A broken pipe is normal here (the reader finished first), so
    /// flushing never throws; commands that care learn about it from their own writes.
    /// </summary>
    public void Flush()
    {
        try { _out?.Flush(); } catch (Exception) { }
        try { _error?.Flush(); } catch (Exception) { }
        try { Output.Flush(); } catch (Exception) { }
        try { Error.Flush(); } catch (Exception) { }
    }

    public void Dispose()
    {
        Flush();

        foreach (var item in Owned)
        {
            try { item.Dispose(); } catch (Exception) { /* best effort */ }
        }

        Owned.Clear();
    }
}
