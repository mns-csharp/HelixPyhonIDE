using System;
using System.Collections.Generic;
using HelixPythonNamespace.VirtualCompiler.Typing.Stubs;

namespace HelixPythonNamespace.VirtualCompiler.Typing
{
    /// <summary>
    /// Structural subtype check between <see cref="TypeRef"/>s. Answers "is a
    /// value of type <paramref name="source"/> assignable to a parameter of
    /// type <paramref name="target"/>?" Lenient on purpose — any involvement
    /// of <see cref="AnyRef"/> or <see cref="UnknownRef"/> short-circuits to
    /// true so inference gaps don't cause false positives.
    /// </summary>
    public static class SubtypeChecker
    {
        private const int MaxBaseChainDepth = 20;

        /// <summary>
        /// Returns true if a value of <paramref name="source"/> can be used
        /// where a <paramref name="target"/> is expected. See class summary
        /// for the precedence of the rules consulted.
        /// </summary>
        public static bool IsAssignable(TypeRef source, TypeRef target, TypeshedIndex stubs = null)
        {
            // Rule 1: reference equality short-circuit
            if (ReferenceEquals(source, target)) return true;
            if (source == null || target == null) return false;

            // Rule 2: gradual-typing escape hatch — Any or Unknown on either side
            if (source is AnyRef || target is AnyRef) return true;
            if (source is UnknownRef || target is UnknownRef) return true;

            // Rule 2b: a bare `object` SOURCE is treated as gradual (assignable anywhere).
            // In this checker `object` is the fallback type for a value we couldn't infer
            // precisely — notably the return of un-tracked accessors (dict.get()/
            // setdefault()/pop(), subscripts). A real value flowing through such an
            // accessor is almost always a concrete subtype at runtime, so rejecting it
            // against a specific parameter is a false positive (_rouse_dynamics_evidence.py:
            // `a1 = cells.get(...); plot_money_shot(a1, ...)` where the param is `dict`).
            // Mirrors the iter-18 leniency for attribute access on a bare-object receiver.
            if (source is ClassRef so
                && (so.QualifiedName == "object" || so.QualifiedName == "builtins.object"))
                return true;

            // Rule 2c: `object` is the TOP type — every value is assignable to it (PEP 483). A user
            // class instance, None, a builtin, a union, a callable — all are objects. Without this,
            // passing a workspace-class instance to a `def f(x: object)` parameter false-flagged
            // PY6003 (requests/sessions.py:209, dispatch_hook(..., hook_data=r) where r is a
            // Response). Builtins already passed only because their stub chains reach object; a
            // user class's did not, so the check was inconsistent as well as wrong.
            if (target is ClassRef to
                && (to.QualifiedName == "object" || to.QualifiedName == "builtins.object"))
                return true;

            // Rule 3: target Never — nothing is assignable to the bottom type.
            if (target is NeverRef) return false;

            // Rule 4: source Never — bottom is assignable to anything.
            if (source is NeverRef) return true;

            // Rule 4b: a TypeGuard[...] / TypeIs[...] SOURCE is a `bool` at runtime (PEP 647 / 742):
            // a function whose declared return is a type predicate returns True/False, so a
            // `-> bool` function may `return is_something(x)` (fastapi _compat/shared.py:
            // `_annotation_is_sequence(...) -> bool` returns lenient_issubclass(...) whose return is
            // TypeGuard[type[_T]]). The narrowing meaning is caller-side only; as a value it IS bool.
            if (IsTypePredicate(source)
                && target is ClassRef tbool
                && (BareNameEqual(tbool.QualifiedName, "bool") || BareNameEqual(tbool.QualifiedName, "int")))
                return true;

            // Rule 15: TypeVar target absorbs anything at the call site.
            if (target is TypeVarRef) return true;

            // Rule 14: TypeVar source — drop to its bound (or accept if unbound).
            if (source is TypeVarRef sv)
            {
                if (sv.Bound == null) return true;
                return IsAssignable(sv.Bound, target, stubs);
            }

            // Rule 5: source Union — assignable iff EVERY source member fits the
            // target. This is the law `A|B <: T  ⟺  A <: T ∧ B <: T`, and it holds
            // even when the target is itself a union — so it MUST be tried before the
            // target-union rule below. Testing the target-union rule first asked
            // "does the whole source union fit into a *single* target member?", which
            // wrongly rejected `str | None` against an identical `str | None`
            // (None never fits the `str` member) — a large class of the sample workspace
            // fixture's phantom PY6003/PY6005 errors (script_config.py, lcs.py,
            // select_pisces_*.py, crmsd.py), and, via the per-item recursion in the
            // Tuple rule, `Tuple[float | None, ...]` against itself as well.
            if (source is UnionRef su)
            {
                if (su.Members.Count == 0) return false;
                for (int i = 0; i < su.Members.Count; i++)
                {
                    if (!IsAssignable(su.Members[i], target, stubs)) return false;
                }
                return true;
            }

            // Rule 6: target Union — a (non-union) source is assignable iff it fits
            // at least one member.
            if (target is UnionRef tu)
            {
                for (int i = 0; i < tu.Members.Count; i++)
                {
                    if (IsAssignable(source, tu.Members[i], stubs)) return true;
                }
                return false;
            }

            // Rule 7: None handling.
            if (source is NoneRef)
            {
                return target is NoneRef;
            }
            if (target is NoneRef)
            {
                return false;
            }

            // Rule 8: Literal source.
            if (source is LiteralRef lit)
            {
                if (target is LiteralRef tlit)
                {
                    return Equals(lit.Value, tlit.Value);
                }
                if (target is ClassRef tcr)
                {
                    string expected = PythonTypeForLiteral(lit.Value);
                    if (expected == null)
                    {
                        // null value → None is handled earlier via NoneRef, but
                        // LiteralRef(null) also represents None. Not a ClassRef.
                        return false;
                    }
                    if (BareNameEqual(expected, tcr.QualifiedName)) return true;
                    // bool literal assigns to int (bool is-a int) through base walk.
                    return IsInBaseChain(expected, tcr.QualifiedName, stubs);
                }
                return false;
            }

            // Rule 10/11: Generic interactions (check before plain ClassRef).
            if (source is GenericRef sg && target is GenericRef tg)
            {
                // Rule 11: same base class → invariant per arg.
                if (!BareNameEqual(sg.Base.QualifiedName, tg.Base.QualifiedName))
                {
                    // Different bases: assignable only if the source's erasure is in the target's
                    // base chain (list <: Sequence, set <: Collection after the collections.abc
                    // wiring in iter 375).
                    if (!IsInBaseChain(sg.Base.QualifiedName, tg.Base.QualifiedName, stubs))
                        return false;
                    // The standard covariant containers map their element arg(s) positionally onto
                    // the ABC (list[T] -> Sequence[T], dict[K, V] -> Mapping[K, V]). When the
                    // arities line up, check each arg COVARIANTLY (Any stays permissive via Rule 2),
                    // so list[Leaf] satisfies Sequence[Any] and Sequence[Leaf] but NOT Sequence[int]
                    // (black trans.py). When the arities differ (dict[K, V] -> Collection[K]) we lack
                    // the arg mapping, so fall back to gradual erasure-only (element types ignored).
                    if (sg.Args.Count == tg.Args.Count)
                    {
                        for (int i = 0; i < sg.Args.Count; i++)
                            if (!IsAssignable(sg.Args[i], tg.Args[i], stubs)) return false;
                    }
                    return true;
                }
                if (sg.Args.Count != tg.Args.Count) return false;
                for (int i = 0; i < sg.Args.Count; i++)
                {
                    if (!IsAssignable(sg.Args[i], tg.Args[i], stubs)) return false;
                    if (!IsAssignable(tg.Args[i], sg.Args[i], stubs)) return false;
                }
                return true;
            }
            if (source is GenericRef sg2 && target is ClassRef tcr2)
            {
                // Rule 10: erasure — generic is assignable to plain class iff
                // its erasure is in the target's base chain.
                return BareNameEqual(sg2.Base.QualifiedName, tcr2.QualifiedName)
                    || IsInBaseChain(sg2.Base.QualifiedName, tcr2.QualifiedName, stubs);
            }
            if (source is ClassRef scr3 && target is GenericRef tg3)
            {
                // Rule 10 reverse: a plain ClassRef is assignable to a parameterized
                // target if it names the same class OR the target's erasure is in the
                // source's base chain (no arg info to compare, so element types are
                // ignored — gradual). Without the base-chain walk a `list` literal was
                // rejected against a `Sequence[str]` param (io_utils.py:61/76).
                return BareNameEqual(scr3.QualifiedName, tg3.Base.QualifiedName)
                    || IsInBaseChain(scr3.QualifiedName, tg3.Base.QualifiedName, stubs);
            }

            // Rule 9: both ClassRef.
            if (source is ClassRef scr && target is ClassRef tcr3)
            {
                if (BareNameEqual(scr.QualifiedName, tcr3.QualifiedName)) return true;
                // PEP 484 numeric tower: for type-checking, bool <: int <: float <: complex even
                // though there is no runtime subclassing between int and float. So an int argument
                // is accepted where a float is expected (black/rich: `f(3)` against `-> float`).
                if (IsNumericWidening(BareName(scr.QualifiedName), BareName(tcr3.QualifiedName))) return true;
                return IsInBaseChain(scr.QualifiedName, tcr3.QualifiedName, stubs);
            }

            // Rule 12: Callable vs Callable. Covariant return; parameters are treated gradually
            // (precise contravariance is FP-prone and rarely what stress code needs). The source is
            // callable-compatible as long as it can be invoked with the target's positional
            // arguments — i.e. its REQUIRED (non-default, non-*args) param count does not exceed the
            // target's param count. So `(leaf: Leaf) -> bool` satisfies `Callable[[Leaf], bool]`, and
            // `(enc=..., err=...) -> TextIO` (all optional) satisfies `() -> TextIO` (click _compat).
            if (source is CallableRef sc && target is CallableRef tc)
            {
                if (!IsAssignable(sc.ReturnType, tc.ReturnType, stubs)) return false;
                if (sc.IsEllipsisParams || tc.IsEllipsisParams) return true;
                // The source must be invocable with exactly the target's positional arguments: its
                // REQUIRED params must not exceed the target's count, and (absent *args) it must
                // accept at least that many. So `(x) -> int` does NOT satisfy `(x, y) -> int`.
                int required = 0, maxPositional = 0;
                bool hasVarArgs = false;
                foreach (CallableParam p in sc.Params)
                {
                    if (p.Kind == CallableParamKind.VarPositional) { hasVarArgs = true; continue; }
                    if (p.Kind == CallableParamKind.VarKeyword) continue;
                    maxPositional++;
                    if (!p.HasDefault) required++;
                }
                if (required > tc.Params.Count) return false;
                if (!hasVarArgs && maxPositional < tc.Params.Count) return false;
                return true;
            }

            // Rule 13: Tuple vs Tuple.
            if (source is TupleRef st && target is TupleRef tt)
            {
                if (st.Items.Count != tt.Items.Count) return false;
                for (int i = 0; i < st.Items.Count; i++)
                {
                    if (!IsAssignable(st.Items[i], tt.Items[i], stubs)) return false;
                    if (!IsAssignable(tt.Items[i], st.Items[i], stubs)) return false;
                }
                return true;
            }

            // Rule 13b: a bare/unparameterized `tuple` (ClassRef) is assignable to a specific
            // TupleRef and back — gradual typing, element types unknown ≈ Any. Without this a
            // function returning a plain tuple was rejected against a Tuple[np.ndarray, ...] annotation.
            if (source is ClassRef sctup && target is TupleRef && BareNameEqual(sctup.QualifiedName, "tuple"))
                return true;
            if (source is TupleRef && target is ClassRef tctup && BareNameEqual(tctup.QualifiedName, "tuple"))
                return true;

            // Rule 16: default.
            return false;
        }

