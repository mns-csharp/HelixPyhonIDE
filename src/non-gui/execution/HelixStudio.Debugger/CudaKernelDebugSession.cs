namespace HelixPythonNamespace.Debugger
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using HelixPythonNamespace.CudaEmulator;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// The IDE's half of a kernel debug session that runs INSIDE a live Python program.
    ///
    /// <para>
    /// It is the second engine in a co-session. <see cref="PythonDebugEngine"/> drives the Python
    /// side over debugpy; this drives the CUDA side over a loopback socket to
    /// <c>helix_cuda_bridge.exe</c>, which is where the emulator actually lives for a Python run.
    /// A breakpoint in <c>main.py</c> and a breakpoint in <c>saxpy.cu</c> are both live in the same
    /// session, and control passes between them at the kernel launch with nothing for the user to
    /// arrange.
    /// </para>
    ///
    /// <para>
    /// <b>Why the IDE listens and the bridge dials.</b> The bridge is not started by the IDE — the
    /// Python program starts it, whenever it first touches CUDA, which may be immediately or
    /// several seconds in or never. The IDE cannot connect to a process that does not exist yet, so
    /// it opens a port first, hands the number to Python in the environment, and waits. A run whose
    /// program never uses CUDA simply never dials, and the listener closes at the end of the
    /// session having cost nothing.
    /// </para>
    ///
    /// <para>
    /// <b>What is deliberately NOT here.</b> No attempt is made to suspend the Python side when a
    /// kernel stops. It is already suspended: it is blocked on the reply to the launch it sent. Any
    /// code here that also told debugpy to pause would be racing its own program for no benefit.
    /// </para>
    /// </summary>
    public sealed class CudaKernelDebugSession : IDebugEngine
    {
        /// <summary>
        /// Extensions whose breakpoints are offered to the kernel debugger. These are the file
        /// kinds a CUDA kernel is actually written in — the two the user's own programs load from
        /// (<c>.cu</c> and <c>.txt</c>) plus headers. A <c>.py</c> breakpoint belongs to debugpy and
        /// is never sent here, so the two engines can never both claim the same stop.
        /// </summary>
        private static readonly string[] KernelExtensions = { ".cu", ".cuh", ".txt" };

        private readonly BreakpointManager _breakpoints;
        private readonly object _mtx = new object();
        // Sending has its own lock so a write that blocks on a full socket buffer cannot hold up
        // the UI thread asking for the call stack.
        private readonly object _sendLock = new object();

        private TcpListener _listener;
        private TcpClient _client;
        private StreamWriter _writer;
        private StreamReader _reader;
        private Thread _acceptThread;

        private volatile bool _running;
        private volatile bool _paused;
        private volatile bool _bridgeConnected;
        private volatile bool _insideKernel;

        private List<DebugStackFrame> _frames = new List<DebugStackFrame>();
        private readonly Dictionary<int, List<DebugVariable>> _frameVariables =
            new Dictionary<int, List<DebugVariable>>();

        public event EventHandler<DebugStoppedEventArgs> Stopped;
        public event EventHandler<DebugOutputEventArgs> OutputReceived;
        public event EventHandler SessionEnded;

        /// <summary>Raised when the bridge dials in, i.e. the program has reached its first CUDA call.</summary>
        public event EventHandler BridgeAttached;

        /// <summary>Raised when a kernel launch finishes, so the UI can hand the session back to Python.</summary>
        public event EventHandler KernelExited;

        public CudaKernelDebugSession(BreakpointManager breakpoints)
        {
            _breakpoints = breakpoints;
        }

        /// <summary>The loopback port the bridge should dial, or 0 before <see cref="Listen"/>.</summary>
        public int Port { get; private set; }

        /// <summary>The lane whose execution is followed. Defaults to thread (0,0,0) of block (0,0,0).</summary>
        public Dim3 DebugThread { get; set; }

        /// <summary>The block containing the followed lane.</summary>
        public Dim3 DebugBlock { get; set; }

        /// <summary>
        /// Stop at the first statement of every kernel, whether or not a breakpoint is set. This is
        /// what makes an INLINE kernel — one written as a string in the <c>.py</c>, with no file to
        /// put a breakpoint in — debuggable at all: the first stop produces a file, and from then
        /// on ordinary breakpoints work in it.
        /// </summary>
        public bool StopOnKernelEntry { get; set; }

        /// <summary>True once the program has started the bridge and it has dialled in.</summary>
        public bool IsBridgeConnected { get { return _bridgeConnected; } }

        /// <summary>True while a kernel is running (paused or not) — i.e. while Python is blocked.</summary>
        public bool IsInsideKernel { get { return _insideKernel; } }

        public bool IsDebugging { get { return _running; } }
        public bool IsPaused { get { return _paused; } }

        // ---- lifecycle -------------------------------------------------------

        /// <summary>
        /// Open the loopback port and start waiting for the bridge. Call BEFORE launching Python,
        /// because <see cref="Port"/> has to go into the child's environment.
        /// </summary>
        public void Listen()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;

            if (_breakpoints != null) _breakpoints.BreakpointChanged += OnBreakpointChanged;

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "helix-cuda-debug-listen" };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            TcpListener listener = _listener;
            if (listener == null) return;
            try
            {
                _client = listener.AcceptTcpClient();
                _client.NoDelay = true;
                NetworkStream stream = _client.GetStream();
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Listener closed because the session ended before the program touched CUDA. That
                // is the common case for any program that does not use a GPU, and is not an error.
                _running = false;
                return;
            }

            // One connection per session: the program has exactly one bridge. Closing the listener
            // now frees the port and makes a second, confusing connection impossible.
            try { listener.Stop(); } catch { }
            _listener = null;

            ReadLoop();
        }

        private void ReadLoop()
        {
            try
            {
                string line;
                while ((line = _reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    JObject message;
                    try { message = JObject.Parse(line); }
                    catch (Exception) { continue; }
                    Handle(message);
                }
            }
            catch (Exception)
            {
                // Bridge gone — the program exited, or was stopped. Handled below.
            }

            _bridgeConnected = false;
            _insideKernel = false;
            _paused = false;
            _running = false;
            RaiseSessionEnded();
        }

        private void Handle(JObject message)
        {
            string kind = (string)message["event"] ?? string.Empty;
            switch (kind)
            {
                case KernelDebugProtocol.EventAttached:
                    _bridgeConnected = true;
                    SendConfiguration();
                    Send(new JObject { ["cmd"] = KernelDebugProtocol.CmdGo });
                    RaiseHandler(BridgeAttached);
                    break;

                case KernelDebugProtocol.EventKernelEntered:
                    _insideKernel = true;
                    break;

                case KernelDebugProtocol.EventStopped:
                    ApplyStop(message);
                    break;

                case KernelDebugProtocol.EventResumed:
                    _paused = false;
                    break;

                case KernelDebugProtocol.EventKernelExited:
                    _insideKernel = false;
                    _paused = false;
                    RaiseHandler(KernelExited);
                    break;

                case KernelDebugProtocol.EventOutput:
                    EventHandler<DebugOutputEventArgs> output = OutputReceived;
                    if (output != null)
                        output(this, new DebugOutputEventArgs
                        {
                            Text = (string)message["text"] ?? string.Empty,
                            Category = "stdout",
                        });
                    break;
            }
        }

        private void ApplyStop(JObject message)
        {
            var frames = new List<DebugStackFrame>();
            var variables = new Dictionary<int, List<DebugVariable>>();

            var array = message["frames"] as JArray;
            if (array != null)
                foreach (JToken token in array)
                {
                    var item = token as JObject;
                    if (item == null) continue;
                    int id = item["id"] != null ? (int)item["id"] : frames.Count;
                    frames.Add(new DebugStackFrame
                    {
                        Id = id,
                        Name = (string)item["name"] ?? "<kernel>",
                        FilePath = (string)item["path"] ?? string.Empty,
                        Line = item["line"] != null ? (int)item["line"] : 0,
                        Column = item["column"] != null ? (int)item["column"] : 0,
                    });

                    var locals = new List<DebugVariable>();
                    var vars = item["vars"] as JArray;
                    if (vars != null)
                        foreach (JToken varToken in vars)
                        {
                            var variable = varToken as JObject;
                            if (variable == null) continue;
                            locals.Add(new DebugVariable
                            {
                                Name = (string)variable["name"] ?? string.Empty,
                                Value = (string)variable["value"] ?? string.Empty,
                                Type = (string)variable["type"] ?? string.Empty,
                                VariablesReference = 0,
                            });
                        }
                    variables[id] = locals;
                }

            lock (_mtx)
            {
                _frames = frames;
                _frameVariables.Clear();
                foreach (KeyValuePair<int, List<DebugVariable>> pair in variables)
                    _frameVariables[pair.Key] = pair.Value;
            }

            _paused = true;
            EventHandler<DebugStoppedEventArgs> handler = Stopped;
            if (handler != null)
                handler(this, new DebugStoppedEventArgs
                {
                    Reason = (string)message["reason"] ?? "breakpoint",
                    FilePath = (string)message["path"] ?? string.Empty,
                    Line = message["line"] != null ? (int)message["line"] : 0,
                    ThreadId = 0,
                });
        }

        // ---- breakpoints -----------------------------------------------------

        private void OnBreakpointChanged(object sender, BreakpointChangedEventArgs e)
        {
            if (!_bridgeConnected) return;
            if (e != null && !IsKernelSource(e.FilePath)) return;   // a .py change is debugpy's business
            try { Send(BreakpointMessage(KernelDebugProtocol.CmdBreakpoints)); }
            catch (Exception) { }
        }

        private void SendConfiguration()
        {
            JObject configure = BreakpointMessage(KernelDebugProtocol.CmdConfigure);
            Dim3 thread = DebugThread;
            Dim3 block = DebugBlock;
            configure["thread"] = new JArray((long)thread.X, (long)thread.Y, (long)thread.Z);
            configure["block"] = new JArray((long)block.X, (long)block.Y, (long)block.Z);
            configure["stopOnKernelEntry"] = StopOnKernelEntry;
            Send(configure);
        }

        /// <summary>
        /// Every kernel-source file that has an enabled breakpoint, WITH ITS TEXT.
        ///
        /// <para>
        /// The text is the whole trick. The bridge sees kernels as anonymous strings — the program
        /// read them with <c>open()</c> long before the shim was involved — so there is no path to
        /// match on. Sending the text lets the bridge find the file inside the module it compiled,
        /// which works for a file loaded whole, a file concatenated onto a header, and the same
        /// file loaded twice.
        /// </para>
        /// </summary>
        private JObject BreakpointMessage(string command)
        {
            var files = new JArray();
            if (_breakpoints != null)
            {
                foreach (string path in _breakpoints.GetFilesWithBreakpoints())
                {
                    if (!IsKernelSource(path)) continue;
                    List<int> lines = _breakpoints.GetEnabledBreakpoints(path);
                    if (lines == null || lines.Count == 0) continue;
                    string text;
                    try { text = File.ReadAllText(path); }
                    catch (Exception) { continue; }   // file gone or locked: skip, don't fail the run

                    // Each line goes as an object carrying whatever the user attached to it, so a
                    // condition typed into the breakpoint dialog means the same thing in a .cu file
                    // as it does in a .py one. A kernel over a million threads stops in whichever
                    // lane arrives first without one, which is almost never the lane with the bug.
                    var numbers = new JArray();
                    foreach (int line in lines.OrderBy(n => n))
                    {
                        var entry = new JObject { ["line"] = line };
                        BreakpointCondition condition = _breakpoints.GetCondition(path, line);
                        if (condition != null)
                        {
                            if (!string.IsNullOrWhiteSpace(condition.Condition))
                                entry["condition"] = condition.Condition;
                            if (!string.IsNullOrWhiteSpace(condition.HitCondition))
                                entry["hit"] = condition.HitCondition;
                            if (!string.IsNullOrWhiteSpace(condition.LogMessage))
                                entry["log"] = condition.LogMessage;
                        }
                        numbers.Add(entry);
                    }
                    files.Add(new JObject
                    {
                        ["path"] = path,
                        ["text"] = text,
                        ["lines"] = numbers,
                    });
                }
            }
            return new JObject { ["cmd"] = command, ["files"] = files };
        }

        /// <summary>True for a file a CUDA kernel can be written in.</summary>
        public static bool IsKernelSource(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string extension = Path.GetExtension(path);
            for (int i = 0; i < KernelExtensions.Length; i++)
                if (string.Equals(extension, KernelExtensions[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>True when this session could stop somewhere — a kernel breakpoint exists, or
        /// break-on-entry is armed. The controller uses it to decide whether to bother listening.</summary>
        public bool HasAnythingToStopFor()
        {
            if (StopOnKernelEntry) return true;
            if (_breakpoints == null) return false;
            foreach (string path in _breakpoints.GetFilesWithBreakpoints())
            {
                if (!IsKernelSource(path)) continue;
                List<int> lines = _breakpoints.GetEnabledBreakpoints(path);
                if (lines != null && lines.Count > 0) return true;
            }
            return false;
        }

        // ---- control ---------------------------------------------------------

        public void Continue() { SendCommand(KernelDebugProtocol.CmdContinue); }
        public void StepInto() { SendCommand(KernelDebugProtocol.CmdStepInto); }
        public void StepOver() { SendCommand(KernelDebugProtocol.CmdStepOver); }
        public void StepOut() { SendCommand(KernelDebugProtocol.CmdStepOut); }

        /// <summary>
        /// No-op. A kernel lane is not interruptible mid-statement — the emulator only offers
        /// control at statement boundaries, and the only way to reach one is a breakpoint or a
        /// step. Break-on-entry (<see cref="StopOnKernelEntry"/>) is the supported way to catch a
        /// kernel that is about to run.
        /// </summary>
        public void Pause() { }

        private void SendCommand(string command)
        {
            if (!_paused) return;
            _paused = false;
            try { Send(new JObject { ["cmd"] = command }); } catch (Exception) { }
        }

        /// <summary>
        /// Detach. The kernel that is paused runs to completion undebugged and the Python program
        /// keeps its results — stopping the DEBUGGER must not corrupt the run. Killing the program
        /// outright is the Python engine's job, and the controller calls both.
        /// </summary>
        public void StopDebugging()
        {
            try { if (_bridgeConnected) Send(new JObject { ["cmd"] = KernelDebugProtocol.CmdStop }); }
            catch (Exception) { }
            _paused = false;
            CloseTransport();
        }

        // ---- queries ---------------------------------------------------------

        public List<DebugStackFrame> GetCallStack()
        {
            lock (_mtx) { return new List<DebugStackFrame>(_frames); }
        }

        public List<DebugScope> GetScopes(int frameId)
        {
            return new List<DebugScope>
            {
                new DebugScope { Name = "Locals", VariablesReference = frameId + 1, Expensive = false },
            };
        }

        public List<DebugVariable> GetVariables(int variablesReference)
        {
            lock (_mtx)
            {
                List<DebugVariable> variables;
                if (_frameVariables.TryGetValue(variablesReference - 1, out variables))
                    return new List<DebugVariable>(variables);
                return new List<DebugVariable>();
            }
        }

        /// <summary>
        /// Resolve a watch expression at the current kernel stop. Names only — a local, or a thread
        /// coordinate such as <c>threadIdx</c>. The emulator has no expression evaluator to borrow,
        /// and inventing one that silently disagreed with the kernel's own arithmetic would be
        /// worse than answering nothing.
        /// </summary>
        public DebugVariable Evaluate(string expression, int frameId = 0, string context = "watch")
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;
            string name = expression.Trim();
            foreach (DebugVariable variable in GetVariables(frameId + 1))
                if (string.Equals(variable.Name, name, StringComparison.Ordinal))
                    return new DebugVariable
                    {
                        Name = expression,
                        Value = variable.Value,
                        Type = variable.Type,
                        VariablesReference = 0,
                    };
            return null;
        }

        // ---- transport -------------------------------------------------------

        private void Send(JObject message)
        {
            StreamWriter writer = _writer;
            if (writer == null) return;
            lock (_sendLock) { writer.WriteLine(message.ToString(Newtonsoft.Json.Formatting.None)); }
        }

        private void RaiseHandler(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseSessionEnded()
        {
            EventHandler handler = SessionEnded;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void CloseTransport()
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            try { _listener?.Stop(); } catch { }
            _writer = null;
            _reader = null;
            _client = null;
            _listener = null;
            _bridgeConnected = false;
            _insideKernel = false;
            _running = false;
        }

        public void Dispose()
        {
            if (_breakpoints != null) _breakpoints.BreakpointChanged -= OnBreakpointChanged;
            StopDebugging();
            Thread accept = _acceptThread;
            if (accept != null && accept.IsAlive) accept.Join(1000);
            _acceptThread = null;
        }

        /// <summary>A human-readable coordinate, for the status bar: "block (0,0,0) thread (3,0,0)".</summary>
        public string LaneDescription()
        {
            return "block (" + Format(DebugBlock) + ") thread (" + Format(DebugThread) + ")";
        }

        private static string Format(Dim3 value)
        {
            return value.X.ToString(CultureInfo.InvariantCulture) + ","
                 + value.Y.ToString(CultureInfo.InvariantCulture) + ","
                 + value.Z.ToString(CultureInfo.InvariantCulture);
        }
    }
}
