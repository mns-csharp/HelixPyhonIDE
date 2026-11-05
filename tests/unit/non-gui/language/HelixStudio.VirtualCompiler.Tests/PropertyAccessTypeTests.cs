using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 384 (rich stress-test): reading a @property yields the property VALUE type, not the
    /// getter's callable type. `self._buffer` (a `@property -> List[Segment]`) was inferred as
    /// `() -> List[Segment]` and rejected when passed where `Iterable[Segment]` was expected
    /// (rich console.py:876). A plain method is still a callable; a @name.setter re-definition is
    /// skipped so the getter's type is always used.
    /// </summary>
    [TestClass]
    public class PropertyAccessTypeTests
    {
        private static bool HasArgOrAnnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch
                                          || d.Code == VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private const string Prelude =
            "from typing import List\n" +
            "def take_list(xs: List[int]) -> None: ...\n";

        [TestMethod]
        public void PropertyRead_YieldsValueType_NotCallable()
        {
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "class C:\n" +
                "    def __init__(self) -> None:\n" +
                "        self._items: List[int] = []\n" +
                "    @property\n" +
                "    def items(self) -> List[int]:\n" +
                "        return self._items\n" +
                "    def use(self) -> None:\n" +
                "        take_list(self.items)\n" +
                "        x: List[int] = self.items\n");
            Assert.IsFalse(HasArgOrAnnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void PropertyRead_WithSetterDefined_StillYieldsGetterType()
        {
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "class C:\n" +
                "    def __init__(self) -> None:\n" +
                "        self._items: List[int] = []\n" +
                "    @property\n" +
                "    def items(self) -> List[int]:\n" +
                "        return self._items\n" +
                "    @items.setter\n" +
                "    def items(self, v: List[int]) -> None:\n" +
                "        self._items = v\n" +
                "    def use(self) -> None:\n" +
                "        take_list(self.items)\n");
            Assert.IsFalse(HasArgOrAnnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void PlainMethodReference_IsStillCallable_And_MismatchesAList()
        {
            // Regression guard: a non-property method reference is still a callable, so passing the
            // METHOD (not its result) where a List is expected is still a genuine mismatch.
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "class C:\n" +
                "    def items(self) -> List[int]:\n" +
                "        return []\n" +
                "    def use(self) -> None:\n" +
                "        take_list(self.items)\n");   // passing the bound method, not items()
            Assert.IsTrue(HasArgOrAnnMismatch(r), Dump(r));
        }
    }
}
