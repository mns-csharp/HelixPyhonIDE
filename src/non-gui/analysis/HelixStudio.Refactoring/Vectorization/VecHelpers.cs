namespace HelixPythonNamespace.Refactoring.Vectorization
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    // Match-data structs carried from matchers to emitters (plain strings).
    public sealed class ElementwiseBinopData { public string TargetName; public string LhsArrayName; public string RhsArrayName; public BinOp Op; }
    public sealed class ElementwiseNegateData { public string TargetName; public string SourceArrayName; }
    public sealed class ScalarBroadcastData { public string TargetName; public string ScalarExpr; public string ArrayName; public BinOp Op; public bool ScalarOnLeft; }
    public sealed class MathToNumpyData { public string TargetName; public string MathFn; public string ArrayName; }
    public sealed class MapAppendData { public string ResultName; public string LoopVar; public string IterableExpr; public string ElementExpr; }
    public sealed class FilterAppendData { public string ResultName; public string LoopVar; public string IterableExpr; public string CondExpr; }
    public sealed class FilterMapAppendData { public string ResultName; public string LoopVar; public string IterableExpr; public string CondExpr; public string ElementExpr; }
    public sealed class SumData { public string TargetName; public string IterableExpr; public string StartExpr = ""; }
    public sealed class ProductData { public string TargetName; public string IterableExpr; }
    public sealed class MinMaxData { public string TargetName; public string IterableExpr; public bool IsMax; }
    public sealed class CountData { public string TargetName; public string LoopVar; public string IterableExpr; public string CondExpr; }
    public sealed class AnyAllData { public string LoopVar; public string IterableExpr; public string CondExpr; public bool IsAny; }
    public sealed class WhereData { public string TargetName; public string CondArrayName; public string TrueArrayName; public string FalseArrayName; }
    public sealed class CumsumData { public string OutName; public string IterableExpr; }
    public sealed class RangeFillData { public string OutName; public string LoopVar; public string SizeExpr; public string ElementExpr; }

    public struct RangeOverLen { public string LoopVar; public string ArrayName; }
    public struct ForOverIter { public string LoopVar; public string IterableExpr; }

    /// <summary>
    /// Minimal expression printer + shape detectors shared by the vectorization
    /// matcher, emitter, and relaxation engine. Port of the C++ matcher helpers +
    /// <c>PyExprPrint</c>.
    /// </summary>
    public static class VecHelpers
    {
        // ---- Constant inspection ----
        public static bool IsInt(Constant c) { return c != null && c.Value is long; }
        public static bool IsDouble(Constant c) { return c != null && c.Value is double; }
        public static bool IsBool(Constant c) { return c != null && c.Value is bool; }
        public static long AsLong(Constant c) { return (long)c.Value; }

        // ---- Operators ----
        public static string OpToString(BinOp op)
        {
            switch (op)
            {
                case BinOp.Add: return "+";
                case BinOp.Sub: return "-";
                case BinOp.Mult: return "*";
                case BinOp.Div: return "/";
                case BinOp.FloorDiv: return "//";
                case BinOp.Mod: return "%";
                case BinOp.Pow: return "**";
                case BinOp.MatMult: return "@";
                case BinOp.LShift: return "<<";
                case BinOp.RShift: return ">>";
                case BinOp.BitOr: return "|";
                case BinOp.BitXor: return "^";
                case BinOp.BitAnd: return "&";
            }
            return "?";
        }

        public static bool IsArithmeticOp(BinOp op)
        {
            switch (op)
            {
                case BinOp.Add:
                case BinOp.Sub:
                case BinOp.Mult:
                case BinOp.Div:
                case BinOp.FloorDiv:
                case BinOp.Mod:
                case BinOp.Pow:
                case BinOp.BitAnd:
                case BinOp.BitOr:
                case BinOp.BitXor:
                    return true;
                default:
                    return false;
            }
        }

        private static string CmpOpToken(CmpOp op)
        {
            switch (op)
            {
                case CmpOp.Eq: return "==";
                case CmpOp.NotEq: return "!=";
                case CmpOp.Lt: return "<";
                case CmpOp.LtE: return "<=";
                case CmpOp.Gt: return ">";
                case CmpOp.GtE: return ">=";
                case CmpOp.Is: return " is ";
                case CmpOp.IsNot: return " is not ";
                case CmpOp.In: return " in ";
                case CmpOp.NotIn: return " not in ";
            }
            return "?";
        }

        private static string UnaryOpToken(UnaryOp op)
        {
            switch (op)
            {
                case UnaryOp.Invert: return "~";
                case UnaryOp.Not: return "not ";
                case UnaryOp.UAdd: return "+";
                case UnaryOp.USub: return "-";
            }
            return "?";
        }

        private static string BoolOpToken(BoolOp op) { return op == BoolOp.And ? " and " : " or "; }

        // ---- Expression printer ----
        public static string PyExprPrint(Expr e)
        {
            StringBuilder sb = new StringBuilder();
            EmitExpr(e, sb);
            return sb.ToString();
        }

        public static bool ContainsUnsupported(string s)
        {
            return s.IndexOf("<unsupported>", StringComparison.Ordinal) >= 0
                || s.IndexOf("<null>", StringComparison.Ordinal) >= 0;
        }

        private static void EmitConstant(Constant c, StringBuilder outSb)
        {
            object v = c.Value;
            if (v == null) { outSb.Append("None"); return; }
            if (v is bool b) { outSb.Append(b ? "True" : "False"); return; }
            if (v is long l) { outSb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double d)
            {
                string s = d.ToString("R", CultureInfo.InvariantCulture);
                if (s.IndexOf('.') < 0 && s.IndexOf('e') < 0 && s.IndexOf('E') < 0 &&
                    s.IndexOf('n') < 0 && s.IndexOf('N') < 0) s += ".0";
                outSb.Append(s); return;
            }
            if (v is string str)
            {
                outSb.Append('"');
                foreach (char ch in str)
                {
                    switch (ch)
                    {
                        case '\\': outSb.Append("\\\\"); break;
                        case '"': outSb.Append("\\\""); break;
                        case '\n': outSb.Append("\\n"); break;
                        case '\t': outSb.Append("\\t"); break;
                        case '\r': outSb.Append("\\r"); break;
                        default: outSb.Append(ch); break;
                    }
                }
                outSb.Append('"'); return;
            }
            if (v is ConstantEllipsis) { outSb.Append("..."); return; }
            outSb.Append("<unsupported>");
        }

        private static void EmitCall(Call c, StringBuilder outSb)
        {
            EmitExpr(c.Func, outSb);
            outSb.Append("(");
            bool first = true;
            foreach (Expr a in c.Args)
            {
                if (!first) outSb.Append(", ");
                first = false;
                EmitExpr(a, outSb);
            }
            foreach (Keyword kw in c.Keywords)
            {
                if (!first) outSb.Append(", ");
                first = false;
                if (!string.IsNullOrEmpty(kw.Name)) outSb.Append(kw.Name).Append("=");
                else outSb.Append("**");
                EmitExpr(kw.Value, outSb);
            }
            outSb.Append(")");
        }

        private static void EmitExpr(Expr e, StringBuilder outSb)
        {
            if (e == null) { outSb.Append("<null>"); return; }
            if (e is Name n) { outSb.Append(n.Id); return; }
            if (e is Constant c) { EmitConstant(c, outSb); return; }
            if (e is BinOpExpr b)
            {
                outSb.Append("(");
                EmitExpr(b.Left, outSb);
                outSb.Append(" ").Append(OpToString(b.Op)).Append(" ");
                EmitExpr(b.Right, outSb);
                outSb.Append(")");
                return;
            }
            if (e is UnaryOpExpr u)
            {
                outSb.Append("(").Append(UnaryOpToken(u.Op));
                EmitExpr(u.Operand, outSb);
                outSb.Append(")");
                return;
            }
            if (e is AttributeExpr a)
            {
                EmitExpr(a.Value, outSb);
                outSb.Append(".").Append(a.Attr);
                return;
            }
            if (e is Call call) { EmitCall(call, outSb); return; }
            if (e is Subscript sub)
            {
                EmitExpr(sub.Value, outSb);
                outSb.Append("[");
                // A tuple subscript renders WITHOUT the surrounding parens a bare tuple
                // expression would get: `X[a, b, c]`, not `X[(a, b, c)]`. This keeps
                // type hints (Tuple[int, str], Dict[K, V], Callable[[int], str]) and
                // numpy multi-index (arr[i, j]) textually exact.
                TupleExpr subTuple = sub.Slice as TupleExpr;
                if (subTuple != null)
                {
                    bool firstEl = true;
                    foreach (Expr el in subTuple.Elts)
                    {
                        if (!firstEl) outSb.Append(", ");
                        firstEl = false;
                        EmitExpr(el, outSb);
                    }
                    if (subTuple.Elts.Count == 1) outSb.Append(",");
                }
                else
                {
                    EmitExpr(sub.Slice, outSb);
                }
                outSb.Append("]");
                return;
            }
            if (e is Compare cmp)
            {
                outSb.Append("(");
                EmitExpr(cmp.Left, outSb);
                IReadOnlyList<CmpOp> ops = cmp.Ops;
                IReadOnlyList<Expr> cs = cmp.Comparators;
                for (int i = 0; i < ops.Count && i < cs.Count; i++)
                {
                    string tok = CmpOpToken(ops[i]);
                    if (tok.Length > 0 && tok[0] == ' ') outSb.Append(tok);
                    else outSb.Append(" ").Append(tok).Append(" ");
                    EmitExpr(cs[i], outSb);
                }
                outSb.Append(")");
                return;
            }
            if (e is BoolOpExpr bo)
            {
                outSb.Append("(");
                bool first = true;
                foreach (Expr v in bo.Values)
                {
                    if (!first) outSb.Append(BoolOpToken(bo.Op));
                    first = false;
                    EmitExpr(v, outSb);
                }
                outSb.Append(")");
                return;
            }
            if (e is TupleExpr t)
            {
                outSb.Append("(");
                bool first = true;
                foreach (Expr el in t.Elts)
                {
                    if (!first) outSb.Append(", ");
                    first = false;
                    EmitExpr(el, outSb);
                }
                if (t.Elts.Count == 1) outSb.Append(",");
                outSb.Append(")");
                return;
            }
            if (e is ListExpr lst)
            {
                outSb.Append("[");
                bool first = true;
                foreach (Expr el in lst.Elts)
                {
                    if (!first) outSb.Append(", ");
                    first = false;
                    EmitExpr(el, outSb);
                }
                outSb.Append("]");
                return;
            }
            outSb.Append("<unsupported>");
        }

        // ---- Shape detectors ----
        public static bool IsNameWithId(Expr e, string id)
        {
            Name n = e as Name;
            return n != null && n.Id == id;
        }

        public static bool IsIndexedNameWithLoopVar(Expr e, string arrayName, string loopVar)
        {
            Subscript sub = e as Subscript;
            if (sub == null) return false;
            if (!IsNameWithId(sub.Value, arrayName)) return false;
            return IsNameWithId(sub.Slice, loopVar);
        }

        public static bool TryAsRangeOverLen(For f, out RangeOverLen result)
        {
            result = default(RangeOverLen);
            if (f == null) return false;
            if (f.OrElse.Count > 0) return false;
            Name targetName = f.Target as Name;
            if (targetName == null) return false;
            Call iterCall = f.Iter as Call;
            if (iterCall == null) return false;
            if (!IsNameWithId(iterCall.Func, "range")) return false;
            if (iterCall.Args.Count != 1 || iterCall.Keywords.Count != 0) return false;
            Call lenCall = iterCall.Args[0] as Call;
            if (lenCall == null) return false;
            if (!IsNameWithId(lenCall.Func, "len")) return false;
            if (lenCall.Args.Count != 1 || lenCall.Keywords.Count != 0) return false;
            Name arrName = lenCall.Args[0] as Name;
            if (arrName == null) return false;
            result.LoopVar = targetName.Id;
            result.ArrayName = arrName.Id;
            return true;
        }

        public static bool TryAsForOverIter(For f, out ForOverIter result)
        {
            result = default(ForOverIter);
            if (f == null) return false;
            if (f.OrElse.Count > 0) return false;
            Name targetName = f.Target as Name;
            if (targetName == null) return false;
            string iterStr = PyExprPrint(f.Iter);
            if (ContainsUnsupported(iterStr)) return false;
            result.LoopVar = targetName.Id;
            result.IterableExpr = iterStr;
            return true;
        }

        public static IReadOnlyList<Stmt> StrictForBody(For f)
        {
            if (f == null) return null;
            if (f.OrElse.Count > 0) return null;
            if (f.Body.Count == 0) return null;
            return f.Body;
        }

        public static List<Stmt> BorrowStmts(IReadOnlyList<Stmt> owned)
        {
            return new List<Stmt>(owned);
        }

        public static void WalkAllBodies(IReadOnlyList<Stmt> body, Action<IReadOnlyList<Stmt>> cb)
        {
            cb(body);
            foreach (Stmt s in body)
            {
                if (s is FunctionDef fd) WalkAllBodies(fd.Body, cb);
                else if (s is AsyncFunctionDef afd) WalkAllBodies(afd.Body, cb);
                else if (s is ClassDef cd) WalkAllBodies(cd.Body, cb);
                else if (s is For f) { WalkAllBodies(f.Body, cb); WalkAllBodies(f.OrElse, cb); }
                else if (s is AsyncFor af) { WalkAllBodies(af.Body, cb); WalkAllBodies(af.OrElse, cb); }
                else if (s is While w) { WalkAllBodies(w.Body, cb); WalkAllBodies(w.OrElse, cb); }
                else if (s is If i) { WalkAllBodies(i.Body, cb); WalkAllBodies(i.OrElse, cb); }
                else if (s is With wi) WalkAllBodies(wi.Body, cb);
                else if (s is AsyncWith aw) WalkAllBodies(aw.Body, cb);
                else if (s is Try tr) { WalkAllBodies(tr.Body, cb); WalkAllBodies(tr.OrElse, cb); WalkAllBodies(tr.FinalBody, cb); }
                else if (s is TryStar ts) { WalkAllBodies(ts.Body, cb); WalkAllBodies(ts.OrElse, cb); WalkAllBodies(ts.FinalBody, cb); }
            }
        }
    }
}
