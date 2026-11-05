namespace HelixPythonNamespace.CudaEmulator
{
    /// <summary>
    /// The wire vocabulary shared by the two halves of a kernel debug session: the IDE
    /// (<c>CudaKernelDebugSession</c>, in HelixStudio.Debugger) and the bridge process
    /// (<c>KernelDebugChannel</c>, in helix_cuda_bridge.exe).
    ///
    /// <para>
    /// It lives here, in the emulator assembly, because that is the only assembly both halves
    /// already reference. Duplicating the strings on each side would compile perfectly and then
    /// fail at run time as a session that connects and never speaks — the worst shape of bug for
    /// something whose failure mode is silence.
    /// </para>
    ///
    /// <para>
    /// <b>Why a second channel at all.</b> The bridge's stdin/stdout already carries the Python
    /// program's CUDA calls, one JSON line each way, strictly alternating. A debugger cannot share
    /// that pipe: it has to speak while a launch request is still unanswered — that is precisely
    /// what being paused inside a kernel means. So the IDE listens on a loopback port, passes it in
    /// <see cref="PortVariable"/>, and the bridge dials back. Python never sees any of it.
    /// </para>
    /// </summary>
    public static class KernelDebugProtocol
    {
        /// <summary>
        /// Environment variable carrying the loopback port the IDE is listening on. Set on the
        /// PYTHON process; the bridge inherits it because Python starts the bridge without an
        /// explicit environment. Unset means "run kernels normally, with no debugger attached",
        /// which is what every non-debug run and every run outside the IDE gets.
        /// </summary>
        public const string PortVariable = "HELIX_CUDA_DEBUG_PORT";

        /// <summary>How long the bridge waits for the IDE to finish configuring, in milliseconds.
        /// On expiry it runs kernels undebugged rather than hanging: a debugger that failed to
        /// configure must not be able to freeze the user's program.</summary>
        public const int ConfigureTimeoutMs = 15000;

        // ---- IDE -> bridge ----
        public const string CmdConfigure = "configure";     // breakpoints + lane, before any kernel runs
        public const string CmdBreakpoints = "breakpoints"; // re-push while the program is running
        public const string CmdGo = "go";                   // configuration done; serve Python
        public const string CmdContinue = "continue";
        public const string CmdStepInto = "stepInto";
        public const string CmdStepOver = "stepOver";
        public const string CmdStepOut = "stepOut";
        public const string CmdStop = "stop";               // detach: stop pausing, let kernels run out

        // ---- bridge -> IDE ----
        public const string EventAttached = "attached";
        public const string EventKernelEntered = "kernelEntered";
        public const string EventStopped = "stopped";
        public const string EventResumed = "resumed";
        public const string EventKernelExited = "kernelExited";
        public const string EventOutput = "output";
        public const string EventDetached = "detached";
    }
}
