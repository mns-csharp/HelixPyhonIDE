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
    /// F10 / F11 / Shift+F11 must keep working while the debugger is paused at a breakpoint.
    ///
    /// The bug this pins (reported live against a real solution): those chords are
    /// <see cref="ToolStripMenuItem.ShortcutKeys"/> on the Debug menu items, and WinForms refuses to
    /// dispatch a shortcut whose menu item is disabled — measured, not assumed: a menu item with
    /// ShortcutKeys=F11 fires its handler on a real key press when Enabled, and is silently
    /// swallowed when not. <c>MenuWorkflowScopeTests</c> correctly stopped the Step commands being
    /// enabled with no session, but the enable-state was applied ONLY from the menu's
    /// DropDownOpening handler. So opening the Debug menu once before starting a session latched
    /// Step Over/Into/Out to Enabled=false, and NOTHING re-enabled them when the session later
    /// paused: the toolbar Step buttons worked (they refresh on every transition) and Debug ▸ Step
    /// Into worked (opening the menu re-enables it on the way), while every press of F10/F11 did
    /// nothing at all and the debugger looked wedged at the breakpoint.
    ///
    /// The fix makes the menu refresh part of the same state transition the toolbar already used,
    /// so the two surfaces cannot drift again. Stop (Shift+F5), Restart (Ctrl+Shift+F5) and Run to
    /// Cursor (Ctrl+F10) were dead the same way and are covered here too.
    /// </summary>
    [TestClass]
    public class DebugMenuShortcutStateTests
    {
        private static readonly string[] StepTokens = { "Step Over", "Step Into", "Step Out" };

        /// <summary>A session that is live and paused at a breakpoint — the state the user is in.</summary>
        private sealed class PausedEngine : IDebugEngine
        {
            public bool IsDebugging { get { return true; } }
            public bool IsPaused { get { return true; } }

#pragma warning disable 67 // never raised: this stub only has to report the paused state
            public event EventHandler<DebugStoppedEventArgs> Stopped;
            public event EventHandler<DebugOutputEventArgs> OutputReceived;
            public event EventHandler SessionEnded;
#pragma warning restore 67

            public void Continue() { }
            public void StepOver() { }
            public void StepInto() { }
            public void StepOut() { }
            public void Pause() { }
            public void StopDebugging() { }
            public List<DebugStackFrame> GetCallStack() { return new List<DebugStackFrame>(); }
            public List<DebugScope> GetScopes(int frameId) { return new List<DebugScope>(); }
            public List<DebugVariable> GetVariables(int variablesReference) { return new List<DebugVariable>(); }
            public void Dispose() { }
        }

        private static T Field<T>(object obj, string name)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f, name + " field expected");
            return (T)f.GetValue(obj);
        }

        private static ToolStripItem DebugItem(MainForm form, string token)
        {
            var debug = Field<ToolStripMenuItem>(form, "debugMenuItem");
            foreach (ToolStripItem it in debug.DropDownItems)
            {
                string text = (it.Text ?? string.Empty).Replace("&", string.Empty);
                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return it;
            }
            return null;
        }

        // The user opens the Debug menu with no session running — the action that used to latch the
        // Step commands off for the rest of the process.
        private static void OpenDebugMenu(MainForm form)
        {
            MethodInfo m = typeof(MainForm).GetMethod(
                "OnDebugMenuOpening", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "OnDebugMenuOpening expected");
            m.Invoke(form, new object[] { form, EventArgs.Empty });
        }

        // The session-state transition the IDE performs on every stop / step / start / end. This is
        // the production path — DebugCoordinator.OnDebugStopped calls it when the breakpoint hits.
        private static void ApplySessionStateTransition(MainForm form)
        {
            object coordinator = Field<object>(form, "_debugCoordinator");
            Assert.IsNotNull(coordinator, "the composed form must have a DebugCoordinator");
            MethodInfo m = coordinator.GetType().GetMethod(
                "UpdateDebugToolbar", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(m, "UpdateDebugToolbar expected");
            m.Invoke(coordinator, null);
        }

        // Put the controller into the "paused at a breakpoint" state without a real debuggee.
        private static void EnterPausedSession(MainForm form)
        {
            object coordinator = Field<object>(form, "_debugCoordinator");
            PropertyInfo p = coordinator.GetType().GetProperty(
                "Controller", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(p, "DebugCoordinator.Controller expected");
            object controller = p.GetValue(coordinator, null);
            Assert.IsNotNull(controller, "a composed form must have a DebugController");

            FieldInfo active = controller.GetType().GetField(
                "_active", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(active, "DebugController._active expected");
            active.SetValue(controller, new PausedEngine());
        }

        [TestMethod]
        public void TheStepChordsAreOnTheDebugMenuItems()
        {
            // If these ever move off the menu items this whole class is testing the wrong thing.
            using (var form = TestMainForm.CreateComposed())
            {
                Assert.AreEqual(Keys.F10, ((ToolStripMenuItem)DebugItem(form, "Step Over")).ShortcutKeys);
                Assert.AreEqual(Keys.F11, ((ToolStripMenuItem)DebugItem(form, "Step Into")).ShortcutKeys);
                Assert.AreEqual(Keys.Shift | Keys.F11, ((ToolStripMenuItem)DebugItem(form, "Step Out")).ShortcutKeys);
            }
        }

        [TestMethod]
        public void PausingAtABreakpoint_ReEnablesTheStepCommands_WithoutOpeningTheDebugMenu()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                // 1. The user glances at the Debug menu before starting. This disables the Step
                //    commands — correct in itself, and the trigger for the bug.
                OpenDebugMenu(form);
                foreach (string token in StepTokens)
                    Assert.IsFalse(DebugItem(form, token).Enabled,
                        token + " should be disabled with no session (precondition)");

                // 2. A session starts and pauses at a breakpoint.
                EnterPausedSession(form);
                ApplySessionStateTransition(form);

                // 3. ...and the keyboard must work again. WinForms will not dispatch F10/F11 while
                //    the owning menu item is disabled, so Enabled here IS the shortcut working.
                foreach (string token in StepTokens)
                {
                    ToolStripItem it = DebugItem(form, token);
                    Assert.IsTrue(it.Available, token + " must be shown once a session is live");
                    Assert.IsTrue(it.Enabled,
                        token + " must be enabled while paused — otherwise its keyboard shortcut is dead");
                }
            }
        }

        [TestMethod]
        public void PausingAtABreakpoint_AlsoRevivesStopRestartAndRunToCursor()
        {
            using (var form = TestMainForm.CreateComposed())
            {
                OpenDebugMenu(form);
                EnterPausedSession(form);
                ApplySessionStateTransition(form);

                foreach (string token in new[] { "Stop", "Restart", "Run to Cursor" })
                {
                    ToolStripItem it = DebugItem(form, token);
                    Assert.IsNotNull(it, token + " expected in the Debug menu");
                    Assert.IsTrue(it.Enabled && it.Available,
                        token + " must be live while a session is paused — it carries a keyboard chord too");
                }
            }
        }

        [TestMethod]
        public void EndingTheSession_DisablesTheStepCommandsAgain()
        {
            // The transition has to work in both directions, or the menu would claim a dead session
            // is still steppable.
            using (var form = TestMainForm.CreateComposed())
            {
                EnterPausedSession(form);
                ApplySessionStateTransition(form);
                foreach (string token in StepTokens)
                    Assert.IsTrue(DebugItem(form, token).Enabled, token + " (precondition)");

                object coordinator = Field<object>(form, "_debugCoordinator");
                PropertyInfo p = coordinator.GetType().GetProperty(
                    "Controller", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object controller = p.GetValue(coordinator, null);
                controller.GetType().GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(controller, null);

                ApplySessionStateTransition(form);

                foreach (string token in StepTokens)
                    Assert.IsFalse(DebugItem(form, token).Enabled,
                        token + " must not stay enabled after the session ends");
            }
        }
    }
}