        // ---- Helpers ----

        // True for a TypeGuard[...] / TypeIs[...] type — a PEP 647/742 type predicate, which is a
        // bool at runtime. Matched on the generic base's last dotted segment (typing.TypeGuard,
        // typing_extensions.TypeIs, ...).
        private static bool IsTypePredicate(TypeRef t)
        {
            GenericRef gr = t as GenericRef;
            if (gr == null || gr.Base == null) return false;
            string qn = gr.Base.QualifiedName;
            if (string.IsNullOrEmpty(qn)) return false;
            int dot = qn.LastIndexOf('.');
            string last = dot < 0 ? qn : qn.Substring(dot + 1);
            return last == "TypeGuard" || last == "TypeIs";
        }

        /// <summary>
        /// Strips module prefix so <c>int</c> and <c>builtins.int</c> compare
        /// equal, and keeps dotted equality intact.
        /// </summary>
        private static bool BareNameEqual(string a, string b)
        {
            if (a == null || b == null) return false;
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            string bareA = NormalizeTypingAlias(BareName(a));
            string bareB = NormalizeTypingAlias(BareName(b));
            return string.Equals(bareA, bareB, StringComparison.Ordinal);
        }

        // typing's capitalized aliases denote the SAME runtime class as the lowercase builtin
        // (typing.Dict is builtins.dict, etc.). Names were compared case-sensitively, so a dict
        // literal ('builtins.dict') was rejected against a 'Dict[str, str]' annotation — the bulk
        // of the remaining phantom PY6003/PY6005 errors on the sample workspace fixture (xml_utils.py, ...).
        private static readonly Dictionary<string, string> TypingAliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Dict", "dict" }, { "List", "list" }, { "Tuple", "tuple" }, { "Set", "set" },
                { "FrozenSet", "frozenset" }, { "Type", "type" }, { "Deque", "deque" },
                { "DefaultDict", "defaultdict" },
            };

