namespace HelixPythonNamespace.Testing
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text;
    using System.Threading;

    /// <summary>
    /// Runs pytest or unittest as a child process and parses output into a flat list of
    /// <see cref="TestNode"/> results, coalescing node updates into ~10 Hz batches.
    /// Port of the C++ <c>TestRunner</c> (QProcess → <see cref="Process"/>, QTimer →
    /// <see cref="Timer"/>, Qt signals → .NET events). Channels are merged so unittest's
    /// stderr-bound verbose output is parsed alongside pytest's stdout.
    /// </summary>
    public sealed class TestRunner
    {
        private readonly object _gate = new object();
        private volatile bool _running;
        private Process _process;
        private Timer _flushTimer;
        private TestOutputParser _parser;
        private readonly List<TestNode> _pendingBatch = new List<TestNode>();

        public event Action Started;
        public event Action<string> StartFailed;
        public event Action<int> Finished;
        public event Action Stopped;
        public event Action<string> OutputLine;
        public event Action<List<TestNode>> TestsBatch;
        public event Action<int, int, int> Progress;

        public bool IsRunning { get { return _running; } }

        // Only safe to read when !IsRunning.
        public IReadOnlyList<TestNode> Results
        {
            get { return _parser != null ? (IReadOnlyList<TestNode>)_parser.Results : new List<TestNode>(); }
        }

        public List<string> FailedIds()
        {
            return _parser != null ? _parser.FailedIds() : new List<string>();
        }

        public void Start(TestRunRequest request)
        {
            if (_running) return;
            if (string.IsNullOrEmpty(request.InterpreterPath))
            {
                Raise(StartFailed, "Interpreter path is empty.");
                return;
            }

            _parser = new TestOutputParser(request.Framework, request.WorkingDirectory);
            _parser.NodeBatched += node =>
            {
                lock (_gate) { _pendingBatch.Add(node); }
            };
            _parser.ProgressReported += (pct, completed, totalEst) =>
            {
                Action<int, int, int> h = Progress;
                if (h != null) h(pct, completed, totalEst);
            };

            List<string> args = new List<string> { "-m", request.Framework == TestFramework.Pytest ? "pytest" : "unittest" };
            if (request.Framework == TestFramework.Pytest) { args.Add("-v"); args.Add("--no-header"); args.Add("-rN"); }
            else args.Add("-v");
            if (request.Targets != null) args.AddRange(request.Targets);
            if (request.ExtraArgs != null) args.AddRange(request.ExtraArgs);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = request.InterpreterPath,
                    Arguments = BuildArguments(args),
                    WorkingDirectory = request.WorkingDirectory ?? "",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
                _process.ErrorDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
                _process.Exited += (s, e) => OnFinished();

                lock (_gate) { _pendingBatch.Clear(); }
                _flushTimer = new Timer(_ => FlushBatch(), null, 100, 100);

                _running = true;
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                Raise(Started);
            }
            catch (Exception ex)
            {
                _running = false;
                StopFlushTimer();
                if (_process != null) { _process.Dispose(); _process = null; }
                Raise(StartFailed, ex.Message);
            }
        }

        public void Stop()
        {
            StopFlushTimer();
            FlushBatch();   // last partial batch reaches subscribers before shutdown

            Process p;
            lock (_gate)
            {
                p = _process;
                _process = null;
                _running = false;
            }

            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        KillProcessTree(p);
                        p.WaitForExit(2000);
                    }
                }
                catch { }
                p.Dispose();
            }
            Raise(Stopped);
        }

        /// <summary>
        /// Kill the process and all of its descendants. .NET Framework 4.7.2 has no
        /// <c>Process.Kill(entireProcessTree)</c>, so a plain <c>Kill()</c> only terminates
        /// the launched process (e.g. <c>python -m pytest</c>) and leaves its children — the
        /// py/python launcher chain, pytest-xdist workers, subprocess-based tests — running.
        /// Those orphans are why "Cancel" appeared not to stop a run. <c>taskkill /T</c>
        /// terminates the whole tree; fall back to a direct Kill if it is unavailable.
        /// </summary>
        internal static void KillProcessTree(Process p)
        {
            if (p == null) return;
            int pid;
            try { pid = p.Id; } catch { return; }

            bool treeKilled = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/T /F /PID " + pid)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (Process tk = Process.Start(psi))
                {
                    if (tk != null)
                    {
                        tk.WaitForExit(3000);
                        treeKilled = tk.HasExited && tk.ExitCode == 0;
                    }
                }
            }
            catch { /* taskkill missing or blocked — fall back below */ }

            if (!treeKilled)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
            }
        }

        private void OnLine(string line)
        {
            if (!string.IsNullOrEmpty(line) && line[line.Length - 1] == '\r')
                line = line.Substring(0, line.Length - 1);
            Action<string> oh = OutputLine;
            if (oh != null) oh(line);
            _parser.HandleLine(line);
        }

        private void OnFinished()
        {
            int exitCode = 0;
            lock (_gate)
            {
                if (_process != null)
                {
                    try { exitCode = _process.ExitCode; } catch { }
                }
            }
            StopFlushTimer();
            FlushBatch();   // final partial batch
            _running = false;
            lock (_gate)
            {
                if (_process != null) { _process.Dispose(); _process = null; }
            }
            Action<int> h = Finished;
            if (h != null) h(exitCode);
        }

        private void FlushBatch()
        {
            List<TestNode> batch;
            lock (_gate)
            {
                if (_pendingBatch.Count == 0) return;
                batch = new List<TestNode>(_pendingBatch);
                _pendingBatch.Clear();
            }
            Action<List<TestNode>> h = TestsBatch;
            if (h != null) h(batch);
        }

        private void StopFlushTimer()
        {
            Timer t = _flushTimer;
            _flushTimer = null;
            if (t != null) t.Dispose();
        }

        /// <summary>Join args into a single command-line string, quoting any that contain whitespace.</summary>
        internal static string BuildArguments(IList<string> args)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(QuoteArg(args[i]));
            }
            return sb.ToString();
        }

        private static string QuoteArg(string a)
        {
            if (string.IsNullOrEmpty(a)) return "\"\"";
            bool needsQuote = a.IndexOf(' ') >= 0 || a.IndexOf('\t') >= 0 || a.IndexOf('"') >= 0;
            if (!needsQuote) return a;
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        }

        private static void Raise(Action h)
        {
            if (h != null) h();
        }

        private static void Raise(Action<string> h, string arg)
        {
            if (h != null) h(arg);
        }
    }
}
