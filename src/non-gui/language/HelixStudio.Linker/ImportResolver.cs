namespace HelixPythonNamespace.Linker
{
    using System.Collections.Generic;
    using System.Threading;
    using HelixPythonNamespace.Catalog;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Resolves every Import / ImportFrom node in a module table. Solution-first; an
    /// optional project / system catalog is consulted as a fallback. Port of the C++
    /// <c>ImportResolver</c> (including CPython relative-import semantics).
    /// </summary>
    public sealed class ImportResolver
    {
        public sealed class Output
        {
            public Dictionary<AstNode, BindingTarget> Resolution = new Dictionary<AstNode, BindingTarget>();
            public List<LinkDiagnostic> Diagnostics = new List<LinkDiagnostic>();
        }

        private readonly Dictionary<string, ModuleSummary> _modules;
        private readonly PythonCatalog _projectCatalog;
        private readonly PythonCatalog _systemCatalog;
        private readonly Output _out = new Output();

        private ImportResolver(Dictionary<string, ModuleSummary> modules, PythonCatalog projectCatalog, PythonCatalog systemCatalog)
        {
            _modules = modules;
            _projectCatalog = projectCatalog;
            _systemCatalog = systemCatalog;
        }

        public static Output Resolve(Dictionary<string, ModuleSummary> modules,
            PythonCatalog projectCatalog, PythonCatalog systemCatalog, CancellationToken cancel = default(CancellationToken))
        {
            ImportResolver r = new ImportResolver(modules, projectCatalog, systemCatalog);
            foreach (KeyValuePair<string, ModuleSummary> kv in modules)
            {
                if (cancel.IsCancellationRequested) return r._out;
                ModuleSummary summary = kv.Value;
                if (summary.AstModule == null) continue;
                r.WalkBody(summary.AstModule.Body, kv.Key, summary);
            }
            return r._out;
        }

        /// <summary>CPython importlib._resolve_name semantics for relative imports.</summary>
        public static string ResolveRelative(string importerFqn, string moduleSpec, int level, bool importerIsPackage, out bool escapesPackage)
        {
            escapesPackage = false;
            if (level <= 0)
            {
                return moduleSpec;
            }
            string pkg = importerFqn;
            if (!importerIsPackage)
            {
                int lastDot = pkg.LastIndexOf('.');
                pkg = lastDot < 0 ? "" : pkg.Substring(0, lastDot);
            }
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(pkg))
            {
                parts.AddRange(pkg.Split('.'));
            }
            if (parts.Count < level)
            {
                escapesPackage = true;
                return "";
            }
            for (int i = 0; i < level - 1 && parts.Count > 0; i++)
            {
                parts.RemoveAt(parts.Count - 1);
            }
            string baseFqn = string.Join(".", parts);
            if (string.IsNullOrEmpty(moduleSpec)) return baseFqn;
            if (string.IsNullOrEmpty(baseFqn)) return moduleSpec;
            return baseFqn + "." + moduleSpec;
        }

        // ---- resolution helpers ----

        private static bool IsInitPyFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string baseName = System.IO.Path.GetFileName(filePath);
            return baseName == "__init__.py";
        }

        private ModuleSummary FindModule(string fqn)
        {
            ModuleSummary m;
            if (_modules.TryGetValue(fqn, out m)) return m;
            return FindModuleByPathSuffix(fqn);
        }

        // Namespace-package (PEP 420) fallback. PythonModuleFqn.Derive builds an FQN by
        // walking the __init__.py chain, so a file under an implicit namespace package (no
        // __init__.py — common in src-layout projects) gets a SHORT FQN: e.g.
        // sample_project/verify/lib/sample_project/data/dataset.py, where sample_project/ has no __init__.py, derives
        // to 'data.dataset', not 'sample_project.data.dataset'. An `import sample_project.data.dataset` then
        // failed to resolve -> phantom PY7001, even though the file EXISTS. Resolve by
        // matching the import's dotted path against module FILE PATHS: the module resolves if
        // some workspace file's path ends with '<a/b/c>.py' or '<a/b/c>/__init__.py'. This
        // approximates 'importable from a src root on sys.path' without needing to know the
        // exact root. Conservative — a full dotted path rarely suffix-matches the wrong file.
        private ModuleSummary FindModuleByPathSuffix(string fqn)
        {
            // Handles BOTH dotted namespace imports (sample_project.data.dataset) and BARE imports of
            // files reached via a sys.path tweak (import deepbbq2_input, where the file lives
            // at .../sample_project/tools/deepbbq2_input.py). Both resolve iff some workspace file's
            // path ends with the corresponding '/<segments>.py' or '.../__init__.py'.
            if (string.IsNullOrEmpty(fqn)) return null;
            string rel = fqn.Replace('.', '/');
            string tailModule = "/" + rel + ".py";
            string tailPkg = "/" + rel + "/__init__.py";
            foreach (ModuleSummary s in _modules.Values)
            {
                if (s == null || string.IsNullOrEmpty(s.FilePath)) continue;
                string p = s.FilePath.Replace('\\', '/');
                if (p.EndsWith(tailModule, System.StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(tailPkg, System.StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            return null;
        }

        private static bool CatalogHasModule(PythonCatalog cat, string fqn)
        {
            if (cat == null) return false;
            if (cat.Lookup(fqn) != null) return true;
            foreach (CatalogSymbol e in cat.Entries)
            {
                if (e.ModuleFqn == fqn) return true;
                if (e.FullyQualifiedName() == fqn) return true;
            }
            return false;
        }

        private static bool CatalogHasMember(PythonCatalog cat, string moduleFqn, string member)
        {
            if (cat == null) return false;
            if (cat.ResolveImport(moduleFqn, member) != null) return true;
            return CatalogHasModule(cat, moduleFqn + "." + member);
        }

        private bool IsStdlibTopLevel(string fqn)
        {
            int dot = fqn.IndexOf('.');
            string top = dot < 0 ? fqn : fqn.Substring(0, dot);
            return AlwaysResolvableModules.Contains(top);
        }

        private bool IsModuleResolvable(string fqn)
        {
            if (IsStdlibTopLevel(fqn)) return true;
            if (FindModule(fqn) != null) return true;
            if (CatalogHasModule(_projectCatalog, fqn)) return true;
            if (CatalogHasModule(_systemCatalog, fqn)) return true;
            return false;
        }

        private BindingTarget ResolveMember(string moduleFqn, string member)
        {
            if (IsStdlibTopLevel(moduleFqn))
            {
                return BindingTarget.CatalogSymbol(moduleFqn, member);
            }
            ModuleSummary mod = FindModule(moduleFqn);
            if (mod != null)
            {
                ModuleBinding b;
                if (mod.Bindings.TryGetValue(member, out b))
                {
                    return BindingTarget.Symbol(moduleFqn, member, b.DefSpan);
                }
                if (IsModuleResolvable(moduleFqn + "." + member))
                {
                    return BindingTarget.Module(moduleFqn + "." + member);
                }
                return BindingTarget.Unresolved(UnresolvedReason.NameNotInModule);
            }
            if (CatalogHasMember(_projectCatalog, moduleFqn, member)) return BindingTarget.CatalogSymbol(moduleFqn, member);
            if (CatalogHasMember(_systemCatalog, moduleFqn, member)) return BindingTarget.CatalogSymbol(moduleFqn, member);
            if (CatalogHasModule(_projectCatalog, moduleFqn) || CatalogHasModule(_systemCatalog, moduleFqn))
            {
                if (IsModuleResolvable(moduleFqn + "." + member)) return BindingTarget.Module(moduleFqn + "." + member);
                return BindingTarget.Unresolved(UnresolvedReason.NameNotInModule);
            }
            return BindingTarget.Unresolved(UnresolvedReason.ModuleNotFound);
        }

        // ---- statement walk ----

        private void WalkBody(IReadOnlyList<Stmt> body, string importerFqn, ModuleSummary summary)
        {
            foreach (Stmt s in body)
            {
                HandleStmt(s, importerFqn, summary);
            }
        }

        private void HandleStmt(Stmt node, string importerFqn, ModuleSummary summary)
        {
            if (node == null) return;

            // Recurse into container bodies first so nested imports are reached.
            if (node is FunctionDef fn) WalkBody(fn.Body, importerFqn, summary);
            else if (node is AsyncFunctionDef afn) WalkBody(afn.Body, importerFqn, summary);
            else if (node is ClassDef cls) WalkBody(cls.Body, importerFqn, summary);
            else if (node is If iff) { WalkBody(iff.Body, importerFqn, summary); WalkBody(iff.OrElse, importerFqn, summary); }
            else if (node is While w) { WalkBody(w.Body, importerFqn, summary); WalkBody(w.OrElse, importerFqn, summary); }
            else if (node is For ff) { WalkBody(ff.Body, importerFqn, summary); WalkBody(ff.OrElse, importerFqn, summary); }
            else if (node is AsyncFor af) { WalkBody(af.Body, importerFqn, summary); WalkBody(af.OrElse, importerFqn, summary); }
            else if (node is With wi) WalkBody(wi.Body, importerFqn, summary);
            else if (node is AsyncWith aw) WalkBody(aw.Body, importerFqn, summary);
            else if (node is Try tr)
            {
                WalkBody(tr.Body, importerFqn, summary);
                foreach (ExceptHandler h in tr.Handlers) WalkBody(h.Body, importerFqn, summary);
                WalkBody(tr.OrElse, importerFqn, summary);
                WalkBody(tr.FinalBody, importerFqn, summary);
            }

            if (node is Import imp)
            {
                foreach (Alias a in imp.Names)
                {
                    if (IsModuleResolvable(a.Name))
                    {
                        _out.Resolution[a] = BindingTarget.Module(a.Name);
                    }
                    else
                    {
                        _out.Resolution[a] = BindingTarget.Unresolved(UnresolvedReason.ModuleNotFound);
                        _out.Diagnostics.Add(new LinkDiagnostic(summary.FilePath,
                            LinkDiagnosticCodes.LinkUnresolvedModule, ParseSeverity.Error, a.Span,
                            "no module named '" + a.Name + "'"));
                    }
                }
                return;
            }
            if (node is ImportFrom imf)
            {
                string targetModuleFqn;
                if (imf.Level > 0)
                {
                    bool escapes;
                    bool importerIsPkg = IsInitPyFile(summary.FilePath);
                    targetModuleFqn = ResolveRelative(importerFqn, imf.Module ?? "", imf.Level, importerIsPkg, out escapes);
                    if (escapes)
                    {
                        _out.Resolution[imf] = BindingTarget.Unresolved(UnresolvedReason.RelativeImportEscapesPackage);
                        _out.Diagnostics.Add(new LinkDiagnostic(summary.FilePath,
                            LinkDiagnosticCodes.LinkRelativeImportEscapesPackage, ParseSeverity.Error, imf.Span,
                            "relative import goes above top-level package"));
                        return;
                    }
                }
                else
                {
                    targetModuleFqn = imf.Module ?? "";
                }
                if (string.IsNullOrEmpty(targetModuleFqn))
                {
                    _out.Resolution[imf] = BindingTarget.Unresolved(UnresolvedReason.ModuleNotFound);
                    return;
                }
                if (!IsModuleResolvable(targetModuleFqn))
                {
                    _out.Resolution[imf] = BindingTarget.Unresolved(UnresolvedReason.ModuleNotFound);
                    _out.Diagnostics.Add(new LinkDiagnostic(summary.FilePath,
                        LinkDiagnosticCodes.LinkUnresolvedModule, ParseSeverity.Error, imf.Span,
                        "no module named '" + targetModuleFqn + "'"));
                    return;
                }
                _out.Resolution[imf] = BindingTarget.Module(targetModuleFqn);
                foreach (Alias a in imf.Names)
                {
                    if (a.Name == "*") continue;
                    BindingTarget bt = ResolveMember(targetModuleFqn, a.Name);
                    _out.Resolution[a] = bt;
                    if (bt.Kind == BindingTargetKind.Unresolved && bt.Reason == UnresolvedReason.NameNotInModule)
                    {
                        _out.Diagnostics.Add(new LinkDiagnostic(summary.FilePath,
                            LinkDiagnosticCodes.LinkUnresolvedImport, ParseSeverity.Error, a.Span,
                            "cannot import name '" + a.Name + "' from '" + targetModuleFqn + "'"));
                    }
                }
            }
        }

        // Top-level stdlib + always-resolvable pseudo-modules. The list itself lives in
        // HelixPythonNamespace.VirtualCompiler.Imports.KnownExternalModules, which the compile-time import
        // checker also consults — two copies would drift, and the linker and the compiler
        // disagreeing about what counts as "external" produces exactly the phantom PY7001 /
        // missing-PY5002 pairs this set exists to prevent.
        private static readonly HashSet<string> AlwaysResolvableModules =
            HelixPythonNamespace.VirtualCompiler.Imports.KnownExternalModules.Names;
    }
}
