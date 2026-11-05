using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 387 (click/black stress-test): a literal CONSTANT returned/assigned against a Literal[...]
    /// annotation is valid, but the inferencer widened the constant to bool/int/str and rejected it
    /// (click _winconsole.py: `def isatty() -> Literal[True]: return True`). A matching literal
    /// constant now satisfies the Literal target; a wrong constant, or a variable, is still checked.
    /// </summary>
    [TestClass]
    public class LiteralConstantReturnTests
    {
        private static bool HasReturnOrAnn(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch
                                          || d.Code == VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void ReturnTrue_AgainstLiteralTrue_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal\n" +
                "def isatty() -> Literal[True]:\n" +
                "    return True\n");
            Assert.IsFalse(HasReturnOrAnn(r), Dump(r));
        }

        [TestMethod]
        public void ReturnInt_AgainstLiteralUnion_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal\n" +
                "def f() -> Literal[0] | Literal[1]:\n" +
                "    return 1\n");
            Assert.IsFalse(HasReturnOrAnn(r), Dump(r));
        }

        [TestMethod]
        public void ReturnStringAndNone_AgainstLiteralStringUnionNone_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal, Optional\n" +
                "def a() -> Literal[\"return\"] | Literal[\"param\"] | None:\n" +
                "    return \"return\"\n" +
                "def b() -> Literal[\"x\"] | None:\n" +
                "    return None\n");
            Assert.IsFalse(HasReturnOrAnn(r), Dump(r));
        }

        [TestMethod]
        public void AnnAssignLiteralConstant_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal\n" +
                "def f() -> None:\n" +
                "    mode: Literal[\"rb\"] = \"rb\"\n");
            Assert.IsFalse(HasReturnOrAnn(r), Dump(r));
        }

        [TestMethod]
        public void WrongConstant_AgainstLiteral_StillFlags()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal\n" +
                "def f() -> Literal[0]:\n" +
                "    return 5\n");
            Assert.IsTrue(HasReturnOrAnn(r), Dump(r));
        }

        [TestMethod]
        public void VariableNotConstant_AgainstLiteral_StillFlags()
        {
            // A bool variable is not provably Literal[True] — mypy flags this too.
            var r = VirtualCompiler.CompileSource(
                "from typing import Literal\n" +
                "def f() -> Literal[True]:\n" +
                "    b: bool = False\n" +
                "    return b\n");
            Assert.IsTrue(HasReturnOrAnn(r), Dump(r));
        }
    }
}
