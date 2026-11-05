using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Symtable
{
    /// <summary>
    /// Pass 2 of the symbol-table analysis: using the <see cref="SymbolTable"/>
    /// built in Pass 1, walk the AST once more, resolve every <see cref="Name"/>
    /// use against the scope chain, and emit diagnostics for undefined names,
    /// bad <c>global</c>/<c>nonlocal</c> declarations, and scope-dependent
    /// context errors (<c>return</c>/<c>yield</c>/<c>break</c>/<c>continue</c>
    /// in the wrong place).
    /// </summary>
    public sealed class NameResolver
    {
        private readonly string _filePath;
        private readonly SymbolTable _table;
        private readonly List<CompileDiagnostic> _diagnostics;
        private readonly Stack<Scope> _scopeStack = new Stack<Scope>();
        private int _loopDepth;

        public NameResolver(string filePath, SymbolTable table)
        {
            _filePath = filePath ?? string.Empty;
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _diagnostics = new List<CompileDiagnostic>();
        }

        public IReadOnlyList<CompileDiagnostic> Diagnostics { get { return _diagnostics; } }

        public void Resolve()
        {
            _scopeStack.Clear();
            _scopeStack.Push(_table.Root);
            ValidateScope(_table.Root);
            try
            {
                foreach (Stmt s in _table.Module.Body) VisitStmt(s);
            }
            finally
            {
                _scopeStack.Pop();
            }
        }

        private Scope Current { get { return _scopeStack.Peek(); } }

        // ---------- Scope-level validation ----------

        private void ValidateScope(Scope s)
        {
            // The "global x + local x in same scope" check that once lived here was
            // over-eager vs. CPython — once `global x` is declared, all writes to `x`
            // in that scope are global, so DefLocal+DefGlobal coexisting isn't an error
            // in itself. The real conflict — `x = 1; global x` (declaration after use/
            // assign) — is already caught by PY4008 in SymbolTableBuilder.

            // nonlocal checks: each declared name must resolve to a binding in some
            // enclosing function scope (not module, not class).
            foreach (string name in s.NonlocalDeclarations)
            {
                if (!NonlocalBindingExists(s, name))
                {
                    _diagnostics.Add(new CompileDiagnostic(_filePath,
                        VirtualCompilerDiagnosticCodes.SymNonlocalWithoutBinding,
                        ParseSeverity.Error, s.Span,
                        "no binding for nonlocal '" + name + "' found in any enclosing function"));
                }
            }
        }

        private static bool NonlocalBindingExists(Scope from, string name)
        {
            // Walk up: the first enclosing Function (not Class, not Module) that has
            // the name bound satisfies nonlocal.
            for (Scope p = from.Parent; p != null; p = p.Parent)
            {
                if (p.Kind == BlockKind.Module) return false;   // hit module boundary
                if (p.Kind == BlockKind.Class) continue;         // class scopes are skipped
                Symbol sym;
                if (p.TryGetSymbol(name, out sym) && sym.Has(SymbolFlag.DefBound)) return true;
            }
            return false;
        }

        // ---------- Statement walk ----------

        private void VisitStmt(Stmt s)
        {
            if (s == null) return;
            switch (s)
            {
                case FunctionDef f: VisitFunctionLike(f, f.Args, f.Body, f.DecoratorList, f.Returns); break;
                case AsyncFunctionDef af: VisitFunctionLike(af, af.Args, af.Body, af.DecoratorList, af.Returns); break;
                case ClassDef c: VisitClassDef(c); break;
                case Return r:
                    if (!IsInsideFunction())
                    {
                        Emit(VirtualCompilerDiagnosticCodes.CompReturnOutsideFunction,
                            "'return' outside function", r.Span);
                    }
                    if (r.Value != null) VisitExpr(r.Value);
                    break;
                case Break br:
                    if (_loopDepth <= 0)
                        Emit(VirtualCompilerDiagnosticCodes.CompBreakOutsideLoop,
                            "'break' outside loop", br.Span);
                    break;
                case Continue co:
                    if (_loopDepth <= 0)
                        Emit(VirtualCompilerDiagnosticCodes.CompContinueOutsideLoop,
                            "'continue' not properly in loop", co.Span);
                    break;
                case Delete d: foreach (Expr t in d.Targets) VisitExpr(t); break;
                case Assign a:
                    if (a.Value != null) VisitExpr(a.Value);
                    foreach (Expr t in a.Targets) VisitExpr(t);
                    break;
                case AugAssign aug: VisitExpr(aug.Value); VisitExpr(aug.Target); break;
                case AnnAssign an:
                    VisitExpr(an.Annotation);
                    if (an.Value != null) VisitExpr(an.Value);
                    if (!(an.Target is Name && an.Simple)) VisitExpr(an.Target);
                    break;
                case For f2:
                    VisitExpr(f2.Iter);
                    VisitExpr(f2.Target);
                    _loopDepth++;
                    try { foreach (Stmt ss in f2.Body) VisitStmt(ss); }
                    finally { _loopDepth--; }
                    foreach (Stmt ss in f2.OrElse) VisitStmt(ss);
                    break;
                case AsyncFor af2:
                    VisitExpr(af2.Iter);
                    VisitExpr(af2.Target);
                    _loopDepth++;
                    try { foreach (Stmt ss in af2.Body) VisitStmt(ss); }
                    finally { _loopDepth--; }
                    foreach (Stmt ss in af2.OrElse) VisitStmt(ss);
                    break;
                case While w:
                    VisitExpr(w.Test);
                    _loopDepth++;
                    try { foreach (Stmt ss in w.Body) VisitStmt(ss); }
                    finally { _loopDepth--; }
                    foreach (Stmt ss in w.OrElse) VisitStmt(ss);
                    break;
                case If i:
                    VisitExpr(i.Test);
                    foreach (Stmt ss in i.Body) VisitStmt(ss);
                    foreach (Stmt ss in i.OrElse) VisitStmt(ss);
                    break;
                case With wi:
                    foreach (WithItem it in wi.Items)
                    {
                        VisitExpr(it.ContextExpr);
                        if (it.OptionalVars != null) VisitExpr(it.OptionalVars);
                    }
                    foreach (Stmt ss in wi.Body) VisitStmt(ss);
                    break;
                case AsyncWith aw:
                    foreach (WithItem it in aw.Items)
                    {
                        VisitExpr(it.ContextExpr);
                        if (it.OptionalVars != null) VisitExpr(it.OptionalVars);
                    }
                    foreach (Stmt ss in aw.Body) VisitStmt(ss);
                    break;
                case Try t:
                    foreach (Stmt ss in t.Body) VisitStmt(ss);
                    foreach (Ast.ExceptHandler h in t.Handlers) VisitHandler(h);
                    foreach (Stmt ss in t.OrElse) VisitStmt(ss);
                    foreach (Stmt ss in t.FinalBody) VisitStmt(ss);
                    break;
                case TryStar ts:
                    foreach (Stmt ss in ts.Body) VisitStmt(ss);
                    foreach (Ast.ExceptHandler h in ts.Handlers) VisitHandler(h);
                    foreach (Stmt ss in ts.OrElse) VisitStmt(ss);
                    foreach (Stmt ss in ts.FinalBody) VisitStmt(ss);
                    break;
                case Raise r2:
                    if (r2.Exc != null) VisitExpr(r2.Exc);
                    if (r2.Cause != null) VisitExpr(r2.Cause);
                    break;
                case Assert ass:
                    VisitExpr(ass.Test);
                    if (ass.Msg != null) VisitExpr(ass.Msg);
                    break;
                case Import _: case ImportFrom _: case Global _: case Nonlocal _:
                    // Already processed in Pass 1; nothing to resolve here.
                    break;
                case ExprStmt e: VisitExpr(e.Value); break;
                case Match m:
                    VisitExpr(m.Subject);
                    foreach (MatchCase mc in m.Cases)
                    {
                        if (mc.Guard != null) VisitExpr(mc.Guard);
                        foreach (Stmt ss in mc.Body) VisitStmt(ss);
                    }
                    break;
            }
        }

        private void VisitFunctionLike(AstNode funcNode, Arguments args, IReadOnlyList<Stmt> body,
            IReadOnlyList<Expr> decorators, Expr returns)
        {
            // Decorators and defaults resolve in enclosing scope.
            foreach (Expr d in decorators) VisitExpr(d);
            if (args != null)
            {
                foreach (Expr def in args.Defaults) VisitExpr(def);
                foreach (Expr def in args.KwDefaults) if (def != null) VisitExpr(def);
            }
            Scope fn = _table.LookupScope(funcNode);
            if (fn == null) return;
            _scopeStack.Push(fn);
            ValidateScope(fn);
            int savedLoopDepth = _loopDepth;
            _loopDepth = 0;
            try
            {
                if (args != null)
                {
                    void visitArg(Arg a)
                    {
                        if (a != null && a.Annotation != null) VisitExpr(a.Annotation);
                    }
                    foreach (Arg a in args.PosOnlyArgs) visitArg(a);
                    foreach (Arg a in args.Args) visitArg(a);
                    visitArg(args.VarArg);
                    foreach (Arg a in args.KwOnlyArgs) visitArg(a);
                    visitArg(args.KwArg);
                }
                if (returns != null) VisitExpr(returns);
                foreach (Stmt s in body) VisitStmt(s);
            }
            finally
            {
                _loopDepth = savedLoopDepth;
                _scopeStack.Pop();
            }
        }

        private void VisitClassDef(ClassDef c)
        {
            foreach (Expr d in c.DecoratorList) VisitExpr(d);
            foreach (Expr b in c.Bases) VisitExpr(b);
            foreach (Keyword kw in c.Keywords) if (kw.Value != null) VisitExpr(kw.Value);
            Scope cls = _table.LookupScope(c);
            if (cls == null) return;
            _scopeStack.Push(cls);
            ValidateScope(cls);
            int savedLoopDepth = _loopDepth;
            _loopDepth = 0;
            try
            {
                foreach (Stmt s in c.Body) VisitStmt(s);
            }
            finally
            {
                _loopDepth = savedLoopDepth;
                _scopeStack.Pop();
            }
        }

        private void VisitHandler(Ast.ExceptHandler h)
        {
            if (h.Type != null) VisitExpr(h.Type);
            foreach (Stmt s in h.Body) VisitStmt(s);
        }

        // ---------- Expression walk ----------

        private void VisitExpr(Expr e)
        {
            if (e == null) return;
            switch (e)
            {
                case Name n:
                    if (n.Ctx == ExprContext.Load) ResolveNameLoad(n);
                    break;
                case Constant _: break;
                case BoolOpExpr b: foreach (Expr v in b.Values) VisitExpr(v); break;
                case BinOpExpr bi: VisitBinaryOperands(bi); break;
                case UnaryOpExpr u: VisitExpr(u.Operand); break;
                case Lambda lam: VisitLambda(lam); break;
                case IfExp ie: VisitExpr(ie.Test); VisitExpr(ie.Body); VisitExpr(ie.OrElse); break;
                case Dict d:
                    for (int i = 0; i < d.Keys.Count; i++)
                    {
                        if (d.Keys[i] != null) VisitExpr(d.Keys[i]);
                        if (i < d.Values.Count) VisitExpr(d.Values[i]);
                    }
                    break;
                case Ast.SetExpr se: foreach (Expr v in se.Elts) VisitExpr(v); break;
                case ListComp lc: VisitComprehension(lc, lc.Elt, null, lc.Generators); break;
                case SetComp sc: VisitComprehension(sc, sc.Elt, null, sc.Generators); break;
                case DictComp dc: VisitComprehension(dc, dc.Key, dc.Value, dc.Generators); break;
                case GeneratorExp ge: VisitComprehension(ge, ge.Elt, null, ge.Generators); break;
                case Await aw: VisitExpr(aw.Value); break;
                case Yield y:
                    if (!IsInsideFunction())
                        Emit(VirtualCompilerDiagnosticCodes.CompYieldOutsideFunction,
                            "'yield' outside function", y.Span);
                    if (y.Value != null) VisitExpr(y.Value);
                    break;
                case YieldFrom yf:
                    if (!IsInsideFunction())
                        Emit(VirtualCompilerDiagnosticCodes.CompYieldOutsideFunction,
                            "'yield from' outside function", yf.Span);
                    VisitExpr(yf.Value);
                    break;
                case Compare c:
                    VisitExpr(c.Left);
                    foreach (Expr cc in c.Comparators) VisitExpr(cc);
                    break;
                case Call call:
                    VisitExpr(call.Func);
                    foreach (Expr a in call.Args) VisitExpr(a);
                    foreach (Keyword k in call.Keywords) if (k.Value != null) VisitExpr(k.Value);
                    break;
                case FormattedValue fv: VisitExpr(fv.Value); if (fv.FormatSpec != null) VisitExpr(fv.FormatSpec); break;
                case JoinedStr js: foreach (Expr v in js.Values) VisitExpr(v); break;
                case AttributeExpr ae: VisitExpr(ae.Value); break;
                case Subscript s: VisitExpr(s.Value); VisitExpr(s.Slice); break;
                case Starred st: VisitExpr(st.Value); break;
                case Ast.ListExpr le: foreach (Expr v in le.Elts) VisitExpr(v); break;
                case Ast.TupleExpr te: foreach (Expr v in te.Elts) VisitExpr(v); break;
                case Ast.Slice sl:
                    if (sl.Lower != null) VisitExpr(sl.Lower);
                    if (sl.Upper != null) VisitExpr(sl.Upper);
                    if (sl.Step != null) VisitExpr(sl.Step);
                    break;
                case Ast.NamedExpr ne:
                    VisitExpr(ne.Value);
                    // walrus target is Store — no undefined-name check needed.
                    break;
            }
        }

        private void VisitBinaryOperands(BinOpExpr root)
        {
            Stack<Expr> pending = new Stack<Expr>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                Expr current = pending.Pop();
                if (current is BinOpExpr binary)
                {
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                }
                else VisitExpr(current);
            }
        }

        private void VisitLambda(Lambda lam)
        {
            if (lam.Args != null)
            {
                foreach (Expr def in lam.Args.Defaults) VisitExpr(def);
                foreach (Expr def in lam.Args.KwDefaults) if (def != null) VisitExpr(def);
            }
            Scope lScope = _table.LookupScope(lam);
            if (lScope == null) return;
            _scopeStack.Push(lScope);
            ValidateScope(lScope);
            int savedLoopDepth = _loopDepth;
            _loopDepth = 0;
            try
            {
                VisitExpr(lam.Body);
            }
            finally
            {
                _loopDepth = savedLoopDepth;
                _scopeStack.Pop();
            }
        }

        private void VisitComprehension(Expr compNode, Expr elt, Expr valueElt, IReadOnlyList<Comprehension> gens)
        {
            if (gens == null || gens.Count == 0) return;
            // First iter evaluates in enclosing scope.
            VisitExpr(gens[0].Iter);

            Scope cScope = _table.LookupScope(compNode);
            if (cScope == null) return;
            _scopeStack.Push(cScope);
            ValidateScope(cScope);
            try
            {
                for (int i = 0; i < gens.Count; i++)
                {
                    if (i > 0) VisitExpr(gens[i].Iter);
                    VisitExpr(gens[i].Target);
                    foreach (Expr ife in gens[i].Ifs) VisitExpr(ife);
                }
                if (elt != null) VisitExpr(elt);
                if (valueElt != null) VisitExpr(valueElt);
            }
            finally
            {
                _scopeStack.Pop();
            }
        }

        // ---------- Name resolution ----------

        private void ResolveNameLoad(Name n)
        {
            if (string.IsNullOrEmpty(n.Id)) return;
            if (ResolveName(n.Id)) return;
            if (Builtins.IsBuiltin(n.Id)) return;
            if (_table.Root.HasStarImport) return;
            // Inside a method, __class__ / __classcell__ are implicit.
            if (Builtins.IsImplicitInsideMethod(n.Id) && IsInsideMethod()) return;

            Emit(VirtualCompilerDiagnosticCodes.SymUndefinedName,
                "undefined name '" + n.Id + "'", n.Span);
        }

        /// <summary>
        /// Walks enclosing scopes looking for any binding of <paramref name="name"/>.
        /// Class scopes are skipped when the current scope isn't a class body itself
        /// (CPython's "class scope not inherited" rule).
        /// </summary>
        private bool ResolveName(string name)
        {
            bool first = true;
            foreach (Scope s in _scopeStack)
            {
                if (!first && s.Kind == BlockKind.Class)
                {
                    // Skip enclosing class scope for name lookups originating below.
                    first = false;
                    continue;
                }
                first = false;
                Symbol sym;
                if (s.TryGetSymbol(name, out sym))
                {
                    // DefAnnot alone is not enough (bare `x: int` doesn't bind a value),
                    // but for MVP we accept DefAnnot as known — typo checking still works.
                    if (sym.Has(SymbolFlag.DefBound | SymbolFlag.DefGlobal | SymbolFlag.DefFree
                                | SymbolFlag.DefAnnot | SymbolFlag.DefTypeParam))
                    {
                        return true;
                    }
                }
                // Module scope — its globals are declared globals. If it has no binding,
                // the name is still fair game if it's in builtins (checked by caller).
            }
            return false;
        }

        private bool IsInsideFunction()
        {
            // `return`/`yield` are valid only inside a def/async-def/lambda. Class bodies
            // and comprehensions do not enclose function-scope semantics for these.
            foreach (Scope s in _scopeStack)
            {
                if (s.Kind == BlockKind.Function || s.Kind == BlockKind.Lambda) return true;
            }
            return false;
        }

        private bool IsInsideMethod()
        {
            // `__class__` is only implicit when inside a function that's directly inside a class body.
            bool sawFunc = false;
            foreach (Scope s in _scopeStack)
            {
                if (!sawFunc)
                {
                    if (s.Kind == BlockKind.Function) { sawFunc = true; continue; }
                }
                else
                {
                    if (s.Kind == BlockKind.Class) return true;
                    if (s.Kind == BlockKind.Function) { /* nested — still ok */ }
                    else return false;
                }
            }
            return false;
        }

        private void Emit(string code, string message, SourceSpan span)
        {
            _diagnostics.Add(new CompileDiagnostic(_filePath, code, ParseSeverity.Error, span, message));
        }
    }
}
