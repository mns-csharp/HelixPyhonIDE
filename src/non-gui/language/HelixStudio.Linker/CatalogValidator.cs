namespace HelixPythonNamespace.Linker
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Catalog;
    using HelixPythonNamespace.Python.Diagnostics;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Revisits every ResolvedCatalogSymbol in a binding map and validates the
    /// (moduleFqn, name) pair against the catalogs. A miss against a catalogued module
    /// emits PY7003 and downgrades the binding to Unresolved. Port of the C++
    /// <c>CatalogValidator</c>.
    /// </summary>
    public static class CatalogValidator
    {
        public static List<LinkDiagnostic> Validate(
            Dictionary<string, ModuleSummary> modules,
            Dictionary<AstNode, BindingTarget> bindings,
            Dictionary<AstNode, string> nodeOwner,
            PythonCatalog projectCatalog,
            PythonCatalog systemCatalog)
        {
            List<LinkDiagnostic> diags = new List<LinkDiagnostic>();
            if (projectCatalog == null && systemCatalog == null) return diags;

            // Snapshot the catalog-symbol nodes first: we mutate `bindings` values below,
            // and a Dictionary can't be modified during its own enumeration.
            List<AstNode> catalogNodes = new List<AstNode>();
            foreach (KeyValuePair<AstNode, BindingTarget> kv in bindings)
            {
                if (kv.Value.Kind == BindingTargetKind.ResolvedCatalogSymbol)
                {
                    catalogNodes.Add(kv.Key);
                }
            }

            foreach (AstNode node in catalogNodes)
            {
                BindingTarget target = bindings[node];
                bool ok = CatalogHasMember(projectCatalog, target.ModuleFqn, target.Name) ||
                          CatalogHasMember(systemCatalog, target.ModuleFqn, target.Name);
                if (ok) continue;
                bool moduleIsCatalogued = CatalogHasModule(projectCatalog, target.ModuleFqn) ||
                                          CatalogHasModule(systemCatalog, target.ModuleFqn);
                if (!moduleIsCatalogued) continue;

                string filePath = "";
                string owner;
                if (nodeOwner.TryGetValue(node, out owner))
                {
                    ModuleSummary mod;
                    if (modules.TryGetValue(owner, out mod)) filePath = mod.FilePath;
                }
                diags.Add(new LinkDiagnostic(filePath, LinkDiagnosticCodes.LinkUnresolvedAttribute,
                    ParseSeverity.Error, node.Span,
                    "module '" + target.ModuleFqn + "' has no attribute '" + target.Name + "'"));
                bindings[node] = BindingTarget.Unresolved(UnresolvedReason.NameNotInModule);
            }
            return diags;
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
    }
}
