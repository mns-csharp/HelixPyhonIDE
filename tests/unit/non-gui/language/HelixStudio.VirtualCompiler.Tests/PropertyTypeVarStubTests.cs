using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 434 (pydantic stress-test): stub gaps closed. `property` exposes fget/fset/fdel plus the
    /// descriptor protocol (__get__/__set__/__delete__) and getter/setter/deleter; `TypeVar` exposes
    /// __bound__/__constraints__/__covariant__/__default__ and has_default(); NameError/AttributeError
    /// expose `name`. These attribute accesses no longer false-flag PY6001.
    /// </summary>
    [TestClass]
    public class PropertyTypeVarStubTests
    {
        private static bool HasAttrNotFound(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void PropertyAccessors_Resolve()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(p: property) -> None:\n" +
                "    _ = p.fget\n" +
                "    _ = p.fset\n" +
                "    _ = p.fdel\n" +
                "    _ = p.__set__\n");
            Assert.IsFalse(HasAttrNotFound(r), Dump(r));
        }

        [TestMethod]
        public void TypeVarAttributes_Resolve()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TypeVar\n" +
                "def f(tv: TypeVar) -> None:\n" +
                "    _ = tv.__bound__\n" +
                "    _ = tv.__constraints__\n" +
                "    _ = tv.__default__\n" +
                "    _ = tv.has_default()\n");
            Assert.IsFalse(HasAttrNotFound(r), Dump(r));
        }

        [TestMethod]
        public void NameError_Name_Resolves()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(e: NameError) -> str:\n" +
                "    return e.name\n");
            Assert.IsFalse(HasAttrNotFound(r), Dump(r));
        }

        [TestMethod]
        public void PropertyMissingAttribute_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(p: property) -> None:\n" +
                "    _ = p.no_such_attr\n");
            Assert.IsTrue(HasAttrNotFound(r), Dump(r));
        }
    }
}
