namespace HelixPythonNamespace.Debugger
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using HelixPythonNamespace.CudaEmulator;
    using HelixPythonNamespace.CudaVirtualCompiler;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;

    /// <summary>
    /// Single-thread CUDA debugger. Runs a .cu program on the CPU emulator on a
    /// worker thread for one chosen (block,thread) coordinate and uses the
    /// interpreter's IDebugHook to pause at breakpoints / steps. Mirrors the
    /// PythonDebugEngine query/control surface (same Stopped / OutputReceived /
    /// SessionEnded events and DebugStackFrame / DebugScope / DebugVariable models)
    /// so it drives the existing debug panels and editor markers. Port of the C++
    /// helix::debugger::CudaEmulatorDebugEngine
    /// (debugger/cuda_emulator_debug_engine.{h,cpp}); Qt QObject/signals →
    /// .NET events, std::thread + condition_variable → Thread + Monitor.
    /// </summary>
    public sealed class CudaEmulatorDebugEngine : IDebugHook, IDisposable, IDebugEngine
    {
        private enum Mode { Run, StepInto, StepOver, StepOut }

        // Unwinds the interpreter when the user stops the session.
        private sealed class DebugAbort : Exception { }

        private sealed class EngineSink : IOutputSink
        {
            private readonly CudaEmulatorDebugEngine _engine;
            public EngineSink(CudaEmulatorDebugEngine engine) { _engine = engine; }
            public void Write(string text) { _engine.EmitOutput(text); }
        }

        private readonly BreakpointManager _breakpoints;
        private string _filePath;
        private string _source;
        private Dim3 _debugBlock;
        private Dim3 _debugThread;

        private Thread _worker;
        private CudaCompileResult _compiled;

        private readonly object _mtx = new object();
        private Mode _mode = Mode.StepInto;   // stop on the first statement
        private int _baselineDepth;
        private int _lastStopDepth;
        private int _prevExecLine = -1;        // line of the previously executed statement
        private bool _resumeRequested;

        private int _debuggingFlag;            // 0/1 via Interlocked
        private volatile bool _paused;
        private volatile bool _stopRequested;

        private readonly object _snapMutex = new object();
        private List<DebugStackFrame> _callStack = new List<DebugStackFrame>();
        private readonly Dictionary<int, List<DebugVariable>> _frameVars = new Dictionary<int, List<DebugVariable>>();

        public event EventHandler<DebugStoppedEventArgs> Stopped;
        public event EventHandler<DebugOutputEventArgs> OutputReceived;
        public event EventHandler SessionEnded;

        public CudaEmulatorDebugEngine(BreakpointManager breakpoints)
        {
            _breakpoints = breakpoints;
        }

        public bool IsDebugging { get { return _debuggingFlag != 0; } }
        public bool IsPaused { get { return _paused; } }

        /// <summary>Start debugging <paramref name="source"/> (file <paramref name="filePath"/>) for the chosen coordinate.</summary>
        public void Start(string filePath, string source, Dim3 debugBlock, Dim3 debugThread)
        {
            if (Interlocked.Exchange(ref _debuggingFlag, 1) == 1) return;
            if (_worker != null && _worker.IsAlive) _worker.Join();
            _filePath = filePath ?? string.Empty;
            _source = source ?? string.Empty;
            _debugBlock = debugBlock;
            _debugThread = debugThread;
            lock (_mtx)
            {
                _mode = Mode.StepInto;
                _stopRequested = false;
                _resumeRequested = false;
                _prevExecLine = -1;
            }

            _worker = new Thread(WorkerBody);
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void WorkerBody()
        {
            EngineSink sink = new EngineSink(this);
            try
            {
                _compiled = CudaVirtualCompiler.CompileSource(_source, _filePath);
                CudaEmulator emu = new CudaEmulator(_compiled, sink);
                emu.RunProgramDebug("main", this, _debugBlock, _debugThread);
            }
            catch (DebugAbort)
            {
                // stopped by user
            }
            catch (Exception)
            {
                // swallow — surfaced via output / SessionEnded
            }
            _debuggingFlag = 0;
            _paused = false;
            RaiseSessionEnded();
        }

        // ---- control (called from the GUI thread) ----

        public void Continue() { Resume(Mode.Run); }
        public void StepInto() { Resume(Mode.StepInto); }
        public void StepOver() { Resume(Mode.StepOver); }
        public void StepOut() { Resume(Mode.StepOut); }
        public void Pause() { /* single-thread runs fast; pause-on-demand is a no-op */ }

        private void Resume(Mode mode)
        {
            lock (_mtx)
            {
                _mode = mode;
                if (mode == Mode.StepOver || mode == Mode.StepOut) _baselineDepth = _lastStopDepth;
                _resumeRequested = true;
                Monitor.PulseAll(_mtx);
            }
        }

        public void StopDebugging()
        {
            lock (_mtx)
            {
                _stopRequested = true;
                _resumeRequested = true;
                Monitor.PulseAll(_mtx);
            }
        }

        // ---- IDebugHook (worker thread) ----

        public void OnStatement(Stmt stmt, int callDepth, Interpreter interp)
        {
            if (_stopRequested) throw new DebugAbort();
            if (stmt == null) return;

            int line = stmt.Span.Start.Line;
            // A disabled breakpoint stays in the list but must not stop the emulator.
            bool hit = _breakpoints != null && _breakpoints.HasBreakpoint(_filePath, line)
                       && _breakpoints.IsEnabled(_filePath, line);
            // Line-granular: don't re-stop for a second statement on the same source line.
            bool sameLineAsPrev = (line == _prevExecLine);
            _prevExecLine = line;

            bool shouldStop;
            lock (_mtx)
            {
                switch (_mode)
                {
                    case Mode.Run: shouldStop = hit; break;
                    case Mode.StepInto: shouldStop = true; break;
                    case Mode.StepOver: shouldStop = (callDepth <= _baselineDepth) || hit; break;
                    case Mode.StepOut: shouldStop = (callDepth < _baselineDepth) || hit; break;
                    default: shouldStop = hit; break;
                }
            }
            if (sameLineAsPrev) shouldStop = false;
            if (!shouldStop) return;

            BuildSnapshot(interp);
            lock (_mtx) { _lastStopDepth = callDepth; }

            DebugStoppedEventArgs info = new DebugStoppedEventArgs();
            info.Reason = hit ? "breakpoint" : "step";
            info.FilePath = _filePath;
            info.Line = line;
            info.ThreadId = 0;
            _paused = true;
            RaiseStopped(info);

            lock (_mtx)
            {
                while (!(_resumeRequested || _stopRequested)) Monitor.Wait(_mtx);
                _resumeRequested = false;
            }
            _paused = false;
            if (_stopRequested) throw new DebugAbort();
        }

        public void EmitOutput(string text)
        {
            DebugOutputEventArgs o = new DebugOutputEventArgs();
            o.Text = text;
            o.Category = "stdout";
            EventHandler<DebugOutputEventArgs> handler = OutputReceived;
            if (handler != null) handler(this, o);
        }

        // ---- queries (call when paused; read a snapshot taken at the pause point) ----

        public List<DebugStackFrame> GetCallStack()
        {
            lock (_snapMutex)
            {
                return new List<DebugStackFrame>(_callStack);
            }
        }

        public List<DebugScope> GetScopes(int frameId)
        {
            DebugScope s = new DebugScope();
            s.Name = "Locals";
            s.VariablesReference = frameId + 1;
            s.Expensive = false;
            List<DebugScope> outList = new List<DebugScope>();
            outList.Add(s);
            return outList;
        }

        public List<DebugVariable> GetVariables(int variablesReference)
        {
            lock (_snapMutex)
            {
                List<DebugVariable> vars;
                if (_frameVars.TryGetValue(variablesReference - 1, out vars))
                    return new List<DebugVariable>(vars);
                return new List<DebugVariable>();
            }
        }

        /// <summary>
        /// Resolve a watch expression at the current stop. The SIMT emulator has no full expression
        /// evaluator, so this supports a bare local / thread-coordinate NAME (e.g. <c>v</c>, <c>r</c>,
        /// <c>threadIdx</c>) looked up in the given frame's variables. Unknown names return null
        /// (the Variables panel renders that as &lt;error&gt;). The <paramref name="context"/> arg
        /// mirrors the Python engine's signature and is unused here.
        /// </summary>
        public DebugVariable Evaluate(string expression, int frameId = 0, string context = "watch")
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;
            string name = expression.Trim();
            foreach (DebugVariable v in GetVariables(frameId + 1))
            {
                if (string.Equals(v.Name, name, StringComparison.Ordinal))
                    return new DebugVariable
                    {
                        Name = expression,
                        Value = v.Value,
                        Type = v.Type,
                        VariablesReference = v.VariablesReference,
                    };
            }
            return null;
        }

        private void BuildSnapshot(Interpreter interp)
        {
            lock (_snapMutex)
            {
                _callStack = new List<DebugStackFrame>();
                _frameVars.Clear();
                List<Frame> frames = interp.Frames;
                int n = frames.Count;
                for (int i = 0; i < n; ++i)
                {
                    Frame f = frames[n - 1 - i];  // top frame first
                    DebugStackFrame sf = new DebugStackFrame();
                    sf.Id = i;
                    sf.Name = f.Func != null ? f.Func.Name : "<host>";
                    sf.FilePath = _filePath;
                    sf.Line = f.CurrentStmt != null ? f.CurrentStmt.Span.Start.Line : 0;
                    sf.Column = f.CurrentStmt != null ? f.CurrentStmt.Span.Start.Column : 0;
                    _callStack.Add(sf);

                    List<DebugVariable> vars = new List<DebugVariable>();
                    for (int si = 0; si < f.Scopes.Count; ++si)
                    {
                        foreach (KeyValuePair<string, Cell> kv in f.Scopes[si].Locals)
                        {
                            DebugVariable v = new DebugVariable();
                            v.Name = kv.Key;
                            v.Value = kv.Value.Value != null ? kv.Value.Value.ToString() : string.Empty;
                            v.Type = kv.Value.DeclType != null ? kv.Value.DeclType.ToString() : string.Empty;
                            v.VariablesReference = 0;
                            vars.Add(v);
                        }
                    }
                    // Surface the CUDA thread coordinates on the kernel frame.
                    if (f.Thread != null)
                    {
                        AddCoord("threadIdx", f.Thread.ThreadIdx, vars);
                        AddCoord("blockIdx", f.Thread.BlockIdx, vars);
                        AddCoord("blockDim", f.Thread.BlockDim, vars);
                        AddCoord("gridDim", f.Thread.GridDim, vars);
                    }
                    _frameVars[i] = vars;
                }
            }
        }

        private static void AddCoord(string name, Dim3 d, List<DebugVariable> outList)
        {
            DebugVariable v = new DebugVariable();
            v.Name = name;
            v.Value = "(" + d.X.ToString(CultureInfo.InvariantCulture) + "," +
                      d.Y.ToString(CultureInfo.InvariantCulture) + "," +
                      d.Z.ToString(CultureInfo.InvariantCulture) + ")";
            v.Type = "uint3";
            v.VariablesReference = 0;
            outList.Add(v);
        }

        private void RaiseStopped(DebugStoppedEventArgs info)
        {
            EventHandler<DebugStoppedEventArgs> handler = Stopped;
            if (handler != null) handler(this, info);
        }

        private void RaiseSessionEnded()
        {
            EventHandler handler = SessionEnded;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            StopDebugging();
            Thread w = _worker;
            if (w != null && w.IsAlive)
            {
                w.Join(2000);
            }
        }
    }
}
