using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 399 (black stress-test): a bare typing base (Iterator, Iterable, ...) in the subtype
    /// base-chain walk was forced to "builtins.Iterator" and never resolved, so Iterator[int] was
    /// wrongly rejected against an Iterable parameter. WalkBaseChain now starts from the bare name so
    /// ResolveStub tries builtins AND typing. A genuine mismatch still reports.
    /// </summary>
    [TestClass]
    public class BareTypingBaseChainTests
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
        public void Iterator_SatisfiesIterableParam()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Iterator, Iterable\n" +
                "def take(xs: Iterable) -> None: ...\n" +
                "def f(it: Iterator[int]) -> None:\n" +
                "    take(it)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void GenericIterator_SatisfiesGenericIterable()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Iterator, Iterable\n" +
                "def take(xs: Iterable[int]) -> None: ...\n" +
                "def f(it: Iterator[int]) -> None:\n" +
                "    take(it)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnrelatedType_AgainstIterable_StillFlags()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Iterable\n" +
                "class Widget: ...\n" +
                "def take(xs: Iterable) -> None: ...\n" +
                "def f(w: Widget) -> None:\n" +
                "    take(w)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
