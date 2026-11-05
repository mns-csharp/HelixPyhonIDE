using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler.Ast;
using HelixPythonNamespace.VirtualCompiler.Symtable;

namespace HelixPythonNamespace.VirtualCompiler.Compile
{
    /// <summary>
    /// Phase 1.2 pass — C# port of the user-facing compile-time checks in
    /// CPython's <c>Python/compile.c</c> that need scope information the
    /// <see cref="SymbolTableBuilder"/> already collected. Examples:
    /// <c>await</c> outside an async function, <c>async for</c> / <c>async with</c>
    /// context, annotated names conflicting with <c>global</c>/<c>nonlocal</c>
    /// declarations in the same scope.
    /// </summary>
    public sealed class CompileValidator
    {
        private readonly string _filePath;
        private readonly SymbolTable _table;
        private readonly List<CompileDiagnostic> _diagnostics;
        private readonly Stack<Scope> _scopeStack = new Stack<Scope>();

        public CompileValidator(string filePath, SymbolTable table)
        {
            _filePath = filePath ?? string.Empty;
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _diagnostics = new List<CompileDiagnostic>();
        }

        public IReadOnlyList<CompileDiagnostic> Diagnostics { get { return _diagnostics; } }

        public void Validate()
        {
            _scopeStack.Clear();
            _scopeStack.Push(_table.Root);
            CheckScopeForAnnotationConflicts(_table.Root);
            CheckUnreachableCode(_table.Module.Body);
            try
            {
                foreach (Stmt s in _table.Module.Body) VisitStmt(s);
            }
            finally { _scopeStack.Pop(); }
        }

        private Scope Current { get { return _scopeStack.Peek(); } }

        // ---------- Scope-level checks ----------

        /// <summary>
        /// CPython flags <c>global x; x: int = 1</c> and <c>nonlocal x; x: int = 1</c>
        /// because an annotation on a name carries a namespace side-effect inconsistent
        /// with global/nonlocal declaration.
        /// </summary>
        private void CheckScopeForAnnotationConflicts(Scope s)
        {
            foreach (KeyValuePair<string, Symbol> kv in s.Symbols)
            {
                Symbol sym = kv.Value;
                if (!sym.Has(SymbolFlag.DefAnnot)) continue;
                if (s.IsDeclaredGlobal(sym.Name))
                {
                    Emit(VirtualCompilerDiagnosticCodes.CompAnnotatedGlobal,
                        "annotated name '" + sym.Name + "' can't be global", sym.FirstDefSpan);
                }
                if (s.IsDeclaredNonlocal(sym.Name))
                {
                    Emit(VirtualCompilerDiagnosticCodes.CompAnnotatedNonlocal,
                        "annotated name '" + sym.Name + "' can't be nonlocal", sym.FirstDefSpan);
                }
            }
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
                case AsyncFor af2:
                    if (!IsInsideAsyncFunction())
                    {
                        Emit(VirtualCompilerDiagnosticCodes.CompAsyncForOutsideAsync,
                            "'async for' outside async function", af2.Span);
                    }
                    VisitExpr(af2.Iter); VisitExpr(af2.Target);
                    VisitStmts(af2.Body); VisitStmts(af2.OrElse);
                    break;
                case AsyncWith aw:
                    if (!IsInsideAsyncFunction())
                    {
                        Emit(VirtualCompilerDiagnosticCodes.CompAsyncWithOutsideAsync,
                            "'async with' outside async function", aw.Span);
                    }
                    foreach (WithItem it in aw.Items)
                    {
                        VisitExpr(it.ContextExpr);
                        if (it.OptionalVars != null) VisitExpr(it.OptionalVars);
                    }
                    VisitStmts(aw.Body);
                    break;
                case For f2: VisitExpr(f2.Iter); VisitExpr(f2.Target); VisitStmts(f2.Body); VisitStmts(f2.OrElse); break;
                case While w: VisitExpr(w.Test); VisitStmts(w.Body); VisitStmts(w.OrElse); break;
                case If i: VisitExpr(i.Test); VisitStmts(i.Body); VisitStmts(i.OrElse); break;
                case With wi:
                    foreach (WithItem it in wi.Items)
                    {
                        VisitExpr(it.ContextExpr);
                        if (it.OptionalVars != null) VisitExpr(it.OptionalVars);
                    }
                    VisitStmts(wi.Body);
                    break;
                case Try t:
                    VisitStmts(t.Body);
                    foreach (Ast.ExceptHandler h in t.Handlers) VisitHandler(h);
                    VisitStmts(t.OrElse); VisitStmts(t.FinalBody);
                    CheckControlFlowInFinally(t.FinalBody);
                    break;
                case TryStar ts:
                    VisitStmts(ts.Body);
                    foreach (Ast.ExceptHandler h in ts.Handlers) VisitHandler(h);
                    VisitStmts(ts.OrElse); VisitStmts(ts.FinalBody);
                    CheckControlFlowInFinally(ts.FinalBody);
                    break;
                case Return r: if (r.Value != null) VisitExpr(r.Value); break;
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
                case Raise r2:
                    if (r2.Exc != null) VisitExpr(r2.Exc);
                    if (r2.Cause != null) VisitExpr(r2.Cause);
                    break;
                case Assert ass:
                    VisitExpr(ass.Test);
                    if (ass.Msg != null) VisitExpr(ass.Msg);
                    break;
                case ExprStmt e: VisitExpr(e.Value); break;
                case Match m:
                    VisitExpr(m.Subject);
                    foreach (MatchCase mc in m.Cases)
                    {
                        if (mc.Guard != null) VisitExpr(mc.Guard);
                        VisitStmts(mc.Body);
                    }
                    break;
                // Import / Global / Nonlocal / Pass / Break / Continue — nothing to do here
            }
        }

