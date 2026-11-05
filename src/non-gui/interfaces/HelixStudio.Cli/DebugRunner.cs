namespace HelixPythonNamespace.Cli
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using HelixPythonNamespace.Debugger;
    using HelixPythonNamespace.VirtualCompiler;

    /// <summary>
    /// Headless driver for the <see cref="PythonDebugEngine"/> — surfaces the debugger
    /// (debugpy + DAP) on the command line so a debug session can be scripted and
    /// CI-verified without the WinForms UI.
    ///
    /// Usage:
    ///   helix_cli debug &lt;script.py&gt; [--break=LINE]... [--break=FILE:LINE]...
    ///                                 [--stop-on-entry] [--continue] [--json]
    ///
    /// Exit codes: 0 ran to completion (and, if breakpoints were requested, at least one
    /// bound and was hit); 3 no interpreter; 4 launch failed; 5 timed out; 6 a requested
    /// breakpoint never bound / never hit.
    /// </summary>
    internal static class DebugRunner
    {
        public static int Run(string script, IList<string> breakSpecs, bool stopOnEntry,
                              string stepMode, IList<string> watches, IList<string> exceptionFilters,
                              int maxStops, bool json, int timeoutMs, TextWriter outw, TextWriter errw)
        {
            if (stepMode != null && stepMode != "over" && stepMode != "into" && stepMode != "out")
            {
                errw.Write("debug: bad --step mode (want over|into|out): " + stepMode + "\n");
                return 2;
            }
            if (string.IsNullOrEmpty(script) || !File.Exists(script))
            {
                errw.Write("debug: script not found: " + (script ?? "<null>") + "\n");
                return 4;
            }
            script = Path.GetFullPath(script);

            PythonInterpreterManager manager = new PythonInterpreterManager();
            PythonInterpreter interp = manager.DiscoverDefault();
            if (interp == null)
            {
                errw.Write("debug: no Python interpreter found on PATH\n");
                return 3;
            }

            BreakpointManager bpm = new BreakpointManager();
            int requested = 0;
            foreach (string spec in breakSpecs)
            {
                string file;
                int line;
                if (!TryParseBreak(spec, script, out file, out line))
                {
                    errw.Write("debug: bad --break spec: " + spec + "\n");
                    return 2;
                }
                bpm.AddBreakpoint(file, line);
                requested++;
            }

            // stop-on-entry: debugpy honors `stopOnEntry` only for `launch`, NOT `attach`
            // (we attach via --wait-for-client), so the adapter silently ignores it and the
            // program runs to completion. Synthesize it deterministically as a breakpoint on
            // the script's first executable line (skipping the module docstring); it binds
            // when module load reaches that line. Fall back to the adapter flag only if the
            // script can't be parsed.
            bool entryViaBreakpoint = false;
            if (stopOnEntry)
            {
                int entryLine = EntryLineResolver.FindEntryLine(script);
                if (entryLine > 0) { bpm.AddBreakpoint(script, entryLine); entryViaBreakpoint = true; }
            }

            using (PythonDebugEngine engine = new PythonDebugEngine(manager, bpm))
            {
                engine.StopOnEntry = stopOnEntry && !entryViaBreakpoint;
                if (exceptionFilters != null && exceptionFilters.Count > 0)
                    engine.ExceptionBreakFilters = exceptionFilters;

                ManualResetEventSlim ended = new ManualResetEventSlim(false);
                ManualResetEventSlim launched = new ManualResetEventSlim(false);
                int hits = 0;
                bool launchOk = false;
                string launchError = null;
                object gate = new object();

                engine.LaunchCompleted += (s, e) =>
                {
                    launchOk = e.Success;
                    launchError = e.ErrorMessage;
                    launched.Set();
                    if (!e.Success) ended.Set();
                };

                engine.OutputReceived += (s, e) =>
                {
                    lock (gate)
                    {
                        string cat = string.IsNullOrEmpty(e.Category) ? "out" : e.Category;
                        if (json)
                            outw.Write("{\"output\":\"" + CliRunner.EscapeJson(e.Text)
                                + "\",\"category\":\"" + cat + "\"}\n");
                        else
                            outw.Write("[" + cat + "] " + e.Text);
                    }
                };

                engine.Stopped += (s, e) =>
                {
                    int n = Interlocked.Increment(ref hits);
                    List<DebugStackFrame> frames = engine.GetCallStack();
                    DebugStackFrame top = frames.Count > 0 ? frames[0] : null;
                    string locals = top != null ? DescribeLocals(engine, top.Id) : "";
                    string watchStr = (top != null && watches.Count > 0) ? DescribeWatches(engine, top.Id, watches) : "";
                    lock (gate)
                    {
                        if (json)
                        {
                            outw.Write("{\"stop\":" + n
                                + ",\"reason\":\"" + CliRunner.EscapeJson(e.Reason)
                                + "\",\"file\":\"" + CliRunner.EscapeJson(top != null ? top.FilePath : e.FilePath)
                                + "\",\"line\":" + (top != null ? top.Line : e.Line)
                                + ",\"function\":\"" + CliRunner.EscapeJson(top != null ? top.Name : "")
                                + "\",\"locals\":\"" + CliRunner.EscapeJson(locals) + "\""
                                + (watchStr.Length > 0 ? ",\"watch\":\"" + CliRunner.EscapeJson(watchStr) + "\"" : "")
                                + "}\n");
                        }
                        else
                        {
                            outw.Write("stop #" + n + " (" + e.Reason + ") at "
                                + (top != null ? top.FilePath + ":" + top.Line + " in " + top.Name : e.FilePath + ":" + e.Line)
                                + (locals.Length > 0 ? "  {" + locals + "}" : "")
                                + (watchStr.Length > 0 ? "  watch[" + watchStr + "]" : "") + "\n");
                        }
                    }

                    // Bound the session: stepping line-by-line through a loop produces a stop
                    // per line, so cap total stops and stop cleanly when the cap is reached.
                    if (n >= maxStops)
                    {
                        try { engine.StopDebugging(); } catch { }
                        return;
                    }
                    // Step (single-step the chosen way) or run to the next breakpoint.
                    if (stepMode == "over") engine.StepOver();
                    else if (stepMode == "into") engine.StepInto();
                    else if (stepMode == "out") engine.StepOut();
                    else engine.Continue();
                };

                engine.SessionEnded += (s, e) => ended.Set();

                try { engine.StartDebuggingAsync(script); }
                catch (Exception ex)
                {
                    errw.Write("debug: failed to start: " + ex.Message + "\n");
                    return 4;
                }

                if (!ended.Wait(timeoutMs))
                {
                    errw.Write("debug: session timed out after " + timeoutMs + " ms\n");
                    try { engine.StopDebugging(); } catch { }
                    return 5;
                }

                if (!launchOk)
                {
                    errw.Write("debug: launch failed: " + (launchError ?? "unknown") + "\n");
                    return 4;
                }

                errw.Write("debug: interpreter " + interp.ExecutablePath
                    + ", " + requested + " breakpoint(s) requested, " + hits + " stop(s)\n");

                // If breakpoints were requested but none ever bound/hit, the debugger is
                // not functioning — report it as a failure so CI catches a regression.
                if (requested > 0 && hits == 0) return 6;
                return 0;
            }
        }

        private static string DescribeLocals(PythonDebugEngine engine, int frameId)
        {
            List<DebugScope> scopes = engine.GetScopes(frameId);
            foreach (DebugScope sc in scopes)
            {
                if (!string.Equals(sc.Name, "Locals", StringComparison.OrdinalIgnoreCase)) continue;
                List<DebugVariable> vars = engine.GetVariables(sc.VariablesReference);
                List<string> parts = new List<string>();
                foreach (DebugVariable v in vars)
                {
                    // Skip the dunder/special entries debugpy injects; keep user locals.
                    if (v.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                    if (v.Name == "special variables" || v.Name == "function variables") continue;
                    parts.Add(v.Name + "=" + v.Value);
                    if (parts.Count >= 8) break;
                }
                return string.Join(", ", parts);
            }
            return "";
        }

        // Evaluate each --watch expression in the top frame's context and format as
        // "expr=value" pairs. A failed/undefined expression (e.g. a name not yet bound at
        // this stop) is reported as "expr=<error>" rather than dropped, so the watch list
        // stays positional and a typo is visible instead of silently missing.
        private static string DescribeWatches(PythonDebugEngine engine, int frameId, IList<string> watches)
        {
            List<string> parts = new List<string>();
            foreach (string expr in watches)
            {
                DebugVariable v = engine.Evaluate(expr, frameId);
                parts.Add(expr + "=" + (v != null ? v.Value : "<error>"));
            }
            return string.Join(", ", parts);
        }

        // "LINE" → (defaultFile, LINE);  "FILE:LINE" → (FILE, LINE). FILE is resolved
        // relative to the script's directory when not absolute.
        private static bool TryParseBreak(string spec, string script, out string file, out int line)
        {
            file = null; line = 0;
            if (string.IsNullOrEmpty(spec)) return false;

            // Split on the LAST colon: the segment after it must be a positive integer to
            // be a FILE:LINE spec. A bare "10" (no colon) or "C:\f.py:10" both resolve here —
            // the trailing ":10" is the separator; a lone drive colon leaves a non-numeric
            // tail and is rejected, which is correct (a line number is always required).
            int lastColon = spec.LastIndexOf(':');
            string tail = lastColon >= 0 ? spec.Substring(lastColon + 1) : spec;
            int parsed;
            if (!int.TryParse(tail, out parsed) || parsed <= 0) return false;
            line = parsed;

            string pathPart = lastColon >= 0 ? spec.Substring(0, lastColon) : "";
            if (string.IsNullOrEmpty(pathPart))
            {
                file = script; // bare line number → the script being debugged
                return true;
            }
            if (Path.IsPathRooted(pathPart)) file = Path.GetFullPath(pathPart);
            else file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script) ?? ".", pathPart));
            return true;
        }
    }
}
