namespace HelixPythonNamespace.DeadCode
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Text;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Per-function scan collecting local Name(Store) targets plus every Name reference
    /// (load) in the body and nested closures. A store whose name never appears as a load
    /// is dead. Skips underscore names, loop targets, and unpacking slots. Port of the
    /// C++ dead-code <c>LocalUseScan</c>.
    /// </summary>
    internal sealed class LocalUseScan
    {
        public struct Store
        {
            public string Name;
            public SourceSpan Span;
        }

        private readonly List<Store> _stores = new List<Store>();
        private readonly HashSet<string> _loads = new HashSet<string>(System.StringComparer.Ordinal);

        public IReadOnlyList<Store> Stores { get { return _stores; } }
        public HashSet<string> Loads { get { return _loads; } }

        public void Scan(IReadOnlyList<Stmt> body)
        {
            foreach (Stmt s in body) ScanStmt(s);
        }

        // Walk a nested-class body collecting only loads; method defs are not locals.
        private void ScanForLoadsOnly(IReadOnlyList<Stmt> body)
        {
            foreach (Stmt s in body)
            {
                if (s == null) continue;
                if (s is FunctionDef fd)
                {
                    foreach (Expr d in fd.DecoratorList) ScanExpr(d);
                    ScanForLoadsOnly(fd.Body);
                    continue;
                }
                if (s is AsyncFunctionDef afd)
                {
                    foreach (Expr d in afd.DecoratorList) ScanExpr(d);
                    ScanForLoadsOnly(afd.Body);
                    continue;
                }
                if (s is ClassDef cd)
                {
                    foreach (Expr d in cd.DecoratorList) ScanExpr(d);
                    foreach (Expr b in cd.Bases) ScanExpr(b);
                    ScanForLoadsOnly(cd.Body);
                    continue;
                }
                ScanStmt(s);
            }
        }

        private void RecordStore(Expr target, SourceSpan span)
        {
            Name n = target as Name;
            if (n == null) return;
            if (string.IsNullOrEmpty(n.Id)) return;
            if (n.Id[0] == '_') return; // _ / _foo convention
            _stores.Add(new Store { Name = n.Id, Span = span });
        }

        private void ScanStmt(Stmt s)
        {
            if (s == null) return;
            if (s is Assign a)
            {
                if (a.Targets.Count == 1) RecordStore(a.Targets[0], s.Span);
                foreach (Expr t in a.Targets)
                {
                    if (!(t is Name)) ScanExpr(t);
                }
                ScanExpr(a.Value);
                return;
            }
            if (s is AnnAssign an)
            {
                if (an.Value != null) RecordStore(an.Target, s.Span);
                if (an.Annotation != null) ScanExpr(an.Annotation);
                if (an.Target != null && !(an.Target is Name)) ScanExpr(an.Target);
                if (an.Value != null) ScanExpr(an.Value);
                return;
            }
            if (s is AugAssign ag)
            {
                Name n = ag.Target as Name;
                if (n != null) _loads.Add(n.Id);
                ScanExpr(ag.Value);
                return;
            }
            if (s is FunctionDef fd)
            {
                _stores.Add(new Store { Name = fd.Name, Span = s.Span });
                foreach (Expr d in fd.DecoratorList) ScanExpr(d);
                Scan(fd.Body);
                return;
            }
            if (s is AsyncFunctionDef afd)
            {
                _stores.Add(new Store { Name = afd.Name, Span = s.Span });
                foreach (Expr d in afd.DecoratorList) ScanExpr(d);
                Scan(afd.Body);
                return;
            }
            if (s is ClassDef cd)
            {
                _stores.Add(new Store { Name = cd.Name, Span = s.Span });
                foreach (Expr d in cd.DecoratorList) ScanExpr(d);
                foreach (Expr b in cd.Bases) ScanExpr(b);
                ScanForLoadsOnly(cd.Body);
                return;
            }
            if (s is If iff) { ScanExpr(iff.Test); Scan(iff.Body); Scan(iff.OrElse); return; }
            if (s is While w) { ScanExpr(w.Test); Scan(w.Body); Scan(w.OrElse); return; }
            if (s is For f)
            {
                if (f.Target != null && !(f.Target is Name)) ScanExpr(f.Target);
                ScanExpr(f.Iter); Scan(f.Body); Scan(f.OrElse);
                return;
            }
            if (s is AsyncFor af)
            {
                if (af.Target != null && !(af.Target is Name)) ScanExpr(af.Target);
                ScanExpr(af.Iter); Scan(af.Body); Scan(af.OrElse);
                return;
            }
            if (s is With wi)
            {
                foreach (WithItem it in wi.Items)
                {
                    if (it.ContextExpr != null) ScanExpr(it.ContextExpr);
                    if (it.OptionalVars != null)
                    {
                        RecordStore(it.OptionalVars, s.Span);
                        if (!(it.OptionalVars is Name)) ScanExpr(it.OptionalVars);
                    }
                }
                Scan(wi.Body);
                return;
            }
            if (s is AsyncWith aw)
            {
                foreach (WithItem it in aw.Items)
                {
                    if (it.ContextExpr != null) ScanExpr(it.ContextExpr);
                    if (it.OptionalVars != null)
                    {
                        RecordStore(it.OptionalVars, s.Span);
                        if (!(it.OptionalVars is Name)) ScanExpr(it.OptionalVars);
                    }
                }
                Scan(aw.Body);
                return;
            }
            if (s is Return r) { if (r.Value != null) ScanExpr(r.Value); return; }
            if (s is Raise rz) { if (rz.Exc != null) ScanExpr(rz.Exc); if (rz.Cause != null) ScanExpr(rz.Cause); return; }
            if (s is ExprStmt e) { ScanExpr(e.Value); return; }
            if (s is Try tr)
            {
                Scan(tr.Body);
                foreach (ExceptHandler h in tr.Handlers) { if (h.Type != null) ScanExpr(h.Type); Scan(h.Body); }
                Scan(tr.OrElse); Scan(tr.FinalBody);
                return;
            }
            if (s is TryStar trs)
            {
                Scan(trs.Body);
                foreach (ExceptHandler h in trs.Handlers) { if (h.Type != null) ScanExpr(h.Type); Scan(h.Body); }
                Scan(trs.OrElse); Scan(trs.FinalBody);
                return;
            }
            if (s is Assert ast) { if (ast.Test != null) ScanExpr(ast.Test); if (ast.Msg != null) ScanExpr(ast.Msg); return; }
            if (s is Delete del) { foreach (Expr t in del.Targets) ScanExpr(t); return; }
            if (s is Match ma)
            {
                ScanExpr(ma.Subject);
                foreach (MatchCase c in ma.Cases) Scan(c.Body);
                return;
            }
            // Pass / Break / Continue / Global / Nonlocal / Import / ImportFrom / TypeAlias — no local store/load.
        }

        private void ScanExpr(Expr e)
        {
            if (e == null) return;
            if (e is Name n) { _loads.Add(n.Id); return; }
            if (e is AttributeExpr a) { ScanExpr(a.Value); return; }
            if (e is Subscript sub) { ScanExpr(sub.Value); ScanExpr(sub.Slice); return; }
            if (e is Slice sl)
            {
                if (sl.Lower != null) ScanExpr(sl.Lower);
                if (sl.Upper != null) ScanExpr(sl.Upper);
                if (sl.Step != null) ScanExpr(sl.Step);
                return;
            }
            if (e is Call c)
            {
                ScanExpr(c.Func);
                foreach (Expr arg in c.Args) ScanExpr(arg);
                foreach (Keyword kw in c.Keywords) if (kw != null && kw.Value != null) ScanExpr(kw.Value);
                return;
            }
            if (e is BoolOpExpr bo) { foreach (Expr v in bo.Values) ScanExpr(v); return; }
            if (e is BinOpExpr bi) { ScanExpr(bi.Left); ScanExpr(bi.Right); return; }
            if (e is UnaryOpExpr u) { ScanExpr(u.Operand); return; }
            if (e is Compare cmp) { ScanExpr(cmp.Left); foreach (Expr r in cmp.Comparators) ScanExpr(r); return; }
            if (e is IfExp ie) { ScanExpr(ie.Test); ScanExpr(ie.Body); ScanExpr(ie.OrElse); return; }
            if (e is ListExpr le) { foreach (Expr el in le.Elts) ScanExpr(el); return; }
            if (e is TupleExpr te) { foreach (Expr el in te.Elts) ScanExpr(el); return; }
            if (e is SetExpr se) { foreach (Expr el in se.Elts) ScanExpr(el); return; }
            if (e is Dict d)
            {
                foreach (Expr k in d.Keys) if (k != null) ScanExpr(k);
                foreach (Expr v in d.Values) ScanExpr(v);
                return;
            }
            if (e is Lambda la) { ScanExpr(la.Body); return; }
            if (e is ListComp lc) { ScanExpr(lc.Elt); ScanGens(lc.Generators); return; }
            if (e is SetComp sc) { ScanExpr(sc.Elt); ScanGens(sc.Generators); return; }
            if (e is DictComp dc) { ScanExpr(dc.Key); ScanExpr(dc.Value); ScanGens(dc.Generators); return; }
            if (e is GeneratorExp ge) { ScanExpr(ge.Elt); ScanGens(ge.Generators); return; }
            if (e is NamedExpr ne)
            {
                Name t = ne.Target as Name;
                if (t != null) _loads.Add(t.Id);
                ScanExpr(ne.Value);
                return;
            }
            if (e is Starred st) { ScanExpr(st.Value); return; }
            if (e is Yield y) { if (y.Value != null) ScanExpr(y.Value); return; }
            if (e is YieldFrom yf) { ScanExpr(yf.Value); return; }
            if (e is Await aw) { ScanExpr(aw.Value); return; }
            if (e is JoinedStr js) { foreach (Expr part in js.Values) ScanExpr(part); return; }
            if (e is FormattedValue fv) { if (fv.Value != null) ScanExpr(fv.Value); if (fv.FormatSpec != null) ScanExpr(fv.FormatSpec); return; }
            // Constants, etc — no name references.
        }

        private void ScanGens(IReadOnlyList<Comprehension> gens)
        {
            foreach (Comprehension g in gens)
            {
                ScanExpr(g.Iter);
                foreach (Expr i in g.Ifs) ScanExpr(i);
                if (g.Target != null && !(g.Target is Name)) ScanExpr(g.Target);
            }
        }
    }
}
