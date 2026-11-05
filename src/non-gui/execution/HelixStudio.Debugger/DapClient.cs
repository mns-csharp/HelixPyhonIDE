using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Debugger
{
    /// <summary>
    /// Debug Adapter Protocol (DAP) client that communicates with a DAP server over TCP.
    /// Uses Content-Length framing (same as LSP) for message transport.
    ///
    /// Architecture (inspired by SharpDevelop's MTA2STA bridge pattern):
    /// - Reader thread: reads DAP messages from the TCP stream. Responses are dispatched
    ///   immediately via ManualResetEventSlim to unblock waiting SendRequest callers.
    ///   Events are enqueued to a BlockingCollection for async dispatch.
    /// - Dispatcher thread: drains the event queue and fires EventReceived handlers.
    ///   This keeps the reader thread free to process responses even when event handlers
    ///   call SendRequest (which would otherwise deadlock).
    /// </summary>
    public class DapClient : IDisposable
    {
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private Thread _readerThread;
        private Thread _dispatcherThread;
        private int _sequenceNumber;
        private readonly ConcurrentDictionary<int, ManualResetEventSlim> _pendingSignals;
        private readonly ConcurrentDictionary<int, JObject> _pendingResponses;
        private readonly BlockingCollection<DapEventArgs> _eventQueue;
        private volatile bool _running;
        private bool _disposed;

        /// <summary>Fired when the debug adapter sends an event (stopped, output, etc.).</summary>
        public event EventHandler<DapEventArgs> EventReceived;

        /// <summary>Fired when the connection is lost.</summary>
        public event EventHandler Disconnected;

        public bool IsConnected
        {
            get { return _tcpClient != null && _tcpClient.Connected && _running; }
        }

        public DapClient()
        {
            _pendingSignals = new ConcurrentDictionary<int, ManualResetEventSlim>();
            _pendingResponses = new ConcurrentDictionary<int, JObject>();
            _eventQueue = new BlockingCollection<DapEventArgs>();
        }

        /// <summary>
        /// Connects to a DAP server with retry logic.
        /// </summary>
        public void Connect(string host, int port, int maxRetries = 50, int retryDelayMs = 100)
        {
            Exception lastError = null;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    _tcpClient = new TcpClient();
                    _tcpClient.Connect(host, port);
                    _stream = _tcpClient.GetStream();
                    _running = true;

                    _readerThread = new Thread(ReaderLoop);
                    _readerThread.IsBackground = true;
                    _readerThread.Name = "DAP Reader";
                    _readerThread.Start();

                    _dispatcherThread = new Thread(DispatcherLoop);
                    _dispatcherThread.IsBackground = true;
                    _dispatcherThread.Name = "DAP Dispatcher";
                    _dispatcherThread.Start();

                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (_tcpClient != null)
                    {
                        _tcpClient.Close();
                        _tcpClient = null;
                    }
                    Thread.Sleep(retryDelayMs);
                }
            }
            throw new InvalidOperationException(
                string.Format("Failed to connect to DAP server at {0}:{1} after {2} retries: {3}",
                    host, port, maxRetries, lastError != null ? lastError.Message : "Unknown error"));
        }

        /// <summary>
        /// Sends a DAP request and waits for the response synchronously.
        /// Safe to call from any thread including event handlers (the reader thread
        /// is never blocked by event dispatch thanks to the dispatcher thread pattern).
        /// </summary>
        public JObject SendRequest(string command, JObject arguments = null, int timeoutMs = 15000)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to debug adapter");

            int seq = Interlocked.Increment(ref _sequenceNumber);

            var request = new JObject
            {
                ["seq"] = seq,
                ["type"] = "request",
                ["command"] = command
            };
            if (arguments != null)
                request["arguments"] = arguments;

            var signal = new ManualResetEventSlim(false);
            _pendingSignals[seq] = signal;

            SendMessage(request);

            if (!signal.Wait(timeoutMs))
            {
                ManualResetEventSlim removed;
                _pendingSignals.TryRemove(seq, out removed);
                throw new TimeoutException(
                    string.Format("Timeout waiting for response to '{0}' (seq={1})", command, seq));
            }

            signal.Dispose();
            ManualResetEventSlim sig;
            _pendingSignals.TryRemove(seq, out sig);

            JObject response;
            _pendingResponses.TryRemove(seq, out response);
            return response;
        }

        /// <summary>
        /// Sends a DAP request without waiting for a response (fire-and-forget).
        /// </summary>
        public void SendRequestAsync(string command, JObject arguments = null)
        {
            if (!IsConnected) return;

            int seq = Interlocked.Increment(ref _sequenceNumber);

            var request = new JObject
            {
                ["seq"] = seq,
                ["type"] = "request",
                ["command"] = command
            };
            if (arguments != null)
                request["arguments"] = arguments;

            SendMessage(request);
        }

        private void SendMessage(JObject message)
        {
            string json = message.ToString(Formatting.None);
            byte[] body = Encoding.UTF8.GetBytes(json);
            string header = string.Format("Content-Length: {0}\r\n\r\n", body.Length);
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);

            lock (_stream)
            {
                _stream.Write(headerBytes, 0, headerBytes.Length);
                _stream.Write(body, 0, body.Length);
                _stream.Flush();
            }
        }

        /// <summary>
        /// Reader thread: reads DAP messages from the stream.
        /// Responses are signaled immediately; events are enqueued for async dispatch.
        /// This thread must never block on event handling to avoid deadlocks.
        /// </summary>
        private void ReaderLoop()
        {
            try
            {
                while (_running)
                {
                    var message = ReadMessage();
                    if (message == null) break;

                    string type = message.Value<string>("type");

                    if (type == "response")
                    {
                        int requestSeq = message.Value<int>("request_seq");
                        _pendingResponses[requestSeq] = message;

                        ManualResetEventSlim signal;
                        if (_pendingSignals.TryGetValue(requestSeq, out signal))
                        {
                            // A completed or timed-out SendRequest disposes its signal and removes
                            // it from the dictionary; that can race this Set() (we may have fetched
                            // the reference just before disposal). Swallow the resulting
                            // ObjectDisposedException — otherwise it would escape to the outer
                            // catch and break the reader loop, silently killing the whole debug
                            // session over a benign, already-satisfied wait.
                            try { signal.Set(); }
                            catch (ObjectDisposedException) { }
                        }
                    }
                    else if (type == "event")
                    {
                        // Enqueue for dispatcher thread — never fire inline
                        string eventName = message.Value<string>("event");
                        JObject body = message.Value<JObject>("body") ?? new JObject();
                        try
                        {
                            _eventQueue.Add(new DapEventArgs
                            {
                                EventType = eventName,
                                Body = body,
                                RawMessage = message
                            });
                        }
                        catch (InvalidOperationException)
                        {
                            // Queue has been completed (disposing)
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Connection lost or stream closed
            }
            finally
            {
                _running = false;
                // Complete the event queue so the dispatcher exits
                try { _eventQueue.CompleteAdding(); } catch { }
                var handler = Disconnected;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Dispatcher thread: processes queued events and fires EventReceived.
        /// Runs on a separate thread from the reader, so event handlers can safely
        /// call SendRequest without deadlocking the reader.
        /// This is the MTA2STA bridge pattern from SharpDevelop's debugger.
        /// </summary>
        private void DispatcherLoop()
        {
            try
            {
                foreach (var evt in _eventQueue.GetConsumingEnumerable())
                {
                    var handler = EventReceived;
                    if (handler != null)
                    {
                        try
                        {
                            handler(this, evt);
                        }
                        catch (Exception)
                        {
                            // Don't let handler exceptions kill the dispatcher
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (ArgumentNullException)
            {
                // BlockingCollection disposed during enumeration - normal during shutdown
            }
            catch (ObjectDisposedException)
            {
                // Collection disposed - normal during shutdown
            }
            catch (InvalidOperationException)
            {
                // Collection completed - normal during shutdown
            }
        }

        private JObject ReadMessage()
        {
            // Read headers until we find Content-Length
            int contentLength = -1;
            string headerLine;

            while ((headerLine = ReadHeaderLine()) != null)
            {
                if (string.IsNullOrEmpty(headerLine))
                    break; // Empty line = end of headers

                if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    string value = headerLine.Substring("Content-Length:".Length).Trim();
                    contentLength = int.Parse(value);
                }
            }

            if (contentLength <= 0)
                return null;

            // Read exactly contentLength bytes
            byte[] bodyBytes = new byte[contentLength];
            int totalRead = 0;
            while (totalRead < contentLength)
            {
                int read = _stream.Read(bodyBytes, totalRead, contentLength - totalRead);
                if (read == 0) return null; // Connection closed
                totalRead += read;
            }

            string json = Encoding.UTF8.GetString(bodyBytes);
            return JObject.Parse(json);
        }

        private string ReadHeaderLine()
        {
            var sb = new StringBuilder();
            int prev = -1;

            while (true)
            {
                int b = _stream.ReadByte();
                if (b == -1) return null; // Connection closed

                if (b == '\n' && prev == '\r')
                {
                    // Remove trailing \r
                    if (sb.Length > 0) sb.Length--;
                    return sb.ToString();
                }

                sb.Append((char)b);
                prev = b;
            }
        }

        public void Disconnect()
        {
            _running = false;
            try { _eventQueue.CompleteAdding(); } catch { }
            try
            {
                if (_stream != null) _stream.Close();
                if (_tcpClient != null) _tcpClient.Close();
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Disconnect();

                foreach (var kvp in _pendingSignals)
                {
                    kvp.Value.Set();
                    kvp.Value.Dispose();
                }
                _pendingSignals.Clear();
                _pendingResponses.Clear();

                try { _eventQueue.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Event args for DAP events received from the debug adapter.
    /// </summary>
    public class DapEventArgs : EventArgs
    {
        public string EventType { get; set; }
        public JObject Body { get; set; }
        public JObject RawMessage { get; set; }
    }
}
