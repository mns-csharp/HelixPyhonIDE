namespace HelixPythonNamespace.App.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Forms;

    /// <summary>The observable lifecycle of the single foreground operation.</summary>
    public enum JobRunnerState
    {
        Idle,
        Running,
        Cancelling
    }

    public sealed class JobRunnerEventArgs : EventArgs
    {
        public JobRunnerEventArgs(string title, string finalStatus, bool cancelled)
        {
            Title = title;
            FinalStatus = finalStatus;
            Cancelled = cancelled;
        }

        public string Title { get; private set; }
        public string FinalStatus { get; private set; }
        public bool Cancelled { get; private set; }
    }

    /// <summary>
    /// Runs a single long-running operation on a background thread with cooperative
    /// cancellation, surfacing progress text and a Cancel button on the status strip.
    /// Only one job runs at a time; while busy <see cref="IsBusy"/> is true and the
    /// shared Cancel button is shown.
    ///
    /// Two job shapes are supported:
    ///  * <see cref="Run{T}"/> — blocking work executed on a <see cref="Task"/>, driven
    ///    by a <see cref="CancellationToken"/>. Cancellation cancels the token; if the
    ///    work returns a partial result after the token was tripped, it is discarded.
    ///  * <see cref="BeginExternal"/> / <see cref="Finish"/> — an already-started
    ///    event-driven engine (e.g. a child process wrapped by PythonProfiler / TestRunner).
    ///    The runner shows the Cancel button and wires it to the engine's stop delegate;
    ///    the caller must call <see cref="Finish"/> when the engine reports completion.
    /// </summary>
    public sealed class JobRunner
    {
        private readonly Control _marshal;            // marshals callbacks back onto the UI thread
        private readonly ToolStripStatusLabel _status;
        private readonly ToolStripItem _cancelButton;
        private CancellationTokenSource _cts;
        private Action _externalStop;                 // stop delegate for event-driven engines
        private string _title;
        // True between a token-job Cancel and its completion, while we wait for the background
        // thread to observe the token and wind down. Blocks the watchdog from re-arming.
        private bool _cancelling;
        // Grace period a cancelled token job is given to stop cooperatively before it is abandoned.
        private const int CancelGraceMs = 5000;
        // Monotonic job epoch. Bumped when a job starts and when a job is abandoned on cancel.
        // A background thread captures the epoch it ran under and no-ops its completion if the
        // epoch has since moved on — so an orphaned (non-cooperative) engine that keeps running
        // after Cancel cannot clobber the UI or a newer job when it finally returns.
        private int _generation;

        // Extra toolbar/menu items that mirror the busy state (Enabled, not Visible/hidden — these
        // are persistent, always-visible affordances like a toolbar Stop button, unlike the
        // status-strip Cancel button which only appears while a job is running) and route their
        // clicks to the same Cancel().
        private readonly System.Collections.Generic.List<ToolStripItem> _linkedStopItems =
            new System.Collections.Generic.List<ToolStripItem>();

        public JobRunner(Control marshal, ToolStripStatusLabel status, ToolStripItem cancelButton)
        {
            _marshal = marshal;
            _status = status;
            _cancelButton = cancelButton;
            if (_cancelButton != null)
            {
                _cancelButton.Visible = false;
                _cancelButton.Click += delegate { Cancel(); };
            }
        }

        /// <summary>
        /// Register an always-visible toolbar/menu item (e.g. a red "Stop" button) that should be
        /// enabled only while a job is running and clicking it should Cancel() the active job.
        /// </summary>
        public void RegisterStopItem(ToolStripItem item)
        {
            if (item == null) return;
            item.Enabled = IsBusy;
            item.Click += delegate { Cancel(); };
            _linkedStopItems.Add(item);
        }

        /// <summary>True while a job is running or winding down after cancellation.</summary>
        public bool IsBusy { get { return State != JobRunnerState.Idle; } }

        /// <summary>Authoritative operation state for command enablement and status consumers.</summary>
        public JobRunnerState State { get; private set; }

        /// <summary>The active operation title, or null while idle.</summary>
        public string CurrentTitle { get { return _title; } }

        public event EventHandler<JobRunnerEventArgs> JobStarted;
        public event EventHandler<JobRunnerEventArgs> JobFinished;

        /// <summary>
        /// Run blocking, token-aware <paramref name="work"/> on a background thread. On
        /// completion <paramref name="onSuccess"/> runs on the UI thread (skipped if the job
        /// was cancelled). Returns false if a job is already running.
        /// </summary>
        public bool Run<T>(string title, Func<CancellationToken, T> work, Action<T> onSuccess,
            Action<Exception> onError = null, Action onCancelled = null)
        {
            // Enforce single-job policy.
            if (IsBusy) return false;
            BeginJob(title, null);
            // Capture the epoch and token this run belongs to before spawning the thread.
            int gen = _generation;
            CancellationToken token = _cts.Token;
            Task.Run(delegate
            {
                try
                {
                    // Run the blocking work off-thread, then note whether cancel was requested.
                    T result = work(token);
                    bool cancelled = token.IsCancellationRequested;
                    // Back on the UI thread: deliver success (unless cancelled) and end the job.
                    Marshal(delegate
                    {
                        if (gen != _generation) return;   // abandoned on cancel / superseded
                        if (cancelled) { if (onCancelled != null) onCancelled(); }
                        else if (onSuccess != null) onSuccess(result);
                        EndJob(cancelled ? title + " cancelled" : null);
                    });
                }
                // Cooperative cancellation via the token: run the cancel cleanup and close out.
                catch (OperationCanceledException)
                {
                    Marshal(delegate
                    {
                        if (gen != _generation) return;
                        if (onCancelled != null) onCancelled();
                        EndJob(title + " cancelled");
                    });
                }
                // Any other failure: report via onError (or the status label) and end the job.
                catch (Exception ex)
                {
                    Marshal(delegate
                    {
                        if (gen != _generation) return;
                        if (onError != null) onError(ex);
                        else if (_status != null) _status.Text = title + " failed: " + ex.Message;
                        EndJob(null);
                    });
                }
            });
            return true;
        }

        /// <summary>
        /// Register an already-started event-driven engine. Shows the Cancel button wired to
        /// <paramref name="stop"/>. The caller must invoke <see cref="Finish"/> when done.
        /// Returns false if a job is already running.
        /// </summary>
        public bool BeginExternal(string title, Action stop)
        {
            if (IsBusy) return false;
            BeginJob(title, stop);
            return true;
        }

        /// <summary>Complete an external job (safe to call from any thread).</summary>
        public void Finish(string finalStatus = null)
        {
            Marshal(delegate { EndJob(finalStatus); });
        }

        /// <summary>Cancel the active job: cancels the token and/or calls the engine stop delegate.</summary>
        public void Cancel()
        {
            if (!IsBusy) return;
            State = JobRunnerState.Cancelling;
            // Token cancel is instant — do it inline so a cooperative engine stops ASAP.
            try { if (_cts != null) _cts.Cancel(); } catch { }

            Action stop = _externalStop;
            if (stop != null)
            {
                // Event-driven engine (tests/profiler): stopping kills a process tree
                // (taskkill + WaitForExit) and can block for seconds — offload it so the UI
                // stays responsive. The engine raises its own completion (→ Finish → EndJob).
                if (_status != null) _status.Text = (_title ?? "Job") + ": cancelling…";
                Task.Run(delegate { try { stop(); } catch { } });
            }
            else
            {
                // Blocking token job (lint / compile / build / metrics / analyze). The engines
                // now observe the token at a fine grain (stage/rule/statement boundaries), so the
                // background thread stops promptly and its own completion path runs EndJob. Keep
                // IsBusy true meanwhile — reflecting a "cancelling" state — so the user cannot
                // stack a second heavy job on top of one that is still winding down (which is what
                // made Cancel feel like it did nothing). A watchdog abandons a genuinely
                // non-cooperative engine after a grace period so a misbehaving stage can't wedge
                // the runner forever.
                if (!_cancelling)
                {
                    _cancelling = true;
                    if (_status != null) _status.Text = (_title ?? "Job") + ": cancelling…";
                    ArmCancelWatchdog(_generation);
                }
            }
        }

        // Fallback for a token job whose engine never observes cancellation: after a grace period,
        // if the same job is still running, abandon it — bump the epoch so its eventual completion
        // no-ops, and free the UI. Cooperative jobs finish well before this fires.
        private void ArmCancelWatchdog(int gen)
        {
            Task.Delay(CancelGraceMs).ContinueWith(delegate
            {
                Marshal(delegate
                {
                    if (IsBusy && gen == _generation)
                    {
                        _generation++;
                        EndJob((_title ?? "Job") + " cancelled");
                    }
                });
            });
        }

        /// <summary>Optional status-strip busy indicator (e.g. a marquee progress bar), shown for the
        /// duration of a job so the user can see the app is working, not frozen.</summary>
        public ToolStripItem BusyIndicator { get; set; }

        // Enter the busy state: bump the epoch, arm a fresh token, and show progress + Cancel.
        private void BeginJob(string title, Action externalStop)
        {
            State = JobRunnerState.Running;
            _generation++;
            _title = title;
            _cancelling = false;
            _cts = new CancellationTokenSource();
            _externalStop = externalStop;
            if (_status != null) _status.Text = title + "…";
            if (_cancelButton != null) _cancelButton.Visible = true;
            if (BusyIndicator != null) BusyIndicator.Visible = true;
            foreach (ToolStripItem it in _linkedStopItems) it.Enabled = true;
            EventHandler<JobRunnerEventArgs> started = JobStarted;
            if (started != null) started(this, new JobRunnerEventArgs(title, null, false));
        }

        // Leave the busy state: dispose the token, hide Cancel, and optionally set final status.
        private void EndJob(string finalStatus)
        {
            string completedTitle = _title;
            bool cancelled = _cancelling || State == JobRunnerState.Cancelling;
            State = JobRunnerState.Idle;
            _cancelling = false;
            _externalStop = null;
            if (_cts != null) { _cts.Dispose(); _cts = null; }
            if (_cancelButton != null) _cancelButton.Visible = false;
            if (BusyIndicator != null) BusyIndicator.Visible = false;
            foreach (ToolStripItem it in _linkedStopItems) it.Enabled = false;
            if (finalStatus != null && _status != null) _status.Text = finalStatus;
            _title = null;
            EventHandler<JobRunnerEventArgs> finished = JobFinished;
            if (finished != null) finished(this, new JobRunnerEventArgs(completedTitle, finalStatus, cancelled));
        }

        // Run a callback on the UI thread. During shutdown the handle may be torn down between
        // the check and the call, so a failed BeginInvoke falls through to running inline.
        private void Marshal(Action a)
        {
            if (a == null) return;
            if (_marshal != null && _marshal.IsHandleCreated && _marshal.InvokeRequired)
            {
                try { _marshal.BeginInvoke(a); return; }
                catch { /* handle torn down during shutdown — fall through */ }
            }
            a();
        }
    }
}
