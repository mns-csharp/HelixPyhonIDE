namespace HelixPythonNamespace.Core.Execution
{
    using System;
    using System.Threading;

    /// <summary>
    /// An <see cref="ISubsystem"/> backed by delegates. Lets the two engine families be
    /// presented through one interface without every engine implementing <see cref="ISubsystem"/>
    /// directly:
    ///  * in-process token jobs (compiler, linter, linker, analyzer): <c>stop</c> = trip the
    ///    <see cref="CancellationTokenSource"/>; <c>isRunning</c> = the job's busy flag.
    ///  * process-based engines (profiler, tests): <c>stop</c> = the engine's tree-kill
    ///    <c>Stop()</c>; <c>isRunning</c> = the engine's IsRunning.
    ///
    /// <see cref="Stop"/> is guarded so the underlying stop delegate runs at most once, is
    /// safe from any thread, and never throws.
    /// </summary>
    public sealed class DelegateSubsystem : ISubsystem
    {
        private readonly SubsystemKind _kind;
        private readonly string _name;
        private readonly Action _stop;
        private readonly Func<bool> _isRunning;
        private int _stopped;   // 0 = not yet requested, 1 = stop already invoked

        public DelegateSubsystem(SubsystemKind kind, string displayName, Action stop, Func<bool> isRunning)
        {
            _kind = kind;
            _name = string.IsNullOrEmpty(displayName) ? kind.ToString() : displayName;
            _stop = stop;
            _isRunning = isRunning;
        }

        public SubsystemKind Kind { get { return _kind; } }

        public string DisplayName { get { return _name; } }

        public bool IsRunning
        {
            get
            {
                // Once Stop() has been requested the subsystem is not running, regardless of what a
                // lagging engine probe reports.
                if (Interlocked.CompareExchange(ref _stopped, 0, 0) != 0) return false;
                if (_isRunning == null) return false;
                try { return _isRunning(); } catch { return false; }
            }
        }

        public event EventHandler StateChanged;

        public void Stop()
        {
            // Idempotent: only the first Stop() actually invokes the underlying delegate.
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            try { if (_stop != null) _stop(); } catch { }
            EventHandler h = StateChanged;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }
    }
}
