using System;
using System.Collections.Generic;

namespace HelixPythonNamespace.VirtualCompiler.Typing
{
    /// <summary>
    /// Substitutes <see cref="TypeVarRef"/> occurrences in a <see cref="TypeRef"/>
    /// tree with concrete types from a binding map. Pure function — the input
    /// tree is never mutated; a new tree is returned when substitutions apply,
    /// and the original is returned unchanged when none do.
    /// </summary>
    public static class GenericSubstitution
    {
        /// <summary>
        /// Walk <paramref name="input"/> replacing any <see cref="TypeVarRef"/> whose
        /// <see cref="TypeVarRef.Name"/> is a key in <paramref name="bindings"/> with
        /// the bound type. Unbound TypeVarRefs pass through unchanged. Returns the
        /// original reference identity when no substitutions are performed, so
        /// callers can cheaply check <c>ReferenceEquals(result, input)</c>.
        /// </summary>
        public static TypeRef Substitute(TypeRef input, IReadOnlyDictionary<string, TypeRef> bindings)
        {
            if (input == null) return null;
            if (bindings == null || bindings.Count == 0) return input;

            // Leaf categories without TypeVars inside — always identity.
            if (input is AnyRef) return input;
            if (input is UnknownRef) return input;
            if (input is NoneRef) return input;
            if (input is NeverRef) return input;
            if (input is LiteralRef) return input;
            if (input is ClassRef) return input;

            TypeVarRef tv = input as TypeVarRef;
            if (tv != null)
            {
                TypeRef bound;
                if (bindings.TryGetValue(tv.Name, out bound) && bound != null)
                {
                    return bound;
                }
                return input;
            }

            GenericRef g = input as GenericRef;
            if (g != null)
            {
                int count = g.Args.Count;
                TypeRef[] newArgs = null;
                for (int i = 0; i < count; i++)
                {
                    TypeRef original = g.Args[i];
                    TypeRef replaced = Substitute(original, bindings);
                    if (!ReferenceEquals(replaced, original) && newArgs == null)
                    {
                        newArgs = new TypeRef[count];
                        for (int j = 0; j < i; j++) newArgs[j] = g.Args[j];
                    }
                    if (newArgs != null) newArgs[i] = replaced;
                }
                if (newArgs == null) return input;
                return new GenericRef(g.Base, newArgs);
            }

            UnionRef u = input as UnionRef;
            if (u != null)
            {
                int count = u.Members.Count;
                TypeRef[] newMembers = null;
                for (int i = 0; i < count; i++)
                {
                    TypeRef original = u.Members[i];
                    TypeRef replaced = Substitute(original, bindings);
                    if (!ReferenceEquals(replaced, original) && newMembers == null)
                    {
                        newMembers = new TypeRef[count];
                        for (int j = 0; j < i; j++) newMembers[j] = u.Members[j];
                    }
                    if (newMembers != null) newMembers[i] = replaced;
                }
                if (newMembers == null) return input;
                return new UnionRef(newMembers);
            }

            TupleRef t = input as TupleRef;
            if (t != null)
            {
                int count = t.Items.Count;
                TypeRef[] newItems = null;
                for (int i = 0; i < count; i++)
                {
                    TypeRef original = t.Items[i];
                    TypeRef replaced = Substitute(original, bindings);
                    if (!ReferenceEquals(replaced, original) && newItems == null)
                    {
                        newItems = new TypeRef[count];
                        for (int j = 0; j < i; j++) newItems[j] = t.Items[j];
                    }
                    if (newItems != null) newItems[i] = replaced;
                }
                if (newItems == null) return input;
                return new TupleRef(newItems, t.IsVariadic);
            }

            CallableRef c = input as CallableRef;
            if (c != null)
            {
                int count = c.Params.Count;
                CallableParam[] newParams = null;
                for (int i = 0; i < count; i++)
                {
                    CallableParam p = c.Params[i];
                    TypeRef replacedType = Substitute(p.Type, bindings);
                    if (!ReferenceEquals(replacedType, p.Type) && newParams == null)
                    {
                        newParams = new CallableParam[count];
                        for (int j = 0; j < i; j++) newParams[j] = c.Params[j];
                    }
                    if (newParams != null)
                    {
                        newParams[i] = ReferenceEquals(replacedType, p.Type)
                            ? p
                            : new CallableParam(p.Name, replacedType, p.Kind, p.HasDefault);
                    }
                }

                TypeRef newReturn = Substitute(c.ReturnType, bindings);
                bool returnChanged = !ReferenceEquals(newReturn, c.ReturnType);

                if (newParams == null && !returnChanged) return input;

                IReadOnlyList<CallableParam> finalParams = newParams != null
                    ? (IReadOnlyList<CallableParam>)newParams
                    : c.Params;
                return new CallableRef(finalParams, newReturn, c.IsEllipsisParams);
            }

            // Unknown subclass — conservative passthrough.
            return input;
        }
    }
}
