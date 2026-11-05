using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.App;
using HelixPythonNamespace.Debugger;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Iter 279: closing the IDE while a debug session is live must terminate the debuggee + its
    /// adapter. Debugging is NOT routed through the JobRunner that OnFormClosing cancels, so before
    /// this fix a session paused at a breakpoint (or a running debuggee) leaked its process when the
    /// window closed. Guards that OnFormClosing drives the debug controller's Stop() — i.e. the live
    /// engine's StopDebugging() — mirroring the terminal-panel Dispose leak fix (iter 278).
    /// </summary>
    [TestClass]
    public class DebugSessionShutdownTests
    {
        [TestMethod]
        public void ClosingTheForm_StopsALiveDebugSession()
        {
            using (var form = new MainForm())
            {
                // The parameterless ctor (used by tests) doesn't run the DI init that builds the
                // debug coordinator, so wire a minimal live one: a real DebugController (owner=null →
                // synchronous, no UI loop) driving a fake engine that stands in for a live
                // debugpy/CUDA session — deterministic, no external process. Inject it into the form's
                // (readonly) _debugCoordinator field so OnFormClosing sees exactly what the real app does.
                var controller = new HelixPythonNamespace.App.Services.DebugController(null);
                var engine = new FakeEngine();
                SetField(controller, "_active", engine);

                var coordinator = new HelixPythonNamespace.App.Coordinators.DebugCoordinator();
                SetField(coordinator, "_debugController", controller);
                SetField(form, "_debugCoordinator", coordinator);

                bool isDebugging = (bool)controller.GetType()
                    .GetProperty("IsDebugging", BindingFlags.Instance | BindingFlags.Public)
                    .GetValue(controller, null);
                Assert.IsTrue(isDebugging, "controller should report a live session before close");

                InvokeOnFormClosing(form);

                Assert.IsTrue(engine.StopCalled,
                    "closing the IDE must stop the live debug session (else the debuggee/adapter leaks)");
            }
        }

        private static void InvokeOnFormClosing(Form form)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "OnFormClosing expected");
            // WindowsShutDown skips the unsaved-changes prompt (no MessageBox under test). The debug
            // Stop() runs at the very top of the method, before any settings persistence, so even if
            // later shutdown code throws on a bare form the stop has already happened — assert on that.
            var args = new FormClosingEventArgs(CloseReason.WindowsShutDown, false);
            try { m.Invoke(form, new object[] { args }); }
            catch (TargetInvocationException) { /* later persistence code is not under test here */ }
        }

        private static void SetField(object obj, string name, object value)
        {
            FieldInfo f = obj.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(f, name + " field expected on " + obj.GetType().Name);
            f.SetValue(obj, value);
        }

        // Minimal IDebugEngine that reports itself as a live session and records the stop.
        private sealed class FakeEngine : IDebugEngine
        {
            public bool StopCalled { get; private set; }
            public bool IsDebugging { get { return !StopCalled; } }
            public bool IsPaused { get { return !StopCalled; } }

#pragma warning disable 0067 // events are part of the interface but not raised by the fake
            public event EventHandler<DebugStoppedEventArgs> Stopped;
            public event EventHandler<DebugOutputEventArgs> OutputReceived;
            public event EventHandler SessionEnded;
#pragma warning restore 0067

            public void Continue() { }
            public void StepOver() { }
            public void StepInto() { }
            public void StepOut() { }
            public void Pause() { }
            public void StopDebugging() { StopCalled = true; }

            public List<DebugStackFrame> GetCallStack() { return new List<DebugStackFrame>(); }
            public List<DebugScope> GetScopes(int frameId) { return new List<DebugScope>(); }
            public List<DebugVariable> GetVariables(int variablesReference) { return new List<DebugVariable>(); }

            public void Dispose() { }
        }
    }
}
