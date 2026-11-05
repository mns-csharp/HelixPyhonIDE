namespace HelixPythonNamespace.CudaVirtualCompiler.Symtable
{
    using System.Collections.Generic;

    /// <summary>
    /// Per-translation-unit symbol table: the scope tree, an AST-node -&gt; Scope index
    /// (so an analysis can fetch a function's body scope), and a flat function index
    /// by bare name (including seeded builtins) used for callee / kernel lookups that
    /// should succeed regardless of namespace nesting. Port of the C++
    /// helix::cuda_vcompiler::symtable::SymbolTable (cuda_vcompiler/symtable/scope.h).
    /// The node key uses reference identity (mirrors the C++ const void* key).
    /// </summary>
    public sealed class SymbolTable
    {
        private readonly Scope root_;
        private readonly Dictionary<object, Scope> nodeScopes_;
        private readonly Dictionary<string, List<Symbol>> funcIndex_;

        public SymbolTable()
        {
            root_ = new Scope(BlockKind.TranslationUnit, null, "");
            nodeScopes_ = new Dictionary<object, Scope>();
            funcIndex_ = new Dictionary<string, List<Symbol>>();
        }

        public Scope Root { get { return root_; } }

        public void MapNode(object node, Scope scope)
        {
            if (node != null) nodeScopes_[node] = scope;
        }

        public Scope ScopeFor(object node)
        {
            Scope s;
            if (node != null && nodeScopes_.TryGetValue(node, out s)) return s;
            return null;
        }

        /// <summary>Register a function symbol in the flat, namespace-agnostic index.</summary>
        public void IndexFunction(Symbol s)
        {
            List<Symbol> bucket;
            if (!funcIndex_.TryGetValue(s.Name, out bucket))
            {
                bucket = new List<Symbol>();
                funcIndex_[s.Name] = bucket;
            }
            bucket.Add(s);
        }

        public List<Symbol> FindFunctions(string bareName)
        {
            List<Symbol> outList = new List<Symbol>();
            List<Symbol> bucket;
            if (bareName != null && funcIndex_.TryGetValue(bareName, out bucket))
            {
                for (int i = 0; i < bucket.Count; ++i) outList.Add(bucket[i]);
            }
            return outList;
        }

        /// <summary>First function (if any) with this bare name.</summary>
        public Symbol FindFunction(string bareName)
        {
            List<Symbol> bucket;
            if (bareName != null && funcIndex_.TryGetValue(bareName, out bucket) && bucket.Count > 0) return bucket[0];
            return null;
        }

        /// <summary>Namespaces opened by <c>using namespace a::b;</c> anywhere in the unit
        /// (coarse — file-wide — which errs on the resolving side).</summary>
        public readonly List<string> OpenedNamespaces = new List<string>();

        /// <summary>First Class scope anywhere in the tree with this name (for base-class
        /// member resolution). Null when unknown.</summary>
        public Scope FindClassScope(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return FindClass(root_, name);
        }

        private static Scope FindClass(Scope scope, string want)
        {
            IReadOnlyList<Scope> children = scope.Children;
            for (int i = 0; i < children.Count; ++i)
            {
                Scope c = children[i];
                if (c.Kind == BlockKind.Class && c.Name == want) return c;
                Scope inner = FindClass(c, want);
                if (inner != null) return inner;
            }
            return null;
        }

        /// <summary>
        /// The scope of namespace <paramref name="qualified"/> (e.g. "lib_core::shapes"),
        /// matching both nested single-segment namespaces and the composite-name scopes a
        /// C++17 <c>namespace a::b {</c> definition produces. Null when unknown.
        /// </summary>
        public Scope FindNamespaceScope(string qualified)
        {
            if (string.IsNullOrEmpty(qualified)) return null;
            return FindNs(root_, qualified);
        }

        private static Scope FindNs(Scope scope, string want)
        {
            IReadOnlyList<Scope> children = scope.Children;
            for (int i = 0; i < children.Count; ++i)
            {
                Scope c = children[i];
                if (c.Kind != BlockKind.Namespace) continue;
                string name = c.Name ?? string.Empty;
                if (name == want) return c;
                if (want.Length > name.Length &&
                    want.StartsWith(name, System.StringComparison.Ordinal) &&
                    string.CompareOrdinal(want, name.Length, "::", 0, 2) == 0)
                {
                    Scope inner = FindNs(c, want.Substring(name.Length + 2));
                    if (inner != null) return inner;
                }
            }
            return null;
        }
    }
}
