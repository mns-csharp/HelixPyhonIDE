namespace HelixPythonNamespace.Refactoring.Vectorization
{
    using System.Collections.Generic;
    using HelixPythonNamespace.VirtualCompiler.Ast;
    using H = VecHelpers;

    /// <summary>
    /// Tier-1 deterministic AST-pattern matchers (16 forward + 5 reverse) plus the
    /// MatchAll dispatcher. Port of the C++ <c>PatternMatcher</c>. Each TryMatch_*
    /// returns the extracted data or null.
    /// </summary>
    public static class VecPatternMatcher
    {
        private static readonly HashSet<string> MathToNumpyNames = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "sin", "cos", "tan", "asin", "acos", "atan",
            "sinh", "cosh", "tanh", "asinh", "acosh", "atanh",
            "exp", "log", "log2", "log10", "sqrt",
            "floor", "ceil", "trunc", "fabs",
        };

        // ---- local detectors ----

        private static string AsEmptyListAssign(Stmt s)
        {
            Assign a = s as Assign;
            if (a == null || a.Targets.Count != 1) return null;
            Name tgt = a.Targets[0] as Name;
            if (tgt == null) return null;
            ListExpr lit = a.Value as ListExpr;
            if (lit == null || lit.Elts.Count != 0) return null;
            return tgt.Id;
        }

        // returns (name, renderedLiteral) or null
        private static bool AsSimpleNumericAssign(Stmt s, out string name, out string rendered)
        {
            name = null; rendered = null;
            Assign a = s as Assign;
            if (a == null || a.Targets.Count != 1) return false;
            Name tgt = a.Targets[0] as Name;
            if (tgt == null) return false;
            Constant lit = a.Value as Constant;
            if (lit == null) return false;
            if (!H.IsInt(lit) && !H.IsDouble(lit)) return false;
            name = tgt.Id;
            rendered = H.PyExprPrint(lit);
            return true;
        }

        private static bool AsFirstElementInit(Stmt s, out string accumName, out string iterableExpr)
        {
            accumName = null; iterableExpr = null;
            Assign a = s as Assign;
            if (a == null || a.Targets.Count != 1) return false;
            Name tgt = a.Targets[0] as Name;
            if (tgt == null) return false;
            Subscript sub = a.Value as Subscript;
            if (sub == null) return false;
            Constant zero = sub.Slice as Constant;
            if (zero == null || !H.IsInt(zero) || H.AsLong(zero) != 0) return false;
            Name iterName = sub.Value as Name;
            if (iterName == null) return false;
            accumName = tgt.Id;
            iterableExpr = iterName.Id;
            return true;
        }

        private static bool IsAugAddAccumPlusVar(Stmt s, string accum, string loopVar, BinOp expectedOp)
        {
            AugAssign aa = s as AugAssign;
            if (aa == null) return false;
            if (aa.Op != expectedOp) return false;
            if (!H.IsNameWithId(aa.Target, accum)) return false;
            return H.IsNameWithId(aa.Value, loopVar);
        }

        private static string AsAppendCall(Stmt s, string resultName)
        {
            ExprStmt es = s as ExprStmt;
            if (es == null) return null;
            Call call = es.Value as Call;
            if (call == null) return null;
            if (call.Args.Count != 1 || call.Keywords.Count != 0) return null;
            AttributeExpr attr = call.Func as AttributeExpr;
            if (attr == null || attr.Attr != "append") return null;
            if (!H.IsNameWithId(attr.Value, resultName)) return null;
            string arg = H.PyExprPrint(call.Args[0]);
            if (H.ContainsUnsupported(arg)) return null;
            return arg;
        }

        private static bool AsIfNoElse(Stmt s, out Expr cond, out IReadOnlyList<Stmt> body)
        {
            cond = null; body = null;
            If i = s as If;
            if (i == null) return false;
            if (i.OrElse.Count > 0) return false;
            if (i.Body.Count == 0) return false;
            cond = i.Test; body = i.Body;
            return true;
        }

        private static bool IsScalarLeaf(Expr e, string loopVar)
        {
            if (e == null) return false;
            Constant c = e as Constant;
            if (c != null) return H.IsInt(c) || H.IsDouble(c) || H.IsBool(c);
            Name n = e as Name;
            if (n != null) return n.Id != loopVar;
            return false;
        }

        // ---- F1..F16 ----

        public static ElementwiseBinopData TryMatch_F1(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 1) return null;
            For f = body[0] as For;
            RangeOverLen ro;
            if (!H.TryAsRangeOverLen(f, out ro)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Assign assign = fb[0] as Assign;
            if (assign == null || assign.Targets.Count != 1) return null;
            Subscript tgt = assign.Targets[0] as Subscript;
            if (tgt == null) return null;
            Name tgtArr = tgt.Value as Name;
            if (tgtArr == null) return null;
            if (!H.IsNameWithId(tgt.Slice, ro.LoopVar)) return null;
            BinOpExpr bin = assign.Value as BinOpExpr;
            if (bin == null || !H.IsArithmeticOp(bin.Op)) return null;
            Subscript lhs = bin.Left as Subscript;
            Subscript rhs = bin.Right as Subscript;
            if (lhs == null || rhs == null) return null;
            Name lhsArr = lhs.Value as Name;
            Name rhsArr = rhs.Value as Name;
            if (lhsArr == null || rhsArr == null) return null;
            if (!H.IsNameWithId(lhs.Slice, ro.LoopVar)) return null;
            if (!H.IsNameWithId(rhs.Slice, ro.LoopVar)) return null;
            return new ElementwiseBinopData { TargetName = tgtArr.Id, LhsArrayName = lhsArr.Id, RhsArrayName = rhsArr.Id, Op = bin.Op };
        }

        public static ElementwiseNegateData TryMatch_F2(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 1) return null;
            For f = body[0] as For;
            RangeOverLen ro;
            if (!H.TryAsRangeOverLen(f, out ro)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Assign assign = fb[0] as Assign;
            if (assign == null || assign.Targets.Count != 1) return null;
            Subscript tgt = assign.Targets[0] as Subscript;
            if (tgt == null) return null;
            Name tgtArr = tgt.Value as Name;
            if (tgtArr == null) return null;
            if (!H.IsNameWithId(tgt.Slice, ro.LoopVar)) return null;
            UnaryOpExpr un = assign.Value as UnaryOpExpr;
            if (un == null || un.Op != UnaryOp.USub) return null;
            Subscript sub = un.Operand as Subscript;
            if (sub == null) return null;
            Name arr = sub.Value as Name;
            if (arr == null) return null;
            if (!H.IsNameWithId(sub.Slice, ro.LoopVar)) return null;
            return new ElementwiseNegateData { TargetName = tgtArr.Id, SourceArrayName = arr.Id };
        }

        public static ScalarBroadcastData TryMatch_F3(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 1) return null;
            For f = body[0] as For;
            RangeOverLen ro;
            if (!H.TryAsRangeOverLen(f, out ro)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Assign assign = fb[0] as Assign;
            if (assign == null || assign.Targets.Count != 1) return null;
            Subscript tgt = assign.Targets[0] as Subscript;
            if (tgt == null) return null;
            Name tgtArr = tgt.Value as Name;
            if (tgtArr == null) return null;
            if (!H.IsNameWithId(tgt.Slice, ro.LoopVar)) return null;
            BinOpExpr bin = assign.Value as BinOpExpr;
            if (bin == null || !H.IsArithmeticOp(bin.Op)) return null;
            bool leftIsArr = H.IsIndexedNameWithLoopVar(bin.Left, ro.ArrayName, ro.LoopVar);
            bool rightIsArr = H.IsIndexedNameWithLoopVar(bin.Right, ro.ArrayName, ro.LoopVar);
            ScalarBroadcastData d = new ScalarBroadcastData { TargetName = tgtArr.Id, ArrayName = ro.ArrayName, Op = bin.Op };
            if (leftIsArr && IsScalarLeaf(bin.Right, ro.LoopVar)) { d.ScalarExpr = H.PyExprPrint(bin.Right); d.ScalarOnLeft = false; }
            else if (rightIsArr && IsScalarLeaf(bin.Left, ro.LoopVar)) { d.ScalarExpr = H.PyExprPrint(bin.Left); d.ScalarOnLeft = true; }
            else return null;
            if (H.ContainsUnsupported(d.ScalarExpr)) return null;
            return d;
        }

        public static MathToNumpyData TryMatch_F4(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 1) return null;
            For f = body[0] as For;
            RangeOverLen ro;
            if (!H.TryAsRangeOverLen(f, out ro)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Assign assign = fb[0] as Assign;
            if (assign == null || assign.Targets.Count != 1) return null;
            Subscript tgt = assign.Targets[0] as Subscript;
            if (tgt == null) return null;
            Name tgtArr = tgt.Value as Name;
            if (tgtArr == null) return null;
            if (!H.IsNameWithId(tgt.Slice, ro.LoopVar)) return null;
            Call call = assign.Value as Call;
            if (call == null) return null;
            if (call.Args.Count != 1 || call.Keywords.Count != 0) return null;
            AttributeExpr funcAttr = call.Func as AttributeExpr;
            if (funcAttr == null) return null;
            if (!H.IsNameWithId(funcAttr.Value, "math")) return null;
            if (!MathToNumpyNames.Contains(funcAttr.Attr)) return null;
            if (!H.IsIndexedNameWithLoopVar(call.Args[0], ro.ArrayName, ro.LoopVar)) return null;
            return new MathToNumpyData { TargetName = tgtArr.Id, MathFn = funcAttr.Attr, ArrayName = ro.ArrayName };
        }

        public static MapAppendData TryMatch_F5(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            string resultName = AsEmptyListAssign(body[0]);
            if (resultName == null) return null;
            For f = body[1] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            string arg = AsAppendCall(fb[0], resultName);
            if (arg == null) return null;
            return new MapAppendData { ResultName = resultName, LoopVar = over.LoopVar, IterableExpr = over.IterableExpr, ElementExpr = arg };
        }

        public static FilterAppendData TryMatch_F6(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            string resultName = AsEmptyListAssign(body[0]);
            if (resultName == null) return null;
            For f = body[1] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Expr cond; IReadOnlyList<Stmt> ifBody;
            if (!AsIfNoElse(fb[0], out cond, out ifBody) || ifBody.Count != 1) return null;
            string arg = AsAppendCall(ifBody[0], resultName);
            if (arg == null) return null;
            if (arg != over.LoopVar) return null; // pure filter
            string condStr = H.PyExprPrint(cond);
            if (H.ContainsUnsupported(condStr)) return null;
            return new FilterAppendData { ResultName = resultName, LoopVar = over.LoopVar, IterableExpr = over.IterableExpr, CondExpr = condStr };
        }

        public static FilterMapAppendData TryMatch_F7(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            string resultName = AsEmptyListAssign(body[0]);
            if (resultName == null) return null;
            For f = body[1] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Expr cond; IReadOnlyList<Stmt> ifBody;
            if (!AsIfNoElse(fb[0], out cond, out ifBody) || ifBody.Count != 1) return null;
            string arg = AsAppendCall(ifBody[0], resultName);
            if (arg == null) return null;
            if (arg == over.LoopVar) return null; // not a pure filter
            string condStr = H.PyExprPrint(cond);
            if (H.ContainsUnsupported(condStr)) return null;
            return new FilterMapAppendData { ResultName = resultName, LoopVar = over.LoopVar, IterableExpr = over.IterableExpr, CondExpr = condStr, ElementExpr = arg };
        }

        public static SumData TryMatch_F8(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            string name, rendered;
            if (!AsSimpleNumericAssign(body[0], out name, out rendered)) return null;
            For f = body[1] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            if (!IsAugAddAccumPlusVar(fb[0], name, over.LoopVar, BinOp.Add)) return null;
            SumData d = new SumData { TargetName = name, IterableExpr = over.IterableExpr };
            if (rendered != "0" && rendered != "0.0") d.StartExpr = rendered;
            return d;
        }

        public static ProductData TryMatch_F9(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            string name, rendered;
            if (!AsSimpleNumericAssign(body[0], out name, out rendered)) return null;
            if (rendered != "1" && rendered != "1.0") return null;
            For f = body[1] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            if (!IsAugAddAccumPlusVar(fb[0], name, over.LoopVar, BinOp.Mult)) return null;
            return new ProductData { TargetName = name, IterableExpr = over.IterableExpr };
        }

        private static bool AsTailSlice(Expr e, out string name)
        {
            name = null;
            Subscript sub = e as Subscript;
            if (sub == null) return false;
            Slice slc = sub.Slice as Slice;
            if (slc == null) return false;
            Constant lower = slc.Lower as Constant;
            if (lower == null || !H.IsInt(lower) || H.AsLong(lower) != 1) return false;
            if (slc.Upper != null || slc.Step != null) return false;
            Name baseN = sub.Value as Name;
            if (baseN == null) return false;
            name = baseN.Id;
            return true;
        }

        public static MinMaxData TryMatch_F10(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            string accumName, firstIter;
            if (!AsFirstElementInit(body[0], out accumName, out firstIter)) return null;
            For f = body[1] as For;
            if (f == null) return null;
            Name loopVar = f.Target as Name;
            if (loopVar == null) return null;
            string iterableName;
            string tail;
            if (AsTailSlice(f.Iter, out tail)) iterableName = tail;
            else if (f.Iter is Name nIter) iterableName = nIter.Id;
            else return null;
            if (iterableName != firstIter) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Expr cond; IReadOnlyList<Stmt> ifBody;
            if (!AsIfNoElse(fb[0], out cond, out ifBody) || ifBody.Count != 1) return null;
            Compare cmp = cond as Compare;
            if (cmp == null || cmp.Ops.Count != 1 || cmp.Comparators.Count != 1) return null;
            bool leftIsLoopVar = H.IsNameWithId(cmp.Left, loopVar.Id);
            bool rightIsAccum = H.IsNameWithId(cmp.Comparators[0], accumName);
            bool leftIsAccum = H.IsNameWithId(cmp.Left, accumName);
            bool rightIsLoopVar = H.IsNameWithId(cmp.Comparators[0], loopVar.Id);
            bool isMax;
            if (leftIsLoopVar && rightIsAccum)
            {
                if (cmp.Ops[0] == CmpOp.Gt) isMax = true;
                else if (cmp.Ops[0] == CmpOp.Lt) isMax = false;
                else return null;
            }
            else if (leftIsAccum && rightIsLoopVar)
            {
                if (cmp.Ops[0] == CmpOp.Lt) isMax = true;
                else if (cmp.Ops[0] == CmpOp.Gt) isMax = false;
                else return null;
            }
            else return null;
            Assign upd = ifBody[0] as Assign;
            if (upd == null || upd.Targets.Count != 1) return null;
            if (!H.IsNameWithId(upd.Targets[0], accumName)) return null;
            if (!H.IsNameWithId(upd.Value, loopVar.Id)) return null;
            return new MinMaxData { TargetName = accumName, IterableExpr = firstIter, IsMax = isMax };
        }

        public static CountData TryMatch_F11(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            string name, rendered;
            if (!AsSimpleNumericAssign(body[0], out name, out rendered) || rendered != "0") return null;
            For f = body[1] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Expr cond; IReadOnlyList<Stmt> ifBody;
            if (!AsIfNoElse(fb[0], out cond, out ifBody) || ifBody.Count != 1) return null;
            AugAssign aa = ifBody[0] as AugAssign;
            if (aa == null || aa.Op != BinOp.Add) return null;
            if (!H.IsNameWithId(aa.Target, name)) return null;
            Constant one = aa.Value as Constant;
            if (one == null || !H.IsInt(one) || H.AsLong(one) != 1) return null;
            string condStr = H.PyExprPrint(cond);
            if (H.ContainsUnsupported(condStr)) return null;
            return new CountData { TargetName = name, LoopVar = over.LoopVar, IterableExpr = over.IterableExpr, CondExpr = condStr };
        }

        private static bool IsBoolReturn(Stmt s, bool wantValue)
        {
            Return r = s as Return;
            if (r == null) return false;
            Constant c = r.Value as Constant;
            if (c == null || !H.IsBool(c)) return false;
            return (bool)c.Value == wantValue;
        }

        public static AnyAllData TryMatch_F12(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            For f = body[0] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Expr cond; IReadOnlyList<Stmt> ifBody;
            if (!AsIfNoElse(fb[0], out cond, out ifBody) || ifBody.Count != 1) return null;
            if (!IsBoolReturn(ifBody[0], true)) return null;
            if (!IsBoolReturn(body[1], false)) return null;
            string condStr = H.PyExprPrint(cond);
            if (H.ContainsUnsupported(condStr)) return null;
            return new AnyAllData { LoopVar = over.LoopVar, IterableExpr = over.IterableExpr, CondExpr = condStr, IsAny = true };
        }

        public static AnyAllData TryMatch_F13(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            For f = body[0] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Expr cond; IReadOnlyList<Stmt> ifBody;
            if (!AsIfNoElse(fb[0], out cond, out ifBody) || ifBody.Count != 1) return null;
            if (!IsBoolReturn(ifBody[0], false)) return null;
            if (!IsBoolReturn(body[1], true)) return null;
            UnaryOpExpr notExpr = cond as UnaryOpExpr;
            if (notExpr == null || notExpr.Op != UnaryOp.Not) return null;
            string condStr = H.PyExprPrint(notExpr.Operand);
            if (H.ContainsUnsupported(condStr)) return null;
            return new AnyAllData { LoopVar = over.LoopVar, IterableExpr = over.IterableExpr, CondExpr = condStr, IsAny = false };
        }

        public static WhereData TryMatch_F14(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 1) return null;
            For f = body[0] as For;
            RangeOverLen ro;
            if (!H.TryAsRangeOverLen(f, out ro)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Assign assign = fb[0] as Assign;
            if (assign == null || assign.Targets.Count != 1) return null;
            Subscript tgt = assign.Targets[0] as Subscript;
            if (tgt == null) return null;
            Name tgtArr = tgt.Value as Name;
            if (tgtArr == null) return null;
            if (!H.IsNameWithId(tgt.Slice, ro.LoopVar)) return null;
            IfExp ifx = assign.Value as IfExp;
            if (ifx == null) return null;
            Subscript trueArr = ifx.Body as Subscript;
            Subscript falseArr = ifx.OrElse as Subscript;
            Subscript condArr = ifx.Test as Subscript;
            if (trueArr == null || falseArr == null || condArr == null) return null;
            string t = ExtractIndexedName(trueArr, ro.LoopVar);
            string fls = ExtractIndexedName(falseArr, ro.LoopVar);
            string cnd = ExtractIndexedName(condArr, ro.LoopVar);
            if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(fls) || string.IsNullOrEmpty(cnd)) return null;
            return new WhereData { TargetName = tgtArr.Id, CondArrayName = cnd, TrueArrayName = t, FalseArrayName = fls };
        }

        private static string ExtractIndexedName(Subscript s, string loopVar)
        {
            if (!H.IsNameWithId(s.Slice, loopVar)) return "";
            Name nm = s.Value as Name;
            return nm == null ? "" : nm.Id;
        }

        public static CumsumData TryMatch_F15(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 3) return null;
            string runName, runRendered;
            if (!AsSimpleNumericAssign(body[0], out runName, out runRendered) || runRendered != "0") return null;
            string outName = AsEmptyListAssign(body[1]);
            if (outName == null) return null;
            For f = body[2] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 2) return null;
            if (!IsAugAddAccumPlusVar(fb[0], runName, over.LoopVar, BinOp.Add)) return null;
            string appendArg = AsAppendCall(fb[1], outName);
            if (appendArg == null || appendArg != runName) return null;
            return new CumsumData { OutName = outName, IterableExpr = over.IterableExpr };
        }

        private static bool AsListMultiplyInit(Stmt s, out string outName, out string sizeExpr)
        {
            outName = null; sizeExpr = null;
            Assign a = s as Assign;
            if (a == null || a.Targets.Count != 1) return false;
            Name tgt = a.Targets[0] as Name;
            if (tgt == null) return false;
            BinOpExpr bin = a.Value as BinOpExpr;
            if (bin == null || bin.Op != BinOp.Mult) return false;
            ListExpr lst = bin.Left as ListExpr;
            Expr sizeSide = bin.Right;
            if (lst == null) { lst = bin.Right as ListExpr; sizeSide = bin.Left; }
            if (lst == null) return false;
            if (lst.Elts.Count != 1) return false;
            string sizeStr = H.PyExprPrint(sizeSide);
            if (H.ContainsUnsupported(sizeStr)) return false;
            outName = tgt.Id; sizeExpr = sizeStr;
            return true;
        }

        private static bool AsRangeOver(For f, out string loopVar, out string sizeExpr)
        {
            loopVar = null; sizeExpr = null;
            if (f == null || f.OrElse.Count > 0) return false;
            Name targetName = f.Target as Name;
            if (targetName == null) return false;
            Call iterCall = f.Iter as Call;
            if (iterCall == null) return false;
            if (!H.IsNameWithId(iterCall.Func, "range")) return false;
            if (iterCall.Args.Count != 1 || iterCall.Keywords.Count != 0) return false;
            string sizeStr = H.PyExprPrint(iterCall.Args[0]);
            if (H.ContainsUnsupported(sizeStr)) return false;
            loopVar = targetName.Id; sizeExpr = sizeStr;
            return true;
        }

        public static RangeFillData TryMatch_F16(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            string outName, initSize;
            if (!AsListMultiplyInit(body[0], out outName, out initSize)) return null;
            For f = body[1] as For;
            string loopVar, rangeSize;
            if (!AsRangeOver(f, out loopVar, out rangeSize)) return null;
            if (rangeSize != initSize) return null;
            IReadOnlyList<Stmt> fb = H.StrictForBody(f);
            if (fb == null || fb.Count != 1) return null;
            Assign assign = fb[0] as Assign;
            if (assign == null || assign.Targets.Count != 1) return null;
            Subscript tgt = assign.Targets[0] as Subscript;
            if (tgt == null) return null;
            if (!H.IsNameWithId(tgt.Value, outName)) return null;
            if (!H.IsNameWithId(tgt.Slice, loopVar)) return null;
            string elt = H.PyExprPrint(assign.Value);
            if (H.ContainsUnsupported(elt)) return null;
            return new RangeFillData { OutName = outName, LoopVar = loopVar, SizeExpr = rangeSize, ElementExpr = elt };
        }

        // ---- Reverse matchers ----

        private static Assign AsSingleStmtAssign(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 1) return null;
            Assign a = body[0] as Assign;
            if (a == null || a.Targets.Count != 1) return null;
            if (!(a.Targets[0] is Name)) return null;
            return a;
        }

        private static bool IsNumpyAlias(Expr e)
        {
            Name n = e as Name;
            return n != null && (n.Id == "np" || n.Id == "numpy");
        }

        private static bool AsAsarrayOrName(Expr e, out string name)
        {
            name = null;
            Name n = e as Name;
            if (n != null) { name = n.Id; return true; }
            Call call = e as Call;
            if (call == null) return false;
            if (call.Args.Count != 1 || call.Keywords.Count != 0) return false;
            AttributeExpr attr = call.Func as AttributeExpr;
            if (attr == null || attr.Attr != "asarray") return false;
            if (!IsNumpyAlias(attr.Value)) return false;
            Name baseN = call.Args[0] as Name;
            if (baseN == null) return false;
            name = baseN.Id;
            return true;
        }

        public static ElementwiseBinopData TryMatch_R1(IReadOnlyList<Stmt> body)
        {
            Assign a = AsSingleStmtAssign(body);
            if (a == null) return null;
            BinOpExpr bin = a.Value as BinOpExpr;
            if (bin == null || !H.IsArithmeticOp(bin.Op)) return null;
            string lhs, rhs;
            if (!AsAsarrayOrName(bin.Left, out lhs) || !AsAsarrayOrName(bin.Right, out rhs)) return null;
            bool lhsExplicit = bin.Left is Call;
            bool rhsExplicit = bin.Right is Call;
            if (!lhsExplicit && !rhsExplicit) return null;
            return new ElementwiseBinopData { TargetName = ((Name)a.Targets[0]).Id, LhsArrayName = lhs, RhsArrayName = rhs, Op = bin.Op };
        }

        public static SumData TryMatch_R2(IReadOnlyList<Stmt> body)
        {
            Assign a = AsSingleStmtAssign(body);
            if (a == null) return null;
            Call call = a.Value as Call;
            if (call == null) return null;
            if (!H.IsNameWithId(call.Func, "sum")) return null;
            if (call.Args.Count == 0 || call.Args.Count > 2) return null;
            string iter = H.PyExprPrint(call.Args[0]);
            if (H.ContainsUnsupported(iter)) return null;
            SumData d = new SumData { TargetName = ((Name)a.Targets[0]).Id, IterableExpr = iter };
            if (call.Args.Count == 2)
            {
                string s = H.PyExprPrint(call.Args[1]);
                if (H.ContainsUnsupported(s)) return null;
                d.StartExpr = s;
            }
            return d;
        }

        public static MapAppendData TryMatch_R5(IReadOnlyList<Stmt> body)
        {
            Assign a = AsSingleStmtAssign(body);
            if (a == null) return null;
            ListComp lc = a.Value as ListComp;
            if (lc == null) return null;
            if (lc.Generators.Count != 1) return null;
            Comprehension g = lc.Generators[0];
            if (g.Ifs.Count != 0) return null;
            Name tgt = g.Target as Name;
            if (tgt == null) return null;
            string iter = H.PyExprPrint(g.Iter);
            string elt = H.PyExprPrint(lc.Elt);
            if (H.ContainsUnsupported(iter) || H.ContainsUnsupported(elt)) return null;
            return new MapAppendData { ResultName = ((Name)a.Targets[0]).Id, LoopVar = tgt.Id, IterableExpr = iter, ElementExpr = elt };
        }

        public static MinMaxData TryMatch_R10(IReadOnlyList<Stmt> body)
        {
            Assign a = AsSingleStmtAssign(body);
            if (a == null) return null;
            Call call = a.Value as Call;
            if (call == null) return null;
            bool isMax = H.IsNameWithId(call.Func, "max");
            bool isMin = H.IsNameWithId(call.Func, "min");
            if (!isMax && !isMin) return null;
            if (call.Args.Count != 1 || call.Keywords.Count != 0) return null;
            Name iter = call.Args[0] as Name;
            if (iter == null) return null;
            return new MinMaxData { TargetName = ((Name)a.Targets[0]).Id, IterableExpr = iter.Id, IsMax = isMax };
        }

        public static WhereData TryMatch_R14(IReadOnlyList<Stmt> body)
        {
            Assign a = AsSingleStmtAssign(body);
            if (a == null) return null;
            Call call = a.Value as Call;
            if (call == null) return null;
            AttributeExpr attr = call.Func as AttributeExpr;
            if (attr == null || attr.Attr != "where") return null;
            if (!IsNumpyAlias(attr.Value)) return null;
            if (call.Args.Count != 3 || call.Keywords.Count != 0) return null;
            Name cnd = call.Args[0] as Name;
            Name tru = call.Args[1] as Name;
            Name fls = call.Args[2] as Name;
            if (cnd == null || tru == null || fls == null) return null;
            return new WhereData { TargetName = ((Name)a.Targets[0]).Id, CondArrayName = cnd.Id, TrueArrayName = tru.Id, FalseArrayName = fls.Id };
        }

        // ---- MatchAll dispatcher ----

        public static List<VectorizationCandidate> MatchAll(Module m)
        {
            List<VectorizationCandidate> outList = new List<VectorizationCandidate>();

            System.Action<IReadOnlyList<Stmt>> runOnBody = body =>
            {
                List<Stmt> raw = H.BorrowStmts(body);
                for (int start = 0; start < raw.Count; start++)
                {
                    foreach (int sz in new[] { 1, 2, 3 })
                    {
                        if (start + sz > raw.Count) continue;
                        List<Stmt> window = raw.GetRange(start, sz);
                        if (sz == 1)
                        {
                            Add(outList, window, TryMatch_F1(window), VecPatternEmitter.EmitF1);
                            Add(outList, window, TryMatch_F2(window), VecPatternEmitter.EmitF2);
                            Add(outList, window, TryMatch_F3(window), VecPatternEmitter.EmitF3);
                            Add(outList, window, TryMatch_F4(window), VecPatternEmitter.EmitF4);
                            Add(outList, window, TryMatch_F14(window), VecPatternEmitter.EmitF14);
                            Add(outList, window, TryMatch_R1(window), VecPatternEmitter.EmitR1);
                            Add(outList, window, TryMatch_R2(window), VecPatternEmitter.EmitR2);
                            Add(outList, window, TryMatch_R5(window), VecPatternEmitter.EmitR5);
                            Add(outList, window, TryMatch_R10(window), VecPatternEmitter.EmitR10);
                            Add(outList, window, TryMatch_R14(window), VecPatternEmitter.EmitR14);
                        }
                        else if (sz == 2)
                        {
                            Add(outList, window, TryMatch_F5(window), VecPatternEmitter.EmitF5);
                            Add(outList, window, TryMatch_F6(window), VecPatternEmitter.EmitF6);
                            Add(outList, window, TryMatch_F7(window), VecPatternEmitter.EmitF7);
                            Add(outList, window, TryMatch_F8(window), VecPatternEmitter.EmitF8);
                            Add(outList, window, TryMatch_F9(window), VecPatternEmitter.EmitF9);
                            Add(outList, window, TryMatch_F10(window), VecPatternEmitter.EmitF10);
                            Add(outList, window, TryMatch_F11(window), VecPatternEmitter.EmitF11);
                            Add(outList, window, TryMatch_F12(window), VecPatternEmitter.EmitF12);
                            Add(outList, window, TryMatch_F13(window), VecPatternEmitter.EmitF13);
                            Add(outList, window, TryMatch_F16(window), VecPatternEmitter.EmitF16);
                        }
                        else if (sz == 3)
                        {
                            Add(outList, window, TryMatch_F15(window), VecPatternEmitter.EmitF15);
                        }
                    }
                }
            };

            H.WalkAllBodies(m.Body, runOnBody);

            // Dedup by (kind, subrangeStartLine).
            List<VectorizationCandidate> uniq = new List<VectorizationCandidate>();
            HashSet<long> seen = new HashSet<long>();
            foreach (VectorizationCandidate c in outList)
            {
                long key = ((long)c.PatternKind << 32) | (uint)c.SubrangeStartLine;
                if (seen.Add(key)) uniq.Add(c);
            }
            return uniq;
        }

        private static void Add<T>(List<VectorizationCandidate> outList, IReadOnlyList<Stmt> window, T data, System.Func<T, VectorizationCandidate> emit) where T : class
        {
            if (data == null) return;
            VectorizationCandidate cand = emit(data);
            FillSubrange(cand, window);
            outList.Add(cand);
        }

        internal static void FillSubrange(VectorizationCandidate c, IReadOnlyList<Stmt> window)
        {
            if (window.Count == 0) return;
            Python.Text.SourceSpan s0 = window[0].Span;
            Python.Text.SourceSpan s1 = window[window.Count - 1].Span;
            c.HasSubrange = true;
            c.SubrangeStartLine = s0.Start.Line;
            c.SubrangeStartColumn = s0.Start.Column;
            c.SubrangeEndLine = s1.End.Line;
            c.SubrangeEndColumn = s1.End.Column;
        }
    }
}
