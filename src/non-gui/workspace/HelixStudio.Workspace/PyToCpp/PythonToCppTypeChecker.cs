using System;
using System.Collections.Generic;
using System.Linq;
using HelixPythonNamespace.Python.Syntax;

namespace HelixPythonNamespace.Workspace.PyToCpp
{
    public sealed class TypedPythonResult
    {
        public bool Success;
        public string TypedSourceText;
        public List<TypeCheckDiagnostic> Diagnostics = new List<TypeCheckDiagnostic>();
        public string EntryFunctionName;
    }

    /// <summary>
    /// Pass 1 of the Python -> C++ translator: a STRICT type checker, not a gradual/best-effort
    /// inferencer. Every value must resolve to exactly one concrete, in-scope type; anything
    /// that can't is rejected with a diagnostic (code + line), never silently guessed at. On
    /// success, the (already fully consistent) AST is re-emitted as literal Python source via
    /// <see cref="PythonPrettyPrinter"/> — Pass 2 re-parses that text from disk.
    ///
    /// ============================== Supported subset ==============================
    /// Statements: def, class (single inheritance), if/elif/else, while (no else-clause),
    ///   for (no else-clause), try/except/else/finally, raise, assert, del, global, nonlocal
    ///   (module-scope only — see NestedFunctionCapture below), pass/break/continue, return,
    ///   simple/annotated/tuple(N-target, no starred-target) assignment, augmented assignment.
    /// Expressions: name, literal, binary/unary/comparison ops, conditional (ternary), lambda
    ///   (only the "assign a bare name, then call it" pattern — see below), call, subscript,
    ///   slice (list/str only), attribute (direct method calls only, not method references),
    ///   starred (call arguments only), tuple/list/set/dict literals (homogeneous elements),
    ///   comprehensions (list/set/dict; generator expressions are treated as eager/List),
    ///   f-strings (literal, non-nested format specs only), parenthesized.
    /// Types: int (fixed 64-bit, overflow throws at runtime — see pyrt.hpp), float, bool, str
    ///   (Unicode-correct), None, List[T]/Dict[K,V]/Set[T]/Tuple[T...], Optional[T] (`is None`
    ///   / `is not None` checks only — general `is`/`is not` is rejected), user classes.
    /// Decorators: @staticmethod, @classmethod, @property only.
    /// Builtins: print, len, str, int, float, bool, range, sorted, abs, min, max, list, dict,
    ///   set, tuple (as constructors). No other imports/stdlib calls — no typeshed-style stub
    ///   resolution in v1, so an arbitrary library call has no way to get a trustworthy type.
    ///
    /// ============================== Explicitly rejected ============================
    /// eval/exec, multiple inheritance, metaclasses, generators (yield in a function body —
    /// NOT the same as a generator expression, which IS supported as eager), async/await,
    /// match/case, with-statements, mutable default arguments, dynamic getattr/setattr/
    /// hasattr (non-literal attribute name), for/while `else:` clauses, starred assignment
    /// targets (`a, *b = ...`), nested functions that capture an enclosing local via
    /// `nonlocal` (closures crossing a function-return boundary need heap-boxed captured
    /// state in the C++ backend — a real, addressable feature, just not in v1), method
    /// references as first-class values (only direct `obj.method(...)` calls), non-literal
    /// tuple subscript index, tuple slicing, `is`/`is not` on anything but a None check.
    /// </summary>
    public sealed class PythonToCppTypeChecker
    {
        private readonly Dictionary<string, ClassInfo> _classes = new Dictionary<string, ClassInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, FunctionInfo> _functions = new Dictionary<string, FunctionInfo>(StringComparer.Ordinal);
        // Lambdas bound to a bare module/function-local name, not yet type-checked (deferred
        // until first call site reveals argument types — see class comment).
        private readonly Dictionary<string, LambdaExpression> _pendingLambdas = new Dictionary<string, LambdaExpression>(StringComparer.Ordinal);
        private readonly List<TypeCheckDiagnostic> _diagnostics = new List<TypeCheckDiagnostic>();
        private string _filePath;
        private bool _hasMain;

        private static readonly HashSet<string> WhitelistedBuiltins = new HashSet<string>(StringComparer.Ordinal)
        { "print", "len", "str", "int", "float", "bool", "range", "sorted", "abs", "min", "max", "list", "dict", "set", "tuple" };

        private static readonly HashSet<string> BuiltinExceptionNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Exception", "BaseException", "ValueError", "TypeError", "KeyError", "IndexError",
            "ZeroDivisionError", "OverflowError", "AttributeError", "RuntimeError",
            "NotImplementedError", "AssertionError", "StopIteration", "NameError"
        };

        public TypedPythonResult Check(ModuleSyntax module, string filePath)
        {
            _filePath = filePath ?? string.Empty;
            var result = new TypedPythonResult();

            SeedModule(module);
            var moduleScope = new Scope(null);
            CheckModule(module, moduleScope);

            if (!_hasMain) Reject(PyCppDiagnosticCodes.NoEntryPoint, module, "no module-level `def main()` found — the program has no C++ entry point");

            result.Diagnostics = _diagnostics;
            result.Success = !_diagnostics.Any();
            if (result.Success)
            {
                result.TypedSourceText = new PythonPrettyPrinter().Print(module);
                result.EntryFunctionName = "main";
            }
            return result;
        }

        private void Reject(string code, SyntaxNode node, string message)
        {
            int line = node?.Span.Start.Line ?? 0;
            int col = node?.Span.Start.Column ?? 0;
            _diagnostics.Add(new TypeCheckDiagnostic(code, _filePath, line, col, message));
        }

        // ================================================================
        // Seed pass — register class/function signatures before checking bodies,
        // mirroring CPython's own two-pass semantic walk (forward references work).
        // ================================================================

        private void SeedModule(ModuleSyntax module)
        {
            foreach (StatementSyntax s in module.Statements)
            {
                switch (s)
                {
                    case FunctionDefSyntax fd:
                        if (_functions.ContainsKey(fd.Name) || _classes.ContainsKey(fd.Name))
                            Reject(PyCppDiagnosticCodes.DuplicateDefinition, fd, "duplicate top-level definition: " + fd.Name);
                        else
                        {
                            FunctionInfo fi = BuildFunctionInfo(fd, null);
                            if (fi != null) _functions[fd.Name] = fi;
                        }
                        break;
                    case ClassDefSyntax cd:
                        if (_functions.ContainsKey(cd.Name) || _classes.ContainsKey(cd.Name))
                            Reject(PyCppDiagnosticCodes.DuplicateDefinition, cd, "duplicate top-level definition: " + cd.Name);
                        else SeedClass(cd);
                        break;
                }
            }
        }

        private void SeedClass(ClassDefSyntax cd)
        {
            if (cd.BaseExpressions.Count > 1)
            {
                Reject(PyCppDiagnosticCodes.MultipleInheritance, cd, "multiple inheritance is not supported: " + cd.Name);
                return;
            }
            if (cd.KeywordArguments.Any(kv => kv.Key == "metaclass"))
            {
                Reject(PyCppDiagnosticCodes.Metaclass, cd, "metaclasses are not supported: " + cd.Name);
                return;
            }

            string baseName = null;
            if (cd.BaseExpressions.Count == 1)
            {
                if (cd.BaseExpressions[0] is NameExpression bn) baseName = bn.Name;
                else { Reject(PyCppDiagnosticCodes.UnresolvableType, cd, "base class must be a plain name: " + cd.Name); return; }
            }

            var info = new ClassInfo { Name = cd.Name, BaseClassName = baseName };
            info.IsExceptionType = baseName != null && (BuiltinExceptionNames.Contains(baseName) ||
                (_classes.TryGetValue(baseName, out ClassInfo baseInfo) && baseInfo.IsExceptionType));
            _classes[cd.Name] = info;

            // __init__ establishes instance fields via `self.x: T = ...` / `self.x = <inferable>`.
            FunctionDefSyntax initFd = cd.Body?.Statements.OfType<FunctionDefSyntax>().FirstOrDefault(f => f.Name == "__init__");
            if (initFd != null) SeedFieldsFromInit(info, initFd);

            foreach (StatementSyntax member in cd.Body?.Statements ?? Array.Empty<StatementSyntax>())
            {
                if (!(member is FunctionDefSyntax fd)) continue;
                FunctionInfo fi = BuildFunctionInfo(fd, cd.Name);
                if (fi != null) info.Methods[fd.Name] = fi;
            }
        }

        private void SeedFieldsFromInit(ClassInfo info, FunctionDefSyntax initFd)
        {
            if (initFd.Body == null) return;
            foreach (StatementSyntax s in initFd.Body.Statements)
            {
                if (!(s is AssignmentStatement asn) || asn.Targets.Count != 1) continue;
                if (!(asn.Targets[0] is AttributeExpression attr) || !(attr.Target is NameExpression selfRef) || selfRef.Name != "self")
                    continue;

                CppType fieldType = null;
                if (asn.Annotation != null) fieldType = ResolveAnnotation(asn.Annotation, asn);
                else fieldType = TryInferLiteralOrObviousType(asn.Value);

                if (fieldType != null) info.Fields[attr.Name] = new FieldInfo { Name = attr.Name, Type = fieldType };
                // Fields whose type can't be determined here are simply not registered; a
                // later read of self.<field> will fail with UnknownAttribute, which is the
                // right diagnostic (the field's type genuinely can't be established).
            }
        }

