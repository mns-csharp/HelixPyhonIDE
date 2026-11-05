namespace HelixPythonNamespace.CudaVirtualCompiler.Analyze
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.CudaVirtualCompiler.Symtable;
    using HelixPythonNamespace.Python.Diagnostics;

    /// <summary>
    /// CU2101: use of an undefined name inside a function body (SRS §10 semantic
    /// analysis; previously a declared-but-never-emitted diagnostic). Deliberately
    /// conservative — a Warning, not an Error, and it skips every category the
    /// front end cannot prove absent:
    /// qualified names (`std::foo` — namespaces come from unexpanded headers),
    /// member accesses (only the base object is a name reference),
    /// ALL_CAPS identifiers (function-like macros are not expanded),
    /// `__`-prefixed identifiers (compiler intrinsics beyond the seeded set),
    /// and the whole pass is disabled by the driver when the translation unit
    /// still contains unresolved system includes.
    ///
    /// Locals are checked with an ordered block-scope walk of the body (the symbol
    /// table's function scope stays flat for the other analyses): a declaration is
    /// visible from its statement onward and only inside its enclosing block, so
    /// use-before-declaration and sibling-block leakage are caught. A name that
    /// resolves in the flat scope but not in the ordered walk gets the more precise
    /// out-of-scope wording; a name resolving nowhere is plain undefined. Names
    /// outside the function (params are seeded; fields/globals/builtins via the
    /// scope parent chain; functions via the table) resolve position-independently.
    /// </summary>
    public sealed class NameResolutionAnalyzer
    {
        private const int MaxReports = 100;

        private readonly string filePath_;
        private readonly bool strict_;
        private readonly List<ParseDiagnostic> diagnostics_ = new List<ParseDiagnostic>();

        // Per-function walk state.
        private readonly List<HashSet<string>> blockStack_ = new List<HashSet<string>>();
        private HashSet<string> reported_;
        private Scope fnScope_;
        private SymbolTable table_;

        public NameResolutionAnalyzer(string filePath) : this(filePath, false) { }

        /// <summary><paramref name="strict"/>: the caller resolved every quoted include, so an
        /// undefined name is an ERROR (nvcc parity) rather than an advisory warning.</summary>
        public NameResolutionAnalyzer(string filePath, bool strict)
        {
            filePath_ = filePath ?? string.Empty;
            strict_ = strict;
        }

        public IReadOnlyList<ParseDiagnostic> Diagnostics { get { return diagnostics_; } }

        public void Analyze(TranslationUnit unit, SymbolTable table)
        {
            if (unit == null || table == null) return;
            table_ = table;
            AstWalk.ForEachFunction(unit, fn =>
            {
                if (fn == null || !fn.HasBody || diagnostics_.Count >= MaxReports) return;
                // Out-of-line member definition (`double Coordinate::magnitude() const`):
                // the class's fields are not on this scope's parent chain, so every field
                // reference would be a false undefined-name. Skipped until members are
                // attached to their class scope.
                if (!string.IsNullOrEmpty(fn.QualifiedName) &&
                    fn.QualifiedName.IndexOf("::", System.StringComparison.Ordinal) >= 0) return;
                fnScope_ = table.ScopeFor(fn);
                if (fnScope_ == null) return;   // body scope unavailable — nothing provable

                reported_ = new HashSet<string>();
                blockStack_.Clear();
                HashSet<string> root = new HashSet<string>();
                IReadOnlyList<Parameter> ps = fn.Params;
                for (int i = 0; i < ps.Count; ++i)
                {
                    if (!string.IsNullOrEmpty(ps[i].Name)) root.Add(ps[i].Name);
                }
                blockStack_.Add(root);
                WalkStmt(fn.Body);
                blockStack_.Clear();
                fnScope_ = null;
                reported_ = null;
            });
            table_ = null;
        }

        // ---------------------------------------------------------- ordered walk

        private void Push() { blockStack_.Add(new HashSet<string>()); }
        private void Pop() { blockStack_.RemoveAt(blockStack_.Count - 1); }
        private void Declare(string name)
        {
            if (!string.IsNullOrEmpty(name)) blockStack_[blockStack_.Count - 1].Add(name);
        }

        private bool InBlockStack(string id)
        {
            for (int i = blockStack_.Count - 1; i >= 0; --i)
            {
                if (blockStack_[i].Contains(id)) return true;
            }
            return false;
        }

        private void WalkStmt(Stmt stmt)
        {
            if (stmt == null || diagnostics_.Count >= MaxReports) return;
            CompoundStmt c = stmt as CompoundStmt;
            if (c != null)
            {
                // A synthetic group (`double a, b;` split into DeclStmts, a structured
                // binding's names) declares into the ENCLOSING block — opening a scope
                // here made every later use of those names a bogus out-of-scope report.
                if (c.IsSyntheticGroup)
                {
                    IReadOnlyList<Stmt> grp = c.Body;
                    for (int i = 0; i < grp.Count; ++i) WalkStmt(grp[i]);
                    return;
                }
                Push();
                IReadOnlyList<Stmt> body = c.Body;
                for (int i = 0; i < body.Count; ++i) WalkStmt(body[i]);
                Pop();
                return;
            }
            DeclStmt d = stmt as DeclStmt;
            if (d != null)
            {
                // Declare first: `int x = x;` compiles in C++ (the name is in scope
                // in its own initializer), so self-reference must not warn.
                DeclareNames(d.Decl);
                CheckDeclExprs(d.Decl);
                return;
            }
            ExprStmt e = stmt as ExprStmt;
            if (e != null) { CheckExpr(e.Expr); return; }
            IfStmt i2 = stmt as IfStmt;
            if (i2 != null)
            {
                CheckExpr(i2.Cond);
                Push(); WalkStmt(i2.Then); Pop();
                Push(); WalkStmt(i2.Else); Pop();
                return;
            }
            ForStmt f = stmt as ForStmt;
            if (f != null)
            {
                Push();                    // for-init declarations scope to the loop
                WalkStmt(f.Init);
                CheckExpr(f.Cond);
                CheckExpr(f.Inc);
                WalkStmt(f.Body);
                Pop();
                return;
            }
            WhileStmt w = stmt as WhileStmt;
            if (w != null)
            {
                CheckExpr(w.Cond);
                Push(); WalkStmt(w.Body); Pop();
                return;
            }
            DoStmt dw = stmt as DoStmt;
            if (dw != null)
            {
                Push(); WalkStmt(dw.Body); Pop();
                CheckExpr(dw.Cond);
                return;
            }
            SwitchStmt sw = stmt as SwitchStmt;
            if (sw != null)
            {
                CheckExpr(sw.Cond);
                Push(); WalkStmt(sw.Body); Pop();
                return;
            }
            ReturnStmt r = stmt as ReturnStmt;
            if (r != null) { CheckExpr(r.Value); return; }
            // Break/Continue/Empty: no names.
        }

        // Mirror of SymbolTableBuilder.CollectLocals: every decl form it flattens
        // into the function scope must also register here, or the ordered walk
        // would flag a name the flat scope resolves (a tier-2 false positive).
        private void DeclareNames(Decl decl)
        {
            if (decl == null) return;
            VarDecl v = decl as VarDecl;
            if (v != null) { Declare(v.Name); return; }
            RecordDecl rec = decl as RecordDecl;
            if (rec != null) { Declare(rec.Name); return; }
            EnumDecl en = decl as EnumDecl;
            if (en != null)
            {
                Declare(en.Name);
                IReadOnlyList<string> es = en.Enumerators;
                for (int i = 0; i < es.Count; ++i) Declare(es[i]);
                return;
            }
            TypeAliasDecl alias = decl as TypeAliasDecl;
            if (alias != null) { Declare(alias.Name); return; }
            FunctionDecl fn = decl as FunctionDecl;
            if (fn != null) { Declare(fn.Name); return; }
            NamespaceDecl ns = decl as NamespaceDecl;
            if (ns != null)
            {
                // Anonymous multi-declarator wrapper: flatten, like the builder does.
                IReadOnlyList<Decl> ds = ns.Decls;
                for (int i = 0; i < ds.Count; ++i) DeclareNames(ds[i]);
                return;
            }
        }

        private void CheckDeclExprs(Decl decl)
        {
            VarDecl v = decl as VarDecl;
            if (v == null) return;
            CheckExpr(v.Initializer);
            CheckExpr(v.ArraySize);
        }

        // ---------------------------------------------------------- name checking

        private void CheckExpr(Expr expr)
        {
            if (expr == null) return;
            AstWalk.ForEachExprIn(expr, e =>
            {
                if (diagnostics_.Count >= MaxReports) return;
                NameExpr name = e as NameExpr;
                if (name == null) return;
                string id = name.Id;
                if (!ShouldCheck(id, name.Qualified)) return;
                if (reported_.Contains(id)) return;
                if (InBlockStack(id)) return;                       // param or in-scope local
                Scope outer = fnScope_.Parent;
                if (outer != null && outer.Lookup(id) != null) return;   // field/global/type/builtin
                if (table_.FindFunction(id) != null) return;             // namespace-agnostic fallback
                // `using namespace a::b;` anywhere in the unit: the namespace's members
                // are legitimately usable unqualified.
                for (int oi = 0; oi < table_.OpenedNamespaces.Count; ++oi)
                {
                    Scope nsScope = table_.FindNamespaceScope(table_.OpenedNamespaces[oi]);
                    if (nsScope != null && nsScope.LookupLocal(id) != null) return;
                }
                // Inherited members: climb any enclosing Class scope's base-class chain
                // (`Square : Rectangle` methods may use Rectangle's `width` unqualified).
                if (ResolvesInBaseClasses(id)) return;
                reported_.Add(id);
                string message = fnScope_.LookupLocal(id) != null
                    ? "'" + id + "' is used before its declaration or outside its declaring block"
                    : "Use of undefined name '" + id + "'";
                diagnostics_.Add(new ParseDiagnostic(CudaDiagnosticCodes.SemUndefinedName,
                    strict_ ? ParseSeverity.Error : ParseSeverity.Warning, name.Span, message));
            });
        }

        // Unqualified names that unexpanded SYSTEM headers are known to provide — the C
        // library surface plus the handful of framework identifiers the fixtures'
        // idioms use unqualified (Catch2's Approx). The full fixture sweep is the
        // false-positive oracle for this list: every name it flags on valid fixture
        // code either exposes a real modeling gap or belongs here.
        private static readonly HashSet<string> KnownExternalNames = new HashSet<string>(System.StringComparer.Ordinal)
        {
            // <cstdio> / <cstdlib> / <cstring>
            "printf", "fprintf", "sprintf", "snprintf", "scanf", "sscanf", "puts", "putchar",
            "getchar", "fgets", "fopen", "fclose", "fread", "fwrite", "stdout", "stderr", "stdin",
            "malloc", "calloc", "realloc", "free", "exit", "abort", "atexit", "getenv",
            "atoi", "atof", "atol", "strtol", "strtoul", "strtod", "rand", "srand", "qsort", "bsearch",
            "memcpy", "memset", "memcmp", "memmove", "strlen", "strcmp", "strncmp", "strcpy",
            "strncpy", "strcat", "strncat", "strchr", "strrchr", "strstr",
            // <cmath>
            "abs", "labs", "fabs", "fabsf", "sqrt", "sqrtf", "cbrt", "pow", "powf",
            "exp", "expf", "exp2", "log", "logf", "log2", "log10", "sin", "cos", "tan",
            "sinf", "cosf", "tanf", "asin", "acos", "atan", "atan2", "atan2f",
            "sinh", "cosh", "tanh", "floor", "floorf", "ceil", "ceilf", "round", "roundf",
            "trunc", "fmod", "fmodf", "fmin", "fmax", "fminf", "fmaxf", "hypot",
            "rint", "rintf", "nearbyint", "remainder", "copysign", "signbit",
            "frexp", "ldexp", "modf",
            "isnan", "isinf", "isfinite", "nan", "nanf", "erf", "erfc", "tgamma", "lgamma",
            // <ctime> / <cassert> / misc
            "time", "clock", "assert", "main",
            "llabs", "llrint", "lrint", "lround", "llround",
            "localtime", "localtime_s", "localtime_r", "gmtime", "strftime", "mktime", "difftime",
            // Catch2 (macros are ALL_CAPS-skipped; Approx is the one mixed-case entry point)
            "Approx",
        };

        // True when any Class scope on the function's parent chain inherits `id` from a
        // base class (transitively, cycle-guarded).
        private bool ResolvesInBaseClasses(string id)
        {
            for (Scope s = fnScope_; s != null; s = s.Parent)
            {
                if (s.Kind != BlockKind.Class || s.BaseClassNames.Count == 0) continue;
                var visited = new HashSet<string>(System.StringComparer.Ordinal);
                if (BaseChainHas(s, id, visited)) return true;
            }
            return false;
        }

        private bool BaseChainHas(Scope classScope, string id, HashSet<string> visited)
        {
            for (int i = 0; i < classScope.BaseClassNames.Count; ++i)
            {
                string baseName = classScope.BaseClassNames[i];
                if (!visited.Add(baseName)) continue;
                Scope baseScope = table_.FindClassScope(baseName);
                if (baseScope == null) return true;   // unknown base — inherited set unprovable
                if (baseScope.LookupLocal(id) != null) return true;
                if (BaseChainHas(baseScope, id, visited)) return true;
            }
            return false;
        }

        // The categories the front end can actually adjudicate. Everything else is
        // skipped rather than guessed at (a wrong CU2101 is worse than a missing one).
        internal static bool ShouldCheck(string id, string qualified)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (!string.IsNullOrEmpty(qualified) && qualified.Contains("::")) return false;
            if (id.StartsWith("__", System.StringComparison.Ordinal)) return false;
            if (KnownExternalNames.Contains(id)) return false;
            // The CUDA runtime API surface (cudaMalloc, cudaFuncGetAttributes, ...) is far
            // larger than the seeded builtin set — the prefix is unambiguous.
            if (id.StartsWith("cuda", System.StringComparison.Ordinal)) return false;
            switch (id)
            {
                case "true":
                case "false":
                case "nullptr":
                case "this":
                case "NULL":
                case "std":
                case "typeid":
                case "sizeof":
                case "alignof":
                case "decltype":
                case "static_assert":
                    return false;
            }
            // Macro naming convention: ALL_CAPS (with digits/underscores) —
            // function-like macros are not expanded, so these are unprovable.
            bool sawLetter = false;
            bool allCaps = true;
            for (int i = 0; i < id.Length; ++i)
            {
                char c = id[i];
                if (char.IsLower(c)) { allCaps = false; break; }
                if (char.IsUpper(c)) sawLetter = true;
            }
            if (allCaps && sawLetter && id.Length >= 2) return false;
            return true;
        }
    }
}
