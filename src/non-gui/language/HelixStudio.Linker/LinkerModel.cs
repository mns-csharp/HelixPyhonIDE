namespace HelixPythonNamespace.Linker
{
    using System.Collections.Generic;
    using HelixPythonNamespace.Python.Text;
    using HelixPythonNamespace.VirtualCompiler.Ast;

    public enum BindingKind
    {
        Function,
        AsyncFunction,
        Class,
        Variable,       // x = ...
        AnnotatedVar,   // x: T = ... or x: T
        TypeAlias,
        ImportAlias,    // import foo / from foo import bar
        StarImport,     // re-exported via from foo import *
    }

    /// <summary>A name bound at module scope. Port of the C++ <c>ModuleBinding</c>.</summary>
    public sealed class ModuleBinding
    {
        public string Name = "";
        public BindingKind Kind;
        public SourceSpan DefSpan;
        public AstNode DefNode;
        public bool IsPrivate;     // leading underscore
        public bool IsReExport;    // came from import / from-import / __all__
    }

    /// <summary>Per-module summary aggregated by the module-table builder.</summary>
    public sealed class ModuleSummary
    {
        public string ModuleFqn = "";
        public string FilePath = "";
        public Module AstModule;
        public Dictionary<string, ModuleBinding> Bindings = new Dictionary<string, ModuleBinding>();
        public List<string> All = new List<string>();   // explicit __all__ (empty => default rule)
        public bool HasExplicitAll;
        public List<string> StarImportSources = new List<string>(); // FQNs of from X import *
        public List<string> DirectImportFqns = new List<string>();  // FQNs of import X / from X import ...

        public bool IsExportedByDefault(string name)
        {
            if (HasExplicitAll)
            {
                return All.Contains(name);
            }
            return !string.IsNullOrEmpty(name) && name[0] != '_';
        }
    }

    public enum BindingTargetKind
    {
        Unresolved,
        ResolvedSymbol,        // intra-solution symbol
        ResolvedModule,        // an entire module (from import foo)
        ResolvedCatalogSymbol, // external package symbol
    }

    public enum UnresolvedReason
    {
        None,
        ModuleNotFound,
        NameNotInModule,
        RelativeImportEscapesPackage,
        StarImportAmbiguous,
    }

    /// <summary>Where an Import / ImportFrom / cross-module reference resolves. Port of the C++ <c>BindingTarget</c>.</summary>
    public struct BindingTarget
    {
        public BindingTargetKind Kind;
        public string ModuleFqn;
        public string Name;
        public SourceSpan DefSpan;
        public UnresolvedReason Reason;

        public bool IsResolved
        {
            get { return Kind != BindingTargetKind.Unresolved; }
        }

        public static BindingTarget Module(string fqn)
        {
            return new BindingTarget { Kind = BindingTargetKind.ResolvedModule, ModuleFqn = fqn };
        }

        public static BindingTarget Symbol(string fqn, string n, SourceSpan span)
        {
            return new BindingTarget { Kind = BindingTargetKind.ResolvedSymbol, ModuleFqn = fqn, Name = n, DefSpan = span };
        }

        public static BindingTarget CatalogSymbol(string fqn, string n)
        {
            return new BindingTarget { Kind = BindingTargetKind.ResolvedCatalogSymbol, ModuleFqn = fqn, Name = n };
        }

        public static BindingTarget Unresolved(UnresolvedReason r)
        {
            return new BindingTarget { Kind = BindingTargetKind.Unresolved, Reason = r };
        }
    }

    public struct DefId
    {
        public string ModuleFqn;
        public string Name;

        public string Key()
        {
            return string.IsNullOrEmpty(ModuleFqn) ? Name : ModuleFqn + "::" + Name;
        }
    }

    public sealed class UseSite
    {
        public string ModuleFqn;
        public string FilePath;
        public AstNode Node;
        public SourceSpan Span;
    }

    /// <summary>Reverse map from a definition to every place it is used. Port of the C++ <c>ReferenceIndex</c>.</summary>
    public sealed class ReferenceIndex
    {
        private readonly Dictionary<string, List<UseSite>> _uses = new Dictionary<string, List<UseSite>>();

        public void Add(DefId def, UseSite use)
        {
            string key = def.Key();
            List<UseSite> list;
            if (!_uses.TryGetValue(key, out list))
            {
                list = new List<UseSite>();
                _uses[key] = list;
            }
            list.Add(use);
        }

        public IReadOnlyList<UseSite> Lookup(DefId def)
        {
            List<UseSite> list;
            return _uses.TryGetValue(def.Key(), out list) ? list : null;
        }

        public int TotalUses()
        {
            int n = 0;
            foreach (KeyValuePair<string, List<UseSite>> kv in _uses)
            {
                n += kv.Value.Count;
            }
            return n;
        }

        public IReadOnlyDictionary<string, List<UseSite>> All
        {
            get { return _uses; }
        }
    }
}
