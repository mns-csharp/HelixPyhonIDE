namespace HelixPythonNamespace.CudaEmulator
{
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Text;
    using HelixPythonNamespace.CudaVirtualCompiler;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;
    using HelixPythonNamespace.Python.Text;

    /// <summary>Outcome of an emulation run. Port of helix::cuda_emulator::EmulationResult.</summary>
    public sealed class EmulationResult
    {
        public bool Ok;
        public RuntimeError Error;   // null unless a fault occurred
        public int ExitCode;
    }

    /// <summary>Per-kernel timing sample. Port of helix::cuda_emulator::KernelTimingSample.</summary>
    public sealed class KernelTimingSample
    {
        public string Name;
        public Dim3 Grid;
        public Dim3 Block;
        public ulong TotalThreads;
        public long ElapsedNs;
    }

    /// <summary>Aggregate statistics for a RunProgram() call. Port of helix::cuda_emulator::EmulationStats.</summary>
    public sealed class EmulationStats
    {
        public readonly List<KernelTimingSample> Kernels = new List<KernelTimingSample>();
        public long TotalKernelNs;
    }

    /// <summary>Convenience sink that captures output for tests. Port of helix::cuda_emulator::StringOutputSink.</summary>
    public sealed class StringOutputSink : IOutputSink
    {
        private readonly StringBuilder buffer_ = new StringBuilder();
        private readonly object lock_ = new object();

        public void Write(string text)
        {
            lock (lock_) { buffer_.Append(text); }
        }

        public string Text { get { lock (lock_) { return buffer_.ToString(); } } }
    }

    /// <summary>
    /// Top-level CPU CUDA emulator. Holds a reference to the compiled result (which
    /// owns the AST + symbol table) for the lifetime of the run. Acts as the
    /// IKernelLauncher for KernelLaunchExpr encountered in host code. Port of the C++
    /// helix::cuda_emulator::CudaEmulator (cuda_emulator/cuda_emulator.{h,cpp}).
    /// </summary>
    public sealed class CudaEmulator : IKernelLauncher
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly CudaCompileResult compiled_;
        private readonly SymbolTable symtab_;
        private readonly IOutputSink out_;
        private readonly Memory mem_ = new Memory();
        private readonly IntrinsicRegistry intrinsics_ = new IntrinsicRegistry();
        private readonly HostRuntime host_;

        private IDebugHook debugHook_;
        private Dim3 debugBlock_;
        private Dim3 debugThread_;

        private EmulationStats stats_ = new EmulationStats();

        public CudaEmulator(CudaCompileResult compiled, IOutputSink outSink)
        {
            compiled_ = compiled;
            symtab_ = compiled != null ? compiled.SymbolTable : null;
            out_ = outSink;
            host_ = new HostRuntime(mem_, outSink);
            DeviceIntrinsics.SeedDeviceIntrinsics(intrinsics_);
        }

        public Memory Mem { get { return mem_; } }
        public EmulationStats Stats { get { return stats_; } }

        private static long NowNs()
        {
            return Clock.Elapsed.Ticks * 100L;  // 1 tick = 100 ns
        }

        public EmulationResult RunProgram(string entry = "main", IDebugHook hook = null)
        {
            stats_ = new EmulationStats();  // clear per-run stats
            EmulationResult result = new EmulationResult();
            if (symtab_ == null)
            {
                result.Error = new RuntimeError(RuntimeError.Kind.Other, "no symbol table (parse failed)", SourceSpan.Empty);
                return result;
            }
            FunctionDecl entryFn = null;
            List<Symbol> syms = symtab_.FindFunctions(entry);
            for (int i = 0; i < syms.Count; ++i)
            {
                if (syms[i].FuncDecl != null && syms[i].FuncDecl.HasBody) { entryFn = syms[i].FuncDecl; break; }
            }
            if (entryFn == null)
            {
                result.Error = new RuntimeError(RuntimeError.Kind.Other, "no runnable entry function '" + entry + "'", SourceSpan.Empty);
                out_.Write("error: " + result.Error.ErrorMessage + "\n");
                return result;
            }

            Interpreter interp = new Interpreter(mem_, symtab_, intrinsics_, host_, out_);
            interp.SetKernelLauncher(this);
            if (hook != null) interp.SetDebugHook(hook);
            // Allocate + initialize top-level globals into the shared arena BEFORE the
            // entry point runs, so both host code and every launched kernel see them.
            if (compiled_ != null && compiled_.Unit != null)
                interp.InitGlobals(compiled_.Unit.Decls);
            try
            {
                Value r = interp.Call(entryFn, new List<Value>());
                result.Ok = true;
                result.ExitCode = (int)r.ToInt64();
            }
            catch (RuntimeError e)
            {
                result.Ok = false;
                result.Error = e;
                out_.Write("runtime error (" + e.Span.Start.Line.ToString(CultureInfo.InvariantCulture) + ":" +
                           e.Span.Start.Column.ToString(CultureInfo.InvariantCulture) + "): " + e.ErrorMessage + "\n");
            }
            return result;
        }

        private void RunGrid(FunctionDecl kernel, Dim3 grid, Dim3 block, List<Value> args, long dynamicShared)
        {
            RunGrid(kernel, grid, block, args, dynamicShared, null, default(Dim3), default(Dim3));
        }

        /// <summary>
        /// Run the whole grid, optionally with <paramref name="hook"/> attached to the single lane
        /// (<paramref name="debugBlock"/>, <paramref name="debugThread"/>).
        ///
        /// <para>
        /// The grid runs in full either way. That is the difference between this and the
        /// single-thread debug path used for a standalone <c>.cu</c> program: here the kernel was
        /// launched by a live Python program that is going to copy the results back and keep
        /// going, so every thread must do its work even while one of them is parked at a
        /// breakpoint.
        /// </para>
        /// </summary>
        private void RunGrid(FunctionDecl kernel, Dim3 grid, Dim3 block, List<Value> args,
            long dynamicShared, IDebugHook hook, Dim3 debugThread, Dim3 debugBlock)
        {
            long t0 = NowNs();
            for (ulong bz = 0; bz < grid.Z; ++bz)
                for (ulong by = 0; by < grid.Y; ++by)
                    for (ulong bx = 0; bx < grid.X; ++bx)
                    {
                        BlockScheduler.Config cfg = new BlockScheduler.Config();
                        cfg.Kernel = kernel;
                        cfg.Args = args;
                        cfg.BlockIdx = new Dim3(bx, by, bz);
                        cfg.BlockDim = block;
                        cfg.GridDim = grid;
                        cfg.DynamicSharedBytes = dynamicShared;
                        cfg.DebugHook = hook;
                        cfg.DebugThread = debugThread;
                        cfg.DebugBlock = debugBlock;
                        cfg.DebugLaneOnly = hook != null
                            && bx == debugBlock.X && by == debugBlock.Y && bz == debugBlock.Z;
                        BlockScheduler sched = new BlockScheduler(cfg, mem_, intrinsics_, host_, symtab_, out_, this);
                        sched.RunBlock();  // throws RuntimeError on fault → propagates
                    }
            long elapsed = NowNs() - t0;
            ulong totalThreads = grid.X * grid.Y * grid.Z * block.X * block.Y * block.Z;
            KernelTimingSample s = new KernelTimingSample();
            s.Name = kernel != null ? kernel.Name : "(unknown)";
            s.Grid = grid;
            s.Block = block;
            s.TotalThreads = totalThreads;
            s.ElapsedNs = elapsed;
            stats_.Kernels.Add(s);
            stats_.TotalKernelNs += elapsed;
        }

        public EmulationResult RunProgramDebug(string entry, IDebugHook hook, Dim3 debugBlock, Dim3 debugThread)
        {
            debugHook_ = hook;
            debugBlock_ = debugBlock;
            debugThread_ = debugThread;
            EmulationResult r = RunProgram(entry, hook);
            debugHook_ = null;
            return r;
        }

        public Value Launch(KernelLaunchExpr launch, Interpreter caller)
        {
            FunctionDecl kernel = null;
            NameExpr callee = launch.Callee as NameExpr;
            if (callee != null && symtab_ != null)
            {
                List<Symbol> syms = symtab_.FindFunctions(callee.Id);
                for (int i = 0; i < syms.Count; ++i)
                {
                    if (syms[i].IsKernel && syms[i].FuncDecl != null && syms[i].FuncDecl.HasBody)
                    {
                        kernel = syms[i].FuncDecl;
                        break;
                    }
                }
            }
            if (kernel == null)
                throw new RuntimeError(RuntimeError.Kind.BadLaunch, "launch of unknown kernel", launch.Span);

            Dim3 grid = ToDim3(caller.Eval(launch.Grid));
            Dim3 block = ToDim3(caller.Eval(launch.Block));
            long dynShared = launch.SharedMem != null ? caller.Eval(launch.SharedMem).ToInt64() : 0;
            List<Value> args = new List<Value>();
            for (int i = 0; i < launch.Args.Count; ++i) args.Add(caller.Eval(launch.Args[i]));

            // Debug mode: run only the chosen thread, with the hook, barriers no-op.
            if (debugHook_ != null)
            {
                Dictionary<string, Pointer> shared = BlockScheduler.AllocateKernelSharedVars(
                    kernel, mem_, intrinsics_, host_, symtab_, out_, debugBlock_, block, grid, dynShared);
                ThreadContext tc = new ThreadContext();
                tc.ThreadIdx = debugThread_;
                tc.BlockIdx = debugBlock_;
                tc.BlockDim = block;
                tc.GridDim = grid;
                tc.LinearTid = (uint)((debugThread_.Z * block.Y + debugThread_.Y) * block.X + debugThread_.X);
                tc.SharedVars = shared;
                Interpreter interp = new Interpreter(mem_, symtab_, intrinsics_, host_, out_);
                interp.SetThreadContext(tc);
                interp.SetKernelLauncher(this);
                interp.SetDebugHook(debugHook_);
                interp.Call(kernel, args);
                return new Value();
            }

            RunGrid(kernel, grid, block, args, dynShared);
            return new Value();
        }

        public EmulationResult LaunchKernel(FunctionDecl kernel, Dim3 grid, Dim3 block,
            List<Value> boundArgs, long dynamicSharedBytes = 0,
            IDebugHook singleThreadHook = null, Dim3 debugThread = default(Dim3), Dim3 debugBlock = default(Dim3))
        {
            EmulationResult result = new EmulationResult();
            try
            {
                RunGrid(kernel, grid, block, boundArgs, dynamicSharedBytes,
                        singleThreadHook, debugThread, debugBlock);
                result.Ok = true;
            }
            catch (RuntimeError e)
            {
                result.Ok = false;
                result.Error = e;
                out_.Write("runtime error (" + e.Span.Start.Line.ToString(CultureInfo.InvariantCulture) + ":" +
                           e.Span.Start.Column.ToString(CultureInfo.InvariantCulture) + "): " + e.ErrorMessage + "\n");
            }
            return result;
        }

        private static Dim3 ToDim3(Value v)
        {
            if (v.IsDim3) return v.AsDim3;
            if (v.IsInt || v.IsDouble) return new Dim3((ulong)v.ToInt64(), 1, 1);
            return new Dim3(1, 1, 1);
        }
    }
}
