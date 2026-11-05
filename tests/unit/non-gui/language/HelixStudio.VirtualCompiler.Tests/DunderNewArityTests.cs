using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 385 (rich stress-test): `__new__` is an implicit staticmethod, so `Cls.__new__(Cls)` /
    /// `self.__new__(Cls)` pass the class explicitly. The checker stripped the first (cls) param as
    /// if it were a bound method, leaving "0 accepted", and false-flagged PY6002 "too many positional
    /// arguments" (rich style.py x5, console.py). Its param is now preserved.
    /// </summary>
    [TestClass]
    public class DunderNewArityTests
    {
        private static bool HasArity(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgCountMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void ClassDotNew_WithClassArg_NoArityError()
        {
            var r = VirtualCompiler.CompileSource(
                "class Style:\n" +
                "    def __new__(cls) -> \"Style\":\n" +
                "        return object.__new__(cls)\n" +
                "def make() -> Style:\n" +
                "    return Style.__new__(Style)\n");
            Assert.IsFalse(HasArity(r), Dump(r));
        }

        [TestMethod]
        public void SelfDotNew_WithClassArg_NoArityError()
        {
            var r = VirtualCompiler.CompileSource(
                "class Style:\n" +
                "    def __new__(cls) -> \"Style\":\n" +
                "        return object.__new__(cls)\n" +
                "    def copy(self) -> \"Style\":\n" +
                "        s: Style = self.__new__(Style)\n" +
                "        return s\n");
            Assert.IsFalse(HasArity(r), Dump(r));
        }

        [TestMethod]
        public void OrdinaryMethod_StillStripsSelf_AndChecksArity()
        {
            // Regression guard: a normal method's self is still dropped, so calling it with an extra
            // positional arg (beyond its declared params) is still a genuine arity error.
            var r = VirtualCompiler.CompileSource(
                "class C:\n" +
                "    def m(self) -> int:\n" +
                "        return 1\n" +
                "    def use(self) -> None:\n" +
                "        self.m(123)\n");   // m() takes no args after self
            Assert.IsTrue(HasArity(r), Dump(r));
        }
    }
}
