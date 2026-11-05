using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler.Ast;
using HelixPythonNamespace.VirtualCompiler.Symtable;
using HelixPythonNamespace.VirtualCompiler.Typing.Stubs;

namespace HelixPythonNamespace.VirtualCompiler.Typing
{
    /// <summary>
    /// Phase-3.0 type inferencer. Walks the module AST, maintains a
    /// <see cref="TypeEnv"/>, assigns each expression a <see cref="TypeRef"/>,
    /// and emits <c>PY6xxx</c> diagnostics for type-world errors (missing
    /// attributes, arity mismatches on calls against known signatures). This
    /// is a minimal first pass — it only narrows into cases the stubs or the
    /// workspace's own class definitions can answer confidently, and returns
    /// <see cref="UnknownRef"/> whenever it isn't sure.
    /// </summary>
    public sealed class TypeInferencer
    {
        private readonly string _filePath;
        private readonly SymbolTable _symtable;
        private readonly TypeshedIndex _stubs;
        private readonly WorkspaceIndex _workspace;
        private readonly List<CompileDiagnostic> _diagnostics;
        private readonly TypeEnv _env;
        private readonly Dictionary<string, ClassDef> _workspaceClasses;
        // Local names bound (via `from mod import Cls as Alias`) to a class OBJECT — so a call on them
        // constructs, even though the alias itself is not literally a class name. One inferencer runs
        // per module, so this stays module-scoped. See the instance-call decision in InferCall.
        private readonly HashSet<string> _classObjectNames = new HashSet<string>(StringComparer.Ordinal);
        // Names introduced by `X = TypeVar(...)` in this module — type parameters, not concrete types.
        private readonly HashSet<string> _typeVarNames = new HashSet<string>(StringComparer.Ordinal);
        // Local name -> fully-qualified class name for `from mod import Cls [as name]` (Cls is a class).
        // Lets the attribute-hierarchy walk follow a base into another module via WorkspaceIndex.
        private readonly Dictionary<string, string> _importedClassFqns = new Dictionary<string, string>(StringComparer.Ordinal);

        // True when an expression is a `TypeVar(...)` / `typing.TypeVar(...)` construction.
        private static bool IsTypeVarConstruction(Expr value)
        {
            Call call = value as Call;
            if (call == null) return false;
            if (call.Func is Name fn) return fn.Id == "TypeVar";
            if (call.Func is AttributeExpr fa) return fa.Attr == "TypeVar";
            return false;
        }

        // True when a type is a bare ClassRef naming a module TypeVar (`_T`, `AnyStr`-style). Such a
        // type is a parameter, not a concrete class, so it is neither a judgeable SOURCE nor a
        // checkable TARGET — treating it as gradual mirrors how unresolvable aliases are handled.
        private bool IsTypeVarName(TypeRef t)
        {
            ClassRef cr = t as ClassRef;
            return cr != null && _typeVarNames.Contains(cr.QualifiedName);
        }
        // Suppresses PY6001 "has no attribute" while a narrowing pass re-infers an attribute type
        // that the `if` test already inferred once — a second emit would double-report (iter 370).
        private bool _suppressAttrNotFound;

        public TypeInferencer(string filePath, SymbolTable symtable, TypeshedIndex stubs)
            : this(filePath, symtable, stubs, null) { }

        public TypeInferencer(string filePath, SymbolTable symtable, TypeshedIndex stubs, WorkspaceIndex workspace)
        {
            _filePath = filePath ?? string.Empty;
            _symtable = symtable;
            _stubs = stubs ?? new TypeshedIndex();
            _workspace = workspace;
            _diagnostics = new List<CompileDiagnostic>();
            _env = new TypeEnv();
            _workspaceClasses = new Dictionary<string, ClassDef>(StringComparer.Ordinal);
        }

        public IReadOnlyList<CompileDiagnostic> Diagnostics { get { return _diagnostics; } }

        public void CheckModule(Module m)
        {
            if (m == null) return;
            // Pass A: seed the module scope with every top-level class/function so that
            // forward references within the module resolve. Mirrors CPython's two-pass
            // semantic walk — we don't need bodies for this, just signatures.
            SeedModuleScope(m);
            // Pass B: walk statements and infer.
            foreach (Stmt s in m.Body) VisitStmt(s);
        }

        // ---------- Pass A: hoist declarations ----------

        private void SeedModuleScope(Module m)
        {
            foreach (Stmt s in m.Body) SeedStmt(s);
        }

        private void SeedStmt(Stmt s)
        {
            switch (s)
            {
                case ClassDef c:
                    _workspaceClasses[c.Name] = c;
                    _env.Bind(c.Name, new ClassRef(c.Name));
                    break;
                case Assign asn:
                    // `_T = TypeVar("_T")` — remember the name so annotations that mention it are
                    // treated as a type parameter (gradual), not a concrete class. Without this a bare
                    // `-> _T` collided with typeshed's internal `_T` symbol and looked "resolvable", so
                    // returning a concrete value false-flagged PY6005 (fastapi routing.py:264,
                    // `def __call__(self: _T, app: object) -> _T: return self`).
                    if (IsTypeVarConstruction(asn.Value))
                        foreach (Expr tgt in asn.Targets)
                            if (tgt is Name tn) _typeVarNames.Add(tn.Id);
                    break;
                case FunctionDef f:
                    _env.Bind(f.Name, BuildDecoratedFunctionBinding(f.Args, f.Returns, f.DecoratorList));
                    break;
                case AsyncFunctionDef af:
                    _env.Bind(af.Name, BuildDecoratedFunctionBinding(af.Args, af.Returns, af.DecoratorList));
                    break;
                case Import imp:
                    foreach (Alias a in imp.Names)
                    {
                        string bound = !string.IsNullOrEmpty(a.AsName) ? a.AsName : TopDotted(a.Name);
                        // Module import: bind to Any — member access via `mod.foo` is resolved
                        // in InferAttribute lazily against the workspace index when present.
                        _env.Bind(bound, AnyRef.Instance);
                    }
                    break;
                case ImportFrom imf:
                    foreach (Alias a in imf.Names)
                    {
                        if (a.Name == "*") continue;
                        string bound = !string.IsNullOrEmpty(a.AsName) ? a.AsName : a.Name;
                        TypeRef resolved = null;
                        if (_workspace != null && !string.IsNullOrEmpty(imf.Module) && imf.Level == 0)
                        {
                            string fqn = imf.Module + "." + a.Name;
                            resolved = _workspace.Resolve(fqn);
                            // Remember when this local name aliases a CLASS (collision-free FQN lookup):
                            // `from m import HTTPBase as HTTPBaseModel` makes HTTPBaseModel a class object,
                            // so `HTTPBaseModel(...)` constructs — it must not be mistaken for an instance
                            // call to __call__ just because the alias itself is not a class name (fastapi
                            // security/http.py, where a same-named security class shadows __call__).
                            // Loose lookup so a class re-exported through a package __init__ (imported
                            // as `pkg.Cls` but defined in `pkg.submodule.Cls`) is still recognised.
                            if (_workspace.ResolveClassDefLoose(fqn) != null)
                            {
                                _classObjectNames.Add(bound);
                                // Remember the FQN so a base named `bound` can be resolved across modules
                                // (a subclass inheriting Django's Lookup from db.models.lookups).
                                _importedClassFqns[bound] = fqn;
                            }
                        }
                        _env.Bind(bound, resolved ?? AnyRef.Instance);
                    }
                    break;
            }
        }

        // ---------- Pass B: statement walk ----------

        private void VisitStmt(Stmt s)
        {
            if (s == null) return;
            switch (s)
            {
                case FunctionDef f: VisitFunctionLike(f, f.Name, f.Args, f.Body, f.Returns, false); break;
                case AsyncFunctionDef af: VisitFunctionLike(af, af.Name, af.Args, af.Body, af.Returns, true); break;
                case ClassDef c: VisitClassDef(c); break;
                case Assign a:
                    {
                        TypeRef rhs = a.Value != null ? InferExpr(a.Value) : UnknownRef.Instance;
                        rhs = PreserveAssignedLiteral(a.Value, rhs);
                        foreach (Expr t in a.Targets) BindAssignTarget(t, rhs);
                        break;
                    }
                case AnnAssign an:
                    {
                        TypeRef annotType = AnnotationConverter.Convert(an.Annotation);
                        if (an.Value != null)
                        {
                            TypeRef valueType = InferExpr(an.Value);
                            // PY6008: the initializer's type must be assignable to the declared annotation
                            // (x: int = "hello"). Only flag when both sides are concrete/known — a None value
                            // against a non-Optional annotation is the ubiquitous under-annotated-Optional
                            // pattern CPython always accepts, so it is exempt (see AnnotationParityExempt).
                            if (annotType != null && !(annotType is UnknownRef)
                                && !AnnotationParityExempt(valueType, annotType)
                                && !SubtypeChecker.IsAssignable(valueType, annotType, _stubs)
                                && !DictDisplaySatisfiesTypedDict(an.Value, annotType)
                                && !LiteralConstantSatisfiesExpected(an.Value, annotType)
                                && !WorkspaceSubtypeSatisfies(valueType, annotType)
                                && !ClassValueSatisfiesTypeAnnotation(an.Value, annotType)
                                && !TypeCovarianceSatisfies(valueType, annotType)
                                && SourceTypeIsJudgeable(valueType)
                                && ExpectedTypeIsCheckable(annotType))
                            {
                                Emit(VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch,
                                    "value of type '" + valueType + "' is not assignable to annotated '"
                                    + annotType + "'", an.Value.Span);
                            }
                        }
                        BindAssignTarget(an.Target, annotType);
                        break;
                    }
                case AugAssign aug:
                    {
                        InferExpr(aug.Value);
                        BindAssignTarget(aug.Target, UnknownRef.Instance);
                        break;
                    }
                case For ff:
                    InferExpr(ff.Iter);
                    BindAssignTarget(ff.Target, UnknownRef.Instance);
                    foreach (Stmt ss in ff.Body) VisitStmt(ss);
                    foreach (Stmt ss in ff.OrElse) VisitStmt(ss);
                    break;
                case AsyncFor af2:
                    InferExpr(af2.Iter);
                    BindAssignTarget(af2.Target, UnknownRef.Instance);
                    foreach (Stmt ss in af2.Body) VisitStmt(ss);
                    foreach (Stmt ss in af2.OrElse) VisitStmt(ss);
                    break;
                case If i:
                    InferExpr(i.Test);
                    {
                        // Names live before the `if` — the only ones we merge across branches
                        // (branch-local temporaries are left to existing behavior).
                        List<string> preNames = new List<string>(_env.Current.Bindings.Keys);
                        var trueN = CollectNarrowings(i.Test, forTrueBranch: true);
                        var falseN = CollectNarrowings(i.Test, forTrueBranch: false);

                        // Visit each branch under its narrowing; capture the names' types at
                        // the branch's exit, then restore to the pre-if state.
                        var snap = _env.OverrideInCurrent(trueN);
                        Dictionary<string, TypeRef> trueExit;
                        try { foreach (Stmt ss in i.Body) VisitStmt(ss); }
                        finally { trueExit = SnapshotCurrentBindings(); _env.Restore(snap); }
                        bool trueTerminates = AlwaysTerminates(i.Body);

                        var snapElse = _env.OverrideInCurrent(falseN);
                        Dictionary<string, TypeRef> falseExit;
                        try { foreach (Stmt ss in i.OrElse) VisitStmt(ss); }
                        finally { falseExit = SnapshotCurrentBindings(); _env.Restore(snapElse); }
                        bool falseTerminates = i.OrElse != null && i.OrElse.Count > 0 && AlwaysTerminates(i.OrElse);

                        // Branch JOIN: a name's type after the `if` is the union of the types
                        // it has at the exit of each REACHABLE branch (a branch that always
                        // terminates — return/raise/break/continue/sys.exit — contributes
                        // nothing, since control never rejoins from it). This both narrows a
                        // guard clause (`if x is None: return` → x non-None afterwards,
                        // verify_crmsd.py) AND merges a re-assignment (`if x is None:
                        // x = len(...)` → x is int on both paths, crmsd.py:100).
                        foreach (string name in preNames)
                        {
                            TypeRef reachable = null;
                            TypeRef tv, fv;
                            if (!trueTerminates && trueExit.TryGetValue(name, out tv)) reachable = tv;
                            if (!falseTerminates && falseExit.TryGetValue(name, out fv))
                                reachable = reachable == null ? fv : JoinTypes(reachable, fv);
                            if (reachable != null) _env.Bind(name, reachable);
                        }

                        // Early-return guard on an ATTRIBUTE path: `if self.call is None: return` leaves
                        // self.call non-None for the rest of the scope. The name join above covers only
                        // simple locals; dotted paths live as overrides consulted by InferAttribute, so
                        // when exactly one branch always terminates, persist the reachable branch's
                        // attribute narrowings (fastapi dependencies/models.py: _impartial(self.call)
                        // after `if self.call is None: return`).
                        if (trueTerminates ^ falseTerminates)
                        {
                            var survivingN = trueTerminates ? falseN : trueN;
                            foreach (KeyValuePair<string, TypeRef> kv in survivingN)
                                if (kv.Key.IndexOf('.') >= 0) _env.Bind(kv.Key, kv.Value);
                        }
                    }
                    break;
                case While w:
                    InferExpr(w.Test);
                    {
                        var trueN = CollectNarrowings(w.Test, forTrueBranch: true);
                        var snap = _env.OverrideInCurrent(trueN);
                        try { foreach (Stmt ss in w.Body) VisitStmt(ss); }
                        finally { _env.Restore(snap); }
                        foreach (Stmt ss in w.OrElse) VisitStmt(ss);
                    }
                    break;
                case With wi:
                    foreach (WithItem it in wi.Items)
                    {
                        TypeRef ctxT = InferExpr(it.ContextExpr);
                        if (it.OptionalVars != null) BindAssignTarget(it.OptionalVars, UnknownRef.Instance);
                    }
                    foreach (Stmt ss in wi.Body) VisitStmt(ss);
                    break;
                case AsyncWith aw:
                    foreach (WithItem it in aw.Items)
                    {
                        InferExpr(it.ContextExpr);
                        if (it.OptionalVars != null) BindAssignTarget(it.OptionalVars, UnknownRef.Instance);
                    }
                    foreach (Stmt ss in aw.Body) VisitStmt(ss);
                    break;
                case Try t:
                    foreach (Stmt ss in t.Body) VisitStmt(ss);
                    foreach (Ast.ExceptHandler h in t.Handlers)
                    {
                        if (h.Type != null) InferExpr(h.Type);
                        if (!string.IsNullOrEmpty(h.Name)) _env.Bind(h.Name, UnknownRef.Instance);
                        foreach (Stmt ss in h.Body) VisitStmt(ss);
                    }
                    foreach (Stmt ss in t.OrElse) VisitStmt(ss);
                    foreach (Stmt ss in t.FinalBody) VisitStmt(ss);
                    break;
                case Return r:
                    {
                        TypeRef returned = r.Value != null ? InferExpr(r.Value) : NoneRef.Instance;
                        TypeRef expected = _env.Current.ExpectedReturnType;
                        if (expected != null && !(expected is UnknownRef)
                            && !AnnotationParityExempt(returned, expected)
                            && !SubtypeChecker.IsAssignable(returned, expected, _stubs)
                            && !DictDisplaySatisfiesTypedDict(r.Value, expected)
                            && !LiteralConstantSatisfiesExpected(r.Value, expected)
                            && !WorkspaceSubtypeSatisfies(returned, expected)
                            && !ReturnSatisfiesTypePredicate(returned, expected)
                            && !TypeCovarianceSatisfies(returned, expected)
                            && !IsSelfType(returned)
                            && !(r.Value != null && ClassValueSatisfiesTypeAnnotation(r.Value, expected))
                            && SourceTypeIsJudgeable(returned)
                            && ExpectedTypeIsCheckable(expected))
                        {
                            Emit(VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch,
                                "return type '" + returned + "' is not assignable to annotated '"
                                + expected + "'", r.Span);
                        }
                        break;
                    }
                case Raise rr:
                    if (rr.Exc != null) InferExpr(rr.Exc);
                    if (rr.Cause != null) InferExpr(rr.Cause);
                    break;
                case Assert ass:
                    InferExpr(ass.Test);
                    if (ass.Msg != null) InferExpr(ass.Msg);
                    break;
                case ExprStmt es: InferExpr(es.Value); break;
                // Import / Global / Nonlocal / Pass / Break / Continue — no-op here.
            }
        }

