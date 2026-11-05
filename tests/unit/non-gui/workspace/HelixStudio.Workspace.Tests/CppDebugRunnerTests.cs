using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Workspace.Cpp;

namespace HelixPythonNamespace.Tests
{
    /// <summary>
    /// Verifies the gdb <c>--interpreter=mi2</c> transcript parser used by <see cref="CppDebugRunner"/> for
    /// plain (non-CUDA) C/C++ debugging. GDB/MI is the same async-record family netcoredbg speaks for
    /// <see cref="HelixPythonNamespace.Workspace.CSharp.CSharpDebugRunner"/>, so this transcript (and the parser) mirrors
    /// <c>CSharpDebugRunnerTests</c> almost verbatim — the mapping is correct the moment gdb is installed.
    /// (Neither gdb nor lldb is guaranteed present on the CI box — the runner reports tool-absence honestly.)
    /// </summary>
    [TestClass]
    public class CppDebugRunnerTests
    {
        private const string Transcript =
            "=thread-group-added,id=\"i1\"\n" +
            "^done\n" +
            "*stopped,reason=\"breakpoint-hit\",disp=\"keep\",bkptno=\"1\",frame={addr=\"0x0\",func=\"compute\",args=[{name=\"n\",value=\"5\"}],file=\"t.cpp\",fullname=\"C:\\\\p\\\\t.cpp\",line=\"5\"},thread-id=\"1\",stopped-threads=\"all\"\n" +
            "^done,variables=[{name=\"n\",value=\"5\"},{name=\"total\",value=\"0\"},{name=\"i\",value=\"1\"}]\n" +
            "*stopped,reason=\"end-stepping-range\",frame={func=\"compute\",args=[],file=\"t.cpp\",line=\"6\"},thread-id=\"1\"\n" +
            "^done,variables=[{name=\"n\",value=\"5\"},{name=\"total\",value=\"1\"},{name=\"i\",value=\"1\"}]\n";

        [TestMethod]
        public void Parses_BreakpointHit_WithFrameAndLocals()
        {
            var stops = CppDebugRunner.ParseMiOutput(Transcript);
            Assert.IsTrue(stops.Count >= 2);
            var bp = stops[0];
            Assert.AreEqual("breakpoint", bp.Kind);
            Assert.AreEqual("compute", bp.Method);
            Assert.AreEqual("t.cpp", bp.File);
            Assert.AreEqual(5, bp.Line);
            Assert.AreEqual("5", bp.Locals["n"]);
            Assert.AreEqual("0", bp.Locals["total"]);
            Assert.AreEqual("1", bp.Locals["i"]);
        }

        [TestMethod]
        public void Parses_Step_AsStepKind_WithMutatedState()
        {
            var stops = CppDebugRunner.ParseMiOutput(Transcript);
            var step = stops.First(s => s.Kind == "step");
            Assert.AreEqual(6, step.Line);
            Assert.AreEqual("1", step.Locals["total"], "total advanced 0 -> 1");
        }

        [TestMethod]
        public void EmptyOrNonMi_YieldsNoStops()
        {
            Assert.AreEqual(0, CppDebugRunner.ParseMiOutput(null).Count);
            Assert.AreEqual(0, CppDebugRunner.ParseMiOutput("^done\n(gdb)\n").Count);
        }

        [TestMethod]
        public void HonestToolAbsence_WhenNeitherGdbNorLldbPresent()
        {
            // On a box without gdb/lldb the runner must report unavailability, never fabricate a stop.
            // Absence is simulated, so the branch runs whether or not this machine has a debugger.
            CppDebugRunner.SimulateNoDebuggerForTests = true;
            try
            {
                Assert.IsFalse(CppDebugRunner.IsAvailable);
                var r = CppDebugRunner.Debug(System.IO.Path.GetTempPath(), "t.cpp", 5);
                Assert.IsFalse(r.Available);
                Assert.IsNotNull(r.Reason);
                StringAssert.Contains(r.Reason, "gdb");
                Assert.AreEqual(0, r.Stops.Count);
            }
            finally
            {
                CppDebugRunner.SimulateNoDebuggerForTests = false;
            }
        }

        // The lldb-fallback path (RustDebugRunner's --batch pattern) uses a separate parser; verify it
        // independently of the gdb-MI one so a regression in either can't hide behind the other.
        private const string LldbTranscript =
            "(lldb) breakpoint set --file t.cpp --line 5\n" +
            "Breakpoint 1: where = t.exe`compute(n=5) + 16 at t.cpp:5, address = 0x1400\n" +
            "(lldb) run\n" +
            "Process 1234 stopped\n" +
            "* thread #1, stop reason = breakpoint 1.1\n" +
            "    frame #0: 0x1400 t.exe`compute(n=5) at t.cpp:5\n" +
            "(lldb) frame variable\n" +
            "(int) n = 5\n" +
            "(int) total = 0\n" +
            "(lldb) thread step-over\n" +
            "Process 1234 stopped\n" +
            "* thread #1, stop reason = step over\n" +
            "    frame #0: 0x1410 t.exe`compute(n=5) at t.cpp:6\n" +
            "(lldb) frame variable\n" +
            "(int) n = 5\n" +
            "(int) total = 1\n";

        [TestMethod]
        public void ParsesLldbTranscript_BreakpointAndStep()
        {
            var stops = CppDebugRunner.ParseLldbOutput(LldbTranscript);
            Assert.AreEqual(2, stops.Count);
            Assert.AreEqual("breakpoint", stops[0].Kind);
            Assert.AreEqual("compute", stops[0].Method);
            Assert.AreEqual("t.cpp", stops[0].File);
            Assert.AreEqual(5, stops[0].Line);
            Assert.AreEqual("0", stops[0].Locals["total"]);

            Assert.AreEqual("step", stops[1].Kind);
            Assert.AreEqual(6, stops[1].Line);
            Assert.AreEqual("1", stops[1].Locals["total"]);
        }
    }
}
