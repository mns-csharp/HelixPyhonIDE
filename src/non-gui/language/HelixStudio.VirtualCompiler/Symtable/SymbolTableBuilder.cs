using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Symtable
{
    /// <summary>
    /// Pass 1 of the symbol-table build. Walks the AST, constructs the scope
    /// tree, and records every definition/use flag per name per scope. This is
    /// the C# port of <c>symtable_enter_block</c> / <c>symtable_visit_*</c>
    /// family from CPython's <c>Python/symtable.c</c>.
    /// </summary>
    public sealed class SymbolTableBuilder
    {
        private readonly string _filePath;
        private readonly List<CompileDiagnostic> _diagnostics;
        private readonly Dictionary<AstNode, Scope> _scopeByNode = new Dictionary<AstNode, Scope>();
        // Per-scope def tracker for the PY3007 duplicate-definition lint. Tracks, per name, whether
        // it is mid-@overload-sequence and whether ANY of its definitions was guarded by an if/try
        // branch (so mutually-exclusive/conditional redefinitions are not flagged as shadows).
        private sealed class DefTrack
        {
            public bool InOverloadSequence;
            public bool AnyConditional;
        }

        private readonly Stack<Dictionary<string, DefTrack>> _defNamesStack = new Stack<Dictionary<string, DefTrack>>();

        // >0 while visiting the body of an if/elif/else or try/except/else — definitions recorded
        // here are conditional and never unconditionally coexist with a sibling of the same name.
        private int _conditionalDepth;
        private Scope _current;
        private Scope _root;

        public SymbolTableBuilder(string filePath)
        {
            _filePath = filePath ?? string.Empty;
            _diagnostics = new List<CompileDiagnostic>();
        }

        public IReadOnlyList<CompileDiagnostic> Diagnostics { get { return _diagnostics; } }

        public SymbolTable Build(Module module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            _root = new Scope(BlockKind.Module, "<module>", module, module.Span, null);
            _scopeByNode[module] = _root;
            _current = _root;
            _defNamesStack.Push(new Dictionary<string, DefTrack>(StringComparer.Ordinal));
            try
            {
                foreach (Stmt s in module.Body) VisitStmt(s);
            }
            finally { _defNamesStack.Pop(); }
            return new SymbolTable(_root, module, _scopeByNode);
        }

        // ---------- Scope management ----------

        private Scope EnterBlock(BlockKind kind, string name, AstNode node, SourceSpan span)
        {
            Scope child = _current.AddChild(kind, name, node, span);
            if (node != null) _scopeByNode[node] = child;
            _current = child;
            return child;
        }

        private void ExitBlock()
        {
            if (_current.Parent != null) _current = _current.Parent;
        }

        private void AddDef(string name, SymbolFlag flag, SourceSpan span)
        {
            if (string.IsNullOrEmpty(name)) return;

            // `global x` anywhere in a scope means that name never becomes local
            // here. CPython emits a SyntaxError for `global x; x = 1` conflict
            // only when both appear in the same block; we defer that to the
            // NameResolver (Pass 2). Here we just record the flag.
            Symbol s = _current.GetOrCreate(name);
            s.AddFlag(flag, span);
            // An assignment in a scope that declared `global name` creates or
            // updates the module binding. Record that binding on the root too so
            // later module-level loads resolve even when initialization is lazy
            // inside a helper function.
            if (_current != _root
                && _current.IsDeclaredGlobal(name)
                && (flag & (SymbolFlag.DefLocal | SymbolFlag.DefAnnot)) != 0)
            {
                _root.GetOrCreate(name).AddFlag(SymbolFlag.DefLocal, span);
            }
        }

        // ---------- Statement visitors ----------

        private void VisitStmt(Stmt s)
        {
            if (s == null) return;
            switch (s)
            {
                case FunctionDef f: VisitFunctionDef(f, f.Name, f.Args, f.Body, f.DecoratorList, f.Returns, f.TypeParams, false, f.Span); break;
                case AsyncFunctionDef af: VisitFunctionDef(af, af.Name, af.Args, af.Body, af.DecoratorList, af.Returns, af.TypeParams, true, af.Span); break;
                case ClassDef c: VisitClassDef(c); break;
                case Return r:
                    if (r.Value != null) { VisitExpr(r.Value); _current.HasReturnWithValue = true; }
                    break;
                case Delete d: foreach (Expr t in d.Targets) VisitExpr(t); break;
                case Assign a:
                    // Visit RHS first (mirrors CPython order; affects walrus sequencing).
                    if (a.Value != null) VisitExpr(a.Value);
                    foreach (Expr t in a.Targets) VisitTarget(t);
                    break;
                case AugAssign aug:
                    VisitExpr(aug.Value);
                    VisitTarget(aug.Target);
                    break;
                case AnnAssign an:
                    VisitExpr(an.Annotation);
                    if (an.Value != null) VisitExpr(an.Value);
                    if (an.Target is Name nm && an.Simple)
                    {
                        // Simple annotated assignment: `x: int [= ...]`
                        AddDef(nm.Id, SymbolFlag.DefAnnot, nm.Span);
                        if (an.Value != null) AddDef(nm.Id, SymbolFlag.DefLocal, nm.Span);
                    }
                    else
                    {
                        VisitTarget(an.Target);
                    }
                    break;
                case For f2:
                    VisitExpr(f2.Iter);
                    VisitTarget(f2.Target);
                    VisitStmts(f2.Body);
                    VisitStmts(f2.OrElse);
                    break;
                case AsyncFor af2:
                    VisitExpr(af2.Iter);
                    VisitTarget(af2.Target);
                    VisitStmts(af2.Body);
                    VisitStmts(af2.OrElse);
                    break;
                case While w:
                    VisitExpr(w.Test); VisitStmts(w.Body); VisitStmts(w.OrElse); break;
                case If i:
                    VisitExpr(i.Test);
                    _conditionalDepth++;
                    VisitStmts(i.Body); VisitStmts(i.OrElse);
                    _conditionalDepth--;
                    break;
                case With wi: VisitWithItems(wi.Items); VisitStmts(wi.Body); break;
                case AsyncWith aw: VisitWithItems(aw.Items); VisitStmts(aw.Body); break;
                case Try t:
                    _conditionalDepth++;
                    VisitStmts(t.Body);
                    foreach (Ast.ExceptHandler h in t.Handlers) VisitHandler(h);
                    VisitStmts(t.OrElse);
                    _conditionalDepth--;
                    VisitStmts(t.FinalBody); break;   // finally always runs — not conditional
                case TryStar ts:
                    _conditionalDepth++;
                    VisitStmts(ts.Body);
                    foreach (Ast.ExceptHandler h in ts.Handlers) VisitHandler(h);
                    VisitStmts(ts.OrElse);
                    _conditionalDepth--;
                    VisitStmts(ts.FinalBody); break;
                case Raise r2:
                    if (r2.Exc != null) VisitExpr(r2.Exc);
                    if (r2.Cause != null) VisitExpr(r2.Cause);
                    break;
                case Assert ass:
                    VisitExpr(ass.Test);
                    if (ass.Msg != null) VisitExpr(ass.Msg);
                    break;
                case Import imp:
                    foreach (Alias a in imp.Names)
                    {
                        // `import pkg.sub` binds the top-level `pkg`, not the dotted name.
                        string local = !string.IsNullOrEmpty(a.AsName) ? a.AsName : TopDotted(a.Name);
                        AddDef(local, SymbolFlag.DefImport, a.Span);
                    }
                    break;
                case ImportFrom imf:
                    foreach (Alias a in imf.Names)
                    {
                        if (a.Name == "*") _current.HasStarImport = true;
                        if (a.Name == "*") continue;  // star import — we can't enumerate names
                        string local = !string.IsNullOrEmpty(a.AsName) ? a.AsName : a.Name;
                        AddDef(local, SymbolFlag.DefImport, a.Span);
                    }
                    break;
                case Global g:
                    foreach (string n in g.Names)
                    {
                        Symbol existing;
                        if (_current.TryGetSymbol(n, out existing) &&
                            existing.Has(SymbolFlag.DefLocal | SymbolFlag.DefParam | SymbolFlag.Use | SymbolFlag.DefAnnot))
                        {
                            _diagnostics.Add(new CompileDiagnostic(_filePath,
                                VirtualCompilerDiagnosticCodes.CompGlobalAfterAssign,
                                ParseSeverity.Error, g.Span,
                                "name '" + n + "' is assigned to before global declaration"));
                        }
                        _current.DeclareGlobal(n);
                        AddDef(n, SymbolFlag.DefGlobal, g.Span);
                    }
                    break;
                case Nonlocal nl:
                    foreach (string n in nl.Names)
                    {
                        Symbol existing;
                        if (_current.TryGetSymbol(n, out existing) &&
                            existing.Has(SymbolFlag.DefLocal | SymbolFlag.DefParam | SymbolFlag.Use | SymbolFlag.DefAnnot))
                        {
                            _diagnostics.Add(new CompileDiagnostic(_filePath,
                                VirtualCompilerDiagnosticCodes.CompNonlocalAfterAssign,
                                ParseSeverity.Error, nl.Span,
                                "name '" + n + "' is assigned to before nonlocal declaration"));
                        }
                        _current.DeclareNonlocal(n);
                        AddDef(n, SymbolFlag.DefFree, nl.Span);
                    }
                    break;
                case ExprStmt e: VisitExpr(e.Value); break;
                case Match m:
                    VisitExpr(m.Subject);
                    foreach (MatchCase mc in m.Cases)
                    {
                        VisitPattern(mc.Pattern);
                        if (mc.Guard != null) VisitExpr(mc.Guard);
                        VisitStmts(mc.Body);
                    }
                    break;
                case TypeAlias ta:
                    if (ta.Name is Name tn) AddDef(tn.Id, SymbolFlag.DefLocal, tn.Span);
                    VisitExpr(ta.Value);
                    break;
            }
        }

        private void VisitStmts(IReadOnlyList<Stmt> list)
        {
            foreach (Stmt s in list) VisitStmt(s);
        }

        private void VisitFunctionDef(AstNode funcNode, string name, Arguments args, IReadOnlyList<Stmt> body,
            IReadOnlyList<Expr> decorators, Expr returns, IReadOnlyList<TypeParam> typeParams,
            bool isAsync, SourceSpan span)
        {
            // Decorators and default expressions are evaluated in the enclosing scope.
            foreach (Expr d in decorators) VisitExpr(d);
            if (args != null)
            {
                foreach (Expr def in args.Defaults) VisitExpr(def);
                foreach (Expr def in args.KwDefaults) if (def != null) VisitExpr(def);
            }
            // The function's name binds in the enclosing scope.
            AddDef(name, SymbolFlag.DefLocal, span);
            CheckDuplicateDef(name, decorators, span, "function");

            // Enter function block.
            Scope fnScope = EnterBlock(BlockKind.Function, name, funcNode, span);
            fnScope.IsAsync = isAsync;
            _defNamesStack.Push(new Dictionary<string, DefTrack>(StringComparer.Ordinal));
            // Conditionality is relative to the name's own scope: a def inside this body is not
            // "conditional" just because the whole function sits in an outer if/try branch.
            int savedFnCond = _conditionalDepth;
            _conditionalDepth = 0;
            try
            {
                if (typeParams != null && typeParams.Count > 0)
                {
                    // PEP 695 type params live in an intermediate TypeParam scope; for
                    // the MVP we just bind them at the outer edge of the function block.
                    foreach (TypeParam tp in typeParams)
                    {
                        AddDef(tp.Name, SymbolFlag.DefTypeParam, tp.Span);
                    }
                }
                if (args != null) DefineArguments(args);
                if (returns != null) VisitExpr(returns);
                VisitStmts(body);
            }
            finally
            {
                _conditionalDepth = savedFnCond;
                _defNamesStack.Pop();
                ExitBlock();
            }
        }

        private void DefineArguments(Arguments args)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            void defArg(Arg a)
            {
                if (a == null) return;
                if (!string.IsNullOrEmpty(a.Name))
                {
                    if (!seen.Add(a.Name))
                    {
                        _diagnostics.Add(new CompileDiagnostic(_filePath,
                            VirtualCompilerDiagnosticCodes.SymDuplicateParameter,
                            ParseSeverity.Error, a.Span,
                            "duplicate argument '" + a.Name + "' in function definition"));
                    }
                    AddDef(a.Name, SymbolFlag.DefParam, a.Span);
                }
                if (a.Annotation != null) VisitExpr(a.Annotation);
            }
            foreach (Arg a in args.PosOnlyArgs) defArg(a);
            foreach (Arg a in args.Args) defArg(a);
            defArg(args.VarArg);
            foreach (Arg a in args.KwOnlyArgs) defArg(a);
            defArg(args.KwArg);
        }

        private void VisitClassDef(ClassDef c)
        {
            foreach (Expr d in c.DecoratorList) VisitExpr(d);
            foreach (Expr b in c.Bases) VisitExpr(b);
            foreach (Keyword kw in c.Keywords) if (kw.Value != null) VisitExpr(kw.Value);
            AddDef(c.Name, SymbolFlag.DefLocal, c.Span);
            CheckDuplicateDef(c.Name, c.DecoratorList, c.Span, "class");

            Scope clsScope = EnterBlock(BlockKind.Class, c.Name, c, c.Span);
            _defNamesStack.Push(new Dictionary<string, DefTrack>(StringComparer.Ordinal));
            int savedClsCond = _conditionalDepth;
            _conditionalDepth = 0;
            try
            {
                foreach (TypeParam tp in c.TypeParams)
                {
                    AddDef(tp.Name, SymbolFlag.DefTypeParam, tp.Span);
                }
                VisitStmts(c.Body);
            }
            finally
            {
                _conditionalDepth = savedClsCond;
                _defNamesStack.Pop();
                ExitBlock();
            }
        }

        private void VisitWithItems(IReadOnlyList<WithItem> items)
        {
            foreach (WithItem it in items)
            {
                VisitExpr(it.ContextExpr);
                if (it.OptionalVars != null) VisitTarget(it.OptionalVars);
            }
        }

        private void VisitHandler(Ast.ExceptHandler h)
        {
            if (h.Type != null) VisitExpr(h.Type);
            if (!string.IsNullOrEmpty(h.Name))
            {
                AddDef(h.Name, SymbolFlag.DefLocal, h.Span);
            }
            VisitStmts(h.Body);
        }

        // ---------- Expression visitors ----------

        private void VisitExpr(Expr e)
        {
            if (e == null) return;
            switch (e)
            {
                case Name n: AddDef(n.Id, SymbolFlag.Use, n.Span); break;
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
                case ListComp lc: VisitComprehension(lc, "<listcomp>", lc.Elt, null, lc.Generators, lc.Span); break;
                case SetComp sc: VisitComprehension(sc, "<setcomp>", sc.Elt, null, sc.Generators, sc.Span); break;
                case DictComp dc: VisitComprehension(dc, "<dictcomp>", dc.Key, dc.Value, dc.Generators, dc.Span); break;
                case GeneratorExp ge: VisitComprehension(ge, "<genexpr>", ge.Elt, null, ge.Generators, ge.Span); break;
                case Await aw:
                    if (!IsInsideAsyncFunction()) { /* reserved for compile.c port */ }
                    VisitExpr(aw.Value);
                    break;
                case Yield y:
                    _current.HasYield = true;
                    if (y.Value != null) VisitExpr(y.Value);
                    break;
                case YieldFrom yf: _current.HasYield = true; VisitExpr(yf.Value); break;
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
                    // Walrus: target binds in enclosing function/module/comp-target scope
                    // per PEP 572. For MVP, bind in the *current* scope — subsequently
                    // correct for non-comprehension cases.
                    VisitExpr(ne.Value);
                    if (ne.Target is Name walrusName)
                    {
                        AddDef(walrusName.Id, SymbolFlag.DefLocal, walrusName.Span);
                    }
                    else
                    {
                        VisitTarget(ne.Target);
                    }
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
            EnterBlock(BlockKind.Lambda, "<lambda>", lam, lam.Span);
            _defNamesStack.Push(new Dictionary<string, DefTrack>(StringComparer.Ordinal));
            try
            {
                if (lam.Args != null) DefineArguments(lam.Args);
                VisitExpr(lam.Body);
            }
            finally
            {
                _defNamesStack.Pop();
                ExitBlock();
            }
        }

        private void VisitComprehension(Expr compNode, string blockName, Expr elt, Expr valueElt,
            IReadOnlyList<Comprehension> generators, SourceSpan span)
        {
            if (generators == null || generators.Count == 0) return;

            // First generator's iter evaluates in the enclosing scope (CPython rule).
            VisitExpr(generators[0].Iter);

            EnterBlock(BlockKind.Comprehension, blockName, compNode, span);
            _defNamesStack.Push(new Dictionary<string, DefTrack>(StringComparer.Ordinal));
            try
            {
                // Use a synthesized iteration variable ".0" (CPython style) for the first iter.
                _current.GetOrCreate(".0").AddFlag(SymbolFlag.DefParam, span);

                for (int i = 0; i < generators.Count; i++)
                {
                    Comprehension g = generators[i];
                    if (i > 0) VisitExpr(g.Iter);     // subsequent iters live in comp scope
                    VisitTarget(g.Target);
                    foreach (Expr ife in g.Ifs) VisitExpr(ife);
                }
                if (elt != null) VisitExpr(elt);
                if (valueElt != null) VisitExpr(valueElt);
            }
            finally
            {
                _defNamesStack.Pop();
                ExitBlock();
            }
        }

        // ---------- Assignment target binding ----------

        private void VisitTarget(Expr e)
        {
            if (e == null) return;
            switch (e)
            {
                case Name n: AddDef(n.Id, SymbolFlag.DefLocal, n.Span); break;
                case Ast.TupleExpr tup: foreach (Expr el in tup.Elts) VisitTarget(el); break;
                case Ast.ListExpr lst: foreach (Expr el in lst.Elts) VisitTarget(el); break;
                case Starred st: VisitTarget(st.Value); break;
                case AttributeExpr ae: VisitExpr(ae.Value); break;    // a.b = x → `a` is used, not bound
                case Subscript s: VisitExpr(s.Value); VisitExpr(s.Slice); break;
                default: VisitExpr(e); break;
            }
        }

        // ---------- Pattern visitor (capture bindings) ----------

        private void VisitPattern(Pattern p)
        {
            if (p == null) return;
            switch (p)
            {
                case MatchAs ma:
                    if (!string.IsNullOrEmpty(ma.Name))
                        AddDef(ma.Name, SymbolFlag.DefLocal, ma.Span);
                    if (ma.PatternOpt != null) VisitPattern(ma.PatternOpt);
                    break;
                case MatchStar ms:
                    if (!string.IsNullOrEmpty(ms.Name))
                        AddDef(ms.Name, SymbolFlag.DefLocal, ms.Span);
                    break;
                case MatchSequence mseq:
                    foreach (Pattern i in mseq.Patterns) VisitPattern(i);
                    break;
                case MatchMapping mm:
                    foreach (Expr k in mm.Keys) VisitExpr(k);
                    foreach (Pattern v in mm.Patterns) VisitPattern(v);
                    if (!string.IsNullOrEmpty(mm.Rest))
                        AddDef(mm.Rest, SymbolFlag.DefLocal, mm.Span);
                    break;
                case MatchClass mc:
                    VisitExpr(mc.Cls);
                    foreach (Pattern pos in mc.Patterns) VisitPattern(pos);
                    foreach (Pattern kp in mc.KwdPatterns) VisitPattern(kp);
                    break;
                case MatchOr mo:
                    foreach (Pattern alt in mo.Patterns) VisitPattern(alt);
                    break;
                case MatchValue mv: VisitExpr(mv.Value); break;
                case MatchSingleton _: break;
            }
        }

        // ---------- Duplicate-definition lint (PY3007) ----------

        /// <summary>
        /// In the currently-top scope's def-tracker, record that <paramref name="name"/> was
        /// defined. If a prior same-scope <c>def</c>/<c>class</c> with the same name already
        /// exists and neither is a legitimate <c>@typing.overload</c> stack member,
        /// emit <see cref="VirtualCompilerDiagnosticCodes.SymDuplicateDefinition"/>.
        /// </summary>
        private void CheckDuplicateDef(string name, IReadOnlyList<Expr> decorators,
            SourceSpan span, string kind)
        {
            if (string.IsNullOrEmpty(name) || _defNamesStack.Count == 0) return;
            // `_` is Python's conventional throwaway binding. Registration APIs such as
            // singledispatch and SymPy's predicate handlers intentionally define many decorated
            // functions under that name; each decorator retains the function before `_` is rebound.
            if (name == "_") return;
            Dictionary<string, DefTrack> top = _defNamesStack.Peek();
            bool currHasOverload = kind == "function" && HasOverloadDecorator(decorators);
            bool currIsPropertyAccessor = kind == "function" && HasPropertyAccessorDecorator(decorators);
            bool currConditional = _conditionalDepth > 0;

            DefTrack prev;
            if (top.TryGetValue(name, out prev))
            {
                if (currIsPropertyAccessor)
                {
                    // A @property getter followed by its @name.setter/.deleter/.getter is a single
                    // logical member (rich Console.file / _buffer_index), never a shadow.
                    prev.AnyConditional |= currConditional;
                }
                else if (prev.InOverloadSequence && currHasOverload)
                {
                    // still inside the overload sequence — OK
                }
                else if (prev.InOverloadSequence && !currHasOverload)
                {
                    // this is the implementation that closes the overload group — OK
                    prev.InOverloadSequence = false;
                }
                else if (prev.AnyConditional || currConditional)
                {
                    // At least one definition is guarded by an if/elif/else or try/except branch
                    // (platform or TYPE_CHECKING fallback, an import try/except, click's nargs
                    // if/elif/else `convert`) — the two never unconditionally coexist, so this is
                    // not a shadow.
                    prev.AnyConditional = true;
                }
                else
                {
                    _diagnostics.Add(new CompileDiagnostic(_filePath,
                        VirtualCompilerDiagnosticCodes.SymDuplicateDefinition,
                        ParseSeverity.Warning, span,
                        "redefinition of " + kind + " '" + name + "' shadows the previous definition"));
                }
            }
            else
            {
                top[name] = new DefTrack { InOverloadSequence = currHasOverload, AnyConditional = currConditional };
            }
        }

        private static bool HasOverloadDecorator(IReadOnlyList<Expr> decorators)
        {
            if (decorators == null) return false;
            foreach (Expr d in decorators)
            {
                string n = DecoratorName(d);
                if (n == "overload") return true;
            }
            return false;
        }

        // A property accessor decorator is an attribute access on the property name:
        // @name.setter / @name.deleter / @name.getter. Such a def re-binds the same logical
        // property member, so it must not count as a shadow of the @property getter.
        private static bool HasPropertyAccessorDecorator(IReadOnlyList<Expr> decorators)
        {
            if (decorators == null) return false;
            foreach (Expr d in decorators)
            {
                if (d is AttributeExpr ae
                    && (ae.Attr == "setter" || ae.Attr == "deleter" || ae.Attr == "getter"))
                    return true;
            }
            return false;
        }

        private static string DecoratorName(Expr d)
        {
            if (d is Name n) return n.Id;
            if (d is AttributeExpr ae) return ae.Attr;
            if (d is Call c) return DecoratorName(c.Func);
            return null;
        }

        // ---------- Misc helpers ----------

        private static string TopDotted(string dotted)
        {
            if (string.IsNullOrEmpty(dotted)) return dotted;
            int dot = dotted.IndexOf('.');
            return dot < 0 ? dotted : dotted.Substring(0, dot);
        }

        private bool IsInsideAsyncFunction()
        {
            for (Scope s = _current; s != null; s = s.Parent)
            {
                if (s.Kind == BlockKind.Function && s.IsAsync) return true;
                if (s.Kind == BlockKind.Class) return false;  // async doesn't cross class
            }
            return false;
        }
    }
}
