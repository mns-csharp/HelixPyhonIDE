namespace HelixPythonNamespace.CudaEmulator
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Seeds device-side intrinsics: math (expf/__expf/sqrtf/...), atomics, bit ops,
    /// warp shuffles/votes, vector constructors, printf, and sync stubs. Port of the
    /// C++ helix::cuda_emulator::SeedDeviceIntrinsics + helpers
    /// (cuda_emulator/intrinsic_registry.cpp).
    /// </summary>
    public static class DeviceIntrinsics
    {
        public static void SeedDeviceIntrinsics(IntrinsicRegistry reg)
        {
            RegisterMath(reg);
            RegisterAtomics(reg);
            RegisterBitOps(reg);
            RegisterVectorOps(reg);
            RegisterWarpOps(reg);
            reg.Register("printf", delegate (IntrinsicCtx c, List<Value> a) { return DoPrintf(c, a); });
            // __syncthreads is a real block barrier when running under a BlockScheduler;
            // a no-op in single-thread / host context.
            reg.Register("__syncthreads", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (c.Block != null && c.Thread != null) c.Block.Barrier(c.Thread);
                return new Value();
            });
            reg.Register("__syncwarp", Noop);
            reg.Register("__threadfence", Noop);
            reg.Register("__threadfence_block", Noop);
            reg.Register("min", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count < 2) return new Value(0L);
                if (a[0].IsDouble || a[1].IsDouble) return new Value(Math.Min(a[0].ToDouble(), a[1].ToDouble()));
                return new Value(a[0].ToInt64() < a[1].ToInt64() ? a[0].ToInt64() : a[1].ToInt64());
            });
            reg.Register("max", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count < 2) return new Value(0L);
                if (a[0].IsDouble || a[1].IsDouble) return new Value(Math.Max(a[0].ToDouble(), a[1].ToDouble()));
                return new Value(a[0].ToInt64() > a[1].ToInt64() ? a[0].ToInt64() : a[1].ToInt64());
            });
        }

        private static Value Noop(IntrinsicCtx c, List<Value> a) { return new Value(); }

        // --- math ---------------------------------------------------------------

        private static void RegisterMath(IntrinsicRegistry reg)
        {
            RegUnary(reg, new[] { "expf", "__expf", "exp" }, Math.Exp);
            RegUnary(reg, new[] { "exp2f", "exp2" }, delegate (double x) { return Math.Pow(2.0, x); });
            RegUnary(reg, new[] { "logf", "__logf", "log" }, Math.Log);
            RegUnary(reg, new[] { "log2f", "log2" }, delegate (double x) { return Math.Log(x, 2.0); });
            RegUnary(reg, new[] { "log10f", "log10" }, Math.Log10);
            RegUnary(reg, new[] { "sqrtf", "sqrt" }, Math.Sqrt);
            RegUnary(reg, new[] { "rsqrtf" }, delegate (double x) { return 1.0 / Math.Sqrt(x); });
            RegUnary(reg, new[] { "fabsf", "fabs", "abs" }, Math.Abs);
            RegUnary(reg, new[] { "floorf", "floor" }, Math.Floor);
            RegUnary(reg, new[] { "ceilf", "ceil" }, Math.Ceiling);
            RegUnary(reg, new[] { "roundf", "round" }, delegate (double x) { return Math.Round(x, MidpointRounding.AwayFromZero); });
            RegUnary(reg, new[] { "truncf", "trunc" }, Math.Truncate);
            RegUnary(reg, new[] { "sinf", "sin" }, Math.Sin);
            RegUnary(reg, new[] { "cosf", "cos" }, Math.Cos);
            RegUnary(reg, new[] { "tanf", "tan" }, Math.Tan);
            RegUnary(reg, new[] { "asinf", "asin" }, Math.Asin);
            RegUnary(reg, new[] { "acosf", "acos" }, Math.Acos);
            RegUnary(reg, new[] { "atanf", "atan" }, Math.Atan);
            RegUnary(reg, new[] { "sinhf", "sinh" }, Math.Sinh);
            RegUnary(reg, new[] { "coshf", "cosh" }, Math.Cosh);
            RegUnary(reg, new[] { "tanhf", "tanh" }, Math.Tanh);
            RegBinary(reg, new[] { "powf", "pow", "__powf" }, Math.Pow);
            RegBinary(reg, new[] { "fminf", "fmin" }, Math.Min);
            RegBinary(reg, new[] { "fmaxf", "fmax" }, Math.Max);
            RegBinary(reg, new[] { "atan2f", "atan2" }, Math.Atan2);
            RegBinary(reg, new[] { "hypotf", "hypot" }, delegate (double a, double b) { return Math.Sqrt(a * a + b * b); });
            RegBinary(reg, new[] { "fmodf", "fmod" }, delegate (double a, double b) { return a % b; });
            RegTernary(reg, new[] { "fmaf", "__fmaf_rn", "fma" }, delegate (double a, double b, double c) { return a * b + c; });
            foreach (string n in new[] { "ldexpf", "ldexp" })
            {
                reg.Register(n, delegate (IntrinsicCtx c, List<Value> a)
                {
                    double x = a.Count > 0 ? a[0].ToDouble() : 0.0;
                    int e = a.Count > 1 ? (int)a[1].ToInt64() : 0;
                    return new Value(x * Math.Pow(2.0, e));
                });
            }
        }

        private static void RegUnary(IntrinsicRegistry reg, string[] names, Func<double, double> fn)
        {
            foreach (string name in names)
                reg.Register(name, delegate (IntrinsicCtx c, List<Value> a)
                {
                    return new Value(a.Count == 0 ? 0.0 : fn(a[0].ToDouble()));
                });
        }

        private static void RegBinary(IntrinsicRegistry reg, string[] names, Func<double, double, double> fn)
        {
            foreach (string name in names)
                reg.Register(name, delegate (IntrinsicCtx c, List<Value> a)
                {
                    double x = a.Count > 0 ? a[0].ToDouble() : 0.0;
                    double y = a.Count > 1 ? a[1].ToDouble() : 0.0;
                    return new Value(fn(x, y));
                });
        }

        private static void RegTernary(IntrinsicRegistry reg, string[] names, Func<double, double, double, double> fn)
        {
            foreach (string name in names)
                reg.Register(name, delegate (IntrinsicCtx c, List<Value> a)
                {
                    double x = a.Count > 0 ? a[0].ToDouble() : 0.0;
                    double y = a.Count > 1 ? a[1].ToDouble() : 0.0;
                    double z = a.Count > 2 ? a[2].ToDouble() : 0.0;
                    return new Value(fn(x, y, z));
                });
        }

        // --- atomics ------------------------------------------------------------

        private enum AtomicOp { Add, Sub, Exch, Min, Max, And, Or, Xor }

        private static Value DoAtomic(AtomicOp op, IntrinsicCtx ctx, List<Value> args)
        {
            if (args.Count < 2 || !args[0].IsPointer)
                throw new RuntimeError(RuntimeError.Kind.BadLaunch, "atomic expects (pointer, value)", ctx.CallSpan);
            Pointer p = args[0].AsPointer;
            lock (ctx.Mem.BufferMutex(p.Buffer))
            {
                Value old = ctx.Mem.Load(p, p.ElemType, ctx.CallSpan);
                bool isFloat = (p.ElemType == ElemType.Float32 || p.ElemType == ElemType.Float64);
                Value next;
                if (isFloat)
                {
                    double a = old.ToDouble(), b = args[1].ToDouble();
                    switch (op)
                    {
                        case AtomicOp.Add: next = new Value(a + b); break;
                        case AtomicOp.Sub: next = new Value(a - b); break;
                        case AtomicOp.Exch: next = new Value(b); break;
                        case AtomicOp.Min: next = new Value(Math.Min(a, b)); break;
                        case AtomicOp.Max: next = new Value(Math.Max(a, b)); break;
                        default: next = old; break;
                    }
                }
                else
                {
                    long a = old.ToInt64(), b = args[1].ToInt64();
                    switch (op)
                    {
                        case AtomicOp.Add: next = new Value(a + b); break;
                        case AtomicOp.Sub: next = new Value(a - b); break;
                        case AtomicOp.Exch: next = new Value(b); break;
                        case AtomicOp.Min: next = new Value(a < b ? a : b); break;
                        case AtomicOp.Max: next = new Value(a > b ? a : b); break;
                        case AtomicOp.And: next = new Value(a & b); break;
                        case AtomicOp.Or: next = new Value(a | b); break;
                        case AtomicOp.Xor: next = new Value(a ^ b); break;
                        default: next = old; break;
                    }
                }
                ctx.Mem.Store(p, p.ElemType, next, ctx.CallSpan);
                return old;
            }
        }

        private static void RegisterAtomics(IntrinsicRegistry reg)
        {
            reg.Register("atomicAdd", delegate (IntrinsicCtx c, List<Value> a) { return DoAtomic(AtomicOp.Add, c, a); });
            reg.Register("atomicSub", delegate (IntrinsicCtx c, List<Value> a) { return DoAtomic(AtomicOp.Sub, c, a); });
            reg.Register("atomicExch", delegate (IntrinsicCtx c, List<Value> a) { return DoAtomic(AtomicOp.Exch, c, a); });
            reg.Register("atomicMin", delegate (IntrinsicCtx c, List<Value> a) { return DoAtomic(AtomicOp.Min, c, a); });
            reg.Register("atomicMax", delegate (IntrinsicCtx c, List<Value> a) { return DoAtomic(AtomicOp.Max, c, a); });
            reg.Register("atomicAnd", delegate (IntrinsicCtx c, List<Value> a) { return DoAtomic(AtomicOp.And, c, a); });
            reg.Register("atomicOr", delegate (IntrinsicCtx c, List<Value> a) { return DoAtomic(AtomicOp.Or, c, a); });
            reg.Register("atomicXor", delegate (IntrinsicCtx c, List<Value> a) { return DoAtomic(AtomicOp.Xor, c, a); });
            reg.Register("atomicCAS", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count < 3 || !a[0].IsPointer)
                    throw new RuntimeError(RuntimeError.Kind.BadLaunch, "atomicCAS expects (ptr, compare, val)", c.CallSpan);
                Pointer p = a[0].AsPointer;
                lock (c.Mem.BufferMutex(p.Buffer))
                {
                    Value old = c.Mem.Load(p, p.ElemType, c.CallSpan);
                    if (old.ToInt64() == a[1].ToInt64()) c.Mem.Store(p, p.ElemType, a[2], c.CallSpan);
                    return old;
                }
            });
        }

        // --- bit manipulation ---------------------------------------------------

        private static void RegisterBitOps(IntrinsicRegistry reg)
        {
            reg.Register("__popc", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count == 0) return new Value(0L);
                return new Value((long)Popcnt32((uint)a[0].ToInt64()));
            });
            reg.Register("__popcll", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count == 0) return new Value(0L);
                return new Value((long)Popcnt64((ulong)a[0].ToInt64()));
            });
            reg.Register("__clz", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count == 0) return new Value(32L);
                return new Value((long)Clz32((uint)a[0].ToInt64()));
            });
            reg.Register("__clzll", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count == 0) return new Value(64L);
                return new Value((long)Clz64((ulong)a[0].ToInt64()));
            });
            reg.Register("__ffs", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count == 0) return new Value(0L);
                uint v = (uint)a[0].ToInt64();
                return new Value((long)(v == 0 ? 0 : Ctz32(v) + 1));
            });
            reg.Register("__ffsll", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count == 0) return new Value(0L);
                ulong v = (ulong)a[0].ToInt64();
                if (v == 0) return new Value(0L);
                uint lo = (uint)v;
                if (lo != 0) return new Value((long)(Ctz32(lo) + 1));
                return new Value((long)(32 + Ctz32((uint)(v >> 32)) + 1));
            });
            reg.Register("__brev", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count == 0) return new Value(0L);
                return new Value((long)Rev32((uint)a[0].ToInt64()));
            });
            reg.Register("__brevll", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count == 0) return new Value(0L);
                return new Value((long)Rev64((ulong)a[0].ToInt64()));
            });
            reg.Register("__mulhi", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count < 2) return new Value(0L);
                long hi = (a[0].ToInt64() * a[1].ToInt64()) >> 32;
                return new Value(hi);
            });
            reg.Register("__byte_perm", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count < 3) return new Value(0L);
                uint x = (uint)a[0].ToInt64();
                uint y = (uint)a[1].ToInt64();
                uint sel = (uint)a[2].ToInt64();
                ulong src = ((ulong)y << 32) | x;
                uint result = 0;
                for (int i = 0; i < 4; ++i)
                {
                    int idx = (int)((sel >> (i * 4)) & 0xF);
                    result |= (uint)(((src >> (idx * 8)) & 0xFF) << (i * 8));
                }
                return new Value((long)result);
            });
        }

        // --- warp shuffle & vote ------------------------------------------------

        private static void RegisterWarpOps(IntrinsicRegistry reg)
        {
            reg.Register("__shfl_sync", delegate (IntrinsicCtx c, List<Value> a) { return Shfl(c, a, ShuffleOp.Idx); });
            reg.Register("__shfl_xor_sync", delegate (IntrinsicCtx c, List<Value> a) { return Shfl(c, a, ShuffleOp.Xor); });
            reg.Register("__shfl_up_sync", delegate (IntrinsicCtx c, List<Value> a) { return Shfl(c, a, ShuffleOp.Up); });
            reg.Register("__shfl_down_sync", delegate (IntrinsicCtx c, List<Value> a) { return Shfl(c, a, ShuffleOp.Down); });

            reg.Register("__activemask", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (c.Thread == null) return new Value((long)0xFFFFFFFFu);
                return new Value((long)0xFFFFFFFFu);
            });

            reg.Register("__ballot_sync", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (c.Block == null || c.Thread == null)
                {
                    long pred0 = a.Count > 1 ? a[1].ToInt64() : 0;
                    return new Value(pred0 != 0 ? 1L : 0L);
                }
                uint mask = a.Count > 0 ? (uint)a[0].ToInt64() : 0xFFFFFFFFu;
                long pred = a.Count > 1 ? a[1].ToInt64() : 0;
                int laneId = (int)(c.Thread.LinearTid % 32);
                long bit = (pred != 0) ? (1L << laneId) : 0L;
                Value result = new Value(bit);
                foreach (int offset in new[] { 16, 8, 4, 2, 1 })
                {
                    Value other = c.Block.WarpShuffle(c.Thread, ShuffleOp.Xor, result, offset, 32, mask);
                    result = new Value(result.ToInt64() | other.ToInt64());
                }
                return result;
            });

            reg.Register("__any_sync", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (c.Block == null || c.Thread == null)
                    return new Value(a.Count > 1 && a[1].ToInt64() != 0 ? 1L : 0L);
                uint mask = a.Count > 0 ? (uint)a[0].ToInt64() : 0xFFFFFFFFu;
                long pred = a.Count > 1 ? (a[1].ToInt64() != 0 ? 1 : 0) : 0;
                Value v = new Value(pred);
                foreach (int off in new[] { 16, 8, 4, 2, 1 })
                {
                    Value other = c.Block.WarpShuffle(c.Thread, ShuffleOp.Xor, v, off, 32, mask);
                    v = new Value((v.ToInt64() | other.ToInt64()) != 0 ? 1L : 0L);
                }
                return v;
            });

            reg.Register("__all_sync", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (c.Block == null || c.Thread == null)
                    return new Value(a.Count > 1 && a[1].ToInt64() != 0 ? 1L : 0L);
                uint mask = a.Count > 0 ? (uint)a[0].ToInt64() : 0xFFFFFFFFu;
                long pred = a.Count > 1 ? (a[1].ToInt64() != 0 ? 1 : 0) : 0;
                Value v = new Value(pred);
                foreach (int off in new[] { 16, 8, 4, 2, 1 })
                {
                    Value other = c.Block.WarpShuffle(c.Thread, ShuffleOp.Xor, v, off, 32, mask);
                    v = new Value((v.ToInt64() & other.ToInt64()) != 0 ? 1L : 0L);
                }
                return v;
            });
        }

        private static Value Shfl(IntrinsicCtx c, List<Value> a, ShuffleOp op)
        {
            if (c.Block == null || c.Thread == null)
                return a.Count > 1 ? a[1] : new Value(0L);
            uint mask = a.Count > 0 ? (uint)a[0].ToInt64() : 0xFFFFFFFFu;
            Value var = a.Count > 1 ? a[1] : new Value(0L);
            int param = a.Count > 2 ? (int)a[2].ToInt64() : 0;
            int width = a.Count > 3 ? (int)a[3].ToInt64() : 32;
            return c.Block.WarpShuffle(c.Thread, op, var, param, width, mask);
        }

        // --- vector constructors + __ldg ---------------------------------------

        private static void RegisterVectorOps(IntrinsicRegistry reg)
        {
            MakeVec(reg, "make_float2", ElemType.Float32, 2);
            MakeVec(reg, "make_float3", ElemType.Float32, 3);
            MakeVec(reg, "make_float4", ElemType.Float32, 4);
            MakeVec(reg, "make_double2", ElemType.Float64, 2);
            MakeVec(reg, "make_int2", ElemType.Int32, 2);
            MakeVec(reg, "make_int3", ElemType.Int32, 3);
            MakeVec(reg, "make_int4", ElemType.Int32, 4);
            MakeVec(reg, "make_uint2", ElemType.UInt32, 2);
            MakeVec(reg, "make_uint3", ElemType.UInt32, 3);
            MakeVec(reg, "make_uint4", ElemType.UInt32, 4);
            MakeVec(reg, "make_char4", ElemType.Int8, 4);
            MakeVec(reg, "make_uchar4", ElemType.UInt8, 4);
            MakeVec(reg, "make_short2", ElemType.Int16, 2);
            MakeVec(reg, "make_ushort2", ElemType.UInt16, 2);

            reg.Register("__ldg", delegate (IntrinsicCtx c, List<Value> a)
            {
                if (a.Count == 0 || !a[0].IsPointer) return new Value();
                Pointer p = a[0].AsPointer;
                return c.Mem.Load(p, p.ElemType, c.CallSpan);
            });
        }

        private static void MakeVec(IntrinsicRegistry reg, string name, ElemType elem, int count)
        {
            reg.Register(name, delegate (IntrinsicCtx c, List<Value> a)
            {
                VecValue vec = new VecValue();
                vec.Elem = elem;
                vec.Count = count;
                for (int i = 0; i < count && i < 4; ++i)
                    vec.Comps[i] = (i < a.Count) ? a[i].ToDouble() : 0.0;
                return new Value(vec);
            });
        }

        // --- printf -------------------------------------------------------------

        private static Value DoPrintf(IntrinsicCtx ctx, List<Value> args)
        {
            if (args.Count == 0 || !args[0].IsPointer) return new Value(0L);
            string fmt = ctx.Mem.ReadCString(args[0].AsPointer, ctx.CallSpan);
            StringBuilder outSb = new StringBuilder();
            int argi = 1;
            for (int i = 0; i < fmt.Length; ++i)
            {
                if (fmt[i] != '%') { outSb.Append(fmt[i]); continue; }
                if (i + 1 < fmt.Length && fmt[i + 1] == '%') { outSb.Append('%'); ++i; continue; }
                // Collect the spec up to the conversion char (dropping length modifiers).
                StringBuilder spec = new StringBuilder("%");
                int j = i + 1;
                while (j < fmt.Length && "-+ #0123456789.*lhLzjt".IndexOf(fmt[j]) >= 0)
                {
                    char fj = fmt[j];
                    if (fj != 'l' && fj != 'h' && fj != 'L' && fj != 'z' && fj != 'j' && fj != 't')
                        spec.Append(fj);
                    ++j;
                }
                if (j >= fmt.Length) { outSb.Append(spec); break; }
                char conv = fmt[j];
                spec.Append(conv);
                Value arg = (argi < args.Count) ? args[argi++] : new Value(0L);
                outSb.Append(FormatSpec(spec.ToString(), conv, arg, ctx.Mem, ctx.CallSpan));
                i = j;
            }
            ctx.Out.Write(outSb.ToString());
            return new Value((long)outSb.Length);
        }

        // Format one printf-style spec (already including the leading '%') against arg.
        private static string FormatSpec(string spec, char conv, Value arg, Memory mem, SourceSpan span)
        {
            // Parse: %[flags][width][.precision]conv  (length modifiers already stripped).
            bool left = false, plus = false, spaceFlag = false, zero = false, alt = false;
            int k = 1;
            while (k < spec.Length)
            {
                char ch = spec[k];
                if (ch == '-') { left = true; ++k; }
                else if (ch == '+') { plus = true; ++k; }
                else if (ch == ' ') { spaceFlag = true; ++k; }
                else if (ch == '0') { zero = true; ++k; }
                else if (ch == '#') { alt = true; ++k; }
                else break;
            }
            int width = 0;
            while (k < spec.Length && spec[k] >= '0' && spec[k] <= '9') { width = width * 10 + (spec[k] - '0'); ++k; }
            int precision = -1;
            if (k < spec.Length && spec[k] == '.')
            {
                ++k;
                precision = 0;
                while (k < spec.Length && spec[k] >= '0' && spec[k] <= '9') { precision = precision * 10 + (spec[k] - '0'); ++k; }
            }

            string body;
            switch (conv)
            {
                case 'd':
                case 'i':
                    {
                        long v = arg.ToInt64();
                        string digits = Math.Abs(v).ToString(CultureInfo.InvariantCulture);
                        if (precision >= 0 && digits.Length < precision) digits = digits.PadLeft(precision, '0');
                        string sign = v < 0 ? "-" : plus ? "+" : spaceFlag ? " " : "";
                        body = sign + digits;
                        return Pad(body, width, left, zero && precision < 0, sign.Length);
                    }
                case 'u':
                    {
                        ulong v = (ulong)arg.ToInt64();
                        body = v.ToString(CultureInfo.InvariantCulture);
                        if (precision >= 0 && body.Length < precision) body = body.PadLeft(precision, '0');
                        return Pad(body, width, left, zero && precision < 0, 0);
                    }
                case 'x':
                case 'X':
                    {
                        ulong v = (uint)arg.ToInt64();
                        body = v.ToString(conv == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
                        if (precision >= 0 && body.Length < precision) body = body.PadLeft(precision, '0');
                        if (alt && v != 0) body = (conv == 'x' ? "0x" : "0X") + body;
                        return Pad(body, width, left, zero && precision < 0, 0);
                    }
                case 'o':
                    {
                        ulong v = (uint)arg.ToInt64();
                        body = Convert.ToString((long)v, 8);
                        if (precision >= 0 && body.Length < precision) body = body.PadLeft(precision, '0');
                        return Pad(body, width, left, zero && precision < 0, 0);
                    }
                case 'f':
                case 'F':
                    {
                        int prec = precision < 0 ? 6 : precision;
                        double d = arg.ToDouble();
                        string mag = Math.Abs(d).ToString("F" + prec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                        string sign = d < 0 ? "-" : plus ? "+" : spaceFlag ? " " : "";
                        body = sign + mag;
                        return Pad(body, width, left, zero, sign.Length);
                    }
                case 'e':
                case 'E':
                    {
                        int prec = precision < 0 ? 6 : precision;
                        double d = arg.ToDouble();
                        string s = d.ToString((conv == 'e' ? "e" : "E") + prec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                        // .NET uses 3-digit exponent (e+000); C uses at least 2. Normalize to 2+.
                        s = NormalizeExponent(s, conv);
                        return Pad(s, width, left, zero, s.StartsWith("-") ? 1 : 0);
                    }
                case 'g':
                case 'G':
                    {
                        body = CFormat.G(arg.ToDouble());
                        if (conv == 'G') body = body.ToUpperInvariant();
                        return Pad(body, width, left, zero, body.StartsWith("-") ? 1 : 0);
                    }
                case 'c':
                    {
                        body = ((char)(int)arg.ToInt64()).ToString();
                        return Pad(body, width, left, false, 0);
                    }
                case 'p':
                    {
                        if (arg.IsPointer)
                            return "0x" + arg.AsPointer.Buffer.ToString(CultureInfo.InvariantCulture) + "+" +
                                   arg.AsPointer.Offset.ToString(CultureInfo.InvariantCulture);
                        return "(nil)";
                    }
                case 's':
                    {
                        string str = arg.IsPointer ? mem.ReadCString(arg.AsPointer, span) : "";
                        if (precision >= 0 && str.Length > precision) str = str.Substring(0, precision);
                        return Pad(str, width, left, false, 0);
                    }
                default:
                    return spec;
            }
        }

        private static string Pad(string body, int width, bool left, bool zero, int signLen)
        {
            if (body.Length >= width) return body;
            int padCount = width - body.Length;
            if (left) return body + new string(' ', padCount);
            if (zero)
            {
                // Zero-pad after the sign.
                string sign = body.Substring(0, signLen);
                string rest = body.Substring(signLen);
                return sign + new string('0', padCount) + rest;
            }
            return new string(' ', padCount) + body;
        }

        private static string NormalizeExponent(string s, char conv)
        {
            char e = conv == 'e' ? 'e' : 'E';
            int idx = s.IndexOf(e);
            if (idx < 0) return s;
            string mantissa = s.Substring(0, idx);
            string exp = s.Substring(idx + 1);
            char expSign = '+';
            if (exp.Length > 0 && (exp[0] == '+' || exp[0] == '-')) { expSign = exp[0]; exp = exp.Substring(1); }
            exp = exp.TrimStart('0');
            if (exp.Length < 2) exp = exp.PadLeft(2, '0');
            return mantissa + e + expSign + exp;
        }

        // --- portable bit helpers (no BitOperations on net472) ------------------

        private static int Popcnt32(uint v)
        {
            v = v - ((v >> 1) & 0x55555555u);
            v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
            v = (v + (v >> 4)) & 0x0F0F0F0Fu;
            return (int)((v * 0x01010101u) >> 24);
        }

        private static int Popcnt64(ulong v)
        {
            return Popcnt32((uint)v) + Popcnt32((uint)(v >> 32));
        }

        private static int Clz32(uint v)
        {
            if (v == 0) return 32;
            int n = 0;
            while ((v & 0x80000000u) == 0) { ++n; v <<= 1; }
            return n;
        }

        private static int Clz64(ulong v)
        {
            if (v == 0) return 64;
            uint hi = (uint)(v >> 32);
            if (hi != 0) return Clz32(hi);
            return 32 + Clz32((uint)v);
        }

        private static int Ctz32(uint v)
        {
            if (v == 0) return 32;
            int n = 0;
            while ((v & 1u) == 0) { ++n; v >>= 1; }
            return n;
        }

        private static uint Rev32(uint v)
        {
            v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
            v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
            v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
            v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
            return (v >> 16) | (v << 16);
        }

        private static ulong Rev64(ulong v)
        {
            v = ((v >> 1) & 0x5555555555555555UL) | ((v & 0x5555555555555555UL) << 1);
            v = ((v >> 2) & 0x3333333333333333UL) | ((v & 0x3333333333333333UL) << 2);
            v = ((v >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((v & 0x0F0F0F0F0F0F0F0FUL) << 4);
            v = ((v >> 8) & 0x00FF00FF00FF00FFUL) | ((v & 0x00FF00FF00FF00FFUL) << 8);
            v = ((v >> 16) & 0x0000FFFF0000FFFFUL) | ((v & 0x0000FFFF0000FFFFUL) << 16);
            return (v >> 32) | (v << 32);
        }
    }
}
