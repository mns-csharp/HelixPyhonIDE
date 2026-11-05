namespace HelixPythonNamespace.Debugger
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The control + query surface shared by the two debug engines — <see cref="PythonDebugEngine"/>
    /// (debugpy/DAP) and <see cref="CudaEmulatorDebugEngine"/> (CPU SIMT emulator). It lets the App's
    /// <c>DebugController</c> and the single Debug panel (Variables / Call Stack / Breakpoints, plus
    /// the editor's breakpoint + current-line markers) drive either engine without knowing which, so
    /// CUDA kernels debug from the same tabbed-editor position as Python.
    ///
    /// The two divergent bits are intentionally excluded: each engine's <c>Start(...)</c> takes
    /// different inputs (a script path vs. source text + a thread lane), and watch <c>Evaluate(...)</c>
    /// is debugpy-only. Both are handled at the DebugController seam.
    /// </summary>
    public interface IDebugEngine : IDisposable
    {
        bool IsDebugging { get; }
        bool IsPaused { get; }

        event EventHandler<DebugStoppedEventArgs> Stopped;
        event EventHandler<DebugOutputEventArgs> OutputReceived;
        event EventHandler SessionEnded;

        void Continue();
        void StepOver();
        void StepInto();
        void StepOut();
        void Pause();
        void StopDebugging();

        List<DebugStackFrame> GetCallStack();
        List<DebugScope> GetScopes(int frameId);
        List<DebugVariable> GetVariables(int variablesReference);
    }
}
