namespace HelixPythonNamespace.Core.Execution
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Central registry of currently-running subsystems and the single place the UI stops them.
    /// Thread-safe. Each engine registers itself for the duration of a run (via
    /// <see cref="Register"/>, disposing the returned token when the run ends); the UI drives one
    /// unified Stop affordance through <see cref="StopAll"/> / <see cref="StopKind"/> regardless of
    /// which engine is live. This replaces the previous ad-hoc mix (tests had its own Stop menu,
    /// everything else shared the Cancel button) with one uniform stop contract.
    /// </summary>
    public sealed class SubsystemController
    {
        private readonly object _gate = new object();
        private readonly List<ISubsystem> _running = new List<ISubsystem>();

        /// <summary>Raised whenever the set of running subsystems changes (register/unregister).</summary>
        public event EventHandler RunningChanged;

        /// <summary>True while at least one subsystem is registered as running.</summary>
        public bool IsAnyRunning
        {
            get { lock (_gate) { return _running.Count > 0; } }
        }

        /// <summary>Snapshot of the kinds currently running (order = registration order).</summary>
        public IReadOnlyList<SubsystemKind> RunningKinds
        {
            get
            {
                lock (_gate)
                {
                    List<SubsystemKind> kinds = new List<SubsystemKind>(_running.Count);
                    for (int i = 0; i < _running.Count; i++) kinds.Add(_running[i].Kind);
                    return kinds;
                }
            }
        }

        /// <summary>True if a subsystem of the given kind is currently registered as running.</summary>
        public bool IsRunning(SubsystemKind kind)
        {
            lock (_gate)
            {
                for (int i = 0; i < _running.Count; i++)
                    if (_running[i].Kind == kind) return true;
            }
            return false;
        }

        /// <summary>
        /// Register <paramref name="subsystem"/> as running. Returns a token whose
        /// <see cref="IDisposable.Dispose"/> unregisters it (call when the run completes,
        /// cancels, or fails). Registering the same instance twice is a no-op.
        /// </summary>
        public IDisposable Register(ISubsystem subsystem)
        {
            if (subsystem == null) throw new ArgumentNullException("subsystem");
            bool added = false;
            lock (_gate)
            {
                if (!_running.Contains(subsystem)) { _running.Add(subsystem); added = true; }
            }
            if (added) RaiseChanged();
            return new Registration(this, subsystem);
        }

        private void Unregister(ISubsystem subsystem)
        {
            bool removed;
            lock (_gate) { removed = _running.Remove(subsystem); }
            if (removed) RaiseChanged();
        }

        /// <summary>
        /// Stop every running subsystem. Returns how many were asked to stop. Never throws;
        /// a Stop() that throws is swallowed so one misbehaving engine cannot block the others.
        /// </summary>
        public int StopAll()
        {
            ISubsystem[] snapshot;
            lock (_gate) { snapshot = _running.ToArray(); }
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i].Stop(); } catch { }
            }
            return snapshot.Length;
        }

        /// <summary>Stop all running subsystems of a given kind. Returns how many were asked to stop.</summary>
        public int StopKind(SubsystemKind kind)
        {
            ISubsystem[] snapshot;
            lock (_gate) { snapshot = _running.ToArray(); }
            int n = 0;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i].Kind == kind)
                {
                    try { snapshot[i].Stop(); n++; } catch { }
                }
            }
            return n;
        }

        private void RaiseChanged()
        {
            EventHandler h = RunningChanged;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }

        // Per-registration token: unregisters exactly once, even if disposed repeatedly.
        private sealed class Registration : IDisposable
        {
            private SubsystemController _owner;
            private ISubsystem _subsystem;

            public Registration(SubsystemController owner, ISubsystem subsystem)
            {
                _owner = owner;
                _subsystem = subsystem;
            }

            public void Dispose()
            {
                SubsystemController owner = _owner;
                ISubsystem sub = _subsystem;
                _owner = null;
                _subsystem = null;
                if (owner != null && sub != null) owner.Unregister(sub);
            }
        }
    }
}