        // A conservative, syntax-only type guess usable during the seed pass (before full
        // scope-aware inference is available) — literals and simple constructor/comprehension
        // shapes only. Falls back to null (undetermined) for anything more complex; the
        // relevant call site turns that into a proper diagnostic.
        private CppType TryInferLiteralOrObviousType(ExpressionSyntax e)
        {
            switch (e)
            {
                case LiteralExpression lit: return TypeOfLiteral(lit);
                case ListExpression le when le.Elements.Count > 0:
                {
                    CppType elem = TryInferLiteralOrObviousType(le.Elements[0]);
                    return elem != null ? new ListType(elem) : null;
                }
                default: return null;
            }
        }

        private FunctionInfo BuildFunctionInfo(FunctionDefSyntax fd, string ownerClass)
        {
            if (fd.IsAsync)
            {
                // Calling an async function synchronously in real Python returns a coroutine
                // object, not the computed value — silently treating it as an ordinary
                // function (even one that never actually uses `await`) would be exactly the
                // kind of silent semantic drift this checker exists to prevent. Reject outright
                // rather than only catching it indirectly via individual `await` expressions.
                Reject(PyCppDiagnosticCodes.UnsupportedStatement, fd, "async def is not supported (async/await is not in this subset): " + fd.Name);
                return null;
            }

            bool isStatic = fd.Decorators.Any(d => d.Expression is NameExpression n && n.Name == "staticmethod");
            bool isClassMethod = fd.Decorators.Any(d => d.Expression is NameExpression n && n.Name == "classmethod");
            bool isProperty = fd.Decorators.Any(d => d.Expression is NameExpression n && n.Name == "property");
            foreach (DecoratorSyntax d in fd.Decorators)
            {
                if (d.Expression is NameExpression dn && (dn.Name == "staticmethod" || dn.Name == "classmethod" || dn.Name == "property")) continue;
                Reject(PyCppDiagnosticCodes.UnsupportedDecorator, d, "unsupported decorator on " + fd.Name + " (only @staticmethod/@classmethod/@property)");
                return null;
            }

            var parameters = new List<ParamInfo>();
            bool first = true;
            foreach (ParameterSyntax p in fd.Parameters)
            {
                // Skip the implicit `self`/`cls` receiver.
                if (first && ownerClass != null && !isStatic) { first = false; continue; }
                first = false;

                if (p.Kind == ParameterKind.PositionalOnly || p.Kind == ParameterKind.KeywordOnly || p.Kind == ParameterKind.Positional
                    || p.Kind == ParameterKind.VarPositional || p.Kind == ParameterKind.VarKeyword)
                {
                    if (p.DefaultValue != null && IsMutableLiteral(p.DefaultValue))
                    {
                        Reject(PyCppDiagnosticCodes.MutableDefaultArgument, p,
                            "mutable default argument on " + fd.Name + "(" + p.Name + "=...) is not supported (Python's shared-default-object gotcha is not replicated in this subset)");
                        return null;
                    }
                    CppType pType = p.Annotation != null ? ResolveAnnotation(p.Annotation, p) : null;
                    if (pType == null && p.Annotation == null)
                    {
                        Reject(PyCppDiagnosticCodes.UnresolvableType, p, "parameter '" + p.Name + "' of " + fd.Name + " needs a type annotation");
                        return null;
                    }
                    if (pType == null) return null; // annotation present but unresolvable — already reported
                    parameters.Add(new ParamInfo
                    {
                        Name = p.Name,
                        Type = pType,
                        HasDefault = p.DefaultValue != null,
                        IsVarPositional = p.Kind == ParameterKind.VarPositional,
                        IsVarKeyword = p.Kind == ParameterKind.VarKeyword,
                    });
                }
            }

            CppType returnType;
            if (fd.ReturnAnnotation != null)
            {
                returnType = ResolveAnnotation(fd.ReturnAnnotation, fd);
                if (returnType == null) return null;
            }
            else if (ownerClass != null && fd.Name == "__init__")
            {
                returnType = CppType.None;
            }
            else
            {
                Reject(PyCppDiagnosticCodes.UnresolvableType, fd, "function '" + fd.Name + "' needs a return type annotation (-> T)");
                return null;
            }

            if (BodyContainsYield(fd.Body))
            {
                Reject(PyCppDiagnosticCodes.UnsupportedStatement, fd, "generators (yield) are not supported: " + fd.Name);
                return null;
            }

            return new FunctionInfo
            {
                Name = fd.Name,
                Parameters = parameters,
                ReturnType = returnType,
                IsStatic = isStatic,
                IsClassMethod = isClassMethod,
                IsProperty = isProperty,
                OwnerClass = ownerClass,
                Syntax = new FunctionDefSyntaxRef(fd),
            };
        }

        private static bool IsMutableLiteral(ExpressionSyntax e) =>
            e is ListExpression || e is DictExpression || e is SetExpression ||
            (e is CallExpression ce && ce.Callee is NameExpression cn && (cn.Name == "list" || cn.Name == "dict" || cn.Name == "set"));

        private static bool BodyContainsYield(SuiteSyntax body)
        {
            if (body == null) return false;
            foreach (StatementSyntax s in body.Statements)
            {
                if (s is YieldStatement) return true;
                if (s is FunctionDefSyntax) continue; // a nested def's own yield doesn't make THIS function a generator
                foreach (SyntaxNode d in Descendants(s))
                {
                    if (d is FunctionDefSyntax) continue;
                    if (d is YieldExpression || d is YieldStatement) return true;
                }
            }
            return false;
        }

        private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
        {
            if (node == null) yield break;
            foreach (SyntaxNode child in node.Children)
            {
                yield return child;
                foreach (SyntaxNode d in Descendants(child)) yield return d;
            }
        }

        // Resolves a type annotation expression (`int`, `List[int]`, `Dict[str, Foo]`,
        // `Optional[int]`, a user class name) to a CppType, or reports a diagnostic and
        // returns null.
        private CppType ResolveAnnotation(ExpressionSyntax ann, SyntaxNode context)
        {
            switch (ann)
            {
                // `None` in annotation position (`-> None`) parses as a LiteralExpression
                // (TokenKind.KwNone), not a NameExpression — Python's `None` is a keyword,
                // not an identifier, so the parser tokenizes/represents it the same way in
                // every expression position, annotations included.
                case LiteralExpression lit when lit.Token?.Kind == TokenKind.KwNone:
                    return CppType.None;

                case NameExpression n:
                    switch (n.Name)
                    {
                        case "int": return CppType.Int;
                        case "float": return CppType.Float;
                        case "bool": return CppType.Bool;
                        case "str": return CppType.Str;
                    }
                    if (_classes.ContainsKey(n.Name)) return new ClassType(n.Name);
                    Reject(PyCppDiagnosticCodes.UnresolvableType, context, "unresolvable type annotation: " + n.Name);
                    return null;

                case SubscriptExpression sub when sub.Target is NameExpression baseName:
                {
                    switch (baseName.Name)
                    {
                        case "List":
                        {
                            CppType elem = ResolveAnnotation(sub.Index, context);
                            return elem != null ? new ListType(elem) : null;
                        }
                        case "Set":
                        {
                            CppType elem = ResolveAnnotation(sub.Index, context);
                            return elem != null ? new SetType(elem) : null;
                        }
                        case "Optional":
                        {
                            CppType inner = ResolveAnnotation(sub.Index, context);
                            return inner != null ? new OptionalType(inner) : null;
                        }
                        case "Dict":
                        {
                            if (sub.Index is TupleExpression te && te.Elements.Count == 2)
                            {
                                CppType k = ResolveAnnotation(te.Elements[0], context);
                                CppType v = ResolveAnnotation(te.Elements[1], context);
                                return (k != null && v != null) ? new DictType(k, v) : null;
                            }
                            Reject(PyCppDiagnosticCodes.UnresolvableType, context, "Dict[...] annotation needs exactly Key, Value");
                            return null;
                        }
                        case "Tuple":
                        {
                            List<CppType> elems = new List<CppType>();
                            IEnumerable<ExpressionSyntax> parts = sub.Index is TupleExpression tt ? tt.Elements : new[] { sub.Index };
                            foreach (ExpressionSyntax p in parts)
                            {
                                CppType t = ResolveAnnotation(p, context);
                                if (t == null) return null;
                                elems.Add(t);
                            }
                            return new TupleType(elems);
                        }
                        default:
                            Reject(PyCppDiagnosticCodes.UnresolvableType, context, "unresolvable generic annotation: " + baseName.Name);
                            return null;
                    }
                }
                default:
                    Reject(PyCppDiagnosticCodes.UnresolvableType, context, "unresolvable type annotation");
                    return null;
            }
        }

        // ================================================================
        // Check pass
        // ================================================================

