namespace HelixPythonNamespace.CudaEmulator
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using HelixPythonNamespace.CudaVirtualCompiler.Analyze;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;

    /// <summary>Which __shfl_*_sync variant to perform. Port of helix::cuda_emulator::ShuffleOp.</summary>
    public enum ShuffleOp { Idx, Xor, Up, Down }

    // Per-warp exchange buffer for warp shuffle intrinsics. Two-phase protocol: all
    // lanes write their value, then all lanes read their source lane's value. C++
    // used a mutex + two condition_variables; C# uses one lock + Monitor.Wait/PulseAll
    // with predicate loops.
    internal sealed class WarpShuffleBuf
    {
        public readonly object Mu = new object();
        public int Total;
        public int WriteArrived;
        public int ReadDone;
        public bool ReadReady;
        public List<Value> Vals = new List<Value>();
    }

    /// <summary>
    /// Runs one CUDA block of threads. Kernels that use __syncthreads or warp shuffle
    /// intrinsics run each thread on its own Thread with manual barriers; kernels
    /// without them run threads sequentially for speed. __shared__ variables are
    /// allocated once and shared by the block. Port of the C++
    /// helix::cuda_emulator::BlockScheduler (cuda_emulator/block_scheduler.{h,cpp}).
    /// </summary>
    public sealed class BlockScheduler
    {
        public sealed class Config
        {
            public FunctionDecl Kernel;
            public List<Value> Args;
            public Dim3 BlockIdx;
            public Dim3 BlockDim;
            public Dim3 GridDim;
            public long DynamicSharedBytes;

            /// <summary>
            /// Optional debugger. Installed on ONE thread only -- the lane named by
            /// <see cref="DebugThread"/> inside the block named by <see cref="DebugBlock"/> -- and
            /// only when <see cref="DebugLaneOnly"/> says this block is that block.
            ///
            /// <para>
            /// Every other thread runs at full speed with no hook, which is the point: a kernel
            /// being debugged still executes its whole grid, so the buffers the program reads back
            /// afterwards hold the real answer. Stopping the grid at one thread and calling that a
            /// debug session would hand the user correct-looking control flow over wrong data.
            /// </para>
            /// </summary>
            public IDebugHook DebugHook;
            public Dim3 DebugThread;
            public Dim3 DebugBlock;
            /// <summary>True when this block contains the debugged lane.</summary>
            public bool DebugLaneOnly;
        }

        private readonly Config cfg_;
        private readonly Memory mem_;
        private readonly IntrinsicRegistry intr_;
        private readonly HostRuntime host_;
        private readonly SymbolTable symtab_;
        private readonly IOutputSink out_;
        private readonly IKernelLauncher launcher_;

        private Dictionary<string, Pointer> sharedVars_ = new Dictionary<string, Pointer>();

        // Block-level barrier state (threaded path).
        private readonly object barrierMutex_ = new object();
        private int total_;
        private int arrived_;
        private int finished_;
        private int barrierGen_;
        private bool divergent_;

        private readonly List<WarpShuffleBuf> warpBufs_ = new List<WarpShuffleBuf>();

        private readonly object errorMutex_ = new object();
        private RuntimeError firstError_;

        public BlockScheduler(Config cfg, Memory mem, IntrinsicRegistry intr, HostRuntime host,
                              SymbolTable symtab, IOutputSink outSink, IKernelLauncher launcher)
        {
            cfg_ = cfg;
            mem_ = mem;
            intr_ = intr;
            host_ = host;
            symtab_ = symtab;
            out_ = outSink;
            launcher_ = launcher;
        }

        /// <summary>Runs the whole block. Throws RuntimeError on the first thread fault.</summary>
        public void RunBlock()
        {
            AllocateShared();
            InitWarpBufs();
            if (KernelUsesBarrier(cfg_.Kernel, symtab_))
                RunThreaded();
            else
                RunSequential();
            if (firstError_ != null) throw firstError_;
        }

        private void AllocateShared()
        {
            sharedVars_ = AllocateKernelSharedVars(cfg_.Kernel, mem_, intr_, host_, symtab_, out_,
                                                   cfg_.BlockIdx, cfg_.BlockDim, cfg_.GridDim,
                                                   cfg_.DynamicSharedBytes);
        }

        private void RecordError(RuntimeError e)
        {
            lock (errorMutex_)
            {
                if (firstError_ == null) firstError_ = e;
            }
        }

        private void InitWarpBufs()
        {
            Dim3 bd = cfg_.BlockDim;
            int totalThreads = (int)(bd.X * bd.Y * bd.Z);
            int numWarps = (totalThreads + 31) / 32;
            warpBufs_.Clear();
            for (int w = 0; w < numWarps; ++w)
            {
                int start = w * 32;
                int end = Math.Min(start + 32, totalThreads);
                WarpShuffleBuf buf = new WarpShuffleBuf();
                buf.Total = end - start;
                buf.Vals = new List<Value>(end - start);
                for (int i = 0; i < end - start; ++i) buf.Vals.Add(new Value());
                buf.WriteArrived = 0;
                buf.ReadDone = 0;
                buf.ReadReady = false;
                warpBufs_.Add(buf);
            }
        }

        private void RunSequential()
        {
            Dim3 bd = cfg_.BlockDim;
            for (ulong tz = 0; tz < bd.Z; ++tz)
                for (ulong ty = 0; ty < bd.Y; ++ty)
                    for (ulong tx = 0; tx < bd.X; ++tx)
                    {
                        ThreadContext tc = MakeThreadContext(tx, ty, tz, bd);
                        Interpreter interp = new Interpreter(mem_, symtab_, intr_, host_, out_);
                        interp.SetThreadContext(tc);
                        interp.SetBlockScheduler(this);
                        interp.SetKernelLauncher(launcher_);
                        if (IsDebugLane(tc)) interp.SetDebugHook(cfg_.DebugHook);
                        interp.Call(cfg_.Kernel, cfg_.Args);  // throws on fault → aborts block
                    }
        }

        private void OnThreadFinished()
        {
            lock (barrierMutex_)
            {
                ++finished_;
                // A barrier is in progress (some threads parked) but this thread exited
                // without arriving — the parked threads can never be released → divergent.
                if (arrived_ > 0 && arrived_ < total_ && arrived_ + finished_ == total_)
                {
                    divergent_ = true;
                    Monitor.PulseAll(barrierMutex_);
                }
            }
        }

        /// <summary>Called by the __syncthreads intrinsic from a thread.</summary>
        public void Barrier(ThreadContext self)
        {
            lock (barrierMutex_)
            {
                int gen = barrierGen_;
                ++arrived_;
                if (arrived_ == total_)
                {
                    ++barrierGen_;
                    arrived_ = 0;
                    Monitor.PulseAll(barrierMutex_);
                }
                else if (arrived_ + finished_ == total_)
                {
                    divergent_ = true;
                    Monitor.PulseAll(barrierMutex_);
                }
                else
                {
                    while (barrierGen_ == gen && !divergent_) Monitor.Wait(barrierMutex_);
                }
                if (divergent_)
                    throw new RuntimeError(RuntimeError.Kind.DivergentBarrier,
                        "__syncthreads() reached by only some threads in the block " +
                        "(divergent barrier → would hang on GPU)",
                        cfg_.Kernel.Span);
            }
        }

        private void RunThreaded()
        {
            Dim3 bd = cfg_.BlockDim;
            int total = (int)(bd.X * bd.Y * bd.Z);
            lock (barrierMutex_)
            {
                total_ = total;
                arrived_ = 0;
                finished_ = 0;
            }
            List<Thread> threads = new List<Thread>(total);
            for (ulong tz = 0; tz < bd.Z; ++tz)
                for (ulong ty = 0; ty < bd.Y; ++ty)
                    for (ulong tx = 0; tx < bd.X; ++tx)
                    {
                        ulong ltx = tx, lty = ty, ltz = tz;
                        Thread t = new Thread(delegate ()
                        {
                            ThreadContext tc = MakeThreadContext(ltx, lty, ltz, bd);
                            Interpreter interp = new Interpreter(mem_, symtab_, intr_, host_, out_);
                            interp.SetThreadContext(tc);
                            interp.SetBlockScheduler(this);
                            interp.SetKernelLauncher(launcher_);
                            if (IsDebugLane(tc)) interp.SetDebugHook(cfg_.DebugHook);
                            try
                            {
                                interp.Call(cfg_.Kernel, cfg_.Args);
                            }
                            catch (RuntimeError e)
                            {
                                RecordError(e);
                            }
                            catch (Exception)
                            {
                                RecordError(new RuntimeError(RuntimeError.Kind.Other,
                                    "unknown error in kernel thread", cfg_.Kernel.Span));
                            }
                            OnThreadFinished();
                        });
                        t.IsBackground = true;
                        threads.Add(t);
                    }
            for (int i = 0; i < threads.Count; ++i) threads[i].Start();
            for (int i = 0; i < threads.Count; ++i) threads[i].Join();
        }

        /// <summary>
        /// True when <paramref name="tc"/> is the one thread the debugger is watching. Both the
        /// block and the thread coordinate must match: a grid of 64 blocks would otherwise stop
        /// 64 times at the same breakpoint, once per block, for what the user set once.
        /// </summary>
        private bool IsDebugLane(ThreadContext tc)
        {
            if (cfg_.DebugHook == null || !cfg_.DebugLaneOnly) return false;
            return tc.ThreadIdx.X == cfg_.DebugThread.X
                && tc.ThreadIdx.Y == cfg_.DebugThread.Y
                && tc.ThreadIdx.Z == cfg_.DebugThread.Z;
        }

        private ThreadContext MakeThreadContext(ulong tx, ulong ty, ulong tz, Dim3 bd)
        {
            ThreadContext tc = new ThreadContext();
            tc.ThreadIdx = new Dim3(tx, ty, tz);
            tc.BlockIdx = cfg_.BlockIdx;
            tc.BlockDim = cfg_.BlockDim;
            tc.GridDim = cfg_.GridDim;
            tc.LinearTid = (uint)((tz * bd.Y + ty) * bd.X + tx);
            tc.SharedVars = sharedVars_;
            return tc;
        }

        /// <summary>
        /// Called by __shfl_*_sync intrinsics: atomically exchanges lane values within
        /// a warp and returns the value from the computed source lane.
        /// </summary>
        public Value WarpShuffle(ThreadContext self, ShuffleOp op, Value var, int param, int width, uint mask)
        {
            int laneId = (int)(self.LinearTid % 32);
            int warpId = (int)(self.LinearTid / 32);
            if (warpId >= warpBufs_.Count) return var;
            WarpShuffleBuf buf = warpBufs_[warpId];

            int effectiveWidth = Math.Min(width, buf.Total);
            if (effectiveWidth <= 0) return var;
            int groupBase = (laneId / effectiveWidth) * effectiveWidth;
            int laneInGroup = laneId % effectiveWidth;

            int srcInGroup;
            switch (op)
            {
                case ShuffleOp.Idx:
                    srcInGroup = param % effectiveWidth;
                    break;
                case ShuffleOp.Xor:
                    srcInGroup = laneInGroup ^ param;
                    if (srcInGroup < 0 || srcInGroup >= effectiveWidth) srcInGroup = laneInGroup;
                    break;
                case ShuffleOp.Up:
                    {
                        int src = laneInGroup - param;
                        srcInGroup = (src < 0) ? laneInGroup : src;
                        break;
                    }
                case ShuffleOp.Down:
                    {
                        int src = laneInGroup + param;
                        srcInGroup = (src >= effectiveWidth) ? laneInGroup : src;
                        break;
                    }
                default:
                    srcInGroup = laneInGroup;
                    break;
            }
            int srcLane = groupBase + srcInGroup;

            // ---- Write phase: each lane stores its value; last one releases all. ----
            lock (buf.Mu)
            {
                if (laneId < buf.Vals.Count) buf.Vals[laneId] = var;
                ++buf.WriteArrived;
                if (buf.WriteArrived == buf.Total)
                {
                    buf.WriteArrived = 0;
                    buf.ReadReady = true;
                    Monitor.PulseAll(buf.Mu);
                }
                else
                {
                    while (!buf.ReadReady) Monitor.Wait(buf.Mu);
                }
            }

            // ---- Read phase: read the source lane's value from the stable vals[]. ----
            Value result = var;
            if (srcLane >= 0 && srcLane < buf.Vals.Count)
                result = buf.Vals[srcLane];

            // ---- Done-reading phase: last reader resets state for the next shuffle. ----
            lock (buf.Mu)
            {
                ++buf.ReadDone;
                if (buf.ReadDone == buf.Total)
                {
                    buf.ReadDone = 0;
                    buf.ReadReady = false;
                    Monitor.PulseAll(buf.Mu);
                }
                else
                {
                    while (buf.ReadReady) Monitor.Wait(buf.Mu);
                }
            }

            return result;
        }

        /// <summary>
        /// True if the kernel body uses __syncthreads, __syncwarp, or any warp shuffle
        /// / vote intrinsic — i.e., anything that requires the threaded execution path.
        /// </summary>
        public static bool KernelUsesBarrier(FunctionDecl kernel, SymbolTable symtab)
        {
            if (kernel == null || !kernel.HasBody) return false;
            bool[] found = { false };
            AstWalk.ForEachExpr(kernel.Body, delegate (Expr e)
            {
                if (found[0]) return;
                CallExpr call = e as CallExpr;
                if (call != null)
                {
                    NameExpr n = call.Callee as NameExpr;
                    if (n != null)
                    {
                        for (int i = 0; i < ThreadedNames.Length; ++i)
                            if (n.Id == ThreadedNames[i]) { found[0] = true; break; }
                    }
                }
            });
            return found[0];
        }

        private static readonly string[] ThreadedNames =
        {
            "__syncthreads", "__syncwarp",
            "__shfl_sync", "__shfl_xor_sync", "__shfl_up_sync", "__shfl_down_sync",
            "__ballot_sync", "__any_sync", "__all_sync",
        };

        /// <summary>
        /// Allocate a kernel's __shared__ variables (one buffer each) and return the
        /// name → pointer map. Shared by the block scheduler and the single-thread
        /// debug launch path.
        /// </summary>
        public static Dictionary<string, Pointer> AllocateKernelSharedVars(
            FunctionDecl kernel, Memory mem, IntrinsicRegistry intr, HostRuntime host,
            SymbolTable symtab, IOutputSink outSink, Dim3 blockIdx, Dim3 blockDim,
            Dim3 gridDim, long dynamicShared)
        {
            Dictionary<string, Pointer> sharedVars = new Dictionary<string, Pointer>();
            // Eval array sizes with a throwaway interpreter bound to thread (0,0,0).
            ThreadContext probe = new ThreadContext();
            probe.BlockIdx = blockIdx;
            probe.BlockDim = blockDim;
            probe.GridDim = gridDim;
            Interpreter interp = new Interpreter(mem, symtab, intr, host, outSink);
            interp.SetThreadContext(probe);

            ScanShared(kernel != null ? kernel.Body : null, sharedVars, interp, mem, dynamicShared);
            return sharedVars;
        }

        private static void ScanShared(Stmt s, Dictionary<string, Pointer> sharedVars,
            Interpreter interp, Memory mem, long dynamicShared)
        {
            if (s == null) return;
            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                for (int i = 0; i < c.Body.Count; ++i) ScanShared(c.Body[i], sharedVars, interp, mem, dynamicShared);
                return;
            }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null && v.Storage == StorageClass.Shared)
                {
                    TypeInfo pi = TypeMapping.PointeeInfo(v.Type);
                    long bytes;
                    if (v.ArraySize != null)
                    {
                        long n = interp.Eval(v.ArraySize).ToInt64();
                        bytes = n * pi.Size;
                    }
                    else
                    {
                        bytes = dynamicShared != 0 ? dynamicShared : pi.Size;
                    }
                    Pointer p = mem.Allocate(bytes, MemSpace.Shared, pi.Elem, pi.Size, "shared:" + v.Name);
                    sharedVars[v.Name] = p;
                }
                return;
            }
            IfStmt ifs = s as IfStmt;
            if (ifs != null) { ScanShared(ifs.Then, sharedVars, interp, mem, dynamicShared); ScanShared(ifs.Else, sharedVars, interp, mem, dynamicShared); return; }
            ForStmt f = s as ForStmt;
            if (f != null) { ScanShared(f.Body, sharedVars, interp, mem, dynamicShared); return; }
            WhileStmt w = s as WhileStmt;
            if (w != null) { ScanShared(w.Body, sharedVars, interp, mem, dynamicShared); return; }
        }
    }
}
