namespace HelixPythonNamespace.Linker
{
    using System.Collections.Generic;
    using System.Threading;
    using HelixPythonNamespace.Catalog;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.Python.Text;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Top-level linker entry point. Consumes per-file compile results plus optional
    /// catalogs and produces a fully-populated <see cref="LinkResult"/>: module-table
    /// extraction, star-import fixpoint, import resolution, cycle detection, reference
    /// binding, and catalog validation. Port of the C++ <c>PythonLinker</c>.
    /// </summary>
    public static class PythonLinker
    {
        public static LinkResult Link(IReadOnlyList<CompileFileResult> files,
            PythonCatalog projectCatalog = null, PythonCatalog systemCatalog = null,
            CancellationToken cancel = default(CancellationToken))
        {
            LinkResult outp = new LinkResult();

            foreach (CompileFileResult f in files)
            {
                if (cancel.IsCancellationRequested) return outp;
                if (f.AstModule == null) continue;
                ModuleSummary summary = ModuleTableBuilder.Build(f.AstModule, f.ModuleFqn, f.FilePath);
                outp.Modules[f.ModuleFqn] = summary;
            }
            if (cancel.IsCancellationRequested) return outp;

            RunStarImportFixpoint(outp.Modules, outp.Diagnostics, cancel);
            if (cancel.IsCancellationRequested) return outp;

            ImportResolver.Output resolved = ImportResolver.Resolve(outp.Modules, projectCatalog, systemCatalog, cancel);
            foreach (KeyValuePair<AstNode, BindingTarget> kv in resolved.Resolution)
            {
                outp.ImportResolution[kv.Key] = kv.Value;
            }
            outp.Diagnostics.AddRange(resolved.Diagnostics);
            if (cancel.IsCancellationRequested) return outp;

            ModuleGraph graph = ModuleGraph.Build(outp.Modules);
            EmitCycleDiagnostics(graph, outp.Modules, outp.Diagnostics);
            if (cancel.IsCancellationRequested) return outp;

            EmitUnusedReExports(outp.Modules, outp.Diagnostics);
            if (cancel.IsCancellationRequested) return outp;

            ReferenceBinder.Output binderOut = ReferenceBinder.Bind(outp.Modules, outp.ImportResolution, cancel);
            foreach (KeyValuePair<AstNode, BindingTarget> kv in binderOut.Bindings) outp.BindingMap[kv.Key] = kv.Value;
            foreach (KeyValuePair<AstNode, string> kv in binderOut.Owner) outp.NodeOwner[kv.Key] = kv.Value;
            outp.SetReferences(binderOut.References);
            if (cancel.IsCancellationRequested) return outp;

            List<LinkDiagnostic> catDiags = CatalogValidator.Validate(outp.Modules, outp.BindingMap, outp.NodeOwner, projectCatalog, systemCatalog);
            outp.Diagnostics.AddRange(catDiags);

            return outp;
        }

        private static List<string> ExportedNames(ModuleSummary mod)
        {
            List<string> outList = new List<string>();
            if (mod.HasExplicitAll)
            {
                outList.AddRange(mod.All);
                return outList;
            }
            foreach (KeyValuePair<string, ModuleBinding> kv in mod.Bindings)
            {
                if (!string.IsNullOrEmpty(kv.Key) && kv.Key[0] != '_') outList.Add(kv.Key);
            }
            return outList;
        }

        private static void EmitCycleDiagnostics(ModuleGraph g, Dictionary<string, ModuleSummary> modules, List<LinkDiagnostic> diagnostics)
        {
            foreach (List<string> scc in g.StronglyConnectedComponents())
            {
                if (scc.Count < 2) continue; // self-loops fine
                string members = string.Join(", ", scc);
                ModuleSummary anchor = null;
                foreach (string fqn in scc)
                {
                    ModuleSummary s;
                    if (modules.TryGetValue(fqn, out s)) { anchor = s; break; }
                }
                string path = anchor != null ? anchor.FilePath : "";
                diagnostics.Add(new LinkDiagnostic(path, LinkDiagnosticCodes.LinkCircularImport,
                    ParseSeverity.Warning, default(SourceSpan),
                    "circular import among modules: " + members));
            }
        }