        // Module-level code becomes C++ global-scope declarations (functions/classes) or
        // global variable initializers (assignments) — there is no C++ equivalent of "a
        // statement that just runs when the module loads" outside of that, so anything else
        // at module level is out of scope. The standard `if __name__ == "__main__": main()`
        // entry guard (and an equivalent bare `main()` call with no args) is recognized and
        // skipped entirely — it becomes a no-op once def main() is emitted as the literal
        // C++ process entry point (see PythonToCppEmitter).
        private void CheckModule(ModuleSyntax module, Scope moduleScope)
        {
            foreach (StatementSyntax s in module.Statements)
            {
                if (s is FunctionDefSyntax fd)
                {
                    if (fd.Name == "main") _hasMain = true;
                    if (_functions.TryGetValue(fd.Name, out FunctionInfo fi)) CheckFunctionBody(fi, moduleScope);
                }
                else if (s is ClassDefSyntax cd)
                {
                    if (_classes.TryGetValue(cd.Name, out ClassInfo ci))
                        foreach (FunctionInfo m in ci.Methods.Values) CheckFunctionBody(m, moduleScope);
                }
                else if (s is ImportStatement || s is FromImportStatement)
                {
                    // recorded but not resolved — see class comment on stdlib scope.
                }
                else if (IsMainGuard(s) || IsBareMainCall(s))
                {
                    // no-op — see method comment.
                }
                else if (s is AssignmentStatement asn)
                {
                    CheckAssignment(asn, moduleScope);
                }
                else
                {
                    Reject(PyCppDiagnosticCodes.UnsupportedStatement, s,
                        "module-level code must be limited to def/class, simple variable initialization, imports, and the `if __name__ == \"__main__\":` entry guard");
                }
            }

            // Any lambda bound at module scope but never called can't have its parameter
            // types determined.
            foreach (var kv in _pendingLambdas)
                Reject(PyCppDiagnosticCodes.UnresolvableType, kv.Value, "lambda assigned to '" + kv.Key + "' is never called, so its parameter types can't be determined");
        }

        private static bool IsBareMainCall(StatementSyntax s) =>
            s is ExpressionStatement es && es.Expression is CallExpression ce &&
            ce.Callee is NameExpression cn && cn.Name == "main" && ce.Arguments.Count == 0;

        // Recognizes exactly `if __name__ == "__main__": ...` (no elif/else); the body isn't
        // inspected further — it's discarded along with the guard itself, same as the retired
        // C pipeline's RuleBasedPythonToCTranslator.IsMainGuard.
        private static bool IsMainGuard(StatementSyntax s)
        {
            if (!(s is IfStatement ifs)) return false;
            if (ifs.Elifs != null && ifs.Elifs.Count > 0) return false;
            if (ifs.Else != null) return false;
            if (!(ifs.Test is ComparisonExpression cmp)) return false;
            if (cmp.Comparators == null || cmp.Comparators.Count != 1) return false;
            if (cmp.Operators == null || cmp.Operators.Count != 1 || cmp.Operators[0] != ComparisonOperator.Eq) return false;
            if (!(cmp.Left is NameExpression left) || left.Name != "__name__") return false;
            if (!(cmp.Comparators[0] is LiteralExpression lit) || lit.Token?.Kind != TokenKind.String) return false;
            return StripPythonQuotes(lit.Token.Text) == "__main__";
        }

        private static string StripPythonQuotes(string pyLit)
        {
            if (string.IsNullOrEmpty(pyLit)) return "";
            if (pyLit.Length >= 6 && (pyLit.StartsWith("\"\"\"", StringComparison.Ordinal) || pyLit.StartsWith("'''", StringComparison.Ordinal)))
                return pyLit.Substring(3, pyLit.Length - 6);
            if (pyLit.Length >= 2 && (pyLit[0] == '"' || pyLit[0] == '\'')) return pyLit.Substring(1, pyLit.Length - 2);
            return pyLit;
        }

        // Tracks which class's method body is currently being checked, so a `super()` call
        // inside it can resolve against that class's base — a simple instance field rather
        // than threading an extra parameter through every InferExpr/InferCall call site.
        // Methods aren't checked re-entrantly (CheckFunctionBody isn't called while another
        // CheckFunctionBody call is on the stack for the same checker instance beyond simple
        // save/restore nesting), so a single field is sufficient here.
        private string _currentClassContext;

        private void CheckFunctionBody(FunctionInfo fn, Scope enclosing)
        {
            var scope = new Scope(enclosing);
            foreach (ParamInfo p in fn.Parameters)
                scope.Declare(p.Name, p.IsVarPositional ? new ListType(p.Type) : p.IsVarKeyword ? new DictType(CppType.Str, p.Type) : p.Type);
            if (fn.OwnerClass != null && !fn.IsStatic) scope.Declare("self", new ClassType(fn.OwnerClass));

            string previousClassContext = _currentClassContext;
            _currentClassContext = fn.OwnerClass;
            try
            {
                var returnTypes = new List<CppType>();
                if (fn.Syntax.Node.Body != null)
                    foreach (StatementSyntax s in fn.Syntax.Node.Body.Statements)
                        CheckStatement(s, scope, fn, returnTypes);

                foreach (CppType rt in returnTypes)
                {
                    if (!TypesCompatible(rt, fn.ReturnType))
                        Reject(PyCppDiagnosticCodes.InconsistentReturnTypes, fn.Syntax.Node,
                            "function '" + fn.Name + "' returns " + rt + " but is annotated -> " + fn.ReturnType);
                }
            }
            finally { _currentClassContext = previousClassContext; }
        }

        private void CheckStatement(StatementSyntax s, Scope scope, FunctionInfo currentFunction, List<CppType> returnTypes = null)
        {
            switch (s)
            {
                case FunctionDefSyntax nestedFd:
                    CheckNestedFunction(nestedFd, scope);
                    return;

                case AssignmentStatement asn: CheckAssignment(asn, scope); return;

                case AugmentedAssignmentStatement aug:
                {
                    CppType targetType = InferExpr(aug.Target, scope);
                    CppType valueType = InferExpr(aug.Value, scope);
                    if (targetType != null && valueType != null) CheckBinaryOperandTypes(aug.Operator, targetType, valueType, aug);
                    return;
                }

                case ExpressionStatement es: InferExpr(es.Expression, scope); return;
                case PassStatement _: case BreakStatement _: case ContinueStatement _: return;

                case ReturnStatement rs:
                {
                    CppType t = rs.Value != null
                        ? InferExprAllowingNone(rs.Value, scope, currentFunction?.ReturnType)
                        : CppType.None;
                    if (t != null) returnTypes?.Add(t);
                    return;
                }

                case RaiseStatement raise:
                    if (raise.Exception != null) InferExpr(raise.Exception, scope);
                    return;

                case AssertStatement assertS:
                    InferExpr(assertS.Test, scope);
                    if (assertS.Message != null) InferExpr(assertS.Message, scope);
                    return;

                case DelStatement del:
                    foreach (ExpressionSyntax t in del.Targets) InferExpr(t, scope);
                    return;

                case GlobalStatement _: return; // module-level globals map directly to C++ globals; no lifetime issue.

                case NonlocalStatement nl:
                    Reject(PyCppDiagnosticCodes.NestedFunctionCapture, nl,
                        "nonlocal capture is not supported in this subset (closures crossing a function-return boundary need heap-boxed state)");
                    return;

                case YieldStatement ys:
                    Reject(PyCppDiagnosticCodes.UnsupportedStatement, ys, "generators (yield) are not supported");
                    return;

                case IfStatement ifs:
                    InferExpr(ifs.Test, scope);
                    CheckSuite(ifs.Body, scope, currentFunction, returnTypes);
                    foreach (IfStatement elif in ifs.Elifs)
                    {
                        InferExpr(elif.Test, scope);
                        CheckSuite(elif.Body, scope, currentFunction, returnTypes);
                    }
                    if (ifs.Else != null) CheckSuite(ifs.Else, scope, currentFunction, returnTypes);
                    return;

                case WhileStatement ws:
                    if (ws.Else != null) { Reject(PyCppDiagnosticCodes.UnsupportedStatement, ws, "while/else is not supported"); return; }
                    InferExpr(ws.Test, scope);
                    CheckSuite(ws.Body, scope, currentFunction, returnTypes);
                    return;

                case ForStatement fs: CheckFor(fs, scope, currentFunction, returnTypes); return;
                case TryStatement trys: CheckTry(trys, scope, currentFunction, returnTypes); return;

                case WithStatement withS:
                    Reject(PyCppDiagnosticCodes.UnsupportedStatement, withS, "with-statements are not supported in this subset");
                    return;

                case ImportStatement _: case FromImportStatement _:
                    // Recorded but not resolved — no rich stdlib typing in v1. Not itself an
                    // error; a subsequent USE of an imported name that isn't in the builtin
                    // whitelist is what gets rejected (UnknownName / UnsupportedImport).
                    return;

                case MatchStatement matchS:
                    Reject(PyCppDiagnosticCodes.UnsupportedStatement, matchS, "match/case is not supported in this subset");
                    return;

                case UnparsedStatement up:
                    Reject(PyCppDiagnosticCodes.UnsupportedStatement, up, "syntax the parser could not fully analyze is not supported");
                    return;

                default:
                    Reject(PyCppDiagnosticCodes.UnsupportedStatement, s, "unsupported statement: " + s.GetType().Name);
                    return;
            }
        }

        private void CheckSuite(SuiteSyntax suite, Scope scope, FunctionInfo currentFunction, List<CppType> returnTypes)
        {
            if (suite == null) return;
            foreach (StatementSyntax s in suite.Statements) CheckStatement(s, scope, currentFunction, returnTypes);
        }

        private void CheckNestedFunction(FunctionDefSyntax fd, Scope scope)
        {
            bool usesNonlocal = Descendants(fd.Body).Any(d => d is NonlocalStatement);
            if (usesNonlocal)
            {
                Reject(PyCppDiagnosticCodes.NestedFunctionCapture, fd,
                    "nested function '" + fd.Name + "' uses nonlocal — closures crossing a function-return boundary are not supported in this subset");
                return;
            }
            FunctionInfo fi = BuildFunctionInfo(fd, null);
            if (fi == null) return;
            _functions[fd.Name] = fi; // treat as a free function of that name for the rest of the file
            CheckFunctionBody(fi, scope);
        }

