using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 438 (pydantic stress-test): a literal-constant argument that matches one of a parameter's
    /// Literal values type-checks — `apply(schema, validators, 'inner')` against
    /// `Literal["inner","outer","all"]` (pydantic _generate_schema.py). String constants infer as
    /// `str`, so the literal-match guard is now applied at argument sites (positional and keyword). A
    /// constant NOT in the allowed set is still rejected.
    /// </summary>
    [TestClass]
    public class LiteralArgumentTests
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
        public void MatchingLiteralConstant_Positional_Accepted()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal\n" +
                "def apply(name: str, mode: Literal['inner', 'outer', 'all']) -> None: ...\n" +
                "def f() -> None:\n" +
                "    apply('x', 'inner')\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void MatchingLiteralConstant_Keyword_Accepted()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal\n" +
                "def apply(name: str, mode: Literal['inner', 'outer', 'all']) -> None: ...\n" +
                "def f() -> None:\n" +
                "    apply('x', mode='all')\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void NonMatchingLiteralConstant_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal\n" +
                "def apply(mode: Literal['inner', 'outer', 'all']) -> None: ...\n" +
                "def f() -> None:\n" +
                "    apply('sideways')\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
