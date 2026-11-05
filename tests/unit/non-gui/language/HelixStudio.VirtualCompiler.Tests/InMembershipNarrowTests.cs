using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 468 (fastapi stress-test): `x in {"a", "b"}` over a literal container narrows x on the TRUE
    /// branch to those literals, dropping None — so passing it where a `str` is expected is valid
    /// (fastapi routing.py:1888, `if fallback in {"index.html", "404.html"}:` with fallback of type
    /// Literal[...] | None). Also: a parameterized external alias (`Lifespan[Any]`) is an unjudgeable
    /// source, not flagged against a concrete callable parameter.
    /// </summary>
    [TestClass]
    public class InMembershipNarrowTests
    {
        private static bool HasArgMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void InSetGuard_NarrowsAwayNone_ForAStrParameter()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal, Optional\n" +
                "def take(s: str) -> None:\n" +
                "    pass\n" +
                "def use(fallback: Optional[Literal['auto', 'index.html', '404.html']]) -> None:\n" +
                "    if fallback in {'index.html', '404.html'}:\n" +
                "        take(fallback)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void InTupleGuard_AlsoNarrows()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional\n" +
                "def take(s: str) -> None:\n" +
                "    pass\n" +
                "def use(x: Optional[str]) -> None:\n" +
                "    if x in ('a', 'b'):\n" +
                "        take(x)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void OutsideTheGuard_TheOptionalIsStillFlagged()
        {
            // No membership guard: the value is genuinely Optional, so passing it to a str param flags.
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional\n" +
                "def take(s: str) -> None:\n" +
                "    pass\n" +
                "def use(x: Optional[str]) -> None:\n" +
                "    take(x)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ParameterizedExternalAlias_IsNotFlaggedAgainstACallable()
        {
            // Lifespan[Any] — an unresolvable external alias applied as a generic — can't be judged
            // against a concrete callable parameter (fastapi routing.py:2484).
            var r = VirtualCompiler.CompileSource(
                "from typing import Any\n" +
                "from starlette.types import Lifespan\n" +
                "from typing import Callable, Generator\n" +
                "def wrap(f: Callable[[Any], Generator[Any, Any, Any]]) -> Any:\n" +
                "    return f\n" +
                "def use(lifespan: Lifespan[Any]) -> Any:\n" +
                "    return wrap(lifespan)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }
    }
}
