using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Notebooks.Kernel
{
    /// <summary>What the kernel just told us. One event type, because the caller switches anyway.</summary>
    public sealed class KernelMessageEventArgs : EventArgs
    {
        public KernelMessageEventArgs(string kind, string cellId, NotebookOutput output,
            bool ok, int? executionCount, string detail)
        {
            Kind = kind;
            CellId = cellId;
            Output = output;
            Ok = ok;
            ExecutionCount = executionCount;
            Detail = detail;
        }

        /// <summary>"ready", "output", "status", "pong", "exit", "failed".</summary>
        public string Kind { get; private set; }

        /// <summary>The execution this belongs to, or null for lifecycle messages.</summary>
        public string CellId { get; private set; }

        /// <summary>The output to append, for Kind == "output".</summary>
        public NotebookOutput Output { get; private set; }

        /// <summary>For Kind == "status": did the cell finish without raising?</summary>
        public bool Ok { get; private set; }

        public int? ExecutionCount { get; private set; }

        /// <summary>Human-readable extra: the interpreter banner on ready, the reason on failed.</summary>
        public string Detail { get; private set; }
    }

    /// <summary>
    /// The IDE's half of the conversation with <c>helix_kernel_host.py</c>.
    ///
    /// <para>
    /// One process per notebook, started on the first Run and living until the user restarts it or
    /// closes the tab — which is the whole point of a notebook: cell 7 sees what cell 3 defined.
    /// Messages arrive on a reader thread and are raised as <see cref="Message"/>; the caller
    /// marshals to its UI thread, because this class knows nothing about one.
    /// </para>
    ///
    /// <para>
    /// <b>Interrupt does not kill the process.</b> It writes an interrupt request that the host's
    /// stdin thread turns into a KeyboardInterrupt in the thread running the cell, so a runaway loop
    /// stops with the namespace intact. Killing is what <see cref="Restart"/> is for, and it says so
    /// by clearing every execution count.
    /// </para>
    /// </summary>
    public sealed class NotebookKernelClient : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _interpreterPath;
        private readonly string _hostScriptPath;
        private readonly string _workingDirectory;

        private Process _process;
        private Thread _reader;
        private volatile bool _stopping;
        private string _runningCellId;

        /// <summary>
        /// Directories the notebook wants on the kernel's `sys.path`. Held here rather than passed
        /// per call because they have to be re-applied to every kernel this client starts — a
        /// restart that quietly dropped them would break imports that worked a minute ago.
        /// </summary>
        private List<string> _modulePaths = new List<string>();

        public NotebookKernelClient(string interpreterPath, string hostScriptPath, string workingDirectory)
        {
            _interpreterPath = interpreterPath;
            _hostScriptPath = hostScriptPath;
            _workingDirectory = workingDirectory;
        }

        public event EventHandler<KernelMessageEventArgs> Message;

        /// <summary>True once the host has answered "ready" — not merely once the process started.</summary>
        public bool IsReady { get; private set; }

        /// <summary>"ipython" or "plain": what the user's interpreter could give them.</summary>
        public string Implementation { get; private set; }

        /// <summary>The interpreter's version, as it reported it.</summary>
        public string PythonVersion { get; private set; }

        /// <summary>True while a cell is executing.</summary>
        public bool IsBusy { get { lock (_gate) return _runningCellId != null; } }

        /// <summary>
        /// Start the host process. Returns false with a reason on <see cref="Message"/> ("failed")
        /// rather than throwing: a missing interpreter is a thing to tell the user, not a crash.
        /// </summary>
        public bool Start()
        {
            lock (_gate)
            {
                if (_process != null && !_process.HasExited) return true;

                if (string.IsNullOrEmpty(_interpreterPath) || !File.Exists(_interpreterPath))
                {
                    Raise("failed", null, null, false, null,
                        "No Python interpreter is configured for this notebook.");
                    return false;
                }
                if (string.IsNullOrEmpty(_hostScriptPath) || !File.Exists(_hostScriptPath))
                {
                    Raise("failed", null, null, false, null,
                        "The kernel host script is missing from the installation: " + _hostScriptPath);
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = _interpreterPath,
                    // -u: unbuffered. Without it a print inside a long loop arrives when the cell
                    // ends, which is exactly when it is no longer useful.
                    Arguments = "-u \"" + _hostScriptPath + "\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    WorkingDirectory = Directory.Exists(_workingDirectory)
                        ? _workingDirectory
                        : Environment.CurrentDirectory,
                };
                // A notebook prints DataFrames and tracebacks full of non-ASCII; the default OEM
                // codepage turns those into mojibake before they ever reach the IDE.
                startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                // Matplotlib must not try to open a window from a child process with no console.
                if (!startInfo.EnvironmentVariables.ContainsKey("MPLBACKEND"))
                    startInfo.EnvironmentVariables["MPLBACKEND"] = "Agg";

                try
                {
                    _stopping = false;
                    _process = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    Raise("failed", null, null, false, null, "Could not start the kernel: " + ex.Message);
                    return false;
                }

                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "helix-notebook-kernel" };
                _reader.Start();
                ReadStderrInBackground();

                // BEFORE anything else reaches the host. The host answers requests in order on one
                // thread, so sending this first is what guarantees a cell can never run against a
                // sys.path that has not been set up yet — Execute() calls Start() and then queues
                // its code immediately, and the two would otherwise race on every cold start.
                SendModulePaths(_process);
                return true;
            }
        }

        /// <summary>Queue a cell for execution. <paramref name="cellId"/> comes back on every message.</summary>
        public bool Execute(string cellId, string code)
        {
            if (!Start()) return false;
            lock (_gate) _runningCellId = cellId;
            return Send(new JObject
            {
                { "type", "execute" },
                { "id", cellId },
                { "code", code ?? string.Empty },
            });
        }

        /// <summary>
        /// Set the directories on the kernel's <c>sys.path</c>, replacing whatever was set before.
        ///
        /// <para>
        /// Applied immediately when a kernel is already up, and re-applied automatically to every
        /// kernel started afterwards. Never starts one: registering a path is a thing a user does
        /// while reading a notebook, and it must not launch a Python process on its own.
        /// </para>
        /// </summary>
        public void SetModuleSearchPaths(IEnumerable<string> paths)
        {
            Process process;
            lock (_gate)
            {
                _modulePaths = paths == null ? new List<string>() : new List<string>(paths);
                process = _process;
            }
            if (process != null && !process.HasExited) SendModulePaths(process);
        }

        private void SendModulePaths(Process process)
        {
            List<string> paths;
            lock (_gate) paths = new List<string>(_modulePaths);
            if (paths.Count == 0) return;

            var array = new JArray();
            foreach (string path in paths) array.Add(path);
            Send(process, new JObject { { "type", "sys_path" }, { "paths", array } });
        }

        /// <summary>Ask the live kernel what completes at <paramref name="cursor"/>. Answers arrive as a message.</summary>
        public bool RequestCompletions(string requestId, string code, int cursor)
        {
            if (!IsReady) return false;
            return Send(new JObject
            {
                { "type", "complete" },
                { "id", requestId },
                { "code", code ?? string.Empty },
                { "cursor", cursor },
            });
        }

        /// <summary>
        /// Ask what names the notebook has bound — the Variables pane's data. The answer arrives as
        /// a "variables" message; the kernel is a process, so this cannot be a return value.
        /// </summary>
        public bool RequestVariables()
        {
            if (!IsReady) return false;
            return Send(new JObject { { "type", "variables" } });
        }

        /// <summary>
        /// Ask the host whether its request loop is IDLE. The answer is a "pong" message.
        ///
        /// <para>
        /// This is a diagnostic, not a keep-alive. The host runs one request at a time on its main
        /// thread, so a ping sent during a cell waits behind that cell and is answered only when it
        /// finishes. <b>A pong that arrives while the IDE still believes a cell is running
        /// therefore proves the host is NOT running it</b> — the execute request was lost, or its
        /// status was. That is the difference between "your cell is slow" and "the IDE is stuck",
        /// and without asking, the two look identical: a spinner that never stops.
        /// </para>
        /// </summary>
        public bool Ping()
        {
            if (!IsReady) return false;
            return Send(new JObject { { "type", "ping" } });
        }

        /// <summary>Ask for the runtime's RAM and disk — the resource meter.</summary>
        public bool RequestResources()
        {
            if (!IsReady) return false;
            return Send(new JObject { { "type", "resources" } });
        }

        /// <summary>Stop the running cell without losing the namespace.</summary>
        public void Interrupt()
        {
            Send(new JObject { { "type", "interrupt" } });
        }

        /// <summary>Kill the process and start a clean one. Every name the notebook defined is gone.</summary>
        public void Restart()
        {
            Stop();
            IsReady = false;
            Start();
        }

        public void Stop()
        {
            Process process;
            lock (_gate)
            {
                _stopping = true;
                _runningCellId = null;
                process = _process;
                _process = null;
            }
            if (process == null) return;

            try { if (!process.HasExited) Send(process, new JObject { { "type", "shutdown" } }); }
            catch (Exception) { }

            try
            {
                if (!process.WaitForExit(750) && !process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (NotSupportedException) { }
            finally { try { process.Dispose(); } catch (Exception) { } }
        }

        public void Dispose() { Stop(); }

        // ------------------------------------------------------------------ plumbing

        private bool Send(JObject request)
        {
            Process process;
            lock (_gate) process = _process;
            if (process == null) return false;
            return Send(process, request);
        }

        private bool Send(Process process, JObject request)
        {
            try
            {
                lock (_gate)
                {
                    process.StandardInput.Write(request.ToString(Formatting.None));
                    process.StandardInput.Write('\n');
                    process.StandardInput.Flush();
                }
                return true;
            }
            catch (IOException) { return false; }              // the kernel died under us
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private void ReadLoop()
        {
            Process process;
            lock (_gate) process = _process;
            if (process == null) return;

            try
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    Dispatch(line);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }

            lock (_gate) _runningCellId = null;
            IsReady = false;
            if (!_stopping) Raise("exit", null, null, false, null, "The kernel exited.");
        }

        /// <summary>
        /// The host's own crashes come out of stderr, not out of the protocol. Losing them means a
        /// kernel that "just does nothing" — so they are surfaced as an error output instead.
        /// </summary>
        private void ReadStderrInBackground()
        {
            Process process = _process;
            if (process == null) return;
            var thread = new Thread(delegate ()
            {
                try
                {
                    string line;
                    while ((line = process.StandardError.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        var output = new NotebookOutput
                        {
                            Kind = NotebookOutputKind.Stream,
                            StreamName = "stderr",
                            Text = line + "\n",
                        };
                        Raise("output", RunningCell(), output, false, null, null);
                    }
                }
                catch (Exception) { }
            })
            { IsBackground = true, Name = "helix-notebook-kernel-stderr" };
            thread.Start();
        }

        private string RunningCell() { lock (_gate) return _runningCellId; }

        private void Dispatch(string line)
        {
            JObject message;
            try { message = JObject.Parse(line); }
            catch (JsonException)
            {
                // Anything the host printed outside the protocol (a warning from an import, a
                // library writing to fd 1 directly) is still the user's output.
                var stray = new NotebookOutput
                {
                    Kind = NotebookOutputKind.Stream,
                    StreamName = "stdout",
                    Text = line + "\n",
                };
                Raise("output", RunningCell(), stray, false, null, null);
                return;
            }

            string type = Value(message, "type");
            string cellId = Value(message, "id");

            switch (type)
            {
                case "ready":
                    IsReady = true;
                    Implementation = Value(message, "implementation");
                    PythonVersion = Value(message, "python");
                    Raise("ready", null, null, true, null,
                        "Python " + PythonVersion + " (" + Implementation + ")");
                    return;

                case "stream":
                    Raise("output", cellId, new NotebookOutput
                    {
                        Kind = NotebookOutputKind.Stream,
                        StreamName = Value(message, "name") ?? "stdout",
                        Text = Value(message, "text") ?? string.Empty,
                    }, false, null, null);
                    return;

                case "execute_result":
                case "display_data":
                    {
                        var output = new NotebookOutput
                        {
                            Kind = type == "execute_result"
                                ? NotebookOutputKind.ExecuteResult
                                : NotebookOutputKind.DisplayData,
                            ExecutionCount = ReadInt(message["execution_count"]),
                        };
                        ReadBundle(message["data"] as JObject, output);
                        Raise("output", cellId, output, false, output.ExecutionCount, null);
                        return;
                    }

                case "error":
                    {
                        var output = new NotebookOutput
                        {
                            Kind = NotebookOutputKind.Error,
                            ErrorName = Value(message, "ename"),
                            ErrorValue = Value(message, "evalue"),
                        };
                        var traceback = message["traceback"] as JArray;
                        if (traceback != null)
                            foreach (JToken entry in traceback) output.Traceback.Add(entry.ToString());
                        Raise("output", cellId, output, false, null, null);
                        return;
                    }

                case "status":
                    lock (_gate) if (_runningCellId == cellId) _runningCellId = null;
                    Raise("status", cellId, null, message["ok"] != null && message["ok"].Value<bool>(),
                        ReadInt(message["execution_count"]), null);
                    return;

                case "completions":
                    {
                        var matches = new List<string>();
                        var array = message["matches"] as JArray;
                        if (array != null) foreach (JToken match in array) matches.Add(match.ToString());
                        Raise("completions", cellId, null, true, ReadInt(message["cursor_start"]),
                            string.Join("\n", matches.ToArray()));
                        return;
                    }

                case "variables":
                case "resources":
                    // Handed on as the message's own JSON: the Variables pane and the resource meter
                    // each know their own shape, and re-modelling it here would be a third place to
                    // keep in step with the host script.
                    Raise(type, cellId, null, true, null, message.ToString(Formatting.None));
                    return;

                case "sys_path":
                    // Handed on as its own JSON for the same reason `variables` is: the pane that
                    // asked knows the shape, and re-modelling added/missing/shadowed here would be
                    // a third copy to keep in step with the host script.
                    Raise("sys_path", cellId, null, true, null, message.ToString(Formatting.None));
                    return;

                case "pong":
                    Raise("pong", cellId, null, true, null, null);
                    return;

                case "exit":
                    IsReady = false;
                    Raise("exit", null, null, true, null, null);
                    return;
            }
        }

        private static void ReadBundle(JObject data, NotebookOutput into)
        {
            if (data == null) return;
            foreach (KeyValuePair<string, JToken> pair in data)
            {
                if (pair.Value == null) continue;
                into.Data[pair.Key] = pair.Value.Type == JTokenType.Object || pair.Value.Type == JTokenType.Array
                    ? pair.Value.ToString(Formatting.None)
                    : pair.Value.ToString();
            }
        }

        private static string Value(JObject message, string name)
        {
            JToken token = message[name];
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static int? ReadInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            int value;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? (int?)value
                : null;
        }

        private void Raise(string kind, string cellId, NotebookOutput output, bool ok, int? count, string detail)
        {
            EventHandler<KernelMessageEventArgs> handler = Message;
            if (handler == null) return;
            try { handler(this, new KernelMessageEventArgs(kind, cellId, output, ok, count, detail)); }
            catch (Exception) { /* a subscriber's failure must not kill the reader thread */ }
        }
    }
}
