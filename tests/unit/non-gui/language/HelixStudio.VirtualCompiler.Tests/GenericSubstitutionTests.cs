using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler.Typing;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Unit tests for <see cref="GenericSubstitution"/> and <see cref="GenericBinder"/>.
    /// Verify identity preservation when no substitution applies, structural
    /// reconstruction when it does, and positional binding semantics.
    /// </summary>
    [TestClass]
    public class GenericSubstitutionTests
    {
        private static IReadOnlyDictionary<string, TypeRef> Map(params object[] pairs)
        {
            Dictionary<string, TypeRef> d = new Dictionary<string, TypeRef>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                d[(string)pairs[i]] = (TypeRef)pairs[i + 1];
            }
            return d;
        }

        private static ClassRef Int() { return new ClassRef("builtins.int"); }
        private static ClassRef Str() { return new ClassRef("builtins.str"); }
        private static ClassRef Bool() { return new ClassRef("builtins.bool"); }
        private static ClassRef ListC() { return new ClassRef("builtins.list"); }
        private static ClassRef DictC() { return new ClassRef("builtins.dict"); }

        // 1. Leaf types pass through unchanged (reference-equal).
        [TestMethod]
        public void Substitute_AnyRef_PassesThrough()
        {
            TypeRef input = AnyRef.Instance;
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", Int()));
            Assert.IsTrue(ReferenceEquals(input, output));
        }

        [TestMethod]
        public void Substitute_NoneRef_PassesThrough()
        {
            TypeRef input = NoneRef.Instance;
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", Int()));
            Assert.IsTrue(ReferenceEquals(input, output));
        }

        [TestMethod]
        public void Substitute_ClassRef_PassesThrough()
        {
            TypeRef input = new ClassRef("builtins.int");
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", Str()));
            Assert.IsTrue(ReferenceEquals(input, output));
        }

        // 2. TypeVarRef with binding replaced.
        [TestMethod]
        public void Substitute_TypeVar_Bound_ReplacedWithTarget()
        {
            ClassRef target = Int();
            TypeRef input = new TypeVarRef("T");
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", target));
            Assert.IsTrue(ReferenceEquals(target, output));
        }

        // 3. Unbound TypeVarRef passes through.
        [TestMethod]
        public void Substitute_TypeVar_Unbound_ReferenceEqual()
        {
            TypeVarRef input = new TypeVarRef("U");
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", Int()));
            Assert.IsTrue(ReferenceEquals(input, output));
        }

        // 4. GenericRef with substitutable arg returns new instance.
        [TestMethod]
        public void Substitute_Generic_WithBoundArg_ReturnsNew()
        {
            GenericRef input = new GenericRef(ListC(), new TypeRef[] { new TypeVarRef("T") });
            ClassRef target = Str();
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", target));
            Assert.IsFalse(ReferenceEquals(input, output));
            GenericRef g = output as GenericRef;
            Assert.IsNotNull(g);
            Assert.AreEqual("builtins.list", g.Base.QualifiedName);
            Assert.AreEqual(1, g.Args.Count);
            Assert.IsTrue(ReferenceEquals(target, g.Args[0]));
        }

        // 5. GenericRef with no substitutable args preserves identity.
        [TestMethod]
        public void Substitute_Generic_NoSubstitutableArgs_ReferenceEqual()
        {
            GenericRef input = new GenericRef(ListC(), new TypeRef[] { Int() });
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", Str()));
            Assert.IsTrue(ReferenceEquals(input, output));
        }

        // 6. UnionRef substitution.
        [TestMethod]
        public void Substitute_Union_ReplacesMembers()
        {
            UnionRef input = new UnionRef(new TypeRef[] { new TypeVarRef("T"), NoneRef.Instance });
            ClassRef target = Int();
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", target));
            Assert.IsFalse(ReferenceEquals(input, output));
            UnionRef u = output as UnionRef;
            Assert.IsNotNull(u);
            Assert.AreEqual(2, u.Members.Count);
            Assert.IsTrue(ReferenceEquals(target, u.Members[0]));
            Assert.IsTrue(ReferenceEquals(NoneRef.Instance, u.Members[1]));
        }

        // 7. Nested substitution.
        [TestMethod]
        public void Substitute_Nested_GenericInGeneric_Recurses()
        {
            ClassRef target = Str();
            GenericRef inner = new GenericRef(ListC(), new TypeRef[] { new TypeVarRef("T") });
            GenericRef outer = new GenericRef(DictC(), new TypeRef[] { new TypeVarRef("T"), inner });
            TypeRef output = GenericSubstitution.Substitute(outer, Map("T", target));
            Assert.IsFalse(ReferenceEquals(outer, output));
            GenericRef g = output as GenericRef;
            Assert.IsNotNull(g);
            Assert.AreEqual("builtins.dict", g.Base.QualifiedName);
            Assert.AreEqual(2, g.Args.Count);
            Assert.IsTrue(ReferenceEquals(target, g.Args[0]));
            GenericRef innerOut = g.Args[1] as GenericRef;
            Assert.IsNotNull(innerOut);
            Assert.AreEqual("builtins.list", innerOut.Base.QualifiedName);
            Assert.AreEqual(1, innerOut.Args.Count);
            Assert.IsTrue(ReferenceEquals(target, innerOut.Args[0]));
        }

        // 8. TupleRef substitution across multiple type vars.
        [TestMethod]
        public void Substitute_Tuple_ReplacesMultipleVars()
        {
            ClassRef tStr = Str();
            ClassRef tBool = Bool();
            ClassRef litInt = Int();
            TupleRef input = new TupleRef(
                new TypeRef[] { new TypeVarRef("T"), new TypeVarRef("U"), litInt },
                false);
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", tStr, "U", tBool));
            Assert.IsFalse(ReferenceEquals(input, output));
            TupleRef t = output as TupleRef;
            Assert.IsNotNull(t);
            Assert.AreEqual(3, t.Items.Count);
            Assert.IsFalse(t.IsVariadic);
            Assert.IsTrue(ReferenceEquals(tStr, t.Items[0]));
            Assert.IsTrue(ReferenceEquals(tBool, t.Items[1]));
            Assert.IsTrue(ReferenceEquals(litInt, t.Items[2]));
        }

        // 9. CallableRef substitution of both parameter and return type.
        [TestMethod]
        public void Substitute_Callable_ReplacesParamAndReturn()
        {
            ClassRef target = Int();
            CallableParam p = new CallableParam("x", new TypeVarRef("T"), CallableParamKind.PositionalOrKeyword, false);
            CallableRef input = new CallableRef(new CallableParam[] { p }, new TypeVarRef("T"));
            TypeRef output = GenericSubstitution.Substitute(input, Map("T", target));
            Assert.IsFalse(ReferenceEquals(input, output));
            CallableRef c = output as CallableRef;
            Assert.IsNotNull(c);
            Assert.AreEqual(1, c.Params.Count);
            Assert.AreEqual("x", c.Params[0].Name);
            Assert.IsTrue(ReferenceEquals(target, c.Params[0].Type));
            Assert.IsTrue(ReferenceEquals(target, c.ReturnType));
        }

        // 10. CallableParam kinds preserved.
        [TestMethod]
        public void Substitute_Callable_ParamKindsPreserved()
        {
            ClassRef target = Int();
            CallableParam p1 = new CallableParam("a", new TypeVarRef("T"), CallableParamKind.PositionalOnly, false);
            CallableParam p2 = new CallableParam("b", new TypeVarRef("T"), CallableParamKind.KeywordOnly, false);
            CallableParam p3 = new CallableParam("args", new TypeVarRef("T"), CallableParamKind.VarPositional, false);
            CallableParam p4 = new CallableParam("kwargs", new TypeVarRef("T"), CallableParamKind.VarKeyword, false);
            CallableRef input = new CallableRef(new CallableParam[] { p1, p2, p3, p4 }, NoneRef.Instance);
            CallableRef output = (CallableRef)GenericSubstitution.Substitute(input, Map("T", target));
            Assert.AreEqual(CallableParamKind.PositionalOnly, output.Params[0].Kind);
            Assert.AreEqual(CallableParamKind.KeywordOnly, output.Params[1].Kind);
            Assert.AreEqual(CallableParamKind.VarPositional, output.Params[2].Kind);
            Assert.AreEqual(CallableParamKind.VarKeyword, output.Params[3].Kind);
            Assert.AreEqual("a", output.Params[0].Name);
            Assert.AreEqual("b", output.Params[1].Name);
            Assert.AreEqual("args", output.Params[2].Name);
            Assert.AreEqual("kwargs", output.Params[3].Name);
        }

        // 11. HasDefault preserved.
        [TestMethod]
        public void Substitute_Callable_HasDefaultPreserved()
        {
            ClassRef target = Str();
            CallableParam p1 = new CallableParam("x", new TypeVarRef("T"), CallableParamKind.PositionalOrKeyword, true);
            CallableParam p2 = new CallableParam("y", new TypeVarRef("T"), CallableParamKind.PositionalOrKeyword, false);
            CallableRef input = new CallableRef(new CallableParam[] { p1, p2 }, NoneRef.Instance);
            CallableRef output = (CallableRef)GenericSubstitution.Substitute(input, Map("T", target));
            Assert.IsTrue(output.Params[0].HasDefault);
            Assert.IsFalse(output.Params[1].HasDefault);
        }

        // 12. Empty binding → reference-equal.
        [TestMethod]
        public void Substitute_EmptyBindings_ReferenceEqual()
        {
            GenericRef input = new GenericRef(ListC(), new TypeRef[] { new TypeVarRef("T") });
            TypeRef output = GenericSubstitution.Substitute(input, new Dictionary<string, TypeRef>());
            Assert.IsTrue(ReferenceEquals(input, output));
        }

        // 13. Bind pairs positionally.
        [TestMethod]
        public void Bind_PairsPositionally()
        {
            ClassRef ti = Int();
            ClassRef ts = Str();
            IReadOnlyDictionary<string, TypeRef> bindings = GenericBinder.Bind(
                new TypeVarRef[] { new TypeVarRef("T"), new TypeVarRef("U") },
                new TypeRef[] { ti, ts });
            Assert.AreEqual(2, bindings.Count);
            Assert.IsTrue(ReferenceEquals(ti, bindings["T"]));
            Assert.IsTrue(ReferenceEquals(ts, bindings["U"]));
        }

        // 14. Bind with fewer args than params.
        [TestMethod]
        public void Bind_FewerArgsThanParams_BindsOverlap()
        {
            ClassRef ti = Int();
            IReadOnlyDictionary<string, TypeRef> bindings = GenericBinder.Bind(
                new TypeVarRef[] { new TypeVarRef("T"), new TypeVarRef("U") },
                new TypeRef[] { ti });
            Assert.AreEqual(1, bindings.Count);
            Assert.IsTrue(ReferenceEquals(ti, bindings["T"]));
            Assert.IsFalse(bindings.ContainsKey("U"));
        }

        // 15. Bind with more args than params.
        [TestMethod]
        public void Bind_MoreArgsThanParams_BindsOverlap()
        {
            ClassRef ti = Int();
            ClassRef ts = Str();
            ClassRef tb = Bool();
            IReadOnlyDictionary<string, TypeRef> bindings = GenericBinder.Bind(
                new TypeVarRef[] { new TypeVarRef("T") },
                new TypeRef[] { ti, ts, tb });
            Assert.AreEqual(1, bindings.Count);
            Assert.IsTrue(ReferenceEquals(ti, bindings["T"]));
        }

        // 16. FreeTypeVars single entry.
        [TestMethod]
        public void FreeTypeVars_SingleVarInGeneric()
        {
            GenericRef g = new GenericRef(ListC(), new TypeRef[] { new TypeVarRef("T") });
            IReadOnlyList<TypeVarRef> vars = GenericBinder.FreeTypeVars(g);
            Assert.AreEqual(1, vars.Count);
            Assert.AreEqual("T", vars[0].Name);
        }

        // 17. FreeTypeVars order and dedup.
        [TestMethod]
        public void FreeTypeVars_DedupAndOrder()
        {
            UnionRef u = new UnionRef(new TypeRef[]
            {
                new TypeVarRef("T"),
                new TypeVarRef("U"),
                new TypeVarRef("T")
            });
            IReadOnlyList<TypeVarRef> vars = GenericBinder.FreeTypeVars(u);
            Assert.AreEqual(2, vars.Count);
            Assert.AreEqual("T", vars[0].Name);
            Assert.AreEqual("U", vars[1].Name);
        }

        // 18. FreeTypeVars empty for monomorphic.
        [TestMethod]
        public void FreeTypeVars_Monomorphic_ReturnsEmpty()
        {
            GenericRef g = new GenericRef(DictC(), new TypeRef[] { Int(), Str() });
            IReadOnlyList<TypeVarRef> vars = GenericBinder.FreeTypeVars(g);
            Assert.AreEqual(0, vars.Count);
        }
    }
}
