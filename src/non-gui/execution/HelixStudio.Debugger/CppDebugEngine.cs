namespace HelixPythonNamespace.Debugger
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using HelixPythonNamespace.Workspace.Cpp;

    /// <summary>
    /// A plain (non-CUDA) C/C++ debug engine behind the shared <see cref="IDebugEngine"/> surface, so
    /// <c>.c</c>/<c>.cpp</c>/<c>.h</c>/<c>.hpp</c> files debug from the same tabbed editor + Debug panel as
    /// Python and CUDA. Drives real <b>gdb</b> (gdb-MI), falling back to <b>lldb</b>, via
    /// <see cref="CppDebugRunner"/>. When neither tool (nor a usable GNU compiler) is available it raises the
    /// tool-absence reason through <see cref="OutputReceived"/> and ends the session — it never fabricates stops.
    /// </summary>
    public sealed class CppDebugEngine : IDebugEngine
    {
        private readonly List<CppDebugStop> _stops = new List<CppDebugStop>();
        private int _index = -1;
        private string _sourceFile;
        private volatile bool _debugging;

        public bool IsDebugging { get { return _debugging; } }
        public bool IsPaused { get { return _debugging && _index >= 0 && _index < _stops.Count; } }

        public event EventHandler<DebugStoppedEventArgs> Stopped;
        public event EventHandler<DebugOutputEventArgs> OutputReceived;
        public event EventHandler SessionEnded;

        public static bool IsAvailable { get { return CppDebugRunner.IsAvailable; } }

        /// <summary>Build (or locate) the binary for <paramref name="sourceFile"/> and start a gdb/lldb session.</summary>
        public void Start(string projectDir, string sourceFile, int breakLine, int steps = 2)
        {
            _sourceFile = sourceFile;
            _debugging = true;
            _stops.Clear();
            _index = -1;
            var t = new Thread(() =>
            {
                CppDebugResult r;
                try { r = CppDebugRunner.Debug(projectDir, sourceFile, breakLine, steps < 1 ? 1 : steps); }
                catch (Exception ex)
                {
                    OutputReceived?.Invoke(this, new DebugOutputEventArgs { Text = "gdb/lldb error: " + ex.Message, Category = "stderr" });
                    _debugging = false; SessionEnded?.Invoke(this, EventArgs.Empty); return;
                }
                if (!r.Available) { OutputReceived?.Invoke(this, new DebugOutputEventArgs { Text = r.Reason ?? "gdb/lldb unavailable", Category = "stderr" }); _debugging = false; SessionEnded?.Invoke(this, EventArgs.Empty); return; }
                foreach (var s in r.Stops) _stops.Add(s);
                if (!string.IsNullOrEmpty(r.RawOutput)) OutputReceived?.Invoke(this, new DebugOutputEventArgs { Text = r.RawOutput, Category = "stdout" });
                if (_stops.Count == 0) { _debugging = false; SessionEnded?.Invoke(this, EventArgs.Empty); return; }
                _index = 0;
                RaiseStopped();
            });
            t.IsBackground = true;
            t.Start();
        }

        private void RaiseStopped()
        {
            var s = _stops[_index];
            Stopped?.Invoke(this, new DebugStoppedEventArgs { Reason = s.Kind, ThreadId = 1, FilePath = _sourceFile, Line = s.Line });
        }

        public void Continue() { if (!_debugging) return; if (_index + 1 < _stops.Count) { _index++; RaiseStopped(); } else EndSession(); }
        public void StepOver() { Continue(); }
        public void StepInto() { Continue(); }
        public void StepOut() { Continue(); }
        public void Pause() { }
        public void StopDebugging() { EndSession(); }
        private void EndSession() { _debugging = false; _index = -1; SessionEnded?.Invoke(this, EventArgs.Empty); }

        public List<DebugStackFrame> GetCallStack()
        {
            var frames = new List<DebugStackFrame>();
            if (_index < 0 || _index >= _stops.Count) return frames;
            var s = _stops[_index];
            frames.Add(new DebugStackFrame { Id = 0, Name = s.Method, FilePath = _sourceFile, Line = s.Line, Column = 1 });
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
            var vars = new List<DebugVariable>();
            if (_index < 0 || _index >= _stops.Count || variablesReference != 1) return vars;
            foreach (var kv in _stops[_index].Locals)
                vars.Add(new DebugVariable { Name = kv.Key, Value = kv.Value, Type = "", VariablesReference = 0 });
            return vars;
        }

        public void Dispose() { _debugging = false; }
    }
}
