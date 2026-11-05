namespace HelixPythonNamespace.Refactoring.Vectorization
{
    using System.Collections.Generic;
    using HelixPythonNamespace.VirtualCompiler.Ast;
    using H = VecHelpers;

    /// <summary>
    /// Tier-2: accept selections that deviate from a strict pattern by up to
    /// <see cref="RelaxationBudget"/> constraints, surfacing each deviation as a
    /// caveat and discounting confidence. Port of the C++ <c>RelaxationEngine</c>.
    /// </summary>
    public static class VecRelaxationEngine
    {
        private const int RelaxationBudget = 2;

        private sealed class ConstraintRecorder
        {
            public readonly List<CandidateIssue> Issues = new List<CandidateIssue>();
            public void Fail(string code, string description) { Issues.Add(new CandidateIssue(code, description)); }
            public bool Empty { get { return Issues.Count == 0; } }
            public int Count { get { return Issues.Count; } }
        }

        private static int ConfidenceFromIssueCount(int n)
        {
            if (n == 0) return 100;
            int c = 100 - 25 * n;
            return c < 25 ? 25 : c;
        }

        // ---- F1 relaxation: c[i] = a[i] OP b[i] ----

        public static VectorizationCandidate TryMatchRelaxed_F1(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 1) return null;
            For f = body[0] as For;
            RangeOverLen ro;
            if (!H.TryAsRangeOverLen(f, out ro)) return null;
            IReadOnlyList<Stmt> fbody = H.StrictForBody(f);
            if (fbody == null || fbody.Count != 1) return null;
            Assign assign = fbody[0] as Assign;
            if (assign == null || assign.Targets.Count != 1) return null;
            Subscript tgt = assign.Targets[0] as Subscript;
            if (tgt == null) return null;
            Name tgtArr = tgt.Value as Name;
            if (tgtArr == null) return null;
            BinOpExpr bin = assign.Value as BinOpExpr;
            if (bin == null) return null;
            Subscript lhs = bin.Left as Subscript;
            Subscript rhs = bin.Right as Subscript;
            if (lhs == null || rhs == null) return null;
            Name lhsArr = lhs.Value as Name;
            Name rhsArr = rhs.Value as Name;
            if (lhsArr == null || rhsArr == null) return null;

            ConstraintRecorder rec = new ConstraintRecorder();
            if (!H.IsArithmeticOp(bin.Op))
            {
                rec.Fail("PYR2001", "Binary operator '" + H.OpToString(bin.Op) + "' is not in the element-wise vectorization whitelist; the rewrite assumes ndarray semantics for this operator.");
            }
            if (!H.IsNameWithId(tgt.Slice, ro.LoopVar))
            {
                rec.Fail("PYR2002", "Target index expression doesn't match the loop variable; the rewrite indexes the entire array, which may overwrite rows the original loop didn't touch.");
            }
            if (!H.IsNameWithId(lhs.Slice, ro.LoopVar))
            {
                rec.Fail("PYR2003", "Left operand index expression doesn't match the loop variable; the rewrite reads the whole array regardless of offset.");
            }
            if (!H.IsNameWithId(rhs.Slice, ro.LoopVar))
            {
                rec.Fail("PYR2004", "Right operand index expression doesn't match the loop variable; the rewrite reads the whole array regardless of offset.");
            }

            if (rec.Empty || rec.Count > RelaxationBudget) return null;

            ElementwiseBinopData d = new ElementwiseBinopData { TargetName = tgtArr.Id, LhsArrayName = lhsArr.Id, RhsArrayName = rhsArr.Id, Op = bin.Op };
            VectorizationCandidate cand = VecPatternEmitter.EmitF1(d);
            VecPatternEmitter.ApplyRelaxation(cand, rec.Issues, ConfidenceFromIssueCount(rec.Count));
            return cand;
        }

        // ---- relaxed accumulator-init detector ----

        private sealed class AccumInit
        {
            public string AccumName;
            public string InitExpr;
            public bool InitWasLiteralZero;
            public bool InitWasLiteralOne;
        }

        private static AccumInit AsRelaxedAccumInit(Stmt s)
        {
            Assign a = s as Assign;
            if (a == null || a.Targets.Count != 1) return null;
            Name tgt = a.Targets[0] as Name;
            if (tgt == null) return null;

            AccumInit ai = new AccumInit { AccumName = tgt.Id };
            Constant c = a.Value as Constant;
            if (c != null)
            {
                if (!H.IsInt(c) && !H.IsDouble(c)) return null;
                string s2 = H.PyExprPrint(c);
                ai.InitExpr = s2;
                ai.InitWasLiteralZero = (s2 == "0" || s2 == "0.0");
                ai.InitWasLiteralOne = (s2 == "1" || s2 == "1.0");
                return ai;
            }
            Name n = a.Value as Name;
            if (n != null) { ai.InitExpr = n.Id; return ai; }
            return null;
        }

        // ---- F8 relaxation: total = N; for x in xs: total += x ----

        public static VectorizationCandidate TryMatchRelaxed_F8(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            AccumInit init = AsRelaxedAccumInit(body[0]);
            if (init == null) return null;
            For f = body[1] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fbody = H.StrictForBody(f);
            if (fbody == null || fbody.Count != 1) return null;
            AugAssign aa = fbody[0] as AugAssign;
            if (aa == null) return null;
            if (!H.IsNameWithId(aa.Target, init.AccumName)) return null;
            if (!H.IsNameWithId(aa.Value, over.LoopVar)) return null;

            ConstraintRecorder rec = new ConstraintRecorder();
            if (aa.Op != BinOp.Add)
            {
                rec.Fail("PYR2010", "Loop body uses '" + H.OpToString(aa.Op) + "=' rather than '+='; the sum() rewrite ignores that operator and adds elements unconditionally.");
            }
            bool initIsNonStandard = !init.InitWasLiteralZero;
            if (initIsNonStandard)
            {
                rec.Fail("PYR2011", "Initial accumulator value is '" + init.InitExpr + "' (not 0); the rewrite passes it as sum()'s start argument but verify the original semantics match.");
            }

            if (rec.Empty || rec.Count > RelaxationBudget) return null;

            SumData d = new SumData { TargetName = init.AccumName, IterableExpr = over.IterableExpr };
            if (initIsNonStandard) d.StartExpr = init.InitExpr;
            VectorizationCandidate cand = VecPatternEmitter.EmitF8(d);
            VecPatternEmitter.ApplyRelaxation(cand, rec.Issues, ConfidenceFromIssueCount(rec.Count));
            return cand;
        }

        // ---- F9 relaxation: prod = 1; for x in xs: prod *= x ----

        public static VectorizationCandidate TryMatchRelaxed_F9(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            AccumInit init = AsRelaxedAccumInit(body[0]);
            if (init == null) return null;
            For f = body[1] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fbody = H.StrictForBody(f);
            if (fbody == null || fbody.Count != 1) return null;
            AugAssign aa = fbody[0] as AugAssign;
            if (aa == null) return null;
            if (!H.IsNameWithId(aa.Target, init.AccumName)) return null;
            if (!H.IsNameWithId(aa.Value, over.LoopVar)) return null;

            ConstraintRecorder rec = new ConstraintRecorder();
            if (aa.Op != BinOp.Mult)
            {
                rec.Fail("PYR2020", "Loop body uses '" + H.OpToString(aa.Op) + "=' rather than '*='; the math.prod() rewrite multiplies elements unconditionally.");
            }
            if (!init.InitWasLiteralOne)
            {
                rec.Fail("PYR2021", "Initial accumulator value is '" + init.InitExpr + "' (not 1); math.prod() starts from 1, so the result differs by that factor.");
            }

            if (rec.Empty || rec.Count > RelaxationBudget) return null;

            ProductData d = new ProductData { TargetName = init.AccumName, IterableExpr = over.IterableExpr };
            VectorizationCandidate cand = VecPatternEmitter.EmitF9(d);
            VecPatternEmitter.ApplyRelaxation(cand, rec.Issues, ConfidenceFromIssueCount(rec.Count));
            return cand;
        }

        // ---- F11 relaxation: n = 0; for x in xs: if cond: n += 1 ----

        private static bool IsNumericLiteral(Expr e)
        {
            Constant c = e as Constant;
            return c != null && (H.IsInt(c) || H.IsDouble(c));
        }

        public static VectorizationCandidate TryMatchRelaxed_F11(IReadOnlyList<Stmt> body)
        {
            if (body.Count != 2) return null;
            AccumInit init = AsRelaxedAccumInit(body[0]);
            if (init == null) return null;
            For f = body[1] as For;
            ForOverIter over;
            if (!H.TryAsForOverIter(f, out over)) return null;
            IReadOnlyList<Stmt> fbody = H.StrictForBody(f);
            if (fbody == null || fbody.Count != 1) return null;
            If iff = fbody[0] as If;
            if (iff == null || iff.OrElse.Count > 0 || iff.Body.Count != 1) return null;
            AugAssign aa = iff.Body[0] as AugAssign;
            if (aa == null || aa.Op != BinOp.Add) return null;
            if (!H.IsNameWithId(aa.Target, init.AccumName)) return null;
            if (!IsNumericLiteral(aa.Value)) return null;

            ConstraintRecorder rec = new ConstraintRecorder();
            string incRendered = H.PyExprPrint(aa.Value);
            if (incRendered != "1")
            {
                rec.Fail("PYR2030", "Loop body increments by '" + incRendered + "' rather than 1; sum(1 for ...) treats every match as weight 1.");
            }
            if (!init.InitWasLiteralZero)
            {
                rec.Fail("PYR2031", "Initial counter value is '" + init.InitExpr + "' (not 0); the rewrite resets the count to 0.");
            }

            if (rec.Empty || rec.Count > RelaxationBudget) return null;

            string condStr = H.PyExprPrint(iff.Test);
            if (H.ContainsUnsupported(condStr)) return null;

            CountData d = new CountData { TargetName = init.AccumName, LoopVar = over.LoopVar, IterableExpr = over.IterableExpr, CondExpr = condStr };
            VectorizationCandidate cand = VecPatternEmitter.EmitF11(d);
            VecPatternEmitter.ApplyRelaxation(cand, rec.Issues, ConfidenceFromIssueCount(rec.Count));
            return cand;
        }

        // ---- Top-level dispatcher ----

        public static List<VectorizationCandidate> MatchAllRelaxed(Module m)
        {
            List<VectorizationCandidate> outList = new List<VectorizationCandidate>();

            System.Action<IReadOnlyList<Stmt>> runOnBody = body =>
            {
                List<Stmt> raw = H.BorrowStmts(body);
                for (int start = 0; start < raw.Count; start++)
                {
                    foreach (int sz in new[] { 1, 2 })
                    {
                        if (start + sz > raw.Count) continue;
                        List<Stmt> window = raw.GetRange(start, sz);
                        if (sz == 1)
                        {
                            Run(outList, window, TryMatchRelaxed_F1(window));
                        }
                        else if (sz == 2)
                        {
                            Run(outList, window, TryMatchRelaxed_F8(window));
                            Run(outList, window, TryMatchRelaxed_F9(window));
                            Run(outList, window, TryMatchRelaxed_F11(window));
                        }
                    }
                }
            };

            H.WalkAllBodies(m.Body, runOnBody);
            return outList;
        }

        private static void Run(List<VectorizationCandidate> outList, IReadOnlyList<Stmt> window, VectorizationCandidate cand)
        {
            if (cand == null) return;
            VecPatternMatcher.FillSubrange(cand, window);
            outList.Add(cand);
        }
    }
}