        // PEP 484 numeric tower rank: bool(0) <: int(1) <: float(2) <: complex(3). Source is
        // assignable to target when it sits at or below the target's rank. Returns false for any
        // non-numeric name so str/float etc. mismatches still report.
        private static bool IsNumericWidening(string sourceBare, string targetBare)
        {
            int s = NumericRank(sourceBare);
            int t = NumericRank(targetBare);
            return s >= 0 && t >= 0 && s <= t;
        }

        private static int NumericRank(string bare)
        {
            switch (bare)
            {
                case "bool": return 0;
                case "int": return 1;
                case "float": return 2;
                case "complex": return 3;
                default: return -1;
            }
        }

        private static string NormalizeTypingAlias(string bare)
        {
            if (bare != null && TypingAliases.TryGetValue(bare, out string builtin)) return builtin;
            return bare;
        }

        private static string BareName(string qualifiedName)
        {
            if (string.IsNullOrEmpty(qualifiedName)) return qualifiedName;
            int dot = qualifiedName.LastIndexOf('.');
            if (dot < 0) return qualifiedName;
            return qualifiedName.Substring(dot + 1);
        }

        /// <summary>
        /// True iff <paramref name="targetName"/> appears anywhere in the base
        /// chain of <paramref name="sourceName"/> (inclusive of source).
        /// </summary>
        private static bool IsInBaseChain(string sourceName, string targetName, TypeshedIndex stubs)
        {
            foreach (string name in WalkBaseChain(sourceName, stubs))
            {
                if (BareNameEqual(name, targetName)) return true;
            }
            return false;
        }

