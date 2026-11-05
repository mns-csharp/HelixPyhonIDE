using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.VirtualCompiler.Typing
{
    /// <summary>
    /// Helpers for building TypeVar → TypeRef binding dictionaries when a
    /// generic class or function is instantiated with concrete type arguments.
    /// </summary>
    public static class GenericBinder
    {
        /// <summary>
        /// Extract TypeVar names from an ordered <paramref name="typeParams"/> list
        /// and pair them with <paramref name="typeArgs"/> positionally. If counts
        /// don't match, the caller should emit an arity diagnostic — this method
        /// binds whatever overlaps and silently ignores the rest.
        /// </summary>
        public static IReadOnlyDictionary<string, TypeRef> Bind(
            IReadOnlyList<TypeVarRef> typeParams,
            IReadOnlyList<TypeRef> typeArgs)
        {
            Dictionary<string, TypeRef> result = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            if (typeParams == null || typeArgs == null) return result;

            int n = typeParams.Count < typeArgs.Count ? typeParams.Count : typeArgs.Count;
            for (int i = 0; i < n; i++)
            {
                TypeVarRef tp = typeParams[i];
                TypeRef ta = typeArgs[i];
                if (tp == null || string.IsNullOrEmpty(tp.Name)) continue;
                // Last write wins if duplicate TypeVar names — matches Python semantics.
                result[tp.Name] = ta;
            }
            return result;
        }

        /// <summary>
        /// Collect every distinct <see cref="TypeVarRef"/> reachable from
        /// <paramref name="type"/>. Used by inferencer to know what needs binding.
        /// Returned in first-occurrence order.
        /// </summary>
        public static IReadOnlyList<TypeVarRef> FreeTypeVars(TypeRef type)
        {
            List<TypeVarRef> ordered = new List<TypeVarRef>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            Collect(type, ordered, seen);
            return ordered;
        }

        private static void Collect(TypeRef type, List<TypeVarRef> ordered, HashSet<string> seen)
        {
            if (type == null) return;

            TypeVarRef tv = type as TypeVarRef;
            if (tv != null)
            {
                if (seen.Add(tv.Name))
                {
                    ordered.Add(tv);
                }
                return;
            }

            GenericRef g = type as GenericRef;
            if (g != null)
            {
                for (int i = 0; i < g.Args.Count; i++) Collect(g.Args[i], ordered, seen);
                return;
            }

            UnionRef u = type as UnionRef;
            if (u != null)
            {
                for (int i = 0; i < u.Members.Count; i++) Collect(u.Members[i], ordered, seen);
                return;
            }

            TupleRef t = type as TupleRef;
            if (t != null)
            {
                for (int i = 0; i < t.Items.Count; i++) Collect(t.Items[i], ordered, seen);
                return;
            }

            CallableRef c = type as CallableRef;
            if (c != null)
            {
                for (int i = 0; i < c.Params.Count; i++) Collect(c.Params[i].Type, ordered, seen);
                Collect(c.ReturnType, ordered, seen);
                return;
            }
            // Leaves (AnyRef, UnknownRef, NoneRef, NeverRef, LiteralRef, ClassRef): nothing to collect.
        }
    }
}
