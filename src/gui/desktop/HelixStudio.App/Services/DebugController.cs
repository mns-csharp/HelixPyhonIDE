using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using HelixPythonNamespace.Debugger;
using HelixPythonNamespace.CudaEmulator;   // Dim3
using HelixPythonNamespace.Core.Extensibility;

namespace HelixPythonNamespace.App.Services
{
    /// <summary>
    /// Wraps the debug engines for the App. Exposes simple Start/Stop/Step methods, marshals engine
    /// events onto the UI thread, and surfaces error messages when a backend is unavailable.
    ///
    /// It drives three engines behind the shared <see cref="IDebugEngine"/> surface, chosen by the
    /// file being debugged: <see cref="PythonDebugEngine"/> (debugpy) for <c>.py</c>,
    /// <see cref="CudaEmulatorDebugEngine"/> (CPU SIMT emulator) for <c>.cu</c>/<c>.cuh</c>, and
    /// <see cref="CppDebugEngine"/> (gdb/MI) for the native sources the Convert-to-C/C++ commands
    /// emit. All three share a single <see cref="BreakpointManager"/>, so breakpoints toggled in
    /// the tabbed editor feed whichever engine runs — that is what lets a CUDA kernel debug from
    /// the same editor and Debug-panel position as a Python script.
    ///
    /// The Rust, C#, Java and Julia engines ship in their own Helix products.
    /// </summary>
    public sealed class DebugController : IDisposable
    {
        private readonly Form _owner;
        private readonly PythonInterpreterManager _interpreters;
        private readonly BreakpointManager _breakpoints;
        private PythonDebugEngine _pyEngine;
        private CppDebugEngine _cppEngine;
        private CudaEmulatorDebugEngine _cudaEngine;
        private CudaGdbDebugEngine _cudaGdbEngine;
        private CudaKernelDebugSession _kernelSession;
        private IDebugEngine _active;   // whichever engine owns the current/last session
        private readonly ContributionRegistry<DebugLaunchContribution> _launchers =
            new ContributionRegistry<DebugLaunchContribution>();
        private bool _disposed;

        // Bug-A failure tracking (silent debug-session failure): set at the top of every non-Python
        // Start<Lang> method, updated by each Ensure<Lang>Engine()'s event wiring, and consulted right
        // before re-raising SessionEnded so a session that never stopped anywhere raises
        // EngineFailedToStart with the last thing the engine said (usually the tool-absence reason).
        private bool _stoppedThisSession;
        private string _lastEngineOutput;

        public event EventHandler<string> OutputReceived;     // text from stdout/stderr
        public event EventHandler<DebugStoppedEventArgs> Stopped;
        public event EventHandler SessionEnded;

        /// <summary>
        /// Raised when a non-Python debug session ends WITHOUT ever stopping at a breakpoint — e.g. the
        /// external tool (netcoredbg/gdb/lldb/jdb/julia) is missing, or the build failed. Carries the last
        /// engine output (usually the honest tool-absence/build-failure reason) so the UI can show the user
        /// something more actionable than a silently-vanished session. Python is excluded — StartPython
        /// already pops a synchronous MessageBox for its one failure mode (no interpreter found).
        /// </summary>
        public event EventHandler<string> EngineFailedToStart;

        public BreakpointManager Breakpoints { get { return _breakpoints; } }

        /// <summary>
        /// The engine the user's next Step or Continue should drive.
        ///
        /// <para>
        /// Normally that is <see cref="_active"/> — the engine that started the session. It is the
        /// kernel co-session instead for exactly as long as a kernel is paused, which is the whole
        /// of the hand-off: while the emulator holds a lane at a breakpoint, the Python program is
        /// blocked on the reply to its own launch, so there is no ambiguity about who is stopped.
        /// When the kernel resumes, control returns here by itself.
        /// </para>
        /// </summary>
        private IDebugEngine Current
        {
            get
            {
                CudaKernelDebugSession kernel = _kernelSession;
                if (kernel != null && kernel.IsPaused) return kernel;
                return _active;
            }
        }

        /// <summary>The kernel co-session for the current run, or null when one is not attached.</summary>
        public CudaKernelDebugSession KernelSession { get { return _kernelSession; } }

        /// <summary>True while the stop the user is looking at is inside a CUDA kernel.</summary>
        public bool IsPausedInKernel
        {
            get { CudaKernelDebugSession k = _kernelSession; return k != null && k.IsPaused; }
        }