        /// <summary>
        /// Yields <paramref name="qualifiedName"/> (in both bare and
        /// <c>builtins.</c>-prefixed form) and then every ancestor reachable
        /// through <paramref name="stubs"/>' base-class links. Dedupes via a
        /// <see cref="HashSet{T}"/> and caps depth at 20 to guard against cycles.
        /// Returns just the input (both forms) when <paramref name="stubs"/>
        /// is <c>null</c>.
        /// </summary>
        private static IEnumerable<string> WalkBaseChain(string qualifiedName, TypeshedIndex stubs)
        {
            if (string.IsNullOrEmpty(qualifiedName)) yield break;

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            Queue<KeyValuePair<string, int>> work = new Queue<KeyValuePair<string, int>>();

            string bare = BareName(qualifiedName);
            // Normalize a typing alias (List/typing.List → list) so the base-chain
            // walk resolves the UNDERLYING builtin's stub — otherwise a `List[str]`
            // erased to base "List" never reached list's Iterable base, and a
            // List[str] argument was wrongly rejected against an Iterable parameter.
            string builtinBare = NormalizeTypingAlias(bare);
            bool isAlias = !string.Equals(builtinBare, bare, StringComparison.Ordinal);
            string qualified = (qualifiedName.IndexOf('.') >= 0 && !isAlias)
                ? qualifiedName
                : "builtins." + builtinBare;

            // Emit every form up front so callers can match any of them.
            if (seen.Add(qualifiedName)) yield return qualifiedName;
            if (seen.Add(bare)) yield return bare;
            if (seen.Add(builtinBare)) yield return builtinBare;
            if (seen.Add(qualified)) yield return qualified;

            if (stubs == null) yield break;

            // Start the walk from a BARE name for the no-dot case so ResolveStub tries both builtins
            // AND typing — otherwise a bare typing base (Iterator, Iterable, Mapping, ...) forced to
            // "builtins.Iterator" never resolves, and Iterator[int] was wrongly rejected against an
            // Iterable parameter. Aliases (List) still normalize to their builtin (list); dotted
            // names (os.PathLike) keep their full path.
            string startWork = (qualifiedName.IndexOf('.') >= 0 && !isAlias) ? qualifiedName : builtinBare;
            work.Enqueue(new KeyValuePair<string, int>(startWork, 1));

            while (work.Count > 0)
            {
                KeyValuePair<string, int> cur = work.Dequeue();
                string curName = cur.Key;
                int depth = cur.Value;
                if (depth > MaxBaseChainDepth) continue;

                StubDeclaration decl = ResolveStub(curName, stubs);
                if (decl == null || decl.Kind != StubKind.Class) continue;

                IReadOnlyList<string> baseNames = decl.BaseNames;
                if (baseNames == null) continue;
                for (int i = 0; i < baseNames.Count; i++)
                {
                    string rawBase = baseNames[i];
                    if (string.IsNullOrEmpty(rawBase)) continue;
                    // Skip Protocol/Generic marker bases.
                    if (rawBase == "Protocol" || rawBase == "typing.Protocol") continue;
                    if (rawBase == "Generic" || rawBase == "typing.Generic") continue;

                    string baseBare = BareName(rawBase);
                    string baseQualified = rawBase.IndexOf('.') >= 0 ? rawBase : "builtins." + rawBase;

                    if (seen.Add(rawBase)) yield return rawBase;
                    if (seen.Add(baseBare)) yield return baseBare;
                    if (seen.Add(baseQualified)) yield return baseQualified;

                    // A bare base name is resolved by ResolveStub against BOTH builtins and typing —
                    // forcing "builtins." here made a typing ABC base (MutableMapping, Collection)
                    // unresolvable, so dict/set never reached Mapping/Collection in the chain.
                    string nextName = rawBase.IndexOf('.') >= 0 ? rawBase : baseBare;
                    work.Enqueue(new KeyValuePair<string, int>(nextName, depth + 1));
                }
            }
        }

