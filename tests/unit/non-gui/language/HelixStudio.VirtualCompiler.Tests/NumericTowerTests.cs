using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 396 (black/rich stress-test): PEP 484 numeric tower — for type-checking,
    /// bool &lt;: int &lt;: float &lt;: complex, even though int does not subclass float at runtime.
    /// So an int argument is accepted where a float is expected (rich spinner.py:58, progress.py).
    /// A genuine mismatch (str vs float) still reports.
    /// </summary>
    [TestClass]
    public class NumericTowerTests
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
        public void IntArgument_AgainstFloatParam_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(x: float) -> None: ...\n" +
                "def g() -> None:\n" +
                "    f(3)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void IntAndFloat_AgainstComplexParam_DoNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(x: complex) -> None: ...\n" +
                "def g() -> None:\n" +
                "    f(3)\n" +
                "    f(3.0)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void IntAgainstFloatOrNone_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Optional\n" +
                "def f(x: Optional[float]) -> None: ...\n" +
                "def g() -> None:\n" +
                "    f(7)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void StrAgainstFloat_StillFlags()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(x: float) -> None: ...\n" +
                "def g() -> None:\n" +
                "    f('no')\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void FloatAgainstInt_StillFlags_NotWideningDownward()
        {
            // Narrowing the tower is NOT allowed: a float is not an int.
            var r = VirtualCompiler.CompileSource(
                "def f(x: int) -> None: ...\n" +
                "def g() -> None:\n" +
                "    f(3.5)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