        private void VisitStmts(IReadOnlyList<Stmt> list)
        {
            CheckUnreachableCode(list);
            foreach (Stmt s in list) VisitStmt(s);
        }

        private void VisitFunctionLike(AstNode funcNode, Arguments args, IReadOnlyList<Stmt> body,
            IReadOnlyList<Expr> decorators, Expr returns)
        {
            foreach (Expr d in decorators) VisitExpr(d);
            if (args != null)
            {
                foreach (Expr def in args.Defaults) VisitExpr(def);
                foreach (Expr def in args.KwDefaults) if (def != null) VisitExpr(def);
            }
            Scope fn = _table.LookupScope(funcNode);
            if (fn == null) return;
            _scopeStack.Push(fn);
            CheckScopeForAnnotationConflicts(fn);
            try
            {
                if (args != null)
                {
                    void visitArg(Arg a) { if (a != null && a.Annotation != null) VisitExpr(a.Annotation); }
                    foreach (Arg a in args.PosOnlyArgs) visitArg(a);
                    foreach (Arg a in args.Args) visitArg(a);
                    visitArg(args.VarArg);
                    foreach (Arg a in args.KwOnlyArgs) visitArg(a);
                    visitArg(args.KwArg);
                }
                if (returns != null) VisitExpr(returns);
                VisitStmts(body);
            }
            finally { _scopeStack.Pop(); }
        }

        private void VisitClassDef(ClassDef c)
        {
            foreach (Expr d in c.DecoratorList) VisitExpr(d);
            foreach (Expr b in c.Bases) VisitExpr(b);
            foreach (Keyword kw in c.Keywords) if (kw.Value != null) VisitExpr(kw.Value);
            Scope cls = _table.LookupScope(c);
            if (cls == null) return;
            _scopeStack.Push(cls);
            CheckScopeForAnnotationConflicts(cls);
            try { VisitStmts(c.Body); }
            finally { _scopeStack.Pop(); }
        }

        private void VisitHandler(Ast.ExceptHandler h)
        {
            if (h.Type != null) VisitExpr(h.Type);
            VisitStmts(h.Body);
        }

        // ---------- Expression walk ----------

