using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 450 (pydantic stress-test): a metaclass instance — a value of a class that subclasses
    /// `type` — IS a type, so it satisfies `Type[X]`; and a union satisfies `Type[X]` when every member
    /// does. Covers `Type[T] | ConstrainedNumberMeta` returned as `Type[bytes]` (pydantic v1 types.py).
    /// A union with a non-type member is still flagged.
    /// </summary>
    [TestClass]
    public class MetaclassTypeReturnTests
    {
        private static bool HasReturnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void MetaclassInstance_SatisfiesTypeAnnotation()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Type, TypeVar, Union\n" +
                "T = TypeVar('T')\n" +
                "class ConstrainedNumberMeta(type): ...\n" +
                "def build(flag: bool, meta: ConstrainedNumberMeta, cls: Type[T]) -> Type[bytes]:\n" +
                "    if flag:\n" +
                "        return cls\n" +
                "    return meta\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnionWithNonType_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Type\n" +
                "class Widget: ...\n" +
                "def build(flag: bool, w: Widget) -> Type[bytes]:\n" +
                "    return w\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
