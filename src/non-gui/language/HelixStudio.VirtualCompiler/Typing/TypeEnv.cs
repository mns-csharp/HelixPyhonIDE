using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.VirtualCompiler.Typing
{
    /// <summary>
    /// A single lexical scope's type bindings. One frame per Module / Function /
    /// Lambda / Class / Comprehension block as the inferencer descends.
    /// </summary>
    public sealed class TypeScope
    {
        public string Name { get; }

        /// <summary>For method scopes, the enclosing class name — drives <c>self.*</c> resolution.</summary>
        public string EnclosingClassName { get; set; }

        /// <summary>True if this is an async function scope.</summary>
        public bool IsAsync { get; set; }

        /// <summary>For function/method scopes, the annotated return type (nullable).</summary>
        public TypeRef ExpectedReturnType { get; set; }

        private readonly Dictionary<string, TypeRef> _bindings =
            new Dictionary<string, TypeRef>(StringComparer.Ordinal);

        public TypeScope(string name) { Name = name ?? string.Empty; }

        public IReadOnlyDictionary<string, TypeRef> Bindings { get { return _bindings; } }

        public void Bind(string name, TypeRef type)
        {
            if (string.IsNullOrEmpty(name)) return;
            _bindings[name] = type ?? UnknownRef.Instance;
        }

        public bool TryResolve(string name, out TypeRef type)
        {
            return _bindings.TryGetValue(name, out type);
        }

        internal void Remove(string name)
        {
            if (!string.IsNullOrEmpty(name)) _bindings.Remove(name);
        }
    }

    /// <summary>
    /// Snapshot of bindings for a set of names in the current scope — used
    /// to restore state after a block-scoped narrowing override. Returned by
    /// <see cref="TypeEnv.OverrideInCurrent"/>.
    /// </summary>
    public sealed class BindingSnapshot
    {
        internal TypeScope Scope;
        internal Dictionary<string, TypeRef> Prior;      // name → previous value (null if not present)
        internal HashSet<string> AddedKeys;              // keys that didn't exist before
    }

    /// <summary>
    /// Stack of <see cref="TypeScope"/> frames — the type-world equivalent of
    /// the symtable's scope tree. Resolution walks outward with Python's
    /// class-scope exclusion rule.
    /// </summary>
    public sealed class TypeEnv
    {
        private readonly Stack<TypeScope> _scopes = new Stack<TypeScope>();

        public TypeEnv()
        {
            Push("<module>");
        }

        public TypeScope Current { get { return _scopes.Peek(); } }
        public int Depth { get { return _scopes.Count; } }

        public TypeScope Push(string name)
        {
            TypeScope s = new TypeScope(name);
            _scopes.Push(s);
            return s;
        }

        public void Pop()
        {
            if (_scopes.Count > 1) _scopes.Pop();
        }

        /// <summary>Bind a name in the current (top-of-stack) scope.</summary>
        public void Bind(string name, TypeRef type)
        {
            Current.Bind(name, type);
        }

        /// <summary>
        /// Walk outward looking up <paramref name="name"/>. Class scopes are
        /// skipped when we're not directly in them — mirrors the rule
        /// <see cref="HelixPythonNamespace.VirtualCompiler.Symtable.NameResolver"/> uses.
        /// Returns <c>null</c> when the name isn't bound anywhere.
        /// </summary>
        public TypeRef Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            bool first = true;
            foreach (TypeScope s in _scopes)
            {
                if (!first && !string.IsNullOrEmpty(s.EnclosingClassName))
                {
                    // We're coming from a nested function body — don't consult the
                    // enclosing class body's locals (Python semantics).
                    first = false;
                    continue;
                }
                first = false;
                TypeRef t;
                if (s.TryResolve(name, out t)) return t;
            }
            return null;
        }

        /// <summary>Walk outward looking for the nearest method scope's enclosing class name.</summary>
        public string CurrentEnclosingClass()
        {
            foreach (TypeScope s in _scopes)
            {
                if (!string.IsNullOrEmpty(s.EnclosingClassName)) return s.EnclosingClassName;
            }
            return null;
        }

        /// <summary>
        /// Block-scoped narrowing: overwrite <paramref name="overrides"/> in the
        /// current scope, returning a snapshot the caller passes to
        /// <see cref="Restore"/> on block exit. Used by the inferencer to apply
        /// type narrowings (isinstance, is None) inside if/while bodies
        /// without affecting enclosing-scope state.
        /// </summary>
        public BindingSnapshot OverrideInCurrent(IReadOnlyDictionary<string, TypeRef> overrides)
        {
            BindingSnapshot snap = new BindingSnapshot
            {
                Scope = Current,
                Prior = new Dictionary<string, TypeRef>(StringComparer.Ordinal),
                AddedKeys = new HashSet<string>(StringComparer.Ordinal)
            };
            if (overrides == null) return snap;
            foreach (KeyValuePair<string, TypeRef> kv in overrides)
            {
                TypeRef existing;
                if (Current.TryResolve(kv.Key, out existing)) snap.Prior[kv.Key] = existing;
                else snap.AddedKeys.Add(kv.Key);
                Current.Bind(kv.Key, kv.Value);
            }
            return snap;
        }

        public void Restore(BindingSnapshot snap)
        {
            if (snap == null || snap.Scope == null) return;
            // Write-through restore: BindingSnapshot preserves the previous
            // value (or marks the key as an insertion). We access the scope's
            // internal dict via its Bind method (for replacements) and a new
            // helper <see cref="TypeScope.Remove"/> (for additions).
            foreach (KeyValuePair<string, TypeRef> kv in snap.Prior)
            {
                snap.Scope.Bind(kv.Key, kv.Value);
            }
            foreach (string key in snap.AddedKeys)
            {
                snap.Scope.Remove(key);
            }
        }
    }
}
