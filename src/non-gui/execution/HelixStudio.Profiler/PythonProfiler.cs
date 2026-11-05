namespace HelixPythonNamespace.Profiler
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using Newtonsoft.Json.Linq;

    public sealed class ProfileFinishedEventArgs : EventArgs
    {
        // Records is empty when ErrorMessage is non-empty (target failed to run).
        public List<ProfileRecord> Records = new List<ProfileRecord>();
        public string ErrorMessage = "";
    }

    /// <summary>
    /// Runs a Python script under cProfile via a one-shot wrapper script and captures
    /// per-function timing as JSON on stdout. Single-shot per instance: rebuild for each
    /// profiling run. Port of the C++ <c>PythonProfiler</c> (QProcess → <see cref="Process"/>,
    /// Qt signals → .NET events).
    /// </summary>
    public sealed class PythonProfiler
    {
        // The wrapper enables cProfile, then executes the target as __main__ — via run_module
        // (argv[3], a dotted module name, e.g. "mypkg.chain") when one is given, else run_path (a
        // bare file path). run_module is required for a file that uses genuine relative imports
        // (`from . import x` / `from .sibling import y`): run_path (and a plain `python file.py`)
        // never sets __package__ at all, so relative imports raise ImportError('attempted relative
        // import with no known parent package') regardless of PYTHONPATH — only actually running it
        // as a module of its owning package (like `python -m mypkg.chain`) sets __package__
        // correctly. run_path remains the fallback for a standalone file with no owning project.
        // Then dumps pstats rows as JSON to a RESULT FILE (argv[2]) — NOT stdout. Writing to stdout
        // broke whenever the profiled script printed anything (its output interleaved with the
        // JSON, failing the parse); a dedicated file keeps the results clean regardless of the
        // target's I/O.
        internal const string WrapperSource =
            "\nimport cProfile, pstats, json, sys, runpy\n" +
            "target = sys.argv[1]\n" +
            "out_path = sys.argv[2]\n" +
            "module_name = sys.argv[3] if len(sys.argv) > 3 else ''\n" +
            "prof = cProfile.Profile()\n" +
            "try:\n" +
            "    prof.enable()\n" +
            "    try:\n" +
            "        if module_name:\n" +
            "            runpy.run_module(module_name, run_name='__main__', alter_sys=True)\n" +
            "        else:\n" +
            "            runpy.run_path(target, run_name='__main__')\n" +
            "    except SystemExit:\n" +
            "        pass\n" +
            "    finally:\n" +
            "        prof.disable()\n" +
            "    records = []\n" +
            "    for func, (cc, nc, tt, ct, _callers) in pstats.Stats(prof).stats.items():\n" +
            "        fn_path, fn_line, fn_name = func\n" +
            "        records.append({\n" +
            "            'file': fn_path,\n" +
            "            'line': fn_line,\n" +
            "            'name': fn_name,\n" +
            "            'callCount': cc,\n" +
            "            'totalSeconds': tt,\n" +
            "            'cumulativeSeconds': ct,\n" +
            "        })\n" +
            "    with open(out_path, 'w', encoding='utf-8') as _f:\n" +
            "        _f.write(json.dumps({'records': records}))\n" +
            "except Exception as ex:\n" +
            "    sys.stderr.write('PROFILER_ERROR: ' + repr(ex))\n" +
            "    sys.exit(2)\n";

        private readonly object _gate = new object();
        private Process _process;
        private string _wrapperPath;
        private string _resultPath;
        private readonly StringBuilder _stdoutBuffer = new StringBuilder();
        private readonly StringBuilder _stderrBuffer = new StringBuilder();
        private bool _finishedRaised;

        public event EventHandler<ProfileFinishedEventArgs> Finished;

        public bool IsRunning
        {
            get
            {
                lock (_gate) { return _process != null && !_process.HasExited; }
            }
        }

        internal static string WriteWrapper()
        {
            try
            {
                string tmpDir = Path.GetTempPath();
                if (string.IsNullOrEmpty(tmpDir)) return null;
                string path = Path.Combine(tmpDir, "helix_profiler_wrapper.py");
                File.WriteAllText(path, WrapperSource, new UTF8Encoding(false));
                return path;
            }
            catch
            {
                return null;
            }
        }

        public void Start(string interpreterPath, string targetScriptPath, string workingDirectory,
            IReadOnlyDictionary<string, string> extraEnv = null, string moduleName = null)
        {
            if (_process != null) return;

            _wrapperPath = WriteWrapper();
            if (string.IsNullOrEmpty(_wrapperPath))
            {
                RaiseFinished(new List<ProfileRecord>(), "Could not write profiler wrapper to temp.");
                return;
            }

            _finishedRaised = false;
            _stdoutBuffer.Clear();
            _stderrBuffer.Clear();
            // Unique result file per run — the wrapper writes the JSON here, isolated from the
            // target script's own stdout.
            _resultPath = Path.Combine(Path.GetTempPath(), "helix_profile_" + Guid.NewGuid().ToString("N") + ".json");
            try { if (File.Exists(_resultPath)) File.Delete(_resultPath); } catch { }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = interpreterPath,
                    Arguments = "\"" + _wrapperPath + "\" \"" + targetScriptPath + "\" \"" + _resultPath + "\"" +
                        (string.IsNullOrEmpty(moduleName) ? "" : " \"" + moduleName + "\""),
                    WorkingDirectory = workingDirectory ?? "",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                // Match Run's env (BuildPythonPathEnv): without PYTHONPATH, the profiled script's
                // own top-level absolute imports of sibling source roots fail with ImportError even
                // though the identical script runs fine via Run/Debug, which both set this.
                if (extraEnv != null)
                {
                    foreach (var kv in extraEnv)
                        psi.EnvironmentVariables[kv.Key] = kv.Value;
                }
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (_gate) { _stdoutBuffer.Append(e.Data).Append('\n'); } };
                _process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (_gate) { _stderrBuffer.Append(e.Data).Append('\n'); } };
                _process.Exited += (s, e) => OnProcessFinished();
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _process = null;
                RaiseFinished(new List<ProfileRecord>(), ex.Message);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        KillProcessTree(_process);
                        _process.WaitForExit(2000);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Kill the process and all descendants. .NET Framework 4.7.2 lacks
        /// <c>Process.Kill(entireProcessTree)</c>; a plain <c>Kill()</c> orphans the child
        /// processes the python launcher spawns (so a cancelled profile run keeps running).
        /// <c>taskkill /T</c> terminates the whole tree, with a direct Kill fallback.
        /// </summary>
        private static void KillProcessTree(Process p)
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

        private void OnProcessFinished()
        {
            int exitCode;
            string stderrText;
            lock (_gate)
            {
                if (_process == null) return;
                exitCode = _process.ExitCode;
                stderrText = _stderrBuffer.ToString();
                _process = null;
            }

            if (exitCode != 0)
            {
                string err = string.IsNullOrEmpty(stderrText.Trim())
                    ? "Profiler exited with code " + exitCode + "."
                    : stderrText.Trim();
                RaiseFinished(new List<ProfileRecord>(), err);
                return;
            }

            // Read the JSON results from the dedicated file (NOT stdout — the target may print).
            string resultJson;
            try { resultJson = File.ReadAllText(_resultPath); }
            catch (Exception ex)
            {
                RaiseFinished(new List<ProfileRecord>(),
                    "Profiler produced no result file: " + ex.Message);
                return;
            }
            finally { try { if (File.Exists(_resultPath)) File.Delete(_resultPath); } catch { } }

            string parseError;
            List<ProfileRecord> records = ParseRecords(resultJson, out parseError);
            if (parseError != null)
            {
                RaiseFinished(new List<ProfileRecord>(), parseError);
                return;
            }
            RaiseFinished(records, "");
        }

        /// <summary>
        /// Parses the wrapper's JSON stdout (<c>{"records":[...]}</c>) into records.
        /// Sets <paramref name="error"/> to a message (and returns null) on malformed input.
        /// </summary>
        public static List<ProfileRecord> ParseRecords(string stdoutJson, out string error)
        {
            error = null;
            JObject doc;
            try
            {
                doc = JObject.Parse(stdoutJson);
            }
            catch (Exception ex)
            {
                error = "Profiler returned malformed JSON: " + ex.Message;
                return null;
            }

            List<ProfileRecord> records = new List<ProfileRecord>();
            JToken arrToken = doc["records"];
            if (arrToken != null && arrToken.Type == JTokenType.Array)
            {
                foreach (JToken v in (JArray)arrToken)
                {
                    records.Add(new ProfileRecord
                    {
                        Name = v.Value<string>("name") ?? "",
                        FilePath = v.Value<string>("file") ?? "",
                        Line = v.Value<int?>("line") ?? 0,
                        CallCount = v.Value<int?>("callCount") ?? 0,
                        TotalSeconds = v.Value<double?>("totalSeconds") ?? 0.0,
                        CumulativeSeconds = v.Value<double?>("cumulativeSeconds") ?? 0.0,
                    });
                }
            }
            return records;
        }

        private void RaiseFinished(List<ProfileRecord> records, string errorMessage)
        {
            lock (_gate)
            {
                if (_finishedRaised) return;
                _finishedRaised = true;
            }
            EventHandler<ProfileFinishedEventArgs> h = Finished;
            if (h != null)
            {
                h(this, new ProfileFinishedEventArgs { Records = records, ErrorMessage = errorMessage });
            }
        }
    }
}