        private static void RunStarImportFixpoint(Dictionary<string, ModuleSummary> modules, List<LinkDiagnostic> diagnostics, CancellationToken cancel)
        {
            const int MaxRounds = 3;
            for (int round = 0; round < MaxRounds; round++)
            {
                if (cancel.IsCancellationRequested) return;
                bool changed = false;
                foreach (KeyValuePair<string, ModuleSummary> kv in modules)
                {
                    if (cancel.IsCancellationRequested) return;
                    ModuleSummary importer = kv.Value;
                    if (importer.StarImportSources.Count == 0) continue;

                    Dictionary<string, List<string>> nameSources = new Dictionary<string, List<string>>();
                    foreach (string srcFqn in importer.StarImportSources)
                    {
                        ModuleSummary src;
                        if (!modules.TryGetValue(srcFqn, out src)) continue; // external; defer
                        foreach (string name in ExportedNames(src))
                        {
                            List<string> srcs;
                            if (!nameSources.TryGetValue(name, out srcs)) { srcs = new List<string>(); nameSources[name] = srcs; }
                            srcs.Add(srcFqn);
                        }
                    }

                    foreach (KeyValuePair<string, List<string>> ns in nameSources)
                    {
                        string name = ns.Key;
                        List<string> sources = ns.Value;
                        if (round == 0)
                        {
                            if (WellKnownBuiltins.Contains(name))
                            {
                                diagnostics.Add(new LinkDiagnostic(importer.FilePath, LinkDiagnosticCodes.LinkShadowedBuiltin,
                                    ParseSeverity.Warning, default(SourceSpan),
                                    "star-import of '" + name + "' from '" + sources[0] + "' shadows a builtin"));
                            }
                            ModuleBinding existing;
                            if (importer.Bindings.TryGetValue(name, out existing) && existing.Kind != BindingKind.StarImport)
                            {
                                diagnostics.Add(new LinkDiagnostic(importer.FilePath, LinkDiagnosticCodes.LinkStarImportShadowsLocal,
                                    ParseSeverity.Warning, existing.DefSpan,
                                    "star-import of '" + name + "' from '" + sources[0] + "' shadows a local definition"));
                            }
                            if (sources.Count >= 2)
                            {
                                diagnostics.Add(new LinkDiagnostic(importer.FilePath, LinkDiagnosticCodes.LinkAmbiguousStarImport,
                                    ParseSeverity.Warning, default(SourceSpan),
                                    "name '" + name + "' is exported by multiple star-import sources: " + string.Join(", ", sources)));
                            }
                        }
                        if (!importer.Bindings.ContainsKey(name))
                        {
                            importer.Bindings[name] = new ModuleBinding { Name = name, Kind = BindingKind.StarImport, IsReExport = true };
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }
        }

        private static void EmitUnusedReExports(Dictionary<string, ModuleSummary> modules, List<LinkDiagnostic> diagnostics)
        {
            foreach (KeyValuePair<string, ModuleSummary> kv in modules)
            {
                ModuleSummary mod = kv.Value;
                if (!mod.HasExplicitAll) continue;
                foreach (string name in mod.All)
                {
                    if (mod.Bindings.ContainsKey(name)) continue;
                    diagnostics.Add(new LinkDiagnostic(mod.FilePath, LinkDiagnosticCodes.LinkUnusedReExport,
                        ParseSeverity.Warning, default(SourceSpan),
                        "name '" + name + "' listed in __all__ is not defined or imported"));
                }
            }
        }

        private static readonly HashSet<string> WellKnownBuiltins = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "abs", "all", "any", "ascii", "bin", "bool", "bytearray", "bytes",
            "callable", "chr", "classmethod", "compile", "complex", "delattr",
            "dict", "dir", "divmod", "enumerate", "eval", "exec", "filter",
            "float", "format", "frozenset", "getattr", "globals", "hasattr",
            "hash", "help", "hex", "id", "input", "int", "isinstance", "issubclass",
            "iter", "len", "list", "locals", "map", "max", "memoryview", "min",
            "next", "object", "oct", "open", "ord", "pow", "print", "property",
            "range", "repr", "reversed", "round", "set", "setattr", "slice",
            "sorted", "staticmethod", "str", "sum", "super", "tuple", "type",
            "vars", "zip",
        };
    }
}