        public bool IsDebugging
        {
            get
            {
                if (_active != null && _active.IsDebugging) return true;
                CudaKernelDebugSession k = _kernelSession;
                return k != null && k.IsPaused;
            }
        }

        public bool IsPaused
        {
            get
            {
                CudaKernelDebugSession k = _kernelSession;
                if (k != null && k.IsPaused) return true;
                return _active != null && _active.IsPaused;
            }
        }

        /// <summary>
        /// debugpy exception-breakpoint filters armed at the next <see cref="Start"/>: any of
        /// "raised" / "uncaught" / "userUnhandled" (null/empty = don't break on exceptions).
        /// Drives the Debug → Break on Exceptions menu. Applied at session start (debugpy reads
        /// the filter set during the DAP configuration phase), so a change takes effect on the
        /// next run, not mid-session. Python-only.
        /// </summary>
        public IList<string> ExceptionBreakFilters { get; set; }

        /// <summary>
        /// When true, the next <see cref="Start"/> pauses on the script's first executable line.
        /// debugpy ignores <c>stopOnEntry</c> on the attach we use, so this is realized by
        /// resolving the entry line (<see cref="EntryLineResolver"/>) and arming the engine's
        /// synthetic <c>EntryBreakpointLine</c> — which never touches the user's breakpoints.
        /// Drives the Debug → Break on Entry menu. Python-only.
        /// </summary>
        public bool StopOnEntry { get; set; }

        /// <summary>
        /// Extra PYTHONPATH (platform-separator-joined) applied to the next Python debug session so a
        /// script importing sibling workspace packages resolves them. Set by the coordinator from the
        /// solution's project roots. Ignored for CUDA sessions.
        /// </summary>
        public string ExtraPythonPath { get; set; }

        /// <summary>
        /// The debuggee's own command line — the launched package's
        /// <c>[tool.helixstudio].run-args</c>, verbatim as the user typed it. Set by
        /// <c>DebugCoordinator.ApplyWorkspaceLaunchSettings</c> before each Start so Debug launches
        /// the program with the same argv Run does. Null/empty means no arguments.
        /// </summary>
        public string ScriptArguments { get; set; }

        /// <summary>
        /// The debuggee's working directory — the launched script's PROJECT folder (Visual Studio's
        /// default), so relative paths behave identically under Debug and Run. Null falls back to
        /// the script's own directory.
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// The (block, thread) lane the CUDA emulator single-steps. Defaults to lane 0.
        ///
        /// A kernel runs thousands of threads over the same source lines, so "step" has no meaning
        /// until one lane is named — stepping every lane at once would stop on the same line
        /// thousands of times. Settable so a lane picker can choose before a <c>.cu</c> session
        /// starts.
        /// </summary>
        public Dim3 CudaDebugBlock { get; set; }

        /// <summary>The thread within <see cref="CudaDebugBlock"/> the emulator steps.</summary>
        public Dim3 CudaDebugThread { get; set; }

        public DebugController(Form owner)
        {
            _owner = owner;
            _interpreters = new PythonInterpreterManager();
            _breakpoints = new BreakpointManager();
            CudaDebugBlock = new Dim3(0, 0, 0);
            CudaDebugThread = new Dim3(0, 0, 0);
            EnsurePyEngine();
            RegisterDebugLauncher("cpp", IsCppFile, StartCpp, 100);
            // Above C++ (200 > 100): a .cu is C++ to the native toolchain, so the C++ launcher
            // would otherwise claim it and hand a CUDA kernel to gdb, which cannot run one.
            RegisterDebugLauncher("cuda", IsCudaFile, StartCuda, 200);
            RegisterDebugLauncher("python", path => true, StartPython, -1000);
        }

        /// <summary>True when the path names a CUDA source rather than a script or plain C++.</summary>
        private static bool IsCudaFile(string path)
        {
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".cu", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".cuh", StringComparison.OrdinalIgnoreCase);
        }

        public void RegisterDebugLauncher(string id, Func<string, bool> supportsFile,
            Action<string> start, int priority = 0)
        {
            _launchers.Register(id, new DebugLaunchContribution(id, supportsFile, start), priority);
        }

        public string DebugLauncherForFile(string filePath)
        {
            DebugLaunchContribution launcher = _launchers.FirstOrDefault(c => c.Supports(filePath));
            return launcher != null ? launcher.Id : null;
        }

