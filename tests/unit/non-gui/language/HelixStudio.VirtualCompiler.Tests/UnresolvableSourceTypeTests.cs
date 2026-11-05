using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 400 (rich stress-test): a value whose type is a bare UNRESOLVABLE ClassRef — a private
    /// module type-alias like `_EscapeSubMethod = Callable[[...], str]`, or a qualified `_t.CertType`
    /// — cannot be judged against an annotation (calling such an alias yields the alias name, not its
    /// underlying return type). Returning/assigning it must not false-flag; a resolvable-source
    /// mismatch (int vs str) still reports. Symmetric to the expected-type checkability guard.
    /// </summary>
    [TestClass]
    public class UnresolvableSourceTypeTests
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
        public void ReturningAnUnresolvableAliasValue_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Callable\n" +
                "_Alias = Callable[[int], str]\n" +
                "def f(g: '_Alias') -> str:\n" +
                "    return g(3)\n");
            Assert.IsFalse(HasReturnOrAnn(r), Dump(r));
        }

        [TestMethod]
        public void AssigningAnUnresolvableAliasValue_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                "def make() -> '_CertType': ...\n" +
                "def f() -> None:\n" +
                "    x: str = make()\n");
            Assert.IsFalse(HasReturnOrAnn(r), Dump(r));
        }

        [TestMethod]
        public void ResolvableSourceMismatch_StillFlags()
        {
            var r = VirtualCompiler.CompileSource(
                "def f() -> str:\n" +
                "    return 5\n");
            Assert.IsTrue(HasReturnOrAnn(r), Dump(r));
        }
    }
}
