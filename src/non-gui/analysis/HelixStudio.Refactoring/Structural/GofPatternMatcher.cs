namespace HelixPythonNamespace.Refactoring.Structural
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Refactoring;
    using HelixPythonNamespace.VirtualCompiler.Ast;
    using H = HelixPythonNamespace.Refactoring.Vectorization.VecHelpers;

    // MatchData for the GoF-pattern transforms. As in the vectorization engine,
    // these carry only pre-rendered strings (extracted from the AST via the
    // vectorization PyExprPrint helper) so the emitter never touches AST lifetimes.

    /// <summary>
    /// P1 Factory. The plain (un-patterned) shape is an if/elif create-function; the
    /// patterned shape is a registry factory class. Both directions share this class.
    /// </summary>
    public sealed class FactoryData
    {
        public string FuncName;     // plain side: e.g. "create_animal"
        public string ClassName;    // pattern side: e.g. "AnimalFactory"
        public string ParamName;    // dispatch parameter, e.g. "kind"
        public string RaiseText;    // rendered exception expr, e.g. "ValueError(kind)"
        // Ordered (key, ClassName) dispatch pairs. Key is the raw string literal value
        // (no surrounding quotes); the emitter re-quotes it.
        public List<CaseEntry> Cases = new List<CaseEntry>();
        // 1-based inclusive line range of the matched statement in the selection.
        public int StartLine;
        public int EndLine;
    }

    /// <summary>
    /// P6 Facade (near-identity). The plain shape is a free orchestration function whose
    /// body delegates to &gt;= 2 subsystem calls; the patterned shape wraps it in a
    /// <c>&lt;Name&gt;Facade</c> class as a single method (the body carried VERBATIM).
    /// </summary>
    public sealed class FacadeData
    {
        public string FuncName;             // plain side, e.g. "checkout"
        public string ClassName;            // facade side, e.g. "CheckoutFacade"
        public string ParamsText;           // free-fn params WITHOUT self, e.g. "cart, user"
        public string ReturnText = "";      // rendered "-> T" (with leading space) or "" if none
        public List<string> BodyLines = new List<string>();  // dedented to column 0, verbatim
        public int StartLine;
        public int EndLine;
    }

    /// <summary>
    /// P2 Strategy. The plain shape is an if/elif dispatch function; the patterned shape is
    /// a module-level strategy table (<c>{key: fn}</c>) plus a thin dispatch function.
    /// </summary>
    public sealed class StrategyData
    {
        public string FuncName;        // "execute"
        public string DictName;        // "_execute_strategies"
        public string KeyParam;        // first param — the selector, e.g. "mode"
        public string FullParamsText;  // full signature, e.g. "mode, x"
        public string CallArgsText;    // params after the selector, e.g. "x"
        public string RaiseText;       // rendered exception, e.g. "ValueError(mode)"
        public List<CaseEntry> Cases = new List<CaseEntry>();  // (key, fnName)
        public int StartLine;
        public int EndLine;
    }

    /// <summary>
    /// P5 Template Method. The plain shape is a class with a single skeleton method whose
    /// body is a sequence of <c>self.&lt;step&gt;()</c> primitive calls; the patterned shape
    /// declares the class <c>ABC</c> and adds an abstract stub for each primitive operation.
    /// </summary>
    public sealed class TemplateMethodData
    {
        public string ClassName;                            // "ReportBuilder"
        public List<string> TemplateLines = new List<string>();  // verbatim skeleton method
        public List<string> Steps = new List<string>();     // ordered primitive-operation names
        public int StartLine;
        public int EndLine;
    }

    /// <summary>
    /// P3 Observer. The plain shape is a concrete "subject" class; the patterned shape
    /// injects the canonical observer infrastructure above the original methods, which are
    /// carried VERBATIM. Lossy; carries STR-OBSERVER-LOSSY.
    /// </summary>
    public sealed class ObserverData
    {
        public string ClassName;
        public List<string> BodyLines = new List<string>();  // verbatim subject methods (non-infra body)
        public int StartLine;
        public int EndLine;
    }

    /// <summary>A wrapped-and-forwarded method used to generate P4 decorator delegations.</summary>
    public sealed class DecoMethod
    {
        public string Name;        // "run"
        public string DefParams;   // params after self, e.g. "x, y"
        public string CallArgs;    // bare arg names after self, e.g. "x, y"

        public DecoMethod() { }

        public DecoMethod(string name, string defParams, string callArgs)
        {
            Name = name;
            DefParams = defParams;
            CallArgs = callArgs;
        }
    }

    /// <summary>
    /// P4 Decorator. The plain shape is a concrete component class; the patterned shape keeps
    /// it VERBATIM and appends a sibling <c>&lt;Name&gt;Decorator</c> that wraps an instance
    /// and forwards each method. Lossy (STR-DECORATOR-LOSSY).
    /// </summary>
    public sealed class DecoratorData
    {
        public string ClassName;                            // "Service"
        public List<string> ClassLines = new List<string>();  // verbatim component class (header + body)
        public List<DecoMethod> Methods = new List<DecoMethod>();  // for generating delegations (forward only)
        public int StartLine;
        public int EndLine;
    }

    /// <summary>
    /// The GoF-pattern structural matchers. Port of the C++
    /// <c>helix::refactoring::structural</c> matcher free functions. Named <c>Gof*</c> to
    /// avoid colliding with the Vectorization and LambdaTransform pattern matchers.
    /// Each <c>TryMatch*</c> returns the extracted data or <c>null</c> on no-match.
    /// </summary>
    public static class GofPatternMatcher
    {
        // ---- shared local helpers (C++ anonymous-namespace functions) ----

        private static bool StartsWith(string s, string p)
        {
            return s.StartsWith(p, StringComparison.Ordinal);
        }

        private static bool EndsWith(string s, string p)
        {
            return s.EndsWith(p, StringComparison.Ordinal);
        }

        // Render plain positional params [startIdx, end), including annotations and
        // defaults, via PyExprPrint. Rejects pos-only/kw-only/*args/**kwargs and any
        // expression PyExprPrint cannot render so transforms only fire on shapes that
        // re-render identically. Returns null on rejection.
        private static string RenderPositionalParams(Arguments a, int startIdx)
        {
            if (a == null) return null;
            if (a.PosOnlyArgs.Count != 0 || a.KwOnlyArgs.Count != 0 ||
                a.VarArg != null || a.KwArg != null)
            {
                return null;
            }
            IReadOnlyList<Arg> args = a.Args;
            IReadOnlyList<Expr> defaults = a.Defaults;
            int firstDefault = args.Count - defaults.Count;
            string outStr = "";
            bool first = true;
            for (int i = startIdx; i < args.Count; i++)
            {
                if (!first) outStr += ", ";
                first = false;
                outStr += args[i].Name;
                if (args[i].Annotation != null)
                {
                    string ann = H.PyExprPrint(args[i].Annotation);
                    if (ann == "<unsupported>") return null;
                    outStr += ": " + ann;
                }
                if (i >= firstDefault)
                {
                    string def = H.PyExprPrint(defaults[i - firstDefault]);
                    if (def == "<unsupported>") return null;
                    outStr += (args[i].Annotation != null) ? " = " + def : "=" + def;
                }
            }
            return outStr;
        }

        // A statement that delegates to a call (the hallmark of a facade body):
        // `f(...)`, `x = f(...)`, `return f(...)`, or `x op= f(...)`.
        private static bool IsDelegatingCallStmt(Stmt s)
        {
            ExprStmt es = s as ExprStmt;
            if (es != null) return es.Value is Call;
            Assign asg = s as Assign;
            if (asg != null) return asg.Value is Call;
            Return rt = s as Return;
            if (rt != null) return rt.Value is Call;
            AugAssign au = s as AugAssign;
            if (au != null) return au.Value is Call;
            return false;
        }

        // `cls._registry` shape.
        private static bool IsClsRegistry(Expr e)
        {
            AttributeExpr attr = e as AttributeExpr;
            if (attr == null) return false;
            if (attr.Attr != "_registry") return false;
            return H.IsNameWithId(attr.Value, "cls");
        }

        // ---------------------------------------------------------------------------
        // P1 Factory.

        // Naming bijection: create_animal <-> AnimalFactory.
        public static string FactoryClassNameForFunc(string funcName)
        {
            string core = funcName;
            if (StartsWith(core, "create_")) core = core.Substring(7);
            else if (StartsWith(core, "make_")) core = core.Substring(5);
            if (core.Length == 0) core = funcName;
            string outStr = core;
            if (outStr.Length > 0) outStr = char.ToUpperInvariant(outStr[0]).ToString() + outStr.Substring(1);
            return outStr + "Factory";
        }

        public static string FactoryFuncNameForClass(string className)
        {
            string core = className;
            if (EndsWith(core, "Factory")) core = core.Substring(0, core.Length - 7);
            if (core.Length == 0) core = className;
            if (core.Length > 0) core = char.ToLowerInvariant(core[0]).ToString() + core.Substring(1);
            return "create_" + core;
        }

        public static FactoryData TryMatchFactoryForward(Stmt s)
        {
            FunctionDef fn = s as FunctionDef;
            if (fn == null) return null;

            string name = fn.Name;
            if (!StartsWith(name, "create_") && !StartsWith(name, "make_")) return null;

            Arguments args = fn.Args;
            if (args == null) return null;
            if (args.PosOnlyArgs.Count != 0 || args.Args.Count != 1 ||
                args.KwOnlyArgs.Count != 0 || args.VarArg != null || args.KwArg != null)
            {
                return null;
            }

            IReadOnlyList<Stmt> body = fn.Body;
            if (body.Count != 2) return null;
            If topIf = body[0] as If;
            Raise raise = body[1] as Raise;
            if (topIf == null || raise == null || raise.Exc == null) return null;

            FactoryData d = new FactoryData();
            d.ParamName = args.Args[0].Name;
            d.FuncName = name;
            d.ClassName = FactoryClassNameForFunc(name);
            d.RaiseText = H.PyExprPrint(raise.Exc);

            If cur = topIf;
            while (cur != null)
            {
                Compare cmp = cur.Test as Compare;
                if (cmp == null) return null;
                if (cmp.Ops.Count != 1 || cmp.Ops[0] != CmpOp.Eq) return null;
                if (!H.IsNameWithId(cmp.Left, d.ParamName)) return null;
                if (cmp.Comparators.Count != 1) return null;
                Constant keyConst = cmp.Comparators[0] as Constant;
                if (keyConst == null || !(keyConst.Value is string)) return null;

                if (cur.Body.Count != 1) return null;
                Return ret = cur.Body[0] as Return;
                if (ret == null || ret.Value == null) return null;
                Call call = ret.Value as Call;
                if (call == null || call.Args.Count != 0 || call.Keywords.Count != 0) return null;
                Name clsName = call.Func as Name;
                if (clsName == null) return null;

                d.Cases.Add(new CaseEntry((string)keyConst.Value, clsName.Id));

                IReadOnlyList<Stmt> orelse = cur.OrElse;
                if (orelse.Count == 0) break;
                if (orelse.Count != 1) return null;
                If nextIf = orelse[0] as If;
                if (nextIf == null) return null;
                cur = nextIf;
            }
            if (d.Cases.Count == 0) return null;

            d.StartLine = fn.Span.Start.Line;
            d.EndLine = fn.Span.End.Line;
            return d;
        }

        public static FactoryData TryMatchFactoryReverse(Stmt s)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null) return null;
            string cname = cd.Name;
            if (!EndsWith(cname, "Factory") || cname.Length <= 7) return null;

            Assign regAssign = null;
            FunctionDef createFn = null;
            foreach (Stmt bs in cd.Body)
            {
                Assign asg = bs as Assign;
                if (asg != null)
                {
                    if (asg.Targets.Count == 1)
                    {
                        Name tn = asg.Targets[0] as Name;
                        if (tn != null && tn.Id == "_registry") regAssign = asg;
                    }
                }
                else
                {
                    FunctionDef f = bs as FunctionDef;
                    if (f != null && f.Name == "create") createFn = f;
                }
            }
            if (regAssign == null || createFn == null) return null;

            Dict dict = regAssign.Value as Dict;
            if (dict == null) return null;

            FactoryData d = new FactoryData();
            d.ClassName = cname;
            d.FuncName = FactoryFuncNameForClass(cname);

            IReadOnlyList<Expr> keys = dict.Keys;
            IReadOnlyList<Expr> vals = dict.Values;
            if (keys.Count != vals.Count || keys.Count == 0) return null;
            for (int i = 0; i < keys.Count; i++)
            {
                Constant kc = keys[i] as Constant;
                if (kc == null || !(kc.Value is string)) return null;
                Name vn = vals[i] as Name;
                if (vn == null) return null;
                d.Cases.Add(new CaseEntry((string)kc.Value, vn.Id));
            }

            bool hasClassmethod = false;
            foreach (Expr dec in createFn.DecoratorList)
            {
                if (H.IsNameWithId(dec, "classmethod")) hasClassmethod = true;
            }
            if (!hasClassmethod) return null;

            Arguments a = createFn.Args;
            if (a == null || a.Args.Count != 2 || a.PosOnlyArgs.Count != 0 ||
                a.KwOnlyArgs.Count != 0 || a.VarArg != null || a.KwArg != null)
            {
                return null;
            }
            if (a.Args[0].Name != "cls") return null;
            d.ParamName = a.Args[1].Name;

            IReadOnlyList<Stmt> cbody = createFn.Body;
            if (cbody.Count != 2) return null;
            If guardIf = cbody[0] as If;
            Return ret2 = cbody[1] as Return;
            if (guardIf == null || ret2 == null) return null;

            Compare cmp2 = guardIf.Test as Compare;
            if (cmp2 == null || cmp2.Ops.Count != 1 || cmp2.Ops[0] != CmpOp.NotIn) return null;
            if (!H.IsNameWithId(cmp2.Left, d.ParamName)) return null;
            if (cmp2.Comparators.Count != 1 || !IsClsRegistry(cmp2.Comparators[0])) return null;
            if (guardIf.Body.Count != 1 || guardIf.OrElse.Count != 0) return null;
            Raise raise2 = guardIf.Body[0] as Raise;
            if (raise2 == null || raise2.Exc == null) return null;
            d.RaiseText = H.PyExprPrint(raise2.Exc);

            Call call2 = ret2.Value as Call;
            if (call2 == null || call2.Args.Count != 0 || call2.Keywords.Count != 0) return null;
            Subscript sub = call2.Func as Subscript;
            if (sub == null || !IsClsRegistry(sub.Value)) return null;
            if (!H.IsNameWithId(sub.Slice, d.ParamName)) return null;

            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        // ---------------------------------------------------------------------------
        // P2 Strategy.

        public static string StrategyDictNameForFunc(string funcName)
        {
            return "_" + funcName + "_strategies";
        }

        public static string StrategyFuncNameForDict(string dictName)
        {
            string core = dictName;
            if (StartsWith(core, "_")) core = core.Substring(1);
            if (EndsWith(core, "_strategies")) core = core.Substring(0, core.Length - 11);
            return core;
        }

        // `return <fn>(<args>)` where <fn> is a bare Name and <args> are exactly the
        // names `expectedArgs` in order. Fills fnName on success.
        private static bool IsStrategyReturn(Stmt s, IList<string> expectedArgs, out string fnName)
        {
            fnName = null;
            Return ret = s as Return;
            if (ret == null) return false;
            Call call = ret.Value as Call;
            if (call == null || call.Keywords.Count != 0) return false;
            Name fn = call.Func as Name;
            if (fn == null) return false;
            if (call.Args.Count != expectedArgs.Count) return false;
            for (int i = 0; i < expectedArgs.Count; i++)
            {
                if (!H.IsNameWithId(call.Args[i], expectedArgs[i])) return false;
            }
            fnName = fn.Id;
            return true;
        }

        public static StrategyData TryMatchStrategyIntroduce(Stmt s)
        {
            FunctionDef fn = s as FunctionDef;
            if (fn == null || fn.DecoratorList.Count != 0) return null;

            string fullParams = RenderPositionalParams(fn.Args, 0);
            if (fullParams == null) return null;
            IReadOnlyList<Arg> args = fn.Args.Args;
            if (args.Count < 2) return null;  // selector + >=1 strategy arg
            string keyParam = args[0].Name;
            List<string> strategyArgs = new List<string>();
            for (int i = 1; i < args.Count; i++) strategyArgs.Add(args[i].Name);

            IReadOnlyList<Stmt> body = fn.Body;
            if (body.Count != 2) return null;
            If topIf = body[0] as If;
            Raise raise = body[1] as Raise;
            if (topIf == null || raise == null || raise.Exc == null) return null;

            StrategyData d = new StrategyData();
            d.FuncName = fn.Name;
            d.DictName = StrategyDictNameForFunc(fn.Name);
            d.KeyParam = keyParam;
            d.FullParamsText = fullParams;
            string callArgs = RenderPositionalParams(fn.Args, 1);
            if (callArgs == null) return null;
            d.CallArgsText = callArgs;
            d.RaiseText = H.PyExprPrint(raise.Exc);

            If cur = topIf;
            while (cur != null)
            {
                Compare cmp = cur.Test as Compare;
                if (cmp == null || cmp.Ops.Count != 1 || cmp.Ops[0] != CmpOp.Eq)
                {
                    return null;
                }
                if (!H.IsNameWithId(cmp.Left, keyParam) || cmp.Comparators.Count != 1)
                {
                    return null;
                }
                Constant keyConst = cmp.Comparators[0] as Constant;
                if (keyConst == null || !(keyConst.Value is string)) return null;
                if (cur.Body.Count != 1) return null;
                string fnName;
                if (!IsStrategyReturn(cur.Body[0], strategyArgs, out fnName)) return null;
                d.Cases.Add(new CaseEntry((string)keyConst.Value, fnName));

                IReadOnlyList<Stmt> orelse = cur.OrElse;
                if (orelse.Count == 0) break;
                if (orelse.Count != 1) return null;
                cur = orelse[0] as If;
                if (cur == null) return null;
            }
            if (d.Cases.Count == 0) return null;

            d.StartLine = fn.Span.Start.Line;
            d.EndLine = fn.Span.End.Line;
            return d;
        }

        public static StrategyData TryMatchStrategyRemove(IReadOnlyList<Stmt> body, int startIdx)
        {
            if (startIdx + 1 >= body.Count) return null;
            Assign assign = body[startIdx] as Assign;
            FunctionDef fn = body[startIdx + 1] as FunctionDef;
            if (assign == null || fn == null || fn.DecoratorList.Count != 0) return null;
            if (assign.Targets.Count != 1) return null;
            Name tgt = assign.Targets[0] as Name;
            if (tgt == null || !EndsWith(tgt.Id, "_strategies") || !StartsWith(tgt.Id, "_"))
            {
                return null;
            }
            if (StrategyFuncNameForDict(tgt.Id) != fn.Name) return null;

            Dict dict = assign.Value as Dict;
            if (dict == null || dict.Keys.Count == 0) return null;

            string fullParams = RenderPositionalParams(fn.Args, 0);
            if (fullParams == null) return null;
            IReadOnlyList<Arg> args = fn.Args.Args;
            if (args.Count < 2) return null;
            string keyParam = args[0].Name;
            List<string> strategyArgs = new List<string>();
            for (int i = 1; i < args.Count; i++) strategyArgs.Add(args[i].Name);

            // Body: [if key not in <dict>: raise E, return <dict>[key](args)]
            IReadOnlyList<Stmt> fb = fn.Body;
            if (fb.Count != 2) return null;
            If guard = fb[0] as If;
            Return ret = fb[1] as Return;
            if (guard == null || ret == null) return null;
            Compare cmp = guard.Test as Compare;
            if (cmp == null || cmp.Ops.Count != 1 || cmp.Ops[0] != CmpOp.NotIn) return null;
            if (!H.IsNameWithId(cmp.Left, keyParam) || cmp.Comparators.Count != 1) return null;
            if (!H.IsNameWithId(cmp.Comparators[0], tgt.Id)) return null;
            if (guard.Body.Count != 1 || guard.OrElse.Count != 0) return null;
            Raise raise = guard.Body[0] as Raise;
            if (raise == null || raise.Exc == null) return null;

            Call call = ret.Value as Call;
            if (call == null || call.Keywords.Count != 0) return null;
            Subscript sub = call.Func as Subscript;
            if (sub == null || !H.IsNameWithId(sub.Value, tgt.Id)) return null;
            if (!H.IsNameWithId(sub.Slice, keyParam)) return null;
            if (call.Args.Count != strategyArgs.Count) return null;
            for (int i = 0; i < strategyArgs.Count; i++)
            {
                if (!H.IsNameWithId(call.Args[i], strategyArgs[i])) return null;
            }

            StrategyData d = new StrategyData();
            d.FuncName = fn.Name;
            d.DictName = tgt.Id;
            d.KeyParam = keyParam;
            d.FullParamsText = fullParams;
            string callArgs = RenderPositionalParams(fn.Args, 1);
            if (callArgs == null) return null;
            d.CallArgsText = callArgs;
            d.RaiseText = H.PyExprPrint(raise.Exc);
            for (int i = 0; i < dict.Keys.Count; i++)
            {
                Constant kc = dict.Keys[i] as Constant;
                Name vn = dict.Values[i] as Name;
                if (kc == null || !(kc.Value is string) || vn == null) return null;
                d.Cases.Add(new CaseEntry((string)kc.Value, vn.Id));
            }

            d.StartLine = assign.Span.Start.Line;
            d.EndLine = fn.Span.End.Line;
            return d;
        }

        // ---------------------------------------------------------------------------
        // P5 Template Method.

        // `self.<step>()` zero-arg call as an ExprStmt. Fills step on success.
        private static bool IsSelfStepCall(Stmt s, out string step)
        {
            step = null;
            ExprStmt es = s as ExprStmt;
            if (es == null) return false;
            Call call = es.Value as Call;
            if (call == null || call.Args.Count != 0 || call.Keywords.Count != 0) return false;
            AttributeExpr attr = call.Func as AttributeExpr;
            if (attr == null || !H.IsNameWithId(attr.Value, "self")) return false;
            step = attr.Attr;
            return true;
        }

        // Sequence of >= 2 distinct `self.<step>()` calls. Fills steps in order.
        private static bool IsTemplateBody(IReadOnlyList<Stmt> body, List<string> steps)
        {
            steps.Clear();
            if (body.Count < 2) return false;
            foreach (Stmt bs in body)
            {
                string step;
                if (!IsSelfStepCall(bs, out step)) return false;
                foreach (string existing in steps) if (existing == step) return false;  // distinct
                steps.Add(step);
            }
            return true;
        }

        // Plain concrete `def name(self):` (single self param, no *args/**kwargs).
        private static bool IsSelfOnlyMethod(FunctionDef fn)
        {
            if (fn == null) return false;
            Arguments a = fn.Args;
            return a != null && a.Args.Count == 1 && a.Args[0].Name == "self" &&
                   a.PosOnlyArgs.Count == 0 && a.KwOnlyArgs.Count == 0 &&
                   a.VarArg == null && a.KwArg == null;
        }

        // `@abstractmethod def <step>(self): ...` stub. Fills step.
        private static bool IsAbstractStub(Stmt s, out string step)
        {
            step = null;
            FunctionDef fn = s as FunctionDef;
            if (fn == null || fn.DecoratorList.Count != 1) return false;
            if (!H.IsNameWithId(fn.DecoratorList[0], "abstractmethod")) return false;
            if (!IsSelfOnlyMethod(fn)) return false;
            if (fn.Body.Count != 1) return false;
            Stmt b0 = fn.Body[0];
            bool isEllipsis = false;
            ExprStmt es = b0 as ExprStmt;
            if (es != null)
            {
                Constant k = es.Value as Constant;
                if (k != null) isEllipsis = k.Value is ConstantEllipsis;
            }
            if (!isEllipsis && !(b0 is Pass)) return false;
            step = fn.Name;
            return true;
        }

        private static List<string> SliceMethodVerbatim(string normalizedSource, FunctionDef fn)
        {
            List<string> outLines = new List<string>();
            outLines.Add(BodySlicer.LineText(normalizedSource, fn.Span.Start.Line));
            List<string> body = BodySlicer.SliceIndentedBody(
                normalizedSource, fn.Span.Start.Line, fn.Body[0].Span.Start.Line);
            foreach (string l in body) outLines.Add(l);
            return outLines;
        }

        public static TemplateMethodData TryMatchTemplateIntroduce(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null) return null;
            if (cd.Bases.Count != 0 || cd.Keywords.Count != 0 || cd.DecoratorList.Count != 0)
            {
                return null;
            }
            if (cd.Body.Count != 1) return null;
            FunctionDef fn = cd.Body[0] as FunctionDef;
            if (!IsSelfOnlyMethod(fn) || fn.DecoratorList.Count != 0) return null;

            List<string> steps = new List<string>();
            if (!IsTemplateBody(fn.Body, steps)) return null;

            TemplateMethodData d = new TemplateMethodData();
            d.ClassName = cd.Name;
            d.TemplateLines = SliceMethodVerbatim(normalizedSource, fn);
            d.Steps = steps;
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        public static TemplateMethodData TryMatchTemplateRemove(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null) return null;
            if (cd.Bases.Count != 1 || cd.Keywords.Count != 0 || cd.DecoratorList.Count != 0)
            {
                return null;
            }
            if (!H.IsNameWithId(cd.Bases[0], "ABC")) return null;
            if (cd.Body.Count < 3) return null;  // skeleton + >= 2 stubs

            FunctionDef fn = cd.Body[0] as FunctionDef;
            if (!IsSelfOnlyMethod(fn) || fn.DecoratorList.Count != 0) return null;
            List<string> steps = new List<string>();
            if (!IsTemplateBody(fn.Body, steps)) return null;

            List<string> stubNames = new List<string>();
            for (int i = 1; i < cd.Body.Count; i++)
            {
                string step;
                if (!IsAbstractStub(cd.Body[i], out step)) return null;
                stubNames.Add(step);
            }
            // The stub set must equal the called-step set.
            if (stubNames.Count != steps.Count) return null;
            foreach (string st in steps)
            {
                if (!stubNames.Contains(st)) return null;
            }

            TemplateMethodData d = new TemplateMethodData();
            d.ClassName = cd.Name;
            d.TemplateLines = SliceMethodVerbatim(normalizedSource, fn);
            d.Steps = steps;
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        // ---------------------------------------------------------------------------
        // P3 Observer.

        private static bool ClassHasMethodNamed(ClassDef cd, string name)
        {
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = bs as FunctionDef;
                if (fn != null && fn.Name == name) return true;
            }
            return false;
        }

        private static bool AllSelfMethodsCd(ClassDef cd)
        {
            if (cd.Body.Count == 0) return false;
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = bs as FunctionDef;
                if (fn == null) return false;
                Arguments a = fn.Args;
                if (a == null || a.Args.Count == 0 || a.PosOnlyArgs.Count != 0) return false;
                if (a.Args[0].Name != "self") return false;
            }
            return true;
        }

        // Slice the whole class suite body verbatim (all members), from the first
        // member onward, indentation-based.
        private static List<string> SliceClassBodyVerbatim(string normalizedSource, ClassDef cd, int fromMember)
        {
            if (fromMember >= cd.Body.Count) return new List<string>();
            return BodySlicer.SliceIndentedBody(
                normalizedSource, cd.Span.Start.Line, cd.Body[fromMember].Span.Start.Line);
        }

        private static bool IsPlainClass(ClassDef cd)
        {
            return cd.Bases.Count == 0 && cd.Keywords.Count == 0 && cd.DecoratorList.Count == 0;
        }

        public static ObserverData TryMatchObserverIntroduce(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null || !IsPlainClass(cd)) return null;
            if (EndsWith(cd.Name, "Protocol") || EndsWith(cd.Name, "Factory") ||
                EndsWith(cd.Name, "Facade") || EndsWith(cd.Name, "Decorator"))
            {
                return null;
            }
            if (!AllSelfMethodsCd(cd)) return null;
            foreach (string n in new[] { "__init__", "subscribe", "unsubscribe", "notify" })
            {
                if (ClassHasMethodNamed(cd, n)) return null;
            }

            ObserverData d = new ObserverData();
            d.ClassName = cd.Name;
            d.BodyLines = SliceClassBodyVerbatim(normalizedSource, cd, 0);
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        public static ObserverData TryMatchObserverRemove(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null || !IsPlainClass(cd)) return null;
            if (cd.Body.Count < 5) return null;  // 4 infra + >= 1 subject method
            string[] infra = new[] { "__init__", "subscribe", "unsubscribe", "notify" };
            for (int i = 0; i < 4; i++)
            {
                FunctionDef fn = cd.Body[i] as FunctionDef;
                if (fn == null || fn.Name != infra[i]) return null;
            }

            ObserverData d = new ObserverData();
            d.ClassName = cd.Name;
            d.BodyLines = SliceClassBodyVerbatim(normalizedSource, cd, 4);  // subject methods only
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        // ---------------------------------------------------------------------------
        // P4 Decorator.

        // Lift "class X:" header + verbatim body as a self-contained block.
        private static List<string> SliceWholeClass(string normalizedSource, ClassDef cd)
        {
            List<string> outLines = new List<string>();
            outLines.Add(BodySlicer.LineText(normalizedSource, cd.Span.Start.Line));
            foreach (string l in SliceClassBodyVerbatim(normalizedSource, cd, 0)) outLines.Add(l);
            return outLines;
        }

        private static bool IsDecoratableName(string n)
        {
            return !(EndsWith(n, "Decorator") || EndsWith(n, "Protocol") ||
                     EndsWith(n, "Factory") || EndsWith(n, "Facade"));
        }

        public static DecoratorData TryMatchDecoratorIntroduce(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null || !IsPlainClass(cd)) return null;
            if (!IsDecoratableName(cd.Name)) return null;
            if (!AllSelfMethodsCd(cd)) return null;
            if (ClassHasMethodNamed(cd, "__init__")) return null;

            DecoratorData d = new DecoratorData();
            d.ClassName = cd.Name;
            foreach (Stmt bs in cd.Body)
            {
                FunctionDef fn = bs as FunctionDef;
                if (fn == null || fn.DecoratorList.Count != 0) return null;
                string defParams = RenderPositionalParams(fn.Args, 1);
                if (defParams == null) return null;
                string callArgs = "";
                IReadOnlyList<Arg> args = fn.Args.Args;
                for (int i = 1; i < args.Count; i++)
                {
                    if (i > 1) callArgs += ", ";
                    callArgs += args[i].Name;
                }
                d.Methods.Add(new DecoMethod(fn.Name, defParams, callArgs));
            }
            if (d.Methods.Count == 0) return null;

            d.ClassLines = SliceWholeClass(normalizedSource, cd);
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }

        public static DecoratorData TryMatchDecoratorRemove(IReadOnlyList<Stmt> body, int startIdx, string normalizedSource)
        {
            if (startIdx + 1 >= body.Count) return null;
            ClassDef comp = body[startIdx] as ClassDef;
            ClassDef deco = body[startIdx + 1] as ClassDef;
            if (comp == null || deco == null) return null;
            if (deco.Name != comp.Name + "Decorator") return null;

            DecoratorData d = new DecoratorData();
            d.ClassName = comp.Name;
            d.ClassLines = SliceWholeClass(normalizedSource, comp);
            d.StartLine = comp.Span.Start.Line;
            d.EndLine = deco.Span.End.Line;
            return d;
        }

        // ---------------------------------------------------------------------------
        // P6 Facade.

        public static string FacadeClassNameForFunc(string funcName)
        {
            string outStr = funcName;
            if (outStr.Length > 0) outStr = char.ToUpperInvariant(outStr[0]).ToString() + outStr.Substring(1);
            return outStr + "Facade";
        }

        public static string FacadeFuncNameForClass(string className)
        {
            string core = className;
            if (EndsWith(core, "Facade")) core = core.Substring(0, core.Length - 6);
            if (core.Length > 0) core = char.ToLowerInvariant(core[0]).ToString() + core.Substring(1);
            return core;
        }

        public static FacadeData TryMatchFacadeIntroduce(Stmt s, string normalizedSource)
        {
            FunctionDef fn = s as FunctionDef;
            if (fn == null) return null;
            if (fn.DecoratorList.Count != 0) return null;
            // Avoid overlap with P1 (create_/make_) and O2 (make_) factory shapes.
            if (StartsWith(fn.Name, "create_") || StartsWith(fn.Name, "make_")) return null;

            Arguments a = fn.Args;
            if (a == null) return null;
            // A free function: must NOT already be a method (no `self` first param).
            if (a.Args.Count != 0 && a.Args[0].Name == "self") return null;
            string paramsText = RenderPositionalParams(a, 0);
            if (paramsText == null) return null;

            // Facade gate: the body must delegate to >= 2 subsystem calls.
            IReadOnlyList<Stmt> body = fn.Body;
            int calls = 0;
            foreach (Stmt bs in body)
            {
                if (IsDelegatingCallStmt(bs)) ++calls;
            }
            if (calls < 2) return null;

            FacadeData d = new FacadeData();
            d.FuncName = fn.Name;
            d.ClassName = FacadeClassNameForFunc(fn.Name);
            d.ParamsText = paramsText;
            // Preserve the return annotation (-> T) so the facade round-trips exactly.
            // If it cannot be rendered, reject the candidate rather than lose it.
            if (fn.Returns != null)
            {
                string rann = H.PyExprPrint(fn.Returns);
                if (rann == "<unsupported>") return null;
                d.ReturnText = " -> " + rann;
            }
            List<string> lines = BodySlicer.SliceIndentedBody(
                normalizedSource, fn.Span.Start.Line, body[0].Span.Start.Line);
            d.BodyLines = BodySlicer.Dedent(lines, BodySlicer.CommonIndentWidth(lines));
            d.StartLine = fn.Span.Start.Line;
            d.EndLine = fn.Span.End.Line;
            return d;
        }

        public static FacadeData TryMatchFacadeRemove(Stmt s, string normalizedSource)
        {
            ClassDef cd = s as ClassDef;
            if (cd == null) return null;
            if (cd.Bases.Count != 0 || cd.Keywords.Count != 0 || cd.DecoratorList.Count != 0)
            {
                return null;
            }
            if (!EndsWith(cd.Name, "Facade") || cd.Name.Length <= 6) return null;
            if (cd.Body.Count != 1) return null;
            FunctionDef fn = cd.Body[0] as FunctionDef;
            if (fn == null || fn.DecoratorList.Count != 0) return null;
            if (fn.Name != FacadeFuncNameForClass(cd.Name)) return null;

            Arguments a = fn.Args;
            if (a == null || a.Args.Count == 0 || a.Args[0].Name != "self") return null;
            string paramsText = RenderPositionalParams(a, 1);  // drop self
            if (paramsText == null) return null;

            FacadeData d = new FacadeData();
            d.ClassName = cd.Name;
            d.FuncName = fn.Name;
            d.ParamsText = paramsText;
            if (fn.Returns != null)
            {
                string rann = H.PyExprPrint(fn.Returns);
                if (rann == "<unsupported>") return null;
                d.ReturnText = " -> " + rann;
            }
            List<string> lines = BodySlicer.SliceIndentedBody(
                normalizedSource, fn.Span.Start.Line, fn.Body[0].Span.Start.Line);
            d.BodyLines = BodySlicer.Dedent(lines, BodySlicer.CommonIndentWidth(lines));
            d.StartLine = cd.Span.Start.Line;
            d.EndLine = cd.Span.End.Line;
            return d;
        }
    }
}
