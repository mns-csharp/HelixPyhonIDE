using System;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Runs once, before any test in this assembly, and before any Control exists.
    ///
    /// Two jobs, both learned the hard way:
    ///
    /// 1. A test run must NEVER put a dialog on the developer's screen, and must never take the test
    ///    host down either. WinForms shows its ThreadExceptionDialog ("Unhandled exception has
    ///    occurred in a component in your application… Continue") for any exception that reaches the
    ///    message loop, and no product-side flag can suppress it. Attaching an Application.
    ///    ThreadException handler makes WinForms raise the event INSTEAD of showing the box.
    ///    UnhandledExceptionMode.ThrowException also suppresses the dialog, but it kills the process
    ///    — which aborted a run mid-test and left a fixture solution half-renamed on disk. Catching
    ///    and logging keeps the run alive and the failure visible.
    ///
    /// 2. Restore any fixture solution a PREVIOUS run left modified (see FixtureWorkspace).
    /// </summary>
    [TestClass]
    public static class TestAssemblySetup
    {
        [AssemblyInitialize]
        public static void Initialize(TestContext context)
        {
            Environment.SetEnvironmentVariable("HELIXSTUDIO_NONINTERACTIVE", "1");

            // Route WinForms exceptions to us rather than to a modal crash box.
            //
            // Two halves, and only ONE of them is application-wide. SetUnhandledExceptionMode is:
            // it decides that WinForms catches rather than rethrows, for every thread. The
            // ThreadException *handler* is not — WinForms looks it up on the throwing thread's own
            // ThreadContext, so subscribing here only guards THIS thread. MSTest does not run tests
            // on the AssemblyInitialize thread, which is why the second call below exists.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            GuardCurrentThread();
            Trace("apartment", "AssemblyInitialize thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId
                + " apartment=" + System.Threading.Thread.CurrentThread.GetApartmentState());

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Trace("unhandled", ex != null ? ex.ToString() : "(non-CLR exception)");
            };

            // The guard that actually holds, and the reason it has to be shaped like this.
            //
            // Application.ThreadException is resolved from the THROWING thread's ThreadContext. The
            // subscription above covers the thread MSTest runs tests on — measured:
            // thread 12, STA, the same thread as this method — and covers nothing else. Any OTHER
            // thread that reaches WinForms' message loop with an exception finds no handler, and
            // WinForms shows its modal ThreadExceptionDialog: a crash box on the developer's
            // desktop, behind which the test host blocks until a human clicks it. That happened on
            // during testing with "DragDrop registration did not succeed" out of
            // TreeView.OnHandleCreated, and no test-diagnostics.log was written for the entire run —
            // the documented guard had never fired. Since the test thread is STA, the throwing
            // thread was one the product itself created.
            //
            // FirstChanceException is process-wide and runs ON the throwing thread, at throw time,
            // before any catch — including before NativeWindow.Callback's. So it is the one place
            // that can attach the handler to a thread nobody knew was going to need it. The body is
            // a thread-static bool read on the common path, which is what makes it affordable in a
            // callback that fires for every exception in the process.
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                GuardCurrentThread();
                RecordApartmentViolation(e.Exception);
            };

            // NOTE: apartment state is deliberately NOT asserted. Forcing STA via runsettings makes
            // MSTest use a different STA thread per test, and this suite shares process-wide UI
            // state — which produced "Controls created on one thread cannot be parented to a control
            // on a different thread" and crashed the host. See the comment in HelixPythonNamespace.runsettings.
            // The drag-drop registration failure is swallowed by the ThreadException handler
            // instead — but only once GuardCurrentThread has reached the thread that throws it,
            // which for a long time it had not. See GuardCurrentThread.

            // A crashed or killed run cannot execute its own cleanup, so recover here instead.
            int recovered = FixtureWorkspace.RecoverAbandonedSnapshots();
            if (recovered > 0)
                Console.Error.WriteLine("[fixtures] restored " + recovered +
                                        " fixture solution(s) left modified by an earlier run");
        }

        [ThreadStatic]
        private static bool _guarded;

        private static int _apartmentViolations;

        /// <summary>
        /// Record the CREATING stack for an "OLE calls need STA" failure, the once it matters.
        ///
        /// <para>
        /// The exception's own stack only ever says <c>TreeView.OnHandleCreated</c> →
        /// <c>SetAcceptDrops</c>, which names the victim and not the cause. <c>Environment.StackTrace</c>
        /// read here — at first chance, before the stack unwinds — is the frame that actually
        /// realized a WinForms control on a non-STA thread, which is the thing to go and fix.
        /// Capped, because a stack trace per exception in a suite this size is its own denial of
        /// service.
        /// </para>
        /// </summary>
        private static void RecordApartmentViolation(Exception ex)
        {
            if (ex == null || _apartmentViolations >= 5) return;
            if (!(ex is System.Threading.ThreadStateException)) return;
            if (ex.Message == null
                || ex.Message.IndexOf("single thread apartment", StringComparison.OrdinalIgnoreCase) < 0) return;

            _apartmentViolations++;
            System.Threading.Thread self = System.Threading.Thread.CurrentThread;
            Trace("sta-violation",
                "thread=" + self.ManagedThreadId + " apartment=" + self.GetApartmentState()
                + " name=" + (self.Name ?? "(unnamed)")
                + " pool=" + self.IsThreadPoolThread
                + Environment.NewLine + "creating stack:" + Environment.NewLine + Environment.StackTrace);
        }

        /// <summary>
        /// Attach the WinForms thread-exception handler to the CALLING thread. Idempotent, cheap,
        /// and safe to call from anywhere — including from inside a first-chance callback.
        ///
        /// <para>
        /// <see cref="Application.ThreadException"/> is resolved from the THROWING thread's own
        /// <c>ThreadContext</c>, so a single subscription guards a single thread. With
        /// <see cref="UnhandledExceptionMode.CatchException"/> set application-wide and no handler
        /// on the thread that throws, WinForms falls back to its own modal
        /// <c>ThreadExceptionDialog</c> — a crash box on the developer's desktop, and a test host
        /// blocked behind it until a human clicks it.
        /// </para>
        /// <para>
        /// That is not hypothetical, and the measured cause is worth writing down because the
        /// obvious explanation is wrong. During a full run put "DragDrop registration did not
        /// succeed" on screen out of <c>TreeView.OnHandleCreated</c> → <c>Control.SetAcceptDrops</c>
        /// → OLE <c>RegisterDragDrop</c>, which requires STA — and no <c>test-diagnostics.log</c>
        /// was written for the entire run, so the guard had never fired. The natural reading is
        /// "the runner gave us an MTA thread". It did not: measured the same day, MSTest runs both
        /// <see cref="Initialize"/> and the tests on the SAME thread, and that thread is STA
        /// (see <c>Diag_TestHostApartment</c>). The throw therefore came from some OTHER thread the
        /// product created — which is exactly the thread no static subscription can know about in
        /// advance.
        /// </para>
        /// <para>
        /// Hence the <see cref="AppDomain.FirstChanceException"/> hook in <see cref="Initialize"/>:
        /// it is process-wide, and it runs on the throwing thread at throw time, before any catch.
        /// Only the event subscription is repeated per thread — the exception MODE is
        /// application-wide and is set once, because re-setting it after a control exists throws.
        /// </para>
        /// </summary>
        internal static void GuardCurrentThread()
        {
            // Set BEFORE the subscription, not after: this runs from FirstChanceException, so an
            // exception escaping the line below would re-enter here, and the flag is what stops
            // that becoming infinite recursion.
            if (_guarded) return;
            _guarded = true;
            try { Subscribe(); } catch { /* a guard that cannot install must not become the crash */ }
        }

        private static void Subscribe()
        {
            System.Threading.Thread self = System.Threading.Thread.CurrentThread;
            Application.ThreadException += (s, e) =>
                Trace("ui-thread-exception",
                    "thread=" + self.ManagedThreadId + " apartment=" + self.GetApartmentState()
                    + Environment.NewLine
                    + (e.Exception != null ? e.Exception.ToString() : "(none)"));
        }

        /// <summary>
        /// Append to a file, not Console.Error. When the host dies, buffered console output is lost
        /// with it — which is exactly the case we most need a record of.
        /// </summary>
        internal static void Trace(string tag, string message)
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("HELIXSTUDIO_STATE_DIR");
                if (string.IsNullOrEmpty(dir)) dir = System.IO.Path.GetTempPath();
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "test-diagnostics.log"),
                    "[" + tag + "] " + message + Environment.NewLine + new string('-', 70) + Environment.NewLine);
            }
            catch { }
        }

        [AssemblyCleanup]
        public static void Cleanup()
        {
            FixtureIde.DisposeShared();
        }
    }
}
