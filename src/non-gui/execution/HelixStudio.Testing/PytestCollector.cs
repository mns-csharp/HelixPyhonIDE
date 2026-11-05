namespace HelixPythonNamespace.Testing
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text;

    /// <summary>
    /// Runs <c>&lt;python&gt; -m pytest --collect-only -q &lt;targets&gt;</c> as a child process and
    /// reports every collected test id via <see cref="Collected"/>. Used by the "Run new tests
    /// only" flow to diff the current collection against the previous run's id set. Non-blocking.
    /// Port of the C++ <c>PytestCollector</c> (QProcess → <see cref="Process"/>, Qt signals → .NET events).
    /// </summary>
    public sealed class PytestCollector
    {
        private readonly object _gate = new object();
        private Process _process;
        private readonly StringBuilder _buffer = new StringBuilder();

        public event Action<List<string>> Collected;
        public event Action<string> Failed;

        public bool IsRunning
        {
            get { lock (_gate) { return _process != null && !_process.HasExited; } }
        }

        public bool Collect(string interpreter, string workingDir, IList<string> targets)
        {
            if (IsRunning) return false;
            if (string.IsNullOrEmpty(interpreter)) return false;

            lock (_gate) { _buffer.Clear(); }

            List<string> args = new List<string> { "-m", "pytest", "--collect-only", "-q" };
            if (targets != null) args.AddRange(targets);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = interpreter,
                    Arguments = TestRunner.BuildArguments(args),
                    WorkingDirectory = workingDir ?? "",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                // Merged channels: route both streams into the same buffer.
                _process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (_gate) { _buffer.Append(e.Data).Append('\n'); } };
                _process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (_gate) { _buffer.Append(e.Data).Append('\n'); } };
                _process.Exited += (s, e) => OnFinished();
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                return true;
            }
            catch (Exception ex)
            {
                lock (_gate) { if (_process != null) { _process.Dispose(); _process = null; } }
                Action<string> h = Failed;
                if (h != null) h(ex.Message);
                return false;
            }
        }

        private void OnFinished()
        {
            string output;
            lock (_gate)
            {
                if (_process == null) return;
                output = _buffer.ToString();
                _process.Dispose();
                _process = null;
            }
            Action<List<string>> h = Collected;
            if (h != null) h(ParseCollectedIds(output));
        }

        /// <summary>
        /// Extracts test ids from <c>pytest --collect-only -q</c> output: one id per line,
        /// trimmed, skipping blanks and summary/warning lines (kept only if they contain "::").
        /// </summary>
        public static List<string> ParseCollectedIds(string output)
        {
            List<string> ids = new List<string>();
            if (string.IsNullOrEmpty(output)) return ids;
            string[] lines = output.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.IndexOf("::", StringComparison.Ordinal) < 0) continue;
                ids.Add(line);
            }
            return ids;
        }
    }
}
