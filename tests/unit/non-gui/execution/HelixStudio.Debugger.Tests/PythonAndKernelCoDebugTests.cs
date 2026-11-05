using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using HelixPythonNamespace.CudaEmulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Debugger.Tests
{
    /// <summary>
    /// The Stage 3 claim, end to end: ONE debug session in which a breakpoint in a <c>.py</c> file
    /// and a breakpoint in a <c>.cu</c> file both work, with the hand-off happening by itself at
    /// the kernel launch.
    ///
    /// <para>
    /// Both real engines are driven here — <see cref="PythonDebugEngine"/> over debugpy against a
    /// real interpreter, and <see cref="CudaKernelDebugSession"/> against the real bridge that the
    /// program's own <c>import cupy</c> starts. Nothing is stubbed, because the thing being claimed
    /// is precisely that these two independently-written debuggers take turns correctly over a
    /// program that knows about neither.
    /// </para>
    ///
    /// <para>
    /// It is skipped, not failed, when the machine lacks Python, debugpy, or a built bridge. A test
    /// that cannot run its subject has learned nothing, and reporting that as a failure would train
    /// people to ignore it.
    /// </para>
    /// </summary>
    [TestClass]
    public class PythonAndKernelCoDebugTests
    {
        private const int StepTimeoutMs = 60000;

        private string _repositoryRoot;
        private string _fixtureDirectory;
        private string _scriptPath;
        private string _kernelPath;
        private string _stateDirectory;

        private BreakpointManager _breakpoints;
        private PythonDebugEngine _python;
        private CudaKernelDebugSession _kernel;

        private readonly List<DebugStoppedEventArgs> _stops = new List<DebugStoppedEventArgs>();
        private readonly SemaphoreSlim _stopArrived = new SemaphoreSlim(0);
        private readonly ManualResetEventSlim _ended = new ManualResetEventSlim(false);
        private readonly StringBuilder _output = new StringBuilder();

        private static string FindRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        [TestInitialize]
        public void SetUp()
        {
            _repositoryRoot = FindRepositoryRoot();
            if (_repositoryRoot == null) Assert.Inconclusive("repository root not found");

            _fixtureDirectory = Path.Combine(_repositoryRoot, "tests", "fixtures", "python", "cuda_codebug");
            _scriptPath = Path.Combine(_fixtureDirectory, "main.py");
            _kernelPath = Path.Combine(_fixtureDirectory, "saxpy.cu");
            if (!File.Exists(_scriptPath) || !File.Exists(_kernelPath))
                Assert.Inconclusive("the cuda_codebug fixture is missing");

            _stateDirectory = Path.Combine(Path.GetTempPath(), "helix-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_stateDirectory);
            _breakpoints = new BreakpointManager(_stateDirectory);
        }

        [TestCleanup]
        public void TearDown()
        {
            try { _kernel?.Dispose(); } catch { }
            try { _python?.Dispose(); } catch { }
            _kernel = null;
            _python = null;
            try { Directory.Delete(_stateDirectory, true); } catch { }
        }

        /// <summary>The 1-based line of the single line in <paramref name="path"/> containing <paramref name="needle"/>.</summary>
        private static int LineContaining(string path, string needle)
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(needle)) return i + 1;
            Assert.Fail("no line in " + Path.GetFileName(path) + " contains " + needle);
            return 0;
        }

        private DebugStoppedEventArgs WaitForStop(int index, string what)
        {
            Assert.IsTrue(_stopArrived.Wait(StepTimeoutMs),
                "timed out waiting for " + what + ". Output so far:\n" + _output);
            lock (_stops) return _stops[index];
        }

        private void Record(object sender, DebugStoppedEventArgs e)
        {
            lock (_stops) _stops.Add(e);
            _stopArrived.Release();
        }

        [TestMethod]
        public void OneSessionStopsInPythonThenInTheKernelThenInPythonAgain()
        {
            string bridge = Path.Combine(_repositoryRoot, "artifacts", "bin",
                "HelixStudio.CudaBridge", "Debug", "net472", "helix_cuda_bridge.exe");
            if (!File.Exists(bridge)) Assert.Inconclusive("helix_cuda_bridge.exe is not built");

            var interpreters = new PythonInterpreterManager();
            PythonInterpreter interpreter = interpreters.DiscoverDefault();
            if (interpreter == null) Assert.Inconclusive("no Python interpreter on this machine");

            int pythonLine1 = LineContaining(_scriptPath, "PYTHON BREAKPOINT 1");
            int pythonLine2 = LineContaining(_scriptPath, "PYTHON BREAKPOINT 2");
            int kernelLine = LineContaining(_kernelPath, "data[i] = value * factor;");

            _breakpoints.AddBreakpoint(_scriptPath, pythonLine1);
            _breakpoints.AddBreakpoint(_scriptPath, pythonLine2);
            _breakpoints.AddBreakpoint(_kernelPath, kernelLine);

            // --- the CUDA half: listen before Python starts, because the port goes in its env ---
            _kernel = new CudaKernelDebugSession(_breakpoints) { DebugThread = new Dim3(6, 0, 0) };
            Assert.IsTrue(_kernel.HasAnythingToStopFor());
            _kernel.Stopped += Record;
            _kernel.Listen();

            // --- the Python half ---
            string shim = Path.Combine(_repositoryRoot, "src", "python");
            _python = new PythonDebugEngine(interpreters, _breakpoints)
            {
                PythonPath = shim + Path.PathSeparator + Path.Combine(shim, "helix_cuda_bootstrap"),
                ExtraEnvironment = new Dictionary<string, string>
                {
                    { "HELIX_CUDA_EMULATE", "1" },
                    { "HELIX_CUDA_BRIDGE", bridge },
                    { KernelDebugProtocol.PortVariable, _kernel.Port.ToString() },
                },
            };
            _python.Stopped += Record;
            _python.OutputReceived += (s, e) => { lock (_output) _output.Append(e.Text); };
            _python.SessionEnded += (s, e) => _ended.Set();
            _python.StartDebuggingAsync(_scriptPath, _fixtureDirectory);

            // 1. Python stops first -- before the program has even started the bridge.
            DebugStoppedEventArgs first = WaitForStop(0, "the first Python breakpoint");
            Assert.AreEqual(pythonLine1, first.Line, "stop 1 should be the Python breakpoint");
            // debugpy echoes the path it was given, which can differ from ours in case only.
            Assert.IsTrue(StringComparer.OrdinalIgnoreCase.Equals(first.FilePath, _scriptPath),
                "stop 1 is in the wrong file: " + first.FilePath);
            Assert.IsFalse(_kernel.IsBridgeConnected, "nothing has touched CUDA yet");

            DebugVariable before = _python.Evaluate("list(host)", _python.GetCallStack()[0].Id, "watch");
            Assert.IsNotNull(before);
            Assert.AreEqual("[1, 2, 3, 4, 5, 6, 7, 8]", before.Value);

            // 2. Resume Python. It compiles the kernel (starting the bridge, which dials us) and
            //    launches -- and the session lands inside the KERNEL with no further instruction.
            _python.Continue();
            DebugStoppedEventArgs inKernel = WaitForStop(1, "the kernel breakpoint");
            Assert.AreEqual("breakpoint", inKernel.Reason);
            Assert.AreEqual(_kernelPath, inKernel.FilePath, "the hand-off must land in the .cu file");
            Assert.AreEqual(kernelLine, inKernel.Line);
            Assert.IsTrue(_kernel.IsPaused);
            Assert.IsTrue(_kernel.IsInsideKernel);
            Assert.IsFalse(_python.IsPaused, "Python is blocked on the launch, not paused at a line");

            List<DebugStackFrame> kernelFrames = _kernel.GetCallStack();
            Assert.AreEqual("scale", kernelFrames[0].Name);
            Dictionary<string, string> locals = KernelLocals(kernelFrames[0].Id);
            Assert.AreEqual("(6,0,0)", locals["threadIdx"], "the lane the session asked to follow");
            Assert.AreEqual("(0,0,0)", locals["blockIdx"]);
            Assert.AreEqual("(8,1,1)", locals["blockDim"]);
            Assert.AreEqual("6", locals["i"]);
            Assert.AreEqual("7", locals["value"], "data[6] before it is scaled");
            Assert.AreEqual("3", locals["factor"]);

            // 3. Resume the kernel. Control goes back to Python by itself when the launch returns.
            _kernel.Continue();
            DebugStoppedEventArgs back = WaitForStop(2, "the second Python breakpoint");
            Assert.AreEqual(pythonLine2, back.Line, "control must return to Python after the kernel");
            Assert.IsTrue(_python.IsPaused);
            Assert.IsFalse(_kernel.IsPaused);

            DebugVariable after = _python.Evaluate("list(host)", _python.GetCallStack()[0].Id, "watch");
            Assert.IsNotNull(after);
            Assert.AreEqual("[3, 6, 9, 12, 15, 18, 21, 24]", after.Value,
                "EVERY element must be scaled: the whole grid ran while one lane was parked");

            _python.Continue();
            Assert.IsTrue(_ended.Wait(StepTimeoutMs), "the program never finished");
            lock (_output)
                StringAssert.Contains(_output.ToString(), "scaled = [3, 6, 9, 12, 15, 18, 21, 24]");
        }

        private Dictionary<string, string> KernelLocals(int frameId)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DebugScope scope in _kernel.GetScopes(frameId))
                foreach (DebugVariable variable in _kernel.GetVariables(scope.VariablesReference))
                    result[variable.Name] = variable.Value;
            return result;
        }
    }
}
