namespace HelixPythonNamespace.Terminal.Shell.Runtime;

/// <summary>
/// An in-memory pipe: reads block until data arrives or the write end is closed. This is what
/// connects the stages of a pipeline, each of which runs on its own thread.
/// </summary>
public sealed class BlockingPipeStream : Stream
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly int _capacityBytes;
    private byte[]? _current;
    private int _currentOffset;
    private int _buffered;
    private bool _writerClosed;
    private bool _readerClosed;

    public BlockingPipeStream(int capacityBytes = 1 << 20) => _capacityBytes = capacityBytes;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Signals end-of-stream to readers. Called by the writing stage when it finishes.</summary>
    public void CloseWrite()
    {
        lock (_gate)
        {
            _writerClosed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Discards buffered data and unblocks writers; used when the reader exits early.</summary>
    public void CloseRead()
    {
        lock (_gate)
        {
            _readerClosed = true;
            _chunks.Clear();
            _current = null;
            _buffered = 0;
            Monitor.PulseAll(_gate);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;

        var chunk = new byte[count];
        Buffer.BlockCopy(buffer, offset, chunk, 0, count);

        lock (_gate)
        {
            // Once the reader is gone the write is a broken pipe. Reporting it is what stops
            // `yes | head -n 3`: the producer has to be told nobody is listening any more.
            if (_readerClosed) throw new IOException("Broken pipe");

            while (_buffered >= _capacityBytes && !_readerClosed) Monitor.Wait(_gate);
            if (_readerClosed) throw new IOException("Broken pipe");

            _chunks.Enqueue(chunk);
            _buffered += chunk.Length;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;

        lock (_gate)
        {
            while (true)
            {
                if (_current is not null && _currentOffset < _current.Length) break;

                if (_chunks.Count > 0)
                {
                    _current = _chunks.Dequeue();
                    _currentOffset = 0;
                    _buffered -= _current.Length;
                    Monitor.PulseAll(_gate);
                    continue;
                }

                if (_writerClosed || _readerClosed) return 0;
                Monitor.Wait(_gate);
            }

            var available = _current!.Length - _currentOffset;
            var take = Math.Min(available, count);
            Buffer.BlockCopy(_current, _currentOffset, buffer, offset, take);
            _currentOffset += take;
            return take;
        }
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseWrite();
        base.Dispose(disposing);
    }
}
