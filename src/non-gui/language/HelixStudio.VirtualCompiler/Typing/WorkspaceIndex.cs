using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.VirtualCompiler.Typing
{
    /// <summary>
    /// Fully-qualified-name → <see cref="TypeRef"/> registry populated during
    /// the pre-pass of <see cref="VirtualCompiler.CompileSolution"/>. The
    /// <see cref="TypeInferencer"/> consults this index when resolving
    /// <c>Import</c> / <c>ImportFrom</c> targets so that cross-module calls
    /// bind to real callable/class signatures instead of <see cref="AnyRef"/>.
    /// FQNs are module-dotted (e.g. <c>lib_utils.geometry.total_area</c>).
    /// </summary>
    public sealed class WorkspaceIndex
    {
        private readonly Dictionary<string, TypeRef> _byFqn;
        private readonly Dictionary<string, Ast.ClassDef> _classDefs;
        // Reverse index: a base class's SIMPLE name → FQNs of every workspace class that lists it as a
        // direct base. Lets the attribute checker find a mixin's concrete subclasses in OTHER modules
        // (Django's FileProxyMixin lives in core.files.utils; File(FileProxyMixin) is in core.files.base
        // and supplies self.file).
        private readonly Dictionary<string, List<string>> _subclassFqnsByBaseName;
        // Simple class name → every FQN it is registered under. Lets a `pkg.Cls` import that fails an
        // exact lookup be resolved to `pkg.submodule.Cls` — the class is defined in a submodule and
        // re-exported through the package __init__ (Django's django.core.files.storage.FileSystemStorage,
        // actually defined in ...storage.filesystem).
        private readonly Dictionary<string, List<string>> _classFqnsBySimpleName;

        public WorkspaceIndex()
        {
            _byFqn = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            _classDefs = new Dictionary<string, Ast.ClassDef>(StringComparer.Ordinal);
            _subclassFqnsByBaseName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            _classFqnsBySimpleName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        public void Register(string fqn, TypeRef type)
        {
            if (string.IsNullOrEmpty(fqn) || type == null) return;
            _byFqn[fqn] = type;
        }

        public void RegisterClass(string fqn, Ast.ClassDef cls)
        {
            if (string.IsNullOrEmpty(fqn) || cls == null) return;
            _classDefs[fqn] = cls;
            int lastDot = fqn.LastIndexOf('.');
            string simpleName = lastDot >= 0 ? fqn.Substring(lastDot + 1) : fqn;
            List<string> byName;
            if (!_classFqnsBySimpleName.TryGetValue(simpleName, out byName))
                _classFqnsBySimpleName[simpleName] = byName = new List<string>();
            byName.Add(fqn);
            if (cls.Bases != null)
            {
                foreach (Ast.Expr baseExpr in cls.Bases)
                {
                    string simple = BaseSimpleName(baseExpr);
                    if (string.IsNullOrEmpty(simple)) continue;
                    List<string> subs;
                    if (!_subclassFqnsByBaseName.TryGetValue(simple, out subs))
                        _subclassFqnsByBaseName[simple] = subs = new List<string>();
                    subs.Add(fqn);
                }
            }
        }

        /// <summary>FQNs of workspace classes that list <paramref name="baseSimpleName"/> as a direct
        /// base, or an empty list. Matched by simple name (bases are usually written unqualified).</summary>
        public IReadOnlyList<string> SubclassFqnsOf(string baseSimpleName)
        {
            if (string.IsNullOrEmpty(baseSimpleName)) return System.Array.Empty<string>();
            List<string> subs;
            return _subclassFqnsByBaseName.TryGetValue(baseSimpleName, out subs) ? subs : (IReadOnlyList<string>)System.Array.Empty<string>();
        }

        // The last dotted segment of a (possibly subscripted / dotted) base expression.
        private static string BaseSimpleName(Ast.Expr e)
        {
            while (e is Ast.Subscript sub) e = sub.Value;
            if (e is Ast.Name n) return n.Id;
            if (e is Ast.AttributeExpr a) return a.Attr;
            return null;
        }

        public TypeRef Resolve(string fqn)
        {
            if (string.IsNullOrEmpty(fqn)) return null;
            TypeRef t;
            return _byFqn.TryGetValue(fqn, out t) ? t : null;
        }

        public Ast.ClassDef ResolveClassDef(string fqn)
        {
            if (string.IsNullOrEmpty(fqn)) return null;
            Ast.ClassDef c;
            return _classDefs.TryGetValue(fqn, out c) ? c : null;
        }

        /// <summary>
        /// Like <see cref="ResolveClassDef"/>, but when the exact FQN is not registered it follows a
        /// package re-export: a `pkg.Cls` reference resolves to a class named `Cls` defined in a
        /// submodule of `pkg` (`pkg.sub.Cls`). This is how `from pkg import Cls` works when Cls is
        /// defined in a submodule and surfaced through the package __init__.
        /// </summary>
        public Ast.ClassDef ResolveClassDefLoose(string fqn)
        {
            Ast.ClassDef exact = ResolveClassDef(fqn);
            if (exact != null) return exact;
            if (string.IsNullOrEmpty(fqn)) return null;
            int dot = fqn.LastIndexOf('.');
            if (dot <= 0) return null;
            string pkg = fqn.Substring(0, dot);
            string simpleName = fqn.Substring(dot + 1);
            List<string> candidates;
            if (!_classFqnsBySimpleName.TryGetValue(simpleName, out candidates)) return null;
            string prefix = pkg + ".";
            foreach (string cand in candidates)
                if (cand.StartsWith(prefix, StringComparison.Ordinal))
                    return _classDefs[cand];
            return null;
        }

        /// <summary>
        /// Resolve a class by simple name only when the workspace contains exactly one
        /// definition with that name. This supports relative-imported sibling bases during
        /// hierarchy analysis without guessing when a large workspace has collisions.
        /// </summary>
        public string ResolveUniqueClassFqn(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;
            if (!_classFqnsBySimpleName.TryGetValue(simpleName, out List<string> candidates)
                || candidates.Count != 1)
                return null;
            return candidates[0];
        }

        public int Count { get { return _byFqn.Count; } }
    }
}
