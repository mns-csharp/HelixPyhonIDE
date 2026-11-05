namespace HelixPythonNamespace.CudaEmulator
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Emulates the host-side CUDA runtime API against the shared Memory arena. All
    /// calls return cudaSuccess (int 0) as a Value unless they fault. CudaMalloc takes
    /// an LValue (the `*p` to fill) because there is no real address to pass. Port of
    /// the C++ helix::cuda_emulator::HostRuntime (cuda_emulator/host_runtime.{h,cpp}).
    /// </summary>
    public sealed class HostRuntime
    {
        private static readonly HashSet<string> HostCalls = new HashSet<string>
        {
            "cudaMalloc", "cudaMallocManaged", "cudaMallocHost", "cudaFree", "cudaFreeHost",
            "cudaMemcpy", "cudaMemcpyAsync", "cudaMemset", "cudaMemsetAsync",
            "cudaDeviceSynchronize", "cudaStreamSynchronize", "cudaGetLastError",
            "cudaPeekAtLastError", "cudaSetDevice", "cudaGetDevice", "cudaGetDeviceCount",
            "cudaStreamCreate", "cudaStreamDestroy", "cudaEventCreate", "cudaEventRecord",
            "cudaEventSynchronize", "cudaEventElapsedTime", "cudaEventDestroy",
            "cudaDeviceReset", "cudaGetDeviceProperties",
        };

        private readonly Memory mem_;
        private readonly IOutputSink out_;

        public HostRuntime(Memory mem, IOutputSink outSink)
        {
            mem_ = mem;
            out_ = outSink;
        }

        public static bool IsHostCall(string name)
        {
            return name != null && HostCalls.Contains(name);
        }

        public Value CudaMalloc(LValue outLv, long bytes, ElemType elem, uint elemSize, SourceSpan span)
        {
            Pointer p = mem_.Allocate(bytes, MemSpace.Device, elem, elemSize, "cudaMalloc");
            outLv.Set(mem_, new Value(p), span);
            return new Value(0L);  // cudaSuccess
        }

        public Value CudaMemcpy(Value dst, Value src, long bytes, int kind, SourceSpan span)
        {
            // one coherent arena: all directions are byte copies (bounds-checked)
            if (!dst.IsPointer || !src.IsPointer)
                throw new RuntimeError(RuntimeError.Kind.BadLaunch, "cudaMemcpy expects pointer operands", span);
            mem_.Copy(dst.AsPointer, src.AsPointer, bytes, span);
            return new Value(0L);
        }

        public Value CudaFree(Value p, SourceSpan span)
        {
            if (!p.IsPointer)
                throw new RuntimeError(RuntimeError.Kind.BadLaunch, "cudaFree expects a pointer", span);
            mem_.Free(p.AsPointer, span);
            return new Value(0L);
        }

        public Value CudaMemset(Value p, int value, long bytes, SourceSpan span)
        {
            if (!p.IsPointer)
                throw new RuntimeError(RuntimeError.Kind.BadLaunch, "cudaMemset expects a pointer", span);
            mem_.Memset(p.AsPointer, value, bytes, span);
            return new Value(0L);
        }

        public Value CudaNoOp()
        {
            return new Value(0L);
        }

        public Memory Mem { get { return mem_; } }
    }
}
