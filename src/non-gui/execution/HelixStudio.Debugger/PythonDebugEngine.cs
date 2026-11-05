using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Debugger
{
    #region Event Args

    public class DebugStoppedEventArgs : EventArgs
    {
        public string Reason { get; set; }
        public int ThreadId { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        // Populated only when Reason == "exception": the exception's type name (e.g. "ZeroDivisionError")
        // and message (e.g. "division by zero"), obtained from debugpy's exceptionInfo request.
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
    }

    public class DebugOutputEventArgs : EventArgs
    {
        public string Text { get; set; }
        public string Category { get; set; } // stdout, stderr, console
    }

    public class DebugStackFrame
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }

    public class DebugVariable
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
        public int VariablesReference { get; set; }
    }

    public class DebugScope
    {
        public string Name { get; set; }
        public int VariablesReference { get; set; }
        public bool Expensive { get; set; }
    }

    public class DebugLaunchResultEventArgs : EventArgs
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    #endregion

    /// <summary>
    /// Orchestrates Python debugging sessions using debugpy and the Debug Adapter Protocol.
    /// Inspired by SharpDevelop's WindowsDebugger service and NDebugger pattern
    /// (Debugger.AddIn/Service/WindowsDebugger.cs and Debugger.Core/NDebugger.cs).
    /// </summary>
    public class PythonDebugEngine : IDisposable, IDebugEngine
    {
        private readonly PythonInterpreterManager _interpreterManager;
        private readonly BreakpointManager _breakpointManager;
        private DapClient _dapClient;
        private Process _debugProcess;
        private int _debugPort;
        private bool _disposed;
        private volatile bool _isDebugging;
        private volatile bool _isPaused;
        private int _mainThreadId = 1;
        private ManualResetEventSlim _initializedEvent;
        private string _scriptPath;

        /// <summary>Fired when execution stops (breakpoint, step, exception).</summary>
        public event EventHandler<DebugStoppedEventArgs> Stopped;

        /// <summary>Fired when program output is received.</summary>
        public event EventHandler<DebugOutputEventArgs> OutputReceived;

        /// <summary>Fired when the debug session ends.</summary>
        public event EventHandler SessionEnded;

        public bool IsDebugging { get { return _isDebugging; } }
        public bool IsPaused { get { return _isPaused; } }
        public bool StopOnEntry { get; set; }

        /// <summary>
        /// When &gt; 0, a synthetic breakpoint is set on this line of the script being debugged
        /// (the one passed to <see cref="StartDebuggingAsync"/>) so the session pauses on entry.
        /// debugpy ignores <c>stopOnEntry</c> on <c>attach</c>, so this is how "stop on entry" is
        /// realized. The line is merged into the script's breakpoints at the DAP layer only — it is
        /// NOT added to the user-facing <see cref="BreakpointManager"/>, so it leaves no stray
        /// breakpoint in the margin or Breakpoints list. The caller resolves the line via
        /// <c>EntryLineResolver.FindEntryLine</c>. Consumed (reset to 0) at launch like
        /// <see cref="StopOnEntry"/>.
        /// </summary>
        public int EntryBreakpointLine { get; set; }

        /// <summary>
        /// Transient "Run to Cursor" target: a one-shot breakpoint at (RunToCursorFile, RunToCursorLine)
        /// merged into the adapter's breakpoints for that file WITHOUT touching the user's
        /// BreakpointManager, then cleared once hit. 0/null = inactive.
        /// </summary>
        public string RunToCursorFile { get; set; }
        public int RunToCursorLine { get; set; }

        /// <summary>
        /// DAP exception-breakpoint filter ids to arm at launch (debugpy supports
        /// "raised", "uncaught", "userUnhandled"). Null/empty = don't break on exceptions.
        /// </summary>
        public IList<string> ExceptionBreakFilters { get; set; }

        public BreakpointManager BreakpointManager { get { return _breakpointManager; } }

        public PythonDebugEngine(PythonInterpreterManager interpreterManager, BreakpointManager breakpointManager)
        {
            _interpreterManager = interpreterManager;
            _breakpointManager = breakpointManager;
        }

        /// <summary>Fired when async debug launch completes or fails.</summary>
        public event EventHandler<DebugLaunchResultEventArgs> LaunchCompleted;

        /// <summary>
        /// Starts a debug session asynchronously on a background thread.
        /// Fires LaunchCompleted when done or on error.
        /// </summary>
        /// <summary>
        /// Extra PYTHONPATH entries (already joined with the platform separator) prepended to the
        /// debuggee's environment, so a script that imports sibling workspace packages resolves them
        /// — e.g. debugging a startup package's <c>main.py</c> that does
        /// <c>from lib_core... import ...</c>. Set by the controller from the solution's project
        /// roots before each Start. Null/empty leaves the environment untouched.
        /// </summary>
        public string PythonPath { get; set; }

        /// <summary>
        /// Extra environment variables for the debuggee, applied after the engine's own. This is
        /// how a debug run reaches the CUDA emulator: the shim directories, <c>HELIX_CUDA_EMULATE</c>
        /// and the kernel debugger's port all arrive here.
        ///
        /// <para>
        /// A null VALUE removes the variable instead of setting it to the empty string. The two are
        /// not the same to Python -- an empty <c>PYTHONPATH</c> puts the working directory on
        /// <c>sys.path</c>, which is exactly the import a single-file run is supposed to make
        /// impossible.
        /// </para>
        ///
        /// <para>
        /// <see cref="PythonPath"/> still wins for PYTHONPATH specifically: the solution's own
        /// package roots must come first, or a stale entry here could shadow the user's code.
        /// </para>
        /// </summary>
        public IDictionary<string, string> ExtraEnvironment { get; set; }

        /// <param name="scriptArguments">
        /// The debuggee's own command line, appended after the script exactly as the user wrote it
        /// (the package's <c>[tool.helixstudio].run-args</c>). Passed through verbatim rather than
        /// re-quoted: debugpy is launched through a command line, and the value already IS one, so
        /// re-quoting would double-escape the user's own quotes around paths.
        /// </param>
        public void StartDebuggingAsync(string filePath, string workingDirectory = null,
            string scriptArguments = null)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Script not found: " + filePath);

            _scriptPath = filePath;
            string workDir = workingDirectory ?? Path.GetDirectoryName(filePath);
            string target = "\"" + filePath + "\"";
            if (!string.IsNullOrWhiteSpace(scriptArguments)) target += " " + scriptArguments.Trim();
            LaunchDebugpy(target, workDir);
        }

        /// <summary>
        /// Debugs a Python MODULE invocation (<c>python -m &lt;moduleName&gt; ...</c>) instead of a
        /// single script file — the shape a test-package "Debug Tests" launch needs
        /// (<c>-m pytest &lt;dir&gt; -v</c>). debugpy attaches identically either way (it patches
        /// import machinery generally, not per-entry-file), so breakpoints set in any
        /// <c>test_*.py</c> file pytest collects bind exactly like a normal script debug session.
        /// There is no single "script" here, so <see cref="EntryBreakpointLine"/>/stop-on-entry
        /// don't apply — callers should leave <see cref="StopOnEntry"/> false for module launches.
        /// </summary>
        public void StartDebuggingModuleAsync(string moduleName, IEnumerable<string> moduleArgs, string workingDirectory)
        {
            if (string.IsNullOrEmpty(moduleName))
                throw new ArgumentException("moduleName is required.", nameof(moduleName));
            if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException("Working directory not found: " + workingDirectory);

            _scriptPath = null; // module mode: no single script file to arm an entry breakpoint on
            var target = new StringBuilder("-m ").Append(moduleName);
            if (moduleArgs != null)
                foreach (string a in moduleArgs)
                    target.Append(" \"").Append(a).Append('"');
            LaunchDebugpy(target.ToString(), workingDirectory);
        }

        // Shared launch plumbing for both StartDebuggingAsync (a script file) and
        // StartDebuggingModuleAsync (a "-m module args..." invocation) — everything past building
        // the debugpy target arguments is identical: process env, output piping, and the
        // background DAP connect/handshake thread.
        private void LaunchDebugpy(string debugpyTargetArgs, string workingDirectory)
        {
            if (_isDebugging)
                throw new InvalidOperationException("A debug session is already active.");

            var interpreter = _interpreterManager.SelectedInterpreter;
            if (interpreter == null)
                throw new InvalidOperationException("No Python interpreter selected.");

            string pythonPath = interpreter.ExecutablePath;

            // Find a free port
            _debugPort = FindFreePort();

            // Launch Python with debugpy (on UI thread – process start is fast)
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                // -Xfrozen_modules=off: without it, Python 3.11+ ships frozen stdlib modules
                // that debugpy can't trace, so breakpoints in code reached through them are
                // silently missed (debugpy prints exactly this warning). Disabling frozen
                // modules makes breakpoint binding reliable.
                Arguments = string.Format(
                    "-u -Xfrozen_modules=off -m debugpy --listen 127.0.0.1:{0} --wait-for-client {1}",
                    _debugPort, debugpyTargetArgs),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYDEVD_DISABLE_FILE_VALIDATION"] = "1";
            if (ExtraEnvironment != null)
                foreach (KeyValuePair<string, string> pair in ExtraEnvironment)
                {
                    if (string.IsNullOrEmpty(pair.Key)) continue;
                    if (pair.Value == null) psi.EnvironmentVariables.Remove(pair.Key);
                    else psi.EnvironmentVariables[pair.Key] = pair.Value;
                }
            // Make sibling workspace packages importable (e.g. a startup main.py that imports
            // lib_core/lib_utils). Prepend our roots ahead of any inherited PYTHONPATH.
            if (!string.IsNullOrEmpty(PythonPath))
            {
                string existingPp = psi.EnvironmentVariables.ContainsKey("PYTHONPATH")
                    ? psi.EnvironmentVariables["PYTHONPATH"] : null;
                psi.EnvironmentVariables["PYTHONPATH"] = string.IsNullOrEmpty(existingPp)
                    ? PythonPath
                    : PythonPath + Path.PathSeparator + existingPp;
            }

            _debugProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _debugProcess.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    var handler = OutputReceived;
                    if (handler != null)
                        handler(this, new DebugOutputEventArgs { Text = e.Data + "\n", Category = "stdout" });
                }
            };
            _debugProcess.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    var handler = OutputReceived;
                    if (handler != null)
                        handler(this, new DebugOutputEventArgs { Text = e.Data + "\n", Category = "stderr" });
                }
            };
            _debugProcess.Exited += (s, e) =>
            {
                EndSession();
            };

            try
            {
                _debugProcess.Start();
                _debugProcess.BeginOutputReadLine();
                _debugProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                CleanupProcess();
                FireLaunchCompleted(false, "Failed to start Python: " + ex.Message);
                return;
            }

            // Run the blocking connect + handshake on a background thread
            var bgThread = new Thread(() => ConnectAndInitialize());
            bgThread.IsBackground = true;
            bgThread.Name = "DebugConnect";
            bgThread.Start();
        }

        private void ConnectAndInitialize()
        {
            // Connect DAP client with retries (debugpy needs time to start)
            _dapClient = new DapClient();
            _dapClient.EventReceived += DapClient_EventReceived;
            _dapClient.Disconnected += (s, e) => EndSession();

            try
            {
                _dapClient.Connect("127.0.0.1", _debugPort, maxRetries: 50, retryDelayMs: 100);
            }
            catch (Exception ex)
            {
                CleanupProcess();
                FireLaunchCompleted(false, "Failed to connect to debugpy: " + ex.Message);
                return;
            }

            _isDebugging = true;
            _initializedEvent = new ManualResetEventSlim(false);

            // DAP initialization sequence. The ordering here is load-bearing: debugpy (like
            // most DAP adapters) does NOT respond to the launch/attach request until AFTER it
            // receives `configurationDone`. So `attach` must be sent fire-and-forget — waiting
            // on its response synchronously deadlocks the handshake (the response can only
            // arrive once we've already sent configurationDone, which we can't do while
            // blocked). See the DAP spec "Initialization" sequence and debugpy's deferred
            // launch/attach response.
            try
            {
                // 1. Initialize (synchronous — debugpy answers this immediately, then emits
                //    the `initialized` event that gates the configuration phase).
                var initArgs = new JObject
                {
                    ["clientID"] = "PythonCodeEditor",
                    ["clientName"] = "Python Code Editor",
                    ["adapterID"] = "debugpy",
                    ["linesStartAt1"] = true,
                    ["columnsStartAt1"] = true,
                    ["pathFormat"] = "path",
                    ["supportsVariableType"] = true,
                    ["supportsRunInTerminalRequest"] = false
                };
                _dapClient.SendRequest("initialize", initArgs);

                // 2. Attach — FIRE-AND-FORGET. The response is deferred until configurationDone.
                bool stopOnEntry = StopOnEntry;
                StopOnEntry = false;
                var attachArgs = new JObject
                {
                    ["justMyCode"] = true,
                    ["redirectOutput"] = true
                };
                if (stopOnEntry)
                    attachArgs["stopOnEntry"] = true;
                _dapClient.SendRequestAsync("attach", attachArgs);

                // 3. Wait for the `initialized` event before configuring (deterministic —
                //    replaces a racy fixed sleep). Fall through after a bounded wait so a
                //    non-conforming adapter can't hang us forever.
                _initializedEvent.Wait(5000);

                // 4. Set breakpoints for all files (synchronous — setBreakpoints IS answered
                //    during the configuration phase, before configurationDone).
                SendAllBreakpoints();

                // 5. Set exception breakpoints. Empty by default (don't break on exceptions);
                //    ExceptionBreakFilters arms debugpy's "raised"/"uncaught"/"userUnhandled".
                var exFilters = new JArray();
                if (ExceptionBreakFilters != null)
                {
                    foreach (var f in ExceptionBreakFilters)
                        if (!string.IsNullOrEmpty(f)) exFilters.Add(f);
                }
                var exArgs = new JObject
                {
                    ["filters"] = exFilters
                };
                _dapClient.SendRequest("setExceptionBreakpoints", exArgs);

                // 6. Configuration done — execution starts and the deferred attach response
                //    is released. Fire-and-forget: blocking here is unnecessary and the
                //    response races with the program's first `stopped`/`output` events.
                _dapClient.SendRequestAsync("configurationDone");

                FireLaunchCompleted(true, null);
            }
            catch (Exception ex)
            {
                StopDebugging();
                FireLaunchCompleted(false, "Debug initialization failed: " + ex.Message);
            }
        }

        private void FireLaunchCompleted(bool success, string error)
        {
            var handler = LaunchCompleted;
            if (handler != null)
                handler(this, new DebugLaunchResultEventArgs { Success = success, ErrorMessage = error });
        }

        /// <summary>
        /// Sends breakpoints for all files to the debug adapter.
        /// </summary>
        public void SendAllBreakpoints()
        {
            if (_dapClient == null || !_dapClient.IsConnected) return;

            var files = new List<string>(_breakpointManager.GetFilesWithBreakpoints());
            // The synthetic entry breakpoint lives on the script file only, which may have no
            // user breakpoints — make sure it still gets a setBreakpoints call.
            if (EntryBreakpointLine > 0 && !string.IsNullOrEmpty(_scriptPath)
                && !files.Exists(f => PathsEqual(f, _scriptPath)))
            {
                files.Add(_scriptPath);
            }
            foreach (var file in files)
            {
                SendBreakpointsForFile(file);
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// Sends breakpoints for a specific file to the debug adapter.
        /// </summary>
        public void SendBreakpointsForFile(string filePath)
        {
            if (_dapClient == null || !_dapClient.IsConnected) return;

            // Only ENABLED breakpoints go to the adapter — disabled ones remain in the panel/editor
            // but must not stop execution.
            var lines = new List<int>(_breakpointManager.GetEnabledBreakpoints(filePath));
            // Merge the synthetic stop-on-entry line for the script file (deduped) without
            // touching the user-facing BreakpointManager — see EntryBreakpointLine.
            if (EntryBreakpointLine > 0 && PathsEqual(filePath, _scriptPath)
                && !lines.Contains(EntryBreakpointLine))
            {
                lines.Add(EntryBreakpointLine);
            }
            // Merge the transient Run-to-Cursor line for its file (deduped), same as the entry line.
            if (RunToCursorLine > 0 && !string.IsNullOrEmpty(RunToCursorFile)
                && PathsEqual(filePath, RunToCursorFile) && !lines.Contains(RunToCursorLine))
            {
                lines.Add(RunToCursorLine);
            }
            var bpArray = new JArray();
            foreach (int line in lines)
            {
                var bp = new JObject { ["line"] = line };
                // Attach a condition / hit-count if the user set one — debugpy only fires the
                // breakpoint when `condition` is truthy and the `hitCondition` rule is satisfied.
                BreakpointCondition cond = _breakpointManager.GetCondition(filePath, line);
                if (cond != null)
                {
                    if (!string.IsNullOrEmpty(cond.Condition)) bp["condition"] = cond.Condition;
                    if (!string.IsNullOrEmpty(cond.HitCondition)) bp["hitCondition"] = cond.HitCondition;
                    // A logMessage makes debugpy treat this as a LOGPOINT: it logs the (interpolated)
                    // message at each hit and continues instead of stopping.
                    if (!string.IsNullOrEmpty(cond.LogMessage)) bp["logMessage"] = cond.LogMessage;
                }
                bpArray.Add(bp);
            }

            var args = new JObject
            {
                ["source"] = new JObject { ["path"] = filePath },
                ["breakpoints"] = bpArray
            };

            try
            {
                _dapClient.SendRequest("setBreakpoints", args);
            }
            catch { }
        }

        /// <summary>Continue execution.</summary>
        public void Continue()
        {
            if (!_isPaused) return;
            var args = new JObject { ["threadId"] = _mainThreadId };
            _dapClient.SendRequestAsync("continue", args);
            _isPaused = false;
        }

        /// <summary>Step over (next line).</summary>
        public void StepOver()
        {
            if (!_isPaused) return;
            var args = new JObject { ["threadId"] = _mainThreadId };
            _dapClient.SendRequestAsync("next", args);
            _isPaused = false;
        }

        /// <summary>Step into function call.</summary>
        public void StepInto()
        {
            if (!_isPaused) return;
            var args = new JObject { ["threadId"] = _mainThreadId };
            _dapClient.SendRequestAsync("stepIn", args);
            _isPaused = false;
        }

        /// <summary>Step out of current function.</summary>
        public void StepOut()
        {
            if (!_isPaused) return;
            var args = new JObject { ["threadId"] = _mainThreadId };
            _dapClient.SendRequestAsync("stepOut", args);
            _isPaused = false;
        }

        /// <summary>Pause execution.</summary>
        public void Pause()
        {
            if (_isPaused || !_isDebugging) return;
            var args = new JObject { ["threadId"] = _mainThreadId };
            _dapClient.SendRequestAsync("pause", args);
        }

        /// <summary>Stop the debug session.</summary>
        public void StopDebugging()
        {
            if (!_isDebugging) return;

            try
            {
                if (_dapClient != null && _dapClient.IsConnected)
                {
                    try
                    {
                        _dapClient.SendRequest("disconnect",
                            new JObject { ["terminateDebuggee"] = true }, timeoutMs: 3000);
                    }
                    catch { }
                }
            }
            finally
            {
                CleanupProcess();
                EndSession();
            }
        }

        /// <summary>
        /// Gets the call stack for the main thread.
        /// </summary>
        public List<DebugStackFrame> GetCallStack()
        {
            var result = new List<DebugStackFrame>();
            if (!_isPaused || _dapClient == null || !_dapClient.IsConnected) return result;

            try
            {
                var args = new JObject { ["threadId"] = _mainThreadId };
                var response = _dapClient.SendRequest("stackTrace", args);

                if (response != null && response["success"] != null && response.Value<bool>("success"))
                {
                    var body = response.Value<JObject>("body");
                    if (body != null)
                    {
                        var frames = body.Value<JArray>("stackFrames");
                        if (frames != null)
                        {
                            foreach (var frame in frames)
                            {
                                var sf = new DebugStackFrame
                                {
                                    Id = frame.Value<int>("id"),
                                    Name = frame.Value<string>("name") ?? "",
                                    Line = frame.Value<int>("line"),
                                    Column = frame.Value<int>("column")
                                };
                                var source = frame.Value<JObject>("source");
                                if (source != null)
                                    sf.FilePath = source.Value<string>("path") ?? "";
                                result.Add(sf);
                            }
                        }
                    }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Gets scopes for a stack frame.
        /// </summary>
        public List<DebugScope> GetScopes(int frameId)
        {
            var result = new List<DebugScope>();
            if (!_isPaused || _dapClient == null) return result;

            try
            {
                var args = new JObject { ["frameId"] = frameId };
                var response = _dapClient.SendRequest("scopes", args);

                if (response != null && response.Value<bool>("success"))
                {
                    var body = response.Value<JObject>("body");
                    var scopes = body != null ? body.Value<JArray>("scopes") : null;
                    if (scopes != null)
                    {
                        foreach (var scope in scopes)
                        {
                            result.Add(new DebugScope
                            {
                                Name = scope.Value<string>("name") ?? "",
                                VariablesReference = scope.Value<int>("variablesReference"),
                                Expensive = scope.Value<bool>("expensive")
                            });
                        }
                    }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Gets variables for a given variables reference (from scope or expandable variable).
        /// </summary>
        public List<DebugVariable> GetVariables(int variablesReference)
        {
            var result = new List<DebugVariable>();
            if (!_isPaused || _dapClient == null) return result;

            try
            {
                var args = new JObject { ["variablesReference"] = variablesReference };
                var response = _dapClient.SendRequest("variables", args);

                if (response != null && response.Value<bool>("success"))
                {
                    var body = response.Value<JObject>("body");
                    var variables = body != null ? body.Value<JArray>("variables") : null;
                    if (variables != null)
                    {
                        foreach (var v in variables)
                        {
                            result.Add(new DebugVariable
                            {
                                Name = v.Value<string>("name") ?? "",
                                Value = v.Value<string>("value") ?? "",
                                Type = v.Value<string>("type") ?? "",
                                VariablesReference = v.Value<int>("variablesReference")
                            });
                        }
                    }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Evaluates an expression in the context of a stack frame.
        /// </summary>
        public DebugVariable Evaluate(string expression, int frameId, string context = "watch")
        {
            if (!_isPaused || _dapClient == null) return null;

            try
            {
                var args = new JObject
                {
                    ["expression"] = expression,
                    ["frameId"] = frameId,
                    ["context"] = context
                };
                var response = _dapClient.SendRequest("evaluate", args);

                if (response != null && response.Value<bool>("success"))
                {
                    var body = response.Value<JObject>("body");
                    if (body != null)
                    {
                        return new DebugVariable
                        {
                            Name = expression,
                            Value = body.Value<string>("result") ?? "",
                            Type = body.Value<string>("type") ?? "",
                            VariablesReference = body.Value<int>("variablesReference")
                        };
                    }
                }
            }
            catch { }

            return null;
        }

        #region Private Methods

        private void DapClient_EventReceived(object sender, DapEventArgs e)
        {
            switch (e.EventType)
            {
                case "initialized":
                    // Adapter is ready for the configuration phase (setBreakpoints / etc.).
                    var ie = _initializedEvent;
                    if (ie != null) ie.Set();
                    break;

                case "stopped":
                    HandleStoppedEvent(e.Body);
                    break;

                case "output":
                    HandleOutputEvent(e.Body);
                    break;

                case "terminated":
                case "exited":
                    EndSession();
                    break;

                case "thread":
                    // Track main thread ID
                    if (e.Body.Value<string>("reason") == "started")
                    {
                        _mainThreadId = e.Body.Value<int>("threadId");
                    }
                    break;
            }
        }

        private void HandleStoppedEvent(JObject body)
        {
            _isPaused = true;
            string reason = body.Value<string>("reason") ?? "unknown";
            int threadId = body.Value<int>("threadId");
            _mainThreadId = threadId;

            // Get the top stack frame to find current position
            string filePath = "";
            int line = 0;
            try
            {
                var frames = GetCallStack();
                if (frames.Count > 0)
                {
                    filePath = frames[0].FilePath;
                    line = frames[0].Line;
                }
            }
            catch { }

            // On an exception stop, ask debugpy for the exception type + message so the UI can show
            // "ZeroDivisionError: division by zero" rather than a bare "stopped (exception)".
            string exType = null, exMessage = null;
            if (string.Equals(reason, "exception", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    JObject info = _dapClient.SendRequest("exceptionInfo",
                        new JObject { ["threadId"] = threadId });
                    JObject respBody = info?["body"] as JObject;
                    if (respBody != null)
                    {
                        exType = respBody.Value<string>("exceptionId");
                        JObject details = respBody["details"] as JObject;
                        if (details != null)
                        {
                            // Prefer the specific typeName/message from details when present.
                            string tn = details.Value<string>("typeName");
                            if (!string.IsNullOrEmpty(tn)) exType = tn;
                            exMessage = details.Value<string>("message");
                        }
                        if (string.IsNullOrEmpty(exMessage))
                            exMessage = respBody.Value<string>("description");
                    }
                }
                catch { /* best-effort — fall back to a bare exception stop */ }
            }

            var handler = Stopped;
            if (handler != null)
            {
                handler(this, new DebugStoppedEventArgs
                {
                    Reason = reason,
                    ThreadId = threadId,
                    FilePath = filePath,
                    Line = line,
                    ExceptionType = exType,
                    ExceptionMessage = exMessage
                });
            }
        }

        private void HandleOutputEvent(JObject body)
        {
            string text = body.Value<string>("output") ?? "";
            string category = body.Value<string>("category") ?? "console";

            var handler = OutputReceived;
            if (handler != null)
            {
                handler(this, new DebugOutputEventArgs { Text = text, Category = category });
            }
        }

        private void EndSession()
        {
            if (!_isDebugging) return;
            _isDebugging = false;
            _isPaused = false;
            EntryBreakpointLine = 0; // consumed; the controller re-arms it per Start

            var ie = _initializedEvent;
            if (ie != null)
            {
                ie.Set(); // release any handshake still waiting so it can unwind
                ie.Dispose();
                _initializedEvent = null;
            }

            if (_dapClient != null)
            {
                _dapClient.Dispose();
                _dapClient = null;
            }

            CleanupProcess();

            var handler = SessionEnded;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void CleanupProcess()
        {
            if (_debugProcess != null)
            {
                try
                {
                    if (!_debugProcess.HasExited)
                        _debugProcess.Kill();
                }
                catch { }
                try { _debugProcess.Dispose(); } catch { }
                _debugProcess = null;
            }
        }

        private static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                StopDebugging();
            }
        }

        #endregion
    }
}
