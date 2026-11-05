namespace HelixPythonNamespace.CudaVirtualCompiler.Symtable
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;

    /// <summary>
    /// Pass 1: builds the scope tree and symbol set from a translation unit, seeds
    /// the CUDA built-ins, and records each function's body scope (params + locals)
    /// in the node-&gt;scope index. Emits CU2102 for duplicate non-function definitions
    /// in the same scope. Port of the C++
    /// helix::cuda_vcompiler::symtable::SymbolTableBuilder
    /// (cuda_vcompiler/symtable/symbol_table_builder.{h,cpp}).
    /// </summary>
    public sealed class SymbolTableBuilder
    {
        private readonly string filePath_;
        private readonly List<ParseDiagnostic> diagnostics_;
        private SymbolTable table_;
        private bool fromContext_;   // walking a resolved #include's unit (symbols only)

        public SymbolTableBuilder(string filePath)
        {
            filePath_ = filePath ?? string.Empty;
            diagnostics_ = new List<ParseDiagnostic>();
        }

        public IReadOnlyList<ParseDiagnostic> Diagnostics { get { return diagnostics_; } }

        public SymbolTable Build(TranslationUnit unit)
        {
            return Build(null, unit);
        }

        /// <summary>
        /// Build a table for <paramref name="mainUnit"/> with the declarations of
        /// <paramref name="contextUnits"/> (resolved <c>#include</c>s) merged in first.
        /// Context units contribute SYMBOLS only — any diagnostics their walk produces
        /// (e.g. duplicate definitions inside a header) belong to that header, not to the
        /// file being analyzed, and are dropped.
        /// </summary>
        public SymbolTable Build(IReadOnlyList<TranslationUnit> contextUnits, TranslationUnit mainUnit)
        {
            SymbolTable table = new SymbolTable();
            table_ = table;
            Builtins.SeedBuiltins(table);

            if (contextUnits != null)
            {
                fromContext_ = true;
                for (int u = 0; u < contextUnits.Count; ++u)
                {
                    if (contextUnits[u] == null) continue;
                    IReadOnlyList<Decl> hd = contextUnits[u].Decls;
                    for (int i = 0; i < hd.Count; ++i) WalkDecl(hd[i], table.Root, false);
                }
                fromContext_ = false;
                diagnostics_.Clear();
            }

            IReadOnlyList<Decl> decls = mainUnit.Decls;
            for (int i = 0; i < decls.Count; ++i)
            {
                WalkDecl(decls[i], table.Root, false);
            }
            table_ = null;
            return table;
        }

        private void ReportDuplicate(string name, SourceSpan span)
        {
            diagnostics_.Add(new ParseDiagnostic(CudaDiagnosticCodes.SemDuplicateDefinition,
                ParseSeverity.Error, span, "Redefinition of '" + name + "'"));
        }

        private Scope FindOrCreateNamespace(Scope parent, string name)
        {
            IReadOnlyList<Scope> children = parent.Children;
            for (int i = 0; i < children.Count; ++i)
            {
                Scope child = children[i];
                if (child.Kind == BlockKind.Namespace && child.Name == name)
                    return child;
            }
            return parent.AddChild(BlockKind.Namespace, name);
        }

        private void WalkDecl(Decl decl, Scope scope, bool inClass)
        {
            if (decl == null) return;

            FunctionDecl fn = decl as FunctionDecl;
            if (fn != null)
            {
                WalkFunction(fn, scope, inClass);
                return;
            }
            UsingNamespaceDecl und = decl as UsingNamespaceDecl;
            if (und != null)
            {
                if (!string.IsNullOrEmpty(und.Namespace) &&
                    !table_.OpenedNamespaces.Contains(und.Namespace))
                    table_.OpenedNamespaces.Add(und.Namespace);
                return;
            }
            VarDecl var = decl as VarDecl;
            if (var != null)
            {
                // Duplicate detection (non-function): a same-named non-function symbol
                // already in this scope is a redefinition — unless the existing one came
                // from a resolved #include, in which case the main file is merely
                // REDECLARING it (forward declarations are legal).
                if (!fromContext_)
                {
                    List<Symbol> existing = scope.LookupAllLocal(var.Name);
                    for (int i = 0; i < existing.Count; ++i)
                    {
                        if (existing[i].Kind != SymbolKind.Function && !existing[i].FromContext)
                        {
                            ReportDuplicate(var.Name, var.Span);
                            break;
                        }
                    }
                }
                Symbol s = new Symbol();
                s.Name = var.Name;
                s.Kind = inClass ? SymbolKind.Field : SymbolKind.Variable;
                s.Type = var.Type != null ? var.Type.Clone() : new CxxType();
                s.Storage = var.Storage;
                s.DefSpan = var.Span;
                s.FromContext = fromContext_;
                scope.Add(s);
                return;
            }
            RecordDecl rec = decl as RecordDecl;
            if (rec != null)
            {
                if (!string.IsNullOrEmpty(rec.Name))
                {
                    Symbol s = new Symbol();
                    s.Name = rec.Name;
                    s.Kind = SymbolKind.Type;
                    s.DefSpan = rec.Span;
                    scope.Add(s);
                }
                Scope classScope = scope.AddChild(BlockKind.Class, rec.Name);
                // Record the bases (last segment of a qualified name) so inherited members
                // resolve — `Square : public Rectangle { double side() { return width; } }`.
                IReadOnlyList<BaseClassRef> bases = rec.BaseClasses;
                for (int b = 0; bases != null && b < bases.Count; ++b)
                {
                    string bn = bases[b] != null ? bases[b].Name : null;
                    if (string.IsNullOrEmpty(bn)) continue;
                    int lastSep = bn.LastIndexOf("::", System.StringComparison.Ordinal);
                    if (lastSep >= 0) bn = bn.Substring(lastSep + 2);
                    // A template base written `Step<T>` keeps just the template name.
                    int lt = bn.IndexOf('<');
                    if (lt > 0) bn = bn.Substring(0, lt);
                    if (bn.Length > 0 && !classScope.BaseClassNames.Contains(bn))
                        classScope.BaseClassNames.Add(bn);
                }
                table_.MapNode(rec, classScope);
                IReadOnlyList<Decl> members = rec.Members;
                for (int i = 0; i < members.Count; ++i)
                {
                    WalkDecl(members[i], classScope, true);
                }
                return;
            }
            EnumDecl en = decl as EnumDecl;
            if (en != null)
            {
                if (!string.IsNullOrEmpty(en.Name))
                {
                    Symbol s = new Symbol();
                    s.Name = en.Name;
                    s.Kind = SymbolKind.Type;
                    s.DefSpan = en.Span;
                    scope.Add(s);
                }
                IReadOnlyList<string> enumerators = en.Enumerators;
                for (int i = 0; i < enumerators.Count; ++i)
                {
                    Symbol c = new Symbol();
                    c.Name = enumerators[i];
                    c.Kind = SymbolKind.EnumConstant;
                    c.Type = new CxxType("int");
                    c.DefSpan = en.Span;
                    scope.Add(c);
                }
                return;
            }
            NamespaceDecl ns = decl as NamespaceDecl;
            if (ns != null)
            {
                // Anonymous wrapper (extern "C" / multi-declarator group): flatten.
                Scope target = string.IsNullOrEmpty(ns.Name) ? scope
                                                             : FindOrCreateNamespace(scope, ns.Name);
                if (!string.IsNullOrEmpty(ns.Name))
                {
                    // Register the namespace name once.
                    if (scope.LookupLocal(ns.Name) == null)
                    {
                        Symbol s = new Symbol();
                        s.Name = ns.Name;
                        s.Kind = SymbolKind.Namespace;
                        s.DefSpan = ns.Span;
                        scope.Add(s);
                    }
                }
                IReadOnlyList<Decl> nsDecls = ns.Decls;
                for (int i = 0; i < nsDecls.Count; ++i) WalkDecl(nsDecls[i], target, inClass);
                return;
            }
            TemplateDecl tmpl = decl as TemplateDecl;
            if (tmpl != null)
            {
                // Template parameters are opaque; analyze the inner entity directly.
                WalkDecl(tmpl.Inner, scope, inClass);
                return;
            }
            TypeAliasDecl alias = decl as TypeAliasDecl;
            if (alias != null)
            {
                if (!string.IsNullOrEmpty(alias.Name) && scope.LookupLocal(alias.Name) == null)
                {
                    Symbol s = new Symbol();
                    s.Name = alias.Name;
                    s.Kind = SymbolKind.Type;
                    s.Type = alias.Aliased != null ? alias.Aliased.Clone() : new CxxType();
                    s.DefSpan = alias.Span;
                    scope.Add(s);
                }
                return;
            }
        }

        private void WalkFunction(FunctionDecl fn, Scope scope, bool inClass)
        {
            Symbol s = new Symbol();
            s.Name = fn.Name;
            s.Kind = SymbolKind.Function;
            s.Type = fn.ReturnType != null ? fn.ReturnType.Clone() : new CxxType();
            s.Space = fn.Space;
            s.IsKernel = fn.IsKernel;
            s.FuncDecl = fn;
            s.DefSpan = fn.Span;
            Symbol stored = scope.Add(s);
            table_.IndexFunction(stored);

            if (!fn.HasBody) return;

            Scope fnScope = scope.AddChild(BlockKind.Function, fn.Name);
            table_.MapNode(fn, fnScope);
            IReadOnlyList<Parameter> parameters = fn.Params;
            for (int i = 0; i < parameters.Count; ++i)
            {
                Parameter p = parameters[i];
                if (string.IsNullOrEmpty(p.Name)) continue;
                Symbol ps = new Symbol();
                ps.Name = p.Name;
                ps.Kind = SymbolKind.Parameter;
                ps.Type = p.Type != null ? p.Type.Clone() : new CxxType();
                ps.DefSpan = p.Span;
                fnScope.Add(ps);
            }
            CollectLocals(fn.Body, fnScope);
        }

        // Flatten local declarations of a function body into the function scope. We do
        // not model block-level shadowing — the CUDA analyses don't depend on it, and a
        // flat function scope keeps callee/var lookups simple.
        private void CollectLocals(Stmt stmt, Scope funcScope)
        {
            if (stmt == null) return;
            CompoundStmt c = stmt as CompoundStmt;
            if (c != null)
            {
                IReadOnlyList<Stmt> body = c.Body;
                for (int i = 0; i < body.Count; ++i) CollectLocals(body[i], funcScope);
                return;
            }
            DeclStmt d = stmt as DeclStmt;
            if (d != null)
            {
                VarDecl v = d.Decl as VarDecl;
                if (v != null)
                {
                    Symbol s = new Symbol();
                    s.Name = v.Name;
                    s.Kind = SymbolKind.Variable;
                    s.Type = v.Type != null ? v.Type.Clone() : new CxxType();
                    s.Storage = v.Storage;
                    s.DefSpan = v.Span;
                    funcScope.Add(s);
                }
                else
                {
                    WalkDecl(d.Decl, funcScope, false);
                }
                return;
            }
            IfStmt i2 = stmt as IfStmt;
            if (i2 != null)
            {
                CollectLocals(i2.Then, funcScope);
                CollectLocals(i2.Else, funcScope);
                return;
            }
            ForStmt f = stmt as ForStmt;
            if (f != null)
            {
                CollectLocals(f.Init, funcScope);
                CollectLocals(f.Body, funcScope);
                return;
            }
            WhileStmt w = stmt as WhileStmt;
            if (w != null)
            {
                CollectLocals(w.Body, funcScope);
                return;
            }
            DoStmt dw = stmt as DoStmt;
            if (dw != null)
            {
                CollectLocals(dw.Body, funcScope);
                return;
            }
            SwitchStmt sw = stmt as SwitchStmt;
            if (sw != null)
            {
                CollectLocals(sw.Body, funcScope);
                return;
            }
        }
    }
}
