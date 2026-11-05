namespace HelixPythonNamespace.DeadCode
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using HelixPythonNamespace.Linker;
    using HelixPythonNamespace.Python.Text;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    /// <summary>
    /// Cross-module dead-code detector. Reuses the linker's <see cref="ReferenceIndex"/>
    /// for module-level definitions and walks per-file ASTs for class methods, imports,
    /// unreachable statements, and unused locals/parameters. Port of the C++
    /// <c>helix::dead_code::DeadCodeEngine</c>.
    /// </summary>
    public sealed class DeadCodeEngine
    {
        public DeadCodeReport Analyze(IReadOnlyList<CompileFileResult> files, LinkResult linkResult, CancellationToken cancel = default(CancellationToken))
        {
            DeadCodeReport report = new DeadCodeReport();
            report.LinkSkipped = linkResult.LinkSkipped;

            // Pass 1 — per-file AST walk.
            Dictionary<string, ModuleScan> scansByPath = new Dictionary<string, ModuleScan>(StringComparer.Ordinal);
            HashSet<string> globalAttrUses = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> globalLoadedNames = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> protocolMethodNames = new HashSet<string>(StringComparer.Ordinal);

            int analyzed = 0;
            foreach (CompileFileResult f in files)
            {
                if (cancel.IsCancellationRequested) break;
                Module mod = f.AstModule;
                if (mod == null) continue;
                ModuleScan scan = new ModuleScan { FilePath = f.FilePath };
                scansByPath[f.FilePath] = scan;
                new DeadWalker(scan).WalkModule(mod);
                foreach (string a in scan.AccessedAttrs) globalAttrUses.Add(a);
                foreach (string n in scan.LoadedNames) globalLoadedNames.Add(n);
                foreach (string clsName in scan.ProtocolClassNames)
                {
                    ClassDef cd;
                    if (!scan.TopLevelClasses.TryGetValue(clsName, out cd)) continue;
                    foreach (Stmt member in cd.Body)
                    {
                        if (member is FunctionDef fd) protocolMethodNames.Add(fd.Name);
                        else if (member is AsyncFunctionDef afd) protocolMethodNames.Add(afd.Name);
                    }
                }
                report.Findings.AddRange(scan.Unreachable);
                scan.Unreachable.Clear();
                report.Findings.AddRange(scan.UnusedLocals);
                scan.UnusedLocals.Clear();
                analyzed++;
            }
            report.FilesAnalyzed = analyzed;

            if (report.LinkSkipped)
            {
                SortFindings(report.Findings);
                return report;
            }

            // Pass 2 — unused top-level bindings + imports.
            Dictionary<string, ModuleSummary> modules = linkResult.Modules;
            ReferenceIndex refs = linkResult.References;

            foreach (KeyValuePair<string, ModuleSummary> mkv in modules)
            {
                ModuleSummary summary = mkv.Value;
                ModuleScan scan;
                scansByPath.TryGetValue(summary.FilePath, out scan);

                bool starImportSuppress = (scan != null && scan.HasStarImport) || summary.StarImportSources.Count > 0;

                foreach (KeyValuePair<string, ModuleBinding> bkv in summary.Bindings)
                {
                    string name = bkv.Key;
                    ModuleBinding binding = bkv.Value;
                    if (binding.Kind == BindingKind.StarImport) continue;
                    if (DeadLive.IsAlwaysLive(summary, name)) continue;
                    if (starImportSuppress) continue;

                    if (binding.Kind == BindingKind.Class && DeadLive.IsPytestClassName(name)) continue;
                    if (binding.Kind == BindingKind.Class && scan != null && scan.ProtocolClassNames.Contains(name)) continue;

                    if (binding.Kind == BindingKind.ImportAlias)
                    {
                        if (scan != null && scan.LoadedNames.Contains(name)) continue;
                        ImportFrom imf = binding.DefNode as ImportFrom;
                        if (imf != null && imf.Module == "__future__") continue;
                        report.Findings.Add(new DeadCodeFinding
                        {
                            Kind = DeadCodeKind.UnusedImport,
                            FilePath = summary.FilePath,
                            Line = binding.DefSpan.Start.Line,
                            Column = binding.DefSpan.Start.Column,
                            SymbolName = name,
                            Message = "Imported name '" + name + "' is never used.",
                        });
                        continue;
                    }

                    IReadOnlyList<UseSite> uses = refs.Lookup(new DefId { ModuleFqn = summary.ModuleFqn, Name = name });
                    if (uses != null && uses.Count > 0) continue;
                    if (globalLoadedNames.Contains(name)) continue;
                    // A top-level function/class reached only through module attribute access
                    // (`import _layout; _layout.child_env(...)`) is NOT in the linker's name
                    // reference index, so it looked dead. Treat any `.NAME` access anywhere in
                    // the solution as a use — same conservative rule Pass 3 already applies to
                    // methods (line below), matching vulture. Prevents false positives that tell
                    // the user to delete live code (child_env / insert_sys_path in fixture (a)).
                    if (globalAttrUses.Contains(name)) continue;

                    report.Findings.Add(new DeadCodeFinding
                    {
                        Kind = KindFromBinding(binding.Kind),
                        FilePath = summary.FilePath,
                        Line = binding.DefSpan.Start.Line,
                        Column = binding.DefSpan.Start.Column,
                        SymbolName = name,
                        Message = KindHumanLabel(binding.Kind) + " '" + name + "' is never used.",
                    });
                }

                // Pass 3 — unused class methods.
                if (scan == null) continue;
                foreach (KeyValuePair<string, ClassDef> ckv in scan.TopLevelClasses)
                {
                    string className = ckv.Key;
                    ClassDef classDef = ckv.Value;
                    IReadOnlyList<UseSite> classUses = refs.Lookup(new DefId { ModuleFqn = summary.ModuleFqn, Name = className });
                    bool classDead = (classUses == null || classUses.Count == 0) && !DeadLive.IsAlwaysLive(summary, className);
                    if (classDead) continue;

                    foreach (Stmt memberStmt in classDef.Body)
                    {
                        string methodName = null;
                        SourceSpan methodSpan = default(SourceSpan);
                        IReadOnlyList<Expr> decorators = null;
                        if (memberStmt is FunctionDef fd)
                        {
                            methodName = fd.Name; methodSpan = fd.Span; decorators = fd.DecoratorList;
                        }
                        else if (memberStmt is AsyncFunctionDef afd)
                        {
                            methodName = afd.Name; methodSpan = afd.Span; decorators = afd.DecoratorList;
                        }
                        if (methodName == null) continue;
                        if (DeadLive.IsProtocolMethod(methodName)) continue;
                        if (decorators != null && DeadLive.HasRegisteringDecorator(decorators)) continue;
                        if (protocolMethodNames.Contains(methodName)) continue;
                        if (globalAttrUses.Contains(methodName)) continue;
                        if (refs.Lookup(new DefId { ModuleFqn = summary.ModuleFqn, Name = methodName }) != null) continue;

                        report.Findings.Add(new DeadCodeFinding
                        {
                            Kind = DeadCodeKind.UnusedMethod,
                            FilePath = summary.FilePath,
                            Line = methodSpan.Start.Line,
                            Column = methodSpan.Start.Column,
                            SymbolName = className + "." + methodName,
                            Message = "Method '" + className + "." + methodName + "' is never invoked anywhere in the solution.",
                        });
                    }
                }
            }

            SortFindings(report.Findings);
            return report;
        }

        private static void SortFindings(List<DeadCodeFinding> findings)
        {
            findings.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.FilePath, b.FilePath);
                if (c != 0) return c;
                if (a.Line != b.Line) return a.Line - b.Line;
                return a.Column - b.Column;
            });
        }

        private static DeadCodeKind KindFromBinding(BindingKind bk)
        {
            switch (bk)
            {
                case BindingKind.Function:
                case BindingKind.AsyncFunction:
                    return DeadCodeKind.UnusedFunction;
                case BindingKind.Class:
                    return DeadCodeKind.UnusedClass;
                case BindingKind.Variable:
                case BindingKind.AnnotatedVar:
                case BindingKind.TypeAlias:
                    return DeadCodeKind.UnusedVariable;
                default:
                    return DeadCodeKind.UnusedImport;
            }
        }

        private static string KindHumanLabel(BindingKind bk)
        {
            switch (bk)
            {
                case BindingKind.Function: return "Function";
                case BindingKind.AsyncFunction: return "Async function";
                case BindingKind.Class: return "Class";
                case BindingKind.Variable: return "Variable";
                case BindingKind.AnnotatedVar: return "Variable";
                case BindingKind.TypeAlias: return "Type alias";
                case BindingKind.ImportAlias: return "Import";
                case BindingKind.StarImport: return "Star import";
            }
            return "Symbol";
        }
    }

    // ----- per-module scan state -----

    internal sealed class ModuleScan
    {
        public HashSet<string> LoadedNames = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> AccessedAttrs = new HashSet<string>(StringComparer.Ordinal);
        public bool HasStarImport;
        public Dictionary<string, ClassDef> TopLevelClasses = new Dictionary<string, ClassDef>(StringComparer.Ordinal);
        public HashSet<string> ProtocolClassNames = new HashSet<string>(StringComparer.Ordinal);
        public List<DeadCodeFinding> Unreachable = new List<DeadCodeFinding>();
        public List<DeadCodeFinding> UnusedLocals = new List<DeadCodeFinding>();
        public string FilePath = "";
    }

    // ----- live-set predicates + helpers -----

    internal static class DeadLive
    {
        public static bool IsDunderName(string name)
        {
            return name != null && name.Length >= 4 && name.StartsWith("__") && name.EndsWith("__");
        }

        public static bool IsTestEntry(string name)
        {
            return name != null && name.StartsWith("test_");
        }

        public static bool IsPytestClassName(string name)
        {
            return name != null && name.Length >= 5 && name.StartsWith("Test") && char.IsUpper(name[4]);
        }

        public static bool IsModuleEntryName(string name)
        {
            return name == "main" || name == "__main__";
        }

        public static bool IsExplicitlyExported(ModuleSummary s, string name)
        {
            return s.HasExplicitAll && s.All.Contains(name);
        }

        public static bool IsAlwaysLive(ModuleSummary s, string name)
        {
            return IsExplicitlyExported(s, name) || IsTestEntry(name) || IsModuleEntryName(name) || IsDunderName(name);
        }

        private static readonly HashSet<string> Hooks = new HashSet<string>(StringComparer.Ordinal)
        {
            "setUp", "tearDown", "setUpClass", "tearDownClass",
            "setUpModule", "tearDownModule", "asyncSetUp", "asyncTearDown",
            "setup_method", "teardown_method", "setup_class", "teardown_class",
            "setup_function", "teardown_function", "setup_module", "teardown_module",
        };

        public static bool IsProtocolMethod(string name)
        {
            if (IsDunderName(name)) return true;
            if (IsTestEntry(name)) return true;
            return Hooks.Contains(name);
        }

        private static readonly HashSet<string> NoEffectDecorators = new HashSet<string>(StringComparer.Ordinal)
        {
            "staticmethod", "classmethod", "property", "cached_property",
            "abstractmethod", "abstractclassmethod", "abstractstaticmethod", "abstractproperty",
            "overload", "final", "override", "no_type_check", "runtime_checkable",
            "dataclass", "deprecated",
        };

        public static string DecoratorTrailingName(Expr e)
        {
            if (e == null) return "";
            if (e is Call call) return DecoratorTrailingName(call.Func);
            if (e is Name n) return n.Id;
            if (e is AttributeExpr a) return a.Attr;
            return "";
        }

        public static bool HasRegisteringDecorator(IReadOnlyList<Expr> decorators)
        {
            foreach (Expr d in decorators)
            {
                string name = DecoratorTrailingName(d);
                if (string.IsNullOrEmpty(name)) return true;     // unparseable → assume live
                if (!NoEffectDecorators.Contains(name)) return true;
            }
            return false;
        }

        public static string TrailingBaseName(Expr e)
        {
            if (e is Name n) return n.Id;
            if (e is AttributeExpr a) return a.Attr;
            return "";
        }

        private static readonly HashSet<string> AbstractBases = new HashSet<string>(StringComparer.Ordinal)
        {
            "Protocol", "Generic", "ABC", "ABCMeta",
        };

        public static bool ClassHasAbstractBase(ClassDef cd)
        {
            foreach (Expr b in cd.Bases)
            {
                if (AbstractBases.Contains(TrailingBaseName(b))) return true;
            }
            foreach (Keyword kw in cd.Keywords)
            {
                if (kw != null && kw.Name == "metaclass" && kw.Value != null)
                {
                    if (AbstractBases.Contains(TrailingBaseName(kw.Value))) return true;
                }
            }
            return false;
        }

        public static bool IsTerminator(Stmt s)
        {
            return s is Return || s is Raise || s is Break || s is Continue;
        }

        // -1 unknown, 0 falsy, 1 truthy.
        public static int LiteralTruthiness(Expr e)
        {
            Constant c = e as Constant;
            if (c == null) return -1;
            object v = c.Value;
            if (v == null) return 0;
            if (v is bool b) return b ? 1 : 0;
            if (v is long l) return l == 0 ? 0 : 1;
            if (v is int i) return i == 0 ? 0 : 1;
            if (v is double d) return d == 0.0 ? 0 : 1;
            if (v is string s) return s.Length == 0 ? 0 : 1;
            return -1;
        }
    }
}