        private void CheckFor(ForStatement fs, Scope scope, FunctionInfo currentFunction, List<CppType> returnTypes)
        {
            if (fs.IsAsync) { Reject(PyCppDiagnosticCodes.UnsupportedStatement, fs, "async for is not supported (async/await is not in this subset)"); return; }
            if (fs.Else != null) { Reject(PyCppDiagnosticCodes.UnsupportedStatement, fs, "for/else is not supported"); return; }

            CppType elementType = InferIterableElementType(fs.Iter, scope);
            if (elementType == null) return;

            var bodyScope = new Scope(scope);
            if (fs.Target is NameExpression tn) bodyScope.Declare(tn.Name, elementType);
            else if (fs.Target is TupleExpression tt && elementType is TupleType tupType && tupType.Elements.Count == tt.Elements.Count)
            {
                for (int i = 0; i < tt.Elements.Count; i++)
                    if (tt.Elements[i] is NameExpression en) bodyScope.Declare(en.Name, tupType.Elements[i]);
            }
            else { Reject(PyCppDiagnosticCodes.UnsupportedStatement, fs.Target, "unsupported for-loop target shape"); return; }

            CheckSuite(fs.Body, bodyScope, currentFunction, returnTypes);
        }

        // `range(...)` isn't a first-class value in this subset — only valid as a for-loop's
        // direct Iter — so it's resolved here rather than through the general InferExpr path.
        private CppType InferIterableElementType(ExpressionSyntax iter, Scope scope)
        {
            if (iter is CallExpression ce && ce.Callee is NameExpression cn && cn.Name == "range")
            {
                foreach (CallArgument a in ce.Arguments)
                {
                    CppType at = InferExpr(a.Value, scope);
                    if (at != null && !at.Equals(CppType.Int))
                        Reject(PyCppDiagnosticCodes.TypeMismatch, a.Value, "range(...) arguments must be int");
                }
                return CppType.Int;
            }

            CppType iterType = InferExpr(iter, scope);
            switch (iterType)
            {
                case ListType lt: return lt.Element;
                case SetType st: return st.Element;
                case DictType dt: return dt.Key; // `for k in d` iterates keys, matching Python
                case PrimitiveType p when p.Name == "str": return CppType.Str; // iterating a str yields single-char strs
                case null: return null;
                default:
                    Reject(PyCppDiagnosticCodes.UnsupportedStatement, iter, "unsupported for-loop iterable type: " + iterType);
                    return null;
            }
        }

        private void CheckTry(TryStatement trys, Scope scope, FunctionInfo currentFunction, List<CppType> returnTypes)
        {
            CheckSuite(trys.Body, scope, currentFunction, returnTypes);
            foreach (ExceptHandler h in trys.Handlers)
            {
                if (h.IsStar) { Reject(PyCppDiagnosticCodes.UnsupportedStatement, h, "except* is not supported"); continue; }
                var handlerScope = new Scope(scope);
                if (h.ExceptionType != null)
                {
                    CppType exType = ResolveExceptionType(h.ExceptionType);
                    if (exType != null && !string.IsNullOrEmpty(h.Name)) handlerScope.Declare(h.Name, exType);
                }
                CheckSuite(h.Body, handlerScope, currentFunction, returnTypes);
            }
            if (trys.Else != null) CheckSuite(trys.Else, scope, currentFunction, returnTypes);
            if (trys.Finally != null) CheckSuite(trys.Finally, scope, currentFunction, returnTypes);
        }

        private CppType ResolveExceptionType(ExpressionSyntax exType)
        {
            if (exType is NameExpression n)
            {
                if (BuiltinExceptionNames.Contains(n.Name)) return new ClassType(n.Name);
                if (_classes.TryGetValue(n.Name, out ClassInfo ci) && ci.IsExceptionType) return new ClassType(n.Name);
            }
            Reject(PyCppDiagnosticCodes.UnresolvableType, exType, "unresolvable exception type in except-clause");
            return null;
        }

        private void CheckAssignment(AssignmentStatement asn, Scope scope)
        {
            if (asn.Annotation != null)
            {
                if (asn.Targets.Count != 1 || !(asn.Targets[0] is NameExpression nameTarget))
                {
                    Reject(PyCppDiagnosticCodes.UnsupportedStatement, asn, "annotated assignment must target a single plain name");
                    return;
                }
                CppType declared = ResolveAnnotation(asn.Annotation, asn);
                if (declared == null) return;
                if (asn.Value != null)
                {
                    // An empty collection literal (`[]`, `{}`, `set()`... as a DictExpression/
                    // ListExpression/SetExpression node) carries no element-type information of
                    // its own — InferExpr correctly rejects it standalone, but here the
                    // annotation IS that information, so use it as the value's type directly
                    // (still cached into NodeTypes, same as everywhere else) rather than
                    // re-deriving something InferExpr has no way to know.
                    bool isEmptyCollectionLiteral =
                        (asn.Value is ListExpression le && le.Elements.Count == 0) ||
                        (asn.Value is SetExpression se && se.Elements.Count == 0) ||
                        (asn.Value is DictExpression de && de.Entries.Count == 0);
                    CppType valueType = isEmptyCollectionLiteral
                        ? (_nodeTypes[asn.Value] = declared)
                        : InferExprAllowingNone(asn.Value, scope, declared);
                    if (valueType != null && !TypesCompatible(valueType, declared))
                        Reject(PyCppDiagnosticCodes.TypeMismatch, asn, "cannot assign " + valueType + " to " + nameTarget.Name + ": " + declared);
                }
                scope.Declare(nameTarget.Name, declared);
                return;
            }

            if (asn.Targets.Count == 1 && asn.Targets[0] is NameExpression singleTarget)
            {
                // Deferred-lambda pattern: `f = lambda x: ...` — don't type-check the body yet.
                if (asn.Value is LambdaExpression lam) { _pendingLambdas[singleTarget.Name] = lam; return; }

                CppType valueType = InferExpr(asn.Value, scope);
                if (valueType == null) return;
                if (scope.TryLookup(singleTarget.Name, out CppType existing))
                {
                    if (!TypesCompatible(valueType, existing) && !TypesCompatible(existing, valueType))
                        Reject(PyCppDiagnosticCodes.TypeMismatch, asn,
                            "'" + singleTarget.Name + "' was established as " + existing + "; cannot assign incompatible " + valueType);
                    else
                        scope.Declare(singleTarget.Name, WidenedType(existing, valueType));
                }
                else scope.Declare(singleTarget.Name, valueType);
                return;
            }

            if (asn.Targets.Count == 1 && asn.Targets[0] is TupleExpression tupleTarget)
            {
                if (tupleTarget.Elements.Any(el => el is StarredExpression))
                {
                    Reject(PyCppDiagnosticCodes.UnsupportedStatement, asn, "starred assignment targets (a, *b = ...) are not supported");
                    return;
                }
                CppType valueType = InferExpr(asn.Value, scope);
                if (!(valueType is TupleType tt) || tt.Elements.Count != tupleTarget.Elements.Count)
                {
                    if (valueType != null)
                        Reject(PyCppDiagnosticCodes.TypeMismatch, asn, "tuple-unpacking target arity doesn't match the value's type " + valueType);
                    return;
                }
                for (int i = 0; i < tupleTarget.Elements.Count; i++)
                    if (tupleTarget.Elements[i] is NameExpression en) scope.Declare(en.Name, tt.Elements[i]);
                return;
            }

            if (asn.Targets.Count == 1 && asn.Targets[0] is AttributeExpression attrTarget)
            {
                CppType objType = InferExpr(attrTarget.Target, scope);
                CppType valueType = InferExpr(asn.Value, scope);
                if (objType is ClassType ct && valueType != null)
                {
                    // Field established/refined by this assignment (typically inside __init__,
                    // already seeded — this also allows a plain, non-__init__ instance-field
                    // write as long as the field already exists on the class).
                    if (_classes.TryGetValue(ct.Name, out ClassInfo ci) && ci.Fields.TryGetValue(attrTarget.Name, out FieldInfo existingField))
                    {
                        if (!TypesCompatible(valueType, existingField.Type))
                            Reject(PyCppDiagnosticCodes.TypeMismatch, asn, "field '" + attrTarget.Name + "' is " + existingField.Type + "; cannot assign " + valueType);
                    }
                    else if (_classes.TryGetValue(ct.Name, out ClassInfo ci2))
                    {
                        ci2.Fields[attrTarget.Name] = new FieldInfo { Name = attrTarget.Name, Type = valueType };
                    }
                }
                return;
            }

            if (asn.Targets.Count == 1 && asn.Targets[0] is SubscriptExpression subTarget)
            {
                CppType targetType = InferExpr(subTarget.Target, scope);
                if (targetType is DictType dt)
                {
                    CppType keyType = InferExpr(subTarget.Index, scope);
                    CppType valueType = InferExpr(asn.Value, scope);
                    if (keyType != null && !TypesCompatible(keyType, dt.Key))
                        Reject(PyCppDiagnosticCodes.TypeMismatch, asn, "dict key type mismatch: expected " + dt.Key + ", got " + keyType);
                    if (valueType != null && !TypesCompatible(valueType, dt.Value))
                        Reject(PyCppDiagnosticCodes.TypeMismatch, asn, "dict value type mismatch: expected " + dt.Value + ", got " + valueType);
                }
                else if (targetType is ListType lt)
                {
                    CppType indexType = InferExpr(subTarget.Index, scope);
                    CppType valueType = InferExpr(asn.Value, scope);
                    if (indexType != null && !indexType.Equals(CppType.Int))
                        Reject(PyCppDiagnosticCodes.TypeMismatch, subTarget.Index, "list index must be int");
                    if (valueType != null && !TypesCompatible(valueType, lt.Element))
                        Reject(PyCppDiagnosticCodes.TypeMismatch, asn, "list element type mismatch: expected " + lt.Element + ", got " + valueType);
                }
                else if (targetType != null)
                {
                    Reject(PyCppDiagnosticCodes.UnsupportedSlicingTarget, asn, "subscript assignment is not supported on " + targetType);
                }
                return;
            }

            if (asn.Targets.Count > 1)
            {
                // Chained assignment `a = b = value` — supported as repeated single-name
                // assignment, each independently established/checked.
                CppType valueType = InferExpr(asn.Value, scope);
                if (valueType == null) return;
                foreach (ExpressionSyntax target in asn.Targets)
                {
                    if (!(target is NameExpression tn)) { Reject(PyCppDiagnosticCodes.UnsupportedStatement, asn, "unsupported chained-assignment target shape"); return; }
                    scope.Declare(tn.Name, valueType);
                }
                return;
            }

            Reject(PyCppDiagnosticCodes.UnsupportedStatement, asn, "unsupported assignment target shape");
        }