        private void VisitFunctionLike(AstNode funcNode, string name, Arguments args,
            IReadOnlyList<Stmt> body, Expr returns, bool isAsync)
        {
            TypeScope fn = _env.Push(name);
            fn.IsAsync = isAsync;
            if (returns != null) fn.ExpectedReturnType = AnnotationConverter.Convert(returns);
            try
            {
                if (args != null) BindArguments(args);
                foreach (Stmt s in body) VisitStmt(s);
            }
            finally { _env.Pop(); }
        }

        private void BindArguments(Arguments args)
        {
            foreach (Arg a in args.PosOnlyArgs) BindArg(a);
            foreach (Arg a in args.Args) BindArg(a);
            if (args.VarArg != null) BindArg(args.VarArg);
            foreach (Arg a in args.KwOnlyArgs) BindArg(a);
            if (args.KwArg != null) BindArg(args.KwArg);
        }

        private void BindArg(Arg a)
        {
            if (a == null || string.IsNullOrEmpty(a.Name)) return;
            TypeRef t = a.Annotation != null ? AnnotationConverter.Convert(a.Annotation) : AnyRef.Instance;
            _env.Bind(a.Name, t);
        }

        private void VisitClassDef(ClassDef c)
        {
            TypeScope cls = _env.Push(c.Name);
            cls.EnclosingClassName = c.Name;
            try
            {
                // Walk class body — methods get the enclosing-class marker to resolve `self`.
                foreach (Stmt s in c.Body)
                {
                    if (s is FunctionDef fn) VisitMethod(fn, c.Name, fn.Args, fn.Body, false);
                    else if (s is AsyncFunctionDef afn) VisitMethod(afn, c.Name, afn.Args, afn.Body, true);
                    else VisitStmt(s);
                }
            }
            finally { _env.Pop(); }
        }

        private void VisitMethod(AstNode node, string enclosingClass, Arguments args,
            IReadOnlyList<Stmt> body, bool isAsync)
        {
            TypeScope fn = _env.Push("<method>");
            fn.IsAsync = isAsync;
            fn.EnclosingClassName = enclosingClass;
            Expr returnAnnotation =
                (node is FunctionDef fnode) ? fnode.Returns :
                (node is AsyncFunctionDef afnode ? afnode.Returns : null);
            // `Self` (PEP 673) means "the enclosing class". Resolve it here, where the class is
            // known, so `def __enter__(self) -> Self: return self` type-checks — returning self (or
            // any instance of the class) satisfies a Self return. Before iter 360 Self stayed a bare
            // ClassRef("Self"), so every Self-returning method (__enter__/copy/builder chains) was a
            // PY6005 false positive.
            if (returnAnnotation != null)
                fn.ExpectedReturnType = SubstituteSelf(AnnotationConverter.Convert(returnAnnotation), enclosingClass);
            try
            {
                // self binds to the enclosing class instance type (ClassRef by bare name).
                if (args != null && args.Args.Count > 0 && args.Args[0].Name == "self")
                {
                    _env.Bind("self", new ClassRef(enclosingClass));
                    for (int i = 1; i < args.Args.Count; i++) BindArg(args.Args[i]);
                    foreach (Arg a in args.PosOnlyArgs) BindArg(a);
                    if (args.VarArg != null) BindArg(args.VarArg);
                    foreach (Arg a in args.KwOnlyArgs) BindArg(a);
                    if (args.KwArg != null) BindArg(args.KwArg);
                }
                else if (args != null) BindArguments(args);

                foreach (Stmt s in body) VisitStmt(s);
            }
            finally { _env.Pop(); }
        }

        // Replace the PEP 673 `Self` type with the enclosing class throughout a TypeRef tree, so
        // Self nested in a container resolves too (-> list[Self], -> Self | None, -> tuple[Self, int]).
        // typing.Self and a bare Self both convert to ClassRef("Self")/ClassRef("typing.Self").
        private static TypeRef SubstituteSelf(TypeRef t, string enclosingClass)
        {
            if (t == null || string.IsNullOrEmpty(enclosingClass)) return t;
            switch (t)
            {
                case ClassRef cr:
                    return IsSelfName(cr.QualifiedName) ? new ClassRef(enclosingClass) : cr;
                case GenericRef gr:
                    {
                        ClassRef baseRef = (ClassRef)SubstituteSelf(gr.Base, enclosingClass);
                        var args = new List<TypeRef>(gr.Args.Count);
                        foreach (TypeRef a in gr.Args) args.Add(SubstituteSelf(a, enclosingClass));
                        return new GenericRef(baseRef, args);
                    }
                case UnionRef ur:
                    {
                        var members = new List<TypeRef>(ur.Members.Count);
                        foreach (TypeRef m in ur.Members) members.Add(SubstituteSelf(m, enclosingClass));
                        return new UnionRef(members);
                    }
                case TupleRef tup:
                    {
                        var items = new List<TypeRef>(tup.Items.Count);
                        foreach (TypeRef i in tup.Items) items.Add(SubstituteSelf(i, enclosingClass));
                        return new TupleRef(items, tup.IsVariadic);
                    }
                default:
                    return t;
            }
        }

        private static bool IsSelfName(string qualifiedName)
        {
            return qualifiedName == "Self" || qualifiedName == "typing.Self"
                   || qualifiedName == "typing_extensions.Self";
        }

        // A value whose inferred type is `Self` — e.g. the result of calling a `-> Self` classmethod
        // like `FieldInfo._construct(...)` — is gradual: the bound class of that Self is the receiver,
        // which we don't track through the call. Rather than false-flag it against a concrete
        // annotation (pydantic fields.py `return field_info`), treat a `Self`-typed value as
        // compatible. The precise resolution is a subtype of the enclosing class anyway.
        private static bool IsSelfType(TypeRef t)
        {
            return t is ClassRef cr && IsSelfName(cr.QualifiedName);
        }

        // ---------- Expression inference ----------

        public TypeRef InferExpr(Expr e)
        {
            if (e == null) return UnknownRef.Instance;
            switch (e)
            {
                case Constant c: return InferConstant(c);
                case Name n:
                    {
                        TypeRef t = _env.Resolve(n.Id);
                        return t ?? UnknownRef.Instance;
                    }
                case Call call: return InferCall(call);
                case AttributeExpr ae: return InferAttribute(ae);
                case BinOpExpr b: InferBinaryOperands(b); return UnknownRef.Instance;
                case UnaryOpExpr u: InferExpr(u.Operand); return UnknownRef.Instance;
                case BoolOpExpr bo:
                    {
                        // In `A and B and C` each operand is evaluated only when every operand to its
                        // left is truthy, so a narrowing predicate on the left refines a name/path for
                        // the operands to its right: `isinstance(ctx.command, Group) and
                        // ctx.command.chain` resolves `.chain` on Group, not the declared Command
                        // (click core.py:1448). Apply the accumulated true-narrowings of the earlier
                        // operands while inferring each later one, then restore. (`or` short-circuits
                        // on the opposite polarity and is left un-narrowed — the common attribute-guard
                        // idiom is `and`.)
                        if (bo.Op == BoolOp.And && bo.Values.Count > 1)
                        {
                            List<BindingSnapshot> snaps = new List<BindingSnapshot>();
                            try
                            {
                                foreach (Expr v in bo.Values)
                                {
                                    InferExpr(v);
                                    var narrow = CollectNarrowings(v, forTrueBranch: true);
                                    if (narrow.Count > 0) snaps.Add(_env.OverrideInCurrent(narrow));
                                }
                            }
                            finally
                            {
                                for (int k = snaps.Count - 1; k >= 0; k--) _env.Restore(snaps[k]);
                            }
                            return BuiltinRefs.Bool;
                        }
                        foreach (Expr v in bo.Values) InferExpr(v);
                        return UnknownRef.Instance;
                    }
                case Compare cmp:
                {
                    TypeRef leftT = InferExpr(cmp.Left);
                    bool anyDynamic = leftT is AnyRef || leftT is UnknownRef;
                    bool anyOverloadable = CanOverloadRichComparison(leftT);
                    foreach (Expr rhs in cmp.Comparators)
                    {
                        TypeRef rhsT = InferExpr(rhs);
                        if (rhsT is AnyRef || rhsT is UnknownRef) anyDynamic = true;
                        if (CanOverloadRichComparison(rhsT)) anyOverloadable = true;
                    }
                    bool anyRich = false;
                    foreach (CmpOp op in cmp.Ops)
                    {
                        if (op == CmpOp.Eq || op == CmpOp.NotEq || op == CmpOp.Lt
                            || op == CmpOp.LtE || op == CmpOp.Gt || op == CmpOp.GtE)
                        {
                            anyRich = true;
                            break;
                        }
                    }
                    // A RICH comparison (== != < <= > >=) whose operand is dynamic
                    // (Any/Unknown — e.g. a numpy ndarray, pandas Series or torch
                    // Tensor from an unresolved import) can return a NON-bool via an
                    // __eq__/__lt__ override: `arr == x` is an ndarray, `(t < y)` is a
                    // Tensor. Assuming bool made `.sum()/.any()/.astype()/.to_numpy()`
                    // on the result false-flag PY6001 across the sample workspace fixture. Only
                    // assume bool when every operand is a concrete known type. `is/is
                    // not/in/not in` are always coerced to bool by Python, so they stay
                    // bool regardless of operand type (keeps None-identity narrowing).
                    if (anyRich && (anyDynamic || anyOverloadable)) return UnknownRef.Instance;
                    return BuiltinRefs.Bool;
                }
                case IfExp ie:
                    {
                        InferExpr(ie.Test);
                        // `X if cond else Y`: the X branch sees cond's true-narrowing and the Y branch
                        // the false-narrowing, so a call nested in a branch checks its arguments against
                        // the narrowed type — `f(value) if value is not None else None` passes `str`,
                        // not `str | None` (black __init__.py:267).
                        var ifTrueN = CollectNarrowings(ie.Test, forTrueBranch: true);
                        var ifFalseN = CollectNarrowings(ie.Test, forTrueBranch: false);
                        var ifSnapT = _env.OverrideInCurrent(ifTrueN);
                        try { InferExpr(ie.Body); }
                        finally { _env.Restore(ifSnapT); }
                        var ifSnapF = _env.OverrideInCurrent(ifFalseN);
                        try { InferExpr(ie.OrElse); }
                        finally { _env.Restore(ifSnapF); }
                        return UnknownRef.Instance;
                    }
                case Subscript sub:
                    InferExpr(sub.Value);
                    InferExpr(sub.Slice);
                    return UnknownRef.Instance;
                case Ast.ListExpr le:
                    foreach (Expr el in le.Elts) InferExpr(el);
                    return BuiltinRefs.List;
                case Ast.TupleExpr te:
                    foreach (Expr el in te.Elts) InferExpr(el);
                    return BuiltinRefs.Tuple;
                case Ast.SetExpr se:
                    foreach (Expr el in se.Elts) InferExpr(el);
                    return BuiltinRefs.Set;
                case Dict d:
                    for (int i = 0; i < d.Keys.Count; i++)
                    {
                        if (d.Keys[i] != null) InferExpr(d.Keys[i]);
                        if (i < d.Values.Count) InferExpr(d.Values[i]);
                    }
                    return BuiltinRefs.Dict;
                case Lambda lam:
                    // Don't descend — lambdas in phase-3 MVP have opaque signatures.
                    return UnknownRef.Instance;
                case Ast.NamedExpr ne:
                    {
                        TypeRef value = InferExpr(ne.Value);
                        if (ne.Target is Name nm) _env.Bind(nm.Id, value);
                        return value;
                    }
                case Await aw: InferExpr(aw.Value); return UnknownRef.Instance;
                case Yield y: if (y.Value != null) InferExpr(y.Value); return UnknownRef.Instance;
                case YieldFrom yf: InferExpr(yf.Value); return UnknownRef.Instance;
                case Starred st: InferExpr(st.Value); return UnknownRef.Instance;
                case JoinedStr _: return BuiltinRefs.Str;
                case FormattedValue fv: InferExpr(fv.Value); return BuiltinRefs.Str;
                default: return UnknownRef.Instance;
            }
        }

