using System;
using System.Windows.Forms;
using HelixPythonNamespace.App.Services;
using HelixPythonNamespace.Debugger;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Backend = HelixPythonNamespace.App.Services.DebugController.CudaDebugBackend;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Hard rule 9 asks for a CUDA kernel to be debuggable <b>with and without</b> the emulator, so which
    /// of the two a <c>.cu</c> file gets is a real decision. This pins it.
    ///
    /// <para>
    /// The decision is split out of <c>StartCuda</c> precisely so it can be tested without launching a
    /// debugger, because the case that matters most — asking for the real GPU on a machine that has no
    /// cuda-gdb — is the case this machine is permanently in.
    /// </para>
    /// </summary>
    [TestClass]
    public class CudaDebugBackendChoiceTests
    {
        private Form _owner;
        private DebugController _controller;

        [TestInitialize]
        public void SetUp()
        {
            // Never shown; the controller only needs an invoke target.
            _owner = new Form();
            _controller = new DebugController(_owner);
        }

        [TestCleanup]
        public void TearDown()
        {
            try { _controller?.Dispose(); } catch { }
            try { _owner?.Dispose(); } catch { }
            _controller = null;
            _owner = null;
        }

        private static void RequireNoCudaGdb()
        {
            if (CudaGdbDebugEngine.IsAvailable)
                Assert.Inconclusive("a cuda-gdb IS installed here; this test covers its absence");
        }

        [TestMethod]
        public void AnEmulatorSolutionDebugsOnTheEmulator()
        {
            _controller.CudaBackend = Backend.Auto;
            _controller.CudaToolchainIsRealGpu = false;

            string notice;
            Assert.AreEqual(Backend.Emulator, _controller.ResolveCudaBackend(out notice));
            Assert.IsNull(notice, "the emulator is what was asked for; there is nothing to apologise for");
        }

        /// <summary>
        /// The honest-fallback case. Asking for the real GPU where there is none must not refuse to
        /// debug — but it must not pretend either.
        /// </summary>
        [TestMethod]
        public void AskingForTheRealGpuWithoutCudaGdbFallsBackAndSaysSo()
        {
            RequireNoCudaGdb();
            _controller.CudaBackend = Backend.Auto;
            _controller.CudaToolchainIsRealGpu = true;

            string notice;
            Assert.AreEqual(Backend.Emulator, _controller.ResolveCudaBackend(out notice),
                "a missing cuda-gdb must not block debugging altogether");
            Assert.IsFalse(string.IsNullOrWhiteSpace(notice),
                "a SILENT fallback is the trap: the user would read emulator behaviour as hardware behaviour");
            StringAssert.Contains(notice, "emulator");
            StringAssert.Contains(notice, "cuda-gdb");
        }

        [TestMethod]
        public void AnExplicitEmulatorChoiceIgnoresTheToolchain()
        {
            _controller.CudaBackend = Backend.Emulator;
            _controller.CudaToolchainIsRealGpu = true;

            string notice;
            Assert.AreEqual(Backend.Emulator, _controller.ResolveCudaBackend(out notice));
            Assert.IsNull(notice, "nothing was overridden, so there is nothing to report");
        }

        [TestMethod]
        public void AnExplicitCudaGdbChoiceStillFallsBackWhenTheToolIsMissing()
        {
            RequireNoCudaGdb();
            _controller.CudaBackend = Backend.CudaGdb;
            _controller.CudaToolchainIsRealGpu = false;

            string notice;
            Assert.AreEqual(Backend.Emulator, _controller.ResolveCudaBackend(out notice));
            Assert.IsFalse(string.IsNullOrWhiteSpace(notice));
        }

        /// <summary>
        /// A <c>.cu</c> still reaches the CUDA launcher either way — the backend choice happens inside
        /// it, not by routing the file somewhere else. Otherwise a machine without cuda-gdb would hand
        /// kernels to gdb, which cannot run one.
        /// </summary>
        [TestMethod]
        public void TheBackendChoiceDoesNotChangeWhichLauncherClaimsTheFile()
        {
            _controller.CudaBackend = Backend.CudaGdb;
            Assert.AreEqual("cuda", _controller.DebugLauncherForFile(@"C:\p\kernel.cu"));
            _controller.CudaBackend = Backend.Emulator;
            Assert.AreEqual("cuda", _controller.DebugLauncherForFile(@"C:\p\kernel.cu"));
        }
    }
}