        // A value expression allowed to be a bare `None` ONLY when the surrounding context
        // (a declared type) is Optional[...]. Plain InferExpr rejects bare None outright
        // (see InferExpr's LiteralExpression/None case) since it has no useful standalone type.
        private CppType InferExprAllowingNone(ExpressionSyntax e, Scope scope, CppType context)
        {
            if (e is LiteralExpression lit && lit.Token?.Kind == TokenKind.KwNone)
            {
                if (context is OptionalType)
                {
                    // Cache the FULL Optional[T] context (not just CppType.None) against this
                    // literal node — Pass 2 needs it to know whether the empty state is a
                    // shared_ptr `nullptr` (Optional[ClassType]) or a `std::nullopt`
                    // (Optional[anything else]); those aren't interchangeable in C++.
                    _nodeTypes[e] = context;
                    return CppType.None;
                }
                Reject(PyCppDiagnosticCodes.TypeMismatch, e, "None is only assignable to an Optional[...] target");
                return null;
            }
            return InferExpr(e, scope);
        }

        private static CppType WidenedType(CppType a, CppType b)
        {
            if (a.Equals(b)) return a;
            if (a.IsNumeric && b.IsNumeric) return CppType.Float; // int/float mix widens to float
            return a; // TypesCompatible already confirmed one direction works
        }

        private static bool TypesCompatible(CppType actual, CppType target)
        {
            if (actual.Equals(target)) return true;
            if (actual.Equals(CppType.Int) && target.Equals(CppType.Float)) return true; // int -> float widening
            if (target is OptionalType opt) return actual.Equals(CppType.None) || TypesCompatible(actual, opt.Inner);
            return false;
        }

        // ================================================================
        // Expression inference
        // ================================================================

        // Pass 2 (the C++ emitter) needs to know every expression node's resolved type
        // without re-deriving it — re-running inference independently would risk Pass 1 and
        // Pass 2 silently disagreeing about a type, exactly the kind of drift this whole
        // design exists to prevent. InferExpr caches into this map as a side effect so a
        // single Check() run leaves a complete node -> type oracle behind for Pass 2 to use.
        private readonly Dictionary<ExpressionSyntax, CppType> _nodeTypes = new Dictionary<ExpressionSyntax, CppType>();
        public IReadOnlyDictionary<ExpressionSyntax, CppType> NodeTypes => _nodeTypes;
        public IReadOnlyDictionary<string, ClassInfo> Classes => _classes;
        public IReadOnlyDictionary<string, FunctionInfo> Functions => _functions;

        private CppType InferExpr(ExpressionSyntax e, Scope scope)
        {
            CppType t = InferExprCore(e, scope);
            if (e != null && t != null) _nodeTypes[e] = t;
            return t;
        }

        private CppType InferExprCore(ExpressionSyntax e, Scope scope)
        {
            switch (e)
            {
                case null: return null;

                case LiteralExpression lit:
                    if (lit.Token?.Kind == TokenKind.KwNone)
                    {
                        Reject(PyCppDiagnosticCodes.UnresolvableType, e, "bare None has no usable static type outside an Optional[...] context");
                        return null;
                    }
                    return TypeOfLiteral(lit);

                case NameExpression n:
                    if (scope.TryLookup(n.Name, out CppType t)) return t;
                    Reject(PyCppDiagnosticCodes.UnknownName, e, "unknown name: " + n.Name);
                    return null;

                case ParenthesizedExpression pe: return InferExpr(pe.Inner, scope);

                case BinaryExpression be: return InferBinary(be, scope);
                case UnaryExpression ue: return InferUnary(ue, scope);
                case ComparisonExpression cmp: return InferComparison(cmp, scope);

                case ConditionalExpression ce:
                {
                    InferExpr(ce.Condition, scope);
                    CppType a = InferExpr(ce.IfTrue, scope);
                    CppType b = InferExpr(ce.IfFalse, scope);
                    if (a == null || b == null) return null;
                    if (a.Equals(b)) return a;
                    if (a.IsNumeric && b.IsNumeric) return CppType.Float;
                    Reject(PyCppDiagnosticCodes.TypeMismatch, e, "ternary branches have different types: " + a + " vs " + b);
                    return null;
                }

                case CallExpression call: return InferCall(call, scope);

                case SubscriptExpression sub: return InferSubscript(sub, scope);

                case AttributeExpression attr:
                {
                    CppType objType = InferExpr(attr.Target, scope);
                    if (!(objType is ClassType ct)) { if (objType != null) Reject(PyCppDiagnosticCodes.UnknownAttribute, e, "attribute access on a non-class type"); return null; }
                    if (LookupField(ct.Name, attr.Name, out CppType fieldType)) return fieldType;
                    // `@property` methods are read like plain fields in Python (`obj.name`,
                    // no parens) — the checker must invoke them implicitly, same as Pass 2
                    // will need to emit an implicit call.
                    if (LookupMethod(ct.Name, attr.Name, out FunctionInfo propMethod, out _) && propMethod.IsProperty)
                        return propMethod.ReturnType;
                    Reject(PyCppDiagnosticCodes.UnknownAttribute, e, "'" + ct.Name + "' has no field '" + attr.Name + "' (method references as values are not supported — only direct calls, or a @property)");
                    return null;
                }

                case TupleExpression tup:
                {
                    var elems = new List<CppType>();
                    foreach (ExpressionSyntax el in tup.Elements)
                    {
                        CppType et = InferExpr(el, scope);
                        if (et == null) return null;
                        elems.Add(et);
                    }
                    return new TupleType(elems);
                }

                case ListExpression list: return InferHomogeneousContainer(list.Elements, scope, es => new ListType(es), e);
                case SetExpression set: return InferHomogeneousContainer(set.Elements, scope, es => new SetType(es), e);

                case DictExpression dict:
                {
                    if (dict.Entries.Count == 0) { Reject(PyCppDiagnosticCodes.UnresolvableType, e, "cannot infer element type of an empty dict literal — annotate the target"); return null; }
                    CppType keyType = null, valType = null;
                    foreach (DictEntry entry in dict.Entries)
                    {
                        if (entry.Key == null) { Reject(PyCppDiagnosticCodes.UnsupportedExpression, entry, "**unpacking inside a dict literal is not supported"); return null; }
                        CppType k = InferExpr(entry.Key, scope);
                        CppType v = InferExpr(entry.Value, scope);
                        if (k == null || v == null) return null;
                        if (keyType == null) { keyType = k; valType = v; }
                        else if (!keyType.Equals(k) || !valType.Equals(v))
                        { Reject(PyCppDiagnosticCodes.TypeMismatch, entry, "dict literal entries must share one key type and one value type"); return null; }
                    }
                    return new DictType(keyType, valType);
                }

                case ComprehensionExpression comp: return InferComprehension(comp, scope);

                case FStringExpression fstr: return InferFString(fstr, scope);

                case StarredExpression star:
                    Reject(PyCppDiagnosticCodes.UnsupportedExpression, e, "starred expressions are only supported as call arguments");
                    return null;

                case LambdaExpression _:
                    Reject(PyCppDiagnosticCodes.UnsupportedExpression, e,
                        "a lambda must be directly assigned to a bare name and called by that name (inline lambdas are not supported in this subset)");
                    return null;

                case NamedExpression named:
                {
                    CppType vt = InferExpr(named.Value, scope);
                    if (vt != null) scope.Declare(named.Target.Name, vt);
                    return vt;
                }

                case YieldExpression _:
                    Reject(PyCppDiagnosticCodes.UnsupportedExpression, e, "generators (yield) are not supported");
                    return null;

                default:
                    Reject(PyCppDiagnosticCodes.UnsupportedExpression, e, "unsupported expression: " + e.GetType().Name);
                    return null;
            }
        }

        private CppType InferHomogeneousContainer(IReadOnlyList<ExpressionSyntax> elements, Scope scope, Func<CppType, CppType> wrap, SyntaxNode context)
        {
            if (elements.Count == 0) { Reject(PyCppDiagnosticCodes.UnresolvableType, context, "cannot infer element type of an empty literal — annotate the target"); return null; }
            CppType elemType = null;
            foreach (ExpressionSyntax el in elements)
            {
                if (el is StarredExpression) { Reject(PyCppDiagnosticCodes.UnsupportedExpression, el, "starred unpacking inside a literal is not supported"); return null; }
                CppType t = InferExpr(el, scope);
                if (t == null) return null;
                if (elemType == null) elemType = t;
                else if (!elemType.Equals(t))
                {
                    if (elemType.IsNumeric && t.IsNumeric) elemType = CppType.Float;
                    else { Reject(PyCppDiagnosticCodes.TypeMismatch, el, "literal has mixed element types: " + elemType + " and " + t); return null; }
                }
            }
            return wrap(elemType);
        }

