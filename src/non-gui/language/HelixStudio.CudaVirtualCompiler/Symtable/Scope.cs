namespace HelixPythonNamespace.CudaVirtualCompiler.Symtable
{
    using System.Collections.Generic;

    /// <summary>
    /// One lexical scope. Owns its symbols and child scopes. Name lookup walks the
    /// parent chain. Same-named function symbols co-exist (overload sets). Port of
    /// the C++ helix::cuda_vcompiler::symtable::Scope (cuda_vcompiler/symtable/scope.h).
    /// The C++ unordered_multimap byName_ is modeled as a Dictionary of insertion-
    /// ordered lists.
    /// </summary>
    public sealed class Scope
    {
        private readonly BlockKind kind_;
        private readonly Scope parent_;
        private readonly string name_;
        private readonly List<Symbol> symbols_;
        private readonly List<Scope> children_;
        private readonly Dictionary<string, List<Symbol>> byName_;

        /// <summary>For Class scopes: the record's base-class names (last segment when the
        /// base was written qualified), so inherited members can be resolved.</summary>
        public readonly List<string> BaseClassNames = new List<string>();

        public Scope(BlockKind kind, Scope parent, string name)
        {
            kind_ = kind;
            parent_ = parent;
            name_ = name ?? string.Empty;
            symbols_ = new List<Symbol>();
            children_ = new List<Scope>();
            byName_ = new Dictionary<string, List<Symbol>>();
        }

        public BlockKind Kind { get { return kind_; } }
        public Scope Parent { get { return parent_; } }
        public string Name { get { return name_; } }

        public Symbol Add(Symbol sym)
        {
            symbols_.Add(sym);
            List<Symbol> bucket;
            if (!byName_.TryGetValue(sym.Name, out bucket))
            {
                bucket = new List<Symbol>();
                byName_[sym.Name] = bucket;
            }
            bucket.Add(sym);
            return sym;
        }

        public Scope AddChild(BlockKind kind, string name)
        {
            Scope child = new Scope(kind, this, name);
            children_.Add(child);
            return child;
        }

        /// <summary>First matching symbol in this scope only.</summary>
        public Symbol LookupLocal(string name)
        {
            List<Symbol> bucket;
            if (name != null && byName_.TryGetValue(name, out bucket) && bucket.Count > 0) return bucket[0];
            return null;
        }

        public List<Symbol> LookupAllLocal(string name)
        {
            List<Symbol> outList = new List<Symbol>();
            List<Symbol> bucket;
            if (name != null && byName_.TryGetValue(name, out bucket))
            {
                for (int i = 0; i < bucket.Count; ++i) outList.Add(bucket[i]);
            }
            return outList;
        }

        /// <summary>Walk up the parent chain.</summary>
        public Symbol Lookup(string name)
        {
            for (Scope s = this; s != null; s = s.parent_)
            {
                Symbol sym = s.LookupLocal(name);
                if (sym != null) return sym;
            }
            return null;
        }

        public IReadOnlyList<Symbol> Symbols { get { return symbols_; } }
        public IReadOnlyList<Scope> Children { get { return children_; } }
    }
}
