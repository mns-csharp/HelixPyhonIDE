using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 382 (rich stress-test): an import referenced only from a STRING (forward-ref) annotation
    /// carries no Use flag (the name lives inside a string literal), so it was falsely flagged
    /// PY5001 "imported but unused". This is the dominant shape for `if TYPE_CHECKING:` imports
    /// (rich control.py: `-> "RenderResult"`, `options: "ConsoleOptions"`). The unused-import checker
    /// now parses identifiers out of string annotations; a genuinely unused import still warns.
    /// </summary>
    [TestClass]
    public class ForwardRefImportUsageTests
    {
        private static bool Unused(CompileResult r, string name)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.SymUnusedImport
                                          && d.Message.Contains("'" + name + "'"));
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void WholeStringAnnotation_CountsAsUse()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TYPE_CHECKING\n" +
                "if TYPE_CHECKING:\n" +
                "    from mod import Console\n" +
                "def render(c: \"Console\") -> None: ...\n");
            Assert.IsFalse(Unused(r, "Console"), Dump(r));
        }

        [TestMethod]
        public void StringReturnAndUnionAnnotations_CountAsUse()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TYPE_CHECKING\n" +
                "if TYPE_CHECKING:\n" +
                "    from mod import RenderResult, Options\n" +
                "def render(o: \"Options | None\") -> \"RenderResult\": ...\n");
            Assert.IsFalse(Unused(r, "RenderResult"), Dump(r));
            Assert.IsFalse(Unused(r, "Options"), Dump(r));
        }

        [TestMethod]
        public void NameInsideSubscriptedStringForwardRef_CountsAsUse()
        {
            // Both the fully-stringized `"list[Leaf]"` and the partial `list["Leaf"]` forms.
            var r = VirtualCompiler.CompileSource(
                "from typing import TYPE_CHECKING, List\n" +
                "if TYPE_CHECKING:\n" +
                "    from mod import Leaf, Node\n" +
                "def a(x: \"list[Leaf]\") -> None: ...\n" +
                "def b(y: List[\"Node\"]) -> None: ...\n");
            Assert.IsFalse(Unused(r, "Leaf"), Dump(r));
            Assert.IsFalse(Unused(r, "Node"), Dump(r));
        }

        [TestMethod]
        public void GenuinelyUnusedImport_StillWarns()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TYPE_CHECKING\n" +
                "if TYPE_CHECKING:\n" +
                "    from mod import Used, NeverUsed\n" +
                "def f(x: \"Used\") -> None: ...\n");
            Assert.IsTrue(Unused(r, "NeverUsed"), Dump(r));
            Assert.IsFalse(Unused(r, "Used"), Dump(r));
        }
    }
}
