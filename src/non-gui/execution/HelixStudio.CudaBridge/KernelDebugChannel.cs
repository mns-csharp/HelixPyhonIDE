using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HelixPythonNamespace.CudaEmulator;
using HelixPythonNamespace.CudaVirtualCompiler;
using HelixPythonNamespace.CudaVirtualCompiler.Ast;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.CudaBridge
{
    /// <summary>
    /// The bridge's half of a kernel debug session: a loopback connection back to the IDE, and an
    /// <see cref="IDebugHook"/> that parks one emulator lane at a breakpoint until the IDE says to
    /// go on.
    ///
    /// <para>
    /// <b>The hand-off is free, and that is the whole design.</b> While a kernel is paused here,
    /// the Python program is already stopped — it is sitting in <c>stdout.readline()</c> waiting
    /// for the reply to the <c>launch</c> it sent. Nobody has to suspend it, and nothing can race:
    /// there is exactly one thread of control in the pair, and it is the one holding the pause.
    /// When the user resumes, the launch completes, the reply goes out, and Python carries on under
    /// debugpy as if the kernel had been a slow function call.
    /// </para>
    ///
    /// <para>
    /// <b>Finding the user's file.</b> A kernel arrives here as a string; the program read it with
    /// plain <c>open()</c>, so the path is long gone by the time it reaches the shim. Rather than
    /// plumb a path through an API the user's program does not have, the IDE sends the TEXT of
    /// every file that has a breakpoint in it, and this class looks for that text inside the module
    /// source. A substring match (not equality) is what makes the runtime-assembly style work: a
    /// program that does <c>header + open("k.cu").read()</c> still binds <c>k.cu</c>, at the right
    /// line offset.
    /// </para>
    ///
    /// <para>
    /// A module that matches nothing — a kernel written inline in the <c>.py</c> — is spilled to a
    /// scratch <c>.cu</c> file and reported under that path, so the user can still see where
    /// execution is and set breakpoints for the next launch. Reporting a stop at a location the
    /// IDE cannot open would be worse than not stopping at all.
    /// </para>
    /// </summary>
    internal sealed class KernelDebugChannel : IDebugHook, IDisposable
    {
        private enum Mode { Run, StepInto, StepOver, StepOut }

        /// <summary>One file the IDE has breakpoints in, as the IDE last sent it.</summary>
        private sealed class SourceBinding
        {
            public string Path;
            public string Text;            // newline-normalised
            public int LineCount;
            public readonly Dictionary<int, LineBreakpoint> Lines = new Dictionary<int, LineBreakpoint>();
        }

        /// <summary>
        /// One breakpoint in a kernel file, with whatever the user attached to it.
        ///
        /// <para>
        /// Conditions are what make a kernel breakpoint usable at all. A plain breakpoint in a kernel
        /// launched over a million threads stops in the first lane that reaches it, which is almost
        /// never the one with the bug. <c>i == 1000</c> or <c>threadIdx.x == 37</c> is how you actually
        /// get there.
        /// </para>
        /// </summary>
        private sealed class LineBreakpoint
        {
            public string Condition;       // a CUDA expression, evaluated in the stopped lane's frame
            public string HitCondition;    // "5", ">=3", "%2" -- debugpy's grammar, so both engines agree
            public string LogMessage;      // set => a tracepoint: log and keep going
            public int HitCount;           // per launch-session, counted only when Condition passes
        }

        /// <summary>Where a bound file sits inside one module's source.</summary>
        private sealed class Placement
        {
            public SourceBinding Binding;
            public int LineOffset;         // module line = file line + LineOffset
        }

        private readonly object _mtx = new object();
        private readonly object _sendLock = new object();

        private TcpClient _client;
        private StreamWriter _writer;
        private StreamReader _reader;
        private Thread _readerThread;

        private readonly List<SourceBinding> _bindings = new List<SourceBinding>();
        private readonly Dictionary<string, List<Placement>> _placements =
            new Dictionary<string, List<Placement>>(StringComparer.Ordinal);

        private Dim3 _debugThread = new Dim3(0, 0, 0);
        private Dim3 _debugBlock = new Dim3(0, 0, 0);
        private bool _stopOnKernelEntry;

        private volatile bool _attached;
        private volatile bool _detached;
        private bool _configured;

        // Per-launch pause state.
        private List<Placement> _activePlacements = new List<Placement>();
        private string _activeFallbackPath;
        private Mode _mode = Mode.Run;
        private int _baselineDepth;
        private int _lastStopDepth;
        private int _prevLine = -1;
        private bool _resumeRequested;

        public bool IsAttached { get { return _attached && !_detached; } }
        public Dim3 DebugThread { get { lock (_mtx) return _debugThread; } }
        public Dim3 DebugBlock { get { lock (_mtx) return _debugBlock; } }

        // ---- attach ----------------------------------------------------------

        /// <summary>
        /// Dial the IDE if it asked to be dialled, and block until it has sent its breakpoints.
        ///
        /// <para>
        /// Returns false — quietly — whenever there is no debugger: no port variable, nothing
        /// listening, a refused connection. That is the ordinary case (every non-debug run), and a
        /// bridge that failed loudly here would break running a CUDA program the moment the IDE
        /// was not also debugging it.
        /// </para>
        /// </summary>
        public bool TryAttach()
        {
            string portText = Environment.GetEnvironmentVariable(KernelDebugProtocol.PortVariable);
            int port;
            if (string.IsNullOrEmpty(portText)
                || !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port <= 0 || port > 65535)
                return false;

            try
            {
                _client = new TcpClient();
                _client.Connect("127.0.0.1", port);
                _client.NoDelay = true;
                NetworkStream stream = _client.GetStream();
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _reader = new StreamReader(stream, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                Dispose();
                return false;
            }

            _attached = true;
            _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "helix-cuda-debug" };
            _readerThread.Start();

            Send(new JObject
            {
                ["event"] = KernelDebugProtocol.EventAttached,
                ["emulator"] = CudaCppEmulatorToolchain.DisplayName + " " + CudaCppEmulatorToolchain.Version,
                ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
            });

            // Wait for "go". On timeout we run undebugged rather than hang: the user's program
            // must not be held hostage by a debugger that failed to finish its handshake.
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(KernelDebugProtocol.ConfigureTimeoutMs);
            lock (_mtx)
            {
                while (!_configured && !_detached && DateTime.UtcNow < deadline)
                    Monitor.Wait(_mtx, 250);
            }
            return _attached && !_detached;
        }

        // ---- IDE messages ----------------------------------------------------

        private void ReadLoop()
        {
            try
            {
                string line;
                while ((line = _reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    JObject message;
                    try { message = JObject.Parse(line); }
                    catch (Exception) { continue; }
                    Handle(message);
                }
            }
            catch (Exception)
            {
                // Socket closed, IDE gone, process shutting down. Falls through to the release
                // below, which is the point: a lane blocked on a pause must never outlive the
                // debugger that was going to resume it.
            }
            lock (_mtx)
            {
                _detached = true;
                _resumeRequested = true;
                Monitor.PulseAll(_mtx);
            }
        }

        private void Handle(JObject message)
        {
            string command = (string)message["cmd"] ?? string.Empty;
            switch (command)
            {
                case KernelDebugProtocol.CmdConfigure:
                    ApplyConfiguration(message);
                    break;

                case KernelDebugProtocol.CmdBreakpoints:
                    ApplyBreakpoints(message);
                    break;

                case KernelDebugProtocol.CmdGo:
                    lock (_mtx) { _configured = true; Monitor.PulseAll(_mtx); }
                    break;

                case KernelDebugProtocol.CmdContinue: Resume(Mode.Run); break;
                case KernelDebugProtocol.CmdStepInto: Resume(Mode.StepInto); break;
                case KernelDebugProtocol.CmdStepOver: Resume(Mode.StepOver); break;
                case KernelDebugProtocol.CmdStepOut: Resume(Mode.StepOut); break;

                case KernelDebugProtocol.CmdStop:
                    lock (_mtx)
                    {
                        _detached = true;
                        _resumeRequested = true;
                        Monitor.PulseAll(_mtx);
                    }
                    break;
            }
        }

        private void ApplyConfiguration(JObject message)
        {
            lock (_mtx)
            {
                _debugThread = ReadDim(message["thread"], _debugThread);
                _debugBlock = ReadDim(message["block"], _debugBlock);
                if (message["stopOnKernelEntry"] != null)
                    _stopOnKernelEntry = (bool)message["stopOnKernelEntry"];
            }
            ApplyBreakpoints(message);
        }

        /// <summary>
        /// Replace the breakpoint set wholesale. Wholesale, not incrementally, because the IDE's
        /// breakpoint list is the truth and a missed "removed" message would leave a breakpoint
        /// that the user can see is gone and that still stops the program.
        /// </summary>
        private void ApplyBreakpoints(JObject message)
        {
            var files = message["files"] as JArray;
            if (files == null) return;

            var fresh = new List<SourceBinding>();
            foreach (JToken token in files)
            {
                var item = token as JObject;
                if (item == null) continue;
                string path = (string)item["path"];
                if (string.IsNullOrEmpty(path)) continue;

                var binding = new SourceBinding { Path = path, Text = Normalise((string)item["text"]) };
                binding.LineCount = CountLines(binding.Text);
                // Two shapes accepted: a bare line number, or an object carrying what the user
                // attached to it. The bare form is kept so an older IDE build still binds plain
                // breakpoints against a newer bridge.
                var lines = item["lines"] as JArray;
                if (lines != null)
                    foreach (JToken lineToken in lines)
                    {
                        int line;
                        var asObject = lineToken as JObject;
                        if (asObject != null)
                        {
                            if (asObject["line"] == null) continue;
                            line = (int)asObject["line"];
                            if (line <= 0) continue;
                            binding.Lines[line] = new LineBreakpoint
                            {
                                Condition = Blank((string)asObject["condition"]),
                                HitCondition = Blank((string)asObject["hit"]),
                                LogMessage = Blank((string)asObject["log"]),
                            };
                        }
                        else if (int.TryParse(lineToken.ToString(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out line) && line > 0)
                        {
                            binding.Lines[line] = new LineBreakpoint();
                        }
                    }
                fresh.Add(binding);
            }

            lock (_mtx)
            {
                _bindings.Clear();
                _bindings.AddRange(fresh);
                // Placements are derived from the bindings, so they are stale the moment the
                // bindings change. Recomputed on the next launch, which is cheap and always right.
                _placements.Clear();
            }
        }

        private static string Blank(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        // ---- conditions ------------------------------------------------------

        /// <summary>
        /// A condition compiled once and reused. <see cref="Error"/> non-null means it would not
        /// compile; the breakpoint then stops ANYWAY and reports the error, because silently never
        /// stopping at a breakpoint the user can see in the margin is the worse failure.
        /// </summary>
        private sealed class CompiledCondition
        {
            public Expr Expression;
            public string Error;
        }

        private readonly Dictionary<string, CompiledCondition> _conditions =
            new Dictionary<string, CompiledCondition>(StringComparer.Ordinal);

        /// <summary>
        /// Compile a breakpoint condition into a real CUDA expression.
        ///
        /// <para>
        /// It is wrapped in a synthetic device function and run through the ordinary compiler rather
        /// than matched with a hand-written comparator. That costs a few lines and buys the real
        /// thing: <c>i == 1000</c>, <c>threadIdx.x % 32 == 7</c>, <c>data[i] &lt; 0</c> and
        /// <c>blockIdx.x == 2 &amp;&amp; i &gt; 100</c> all mean exactly what they would mean if you
        /// typed them into the kernel. A comparator that supported "name op number" would quietly
        /// disagree with the kernel's own arithmetic the first time someone wrote something else.
        /// </para>
        /// </summary>
        private CompiledCondition CompileCondition(string condition)
        {
            lock (_mtx)
            {
                CompiledCondition cached;
                if (_conditions.TryGetValue(condition, out cached)) return cached;
            }

            var result = new CompiledCondition();
            try
            {
                string wrapper = "__device__ int __helix_breakpoint_condition() { return ("
                    + condition + "); }";
                CudaCompileResult compiled =
                    CudaVirtualCompiler.CudaVirtualCompiler.CompileSource(wrapper, "<condition>");

                string diagnostic = FirstError(compiled);
                if (diagnostic != null) result.Error = diagnostic;
                else
                {
                    Expr expression = FindReturnedExpression(compiled);
                    if (expression == null) result.Error = "the condition is not an expression";
                    else result.Expression = expression;
                }
            }
            catch (Exception ex) { result.Error = ex.Message; }

            lock (_mtx) { _conditions[condition] = result; }
            return result;
        }

        private static string FirstError(CudaCompileResult compiled)
        {
            if (compiled == null || compiled.Diagnostics == null) return "the condition did not compile";
            foreach (CudaCompileDiagnostic diagnostic in compiled.Diagnostics)
                if (diagnostic.Severity >= HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error)
                    return diagnostic.Message;
            return null;
        }

        private static Expr FindReturnedExpression(CudaCompileResult compiled)
        {
            if (compiled == null || compiled.Unit == null) return null;
            foreach (Decl declaration in compiled.Unit.Decls)
            {
                var function = declaration as FunctionDecl;
                if (function == null) continue;
                var body = function.Body as CompoundStmt;
                if (body == null) continue;
                foreach (Stmt statement in body.Body)
                {
                    var returned = statement as ReturnStmt;
                    if (returned != null && returned.Value != null) return returned.Value;
                }
            }
            return null;
        }

        /// <summary>
        /// Decide whether this hit should stop, in the lane that reached it. Returns false to run on.
        /// <paramref name="note"/> carries a tracepoint's text, or the reason a condition could not be
        /// judged.
        /// </summary>
        private bool ShouldStopAt(LineBreakpoint breakpoint, Interpreter interpreter, out string note)
        {
            note = null;
            if (breakpoint == null) return false;

            if (breakpoint.Condition != null)
            {
                CompiledCondition condition = CompileCondition(breakpoint.Condition);
                if (condition.Error != null)
                {
                    note = "breakpoint condition '" + breakpoint.Condition + "' could not be used ("
                         + condition.Error + "); stopping anyway.";
                    return true;
                }
                try
                {
                    if (!interpreter.Eval(condition.Expression).ToBool()) return false;
                }
                catch (Exception ex)
                {
                    // Names that do not exist in THIS frame, an out-of-range index: report it at the
                    // breakpoint rather than swallowing it, or the user is left wondering why a
                    // condition they can read never fires.
                    note = "breakpoint condition '" + breakpoint.Condition + "' failed to evaluate ("
                         + ex.Message + "); stopping anyway.";
                    return true;
                }
            }

            // Counted only for hits that passed the condition, so "the 5th time i == 1000" means what
            // it reads as rather than "the 5th time this line ran in any lane".
            breakpoint.HitCount++;
            if (breakpoint.HitCondition != null
                && !HitConditionSatisfied(breakpoint.HitCondition, breakpoint.HitCount))
                return false;

            if (breakpoint.LogMessage != null)
            {
                note = Interpolate(breakpoint.LogMessage, interpreter);
                return false;                      // a tracepoint logs and runs on
            }
            return true;
        }

        /// <summary>
        /// debugpy's hit-count grammar: an optional comparison then a number, a bare number meaning
        /// the Nth hit. The same grammar the Python side uses, deliberately — one breakpoint dialog
        /// should not mean two things depending on which file it is in.
        /// </summary>
        private static bool HitConditionSatisfied(string rule, int hitCount)
        {
            string text = rule.Trim();
            string op = "==";
            foreach (string candidate in new[] { "==", "!=", ">=", "<=", ">", "<", "%" })
                if (text.StartsWith(candidate, StringComparison.Ordinal))
                {
                    op = candidate;
                    text = text.Substring(candidate.Length).Trim();
                    break;
                }
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;                       // unreadable rule: stop, do not silently skip
            switch (op)
            {
                case "!=": return hitCount != value;
                case ">=": return hitCount >= value;
                case "<=": return hitCount <= value;
                case ">": return hitCount > value;
                case "<": return hitCount < value;
                case "%": return value != 0 && hitCount % value == 0;
                default: return hitCount == value;
            }
        }

        /// <summary>Replace each <c>{expr}</c> in a tracepoint message with its value in this lane.</summary>
        private string Interpolate(string message, Interpreter interpreter)
        {
            var text = new StringBuilder();
            int index = 0;
            while (index < message.Length)
            {
                int open = message.IndexOf('{', index);
                if (open < 0) { text.Append(message, index, message.Length - index); break; }
                int close = message.IndexOf('}', open + 1);
                if (close < 0) { text.Append(message, index, message.Length - index); break; }

                text.Append(message, index, open - index);
                string expression = message.Substring(open + 1, close - open - 1).Trim();
                CompiledCondition compiled = CompileCondition(expression);
                if (compiled.Error != null) text.Append('{').Append(expression).Append('}');
                else
                {
                    try { text.Append(interpreter.Eval(compiled.Expression).ToString()); }
                    catch (Exception) { text.Append('{').Append(expression).Append('}'); }
                }
                index = close + 1;
            }
            return text.ToString();
        }

        private static Dim3 ReadDim(JToken token, Dim3 fallback)
        {
            var array = token as JArray;
            if (array == null) return fallback;
            ulong x = array.Count > 0 ? (ulong)array[0] : 0UL;
            ulong y = array.Count > 1 ? (ulong)array[1] : 0UL;
            ulong z = array.Count > 2 ? (ulong)array[2] : 0UL;
            return new Dim3(x, y, z);
        }

        // ---- launch lifecycle ------------------------------------------------

        /// <summary>
        /// Called by the session just before a kernel runs. Returns the hook to install, or null
        /// when there is nothing to stop for — in which case the launch runs at full speed with no
        /// per-statement callback at all.
        /// </summary>
        public IDebugHook BeginKernel(string moduleHandle, string moduleSource, string kernelName,
            Dim3 grid, Dim3 block)
        {
            if (!IsAttached) return null;

            List<Placement> placements = PlacementsFor(moduleHandle, moduleSource);
            bool anyBreakpoint = false;
            lock (_mtx)
            {
                foreach (Placement placement in placements)
                    if (placement.Binding.Lines.Count > 0) { anyBreakpoint = true; break; }

                // Hit counts are per LAUNCH. "Stop the 5th time" means the fifth time in this kernel
                // call, not the fifth since the program started -- a program that launches the same
                // kernel in a loop would otherwise fire once and never again.
                foreach (Placement placement in placements)
                    foreach (LineBreakpoint breakpoint in placement.Binding.Lines.Values)
                        breakpoint.HitCount = 0;

                _activePlacements = placements;
                _mode = _stopOnKernelEntry ? Mode.StepInto : Mode.Run;
                _prevLine = -1;
                _baselineDepth = 0;
                _lastStopDepth = 0;
                _resumeRequested = false;
                if (!anyBreakpoint && !_stopOnKernelEntry) return null;
            }

            Send(new JObject
            {
                ["event"] = KernelDebugProtocol.EventKernelEntered,
                ["kernel"] = kernelName,
                ["module"] = moduleHandle,
                ["path"] = _activeFallbackPath,
                ["grid"] = new JArray((long)grid.X, (long)grid.Y, (long)grid.Z),
                ["block"] = new JArray((long)block.X, (long)block.Y, (long)block.Z),
            });
            return this;
        }

        public void EndKernel(string kernelName)
        {
            if (!IsAttached) return;
            Send(new JObject
            {
                ["event"] = KernelDebugProtocol.EventKernelExited,
                ["kernel"] = kernelName,
            });
        }

        // ---- source mapping --------------------------------------------------

        private List<Placement> PlacementsFor(string moduleHandle, string moduleSource)
        {
            lock (_mtx)
            {
                List<Placement> cached;
                if (_placements.TryGetValue(moduleHandle ?? string.Empty, out cached))
                {
                    // Refreshed on every hit, not just on the first: the kernelEntered event
                    // carries this path, and a cached module that reported the FIRST module's file
                    // would send the IDE to the wrong source.
                    _activeFallbackPath = cached.Count > 0 ? cached[0].Binding.Path : null;
                    return cached;
                }
            }

            string normalised = Normalise(moduleSource);
            var result = new List<Placement>();
            List<SourceBinding> bindings;
            lock (_mtx) { bindings = new List<SourceBinding>(_bindings); }

            foreach (SourceBinding binding in bindings)
            {
                if (string.IsNullOrEmpty(binding.Text)) continue;
                int index = normalised.IndexOf(binding.Text, StringComparison.Ordinal);
                if (index < 0) continue;
                result.Add(new Placement
                {
                    Binding = binding,
                    LineOffset = CountLineBreaks(normalised, index),
                });
            }

            // Nothing matched: the kernel was written inline in the .py, so give it a file of its
            // own that the IDE can actually open.
            string fallback = result.Count == 0 ? SpillToScratchFile(moduleHandle, normalised) : null;
            if (fallback != null)
            {
                SourceBinding scratch = null;
                lock (_mtx)
                {
                    foreach (SourceBinding binding in _bindings)
                        if (string.Equals(binding.Path, fallback, StringComparison.OrdinalIgnoreCase))
                        { scratch = binding; break; }
                }
                if (scratch == null)
                {
                    scratch = new SourceBinding
                    {
                        Path = fallback,
                        Text = normalised,
                        LineCount = CountLines(normalised),
                    };
                    lock (_mtx) { _bindings.Add(scratch); }
                }
                result.Add(new Placement { Binding = scratch, LineOffset = 0 });
            }

            lock (_mtx)
            {
                _placements[moduleHandle ?? string.Empty] = result;
                _activeFallbackPath = result.Count > 0 ? result[0].Binding.Path : null;
            }
            return result;
        }

        /// <summary>
        /// Write an inline kernel next to the user's other scratch files under
        /// <c>%TEMP%\HelixPython\kernels</c>, named by a hash of its text so the same kernel keeps
        /// the same path across runs — which is what lets a breakpoint set in it survive a restart.
        /// </summary>
        private static string SpillToScratchFile(string moduleHandle, string source)
        {
            try
            {
                string directory = Path.Combine(Path.GetTempPath(), "HelixPython", "kernels");
                Directory.CreateDirectory(directory);
                string name;
                using (var sha = SHA1.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source ?? string.Empty));
                    name = BitConverter.ToString(hash, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
                }
                string path = Path.Combine(directory, "kernel_" + name + ".cu");
                if (!File.Exists(path)) File.WriteAllText(path, source ?? string.Empty, new UTF8Encoding(false));
                return path;
            }
            catch (Exception)
            {
                return null;   // read-only temp, disk full: debug without a file rather than crash
            }
        }

        private static string Normalise(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int lines = 1;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lines++;
            return lines;
        }

        private static int CountLineBreaks(string text, int upTo)
        {
            int count = 0;
            for (int i = 0; i < upTo && i < text.Length; i++) if (text[i] == '\n') count++;
            return count;
        }

        /// <summary>Module line -> (user file, line in that file). Null when it maps nowhere.</summary>
        private Placement MapLine(int moduleLine, out int fileLine)
        {
            fileLine = 0;
            List<Placement> placements;
            lock (_mtx) { placements = _activePlacements; }
            if (placements == null) return null;
            foreach (Placement placement in placements)
            {
                int candidate = moduleLine - placement.LineOffset;
                if (candidate >= 1 && candidate <= placement.Binding.LineCount)
                {
                    fileLine = candidate;
                    return placement;
                }
            }
            return null;
        }

        // ---- IDebugHook (runs on the emulator's lane thread) -----------------

        public void OnStatement(Stmt statement, int callDepth, Interpreter interpreter)
        {
            if (_detached) return;
            if (statement == null) return;
            // A brace is not a place to stop. The interpreter visits the function body's
            // CompoundStmt before anything in it, so break-on-entry would otherwise land the user
            // on "{" -- a line with nothing to look at and no variables in scope yet.
            if (statement is CompoundStmt || statement is EmptyStmt) return;

            int moduleLine = statement.Span.Start.Line;
            int fileLine;
            Placement placement = MapLine(moduleLine, out fileLine);

            LineBreakpoint breakpoint = null;
            if (placement != null) placement.Binding.Lines.TryGetValue(fileLine, out breakpoint);

            // A conditional breakpoint is only a hit when its condition holds IN THIS LANE. The
            // condition runs before the same-line and step checks below, because it is the thing
            // that decides whether this thread is the interesting one at all.
            bool hit = false;
            if (breakpoint != null)
            {
                string note;
                hit = ShouldStopAt(breakpoint, interpreter, out note);
                if (note != null)
                    Send(new JObject
                    {
                        ["event"] = KernelDebugProtocol.EventOutput,
                        ["text"] = note + Environment.NewLine,
                    });
            }

            bool sameLine = moduleLine == _prevLine;
            _prevLine = moduleLine;

            bool shouldStop;
            lock (_mtx)
            {
                switch (_mode)
                {
                    case Mode.StepInto: shouldStop = true; break;
                    case Mode.StepOver: shouldStop = callDepth <= _baselineDepth || hit; break;
                    case Mode.StepOut: shouldStop = callDepth < _baselineDepth || hit; break;
                    default: shouldStop = hit; break;
                }
            }
            if (sameLine) shouldStop = false;
            if (!shouldStop) return;

            JObject stopped = new JObject
            {
                ["event"] = KernelDebugProtocol.EventStopped,
                ["reason"] = hit ? "breakpoint" : "step",
                ["path"] = placement != null ? placement.Binding.Path : null,
                ["line"] = placement != null ? fileLine : moduleLine,
                ["frames"] = BuildFrames(interpreter),
            };
            lock (_mtx) { _lastStopDepth = callDepth; }
            Send(stopped);

            lock (_mtx)
            {
                while (!_resumeRequested && !_detached) Monitor.Wait(_mtx);
                _resumeRequested = false;
            }
            // Detached while paused -- the IDE stopped, or the socket died. The lane is RELEASED,
            // not aborted: the kernel finishes, the launch replies, and the Python program keeps
            // its correct results. A debugger going away must never be able to break a program
            // that was running fine.
            if (_detached) return;
            Send(new JObject { ["event"] = KernelDebugProtocol.EventResumed });
        }

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

        /// <summary>
        /// The call stack and every local, as a snapshot taken at the pause point. Sent with the
        /// stop rather than fetched afterwards: a kernel frame holds a handful of scalars, and one
        /// round trip per variable over a socket would cost more than the values are worth.
        /// </summary>
        private JArray BuildFrames(Interpreter interpreter)
        {
            var frames = new JArray();
            if (interpreter == null) return frames;

            List<Frame> stack = interpreter.Frames;
            for (int i = 0; i < stack.Count; i++)
            {
                Frame frame = stack[stack.Count - 1 - i];     // innermost first
                int moduleLine = frame.CurrentStmt != null ? frame.CurrentStmt.Span.Start.Line : 0;
                int fileLine;
                Placement placement = MapLine(moduleLine, out fileLine);

                var variables = new JArray();
                for (int scope = 0; scope < frame.Scopes.Count; scope++)
                    foreach (KeyValuePair<string, Cell> local in frame.Scopes[scope].Locals)
                        variables.Add(new JObject
                        {
                            ["name"] = local.Key,
                            ["value"] = local.Value.Value != null ? local.Value.Value.ToString() : string.Empty,
                            ["type"] = local.Value.DeclType != null ? local.Value.DeclType.ToString() : string.Empty,
                        });

                // The coordinates are why anyone debugs a kernel at all: "which thread am I?" is
                // the first question, and without these the locals are a puzzle with no key.
                if (frame.Thread != null)
                {
                    AddCoordinate(variables, "threadIdx", frame.Thread.ThreadIdx);
                    AddCoordinate(variables, "blockIdx", frame.Thread.BlockIdx);
                    AddCoordinate(variables, "blockDim", frame.Thread.BlockDim);
                    AddCoordinate(variables, "gridDim", frame.Thread.GridDim);
                }

                frames.Add(new JObject
                {
                    ["id"] = i,
                    ["name"] = frame.Func != null ? frame.Func.Name : "<kernel>",
                    ["path"] = placement != null ? placement.Binding.Path : null,
                    ["line"] = placement != null ? fileLine : moduleLine,
                    ["column"] = frame.CurrentStmt != null ? frame.CurrentStmt.Span.Start.Column : 0,
                    ["vars"] = variables,
                });
            }
            return frames;
        }

        private static void AddCoordinate(JArray target, string name, Dim3 value)
        {
            target.Add(new JObject
            {
                ["name"] = name,
                ["value"] = "(" + value.X.ToString(CultureInfo.InvariantCulture) + ","
                          + value.Y.ToString(CultureInfo.InvariantCulture) + ","
                          + value.Z.ToString(CultureInfo.InvariantCulture) + ")",
                ["type"] = "uint3",
            });
        }

        // ---- transport -------------------------------------------------------

        private void Send(JObject message)
        {
            StreamWriter writer = _writer;
            if (writer == null) return;
            try
            {
                lock (_sendLock) { writer.WriteLine(message.ToString(Newtonsoft.Json.Formatting.None)); }
            }
            catch (Exception)
            {
                lock (_mtx)
                {
                    _detached = true;
                    _resumeRequested = true;
                    Monitor.PulseAll(_mtx);
                }
            }
        }

        public void Dispose()
        {
            lock (_mtx)
            {
                _detached = true;
                _resumeRequested = true;
                Monitor.PulseAll(_mtx);
            }
            try { if (_attached) Send(new JObject { ["event"] = KernelDebugProtocol.EventDetached }); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _writer = null;
            _reader = null;
            _client = null;
            _attached = false;
        }
    }
}
