namespace HelixPythonNamespace.CudaEmulator
{
    using System.Collections.Generic;

    /// <summary>
    /// Per-thread state for SIMT execution. The builtin-variable fields are read by
    /// the interpreter's NameExpr handler; the scheduling fields are used by
    /// BlockScheduler. Port of the C++ helix::cuda_emulator::ThreadContext
    /// (cuda_emulator/thread_context.h). Dim3 fields default to (1,1,1) as in C++.
    /// </summary>
    public sealed class ThreadContext
    {
        public Dim3 ThreadIdx = Dim3.One();
        public Dim3 BlockIdx = Dim3.One();
        public Dim3 BlockDim = Dim3.One();
        public Dim3 GridDim = Dim3.One();
        public uint WarpSize = 32;
        public uint LinearTid;

        // Per-block __shared__ variables (name → arena pointer), allocated once per
        // block by the scheduler and shared by every thread in the block.
        public IReadOnlyDictionary<string, Pointer> SharedVars;

        // Scheduling (BlockScheduler).
        public enum State { Ready, Running, AtBarrier, Finished }
        public State ThreadState = State.Ready;
        public int BarrierEpoch;
        public Interpreter Interp;
        public bool HasError;
    }
}
