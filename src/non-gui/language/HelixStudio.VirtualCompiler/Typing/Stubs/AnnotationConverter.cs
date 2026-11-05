using System.Collections.Generic;
using HelixPythonNamespace.VirtualCompiler.Ast;

namespace HelixPythonNamespace.VirtualCompiler.Typing.Stubs
{
    /// <summary>
    /// Converts annotation expressions from the AST into <see cref="TypeRef"/>s
    /// without consulting any external context. Names are emitted as bare
    /// <see cref="ClassRef"/>s — phase-3 resolution substitutes qualified names
    /// once the import context is known.
    /// </summary>
    internal static class AnnotationConverter
    {
        /// <summary>
        /// Convert an annotation expression (e.g. <c>list[int]</c>, <c>Optional[str]</c>)
        /// to a <see cref="TypeRef"/>. Returns <see cref="UnknownRef"/> on any case
        /// the phase-2 converter can't handle yet (higher-kinded, Callable[..., R]
        /// with complex param lists, forward-ref strings referencing nested scopes).
        /// </summary>
        public static TypeRef Convert(Expr annotation)
        {
            if (annotation == null) return UnknownRef.Instance;

            switch (annotation)
            {
                case Name n:
                    // A bare typing qualifier (`x: Final = ...`, `x: ClassVar = ...`) constrains
                    // nothing about the value — it says "constant"/"class variable" of whatever type
                    // the value has. Treating it as a class named "Final" made every such assignment
                    // a PY6008 false positive on valid, heavily-typed code (all of requests' module
                    // constants). UnknownRef imposes no constraint, so the check is skipped.
                    if (IsBareQualifier(n.Id)) return UnknownRef.Instance;
                    return NameToRef(n.Id);

                case Constant c:
                    {
                        // Forward reference: `x: "Foo"` — treat the string as a name.
                        if (c.Value is string s && !string.IsNullOrEmpty(s)) return new ClassRef(s);
                        if (c.Value == null) return NoneRef.Instance;
                        return UnknownRef.Instance;
                    }

                case AttributeExpr a:
                    {
                        string qname = DottedNameOf(a);
                        if (qname != null)
                        {
                            string last = LastSegment(qname);
                            // Same for a qualified bare qualifier: typing.Final, t.ClassVar, _t.Final.
                            if (IsBareQualifier(last)) return UnknownRef.Instance;
                            // A qualified typing special-form is the special form, not a class named
                            // "t.Any". click does `import typing as t`, so `-> t.Any` / `x: t.Any`
                            // reached here and became ClassRef("t.Any"); the gradual Any was lost and
                            // every `return <concrete>` under a `-> t.Any` false-flagged PY6005.
                            // NameToRef already handled the bare names; mirror it for the dotted form.
                            TypeRef special = SpecialFormRef(last);
                            if (special != null) return special;
                        }
                        return qname != null ? (TypeRef)new ClassRef(qname) : UnknownRef.Instance;
                    }

                case Subscript sub:
                    return ConvertSubscript(sub);

                case BinOpExpr bi when bi.Op == BinOp.BitOr:
                    // PEP 604: X | Y == Union[X, Y]
                    return FlattenUnion(bi);

                case Ast.TupleExpr tup:
                    {
                        // e.g. inside Callable[..., R] second slot, or tuple[...] items
                        List<TypeRef> items = new List<TypeRef>(tup.Elts.Count);
                        foreach (Expr el in tup.Elts) items.Add(Convert(el));
                        return new TupleRef(items, isVariadic: false);
                    }

                default:
                    return UnknownRef.Instance;
            }
        }

        // The special TypeRef for a typing special-form name, or null for an ordinary class name.
        // Shared by the bare-name and dotted-name (typing.Any / t.Any) annotation paths.
        private static TypeRef SpecialFormRef(string name)
        {
            switch (name)
            {
                case "None": return NoneRef.Instance;
                case "Any": return AnyRef.Instance;
                case "Never": case "NoReturn": return NeverRef.Instance;
                default: return null;
            }
        }

        private static TypeRef NameToRef(string id)
        {
            TypeRef special = SpecialFormRef(id);
            if (special != null) return special;
            // Common private TypeVar spellings in library code and typeshed. The
            // context-free converter cannot see the defining `TypeVar(...)` assignment
            // when a callable crosses a module boundary, so preserve these conventional
            // names as type variables instead of concrete classes.
            if (LooksLikePrivateTypeVar(id)) return new TypeVarRef(id);
            switch (id)
            {
                default: return new ClassRef(id);
            }
        }

        private static bool LooksLikePrivateTypeVar(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 2
                || id[0] != '_' || !char.IsUpper(id[1]))
                return false;
            for (int i = 2; i < id.Length; i++)
            {
                char c = id[i];
                if (char.IsUpper(c) || char.IsDigit(c) || c == '_') continue;
                string suffix = id.Substring(i);
                return suffix == "co" || suffix == "contra";
            }
            return true;
        }

