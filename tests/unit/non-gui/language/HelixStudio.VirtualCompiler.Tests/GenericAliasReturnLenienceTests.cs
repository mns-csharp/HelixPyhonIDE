using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 376 (black/click stress-test): the dominant PY6005 false-positive family. A generic
    /// annotation is only judgeable when its BASE resolves to a real class. `TResult[Line]` (where
    /// `TResult = Result[T, CannotTransform]` and `Result = Union[Ok[T], Err[E]]`) has an
    /// unresolvable alias base, so returning `Ok(...)`/`Err(...)` must NOT false-flag PY6005 — the
    /// checker can't expand the alias to its underlying union. A qualified unmodeled typing form
    /// behind a decorator (`t.ContextManager[X]` from `@contextmanager`) is likewise unjudgeable.
    /// A generic over a *resolvable* base (list[int], a real workspace class) is still checked.
    /// </summary>
    [TestClass]
    public class GenericAliasReturnLenienceTests
    {
        private static bool HasReturnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private const string ResultPrelude =
            "from typing import Generic, TypeVar, Union\n" +
            "T = TypeVar('T')\n" +
            "E = TypeVar('E', bound=Exception)\n" +
            "class Ok(Generic[T]):\n" +
            "    def __init__(self, value: T) -> None: ...\n" +
            "class Err(Generic[E]):\n" +
            "    def __init__(self, e: E) -> None: ...\n" +
            "Result = Union[Ok[T], Err[E]]\n" +
            "class CannotTransform(Exception): ...\n" +
            "TResult = Result[T, CannotTransform]\n";

        [TestMethod]
        public void ReturningResultVariants_AgainstAliasGeneric_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                ResultPrelude +
                "def good(x: int) -> TResult[int]:\n" +
                "    if x > 0:\n" +
                "        return Ok(x)\n" +
                "    return Err(CannotTransform())\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnmodeledQualifiedGeneric_DoesNotFlag()
        {
            // `t.ContextManager[...]` (a qualified typing form, unmodeled here) has an unresolvable
            // base, so an Iterator return under it must not false-flag.
            var r = VirtualCompiler.CompileSource(
                "import typing as t\n" +
                "def cm() -> t.ContextManager[int]:\n" +
                "    return open_it()\n" +
                "def open_it() -> t.Iterator[int]: ...\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ResolvableBaseGeneric_StillReportsGenuineMismatch()
        {
            // list resolves, so a return that is plainly the wrong type is still caught — the guard
            // suppresses only unresolvable-base generics, not real ones.
            var r = VirtualCompiler.CompileSource(
                "def f() -> list[int]:\n" +
                "    return 'nope'\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
