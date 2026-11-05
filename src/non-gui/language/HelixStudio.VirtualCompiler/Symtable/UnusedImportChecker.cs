using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Symtable
{
    /// <summary>
    /// Post-<see cref="NameResolver"/> pass that flags module- and function-scope
    /// imports that are never referenced. Pyflakes-parity (F401). Mirrors
    /// CPython's <c>Modules/_pyflakes</c>-style lint. Walks the scope tree:
    /// for each <see cref="SymbolFlag.DefImport"/> symbol, considers it unused
    /// iff neither its defining scope NOR any descendant scope references the
    /// name as a free-use (a <see cref="SymbolFlag.Use"/> without a local
    /// binding). Names re-exported via module-level <c>__all__</c> are
    /// suppressed. Opaque <c>__all__</c> assignments (computed members) are
    /// treated conservatively: any such assignment suppresses all module-level
    /// unused-import warnings, since we can't know which imports are exported.
    /// </summary>
    public sealed class UnusedImportChecker
    {
        private readonly string _filePath;
        private readonly SymbolTable _table;
        private readonly Module _module;
        private readonly List<CompileDiagnostic> _diagnostics = new List<CompileDiagnostic>();
        private HashSet<string> _exportedByAll;
        private HashSet<string> _futureImportNames;
        private HashSet<string> _forwardRefNames;
        private HashSet<string> _explicitReexports;
        private bool _suppressModuleLevelDueToOpaqueAll;
        private bool _isPackageInit;

        public UnusedImportChecker(string filePath, SymbolTable table, Module module)
        {
            _filePath = filePath ?? string.Empty;
            _table = table ?? throw new ArgumentNullException(nameof(table));
            _module = module;
        }

        public IReadOnlyList<CompileDiagnostic> Diagnostics { get { return _diagnostics; } }

        public void Check()
        {
            _exportedByAll = new HashSet<string>(StringComparer.Ordinal);
            _futureImportNames = new HashSet<string>(StringComparer.Ordinal);
            _forwardRefNames = new HashSet<string>(StringComparer.Ordinal);
            _explicitReexports = new HashSet<string>(StringComparer.Ordinal);
            _suppressModuleLevelDueToOpaqueAll = false;
            _isPackageInit = IsInitFile(_filePath);
            if (_module != null) { CollectAllExports(_module); CollectFutureImports(_module); CollectExplicitReexports(_module); CollectForwardRefNames(_module); }
            if (_table.Root == null) return;
            CheckScope(_table.Root, rootModule: true);
        }

        private void CheckScope(Scope scope, bool rootModule)
        {
            foreach (KeyValuePair<string, Symbol> kv in scope.Symbols)
            {
                Symbol s = kv.Value;
                if (!s.Has(SymbolFlag.DefImport)) continue;
                // `from __future__ import X` is a compile-time directive, never referenced
                // by name — pyflakes (and CPython) never flag it as unused. e.g. the
                // ubiquitous `from __future__ import annotations` (~20 files each in both
                // fixtures) was the dominant PY5001 false positive.
                if (_futureImportNames.Contains(s.Name)) continue;
                // `global name; import name` writes the module binding. Treat it as a dynamic
                // module import rather than a function-local candidate; later module/function uses
                // are resolved through that global binding (Pydantic's lazy email_validator load).
                if (scope.IsDeclaredGlobal(s.Name)) continue;
                // A module-level import in a package `__init__.py` is, by near-universal convention,
                // a re-export of the package's public API — PyCharm and Ruff both treat it as used
                // even with no `__all__`. click's entire __init__.py (64 imports) and requests'
                // __init__.py are exactly this. Function-scope imports in __init__.py are NOT
                // re-exports, so only the module (root) scope is exempted.
                if (rootModule && _isPackageInit) continue;
                if (rootModule && _suppressModuleLevelDueToOpaqueAll) continue;
                if (rootModule && _exportedByAll.Contains(s.Name)) continue;
                // `from provider import PublicName as PublicName` is the standard explicit
                // re-export spelling understood by pyflakes/Ruff. Unlike a plain import, the
                // redundant-looking alias is intentional public-API metadata and is common in
                // facade modules such as fastapi.responses and fastapi.requests.
                if (rootModule && _explicitReexports.Contains(s.Name)) continue;
                // A TYPE_CHECKING-only import referenced solely from a STRING (forward-ref)
                // annotation (`def render(self) -> "RenderResult"`) carries no Use flag, because the
                // name lives inside a string literal, not a Name node. pyflakes parses those strings;
                // we collect the identifiers they mention so such imports are not falsely "unused"
                // (rich control.py: ConsoleOptions / RenderResult).
                if (_forwardRefNames.Contains(s.Name)) continue;
                if (IsUsedHereOrBelow(scope, s.Name)) continue;

                _diagnostics.Add(new CompileDiagnostic(_filePath,
                    VirtualCompilerDiagnosticCodes.SymUnusedImport,
                    ParseSeverity.Warning, s.FirstDefSpan,
                    "'" + s.Name + "' imported but unused"));
            }
            foreach (Scope child in scope.Children) CheckScope(child, rootModule: false);
        }

        /// <summary>
        /// The import's own symbol in its defining scope already carries <see cref="SymbolFlag.Use"/>
        /// if the name is referenced there (the symtable builder records name-references
        /// against the enclosing scope). Otherwise, any descendant scope that references
        /// the same identifier as a FREE name (i.e. no local binding in that descendant)
        /// is a use of this outer import.
        /// </summary>
        private bool IsUsedHereOrBelow(Scope definingScope, string name)
        {
            Symbol self;
            if (definingScope.TryGetSymbol(name, out self) && self.Has(SymbolFlag.Use)) return true;
            foreach (Scope child in definingScope.Children)
            {
                if (UsedAsFreeName(child, name)) return true;
            }
            return false;
        }

        private static bool UsedAsFreeName(Scope s, string name)
        {
            Symbol sym;
            if (s.TryGetSymbol(name, out sym))
            {
                // A function/lambda/comprehension binding closes over its descendants and breaks
                // the chain. A CLASS binding does not: Python method bodies skip the class
                // namespace during lexical lookup. Thus `from copy import copy; class C:
                // def copy(...): ...; def clone(...): return copy(...)` uses the MODULE import,
                // not C.copy. Treating the class binding as a closure caused false PY5001 warnings
                // throughout Pydantic.
                if (sym.Has(SymbolFlag.DefBound) && s.Kind != BlockKind.Class) return false;
                if (sym.Has(SymbolFlag.Use) && !sym.Has(SymbolFlag.DefBound)) return true;
            }
            foreach (Scope child in s.Children)
            {
                if (UsedAsFreeName(child, name)) return true;
            }
            return false;
        }

        /// <summary>
        /// Walk the module body for <c>__all__ = [...]</c> / <c>__all__ = (...)</c>
        /// assignments. String-constant members are collected as re-exports; any
        /// non-recognized RHS shape sets <see cref="_suppressModuleLevelDueToOpaqueAll"/>
        /// so we don't false-positive when <c>__all__</c> is computed.
        /// </summary>
        private void CollectAllExports(Module m)
        {
            foreach (Stmt s in m.Body)
            {
                Expr rhs = null;
                bool isAllAssignment = false;
                if (s is Assign a &&
                    a.Targets.Count == 1 &&
                    a.Targets[0] is Name n && n.Id == "__all__")
                { rhs = a.Value; isAllAssignment = true; }
                else if (s is AnnAssign an &&
                    an.Target is Name an2 && an2.Id == "__all__")
                { rhs = an.Value; isAllAssignment = an.Value != null; }
                if (!isAllAssignment) continue;

                IReadOnlyList<Expr> items = null;
                if (rhs is Ast.ListExpr le) items = le.Elts;
                else if (rhs is Ast.TupleExpr te) items = te.Elts;

                if (items == null)
                {
                    _suppressModuleLevelDueToOpaqueAll = true;
                    continue;
                }

                foreach (Expr e in items)
                {
                    if (e is Constant c && c.Value is string str) _exportedByAll.Add(str);
                    else _suppressModuleLevelDueToOpaqueAll = true;
                }
            }
        }

        /// <summary>
        /// Collects the names bound by <c>from __future__ import ...</c> statements so
        /// they are exempt from the unused-import warning (they are compile-time feature
        /// directives, not real imports).
        /// </summary>
        private void CollectFutureImports(Module m)
        {
            foreach (Stmt s in m.Body)
            {
                ImportFrom imp = s as ImportFrom;
                if (imp == null || imp.Module != "__future__") continue;
                foreach (Alias a in imp.Names)
                {
                    string bound = !string.IsNullOrEmpty(a.AsName) ? a.AsName : a.Name;
                    if (!string.IsNullOrEmpty(bound)) _futureImportNames.Add(bound);
                }
            }
        }

        private void CollectExplicitReexports(Module m)
        {
            foreach (Stmt s in m.Body)
            {
                ImportFrom imp = s as ImportFrom;
                if (imp == null) continue;
                foreach (Alias a in imp.Names)
                {
                    if (!string.IsNullOrEmpty(a.Name) &&
                        string.Equals(a.Name, a.AsName, StringComparison.Ordinal))
                        _explicitReexports.Add(a.Name);
                }
            }
        }

        // True for a package initializer (`__init__.py`), whose module-level imports are re-exports.
        private static bool IsInitFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            int slash = path.LastIndexOfAny(new[] { '/', '\\' });
            string name = slash >= 0 ? path.Substring(slash + 1) : path;
            return string.Equals(name, "__init__.py", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "__init__.pyi", StringComparison.OrdinalIgnoreCase);
        }

        // Walks every annotation slot in the module (function params/returns, annotated
        // assignments) and records the identifiers mentioned inside STRING forward-references, so an
        // import used only as `x: "Foo"` / `-> "Bar"` / `List["Foo"]` counts as used.
        private void CollectForwardRefNames(Module m)
        {
            foreach (Stmt s in m.Body) CollectForwardRefsInStmt(s);
        }

        private void CollectForwardRefsInStmt(Stmt s)
        {
            switch (s)
            {
                case FunctionDef f:
                    CollectAnnotationsInArguments(f.Args);
                    ExtractForwardRefs(f.Returns);
                    foreach (Stmt b in f.Body) CollectForwardRefsInStmt(b);
                    break;
                case AsyncFunctionDef af:
                    CollectAnnotationsInArguments(af.Args);
                    ExtractForwardRefs(af.Returns);
                    foreach (Stmt b in af.Body) CollectForwardRefsInStmt(b);
                    break;
                case ClassDef c:
                    foreach (Stmt b in c.Body) CollectForwardRefsInStmt(b);
                    break;
                case AnnAssign an:
                    ExtractForwardRefs(an.Annotation);
                    // TypeAlias declarations commonly put the whole type expression in a string.
                    ExtractForwardRefs(an.Value);
                    ExtractCastForwardRefs(an.Value);
                    break;
                case Assign a:
                    ExtractCastForwardRefs(a.Value);
                    break;
                case If i:
                    foreach (Stmt b in i.Body) CollectForwardRefsInStmt(b);
                    foreach (Stmt b in i.OrElse) CollectForwardRefsInStmt(b);
                    break;
                case For fo:
                    foreach (Stmt b in fo.Body) CollectForwardRefsInStmt(b);
                    foreach (Stmt b in fo.OrElse) CollectForwardRefsInStmt(b);
                    break;
                case AsyncFor afo:
                    foreach (Stmt b in afo.Body) CollectForwardRefsInStmt(b);
                    foreach (Stmt b in afo.OrElse) CollectForwardRefsInStmt(b);
                    break;
                case While w:
                    foreach (Stmt b in w.Body) CollectForwardRefsInStmt(b);
                    foreach (Stmt b in w.OrElse) CollectForwardRefsInStmt(b);
                    break;
                case With wi:
                    foreach (Stmt b in wi.Body) CollectForwardRefsInStmt(b);
                    break;
                case AsyncWith aw:
                    foreach (Stmt b in aw.Body) CollectForwardRefsInStmt(b);
                    break;
                case Try t:
                    foreach (Stmt b in t.Body) CollectForwardRefsInStmt(b);
                    foreach (ExceptHandler h in t.Handlers) foreach (Stmt b in h.Body) CollectForwardRefsInStmt(b);
                    foreach (Stmt b in t.OrElse) CollectForwardRefsInStmt(b);
                    foreach (Stmt b in t.FinalBody) CollectForwardRefsInStmt(b);
                    break;
                case TryStar ts:
                    foreach (Stmt b in ts.Body) CollectForwardRefsInStmt(b);
                    foreach (ExceptHandler h in ts.Handlers) foreach (Stmt b in h.Body) CollectForwardRefsInStmt(b);
                    foreach (Stmt b in ts.OrElse) CollectForwardRefsInStmt(b);
                    foreach (Stmt b in ts.FinalBody) CollectForwardRefsInStmt(b);
                    break;
            }
        }

        private void CollectAnnotationsInArguments(Arguments args)
        {
            if (args == null) return;
            CollectArgAnn(args.PosOnlyArgs);
            CollectArgAnn(args.Args);
            CollectArgAnn(args.KwOnlyArgs);
            if (args.VarArg != null) ExtractForwardRefs(args.VarArg.Annotation);
            if (args.KwArg != null) ExtractForwardRefs(args.KwArg.Annotation);
        }

        private void CollectArgAnn(IReadOnlyList<Arg> list)
        {
            if (list == null) return;
            foreach (Arg a in list) if (a != null) ExtractForwardRefs(a.Annotation);
        }

        // Recurses through the structure of an annotation, pulling identifiers out of any string
        // literal it finds. Handles the whole-string (`"Foo"`), subscripted (`List["Foo"]`,
        // `Dict[str, "Foo"]`), union (`"Foo" | None`), and tuple/list forms.
        private void ExtractForwardRefs(Expr ann)
        {
            if (ann == null) return;
            switch (ann)
            {
                case Name n:
                    _forwardRefNames.Add(n.Id);
                    break;
                case AttributeExpr a:
                    ExtractForwardRefs(a.Value);
                    break;
                case Constant c when c.Value is string sv:
                    AddIdentifiersFromString(sv);
                    break;
                case Subscript sub:
                    ExtractForwardRefs(sub.Value);
                    ExtractForwardRefs(sub.Slice);
                    break;
                case BinOpExpr b:
                    ExtractForwardRefs(b.Left);
                    ExtractForwardRefs(b.Right);
                    break;
                case Ast.TupleExpr t:
                    foreach (Expr e in t.Elts) ExtractForwardRefs(e);
                    break;
                case Ast.ListExpr l:
                    foreach (Expr e in l.Elts) ExtractForwardRefs(e);
                    break;
            }
        }

        private void AddIdentifiersFromString(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int i = 0, n = text.Length;
            while (i < n)
            {
                char ch = text[i];
                if (ch == '_' || char.IsLetter(ch))
                {
                    int start = i;
                    i++;
                    while (i < n && (text[i] == '_' || char.IsLetterOrDigit(text[i]))) i++;
                    _forwardRefNames.Add(text.Substring(start, i - start));
                }
                else i++;
            }
        }

        // typing.cast accepts its type as a runtime expression, and string forward references are
        // idiomatic under TYPE_CHECKING imports: `metadata = cast("CoreMetadata", value)`.
        // The name is a genuine type use even though it is not in an annotation AST slot.
        private void ExtractCastForwardRefs(Expr expr)
        {
            if (expr == null) return;
            switch (expr)
            {
                case Call call:
                    string callee = null;
                    if (call.Func is Name n) callee = n.Id;
                    else if (call.Func is AttributeExpr attr) callee = attr.Attr;
                    if (callee == "cast" && call.Args.Count > 0 &&
                        call.Args[0] is Constant c && c.Value is string typeText)
                        AddIdentifiersFromString(typeText);
                    foreach (Expr arg in call.Args) ExtractCastForwardRefs(arg);
                    foreach (Keyword kw in call.Keywords) ExtractCastForwardRefs(kw.Value);
                    break;
                case AttributeExpr a:
                    ExtractCastForwardRefs(a.Value);
                    break;
                case Subscript s:
                    ExtractCastForwardRefs(s.Value);
                    ExtractCastForwardRefs(s.Slice);
                    break;
                case BinOpExpr b:
                    ExtractCastForwardRefs(b.Left);
                    ExtractCastForwardRefs(b.Right);
                    break;
                case Ast.TupleExpr t:
                    foreach (Expr e in t.Elts) ExtractCastForwardRefs(e);
                    break;
                case Ast.ListExpr l:
                    foreach (Expr e in l.Elts) ExtractCastForwardRefs(e);
                    break;
                case Dict d:
                    foreach (Expr key in d.Keys) ExtractCastForwardRefs(key);
                    foreach (Expr value in d.Values) ExtractCastForwardRefs(value);
                    break;
            }
        }
    }
}
