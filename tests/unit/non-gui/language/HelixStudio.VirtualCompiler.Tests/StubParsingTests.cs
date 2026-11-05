using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.Python.Diagnostics;
using HelixPythonNamespace.VirtualCompiler.Typing;
using HelixPythonNamespace.VirtualCompiler.Typing.Stubs;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Phase-2 coverage: stub parsing, embedded-resource loading, and
    /// TypeshedIndex lookup. The bundled <c>.pyi</c> files must parse cleanly
    /// and expose their top-level declarations by dotted name.
    /// </summary>
    [TestClass]
    public class StubParsingTests
    {
        [TestMethod]
        public void StubParser_Parses_SimpleDef()
        {
            const string src = "def foo(x: int) -> str: ...\n";
            StubModule m = StubParser.Parse(src, "test");
            Assert.AreEqual(0, m.ParseDiagnostics.Count(pd => pd.Severity >= ParseSeverity.Error));
            StubDeclaration d;
            Assert.IsTrue(m.TryGet("foo", out d));
            Assert.AreEqual(StubKind.Function, d.Kind);
            var call = d.Type as CallableRef;
            Assert.IsNotNull(call);
            Assert.AreEqual(1, call.Params.Count);
            Assert.AreEqual("x", call.Params[0].Name);
            Assert.IsTrue(call.ReturnType is ClassRef);
            Assert.AreEqual("str", ((ClassRef)call.ReturnType).QualifiedName);
        }

        [TestMethod]
        public void StubParser_Parses_SimpleClass()
        {
            const string src =
                "class Foo:\n" +
                "    x: int\n" +
                "    def bar(self, y: str) -> bool: ...\n";
            StubModule m = StubParser.Parse(src, "test");
            StubDeclaration cls;
            Assert.IsTrue(m.TryGet("Foo", out cls));
            Assert.AreEqual(StubKind.Class, cls.Kind);
            Assert.AreEqual(2, cls.Members.Count);
            Assert.IsTrue(cls.Members.ContainsKey("x"));
            Assert.IsTrue(cls.Members.ContainsKey("bar"));
            Assert.AreEqual(StubKind.Variable, cls.Members["x"].Kind);
            Assert.AreEqual(StubKind.Function, cls.Members["bar"].Kind);
        }

        [TestMethod]
        public void StubParser_Detects_Protocol_Base()
        {
            const string src = "class Sized(Protocol):\n    def __len__(self) -> int: ...\n";
            StubModule m = StubParser.Parse(src, "test");
            var cls = m.Declarations["Sized"];
            Assert.IsTrue(cls.IsProtocol);
            CollectionAssert.Contains(cls.BaseNames.ToList(), "Protocol");
        }

        [TestMethod]
        public void AnnotationConverter_Handles_Optional()
        {
            const string src = "def f(x: Optional[int]) -> None: ...\n";
            StubModule m = StubParser.Parse(src, "test");
            var call = (CallableRef)m.Declarations["f"].Type;
            var paramType = call.Params[0].Type;
            Assert.IsInstanceOfType(paramType, typeof(UnionRef));
            var union = (UnionRef)paramType;
            Assert.AreEqual(2, union.Members.Count);
            Assert.IsTrue(union.Members.Any(mm => mm is NoneRef));
        }

        [TestMethod]
        public void AnnotationConverter_Handles_Union()
        {
            const string src = "def f(x: Union[int, str]) -> None: ...\n";
            StubModule m = StubParser.Parse(src, "test");
            var call = (CallableRef)m.Declarations["f"].Type;
            var union = call.Params[0].Type as UnionRef;
            Assert.IsNotNull(union);
            Assert.AreEqual(2, union.Members.Count);
        }

        [TestMethod]
        public void AnnotationConverter_Handles_PEP604_Pipe_Union()
        {
            const string src = "def f(x: int | str | None) -> None: ...\n";
            StubModule m = StubParser.Parse(src, "test");
            var call = (CallableRef)m.Declarations["f"].Type;
            var union = call.Params[0].Type as UnionRef;
            Assert.IsNotNull(union);
            Assert.AreEqual(3, union.Members.Count);
        }

        [TestMethod]
        public void AnnotationConverter_Handles_Generic_List()
        {
            const string src = "def f() -> list[int]: ...\n";
            StubModule m = StubParser.Parse(src, "test");
            var call = (CallableRef)m.Declarations["f"].Type;
            var g = call.ReturnType as GenericRef;
            Assert.IsNotNull(g);
            Assert.AreEqual("list", g.Base.QualifiedName);
            Assert.AreEqual(1, g.Args.Count);
        }

        [TestMethod]
        public void StubLoader_Loads_BundledStubs()
        {
            StubLoader.ResetCacheForTests();
            var idx = StubLoader.BuiltinIndex;
            Assert.IsTrue(idx.Count >= 5, "expected at least 5 bundled modules; got " + idx.Count);

            StubModule builtins;
            Assert.IsTrue(idx.TryGetModule("builtins", out builtins),
                "bundled `builtins` stub must load");
            Assert.IsTrue(idx.TryGetModule("typing", out _));
            Assert.IsTrue(idx.TryGetModule("sys", out _));
            Assert.IsTrue(idx.TryGetModule("os", out _));
            Assert.IsTrue(idx.TryGetModule("abc", out _));
            Assert.IsTrue(idx.TryGetModule("collections", out _));
        }

        [TestMethod]
        public void BundledBuiltinsStub_Parses_CleanlyAndExposesKeyNames()
        {
            var idx = StubLoader.BuiltinIndex;
            var builtins = idx.GetModuleOrNull("builtins");
            Assert.IsNotNull(builtins);

            int errors = builtins.ParseDiagnostics.Count(d => d.Severity >= ParseSeverity.Error);
            Assert.AreEqual(0, errors,
                "bundled builtins.pyi should parse cleanly; got: " +
                string.Join(", ", builtins.ParseDiagnostics.Select(d => d.Code + " " + d.Message)));

            foreach (string name in new[] { "print", "len", "range", "int", "str", "list", "dict", "object", "Exception" })
            {
                StubDeclaration d;
                Assert.IsTrue(builtins.TryGet(name, out d),
                    "builtins.pyi should declare '" + name + "'");
            }
        }

        [TestMethod]
        public void TypeshedIndex_Resolves_DottedName()
        {
            var idx = StubLoader.BuiltinIndex;
            StubDeclaration print = idx.Resolve("builtins.print");
            Assert.IsNotNull(print);
            Assert.AreEqual(StubKind.Function, print.Kind);

            StubDeclaration osGetcwd = idx.Resolve("os.getcwd");
            Assert.IsNotNull(osGetcwd);
            Assert.AreEqual(StubKind.Function, osGetcwd.Kind);
        }

        [TestMethod]
        public void TypeshedIndex_ReturnsNull_ForUnknownName()
        {
            var idx = StubLoader.BuiltinIndex;
            Assert.IsNull(idx.Resolve("builtins.nonexistent_function_xyz"));
            Assert.IsNull(idx.Resolve("unknown.module"));
        }

        [TestMethod]
        public void BuiltinsStub_PrintFunction_HasExpectedShape()
        {
            var idx = StubLoader.BuiltinIndex;
            var print = idx.Resolve("builtins.print") as StubDeclaration;
            Assert.IsNotNull(print);
            var call = print.Type as CallableRef;
            Assert.IsNotNull(call);
            // print(*values, sep=..., end=..., file=..., flush=...)
            Assert.IsTrue(call.Params.Any(p => p.Kind == CallableParamKind.VarPositional),
                "print should have *values param");
            Assert.IsTrue(call.ReturnType is NoneRef);
        }

        [TestMethod]
        public void TypingStub_Exposes_TypeVar_Class()
        {
            var idx = StubLoader.BuiltinIndex;
            var typing = idx.GetModuleOrNull("typing");
            Assert.IsNotNull(typing);
            StubDeclaration tv;
            Assert.IsTrue(typing.TryGet("TypeVar", out tv));
            Assert.AreEqual(StubKind.Class, tv.Kind);
        }

        [TestMethod]
        public void BuiltinsStub_ExceptionHierarchy_IsCaptured()
        {
            var idx = StubLoader.BuiltinIndex;
            var builtins = idx.GetModuleOrNull("builtins");
            Assert.IsNotNull(builtins);
            var ex = builtins.Declarations["Exception"];
            Assert.AreEqual(StubKind.Class, ex.Kind);
            CollectionAssert.Contains(ex.BaseNames.ToList(), "BaseException");
        }

        [TestMethod]
        public void BuiltinsStub_List_IsGenericContainer()
        {
            var idx = StubLoader.BuiltinIndex;
            var list = idx.Resolve("builtins.list");
            Assert.IsNotNull(list);
            Assert.AreEqual(StubKind.Class, list.Kind);
            Assert.IsTrue(list.Members.ContainsKey("append"));
            Assert.IsTrue(list.Members.ContainsKey("__len__"));
        }
    }
}
