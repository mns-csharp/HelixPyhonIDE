using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler.Typing;
using HelixPythonNamespace.VirtualCompiler.Typing.Stubs;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Coverage for <see cref="SubtypeChecker.IsAssignable"/>. Each rule from
    /// the spec has at least one positive and (where meaningful) one negative
    /// case. Tests that need the real base-class chain pull the cached
    /// <see cref="StubLoader.BuiltinIndex"/>.
    /// </summary>
    [TestClass]
    public class SubtypeCheckerTests
    {
        private static TypeshedIndex Stubs
        {
            get { return StubLoader.BuiltinIndex; }
        }

        // ---- Identity / gradual typing ----

        [TestMethod]
        public void ReferenceEqual_Types_Assignable()
        {
            ClassRef t = new ClassRef("foo.Bar");
            Assert.IsTrue(SubtypeChecker.IsAssignable(t, t));
        }

        [TestMethod]
        public void AnyRef_Source_Assignable_To_Anything()
        {
            Assert.IsTrue(SubtypeChecker.IsAssignable(AnyRef.Instance, BuiltinRefs.Int));
        }

        [TestMethod]
        public void AnyRef_Target_Accepts_Anything()
        {
            Assert.IsTrue(SubtypeChecker.IsAssignable(BuiltinRefs.Str, AnyRef.Instance));
        }

        [TestMethod]
        public void UnknownRef_Either_Side_Assignable()
        {
            Assert.IsTrue(SubtypeChecker.IsAssignable(UnknownRef.Instance, BuiltinRefs.Int));
            Assert.IsTrue(SubtypeChecker.IsAssignable(BuiltinRefs.Int, UnknownRef.Instance));
        }

        [TestMethod]
        public void NeverRef_Source_Assignable_To_Anything()
        {
            Assert.IsTrue(SubtypeChecker.IsAssignable(NeverRef.Instance, BuiltinRefs.Int));
        }

        [TestMethod]
        public void NeverRef_Target_Rejects_Everything()
        {
            Assert.IsFalse(SubtypeChecker.IsAssignable(BuiltinRefs.Int, NeverRef.Instance));
        }

        // ---- ClassRef ----

        [TestMethod]
        public void Both_ClassRef_Int_Assignable()
        {
            Assert.IsTrue(SubtypeChecker.IsAssignable(
                new ClassRef("builtins.int"),
                new ClassRef("builtins.int")));
        }

        [TestMethod]
        public void ClassRef_Bare_Qualified_Equivalent()
        {
            Assert.IsTrue(SubtypeChecker.IsAssignable(
                new ClassRef("int"),
                new ClassRef("builtins.int")));
            Assert.IsTrue(SubtypeChecker.IsAssignable(
                new ClassRef("builtins.int"),
                new ClassRef("int")));
        }

        [TestMethod]
        public void ClassRef_Bool_IsA_Int_Via_BaseChain()
        {
            // bool extends int in the bundled builtins.pyi.
            Assert.IsTrue(SubtypeChecker.IsAssignable(
                new ClassRef("bool"),
                new ClassRef("int"),
                Stubs));
        }

        [TestMethod]
        public void ClassRef_Int_Not_IsA_Bool()
        {
            Assert.IsFalse(SubtypeChecker.IsAssignable(
                new ClassRef("int"),
                new ClassRef("bool"),
                Stubs));
        }

        [TestMethod]
        public void ClassRef_Str_Not_IsA_Int()
        {
            Assert.IsFalse(SubtypeChecker.IsAssignable(
                new ClassRef("str"),
                new ClassRef("int"),
                Stubs));
        }

        // ---- Union ----

        [TestMethod]
        public void UnionTarget_Int_Str_Accepts_Int()
        {
            UnionRef u = new UnionRef(new List<TypeRef> { BuiltinRefs.Int, BuiltinRefs.Str });
            Assert.IsTrue(SubtypeChecker.IsAssignable(BuiltinRefs.Int, u, Stubs));
        }

        [TestMethod]
        public void UnionTarget_Int_Str_Rejects_Float()
        {
            UnionRef u = new UnionRef(new List<TypeRef> { BuiltinRefs.Int, BuiltinRefs.Str });
            Assert.IsFalse(SubtypeChecker.IsAssignable(BuiltinRefs.Float, u, Stubs));
        }

        [TestMethod]
        public void UnionSource_Int_Str_NotAssignable_To_Int()
        {
            UnionRef u = new UnionRef(new List<TypeRef> { BuiltinRefs.Int, BuiltinRefs.Str });
            Assert.IsFalse(SubtypeChecker.IsAssignable(u, BuiltinRefs.Int, Stubs));
        }

        [TestMethod]
        public void OptionalInt_Accepts_None()
        {
            UnionRef opt = new UnionRef(new List<TypeRef> { BuiltinRefs.Int, NoneRef.Instance });
            Assert.IsTrue(SubtypeChecker.IsAssignable(NoneRef.Instance, opt));
        }

        // ---- Literal ----

        [TestMethod]
        public void Literal_Int_Assignable_To_IntClass()
        {
            Assert.IsTrue(SubtypeChecker.IsAssignable(
                new LiteralRef(42),
                BuiltinRefs.Int));
        }

        [TestMethod]
        public void Literal_Str_Not_Assignable_To_IntClass()
        {
            Assert.IsFalse(SubtypeChecker.IsAssignable(
                new LiteralRef("hi"),
                BuiltinRefs.Int));
        }

        [TestMethod]
        public void Literal_Bool_Assignable_To_IntClass_Via_BaseChain()
        {
            // Literal[True] → int succeeds because bool extends int.
            Assert.IsTrue(SubtypeChecker.IsAssignable(
                new LiteralRef(true),
                BuiltinRefs.Int,
                Stubs));
        }

        // ---- Callable ----

        [TestMethod]
        public void Callable_ReturnType_Covariant()
        {
            // (int) -> int assignable to (int) -> object
            CallableRef src = new CallableRef(
                new List<CallableParam> { new CallableParam("x", BuiltinRefs.Int, CallableParamKind.PositionalOrKeyword, false) },
                BuiltinRefs.Int);
            CallableRef dst = new CallableRef(
                new List<CallableParam> { new CallableParam("x", BuiltinRefs.Int, CallableParamKind.PositionalOrKeyword, false) },
                BuiltinRefs.Object);
            Assert.IsTrue(SubtypeChecker.IsAssignable(src, dst, Stubs));
        }

        [TestMethod]
        public void Callable_Param_Count_Mismatch_Rejected()
        {
            CallableRef src = new CallableRef(
                new List<CallableParam> { new CallableParam("x", BuiltinRefs.Int, CallableParamKind.PositionalOrKeyword, false) },
                BuiltinRefs.Int);
            CallableRef dst = new CallableRef(
                new List<CallableParam>
                {
                    new CallableParam("x", BuiltinRefs.Int, CallableParamKind.PositionalOrKeyword, false),
                    new CallableParam("y", BuiltinRefs.Int, CallableParamKind.PositionalOrKeyword, false)
                },
                BuiltinRefs.Int);
            Assert.IsFalse(SubtypeChecker.IsAssignable(src, dst));
        }

        // ---- Tuple ----

        [TestMethod]
        public void Tuple_Same_Length_Matching_Items_Assignable()
        {
            TupleRef a = new TupleRef(new List<TypeRef> { BuiltinRefs.Int, BuiltinRefs.Str }, false);
            TupleRef b = new TupleRef(new List<TypeRef> { BuiltinRefs.Int, BuiltinRefs.Str }, false);
            Assert.IsTrue(SubtypeChecker.IsAssignable(a, b));
        }

        // ---- TypeVar ----

        [TestMethod]
        public void TypeVar_Source_Without_Bound_Assignable_To_Anything()
        {
            TypeVarRef t = new TypeVarRef("T");
            Assert.IsTrue(SubtypeChecker.IsAssignable(t, BuiltinRefs.Int));
        }

        [TestMethod]
        public void TypeVar_Source_With_Int_Bound_Assignable_To_Object_Via_BaseChain()
        {
            TypeVarRef t = new TypeVarRef("T", BuiltinRefs.Int);
            Assert.IsTrue(SubtypeChecker.IsAssignable(t, BuiltinRefs.Object, Stubs));
        }

        // ---- object is the top type (iter 360/361) ----

        /// <summary>
        /// A user-defined class instance is assignable to a bare `object` parameter — object is the
        /// top type. Without this, passing a workspace class (which has no stub base chain to walk to
        /// object) to `def f(x: object)` false-flagged PY6003 on requests/sessions.py:209, while
        /// builtins slipped through only because their stub chains happen to reach object.
        /// </summary>
        [TestMethod]
        public void UserClass_Assignable_To_Object_Target()
        {
            Assert.IsTrue(SubtypeChecker.IsAssignable(new ClassRef("myapp.Response"), BuiltinRefs.Object));
        }

        [TestMethod]
        public void None_Assignable_To_Object_Target()
        {
            Assert.IsTrue(SubtypeChecker.IsAssignable(NoneRef.Instance, BuiltinRefs.Object));
        }

        [TestMethod]
        public void Union_Assignable_To_Object_Target()
        {
            UnionRef u = new UnionRef(new TypeRef[] { new ClassRef("A"), NoneRef.Instance });
            Assert.IsTrue(SubtypeChecker.IsAssignable(u, BuiltinRefs.Object));
        }

        /// <summary>The rule is one-directional: object is NOT assignable down to a specific type.</summary>
        [TestMethod]
        public void Object_Source_Is_Gradual_But_SpecificTarget_Still_Distinct()
        {
            // object source stays gradual (Rule 2b) — assignable to a specific type...
            Assert.IsTrue(SubtypeChecker.IsAssignable(BuiltinRefs.Object, new ClassRef("A")));
            // ...but a specific class is NOT wrongly accepted as a DIFFERENT specific class.
            Assert.IsFalse(SubtypeChecker.IsAssignable(new ClassRef("A"), new ClassRef("B")));
        }
    }
}
