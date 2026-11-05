namespace HelixPythonNamespace.Linker
{
    using System.Collections.Generic;
    using System.Threading;
    using HelixPythonNamespace.Python.Text;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Walks every Name / Attribute in every module's AST and resolves uses that target
    /// a top-level (intra- or cross-module) symbol, feeding the per-node binding map and
    /// the reverse reference index. Port of the C++ <c>ReferenceBinder</c>.
    /// </summary>
    public sealed class ReferenceBinder
    {
        public sealed class Output
        {
            public Dictionary<AstNode, BindingTarget> Bindings = new Dictionary<AstNode, BindingTarget>();
            public Dictionary<AstNode, string> Owner = new Dictionary<AstNode, string>();
            public ReferenceIndex References = new ReferenceIndex();
        }

        public static Output Bind(Dictionary<string, ModuleSummary> modules,
            Dictionary<AstNode, BindingTarget> importResolution, CancellationToken cancel = default(CancellationToken))
        {
            Output outp = new Output();
            foreach (KeyValuePair<string, ModuleSummary> kv in modules)
            {
                if (cancel.IsCancellationRequested) return outp;
                Walker w = new Walker(modules, importResolution, outp);
                w.Walk(kv.Value);
            }
            return outp;
        }

        private sealed class Walker
        {
            private readonly Dictionary<string, ModuleSummary> _modules;
            private readonly Dictionary<AstNode, BindingTarget> _importRes;
            private readonly Output _out;
            private string _importerFqn;
            private string _importerFile;

            public Walker(Dictionary<string, ModuleSummary> modules, Dictionary<AstNode, BindingTarget> importRes, Output outp)
            {
                _modules = modules;
                _importRes = importRes;
                _out = outp;
            }

            public void Walk(ModuleSummary summary)
            {
                _importerFqn = summary.ModuleFqn;
                _importerFile = summary.FilePath;
                if (summary.AstModule == null) return;
                foreach (Stmt s in summary.AstModule.Body) VisitStmt(s);
            }

            private ModuleSummary CurrentSummary()
            {
                ModuleSummary m;
                return _modules.TryGetValue(_importerFqn, out m) ? m : null;
            }

            private BindingTarget ResolveTopLevelName(string name)
            {
                ModuleSummary mod = CurrentSummary();
                if (mod == null) return BindingTarget.Unresolved(UnresolvedReason.None);
                ModuleBinding b;
                if (!mod.Bindings.TryGetValue(name, out b))
                {
                    return BindingTarget.Unresolved(UnresolvedReason.None);
                }
                if (b.Kind == BindingKind.ImportAlias)
                {
                    Import impStmt = b.DefNode as Import;
                    if (impStmt != null)
                    {
                        foreach (Alias a in impStmt.Names)
                        {
                            int dot = a.Name.IndexOf('.');
                            string bound = !string.IsNullOrEmpty(a.AsName) ? a.AsName : (dot < 0 ? a.Name : a.Name.Substring(0, dot));
                            if (bound == name)
                            {
                                BindingTarget bt;
                                if (_importRes.TryGetValue(a, out bt)) return bt;
                                return BindingTarget.Unresolved(UnresolvedReason.ModuleNotFound);
                            }
                        }
                    }
                    else
                    {
                        ImportFrom imfStmt = b.DefNode as ImportFrom;
                        if (imfStmt != null)
                        {
                            foreach (Alias a in imfStmt.Names)
                            {
                                string bound = !string.IsNullOrEmpty(a.AsName) ? a.AsName : a.Name;
                                if (bound == name)
                                {
                                    BindingTarget bt;
                                    if (_importRes.TryGetValue(a, out bt)) return bt;
                                    return BindingTarget.Unresolved(UnresolvedReason.NameNotInModule);
                                }
                            }
                        }
                    }
                    return BindingTarget.Unresolved(UnresolvedReason.None);
                }
                return BindingTarget.Symbol(_importerFqn, name, b.DefSpan);
            }

            private void RecordUse(AstNode node, BindingTarget target, SourceSpan span)
            {
                if (node == null) return;
                _out.Bindings[node] = target;
                _out.Owner[node] = _importerFqn;
                if (target.Kind == BindingTargetKind.ResolvedSymbol || target.Kind == BindingTargetKind.ResolvedCatalogSymbol)
                {
                    _out.References.Add(new DefId { ModuleFqn = target.ModuleFqn, Name = target.Name },
                        new UseSite { ModuleFqn = _importerFqn, FilePath = _importerFile, Node = node, Span = span });
                }
            }

            private BindingTarget EvalExpr(Expr e)
            {
                if (e == null) return BindingTarget.Unresolved(UnresolvedReason.None);
                if (e is Name nm)
                {
                    BindingTarget bt = ResolveTopLevelName(nm.Id);
                    RecordUse(nm, bt, nm.Span);
                    return bt;
                }
                if (e is AttributeExpr ae)
                {
                    BindingTarget lhs = EvalExpr(ae.Value);
                    BindingTarget result = BindingTarget.Unresolved(UnresolvedReason.None);
                    if (lhs.Kind == BindingTargetKind.ResolvedModule)
                    {
                        string targetFqn = lhs.ModuleFqn;
                        ModuleSummary mod;
                        if (_modules.TryGetValue(targetFqn, out mod))
                        {
                            ModuleBinding b;
                            if (mod.Bindings.TryGetValue(ae.Attr, out b))
                            {
                                result = BindingTarget.Symbol(targetFqn, ae.Attr, b.DefSpan);
                            }
                            else
                            {
                                string subFqn = targetFqn + "." + ae.Attr;
                                if (_modules.ContainsKey(subFqn))
                                {
                                    result = BindingTarget.Module(subFqn);
                                }
                            }
                        }
                        else
                        {
                            result = BindingTarget.CatalogSymbol(targetFqn, ae.Attr);
                        }
                    }
                    RecordUse(ae, result, ae.Span);
                    return result;
                }
                VisitExpr(e);
                return BindingTarget.Unresolved(UnresolvedReason.None);
            }

            private void VisitExpr(Expr e)
            {
                if (e == null) return;
                if (e is Name n) { RecordUse(n, ResolveTopLevelName(n.Id), n.Span); return; }
                if (e is AttributeExpr ae) { EvalExpr(ae); return; }
                if (e is Call call)
                {
                    VisitExpr(call.Func);
                    foreach (Expr a in call.Args) VisitExpr(a);
                    foreach (Keyword kw in call.Keywords) if (kw.Value != null) VisitExpr(kw.Value);
                    return;
                }
                if (e is Subscript sub) { VisitExpr(sub.Value); VisitExpr(sub.Slice); return; }
                if (e is ListExpr le) { foreach (Expr el in le.Elts) VisitExpr(el); return; }
                if (e is TupleExpr te) { foreach (Expr el in te.Elts) VisitExpr(el); return; }
                if (e is SetExpr se) { foreach (Expr el in se.Elts) VisitExpr(el); return; }
                if (e is Dict de)
                {
                    foreach (Expr k in de.Keys) if (k != null) VisitExpr(k);
                    foreach (Expr v in de.Values) VisitExpr(v);
                    return;
                }
                if (e is BoolOpExpr bo) { foreach (Expr v in bo.Values) VisitExpr(v); return; }
                if (e is BinOpExpr bn) { VisitExpr(bn.Left); VisitExpr(bn.Right); return; }
                if (e is UnaryOpExpr un) { VisitExpr(un.Operand); return; }
                if (e is Compare cmp) { VisitExpr(cmp.Left); foreach (Expr c in cmp.Comparators) VisitExpr(c); return; }
                if (e is IfExp ifx) { VisitExpr(ifx.Test); VisitExpr(ifx.Body); VisitExpr(ifx.OrElse); return; }
                if (e is Starred st) { VisitExpr(st.Value); return; }
                if (e is Await aw) { VisitExpr(aw.Value); return; }
                if (e is Yield yi) { if (yi.Value != null) VisitExpr(yi.Value); return; }
                if (e is YieldFrom yf) { VisitExpr(yf.Value); return; }
                if (e is FormattedValue fv) { VisitExpr(fv.Value); if (fv.FormatSpec != null) VisitExpr(fv.FormatSpec); return; }
                if (e is JoinedStr js) { foreach (Expr v in js.Values) VisitExpr(v); return; }
                if (e is NamedExpr nv) { VisitExpr(nv.Target); VisitExpr(nv.Value); return; }
                if (e is Lambda lam) { VisitArguments(lam.Args); VisitExpr(lam.Body); return; }
                if (e is ListComp lc) { VisitExpr(lc.Elt); VisitGenerators(lc.Generators); return; }
                if (e is SetComp scp) { VisitExpr(scp.Elt); VisitGenerators(scp.Generators); return; }
                if (e is DictComp dc) { VisitExpr(dc.Key); VisitExpr(dc.Value); VisitGenerators(dc.Generators); return; }
                if (e is GeneratorExp gx) { VisitExpr(gx.Elt); VisitGenerators(gx.Generators); return; }
                // Constants / other leaves — nothing to do.
            }

            private void VisitGenerators(IReadOnlyList<Comprehension> gens)
            {
                foreach (Comprehension g in gens)
                {
                    if (g.Target != null) VisitExpr(g.Target);
                    if (g.Iter != null) VisitExpr(g.Iter);
                    foreach (Expr cond in g.Ifs) VisitExpr(cond);
                }
            }

            private void VisitStmt(Stmt s)
            {
                if (s == null) return;
                if (s is FunctionDef f)
                {
                    foreach (Expr d in f.DecoratorList) VisitExpr(d);
                    if (f.Returns != null) VisitExpr(f.Returns);
                    VisitArguments(f.Args);
                    foreach (Stmt bs in f.Body) VisitStmt(bs);
                    return;
                }
                if (s is AsyncFunctionDef af)
                {
                    foreach (Expr d in af.DecoratorList) VisitExpr(d);
                    if (af.Returns != null) VisitExpr(af.Returns);
                    VisitArguments(af.Args);
                    foreach (Stmt bs in af.Body) VisitStmt(bs);
                    return;
                }
                if (s is ClassDef c)
                {
                    foreach (Expr d in c.DecoratorList) VisitExpr(d);
                    foreach (Expr b in c.Bases) VisitExpr(b);
                    foreach (Keyword kw in c.Keywords) if (kw.Value != null) VisitExpr(kw.Value);
                    foreach (Stmt bs in c.Body) VisitStmt(bs);
                    return;
                }
                if (s is Assign a)
                {
                    foreach (Expr t in a.Targets) VisitExpr(t);
                    if (a.Value != null) VisitExpr(a.Value);
                    return;
                }
                if (s is AnnAssign an)
                {
                    VisitExpr(an.Target);
                    if (an.Annotation != null) VisitExpr(an.Annotation);
                    if (an.Value != null) VisitExpr(an.Value);
                    return;
                }
                if (s is AugAssign aug) { VisitExpr(aug.Target); VisitExpr(aug.Value); return; }
                if (s is Return ret) { if (ret.Value != null) VisitExpr(ret.Value); return; }
                if (s is ExprStmt es) { VisitExpr(es.Value); return; }
                if (s is If ifs)
                {
                    VisitExpr(ifs.Test);
                    foreach (Stmt bs in ifs.Body) VisitStmt(bs);
                    foreach (Stmt bs in ifs.OrElse) VisitStmt(bs);
                    return;
                }
                if (s is While w)
                {
                    VisitExpr(w.Test);
                    foreach (Stmt bs in w.Body) VisitStmt(bs);
                    foreach (Stmt bs in w.OrElse) VisitStmt(bs);
                    return;
                }
                if (s is For fs)
                {
                    VisitExpr(fs.Target); VisitExpr(fs.Iter);
                    foreach (Stmt bs in fs.Body) VisitStmt(bs);
                    foreach (Stmt bs in fs.OrElse) VisitStmt(bs);
                    return;
                }
                if (s is AsyncFor afs)
                {
                    VisitExpr(afs.Target); VisitExpr(afs.Iter);
                    foreach (Stmt bs in afs.Body) VisitStmt(bs);
                    foreach (Stmt bs in afs.OrElse) VisitStmt(bs);
                    return;
                }
                if (s is With ws)
                {
                    foreach (WithItem it in ws.Items)
                    {
                        if (it.ContextExpr != null) VisitExpr(it.ContextExpr);
                        if (it.OptionalVars != null) VisitExpr(it.OptionalVars);
                    }
                    foreach (Stmt bs in ws.Body) VisitStmt(bs);
                    return;
                }
                if (s is AsyncWith aws)
                {
                    foreach (WithItem it in aws.Items)
                    {
                        if (it.ContextExpr != null) VisitExpr(it.ContextExpr);
                        if (it.OptionalVars != null) VisitExpr(it.OptionalVars);
                    }
                    foreach (Stmt bs in aws.Body) VisitStmt(bs);
                    return;
                }
                if (s is Try ts)
                {
                    foreach (Stmt bs in ts.Body) VisitStmt(bs);
                    foreach (ExceptHandler h in ts.Handlers)
                    {
                        if (h.Type != null) VisitExpr(h.Type);
                        foreach (Stmt bs in h.Body) VisitStmt(bs);
                    }
                    foreach (Stmt bs in ts.OrElse) VisitStmt(bs);
                    foreach (Stmt bs in ts.FinalBody) VisitStmt(bs);
                    return;
                }
                if (s is Raise rs) { if (rs.Exc != null) VisitExpr(rs.Exc); if (rs.Cause != null) VisitExpr(rs.Cause); return; }
                if (s is Assert ast) { if (ast.Test != null) VisitExpr(ast.Test); if (ast.Msg != null) VisitExpr(ast.Msg); return; }
                if (s is Delete ds) { foreach (Expr t in ds.Targets) VisitExpr(t); return; }
                if (s is TypeAlias ta) { VisitExpr(ta.Name); if (ta.Value != null) VisitExpr(ta.Value); return; }
                // Import / ImportFrom / Pass / Break / Continue / Global / Nonlocal — no refs to walk.
            }

            private void VisitArguments(Arguments args)
            {
                if (args == null) return;
                foreach (Arg a in args.PosOnlyArgs) VisitArg(a);
                foreach (Arg a in args.Args) VisitArg(a);
                if (args.VarArg != null) VisitArg(args.VarArg);
                foreach (Arg a in args.KwOnlyArgs) VisitArg(a);
                if (args.KwArg != null) VisitArg(args.KwArg);
                foreach (Expr d in args.Defaults) VisitExpr(d);
                foreach (Expr d in args.KwDefaults) if (d != null) VisitExpr(d);
            }

            private void VisitArg(Arg a)
            {
                if (a == null) return;
                if (a.Annotation != null) VisitExpr(a.Annotation);
            }
        }
    }
}
