namespace HelixPythonNamespace.Catalog
{
    using System;
    using System.Diagnostics;
    using Newtonsoft.Json.Linq;

    public sealed class ScannerProgressEventArgs : EventArgs
    {
        public string Message;
        public int Done;
        public int Total;
    }

    public sealed class ScannerWarningEventArgs : EventArgs
    {
        public string Package;
        public string Message;
    }

    public sealed class ScannerFinishedEventArgs : EventArgs
    {
        public bool Ok;
        public string ErrorMessage;
    }

    /// <summary>
    /// Spawns <c>&lt;interpreter&gt; &lt;introspect script&gt;</c> and parses its
    /// line-delimited JSON stdout into a <see cref="PythonCatalog"/>, raising progress
    /// / warning / completion events. Port of the C++ <c>CatalogScanner</c> (QProcess →
    /// <see cref="Process"/>; Qt signals → .NET events). The catalog must outlive the scan.
    /// </summary>
    public sealed class CatalogScanner
    {
        private Process _proc;
        private PythonCatalog _outCatalog;
        private int _lastTotal;
        private bool _finishedRaised;
        private readonly object _gate = new object();

        public event EventHandler<ScannerProgressEventArgs> ProgressUpdated;
        public event EventHandler<ScannerWarningEventArgs> ScannerWarning;
        public event EventHandler<ScannerFinishedEventArgs> Finished;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _proc != null && !_proc.HasExited;
                }
            }
        }

        public void StartScan(string interpreterPath, string introspectScriptPath, PythonCatalog outCatalog)
        {
            StartScan(interpreterPath, introspectScriptPath, outCatalog, null);
        }

        /// <summary>
        /// <paramref name="modules"/>, when non-empty, REPLACES introspect.py's own
        /// DEFAULT_MODULES stdlib list for this run (its argv contract: any module names
        /// passed on the command line take over from the built-in default) — so callers
        /// wanting the stdlib baseline *and* a third-party package must include both, e.g.
        /// DEFAULT_MODULES-equivalent names plus "numpy". Module names are plain Python
        /// dotted identifiers (import statement targets), never shell-interpreted here.
        /// </summary>
        public void StartScan(string interpreterPath, string introspectScriptPath, PythonCatalog outCatalog,
            System.Collections.Generic.IEnumerable<string> modules)
        {
            _outCatalog = outCatalog;
            _finishedRaised = false;
            _lastTotal = 0;
            try
            {
                string args = "\"" + introspectScriptPath + "\"";
                if (modules != null)
                    foreach (string m in modules)
                        if (!string.IsNullOrEmpty(m)) args += " " + m;
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = interpreterPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.OutputDataReceived += (s, e) => { if (e.Data != null) HandleEvent(e.Data); };
                _proc.Exited += (s, e) => RaiseFinished(_proc.ExitCode == 0, _proc.ExitCode == 0 ? "" : "scanner exited with code " + _proc.ExitCode);
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                RaiseFinished(false, ex.Message);
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                try
                {
                    if (_proc != null && !_proc.HasExited)
                    {
                        _proc.Kill();
                    }
                }
                catch { }
            }
            RaiseFinished(false, "cancelled");
        }

        private void HandleEvent(string jsonLine)
        {
            if (string.IsNullOrWhiteSpace(jsonLine))
            {
                return;
            }
            JObject j;
            try
            {
                j = JObject.Parse(jsonLine);
            }
            catch
            {
                return;
            }
            string evt = j.Value<string>("event") ?? "";
            if (evt == "progress")
            {
                string pkg = j.Value<string>("package") ?? "";
                int done = j.Value<int?>("done") ?? 0;
                int total = j.Value<int?>("total") ?? 0;
                _lastTotal = total;
                OnProgress(pkg, done, total);
            }
            else if (evt == "symbols" && _outCatalog != null)
            {
                JToken arr = j["symbols"];
                if (arr != null && arr.Type == JTokenType.Array)
                {
                    foreach (JToken it in (JArray)arr)
                    {
                        CatalogSymbol sym = new CatalogSymbol
                        {
                            ModuleFqn = it.Value<string>("moduleFqn") ?? "",
                            Name = it.Value<string>("name") ?? "",
                            Kind = it.Value<string>("kind") ?? "",
                            Signature = it.Value<string>("signature") ?? "",
                            Summary = it.Value<string>("summary") ?? "",
                            ReturnSummary = it.Value<string>("returnSummary") ?? "",
                            FullDoc = it.Value<string>("fullDoc") ?? "",
                        };
                        JToken ps = it["params"];
                        if (ps != null && ps.Type == JTokenType.Array)
                        {
                            foreach (JToken jp in (JArray)ps)
                            {
                                sym.Params.Add(new CatalogParam
                                {
                                    Name = jp.Value<string>("name") ?? "",
                                    TypeAnnotation = jp.Value<string>("type") ?? "",
                                    DefaultValue = jp.Value<string>("default") ?? "",
                                    Description = jp.Value<string>("description") ?? "",
                                });
                            }
                        }
                        _outCatalog.Add(sym);
                    }
                }
            }
            else if (evt == "error")
            {
                string pkg = j.Value<string>("package") ?? "";
                string msg = j.Value<string>("message") ?? "";
                EventHandler<ScannerWarningEventArgs> h = ScannerWarning;
                if (h != null)
                {
                    h(this, new ScannerWarningEventArgs { Package = pkg, Message = msg });
                }
            }
            else if (evt == "done")
            {
                RaiseFinished(true, "");
            }
        }

        private void OnProgress(string message, int done, int total)
        {
            EventHandler<ScannerProgressEventArgs> h = ProgressUpdated;
            if (h != null)
            {
                h(this, new ScannerProgressEventArgs { Message = message, Done = done, Total = total });
            }
        }

        private void RaiseFinished(bool ok, string errorMessage)
        {
            lock (_gate)
            {
                if (_finishedRaised)
                {
                    return;
                }
                _finishedRaised = true;
            }
            EventHandler<ScannerFinishedEventArgs> h = Finished;
            if (h != null)
            {
                h(this, new ScannerFinishedEventArgs { Ok = ok, ErrorMessage = errorMessage });
            }
        }
    }
}