        /// <summary>
        /// Resolves a class stub by trying both the raw name and a
        /// <c>builtins.</c>-prefixed fallback — base names in stub files often
        /// appear bare (<c>class bool(int):</c>).
        /// </summary>
        private static StubDeclaration ResolveStub(string dottedName, TypeshedIndex stubs)
        {
            if (stubs == null || string.IsNullOrEmpty(dottedName)) return null;
            StubDeclaration d = stubs.Resolve(dottedName);
            if (d != null) return d;
            if (dottedName.IndexOf('.') < 0)
            {
                d = stubs.Resolve("builtins." + dottedName);
                if (d != null) return d;
                // Collections ABCs (Collection, Mapping, MutableMapping, Set, Sequence, ...) live in
                // typing/collections.abc, not builtins, so a bare ABC base resolves here.
                d = stubs.Resolve("typing." + dottedName);
                if (d != null) return d;
            }
            return null;
        }

        /// <summary>
        /// Maps a literal's CLR runtime type to its Python builtin class name.
        /// Returns <c>null</c> for values we don't know how to handle (e.g.
        /// <c>None</c>, which LiteralRef represents with a null Value).
        /// </summary>
        private static string PythonTypeForLiteral(object value)
        {
            if (value == null) return null;
            // Check bool before int — `true is int` in C# via IConvertible but
            // the runtime type is distinct, so "is bool" resolves first here.
            if (value is bool) return "builtins.bool";
            if (value is string) return "builtins.str";
            if (value is byte || value is sbyte
                || value is short || value is ushort
                || value is int || value is uint
                || value is long || value is ulong)
                return "builtins.int";
            if (value is float || value is double || value is decimal)
                return "builtins.float";
            return null;
        }
    }
}