        private void InferBinaryOperands(BinOpExpr root)
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
                else InferExpr(current);
            }
        }

        private TypeRef InferConstant(Constant c)
        {
            if (c.Value == null) return NoneRef.Instance;
            switch (c.Value)
            {
                case bool _: return BuiltinRefs.Bool;
                // BigInteger is how a literal too large for `long` arrives; it is still an int,
                // because Python integers are arbitrary precision.
                case int _: case long _: case System.Numerics.BigInteger _: return BuiltinRefs.Int;
                case double _: case float _: case decimal _: return BuiltinRefs.Float;
                case string _: return BuiltinRefs.Str;
                case byte[] _: return BuiltinRefs.Bytes;
                default:
                    if (c.Value is ConstantEllipsis) return AnyRef.Instance;
                    return UnknownRef.Instance;
            }
        }

        private TypeRef InferCall(Call call)
        {
            TypeRef calleeType = InferExpr(call.Func);

            // Infer argument types so side effects (nested calls) get checked.
            List<TypeRef> positionalTypes = new List<TypeRef>(call.Args.Count);
            foreach (Expr a in call.Args) positionalTypes.Add(InferExpr(a));
            Dictionary<string, TypeRef> keywordTypes = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            foreach (Keyword k in call.Keywords)
            {
                TypeRef kt = k.Value != null ? InferExpr(k.Value) : UnknownRef.Instance;
                if (!string.IsNullOrEmpty(k.Name)) keywordTypes[k.Name] = kt;
            }

            if (calleeType is CallableRef cr) return CheckCallableCall(cr, call, positionalTypes, keywordTypes);
            // A bare `object` callee is this checker's un-inferred fallback — e.g.
            // `cust = _CUSTOM.get(p["num"]); cust(p, out_dir, run_dir)` where dict.get()
            // returns object. It could be any callable at runtime, so DON'T arity-check it
            // as a constructor (that reported "3 given, 0 accepted" against object.__init__,
            // plot_prop_logs_rouse_intspace.py:647).
            if (calleeType is ClassRef oc && (oc.QualifiedName == "object" || oc.QualifiedName == "builtins.object"))
                return UnknownRef.Instance;
            // A callee whose TYPE is `type` is an arbitrary class object, not the metaclass `type`
            // itself: `dict_class: type = OrderedDict; dict_class(data)` constructs an instance of an
            // unknown class through an unknown constructor. Arity-checking it against type's own
            // 3-arg (name, bases, dict) signature reported "1 given, 3 required", and returning `type`
            // made the result's methods (.update()) vanish. Both the bare metaclass call `type(x)`
            // (valid 1-arg) and this parameterised-factory pattern land here, so stay lenient: the
            // call yields an unknown instance. (requests/sessions.py:96-97.)
            if (calleeType is ClassRef tc && (tc.QualifiedName == "type" || tc.QualifiedName == "builtins.type"))
                return UnknownRef.Instance;
            if (calleeType is ClassRef ccr)
            {
                // A call on an INSTANCE dispatches to __call__, not the constructor. The callee is the
                // class OBJECT (→ construct) only when it is a known class NAME; `self`, a parameter,
                // or a variable holding an instance is not — calling it invokes __call__. pydantic
                // `_PydanticWeakRef.__call__`: `self()` / `v()` were mis-checked against __init__ arity.
                bool isInstanceCall = call.Func is Name cn && !IsResolvableClassName(cn.Id)
                    && !_classObjectNames.Contains(cn.Id);
                if (isInstanceCall)
                {
                    TypeRef callMember = LookupMemberType(ccr.QualifiedName, "__call__");
                    if (callMember is CallableRef callSig)
                    {
                        CallableRef bound = BindSelfIfMethod(callSig, "__call__") as CallableRef ?? callSig;
                        return CheckCallableCall(bound, call, positionalTypes, keywordTypes);
                    }
                    return UnknownRef.Instance;   // instance not visibly callable — stay lenient
                }
                return CheckConstructorCall(ccr, call, positionalTypes, keywordTypes);
            }
            if (calleeType is AnyRef || calleeType is UnknownRef) return UnknownRef.Instance;
            return UnknownRef.Instance;
        }

        private TypeRef CheckCallableCall(CallableRef sig, Call call,
            IReadOnlyList<TypeRef> positionalTypes, IReadOnlyDictionary<string, TypeRef> keywordTypes)
        {
            if (sig.IsEllipsisParams) return sig.ReturnType;
            CheckArity(sig.Params, call, positionalTypes, keywordTypes);
            return sig.ReturnType ?? UnknownRef.Instance;
        }

        private TypeRef CheckConstructorCall(ClassRef cls, Call call,
            IReadOnlyList<TypeRef> positionalTypes, IReadOnlyDictionary<string, TypeRef> keywordTypes)
        {
            IReadOnlyList<CallableParam> initParams = ResolveConstructorParams(cls.QualifiedName);
            if (initParams != null) CheckArity(initParams, call, positionalTypes, keywordTypes);
            return cls;
        }

        private IReadOnlyList<CallableParam> ResolveConstructorParams(string className)
        {
            // Workspace class? Walk body for __init__ FunctionDef.
            ClassDef wcls;
            if (_workspaceClasses.TryGetValue(className, out wcls))
            {
                // A ClassRef callee is ambiguous: it is BOTH the class object (a real
                // constructor call `Cls(...)`) AND the type of an INSTANCE of that class
                // (`x = Cls(); x(...)` — an instance-call that dispatches to __call__, not
                // __init__). When the class inherits from an unresolved external base
                // (e.g. torch.nn.Module), instances are typically callable via an inherited
                // __call__ we cannot see (nn.Module.__call__ -> forward), AND the base may
                // alter construction — so we cannot reliably arity-check either shape.
                // Stay silent, mirroring the external-base attribute suppression. Without
                // this, `builder(bb, rt, chis)` (builder = SideChainBuilder(), a nn.Module)
                // was checked against __init__(self, npz_path=...) → "3 given, 1 accepted".
                if (HasUnresolvedExternalBase(wcls, new HashSet<string>(StringComparer.Ordinal)))
                    return null;

                foreach (Stmt s in wcls.Body)
                {
                    FunctionDef fn = s as FunctionDef;
                    if (fn != null && fn.Name == "__init__")
                    {
                        CallableRef sig = BuildCallableFromFunctionDef(fn.Args, fn.Returns);
                        return DropSelfParam(sig.Params);
                    }
                }
                foreach (Stmt s in wcls.Body)
                {
                    FunctionDef fn = s as FunctionDef;
                    if (fn != null && fn.Name == "__new__")
                    {
                        CallableRef sig = BuildCallableFromFunctionDef(fn.Args, fn.Returns);
                        return DropFirstParam(sig.Params);
                    }
                }
                return null;  // no __init__ or __new__ -> accept any args
            }
            // Stub class?
            StubDeclaration decl = LookupClassDeclaration(className);
            if (decl != null && decl.Members != null &&
                decl.Members.TryGetValue("__init__", out StubDeclaration init) &&
                init.Type is CallableRef sig2)
            {
                return DropSelfParam(sig2.Params);
            }
            return null;
        }

        // Accessing a method through an INSTANCE (obj.method) yields a *bound* method: the
        // receiver is already supplied, so the callable the call site sees no longer has self/cls.
        // Without this, `lambdas.append(0.0)` checked against list.append(self, object) reported
        // "1 given, 2 required" — a phantom error on every ordinary method call.
        private static TypeRef BindSelfIfMethod(TypeRef member, string memberName)
        {
            // `__new__` is an implicit STATICMETHOD: `Cls.__new__(Cls, ...)` / `self.__new__(Cls)`
            // pass the class explicitly, so its first (cls) param must NOT be stripped — otherwise
            // the checker saw "0 accepted" and false-flagged PY6002 (rich style.py, console.py).
            if (memberName == "__new__") return member;
            // Descriptor binding is positional, not name-based. Python projects commonly
            // spell the receiver `f`, `kls`, or another domain name; it is still supplied
            // implicitly when the method is fetched through an instance.
            if (member is CallableRef c && !c.IsEllipsisParams && c.Params.Count > 0)
                return new CallableRef(DropFirstParam(c.Params), c.ReturnType, c.IsEllipsisParams);
            return member;
        }

        private static IReadOnlyList<CallableParam> DropFirstParam(IReadOnlyList<CallableParam> src)
        {
            if (src == null || src.Count == 0) return src;
            List<CallableParam> trimmed = new List<CallableParam>(src.Count - 1);
            for (int i = 1; i < src.Count; i++) trimmed.Add(src[i]);
            return trimmed;
        }

        private static IReadOnlyList<CallableParam> DropSelfParam(IReadOnlyList<CallableParam> src)
        {
            if (src == null || src.Count == 0) return src;
            if (src[0].Name == "self" || src[0].Name == "cls")
            {
                List<CallableParam> trimmed = new List<CallableParam>(src.Count - 1);
                for (int i = 1; i < src.Count; i++) trimmed.Add(src[i]);
                return trimmed;
            }
            return src;
        }

        private void CheckArity(IReadOnlyList<CallableParam> paramList, Call call,
            IReadOnlyList<TypeRef> positionalTypes, IReadOnlyDictionary<string, TypeRef> keywordTypes)
        {
            int positionalProvided = 0;
            bool hasStar = false, hasDoubleStar = false;
            foreach (Expr a in call.Args)
            {
                if (a is Starred) hasStar = true;
                else positionalProvided++;
            }
            HashSet<string> namedProvided = new HashSet<string>(StringComparer.Ordinal);
            foreach (Keyword k in call.Keywords)
            {
                if (string.IsNullOrEmpty(k.Name)) hasDoubleStar = true;
                else namedProvided.Add(k.Name);
            }
            if (hasStar || hasDoubleStar) return;  // can't statically check splats

            int minRequired = 0;
            int maxPositional = 0;
            bool hasVarArgs = false;
            List<string> allowedKw = new List<string>();
            bool hasVarKwargs = false;

            foreach (CallableParam p in paramList)
            {
                switch (p.Kind)
                {
                    case CallableParamKind.PositionalOnly:
                    case CallableParamKind.PositionalOrKeyword:
                        if (!p.HasDefault) minRequired++;
                        maxPositional++;
                        if (p.Kind == CallableParamKind.PositionalOrKeyword) allowedKw.Add(p.Name);
                        break;
                    case CallableParamKind.VarPositional: hasVarArgs = true; break;
                    case CallableParamKind.KeywordOnly:
                        allowedKw.Add(p.Name);
                        if (!p.HasDefault) minRequired++;
                        break;
                    case CallableParamKind.VarKeyword: hasVarKwargs = true; break;
                }
            }

            int totalProvided = positionalProvided + namedProvided.Count;
            if (!hasVarArgs && positionalProvided > maxPositional)
            {
                Emit(VirtualCompilerDiagnosticCodes.TypeArgCountMismatch,
                    "too many positional arguments: " + positionalProvided + " given, "
                    + maxPositional + " accepted", call.Span);
                return;
            }
            if (totalProvided < minRequired && !hasVarArgs)
            {
                Emit(VirtualCompilerDiagnosticCodes.TypeArgCountMismatch,
                    "missing required argument(s): " + totalProvided + " given, "
                    + minRequired + " required", call.Span);
                return;
            }
            if (!hasVarKwargs)
            {
                foreach (string kw in namedProvided)
                {
                    if (!allowedKw.Contains(kw))
                    {
                        Emit(VirtualCompilerDiagnosticCodes.TypeUnknownKeywordArg,
                            "unexpected keyword argument '" + kw + "'", call.Span);
                        return;
                    }
                }
            }

            // ---- PY6003: positional-arg type-compat check ----
            // Match each provided positional argument to its corresponding param.
            // Positional-only + positional-or-keyword params are consumed in order;
            // VarPositional absorbs any remaining positional args.
            int paramIdx = 0;
            for (int argIdx = 0; argIdx < positionalTypes.Count; argIdx++)
            {
                if (paramIdx >= paramList.Count) break;
                CallableParam cur = paramList[paramIdx];
                if (cur.Kind == CallableParamKind.VarPositional) { /* absorbs */ continue; }
                if (cur.Kind != CallableParamKind.PositionalOnly &&
                    cur.Kind != CallableParamKind.PositionalOrKeyword) break;
                TypeRef argType = positionalTypes[argIdx];
                Expr posArgExpr = argIdx < call.Args.Count ? call.Args[argIdx] : null;
                if (argType != null && !SubtypeChecker.IsAssignable(argType, cur.Type, _stubs)
                    && !WorkspaceSubtypeSatisfies(argType, cur.Type)
                    && !(posArgExpr != null && ClassValueSatisfiesTypeAnnotation(posArgExpr, cur.Type))
                    && !(posArgExpr != null && LiteralConstantSatisfiesExpected(posArgExpr, cur.Type))
                    && !TypeCovarianceSatisfies(argType, cur.Type)
                    && SourceTypeIsJudgeable(argType)
                    && ExpectedTypeIsCheckable(cur.Type))
                {
                    Emit(VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch,
                        "argument " + (argIdx + 1) + " has incompatible type '" + argType
                        + "'; expected '" + cur.Type + "'", call.Span);
                }
                paramIdx++;
            }
            // Keyword-arg type compat (only for params we can identify by name).
            foreach (CallableParam p in paramList)
            {
                if (p.Kind != CallableParamKind.PositionalOrKeyword &&
                    p.Kind != CallableParamKind.KeywordOnly) continue;
                TypeRef argType;
                if (!keywordTypes.TryGetValue(p.Name, out argType)) continue;
                Expr kwExpr = KeywordValueExpr(call, p.Name);
                if (argType != null && !SubtypeChecker.IsAssignable(argType, p.Type, _stubs)
                    && !WorkspaceSubtypeSatisfies(argType, p.Type)
                    && !(kwExpr != null && ClassValueSatisfiesTypeAnnotation(kwExpr, p.Type))
                    && !(kwExpr != null && LiteralConstantSatisfiesExpected(kwExpr, p.Type))
                    && !TypeCovarianceSatisfies(argType, p.Type)
                    && SourceTypeIsJudgeable(argType)
                    && ExpectedTypeIsCheckable(p.Type))
                {
                    Emit(VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch,
                        "keyword argument '" + p.Name + "' has incompatible type '" + argType
                        + "'; expected '" + p.Type + "'", call.Span);
                }
            }
        }

        // A type-mismatch diagnostic is only trustworthy when the EXPECTED type resolves to real
        // classes. If it mentions one the checker cannot resolve — a type alias it does not expand
        // (rich's StyleType = str | Style, RenderableType, TextType, ...), or an external/unstubbed
        // library type — incompatibility cannot be proven, so we must stay silent rather than
        // false-flag valid code. Symmetric with the existing leniency for unknown SOURCE types and
        // unresolved external bases. (iter 371)
        // Symmetric to ExpectedTypeIsCheckable, but for the ACTUAL (returned/assigned) type: a value
        // whose type is a bare unresolvable ClassRef — a private module type-alias like
        // `_EscapeSubMethod = Callable[[...], str]`, or a qualified `_t.CertType` — cannot be judged
        // against the annotation, because we don't know its real shape (calling such an alias yields
        // the alias name, not its underlying return type). rich markup.py/_emoji_replace.py.
        private bool SourceTypeIsJudgeable(TypeRef t)
        {
            if (IsTypeVarName(t)) return false;   // a type parameter can stand for anything
            if (t is ClassRef cr) return IsResolvableClassName(cr.QualifiedName);
            // A union is judgeable only when EVERY member is — symmetric with ExpectedTypeIsCheckable.
            // One unresolvable member (an external/unstubbed type we can't prove is a subtype) makes the
            // whole union unprovable, so flagging it is a false positive: fastapi passes
            // `Mapping[str, Any] | QueryParams | Headers` (both starlette Mappings we can't see) where a
            // `Mapping[str, Any]` is expected. `str | None` stays judgeable (both members resolve), so
            // genuine Optional mismatches are still caught.
            if (t is UnionRef ur)
            {
                foreach (TypeRef m in ur.Members) if (!SourceTypeIsJudgeable(m)) return false;
                return true;
            }
            // A parameterized type is judgeable only when its BASE resolves — symmetric with
            // ExpectedTypeIsCheckable's GenericRef rule. An external/unstubbed alias applied as a
            // generic (`Lifespan[Any]`, a starlette callable alias) can't be judged against a concrete
            // parameter, so flagging it is a false positive (fastapi routing.py:2484). `list[int]` and
            // workspace generics (`Ok[Line]`) keep resolvable bases and stay judged.
            if (t is GenericRef grf)
            {
                if (grf.Base == null || !IsResolvableClassName(grf.Base.QualifiedName)) return false;
                // A concrete outer container does not make an opaque element type judgeable:
                // list[Decorator[T]] cannot be proven incompatible with
                // Iterable[Decorator[ValidatorInfo]] while T is unresolved.
                foreach (TypeRef arg in grf.Args)
                    if (!SourceTypeIsJudgeable(arg)) return false;
                return true;
            }
            return true;
        }

        // The stub-only SubtypeChecker cannot see WORKSPACE class inheritance, so `return Derived()`
        // against a `Base` annotation (both defined in the project) was false-flagged. This walks the
        // source class's workspace base chain to see whether it (transitively) derives from the
        // expected class — covering `FuncParamType`/`Tuple` returned as `ParamType[Any]` (click
        // types.py) and any subclass-where-base-expected. Type ARGUMENTS are ignored (erasure), which
        // is the same gradual stance the checker takes for cross-base generics.
        private bool WorkspaceSubtypeSatisfies(TypeRef source, TypeRef expected)
        {
            // A union satisfies the expected type only when EVERY member does — each via the stub
            // checker or (for project classes it can't see) workspace subclassing. Covers
            // `theme = ANSISyntaxTheme(...) / PygmentsSyntaxTheme(...)` joined to their common base and
            // returned as `SyntaxTheme` (rich syntax.py:277).
            if (source is UnionRef su)
            {
                if (su.Members.Count == 0) return false;
                foreach (TypeRef m in su.Members)
                    if (!SubtypeChecker.IsAssignable(m, expected, _stubs) && !WorkspaceSubtypeSatisfies(m, expected))
                        return false;
                return true;
            }
            string src = TypeClassName(source);
            if (src == null) return false;
            foreach (string tgt in ExpectedClassNames(expected))
                if (tgt != null && IsWorkspaceSubclassOf(src, tgt, new HashSet<string>(StringComparer.Ordinal)))
                    return true;
            return false;
        }

        private static string TypeClassName(TypeRef t)
        {
            if (t is ClassRef cr) return LastNameSegment(cr.QualifiedName);
            if (t is GenericRef gr) return LastNameSegment(gr.Base.QualifiedName);
            return null;
        }

        private static IEnumerable<string> ExpectedClassNames(TypeRef t)
        {
            switch (t)
            {
                case ClassRef cr: yield return LastNameSegment(cr.QualifiedName); break;
                case GenericRef gr: yield return LastNameSegment(gr.Base.QualifiedName); break;
                case UnionRef ur:
                    foreach (TypeRef m in ur.Members)
                        foreach (string n in ExpectedClassNames(m)) yield return n;
                    break;
            }
        }

        private bool IsWorkspaceSubclassOf(string src, string tgt, HashSet<string> visited)
        {
            src = LastNameSegment(src);
            tgt = LastNameSegment(tgt);
            if (string.IsNullOrEmpty(src)) return false;
            // Reflexive: a class is a subtype of itself. This is what accepts a same-class generic with
            // different type arguments — `_Pipeline[_InT, _NewOutT]` returned as `_Pipeline[_InT, str]`
            // (pydantic experimental) — since WorkspaceSubtypeSatisfies erases the type arguments.
            // EXCLUDE the class metatype: `Type[A]` vs `Type[B]` must compare A and B (done precisely by
            // TypeCovarianceSatisfies), not be accepted just because both are `Type`.
            if (string.Equals(src, tgt, StringComparison.Ordinal) && !IsTypeMetaName(src)) return true;
            if (!visited.Add(src)) return false;
            ClassDef wcls;
            if (!_workspaceClasses.TryGetValue(src, out wcls)) return false;
            foreach (Expr baseExpr in wcls.Bases)
            {
                string baseName = WorkspaceBaseName(baseExpr);
                if (string.IsNullOrEmpty(baseName)) continue;
                if (string.Equals(baseName, tgt, StringComparison.Ordinal)) return true;
                if (IsWorkspaceSubclassOf(baseName, tgt, visited)) return true;
            }
            return false;
        }

        // A bare class name used as a VALUE is a class OBJECT of type `type[C]`, not an instance of
        // C. The env seeds class names as instance ClassRefs (so `Context()` infers an instance), so
        // both `context_class: type[Context] = Context` (PY6008, click core.py:1010) and
        // `_safe_isinstance(obj, Node)` where the param is `type | Tuple[type, ...]` (PY6003, rich
        // pretty.py:906) false-flagged. Accept when the expression names a class and the expected type
        // admits a class object.
        private bool ClassValueSatisfiesTypeAnnotation(Expr valueExpr, TypeRef expected)
        {
            string cls = ClassNameOfValueExpr(valueExpr);
            if (cls == null) return false;                       // not a class-naming expression
            return ExpectedAcceptsClassObject(cls, expected);
        }

        // Whether <paramref name="expected"/> admits a class object for the class named
        // <paramref name="cls"/>: bare `type` accepts any class; `type[Base]` accepts Base or a
        // subclass (type[] is covariant) or an unresolvable Base; a Union accepts it if any member does.
        private bool ExpectedAcceptsClassObject(string cls, TypeRef expected)
        {
            if (expected is ClassRef cr)
                return IsTypeMetaName(cr.QualifiedName);
            if (expected is GenericRef gr && gr.Base != null && IsTypeMetaName(gr.Base.QualifiedName))
            {
                if (gr.Args == null || gr.Args.Count != 1) return true;
                ClassRef baseArg = gr.Args[0] as ClassRef;
                string baseName = baseArg != null ? baseArg.QualifiedName : null;
                if (string.IsNullOrEmpty(baseName)) return true; // type[TypeVar] / type[Any] — any class fits
                if (LastNameSegment(cls) == LastNameSegment(baseName)) return true;
                return IsWorkspaceSubclassOf(cls, baseName, new HashSet<string>(StringComparer.Ordinal))
                    || SubtypeChecker.IsAssignable(new ClassRef(cls), new ClassRef(baseName), _stubs);
            }
            if (expected is UnionRef u)
            {
                foreach (TypeRef m in u.Members)
                    if (ExpectedAcceptsClassObject(cls, m)) return true;
            }
            return false;
        }

        // Whether a name denotes the class metatype: builtins `type` or typing `Type` (typing.Type,
        // t.Type). These are the same runtime concept, so `Type[X]` and `type[X]` are interchangeable.
        private static bool IsTypeMetaName(string qualifiedName)
        {
            string last = LastNameSegment(qualifiedName);
            return last == "type" || last == "Type";
        }

        // The single type argument of a `type[X]` / `Type[X]` generic, or null if it isn't one.
        private static TypeRef TypeSubjectArg(TypeRef t)
        {
            GenericRef gr = t as GenericRef;
            if (gr == null || gr.Base == null || gr.Args == null || gr.Args.Count != 1) return null;
            return IsTypeMetaName(gr.Base.QualifiedName) ? gr.Args[0] : null;
        }

        // `type[A]` / `Type[A]` is assignable to `type[B]` / `Type[B]` when A is-a B — type[] is
        // covariant. Covers `Type[Model]` returned/passed where `Type[BaseModel]` is expected (pydantic
        // v1). A TypeVar / otherwise-unjudgeable A (e.g. `Type[Model]` where Model is a bound TypeVar)
        // stays lenient rather than false-flagging.
        private bool TypeCovarianceSatisfies(TypeRef source, TypeRef expected)
        {
            // A union source satisfies `Type[B]` only when EVERY member does — each via covariance,
            // metaclass-ness, or gradual. Covers `Type[T] | ConstrainedNumberMeta` returned as
            // `Type[bytes]` (pydantic v1 types.py).
            if (source is UnionRef su)
            {
                if (su.Members.Count == 0) return false;
                foreach (TypeRef m in su.Members)
                    if (!TypeCovarianceSatisfies(m, expected)) return false;
                return true;
            }
            // Only meaningful when the EXPECTED type is a metatype `type[...]` / `Type[...]`.
            if (TypeSubjectArg(expected) == null) return false;
            // A metaclass instance — a value whose class subclasses `type` (pydantic
            // `ConstrainedNumberMeta(type)`) — IS a type, so it satisfies any type[X].
            if (source is ClassRef scr && IsMetaclass(scr.QualifiedName)) return true;

            TypeRef aArg = TypeSubjectArg(source);
            TypeRef bArg = TypeSubjectArg(expected);
            if (aArg == null || bArg == null) return false;
            if (bArg is AnyRef || bArg is UnknownRef) return true;              // Type[Any]
            if (SubtypeChecker.IsAssignable(aArg, bArg, _stubs)) return true;
            if (WorkspaceSubtypeSatisfies(aArg, bArg)) return true;
            if (!SourceTypeIsJudgeable(aArg)) return true;                      // TypeVar / gradual arg
            return false;
        }

        // A class that (transitively) subclasses the builtin `type` — i.e. a metaclass. Instances of a
        // metaclass are themselves types, so they are assignable to any type[X] / Type[X].
        private bool IsMetaclass(string className)
        {
            if (string.IsNullOrEmpty(className)) return false;
            string last = LastNameSegment(className);
            if (last == "type" || last == "Type") return true;
            return IsWorkspaceSubclassOf(className, "type", new HashSet<string>(StringComparer.Ordinal))
                || SubtypeChecker.IsAssignable(new ClassRef(className), new ClassRef("type"), _stubs);
        }

        // The value expression supplied for keyword <paramref name="name"/> in a call, or null.
        private static Expr KeywordValueExpr(Call call, string name)
        {
            if (call.Keywords == null) return null;
            foreach (Keyword k in call.Keywords)
                if (k.Name == name) return k.Value;
            return null;
        }

        // The class named by a value expression when it is a bare Name or dotted attribute that
        // resolves to a known class declaration (workspace or bundled stub); otherwise null.
        private string ClassNameOfValueExpr(Expr e)
        {
            string name = null;
            if (e is Name n) name = n.Id;
            else if (e is AttributeExpr a) name = a.Attr;        // pkg.Mod.Class -> "Class"
            if (string.IsNullOrEmpty(name)) return null;
            if (_workspaceClasses.ContainsKey(name) || LookupClassDeclaration(name) != null) return name;
            return null;
        }

        // The class name of a base expression, peeling a subscripted generic base first so
        // `class FuncParamType(ParamType[_T])` reports base "ParamType" (click types.py).
        private static string WorkspaceBaseName(Expr baseExpr)
        {
            while (baseExpr is Subscript sub) baseExpr = sub.Value;
            return LastNameSegment(BaseSimpleName(baseExpr));
        }

        private bool ExpectedTypeIsCheckable(TypeRef t)
        {
            switch (t)
            {
                case ClassRef cr:
                    // A type parameter (`_T` from `_T = TypeVar(...)`) is not a concrete type — a
                    // `-> _T` return accepts whatever the call binds it to (fastapi routing.py:264).
                    if (_typeVarNames.Contains(cr.QualifiedName)) return false;
                    // A Protocol is a STRUCTURAL type: any class with the right members satisfies it,
                    // which this nominal checker cannot verify, so it must stay silent rather than
                    // false-flag a structurally-valid argument (fastapi routing.py: APIRoute vs
                    // `_RouteWithPath(Protocol)`).
                    if (ClassChainReachesProtocol(cr.QualifiedName, new HashSet<string>(StringComparer.Ordinal)))
                        return false;
                    // A BARE class annotation must resolve to a real type; a bare alias (StyleType)
                    // does not and is therefore unjudgeable.
                    return IsResolvableClassName(cr.QualifiedName);
                case UnionRef ur:
                    // `StyleType | None` is only judgeable if every member is — one unresolvable
                    // alias member makes the whole union unprovable.
                    foreach (TypeRef m in ur.Members) if (!ExpectedTypeIsCheckable(m)) return false;
                    return true;
                case GenericRef grf:
                    // A parameterized *alias* — `TResult[Line]` where `TResult = Result[T, ...]`
                    // expands to `Union[Ok[T], Err[E]]` — or an unmodeled qualified typing form
                    // (`t.ContextManager[X]` behind `@contextmanager`) has a base we cannot expand
                    // to its underlying type, so a structural return/arg check false-positives
                    // (black trans.py: `Ok`/`Err` vs `TResult`). Only judge a generic whose base
                    // is a real, resolvable class (list[X], Sequence[T], Ok[Line]); the type
                    // ARGUMENTS stay unjudged, exactly as before.
                    return IsResolvableClassName(grf.Base.QualifiedName);
                case NeverRef _:
                    // `NoReturn` / `Never` as a *parameter* type is the assert_never() exhaustiveness
                    // idiom: the caller passes "the value whose type should be impossible here", and a
                    // checker is expected to prove that type has been narrowed to Never. Gradual
                    // analysis under-narrows aliased / forward-ref unions (pydantic v1/utils.py:609,
                    // assert_never(items) where items is 'AbstractSetIntStr | MappingIntStrAny'), so a
                    // Never target — against which every concrete argument looks unassignable —
                    // false-positives far more often than it catches a real non-exhaustive branch.
                    return false;
                default:
                    // TupleRef, Callable, Literal, Any/None/... — judgeable structurally.
                    return true;
            }
        }

        // A class name is resolvable when it is a workspace class, a bundled stub class, or the
        // implicit `object` root. An alias (StyleType) or an unstubbed external type is not.
        private bool IsResolvableClassName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == "object" || name == "builtins.object") return true;
            if (_workspaceClasses.ContainsKey(name)) return true;
            if (LookupClassDeclaration(name) != null) return true;
            // A stub TYPE ALIAS names a real type too (typing.List = list, Dict = dict, ...), even
            // though it is not a class declaration — so List[Shape] stays checkable. A WORKSPACE alias
            // (rich's StyleType = str | Style) is in no stub, so it correctly stays unresolvable.
            return StubSymbolExists(name);
        }

        private bool StubSymbolExists(string name)
        {
            if (_stubs == null) return false;
            if (_stubs.Resolve(name) != null) return true;
            if (_stubs.Resolve("typing." + name) != null) return true;
            if (_stubs.Resolve("builtins." + name) != null) return true;
            return false;
        }

        private TypeRef InferAttribute(AttributeExpr ae)
        {
            // Flow-sensitive attribute narrowing: inside `if self.metavar is not None:` the path
            // "self.metavar" is bound (in the current scope) to its non-None type. Consult that first
            // so `return self.metavar` sees the narrowed type (iter 370).
            string narrowPath = NarrowingEngine.DottedPath(ae);
            if (narrowPath != null)
            {
                TypeRef narrowed = _env.Resolve(narrowPath);
                if (narrowed != null) return narrowed;
            }

            TypeRef valueType = InferExpr(ae.Value);
            if (valueType is LiteralRef literal)
                valueType = RuntimeClassOfLiteral(literal.Value) ?? valueType;
            if (valueType is GenericRef gr)
            {
                // `type[X]` is a CLASS OBJECT, so attribute access resolves against X's own
                // class-level members (ClassVars, methods) — not the bare `type` metaclass, which
                // only carries __name__/mro(). When X is a TypeVar or otherwise unresolvable, or the
                // member is genuinely absent on a resolvable X, stay silent rather than false-flag
                // (click shell_completion.py `cls.name` with cls: type[_ShellCompleteT]).
                if ((gr.Base.QualifiedName == "type" || gr.Base.QualifiedName == "builtins.type")
                    && gr.Args != null && gr.Args.Count == 1)
                {
                    ClassRef argRef = gr.Args[0] as ClassRef;
                    string argClass = argRef != null ? argRef.QualifiedName : null;
                    if (argClass != null &&
                        (_workspaceClasses.ContainsKey(argClass) || LookupClassDeclaration(argClass) != null))
                    {
                        TypeRef classMember = LookupMemberType(argClass, ae.Attr);
                        if (classMember != null) return BindMemberForAccess(
                            classMember, argClass, ae.Attr, accessedThroughClassObject: true);
                    }
                    return UnknownRef.Instance;
                }
                // Preserve a parameterized dict's value type through get(). The general builtins
                // stub intentionally returns object, but dict[K, V].get(key) is V | None (without
                // an explicit default). Losing V here widened Literal-valued lookup tables to
                // object and produced false literal-argument errors in Pydantic.
                if (LastNameSegment(gr.Base.QualifiedName) == "dict"
                    && ae.Attr == "get"
                    && gr.Args != null
                    && gr.Args.Count >= 2)
                {
                    TypeRef value = gr.Args[1];
                    TypeRef returned;
                    if (value is UnionRef valueUnion)
                    {
                        var members = new List<TypeRef>(valueUnion.Members);
                        members.Add(NoneRef.Instance);
                        returned = new UnionRef(members);
                    }
                    else
                    {
                        returned = new UnionRef(new TypeRef[] { value, NoneRef.Instance });
                    }
                    return new CallableRef(
                        new CallableParam[]
                        {
                            new CallableParam("key", gr.Args[0],
                                CallableParamKind.PositionalOrKeyword, false),
                            new CallableParam("default", AnyRef.Instance,
                                CallableParamKind.PositionalOrKeyword, true)
                        },
                        returned);
                }
                // Generic instance (e.g. list[int]): unwrap to base, look up the
                // member, then substitute type args through any TypeVars the
                // signature declares. Phase-3.2 binding is positional — our
                // curated stubs don't yet declare TypeVars on their classes, so
                // the substitution is a no-op in practice, but the wiring is
                // here for when stubs get upgraded.
                TypeRef rawMember = LookupMemberType(gr.Base.QualifiedName, ae.Attr);
                if (rawMember == null)
                {
                    if (!_suppressAttrNotFound)
                        Emit(VirtualCompilerDiagnosticCodes.TypeAttributeNotFound,
                            "'" + gr.Base.QualifiedName + "' has no attribute '" + ae.Attr + "'", ae.Span);
                    return UnknownRef.Instance;
                }
                IReadOnlyList<TypeVarRef> tparams = GenericBinder.FreeTypeVars(rawMember);
                if (tparams.Count == 0) return BindMemberForAccess(
                    rawMember, gr.Base.QualifiedName, ae.Attr);
                IReadOnlyDictionary<string, TypeRef> bindings = GenericBinder.Bind(tparams, gr.Args);
                return BindMemberForAccess(
                    GenericSubstitution.Substitute(rawMember, bindings),
                    gr.Base.QualifiedName,
                    ae.Attr);
            }
            if (valueType is ClassRef cr)
            {
                // A value typed as the bare top type `object` could be ANY subtype at
                // runtime — and in this checker `object` is also the fallback return of
                // accessors whose value type we can't track (dict.get()/setdefault()/pop(),
                // subscripts, ...). Accessing an attribute on it is therefore not provably
                // wrong, so stay lenient like Any/Unknown rather than false-flag
                // (build_culled_pdb.py `sequences.get(...).get(...)`,
                // derive_sidechain_geometry.py `per_res.setdefault(rn, []).append(...)`).
                if (cr.QualifiedName == "object" || cr.QualifiedName == "builtins.object")
                    return UnknownRef.Instance;

                TypeRef memberType = LookupMemberType(cr.QualifiedName, ae.Attr);
                if (memberType == null)
                {
                    // A `self`/`cls` access inside a base or mixin routinely reads an attribute that a
                    // concrete SUBCLASS provides (Django's BaseModelAdmin methods read self.admin_site,
                    // set by ModelAdmin.__init__). At runtime `self` is a subclass instance, so if any
                    // workspace subclass supplies the member it is not really absent. Restricted to
                    // self/cls: `x.attr` on a base-TYPED variable (cmd: Command → cmd.chain) is a genuine
                    // access on the declared base and stays flagged.
                    bool selfReceiver = ae.Value is Name rn && (rn.Id == "self" || rn.Id == "cls");
                    if (!_suppressAttrNotFound
                        && !(selfReceiver && AttributeProvidedBySubclass(cr.QualifiedName, ae.Attr)))
                        Emit(VirtualCompilerDiagnosticCodes.TypeAttributeNotFound,
                            "'" + cr.QualifiedName + "' has no attribute '" + ae.Attr + "'", ae.Span);
                    return UnknownRef.Instance;
                }
                bool throughClassObject = ae.Value is Name receiverName
                    && (IsResolvableClassName(receiverName.Id)
                        || _classObjectNames.Contains(receiverName.Id));
                return BindMemberForAccess(
                    memberType, cr.QualifiedName, ae.Attr, throughClassObject);
            }
            // Any / Unknown / Union / etc. — don't emit false positives.
            return UnknownRef.Instance;
        }

        private static ClassRef RuntimeClassOfLiteral(object value)
        {
            if (value is bool) return BuiltinRefs.Bool;
            if (value is int || value is long) return BuiltinRefs.Int;
            if (value is float || value is double || value is decimal) return BuiltinRefs.Float;
            if (value is string) return BuiltinRefs.Str;
            if (value is byte[]) return BuiltinRefs.Bytes;
            return null;
        }

        private TypeRef LookupMemberType(string className, string memberName)
        {
            return LookupMemberType(className, memberName, new HashSet<string>(StringComparer.Ordinal));
        }

        private TypeRef BindMemberForAccess(
            TypeRef member,
            string className,
            string memberName,
            bool accessedThroughClassObject = false)
        {
            // @staticmethod disables descriptor receiver binding even when accessed through an
            // instance. A first parameter may legitimately be named `cls` (pydantic v1's
            // get_is_required(cls, stmt, lhs)); stripping it as if it were a classmethod changed a
            // three-argument static call into "3 given, 2 accepted".
            if (IsWorkspaceStaticMethod(className, memberName, new HashSet<string>(StringComparer.Ordinal)))
                return member;
            // A normal function fetched through the class is unbound:
            // `Base.method(obj, arg)` supplies the receiver explicitly. A classmethod
            // remains descriptor-bound to cls even when fetched from the class.
            if (accessedThroughClassObject
                && !IsWorkspaceClassMethod(
                    className, memberName, new HashSet<string>(StringComparer.Ordinal)))
                return member;
            return BindSelfIfMethod(member, memberName);
        }

        private bool IsWorkspaceStaticMethod(
            string className,
            string memberName,
            HashSet<string> visited)
        {
            if (string.IsNullOrEmpty(className)) return false;
            className = LastNameSegment(className);
            if (!visited.Add(className)) return false;
            if (!_workspaceClasses.TryGetValue(className, out ClassDef cls)) return false;

            foreach (Stmt stmt in cls.Body)
            {
                if (stmt is FunctionDef fn && fn.Name == memberName)
                    return HasDecorator(fn.DecoratorList, "staticmethod");
                if (stmt is AsyncFunctionDef afn && afn.Name == memberName)
                    return HasDecorator(afn.DecoratorList, "staticmethod");
            }

            foreach (Expr baseExpr in cls.Bases)
            {
                string baseName = BaseSimpleName(baseExpr);
                if (baseName != null &&
                    IsWorkspaceStaticMethod(baseName, memberName, visited))
                    return true;
            }
            return false;
        }

        private bool IsWorkspaceClassMethod(
            string className,
            string memberName,
            HashSet<string> visited)
        {
            if (string.IsNullOrEmpty(className)) return false;
            className = LastNameSegment(className);
            if (!visited.Add(className)) return false;
            if (!_workspaceClasses.TryGetValue(className, out ClassDef cls)) return false;

            foreach (Stmt stmt in cls.Body)
            {
                if (stmt is FunctionDef fn && fn.Name == memberName)
                    return HasDecorator(fn.DecoratorList, "classmethod");
                if (stmt is AsyncFunctionDef afn && afn.Name == memberName)
                    return HasDecorator(afn.DecoratorList, "classmethod");
            }

            foreach (Expr baseExpr in cls.Bases)
            {
                string baseName = BaseSimpleName(baseExpr);
                if (baseName != null
                    && IsWorkspaceClassMethod(baseName, memberName, visited))
                    return true;
            }
            return false;
        }

        private static bool HasDecorator(IReadOnlyList<Expr> decorators, string name)
        {
            if (decorators == null) return false;
            foreach (Expr decorator in decorators)
                if (DecoratorLastName(decorator) == name) return true;
            return false;
        }

        // Returns the member's type, UnknownRef to STAY SILENT (member unprovable), or null when
        // the member is genuinely absent (→ caller emits PY6001 "has no attribute").
        // `@property` / `@cached_property` (bare Name or qualified `functools.cached_property`).
        private static bool IsPropertyGetter(IReadOnlyList<Expr> decorators)
        {
            if (decorators == null) return false;
            foreach (Expr d in decorators)
            {
                string n = DecoratorLastName(d);
                if (n == "property" || n == "cached_property"
                    || n == "memoize_property") return true;
            }
            return false;
        }

        // `@overload` / `@typing.overload` / `@t.overload` — marks a type-only signature whose body is
        // `...`; the real implementation is the trailing same-named def with no @overload decorator.
        private static bool IsOverloadDecorated(IReadOnlyList<Expr> decorators)
        {
            if (decorators == null) return false;
            foreach (Expr d in decorators)
                if (DecoratorLastName(d) == "overload") return true;
            return false;
        }

        // `@name.setter` / `@name.deleter` — the value-yielding type is the getter's, so these
        // accessor re-definitions are skipped when resolving the property's read type.
        private static bool IsPropertyAccessorSetter(IReadOnlyList<Expr> decorators)
        {
            if (decorators == null) return false;
            foreach (Expr d in decorators)
            {
                if (d is AttributeExpr ae && (ae.Attr == "setter" || ae.Attr == "deleter")) return true;
            }
            return false;
        }

        private static string DecoratorLastName(Expr d)
        {
            switch (d)
            {
                case Name n: return n.Id;
                case AttributeExpr ae: return ae.Attr;
                case Call c: return DecoratorLastName(c.Func);
                default: return null;
            }
        }

        private TypeRef LookupMemberType(string className, string memberName, HashSet<string> visited)
        {
            if (className == null || !visited.Add(className)) return null; // cycle guard (A→B→A)

            // Workspace class first.
            ClassDef wcls;
            if (_workspaceClasses.TryGetValue(className, out wcls))
            {
                // An @overload-decorated def is one of several TYPE-ONLY signatures; the real body is
                // the trailing implementation (no @overload). Checking a call against the first
                // overload's narrow Literal params false-flags valid calls (click core.py:2461:
                // lookup_default(name, call=False) vs overload `call: Literal[True]`). So skip overload
                // stubs and use the implementation; keep the first overload only as a fallback for
                // stub-only sets (all @overload, no body — e.g. imported .pyi-style declarations).
                Stmt overloadFallback = null;
                foreach (Stmt s in wcls.Body)
                {
                    switch (s)
                    {
                        case FunctionDef fn when fn.Name == memberName:
                            // A @property/@cached_property getter, accessed as `self.x`, yields its
                            // RETURN type (the property value), not a callable — otherwise
                            // `self._buffer` read as `() -> List[Segment]` (rich console.py:876).
                            if (IsPropertyAccessorSetter(fn.DecoratorList)) break; // skip setter/deleter; keep scanning for the getter
                            if (IsPropertyGetter(fn.DecoratorList))
                                return fn.Returns != null ? AnnotationConverter.Convert(fn.Returns) : UnknownRef.Instance;
                            if (IsOverloadDecorated(fn.DecoratorList)) { if (overloadFallback == null) overloadFallback = s; break; }
                            return BuildCallableFromFunctionDef(fn.Args, fn.Returns);
                        case AsyncFunctionDef afn when afn.Name == memberName:
                            if (IsPropertyAccessorSetter(afn.DecoratorList)) break;
                            if (IsPropertyGetter(afn.DecoratorList))
                                return afn.Returns != null ? AnnotationConverter.Convert(afn.Returns) : UnknownRef.Instance;
                            if (IsOverloadDecorated(afn.DecoratorList)) { if (overloadFallback == null) overloadFallback = s; break; }
                            return BuildCallableFromFunctionDef(afn.Args, afn.Returns);
                        case AnnAssign an when an.Target is Name anName && anName.Id == memberName:
                            return AnnotationConverter.Convert(an.Annotation);
                        case ClassDef nested when nested.Name == memberName:
                            // A nested class is a class attribute of the enclosing class, reachable as
                            // `self.Nested` / `Outer.Nested` (Django Meta, pydantic Config,
                            // GenerateJsonSchema.ValidationsMapping). Stay silent about its type — the
                            // point is that the attribute EXISTS, so further access on it is gradual.
                            return UnknownRef.Instance;
                        case Assign asn:
                            foreach (Expr t in asn.Targets)
                            {
                                if (AssignmentTargetDeclaresName(t, memberName))
                                    return UnknownRef.Instance;
                            }
                            if (AssignDeclaresSlot(asn, memberName)) return UnknownRef.Instance;
                            break;
                    }
                }
                // No implementation body found — a stub-only overload set. Use the first overload's
                // signature so the return type is still known (arg checks may be imprecise here).
                if (overloadFallback is FunctionDef ofn) return BuildCallableFromFunctionDef(ofn.Args, ofn.Returns);
                if (overloadFallback is AsyncFunctionDef oafn) return BuildCallableFromFunctionDef(oafn.Args, oafn.Returns);
                // Fall back: scan every method body for `self.NAME = ...` assignments.
                // Captures instance attributes set at runtime via __init__ and friends
                // without us needing full data-flow analysis.
                if (InstanceAttributeSetOnSelf(wcls, memberName)) return UnknownRef.Instance;

                // Not in this class's own body — walk its base classes. A class that inherits
                // from an EXTERNAL/unresolved base (e.g. torch.nn.Module) may define the member
                // in that base, which we cannot see; flagging it would be a false positive (this
                // was the bulk of the 652 phantom errors on the sample workspace fixture — register_buffer,
                // to, training, ... inherited from nn.Module). So: recurse into workspace bases,
                // and if ANY base is external/unresolved, stay silent instead of reporting absence.
                bool hasUnresolvedBase = false;
                foreach (Expr baseExpr in wcls.Bases)
                {
                    // Peel a subscripted generic base (ParamType[_T] -> ParamType) so inherited
                    // members resolve through it (click: BoolParamType(ParamType[...]).self.fail).
                    Expr peeled = baseExpr;
                    while (peeled is Subscript sub) peeled = sub.Value;
                    string baseName = BaseSimpleName(peeled);
                    if (baseName == null) { hasUnresolvedBase = true; continue; } // complex base expr
                    if (baseName == "object") continue;                            // implicit root
                    if (_workspaceClasses.ContainsKey(baseName))
                    {
                        TypeRef inherited = LookupMemberType(baseName, memberName, visited);
                        if (inherited != null) return inherited;                    // found up the chain
                        // else: not on this workspace base; keep scanning siblings
                    }
                    else
                    {
                        // Base is not a workspace class. If it's a known stub class that declares
                        // the member, use it; otherwise the base is external (unstubbed lib) or a
                        // partial stub — either way we cannot prove absence, so stay silent.
                        StubDeclaration baseDecl = LookupClassDeclaration(baseName);
                        if (baseDecl != null && baseDecl.Members != null &&
                            baseDecl.Members.TryGetValue(memberName, out StubDeclaration bm))
                            return bm.Type ?? UnknownRef.Instance;
                        hasUnresolvedBase = true;
                    }
                }
                // Before reporting absence, consult the implicit `object` root: EVERY
                // Python class derives from object, so its universal members are always
                // present even when no base is declared (a plain `class ScriptConfig:`).
                // Without this, `self.__dict__` / `self.__class__` on a base-less
                // workspace class false-flagged PY6001 (script_config.py:378/390).
                if (!hasUnresolvedBase)
                {
                    StubDeclaration objDecl = LookupClassDeclaration("object");
                    if (objDecl != null && objDecl.Members != null &&
                        objDecl.Members.TryGetValue(memberName, out StubDeclaration om))
                        return om.Type ?? UnknownRef.Instance;
                }
                // A class that defines __getattr__ (or inherits one) synthesizes an attribute at
                // runtime for ANY name Python can't resolve normally — so an otherwise-absent member
                // is not provably wrong. click's ConsoleStream wraps a stream and delegates every
                // attribute through `def __getattr__(self, name): return getattr(self._text_stream,
                // name)` (_winconsole.py:218), which makes `.flush` (line 208) valid.
                if (ClassProvidesDynamicAttributes(className, new HashSet<string>(StringComparer.Ordinal)))
                    return UnknownRef.Instance;
                // Genuinely missing ONLY if every base was resolvable and lacked the member.
                return hasUnresolvedBase ? UnknownRef.Instance : (TypeRef)null;
            }
            // Stub class lookup — try bare name first, then builtins.<name>.
            StubDeclaration decl = LookupClassDeclaration(className);
            if (decl == null) return UnknownRef.Instance;   // unknown class: stay silent
            if (decl.Members != null && decl.Members.TryGetValue(memberName, out StubDeclaration m))
            {
                return m.Type ?? UnknownRef.Instance;
            }

            // Not on this stub class's own body — walk its stub base classes, exactly as the
            // workspace-class branch above does. Before iter 359 stub lookup stopped here, so a
            // member declared on a base (items/values on Mapping, inherited by MutableMapping;
            // append on MutableSequence's Sequence base; ...) was reported as "has no attribute"
            // on every subclass. That false-flagged idiomatic mapping/sequence code across
            // requests (self.adapters.items()/.values() on a MutableMapping field).
            if (decl.BaseNames != null && decl.BaseNames.Count > 0)
            {
                bool hasUnresolvedBase = false;
                foreach (string baseName in decl.BaseNames)
                {
                    if (string.IsNullOrEmpty(baseName)) { hasUnresolvedBase = true; continue; }
                    if (baseName == "object" || baseName == "builtins.object") continue;
                    if (LookupClassDeclaration(baseName) == null && !_workspaceClasses.ContainsKey(baseName))
                    {
                        hasUnresolvedBase = true;   // base is Protocol/Generic/unstubbed — can't prove absence
                        continue;
                    }
                    TypeRef inherited = LookupMemberType(baseName, memberName, visited);
                    if (inherited != null) return inherited;
                }
                // Every class ultimately derives from object, so its universal members
                // (__class__/__dict__/__doc__/__module__/__hash__/…) are always present — even when a
                // stub class's only declared base is `object` (skipped above) or it lists no base.
                // Without this, `Exception.__class__`, `Iterable.__class__`, `type.__module__` and the
                // like false-flagged PY6001 (pydantic).
                TypeRef fromObject = ObjectRootMember(memberName);
                if (fromObject != null) return fromObject;
                // A member not on the class or any base is genuinely absent ONLY when every base
                // was resolvable; an unresolvable base could carry it, so stay silent instead.
                return hasUnresolvedBase ? UnknownRef.Instance : (TypeRef)null;
            }
            TypeRef objMember = ObjectRootMember(memberName);
            if (objMember != null) return objMember;
            return null;
        }

        // A universal member declared on the implicit `object` root, or null. Read directly from the
        // object stub's declaration (no recursion), so every class inherits object's dunders.
        private TypeRef ObjectRootMember(string memberName)
        {
            StubDeclaration objDecl = LookupClassDeclaration("object");
            if (objDecl != null && objDecl.Members != null &&
                objDecl.Members.TryGetValue(memberName, out StubDeclaration om))
                return om.Type ?? UnknownRef.Instance;
            return null;
        }

        // True if the class — or any of its (workspace or stub) bases — defines `__getattr__`,
        // Python's dynamic-attribute fallback hook. Such a class fabricates a value for any name
        // normal lookup misses, so PY6001 "has no attribute" would be a false positive. `object`
        // never defines `__getattr__` (it defines `__getattribute__`, a different hook), so this
        // stays precise: only classes that opt into dynamic attributes are exempted.
        // True when a method body calls `setattr(self, X, ...)` where X is NOT a constant string — i.e.
        // it sets attributes whose names aren't statically known (Django Settings loads them from a
        // settings module). A constant-name setattr sets one specific attribute and is not "dynamic".
        private static bool MethodDynamicallySetsAttrOnSelf(IReadOnlyList<Stmt> body)
        {
            if (body == null) return false;
            foreach (Stmt s in body)
            {
                switch (s)
                {
                    case ExprStmt es when IsDynamicSetattrOnSelf(es.Value): return true;
                    case For f:
                        if (MethodDynamicallySetsAttrOnSelf(f.Body) || MethodDynamicallySetsAttrOnSelf(f.OrElse)) return true;
                        break;
                    case If i:
                        if (MethodDynamicallySetsAttrOnSelf(i.Body) || MethodDynamicallySetsAttrOnSelf(i.OrElse)) return true;
                        break;
                    case While w:
                        if (MethodDynamicallySetsAttrOnSelf(w.Body)) return true;
                        break;
                    case With wi:
                        if (MethodDynamicallySetsAttrOnSelf(wi.Body)) return true;
                        break;
                    case Try t:
                        if (MethodDynamicallySetsAttrOnSelf(t.Body) || MethodDynamicallySetsAttrOnSelf(t.OrElse)
                            || MethodDynamicallySetsAttrOnSelf(t.FinalBody)) return true;
                        foreach (Ast.ExceptHandler h in t.Handlers)
                            if (MethodDynamicallySetsAttrOnSelf(h.Body)) return true;
                        break;
                }
            }
            return false;
        }

        private static bool IsDynamicSetattrOnSelf(Expr e)
        {
            Call call = e as Call;
            if (call == null || call.Args == null || call.Args.Count < 2) return false;
            if (!(call.Func is Name fn && fn.Id == "setattr")) return false;
            if (!(call.Args[0] is Name self && self.Id == "self")) return false;
            // Dynamic only when the name argument is not a constant string literal.
            return !(call.Args[1] is Constant c && c.Value is string);
        }

        private bool ClassProvidesDynamicAttributes(string className, HashSet<string> visited)
        {
            if (className == null || !visited.Add(className)) return false;
            if (_workspaceClasses.TryGetValue(className, out ClassDef wcls))
            {
                foreach (Stmt s in wcls.Body)
                {
                    if (s is FunctionDef fn)
                    {
                        if (fn.Name == "__getattr__") return true;
                        // A method that calls `setattr(self, <name>, ...)` with a non-constant name
                        // populates arbitrary instance attributes at runtime — Django's
                        // Settings.__init__ does `setattr(self, setting, ...)` over dir(global_settings),
                        // so self.TIME_ZONE etc. are real but invisible to a literal-assignment scan.
                        if (MethodDynamicallySetsAttrOnSelf(fn.Body)) return true;
                    }
                    if (s is AsyncFunctionDef afn)
                    {
                        if (afn.Name == "__getattr__") return true;
                        if (MethodDynamicallySetsAttrOnSelf(afn.Body)) return true;
                    }
                }
                foreach (Expr baseExpr in wcls.Bases)
                {
                    Expr peeled = baseExpr;
                    while (peeled is Subscript sub) peeled = sub.Value;
                    string baseName = BaseSimpleName(peeled);
                    if (baseName != null && baseName != "object" &&
                        ClassProvidesDynamicAttributes(baseName, visited)) return true;
                }
                return false;
            }
            StubDeclaration decl = LookupClassDeclaration(className);
            if (decl != null)
            {
                if (decl.Members != null && decl.Members.ContainsKey("__getattr__")) return true;
                if (decl.BaseNames != null)
                    foreach (string b in decl.BaseNames)
                        if (!string.IsNullOrEmpty(b) && b != "object" && b != "builtins.object" &&
                            ClassProvidesDynamicAttributes(b, visited)) return true;
            }
            return false;
        }

        // True if the class (or, transitively, a workspace base) inherits from a base we
        // cannot resolve to a workspace class or a bundled stub — i.e. an external library
        // type (torch.nn.Module, pd.DataFrame, ...) or a complex base expression. Such a
        // base can inject members / __call__ / __new__ we can't see, so both attribute and
        // constructor-arity checks must stay silent to avoid false positives.
        private bool HasUnresolvedExternalBase(ClassDef wcls, HashSet<string> visited)
        {
            if (wcls == null) return false;
            foreach (Expr baseExpr in wcls.Bases)
            {
                string baseName = BaseSimpleName(baseExpr);
                if (baseName == null) return true;        // complex base expression
                if (baseName == "object") continue;       // implicit root, always resolved
                ClassDef baseCls;
                if (_workspaceClasses.TryGetValue(baseName, out baseCls))
                {
                    if (!visited.Add(baseName)) continue;  // cycle guard
                    if (HasUnresolvedExternalBase(baseCls, visited)) return true;
                    continue;
                }
                if (LookupClassDeclaration(baseName) != null) continue; // known bundled stub
                return true;                                // external / unstubbed base
            }
            return false;
        }

        // A dict-display literal (`{...}`) structurally satisfies a TypedDict-derived annotation:
        // `return {"k": v}` / `x: SomeInfoDict = {...}` is idiomatic and accepted by type checkers
        // (click types.py: to_info_dict returns a plain dict display against a TypedDict). We do not
        // model the per-key fields, so any dict DISPLAY is treated as satisfying a TypedDict target.
        // A non-display value (a str, or a dict held in a variable) is unaffected and still checked,
        // so genuine mismatches (`return "x"` against a TypedDict) are preserved.
        private bool DictDisplaySatisfiesTypedDict(Expr value, TypeRef expected)
        {
            if (!(value is Dict)) return false;
            return ExpectedNamesTypedDict(expected);
        }

        // A literal CONSTANT (`return True`, `return 0`, `x: Literal["r"] = "r"`) satisfies a
        // Literal[...] annotation when its value is one of the Literal's members. The inferencer
        // widens the constant to bool/int/str, losing the literal-ness, so `def isatty() ->
        // Literal[True]: return True` was rejected (click _winconsole.py). Scoped to literal
        // constants: `return some_bool` (a variable) is NOT a Literal[True] and stays checked.
        private static bool LiteralConstantSatisfiesExpected(Expr value, TypeRef expected)
        {
            if (!(value is Constant c)) return false;
            return ExpectedLiteralAccepts(expected, c.Value);
        }

        private static bool ExpectedLiteralAccepts(TypeRef expected, object constValue)
        {
            switch (expected)
            {
                case LiteralRef lr: return LiteralValueEquals(lr.Value, constValue);
                case NoneRef _: return constValue == null;
                case UnionRef ur:
                    foreach (TypeRef m in ur.Members)
                        if (ExpectedLiteralAccepts(m, constValue)) return true;
                    return false;
                default: return false;
            }
        }

        private static bool LiteralValueEquals(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is bool || b is bool) return a is bool && b is bool && (bool)a == (bool)b;
            bool aInt = a is int || a is long;
            bool bInt = b is int || b is long;
            if (aInt && bInt) return Convert.ToInt64(a) == Convert.ToInt64(b);
            return a.Equals(b);
        }

        // True when `expected` names a class whose base chain reaches typing.TypedDict — either the
        // bundled `class TypedDict` itself, or a workspace class that (transitively) derives from it.
        private bool ExpectedNamesTypedDict(TypeRef expected)
        {
            string name = null;
            if (expected is ClassRef cr) name = cr.QualifiedName;
            else if (expected is GenericRef gr) name = gr.Base.QualifiedName;
            if (string.IsNullOrEmpty(name)) return false;
            return ClassChainReachesTypedDict(name, new HashSet<string>(StringComparer.Ordinal));
        }

        private bool ClassChainReachesTypedDict(string className, HashSet<string> visited)
        {
            string bare = LastNameSegment(className);
            if (string.IsNullOrEmpty(bare)) return false;
            if (bare == "TypedDict") return true;
            if (!visited.Add(bare)) return false;
            ClassDef wcls;
            if (_workspaceClasses.TryGetValue(bare, out wcls))
            {
                foreach (Expr baseExpr in wcls.Bases)
                {
                    string baseName = BaseSimpleName(baseExpr);   // "t.TypedDict" | "ParamTypeInfoDict" | ...
                    if (string.IsNullOrEmpty(baseName)) continue;
                    if (ClassChainReachesTypedDict(baseName, visited)) return true;
                }
            }
            return false;
        }

        // True when a workspace class (transitively) derives from typing.Protocol — a STRUCTURAL type.
        // Any class with the right members satisfies it, which this stub-only checker cannot verify, so
        // a Protocol target must not be nominally judged (fastapi routing.py: APIRoute passed where the
        // `_RouteWithPath(Protocol)` structural type is expected).
        private bool ClassChainReachesProtocol(string className, HashSet<string> visited)
        {
            string bare = LastNameSegment(className);
            if (string.IsNullOrEmpty(bare)) return false;
            if (bare == "Protocol") return true;
            if (!visited.Add(bare)) return false;
            ClassDef wcls;
            if (_workspaceClasses.TryGetValue(bare, out wcls))
            {
                foreach (Expr baseExpr in wcls.Bases)
                {
                    string baseName = BaseSimpleName(baseExpr);   // "Protocol" | "t.Protocol" | "Protocol[T]"
                    if (string.IsNullOrEmpty(baseName)) continue;
                    if (ClassChainReachesProtocol(baseName, visited)) return true;
                }
            }
            return false;
        }

        private static string LastNameSegment(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int dot = name.LastIndexOf('.');
            return dot >= 0 ? name.Substring(dot + 1) : name;
        }

        // Copy of the current scope's bindings — a branch's exit environment.
        private Dictionary<string, TypeRef> SnapshotCurrentBindings()
        {
            Dictionary<string, TypeRef> d = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, TypeRef> kv in _env.Current.Bindings) d[kv.Key] = kv.Value;
            return d;
        }

        // Least-upper-bound of two branch-exit types: same type → itself; any dynamic
        // (Any/Unknown) → dynamic (stay lenient); otherwise the flattened, de-duplicated
        // union of members. `int` ⊔ `int` = `int` (so `if x is None: x = len(q)` yields int).
        private static TypeRef JoinTypes(TypeRef a, TypeRef b)
        {
            if (a == null) return b;
            if (b == null) return a;
            if (ReferenceEquals(a, b)) return a;
            if (a is AnyRef || b is AnyRef) return AnyRef.Instance;
            if (a is UnknownRef || b is UnknownRef) return UnknownRef.Instance;
            if (string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal)) return a;

            List<TypeRef> members = new List<TypeRef>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            AddUnionMembers(members, seen, a);
            AddUnionMembers(members, seen, b);
            return members.Count == 1 ? members[0] : new UnionRef(members);
        }

        private static void AddUnionMembers(List<TypeRef> into, HashSet<string> seen, TypeRef t)
        {
            if (t is UnionRef u)
            {
                for (int i = 0; i < u.Members.Count; i++) AddUnionMembers(into, seen, u.Members[i]);
                return;
            }
            if (t != null && seen.Add(t.ToString())) into.Add(t);
        }

        // True if `body` cannot fall through to the statement after it — its last
        // statement unconditionally leaves the enclosing block (return / raise / break /
        // continue, a NoReturn call like sys.exit(), or a nested if whose both branches
        // terminate). Used for guard-clause narrowing.
        private bool AlwaysTerminates(IReadOnlyList<Stmt> body)
        {
            if (body == null || body.Count == 0) return false;
            Stmt last = body[body.Count - 1];
            switch (last)
            {
                case Return _: return true;
                case Raise _: return true;
                case Break _: return true;
                case Continue _: return true;
                case ExprStmt es: return IsNoReturnCall(es.Value);
                case If nested:
                    return nested.OrElse != null && nested.OrElse.Count > 0
                        && AlwaysTerminates(nested.Body) && AlwaysTerminates(nested.OrElse);
                default: return false;
            }
        }

        // Recognizes the handful of stdlib calls that never return (annotated NoReturn in
        // typeshed): sys.exit / exit / quit / os._exit / os.abort. A call to one of these
        // as a statement terminates its block just like `return`/`raise`.
        private bool IsNoReturnCall(Expr e)
        {
            Call c = e as Call;
            if (c == null) return false;
            string name = CalleeDottedName(c.Func);
            switch (name)
            {
                case "sys.exit":
                case "exit":
                case "quit":
                case "os._exit":
                case "os.abort":
                    return true;
            }
            // A call whose callee resolves to a function/method annotated `-> NoReturn` / `-> Never`
            // (both become NeverRef) never returns, so the branch that ends with it terminates. This
            // narrows the fall-through: `if x is None: self.fail(...)` leaves x non-None afterward
            // (click ParamType.convert: str_to_bool(...) then `if ... is None: self.fail(...)`).
            bool prev = _suppressAttrNotFound;
            _suppressAttrNotFound = true;
            TypeRef calleeType;
            try { calleeType = InferExpr(c.Func); }
            finally { _suppressAttrNotFound = prev; }
            return calleeType is CallableRef cr && cr.ReturnType is NeverRef;
        }

        // Dotted name of a call target: `exit` → "exit", `sys.exit` → "sys.exit".
        // Returns null for anything more complex (subscript, call chain, ...).
        private static string CalleeDottedName(Expr func)
        {
            switch (func)
            {
                case Name n: return n.Id;
                case AttributeExpr ae:
                    string baseName = CalleeDottedName(ae.Value);
                    return baseName == null ? null : baseName + "." + ae.Attr;
                default: return null;
            }
        }

        // The simple (last-segment) name of a base-class expression: `Base` → "Base",
        // `nn.Module` / `torch.nn.Module` → "Module". Anything else (subscripted generic base,
        // call, etc.) → null, which the caller treats as an unresolved base (stay silent).
        private static string BaseSimpleName(Expr baseExpr)
        {
            switch (baseExpr)
            {
                case Name n: return n.Id;
                // A QUALIFIED base names a specific module's class — keep the whole dotted path
                // ("textwrap.TextWrapper"), not just the last segment. Collapsing it to "TextWrapper"
                // made an EXTERNAL stdlib base collide with a workspace class of the same name
                // (click's `class TextWrapper(textwrap.TextWrapper)`): the base resolved to the class
                // itself, the cycle guard returned nothing, and — because the "base" looked resolved —
                // every inherited attribute (self.width, self.initial_indent, ...) false-flagged
                // PY6001. The dotted name isn't a workspace class and (absent a textwrap stub) isn't a
                // known stub either, so the external-base suppression correctly stays silent.
                // LookupClassDeclaration already resolves qualified stub names (os.PathLike, ...).
                case AttributeExpr ae: return DottedBaseName(ae);
                default: return null;
            }
        }

        // Full dotted path of an attribute chain used as a base class ("a.b.C"); null if the chain
        // does not bottom out in a plain name (a subscripted or called base we can't name).
        private static string DottedBaseName(AttributeExpr a)
        {
            List<string> parts = new List<string> { a.Attr };
            Expr cur = a.Value;
            while (cur is AttributeExpr inner) { parts.Add(inner.Attr); cur = inner.Value; }
            if (!(cur is Name nm)) return null;
            parts.Add(nm.Id);
            parts.Reverse();
            return string.Join(".", parts);
        }

        // True when a workspace SUBCLASS of <paramref name="baseClassName"/> declares or self-assigns
        // <paramref name="memberName"/> — so a base/mixin that references it is not really "missing" the
        // attribute; the concrete subclass provides it at runtime. Scans the per-module class set (base
        // and subclass usually live together, e.g. BaseModelAdmin/ModelAdmin in Django's options.py).
        private bool AttributeProvidedBySubclass(string baseClassName, string memberName)
        {
            string bare = LastNameSegment(baseClassName);
            if (string.IsNullOrEmpty(bare)) return false;
            foreach (KeyValuePair<string, ClassDef> kv in _workspaceClasses)
            {
                if (string.Equals(kv.Key, bare, StringComparison.Ordinal)) continue;
                if (!IsWorkspaceSubclassOf(kv.Key, bare, new HashSet<string>(StringComparer.Ordinal))) continue;
                // The concrete subclass supplies the member — directly, or by inheriting it from one of
                // its OTHER workspace bases. A MIXIN's self.lhs is satisfied by the sibling base it is
                // combined with: `class IntegerFieldExact(FieldGetDbPrepValueMixin, Lookup)`, where
                // Lookup.__init__ sets self.lhs. Walk the subclass's whole workspace hierarchy (sound:
                // only real declarations count, no external-base leniency).
                if (WorkspaceHierarchyDeclaresMember(kv.Key, memberName, new HashSet<string>(StringComparer.Ordinal)))
                    return true;
            }

            // A mixin's concrete subclass often lives in ANOTHER module (Django's FileProxyMixin is in
            // core.files.utils; File(FileProxyMixin), which sets self.file, is in core.files.base). Ask
            // the global reverse index for every workspace class that names this one as a direct base,
            // then walk each such subclass's hierarchy by FQN.
            if (_workspace != null)
            {
                foreach (string subFqn in _workspace.SubclassFqnsOf(bare))
                    if (WorkspaceHierarchyDeclaresMemberFqn(subFqn, memberName, new HashSet<string>(StringComparer.Ordinal)))
                        return true;
            }
            return false;
        }

        // True when <paramref name="className"/> or any of its WORKSPACE base classes (transitively)
        // directly declares or self-assigns <paramref name="memberName"/>. Unlike LookupMemberType this
        // does NOT apply external-base leniency — a class with an unresolved base does not vacuously
        // "provide" every member — so it can't over-suppress.
        private bool WorkspaceHierarchyDeclaresMember(string className, string memberName, HashSet<string> visited)
        {
            string bare = LastNameSegment(className);
            if (string.IsNullOrEmpty(bare) || !visited.Add(bare)) return false;
            ClassDef wcls;
            if (!_workspaceClasses.TryGetValue(bare, out wcls))
            {
                // Not defined in this module — follow the import into its module (Django's Lookup lives
                // in db.models.lookups; a concrete class here inherits it). Cross-module, keyed by FQN.
                string importedFqn;
                if (_importedClassFqns.TryGetValue(bare, out importedFqn))
                    return WorkspaceHierarchyDeclaresMemberFqn(importedFqn, memberName, new HashSet<string>(StringComparer.Ordinal));
                // Relative imports are not represented as absolute local aliases here. A
                // workspace-wide simple-name fallback is safe only when the class is unique.
                string uniqueFqn = _workspace?.ResolveUniqueClassFqn(bare);
                if (uniqueFqn != null)
                    return WorkspaceHierarchyDeclaresMemberFqn(
                        uniqueFqn, memberName, new HashSet<string>(StringComparer.Ordinal));
                return false;
            }
            if (SubclassDeclaresOrAssigns(wcls, memberName)) return true;
            foreach (Expr baseExpr in wcls.Bases)
            {
                Expr peeled = baseExpr;
                while (peeled is Subscript sub) peeled = sub.Value;
                string bn = BaseSimpleName(peeled);
                if (!string.IsNullOrEmpty(bn) && WorkspaceHierarchyDeclaresMember(bn, memberName, visited))
                    return true;
            }
            return false;
        }

        // Cross-module counterpart of WorkspaceHierarchyDeclaresMember, walking by fully-qualified name
        // through the WorkspaceIndex. A base name inside the resolved class is resolved against the
        // SAME module prefix first (a class and its base usually share a module — Exact / Lookup both in
        // db.models.lookups), which covers the common cooperative-hierarchy case without a full
        // per-module import map for every other file. Sound: only real declarations count.
        private bool WorkspaceHierarchyDeclaresMemberFqn(string fqn, string memberName, HashSet<string> visited)
        {
            if (_workspace == null || string.IsNullOrEmpty(fqn) || !visited.Add(fqn)) return false;
            Ast.ClassDef cls = _workspace.ResolveClassDefLoose(fqn);   // follows package re-exports
            if (cls == null) return false;
            if (SubclassDeclaresOrAssigns(cls, memberName)) return true;
            int dot = fqn.LastIndexOf('.');
            string modulePrefix = dot > 0 ? fqn.Substring(0, dot) : string.Empty;
            foreach (Expr baseExpr in cls.Bases)
            {
                Expr peeled = baseExpr;
                while (peeled is Subscript sub) peeled = sub.Value;
                string bn = BaseSimpleName(peeled);
                if (string.IsNullOrEmpty(bn)) continue;
                string last = LastNameSegment(bn);
                if (!string.IsNullOrEmpty(modulePrefix)
                    && WorkspaceHierarchyDeclaresMemberFqn(modulePrefix + "." + last, memberName, visited)) return true;
                // Also try the base name as a full FQN of its own (dotted import path).
                if (bn != last && WorkspaceHierarchyDeclaresMemberFqn(bn, memberName, visited)) return true;
                // A relative-imported base may be defined in a sibling module rather than
                // directly under this module prefix. Follow it only when its simple name
                // identifies exactly one class in the workspace.
                string uniqueFqn = _workspace.ResolveUniqueClassFqn(last);
                if (uniqueFqn != null
                    && WorkspaceHierarchyDeclaresMemberFqn(
                        uniqueFqn, memberName, visited))
                    return true;
            }
            return false;
        }

        // True when a class body declares <paramref name="memberName"/> (a def / annotated or plain
        // class variable / nested class) or a method sets it on self.
        private static bool SubclassDeclaresOrAssigns(ClassDef cls, string memberName)
        {
            foreach (Stmt s in cls.Body)
            {
                if (s is FunctionDef fn && fn.Name == memberName) return true;
                if (s is AsyncFunctionDef afn && afn.Name == memberName) return true;
                if (s is AnnAssign an && an.Target is Name anN && anN.Id == memberName) return true;
                if (s is ClassDef nc && nc.Name == memberName) return true;
                if (s is Assign asn)
                {
                    foreach (Expr t in asn.Targets)
                        if (AssignmentTargetDeclaresName(t, memberName)) return true;
                    if (AssignDeclaresSlot(asn, memberName)) return true;
                }
            }
            return InstanceAttributeSetOnSelf(cls, memberName);
        }

        private static bool AssignDeclaresSlot(Assign assignment, string memberName)
        {
            bool isSlots = assignment.Targets.Any(t => t is Name n && n.Id == "__slots__");
            if (!isSlots) return false;
            IReadOnlyList<Expr> entries = null;
            if (assignment.Value is Ast.TupleExpr tuple) entries = tuple.Elts;
            else if (assignment.Value is ListExpr list) entries = list.Elts;
            if (entries == null) return false;
            return entries.Any(e => e is Constant c && c.Value is string s && s == memberName);
        }

        private static bool AssignmentTargetDeclaresName(Expr target, string memberName)
        {
            switch (target)
            {
                case Name name:
                    return name.Id == memberName;
                case Ast.TupleExpr tuple:
                    return tuple.Elts.Any(e => AssignmentTargetDeclaresName(e, memberName));
                case ListExpr list:
                    return list.Elts.Any(e => AssignmentTargetDeclaresName(e, memberName));
                case Starred starred:
                    return AssignmentTargetDeclaresName(starred.Value, memberName);
                default:
                    return false;
            }
        }

        private static bool CanOverloadRichComparison(TypeRef type)
        {
            if (type is ClassRef cls)
                return !cls.QualifiedName.StartsWith("builtins.", StringComparison.Ordinal);
            if (type is GenericRef generic)
                return CanOverloadRichComparison(generic.Base);
            if (type is UnionRef union)
                return union.Members.Any(CanOverloadRichComparison);
            return false;
        }

        private static bool InstanceAttributeSetOnSelf(ClassDef cls, string memberName)
        {
            foreach (Stmt s in cls.Body)
            {
                IReadOnlyList<Stmt> body = null;
                Arguments args = null;
                IReadOnlyList<Expr> decorators = null;
                if (s is FunctionDef fn)
                {
                    body = fn.Body;
                    args = fn.Args;
                    decorators = fn.DecoratorList;
                }
                else if (s is AsyncFunctionDef afn)
                {
                    body = afn.Body;
                    args = afn.Args;
                    decorators = afn.DecoratorList;
                }
                if (body == null) continue;
                string receiverName = HasDecorator(decorators, "staticmethod")
                    ? null
                    : FirstParameterName(args);
                if (receiverName != null
                    && ScanBodyForSelfAttr(body, receiverName, memberName))
                    return true;
                if (s is FunctionDef factory &&
                    (factory.Name == "__new__" || HasDecorator(factory.DecoratorList, "classmethod")) &&
                    ScanFactoryForReturnedObjectAttr(factory.Body, memberName))
                    return true;
            }
            return false;
        }

        private static string FirstParameterName(Arguments args)
        {
            if (args == null) return null;
            if (args.PosOnlyArgs != null && args.PosOnlyArgs.Count > 0)
                return args.PosOnlyArgs[0].Name;
            if (args.Args != null && args.Args.Count > 0)
                return args.Args[0].Name;
            return null;
        }

        private static bool ScanFactoryForReturnedObjectAttr(
            IReadOnlyList<Stmt> body, string memberName)
        {
            HashSet<string> returnedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Stmt stmt in body)
                if (stmt is Return ret && ret.Value is Name returned)
                    returnedNames.Add(returned.Id);
            if (returnedNames.Count == 0) return false;

            foreach (Stmt stmt in body)
            {
                if (stmt is Assign assignment)
                    foreach (Expr target in assignment.Targets)
                        if (TargetSetsNamedAttr(target, returnedNames, memberName)) return true;
                if (stmt is AnnAssign annotated &&
                    TargetSetsNamedAttr(annotated.Target, returnedNames, memberName)) return true;
            }
            return false;
        }

        private static bool TargetSetsNamedAttr(
            Expr target, HashSet<string> objectNames, string memberName)
        {
            switch (target)
            {
                case AttributeExpr attribute:
                    return attribute.Value is Name name &&
                           objectNames.Contains(name.Id) &&
                           attribute.Attr == memberName;
                case Ast.TupleExpr tuple:
                    return tuple.Elts.Any(e => TargetSetsNamedAttr(e, objectNames, memberName));
                case ListExpr list:
                    return list.Elts.Any(e => TargetSetsNamedAttr(e, objectNames, memberName));
                case Starred starred:
                    return TargetSetsNamedAttr(starred.Value, objectNames, memberName);
                default:
                    return false;
            }
        }

        // Does an assignment target set `self.<memberName>`? Handles a plain `self.x`, and — new in
        // iter 372 — tuple/list unpacking (`self.a, self.b = ...`, common for setting several
        // instance attributes at once, e.g. rich Box's `self.top_left, self.top, ... = iter(line)`)
        // and starred targets (`self.head, *self.rest = ...`), recursing through nested patterns.
        private static bool TargetSetsSelfAttr(
            Expr target, string receiverName, string memberName)
        {
            switch (target)
            {
                case AttributeExpr ae:
                    return ae.Value is Name n && n.Id == receiverName && ae.Attr == memberName;
                case Ast.TupleExpr tup:
                    foreach (Expr el in tup.Elts)
                        if (TargetSetsSelfAttr(el, receiverName, memberName)) return true;
                    return false;
                case ListExpr lst:
                    foreach (Expr el in lst.Elts)
                        if (TargetSetsSelfAttr(el, receiverName, memberName)) return true;
                    return false;
                case Starred st:
                    return TargetSetsSelfAttr(st.Value, receiverName, memberName);
                default:
                    return false;
            }
        }

        private static bool ScanBodyForSelfAttr(
            IReadOnlyList<Stmt> body, string receiverName, string memberName)
        {
            foreach (Stmt s in body)
            {
                switch (s)
                {
                    case Assign asn:
                        foreach (Expr t in asn.Targets)
                            if (TargetSetsSelfAttr(t, receiverName, memberName)) return true;
                        break;
                    case AnnAssign an when an.Target is AttributeExpr aae &&
                                            aae.Value is Name nm && nm.Id == receiverName &&
                                            aae.Attr == memberName:
                        return true;
                    case If i:
                        if (ScanBodyForSelfAttr(i.Body, receiverName, memberName)) return true;
                        if (ScanBodyForSelfAttr(i.OrElse, receiverName, memberName)) return true;
                        break;
                    case For ff:
                        if (ScanBodyForSelfAttr(ff.Body, receiverName, memberName)) return true;
                        break;
                    case While w:
                        if (ScanBodyForSelfAttr(w.Body, receiverName, memberName)) return true;
                        break;
                    case Try tr:
                        if (ScanBodyForSelfAttr(tr.Body, receiverName, memberName)) return true;
                        foreach (Ast.ExceptHandler h in tr.Handlers)
                            if (ScanBodyForSelfAttr(h.Body, receiverName, memberName)) return true;
                        break;
                    case With wi:
                        if (ScanBodyForSelfAttr(wi.Body, receiverName, memberName)) return true;
                        break;
                }
            }
            return false;
        }

        /// <summary>Find the stub class declaration matching <paramref name="name"/> — bare or qualified.</summary>
        private StubDeclaration LookupClassDeclaration(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            // Qualified: "os.PathLike" → resolve directly
            StubDeclaration qDecl = _stubs.Resolve(name);
            if (qDecl != null && qDecl.Kind == StubKind.Class) return qDecl;
            // Bare: try builtins.<name>, typing.<name>
            foreach (string modName in new[] { "builtins", "typing" })
            {
                StubDeclaration d = _stubs.Resolve(modName + "." + name);
                if (d != null && d.Kind == StubKind.Class) return d;
            }
            return null;
        }

        // ---------- Helpers ----------

        // Decorators known to preserve a function's CALL signature (arity + parameters), so a call to
        // the decorated function can still be arity-checked. Anything else — @click.command(),
        // @app.route(), @pytest.fixture, custom wrappers — can replace the signature wholesale, so we
        // must not arity-check through it.
        private static readonly HashSet<string> SignaturePreservingDecorators = new HashSet<string>(StringComparer.Ordinal)
        {
            "staticmethod", "classmethod", "property", "cached_property", "abstractmethod", "abstractproperty",
            "final", "override", "overload", "no_type_check", "runtime_checkable", "wraps", "lru_cache",
            "cache", "total_ordering", "deprecated", "setter", "getter", "deleter",
            "contextmanager", "asynccontextmanager"
        };

        private static bool HasOnlySignaturePreservingDecorators(IReadOnlyList<Expr> decorators)
        {
            if (decorators == null || decorators.Count == 0) return true;
            foreach (Expr d in decorators)
            {
                string n = DecoratorLastName(d);
                if (n == null || !SignaturePreservingDecorators.Contains(n)) return false;
            }
            return true;
        }

        // Bind a module-level function: its precise callable normally, but a GRADUAL one (ellipsis
        // params, so calls skip the arity/arg checks) when a signature-transforming decorator is
        // present. `@click.command()` turns `main(<30 options>)` into a 0-arg Command, so `main()`
        // must not report "0 given, 30 required" (black __init__.py:1718).
        internal static TypeRef BuildDecoratedFunctionBinding(
            Arguments args, Expr returns, IReadOnlyList<Expr> decorators)
        {
            CallableRef c = BuildCallableFromFunctionDef(args, returns);
            if (HasOnlySignaturePreservingDecorators(decorators)) return c;
            return new CallableRef(c.Params, c.ReturnType, isEllipsisParams: true);
        }

        internal static CallableRef BuildCallableFromFunctionDef(Arguments args, Expr returns)
        {
            List<CallableParam> list = new List<CallableParam>();
            if (args != null)
            {
                int posOnlyCount = args.PosOnlyArgs.Count;
                int defaultBase = (posOnlyCount + args.Args.Count) - args.Defaults.Count;
                int idx = 0;
                foreach (Arg a in args.PosOnlyArgs)
                {
                    bool hasDef = idx >= defaultBase;
                    Expr defExpr = hasDef && (idx - defaultBase) < args.Defaults.Count ? args.Defaults[idx - defaultBase] : null;
                    TypeRef pt = a.Annotation != null ? AnnotationConverter.Convert(a.Annotation) : AnyRef.Instance;
                    list.Add(new CallableParam(a.Name, ApplyImplicitOptional(pt, defExpr),
                        CallableParamKind.PositionalOnly, hasDef));
                    idx++;
                }
                foreach (Arg a in args.Args)
                {
                    bool hasDef = idx >= defaultBase;
                    Expr defExpr = hasDef && (idx - defaultBase) < args.Defaults.Count ? args.Defaults[idx - defaultBase] : null;
                    TypeRef pt = a.Annotation != null ? AnnotationConverter.Convert(a.Annotation) : AnyRef.Instance;
                    list.Add(new CallableParam(a.Name, ApplyImplicitOptional(pt, defExpr),
                        CallableParamKind.PositionalOrKeyword, hasDef));
                    idx++;
                }
                if (args.VarArg != null)
                    list.Add(new CallableParam(args.VarArg.Name,
                        args.VarArg.Annotation != null ? AnnotationConverter.Convert(args.VarArg.Annotation) : AnyRef.Instance,
                        CallableParamKind.VarPositional, false));
                for (int i = 0; i < args.KwOnlyArgs.Count; i++)
                {
                    Arg a = args.KwOnlyArgs[i];
                    Expr defExpr = i < args.KwDefaults.Count ? args.KwDefaults[i] : null;
                    bool hasDef = defExpr != null;
                    TypeRef pt = a.Annotation != null ? AnnotationConverter.Convert(a.Annotation) : AnyRef.Instance;
                    list.Add(new CallableParam(a.Name, ApplyImplicitOptional(pt, defExpr),
                        CallableParamKind.KeywordOnly, hasDef));
                }
                if (args.KwArg != null)
                    list.Add(new CallableParam(args.KwArg.Name,
                        args.KwArg.Annotation != null ? AnnotationConverter.Convert(args.KwArg.Annotation) : AnyRef.Instance,
                        CallableParamKind.VarKeyword, false));
            }
            TypeRef ret = returns != null ? AnnotationConverter.Convert(returns) : UnknownRef.Instance;
            return new CallableRef(list, ret);
        }

        // PEP 484 "implicit Optional" (the historical mypy default and near-universal in
        // real code): a parameter annotated `T` whose default value is the literal `None`
        // is treated as `Optional[T]`. `def build_allatom_model(ipa_ckpt: str = None)` with
        // a body like `if ipa_ckpt:` clearly accepts None — flagging a None argument was a
        // false positive (test_allatom_model.py:29). No-op unless the default is None and
        // the annotation is an explicit, non-optional, non-dynamic type.
        private static TypeRef ApplyImplicitOptional(TypeRef annotated, Expr defaultExpr)
        {
            if (defaultExpr == null || !IsNoneLiteral(defaultExpr)) return annotated;
            if (annotated == null || annotated is AnyRef || annotated is UnknownRef || annotated is NoneRef)
                return annotated;
            if (annotated is UnionRef u)
            {
                for (int i = 0; i < u.Members.Count; i++)
                    if (u.Members[i] is NoneRef) return annotated;   // already optional
                List<TypeRef> widened = new List<TypeRef>(u.Members) { NoneRef.Instance };
                return new UnionRef(widened);
            }
            return new UnionRef(new List<TypeRef> { annotated, NoneRef.Instance });
        }

        private static bool IsNoneLiteral(Expr e)
        {
            Constant c = e as Constant;
            if (c != null && c.Value == null) return true;
            Name n = e as Name;
            return n != null && n.Id == "None";
        }

        private static TypeRef PreserveAssignedLiteral(Expr value, TypeRef inferred)
        {
            // A plain assignment establishes the exact value until a later write. Keeping that
            // literal precision is essential when branches extend a Literal union:
            // `mode = lookup.get(...); if mode is None: mode = "uniform"`.
            // LiteralRef remains assignable to its ordinary runtime class (str/int/bool), so normal
            // arguments retain their existing compatibility while literal-constrained APIs gain
            // the precision they require.
            if (value is Constant c && c.Value != null && !(c.Value is ConstantEllipsis))
                return new LiteralRef(c.Value);
            return inferred;
        }

        private void BindAssignTarget(Expr target, TypeRef rhsType)
        {
            if (target == null) return;
            switch (target)
            {
                case Name n: _env.Bind(n.Id, rhsType); break;
                case Ast.TupleExpr tup:
                    foreach (Expr el in tup.Elts) BindAssignTarget(el, UnknownRef.Instance);
                    break;
                case Ast.ListExpr lst:
                    foreach (Expr el in lst.Elts) BindAssignTarget(el, UnknownRef.Instance);
                    break;
                case Starred st:
                    BindAssignTarget(st.Value, UnknownRef.Instance);
                    break;
                case AttributeExpr ae:
                    {
                        // Flow-sensitive attribute narrowing on assignment: after `self._hash =
                        // hash(...)` reads of `self._hash` see the assigned type, not the declared
                        // union (int | None) — rich style.py:445. Keyed by dotted path;
                        // InferAttribute consults _env.Resolve(path) first (iter 370). An
                        // Unknown/Any RHS binds too (we assigned *something*, so the stale declared
                        // type no longer applies — reads become leniently unknown, not falsely
                        // typed), which is the flow-typing-correct outcome.
                        string path = NarrowingEngine.DottedPath(ae);
                        if (path != null && rhsType != null)
                            _env.Bind(path, rhsType);
                        break;
                    }
                case Subscript _:
                    // No local binding — assignment to a subscript affects the target, not locals.
                    break;
            }
        }

        private static string TopDotted(string dotted)
        {
            if (string.IsNullOrEmpty(dotted)) return dotted;
            int dot = dotted.IndexOf('.');
            return dot < 0 ? dotted : dotted.Substring(0, dot);
        }

        /// <summary>
        /// Walk <paramref name="condition"/>, find names that <see cref="NarrowingEngine"/>
        /// produces a non-identity result for, and return name → narrowed-type.
        /// Empty dict when nothing narrows. Branch direction selects True vs. False result.
        /// </summary>
        private Dictionary<string, TypeRef> CollectNarrowings(Expr condition, bool forTrueBranch)
        {
            Dictionary<string, TypeRef> result = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            if (condition == null) return result;
            HashSet<string> candidates = new HashSet<string>(StringComparer.Ordinal);
            CollectNamesInExpr(condition, candidates);
            foreach (string name in candidates)
            {
                TypeRef current = _env.Resolve(name);
                if (current == null) continue;
                // Do not manufacture a concrete branch type from an opaque alias/TypeVar. For
                // example, pydantic's ColorType alias is not expanded yet; narrowing it with
                // isinstance(value, (tuple, list)) used to replace the opaque alias with
                // `tuple | list`, after which the list member was falsely rejected by a helper
                // accepting tuple. Keeping the unresolved source gradual is the only sound choice
                // until alias expansion is available.
                if (!SourceTypeIsJudgeable(current)) continue;
                NarrowResult nr = NarrowingEngine.NarrowAtTest(current, condition, name);
                TypeRef narrowed = forTrueBranch ? nr.TrueBranch : nr.FalseBranch;
                if (narrowed != null && !ReferenceEquals(narrowed, current))
                {
                    result[name] = narrowed;
                }
            }

            // Attribute-path narrowing (iter 370): `if self.metavar is not None: return self.metavar`
            // — the attribute is Optional but is narrowed to non-None inside the guard. Keyed by the
            // dotted path ("self.metavar"), which never collides with a real variable name (names have
            // no dots). InferAttribute consults these overrides before resolving the member.
            var attrExprs = new Dictionary<string, AttributeExpr>(StringComparer.Ordinal);
            CollectAttributePathsInExpr(condition, attrExprs);
            foreach (var kv in attrExprs)
            {
                string path = kv.Key;
                // Infer the attribute's current type WITHOUT emitting a missing-attribute diagnostic
                // (the `if` test already inferred it once; a second emit would double-report).
                bool prev = _suppressAttrNotFound;
                _suppressAttrNotFound = true;
                TypeRef current;
                try { current = InferAttribute(kv.Value); }
                finally { _suppressAttrNotFound = prev; }
                if (current == null || current is UnknownRef || current is AnyRef) continue;
                if (!SourceTypeIsJudgeable(current)) continue;

                NarrowResult nr = NarrowingEngine.NarrowAtTest(current, condition, path);
                TypeRef narrowed = forTrueBranch ? nr.TrueBranch : nr.FalseBranch;
                if (narrowed != null && !ReferenceEquals(narrowed, current))
                {
                    result[path] = narrowed;
                }
            }

            // PEP 647/742 type predicates: `if pred(x):` where pred returns TypeGuard[T] / TypeIs[T]
            // narrows x to T on the true branch; TypeIs additionally narrows x to (type(x) - T) on the
            // false branch. Standard isinstance/None narrowing can't express this. click types.py:983:
            // after `if _is_file_like(value): return`, value is `str | os.PathLike[str]`, not the full
            // `... | t.IO[Any]`, so the following `resolve_lazy_flag(value)` type-checks.
            ApplyTypePredicateNarrowing(condition, forTrueBranch, result);
            return result;
        }

        private void ApplyTypePredicateNarrowing(Expr condition, bool forTrueBranch, Dictionary<string, TypeRef> result)
        {
            while (condition is UnaryOpExpr u && u.Op == UnaryOp.Not) { condition = u.Operand; forTrueBranch = !forTrueBranch; }
            Call call = condition as Call;
            if (call == null || call.Args == null || call.Args.Count < 1) return;

            TypeRef calleeType;
            bool prev = _suppressAttrNotFound;
            _suppressAttrNotFound = true;
            try { calleeType = InferExpr(call.Func); }
            finally { _suppressAttrNotFound = prev; }
            CallableRef cr = calleeType as CallableRef;
            if (cr == null) return;
            TypeRef guarded; bool isTypeIs;
            if (!IsTypePredicateReturn(cr.ReturnType, out guarded, out isTypeIs)) return;

            Expr subjectExpr = call.Args[0];
            string subject = NarrowingEngine.DottedPath(subjectExpr);
            if (subject == null) return;

            TypeRef current = _env.Resolve(subject);
            if (current == null && subjectExpr is AttributeExpr ae)
            {
                bool p2 = _suppressAttrNotFound;
                _suppressAttrNotFound = true;
                try { current = InferAttribute(ae); }
                finally { _suppressAttrNotFound = p2; }
            }
            if (current == null) return;

            TypeRef narrowed;
            if (forTrueBranch) narrowed = guarded;                 // both TypeGuard and TypeIs narrow to T here
            else if (isTypeIs) narrowed = SubtractType(current, guarded);
            else return;                                           // TypeGuard: no negative narrowing

            if (narrowed != null && !ReferenceEquals(narrowed, current)) result[subject] = narrowed;
        }

        // A function annotated `-> TypeGuard[T]` / `-> TypeIs[T]` returns a plain bool at runtime (the
        // annotation only adds narrowing at call sites), so any bool return satisfies it. Without this,
        // `return isinstance(...) and ...` under a `-> TypeGuard[ast.Attribute]` annotation false-flagged
        // PY6005 (black handle_ipynb_magics.py:349).
        private bool ReturnSatisfiesTypePredicate(TypeRef returned, TypeRef expected)
        {
            TypeRef guarded; bool isTypeIs;
            if (!IsTypePredicateReturn(expected, out guarded, out isTypeIs)) return false;
            if (returned is ClassRef cr) return cr.QualifiedName == "bool" || cr.QualifiedName == "builtins.bool";
            // A gradual (isinstance chain inferred as Unknown/Any) return is also fine.
            return returned is AnyRef || returned is UnknownRef;
        }

        // Recognise a TypeGuard[T] / TypeIs[T] return annotation (bare or dotted spelling), yielding
        // the guarded type T and whether it is a TypeIs (which also narrows the negative branch).
        private static bool IsTypePredicateReturn(TypeRef ret, out TypeRef guarded, out bool isTypeIs)
        {
            guarded = null; isTypeIs = false;
            GenericRef gr = ret as GenericRef;
            if (gr == null || gr.Base == null || gr.Args == null || gr.Args.Count < 1) return false;
            string last = LastNameSegment(gr.Base.QualifiedName);
            if (last == "TypeIs") { isTypeIs = true; guarded = gr.Args[0]; return true; }
            if (last == "TypeGuard") { guarded = gr.Args[0]; return true; }
            return false;
        }

        // Structural set difference for a union: drop the members subsumed by <paramref name="remove"/>.
        private TypeRef SubtractType(TypeRef current, TypeRef remove)
        {
            if (current is UnionRef u)
            {
                var kept = new List<TypeRef>();
                foreach (TypeRef m in u.Members)
                    if (!SubtypeChecker.IsAssignable(m, remove, _stubs)) kept.Add(m);
                if (kept.Count == u.Members.Count) return current;   // nothing removed
                if (kept.Count == 0) return NeverRef.Instance;
                if (kept.Count == 1) return kept[0];
                return new UnionRef(kept);
            }
            if (SubtypeChecker.IsAssignable(current, remove, _stubs)) return NeverRef.Instance;
            return current;
        }

        // Collect the dotted paths of attribute chains that appear as a narrowing SUBJECT in the
        // condition — the left of an `is`/`is not None`, an isinstance() first arg, or a bare truthy
        // test — mapped to one representative AttributeExpr so its type can be inferred.
        private static void CollectAttributePathsInExpr(Expr e, Dictionary<string, AttributeExpr> acc)
        {
            switch (e)
            {
                case UnaryOpExpr u:
                    CollectAttributePathsInExpr(u.Operand, acc);
                    break;
                case BoolOpExpr b:
                    foreach (Expr v in b.Values) CollectAttributePathsInExpr(v, acc);
                    break;
                case Compare cmp:
                    AddAttrPath(cmp.Left, acc);
                    break;
                case Call call when IsIsInstanceCall(call) && call.Args.Count >= 1:
                    AddAttrPath(call.Args[0], acc);
                    break;
                case AttributeExpr ae:                 // `if self.x:`
                    AddAttrPath(ae, acc);
                    break;
            }
        }

        private static void AddAttrPath(Expr operand, Dictionary<string, AttributeExpr> acc)
        {
            if (operand is AttributeExpr ae)
            {
                string path = NarrowingEngine.DottedPath(ae);
                if (path != null && !acc.ContainsKey(path)) acc[path] = ae;
            }
        }

        private static bool IsIsInstanceCall(Call call)
        {
            if (call == null) return false;
            if (call.Func is Name n && n.Id == "isinstance") return true;
            if (call.Func is AttributeExpr a && a.Attr == "isinstance") return true;
            return false;
        }

        private static void CollectNamesInExpr(Expr e, HashSet<string> acc)
        {
            if (e == null) return;
            switch (e)
            {
                case Name n: acc.Add(n.Id); break;
                case Call c:
                    foreach (Expr a in c.Args) CollectNamesInExpr(a, acc);
                    break;
                case Compare cmp:
                    CollectNamesInExpr(cmp.Left, acc);
                    foreach (Expr rhs in cmp.Comparators) CollectNamesInExpr(rhs, acc);
                    break;
                case UnaryOpExpr u: CollectNamesInExpr(u.Operand, acc); break;
                case BoolOpExpr b:
                    foreach (Expr v in b.Values) CollectNamesInExpr(v, acc);
                    break;
                case BinOpExpr bi:
                    CollectNamesInExpr(bi.Left, acc);
                    CollectNamesInExpr(bi.Right, acc);
                    break;
                case AttributeExpr _: break;  // attribute chains don't narrow root names in MVP
            }
        }

        private void Emit(string code, string message, SourceSpan span)
        {
            _diagnostics.Add(new CompileDiagnostic(_filePath, code, ParseSeverity.Error, span, message));
        }

        /// <summary>
        /// True when a value/return type mismatch against a declared annotation must NOT be reported, to keep
        /// the compiler in step with the actual interpreter (NFR-02 / "match CPython 100%"). CPython never
        /// enforces annotations, and the single most common under-annotation is a function/variable declared
        /// <c>-&gt; T</c> / <c>: T</c> that yields <c>None</c> (the annotation should have been
        /// <c>Optional[T]</c>). Flagging that produces false positives on real code (e.g. sample workspace
        /// <c>xml_utils.py</c>'s <c>if path is None: return None</c> under a <c>-&gt; str</c>). So a bare
        /// <c>None</c> value against a non-<c>None</c> annotation is exempt; genuine wrong-<em>concrete</em>-type
        /// mismatches (<c>return "x"</c> from <c>-&gt; int</c>) are still reported. Deeper Optional-strictness
        /// is the analyzer's job (FR-ANA), not the interpreter-parity compile pass.
        /// </summary>
        private static bool AnnotationParityExempt(TypeRef value, TypeRef annotation)
        {
            return value is NoneRef && !(annotation is NoneRef);
        }
    }
}
