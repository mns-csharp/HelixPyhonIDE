namespace HelixPythonNamespace.Debugger
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using HelixPythonNamespace.CudaEmulator;
    using HelixPythonNamespace.Workspace.Cuda;

    /// <summary>
    /// Hard rule 9's other half behind the shared <see cref="IDebugEngine"/> surface: a CUDA kernel
    /// debugged on the <b>real GPU</b> by NVIDIA's <c>cuda-gdb</c>, driving the same editor markers and
    /// Debug panels as <see cref="CudaEmulatorDebugEngine"/> does on the emulator.
    ///
    /// <para>
    /// <b>Never run against a real cuda-gdb.</b> See <see cref="CudaGdbRunner"/> for the full statement;
    /// the short version is that cuda-gdb does not exist on Windows and this machine has no CUDA GPU. The
    /// transcript parsing is unit-tested; the live session is not. When cuda-gdb is absent this engine
    /// says so through <see cref="OutputReceived"/> and ends the session, exactly as
    /// <see cref="CppDebugEngine"/> does for a missing gdb — it never invents a stop.
    /// </para>
    ///
    /// <para>
    /// Like the C/C++ engine it is a <b>replay</b> engine: cuda-gdb is driven to completion on a worker
    /// thread and the captured stops are then walked with Continue/Step. That is a real limitation next
    /// to the emulator's live session — you cannot change your mind about where to go — and it is the
    /// honest shape for a tool driven in batch. It is also why the <c>steps</c> count matters: it decides
    /// how far the recorded session goes.
    /// </para>
    ///
    /// <para>
    /// The one thing deliberately matched to the emulator engine is what the Variables panel shows.
    /// A stop in device code contributes <c>blockIdx</c>, <c>threadIdx</c> and the hardware coordinate
    /// (device/sm/warp/lane) alongside the kernel's locals, so switching a solution between the emulator
    /// and a real GPU does not change what a user is looking at — only where it came from.
    /// </para>
    /// </summary>
    public sealed class CudaGdbDebugEngine : IDebugEngine
    {
        private readonly List<CudaGdbStop> _stops = new List<CudaGdbStop>();
        private int _index = -1;
        private string _sourceFile;
        private volatile bool _debugging;

        public event EventHandler<DebugStoppedEventArgs> Stopped;
        public event EventHandler<DebugOutputEventArgs> OutputReceived;
        public event EventHandler SessionEnded;

        public bool IsDebugging { get { return _debugging; } }
        public bool IsPaused { get { return _debugging && _index >= 0 && _index < _stops.Count; } }

        /// <summary>True when a cuda-gdb was located. False is the normal answer on Windows.</summary>
        public static bool IsAvailable { get { return CudaGdbRunner.IsAvailable; } }

        /// <summary>Why it is unavailable, in words a user can act on, or null when it is available.</summary>
        public static string UnavailableReason() { return CudaGdbRunner.UnavailableReason(); }

        /// <summary>The lane to switch focus to once the breakpoint is hit. Defaults to (0,0,0)/(0,0,0).</summary>
        public Dim3 DebugBlock { get; set; }

        /// <summary>The thread within <see cref="DebugBlock"/>.</summary>
        public Dim3 DebugThread { get; set; }

        /// <summary>
        /// Build <paramref name="sourceFile"/> with <c>nvcc -g -G</c> and drive cuda-gdb to
        /// <paramref name="breakLine"/>, then walk the recorded stops.
        /// </summary>
        public void Start(string sourceFile, int breakLine, int steps = 2)
        {
            _sourceFile = sourceFile;
            _debugging = true;
            _stops.Clear();
            _index = -1;

            int[] block = { (int)DebugBlock.X, (int)DebugBlock.Y, (int)DebugBlock.Z };
            int[] thread = { (int)DebugThread.X, (int)DebugThread.Y, (int)DebugThread.Z };

            var worker = new Thread(delegate ()
            {
                CudaGdbResult result;
                try
                {
                    result = CudaGdbRunner.Debug(sourceFile, breakLine, block, thread, steps < 1 ? 1 : steps);
                }
                catch (Exception ex)
                {
                    Emit("cuda-gdb error: " + ex.Message, "stderr");
                    End();
                    return;
                }

                if (!result.Available)
                {
                    Emit(result.Reason ?? "cuda-gdb unavailable", "stderr");
                    End();
                    return;
                }
                if (!string.IsNullOrEmpty(result.RawOutput)) Emit(result.RawOutput, "stdout");
                foreach (CudaGdbStop stop in result.Stops) _stops.Add(stop);
                if (_stops.Count == 0)
                {
                    // cuda-gdb ran and stopped nowhere. Almost always the -G flag or a breakpoint on a
                    // line with no code; say so rather than ending silently, because a session that just
                    // vanishes is indistinguishable from a broken debugger.
                    Emit("cuda-gdb produced no stops. The breakpoint may be on a line with no generated "
                         + "code, or the kernel may never have been launched.", "stderr");
                    End();
                    return;
                }
                _index = 0;
                RaiseStopped();
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void Emit(string text, string category)
        {
            EventHandler<DebugOutputEventArgs> handler = OutputReceived;
            if (handler != null) handler(this, new DebugOutputEventArgs { Text = text, Category = category });
        }

        private void End()
        {
            _debugging = false;
            _index = -1;
            EventHandler handler = SessionEnded;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseStopped()
        {
            CudaGdbStop stop = _stops[_index];
            EventHandler<DebugStoppedEventArgs> handler = Stopped;
            if (handler != null)
                handler(this, new DebugStoppedEventArgs
                {
                    Reason = stop.Kind,
                    ThreadId = 1,
                    FilePath = _sourceFile,
                    Line = stop.Line,
                });
        }

        public void Continue()
        {
            if (!_debugging) return;
            if (_index + 1 < _stops.Count) { _index++; RaiseStopped(); }
            else End();
        }

        // A replayed session has one motion. Mapping all three to it is honest about that: the recorded
        // stops are what cuda-gdb was asked for, and pretending Step Out could unwind to a caller that
        // was never captured would put the user at a line the session does not have.
        public void StepOver() { Continue(); }
        public void StepInto() { Continue(); }
        public void StepOut() { Continue(); }
        public void Pause() { }
        public void StopDebugging() { End(); }

        public List<DebugStackFrame> GetCallStack()
        {
            var frames = new List<DebugStackFrame>();
            if (_index < 0 || _index >= _stops.Count) return frames;
            CudaGdbStop stop = _stops[_index];
            frames.Add(new DebugStackFrame
            {
                Id = 0,
                Name = string.IsNullOrEmpty(stop.Method) ? "<kernel>" : stop.Method,
                FilePath = _sourceFile,
                Line = stop.Line,
                Column = 1,
            });
            return frames;
        }

        public List<DebugScope> GetScopes(int frameId)
        {
            var scopes = new List<DebugScope>();
            if (_index < 0 || _index >= _stops.Count) return scopes;
            scopes.Add(new DebugScope { Name = "Locals", VariablesReference = 1, Expensive = false });
            return scopes;
        }

        public List<DebugVariable> GetVariables(int variablesReference)
        {
            var variables = new List<DebugVariable>();
            if (_index < 0 || _index >= _stops.Count || variablesReference != 1) return variables;

            CudaGdbStop stop = _stops[_index];
            foreach (KeyValuePair<string, string> local in stop.Locals)
                variables.Add(new DebugVariable
                {
                    Name = local.Key,
                    Value = local.Value,
                    Type = string.Empty,
                    VariablesReference = 0,
                });

            // Same names the emulator engine uses, so the panel reads identically either way.
            if (stop.Focus.IsDeviceFocus)
            {
                Add(variables, "threadIdx", Coordinate(stop.Focus.ThreadX, stop.Focus.ThreadY, stop.Focus.ThreadZ), "uint3");
                Add(variables, "blockIdx", Coordinate(stop.Focus.BlockX, stop.Focus.BlockY, stop.Focus.BlockZ), "uint3");
                if (stop.Focus.Device >= 0)
                    Add(variables, "(hardware)",
                        "device " + stop.Focus.Device + ", sm " + stop.Focus.Sm
                        + ", warp " + stop.Focus.Warp + ", lane " + stop.Focus.Lane, "cuda");
            }
            return variables;
        }

        /// <summary>The lane the current stop is in, for the status bar, or null in host code.</summary>
        public string LaneDescription()
        {
            if (_index < 0 || _index >= _stops.Count) return null;
            CudaGdbStop stop = _stops[_index];
            return stop.Focus.IsDeviceFocus ? stop.Focus.ToString() : null;
        }

        /// <summary>
        /// Resolve a watch at the current stop. Names only, matching the emulator engine's limit rather
        /// than cuda-gdb's real expression evaluator — this is a replayed session, so there is nothing
        /// live left to ask.
        /// </summary>
        public DebugVariable Evaluate(string expression, int frameId = 0, string context = "watch")
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;
            string name = expression.Trim();
            foreach (DebugVariable variable in GetVariables(1))
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

        private static void Add(List<DebugVariable> target, string name, string value, string type)
        {
            target.Add(new DebugVariable { Name = name, Value = value, Type = type, VariablesReference = 0 });
        }

        private static string Coordinate(int x, int y, int z)
        {
            return "(" + x.ToString(CultureInfo.InvariantCulture) + ","
                 + y.ToString(CultureInfo.InvariantCulture) + ","
                 + z.ToString(CultureInfo.InvariantCulture) + ")";
        }

        public void Dispose() { _debugging = false; }
    }
}
