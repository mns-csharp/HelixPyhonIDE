using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 375 (black/click stress-test): concrete containers (list/tuple/str/bytes/set/
    /// frozenset/dict) must satisfy the collections.abc ABCs, so passing a set where a
    /// Collection[T] is expected (black: leaves.py) or a list where a cabc.Sequence is
    /// expected (click: core.py) is NOT a type error. The fix rewired the container stubs
    /// to inherit Collection/Sequence/Mapping/Set and added a `class Collection` to the
    /// bundled typing stub. A latent trap fixed along the way: `Set = set` at the bottom of
    /// typing.pyi shadowed `class Set`, which made set's ABC base unresolvable and silently
    /// turned OFF attribute checking for sets — regression-guarded below.
    /// </summary>
    [TestClass]
    public class CollectionAbcCovarianceTests
    {
        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private static bool HasMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch
                                          || d.Code == VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch);
        }

        [TestMethod]
        public void ConcreteContainers_SatisfyCollection()
        {
            // set/list/tuple/frozenset all satisfy Collection[int] — the exact shape of the
            // black (set -> Collection[LeafID]) and click (list -> cabc.Sequence) false
            // positives this iteration removed.
            var r = VirtualCompiler.CompileSource(
                "from typing import Collection\n" +
                "def take(c: Collection[int]) -> None: ...\n" +
                "def f() -> None:\n" +
                "    take([1, 2])\n" +
                "    take((1, 2))\n" +
                "    take({1, 2})\n" +
                "    take(frozenset({1, 2}))\n");
            Assert.IsFalse(HasMismatch(r), Dump(r));
        }

        [TestMethod]
        public void List_SatisfiesSequence()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Sequence\n" +
                "def seq(s: Sequence[int]) -> None: ...\n" +
                "def f() -> None:\n" +
                "    seq([1, 2])\n");
            Assert.IsFalse(HasMismatch(r), Dump(r));
        }

        /// <summary>
        /// dict satisfies Mapping/Collection at the *raw* (unparameterized) level. Propagating
        /// dict's [K, V] onto Mapping[K, V] / Collection[K] through the ABC base chain is a known
        /// gap (the bundled stubs declare unparameterized bases), so only the raw form is asserted.
        /// </summary>
        [TestMethod]
        public void Dict_SatisfiesMapping_Raw()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Mapping, Collection\n" +
                "def mp(m: Mapping) -> None: ...\n" +
                "def col(c: Collection) -> None: ...\n" +
                "def f() -> None:\n" +
                "    d: dict[int, int] = {}\n" +
                "    mp(d)\n" +
                "    col(d)\n");
            Assert.IsFalse(HasMismatch(r), Dump(r));
        }

        [TestMethod]
        public void Set_SatisfiesSet_Abc()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Set\n" +
                "def take(s: Set[int]) -> None: ...\n" +
                "def f() -> None:\n" +
                "    take({1, 2})\n" +
                "    take(frozenset({1}))\n");
            Assert.IsFalse(HasMismatch(r), Dump(r));
        }

        /// <summary>
        /// Regression guard: rewiring set's bases must NOT make set permissive. A bogus
        /// attribute on a set still has to report PY6001 — otherwise the `Set = set` alias
        /// shadow (which silently disabled set attribute checks) has crept back in.
        /// </summary>
        [TestMethod]
        public void SetStillReportsMadeUpAttribute()
        {
            var r = VirtualCompiler.CompileSource(
                "def f() -> None:\n" +
                "    s: set[int] = set()\n" +
                "    s.frobnicate()\n");
            Assert.IsTrue(
                r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound
                                       && d.Message.Contains("frobnicate")),
                Dump(r));
        }
    }
}