        private static CppType TypeOfLiteral(LiteralExpression lit)
        {
            Token t = lit.Token;
            switch (t.Kind)
            {
                case TokenKind.Number:
                    bool isFloat = t.Text != null && (t.Text.IndexOf('.') >= 0 || t.Text.IndexOf('e') >= 0 || t.Text.IndexOf('E') >= 0);
                    return isFloat ? CppType.Float : CppType.Int;
                case TokenKind.String: return CppType.Str;
                case TokenKind.KwTrue: case TokenKind.KwFalse: return CppType.Bool;
                default: return null;
            }
        }

        private CppType InferBinary(BinaryExpression be, Scope scope)
        {
            CppType l = InferExpr(be.Left, scope);
            CppType r = InferExpr(be.Right, scope);
            if (l == null || r == null) return null;
            return CheckBinaryOperandTypes(be.Operator, l, r, be);
        }

        private CppType CheckBinaryOperandTypes(BinaryOperator op, CppType l, CppType r, SyntaxNode context)
        {
            switch (op)
            {
                case BinaryOperator.Add:
                    if (l.IsNumeric && r.IsNumeric) return (l.Equals(CppType.Float) || r.Equals(CppType.Float)) ? CppType.Float : CppType.Int;
                    if (l.Equals(CppType.Str) && r.Equals(CppType.Str)) return CppType.Str;
                    if (l is ListType lt && r is ListType rt && lt.Element.Equals(rt.Element)) return l;
                    break;
                case BinaryOperator.Subtract: case BinaryOperator.FloorDivide: case BinaryOperator.Modulo:
                    if (l.IsNumeric && r.IsNumeric) return (l.Equals(CppType.Float) || r.Equals(CppType.Float)) ? CppType.Float : CppType.Int;
                    break;
                case BinaryOperator.Multiply:
                    if (l.IsNumeric && r.IsNumeric) return (l.Equals(CppType.Float) || r.Equals(CppType.Float)) ? CppType.Float : CppType.Int;
                    if (l.Equals(CppType.Str) && r.Equals(CppType.Int)) return CppType.Str;
                    if (l.Equals(CppType.Int) && r.Equals(CppType.Str)) return CppType.Str;
                    if (l is ListType && r.Equals(CppType.Int)) return l;
                    break;
                case BinaryOperator.Divide:
                    if (l.IsNumeric && r.IsNumeric) return CppType.Float;
                    break;
                case BinaryOperator.Power:
                    if (l.Equals(CppType.Float) || r.Equals(CppType.Float)) return CppType.Float;
                    if (l.Equals(CppType.Int) && r.Equals(CppType.Int)) return CppType.Int; // exact non-negativity is a Pass-2/runtime concern (pyrt::pow_int throws otherwise)
                    break;
                case BinaryOperator.BitAnd: case BinaryOperator.BitOr: case BinaryOperator.BitXor:
                case BinaryOperator.LShift: case BinaryOperator.RShift:
                    if (l.Equals(CppType.Int) && r.Equals(CppType.Int)) return CppType.Int;
                    break;
                case BinaryOperator.And: case BinaryOperator.Or:
                    if (l.Equals(r)) return l;
                    Reject(PyCppDiagnosticCodes.TypeMismatch, context, "and/or requires both operands to have the same static type in this subset: " + l + " vs " + r);
                    return null;
                case BinaryOperator.MatMul:
                    Reject(PyCppDiagnosticCodes.UnsupportedExpression, context, "the @ (matrix multiply) operator is not supported");
                    return null;
            }
            Reject(PyCppDiagnosticCodes.TypeMismatch, context, "unsupported operand types for " + op + ": " + l + " and " + r);
            return null;
        }

        private CppType InferUnary(UnaryExpression ue, Scope scope)
        {
            CppType t = InferExpr(ue.Operand, scope);
            if (t == null) return null;
            switch (ue.Operator)
            {
                case UnaryOperator.Plus: case UnaryOperator.Minus:
                    if (t.IsNumeric) return t;
                    break;
                case UnaryOperator.BitNot:
                    if (t.Equals(CppType.Int)) return CppType.Int;
                    break;
                case UnaryOperator.Not: return CppType.Bool; // truthy-conversion — pyrt::truthy() handles the value side
                case UnaryOperator.Await:
                    Reject(PyCppDiagnosticCodes.UnsupportedExpression, ue, "await is not supported (async is not in this subset)");
                    return null;
            }
            Reject(PyCppDiagnosticCodes.TypeMismatch, ue, "unsupported operand type for " + ue.Operator + ": " + t);
            return null;
        }

        private CppType InferComparison(ComparisonExpression cmp, Scope scope)
        {
            CppType left = InferExpr(cmp.Left, scope);
            for (int i = 0; i < cmp.Operators.Count; i++)
            {
                ComparisonOperator op = cmp.Operators[i];
                ExpressionSyntax rightExpr = cmp.Comparators[i];

                if (op == ComparisonOperator.Is || op == ComparisonOperator.IsNot)
                {
                    bool rightIsNone = rightExpr is LiteralExpression rl && rl.Token?.Kind == TokenKind.KwNone;
                    if (!rightIsNone)
                    { Reject(PyCppDiagnosticCodes.UnsupportedExpression, cmp, "'is'/'is not' is only supported for a None check"); return null; }
                    if (!(left is OptionalType))
                    { Reject(PyCppDiagnosticCodes.TypeMismatch, cmp, "'is None' requires an Optional[...] value"); return null; }
                    left = CppType.Bool;
                    continue;
                }

                CppType right = InferExpr(rightExpr, scope);
                if (left == null || right == null) return null;

                switch (op)
                {
                    case ComparisonOperator.Eq: case ComparisonOperator.NotEq:
                        if (!left.Equals(right) && !(left.IsNumeric && right.IsNumeric))
                        { Reject(PyCppDiagnosticCodes.TypeMismatch, cmp, "== / != between different types: " + left + " and " + right); return null; }
                        break;
                    case ComparisonOperator.Less: case ComparisonOperator.LessEq: case ComparisonOperator.Greater: case ComparisonOperator.GreaterEq:
                        bool okNumeric = left.IsNumeric && right.IsNumeric;
                        bool okStr = left.Equals(CppType.Str) && right.Equals(CppType.Str);
                        if (!okNumeric && !okStr)
                        { Reject(PyCppDiagnosticCodes.TypeMismatch, cmp, "ordering comparison between unsupported types: " + left + " and " + right); return null; }
                        break;
                    case ComparisonOperator.In: case ComparisonOperator.NotIn:
                        bool memberOk = (right is ListType lt2 && lt2.Element.Equals(left)) ||
                                        (right is SetType st2 && st2.Element.Equals(left)) ||
                                        (right is DictType dt2 && dt2.Key.Equals(left)) ||
                                        (right.Equals(CppType.Str) && left.Equals(CppType.Str));
                        if (!memberOk)
                        { Reject(PyCppDiagnosticCodes.TypeMismatch, cmp, "'in' between incompatible types: " + left + " and " + right); return null; }
                        break;
                }
                left = CppType.Bool;
            }
            return CppType.Bool;
        }

        private CppType InferSubscript(SubscriptExpression sub, Scope scope)
        {
            CppType targetType = InferExpr(sub.Target, scope);
            if (targetType == null) return null;

            if (sub.Index is SliceExpression sl)
            {
                if (sl.Lower != null) InferExpr(sl.Lower, scope);
                if (sl.Upper != null) InferExpr(sl.Upper, scope);
                if (sl.Step != null) InferExpr(sl.Step, scope);
                if (targetType is ListType || targetType.Equals(CppType.Str)) return targetType;
                Reject(PyCppDiagnosticCodes.UnsupportedSlicingTarget, sub, "slicing is only supported on list/str, not " + targetType);
                return null;
            }

            switch (targetType)
            {
                case ListType lt:
                {
                    CppType idx = InferExpr(sub.Index, scope);
                    if (idx != null && !idx.Equals(CppType.Int)) Reject(PyCppDiagnosticCodes.TypeMismatch, sub.Index, "list index must be int");
                    return lt.Element;
                }
                case DictType dt:
                {
                    CppType key = InferExpr(sub.Index, scope);
                    if (key != null && !key.Equals(dt.Key)) Reject(PyCppDiagnosticCodes.TypeMismatch, sub.Index, "dict key type mismatch: expected " + dt.Key + ", got " + key);
                    return dt.Value;
                }
                case PrimitiveType p when p.Name == "str":
                {
                    CppType idx = InferExpr(sub.Index, scope);
                    if (idx != null && !idx.Equals(CppType.Int)) Reject(PyCppDiagnosticCodes.TypeMismatch, sub.Index, "string index must be int");
                    return CppType.Str;
                }
                case TupleType tt:
                {
                    if (!(sub.Index is LiteralExpression lit) || lit.Token?.Kind != TokenKind.Number)
                    { Reject(PyCppDiagnosticCodes.UnsupportedSlicingTarget, sub, "tuple subscript requires a literal integer index"); return null; }
                    if (!int.TryParse(lit.Token.Text, out int i) || i < 0 || i >= tt.Elements.Count)
                    { Reject(PyCppDiagnosticCodes.TypeMismatch, sub, "tuple index out of range at translate time"); return null; }
                    return tt.Elements[i];
                }
                default:
                    Reject(PyCppDiagnosticCodes.UnsupportedSlicingTarget, sub, "subscript is not supported on " + targetType);
                    return null;
            }
        }

