namespace HelixPythonNamespace.Core.Execution
{
    using System;

    /// <summary>
    /// A long-running IDE subsystem (virtual-compiler, linter, profiler, linker, analyzer,
    /// or unit-test runner) that is started elsewhere but can be stopped through one uniform
    /// contract. This is the single abstraction the UI's unified Stop affordance targets.
    ///
    /// Contract for implementers:
    ///  * <see cref="Stop"/> MUST be idempotent, safe to call from any thread, and MUST NOT throw.
    ///  * <see cref="Stop"/> is a *request* — for cooperative token jobs it trips the token; for
    ///    process-based engines it tree-kills the child. Either way the caller may observe
    ///    <see cref="IsRunning"/> going false slightly later, once the work actually winds down.
    /// </summary>
    public interface ISubsystem
    {
        /// <summary>Which of the six subsystems this represents.</summary>
        SubsystemKind Kind { get; }

        /// <summary>Human-readable label shown in status text and the running-jobs UI.</summary>
        string DisplayName { get; }

        /// <summary>True while the underlying work is running.</summary>
        bool IsRunning { get; }

        /// <summary>
        /// Request that the running work stop as soon as possible. Idempotent, thread-safe,
        /// and must not throw. Returns immediately; actual teardown may complete asynchronously.
        /// </summary>
        void Stop();

        /// <summary>Raised when <see cref="IsRunning"/> transitions. May be raised on a background thread.</summary>
        event EventHandler StateChanged;
    }
}
