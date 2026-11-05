namespace HelixPythonNamespace.CudaVirtualCompiler.Analyze
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Lexing;

    /// <summary>
    /// Device limits. These mirror helix::cuda_designer::CudaValidator
    /// (kMaxThreadsPerBlock / kMaxBlockDim / kMaxBlockDimZ) — kept in sync with the
    /// visual designer's source of truth, but duplicated here so the analysis engine
    /// stays free of the designer dependency. Port of cuda_vcompiler/analyze/cuda_context.h limits.
    /// </summary>
    public static class Limits
    {
        public const long MaxThreadsPerBlock = 1024;
        public const long MaxBlockDimXY = 1024;
        public const long MaxBlockDimZ = 64;
        public const long MaxGridDimYZ = 65535;
        public const long MaxStaticSharedMem = 49152;  // 48 KB
    }

    /// <summary>
    /// Lattice of pointer memory spaces. Join is the usual upper bound;
    /// Host join Device = Conflict, X join Unknown = X. Port of
    /// helix::cuda_vcompiler::analyze::MemSpace.
    /// </summary>
    public enum MemSpace { Unknown, Host, Device, Managed, Conflict }

    /// <summary>
    /// Context for constant folding. <see cref="Pp"/> supplies object-like #define
    /// values; <see cref="VarInits"/> maps a const-ish local variable name to its
    /// initializer expression so `dim3 block(256); kernel&lt;&lt;&lt;g, block&gt;&gt;&gt;()` can be
    /// resolved one level deep. Port of helix::cuda_vcompiler::analyze::EvalContext.
    /// </summary>
    public sealed class EvalContext
    {
        public Preprocessor Pp;                             // nullable
        public IReadOnlyDictionary<string, Expr> VarInits;  // nullable
    }

    /// <summary>
    /// A folded launch dimension. <see cref="Known"/> is false if any component could
    /// not be evaluated (in which case x/y/z are meaningless). Port of
    /// helix::cuda_vcompiler::analyze::Dim3Value.
    /// </summary>
    public sealed class Dim3Value
    {
        public long X = 1, Y = 1, Z = 1;
        public bool Known;

        public long Product()
        {
            return X * Y * Z;
        }
    }

    /// <summary>
    /// Constant folding + memory-space lattice helpers shared by the CUDA analyses.
    /// Port of the C++ helix::cuda_vcompiler::analyze free functions
    /// (cuda_vcompiler/analyze/cuda_context.{h,cpp}).
    /// </summary>
    public static class CudaContext
    {
        public static MemSpace JoinSpace(MemSpace a, MemSpace b)
        {
            if (a == b) return a;
            if (a == MemSpace.Unknown) return b;
            if (b == MemSpace.Unknown) return a;
            if (a == MemSpace.Conflict || b == MemSpace.Conflict) return MemSpace.Conflict;
            // Distinct concrete spaces conflict (e.g. Host vs Device). Managed mixes
            // freely with either, so treat Managed join X as X's stronger constraint:
            if (a == MemSpace.Managed) return b;
            if (b == MemSpace.Managed) return a;
            return MemSpace.Conflict;
        }

        public static string SpaceName(MemSpace s)
        {
            switch (s)
            {
                case MemSpace.Unknown: return "unknown";
                case MemSpace.Host: return "host";
                case MemSpace.Device: return "device";
                case MemSpace.Managed: return "managed";
                case MemSpace.Conflict: return "conflicting";
            }
            return "unknown";
        }

        /// <summary>Best-effort constant integer evaluation; null if not foldable.</summary>
        public static long? EvalConstInt(Expr e, EvalContext ctx)
        {
            if (e == null) return null;

            LiteralExpr lit = e as LiteralExpr;
            if (lit != null)
            {
                if (lit.Kind == LiteralKind.Int) return ParseIntLiteral(lit.Text);
                if (lit.Kind == LiteralKind.Bool) return lit.Text == "true" ? (long?)1 : (long?)0;
                return null;
            }
            ParenExpr paren = e as ParenExpr;
            if (paren != null)
            {
                return EvalConstInt(paren.Inner, ctx);
            }
            CastExpr cast = e as CastExpr;
            if (cast != null)
            {
                return EvalConstInt(cast.Operand, ctx);
            }
            NameExpr name = e as NameExpr;
            if (name != null)
            {
                // Local const-ish variable initializer (one level).
                if (ctx.VarInits != null)
                {
                    Expr init;
                    if (ctx.VarInits.TryGetValue(name.Id, out init) && init != null && !ReferenceEquals(init, e))
                    {
                        long? v = EvalConstInt(init, ctx);
                        if (v.HasValue) return v;
                    }
                }
                // Object-like macro whose body is an integer.
                if (ctx.Pp != null)
                {
                    string m = ctx.Pp.Lookup(name.Id);
                    if (m != null)
                    {
                        long? v = ParseIntLiteral(m);
                        if (v.HasValue) return v;
                    }
                }
                return null;
            }
            UnaryExpr un = e as UnaryExpr;
            if (un != null)
            {
                long? v = EvalConstInt(un.Operand, ctx);
                if (!v.HasValue) return null;
                switch (un.Op)
                {
                    case UnaryOpKind.Minus: return -v.Value;
                    case UnaryOpKind.Plus: return v.Value;
                    case UnaryOpKind.BitNot: return ~v.Value;
                    case UnaryOpKind.Not: return v.Value != 0 ? (long?)0 : (long?)1;
                    default: return null;
                }
            }
            BinaryExpr bin = e as BinaryExpr;
            if (bin != null)
            {
                long? l = EvalConstInt(bin.Lhs, ctx);
                long? r = EvalConstInt(bin.Rhs, ctx);
                if (!l.HasValue || !r.HasValue) return null;
                long lv = l.Value, rv = r.Value;
                switch (bin.Op)
                {
                    case BinOpKind.Add: return lv + rv;
                    case BinOpKind.Sub: return lv - rv;
                    case BinOpKind.Mul: return lv * rv;
                    case BinOpKind.Div: return rv != 0 ? (long?)(lv / rv) : null;
                    case BinOpKind.Mod: return rv != 0 ? (long?)(lv % rv) : null;
                    case BinOpKind.Shl: return lv << (int)rv;
                    case BinOpKind.Shr: return lv >> (int)rv;
                    case BinOpKind.BitAnd: return lv & rv;
                    case BinOpKind.BitOr: return lv | rv;
                    case BinOpKind.BitXor: return lv ^ rv;
                    default: return null;
                }
            }
            return null;
        }

        /// <summary>
        /// Evaluate a grid/block launch argument: a scalar count, dim3(x,y,z), or a
        /// brace-init list, following one level of variable indirection via ctx.VarInits.
        /// </summary>
        public static Dim3Value EvalDim(Expr e, EvalContext ctx)
        {
            Dim3Value outVal = new Dim3Value();
            if (e == null) return outVal;

            // Follow one level of variable indirection (e.g. `dim3 block(256)`).
            NameExpr name = e as NameExpr;
            if (name != null)
            {
                if (ctx.VarInits != null)
                {
                    Expr init;
                    if (ctx.VarInits.TryGetValue(name.Id, out init) && init != null && !ReferenceEquals(init, e))
                        return EvalDim(init, ctx);
                }
                // Bare scalar variable we can't resolve.
                return outVal;
            }
            ParenExpr paren = e as ParenExpr;
            if (paren != null)
            {
                return EvalDim(paren.Inner, ctx);
            }

            IReadOnlyList<Expr> args = DimArgs(e);
            if (args != null)
            {
                long[] dims = { 1, 1, 1 };
                bool known = true;
                for (int i = 0; i < 3 && i < args.Count; ++i)
                {
                    long? v = EvalConstInt(args[i], ctx);
                    if (v.HasValue) dims[i] = v.Value; else known = false;
                }
                if (args.Count == 0) known = false;
                outVal.X = dims[0]; outVal.Y = dims[1]; outVal.Z = dims[2];
                outVal.Known = known;
                return outVal;
            }

            // Scalar count => 1-D block/grid of that many.
            long? scalar = EvalConstInt(e, ctx);
            if (scalar.HasValue)
            {
                outVal.X = scalar.Value; outVal.Y = 1; outVal.Z = 1; outVal.Known = true;
                return outVal;
            }
            return outVal;
        }

        /// <summary>True if <paramref name="e"/> transitively reads a name for which <paramref name="pred"/> returns true.</summary>
        public static bool ExprReadsName(Expr e, Func<string, bool> pred)
        {
            if (e == null) return false;
            NameExpr n = e as NameExpr;
            if (n != null) return pred(n.Id);
            ParenExpr p = e as ParenExpr;
            if (p != null) return ExprReadsName(p.Inner, pred);
            UnaryExpr u = e as UnaryExpr;
            if (u != null) return ExprReadsName(u.Operand, pred);
            BinaryExpr b = e as BinaryExpr;
            if (b != null)
                return ExprReadsName(b.Lhs, pred) || ExprReadsName(b.Rhs, pred);
            MemberExpr m = e as MemberExpr;
            if (m != null) return ExprReadsName(m.Object, pred);
            IndexExpr ix = e as IndexExpr;
            if (ix != null)
                return ExprReadsName(ix.Base, pred) || ExprReadsName(ix.Index, pred);
            CallExpr c = e as CallExpr;
            if (c != null)
            {
                if (ExprReadsName(c.Callee, pred)) return true;
                for (int i = 0; i < c.Args.Count; ++i) if (ExprReadsName(c.Args[i], pred)) return true;
                return false;
            }
            ConditionalExpr cond = e as ConditionalExpr;
            if (cond != null)
                return ExprReadsName(cond.Cond, pred) || ExprReadsName(cond.Then, pred) ||
                       ExprReadsName(cond.Else, pred);
            CastExpr cast = e as CastExpr;
            if (cast != null) return ExprReadsName(cast.Operand, pred);
            return false;
        }

        // If `e` is a call/brace constructing a dim3 (or similar), return its argument
        // list; else null.
        private static IReadOnlyList<Expr> DimArgs(Expr e)
        {
            CallExpr call = e as CallExpr;
            if (call != null)
            {
                NameExpr n = call.Callee as NameExpr;
                if (n != null)
                {
                    // dim3(...) / make_uint3(...) etc.
                    return call.Args;
                }
            }
            InitListExpr init = e as InitListExpr;
            if (init != null)
            {
                return init.Elements;
            }
            return null;
        }

        /// <summary>
        /// Parse a C++ integer literal honoring 0x / 0 (octal) prefixes and stripping
        /// u/U/l/L suffixes; null if the whole token is not a valid integer. Mirrors
        /// the C++ ParseIntLiteral (std::stoll base 0) with a whole-string check.
        /// </summary>
        private static long? ParseIntLiteral(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            // Strip integer suffixes (u/U/l/L).
            string t = text;
            while (t.Length > 0)
            {
                char c = t[t.Length - 1];
                if (c == 'u' || c == 'U' || c == 'l' || c == 'L') t = t.Substring(0, t.Length - 1);
                else break;
            }
            // Remove digit separators (') the way the C++ number lexer permits them.
            if (t.IndexOf('\'') >= 0) t = t.Replace("'", string.Empty);
            if (t.Length == 0) return null;

            int i = 0;
            int sign = 1;
            if (t[i] == '+' || t[i] == '-')
            {
                if (t[i] == '-') sign = -1;
                ++i;
            }
            if (i >= t.Length) return null;

            int radix = 10;
            if (t[i] == '0' && i + 1 < t.Length && (t[i + 1] == 'x' || t[i + 1] == 'X'))
            {
                radix = 16;
                i += 2;
                if (i >= t.Length) return null;
            }
            else if (t[i] == '0' && i + 1 < t.Length)
            {
                radix = 8;
                i += 1;
            }

            long value = 0;
            for (; i < t.Length; ++i)
            {
                int digit = DigitValue(t[i]);
                if (digit < 0 || digit >= radix) return null;  // trailing junk / wrong base
                try
                {
                    value = checked(value * radix + digit);
                }
                catch (OverflowException)
                {
                    return null;
                }
            }
            return sign * value;
        }

        private static int DigitValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            return -1;
        }
    }
}