        private CppType InferComprehension(ComprehensionExpression comp, Scope outerScope)
        {
            Scope scope = outerScope;
            foreach (ComprehensionClause clause in comp.Clauses)
            {
                CppType elemType = InferIterableElementType(clause.Iter, scope);
                if (elemType == null) return null;
                var inner = new Scope(scope);
                if (clause.Target is NameExpression tn) inner.Declare(tn.Name, elemType);
                else if (clause.Target is TupleExpression tt && elemType is TupleType tupType && tupType.Elements.Count == tt.Elements.Count)
                {
                    for (int i = 0; i < tt.Elements.Count; i++)
                        if (tt.Elements[i] is NameExpression en) inner.Declare(en.Name, tupType.Elements[i]);
                }
                else { Reject(PyCppDiagnosticCodes.UnsupportedStatement, clause.Target, "unsupported comprehension target shape"); return null; }
                foreach (ExpressionSyntax ifExpr in clause.Ifs) InferExpr(ifExpr, inner);
                scope = inner;
            }

            if (comp.Kind == ComprehensionKind.Dict)
            {
                CppType keyType = InferExpr(comp.Element, scope);
                CppType valType = InferExpr(comp.ValueElement, scope);
                return (keyType != null && valType != null) ? new DictType(keyType, valType) : null;
            }

            CppType elem = InferExpr(comp.Element, scope);
            if (elem == null) return null;
            switch (comp.Kind)
            {
                case ComprehensionKind.Set: return new SetType(elem);
                default: return new ListType(elem); // List and Generator both materialize eagerly (see class comment)
            }
        }

        private CppType InferFString(FStringExpression fstr, Scope scope)
        {
            for (int i = 0; i < fstr.Parts.Count; i++)
            {
                if (!(fstr.Parts[i] is ExpressionSyntax expr) || fstr.Parts[i] is LiteralExpression) continue;
                CppType t = InferExpr(expr, scope);
                string spec = i < fstr.FormatSpecs.Count ? fstr.FormatSpecs[i] : null;
                if (!string.IsNullOrEmpty(spec) && spec.IndexOf('{') >= 0)
                    Reject(PyCppDiagnosticCodes.DynamicFormatSpec, expr, "nested/dynamic format specs (e.g. f\"{x:{width}}\") are not supported");
                if (t == null) continue;
                if (!(t.Equals(CppType.Int) || t.Equals(CppType.Float) || t.Equals(CppType.Bool) || t.Equals(CppType.Str) || t is ClassType))
                    Reject(PyCppDiagnosticCodes.UnsupportedExpression, expr, "f-string expression has an unformattable type: " + t);
            }
            return CppType.Str;
        }

        private bool LookupField(string className, string fieldName, out CppType type)
        {
            for (string current = className; current != null;)
            {
                if (!_classes.TryGetValue(current, out ClassInfo ci)) break;
                if (ci.Fields.TryGetValue(fieldName, out FieldInfo fi)) { type = fi.Type; return true; }
                current = ci.BaseClassName;
            }
            type = null;
            return false;
        }

        private bool LookupMethod(string className, string methodName, out FunctionInfo method, out string ownerClass)
        {
            for (string current = className; current != null;)
            {
                if (!_classes.TryGetValue(current, out ClassInfo ci)) break;
                if (ci.Methods.TryGetValue(methodName, out FunctionInfo fi)) { method = fi; ownerClass = current; return true; }
                current = ci.BaseClassName;
            }
            method = null; ownerClass = null;
            return false;
        }

        private CppType InferCall(CallExpression call, Scope scope)
        {
            if (call.Callee is NameExpression calleeName)
            {
                string name = calleeName.Name;

                if (name == "eval" || name == "exec")
                { Reject(PyCppDiagnosticCodes.UnsupportedExpression, call, name + "(...) is not supported"); return null; }
                if (name == "getattr" || name == "setattr" || name == "hasattr")
                { CheckDynamicAttributeCall(call); return null; }

                if (_classes.ContainsKey(name)) return InferConstructorCall(call, name, scope);
                if (_functions.TryGetValue(name, out FunctionInfo fi)) return InferFreeCall(call, fi, scope);
                if (_pendingLambdas.TryGetValue(name, out LambdaExpression lam)) return ResolveAndCallLambda(call, name, lam, scope);
                if (WhitelistedBuiltins.Contains(name)) return InferBuiltinCall(call, name, scope);

                Reject(PyCppDiagnosticCodes.UnknownName, call, "unknown function (or unsupported import): " + name);
                return null;
            }

            // `super().__init__(...)` — the standard way a derived class's __init__ chains to
            // its base's. Only this exact shape is recognized (no-arg super(), immediately
            // .__init__(...)) — general super().method() or the two-argument super(T, self)
            // form are out of scope.
            if (call.Callee is AttributeExpression superAttr && superAttr.Name == "__init__" &&
                superAttr.Target is CallExpression superCall && superCall.Callee is NameExpression superName &&
                superName.Name == "super" && superCall.Arguments.Count == 0)
            {
                if (_currentClassContext == null || !_classes.TryGetValue(_currentClassContext, out ClassInfo currentClass) || currentClass.BaseClassName == null)
                {
                    Reject(PyCppDiagnosticCodes.UnsupportedExpression, call, "super().__init__(...) used outside a derived class's __init__");
                    return null;
                }
                if (!LookupMethod(currentClass.BaseClassName, "__init__", out FunctionInfo baseInit, out _))
                {
                    foreach (CallArgument a in call.Arguments) InferExpr(a.Value, scope);
                    return CppType.None; // base class has no explicit __init__ — nothing to check args against
                }
                return CheckArgsAndReturn(call, baseInit, scope, isConstructor: true);
            }

            if (call.Callee is AttributeExpression methodAttr)
            {
                // `ClassName.method(...)` — a static/classmethod call via the class name
                // itself, not through an instance. Only resolvable when the target is a bare
                // name that IS a known class (an instance variable happening to be named the
                // same as a class isn't ambiguous here: InferExpr on a NameExpression only
                // ever returns a scope-variable's type, never the class object itself).
                if (methodAttr.Target is NameExpression classNameRef && !scope.TryLookup(classNameRef.Name, out _) &&
                    _classes.ContainsKey(classNameRef.Name))
                {
                    if (!LookupMethod(classNameRef.Name, methodAttr.Name, out FunctionInfo staticMethod, out _))
                    { Reject(PyCppDiagnosticCodes.UnknownAttribute, call, "'" + classNameRef.Name + "' has no method '" + methodAttr.Name + "'"); return null; }
                    if (!staticMethod.IsStatic && !staticMethod.IsClassMethod)
                    { Reject(PyCppDiagnosticCodes.UnsupportedExpression, call, "'" + methodAttr.Name + "' is an instance method — call it through an instance, not the class"); return null; }
                    return CheckArgsAndReturn(call, staticMethod, scope);
                }

                CppType objType = InferExpr(methodAttr.Target, scope);
                if (objType is ListType || objType is DictType || objType is SetType)
                    return InferCollectionMethodCall(call, objType, methodAttr.Name, scope);
                if (!(objType is ClassType ct)) { if (objType != null) Reject(PyCppDiagnosticCodes.UnsupportedExpression, call, "method call on a non-class type"); return null; }
                if (!LookupMethod(ct.Name, methodAttr.Name, out FunctionInfo method, out _))
                { Reject(PyCppDiagnosticCodes.UnknownAttribute, call, "'" + ct.Name + "' has no method '" + methodAttr.Name + "'"); return null; }
                return CheckArgsAndReturn(call, method, scope);
            }

            Reject(PyCppDiagnosticCodes.UnsupportedExpression, call, "unsupported call target");
            return null;
        }