        private void VisitExpr(Expr e)
        {
            if (e == null) return;
            switch (e)
            {
                case Await aw:
                    if (!IsInsideAsyncFunction())
                    {
                        Emit(VirtualCompilerDiagnosticCodes.CompAwaitOutsideAsync,
                            "'await' outside async function", aw.Span);
                    }
                    VisitExpr(aw.Value);
                    break;
                case Lambda lam: VisitLambda(lam); break;
                case ListComp lc: VisitComprehension(lc, lc.Elt, null, lc.Generators); break;
                case SetComp sc: VisitComprehension(sc, sc.Elt, null, sc.Generators); break;
                case DictComp dc: VisitComprehension(dc, dc.Key, dc.Value, dc.Generators); break;
                case GeneratorExp ge: VisitComprehension(ge, ge.Elt, null, ge.Generators); break;
                case BoolOpExpr b: foreach (Expr v in b.Values) VisitExpr(v); break;
                case BinOpExpr bi: VisitBinaryOperands(bi); break;
                case UnaryOpExpr u: VisitExpr(u.Operand); break;
                case IfExp ie: VisitExpr(ie.Test); VisitExpr(ie.Body); VisitExpr(ie.OrElse); break;
                case Dict d:
                    for (int i = 0; i < d.Keys.Count; i++)
                    {
                        if (d.Keys[i] != null) VisitExpr(d.Keys[i]);
                        if (i < d.Values.Count) VisitExpr(d.Values[i]);
                    }
                    break;
                case Ast.SetExpr se: foreach (Expr v in se.Elts) VisitExpr(v); break;
                case Yield y: if (y.Value != null) VisitExpr(y.Value); break;
                case YieldFrom yf: VisitExpr(yf.Value); break;
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
                case Subscript sub: VisitExpr(sub.Value); VisitExpr(sub.Slice); break;
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
                    break;
                case Name _: case Constant _: break;
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
            CheckScopeForAnnotationConflicts(lScope);
            try { VisitExpr(lam.Body); }
            finally { _scopeStack.Pop(); }
        }

        private void VisitComprehension(Expr compNode, Expr elt, Expr valueElt, IReadOnlyList<Comprehension> gens)
        {
            if (gens == null || gens.Count == 0) return;
            VisitExpr(gens[0].Iter);

            Scope cScope = _table.LookupScope(compNode);
            if (cScope == null) return;

            // Flag `async for` inside comprehension when enclosing function isn't async.
            bool compHasAsync = false;
            SourceSpan asyncSpan = default;
            foreach (Comprehension g in gens)
            {
                if (g.IsAsync) { compHasAsync = true; asyncSpan = g.Target != null ? g.Target.Span : compNode.Span; break; }
            }
            if (compHasAsync && !IsInsideAsyncFunction())
            {
                Emit(VirtualCompilerDiagnosticCodes.CompAsyncComprehensionOutsideAsync,
                    "asynchronous comprehension outside of async function", asyncSpan);
            }

            _scopeStack.Push(cScope);
            CheckScopeForAnnotationConflicts(cScope);
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
            finally { _scopeStack.Pop(); }
        }

        // ---------- Context queries ----------

        /// <summary>
        /// Walk the scope stack outward. <c>await</c>/<c>async for</c>/<c>async with</c>
        /// are permitted inside an <c>async def</c>; they're also permitted inside a
        /// nested non-async scope only if the nearest enclosing function-like scope is
        /// async (CPython's compile.c rule). Class scopes block the search since you
        /// can't await inside a method body of a non-async class method, even if the
        /// enclosing module defines an async function.
        /// </summary>
        private bool IsInsideAsyncFunction()
        {
            foreach (Scope s in _scopeStack)
            {
                if (s.Kind == BlockKind.Function) return s.IsAsync;
                if (s.Kind == BlockKind.Lambda) return false;
                if (s.Kind == BlockKind.Class) return false;
                if (s.Kind == BlockKind.Module) return false;
                // Comprehension / TypeParam / etc.: keep searching outward.
            }
            return false;
        }

        // ---------- Unreachable code (PY4015) ----------

        /// <summary>
        /// Flag statements that appear after an unconditional terminator
        /// (<c>return</c>/<c>raise</c>/<c>break</c>/<c>continue</c>) in the same
        /// statement list. Single-block, no flow analysis — conditional terminators
        /// inside nested <c>if</c>/loops do NOT propagate reachability information
        /// to the enclosing block, matching mypy's default conservative behavior.
        /// One diagnostic per block to avoid noise.
        /// </summary>
        private void CheckUnreachableCode(IReadOnlyList<Stmt> stmts)
        {
            if (stmts == null || stmts.Count < 2) return;
            for (int i = 0; i < stmts.Count - 1; i++)
            {
                if (!IsTerminator(stmts[i])) continue;
                Stmt first = stmts[i + 1];
                Emit(VirtualCompilerDiagnosticCodes.CompUnreachableCode,
                    "unreachable code after '" + TerminatorName(stmts[i]) + "'",
                    first.Span, ParseSeverity.Warning);
                return;
            }
        }

        private static bool IsTerminator(Stmt s)
        {
            return s is Return || s is Raise || s is Break || s is Continue;
        }

        private static string TerminatorName(Stmt s)
        {
            switch (s)
            {
                case Return _:   return "return";
                case Raise _:    return "raise";
                case Break _:    return "break";
                case Continue _: return "continue";
                default:         return "terminator";
            }
        }

        // ---------- Control-flow in finally (PY4014) ----------

        /// <summary>
        /// Walk each top-level statement of a <c>finally</c> body and warn when
        /// <c>return</c> / <c>break</c> / <c>continue</c> escapes the finally — this
        /// silently swallows any pending exception/return value. Ports CPython's
        /// <c>control_flow_in_finally_warning</c> from <c>Python/ast_preprocess.c</c>.
        /// Nested loops absorb their own break/continue; nested function/class
        /// definitions introduce new control-flow scopes, so neither descend.
        /// </summary>
        private void CheckControlFlowInFinally(IReadOnlyList<Stmt> finalBody)
        {
            if (finalBody == null || finalBody.Count == 0) return;
            foreach (Stmt s in finalBody) WalkFinallyStmt(s);
        }

        private void WalkFinallyStmt(Stmt s)
        {
            if (s == null) return;
            switch (s)
            {
                case Return _:
                    Emit(VirtualCompilerDiagnosticCodes.CompControlFlowInFinally,
                        "'return' in a 'finally' block", s.Span,
                        severity: ParseSeverity.Warning);
                    return;
                case Break _:
                    Emit(VirtualCompilerDiagnosticCodes.CompControlFlowInFinally,
                        "'break' in a 'finally' block", s.Span,
                        severity: ParseSeverity.Warning);
                    return;
                case Continue _:
                    Emit(VirtualCompilerDiagnosticCodes.CompControlFlowInFinally,
                        "'continue' in a 'finally' block", s.Span,
                        severity: ParseSeverity.Warning);
                    return;
                // Inner loops absorb their own break/continue — don't descend.
                case For _:
                case While _:
                case AsyncFor _:
                    return;
                // Nested scopes — their control flow doesn't escape our finally.
                case FunctionDef _:
                case AsyncFunctionDef _:
                case ClassDef _:
                    return;
                case If i:
                    foreach (Stmt ss in i.Body) WalkFinallyStmt(ss);
                    foreach (Stmt ss in i.OrElse) WalkFinallyStmt(ss);
                    return;
                case With wi:
                    foreach (Stmt ss in wi.Body) WalkFinallyStmt(ss);
                    return;
                case AsyncWith aw:
                    foreach (Stmt ss in aw.Body) WalkFinallyStmt(ss);
                    return;
                case Try tr:
                    foreach (Stmt ss in tr.Body) WalkFinallyStmt(ss);
                    foreach (Ast.ExceptHandler h in tr.Handlers)
                        foreach (Stmt ss in h.Body) WalkFinallyStmt(ss);
                    foreach (Stmt ss in tr.OrElse) WalkFinallyStmt(ss);
                    foreach (Stmt ss in tr.FinalBody) WalkFinallyStmt(ss);
                    return;
                case TryStar trs:
                    foreach (Stmt ss in trs.Body) WalkFinallyStmt(ss);
                    foreach (Ast.ExceptHandler h in trs.Handlers)
                        foreach (Stmt ss in h.Body) WalkFinallyStmt(ss);
                    foreach (Stmt ss in trs.OrElse) WalkFinallyStmt(ss);
                    foreach (Stmt ss in trs.FinalBody) WalkFinallyStmt(ss);
                    return;
                case Match m:
                    foreach (MatchCase c in m.Cases)
                        foreach (Stmt ss in c.Body) WalkFinallyStmt(ss);
                    return;
            }
        }

        private void Emit(string code, string message, SourceSpan span)
        {
            _diagnostics.Add(new CompileDiagnostic(_filePath, code, ParseSeverity.Error, span, message));
        }

        private void Emit(string code, string message, SourceSpan span, ParseSeverity severity)
        {
            _diagnostics.Add(new CompileDiagnostic(_filePath, code, severity, span, message));
        }
    }
}
