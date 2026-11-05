using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HelixPythonNamespace.CudaEmulator;
using HelixPythonNamespace.Debugger;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace HelixPythonNamespace.Debugger.Tests
{
    /// <summary>
    /// The CUDA half of a co-debug session, driven exactly as it is in production: a real
    /// <see cref="CudaKernelDebugSession"/> listening on a loopback port, a real
    /// <c>helix_cuda_bridge.exe</c> dialling back, and the bridge's stdin carrying the same
    /// requests the <c>helix_cuda</c> Python package sends.
    ///
    /// <para>
    /// The Python interpreter is the one piece left out, and deliberately so: it contributes
    /// nothing the debugger can get wrong. A kernel breakpoint is bound by matching the file's TEXT
    /// inside the module source, and the hand-off works because the caller is blocked on the launch
    /// reply — both are true of any caller, and testing against a fixed one keeps the test from
    /// depending on which Python and which debugpy happen to be installed.
    /// </para>
    ///
    /// <para>
    /// The assertion that matters most is the last one in <see cref="BreakpointInAKernelFileStopsTheLane"/>:
    /// after the session resumes, every element of the output buffer is correct. Debugging a kernel
    /// must not change what it computes. An implementation that stopped the grid at the debugged
    /// thread would pass every other check here and quietly hand the user wrong data.
    /// </para>
    /// </summary>
    [TestClass]
    public class CudaKernelCoDebugTests
    {
        // A distinctive line to hang a breakpoint on, found by text rather than by a line number
        // so that editing the kernel above it cannot silently move the breakpoint somewhere else.
        private const string BreakpointMarker = "int right = b[i];";

        private static readonly string[] KernelLines =
        {
            "extern \"C\" __global__ void vadd(const int* a, const int* b, int* c, int n)",
            "{",
            "    int i = blockIdx.x * blockDim.x + threadIdx.x;",
            "    if (i < n)",
            "    {",
            "        int left = a[i];",
            "        int right = b[i];",
            "        c[i] = left + right;",
            "    }",
            "}",
        };

        private string _directory;
        private string _kernelPath;
        private BreakpointManager _breakpoints;
        private CudaKernelDebugSession _session;
        private Process _bridge;

        private static string BridgeExecutable()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                {
                    string path = Path.Combine(dir.FullName, "artifacts", "bin",
                        "HelixStudio.CudaBridge", "Debug", "net472", "helix_cuda_bridge.exe");
                    return File.Exists(path) ? path : null;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private int MarkerLine()
        {
            for (int i = 0; i < KernelLines.Length; i++)
                if (KernelLines[i].Contains(BreakpointMarker)) return i + 1;
            throw new InvalidOperationException("the breakpoint marker is no longer in the kernel");
        }

        [TestInitialize]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "helix-codebug-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _kernelPath = Path.Combine(_directory, "vadd.cu");
            File.WriteAllText(_kernelPath, string.Join(Environment.NewLine, KernelLines) + Environment.NewLine);
            _breakpoints = new BreakpointManager(Path.Combine(_directory, "state"));
        }

        [TestCleanup]
        public void TearDown()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            if (_bridge != null)
            {
                try
                {
                    if (!_bridge.HasExited)
                    {
                        _bridge.StandardInput.WriteLine("{\"op\":\"quit\"}");
                        if (!_bridge.WaitForExit(4000)) _bridge.Kill();
                    }
                }
                catch { }
                finally { _bridge.Dispose(); _bridge = null; }
            }
            try { Directory.Delete(_directory, true); } catch { }
        }

        // ---- harness ---------------------------------------------------------

        /// <summary>Start the bridge with the co-session's port in its environment, as Python does.</summary>
        private void StartBridge(int port)
        {
            string executable = BridgeExecutable();
            if (executable == null)
                Assert.Inconclusive("helix_cuda_bridge.exe is not built; build the solution first.");

            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            info.EnvironmentVariables[KernelDebugProtocol.PortVariable] = port.ToString();
            _bridge = new Process { StartInfo = info };
            _bridge.Start();
        }

        private JObject Call(JObject request)
        {
            _bridge.StandardInput.WriteLine(request.ToString(Newtonsoft.Json.Formatting.None));
            _bridge.StandardInput.Flush();
            string line = _bridge.StandardOutput.ReadLine();
            Assert.IsNotNull(line, "the bridge exited without replying");
            return JObject.Parse(line);
        }

        private static JObject Ok(JObject reply)
        {
            Assert.IsTrue((bool?)reply["ok"] == true, "bridge refused: " + reply.ToString());
            return reply;
        }

        /// <summary>Compile the kernel file's text as a module, exactly as a program that read it would.</summary>
        private string CompileKernelModule()
        {
            JObject reply = Ok(Call(new JObject { ["op"] = "module", ["source"] = File.ReadAllText(_kernelPath) }));
            return (string)reply["module"];
        }

        private string Malloc(int bytes)
        {
            return (string)Ok(Call(new JObject { ["op"] = "malloc", ["bytes"] = bytes }))["ptr"];
        }

        private void Upload(string pointer, int[] values)
        {
            var bytes = new byte[values.Length * 4];
            System.Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            Ok(Call(new JObject
            {
                ["op"] = "upload",
                ["ptr"] = pointer,
                ["data"] = Convert.ToBase64String(bytes),
            }));
        }

        private int[] Download(string pointer, int count)
        {
            JObject reply = Ok(Call(new JObject
            {
                ["op"] = "download",
                ["ptr"] = pointer,
                ["bytes"] = count * 4,
            }));
            byte[] bytes = Convert.FromBase64String((string)reply["data"]);
            var values = new int[count];
            System.Buffer.BlockCopy(bytes, 0, values, 0, Math.Min(bytes.Length, count * 4));
            return values;
        }

        /// <summary>
        /// Send a launch on a worker and hand back the task. It cannot be awaited inline: while the
        /// kernel is paused the reply has not been written, which is precisely the state the test
        /// needs to be in to drive the debugger.
        /// </summary>
        private Task<JObject> LaunchAsync(string module, string a, string b, string c, int n)
        {
            return Task.Run(() => Call(new JObject
            {
                ["op"] = "launch",
                ["module"] = module,
                ["kernel"] = "vadd",
                ["grid"] = new JArray(1, 1, 1),
                ["block"] = new JArray(n, 1, 1),
                ["args"] = new JArray(
                    new JObject { ["ptr"] = a },
                    new JObject { ["ptr"] = b },
                    new JObject { ["ptr"] = c },
                    new JObject { ["i"] = n }),
                ["shared"] = 0,
            }));
        }

        private sealed class StopRecorder
        {
            private readonly SemaphoreSlim _arrived = new SemaphoreSlim(0);
            public readonly List<DebugStoppedEventArgs> Stops = new List<DebugStoppedEventArgs>();

            public void Record(object sender, DebugStoppedEventArgs e)
            {
                lock (Stops) Stops.Add(e);
                _arrived.Release();
            }

            public DebugStoppedEventArgs WaitForStop(int index, int timeoutMs = 30000)
            {
                Assert.IsTrue(_arrived.Wait(timeoutMs), "the kernel never stopped");
                lock (Stops) return Stops[index];
            }
        }

        private CudaKernelDebugSession StartSession(Dim3 lane, bool stopOnEntry = false)
        {
            var session = new CudaKernelDebugSession(_breakpoints)
            {
                DebugThread = lane,
                DebugBlock = new Dim3(0, 0, 0),
                StopOnKernelEntry = stopOnEntry,
            };
            session.Listen();
            _session = session;
            return session;
        }

        private static void WaitForBridge(CudaKernelDebugSession session)
        {
            for (int i = 0; i < 200 && !session.IsBridgeConnected; i++) Thread.Sleep(25);
            Assert.IsTrue(session.IsBridgeConnected, "the bridge never dialled the debug port");
        }

        // ---- tests -----------------------------------------------------------

        /// <summary>
        /// The headline behaviour: a breakpoint set in a <c>.cu</c> file stops the chosen lane of a
        /// kernel that a caller loaded from that file as an anonymous string, reports the user's own
        /// path and line, shows the thread coordinates and locals, and leaves the kernel's output
        /// correct for EVERY thread once resumed.
        /// </summary>
        [TestMethod]
        public void BreakpointInAKernelFileStopsTheLane()
        {
            const int n = 8;
            int line = MarkerLine();
            _breakpoints.AddBreakpoint(_kernelPath, line);

            // Lane 5, not lane 0: a debugger that ignored the requested coordinate and followed
            // whichever thread ran first would still stop, and would still look right.
            CudaKernelDebugSession session = StartSession(new Dim3(5, 0, 0));
            var recorder = new StopRecorder();
            session.Stopped += recorder.Record;

            StartBridge(session.Port);
            WaitForBridge(session);

            string module = CompileKernelModule();
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            Upload(a, new[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Upload(b, new[] { 10, 20, 30, 40, 50, 60, 70, 80 });

            Task<JObject> launch = LaunchAsync(module, a, b, c, n);

            DebugStoppedEventArgs stop = recorder.WaitForStop(0);
            Assert.AreEqual("breakpoint", stop.Reason);
            Assert.AreEqual(_kernelPath, stop.FilePath, "the stop must name the user's own file");
            Assert.AreEqual(line, stop.Line);
            Assert.IsTrue(session.IsPaused);
            Assert.IsTrue(session.IsInsideKernel);

            List<DebugStackFrame> frames = session.GetCallStack();
            Assert.AreEqual(1, frames.Count, "a kernel with no calls has exactly one frame");
            Assert.AreEqual("vadd", frames[0].Name);
            Assert.AreEqual(_kernelPath, frames[0].FilePath);

            Dictionary<string, string> locals = Locals(session, frames[0].Id);
            Assert.AreEqual("(5,0,0)", locals["threadIdx"], "the requested lane is the one that stopped");
            Assert.AreEqual("(0,0,0)", locals["blockIdx"]);
            Assert.AreEqual("(8,1,1)", locals["blockDim"]);
            Assert.AreEqual("5", locals["i"], "the lane's own index, computed by the kernel");
            Assert.AreEqual("6", locals["left"], "a[5], already read at this line");
            Assert.IsFalse(locals.ContainsKey("right"),
                "'right' is declared BY the line we are stopped on and must not exist yet");

            // A watch on a coordinate resolves at the stop.
            DebugVariable watch = session.Evaluate("threadIdx", frames[0].Id);
            Assert.IsNotNull(watch);
            Assert.AreEqual("(5,0,0)", watch.Value);

            session.Continue();
            Ok(launch.Result);

            CollectionAssert.AreEqual(
                new[] { 11, 22, 33, 44, 55, 66, 77, 88 }, Download(c, n),
                "every thread must still have run: debugging a kernel cannot change its answer");
        }

        /// <summary>
        /// Stepping inside the kernel advances a line at a time and the locals change with it —
        /// the variable declared by the line we were stopped on exists after one step.
        /// </summary>
        [TestMethod]
        public void SteppingInsideAKernelAdvancesOneLine()
        {
            const int n = 4;
            int line = MarkerLine();
            _breakpoints.AddBreakpoint(_kernelPath, line);

            CudaKernelDebugSession session = StartSession(new Dim3(1, 0, 0));
            var recorder = new StopRecorder();
            session.Stopped += recorder.Record;

            StartBridge(session.Port);
            WaitForBridge(session);

            string module = CompileKernelModule();
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            Upload(a, new[] { 1, 2, 3, 4 });
            Upload(b, new[] { 100, 200, 300, 400 });

            Task<JObject> launch = LaunchAsync(module, a, b, c, n);

            DebugStoppedEventArgs first = recorder.WaitForStop(0);
            Assert.AreEqual(line, first.Line);

            session.StepOver();
            DebugStoppedEventArgs second = recorder.WaitForStop(1);
            Assert.AreEqual("step", second.Reason);
            Assert.AreEqual(line + 1, second.Line, "one step, one line");

            Dictionary<string, string> locals = Locals(session, session.GetCallStack()[0].Id);
            Assert.AreEqual("200", locals["right"], "b[1], read by the line we stepped over");

            session.Continue();
            Ok(launch.Result);
            CollectionAssert.AreEqual(new[] { 101, 202, 303, 404 }, Download(c, n));
        }

        /// <summary>
        /// A kernel written inline in the caller — no file anywhere — still stops when
        /// break-on-entry is armed, and is reported under a real file the IDE can open. Without
        /// this the inline style would be the one kernel shape with no way in at all.
        /// </summary>
        [TestMethod]
        public void InlineKernelStopsOnEntryAndGetsAFileToShow()
        {
            const int n = 4;
            CudaKernelDebugSession session = StartSession(new Dim3(0, 0, 0), stopOnEntry: true);
            var recorder = new StopRecorder();
            session.Stopped += recorder.Record;

            StartBridge(session.Port);
            WaitForBridge(session);

            // Deliberately NOT the file on disk: this is the "kernel as a string literal" style.
            string source = string.Join("\n", KernelLines);
            string module = (string)Ok(Call(new JObject { ["op"] = "module", ["source"] = source }))["module"];
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            Upload(a, new[] { 1, 1, 1, 1 });
            Upload(b, new[] { 2, 2, 2, 2 });

            Task<JObject> launch = LaunchAsync(module, a, b, c, n);

            DebugStoppedEventArgs stop = recorder.WaitForStop(0);
            Assert.AreEqual("step", stop.Reason, "break-on-entry is a step, not a breakpoint hit");
            Assert.IsFalse(string.IsNullOrEmpty(stop.FilePath), "an inline kernel still needs a file to show");
            Assert.IsTrue(File.Exists(stop.FilePath), "the file the IDE is told to open must exist: " + stop.FilePath);
            Assert.AreEqual(KernelLines[2].Trim(),
                File.ReadAllLines(stop.FilePath)[stop.Line - 1].Trim(),
                "the reported line must be the line of the spilled file we are actually on");

            session.Continue();
            Ok(launch.Result);
            CollectionAssert.AreEqual(new[] { 3, 3, 3, 3 }, Download(c, n));
        }

        /// <summary>
        /// A kernel assembled at run time from a header plus the file's text still binds the file —
        /// at the right line. Equality matching would fail here, and this is the style a program
        /// that injects a <c>#define</c> before a shared kernel actually uses.
        /// </summary>
        [TestMethod]
        public void KernelFileConcatenatedOntoAHeaderStillBinds()
        {
            const int n = 4;
            int line = MarkerLine();
            _breakpoints.AddBreakpoint(_kernelPath, line);

            CudaKernelDebugSession session = StartSession(new Dim3(3, 0, 0));
            var recorder = new StopRecorder();
            session.Stopped += recorder.Record;

            StartBridge(session.Port);
            WaitForBridge(session);

            string prelude = "// generated by the program at run time\n#define HELIX_TILE 32\n\n";
            string source = prelude + File.ReadAllText(_kernelPath);
            string module = (string)Ok(Call(new JObject { ["op"] = "module", ["source"] = source }))["module"];
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            Upload(a, new[] { 7, 7, 7, 7 });
            Upload(b, new[] { 1, 2, 3, 4 });

            Task<JObject> launch = LaunchAsync(module, a, b, c, n);

            DebugStoppedEventArgs stop = recorder.WaitForStop(0);
            Assert.AreEqual(_kernelPath, stop.FilePath);
            Assert.AreEqual(line, stop.Line,
                "the line must be the one in the USER's file, not the offset line in the assembled source");

            session.Continue();
            Ok(launch.Result);
            CollectionAssert.AreEqual(new[] { 8, 9, 10, 11 }, Download(c, n));
        }

        /// <summary>
        /// With no kernel breakpoint anywhere the session declines to listen at all, so an ordinary
        /// Python debug session pays nothing for a feature it is not using.
        /// </summary>
        [TestMethod]
        public void NoKernelBreakpointMeansNoSession()
        {
            _breakpoints.AddBreakpoint(Path.Combine(_directory, "main.py"), 12);
            var session = new CudaKernelDebugSession(_breakpoints);
            _session = session;
            Assert.IsFalse(session.HasAnythingToStopFor(),
                "a .py breakpoint belongs to debugpy and must not arm the kernel debugger");

            _breakpoints.AddBreakpoint(_kernelPath, MarkerLine());
            Assert.IsTrue(session.HasAnythingToStopFor());
        }

        /// <summary>
        /// A disabled breakpoint is still in the list and still drawn in the margin, and must not
        /// stop the kernel — the same rule the Python and standalone-CUDA engines follow.
        /// </summary>
        [TestMethod]
        public void DisabledKernelBreakpointDoesNotStop()
        {
            const int n = 4;
            int line = MarkerLine();
            _breakpoints.AddBreakpoint(_kernelPath, line);
            _breakpoints.SetEnabled(_kernelPath, line, false);

            var session = new CudaKernelDebugSession(_breakpoints);
            _session = session;
            Assert.IsFalse(session.HasAnythingToStopFor());

            // And end to end: arm the session anyway and confirm the launch runs straight through.
            session.StopOnKernelEntry = false;
            session.DebugThread = new Dim3(0, 0, 0);
            session.Listen();
            var recorder = new StopRecorder();
            session.Stopped += recorder.Record;

            StartBridge(session.Port);
            WaitForBridge(session);

            string module = CompileKernelModule();
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            Upload(a, new[] { 5, 5, 5, 5 });
            Upload(b, new[] { 1, 1, 1, 1 });

            Ok(LaunchAsync(module, a, b, c, n).Result);
            lock (recorder.Stops) Assert.AreEqual(0, recorder.Stops.Count, "a disabled breakpoint must not stop");
            CollectionAssert.AreEqual(new[] { 6, 6, 6, 6 }, Download(c, n));
        }

        /// <summary>
        /// Only <c>.cu</c>/<c>.cuh</c>/<c>.txt</c> breakpoints reach the kernel debugger. The split
        /// is what keeps the two engines from both claiming a stop.
        /// </summary>
        [TestMethod]
        public void OnlyKernelSourceExtensionsAreClaimed()
        {
            Assert.IsTrue(CudaKernelDebugSession.IsKernelSource(@"C:\w\saxpy.cu"));
            Assert.IsTrue(CudaKernelDebugSession.IsKernelSource(@"C:\w\tile.CUH"));
            Assert.IsTrue(CudaKernelDebugSession.IsKernelSource(@"C:\w\kernel.txt"));
            Assert.IsFalse(CudaKernelDebugSession.IsKernelSource(@"C:\w\main.py"));
            Assert.IsFalse(CudaKernelDebugSession.IsKernelSource(@"C:\w\host.cpp"));
            Assert.IsFalse(CudaKernelDebugSession.IsKernelSource(null));
        }

        /// <summary>
        /// Fact-1 of the CUDA requirements: programs that load a kernel from a <c>.txt</c> file. A
        /// breakpoint in one binds exactly as a <c>.cu</c> breakpoint does -- the binding is by
        /// text, so the extension only decides whether the IDE offers the file at all.
        /// </summary>
        [TestMethod]
        public void BreakpointInATxtKernelFileStopsToo()
        {
            const int n = 4;
            string textPath = Path.Combine(_directory, "vadd.txt");
            File.WriteAllText(textPath, string.Join(Environment.NewLine, KernelLines) + Environment.NewLine);
            int line = MarkerLine();
            _breakpoints.AddBreakpoint(textPath, line);

            CudaKernelDebugSession session = StartSession(new Dim3(2, 0, 0));
            var recorder = new StopRecorder();
            session.Stopped += recorder.Record;

            StartBridge(session.Port);
            WaitForBridge(session);

            string module = (string)Ok(Call(new JObject
            {
                ["op"] = "module",
                ["source"] = File.ReadAllText(textPath),
            }))["module"];
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            Upload(a, new[] { 1, 2, 3, 4 });
            Upload(b, new[] { 9, 9, 9, 9 });

            Task<JObject> launch = LaunchAsync(module, a, b, c, n);

            DebugStoppedEventArgs stop = recorder.WaitForStop(0);
            Assert.AreEqual(textPath, stop.FilePath, "a .txt kernel must report its own path");
            Assert.AreEqual(line, stop.Line);
            Assert.AreEqual("2", Locals(session, session.GetCallStack()[0].Id)["i"]);

            session.Continue();
            Ok(launch.Result);
            CollectionAssert.AreEqual(new[] { 10, 11, 12, 13 }, Download(c, n));
        }

        /// <summary>
        /// A kernel with <c>__syncthreads</c> takes the emulator's THREADED path: every thread gets
        /// a real .NET thread and they rendezvous at a barrier. Parking one of them at a breakpoint
        /// therefore holds all the others at the barrier -- which is correct, and is also exactly
        /// the shape that deadlocks if the pause and the barrier are not independent.
        ///
        /// <para>
        /// This test would hang rather than fail if that went wrong, so it is worth saying plainly:
        /// a timeout here means the paused lane and the barrier have become entangled.
        /// </para>
        /// </summary>
        [TestMethod]
        public void PausingALaneDoesNotDeadlockABarrierKernel()
        {
            const int n = 8;
            string[] lines =
            {
                "extern \"C\" __global__ void reverse(int* data, int n)",
                "{",
                "    __shared__ int tile[8];",
                "    int i = threadIdx.x;",
                "    tile[i] = data[i];",
                "    __syncthreads();",
                "    int mirrored = tile[n - 1 - i];",
                "    data[i] = mirrored;",
                "}",
            };
            string path = Path.Combine(_directory, "reverse.cu");
            File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);

            int line = 0;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains("int mirrored")) line = i + 1;
            _breakpoints.AddBreakpoint(path, line);

            CudaKernelDebugSession session = StartSession(new Dim3(1, 0, 0));
            var recorder = new StopRecorder();
            session.Stopped += recorder.Record;

            StartBridge(session.Port);
            WaitForBridge(session);

            string module = (string)Ok(Call(new JObject
            {
                ["op"] = "module",
                ["source"] = File.ReadAllText(path),
            }))["module"];
            string data = Malloc(n * 4);
            Upload(data, new[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Task<JObject> launch = Task.Run(() => Call(new JObject
            {
                ["op"] = "launch",
                ["module"] = module,
                ["kernel"] = "reverse",
                ["grid"] = new JArray(1, 1, 1),
                ["block"] = new JArray(n, 1, 1),
                ["args"] = new JArray(
                    new JObject { ["ptr"] = data },
                    new JObject { ["i"] = n }),
                ["shared"] = 0,
            }));

            DebugStoppedEventArgs stop = recorder.WaitForStop(0);
            Assert.AreEqual(line, stop.Line, "stopped past the barrier, in the thread we chose");
            Assert.AreEqual("1", Locals(session, session.GetCallStack()[0].Id)["i"]);

            session.Continue();
            Ok(launch.Result);
            CollectionAssert.AreEqual(new[] { 8, 7, 6, 5, 4, 3, 2, 1 }, Download(data, n),
                "the barrier must still have worked for every thread");
        }

        /// <summary>
        /// A condition picks the lane, and it is evaluated as a REAL CUDA expression in that lane:
        /// the kernel runs 64 threads and the breakpoint fires only in the one where the condition
        /// holds — not in the one that would have reached the line first.
        ///
        /// <para>
        /// This is the feature that makes a kernel breakpoint usable. Without it you stop in
        /// whichever of a million threads happens to arrive first, which is almost never the one
        /// with the bug.
        /// </para>
        /// </summary>
        [TestMethod]
        public void AConditionStopsOnlyInTheLaneItNames()
        {
            const int n = 64;
            int line = MarkerLine();
            _breakpoints.SetCondition(_kernelPath, line, "i == 37", null);

            CudaKernelDebugSession session = StartSession(new Dim3(37, 0, 0));
            var recorder = new StopRecorder();
            session.Stopped += recorder.Record;

            StartBridge(session.Port);
            WaitForBridge(session);

            string module = CompileKernelModule();
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            var left = new int[n];
            var right = new int[n];
            for (int i = 0; i < n; i++) { left[i] = i; right[i] = 1000; }
            Upload(a, left);
            Upload(b, right);

            Task<JObject> launch = LaunchAsync(module, a, b, c, n);

            DebugStoppedEventArgs stop = recorder.WaitForStop(0);
            Assert.AreEqual("breakpoint", stop.Reason);
            Assert.AreEqual(line, stop.Line);
            Dictionary<string, string> locals = Locals(session, session.GetCallStack()[0].Id);
            Assert.AreEqual("37", locals["i"], "the condition must select the lane, not thread 0");
            Assert.AreEqual("(37,0,0)", locals["threadIdx"]);
            Assert.AreEqual("37", locals["left"], "a[37]");

            session.Continue();
            Ok(launch.Result);
            lock (recorder.Stops)
                Assert.AreEqual(1, recorder.Stops.Count,
                    "exactly one lane satisfies i == 37, so exactly one stop");

            int[] result = Download(c, n);
            Assert.AreEqual(1037, result[37]);
            Assert.AreEqual(1000, result[0], "every other thread still ran");
        }

        /// <summary>
        /// The condition is a real CUDA expression, not a "name op number" pattern: a thread
        /// coordinate, arithmetic and a logical operator at once, all meaning exactly what they
        /// would mean written into the kernel — because the emulator's own evaluator runs them.
        /// </summary>
        [TestMethod]
        public void AConditionCanUseCoordinatesArithmeticAndOperators()
        {
            const int n = 64;
            int line = MarkerLine();
            _breakpoints.SetCondition(_kernelPath, line, "threadIdx.x % 16 == 0 && i > 20", null);

            CudaKernelDebugSession session = StartSession(new Dim3(32, 0, 0));
            var recorder = new StopRecorder();
            session.Stopped += recorder.Record;

            StartBridge(session.Port);
            WaitForBridge(session);

            string module = CompileKernelModule();
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            var values = new int[n];
            for (int i = 0; i < n; i++) values[i] = i;
            Upload(a, values);
            Upload(b, values);

            Task<JObject> launch = LaunchAsync(module, a, b, c, n);

            // 0, 16, 32 and 48 satisfy the modulo; only 32 and 48 also satisfy i > 20. The session
            // follows lane 32, so that is the one that can stop.
            DebugStoppedEventArgs stop = recorder.WaitForStop(0);
            Assert.AreEqual(line, stop.Line);
            Assert.AreEqual("32", Locals(session, session.GetCallStack()[0].Id)["i"]);

            session.Continue();
            Ok(launch.Result);
        }

        /// <summary>
        /// A condition that cannot be evaluated STOPS and explains itself. Silently never firing
        /// would leave the user staring at a breakpoint in the margin that does nothing, unable to
        /// tell a wrong condition from a kernel that never reached the line.
        /// </summary>
        [TestMethod]
        public void AnUnusableConditionStopsAndSaysWhy()
        {
            const int n = 4;
            int line = MarkerLine();
            _breakpoints.SetCondition(_kernelPath, line, "no_such_variable == 1", null);

            CudaKernelDebugSession session = StartSession(new Dim3(0, 0, 0));
            var recorder = new StopRecorder();
            var output = new List<string>();
            session.Stopped += recorder.Record;
            session.OutputReceived += (s, e) => { lock (output) output.Add(e.Text ?? string.Empty); };

            StartBridge(session.Port);
            WaitForBridge(session);

            string module = CompileKernelModule();
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            Upload(a, new[] { 1, 1, 1, 1 });
            Upload(b, new[] { 2, 2, 2, 2 });

            Task<JObject> launch = LaunchAsync(module, a, b, c, n);

            DebugStoppedEventArgs stop = recorder.WaitForStop(0);
            Assert.AreEqual(line, stop.Line, "a broken condition must not swallow the breakpoint");

            string all;
            lock (output) all = string.Join(" ", output.ToArray());
            StringAssert.Contains(all, "no_such_variable",
                "the reason must name the condition that failed: " + all);

            session.Continue();
            Ok(launch.Result);
            CollectionAssert.AreEqual(new[] { 3, 3, 3, 3 }, Download(c, n));
        }

        /// <summary>
        /// A tracepoint logs its interpolated message in the chosen lane and does NOT stop — printf
        /// debugging a kernel without editing the kernel.
        /// </summary>
        [TestMethod]
        public void ATracepointLogsAndKeepsGoing()
        {
            const int n = 8;
            int line = MarkerLine();
            // {right} is deliberately included and deliberately unresolvable: it is declared BY the
            // line we are on, so it does not exist yet. An expression that cannot be evaluated is
            // left as written rather than silently becoming an empty string, so a typo in a
            // tracepoint is visible in its own output instead of turning into a plausible blank.
            _breakpoints.SetLogMessage(_kernelPath, line, "lane {i} sees a={left} n={n} b={right}");

            CudaKernelDebugSession session = StartSession(new Dim3(4, 0, 0));
            var recorder = new StopRecorder();
            var output = new List<string>();
            session.Stopped += recorder.Record;
            session.OutputReceived += (s, e) => { lock (output) output.Add(e.Text ?? string.Empty); };

            StartBridge(session.Port);
            WaitForBridge(session);

            string module = CompileKernelModule();
            string a = Malloc(n * 4), b = Malloc(n * 4), c = Malloc(n * 4);
            Upload(a, new[] { 10, 11, 12, 13, 14, 15, 16, 17 });
            Upload(b, new[] { 20, 21, 22, 23, 24, 25, 26, 27 });

            Ok(LaunchAsync(module, a, b, c, n).Result);

            lock (recorder.Stops)
                Assert.AreEqual(0, recorder.Stops.Count, "a tracepoint must not stop");
            string all;
            lock (output) all = string.Join(string.Empty, output.ToArray());
            StringAssert.Contains(all, "lane 4 sees a=14 n=8",
                "the message must be interpolated in the followed lane: " + all);
            StringAssert.Contains(all, "b={right}",
                "a name that is not in scope yet stays visible as written: " + all);

            CollectionAssert.AreEqual(new[] { 30, 32, 34, 36, 38, 40, 42, 44 }, Download(c, n));
        }

        private static Dictionary<string, string> Locals(CudaKernelDebugSession session, int frameId)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DebugScope scope in session.GetScopes(frameId))
                foreach (DebugVariable variable in session.GetVariables(scope.VariablesReference))
                    result[variable.Name] = variable.Value;
            return result;
        }
    }
}