        // Built-in list/dict/set methods, matching exactly what pyrt.hpp's List<T>/Dict<K,V>/
        // Set<T> implement — anything Python offers beyond this (str methods, dict.keys()/
        // values()/items(), list.remove(value), set difference_update, ...) is a documented
        // v1 gap: rejected here rather than accepted and left for Pass 2 to fail on.
        private CppType InferCollectionMethodCall(CallExpression call, CppType objType, string methodName, Scope scope)
        {
            var argTypes = call.Arguments.Select(a => InferExpr(a.Value, scope)).ToList();

            if (objType is ListType lt)
            {
                switch (methodName)
                {
                    case "append": return Expect(argTypes, 1, lt.Element) ? CppType.None : null;
                    case "extend": return Expect(argTypes, 1, lt) ? CppType.None : null;
                    case "insert": return Expect2(argTypes, CppType.Int, lt.Element) ? CppType.None : null;
                    case "pop": return argTypes.Count == 0 ? lt.Element : Expect(argTypes, 1, CppType.Int) ? lt.Element : null;
                    case "clear": return argTypes.Count == 0 ? CppType.None : ArityError(call, methodName);
                    case "copy": return argTypes.Count == 0 ? lt : ArityError(call, methodName);
                    case "reverse": return argTypes.Count == 0 ? CppType.None : ArityError(call, methodName);
                    case "sort": return argTypes.Count == 0 ? CppType.None : ArityError(call, methodName);
                    default: return UnknownCollectionMethod(call, "list", methodName);
                }
            }
            if (objType is DictType dt)
            {
                switch (methodName)
                {
                    case "get":
                        if (argTypes.Count == 1) return Expect(argTypes, 1, dt.Key) ? dt.Value : null;
                        return Expect2(argTypes, dt.Key, dt.Value) ? dt.Value : null;
                    case "clear": return argTypes.Count == 0 ? CppType.None : ArityError(call, methodName);
                    case "copy": return argTypes.Count == 0 ? dt : ArityError(call, methodName);
                    default: return UnknownCollectionMethod(call, "dict", methodName);
                }
            }
            var st = (SetType)objType;
            switch (methodName)
            {
                case "add": return Expect(argTypes, 1, st.Element) ? CppType.None : null;
                case "discard": return Expect(argTypes, 1, st.Element) ? CppType.None : null;
                case "remove": return Expect(argTypes, 1, st.Element) ? CppType.None : null;
                case "clear": return argTypes.Count == 0 ? CppType.None : ArityError(call, methodName);
                case "copy": return argTypes.Count == 0 ? st : ArityError(call, methodName);
                case "union": return Expect(argTypes, 1, st) ? st : null;
                case "intersection": return Expect(argTypes, 1, st) ? st : null;
                case "difference": return Expect(argTypes, 1, st) ? st : null;
                default: return UnknownCollectionMethod(call, "set", methodName);
            }

            bool Expect(List<CppType> args, int count, CppType elemType)
            {
                if (args.Count != count) { ArityError(call, methodName); return false; }
                if (args[0] != null && !TypesCompatible(args[0], elemType))
                { Reject(PyCppDiagnosticCodes.TypeMismatch, call, methodName + "() argument should be " + elemType + ", got " + args[0]); return false; }
                return true;
            }
            bool Expect2(List<CppType> args, CppType t0, CppType t1)
            {
                if (args.Count != 2) { ArityError(call, methodName); return false; }
                bool ok = true;
                if (args[0] != null && !TypesCompatible(args[0], t0)) { Reject(PyCppDiagnosticCodes.TypeMismatch, call, methodName + "() first argument should be " + t0); ok = false; }
                if (args[1] != null && !TypesCompatible(args[1], t1)) { Reject(PyCppDiagnosticCodes.TypeMismatch, call, methodName + "() second argument should be " + t1); ok = false; }
                return ok;
            }
        }

        private CppType ArityError(CallExpression call, string methodName)
        {
            Reject(PyCppDiagnosticCodes.ArityMismatch, call, methodName + "() called with the wrong number of arguments");
            return null;
        }

        private CppType UnknownCollectionMethod(CallExpression call, string kind, string methodName)
        {
            Reject(PyCppDiagnosticCodes.UnknownAttribute, call, "'" + methodName + "' is not a supported " + kind + " method in this subset");
            return null;
        }

        private void CheckDynamicAttributeCall(CallExpression call)
        {
            bool literalName = call.Arguments.Count >= 2 && call.Arguments[1].Value is LiteralExpression lit && lit.Token?.Kind == TokenKind.String;
            if (!literalName)
                Reject(PyCppDiagnosticCodes.DynamicAttributeAccess, call, "getattr/setattr/hasattr with a non-literal attribute name is not supported");
            else
                Reject(PyCppDiagnosticCodes.DynamicAttributeAccess, call, "getattr/setattr/hasattr are not supported even with a literal name in this subset (use direct attribute syntax)");
        }

        private CppType InferConstructorCall(CallExpression call, string className, Scope scope)
        {
            ClassInfo ci = _classes[className];
            if (ci.Methods.TryGetValue("__init__", out FunctionInfo init))
                CheckArgsAndReturn(call, init, scope, isConstructor: true);
            else
                foreach (CallArgument a in call.Arguments) InferExpr(a.Value, scope);
            return new ClassType(className);
        }

        private CppType InferFreeCall(CallExpression call, FunctionInfo fi, Scope scope) => CheckArgsAndReturn(call, fi, scope);

        private CppType ResolveAndCallLambda(CallExpression call, string name, LambdaExpression lam, Scope scope)
        {
            var argTypes = new List<CppType>();
            foreach (CallArgument a in call.Arguments) argTypes.Add(InferExpr(a.Value, scope));
            if (argTypes.Any(t => t == null) || argTypes.Count != lam.Parameters.Count)
            { Reject(PyCppDiagnosticCodes.ArityMismatch, call, "lambda '" + name + "' called with the wrong number of arguments"); return null; }

            var lambdaScope = new Scope(null); // lambdas don't capture outer locals in this subset
            var paramInfos = new List<ParamInfo>();
            for (int i = 0; i < lam.Parameters.Count; i++)
            {
                lambdaScope.Declare(lam.Parameters[i].Name, argTypes[i]);
                paramInfos.Add(new ParamInfo { Name = lam.Parameters[i].Name, Type = argTypes[i] });
            }
            CppType bodyType = InferExpr(lam.Body, lambdaScope);
            if (bodyType == null) return null;

            _pendingLambdas.Remove(name);
            _functions[name] = new FunctionInfo
            {
                Name = name, Parameters = paramInfos, ReturnType = bodyType,
                Syntax = null, // lambda bodies are single expressions; Pass 2 handles them via the resolved FunctionInfo directly
            };
            return bodyType;
        }

        private CppType InferBuiltinCall(CallExpression call, string name, Scope scope)
        {
            var argTypes = call.Arguments.Select(a => InferExpr(a.Value, scope)).ToList();
            switch (name)
            {
                case "print": return CppType.None;
                case "len": return CppType.Int;
                case "str": return CppType.Str;
                case "int": return CppType.Int;
                case "float": return CppType.Float;
                case "bool": return CppType.Bool;
                case "abs": return argTypes.Count == 1 && argTypes[0] != null && argTypes[0].IsNumeric ? argTypes[0] : Reject2(call, "abs() requires one numeric argument");
                case "sorted":
                    if (argTypes.Count == 1 && argTypes[0] is ListType lt) return lt;
                    if (argTypes.Count == 1 && argTypes[0] is SetType st) return new ListType(st.Element);
                    return Reject2(call, "sorted() requires a single List or Set argument in this subset");
                case "min": case "max":
                    if (argTypes.Count == 1 && argTypes[0] is ListType mlt) return mlt.Element;
                    if (argTypes.Count == 1 && argTypes[0] is SetType mst) return mst.Element;
                    if (argTypes.Count >= 2 && argTypes.All(t => t != null && t.Equals(argTypes[0]))) return argTypes[0];
                    return Reject2(call, name + "() requires a List/Set or 2+ same-typed arguments");
                case "list":
                    if (argTypes.Count == 0) return Reject2(call, "list() with no arguments needs a target-type annotation — write `x: List[T] = []` instead");
                    return argTypes[0];
                case "set":
                    if (argTypes.Count == 1 && argTypes[0] is ListType slt) return new SetType(slt.Element);
                    return Reject2(call, "set() requires a single List argument in this subset");
                case "dict": case "tuple":
                    return Reject2(call, name + "() as a bare conversion call is not supported in this subset — use a literal instead");
                default:
                    return Reject2(call, "unsupported builtin: " + name);
            }
        }

        private CppType Reject2(SyntaxNode node, string message)
        {
            Reject(PyCppDiagnosticCodes.UnsupportedBuiltin, node, message);
            return null;
        }

        private CppType CheckArgsAndReturn(CallExpression call, FunctionInfo fi, Scope scope, bool isConstructor = false)
        {
            var positional = fi.Parameters.Where(p => !p.IsVarPositional && !p.IsVarKeyword).ToList();
            int required = positional.Count(p => !p.HasDefault);
            var posArgs = call.Arguments.Where(a => a.Kind == CallArgument.ArgKind.Positional).ToList();
            var kwArgs = call.Arguments.Where(a => a.Kind == CallArgument.ArgKind.Keyword).ToList();

            if (posArgs.Count + kwArgs.Count < required || posArgs.Count > positional.Count)
            {
                Reject(PyCppDiagnosticCodes.ArityMismatch, call, (isConstructor ? "constructor " : "call to ") + fi.Name + " has the wrong number of arguments");
                foreach (CallArgument a in call.Arguments) InferExpr(a.Value, scope);
                return isConstructor ? null : fi.ReturnType;
            }

            for (int i = 0; i < posArgs.Count; i++)
            {
                CppType at = InferExpr(posArgs[i].Value, scope);
                if (at != null && i < positional.Count && !TypesCompatible(at, positional[i].Type))
                    Reject(PyCppDiagnosticCodes.TypeMismatch, posArgs[i].Value, "argument " + (i + 1) + " to " + fi.Name + " should be " + positional[i].Type + ", got " + at);
            }
            foreach (CallArgument kw in kwArgs)
            {
                CppType at = InferExpr(kw.Value, scope);
                ParamInfo p = positional.FirstOrDefault(pp => pp.Name == kw.Name);
                if (p == null) Reject(PyCppDiagnosticCodes.ArityMismatch, kw, "no parameter named '" + kw.Name + "' on " + fi.Name);
                else if (at != null && !TypesCompatible(at, p.Type))
                    Reject(PyCppDiagnosticCodes.TypeMismatch, kw.Value, "keyword argument '" + kw.Name + "' to " + fi.Name + " should be " + p.Type + ", got " + at);
            }
            foreach (CallArgument a in call.Arguments)
                if (a.Kind == CallArgument.ArgKind.Star || a.Kind == CallArgument.ArgKind.DoubleStar)
                { InferExpr(a.Value, scope); }

            return fi.ReturnType;
        }
    }
}