        // True when the path names a plain (non-CUDA) C/C++ source or header — debugged via gdb/lldb over a
        // gcc/g++ (or CMake-GCC) built binary. Checked AFTER IsCudaFile in Start() so .cu/.cuh never lands here.
        internal static bool IsCppFile(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".c":
                case ".cpp":
                case ".cc":
                case ".cxx":
                case ".h":
                case ".hpp":
                case ".hh":
                case ".hxx":
                    return true;
                default:
                    return false;
            }
        }

        private void EnsureCppEngine()
        {
            if (_cppEngine != null) return;
            _cppEngine = new CppDebugEngine();
            _cppEngine.Stopped += (s, e) => { _stoppedThisSession = true; MarshalEvent(() => Stopped?.Invoke(this, e)); };
            _cppEngine.OutputReceived += (s, e) => { _lastEngineOutput = e.Text; MarshalEvent(() => OutputReceived?.Invoke(this, e.Text)); };
            _cppEngine.SessionEnded += (s, e) =>
            {
                if (!_stoppedThisSession) MarshalEvent(() => EngineFailedToStart?.Invoke(this, _lastEngineOutput ?? "Debug session ended before any breakpoint was hit."));
                MarshalEvent(() => SessionEnded?.Invoke(this, EventArgs.Empty));
            };
        }

        // Lazily create the CUDA emulator engine, sharing the SAME BreakpointManager as Python and
        // C++ so a breakpoint toggled in the editor feeds whichever engine runs. Same event
        // re-raising as the others, so the Debug panel, the Variables grid and the editor's
        // current-line marker behave identically whether the session is debugpy, gdb or the SIMT
        // emulator — that shared position is the whole point of routing CUDA through IDebugEngine.
        private void EnsureCudaEngine()
        {
            if (_cudaEngine != null) return;
            _cudaEngine = new CudaEmulatorDebugEngine(_breakpoints);
            _cudaEngine.Stopped += (s, e) => { _stoppedThisSession = true; MarshalEvent(() => Stopped?.Invoke(this, e)); };
            _cudaEngine.OutputReceived += (s, e) => { _lastEngineOutput = e.Text; MarshalEvent(() => OutputReceived?.Invoke(this, e.Text)); };
            _cudaEngine.SessionEnded += (s, e) =>
            {
                if (!_stoppedThisSession) MarshalEvent(() => EngineFailedToStart?.Invoke(this, _lastEngineOutput ?? "Debug session ended before any breakpoint was hit."));
                MarshalEvent(() => SessionEnded?.Invoke(this, EventArgs.Empty));
            };
        }

        /// <summary>
        /// Which debugger a <c>.cu</c> file gets. Hard rule 9 asks for both, so this is a real choice
        /// rather than a fallback: the emulator runs the kernel on the CPU and can stop anywhere, and
        /// cuda-gdb runs it on the actual GPU and tells you what the hardware really did.
        /// </summary>
        public enum CudaDebugBackend
        {
            /// <summary>Follow the solution's CUDA toolchain: real GPU when one is selected, emulator otherwise.</summary>
            Auto = 0,
            /// <summary>Always the CPU SIMT emulator.</summary>
            Emulator = 1,
            /// <summary>Always cuda-gdb on the real GPU.</summary>
            CudaGdb = 2,
        }

        /// <summary>
        /// Which backend <see cref="StartCuda"/> uses. Set by the coordinator from the solution's CUDA
        /// toolchain so Debug and Run agree about which CUDA a project is being developed against.
        /// </summary>
        public CudaDebugBackend CudaBackend { get; set; }

        /// <summary>
        /// True when the solution's toolchain is a real GPU one. Only consulted for
        /// <see cref="CudaDebugBackend.Auto"/>; set alongside <see cref="CudaBackend"/>.
        /// </summary>
        public bool CudaToolchainIsRealGpu { get; set; }

        // Lazily create the cuda-gdb engine. Shares the BreakpointManager like every other engine, so
        // the same breakpoint in the same .cu file drives either backend.
        private void EnsureCudaGdbEngine()
        {
            if (_cudaGdbEngine != null) return;
            _cudaGdbEngine = new CudaGdbDebugEngine();
            _cudaGdbEngine.Stopped += (s, e) => { _stoppedThisSession = true; MarshalEvent(() => Stopped?.Invoke(this, e)); };
            _cudaGdbEngine.OutputReceived += (s, e) => { _lastEngineOutput = e.Text; MarshalEvent(() => OutputReceived?.Invoke(this, e.Text)); };
            _cudaGdbEngine.SessionEnded += (s, e) =>
            {
                if (!_stoppedThisSession) MarshalEvent(() => EngineFailedToStart?.Invoke(this, _lastEngineOutput ?? "Debug session ended before any breakpoint was hit."));
                MarshalEvent(() => SessionEnded?.Invoke(this, EventArgs.Empty));
            };
        }

        /// <summary>
        /// Resolve <see cref="CudaBackend"/> to the backend that will actually run. Separated from
        /// <see cref="StartCuda"/> so the decision is testable without launching a debugger.
        /// </summary>
        internal CudaDebugBackend ResolveCudaBackend(out string fallbackNotice)
        {
            fallbackNotice = null;
            CudaDebugBackend wanted = CudaBackend;
            if (wanted == CudaDebugBackend.Auto)
                wanted = CudaToolchainIsRealGpu ? CudaDebugBackend.CudaGdb : CudaDebugBackend.Emulator;

            if (wanted != CudaDebugBackend.CudaGdb) return wanted;
            if (CudaGdbDebugEngine.IsAvailable) return CudaDebugBackend.CudaGdb;

            // Asked for the real GPU and it is not here. Fall back to the emulator rather than refusing
            // to debug -- but SAY SO. A silent fallback would have the user reading emulator behaviour
            // as hardware behaviour, which is the one confusion this pair of backends exists to avoid.
            fallbackNotice = "Real-GPU kernel debugging is not available, so this kernel is being "
                + "debugged on the Helix CPU emulator instead. " + CudaGdbDebugEngine.UnavailableReason();
            return CudaDebugBackend.Emulator;
        }

        // Debug a .cu kernel on the CPU SIMT emulator for the selected lane. The source is read from
        // the FILE rather than from the editor buffer, so what runs is what is on disk — the same
        // rule every other engine here follows, and the reason a debug session cannot silently
        // disagree with the code the user is looking at once they have saved.
        private void StartCuda(string scriptPath)
        {
            _stoppedThisSession = false; _lastEngineOutput = null;

            string fallbackNotice;
            if (ResolveCudaBackend(out fallbackNotice) == CudaDebugBackend.CudaGdb)
            {
                StartCudaGdb(scriptPath);
                return;
            }
            if (fallbackNotice != null)
                MarshalEvent(() => OutputReceived?.Invoke(this, fallbackNotice + Environment.NewLine));

            EnsureCudaEngine();
            _active = _cudaEngine;
            string source;
            try { source = File.ReadAllText(scriptPath); }
            catch (Exception ex)
            {
                MessageBox.Show(_owner, "Failed to read CUDA source: " + ex.Message,
                    "Debug", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            try { _cudaEngine.Start(scriptPath, source, CudaDebugBlock, CudaDebugThread); }
            catch (Exception ex)
            {
                MessageBox.Show(_owner, "Failed to start CUDA debug session: " + ex.Message,
                    "Debug", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Debug a .cu kernel on the real GPU via cuda-gdb. Untested against real hardware -- see
        // CudaGdbRunner's class remarks -- which is exactly why the engine reports tool absence and
        // build failure through OutputReceived rather than guessing.
        private void StartCudaGdb(string scriptPath)
        {
            EnsureCudaGdbEngine();
            _active = _cudaGdbEngine;
            _cudaGdbEngine.DebugBlock = CudaDebugBlock;
            _cudaGdbEngine.DebugThread = CudaDebugThread;
            try { _cudaGdbEngine.Start(scriptPath, FirstBreakpointLine(scriptPath)); }
            catch (Exception ex)
            {
                MessageBox.Show(_owner, "Failed to start a cuda-gdb session: " + ex.Message,
                    "Debug", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Debug a .c/.cpp/.h/... file via gdb (falling back to lldb) over its compiled binary; the project dir
        // is the nearest ancestor holding a CMakeLists.txt, else the file's own directory. Reports tool-absence
        // (or build failure) honestly when neither gdb nor lldb — or no usable compiler — is available.
        private void StartCpp(string scriptPath)
        {
            _stoppedThisSession = false; _lastEngineOutput = null;
            EnsureCppEngine();
            _active = _cppEngine;
            string projDir = FindProjectDir(scriptPath, "CMakeLists.txt");
            _cppEngine.Start(projDir, scriptPath, FirstBreakpointLine(scriptPath));
        }

        // Walk up from a source file to the nearest directory containing a file matching the pattern.
        private static string FindProjectDir(string sourceFile, string pattern)
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourceFile)));
                while (dir != null)
                {
                    if (dir.GetFiles(pattern).Length > 0) return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch { }
            return Path.GetDirectoryName(Path.GetFullPath(sourceFile));
        }

        // The lowest enabled editor breakpoint line for a file, or 1 when none is set.
        private int FirstBreakpointLine(string scriptPath)
        {
            int line = 0;
            try
            {
                var bps = _breakpoints.GetEnabledBreakpoints(scriptPath);
                if (bps != null && bps.Count > 0) { line = int.MaxValue; foreach (int b in bps) if (b < line) line = b; }
            }
            catch { }
            return line <= 0 ? 1 : line;
        }

        // Lazily create the Python engine and re-run interpreter discovery. Engine events are
        // re-raised through MarshalEvent so subscribers always receive them on the UI thread.
        private void EnsurePyEngine()
        {
            _interpreters.DiscoverDefault();
            if (_pyEngine != null) return;
            _pyEngine = new PythonDebugEngine(_interpreters, _breakpoints);
            _pyEngine.Stopped += (s, e) =>
            {
                // One-shot Run to Cursor: once we've stopped at (or past) the target, clear the transient
                // breakpoint and re-push the file's real breakpoints so it doesn't fire again.
                try
                {
                    if (_pyEngine.RunToCursorLine > 0)
                    {
                        string rtcFile = _pyEngine.RunToCursorFile;
                        _pyEngine.RunToCursorLine = 0;
                        _pyEngine.RunToCursorFile = null;
                        if (!string.IsNullOrEmpty(rtcFile)) _pyEngine.SendBreakpointsForFile(rtcFile);
                    }
                }
                catch { }
                MarshalEvent(() => Stopped?.Invoke(this, e));
            };
            _pyEngine.OutputReceived += (s, e) => MarshalEvent(() => OutputReceived?.Invoke(this, e.Text));
            _pyEngine.SessionEnded += (s, e) =>
            {
                // The program is gone, so its bridge is gone too. Close the listener here rather
                // than waiting for the socket to notice, or the next run would find the port still
                // held by the last one.
                TearDownKernelSession();
                MarshalEvent(() => SessionEnded?.Invoke(this, EventArgs.Empty));
            };
        }

        public void Start(string scriptPath)
        {
            // Nothing to debug without a real file — tell the user and bail.
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                MessageBox.Show(_owner, "No file to debug. Open a Python (.py) or CUDA (.cu) file in the editor first.",
                    "Debug", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DebugLaunchContribution launcher = _launchers.FirstOrDefault(c => c.Supports(scriptPath));
            if (launcher != null) launcher.Start(scriptPath);
        }

        private sealed class DebugLaunchContribution
        {
            private readonly Func<string, bool> _supports;
            private readonly Action<string> _start;
            public DebugLaunchContribution(string id, Func<string, bool> supports, Action<string> start)
            {
                if (supports == null) throw new ArgumentNullException(nameof(supports));
                if (start == null) throw new ArgumentNullException(nameof(start));
                Id = id; _supports = supports; _start = start;
            }
            public string Id { get; private set; }
            public bool Supports(string path) { return _supports(path); }
            public void Start(string path) { _start(path); }
        }

        private void StartPython(string scriptPath)
        {
            // debugpy can't attach without an interpreter — warn and bail.
            if (_interpreters.SelectedInterpreter == null)
            {
                MessageBox.Show(_owner,
                    "No Python interpreter found on PATH. Install Python and `pip install debugpy`, then retry.",
                    "Debug", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            EnsurePyEngine();
            _active = _pyEngine;
            // Make sibling workspace packages importable when debugging a startup main.py.
            _pyEngine.PythonPath = ExtraPythonPath;
            // The solution's run environment, plus — when this run is emulated and a kernel
            // breakpoint exists — the port the co-session is listening on.
            _pyEngine.ExtraEnvironment = PrepareCudaCoSession();
            // Push the current exception-break selection into the engine for this run.
            _pyEngine.ExceptionBreakFilters = ExceptionBreakFilters;
            // Stop-on-entry: resolve the first executable line and arm the engine's synthetic
            // entry breakpoint (debugpy ignores stopOnEntry on attach). Fall back to the adapter
            // flag if the script won't parse. Always set both so a prior run's value can't leak.
            int entryLine = StopOnEntry ? HelixPythonNamespace.VirtualCompiler.EntryLineResolver.FindEntryLine(scriptPath) : 0;
            _pyEngine.EntryBreakpointLine = entryLine;
            _pyEngine.StopOnEntry = StopOnEntry && entryLine <= 0;
            // Kick off the async attach; surface any launch failure as an error dialog.
            try { _pyEngine.StartDebuggingAsync(scriptPath, WorkingDirectory, ScriptArguments); }
            catch (Exception ex)
            {
                MessageBox.Show(_owner, "Failed to start debug session: " + ex.Message,
                    "Debug", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Debugs every test pytest collects under <paramref name="testProjectDir"/> — the
        /// "Debug Tests" verb for a test package's Solution Explorer node. Breakpoints already set
        /// (via the tabbed editor, same <see cref="BreakpointManager"/> as script debugging) in any
        /// <c>test_*.py</c> file under that directory bind and pause exactly like a normal Python
        /// debug session; there is no single entry script here, so stop-on-entry doesn't apply.
        /// </summary>
        public void StartTests(string testProjectDir)
        {
            if (string.IsNullOrWhiteSpace(testProjectDir) || !Directory.Exists(testProjectDir))
            {
                MessageBox.Show(_owner, "Test project directory not found: " + testProjectDir,
                    "Debug", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_interpreters.SelectedInterpreter == null)
            {
                MessageBox.Show(_owner,
                    "No Python interpreter found on PATH. Install Python and `pip install debugpy pytest`, then retry.",
                    "Debug", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            EnsurePyEngine();
            _active = _pyEngine;
            _pyEngine.PythonPath = ExtraPythonPath;
            _pyEngine.ExceptionBreakFilters = ExceptionBreakFilters;
            _pyEngine.EntryBreakpointLine = 0;
            _pyEngine.StopOnEntry = false; // no single entry script to stop on in module-launch mode
            try { _pyEngine.StartDebuggingModuleAsync("pytest", new[] { "-v", testProjectDir }, testProjectDir); }
            catch (Exception ex)
            {
                MessageBox.Show(_owner, "Failed to start test debug session: " + ex.Message,
                    "Debug", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Execution-control passthroughs. Guarded with ?. and swallow exceptions so a
        // race against session teardown (engine gone / already stopped) can't crash the UI.
        // Stop tears down BOTH halves of a co-session, kernel first. Detaching the kernel
        // debugger releases a parked lane so the launch can finish; killing Python first would
        // leave the bridge blocked in a pause with nobody left to resume it.
        public void Stop()
        {
            try { _kernelSession?.StopDebugging(); } catch { }
            try { _active?.StopDebugging(); } catch { }
        }
        public void Continue() { try { Current?.Continue(); } catch { } }
        public void Pause() { try { Current?.Pause(); } catch { } }

        /// <summary>
        /// Run to Cursor (Python): while paused, sets a one-shot transient breakpoint at (file, line),
        /// pushes it to the adapter, and resumes — so execution runs until it reaches that line (or an
        /// earlier real breakpoint). Returns false when not applicable (no active Python session / not
        /// paused). The transient breakpoint is auto-cleared on the next stop.
        /// </summary>
        public bool RunToCursor(string filePath, int line)
        {
            PythonDebugEngine pe = _active as PythonDebugEngine;
            if (pe == null || !pe.IsPaused || string.IsNullOrEmpty(filePath) || line < 1) return false;
            try
            {
                pe.RunToCursorFile = filePath;
                pe.RunToCursorLine = line;
                pe.SendBreakpointsForFile(filePath);
                pe.Continue();
                return true;
            }
            catch { return false; }
        }
        public void StepOver() { try { Current?.StepOver(); } catch { } }
        public void StepInto() { try { Current?.StepInto(); } catch { } }
        public void StepOut() { try { Current?.StepOut(); } catch { } }

        /// <summary>
        /// Extra environment variables for a Python debug run — the solution's run environment as
        /// the Run command computes it, which is where the CUDA emulator switches live. Handed
        /// straight to the debuggee, so Debug and Run put the program in the same world.
        /// </summary>
        public IReadOnlyDictionary<string, string> ExtraEnvironment { get; set; }

        /// <summary>
        /// Break at the first statement of every CUDA kernel the program launches, whether or not a
        /// breakpoint is set in it. The only way to catch a kernel written INLINE in the <c>.py</c>,
        /// which has no file of its own to put a breakpoint in until it has stopped once.
        /// </summary>
        public bool StopOnKernelEntry { get; set; }

        /// <summary>
        /// Stand up the CUDA co-session for a Python run, and return the environment the debuggee
        /// should get — the caller's, plus the port the bridge must dial.
        ///
        /// <para>
        /// Only when the run is actually routed to the emulator (<c>HELIX_CUDA_EMULATE</c>) AND
        /// there is something that could stop: a breakpoint in a kernel file, or break-on-entry. A
        /// listener opened for a program with no kernel breakpoints would sit there for the life of
        /// the session doing nothing, and every extra moving part in a debug session is one more
        /// thing that can be blamed for a breakpoint that did not bind.
        /// </para>
        /// </summary>
        internal IDictionary<string, string> PrepareCudaCoSession()
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (ExtraEnvironment != null)
                foreach (KeyValuePair<string, string> pair in ExtraEnvironment)
                    environment[pair.Key] = pair.Value;

            TearDownKernelSession();

            string emulate;
            if (!environment.TryGetValue("HELIX_CUDA_EMULATE", out emulate)
                || !string.Equals(emulate, "1", StringComparison.Ordinal))
                return environment;

            var session = new CudaKernelDebugSession(_breakpoints)
            {
                DebugBlock = CudaDebugBlock,
                DebugThread = CudaDebugThread,
                StopOnKernelEntry = StopOnKernelEntry,
            };
            if (!session.HasAnythingToStopFor()) { session.Dispose(); return environment; }

            session.Stopped += (s, e) =>
            {
                _stoppedThisSession = true;
                MarshalEvent(() => Stopped?.Invoke(this, e));
            };
            session.OutputReceived += (s, e) => MarshalEvent(() => OutputReceived?.Invoke(this, e.Text));
            // Deliberately NOT forwarded as SessionEnded: the Python program is still running and
            // the Debug panels must not tear down because a kernel finished.
            session.KernelExited += (s, e) => MarshalEvent(() => { });

            try { session.Listen(); }
            catch (Exception) { session.Dispose(); return environment; }

            _kernelSession = session;
            environment[HelixPythonNamespace.CudaEmulator.KernelDebugProtocol.PortVariable] =
                session.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return environment;
        }

        private void TearDownKernelSession()
        {
            CudaKernelDebugSession session = _kernelSession;
            _kernelSession = null;
            if (session != null) try { session.Dispose(); } catch { }
        }

        /// <summary>
        /// Local variables of the top stack frame at the current stop, or an empty list when
        /// not paused. Drives the Debug → Variables panel. Queried synchronously (call stack →
        /// scopes → variables) — only valid while the engine is paused. Works for either engine.
        /// </summary>
        public IList<DebugVariable> GetLocals()
        {
            IDebugEngine e = Current;
            if (e == null || !e.IsPaused) return new List<DebugVariable>();
            try
            {
                List<DebugStackFrame> frames = e.GetCallStack();
                if (frames.Count == 0) return new List<DebugVariable>();
                return LocalsOfFrame(e, frames[0].Id);
            }
            catch { }
            return new List<DebugVariable>();
        }

        /// <summary>
        /// The current call stack (innermost frame first) at the stop, or an empty list when not
        /// paused. Drives the Debug → Call Stack panel.
        /// </summary>
        public IList<DebugStackFrame> GetCallStack()
        {
            IDebugEngine e = Current;
            if (e == null || !e.IsPaused) return new List<DebugStackFrame>();
            try { return e.GetCallStack(); }
            catch { return new List<DebugStackFrame>(); }
        }

        /// <summary>
        /// Local variables of the given stack frame at the current stop (use a frame Id from
        /// <see cref="GetCallStack"/>), or an empty list when not paused. Lets the Variables panel
        /// follow the frame the user selects in the Call Stack.
        /// </summary>
        public IList<DebugVariable> GetLocals(int frameId)
        {
            IDebugEngine e = Current;
            if (e == null || !e.IsPaused) return new List<DebugVariable>();
            try { return LocalsOfFrame(e, frameId); }
            catch { }
            return new List<DebugVariable>();
        }

        // The "Locals" scope of a frame, falling back to the first scope when an engine names it
        // differently. Both current engines expose a scope literally named "Locals".
        private static IList<DebugVariable> LocalsOfFrame(IDebugEngine e, int frameId)
        {
            List<DebugScope> scopes = e.GetScopes(frameId);
            foreach (DebugScope sc in scopes)
                if (string.Equals(sc.Name, "Locals", StringComparison.OrdinalIgnoreCase))
                    return e.GetVariables(sc.VariablesReference);
            if (scopes.Count > 0) return e.GetVariables(scopes[0].VariablesReference);
            return new List<DebugVariable>();
        }

        /// <summary>
        /// Child members of a structured variable (its <see cref="DebugVariable.VariablesReference"/>
        /// from a prior <see cref="GetLocals(int)"/>/<see cref="Evaluate(string)"/> result), or an
        /// empty list when not paused / on error. Drives lazy tree-expansion of lists, dicts and
        /// object instances in the Variables panel.
        /// </summary>
        public IList<DebugVariable> GetChildren(int variablesReference)
        {
            IDebugEngine e = Current;
            if (e == null || !e.IsPaused || variablesReference <= 0) return new List<DebugVariable>();
            try { return e.GetVariables(variablesReference); }
            catch { return new List<DebugVariable>(); }
        }

        /// <summary>
        /// Evaluates a watch expression in the top frame at the current stop, or null when not
        /// paused / on error. Drives watch entries in the Variables panel. Python-only — the CUDA
        /// emulator has no expression evaluator, so watches show &lt;error&gt; while it is active.
        /// </summary>
        public DebugVariable Evaluate(string expression)
        {
            return Evaluate(expression, -1);
        }

        /// <summary>
        /// Evaluates an expression in a specific stack frame (the Immediate/Debug Console and the
        /// Call-Stack-aware Watch use this so evaluation happens in the frame the user selected). Pass
        /// <paramref name="frameId"/> = -1 to target the top frame. Returns null when not paused / on
        /// error / when the requested frame is gone.
        /// </summary>
        public DebugVariable Evaluate(string expression, int frameId)
        {
            return Evaluate(expression, frameId, "watch");
        }

        /// <summary>
        /// Evaluates in a specific frame under a specific DAP <paramref name="context"/>. The
        /// context is not decoration: debugpy treats <c>"hover"</c> as best-effort and silent — a
        /// name that does not resolve returns an error instead of raising into the debuggee — which
        /// is exactly right for a data tip, where the mouse merely came to rest over some text and
        /// the user never asked for anything to be run. Watches and the Immediate window keep
        /// <c>"watch"</c> / <c>"repl"</c>, where the user did ask.
        /// </summary>
        public DebugVariable Evaluate(string expression, int frameId, string context)
        {
            IDebugEngine a = Current;
            if (a == null || !a.IsPaused || string.IsNullOrWhiteSpace(expression)) return null;
            try
            {
                List<DebugStackFrame> frames = a.GetCallStack();
                if (frames.Count == 0) return null;
                // Default (or an unknown frame id) targets the innermost frame; otherwise use the
                // caller's frame if it still exists in the current stack.
                int targetFrame = frames[0].Id;
                if (frameId >= 0)
                {
                    foreach (DebugStackFrame f in frames)
                        if (f.Id == frameId) { targetFrame = frameId; break; }
                }
                // Python: full debugpy expression eval. CUDA: a bare local or coordinate name
                // (threadIdx.x, a kernel local) looked up in the emulator's own frame — there is no
                // expression evaluator behind it, and it does not pretend to be one.
                PythonDebugEngine pe = a as PythonDebugEngine;
                if (pe != null) return pe.Evaluate(expression, targetFrame, context ?? "watch");
                CudaEmulatorDebugEngine ce = a as CudaEmulatorDebugEngine;
                if (ce != null) return ce.Evaluate(expression, targetFrame);
                CudaKernelDebugSession ks = a as CudaKernelDebugSession;
                if (ks != null) return ks.Evaluate(expression, targetFrame, context ?? "watch");
                CudaGdbDebugEngine ge = a as CudaGdbDebugEngine;
                if (ge != null) return ge.Evaluate(expression, targetFrame, context ?? "watch");
                return null;
            }
            catch { return null; }
        }

        // Hop an engine callback onto the UI thread when needed; otherwise run it inline.
        private void MarshalEvent(Action a)
        {
            if (_owner != null && !_owner.IsDisposed && _owner.InvokeRequired)
                _owner.BeginInvoke(a);
            else a();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _kernelSession?.Dispose(); } catch { }
            try { _pyEngine?.Dispose(); } catch { }
            try { _cudaEngine?.Dispose(); } catch { }
            try { _cudaGdbEngine?.Dispose(); } catch { }
            _kernelSession = null;
            _pyEngine = null;
            _cudaEngine = null;
            _cudaGdbEngine = null;
            _active = null;
        }
    }
}
