using System;
using System.Collections.Generic;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Typing
{
    /// <summary>Narrowed type pair for a condition that tests an expression.</summary>
    public struct NarrowResult
    {
        public TypeRef TrueBranch;   // type of the name when condition is truthy
        public TypeRef FalseBranch;  // type of the name when condition is falsy
    }

    /// <summary>
    /// Static utility that computes flow-narrowed types for a name given a
    /// branching condition. Pattern-matches <c>isinstance</c>, <c>is None</c>,
    /// truthy tests, and their boolean compositions. All other conditions
    /// return the input type unchanged.
    /// </summary>
    public static class NarrowingEngine
    {
        /// <summary>
        /// Given the current type of <paramref name="nameBeingTested"/> and the
        /// <paramref name="condition"/> expression controlling a branch, compute the
        /// narrowed types for the true and false branches. Returns the unchanged type
        /// for both branches if no narrowing rule applies.
        /// </summary>
        public static NarrowResult NarrowAtTest(TypeRef currentType, Expr condition, string nameBeingTested)
        {
            if (currentType == null) currentType = UnknownRef.Instance;
            if (condition == null || string.IsNullOrEmpty(nameBeingTested))
            {
                return NoOp(currentType);
            }

            // 4. `not <cond>` — recurse and swap.
            UnaryOpExpr unary = condition as UnaryOpExpr;
            if (unary != null && unary.Op == UnaryOp.Not)
            {
                NarrowResult inner = NarrowAtTest(currentType, unary.Operand, nameBeingTested);
                NarrowResult swapped;
                swapped.TrueBranch = inner.FalseBranch;
                swapped.FalseBranch = inner.TrueBranch;
                return swapped;
            }

            // 5./6. BoolOp: `a and b` chains narrowing on the true branch.
            BoolOpExpr boolOp = condition as BoolOpExpr;
            if (boolOp != null)
            {
                if (boolOp.Op == BoolOp.And)
                {
                    // `a and b` true → BOTH true, so chain the true-narrowing (successive intersection).
                    TypeRef t = currentType;
                    for (int i = 0; i < boolOp.Values.Count; i++)
                    {
                        NarrowResult step = NarrowAtTest(t, boolOp.Values[i], nameBeingTested);
                        t = step.TrueBranch;
                    }
                    // FALSE → not(a and b …) = (not a) or (not b) …: the UNION of each operand's
                    // false-branch, each computed independently from currentType. Sound for a single
                    // tested name, and it recovers the narrowing on the fall-through of a guard whose
                    // body returns — click exceptions.py:23:
                    //   if x is not None and not isinstance(x, str): return " / ".join(...)
                    //   return x    # x is `str | None` here, not `Sequence[str] | str | None`
                    // An operand that leaves the name fully wide on its false side (NoOp → currentType)
                    // makes the union no narrower than currentType, so we bail to no narrowing.
                    TypeRef f = null;
                    for (int i = 0; i < boolOp.Values.Count; i++)
                    {
                        NarrowResult step = NarrowAtTest(currentType, boolOp.Values[i], nameBeingTested);
                        if (ReferenceEquals(step.FalseBranch, currentType)) { f = currentType; break; }
                        f = f == null ? step.FalseBranch : UnionOfBranches(f, step.FalseBranch);
                    }
                    NarrowResult r;
                    r.TrueBranch = t;
                    r.FalseBranch = f ?? currentType;
                    return r;
                }
                // `a or b` — dual of `and` by De Morgan: FALSE means BOTH operands are false, so
                // chain the false-narrowing across them (`if x is None or x.flag: return` leaves x
                // non-None in the fall-through, rich style.py:737). True can't be attributed.
                {
                    TypeRef f = currentType;
                    for (int i = 0; i < boolOp.Values.Count; i++)
                    {
                        NarrowResult step = NarrowAtTest(f, boolOp.Values[i], nameBeingTested);
                        f = step.FalseBranch;
                    }
                    NarrowResult r;
                    r.TrueBranch = currentType;
                    r.FalseBranch = f;
                    return r;
                }
            }

            // 1. isinstance(x, T) / isinstance(x, (T1, T2))
            Call call = condition as Call;
            if (call != null && IsIsInstanceCall(call))
            {
                if (call.Args.Count >= 2)
                {
                    if (MatchesTarget(call.Args[0], nameBeingTested))
                    {
                        IReadOnlyList<string> typeNames = ExtractTestedTypeNames(call.Args[1]);
                        if (typeNames != null && typeNames.Count > 0)
                        {
                            return NarrowByIsInstance(currentType, typeNames);
                        }
                    }
                }
                return NoOp(currentType);
            }

            // 2. `x is None` / `x is not None`
            Compare cmp = condition as Compare;
            if (cmp != null && cmp.Ops.Count == 1 && cmp.Comparators.Count == 1)
            {
                CmpOp op = cmp.Ops[0];
                if (op == CmpOp.Is || op == CmpOp.IsNot)
                {
                    if (MatchesTarget(cmp.Left, nameBeingTested) && IsNoneConstant(cmp.Comparators[0]))
                    {
                        NarrowResult isNone = NarrowIsNone(currentType);
                        if (op == CmpOp.IsNot)
                        {
                            NarrowResult swapped;
                            swapped.TrueBranch = isNone.FalseBranch;
                            swapped.FalseBranch = isNone.TrueBranch;
                            return swapped;
                        }
                        return isNone;
                    }
                }
            }

            // 2b. Membership test: `x in {"a", "b"}` / `x in ("a", "b")` / `x in ["a", "b"]` over a
            // literal container narrows x on the TRUE branch to those literals — dropping None and any
            // other member (fastapi routing.py:1888, `if fallback in {"index.html", "404.html"}:` where
            // fallback is Literal[...] | None, then passed to a `str` parameter). Only a container of
            // constant literals is handled; a variable/computed container imposes no static narrowing.
            if (cmp != null && cmp.Ops.Count == 1 && cmp.Comparators.Count == 1 && cmp.Ops[0] == CmpOp.In
                && MatchesTarget(cmp.Left, nameBeingTested))
            {
                TypeRef literals = LiteralUnionOfContainer(cmp.Comparators[0]);
                if (literals != null)
                {
                    NarrowResult r;
                    r.TrueBranch = literals;      // x is one of the container's literals here
                    r.FalseBranch = currentType;  // outside the set we know nothing more
                    return r;
                }
            }

            // 3. Truthy test: `if x:` / `if self.x:` — condition IS the tested name or attribute path.
            if (MatchesTarget(condition, nameBeingTested))
            {
                NarrowResult r;
                r.TrueBranch = RemoveFalsyFromType(currentType);
                r.FalseBranch = currentType;
                return r;
            }

            return NoOp(currentType);
        }

        // The tested target is either a bare name ("x") or a dotted attribute path ("self.metavar",
        // "a.b.c"). An operand matches when it is that Name, or an AttributeExpr chain rooted at a
        // Name whose full dotted path equals the target. This lets `if self.metavar is not None:`
        // narrow the attribute the same way `if x is not None:` narrows a local (iter 370).
        internal static bool MatchesTarget(Expr operand, string target)
        {
            if (operand is Name n) return n.Id == target;
            if (operand is AttributeExpr) return DottedPath(operand) == target;
            return false;
        }

        /// <summary>Dotted path of a Name/AttributeExpr chain ("self.metavar"), or null if the chain
        /// bottoms out in anything other than a plain Name (a call, subscript, ...).</summary>
        internal static string DottedPath(Expr e)
        {
            if (e is Name nm) return nm.Id;
            AttributeExpr a = e as AttributeExpr;
            if (a == null) return null;
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string> { a.Attr };
            Expr cur = a.Value;
            while (cur is AttributeExpr inner) { parts.Add(inner.Attr); cur = inner.Value; }
            if (!(cur is Name root)) return null;
            parts.Add(root.Id);
            parts.Reverse();
            return string.Join(".", parts);
        }

        // ---------- Helpers ----------

        private static NarrowResult NoOp(TypeRef t)
        {
            NarrowResult r;
            r.TrueBranch = t;
            r.FalseBranch = t;
            return r;
        }

        private static bool IsIsInstanceCall(Call call)
        {
            Name n = call.Func as Name;
            if (n != null && n.Id == "isinstance") return true;
            // Also accept `builtins.isinstance` attribute access.
            AttributeExpr attr = call.Func as AttributeExpr;
            if (attr != null && attr.Attr == "isinstance") return true;
            return false;
        }

        private static bool IsNoneConstant(Expr e)
        {
            Constant c = e as Constant;
            if (c != null && c.Value == null) return true;
            // `None` as a Name also shows up in some pipelines — accept both.
            Name n = e as Name;
            if (n != null && n.Id == "None") return true;
            return false;
        }

        /// <summary>Extracts bare class names from the second arg of an isinstance call.</summary>
        internal static IReadOnlyList<string> ExtractTestedTypeNames(Expr arg)
        {
            List<string> result = new List<string>();
            TupleExpr tup = arg as TupleExpr;
            if (tup != null)
            {
                for (int i = 0; i < tup.Elts.Count; i++)
                {
                    string name = TryExtractTypeName(tup.Elts[i]);
                    if (name != null) result.Add(name);
                }
                return result;
            }
            string single = TryExtractTypeName(arg);
            if (single != null) result.Add(single);
            return result;
        }

        // The union of literal types of a set / list / tuple display whose elements are ALL constant
        // literals — `{"a", "b"}` → Literal["a"] | Literal["b"]. Returns null when the expression is
        // not such a container, or any element is not a constant (so no unsound narrowing is applied).
        private static TypeRef LiteralUnionOfContainer(Expr container)
        {
            IReadOnlyList<Expr> elts = null;
            if (container is SetExpr se) elts = se.Elts;
            else if (container is ListExpr le) elts = le.Elts;
            else if (container is TupleExpr te) elts = te.Elts;
            if (elts == null || elts.Count == 0) return null;

            List<TypeRef> lits = new List<TypeRef>(elts.Count);
            foreach (Expr e in elts)
            {
                Constant c = e as Constant;
                if (c == null || c.Value == null) return null;   // non-literal (or None) element → bail
                lits.Add(new LiteralRef(c.Value));
            }
            return lits.Count == 1 ? lits[0] : new UnionRef(lits);
        }

        private static string TryExtractTypeName(Expr e)
        {
            Name n = e as Name;
            if (n != null && !string.IsNullOrEmpty(n.Id))
                return LooksLikeRuntimeVariable(n.Id) ? null : n.Id;
            AttributeExpr attr = e as AttributeExpr;
            if (attr != null && !string.IsNullOrEmpty(attr.Attr))
                return LooksLikeRuntimeVariable(attr.Attr) ? null : attr.Attr;
            return null;
        }

        /// <summary>
        /// True when a name used as the class argument of <c>isinstance</c> is a snake_case runtime
        /// variable rather than a static class reference — e.g. pydantic's
        /// <c>isinstance(alias, allowed_types)</c>, where <c>allowed_types</c> is a
        /// <c>tuple[type[...], ...]</c> parameter. A lowercase name containing an underscore is never
        /// a real class or builtin (those are PascalCase like <c>AliasPath</c> or bare lowercase
        /// builtins like <c>str</c>/<c>int</c>). Skipping narrowing here is safe: it can only leave a
        /// value's type broader, which removes false positives, never introduces one — whereas
        /// narrowing to a fabricated <c>ClassRef("allowed_types")</c> makes the return unassignable.
        /// </summary>
        private static bool LooksLikeRuntimeVariable(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('_') < 0) return false;
            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsUpper(name[i])) return false; // any uppercase → treat as a real (dunder/class) name
            }
            return true;
        }

        /// <summary>True if <paramref name="t"/> is a ClassRef whose QN equals bareName or ends with "." + bareName.</summary>
        internal static bool MatchesClassName(TypeRef t, string bareName)
        {
            if (string.IsNullOrEmpty(bareName)) return false;
            ClassRef cr = t as ClassRef;
            if (cr == null)
            {
                // Genericized class: list[int] matches "list"
                GenericRef gr = t as GenericRef;
                if (gr != null) cr = gr.Base;
            }
            if (cr == null) return false;
            string qn = cr.QualifiedName;
            if (qn == bareName) return true;
            if (qn.Length > bareName.Length &&
                qn.EndsWith(bareName, StringComparison.Ordinal) &&
                qn[qn.Length - bareName.Length - 1] == '.')
            {
                return true;
            }
            // Also: tested name might itself be dotted, or be the qualified name of the ClassRef.
            if (bareName.Length > qn.Length &&
                bareName.EndsWith(qn, StringComparison.Ordinal) &&
                bareName[bareName.Length - qn.Length - 1] == '.')
            {
                return true;
            }
            return false;
        }

        private static bool MatchesAnyTestedName(TypeRef t, IReadOnlyList<string> names)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (MatchesClassName(t, names[i])) return true;
            }
            return false;
        }

        private static NarrowResult NarrowByIsInstance(TypeRef currentType, IReadOnlyList<string> typeNames)
        {
            UnionRef u = currentType as UnionRef;
            if (u != null)
            {
                List<TypeRef> kept = new List<TypeRef>();
                List<TypeRef> removed = new List<TypeRef>();
                for (int i = 0; i < u.Members.Count; i++)
                {
                    TypeRef m = u.Members[i];
                    if (MatchesAnyTestedName(m, typeNames)) kept.Add(m);
                    else removed.Add(m);
                }

                NarrowResult r;
                r.TrueBranch = kept.Count > 0 ? Collapse(kept) : BuildFromTestedNames(typeNames);
                r.FalseBranch = removed.Count > 0 ? Collapse(removed) : NeverRef.Instance;
                return r;
            }

            // Non-union case
            NarrowResult s;
            if (MatchesAnyTestedName(currentType, typeNames))
            {
                s.TrueBranch = currentType;
                s.FalseBranch = NeverRef.Instance;
            }
            else if (currentType is AnyRef || currentType is UnknownRef)
            {
                s.TrueBranch = BuildFromTestedNames(typeNames);
                s.FalseBranch = currentType;
            }
            else
            {
                // currentType is unrelated to the tested types — still narrow true to tested,
                // leave false unchanged (caller's type isn't compatible; keep as-is for MVP).
                s.TrueBranch = BuildFromTestedNames(typeNames);
                s.FalseBranch = currentType;
            }
            return s;
        }

        private static TypeRef BuildFromTestedNames(IReadOnlyList<string> typeNames)
        {
            if (typeNames.Count == 1) return new ClassRef(typeNames[0]);
            List<TypeRef> items = new List<TypeRef>(typeNames.Count);
            for (int i = 0; i < typeNames.Count; i++)
            {
                items.Add(new ClassRef(typeNames[i]));
            }
            return new UnionRef(items);
        }

        private static TypeRef Collapse(List<TypeRef> members)
        {
            if (members == null || members.Count == 0) return NeverRef.Instance;
            if (members.Count == 1) return members[0];
            return new UnionRef(members);
        }

        // Union of two (possibly already-union) narrowing branches, flattened and de-duplicated,
        // dropping Never. Used for the `and` false branch (De Morgan union of per-operand falses).
        private static TypeRef UnionOfBranches(TypeRef a, TypeRef b)
        {
            List<TypeRef> members = new List<TypeRef>();
            AddUnionMembers(a, members);
            AddUnionMembers(b, members);
            return Collapse(members);
        }

        private static void AddUnionMembers(TypeRef t, List<TypeRef> acc)
        {
            if (t == null || t is NeverRef) return;
            UnionRef u = t as UnionRef;
            if (u != null)
            {
                for (int i = 0; i < u.Members.Count; i++) AddUnionMembers(u.Members[i], acc);
                return;
            }
            string key = t.ToString();
            for (int i = 0; i < acc.Count; i++)
                if (acc[i].ToString() == key) return;   // dedup by canonical string form
            acc.Add(t);
        }

        private static NarrowResult NarrowIsNone(TypeRef currentType)
        {
            NarrowResult r;
            UnionRef u = currentType as UnionRef;
            if (u != null)
            {
                List<TypeRef> nonNone = new List<TypeRef>();
                bool hadNone = false;
                for (int i = 0; i < u.Members.Count; i++)
                {
                    if (u.Members[i] is NoneRef) { hadNone = true; continue; }
                    nonNone.Add(u.Members[i]);
                }
                r.TrueBranch = NoneRef.Instance;
                r.FalseBranch = hadNone ? Collapse(nonNone) : currentType;
                return r;
            }

            if (currentType is NoneRef)
            {
                r.TrueBranch = NoneRef.Instance;
                r.FalseBranch = NeverRef.Instance;
                return r;
            }

            // currentType doesn't include None: True=None, False=unchanged.
            r.TrueBranch = NoneRef.Instance;
            r.FalseBranch = currentType;
            return r;
        }

        /// <summary>Removes a union member matching <paramref name="remove"/>; collapses to scalar or NeverRef.</summary>
        internal static TypeRef RemoveFromUnion(UnionRef u, Predicate<TypeRef> remove)
        {
            if (u == null) return NeverRef.Instance;
            List<TypeRef> kept = new List<TypeRef>();
            for (int i = 0; i < u.Members.Count; i++)
            {
                if (!remove(u.Members[i])) kept.Add(u.Members[i]);
            }
            return Collapse(kept);
        }

        private static bool IsKnownFalsyLiteral(TypeRef t)
        {
            LiteralRef lit = t as LiteralRef;
            if (lit == null) return false;
            if (lit.Value == null) return true;
            if (lit.Value is bool b) return b == false;
            if (lit.Value is int i) return i == 0;
            if (lit.Value is long l) return l == 0L;
            if (lit.Value is string s) return s.Length == 0;
            return false;
        }

        private static TypeRef RemoveFalsyFromType(TypeRef currentType)
        {
            UnionRef u = currentType as UnionRef;
            if (u == null)
            {
                // Scalar NoneRef on its own -> Never in truthy branch.
                if (currentType is NoneRef) return NeverRef.Instance;
                if (IsKnownFalsyLiteral(currentType)) return NeverRef.Instance;
                return currentType;
            }
            return RemoveFromUnion(u, delegate(TypeRef m)
            {
                if (m is NoneRef) return true;
                if (IsKnownFalsyLiteral(m)) return true;
                return false;
            });
        }
    }
}
