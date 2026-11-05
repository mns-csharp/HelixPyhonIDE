using System;
using System.Collections.Generic;
using HelixPythonNamespace.Python.Text;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Symtable
{
    /// <summary>
    /// Mirrors CPython's <c>_symtable_entry</c> (aka <c>PySTEntryObject</c>).
    /// One instance per syntactic scope (module, def, class, lambda, comprehension,
    /// type-param brackets, annotation). Children are ordered by first-visit order
    /// so callers can walk the tree deterministically.
    /// </summary>
    public sealed class Scope
    {
        public BlockKind Kind { get; }
        public string Name { get; }               // "" for module; defname for def/class; "<lambda>"/"<listcomp>" for anonymous blocks
        public AstNode Node { get; }              // originating AST node (Module, FunctionDef, ClassDef, etc.)
        public SourceSpan Span { get; }
        public Scope Parent { get; }

        private readonly Dictionary<string, Symbol> _symbols = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        private readonly List<Scope> _children = new List<Scope>();
        private readonly HashSet<string> _globals = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _nonlocals = new HashSet<string>(StringComparer.Ordinal);

        // Per-block metadata tracked by CPython's symtable.c.
        public bool IsAsync { get; internal set; }
        public bool HasReturnWithValue { get; internal set; }
        public bool HasYield { get; internal set; }
        public bool NeedsClassClosure { get; internal set; }
        public bool HasStarImport { get; internal set; }

        internal Scope(BlockKind kind, string name, AstNode node, SourceSpan span, Scope parent)
        {
            Kind = kind;
            Name = name ?? string.Empty;
            Node = node;
            Span = span;
            Parent = parent;
        }

        public IReadOnlyDictionary<string, Symbol> Symbols { get { return _symbols; } }
        public IReadOnlyList<Scope> Children { get { return _children; } }
        public IReadOnlyCollection<string> GlobalDeclarations { get { return _globals; } }
        public IReadOnlyCollection<string> NonlocalDeclarations { get { return _nonlocals; } }

        internal Symbol GetOrCreate(string name)
        {
            Symbol s;
            if (!_symbols.TryGetValue(name, out s))
            {
                s = new Symbol(name);
                _symbols[name] = s;
            }
            return s;
        }

        internal bool TryGetSymbol(string name, out Symbol s) { return _symbols.TryGetValue(name, out s); }

        internal void DeclareGlobal(string name) { _globals.Add(name); }
        internal void DeclareNonlocal(string name) { _nonlocals.Add(name); }
        public bool IsDeclaredGlobal(string name) { return _globals.Contains(name); }
        public bool IsDeclaredNonlocal(string name) { return _nonlocals.Contains(name); }

        internal Scope AddChild(BlockKind kind, string name, AstNode node, SourceSpan span)
        {
            Scope child = new Scope(kind, name, node, span, this);
            _children.Add(child);
            return child;
        }

        public override string ToString()
        {
            return Kind + " '" + Name + "' " + Span;
        }
    }

    /// <summary>Holds the root scope plus the originating module. Result of Pass 1.</summary>
    public sealed class SymbolTable
    {
        public Scope Root { get; }
        public Module Module { get; }
        public IReadOnlyDictionary<AstNode, Scope> ScopeByNode { get; }

        public SymbolTable(Scope root, Module module, IReadOnlyDictionary<AstNode, Scope> scopeByNode)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            Root = root;
            Module = module;
            ScopeByNode = scopeByNode ?? new Dictionary<AstNode, Scope>();
        }

        public Scope LookupScope(AstNode node)
        {
            if (node == null) return null;
            Scope s;
            return ScopeByNode.TryGetValue(node, out s) ? s : null;
        }
    }
}
