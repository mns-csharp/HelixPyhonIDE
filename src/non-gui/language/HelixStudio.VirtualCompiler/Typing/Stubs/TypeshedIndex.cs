using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.VirtualCompiler.Typing.Stubs
{
    /// <summary>
    /// Module-name (dotted) → <see cref="StubModule"/> map. Populated by
    /// <see cref="StubLoader"/>. Lookup keys match Python's own dotted names —
    /// <c>"builtins"</c>, <c>"typing"</c>, <c>"os"</c>, <c>"os.path"</c> etc.
    /// </summary>
    public sealed class TypeshedIndex
    {
        private readonly Dictionary<string, StubModule> _modules;

        public TypeshedIndex()
        {
            _modules = new Dictionary<string, StubModule>(StringComparer.Ordinal);
        }

        public int Count { get { return _modules.Count; } }

        public IEnumerable<string> ModuleNames { get { return _modules.Keys; } }

        public void Add(StubModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            _modules[module.QualifiedName] = module;
        }

        public bool TryGetModule(string qualifiedName, out StubModule module)
        {
            if (string.IsNullOrEmpty(qualifiedName)) { module = null; return false; }
            return _modules.TryGetValue(qualifiedName, out module);
        }

        public StubModule GetModuleOrNull(string qualifiedName)
        {
            StubModule m;
            return _modules.TryGetValue(qualifiedName, out m) ? m : null;
        }

        /// <summary>
        /// Look up a dotted name like <c>builtins.print</c> or <c>typing.List</c>.
        /// Returns <c>null</c> if the module isn't indexed or the name isn't declared.
        /// </summary>
        public StubDeclaration Resolve(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName)) return null;
            int dot = qualifiedName.LastIndexOf('.');
            if (dot <= 0) return null;  // need module.name
            string modName = qualifiedName.Substring(0, dot);
            string local = qualifiedName.Substring(dot + 1);
            StubModule m;
            if (!_modules.TryGetValue(modName, out m)) return null;
            StubDeclaration d;
            return m.TryGet(local, out d) ? d : null;
        }
    }
}
