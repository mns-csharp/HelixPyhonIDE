namespace HelixPythonNamespace.CudaEmulator
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// What a builtin can touch. Thread/Block are null in host context. Port of
    /// helix::cuda_emulator::IntrinsicCtx (cuda_emulator/intrinsic_registry.h).
    /// </summary>
    public sealed class IntrinsicCtx
    {
        public Memory Mem;
        public IOutputSink Out;
        public ThreadContext Thread;
        public BlockScheduler Block;
        public SourceSpan CallSpan;
    }

    /// <summary>A builtin implementation. Port of the C++ IntrinsicFn std::function.</summary>
    public delegate Value IntrinsicFn(IntrinsicCtx ctx, List<Value> args);

    /// <summary>
    /// name → builtin implementation (math, atomics, sync, warp, printf). Port of
    /// helix::cuda_emulator::IntrinsicRegistry (cuda_emulator/intrinsic_registry.h).
    /// </summary>
    public sealed class IntrinsicRegistry
    {
        private readonly Dictionary<string, IntrinsicFn> fns_ = new Dictionary<string, IntrinsicFn>();

        public void Register(string name, IntrinsicFn fn)
        {
            fns_[name] = fn;
        }

        public IntrinsicFn Find(string name)
        {
            IntrinsicFn fn;
            return (name != null && fns_.TryGetValue(name, out fn)) ? fn : null;
        }
    }
}
