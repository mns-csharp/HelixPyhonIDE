using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 462 (fastapi stress-test): a name introduced by `X = TypeVar(...)` is a type PARAMETER,
    /// not a concrete class, so an annotation mentioning it (`-> _T`, `x: _T`) must be treated as
    /// gradual — a `-> _T` return accepts whatever the call binds it to. Previously a bare `_T`
    /// collided with typeshed's internal `_T` symbol and looked resolvable, false-flagging PY6005
    /// (fastapi routing.py:264, `def __call__(self: _T, app: object) -> _T: return self`).
    /// </summary>
    [TestClass]
    public class TypeVarAnnotationTests
    {
        private static bool HasReturnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch);
        }

        private static bool HasArgMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void ReturningAConcreteValue_AgainstATypeVarReturn_IsAccepted()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TypeVar\n" +
                "_T = TypeVar('_T')\n" +
                "class Lifespan:\n" +
                "    def __call__(self: _T, app: object) -> _T:\n" +
                "        return self\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void PassingAConcreteValue_ToATypeVarParameter_IsAccepted()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TypeVar\n" +
                "T = TypeVar('T')\n" +
                "def identity(x: T) -> T:\n" +
                "    return x\n" +
                "def use() -> None:\n" +
                "    identity(5)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void QualifiedTypeVarConstruction_IsAlsoRecognized()
        {
            var r = VirtualCompiler.CompileSource(
                "import typing\n" +
                "_K = typing.TypeVar('_K')\n" +
                "class Box:\n" +
                "    def get(self, k: _K) -> _K:\n" +
                "        return k\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void AGenuineConcreteMismatch_IsStillFlagged()
        {
            // Proves we only relaxed TypeVar targets, not concrete ones.
            var r = VirtualCompiler.CompileSource(
                "def f() -> int:\n" +
                "    return 'not an int'\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
