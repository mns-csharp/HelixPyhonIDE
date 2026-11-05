using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HelixPythonNamespace.Debugger;
using HelixPythonNamespace.Workspace.Cuda;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Debugger.Tests
{
    /// <summary>
    /// Hard rule 9's real-GPU half: <c>cuda-gdb</c>.
    ///
    /// <para>
    /// <b>What these transcripts are.</b> They are CONSTRUCTED to the documented GDB/MI record format and
    /// cuda-gdb's documented focus banner. They were NOT captured from a real cuda-gdb — there is none on
    /// this machine and none on Windows at all. So what is pinned here is the parser: given output of that
    /// shape, does it produce the right stops, lines, locals and thread coordinates. Whether a real
    /// cuda-gdb emits that shape is the part still to be checked, and the first run on real hardware is
    /// the test for it.
    /// </para>
    ///
    /// <para>
    /// Pinning the parser separately is still worth it. It is the half with all the branching — focus
    /// before the stop, focus after it, host frames, MI string escaping — and it is the half that would
    /// otherwise only ever be exercised on a machine where a failure is hard to tell apart from a driver
    /// problem.
    /// </para>
    /// </summary>
    [TestClass]
    public class CudaGdbTranscriptTests
    {
        // cuda-gdb announces a focus change on its own console line, BEFORE reporting the stop.
        private const string FocusThenStop =
            "=thread-group-added,id=\"i1\"\n"
          + "~\"NVIDIA (R) CUDA Debugger\\n\"\n"
          + "^done\n"
          + "(gdb)\n"
          + "^done,bkpt={number=\"1\",type=\"breakpoint\",disp=\"keep\",enabled=\"y\",func=\"scale\","
          + "file=\"saxpy.cu\",fullname=\"/home/u/saxpy.cu\",line=\"9\",times=\"0\"}\n"
          + "(gdb)\n"
          + "^running\n"
          + "*running,thread-id=\"all\"\n"
          + "(gdb)\n"
          + "~\"[Switching focus to CUDA kernel 0, grid 1, block (0,0,0), thread (0,0,0), "
          + "device 0, sm 0, warp 0, lane 0]\\n\"\n"
          + "*stopped,reason=\"breakpoint-hit\",disp=\"keep\",bkptno=\"1\",frame={addr=\"0x00007fff\","
          + "func=\"scale\",args=[{name=\"data\",value=\"0x7fffd00\"},{name=\"factor\",value=\"3\"}],"
          + "file=\"saxpy.cu\",fullname=\"/home/u/saxpy.cu\",line=\"9\"},thread-id=\"1\","
          + "stopped-threads=\"all\"\n"
          + "(gdb)\n"
          + "^done,variables=[{name=\"data\",value=\"0x7fffd00\"},{name=\"factor\",value=\"3\"},"
          + "{name=\"n\",value=\"8\"},{name=\"i\",value=\"0\"},{name=\"value\",value=\"1\"}]\n"
          + "(gdb)\n";

        [TestMethod]
        public void ABreakpointHitInDeviceCodeCarriesItsLaneAndLocals()
        {
            List<CudaGdbStop> stops = CudaGdbRunner.ParseMiOutput(FocusThenStop);

            Assert.AreEqual(1, stops.Count);
            CudaGdbStop stop = stops[0];
            Assert.AreEqual("breakpoint", stop.Kind);
            Assert.AreEqual("scale", stop.Method);
            Assert.AreEqual("saxpy.cu", stop.File);
            Assert.AreEqual(9, stop.Line);

            Assert.IsTrue(stop.Focus.IsDeviceFocus, "a kernel stop must report a lane");
            Assert.AreEqual(0, stop.Focus.ThreadX);
            Assert.AreEqual(0, stop.Focus.BlockX);
            Assert.AreEqual(0, stop.Focus.Device);
            Assert.AreEqual(0, stop.Focus.Lane);

            Assert.AreEqual("8", stop.Locals["n"]);
            Assert.AreEqual("0", stop.Locals["i"]);
            Assert.AreEqual("1", stop.Locals["value"]);
            Assert.AreEqual("0x7fffd00", stop.Locals["data"]);
        }

        /// <summary>
        /// The banner arrives BEFORE the stop record, so a focus seen while no stop is open has to be
        /// held and attached to the next one. Getting this backwards would silently report every kernel
        /// stop as host code.
        /// </summary>
        [TestMethod]
        public void AFocusAnnouncedBeforeTheStopIsAttachedToIt()
        {
            CudaGdbStop stop = CudaGdbRunner.ParseMiOutput(FocusThenStop)[0];
            Assert.AreEqual("block (0,0,0) thread (0,0,0)", stop.Focus.ToString());
        }

        /// <summary>
        /// Asking for a specific lane produces a SECOND banner after the stop, and that one must win —
        /// it is the lane the user chose, and the locals listed after it belong to that lane.
        /// </summary>
        [TestMethod]
        public void AFocusSwitchAfterTheStopReplacesTheLaneAndItsLocals()
        {
            string transcript = FocusThenStop
                + "~\"[Switching focus to CUDA kernel 0, grid 1, block (0,0,0), thread (6,0,0), "
                + "device 0, sm 0, warp 0, lane 6]\\n\"\n"
                + "^done\n"
                + "(gdb)\n"
                + "^done,variables=[{name=\"data\",value=\"0x7fffd00\"},{name=\"factor\",value=\"3\"},"
                + "{name=\"n\",value=\"8\"},{name=\"i\",value=\"6\"},{name=\"value\",value=\"7\"}]\n"
                + "(gdb)\n";

            List<CudaGdbStop> stops = CudaGdbRunner.ParseMiOutput(transcript);
            Assert.AreEqual(1, stops.Count, "switching focus is not a new stop");
            Assert.AreEqual("block (0,0,0) thread (6,0,0)", stops[0].Focus.ToString());
            Assert.AreEqual(6, stops[0].Focus.Lane);
            Assert.AreEqual("6", stops[0].Locals["i"], "the locals must be the chosen lane's");
            Assert.AreEqual("7", stops[0].Locals["value"]);
        }

        [TestMethod]
        public void AStepIsDistinguishedFromABreakpointHit()
        {
            string transcript = FocusThenStop
                + "*stopped,reason=\"end-stepping-range\",frame={addr=\"0x00007fff\",func=\"scale\","
                + "file=\"saxpy.cu\",fullname=\"/home/u/saxpy.cu\",line=\"10\"},thread-id=\"1\"\n"
                + "(gdb)\n"
                + "^done,variables=[{name=\"i\",value=\"0\"},{name=\"value\",value=\"1\"}]\n";

            List<CudaGdbStop> stops = CudaGdbRunner.ParseMiOutput(transcript);
            Assert.AreEqual(2, stops.Count);
            Assert.AreEqual("breakpoint", stops[0].Kind);
            Assert.AreEqual("step", stops[1].Kind);
            Assert.AreEqual(10, stops[1].Line);
            Assert.IsTrue(stops[1].Focus.IsDeviceFocus,
                "the lane carries forward across a step -- the focus does not change, so no new banner is printed");
        }

        /// <summary>
        /// The other side of carrying a lane forward: once the kernel RETURNS, the next stop is host
        /// code and must not inherit the thread index. A carried-forward coordinate beside host
        /// variables would read as "main is running in thread 6", which is not a thing.
        /// </summary>
        [TestMethod]
        public void TheLaneIsNotCarriedIntoHostCode()
        {
            string transcript = FocusThenStop
                + "*stopped,reason=\"end-stepping-range\",frame={addr=\"0x40118a\",func=\"main\","
                + "file=\"saxpy.cu\",fullname=\"/home/u/saxpy.cu\",line=\"24\"},thread-id=\"1\"\n"
                + "(gdb)\n"
                + "^done,variables=[{name=\"host\",value=\"0x55d1\"}]\n";

            List<CudaGdbStop> stops = CudaGdbRunner.ParseMiOutput(transcript);
            Assert.AreEqual(2, stops.Count);
            Assert.IsTrue(stops[0].Focus.IsDeviceFocus, "the kernel stop still has its lane");
            Assert.IsFalse(stops[1].Focus.IsDeviceFocus,
                "back in main() there is no thread index to report");
            Assert.AreEqual("main", stops[1].Method);
        }

        /// <summary>The process ending is not somewhere the user stopped, and must not appear as a stop.</summary>
        [TestMethod]
        public void ProcessExitIsNotAStop()
        {
            string transcript = FocusThenStop
                + "*stopped,reason=\"exited-normally\"\n"
                + "(gdb)\n";
            Assert.AreEqual(1, CudaGdbRunner.ParseMiOutput(transcript).Count);
        }

        /// <summary>
        /// A stop in host code has no lane. Reporting one anyway — say by carrying the last kernel's
        /// coordinate forward — would put a thread index next to variables that have no thread.
        /// </summary>
        [TestMethod]
        public void AHostStopHasNoLane()
        {
            string transcript =
                "*stopped,reason=\"breakpoint-hit\",disp=\"keep\",bkptno=\"1\",frame={addr=\"0x40118a\","
              + "func=\"main\",file=\"saxpy.cu\",fullname=\"/home/u/saxpy.cu\",line=\"22\"},"
              + "thread-id=\"1\",stopped-threads=\"all\"\n"
              + "(gdb)\n"
              + "^done,variables=[{name=\"host\",value=\"0x55d1\"},{name=\"n\",value=\"8\"}]\n";

            List<CudaGdbStop> stops = CudaGdbRunner.ParseMiOutput(transcript);
            Assert.AreEqual(1, stops.Count);
            Assert.IsFalse(stops[0].Focus.IsDeviceFocus);
            Assert.AreEqual("host", stops[0].Focus.ToString());
            Assert.AreEqual("main", stops[0].Method);
        }

        /// <summary>
        /// A stop with no source frame is gdb reporting a location it has no line for — nothing to show
        /// the user, and nowhere to put the editor's marker.
        /// </summary>
        [TestMethod]
        public void AStopWithNoSourceFrameIsIgnored()
        {
            string transcript = "*stopped,reason=\"signal-received\",signal-name=\"SIGSEGV\","
                + "frame={addr=\"0x0000000000000000\",func=\"??\"},thread-id=\"1\"\n";
            Assert.AreEqual(0, CudaGdbRunner.ParseMiOutput(transcript).Count);
        }

        /// <summary>
        /// Focus banners arrive as MI console records — the text is inside an escaped C string. Without
        /// unescaping, the coordinate regexes would still match (the parens survive), so this pins the
        /// case that actually breaks: a banner whose newline escape runs it into the next field.
        /// </summary>
        [TestMethod]
        public void MiConsoleEscapingDoesNotHideTheLane()
        {
            string transcript =
                "~\"[Switching focus to CUDA kernel 0, grid 1, block (2,1,0), thread (31,0,0), "
              + "device 0, sm 3, warp 1, lane 31]\\n\\n\"\n"
              + "*stopped,reason=\"breakpoint-hit\",bkptno=\"1\",frame={func=\"reduce\","
              + "file=\"reduce.cu\",fullname=\"/home/u/reduce.cu\",line=\"14\"},thread-id=\"1\"\n";

            CudaGdbStop stop = CudaGdbRunner.ParseMiOutput(transcript)[0];
            Assert.AreEqual("block (2,1,0) thread (31,0,0)", stop.Focus.ToString());
            Assert.AreEqual(3, stop.Focus.Sm);
            Assert.AreEqual(1, stop.Focus.Warp);
            Assert.AreEqual(31, stop.Focus.Lane);
        }

        [TestMethod]
        public void GarbageInProducesNoStopsRatherThanAnException()
        {
            Assert.AreEqual(0, CudaGdbRunner.ParseMiOutput(null).Count);
            Assert.AreEqual(0, CudaGdbRunner.ParseMiOutput(string.Empty).Count);
            Assert.AreEqual(0, CudaGdbRunner.ParseMiOutput("not a transcript at all").Count);
            Assert.AreEqual(0, CudaGdbRunner.ParseMiOutput("*stopped,reason=").Count);
        }

        // ---- the engine, when the tool is not installed ----------------------

        /// <summary>
        /// The behaviour this machine CAN verify, and the one that matters most for an untested feature:
        /// with no cuda-gdb present the engine explains why and ends. It does not hang, and it does not
        /// invent a stop.
        /// </summary>
        [TestMethod]
        public void WithNoCudaGdbInstalledTheEngineSaysSoAndEnds()
        {
            if (CudaGdbDebugEngine.IsAvailable)
                Assert.Inconclusive("a cuda-gdb IS installed here; this test covers its absence");

            string directory = Path.Combine(Path.GetTempPath(), "helix-cudagdb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "k.cu");
            File.WriteAllText(source, "__global__ void k(int* d) { d[0] = 1; }\n");
            try
            {
                var engine = new CudaGdbDebugEngine();
                var output = new List<string>();
                var ended = new ManualResetEventSlim(false);
                bool stoppedAnywhere = false;

                engine.OutputReceived += (s, e) => { lock (output) output.Add(e.Text ?? string.Empty); };
                engine.Stopped += (s, e) => stoppedAnywhere = true;
                engine.SessionEnded += (s, e) => ended.Set();

                engine.Start(source, 1);

                Assert.IsTrue(ended.Wait(30000), "the session never ended");
                Assert.IsFalse(stoppedAnywhere, "no cuda-gdb means no stops -- never a fabricated one");
                Assert.IsFalse(engine.IsDebugging);
                Assert.AreEqual(0, engine.GetCallStack().Count);

                string all;
                lock (output) all = string.Join(" ", output.ToArray());
                StringAssert.Contains(all, "cuda-gdb",
                    "the reason must name the missing tool: " + all);
                engine.Dispose();
            }
            finally { try { Directory.Delete(directory, true); } catch { } }
        }

        /// <summary>The reason has to be actionable, not just negative.</summary>
        [TestMethod]
        public void TheUnavailableReasonTellsTheUserWhatToDo()
        {
            if (CudaGdbDebugEngine.IsAvailable)
            {
                Assert.IsNull(CudaGdbDebugEngine.UnavailableReason());
                return;
            }
            string reason = CudaGdbDebugEngine.UnavailableReason();
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            StringAssert.Contains(reason, CudaGdbRunner.ExecutableVariable,
                "the override variable is the one thing a user can actually set");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                StringAssert.Contains(reason, "Windows",
                    "on Windows the honest answer is that the tool does not exist there");
        }
    }
}
