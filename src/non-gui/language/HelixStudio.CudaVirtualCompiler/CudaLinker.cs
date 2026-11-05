namespace HelixPythonNamespace.CudaVirtualCompiler
{
    using System.Collections.Generic;
    using HelixPythonNamespace.CudaVirtualCompiler.Analyze;
    using HelixPythonNamespace.CudaVirtualCompiler.Ast;
    using HelixPythonNamespace.Python.Diagnostics;

    /// <summary>
    /// Symbol-level link step over per-TU compile results (SRS §10 — "link" was
    /// previously textual concatenation; this reasons across translation units).
    /// Two checks, both provable from the per-TU symbol tables alone:
    ///
    /// 1. Cross-TU redefinition: the same function name DEFINED (with a body) in
    ///    more than one TU. Reported as a Warning, not an Error — the front end
    ///    does not model linkage, and `static` (internal-linkage) definitions in
    ///    two TUs are legal C++ this pass cannot distinguish.
    ///
    /// 2. Cross-TU kernel launches: a &lt;&lt;&lt;&gt;&gt;&gt; whose callee is not defined in the
    ///    launching TU but IS defined in a sibling TU — the case the per-TU
    ///    LaunchAnalyzer explicitly skips. Validates kernel-ness (CU3001) and
    ///    argument count (CU3030) against the found definition.
    ///
    /// Callees found in no TU stay silent: they may live in headers this front
    /// end deliberately does not parse.
    /// </summary>
    public static class CudaLinker
    {
        private sealed class DefSite
        {
            public string FilePath;
            public Symtable.Symbol Symbol;
        }

        public static List<CudaCompileDiagnostic> Link(IReadOnlyList<CompileFileResult> results)
        {
            var diagnostics = new List<CudaCompileDiagnostic>();
            if (results == null) return diagnostics;

            // Global index of user function DEFINITIONS (prototype-only decls are
            // not link-relevant; seeded builtins carry no FuncDecl and drop out).
            var defs = new Dictionary<string, List<DefSite>>();
            for (int i = 0; i < results.Count; ++i)
            {
                CompileFileResult r = results[i];
                if (r == null || r.Result == null || r.Result.SymbolTable == null ||
                    r.Result.Unit == null) continue;
                AstWalk.ForEachFunction(r.Result.Unit, delegate (FunctionDecl fn)
                {
                    if (fn == null || !fn.HasBody || string.IsNullOrEmpty(fn.Name)) return;
                    Symtable.Symbol sym = FindSymbolFor(r.Result.SymbolTable, fn);
                    if (sym == null) return;
                    List<DefSite> bucket;
                    if (!defs.TryGetValue(fn.Name, out bucket))
                    {
                        bucket = new List<DefSite>();
                        defs[fn.Name] = bucket;
                    }
                    bucket.Add(new DefSite { FilePath = r.FilePath, Symbol = sym });
                });
            }

            // 1) Cross-TU redefinitions.
            foreach (KeyValuePair<string, List<DefSite>> entry in defs)
            {
                List<DefSite> sites = entry.Value;
                if (sites.Count < 2) continue;
                var tuPaths = new HashSet<string>();
                for (int i = 0; i < sites.Count; ++i) tuPaths.Add(sites[i].FilePath ?? string.Empty);
                if (tuPaths.Count < 2) continue;   // overloads inside one TU are not a link issue
                for (int i = 1; i < sites.Count; ++i)
                {
                    if (string.Equals(sites[i].FilePath, sites[0].FilePath,
                            System.StringComparison.OrdinalIgnoreCase)) continue;
                    diagnostics.Add(new CudaCompileDiagnostic(sites[i].FilePath,
                        CudaDiagnosticCodes.SemDuplicateDefinition, ParseSeverity.Warning,
                        sites[i].Symbol.DefSpan,
                        "'" + entry.Key + "' is also defined in '" + sites[0].FilePath +
                        "' — one definition rule violation unless both are static"));
                }
            }

            // 2) Cross-TU kernel launches.
            for (int i = 0; i < results.Count; ++i)
            {
                CompileFileResult r = results[i];
                if (r == null || r.Result == null || r.Result.Unit == null ||
                    r.Result.SymbolTable == null) continue;
                CompileFileResult launchingTu = r;
                AstWalk.ForEachFunction(r.Result.Unit, delegate (FunctionDecl fn)
                {
                    if (fn == null || !fn.HasBody) return;
                    AstWalk.ForEachExpr(fn.Body, delegate (Expr e)
                    {
                        KernelLaunchExpr launch = e as KernelLaunchExpr;
                        if (launch == null) return;
                        NameExpr callee = launch.Callee as NameExpr;
                        if (callee == null || string.IsNullOrEmpty(callee.Id)) return;

                        // Defined in the launching TU: the per-TU LaunchAnalyzer
                        // already validated it — don't double-report.
                        if (DefinedInTable(launchingTu.Result.SymbolTable, callee.Id)) return;

                        List<DefSite> sites;
                        if (!defs.TryGetValue(callee.Id, out sites)) return;   // header-land

                        DefSite kernelSite = null;
                        for (int s = 0; s < sites.Count; ++s)
                        {
                            if (sites[s].Symbol.IsKernel) { kernelSite = sites[s]; break; }
                        }
                        if (kernelSite == null)
                        {
                            diagnostics.Add(new CudaCompileDiagnostic(launchingTu.FilePath,
                                CudaDiagnosticCodes.LaunchOfNonKernel, ParseSeverity.Error,
                                launch.Span,
                                "'" + callee.Id + "' is launched with <<<>>> but its definition in '" +
                                sites[0].FilePath + "' is not a __global__ kernel"));
                            return;
                        }
                        FunctionDecl def = kernelSite.Symbol.FuncDecl;
                        if (def != null && def.Params.Count != launch.Args.Count)
                        {
                            diagnostics.Add(new CudaCompileDiagnostic(launchingTu.FilePath,
                                CudaDiagnosticCodes.LaunchArgCountMismatch, ParseSeverity.Error,
                                launch.Span,
                                "kernel '" + callee.Id + "' (defined in '" + kernelSite.FilePath +
                                "') expects " + def.Params.Count + " argument(s) but is launched with " +
                                launch.Args.Count));
                        }
                    });
                });
            }
            return diagnostics;
        }

        private static Symtable.Symbol FindSymbolFor(Symtable.SymbolTable table, FunctionDecl fn)
        {
            List<Symtable.Symbol> candidates = table.FindFunctions(fn.Name);
            for (int i = 0; i < candidates.Count; ++i)
            {
                if (ReferenceEquals(candidates[i].FuncDecl, fn)) return candidates[i];
            }
            return null;
        }

        // A body-bearing definition of this name in the given TU's table.
        private static bool DefinedInTable(Symtable.SymbolTable table, string name)
        {
            List<Symtable.Symbol> candidates = table.FindFunctions(name);
            for (int i = 0; i < candidates.Count; ++i)
            {
                Symtable.Symbol s = candidates[i];
                if (s.FuncDecl != null && s.FuncDecl.HasBody) return true;
            }
            return false;
        }
    }
}
