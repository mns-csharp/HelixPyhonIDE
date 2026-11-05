namespace HelixPythonNamespace.Refactoring.LambdaTransform
{
    using System.Collections.Generic;
    using System.Text;
    using HelixPythonNamespace.VirtualCompiler.Ast;
    using H = HelixPythonNamespace.Refactoring.Vectorization.VecHelpers;

    /// <summary>Tier-1 strict matchers + module walker for function ↔ lambda. Port of the C++ <c>LambdaPatternMatcher</c>.</summary>
    public static class LambdaPatternMatcher
    {
        // ---- Per-pattern matchers ----

        public static DefToLambdaData TryMatch_F2L1(FunctionDef fd)
        {
            if (fd.DecoratorList.Count > 0) return null;
            if (fd.Returns != null) return null;
            if (fd.Args != null)
            {
                foreach (Arg a in fd.Args.Args)
                    if (a.Annotation != null) return null;
                if (fd.Args.Defaults.Count > 0) return null;
                if (fd.Args.VarArg != null) return null;
                if (fd.Args.KwArg != null) return null;
                if (fd.Args.KwOnlyArgs.Count > 0) return null;
                if (fd.Args.PosOnlyArgs.Count > 0) return null;
            }
            string body;
            if (!LambdaArgRenderer.IsSingleReturnBody(fd.Body, out body)) return null;
            return new DefToLambdaData { FuncName = fd.Name, ArgsSignature = LambdaArgRenderer.RenderArguments(fd.Args, false), BodyExpr = body };
        }

        public static DefToLambdaData TryMatch_F2L2(FunctionDef fd)
        {
            if (fd.DecoratorList.Count > 0) return null;
            if (fd.Returns != null) return null;
            bool hasExtras = false;
            if (fd.Args != null)
            {
                if (fd.Args.Defaults.Count > 0) hasExtras = true;
                if (fd.Args.VarArg != null) hasExtras = true;
                if (fd.Args.KwArg != null) hasExtras = true;
                if (fd.Args.KwOnlyArgs.Count > 0) hasExtras = true;
                if (fd.Args.PosOnlyArgs.Count > 0) hasExtras = true;
                foreach (Arg a in fd.Args.Args)
                    if (a.Annotation != null) return null;
            }
            if (!hasExtras) return null;
            string body;
            if (!LambdaArgRenderer.IsSingleReturnBody(fd.Body, out body)) return null;
            return new DefToLambdaData { FuncName = fd.Name, ArgsSignature = LambdaArgRenderer.RenderArguments(fd.Args, false), BodyExpr = body };
        }

        public static DefIfReturnTernaryData TryMatch_F2L3(FunctionDef fd)
        {
            if (fd.DecoratorList.Count > 0) return null;
            if (fd.Returns != null) return null;
            if (fd.Args != null)
            {
                foreach (Arg a in fd.Args.Args)
                    if (a.Annotation != null) return null;
            }
            string cond, trueExpr, falseExpr;
            if (!LambdaArgRenderer.IsIfReturnElseReturnBody(fd.Body, out cond, out trueExpr, out falseExpr)) return null;
            return new DefIfReturnTernaryData { FuncName = fd.Name, ArgsSignature = LambdaArgRenderer.RenderArguments(fd.Args, false), CondExpr = cond, TrueExpr = trueExpr, FalseExpr = falseExpr };
        }

        public static NamedLambdaToDefData TryMatch_L2F1(Assign a)
        {
            if (a.Targets.Count != 1) return null;
            Name target = a.Targets[0] as Name;
            if (target == null) return null;
            Lambda lam = a.Value as Lambda;
            if (lam == null) return null;
            return new NamedLambdaToDefData { FuncName = target.Id, ArgsSignature = LambdaArgRenderer.RenderArguments(lam.Args, false), BodyExpr = H.PyExprPrint(lam.Body) };
        }

        public static CurriedLambdaToDefData TryMatch_L2F3(Assign a)
        {
            if (a.Targets.Count != 1) return null;
            Name target = a.Targets[0] as Name;
            if (target == null) return null;
            Lambda outer = a.Value as Lambda;
            if (outer == null) return null;
            Lambda inner = outer.Body as Lambda;
            if (inner == null) return null;

            string innerText = "lambda " + LambdaArgRenderer.RenderArguments(inner.Args, false) + ": " + H.PyExprPrint(inner.Body);
            return new CurriedLambdaToDefData { FuncName = target.Id, OuterArgs = LambdaArgRenderer.RenderArguments(outer.Args, false), InnerLambdaText = innerText };
        }

        // ---- Module walkers for L2F2 (call-site scope) ----

        private static void VisitCallsRec(Expr expr, System.Action<Call> cb)
        {
            if (expr == null) return;
            Call call = expr as Call;
            if (call != null)
            {
                cb(call);
                VisitCallsRec(call.Func, cb);
                foreach (Expr a in call.Args) VisitCallsRec(a, cb);
                foreach (Keyword kw in call.Keywords) VisitCallsRec(kw.Value, cb);
            }
        }

        private static void VisitTopCalls(Module m, System.Action<Call> cb)
        {
            foreach (Stmt stmt in m.Body)
            {
                Expr expr = null;
                Assign assign = stmt as Assign;
                if (assign != null) expr = assign.Value;
                else { ExprStmt es = stmt as ExprStmt; if (es != null) expr = es.Value; else { Return ret = stmt as Return; if (ret != null) expr = ret.Value; } }
                VisitCallsRec(expr, cb);
            }
        }

        private static string RenderCall(Call call, Lambda lambdaToReplace, string replacementName)
        {
            StringBuilder outSb = new StringBuilder();
            outSb.Append(H.PyExprPrint(call.Func)).Append("(");
            bool first = true;
            foreach (Expr arg in call.Args)
            {
                if (!first) outSb.Append(", ");
                first = false;
                if (ReferenceEquals(arg, lambdaToReplace)) outSb.Append(replacementName);
                else outSb.Append(H.PyExprPrint(arg));
            }
            foreach (Keyword kw in call.Keywords)
            {
                if (!first) outSb.Append(", ");
                first = false;
                if (!string.IsNullOrEmpty(kw.Name)) outSb.Append(kw.Name).Append("=");
                else outSb.Append("**");
                if (ReferenceEquals(kw.Value, lambdaToReplace)) outSb.Append(replacementName);
                else outSb.Append(H.PyExprPrint(kw.Value));
            }
            outSb.Append(")");
            return outSb.ToString();
        }

        public static List<LambdaTransformCandidate> ScanAnonLambdas_L2F2(Module m)
        {
            List<LambdaTransformCandidate> outList = new List<LambdaTransformCandidate>();
            int counter = 1;
            VisitTopCalls(m, call =>
            {
                System.Action<Lambda, string> considerLambda = (lam, argHint) =>
                {
                    AnonLambdaToDefData d = new AnonLambdaToDefData();
                    d.GeneratedName = "__lambda_" + counter.ToString();
                    counter++;
                    if (!string.IsNullOrEmpty(argHint))
                        d.GeneratedName = "__" + argHint + "_" + (counter - 1).ToString();
                    d.ArgsSignature = LambdaArgRenderer.RenderArguments(lam.Args, false);
                    d.BodyExpr = H.PyExprPrint(lam.Body);
                    d.CallSiteReplacement = RenderCall(call, lam, d.GeneratedName);
                    outList.Add(LambdaPatternEmitter.EmitL2F2(d));
                };
                foreach (Expr a in call.Args)
                {
                    Lambda lam = a as Lambda;
                    if (lam != null) considerLambda(lam, "");
                }
                foreach (Keyword kw in call.Keywords)
                {
                    Lambda lam = kw.Value as Lambda;
                    if (lam != null) considerLambda(lam, kw.Name);
                }
            });
            return outList;
        }

        public static List<LambdaTransformCandidate> ScanTypedLambdas_L2F4(Module m, string selectedSource)
        {
            // Type-comment recovery requires raw-source line scanning; v1 stub (comments aren't in the AST).
            return new List<LambdaTransformCandidate>();
        }

        public static List<LambdaTransformCandidate> ScanNestedDefs_F2L4(Module m)
        {
            // Single-use nested-def detection requires intra-procedural reference counting; v1 stub.
            return new List<LambdaTransformCandidate>();
        }

        // ---- Top-level dispatcher ----

        public static List<LambdaTransformCandidate> MatchAll(Module m)
        {
            List<LambdaTransformCandidate> outList = new List<LambdaTransformCandidate>();
            foreach (Stmt stmt in m.Body)
            {
                FunctionDef fd = stmt as FunctionDef;
                if (fd != null)
                {
                    DefIfReturnTernaryData d3 = TryMatch_F2L3(fd);
                    if (d3 != null) { outList.Add(LambdaPatternEmitter.EmitF2L3(d3)); continue; }
                    DefToLambdaData d2 = TryMatch_F2L2(fd);
                    if (d2 != null) { outList.Add(LambdaPatternEmitter.EmitF2L2(d2)); continue; }
                    DefToLambdaData d1 = TryMatch_F2L1(fd);
                    if (d1 != null) { outList.Add(LambdaPatternEmitter.EmitF2L1(d1)); continue; }
                }
                else
                {
                    Assign a = stmt as Assign;
                    if (a != null)
                    {
                        CurriedLambdaToDefData c3 = TryMatch_L2F3(a);
                        if (c3 != null) { outList.Add(LambdaPatternEmitter.EmitL2F3(c3)); continue; }
                        NamedLambdaToDefData c1 = TryMatch_L2F1(a);
                        if (c1 != null) { outList.Add(LambdaPatternEmitter.EmitL2F1(c1)); continue; }
                    }
                }
            }
            outList.AddRange(ScanAnonLambdas_L2F2(m));
            return outList;
        }
    }
}
