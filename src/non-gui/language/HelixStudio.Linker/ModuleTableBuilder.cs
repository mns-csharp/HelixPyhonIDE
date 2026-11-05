namespace HelixPythonNamespace.Linker
{
    using System.Collections.Generic;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Walks a module's top-level body and records every name bound at module scope
    /// (defs, assignments, type aliases, imports) plus the <c>__all__</c> literal.
    /// Port of the C++ <c>ModuleTableBuilder</c>.
    /// </summary>
    public static class ModuleTableBuilder
    {
        public static ModuleSummary Build(Module m, string moduleFqn, string filePath)
        {
            ModuleSummary summary = new ModuleSummary
            {
                ModuleFqn = moduleFqn ?? "",
                FilePath = filePath ?? "",
                AstModule = m,
            };

            foreach (Stmt node in m.Body)
            {
                if (node == null) continue;

                if (node is FunctionDef fn)
                {
                    AddDef(summary, fn.Name, BindingKind.Function, fn);
                    continue;
                }
                if (node is AsyncFunctionDef afn)
                {
                    AddDef(summary, afn.Name, BindingKind.AsyncFunction, afn);
                    continue;
                }
                if (node is ClassDef cls)
                {
                    AddDef(summary, cls.Name, BindingKind.Class, cls);
                    continue;
                }
                if (node is Assign a)
                {
                    TryExtractAllLiteral(a, summary);
                    foreach (Expr t in a.Targets)
                    {
                        RecordSimpleTarget(t, BindingKind.Variable, a, a.Span, summary);
                    }
                    continue;
                }
                if (node is AnnAssign an)
                {
                    RecordSimpleTarget(an.Target, BindingKind.AnnotatedVar, an, an.Span, summary);
                    continue;
                }
                if (node is TypeAlias ta)
                {
                    RecordSimpleTarget(ta.Name, BindingKind.TypeAlias, ta, ta.Span, summary);
                    continue;
                }
                if (node is Import imp)
                {
                    foreach (Alias al in imp.Names)
                    {
                        string bound;
                        string targetFqn;
                        if (!string.IsNullOrEmpty(al.AsName))
                        {
                            bound = al.AsName;
                            targetFqn = al.Name;
                        }
                        else
                        {
                            int dot = al.Name.IndexOf('.');
                            bound = dot < 0 ? al.Name : al.Name.Substring(0, dot);
                            targetFqn = bound;
                        }
                        summary.DirectImportFqns.Add(targetFqn);
                        if (!summary.Bindings.ContainsKey(bound))
                        {
                            summary.Bindings[bound] = new ModuleBinding
                            {
                                Name = bound,
                                Kind = BindingKind.ImportAlias,
                                DefSpan = imp.Span,
                                DefNode = imp,
                                IsPrivate = IsPrivate(bound),
                                IsReExport = true,
                            };
                        }
                    }
                    continue;
                }
                if (node is ImportFrom imf)
                {
                    if (!string.IsNullOrEmpty(imf.Module))
                    {
                        summary.DirectImportFqns.Add(imf.Module);
                    }
                    foreach (Alias al in imf.Names)
                    {
                        if (al.Name == "*")
                        {
                            if (!string.IsNullOrEmpty(imf.Module))
                            {
                                summary.StarImportSources.Add(imf.Module);
                            }
                            continue;
                        }
                        string bound = !string.IsNullOrEmpty(al.AsName) ? al.AsName : al.Name;
                        if (!summary.Bindings.ContainsKey(bound))
                        {
                            summary.Bindings[bound] = new ModuleBinding
                            {
                                Name = bound,
                                Kind = BindingKind.ImportAlias,
                                DefSpan = imf.Span,
                                DefNode = imf,
                                IsPrivate = IsPrivate(bound),
                                IsReExport = true,
                            };
                        }
                    }
                    continue;
                }
            }

            return summary;
        }

        private static bool IsPrivate(string n)
        {
            return !string.IsNullOrEmpty(n) && n[0] == '_';
        }

        private static void AddDef(ModuleSummary summary, string name, BindingKind kind, AstNode node)
        {
            if (summary.Bindings.ContainsKey(name)) return; // emplace: first wins
            summary.Bindings[name] = new ModuleBinding
            {
                Name = name,
                Kind = kind,
                DefSpan = node.Span,
                DefNode = node,
                IsPrivate = IsPrivate(name),
            };
        }

        private static void RecordSimpleTarget(Expr target, BindingKind kind, AstNode defNode, Python.Text.SourceSpan span, ModuleSummary summary)
        {
            Name nm = target as Name;
            if (nm == null) return;
            if (summary.Bindings.ContainsKey(nm.Id)) return;
            summary.Bindings[nm.Id] = new ModuleBinding
            {
                Name = nm.Id,
                Kind = kind,
                DefSpan = span,
                DefNode = defNode,
                IsPrivate = IsPrivate(nm.Id),
            };
        }

        private static void TryExtractAllLiteral(Assign a, ModuleSummary summary)
        {
            if (a.Targets.Count != 1) return;
            Name nm = a.Targets[0] as Name;
            if (nm == null || nm.Id != "__all__") return;
            Expr val = a.Value;
            if (val == null) return;

            List<string> names = null;
            if (val is ListExpr le) names = ExtractStringList(le.Elts);
            else if (val is TupleExpr te) names = ExtractStringList(te.Elts);

            if (names != null && names.Count > 0)
            {
                summary.All = names;
                summary.HasExplicitAll = true;
            }
        }

        private static List<string> ExtractStringList(IReadOnlyList<Expr> elts)
        {
            List<string> outList = new List<string>(elts.Count);
            foreach (Expr e in elts)
            {
                Constant c = e as Constant;
                if (c == null || !(c.Value is string))
                {
                    return null; // anything non-string => not a clean literal
                }
                outList.Add((string)c.Value);
            }
            return outList;
        }
    }
}