        private static TypeRef ConvertSubscript(Subscript sub)
        {
            string baseName = BaseNameOf(sub.Value);
            if (baseName == null) return UnknownRef.Instance;

            // Unwrap the slice into a list of arg expressions.
            List<Expr> args = new List<Expr>();
            if (sub.Slice is Ast.TupleExpr tup)
            {
                foreach (Expr e in tup.Elts) args.Add(e);
            }
            else
            {
                args.Add(sub.Slice);
            }

            // Typing qualifiers that wrap a real type: for assignment-checking they reduce to that
            // type. Final[int] IS int (5 is assignable), ClassVar[str] IS str, Annotated[T, meta] IS
            // T. Matched on the last dotted segment so typing.Final / _t.Final work too.
            // Special forms matched on the last dotted segment, so the qualified spellings
            // (typing.Optional[int], t.Union[...], t.Literal[...], _t.Final[T]) resolve the same as
            // the bare ones — click and requests both alias `typing as t` / `import _types as _t`.
            string qualifier = LastSegment(baseName);
            if (qualifier == "Final" || qualifier == "ClassVar" || qualifier == "Annotated")
            {
                return args.Count >= 1 ? Convert(args[0]) : UnknownRef.Instance;
            }

            switch (qualifier)
            {
                case "Union":
                    {
                        List<TypeRef> members = new List<TypeRef>(args.Count);
                        foreach (Expr e in args) members.Add(Convert(e));
                        return new UnionRef(members);
                    }
                case "Optional":
                    {
                        // Optional[T] == Union[T, None]
                        if (args.Count == 0) return NoneRef.Instance;
                        TypeRef inner = Convert(args[0]);
                        return new UnionRef(new[] { inner, (TypeRef)NoneRef.Instance });
                    }
                case "Literal":
                    {
                        if (args.Count == 1) return LiteralFromExpr(args[0]);
                        List<TypeRef> lits = new List<TypeRef>(args.Count);
                        foreach (Expr e in args) lits.Add(LiteralFromExpr(e));
                        return new UnionRef(lits);
                    }
                case "Callable":
                    {
                        // Callable[[P1, P2], Ret] -> a CallableRef with those positional params;
                        // Callable[..., Ret] and Callable[ParamSpec, Ret] -> ellipsis params (gradual
                        // on parameters). Without this the annotation collapsed to GenericRef(Callable,
                        // [Unknown, Ret]) and a real function argument (`(leaf: Leaf) -> bool`) was
                        // rejected against it (black lines.py:1158).
                        TypeRef ret = args.Count >= 2 ? Convert(args[args.Count - 1]) : UnknownRef.Instance;
                        Expr paramSpec = args.Count >= 1 ? args[0] : null;
                        if (paramSpec is Ast.ListExpr plist)
                        {
                            var ps = new List<CallableParam>(plist.Elts.Count);
                            foreach (Expr pe in plist.Elts)
                                ps.Add(new CallableParam(string.Empty, Convert(pe), CallableParamKind.PositionalOnly, false));
                            return new CallableRef(ps, ret);
                        }
                        // `...`, a ParamSpec, or any non-list param spec -> gradual (ellipsis params).
                        return new CallableRef(new List<CallableParam>(), ret, isEllipsisParams: true);
                    }
                default:
                    {
                        List<TypeRef> typeArgs = new List<TypeRef>(args.Count);
                        foreach (Expr e in args) typeArgs.Add(Convert(e));
                        return new GenericRef(new ClassRef(baseName), typeArgs);
                    }
            }
        }

        private static TypeRef LiteralFromExpr(Expr e)
        {
            if (e is Constant c) return new LiteralRef(c.Value);
            return UnknownRef.Instance;
        }

        private static UnionRef FlattenUnion(BinOpExpr bi)
        {
            List<TypeRef> members = new List<TypeRef>();
            Collect(bi, members);
            return new UnionRef(members);
        }

        private static void Collect(Expr e, List<TypeRef> acc)
        {
            if (e is BinOpExpr b && b.Op == BinOp.BitOr)
            {
                Collect(b.Left, acc);
                Collect(b.Right, acc);
            }
            else
            {
                acc.Add(Convert(e));
            }
        }

        // Bare typing qualifiers that carry no type argument, so impose no constraint on the value.
        // TypeAlias (PEP 613) marks the assignment itself as a type-alias definition — the RHS is a
        // type expression or a forward-ref STRING, not a value of any particular type — so
        // `MyAlias: TypeAlias = "ForwardRef"` must not be checked as str-vs-TypeAlias (pydantic).
        private static bool IsBareQualifier(string id)
        {
            return id == "Final" || id == "ClassVar" || id == "TypeAlias";
        }

        // Last dotted segment: "typing.Final" -> "Final", "Final" -> "Final".
        private static string LastSegment(string dotted)
        {
            if (string.IsNullOrEmpty(dotted)) return dotted;
            int dot = dotted.LastIndexOf('.');
            return dot < 0 ? dotted : dotted.Substring(dot + 1);
        }

        private static string BaseNameOf(Expr e)
        {
            if (e is Name n) return n.Id;
            if (e is AttributeExpr a) return DottedNameOf(a);
            return null;
        }

        private static string DottedNameOf(AttributeExpr a)
        {
            // a.b.c → "a.b.c" when the chain is all attribute/name.
            List<string> parts = new List<string>();
            parts.Add(a.Attr);
            Expr cur = a.Value;
            while (cur is AttributeExpr inner)
            {
                parts.Add(inner.Attr);
                cur = inner.Value;
            }
            if (!(cur is Name nm)) return null;
            parts.Add(nm.Id);
            parts.Reverse();
            return string.Join(".", parts);
        }
    }
}
