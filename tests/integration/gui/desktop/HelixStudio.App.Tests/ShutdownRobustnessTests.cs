using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HelixPythonNamespace.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Quitting the IDE must never throw.
    ///
    /// The bug: <c>OnFormClosing</c> ended with an unguarded <c>CloseFloatingPanes()</c>, which
    /// delegates to <c>_buildAnalysisCoordinator</c>. The parameterless constructor — the one the
    /// WinForms designer uses, and the one the DI constructor chains to via <c>: this()</c> —
    /// composes no coordinators at all, so that field is null and closing threw a
    /// NullReferenceException.
    ///
    /// An exception escaping OnFormClosing does not just fail quietly: GlobalExceptionHandler is
    /// installed, so it surfaces as a crash dialog over an IDE the user has just asked to close.
    ///
    /// Note this is NOT reached through Program.Main, which always composes the full graph. It bit
    /// the designer and it bit test harnesses; the value of fixing it is that shutdown stops being
    /// a path that only works when everything else did.
    /// </summary>
    [TestClass]
    public class ShutdownRobustnessTests
    {
        private string _stateDir;
        private string _prevState;

        [TestInitialize]
        public void Setup()
        {
            // OnFormClosing writes user-settings.json; keep it out of the developer's real profile.
            _stateDir = Path.Combine(Path.GetTempPath(), "helix_shutdown_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_stateDir);
            _prevState = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _stateDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Environment.SetEnvironmentVariable("HELIXSTUDIO_STATE_DIR", _prevState);
            try { Directory.Delete(_stateDir, true); } catch { }
        }

        [TestMethod]
        public void ClosingADesignerConstructedFormDoesNotThrow()
        {
            // The exact reproduction: a MainForm with no composed coordinators, closed.
            using (var form = new MainForm())
            {
                InvokeFormClosing(form, CloseReason.UserClosing);
            }
        }

        [TestMethod]
        public void ClosingIsRobustForEveryCloseReason()
        {
            // WindowsShutDown takes a different branch (it skips the save block), so a guard that
            // only covered the ordinary path would leave the shutdown-during-logoff case broken —
            // the one case where a crash dialog is least likely to ever be seen or reported.
            foreach (CloseReason reason in new[]
                     {
                         CloseReason.UserClosing,
                         CloseReason.WindowsShutDown,
                         CloseReason.ApplicationExitCall,
                         CloseReason.TaskManagerClosing,
                     })
            {
                using (var form = new MainForm())
                {
                    InvokeFormClosing(form, reason);
                }
            }
        }

        [TestMethod]
        public void ClosingTwiceDoesNotThrow()
        {
            // WinForms can raise FormClosing more than once (a cancelled close followed by a real
            // one). The second pass runs against fields the first pass already nulled.
            using (var form = new MainForm())
            {
                InvokeFormClosing(form, CloseReason.UserClosing);
                InvokeFormClosing(form, CloseReason.UserClosing);
            }
        }

        [TestMethod]
        public void CloseFloatingPanesToleratesAnUncomposedForm()
        {
            // The specific member that threw, exercised on its own so a regression names itself
            // instead of surfacing as "closing threw" from somewhere in a 150-line method.
            using (var form = new MainForm())
            {
                MethodInfo close = typeof(MainForm).GetMethod("CloseFloatingPanes",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(close, "CloseFloatingPanes must exist — this test is stale");

                try
                {
                    close.Invoke(form, null);
                }
                catch (TargetInvocationException ex)
                {
                    Assert.Fail("CloseFloatingPanes threw on an uncomposed form: " + ex.InnerException);
                }
            }
        }

        /// <summary>
        /// Raise the form's own closing logic. Reflection rather than <c>form.Close()</c>: Close()
        /// on a form that was never shown does not raise FormClosing at all, so the test would pass
        /// without ever running the code under test.
        /// </summary>
        private static void InvokeFormClosing(MainForm form, CloseReason reason)
        {
            MethodInfo closing = typeof(MainForm).GetMethod("OnFormClosing",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(closing, "OnFormClosing must exist — this test is stale");

            try
            {
                closing.Invoke(form, new object[] { new FormClosingEventArgs(reason, false) });
            }
            catch (TargetInvocationException ex)
            {
                Assert.Fail("closing the IDE threw (" + reason + "): " + ex.InnerException);
            }
        }
    }
}
