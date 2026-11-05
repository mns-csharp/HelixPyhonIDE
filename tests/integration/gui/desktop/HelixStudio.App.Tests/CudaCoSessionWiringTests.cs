using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using HelixPythonNamespace.App.Services;
using HelixPythonNamespace.CudaEmulator;
using HelixPythonNamespace.Debugger;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// The decision <see cref="DebugController"/> makes before every Python debug launch: whether
    /// to stand up a CUDA kernel co-session, and what the debuggee's environment ends up saying.
    ///
    /// <para>
    /// Worth pinning separately from the end-to-end session because it is the half that is INVISIBLE
    /// when it goes wrong. A co-session that is never created produces a run in which the Python
    /// breakpoints all work perfectly and the kernel breakpoint is simply ignored — which reads to
    /// a user as "kernel debugging is broken", with no error anywhere to explain it.
    /// </para>
    /// </summary>
    [TestClass]
    public class CudaCoSessionWiringTests
    {
        private Form _owner;
        private DebugController _controller;
        private string _stateDirectory;
        private string _kernelPath;

        [TestInitialize]
        public void SetUp()
        {
            // Never shown — DebugController only needs it as an invoke target for marshalling.
            _owner = new Form();
            _controller = new DebugController(_owner);

            _stateDirectory = Path.Combine(Path.GetTempPath(), "helix-cosession-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_stateDirectory);
            _kernelPath = Path.Combine(_stateDirectory, "k.cu");
            File.WriteAllText(_kernelPath, "extern \"C\" __global__ void k(int* d) { d[0] = 1; }\n");
        }

        [TestCleanup]
        public void TearDown()
        {
            try { _controller?.Dispose(); } catch { }
            try { _owner?.Dispose(); } catch { }
            _controller = null;
            _owner = null;
            try { Directory.Delete(_stateDirectory, true); } catch { }
        }

        private static Dictionary<string, string> Emulated()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "PYTHONPATH", @"C:\shim" },
                { "HELIX_CUDA_EMULATE", "1" },
                { "HELIX_CUDA_BRIDGE", @"C:\bin\helix_cuda_bridge.exe" },
            };
        }

        [TestMethod]
        public void EmulatedRunWithAKernelBreakpointGetsAListeningSession()
        {
            _controller.Breakpoints.AddBreakpoint(_kernelPath, 1);
            _controller.ExtraEnvironment = Emulated();

            IDictionary<string, string> environment = _controller.PrepareCudaCoSession();

            Assert.IsNotNull(_controller.KernelSession, "a kernel breakpoint must arm the co-session");
            Assert.IsTrue(_controller.KernelSession.Port > 0, "the session must be listening");
            string port;
            Assert.IsTrue(environment.TryGetValue("HELIX_CUDA_DEBUG_PORT", out port),
                "the debuggee has no way to find the debugger without the port");
            Assert.AreEqual(_controller.KernelSession.Port.ToString(), port);
            // The rest of the run environment must survive untouched.
            Assert.AreEqual(@"C:\shim", environment["PYTHONPATH"]);
            Assert.AreEqual("1", environment["HELIX_CUDA_EMULATE"]);
        }

        [TestMethod]
        public void RunWithoutEmulationGetsNoSession()
        {
            _controller.Breakpoints.AddBreakpoint(_kernelPath, 1);
            _controller.ExtraEnvironment = new Dictionary<string, string> { { "PYTHONPATH", @"C:\shim" } };

            IDictionary<string, string> environment = _controller.PrepareCudaCoSession();

            Assert.IsNull(_controller.KernelSession,
                "with a real GPU toolchain selected the kernels do not run in our emulator, so there is nothing to attach to");
            Assert.IsFalse(environment.ContainsKey("HELIX_CUDA_DEBUG_PORT"));
        }

        [TestMethod]
        public void EmulatedRunWithNoKernelBreakpointGetsNoSession()
        {
            _controller.Breakpoints.AddBreakpoint(Path.Combine(_stateDirectory, "main.py"), 4);
            _controller.ExtraEnvironment = Emulated();

            IDictionary<string, string> environment = _controller.PrepareCudaCoSession();

            Assert.IsNull(_controller.KernelSession,
                "a .py breakpoint is debugpy's; nothing here could ever stop, so nothing should listen");
            Assert.IsFalse(environment.ContainsKey("HELIX_CUDA_DEBUG_PORT"));
        }

        /// <summary>
        /// Break-on-kernel-entry arms the session on its own — that is the point of it, since an
        /// inline kernel has no file to hold a breakpoint until it has stopped once.
        /// </summary>
        [TestMethod]
        public void BreakOnKernelEntryArmsTheSessionWithNoBreakpointAtAll()
        {
            _controller.StopOnKernelEntry = true;
            _controller.ExtraEnvironment = Emulated();

            IDictionary<string, string> environment = _controller.PrepareCudaCoSession();

            Assert.IsNotNull(_controller.KernelSession);
            Assert.IsTrue(_controller.KernelSession.StopOnKernelEntry);
            Assert.IsTrue(environment.ContainsKey("HELIX_CUDA_DEBUG_PORT"));
        }

        /// <summary>The chosen lane reaches the session, or the Debug ▸ CUDA Debug Lane setting is decoration.</summary>
        [TestMethod]
        public void TheChosenLaneReachesTheSession()
        {
            _controller.Breakpoints.AddBreakpoint(_kernelPath, 1);
            _controller.CudaDebugBlock = new Dim3(1, 2, 0);
            _controller.CudaDebugThread = new Dim3(7, 0, 0);
            _controller.ExtraEnvironment = Emulated();

            _controller.PrepareCudaCoSession();

            CudaKernelDebugSession session = _controller.KernelSession;
            Assert.IsNotNull(session);
            Assert.AreEqual(1UL, session.DebugBlock.X);
            Assert.AreEqual(2UL, session.DebugBlock.Y);
            Assert.AreEqual(7UL, session.DebugThread.X);
            Assert.AreEqual("block (1,2,0) thread (7,0,0)", session.LaneDescription());
        }

        /// <summary>
        /// A second launch must not inherit the first one's listener. Left behind, it would hold a
        /// port and, worse, the previous run's breakpoint set.
        /// </summary>
        [TestMethod]
        public void PreparingTwiceReplacesTheSession()
        {
            _controller.Breakpoints.AddBreakpoint(_kernelPath, 1);
            _controller.ExtraEnvironment = Emulated();

            _controller.PrepareCudaCoSession();
            CudaKernelDebugSession first = _controller.KernelSession;
            _controller.PrepareCudaCoSession();
            CudaKernelDebugSession second = _controller.KernelSession;

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreNotSame(first, second);
            Assert.AreNotEqual(first.Port, second.Port, "the old listener must have let its port go");
        }
    }
}
