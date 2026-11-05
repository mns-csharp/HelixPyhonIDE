using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 377 (black stress-test): the parameterized follow-on to iter 375. iter 375 made the RAW
    /// container satisfy its ABC (list &lt;: Sequence); this makes the PARAMETERIZED form work too.
    /// The subtype checker declined ALL cross-base generic comparisons ("without variance info,
    /// decline"), so `list[Leaf]` was rejected against `Sequence[Any]`, `Sequence[Leaf]`, and even
    /// its own exact ABC. Now, when the source erasure is in the target's base chain and the arities
    /// line up, args are compared covariantly (Any stays permissive) — so a genuine element mismatch
    /// (list[Leaf] vs Sequence[int]) is still caught. black trans.py: `list[Leaf]` vs `Sequence[Any]`.
    /// </summary>
    [TestClass]
    public class GenericContainerAbcVarianceTests
    {
        private static bool HasArgMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private const string Prelude =
            "from typing import Any, Sequence\n" +
            "class Leaf: ...\n" +
            "def seq_any(xs: Sequence[Any]) -> None: ...\n" +
            "def seq_leaf(xs: Sequence[Leaf]) -> None: ...\n" +
            "def seq_int(xs: Sequence[int]) -> None: ...\n";

        [TestMethod]
        public void ListLeaf_SatisfiesSequenceAny()
        {
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "def f() -> None:\n" +
                "    leaves: list[Leaf] = []\n" +
                "    seq_any(leaves)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ListLeaf_SatisfiesSequenceLeaf()
        {
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "def f() -> None:\n" +
                "    leaves: list[Leaf] = []\n" +
                "    seq_leaf(leaves)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ListLeaf_DoesNotSatisfySequenceInt()
        {
            // Covariance still catches a genuine element mismatch — the guard is not blanket-gradual.
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "def f() -> None:\n" +
                "    leaves: list[Leaf] = []\n" +
                "    seq_int(leaves)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void DictToCollection_ArityMismatch_IsGradual()
        {
            // dict[K, V] -> Collection[K] has mismatched arity (2 vs 1); no reliable arg mapping, so
            // it falls back to gradual erasure-only and must not false-flag.
            var r = VirtualCompiler.CompileSource(
                "from typing import Collection\n" +
                "def col(c: Collection[int]) -> None: ...\n" +
                "def f() -> None:\n" +
                "    d: dict[int, str] = {}\n" +
                "    col(d)\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }
    }
}
