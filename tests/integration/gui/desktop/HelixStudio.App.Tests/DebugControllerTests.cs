using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App.Services;
using HelixPythonNamespace.Debugger;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Exercises the GUI debug path through <see cref="DebugController"/> — the wrapper the
    /// WinForms app uses to drive <see cref="PythonDebugEngine"/>. The CLI tests prove the
    /// engine/DAP layer; these prove the App-layer relay (engine lifecycle, event marshaling,
    /// breakpoint manager) that the CLI never touches. Self-skips when python+debugpy is
    /// absent. owner=null makes the controller invoke event callbacks synchronously (no UI
    /// message loop required) while running the exact same relay code the GUI executes.
    /// </summary>
    [TestClass]
    public class DebugControllerTests
    {
        [TestMethod]
        public void GuiDebugController_LiveSession_RelaysStopOutputAndEnd()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script,
                    "def f(n):\n" +              // 1
                    "    s = 0\n" +              // 2
                    "    for i in range(n):\n" + // 3
                    "        s += i\n" +         // 4  <- breakpoint (loop body, hit 5x)
                    "    return s\n" +           // 5
                    "print('RESULT', f(5))\n");  // 6

                using (DebugController controller = new DebugController(null))
                {
                    List<DebugStoppedEventArgs> stops = new List<DebugStoppedEventArgs>();
                    StringBuilder output = new StringBuilder();
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();

                    controller.Stopped += (s, e) =>
                    {
                        lock (gate) stops.Add(e);
                        controller.Continue();   // resume, exactly like the GUI's Continue command
                    };
                    controller.OutputReceived += (s, text) => { lock (gate) output.Append(text); };
                    controller.SessionEnded += (s, e) => ended.Set();

                    // The GUI sets breakpoints through this same manager (F9 / ToggleBreakpoint).
                    controller.Breakpoints.AddBreakpoint(script, 4);
                    Assert.IsTrue(controller.Breakpoints.HasBreakpoint(script, 4), "breakpoint should register");

                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "debug session did not end within 40s");

                    lock (gate)
                    {
                        Assert.AreEqual(5, stops.Count, "loop-body breakpoint should hit 5x");
                        foreach (DebugStoppedEventArgs e in stops)
                        {
                            Assert.AreEqual("breakpoint", e.Reason, "stop reason");
                            Assert.AreEqual(4, e.Line, "stopped line drives the editor's current-line marker");
                            Assert.IsTrue(e.FilePath != null && e.FilePath.EndsWith("t.py"),
                                "stopped file drives editor navigation; got: " + e.FilePath);
                        }
                        Assert.IsTrue(output.ToString().Contains("RESULT 10"),
                            "program output should reach the Debug Output tab; got: " + output);
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void GuiDebugController_LiveSession_ExposesCallStackLocalsAndWatch()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                // Same shape as the sample debug_target.py: factorial(n) called from main(),
                // so the stopped stack is factorial -> main -> <module>.
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script,
                    "def factorial(n):\n" +            // 1
                    "    acc = 1\n" +                  // 2
                    "    for i in range(1, n + 1):\n" +// 3
                    "        acc = acc * i\n" +        // 4  <- breakpoint (loop body)
                    "    return acc\n" +               // 5
                    "def main():\n" +                  // 6
                    "    total = 0\n" +                // 7
                    "    for k in range(1, 5):\n" +    // 8
                    "        total += factorial(k)\n" +// 9
                    "    print('RESULT', total)\n" +   // 10
                    "main()\n");                       // 11

                using (DebugController controller = new DebugController(null))
                {
                    List<DebugStackFrame> firstStack = null;
                    List<DebugVariable> firstLocals = null;
                    DebugVariable firstWatch = null;
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();
                    bool captured = false;

                    controller.Stopped += (s, e) =>
                    {
                        lock (gate)
                        {
                            // Capture the GUI-panel data exactly as DebugCoordinator does on stop:
                            // the call stack drives the Call Stack panel; the top frame's locals
                            // drive the Variables panel; Evaluate drives a Watch row.
                            if (!captured)
                            {
                                captured = true;
                                firstStack = new List<DebugStackFrame>(controller.GetCallStack());
                                if (firstStack.Count > 0)
                                    firstLocals = new List<DebugVariable>(controller.GetLocals(firstStack[0].Id));
                                firstWatch = controller.Evaluate("acc * i");
                            }
                        }
                        controller.Continue();
                    };
                    controller.SessionEnded += (s, e) => ended.Set();

                    controller.Breakpoints.AddBreakpoint(script, 4);
                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "debug session did not end within 40s");

                    lock (gate)
                    {
                        Assert.IsNotNull(firstStack, "a stop should have been captured");
                        // Call Stack panel: innermost frame first, full chain to module scope.
                        Assert.IsTrue(firstStack.Count >= 3, "expected factorial->main-><module>; got " + firstStack.Count);
                        Assert.AreEqual("factorial", firstStack[0].Name, "top frame fn");
                        Assert.AreEqual(4, firstStack[0].Line, "top frame stopped line");
                        Assert.AreEqual("main", firstStack[1].Name, "caller fn");
                        StringAssert.Contains(firstStack[2].Name, "module", "outermost frame is module scope");

                        // Variables panel: the loop-body locals at the first hit (factorial(1)).
                        Assert.IsNotNull(firstLocals, "top-frame locals should resolve");
                        DebugVariable n = firstLocals.Find(v => v.Name == "n");
                        DebugVariable acc = firstLocals.Find(v => v.Name == "acc");
                        DebugVariable i = firstLocals.Find(v => v.Name == "i");
                        Assert.IsNotNull(n, "local n present"); Assert.AreEqual("1", n.Value, "n");
                        Assert.IsNotNull(acc, "local acc present"); Assert.AreEqual("1", acc.Value, "acc");
                        Assert.IsNotNull(i, "local i present"); Assert.AreEqual("1", i.Value, "i");

                        // Watch row: expression evaluated in the stopped frame.
                        Assert.IsNotNull(firstWatch, "watch expression should evaluate");
                        Assert.AreEqual("1", firstWatch.Value, "acc*i at first hit = 1*1");
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Regression for the F9 / Debug-menu "Toggle Breakpoint" double-toggle bug :
        // the editor's ToggleBreakpointAtLine flips the margin marker AND raises BreakpointToggled,
        // which EditorCoordinator.OpenInEditor routes to BreakpointManager.ToggleBreakpoint. The
        // old DebugCoordinator.ToggleBreakpoint ALSO toggled the manager directly, so the two
        // toggles cancelled — the red margin dot showed but NO breakpoint was registered, and the
        // debugger ran straight past it. The fix makes the editor the single channel. This locks
        // the invariant: the manager must end up with exactly one breakpoint, not zero.
        [TestMethod]
        public void BreakpointToggle_SingleChannel_RegistersExactlyOne()
        {
            BreakpointManager mgr = new BreakpointManager();
            const string file = @"C:\proj\_debug_demo.py";
            const int line = 11;

            // Editor stand-in: ToggleBreakpointAtLine flips a marker and raises BreakpointToggled,
            // wired (as OpenInEditor does) to mgr.ToggleBreakpoint — the single source of truth.
            bool marker = false;
            EventHandler<int> breakpointToggled = (s, ln) => mgr.ToggleBreakpoint(file, ln);
            Action f9 = () => { marker = !marker; breakpointToggled(null, line); };

            // One F9 press -> one registered breakpoint + visible marker.
            f9();
            Assert.IsTrue(marker, "margin marker should be set");
            Assert.IsTrue(mgr.HasBreakpoint(file, line), "exactly one F9 must REGISTER the breakpoint");
            Assert.AreEqual(1, mgr.GetBreakpoints(file).Count, "no phantom/duplicate breakpoints");

            // Second F9 press -> cleared (marker + manager stay in sync).
            f9();
            Assert.IsFalse(marker, "second F9 clears the marker");
            Assert.IsFalse(mgr.HasBreakpoint(file, line), "second F9 clears the breakpoint");

            // The bug, made explicit: toggling the manager an EXTRA time per F9 (the old
            // coordinator's direct call on top of the editor event) nets to zero.
            mgr.ToggleBreakpoint(file, line); // editor event
            mgr.ToggleBreakpoint(file, line); // stray direct call (old bug)
            Assert.IsFalse(mgr.HasBreakpoint(file, line),
                "double-toggle per gesture leaves the dot but no breakpoint — the bug that was fixed");
        }

        // A persistent watch (DebugCoordinator keeps the expression and re-renders it every stop)
        // is only useful if the controller re-evaluates the expression against the CURRENT frame
        // each time. This guards that foundation: the same expression yields the changing values
        // as execution progresses (not a stale first-hit value).
        [TestMethod]
        public void GuiDebugController_LiveSession_WatchReevaluatesPerStop()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script,
                    "def factorial(n):\n" +            // 1
                    "    acc = 1\n" +                  // 2
                    "    for i in range(1, n + 1):\n" +// 3
                    "        acc = acc * i\n" +        // 4  <- breakpoint (acc/i change each hit)
                    "    return acc\n" +               // 5
                    "def main():\n" +                  // 6
                    "    for k in range(1, 5):\n" +    // 7
                    "        factorial(k)\n" +         // 8
                    "main()\n");                        // 9

                using (DebugController controller = new DebugController(null))
                {
                    List<string> watchValues = new List<string>();
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();

                    controller.Stopped += (s, e) =>
                    {
                        // Evaluate the same watch expression the GUI's Watch box would, in the
                        // stopped frame, on every hit.
                        DebugVariable v = controller.Evaluate("acc * i");
                        lock (gate) watchValues.Add(v != null ? v.Value : "<null>");
                        controller.Continue();
                    };
                    controller.SessionEnded += (s, e) => ended.Set();

                    controller.Breakpoints.AddBreakpoint(script, 4);
                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "debug session did not end within 40s");

                    lock (gate)
                    {
                        Assert.IsTrue(watchValues.Count >= 4, "expected several breakpoint hits; got " + watchValues.Count);
                        Assert.AreEqual("1", watchValues[0], "acc*i at the very first hit = 1*1");
                        // Re-evaluation: the watch must not be frozen at its first value.
                        bool changed = false;
                        foreach (string val in watchValues) if (val != watchValues[0]) { changed = true; break; }
                        Assert.IsTrue(changed, "watch expression must re-evaluate per stop, not stay '1'; got: "
                            + string.Join(",", watchValues));
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void GuiDebugController_LiveSession_StepInOverOut()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script,
                    "def factorial(n):\n" +            // 1
                    "    acc = 1\n" +                  // 2  <- step-into lands in the body
                    "    for i in range(1, n + 1):\n" +// 3
                    "        acc = acc * i\n" +        // 4
                    "    return acc\n" +               // 5
                    "def main():\n" +                  // 6
                    "    total = 0\n" +                // 7
                    "    for k in range(1, 5):\n" +    // 8
                    "        total += factorial(k)\n" +// 9  <- breakpoint (call site)
                    "    print('RESULT', total)\n" +   // 10
                    "main()\n");                        // 11

                using (DebugController controller = new DebugController(null))
                {
                    List<DebugStoppedEventArgs> stops = new List<DebugStoppedEventArgs>();
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();

                    controller.Stopped += (s, e) =>
                    {
                        int n;
                        lock (gate) { stops.Add(e); n = stops.Count; }
                        // Drive the exact Step commands the Debug menu issues: Into, Over, Over, Out.
                        if (n == 1) controller.StepInto();
                        else if (n == 2 || n == 3) controller.StepOver();
                        else if (n == 4) controller.StepOut();
                        else controller.Continue();
                    };
                    controller.SessionEnded += (s, e) => ended.Set();

                    controller.Breakpoints.AddBreakpoint(script, 9);
                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "debug session did not end within 40s");

                    lock (gate)
                    {
                        Assert.IsTrue(stops.Count >= 5, "expected breakpoint + 4 step stops; got " + stops.Count);
                        Assert.AreEqual("breakpoint", stops[0].Reason, "first stop is the breakpoint");
                        Assert.AreEqual(9, stops[0].Line, "breakpoint at the call site");
                        // Step Into must cross INTO factorial (body lines 2..5), not stay on line 9.
                        Assert.AreEqual("step", stops[1].Reason, "second stop is a step");
                        Assert.IsTrue(stops[1].Line >= 2 && stops[1].Line <= 5,
                            "Step Into should enter factorial's body; landed on line " + stops[1].Line);
                        // Step Out must return to the caller (line 9).
                        Assert.AreEqual("step", stops[4].Reason, "fifth stop is the step-out landing");
                        Assert.AreEqual(9, stops[4].Line, "Step Out returns to the call site");
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void GuiDebugController_LiveSession_UncaughtExceptionBreakpoint()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                // Same shape as the sample exception_target.py: one caught ZeroDivisionError then
                // an uncaught one. With the "uncaught" filter the debugger must pause ONLY on the
                // propagating call (the handled one is skipped).
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script,
                    "def safe_divide(a, b):\n" +     // 1
                    "    return a / b\n" +           // 2  <- uncaught raise lands here
                    "def main():\n" +                // 3
                    "    try:\n" +                   // 4
                    "        safe_divide(10, 0)\n" + // 5  caught -> not 'uncaught'
                    "    except ZeroDivisionError:\n"+// 6
                    "        print('CAUGHT')\n" +     // 7
                    "    safe_divide(1, 0)\n" +       // 8  uncaught -> propagates
                    "    print('UNREACHABLE')\n" +   // 9
                    "main()\n");                      // 10

                using (DebugController controller = new DebugController(null))
                {
                    List<DebugStoppedEventArgs> excStops = new List<DebugStoppedEventArgs>();
                    List<DebugVariable> firstLocals = null;
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();

                    controller.Stopped += (s, e) =>
                    {
                        lock (gate)
                        {
                            if (e.Reason == "exception")
                            {
                                excStops.Add(e);
                                if (firstLocals == null)
                                {
                                    List<DebugStackFrame> st = new List<DebugStackFrame>(controller.GetCallStack());
                                    if (st.Count > 0) firstLocals = new List<DebugVariable>(controller.GetLocals(st[0].Id));
                                }
                            }
                        }
                        controller.Continue();
                    };
                    controller.SessionEnded += (s, e) => ended.Set();

                    // Arm exception breakpoints exactly as the Debug menu does (DebugCoordinator
                    // sets this list on the controller before Start).
                    controller.ExceptionBreakFilters = new List<string> { "uncaught" };
                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "debug session did not end within 40s");

                    lock (gate)
                    {
                        Assert.AreEqual(1, excStops.Count, "uncaught filter should pause exactly once (handled one skipped)");
                        StringAssert.Contains(excStops[0].FilePath ?? "", "t.py", "exception stop file");
                        Assert.AreEqual(2, excStops[0].Line, "uncaught raise lands on the division line");
                        Assert.IsNotNull(firstLocals, "exception-frame locals should resolve");
                        DebugVariable a = firstLocals.Find(v => v.Name == "a");
                        DebugVariable b = firstLocals.Find(v => v.Name == "b");
                        Assert.IsNotNull(a, "arg a present"); Assert.AreEqual("1", a.Value, "a from the propagating call");
                        Assert.IsNotNull(b, "arg b present"); Assert.AreEqual("0", b.Value, "b from the propagating call");
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Stop-on-entry in the GUI: debugpy ignores stopOnEntry on the attach the engine uses, so
        // DebugController.StopOnEntry is realized by resolving the script's first executable line
        // (EntryLineResolver, skipping the module docstring) and arming a SYNTHETIC engine
        // breakpoint that never enters the user-facing BreakpointManager. This asserts: the session
        // pauses on that line, BEFORE any program output, with ZERO user breakpoints registered (the
        // differentiator from an ordinary breakpoint), then resumes to a clean end.
        [TestMethod]
        public void GuiDebugController_LiveSession_StopOnEntryPausesOnFirstExecutableLine()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script,
                    "'''module docstring'''\n" + // 1  docstring — must be SKIPPED
                    "x = 1\n" +                  // 2  <- first executable line (entry stop)
                    "print('RESULT', x)\n");     // 3

                using (DebugController controller = new DebugController(null))
                {
                    List<DebugStoppedEventArgs> stops = new List<DebugStoppedEventArgs>();
                    StringBuilder output = new StringBuilder();
                    string outputAtFirstStop = null;
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();

                    controller.Stopped += (s, e) =>
                    {
                        lock (gate)
                        {
                            stops.Add(e);
                            if (stops.Count == 1) outputAtFirstStop = output.ToString();
                        }
                        controller.Continue();
                    };
                    controller.OutputReceived += (s, text) => { lock (gate) output.Append(text); };
                    controller.SessionEnded += (s, e) => ended.Set();

                    // Arm "stop on entry" exactly as the Debug → Break on Entry menu does — and set
                    // NO line breakpoints.
                    controller.StopOnEntry = true;
                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "stop-on-entry session did not end within 40s");

                    lock (gate)
                    {
                        Assert.IsTrue(stops.Count >= 1, "stop-on-entry should pause at least once");
                        Assert.AreEqual(2, stops[0].Line, "entry stop is the first EXECUTABLE line (docstring skipped)");
                        Assert.IsTrue(stops[0].FilePath != null && stops[0].FilePath.EndsWith("t.py"),
                            "entry stop file drives editor navigation; got: " + stops[0].FilePath);
                        Assert.IsFalse((outputAtFirstStop ?? "").Contains("RESULT"),
                            "must pause BEFORE the program runs; output so far: '" + outputAtFirstStop + "'");
                        // The entry stop is synthetic — it must NOT pollute the user's breakpoints.
                        int bpFiles = 0;
                        foreach (string f in controller.Breakpoints.GetFilesWithBreakpoints()) bpFiles++;
                        Assert.AreEqual(0, bpFiles, "stop-on-entry must not register a user-facing breakpoint");
                        Assert.IsTrue(output.ToString().Contains("RESULT 1"),
                            "after Continue the program runs to completion; got: " + output);
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Breakpoint persistence (pass #18): Save/Load must round-trip breakpoint LINES and their
        // CONDITIONS / hit-counts via the JSON format, and still read the legacy "file|lines" text.
        [TestMethod]
        public void BreakpointPersistence_RoundTripsLinesAndConditions()
        {
            string dir = MakeTempDir();
            try
            {
                // Load only restores breakpoints whose file still exists, so use a real temp file.
                string py = Path.Combine(dir, "t.py");
                File.WriteAllText(py, "x = 1\ny = 2\nz = 3\n");
                string store = Path.Combine(dir, "breakpoints.txt");

                BreakpointManager a = new BreakpointManager();
                a.AddBreakpoint(py, 1);                       // plain
                a.SetCondition(py, 2, "y > 0", null);          // condition only
                a.SetCondition(py, 3, null, ">=2");            // hit-count only
                a.Save(store);

                BreakpointManager b = new BreakpointManager();
                b.Load(store);

                Assert.IsTrue(b.HasBreakpoint(py, 1), "plain breakpoint restored");
                Assert.IsNull(b.GetCondition(py, 1), "plain breakpoint has no condition");

                BreakpointCondition c2 = b.GetCondition(py, 2);
                Assert.IsNotNull(c2, "conditional breakpoint restored");
                Assert.AreEqual("y > 0", c2.Condition, "condition expression round-tripped");
                Assert.IsTrue(string.IsNullOrEmpty(c2.HitCondition), "no hit-count on this one");

                BreakpointCondition c3 = b.GetCondition(py, 3);
                Assert.IsNotNull(c3, "hit-count breakpoint restored");
                Assert.AreEqual(">=2", c3.HitCondition, "hit-count round-tripped");

                // Stale entries for deleted files are dropped on load.
                File.Delete(py);
                BreakpointManager d = new BreakpointManager();
                d.Load(store);
                Assert.IsFalse(d.HasBreakpoint(py, 1), "breakpoints for a missing file are not restored");

                // Legacy "file|lines" text format still loads (conditions reset).
                string legacyPy = Path.Combine(dir, "legacy.py");
                File.WriteAllText(legacyPy, "a = 1\n");
                string legacyStore = Path.Combine(dir, "legacy.txt");
                File.WriteAllText(legacyStore, legacyPy + "|1");
                BreakpointManager e = new BreakpointManager();
                e.Load(legacyStore);
                Assert.IsTrue(e.HasBreakpoint(legacyPy, 1), "legacy line format still readable");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Hit-count validation (pass #21): the dialog rejects malformed hit-counts that debugpy
        // would otherwise silently ignore. This locks the accepted grammar.
        [TestMethod]
        public void BreakpointHitCondition_AcceptsValidRejectsGarbage()
        {
            foreach (string good in new[] { "", "   ", "5", " 3 ", ">=3", "<=10", ">5", "<2", "==4", "!=2", "%3", "% 2" })
                Assert.IsTrue(BreakpointHitCondition.IsValid(good), "should be valid: '" + good + "'");
            foreach (string bad in new[] { "abc", "third", ">=", ">", "3x", "1.5", ">=three", "i==3", "= =3", "==", ">>3" })
                Assert.IsFalse(BreakpointHitCondition.IsValid(bad), "should be invalid: '" + bad + "'");
        }

        // Hit-count breakpoint, end-to-end: debugpy must honor `hitCondition` so the breakpoint
        // fires on the Nth hit (here the 3rd) — proving the engine sends it.
        [TestMethod]
        public void GuiDebugController_LiveSession_HitCountBreakpointFiresOnNthHit()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script,
                    "def run():\n" +              // 1
                    "    total = 0\n" +           // 2
                    "    for i in range(5):\n" +  // 3
                    "        total += i\n" +      // 4  <- hit-count breakpoint: 3rd hit (i==2)
                    "    return total\n" +        // 5
                    "print('DONE', run())\n");    // 6

                using (DebugController controller = new DebugController(null))
                {
                    List<string> iAtStops = new List<string>();
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();

                    controller.Stopped += (s, e) =>
                    {
                        DebugVariable iv = null;
                        foreach (DebugVariable v in controller.GetLocals()) if (v.Name == "i") { iv = v; break; }
                        lock (gate) iAtStops.Add(iv != null ? iv.Value : "<none>");
                        controller.Continue();
                    };
                    controller.SessionEnded += (s, e) => ended.Set();

                    controller.Breakpoints.AddBreakpoint(script, 4);
                    controller.Breakpoints.SetCondition(script, 4, null, "3"); // hit-count only
                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "debug session did not end within 40s");

                    lock (gate)
                    {
                        Assert.AreEqual(1, iAtStops.Count, "hit-count '3' must fire exactly once; got i=["
                            + string.Join(",", iAtStops) + "]");
                        Assert.AreEqual("2", iAtStops[0], "the 3rd hit of i=0..4 is i == 2");
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Conditional / hit-count breakpoints (pass #17) — pure manager behavior.
        [TestMethod]
        public void BreakpointCondition_SetGetClearAndRemoval()
        {
            BreakpointManager mgr = new BreakpointManager();
            const string file = @"C:\proj\t.py";
            const int line = 11;

            mgr.AddBreakpoint(file, line);
            Assert.IsNull(mgr.GetCondition(file, line), "a fresh breakpoint is unconditional");

            mgr.SetCondition(file, line, "i == 3", ">=2");
            BreakpointCondition c = mgr.GetCondition(file, line);
            Assert.IsNotNull(c, "condition stored");
            Assert.AreEqual("i == 3", c.Condition, "condition expression");
            Assert.AreEqual(">=2", c.HitCondition, "hit-count rule");
            Assert.IsFalse(c.IsEmpty);

            // SetCondition on a line with no breakpoint yet creates the breakpoint (one-gesture auth).
            mgr.SetCondition(file, 20, "x > 0", null);
            Assert.IsTrue(mgr.HasBreakpoint(file, 20), "SetCondition adds the breakpoint if missing");

            // Blank condition + blank hit clears the condition but keeps the breakpoint.
            mgr.SetCondition(file, line, "   ", "");
            Assert.IsNull(mgr.GetCondition(file, line), "blank clears the condition");
            Assert.IsTrue(mgr.HasBreakpoint(file, line), "clearing the condition keeps the breakpoint");

            // Removing the breakpoint also drops any condition (no orphan condition).
            mgr.SetCondition(file, line, "i == 3", null);
            mgr.RemoveBreakpoint(file, line);
            Assert.IsNull(mgr.GetCondition(file, line), "removing the breakpoint clears its condition");
        }

        // Conditional breakpoint, end-to-end: debugpy must honor `condition` so the breakpoint
        // fires ONLY when the expression is truthy — proving the engine sends it.
        [TestMethod]
        public void GuiDebugController_LiveSession_ConditionalBreakpointFiresOnlyWhenTrue()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script,
                    "def run():\n" +              // 1
                    "    total = 0\n" +           // 2
                    "    for i in range(5):\n" +  // 3
                    "        total += i\n" +      // 4  <- conditional breakpoint: i == 3
                    "    return total\n" +        // 5
                    "print('DONE', run())\n");    // 6

                using (DebugController controller = new DebugController(null))
                {
                    List<string> iAtStops = new List<string>();
                    StringBuilder output = new StringBuilder();
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();

                    controller.Stopped += (s, e) =>
                    {
                        DebugVariable iv = null;
                        foreach (DebugVariable v in controller.GetLocals()) if (v.Name == "i") { iv = v; break; }
                        lock (gate) iAtStops.Add(iv != null ? iv.Value : "<none>");
                        controller.Continue();
                    };
                    controller.OutputReceived += (s, text) => { lock (gate) output.Append(text); };
                    controller.SessionEnded += (s, e) => ended.Set();

                    controller.Breakpoints.AddBreakpoint(script, 4);
                    controller.Breakpoints.SetCondition(script, 4, "i == 3", null);
                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "debug session did not end within 40s");

                    lock (gate)
                    {
                        // Without the condition the loop body breakpoint would hit 5x (i=0..4).
                        Assert.AreEqual(1, iAtStops.Count, "conditional breakpoint must fire exactly once; got hits at i=["
                            + string.Join(",", iAtStops) + "]");
                        Assert.AreEqual("3", iAtStops[0], "the only stop must be where i == 3");
                        Assert.IsTrue(output.ToString().Contains("DONE 10"), "program ran to completion; got: " + output);
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Variable tree-expansion (pass #16): the Variables panel lazily expands a structured value
        // (list / dict / object instance) by calling DebugController.GetChildren on its
        // VariablesReference. This drives that exact path: locate the list, dict and object locals,
        // expand each, and assert the children — including a SECOND level (dict -> nested list).
        [TestMethod]
        public void GuiDebugController_LiveSession_GetChildrenExpandsStructuredVariables()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                // Same shape as the sample _vars_demo.py.
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script,
                    "class Point:\n" +                                              // 1
                    "    def __init__(self, x, y):\n" +                             // 2
                    "        self.x = x\n" +                                        // 3
                    "        self.y = y\n" +                                        // 4
                    "def build():\n" +                                             // 5
                    "    numbers = [10, 20, 30]\n" +                               // 6
                    "    person = {'name': 'Ada', 'langs': ['py', 'c']}\n" +       // 7
                    "    p = Point(3, 4)\n" +                                       // 8
                    "    total = sum(numbers)\n" +                                 // 9
                    "    return total\n" +                                         // 10 <- breakpoint
                    "build()\n");                                                  // 11

                using (DebugController controller = new DebugController(null))
                {
                    List<DebugVariable> locals = null;
                    List<DebugVariable> numbersKids = null, pointKids = null, langsKids = null;
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();
                    bool captured = false;

                    controller.Stopped += (s, e) =>
                    {
                        lock (gate)
                        {
                            if (!captured)
                            {
                                captured = true;
                                List<DebugStackFrame> st = new List<DebugStackFrame>(controller.GetCallStack());
                                locals = new List<DebugVariable>(controller.GetLocals(st[0].Id));

                                DebugVariable numbers = locals.Find(v => v.Name == "numbers");
                                DebugVariable p = locals.Find(v => v.Name == "p");
                                DebugVariable person = locals.Find(v => v.Name == "person");
                                if (numbers != null) numbersKids = new List<DebugVariable>(controller.GetChildren(numbers.VariablesReference));
                                if (p != null) pointKids = new List<DebugVariable>(controller.GetChildren(p.VariablesReference));
                                // Second level: dict -> the nested 'langs' list.
                                if (person != null)
                                {
                                    List<DebugVariable> personKids = new List<DebugVariable>(controller.GetChildren(person.VariablesReference));
                                    DebugVariable langs = personKids.Find(v => v.Name != null && v.Name.Contains("langs"));
                                    if (langs != null) langsKids = new List<DebugVariable>(controller.GetChildren(langs.VariablesReference));
                                }
                            }
                        }
                        controller.Continue();
                    };
                    controller.SessionEnded += (s, e) => ended.Set();

                    controller.Breakpoints.AddBreakpoint(script, 10);
                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "debug session did not end within 40s");

                    lock (gate)
                    {
                        Assert.IsNotNull(locals, "a stop should have been captured");
                        // The structured locals must report a VariablesReference (>0) so the panel
                        // knows they're expandable.
                        DebugVariable numbers = locals.Find(v => v.Name == "numbers");
                        Assert.IsNotNull(numbers, "local 'numbers' present");
                        Assert.IsTrue(numbers.VariablesReference > 0, "a list must be expandable");

                        // List elements resolve to their values.
                        Assert.IsNotNull(numbersKids, "numbers children resolved");
                        Assert.IsNotNull(numbersKids.Find(v => v.Name == "0" && v.Value == "10"), "numbers[0]==10");
                        Assert.IsNotNull(numbersKids.Find(v => v.Name == "1" && v.Value == "20"), "numbers[1]==20");
                        Assert.IsNotNull(numbersKids.Find(v => v.Name == "2" && v.Value == "30"), "numbers[2]==30");

                        // Object attributes resolve.
                        Assert.IsNotNull(pointKids, "Point children resolved");
                        Assert.IsNotNull(pointKids.Find(v => v.Name == "x" && v.Value == "3"), "p.x==3");
                        Assert.IsNotNull(pointKids.Find(v => v.Name == "y" && v.Value == "4"), "p.y==4");

                        // Second-level expansion: dict -> nested list -> its elements.
                        Assert.IsNotNull(langsKids, "nested 'langs' list children resolved (2nd level)");
                        Assert.IsNotNull(langsKids.Find(v => v.Name == "0" && v.Value == "'py'"), "langs[0]=='py'");
                        Assert.IsNotNull(langsKids.Find(v => v.Name == "1" && v.Value == "'c'"), "langs[1]=='c'");
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Debug toolbar (pass #15): the step/continue/stop buttons must enable/disable with the
        // session state so a user paused at a breakpoint sees live controls and an idle user can't
        // click Stop/Step on a dead session. This locks the pure state mapping the toolbar uses.
        [TestMethod]
        public void DebugToolbar_ButtonEnablement_TracksSessionState()
        {
            // Idle: Continue acts as Start (enabled); nothing to Pause/Restart/Stop/Step; can always set a bp.
            var idle = Coordinators.DebugCoordinator.ComputeToolbarState(debugging: false, paused: false);
            Assert.IsTrue(idle.Continue, "idle: Continue (=Start) enabled");
            Assert.IsFalse(idle.Pause, "idle: nothing running to pause");
            Assert.IsFalse(idle.Restart, "idle: no session to restart");
            Assert.IsFalse(idle.Stop, "idle: nothing to stop");
            Assert.IsFalse(idle.Step, "idle: nothing to step");
            Assert.IsTrue(idle.ToggleBreakpoint, "idle: breakpoints can be toggled");

            // Running (debugging, not paused): can Pause/Restart/Stop, but cannot Continue/Step a running program.
            var running = Coordinators.DebugCoordinator.ComputeToolbarState(debugging: true, paused: false);
            Assert.IsFalse(running.Continue, "running: nothing to resume");
            Assert.IsTrue(running.Pause, "running: can break into the debugger");
            Assert.IsTrue(running.Restart, "running: Restart available");
            Assert.IsTrue(running.Stop, "running: Stop available");
            Assert.IsFalse(running.Step, "running: can't step a running program");
            Assert.IsTrue(running.ToggleBreakpoint, "running: breakpoints can be toggled");

            // Paused at a breakpoint: full control — resume, restart, stop, and all three steps; NOT Pause
            // (already suspended, nothing to break into).
            var paused = Coordinators.DebugCoordinator.ComputeToolbarState(debugging: true, paused: true);
            Assert.IsTrue(paused.Continue, "paused: resume enabled");
            Assert.IsFalse(paused.Pause, "paused: already suspended — nothing to pause");
            Assert.IsTrue(paused.Restart, "paused: Restart enabled");
            Assert.IsTrue(paused.Stop, "paused: Stop enabled");
            Assert.IsTrue(paused.Step, "paused: Step over/into/out enabled");
            Assert.IsTrue(paused.ToggleBreakpoint, "paused: breakpoints can be toggled");
        }

        // "Debug Tests" (the new per-test-package verb): the same DebugController, launched via
        // StartTests (pytest under debugpy, -m pytest <dir>) instead of a single script. Proves the
        // acceptance bar from python-solution-model.md's test-package rule: a breakpoint set on a
        // line INSIDE a test_*.py function actually pauses execution there, with the Variables
        // panel data resolvable — not just that a menu item exists.
        [TestMethod]
        public void GuiDebugController_DebugTests_BreakpointInsideTestFunctionPauses()
        {
            if (!DebugpyAvailable() || !PytestAvailable())
                Assert.Inconclusive("python with debugpy+pytest not available on PATH");

            string dir = MakeTempDir();
            try
            {
                string testFile = Path.Combine(dir, "test_sample.py");
                File.WriteAllText(testFile,
                    "def add(a, b):\n" +           // 1
                    "    return a + b\n" +         // 2
                    "\n" +
                    "def test_add():\n" +          // 4
                    "    result = add(2, 3)\n" +   // 5
                    "    assert result == 5\n");   // 6  <- breakpoint (result already computed)

                using (DebugController controller = new DebugController(null))
                {
                    List<DebugStoppedEventArgs> stops = new List<DebugStoppedEventArgs>();
                    List<DebugVariable> firstLocals = null;
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();

                    controller.Stopped += (s, e) =>
                    {
                        lock (gate)
                        {
                            stops.Add(e);
                            if (firstLocals == null)
                            {
                                List<DebugStackFrame> st = new List<DebugStackFrame>(controller.GetCallStack());
                                if (st.Count > 0) firstLocals = new List<DebugVariable>(controller.GetLocals(st[0].Id));
                            }
                        }
                        controller.Continue();
                    };
                    controller.SessionEnded += (s, e) => ended.Set();

                    // The GUI sets this through the tabbed editor exactly as for a normal script debug.
                    controller.Breakpoints.AddBreakpoint(testFile, 6);
                    controller.StartTests(dir);
                    Assert.IsTrue(ended.Wait(60000), "pytest debug session did not end within 60s");

                    lock (gate)
                    {
                        Assert.IsTrue(stops.Count >= 1, "breakpoint inside test_add() should hit");
                        Assert.AreEqual("breakpoint", stops[0].Reason, "stop reason");
                        Assert.AreEqual(6, stops[0].Line, "stopped line drives the editor's current-line marker");
                        Assert.IsTrue(stops[0].FilePath != null && stops[0].FilePath.EndsWith("test_sample.py"),
                            "stopped file drives editor navigation; got: " + stops[0].FilePath);
                        Assert.IsNotNull(firstLocals, "locals should resolve inside the test function's frame");
                        DebugVariable result = firstLocals.Find(v => v.Name == "result");
                        Assert.IsNotNull(result, "local 'result' present");
                        Assert.AreEqual("5", result.Value, "result == add(2, 3)");
                    }
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [TestMethod]
        public void GuiDebugController_NoBreakpoints_RunsToCleanEnd()
        {
            if (!DebugpyAvailable())
                Assert.Inconclusive("python with debugpy not available on PATH");

            string dir = MakeTempDir();
            try
            {
                string script = Path.Combine(dir, "t.py");
                File.WriteAllText(script, "print('HELLO')\n");

                using (DebugController controller = new DebugController(null))
                {
                    StringBuilder output = new StringBuilder();
                    ManualResetEventSlim ended = new ManualResetEventSlim(false);
                    object gate = new object();
                    controller.OutputReceived += (s, text) => { lock (gate) output.Append(text); };
                    controller.SessionEnded += (s, e) => ended.Set();

                    controller.Start(script);
                    Assert.IsTrue(ended.Wait(40000), "no-breakpoint session should reach SessionEnded");
                    Assert.IsFalse(controller.IsDebugging, "engine should be torn down after the run");
                    lock (gate) Assert.IsTrue(output.ToString().Contains("HELLO"), "output relayed; got: " + output);
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // Bug B regression guard: DebugController.Start's if-chain previously had no branch for plain
        // (non-CUDA) C/C++ sources, so a .c/.cpp/.h file silently fell through to StartPython — launching
        // the wrong debugger with no indication anything was wrong. IsCppFile is internal specifically so
        // this dispatch predicate is unit-testable without spinning up a real gdb/lldb session. The
        // .cu/.cuh cases assert the OTHER half of the fix: IsCudaFile is still checked first in Start(),
        // so a CUDA kernel must never be misrouted into the new C/C++ path.
        [TestMethod]
        public void IsCppFile_TrueForPlainCAndCppExtensions_FalseForCudaAndOtherLanguages()
        {
            foreach (string ext in new[] { ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hh", ".hxx" })
                Assert.IsTrue(DebugController.IsCppFile(@"C:\proj\t" + ext), "should be a C/C++ file: " + ext);

            // Case-insensitivity, matching the idiom the other Is<Lang>File helpers use.
            Assert.IsTrue(DebugController.IsCppFile(@"C:\proj\T.CPP"), "extension check must be case-insensitive");

            foreach (string ext in new[] { ".cu", ".cuh", ".py", ".rs", ".java", ".jl", ".cs" })
                Assert.IsFalse(DebugController.IsCppFile(@"C:\proj\t" + ext),
                    "must NOT claim " + ext + " — .cu/.cuh belong to CUDA (checked first in Start()), the rest to their own engines");
        }

        [TestMethod]
        public void DebugLaunchers_AreRegistryResolved_AndAllowPriorityOverrides()
        {
            using (var owner = new System.Windows.Forms.Form())
            using (var controller = new DebugController(owner))
            {
                // HelixPython registers three launchers: Python (debugpy), C++ (gdb/MI, for the
                // native source the Convert-to-C/C++ commands emit) and CUDA (the CPU SIMT
                // emulator). Every other extension falls through to the lowest-priority python
                // launcher, which is the historical default.
                Assert.AreEqual("cpp", controller.DebugLauncherForFile(@"C:\p\main.cpp"));
                Assert.AreEqual("python", controller.DebugLauncherForFile(@"C:\p\main.py"));
                // Priority is load-bearing here, not decoration: a .cu IS a C++ file to the native
                // toolchain, so without the CUDA launcher outranking the C++ one a kernel would be
                // handed to gdb, which cannot run one.
                Assert.AreEqual("cuda", controller.DebugLauncherForFile(@"C:\p\kernel.cu"),
                    "hard rule 4: a .cu must reach the CUDA emulator debugger, not gdb");
                Assert.AreEqual("cuda", controller.DebugLauncherForFile(@"C:\p\reduce.cuh"));

                controller.RegisterDebugLauncher("custom-python", p => p.EndsWith(".py"), p => { }, 500);
                Assert.AreEqual("custom-python", controller.DebugLauncherForFile(@"C:\p\main.py"));
            }
        }

        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "helix_gui_dbg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static bool DebugpyAvailable()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-c \"import debugpy\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static bool PytestAvailable()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-c \"import pytest\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }
    }
}
