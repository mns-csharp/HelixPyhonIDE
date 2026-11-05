namespace HelixPythonNamespace.DeadCode
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Text;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Per-module AST walk: collects loaded names + accessed attributes, top-level
    /// class defs, protocol-class detection, unreachable-code findings, and per-function
    /// unused locals/params. Port of the C++ dead-code <c>Walker</c>.
    /// </summary>
    internal sealed class DeadWalker
    {
        private readonly ModuleScan _scan;
        private int _classDepth;

        public DeadWalker(ModuleScan scan)
        {
            _scan = scan;
        }

        public void WalkModule(Module m)
        {
            foreach (Stmt s in m.Body)
            {
                if (s is ClassDef cd)
                {
                    _scan.TopLevelClasses[cd.Name] = cd;
                    if (DeadLive.ClassHasAbstractBase(cd))
                    {
                        _scan.ProtocolClassNames.Add(cd.Name);
                    }
                }
            }
            WalkStmtList(m.Body, false);
        }

        private void WalkStmtList(IReadOnlyList<Stmt> body, bool flagUnreachable)
        {
            bool seenTerminator = false;
            bool reportedHere = false;
            foreach (Stmt s in body)
            {
                if (s == null) continue;
                if (flagUnreachable && seenTerminator && !reportedHere)
                {
                    _scan.Unreachable.Add(new DeadCodeFinding
                    {
                        Kind = DeadCodeKind.UnreachableCode,
                        FilePath = _scan.FilePath,
                        Line = s.Span.Start.Line,
                        Column = s.Span.Start.Column,
                        Message = "Statement is unreachable (preceded by return/raise/break/continue).",
                    });
                    reportedHere = true;
                }
                WalkStmt(s);
                if (DeadLive.IsTerminator(s)) seenTerminator = true;
            }
        }

        private void WalkStmt(Stmt s)
        {
            if (s == null) return;

            if (s is FunctionDef fd)
            {
                foreach (Expr d in fd.DecoratorList) WalkExpr(d);
                WalkTypeParams(fd.TypeParams);
                WalkArgs(fd.Args);
                if (fd.Returns != null) WalkExpr(fd.Returns);
                WalkStmtList(fd.Body, true);
                AnalyzeFunctionLocals(fd.Body);
                if (_classDepth == 0) AnalyzeFunctionParams(fd.Args, fd.Body);
                return;
            }
            if (s is AsyncFunctionDef afd)
            {
                foreach (Expr d in afd.DecoratorList) WalkExpr(d);
                WalkTypeParams(afd.TypeParams);
                WalkArgs(afd.Args);
                if (afd.Returns != null) WalkExpr(afd.Returns);
                WalkStmtList(afd.Body, true);
                AnalyzeFunctionLocals(afd.Body);
                if (_classDepth == 0) AnalyzeFunctionParams(afd.Args, afd.Body);
                return;
            }
            if (s is ClassDef cd)
            {
                foreach (Expr d in cd.DecoratorList) WalkExpr(d);
                WalkTypeParams(cd.TypeParams);
                foreach (Expr b in cd.Bases) WalkExpr(b);
                foreach (Keyword kw in cd.Keywords) if (kw != null && kw.Value != null) WalkExpr(kw.Value);
                _classDepth++;
                WalkStmtList(cd.Body, false);
                _classDepth--;
                return;
            }
            if (s is Assign a)
            {
                foreach (Expr t in a.Targets) WalkExpr(t);
                WalkExpr(a.Value);
                return;
            }
            if (s is AugAssign aug) { WalkExpr(aug.Target); WalkExpr(aug.Value); return; }
            if (s is AnnAssign an)
            {
                if (an.Target != null) WalkExpr(an.Target);
                if (an.Annotation != null) WalkExpr(an.Annotation);
                if (an.Value != null) WalkExpr(an.Value);
                return;
            }
            if (s is Return r) { if (r.Value != null) WalkExpr(r.Value); return; }
            if (s is Raise rz) { if (rz.Exc != null) WalkExpr(rz.Exc); if (rz.Cause != null) WalkExpr(rz.Cause); return; }
            if (s is Delete del) { foreach (Expr t in del.Targets) WalkExpr(t); return; }
            if (s is ExprStmt es) { WalkExpr(es.Value); return; }
            if (s is If iff)
            {
                WalkExpr(iff.Test);
                int t = DeadLive.LiteralTruthiness(iff.Test);
                if (t == 0) FlagConstantBranch(iff.Body);
                if (t == 1) FlagConstantBranch(iff.OrElse);
                WalkStmtList(iff.Body, true);
                WalkStmtList(iff.OrElse, true);
                return;
            }
            if (s is While w)
            {
                WalkExpr(w.Test);
                WalkStmtList(w.Body, true);
                WalkStmtList(w.OrElse, true);
                return;
            }
            if (s is For f)
            {
                WalkExpr(f.Target); WalkExpr(f.Iter);
                WalkStmtList(f.Body, true);
                WalkStmtList(f.OrElse, true);
                return;
            }
            if (s is AsyncFor af)
            {
                WalkExpr(af.Target); WalkExpr(af.Iter);
                WalkStmtList(af.Body, true);
                WalkStmtList(af.OrElse, true);
                return;
            }
            if (s is With wi)
            {
                foreach (WithItem it in wi.Items)
                {
                    if (it.ContextExpr != null) WalkExpr(it.ContextExpr);
                    if (it.OptionalVars != null) WalkExpr(it.OptionalVars);
                }
                WalkStmtList(wi.Body, true);
                return;
            }
            if (s is AsyncWith aw)
            {
                foreach (WithItem it in aw.Items)
                {
                    if (it.ContextExpr != null) WalkExpr(it.ContextExpr);
                    if (it.OptionalVars != null) WalkExpr(it.OptionalVars);
                }
                WalkStmtList(aw.Body, true);
                return;
            }
            if (s is Try tr)
            {
                WalkStmtList(tr.Body, true);
                foreach (ExceptHandler h in tr.Handlers)
                {
                    if (h.Type != null) WalkExpr(h.Type);
                    WalkStmtList(h.Body, true);
                }
                WalkStmtList(tr.OrElse, true);
                WalkStmtList(tr.FinalBody, true);
                return;
            }
            if (s is TryStar trs)
            {
                WalkStmtList(trs.Body, true);
                foreach (ExceptHandler h in trs.Handlers)
                {
                    if (h.Type != null) WalkExpr(h.Type);
                    WalkStmtList(h.Body, true);
                }
                WalkStmtList(trs.OrElse, true);
                WalkStmtList(trs.FinalBody, true);
                return;
            }
            if (s is Match ma)
            {
                WalkExpr(ma.Subject);
                foreach (MatchCase c in ma.Cases)
                {
                    WalkPattern(c.Pattern);
                    if (c.Guard != null) WalkExpr(c.Guard);
                    WalkStmtList(c.Body, true);
                }
                return;
            }
            if (s is Assert ast)
            {
                if (ast.Test != null) WalkExpr(ast.Test);
                if (ast.Msg != null) WalkExpr(ast.Msg);
                return;
            }
            if (s is Import || s is ImportFrom)
            {
                ImportFrom imf = s as ImportFrom;
                if (imf != null)
                {
                    foreach (Alias al in imf.Names) if (al != null && al.Name == "*") _scan.HasStarImport = true;
                }
                return;
            }
            // Pass / Break / Continue / Global / Nonlocal / TypeAlias — nothing to recurse.
        }

        private void FlagConstantBranch(IReadOnlyList<Stmt> body)
        {
            if (body.Count == 0) return;
            Stmt first = body[0];
            _scan.Unreachable.Add(new DeadCodeFinding
            {
                Kind = DeadCodeKind.UnreachableCode,
                FilePath = _scan.FilePath,
                Line = first.Span.Start.Line,
                Column = first.Span.Start.Column,
                Message = "Statement is unreachable (constant if condition).",
            });
        }

        // PEP 695 type parameters (def f[T: Bound] / class C[T: Bound]): the bound is an expression
        // that may reference an imported/other name, so it must be walked or that name looks unused.
        private void WalkTypeParams(IReadOnlyList<TypeParam> typeParams)
        {
            if (typeParams == null) return;
            foreach (TypeParam tp in typeParams)
            {
                TypeVarParam tv = tp as TypeVarParam;
                if (tv != null && tv.Bound != null) WalkExpr(tv.Bound);
            }
        }

        private void WalkArgs(Arguments args)
        {
            if (args == null) return;
            foreach (Arg a in args.PosOnlyArgs) if (a.Annotation != null) WalkExpr(a.Annotation);
            foreach (Arg a in args.Args) if (a.Annotation != null) WalkExpr(a.Annotation);
            if (args.VarArg != null && args.VarArg.Annotation != null) WalkExpr(args.VarArg.Annotation);
            foreach (Arg a in args.KwOnlyArgs) if (a.Annotation != null) WalkExpr(a.Annotation);
            if (args.KwArg != null && args.KwArg.Annotation != null) WalkExpr(args.KwArg.Annotation);
            foreach (Expr d in args.Defaults) WalkExpr(d);
            foreach (Expr d in args.KwDefaults) if (d != null) WalkExpr(d);
        }

        private void WalkExpr(Expr e)
        {
            if (e == null) return;
            if (e is Name n)
            {
                if (n.Ctx == ExprContext.Load) _scan.LoadedNames.Add(n.Id);
                return;
            }
            if (e is AttributeExpr a) { _scan.AccessedAttrs.Add(a.Attr); WalkExpr(a.Value); return; }
            if (e is Call c)
            {
                WalkExpr(c.Func);
                foreach (Expr arg in c.Args) WalkExpr(arg);
                foreach (Keyword kw in c.Keywords) if (kw != null && kw.Value != null) WalkExpr(kw.Value);
                return;
            }
            if (e is BinOpExpr bo) { WalkExpr(bo.Left); WalkExpr(bo.Right); return; }
            if (e is BoolOpExpr bl) { foreach (Expr v in bl.Values) WalkExpr(v); return; }
            if (e is UnaryOpExpr u) { WalkExpr(u.Operand); return; }
            if (e is Lambda lam) { WalkArgs(lam.Args); WalkExpr(lam.Body); return; }
            if (e is IfExp ie) { WalkExpr(ie.Test); WalkExpr(ie.Body); WalkExpr(ie.OrElse); return; }
            if (e is Dict d)
            {
                foreach (Expr k in d.Keys) if (k != null) WalkExpr(k);
                foreach (Expr v in d.Values) WalkExpr(v);
                return;
            }
            if (e is SetExpr se) { foreach (Expr v in se.Elts) WalkExpr(v); return; }
            if (e is ListExpr le) { foreach (Expr v in le.Elts) WalkExpr(v); return; }
            if (e is TupleExpr te) { foreach (Expr v in te.Elts) WalkExpr(v); return; }
            if (e is ListComp lc) { WalkExpr(lc.Elt); WalkComps(lc.Generators); return; }
            if (e is SetComp sc) { WalkExpr(sc.Elt); WalkComps(sc.Generators); return; }
            if (e is DictComp dc) { WalkExpr(dc.Key); WalkExpr(dc.Value); WalkComps(dc.Generators); return; }
            if (e is GeneratorExp gc) { WalkExpr(gc.Elt); WalkComps(gc.Generators); return; }
            if (e is Await aw) { WalkExpr(aw.Value); return; }
            if (e is Yield y) { if (y.Value != null) WalkExpr(y.Value); return; }
            if (e is YieldFrom yf) { WalkExpr(yf.Value); return; }
            if (e is Compare cmp) { WalkExpr(cmp.Left); foreach (Expr v in cmp.Comparators) WalkExpr(v); return; }
            if (e is FormattedValue fv) { WalkExpr(fv.Value); if (fv.FormatSpec != null) WalkExpr(fv.FormatSpec); return; }
            if (e is JoinedStr j) { foreach (Expr v in j.Values) WalkExpr(v); return; }
            if (e is Subscript sub) { WalkExpr(sub.Value); WalkExpr(sub.Slice); return; }
            if (e is Starred st) { WalkExpr(st.Value); return; }
            if (e is NamedExpr ne) { WalkExpr(ne.Target); WalkExpr(ne.Value); return; }
            if (e is Slice sl)
            {
                if (sl.Lower != null) WalkExpr(sl.Lower);
                if (sl.Upper != null) WalkExpr(sl.Upper);
                if (sl.Step != null) WalkExpr(sl.Step);
                return;
            }
            // Constants and unmodelled exprs: nothing to recurse into.
        }

        private void WalkComps(IReadOnlyList<Comprehension> comps)
        {
            foreach (Comprehension c in comps)
            {
                WalkExpr(c.Target); WalkExpr(c.Iter);
                foreach (Expr iff in c.Ifs) WalkExpr(iff);
            }
        }

        private void WalkPattern(Pattern p)
        {
            if (p == null) return;
            if (p is MatchClass mc)
            {
                WalkExpr(mc.Cls);
                foreach (Pattern sub in mc.Patterns) WalkPattern(sub);
                foreach (Pattern sub in mc.KwdPatterns) WalkPattern(sub);
                return;
            }
            if (p is MatchValue mv) { WalkExpr(mv.Value); return; }
            if (p is MatchSequence ms) { foreach (Pattern sub in ms.Patterns) WalkPattern(sub); return; }
            if (p is MatchMapping mm)
            {
                foreach (Expr k in mm.Keys) WalkExpr(k);
                foreach (Pattern sub in mm.Patterns) WalkPattern(sub);
                return;
            }
            if (p is MatchAs mas) { WalkPattern(mas.PatternOpt); return; }
            if (p is MatchOr mo) { foreach (Pattern sub in mo.Patterns) WalkPattern(sub); return; }
            // MatchSingleton / MatchStar: capture-only.
        }

        // ----- unused locals / params -----

        private void AnalyzeFunctionLocals(IReadOnlyList<Stmt> body)
        {
            LocalUseScan scan = new LocalUseScan();
            scan.Scan(body);
            HashSet<string> loads = scan.Loads;
            foreach (LocalUseScan.Store store in scan.Stores)
            {
                if (loads.Contains(store.Name)) continue;
                _scan.UnusedLocals.Add(new DeadCodeFinding
                {
                    Kind = DeadCodeKind.UnusedVariable,
                    FilePath = _scan.FilePath,
                    Line = store.Span.Start.Line,
                    Column = store.Span.Start.Column,
                    SymbolName = store.Name,
                    Message = "Local '" + store.Name + "' is assigned but never used.",
                });
            }
        }

        private void AnalyzeFunctionParams(Arguments args, IReadOnlyList<Stmt> body)
        {
            if (args == null) return;
            LocalUseScan scan = new LocalUseScan();
            scan.Scan(body);
            HashSet<string> loads = scan.Loads;
            System.Action<Arg> emit = arg =>
            {
                if (arg == null) return;
                string nm = arg.Name;
                if (string.IsNullOrEmpty(nm)) return;
                if (nm == "self" || nm == "cls") return;
                if (nm[0] == '_') return;
                if (loads.Contains(nm)) return;
                _scan.UnusedLocals.Add(new DeadCodeFinding
                {
                    Kind = DeadCodeKind.UnusedVariable,
                    FilePath = _scan.FilePath,
                    Line = arg.Span.Start.Line,
                    Column = arg.Span.Start.Column,
                    SymbolName = nm,
                    Message = "Parameter '" + nm + "' is never used in the function body.",
                });
            };
            foreach (Arg a in args.PosOnlyArgs) emit(a);
            foreach (Arg a in args.Args) emit(a);
            foreach (Arg a in args.KwOnlyArgs) emit(a);
        }
    }
}
