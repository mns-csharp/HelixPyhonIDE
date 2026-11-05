using System.Threading;
using System.Windows.Forms;
using HelixPythonNamespace.TestSupport;
using HelixPythonNamespace.App;
using HelixPythonNamespace.AddIns.GuiDesigner;

namespace HelixPythonNamespace.App.Tests
{
    /// <summary>
    /// Builds a fully composed <see cref="MainForm"/> the way Program.Main does.
    ///
    /// `new MainForm()` is the designer ctor: it only runs InitializeComponent, so every coordinator
    /// field is null and anything that routes through one (OpenInEditor, the engine commands, …)
    /// throws NullReferenceException. Tests that need real IDE behaviour — not just the designer
    /// shell — must use this.
    /// </summary>
    internal static class TestMainForm
    {
        /// <summary>
        /// Put a WinForms <see cref="SynchronizationContext"/> on this thread, if there is not one
        /// already. Idempotent and cheap.
        ///
        /// <para>
        /// MSTest clears <c>SynchronizationContext.Current</c> at every test entry, and a form that
        /// is never <c>Show()</c>n runs no message loop to install one. With no context, every
        /// <c>await</c> inside <c>MainForm.OpenSolutionAsync</c>/<c>ReloadSolutionAsync</c> resumes
        /// on a THREAD-POOL thread. Those continuations touch the UI — <c>ApplySolutionGate</c>
        /// realizes tab controls, and <c>SolutionCoordinator</c> sets <c>_statusLabel.Text</c> — so
        /// the first one to parent a control throws "Controls created on one thread cannot be
        /// parented to a control on a different thread". That exception is unhandled on a pool
        /// thread, which does not fail a test: it KILLS THE HOST.
        /// </para>
        /// <para>
        /// Measured: that is what aborted a full run at 544 of 938 tests, reporting a
        /// confident-looking <c>Total: 544</c> rather than the <c>Total tests: Unknown</c> that
        /// usually signals a partial run. <c>FixtureSolution.RealizeHeadless</c> had installed the
        /// context since it was written; the ~73 files that use <see cref="CreateComposed"/> without
        /// it did not, which is why the crash looked intermittent — it depended on which test
        /// happened to open a solution first.
        /// </para>
        /// <para>
        /// Installing it here makes the continuations come back to the thread that pumps
        /// <c>Application.DoEvents</c>, which is exactly what a real running IDE does.
        /// </para>
        /// </summary>
        public static void EnsureUiSynchronizationContext()
        {
            // And keep it. Every Application.DoEvents() that is not nested inside an Application.Run
            // ends by calling WindowsFormsSynchronizationContext.Uninstall, which replaces a
            // WindowsFormsSynchronizationContext on this thread with a plain SynchronizationContext.
            // The next `await` in OpenSolutionAsync / ReloadSolutionAsync then resumed on a pool
            // thread and recreated MainForm's HWND there; the next SendMessage to it (Form.Dispose
            // -> SetMenu) blocked forever. Measured with
            // Control.CheckForIllegalCrossThreadCalls on: continuations from SolutionCoordinator
            // lines 573 and 930. A running IDE never sees this -- its DoEvents calls are all nested
            // inside Application.Run's loop.
            //
            // Uninstall only replaces a context that IS a WindowsFormsSynchronizationContext, so the
            // installed one is a thin wrapper around it instead. (Turning
            // WindowsFormsSynchronizationContext.AutoInstall off also stops the swap, but the test
            // host then crashed in AppDomain.Unload with AppDomainUnloadedException.)
            if (SynchronizationContext.Current is UiThreadContext) return;
            SynchronizationContext.SetSynchronizationContext(
                new UiThreadContext(new WindowsFormsSynchronizationContext()));
        }

        /// <summary>Posts to the UI thread exactly as WindowsFormsSynchronizationContext does,
        /// without being one -- see <see cref="EnsureUiSynchronizationContext"/>.</summary>
        private sealed class UiThreadContext : SynchronizationContext
        {
            private readonly WindowsFormsSynchronizationContext _inner;
            internal UiThreadContext(WindowsFormsSynchronizationContext inner) { _inner = inner; }
            public override void Post(SendOrPostCallback d, object state) { _inner.Post(d, state); }
            public override void Send(SendOrPostCallback d, object state) { _inner.Send(d, state); }
            public override SynchronizationContext CreateCopy() { return this; }
        }

        public static MainForm CreateComposed()
        {
            // Before the form exists, not after: MainForm's constructor chain can reach an async
            // path, and a continuation captured with no context is already lost by then.
            EnsureUiSynchronizationContext();

            var form = new MainForm(
                ServiceLocator.CreateSolutionService(),
                ServiceLocator.CreateTypeService(),
                ServiceLocator.CreateProjectService(),
                ServiceLocator.CreateLayoutService(),
                ServiceLocator.CreateAnalysisEngine(),
                ServiceLocator.CreateDeletionImpactAnalyzer(),
                ServiceLocator.CreateRecentFilesManager(),
                ServiceLocator.CreateSolutionGenerator(),
                ServiceLocator.CreateCodeExporter(),
                ServiceLocator.CreateMetadataExporter(),
                new UserSettingsManager());

            form.LoadAddIn(new GuiDesignerAddIn(), AddInKind.Bundled,
                System.IO.Path.GetDirectoryName(typeof(GuiDesignerAddIn).Assembly.Location));
            return form;
        }

        /// <summary>
        /// <see cref="TestWindow.ShowOffScreen"/> (the one way a test shows a window, book Chapter 4,
        /// §4.6.5), then the settle pump the CG canvas tests were written against.
        /// </summary>
        public static void ShowOffScreen(Form form)
        {
            TestWindow.ShowOffScreen(form);
            PumpUntilIdle();
        }

        /// <summary>
        /// Drains the WinForms message queue for a while. Required after any action that triggers
        /// MainForm.ReloadSolutionAsync/OpenSolutionAsync — both are `async void` (fire-and-forget):
        /// their continuation only runs when the message loop next pumps, which without this can
        /// happen much later, on the SAME shared test-host process's SAME UI thread, during some
        /// completely unrelated LATER test's own Application.DoEvents() call. If that later test (or
        /// this one) has already deleted/moved the directory the stale continuation reads from, it
        /// throws inside SolutionService and MainForm shows a real, visible "Error Reloading"
        /// MessageBox with no test left running to dismiss it — reproduced live during this session
        /// . Call this before a test that triggered a reload asserts/returns/closes its
        /// form, so the continuation completes on its own watch instead of a stranger's.
        /// </summary>
        public static void PumpUntilIdle(int iterations = 40, int delayMs = 25)
        {
            for (int i = 0; i < iterations; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(delayMs);
            }
        }
    }
}
