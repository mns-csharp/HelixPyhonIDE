using System;
using System.Collections.Generic;
using System.IO;
using HelixPythonNamespace.Workspace.Cuda;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.Debugger.Tests
{
    /// <summary>
    /// The cuda-gdb parser against transcripts <b>captured from a real cuda-gdb 13.4 debugging a real
    /// kernel on a real RTX 4070</b> (through WSL2 Ubuntu 24.04), not written from documentation.
    ///
    /// <para>
    /// These exist because the first run on hardware found three parser bugs that no amount of reading
    /// would have caught, and every one of them was silent:
    /// </para>
    /// <list type="number">
    /// <item>A CUDA stop record is <c>*stopped,CudaFocus={...},reason="breakpoint-hit"</c> — so
    /// <c>reason</c> is NOT adjacent to <c>*stopped</c> as it is in plain gdb. Anchoring them together
    /// dropped every device breakpoint hit entirely.</item>
    /// <item>The focus arrives as <c>blockIdx="(0,0,0)"</c>/<c>threadIdx="(0,0,0)"</c> inside that
    /// record — not only as the <c>[Switching focus to CUDA kernel ...]</c> console banner. Matching
    /// just the banner made every kernel stop look like host code.</item>
    /// <item><c>-stack-list-variables</c> marks parameters with an extra field:
    /// <c>{name="data",arg="1",value="0x70a600000"}</c>. Requiring name and value to be adjacent
    /// dropped every kernel parameter.</item>
    /// </list>
    /// <para>
    /// Keeping the real bytes in the tree is the point. A constructed transcript tests the parser
    /// against my own assumptions; this tests it against the tool.
    /// </para>
    /// </summary>
    [TestClass]
    public class CudaGdbRealTranscriptTests
    {
        private static string FixtureDirectory()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                    return Path.Combine(dir.FullName, "tests", "fixtures", "cuda", "cuda-gdb-mi");
                dir = dir.Parent;
            }
            return null;
        }

        private static string Load(string name)
        {
            string directory = FixtureDirectory();
            if (directory == null) Assert.Inconclusive("repository root not found");
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) Assert.Inconclusive("missing captured transcript: " + path);
            return File.ReadAllText(path);
        }

        /// <summary>
        /// A real breakpoint hit inside a kernel: the stop is found at all, on the right line, with the
        /// lane from the record's own <c>CudaFocus</c> field and every variable including the
        /// parameters.
        /// </summary>
        [TestMethod]
        public void ARealDeviceBreakpointHitIsParsedWhole()
        {
            List<CudaGdbStop> stops = CudaGdbRunner.ParseMiOutput(Load("breakpoint-hit-thread0.mi.txt"));

            Assert.AreEqual(1, stops.Count, "the device breakpoint hit must be found");
            CudaGdbStop stop = stops[0];
            Assert.AreEqual("breakpoint", stop.Kind);
            Assert.AreEqual("scale", stop.Method);
            Assert.AreEqual("saxpy.cu", stop.File);
            Assert.AreEqual(9, stop.Line);

            Assert.IsTrue(stop.Focus.IsDeviceFocus, "the lane comes from the record's CudaFocus field");
            Assert.AreEqual("block (0,0,0) thread (0,0,0)", stop.Focus.ToString());
            Assert.AreEqual(0, stop.Focus.Device);
            Assert.AreEqual(0, stop.Focus.Sm);
            Assert.AreEqual(0, stop.Focus.Warp);
            Assert.AreEqual(0, stop.Focus.Lane);

            // Locals AND parameters. The parameters are the ones the arg="1" field used to hide.
            Assert.AreEqual("0", stop.Locals["i"]);
            Assert.AreEqual("1", stop.Locals["value"], "data[0] is 1 before scaling");
            Assert.AreEqual("3", stop.Locals["factor"], "a kernel PARAMETER, marked arg=\"1\" by cuda-gdb");
            Assert.AreEqual("8", stop.Locals["n"]);
            Assert.AreEqual("0x70a600000", stop.Locals["data"]);
        }

        /// <summary>
        /// Switching focus to another lane, for real: after <c>cuda block (0,0,0) thread (6,0,0)</c>
        /// the variables are thread 6's — <c>i=6</c>, <c>value=7</c> (that is <c>data[6]</c>). The same
        /// answer the CPU emulator gives for lane 6, which is the whole point of having both.
        /// </summary>
        [TestMethod]
        public void ARealFocusSwitchMovesTheLaneAndItsVariables()
        {
            List<CudaGdbStop> stops = CudaGdbRunner.ParseMiOutput(Load("focus-switch-thread6.mi.txt"));

            Assert.AreEqual(1, stops.Count, "switching focus is not a second stop");
            CudaGdbStop stop = stops[0];
            Assert.AreEqual("block (0,0,0) thread (6,0,0)", stop.Focus.ToString(),
                "the console banner after the switch must win over the record's original focus");
            Assert.AreEqual(6, stop.Focus.Lane);
            Assert.AreEqual("6", stop.Locals["i"]);
            Assert.AreEqual("7", stop.Locals["value"], "data[6] is 7");
            Assert.AreEqual("3", stop.Locals["factor"]);
        }

        /// <summary>
        /// The session that FAILED, kept deliberately. cuda-gdb 12.0 against a CUDA-13 driver refuses
        /// the device image ("CUDA ELF Image contains unknown ABI version: 8") and never stops
        /// anywhere. The parser must report no stops rather than inventing one from the noise — and a
        /// caller that sees zero stops is what turns this into the readable "no stops" message the
        /// engine shows the user.
        /// </summary>
        [TestMethod]
        public void AVersionMismatchSessionYieldsNoStops()
        {
            string transcript = Load("abi-mismatch-failure.mi.txt");
            StringAssert.Contains(transcript, "unknown ABI version",
                "this fixture is only meaningful while it still contains the failure it records");
            Assert.AreEqual(0, CudaGdbRunner.ParseMiOutput(transcript).Count,
                "a session that never stopped must produce no stops");
        }

        /// <summary>
        /// Stepping inside a kernel, for real: break, switch to lane 6, then <c>-exec-next</c> twice.
        /// Line 8 → 9 → 11, with the lane held throughout and the locals changing as they should.
        ///
        /// <para>
        /// This one needed a second round on the GPU. The first attempt failed with
        /// <c>cuobjdump plaintext disassembly failed</c> — stepping in device code needs the
        /// disassembler, and installing cuda-gdb alone had left it out. That is worth recording:
        /// the symptom looked like a debugger defect and was a missing sibling tool.
        /// </para>
        /// </summary>
        [TestMethod]
        public void SteppingInsideARealKernelAdvancesAndKeepsTheLane()
        {
            List<CudaGdbStop> stops = CudaGdbRunner.ParseMiOutput(Load("stepping-thread6.mi.txt"));

            Assert.AreEqual(3, stops.Count, "a breakpoint hit and two steps");
            Assert.AreEqual("breakpoint", stops[0].Kind);
            Assert.AreEqual(8, stops[0].Line);
            Assert.AreEqual("step", stops[1].Kind);
            Assert.AreEqual(9, stops[1].Line);
            Assert.AreEqual("step", stops[2].Kind);
            Assert.AreEqual(11, stops[2].Line);

            // Every step record carries the focus again, so the lane is never inferred here.
            Assert.AreEqual("block (0,0,0) thread (6,0,0)", stops[1].Focus.ToString());
            Assert.AreEqual(6, stops[1].Focus.Lane);
            Assert.AreEqual("block (0,0,0) thread (6,0,0)", stops[2].Focus.ToString());

            // `value` is declared by line 8, so it is only meaningful once that line has run.
            Assert.AreEqual("7", stops[1].Locals["value"], "data[6] after line 8 executed");
            Assert.AreEqual("6", stops[1].Locals["i"]);
            Assert.IsFalse(stops[2].Locals.ContainsKey("value"),
                "past the end of the if-block, `value` has left scope");
        }

        /// <summary>
        /// gdb reports a variable it cannot read as <c>&lt;optimized out&gt;</c>. It is kept
        /// verbatim rather than blanked, because "the compiler removed this" and "this is empty"
        /// are different things and only one of them is worth changing your code over.
        /// </summary>
        [TestMethod]
        public void AnOptimisedOutVariableKeepsGdbsOwnWords()
        {
            CudaGdbStop first = CudaGdbRunner.ParseMiOutput(Load("stepping-thread6.mi.txt"))[0];
            Assert.AreEqual("<optimized out>", first.Locals["value"]);
        }

        /// <summary>
        /// The regression that matters most, stated on its own: a real CUDA stop record puts
        /// <c>CudaFocus={...}</c> before <c>reason=</c>. A parser that anchors the two together finds
        /// nothing at all, and the session looks like a debugger that ignores breakpoints.
        /// </summary>
        [TestMethod]
        public void ReasonIsNotAdjacentToStoppedInACudaRecord()
        {
            string transcript = Load("breakpoint-hit-thread0.mi.txt");
            StringAssert.Contains(transcript, "*stopped,CudaFocus=",
                "the fixture must still show the field order this test exists for");
            Assert.AreEqual(1, CudaGdbRunner.ParseMiOutput(transcript).Count);
        }
    }
}
