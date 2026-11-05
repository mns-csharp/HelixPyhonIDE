using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Validate
{
    /// <summary>
    /// Structural AST validation. Models the user-facing checks in CPython's
    /// <c>Python/ast.c</c> that do not require a symbol table — target-shape
    /// validation for assignments, starred-expression context, duplicate-parameter
    /// detection inside a single argument list, etc. Scope-dependent checks
    /// (<c>global</c>/<c>nonlocal</c>, <c>return</c> outside function) belong
    /// in the symtable pass.
    /// </summary>
    public sealed class AstValidator
    {
        private readonly string _filePath;
        private readonly List<CompileDiagnostic> _diagnostics;

        public AstValidator(string filePath)
        {
            _filePath = filePath ?? string.Empty;
            _diagnostics = new List<CompileDiagnostic>();
        }

        public IReadOnlyList<CompileDiagnostic> Diagnostics => _diagnostics;

        public void ValidateModule(Module m)
        {
            if (m == null) return;
            foreach (Stmt s in m.Body) VisitStmt(s);
        }

        // ---------- Statement visitors ----------

        private void VisitStmt(Stmt s)
        {
            if (s == null) return;
            switch (s)
            {
                case FunctionDef f: VisitFunction(f.Name, f.Args, f.Body, f.DecoratorList, f.Returns); break;
                case AsyncFunctionDef af: VisitFunction(af.Name, af.Args, af.Body, af.DecoratorList, af.Returns); break;
                case ClassDef c:
                    foreach (Expr d in c.DecoratorList) VisitExpr(d);
                    foreach (Expr b in c.Bases) VisitExpr(b);
                    foreach (Keyword k in c.Keywords) if (k.Value != null) VisitExpr(k.Value);
                    foreach (Stmt inner in c.Body) VisitStmt(inner);
                    break;
                case Return r: if (r.Value != null) VisitExpr(r.Value); break;
                case Delete d:
                    foreach (Expr t in d.Targets)
                    {
                        // `del 1` → Delete(Constant). CPython flags as
                        // "cannot delete literal". Handle before generic target-shape rule.
                        if (t is Constant cnstTarget)
                        {
                            EmitError(VirtualCompilerDiagnosticCodes.AstCannotDeleteLiteral,
                                "cannot delete " + DescribeConstant(cnstTarget), cnstTarget.Span);
                            continue;
                        }
                        VisitAssignTarget(t, "del");
                    }
                    break;
                case Assign a:
                    foreach (Expr t in a.Targets)
                    {
                        VisitAssignTarget(t, "assign");
                        CheckForMultipleStarredTargets(t);
                    }
                    if (a.Value != null) VisitExpr(a.Value);
                    break;
                case AugAssign aa:
                    VisitAssignTarget(aa.Target, "augmented assign");
                    VisitExpr(aa.Value);
                    break;
                case AnnAssign an:
                    VisitAssignTarget(an.Target, "annotated assign");
                    if (an.Value != null) VisitExpr(an.Value);
                    if (!IsValidAnnotationTarget(an.Target))
                    {
                        EmitError(VirtualCompilerDiagnosticCodes.AstInvalidAnnotationTarget,
                            "illegal target for annotation", an.Target.Span);
                    }
                    break;
                case For f2:
                    VisitAssignTarget(f2.Target, "for");
                    VisitExpr(f2.Iter);
                    foreach (Stmt ss in f2.Body) VisitStmt(ss);
                    foreach (Stmt ss in f2.OrElse) VisitStmt(ss);
                    break;
                case AsyncFor af2:
                    VisitAssignTarget(af2.Target, "async for");
                    VisitExpr(af2.Iter);
                    foreach (Stmt ss in af2.Body) VisitStmt(ss);
                    foreach (Stmt ss in af2.OrElse) VisitStmt(ss);
                    break;
                case While w:
                    VisitExpr(w.Test);
                    foreach (Stmt ss in w.Body) VisitStmt(ss);
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
                        if (it.OptionalVars != null) VisitAssignTarget(it.OptionalVars, "with");
                    }
                    foreach (Stmt ss in wi.Body) VisitStmt(ss);
                    break;
                case AsyncWith aw:
                    foreach (WithItem it in aw.Items)
                    {
                        VisitExpr(it.ContextExpr);
                        if (it.OptionalVars != null) VisitAssignTarget(it.OptionalVars, "with");
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
                case ExprStmt es: VisitExpr(es.Value); break;
                case Match m:
                    VisitExpr(m.Subject);
                    foreach (MatchCase c in m.Cases)
                    {
                        if (c.Guard != null) VisitExpr(c.Guard);
                        foreach (Stmt ss in c.Body) VisitStmt(ss);
                    }
                    break;
            }
        }

        private void VisitFunction(string name, Arguments args, IReadOnlyList<Stmt> body,
            IReadOnlyList<Expr> decorators, Expr returns)
        {
            if (args != null) CheckArgumentsForDuplicates(args);
            foreach (Expr d in decorators) VisitExpr(d);
            if (returns != null) VisitExpr(returns);
            if (args != null)
            {
                foreach (Expr def in args.Defaults)
                {
                    VisitExpr(def);
                    CheckMutableDefaultArgument(def);
                }
                foreach (Expr def in args.KwDefaults)
                {
                    if (def == null) continue;
                    VisitExpr(def);
                    CheckMutableDefaultArgument(def);
                }
            }
            foreach (Stmt s in body) VisitStmt(s);
        }

        /// <summary>
        /// Flag mutable literal defaults (<c>[]</c>, <c>{}</c>, <c>set()</c>, <c>list()</c>, <c>dict()</c>).
        /// The classic Python gotcha: the default is evaluated once at def-time and
        /// shared across all calls, so mutating it persists between invocations.
        /// </summary>
        private void CheckMutableDefaultArgument(Expr def)
        {
            if (def == null) return;
            string kind = ClassifyMutableDefault(def);
            if (kind != null)
            {
                _diagnostics.Add(new CompileDiagnostic(_filePath,
                    VirtualCompilerDiagnosticCodes.AstMutableDefaultArgument,
                    ParseSeverity.Warning, def.Span,
                    "mutable default argument (" + kind + ") is evaluated once at function definition; "
                    + "use None and construct inside the body instead"));
            }
        }

        private static string ClassifyMutableDefault(Expr def)
        {
            if (def is Ast.ListExpr) return "list literal";
            if (def is Dict) return "dict literal";
            if (def is Ast.SetExpr) return "set literal";
            if (def is Call c && c.Func is Name n &&
                (c.Args == null || c.Args.Count == 0) &&
                (c.Keywords == null || c.Keywords.Count == 0))
            {
                if (n.Id == "list") return "list()";
                if (n.Id == "dict") return "dict()";
                if (n.Id == "set") return "set()";
            }
            return null;
        }

        private void CheckArgumentsForDuplicates(Arguments a)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            void check(Arg arg)
            {
                if (arg == null || string.IsNullOrEmpty(arg.Name)) return;
                if (!seen.Add(arg.Name))
                {
                    EmitError(VirtualCompilerDiagnosticCodes.SymDuplicateParameter,
                        "duplicate argument '" + arg.Name + "' in function definition", arg.Span);
                }
            }
            foreach (Arg p in a.PosOnlyArgs) check(p);
            foreach (Arg p in a.Args) check(p);
            check(a.VarArg);
            foreach (Arg p in a.KwOnlyArgs) check(p);
            check(a.KwArg);
        }

        private void VisitHandler(Ast.ExceptHandler h)
        {
            if (h.Type != null) VisitExpr(h.Type);
            foreach (Stmt s in h.Body) VisitStmt(s);
        }

        // ---------- Assignment-target validation ----------

        private void VisitAssignTarget(Expr e, string context)
        {
            if (e == null) return;
            switch (e)
            {
                case Name n:
                    if (IsReservedName(n.Id))
                    {
                        EmitError(VirtualCompilerDiagnosticCodes.AstAssignToReserved,
                            "cannot " + context + " to '" + n.Id + "'", n.Span);
                    }
                    VisitExpr(e);
                    return;
                case Constant cnst:
                    // `None = x` / `True = x` etc. lower to Constant targets via the literal
                    // lowering. Flag them as assigning to a literal — reserved-name class.
                    EmitError(VirtualCompilerDiagnosticCodes.AstAssignToReserved,
                        "cannot " + context + " to " + DescribeConstant(cnst), cnst.Span);
                    return;
                case AttributeExpr _:
                case Subscript _:
                    VisitExpr(e);
                    return;
                case Starred st:
                    // Starred is valid inside tuple/list targets; standalone Starred as a sole
                    // assignment target is an error (`*x = [1]` is a SyntaxError).
                    // We do not know here whether we're nested — a tuple/list target would have
                    // already dispatched the Starred via the list branch. Accept and let
                    // sub-validation run.
                    VisitAssignTarget(st.Value, context);
                    return;
                case Ast.TupleExpr tup:
                    foreach (Expr el in tup.Elts) VisitAssignTarget(el, context);
                    return;
                case Ast.ListExpr lst:
                    foreach (Expr el in lst.Elts) VisitAssignTarget(el, context);
                    return;
                default:
                    EmitError(VirtualCompilerDiagnosticCodes.AstInvalidAssignmentTarget,
                        "cannot " + context + " to " + DescribeExprForError(e), e.Span);
                    // Still visit to surface deeper issues.
                    VisitExpr(e);
                    return;
            }
        }

        private static bool IsValidAnnotationTarget(Expr e)
        {
            return e is Name || e is AttributeExpr || e is Subscript;
        }

        private static string DescribeExprForError(Expr e)
        {
            switch (e)
            {
                case Constant c:
                    if (c.Value == null) return "None";
                    if (c.Value is bool b) return b ? "True" : "False";
                    return "literal";
                case Call _: return "function call";
                case BinOpExpr _: return "expression";
                case UnaryOpExpr _: return "expression";
                case Lambda _: return "lambda";
                case IfExp _: return "conditional expression";
                case Compare _: return "comparison";
                case Await _: return "await expression";
                case Yield _: case YieldFrom _: return "yield expression";
                case BoolOpExpr _: return "expression";
                case ListComp _: case SetComp _: case DictComp _: case GeneratorExp _: return "comprehension";
                default: return "expression";
            }
        }

        // ---------- Expression visitor ----------

        private void VisitExpr(Expr e)
        {
            if (e == null) return;
            switch (e)
            {
                case BoolOpExpr b: foreach (Expr v in b.Values) VisitExpr(v); break;
                case Ast.NamedExpr ne: VisitExpr(ne.Target); VisitExpr(ne.Value); break;
                case BinOpExpr bi: VisitBinaryOperands(bi); break;
                case UnaryOpExpr u: VisitExpr(u.Operand); break;
                case Lambda lam:
                    if (lam.Args != null) CheckArgumentsForDuplicates(lam.Args);
                    VisitExpr(lam.Body);
                    break;
                case IfExp ie: VisitExpr(ie.Test); VisitExpr(ie.Body); VisitExpr(ie.OrElse); break;
                case Dict d:
                    for (int i = 0; i < d.Keys.Count; i++)
                    {
                        if (d.Keys[i] != null) VisitExpr(d.Keys[i]);
                        if (i < d.Values.Count) VisitExpr(d.Values[i]);
                    }
                    break;
                case Ast.SetExpr se: foreach (Expr v in se.Elts) VisitExpr(v); break;
                case ListComp lc:
                    VisitExpr(lc.Elt);
                    foreach (Comprehension g in lc.Generators) VisitComprehension(g);
                    break;
                case SetComp sc:
                    VisitExpr(sc.Elt);
                    foreach (Comprehension g in sc.Generators) VisitComprehension(g);
                    break;
                case DictComp dc:
                    VisitExpr(dc.Key); VisitExpr(dc.Value);
                    foreach (Comprehension g in dc.Generators) VisitComprehension(g);
                    break;
                case GeneratorExp ge:
                    VisitExpr(ge.Elt);
                    foreach (Comprehension g in ge.Generators) VisitComprehension(g);
                    break;
                case Await aw: VisitExpr(aw.Value); break;
                case Yield y: if (y.Value != null) VisitExpr(y.Value); break;
                case YieldFrom yf: VisitExpr(yf.Value); break;
                case Compare c:
                    VisitExpr(c.Left);
                    foreach (Expr cc in c.Comparators) VisitExpr(cc);
                    CheckIsCompareWithLiteral(c);
                    break;
                case Call call:
                    VisitExpr(call.Func);
                    foreach (Expr a in call.Args) VisitExpr(a);
                    foreach (Keyword k in call.Keywords) if (k.Value != null) VisitExpr(k.Value);
                    break;
                case FormattedValue fv: VisitExpr(fv.Value); if (fv.FormatSpec != null) VisitExpr(fv.FormatSpec); break;
                case JoinedStr js: foreach (Expr v in js.Values) VisitExpr(v); break;
                case Constant _: break;
                case AttributeExpr ae: VisitExpr(ae.Value); break;
                case Subscript s: VisitExpr(s.Value); VisitExpr(s.Slice); break;
                case Starred st:
                    // Starred appears inside tuple/list targets, call arguments, and collection
                    // literals. Standalone use (outside those contexts) is a compile-time error
                    // and would have been caught by VisitAssignTarget when used as a target.
                    VisitExpr(st.Value);
                    break;
                case Name _: break;
                case Ast.ListExpr le: foreach (Expr v in le.Elts) VisitExpr(v); break;
                case Ast.TupleExpr te: foreach (Expr v in te.Elts) VisitExpr(v); break;
                case Ast.Slice sl:
                    if (sl.Lower != null) VisitExpr(sl.Lower);
                    if (sl.Upper != null) VisitExpr(sl.Upper);
                    if (sl.Step != null) VisitExpr(sl.Step);
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

        private void VisitComprehension(Comprehension g)
        {
            VisitAssignTarget(g.Target, "comprehension");
            VisitExpr(g.Iter);
            foreach (Expr i in g.Ifs) VisitExpr(i);
        }

        private static bool IsReservedName(string id)
        {
            // CPython flags assignments to these identifiers as SyntaxError at ast-validate time.
            return id == "__debug__" || id == "None" || id == "True" || id == "False";
        }

        private static string DescribeConstant(Constant c)
        {
            if (c.Value == null) return "'None'";
            if (c.Value is bool b) return b ? "'True'" : "'False'";
            if (c.Value is string) return "literal";
            if (c.Value is int || c.Value is long || c.Value is double || c.Value is decimal) return "literal";
            if (c.Value is ConstantEllipsis) return "'Ellipsis'";
            return "literal";
        }

        private void CheckForMultipleStarredTargets(Expr target)
        {
            // Only tuple/list targets can carry starred elements. Flag more than one star.
            IReadOnlyList<Expr> elts = null;
            if (target is Ast.TupleExpr tup) elts = tup.Elts;
            else if (target is Ast.ListExpr lst) elts = lst.Elts;
            if (elts == null) return;

            int starred = 0;
            Python.Text.SourceSpan firstExtraSpan = default;
            foreach (Expr e in elts)
            {
                if (e is Starred)
                {
                    starred++;
                    if (starred == 2) { firstExtraSpan = e.Span; break; }
                }
            }
            if (starred >= 2)
            {
                EmitError(VirtualCompilerDiagnosticCodes.AstMultipleStarredInAssign,
                    "multiple starred expressions in assignment", firstExtraSpan);
            }
        }

        /// <summary>
        /// Emit <see cref="VirtualCompilerDiagnosticCodes.CompIsCompareLiteral"/> for
        /// <c>x is 42</c> / <c>x is "s"</c> style comparisons. Only <c>None</c>,
        /// <c>True</c>, and <c>False</c> (singletons with well-defined identity) are
        /// allowed to be compared with <c>is</c>. Ports CPython's
        /// <c>ast_preprocess.c</c> <c>SyntaxWarning</c>.
        /// </summary>
        private void CheckIsCompareWithLiteral(Compare c)
        {
            for (int i = 0; i < c.Ops.Count; i++)
            {
                CmpOp op = c.Ops[i];
                if (op != CmpOp.Is && op != CmpOp.IsNot) continue;
                Expr rhs = i < c.Comparators.Count ? c.Comparators[i] : null;
                if (IsNonInternedLiteral(rhs))
                {
                    string opText = op == CmpOp.Is ? "is" : "is not";
                    _diagnostics.Add(new CompileDiagnostic(_filePath,
                        VirtualCompilerDiagnosticCodes.CompIsCompareLiteral,
                        ParseSeverity.Warning, rhs.Span,
                        "'" + opText + "' with a non-singleton literal; identity comparison is "
                        + "implementation-defined — use '==' instead"));
                }
            }
        }

        /// <summary>A literal that is NOT one of the interned singletons
        /// (<c>None</c>, <c>True</c>, <c>False</c>) where <c>is</c> is well-defined.</summary>
        private static bool IsNonInternedLiteral(Expr e)
        {
            if (e is Constant ellipsis && ellipsis.Value is ConstantEllipsis) return false;
            if (!(e is Constant c)) return false;
            if (c.Value == null) return false;            // None — allowed
            if (c.Value is bool) return false;            // True/False — allowed
            return true;                                  // numbers, strings, bytes — flag
        }

        // ---------- Emit ----------

        private void EmitError(string code, string message, Python.Text.SourceSpan span)
        {
            _diagnostics.Add(new CompileDiagnostic(_filePath, code, ParseSeverity.Error, span, message));
        }
    }
}
