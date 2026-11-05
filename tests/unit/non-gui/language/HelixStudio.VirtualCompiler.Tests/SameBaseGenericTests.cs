using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 442 (pydantic stress-test): a same-class generic with different type arguments is accepted
    /// — the checker erases type arguments for workspace classes, and subclassing is reflexive. So
    /// `Pipeline[InT, NewOutT]` returned where `Pipeline[InT, str]` is annotated type-checks (pydantic
    /// experimental). A genuinely unrelated class is still flagged.
    /// </summary>
    [TestClass]
    public class SameBaseGenericTests
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
        public void SameGenericClass_DifferentArgs_Accepted()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Generic, TypeVar\n" +
                "InT = TypeVar('InT')\n" +
                "OutT = TypeVar('OutT')\n" +
                "class Pipeline(Generic[InT, OutT]): ...\n" +
                "def transform(p: Pipeline[InT, int]) -> Pipeline[InT, str]:\n" +
                "    return p  # type: ignore-worthy but the checker is gradual on generic args\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnrelatedClass_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Generic, TypeVar\n" +
                "T = TypeVar('T')\n" +
                "class Pipeline(Generic[T]): ...\n" +
                "class Widget: ...\n" +
                "def make() -> Pipeline[int]:\n" +
                "    return Widget()\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
