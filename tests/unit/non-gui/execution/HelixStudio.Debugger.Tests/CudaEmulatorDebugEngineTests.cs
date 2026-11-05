namespace HelixPythonNamespace.Debugger.Tests
{
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.CudaEmulator;
    using HelixPythonNamespace.Debugger;

    [TestClass]
    public class CudaEmulatorDebugEngineTests
    {
        // Kernel body line 3 (`a[i] = i;`) is where we set the breakpoint.
        private const string Source =
            "__global__ void k(int* a) {\n" +
            "    int i = threadIdx.x;\n" +
            "    a[i] = i;\n" +
            "}\n" +
            "int main() {\n" +
            "    int* d; cudaMalloc(&d, 16);\n" +
            "    k<<<1, 1>>>(d);\n" +
            "    return 0;\n" +
            "}\n";

        [TestMethod]
        public void StepThenBreakpointStopsInKernelWithThreadCoords()
        {
            string file = "kernel.cu";
            BreakpointManager bps = NewBreakpointManager();
            // Fresh manager may fail to persist; add breakpoint on the kernel body line.
            bps.AddBreakpoint(file, 3);

            using (CudaEmulatorDebugEngine engine = new CudaEmulatorDebugEngine(bps))
            {
                object gate = new object();
                bool captured = false;
                string capturedReason = null;
                string topFrame = null;
                bool hasThreadIdx = false;
                ManualResetEventSlim ended = new ManualResetEventSlim(false);

                engine.Stopped += delegate (object s, DebugStoppedEventArgs e)
                {
                    if (e.Reason == "breakpoint")
                    {
                        List<DebugStackFrame> stack = engine.GetCallStack();
                        List<DebugScope> scopes = engine.GetScopes(0);
                        List<DebugVariable> vars = scopes.Count > 0
                            ? engine.GetVariables(scopes[0].VariablesReference)
                            : new List<DebugVariable>();
                        lock (gate)
                        {
                            captured = true;
                            capturedReason = e.Reason;
                            topFrame = stack.Count > 0 ? stack[0].Name : null;
                            foreach (DebugVariable v in vars) if (v.Name == "threadIdx") hasThreadIdx = true;
                        }
                        engine.StopDebugging();   // end the session
                    }
                    else
                    {
                        engine.Continue();        // run to the breakpoint
                    }
                };
                engine.SessionEnded += delegate { ended.Set(); };

                engine.Start(file, Source, new Dim3(0, 0, 0), new Dim3(0, 0, 0));

                bool finished = ended.Wait(15000);
                Assert.IsTrue(finished, "debug session did not end within timeout");
                lock (gate)
                {
                    Assert.IsTrue(captured, "breakpoint was never hit");
                    Assert.AreEqual("breakpoint", capturedReason);
                    Assert.AreEqual("k", topFrame);
                    Assert.IsTrue(hasThreadIdx, "kernel frame should surface threadIdx");
                }
                Assert.IsFalse(engine.IsDebugging);
            }
        }

        [TestMethod]
        public void StepIntoStopsOnFirstStatement()
        {
            string file = "kernel2.cu";
            BreakpointManager bps = NewBreakpointManager();  // no breakpoints

            using (CudaEmulatorDebugEngine engine = new CudaEmulatorDebugEngine(bps))
            {
                ManualResetEventSlim firstStop = new ManualResetEventSlim(false);
                bool sawStep = false;
                ManualResetEventSlim ended = new ManualResetEventSlim(false);

                engine.Stopped += delegate (object s, DebugStoppedEventArgs e)
                {
                    if (!firstStop.IsSet)
                    {
                        if (e.Reason == "step") sawStep = true;
                        firstStop.Set();
                        engine.StopDebugging();
                    }
                    else
                    {
                        engine.StopDebugging();
                    }
                };
                engine.SessionEnded += delegate { ended.Set(); };

                engine.Start(file, Source, new Dim3(0, 0, 0), new Dim3(0, 0, 0));

                Assert.IsTrue(firstStop.Wait(15000), "no initial step stop");
                Assert.IsTrue(ended.Wait(15000), "session did not end");
                Assert.IsTrue(sawStep, "first stop should be a step (StepInto stops on entry)");
            }
        }

        // Multi-thread kernel with two locals, so we can verify per-lane values.
        private const string MultiThread =
            "__global__ void k(int* a) {\n" +
            "    int i = threadIdx.x;\n" +   // line 2
            "    int sq = i * i;\n" +        // line 3
            "    a[i] = sq;\n" +            // line 4  <-- breakpoint
            "}\n" +
            "int main() {\n" +
            "    int* d; cudaMalloc(&d, 64);\n" +
            "    k<<<1, 8>>>(d);\n" +
            "    return 0;\n" +
            "}\n";

        [TestMethod]
        public void BreakpointInspectsSelectedThreadLocalValues()
        {
            // The distinctive CUDA-debugger feature: inspect ONE chosen thread lane.
            // Debugging lane (3,0,0) must show its own locals: i==3, sq==9.
            string file = "multi.cu";
            BreakpointManager bps = NewBreakpointManager();
            bps.AddBreakpoint(file, 4);

            using (CudaEmulatorDebugEngine engine = new CudaEmulatorDebugEngine(bps))
            {
                object gate = new object();
                string iVal = null, sqVal = null, tIdx = null, topFrame = null;
                ManualResetEventSlim ended = new ManualResetEventSlim(false);

                engine.Stopped += delegate (object s, DebugStoppedEventArgs e)
                {
                    if (e.Reason == "breakpoint")
                    {
                        List<DebugStackFrame> stack = engine.GetCallStack();
                        List<DebugScope> scopes = engine.GetScopes(0);
                        List<DebugVariable> vars = scopes.Count > 0
                            ? engine.GetVariables(scopes[0].VariablesReference)
                            : new List<DebugVariable>();
                        lock (gate)
                        {
                            topFrame = stack.Count > 0 ? stack[0].Name : null;
                            foreach (DebugVariable v in vars)
                            {
                                if (v.Name == "i") iVal = v.Value;
                                if (v.Name == "sq") sqVal = v.Value;
                                if (v.Name == "threadIdx") tIdx = v.Value;
                            }
                        }
                        engine.StopDebugging();
                    }
                    else
                    {
                        engine.Continue();
                    }
                };
                engine.SessionEnded += delegate { ended.Set(); };

                engine.Start(file, MultiThread, new Dim3(0, 0, 0), new Dim3(3, 0, 0));

                Assert.IsTrue(ended.Wait(15000), "debug session did not end within timeout");
                lock (gate)
                {
                    Assert.AreEqual("k", topFrame);
                    Assert.AreEqual("3", iVal, "local i should equal the debugged lane's threadIdx.x");
                    Assert.AreEqual("9", sqVal, "local sq should be i*i for the debugged lane");
                    Assert.AreEqual("(3,0,0)", tIdx);
                }
            }
        }

        [TestMethod]
        public void StepOverWalksStatementsAndAdvances()
        {
            // StepOver (no breakpoints) must produce successive 'step' stops on
            // advancing lines, then end cleanly — verifies the step-over path.
            string file = "walk.cu";
            BreakpointManager bps = NewBreakpointManager();  // none

            using (CudaEmulatorDebugEngine engine = new CudaEmulatorDebugEngine(bps))
            {
                object gate = new object();
                List<int> lines = new List<int>();
                ManualResetEventSlim ended = new ManualResetEventSlim(false);

                engine.Stopped += delegate (object s, DebugStoppedEventArgs e)
                {
                    lock (gate)
                    {
                        List<DebugStackFrame> stack = engine.GetCallStack();
                        if (stack.Count > 0) lines.Add(stack[0].Line);
                        if (lines.Count >= 4) { engine.StopDebugging(); return; }
                    }
                    engine.StepOver();
                };
                engine.SessionEnded += delegate { ended.Set(); };

                engine.Start(file, MultiThread, new Dim3(0, 0, 0), new Dim3(0, 0, 0));

                Assert.IsTrue(ended.Wait(15000), "debug session did not end within timeout");
                lock (gate)
                {
                    Assert.IsTrue(lines.Count >= 2, "step-over should produce multiple stops");
                    HashSet<int> distinct = new HashSet<int>(lines);
                    Assert.IsTrue(distinct.Count >= 2, "step-over should advance across distinct lines: " + string.Join(",", lines));
                }
            }
        }

        private static BreakpointManager NewBreakpointManager()
        {
            // The default ctor may touch %LocalAppData%; that's fine — we only use the
            // in-memory Add/HasBreakpoint API, which does not require the settings file.
            return new BreakpointManager();
        }
    }
}
