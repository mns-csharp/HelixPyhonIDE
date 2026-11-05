namespace HelixPythonNamespace.Refactoring.LambdaTransform
{
    using System.Collections.Generic;
    using HelixPythonNamespace.VirtualCompiler.Ast;
    using H = HelixPythonNamespace.Refactoring.Vectorization.VecHelpers;

    /// <summary>Tier-2 relaxed matchers for function ↔ lambda. Port of the C++ lambda <c>RelaxationEngine</c>.</summary>
    public static class LambdaRelaxationEngine
    {
        private static bool BodyHasDocstring(IReadOnlyList<Stmt> body)
        {
            if (body.Count == 0) return false;
            ExprStmt es = body[0] as ExprStmt;
            if (es == null) return false;
            Constant k = es.Value as Constant;
            return k != null && k.Value is string;
        }

        private static bool ArgsHaveAnnotations(Arguments args)
        {
            if (args == null) return false;
            if (CheckAnn(args.Args)) return true;
            if (CheckAnn(args.PosOnlyArgs)) return true;
            if (CheckAnn(args.KwOnlyArgs)) return true;
            if (args.VarArg != null && args.VarArg.Annotation != null) return true;
            if (args.KwArg != null && args.KwArg.Annotation != null) return true;
            return false;
        }

        private static bool CheckAnn(IReadOnlyList<Arg> v)
        {
            foreach (Arg a in v)
                if (a != null && a.Annotation != null) return true;
            return false;
        }

        private static List<Stmt> BodyWithoutDocstring(IReadOnlyList<Stmt> body)
        {
            List<Stmt> outList = new List<Stmt>();
            foreach (Stmt s in body) outList.Add(s);
            if (BodyHasDocstring(body) && outList.Count > 0) outList.RemoveAt(0);
            return outList;
        }

        private static bool IsSingleReturnBodyPtr(IReadOnlyList<Stmt> body, out string outExpr)
        {
            outExpr = null;
            if (body.Count != 1) return false;
            Return ret = body[0] as Return;
            if (ret == null || ret.Value == null) return false;
            outExpr = H.PyExprPrint(ret.Value);
            return true;
        }

        private static LambdaTransformCandidate TryF2L1Relaxed(FunctionDef fd)
        {
            List<Stmt> stripped = BodyWithoutDocstring(fd.Body);
            string bodyExpr;
            if (!IsSingleReturnBodyPtr(stripped, out bodyExpr)) return null;

            List<CandidateIssue> issues = new List<CandidateIssue>();
            if (BodyHasDocstring(fd.Body))
                issues.Add(new CandidateIssue("PYR3001", "Function has a docstring; the lambda form will lose it."));
            if (ArgsHaveAnnotations(fd.Args) || fd.Returns != null)
                issues.Add(new CandidateIssue("PYR3002", "Function has type annotations; the lambda form will strip them."));
            if (fd.DecoratorList.Count > 0)
                issues.Add(new CandidateIssue("PYR3003", "Function has decorators; lambdas cannot carry decorators."));
            if (issues.Count == 0) return null;

            DefToLambdaData d = new DefToLambdaData { FuncName = fd.Name, ArgsSignature = LambdaArgRenderer.RenderArguments(fd.Args, false), BodyExpr = bodyExpr };
            LambdaTransformCandidate c = LambdaPatternEmitter.EmitF2L1(d);
            LambdaPatternEmitter.ApplyRelaxation(c, issues, 65);
            return c;
        }

        // ---- L2F2-relax: multi-site anonymous lambdas ----

        private sealed class LambdaUse
        {
            public Lambda Lam;
            public Call Call;
            public string ArgHint;
        }

        private static void CollectFromCallRec(Expr expr, List<LambdaUse> uses)
        {
            if (expr == null) return;
            Call call = expr as Call;
            if (call == null) return;
            foreach (Expr a in call.Args)
            {
                Lambda lam = a as Lambda;
                if (lam != null) uses.Add(new LambdaUse { Lam = lam, Call = call, ArgHint = "" });
                else CollectFromCallRec(a, uses);
            }
            foreach (Keyword kw in call.Keywords)
            {
                Lambda lam = kw.Value as Lambda;
                if (lam != null) uses.Add(new LambdaUse { Lam = lam, Call = call, ArgHint = kw.Name });
                else CollectFromCallRec(kw.Value, uses);
            }
            CollectFromCallRec(call.Func, uses);
        }

        private static void CollectAnonLambdaUses(Module m, List<LambdaUse> uses)
        {
            foreach (Stmt stmt in m.Body)
            {
                Expr expr = null;
                Assign a = stmt as Assign;
                if (a != null) expr = a.Value;
                else { ExprStmt es = stmt as ExprStmt; if (es != null) expr = es.Value; else { Return r = stmt as Return; if (r != null) expr = r.Value; } }
                CollectFromCallRec(expr, uses);
            }
        }

        private static string LambdaSignature(Lambda lam)
        {
            return LambdaArgRenderer.RenderArguments(lam.Args, false) + " :: " + H.PyExprPrint(lam.Body);
        }

        private static List<LambdaTransformCandidate> ScanMultiSiteAnonLambdas(Module m)
        {
            List<LambdaUse> uses = new List<LambdaUse>();
            CollectAnonLambdaUses(m, uses);
            if (uses.Count < 2) return new List<LambdaTransformCandidate>();

            // Group by signature, preserving first-seen order for stable output.
            Dictionary<string, List<int>> groups = new Dictionary<string, List<int>>(System.StringComparer.Ordinal);
            List<string> order = new List<string>();
            for (int i = 0; i < uses.Count; i++)
            {
                string sig = LambdaSignature(uses[i].Lam);
                List<int> bucket;
                if (!groups.TryGetValue(sig, out bucket)) { bucket = new List<int>(); groups[sig] = bucket; order.Add(sig); }
                bucket.Add(i);
            }

            List<LambdaTransformCandidate> outList = new List<LambdaTransformCandidate>();
            int counter = 1;
            foreach (string sig in order)
            {
                List<int> idxs = groups[sig];
                if (idxs.Count < 2) continue;

                LambdaUse first = uses[idxs[0]];
                AnonLambdaToDefData d = new AnonLambdaToDefData();
                string hint = string.IsNullOrEmpty(first.ArgHint) ? "" : first.ArgHint;
                d.GeneratedName = hint.Length == 0
                    ? "__lambda_shared_" + counter.ToString()
                    : "__" + hint + "_shared_" + counter.ToString();
                counter++;
                d.ArgsSignature = LambdaArgRenderer.RenderArguments(first.Lam.Args, false);
                d.BodyExpr = H.PyExprPrint(first.Lam.Body);
                d.CallSiteReplacement = "# (multi-site lambda extracted to " + d.GeneratedName + ")  # apply manually at " + idxs.Count.ToString() + " call sites";

                LambdaTransformCandidate c = LambdaPatternEmitter.EmitL2F2(d);
                List<CandidateIssue> issues = new List<CandidateIssue>
                {
                    new CandidateIssue("PYR3004", "Same anonymous lambda referenced from " + idxs.Count.ToString() + " call sites; extracting to a single def requires updating each site."),
                };
                LambdaPatternEmitter.ApplyRelaxation(c, issues, 55);
                outList.Add(c);
            }
            return outList;
        }

        public static List<LambdaTransformCandidate> MatchAllRelaxed(Module m, string selectedSource)
        {
            List<LambdaTransformCandidate> outList = new List<LambdaTransformCandidate>();
            foreach (Stmt stmt in m.Body)
            {
                FunctionDef fd = stmt as FunctionDef;
                if (fd != null)
                {
                    LambdaTransformCandidate c = TryF2L1Relaxed(fd);
                    if (c != null) outList.Add(c);
                }
            }
            outList.AddRange(ScanMultiSiteAnonLambdas(m));
            return outList;
        }
    }
}
