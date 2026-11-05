namespace HelixPythonNamespace.CudaEmulator
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Implemented by the facade to run a KernelLaunchExpr. Lets the interpreter stay
    /// free of the block scheduler. Port of helix::cuda_emulator::KernelLauncher.
    /// </summary>
    public interface IKernelLauncher
    {
        Value Launch(KernelLaunchExpr launch, Interpreter caller);
    }

    /// <summary>
    /// Called before each statement when attached (debug phase). Null for normal
    /// runs. Port of helix::cuda_emulator::DebugHook.
    /// </summary>
    public interface IDebugHook
    {
        void OnStatement(Stmt stmt, int callDepth, Interpreter interp);
    }

    /// <summary>
    /// Tree-walking interpreter over the CUDA AST. One instance per executing thread
    /// (host code, or one thread per CUDA thread); all instances share Memory /
    /// SymbolTable / intrinsics / output. Port of the C++
    /// helix::cuda_emulator::Interpreter (cuda_emulator/interpreter.{h,cpp}).
    /// </summary>
    public sealed class Interpreter
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly Memory mem_;
        private readonly SymbolTable symtab_;
        private readonly IntrinsicRegistry intrinsics_;
        private readonly HostRuntime host_;
        private readonly IOutputSink out_;
        private IKernelLauncher launcher_;
        private IDebugHook hook_;
        private ThreadContext thread_;
        private BlockScheduler block_;
        private readonly List<Frame> frames_ = new List<Frame>();

        public Interpreter(Memory mem, SymbolTable symtab, IntrinsicRegistry intrinsics, HostRuntime host, IOutputSink outSink)
        {
            mem_ = mem;
            symtab_ = symtab;
            intrinsics_ = intrinsics;
            host_ = host;
            out_ = outSink;
        }

        public void SetKernelLauncher(IKernelLauncher l) { launcher_ = l; }
        public void SetDebugHook(IDebugHook h) { hook_ = h; }
        public void SetThreadContext(ThreadContext t) { thread_ = t; }
        public void SetBlockScheduler(BlockScheduler b) { block_ = b; }

        public Memory Mem { get { return mem_; } }
        public IOutputSink Out { get { return out_; } }
        public SymbolTable Symtab { get { return symtab_; } }
        public List<Frame> Frames { get { return frames_; } }
        public ThreadContext Thread { get { return thread_; } }

        private Frame Back() { return frames_[frames_.Count - 1]; }

        // Wall-clock nanoseconds since process start (CUDA event timing).
        private static long NowNs()
        {
            return (long)(Clock.Elapsed.Ticks * 100L);  // 1 tick = 100 ns
        }

        public Value Eval(Expr e)
        {
            if (e == null) return new Value();

            LiteralExpr lit = e as LiteralExpr;
            if (lit != null) return EvalLiteral(lit);
            ParenExpr paren = e as ParenExpr;
            if (paren != null) return Eval(paren.Inner);

            NameExpr name = e as NameExpr;
            if (name != null)
            {
                if (frames_.Count != 0)
                {
                    Cell c = Back().Current().Find(name.Id);
                    if (c != null)
                        return c.AddressTaken ? mem_.Load(c.Backing, c.BackingElem, e.Span) : c.Value;
                }
                if (thread_ != null)
                {
                    string n = name.Id;
                    if (n == "threadIdx") return new Value(thread_.ThreadIdx);
                    if (n == "blockIdx") return new Value(thread_.BlockIdx);
                    if (n == "blockDim") return new Value(thread_.BlockDim);
                    if (n == "gridDim") return new Value(thread_.GridDim);
                    if (n == "warpSize") return new Value((long)thread_.WarpSize);
                }
                if (symtab_ != null)
                {
                    Symbol sym = symtab_.Root.LookupLocal(name.Id);
                    if (sym != null && sym.Kind == SymbolKind.EnumConstant)
                        return new Value(0L);
                }
                // Top-level global (__device__/__constant__/file-scope) — one shared
                // arena location, read its current value.
                Pointer gp;
                if (mem_ != null && mem_.Globals.TryGetValue(name.Id, out gp))
                    return mem_.Load(gp, gp.ElemType, e.Span);
                // std::cout/std::cerr/std::endl — resolvable from host code (not
                // gated behind thread_ != null), matches with or without
                // `using namespace std;` since name.Id is already the unqualified
                // last segment (std::cout and bare cout both give Id == "cout").
                if (name.Id == "cout") return new Value(StreamKind.Cout);
                if (name.Id == "cerr") return new Value(StreamKind.Cerr);
                if (name.Id == "endl") return new Value(ManipKind.EndL);
                throw Unsupported("reference to '" + name.Id + "'", e.Span);
            }

            UnaryExpr un = e as UnaryExpr;
            if (un != null)
            {
                switch (un.Op)
                {
                    case UnaryOpKind.Deref:
                    case UnaryOpKind.PreInc:
                    case UnaryOpKind.PreDec:
                    case UnaryOpKind.PostInc:
                    case UnaryOpKind.PostDec: break;
                    case UnaryOpKind.AddrOf:
                        {
                            LValue lv = EvalLValue(un.Operand);
                            if (lv.LValueKind == LValue.Kind.Memory) return new Value(lv.Mem);
                            if (lv.LValueKind == LValue.Kind.Cell) return new Value(AddressOfCell(lv.Cell, e.Span));
                            throw Unsupported("address-of a non-memory value", e.Span);
                        }
                    case UnaryOpKind.Plus: return Eval(un.Operand);
                    case UnaryOpKind.Minus:
                        {
                            Value v = Eval(un.Operand);
                            return v.IsDouble ? new Value(-v.AsDouble) : new Value(-v.ToInt64());
                        }
                    case UnaryOpKind.Not: return new Value(!Eval(un.Operand).ToBool());
                    case UnaryOpKind.BitNot: return new Value(~Eval(un.Operand).ToInt64());
                }
                // Deref + inc/dec are lvalue-based.
                if (un.Op == UnaryOpKind.Deref) return EvalLValue(e).Get(mem_, e.Span);
                LValue lval = EvalLValue(un.Operand);
                Value cur = lval.Get(mem_, e.Span);
                bool inc = (un.Op == UnaryOpKind.PreInc || un.Op == UnaryOpKind.PostInc);
                Value next = cur.IsDouble ? new Value(cur.AsDouble + (inc ? 1 : -1))
                                          : new Value(cur.ToInt64() + (inc ? 1 : -1));
                lval.Set(mem_, next, e.Span);
                return (un.Op == UnaryOpKind.PreInc || un.Op == UnaryOpKind.PreDec) ? next : cur;
            }

            BinaryExpr bin = e as BinaryExpr;
            if (bin != null)
            {
                if (bin.IsAssignment()) return EvalAssignment(bin);
                if (bin.Op == BinOpKind.Comma) { Eval(bin.Lhs); return Eval(bin.Rhs); }
                if (bin.Op == BinOpKind.LogAnd)
                    return new Value(Eval(bin.Lhs).ToBool() && Eval(bin.Rhs).ToBool());
                if (bin.Op == BinOpKind.LogOr)
                    return new Value(Eval(bin.Lhs).ToBool() || Eval(bin.Rhs).ToBool());
                if (bin.Op == BinOpKind.Shl)
                {
                    // `stream << rhs` (std::cout/std::cerr chaining) vs. plain integer
                    // left-shift both use BinOpKind.Shl — disambiguate on the already-
                    // evaluated LHS so `x << 2` is untouched.
                    Value shlLhs = Eval(bin.Lhs);
                    if (shlLhs.IsStream) return WriteStream(shlLhs, bin.Rhs, e.Span);
                    return ApplyBinary(bin.Op, shlLhs, Eval(bin.Rhs), e.Span);
                }
                return ApplyBinary(bin.Op, Eval(bin.Lhs), Eval(bin.Rhs), e.Span);
            }

            ConditionalExpr cond = e as ConditionalExpr;
            if (cond != null)
                return Eval(cond.Cond).ToBool() ? Eval(cond.Then) : Eval(cond.Else);

            CallExpr call = e as CallExpr;
            if (call != null) return EvalCall(call);

            KernelLaunchExpr launch = e as KernelLaunchExpr;
            if (launch != null)
            {
                if (launcher_ != null) return launcher_.Launch(launch, this);
                throw Unsupported("kernel launch (no launcher attached)", e.Span);
            }

            IndexExpr idx = e as IndexExpr;
            if (idx != null) return EvalLValue(e).Get(mem_, e.Span);

            MemberExpr memberE = e as MemberExpr;
            if (memberE != null)
            {
                Value obj = Eval(memberE.Object);
                int lane = MemberLane(memberE.Member);
                if (obj.IsDim3 && lane >= 0 && lane <= 2)
                {
                    Dim3 d = obj.AsDim3;
                    return new Value((long)(lane == 0 ? d.X : lane == 1 ? d.Y : d.Z));
                }
                if (obj.IsVec && lane >= 0 && lane < obj.AsVec.Count)
                    return new Value(obj.AsVec.Comps[lane]);
                throw Unsupported("member access '." + memberE.Member + "'", e.Span);
            }

            CastExpr cast = e as CastExpr;
            if (cast != null)
            {
                Value v = Eval(cast.Operand);
                CxxType t = cast.Type;
                if (t.IsPointer())
                {
                    if (v.IsPointer)
                    {
                        Pointer p = v.AsPointer;
                        TypeInfo pi = TypeMapping.PointeeInfo(t);
                        p.ElemSize = pi.Size; p.ElemType = pi.Elem;
                        return new Value(p);
                    }
                    return v;
                }
                if (t.Base == "float" || t.Base == "double") return new Value(v.ToDouble());
                if (t.Base == "bool") return new Value(v.ToBool());
                return new Value(v.ToInt64());
            }

            // C++ `new T` / `new T[n]` — a host-side heap allocation. The result is
            // a typed pointer into the shared arena (Host space), mirroring how the
            // C malloc family is handled in EvalCall. `delete`/`delete[]` frees it.
            NewExpr nw = e as NewExpr;
            if (nw != null)
            {
                TypeInfo ti = TypeMapping.MapType(nw.Type);
                long count = nw.ArraySize != null ? Eval(nw.ArraySize).ToInt64() : 1;
                if (count < 0) count = 0;
                Pointer p = mem_.Allocate(count * ti.Size, MemSpace.Host, ti.Elem, ti.Size, "new");
                return new Value(p);
            }

            DeleteExpr del = e as DeleteExpr;
            if (del != null)
            {
                Value dv = Eval(del.Operand);
                if (dv.IsPointer) mem_.Free(dv.AsPointer, del.Span);
                return new Value(0L);
            }

            InitListExpr init = e as InitListExpr;
            if (init != null)
            {
                IReadOnlyList<Expr> els = init.Elements;
                if (els.Count >= 1 && els.Count <= 3)
                {
                    Dim3 d = new Dim3(1, 1, 1);
                    for (int i = 0; i < els.Count; ++i)
                    {
                        ulong slot = (ulong)Eval(els[i]).ToInt64();
                        if (i == 0) d.X = slot; else if (i == 1) d.Y = slot; else d.Z = slot;
                    }
                    return new Value(d);
                }
                throw Unsupported("initializer list", e.Span);
            }

            throw Unsupported("expression", e.Span);
        }

        /// <summary>
        /// Allocate and initialize the translation unit's top-level variables into the
        /// shared arena (mem_.Globals). Covers __device__/__constant__ globals and
        /// file-scope const/var. Called once at program start (before the entry point)
        /// so every kernel interpreter — which shares this Memory — sees the same
        /// storage. __shared__ is skipped (bound per-block at launch). Best-effort:
        /// a global whose initializer cannot be evaluated statically is skipped and
        /// only surfaces if actually referenced.
        /// </summary>
        public void InitGlobals(IReadOnlyList<Decl> decls)
        {
            if (decls == null || mem_ == null) return;
            for (int i = 0; i < decls.Count; ++i)
            {
                VarDecl v = decls[i] as VarDecl;
                if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                if (v.Storage == StorageClass.Shared) continue;
                if (mem_.Globals.ContainsKey(v.Name)) continue;
                MemSpace space = (v.Storage == StorageClass.Device || v.Storage == StorageClass.Constant)
                    ? MemSpace.Device : MemSpace.Host;
                try
                {
                    if (v.Type != null && v.Type.IsArray)
                    {
                        long n = v.ArraySize != null ? Eval(v.ArraySize).ToInt64() : 0;
                        TypeInfo pi = TypeMapping.PointeeInfo(v.Type);
                        mem_.Globals[v.Name] = mem_.Allocate(n * pi.Size, space, pi.Elem, pi.Size, "global:" + v.Name);
                    }
                    else
                    {
                        TypeInfo ti = TypeMapping.MapType(v.Type);
                        Pointer p = mem_.Allocate(ti.Size, space, ti.Elem, ti.Size, "global:" + v.Name);
                        Value init = v.Initializer != null ? Eval(v.Initializer) : DefaultForType(v.Type);
                        mem_.Store(p, ti.Elem, init, v.Span);
                        mem_.Globals[v.Name] = p;
                    }
                }
                catch (RuntimeError)
                {
                    // Uninitializable global (e.g. references a not-yet-supported form) —
                    // skip; referencing it later throws the usual unresolved-name error.
                }
            }
        }

        /// <summary>
        /// Promote a local variable whose address is taken (&x) to an arena slot,
        /// seeded with its current value, and remember the backing on the Cell so
        /// subsequent reads/writes of the variable route through the same memory
        /// (keeping it coherent with writes made through the pointer, e.g.
        /// cudaMemcpy(&x, d, n)). Idempotent — a second &x returns the same slot.
        /// </summary>
        private Pointer AddressOfCell(Cell cell, SourceSpan span)
        {
            if (!cell.AddressTaken)
            {
                TypeInfo ti = TypeMapping.MapType(cell.DeclType);
                Pointer p = mem_.Allocate(ti.Size, MemSpace.Host, ti.Elem, ti.Size, "addr-local");
                mem_.Store(p, ti.Elem, cell.Value, span);
                cell.Backing = p;
                cell.BackingElem = ti.Elem;
                cell.AddressTaken = true;
            }
            return cell.Backing;
        }

        public LValue EvalLValue(Expr e)
        {
            ParenExpr paren = e as ParenExpr;
            if (paren != null) return EvalLValue(paren.Inner);
            CastExpr cast = e as CastExpr;
            if (cast != null) return EvalLValue(cast.Operand);

            NameExpr name = e as NameExpr;
            if (name != null)
            {
                if (frames_.Count != 0)
                {
                    Cell c = Back().Current().Find(name.Id);
                    if (c != null)
                        return new LValue(LValue.Kind.Cell, c, new Pointer(), ElemType.Byte, 0);
                }
                // Top-level global — an arena-backed lvalue, so &global yields a real
                // pointer (needed by atomicAdd(&counter, 1)) and stores go to the one
                // shared location.
                Pointer gp;
                if (mem_ != null && mem_.Globals.TryGetValue(name.Id, out gp))
                    return new LValue(LValue.Kind.Memory, null, gp, gp.ElemType, 0);
                throw Unsupported("assignment to '" + name.Id + "'", e.Span);
            }

            UnaryExpr un = e as UnaryExpr;
            if (un != null)
            {
                if (un.Op == UnaryOpKind.Deref)
                {
                    Value pv = Eval(un.Operand);
                    if (!pv.IsPointer) throw Unsupported("dereference of non-pointer", e.Span);
                    Pointer p = pv.AsPointer;
                    return new LValue(LValue.Kind.Memory, null, p, p.ElemType, 0);
                }
            }

            IndexExpr idx = e as IndexExpr;
            if (idx != null)
            {
                Value baseV = Eval(idx.Base);
                if (!baseV.IsPointer) throw Unsupported("subscript of non-pointer", e.Span);
                long i = Eval(idx.Index).ToInt64();
                Pointer p = baseV.AsPointer;
                p.Offset += i * (long)p.ElemSize;
                return new LValue(LValue.Kind.Memory, null, p, p.ElemType, 0);
            }

            MemberExpr memberE = e as MemberExpr;
            if (memberE != null)
            {
                LValue baseLv = EvalLValue(memberE.Object);
                int lane = MemberLane(memberE.Member);
                if (baseLv.LValueKind == LValue.Kind.Cell && lane >= 0)
                {
                    if (baseLv.Cell.Value.IsDim3 && lane <= 2)
                        return new LValue(LValue.Kind.Dim3Lane, baseLv.Cell, new Pointer(), ElemType.Byte, lane);
                    if (baseLv.Cell.Value.IsVec)
                        return new LValue(LValue.Kind.VecLane, baseLv.Cell, new Pointer(), ElemType.Byte, lane);
                }
                throw Unsupported("member assignment '." + memberE.Member + "'", e.Span);
            }

            throw Unsupported("lvalue", e.Span);
        }

        private Value EvalAssignment(BinaryExpr bin)
        {
            LValue lv = EvalLValue(bin.Lhs);
            if (bin.Op == BinOpKind.Assign)
            {
                Value v = Eval(bin.Rhs);
                lv.Set(mem_, v, bin.Span);
                return v;
            }
            Value cur = lv.Get(mem_, bin.Span);
            Value rhs = Eval(bin.Rhs);
            Value res = ApplyBinary(CompoundBase(bin.Op), cur, rhs, bin.Span);
            lv.Set(mem_, res, bin.Span);
            return res;
        }

        private Value ApplyBinary(BinOpKind op, Value l, Value r, SourceSpan span)
        {
            // Pointer arithmetic.
            if (l.IsPointer && (op == BinOpKind.Add || op == BinOpKind.Sub) && (r.IsInt || r.IsDouble))
            {
                Pointer p = l.AsPointer;
                long k = r.ToInt64() * (long)p.ElemSize;
                p.Offset += (op == BinOpKind.Add) ? k : -k;
                return new Value(p);
            }
            if (l.IsPointer && r.IsPointer && op == BinOpKind.Sub)
            {
                long diff = l.AsPointer.Offset - r.AsPointer.Offset;
                int es = (int)(l.AsPointer.ElemSize != 0 ? l.AsPointer.ElemSize : 1);
                return new Value(diff / es);
            }
            // Comparisons.
            switch (op)
            {
                case BinOpKind.Lt:
                case BinOpKind.Gt:
                case BinOpKind.Le:
                case BinOpKind.Ge:
                case BinOpKind.Eq:
                case BinOpKind.Ne:
                    {
                        if (l.IsPointer && r.IsPointer)
                        {
                            long a2 = l.AsPointer.Offset, b2 = r.AsPointer.Offset;
                            bool sameBuf = l.AsPointer.Buffer == r.AsPointer.Buffer;
                            switch (op)
                            {
                                case BinOpKind.Eq: return new Value(sameBuf && a2 == b2);
                                case BinOpKind.Ne: return new Value(!(sameBuf && a2 == b2));
                                case BinOpKind.Lt: return new Value(a2 < b2);
                                case BinOpKind.Gt: return new Value(a2 > b2);
                                case BinOpKind.Le: return new Value(a2 <= b2);
                                case BinOpKind.Ge: return new Value(a2 >= b2);
                            }
                        }
                        double a = l.ToDouble(), b = r.ToDouble();
                        switch (op)
                        {
                            case BinOpKind.Lt: return new Value(a < b);
                            case BinOpKind.Gt: return new Value(a > b);
                            case BinOpKind.Le: return new Value(a <= b);
                            case BinOpKind.Ge: return new Value(a >= b);
                            case BinOpKind.Eq: return new Value(a == b);
                            case BinOpKind.Ne: return new Value(a != b);
                        }
                        break;
                    }
            }
            // Floating arithmetic if either operand is double.
            if ((l.IsDouble || r.IsDouble) &&
                (op == BinOpKind.Add || op == BinOpKind.Sub || op == BinOpKind.Mul || op == BinOpKind.Div))
            {
                double a = l.ToDouble(), b = r.ToDouble();
                switch (op)
                {
                    case BinOpKind.Add: return new Value(a + b);
                    case BinOpKind.Sub: return new Value(a - b);
                    case BinOpKind.Mul: return new Value(a * b);
                    case BinOpKind.Div: return new Value(a / b);
                }
            }
            // Integer arithmetic / bitwise.
            long la = l.ToInt64(), lb = r.ToInt64();
            switch (op)
            {
                case BinOpKind.Add: return new Value(la + lb);
                case BinOpKind.Sub: return new Value(la - lb);
                case BinOpKind.Mul: return new Value(la * lb);
                case BinOpKind.Div:
                    if (lb == 0) throw new RuntimeError(RuntimeError.Kind.DivideByZero, "integer divide by zero", span);
                    return new Value(la / lb);
                case BinOpKind.Mod:
                    if (lb == 0) throw new RuntimeError(RuntimeError.Kind.DivideByZero, "integer modulo by zero", span);
                    return new Value(la % lb);
                case BinOpKind.Shl: return new Value(la << (int)lb);
                case BinOpKind.Shr: return new Value(la >> (int)lb);
                case BinOpKind.BitAnd: return new Value(la & lb);
                case BinOpKind.BitOr: return new Value(la | lb);
                case BinOpKind.BitXor: return new Value(la ^ lb);
            }
            throw Unsupported("binary operator", span);
        }

        // std::cin / operator>> (extraction) is intentionally NOT implemented here —
        // out of scope for this fix. A follow-up would mirror this design: a new
        // StreamKind.Cin, a BinOpKind.Shr special case next to the Shl one in Eval,
        // and a new input-source abstraction (e.g. on IntrinsicCtx/HostRuntime).
        private Value WriteStream(Value stream, Expr rhsExpr, SourceSpan span)
        {
            Value rv = Eval(rhsExpr);
            string text;
            if (rv.IsManipulator && rv.AsManipulator == ManipKind.EndL)
            {
                text = "\n";
            }
            else if (rv.IsBool)
            {
                // Real std::cout defaults to numeric bool formatting ("1"/"0") unless
                // std::boolalpha is set — intentional, not a bug.
                text = rv.AsBool ? "1" : "0";
            }
            else if (rv.IsPointer)
            {
                Pointer p = rv.AsPointer;
                // Same element type ReadCString/printf's %s expect for a C string;
                // any other pointee prints as an address (reuses Value.ToString()).
                text = (p.ElemType == ElemType.Int8) ? mem_.ReadCString(p, span) : rv.ToString();
            }
            else if (rv.IsInt)
            {
                // Note: char literals/values arrive here as plain ints (Value has no
                // separate "char" kind to key off of without new type-tracking
                // infrastructure), so `cout << 'A'` prints "65", not "A".
                text = rv.AsInt.ToString(CultureInfo.InvariantCulture);
            }
            else if (rv.IsDouble)
            {
                text = CFormat.G(rv.AsDouble);
            }
            else
            {
                text = rv.ToString();
            }
            out_.Write(text);
            return stream;
        }

        private void DoCudaMalloc(CallExpr call)
        {
            IReadOnlyList<Expr> args = call.Args;
            if (args.Count < 2) throw new RuntimeError(RuntimeError.Kind.BadLaunch, "cudaMalloc expects (void**, size)", call.Span);
            // Unwrap (T**)&p / &p to the pointer variable's lvalue.
            Expr a0 = args[0];
            while (true)
            {
                ParenExpr p = a0 as ParenExpr;
                if (p != null) { a0 = p.Inner; continue; }
                CastExpr c = a0 as CastExpr;
                if (c != null) { a0 = c.Operand; continue; }
                break;
            }
            UnaryExpr addr = a0 as UnaryExpr;
            if (addr == null || addr.Op != UnaryOpKind.AddrOf)
                throw new RuntimeError(RuntimeError.Kind.BadLaunch, "cudaMalloc first argument must be &pointer", call.Span);
            LValue target = EvalLValue(addr.Operand);
            ElemType elem = ElemType.Byte;
            uint elemSize = 1;
            if (target.LValueKind == LValue.Kind.Cell && target.Cell.DeclType != null)
            {
                TypeInfo pi = TypeMapping.PointeeInfo(target.Cell.DeclType);
                elem = pi.Elem; elemSize = pi.Size;
            }
            long bytes = Eval(args[1]).ToInt64();
            host_.CudaMalloc(target, bytes, elem, elemSize, call.Span);
        }

        private Value EvalCall(CallExpr call)
        {
            NameExpr callee = call.Callee as NameExpr;
            if (callee == null) throw Unsupported("indirect/method call", call.Span);
            string name = callee.Id;

            if (host_ != null && HostRuntime.IsHostCall(name))
            {
                IReadOnlyList<Expr> args = call.Args;
                if (name == "cudaMalloc" || name == "cudaMallocManaged" || name == "cudaMallocHost")
                {
                    DoCudaMalloc(call);
                    return new Value(0L);
                }
                if (name == "cudaMemcpy" || name == "cudaMemcpyAsync")
                {
                    Value dst = Eval(args[0]), src = Eval(args[1]);
                    long n = Eval(args[2]).ToInt64();
                    int kind = args.Count > 3 ? (int)Eval(args[3]).ToInt64() : 0;
                    return host_.CudaMemcpy(dst, src, n, kind, call.Span);
                }
                if (name == "cudaFree" || name == "cudaFreeHost")
                    return host_.CudaFree(Eval(args[0]), call.Span);
                if (name == "cudaMemset" || name == "cudaMemsetAsync")
                {
                    Value p = Eval(args[0]);
                    int v = (int)Eval(args[1]).ToInt64();
                    long n = Eval(args[2]).ToInt64();
                    return host_.CudaMemset(p, v, n, call.Span);
                }
                if (name == "cudaEventCreate" && args.Count != 0)
                {
                    UnaryExpr addr = args[0] as UnaryExpr;
                    if (addr != null && addr.Op == UnaryOpKind.AddrOf)
                    {
                        LValue target = EvalLValue(addr.Operand);
                        host_.CudaMalloc(target, 8, ElemType.Int64, 8, call.Span);
                    }
                    return new Value(0L);
                }
                if (name == "cudaEventRecord" && args.Count != 0)
                {
                    Value evPtr = Eval(args[0]);
                    if (evPtr.IsPointer)
                    {
                        Pointer p = evPtr.AsPointer;
                        p.ElemType = ElemType.Int64;
                        mem_.Store(p, ElemType.Int64, new Value(NowNs()), call.Span);
                    }
                    return new Value(0L);
                }
                if (name == "cudaEventElapsedTime" && args.Count >= 3)
                {
                    UnaryExpr addr = args[0] as UnaryExpr;
                    if (addr != null && addr.Op == UnaryOpKind.AddrOf)
                    {
                        LValue msTarget = EvalLValue(addr.Operand);
                        Value startPtr = Eval(args[1]);
                        Value stopPtr = Eval(args[2]);
                        long t0 = 0, t1 = 0;
                        if (startPtr.IsPointer)
                        {
                            Pointer p = startPtr.AsPointer; p.ElemType = ElemType.Int64;
                            t0 = mem_.Load(p, ElemType.Int64, call.Span).ToInt64();
                        }
                        if (stopPtr.IsPointer)
                        {
                            Pointer p = stopPtr.AsPointer; p.ElemType = ElemType.Int64;
                            t1 = mem_.Load(p, ElemType.Int64, call.Span).ToInt64();
                        }
                        double ms = (double)(t1 - t0) / 1.0e6;
                        msTarget.Set(mem_, new Value(ms), call.Span);
                    }
                    return new Value(0L);
                }
                if (name == "cudaEventDestroy" && args.Count != 0)
                {
                    Value evPtr = Eval(args[0]);
                    if (evPtr.IsPointer) host_.CudaFree(evPtr, call.Span);
                    return new Value(0L);
                }
                return host_.CudaNoOp();
            }

            // C standard-library host memory management. Real programs allocate host
            // mirrors of device buffers with malloc/calloc; model them as ordinary
            // Host-space allocations in the shared arena. The returned byte pointer is
            // retyped by the surrounding `(T*)` cast (handled in Eval). free() releases.
            if (name == "malloc" || name == "calloc" || name == "realloc" || name == "free")
            {
                IReadOnlyList<Expr> a = call.Args;
                if (name == "free")
                {
                    if (a.Count >= 1)
                    {
                        Value pv = Eval(a[0]);
                        if (pv.IsPointer) mem_.Free(pv.AsPointer, call.Span);
                    }
                    return new Value(0L);
                }
                if (name == "realloc")
                {
                    // Minimal grow: fresh block of the requested size (no copy). Adequate
                    // for the common allocate-once / grow-once idiom in these fixtures.
                    long rbytes = a.Count >= 2 ? Eval(a[1]).ToInt64() : 0;
                    return new Value(mem_.Allocate(rbytes, MemSpace.Host, ElemType.Byte, 1, "realloc"));
                }
                long bytes;
                if (name == "calloc")
                {
                    long cnt = a.Count >= 1 ? Eval(a[0]).ToInt64() : 0;
                    long sz = a.Count >= 2 ? Eval(a[1]).ToInt64() : 0;
                    bytes = cnt * sz;
                }
                else
                {
                    bytes = a.Count >= 1 ? Eval(a[0]).ToInt64() : 0;
                }
                Pointer hp = mem_.Allocate(bytes, MemSpace.Host, ElemType.Byte, 1, name);
                if (name == "calloc" && bytes > 0) mem_.Memset(hp, 0, bytes, call.Span);
                return new Value(hp);
            }

            // User-defined function (device/host helper or kernel called as function).
            if (symtab_ != null)
            {
                List<Symbol> syms = symtab_.FindFunctions(name);
                for (int i = 0; i < syms.Count; ++i)
                {
                    Symbol sym = syms[i];
                    if (sym.FuncDecl != null && sym.FuncDecl.HasBody)
                    {
                        List<Value> args = new List<Value>();
                        for (int a = 0; a < call.Args.Count; ++a) args.Add(Eval(call.Args[a]));
                        return Call(sym.FuncDecl, args);
                    }
                }
            }

            // Builtin intrinsic (math / atomics / sync / printf).
            if (intrinsics_ != null)
            {
                IntrinsicFn fn = intrinsics_.Find(name);
                if (fn != null)
                {
                    List<Value> args = new List<Value>();
                    for (int a = 0; a < call.Args.Count; ++a) args.Add(Eval(call.Args[a]));
                    IntrinsicCtx ctx = new IntrinsicCtx();
                    ctx.Mem = mem_;
                    ctx.Out = out_;
                    ctx.Thread = thread_;
                    ctx.Block = block_;
                    ctx.CallSpan = call.Span;
                    return fn(ctx, args);
                }
            }

            throw Unsupported("call to '" + name + "'", call.Span);
        }

        public ExecSignal Exec(Stmt s)
        {
            if (s == null) return ExecSignal.Normal();
            if (frames_.Count != 0) Back().CurrentStmt = s;
            if (hook_ != null) hook_.OnStatement(s, frames_.Count, this);

            CompoundStmt c = s as CompoundStmt;
            if (c != null)
            {
                Frame f = Back();
                f.Push();
                for (int i = 0; i < c.Body.Count; ++i)
                {
                    ExecSignal sig = Exec(c.Body[i]);
                    if (!sig.IsNormal) { f.Pop(); return sig; }
                }
                f.Pop();
                return ExecSignal.Normal();
            }
            DeclStmt d = s as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null)
                {
                    // __shared__ variable: bind to the per-block shared buffer.
                    if (v.Storage == StorageClass.Shared)
                    {
                        Pointer sp = new Pointer();
                        if (thread_ != null && thread_.SharedVars != null)
                        {
                            Pointer found;
                            if (thread_.SharedVars.TryGetValue(v.Name, out found)) sp = found;
                        }
                        Back().Current().Declare(v.Name, new Value(sp), v.Type);
                        return ExecSignal.Normal();
                    }
                    // Local array: allocate a per-thread arena buffer sized by the dim.
                    if (v.Type != null && v.Type.IsArray)
                    {
                        long n = v.ArraySize != null ? Eval(v.ArraySize).ToInt64() : 0;
                        TypeInfo pi = TypeMapping.PointeeInfo(v.Type);
                        Pointer p = mem_.Allocate(n * pi.Size, MemSpace.Host, pi.Elem, pi.Size, "local:" + v.Name);
                        Back().Current().Declare(v.Name, new Value(p), v.Type);
                        return ExecSignal.Normal();
                    }
                    Value init = v.Initializer != null ? Eval(v.Initializer) : DefaultForType(v.Type);
                    Back().Current().Declare(v.Name, init, v.Type);
                }
                return ExecSignal.Normal();
            }
            ExprStmt es = s as ExprStmt;
            if (es != null) { Eval(es.Expr); return ExecSignal.Normal(); }
            IfStmt ifs = s as IfStmt;
            if (ifs != null)
            {
                if (Eval(ifs.Cond).ToBool()) return Exec(ifs.Then);
                if (ifs.Else != null) return Exec(ifs.Else);
                return ExecSignal.Normal();
            }
            ForStmt f2 = s as ForStmt;
            if (f2 != null)
            {
                Frame fr = Back();
                fr.Push();
                if (f2.Init != null) Exec(f2.Init);
                while (f2.Cond == null || Eval(f2.Cond).ToBool())
                {
                    ExecSignal sig = Exec(f2.Body);
                    if (sig.IsReturn) { fr.Pop(); return sig; }
                    if (sig.SignalKind == ExecSignal.Kind.Break) break;
                    if (f2.Inc != null) Eval(f2.Inc);
                }
                fr.Pop();
                return ExecSignal.Normal();
            }
            WhileStmt w = s as WhileStmt;
            if (w != null)
            {
                while (Eval(w.Cond).ToBool())
                {
                    ExecSignal sig = Exec(w.Body);
                    if (sig.IsReturn) return sig;
                    if (sig.SignalKind == ExecSignal.Kind.Break) break;
                }
                return ExecSignal.Normal();
            }
            DoStmt dw = s as DoStmt;
            if (dw != null)
            {
                do
                {
                    ExecSignal sig = Exec(dw.Body);
                    if (sig.IsReturn) return sig;
                    if (sig.SignalKind == ExecSignal.Kind.Break) break;
                } while (Eval(dw.Cond).ToBool());
                return ExecSignal.Normal();
            }
            if (s is SwitchStmt) throw Unsupported("switch statement", s.Span);
            ReturnStmt r = s as ReturnStmt;
            if (r != null) return ExecSignal.Return(r.Value != null ? Eval(r.Value) : new Value());
            if (s is BreakStmt) return ExecSignal.Break();
            if (s is ContinueStmt) return ExecSignal.Continue();
            if (s is EmptyStmt) return ExecSignal.Normal();

            return ExecSignal.Normal();
        }

        public Value Call(FunctionDecl fn, List<Value> args)
        {
            if (frames_.Count > 2048)
                throw new RuntimeError(RuntimeError.Kind.Other, "call stack too deep (recursion?)", fn.Span);
            if (!fn.HasBody)
                throw Unsupported("call to function without a body: '" + fn.Name + "'", fn.Span);

            Frame frame = new Frame();
            frame.Func = fn;
            frame.Thread = thread_;
            frame.CallDepth = frames_.Count;
            frames_.Add(frame);
            frame.Push();

            IReadOnlyList<Parameter> parameters = fn.Params;
            for (int i = 0; i < parameters.Count; ++i)
            {
                Value v = (i < args.Count) ? args[i]
                    : (parameters[i].DefaultValue != null ? Eval(parameters[i].DefaultValue)
                                                          : DefaultForType(parameters[i].Type));
                frame.Current().Declare(parameters[i].Name, v, parameters[i].Type);
            }

            Value result = new Value();
            try
            {
                ExecSignal sig = Exec(fn.Body);
                if (sig.IsReturn) result = sig.ReturnValue;
            }
            catch
            {
                frames_.RemoveAt(frames_.Count - 1);
                throw;
            }
            frames_.RemoveAt(frames_.Count - 1);
            return result;
        }

        private Value EvalLiteral(LiteralExpr lit)
        {
            switch (lit.Kind)
            {
                case LiteralKind.Int: return new Value(ParseIntLit(lit.Text));
                case LiteralKind.Float: return new Value(ParseFloatLit(lit.Text));
                case LiteralKind.Bool: return new Value(lit.Text == "true");
                case LiteralKind.Char: return new Value(ParseCharLit(lit.Text));
                case LiteralKind.String:
                    {
                        // Materialize a NUL-terminated char buffer in the arena; return ptr.
                        string raw = lit.Text;
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        for (int i = 1; i + 1 < raw.Length; ++i)  // strip quotes
                        {
                            if (raw[i] == '\\' && i + 1 < raw.Length)
                            {
                                char nch = raw[++i];
                                sb.Append(nch == 'n' ? '\n' : nch == 't' ? '\t' : nch);
                            }
                            else sb.Append(raw[i]);
                        }
                        string sstr = sb.ToString();
                        Pointer p = mem_.Allocate(sstr.Length + 1, MemSpace.Host, ElemType.Int8, 1, "strlit");
                        for (int i = 0; i < sstr.Length; ++i)
                        {
                            Pointer ep = p; ep.Offset += i;
                            mem_.Store(ep, ElemType.Int8, new Value((long)sstr[i]), lit.Span);
                        }
                        return new Value(p);
                    }
                case LiteralKind.Nullptr: return new Value(Pointer.Null());
            }
            return new Value();
        }

        private Value DefaultForType(CxxType type)
        {
            if (type == null) return new Value(0L);
            if (type.IsPointer()) return new Value(Pointer.Null());
            if (type.Base == "dim3" || type.Base == "uint3") return new Value(new Dim3(1, 1, 1));
            if (type.Base == "float" || type.Base == "double") return new Value(0.0);
            if (type.Base == "bool") return new Value(false);
            return new Value(0L);
        }

        private static RuntimeError Unsupported(string what, SourceSpan span)
        {
            return new RuntimeError(RuntimeError.Kind.Unsupported, "unsupported construct: " + what, span);
        }

        private static int MemberLane(string m)
        {
            if (m == "x") return 0;
            if (m == "y") return 1;
            if (m == "z") return 2;
            if (m == "w") return 3;
            return -1;
        }

        private static BinOpKind CompoundBase(BinOpKind op)
        {
            switch (op)
            {
                case BinOpKind.AddAssign: return BinOpKind.Add;
                case BinOpKind.SubAssign: return BinOpKind.Sub;
                case BinOpKind.MulAssign: return BinOpKind.Mul;
                case BinOpKind.DivAssign: return BinOpKind.Div;
                case BinOpKind.ModAssign: return BinOpKind.Mod;
                case BinOpKind.AndAssign: return BinOpKind.BitAnd;
                case BinOpKind.OrAssign: return BinOpKind.BitOr;
                case BinOpKind.XorAssign: return BinOpKind.BitXor;
                case BinOpKind.ShlAssign: return BinOpKind.Shl;
                case BinOpKind.ShrAssign: return BinOpKind.Shr;
                default: return op;
            }
        }

        private static long ParseIntLit(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            string t = text;
            while (t.Length > 0)
            {
                char b = t[t.Length - 1];
                if (b == 'u' || b == 'U' || b == 'l' || b == 'L') t = t.Substring(0, t.Length - 1);
                else break;
            }
            if (t.IndexOf('\'') >= 0) t = t.Replace("'", string.Empty);
            if (t.Length == 0) return 0;
            try
            {
                int i = 0;
                long sign = 1;
                if (t[0] == '+' || t[0] == '-') { if (t[0] == '-') sign = -1; i = 1; }
                if (i >= t.Length) return 0;
                int radix = 10;
                if (t[i] == '0' && i + 1 < t.Length && (t[i + 1] == 'x' || t[i + 1] == 'X')) { radix = 16; i += 2; }
                else if (t[i] == '0' && i + 1 < t.Length) { radix = 8; i += 1; }
                long value = 0;
                for (; i < t.Length; ++i)
                {
                    int dv = DigitVal(t[i]);
                    if (dv < 0 || dv >= radix) return 0;
                    value = value * radix + dv;
                }
                return sign * value;
            }
            catch (OverflowException)
            {
                return 0;
            }
        }

        private static int DigitVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            return -1;
        }

        private static double ParseFloatLit(string text)
        {
            string t = text ?? string.Empty;
            while (t.Length > 0)
            {
                char b = t[t.Length - 1];
                if (b == 'f' || b == 'F' || b == 'l' || b == 'L') t = t.Substring(0, t.Length - 1);
                else break;
            }
            double d;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return 0.0;
        }

        private static long ParseCharLit(string text)
        {
            // text includes quotes: 'a' or '\n'
            if (text.Length >= 3 && text[1] == '\\')
            {
                switch (text[2])
                {
                    case 'n': return '\n';
                    case 't': return '\t';
                    case 'r': return '\r';
                    case '0': return '\0';
                    case '\\': return '\\';
                    case '\'': return '\'';
                    default: return text[2];
                }
            }
            return text.Length >= 2 ? (long)(byte)text[1] : 0;
        }
    }
}
