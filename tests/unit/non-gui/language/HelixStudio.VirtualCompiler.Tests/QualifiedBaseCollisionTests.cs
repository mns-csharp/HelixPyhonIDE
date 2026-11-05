using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 363 (click stress-test): `class TextWrapper(textwrap.TextWrapper)` subclasses a stdlib
    /// class of the SAME simple name. Base-name resolution collapsed the qualified base
    /// `textwrap.TextWrapper` to just `TextWrapper`, which collided with the workspace class itself —
    /// so the external base looked like self-inheritance, the external-base suppression never fired,
    /// and every inherited attribute (self.width, self.initial_indent, ...) false-flagged PY6001.
    /// Keeping the full dotted base name breaks the collision.
    /// </summary>
    [TestClass]
    public class QualifiedBaseCollisionTests
    {
        private static bool NoAttr(CompileResult r, string member)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound
                                          && d.Message.Contains("'" + member + "'"));
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void InheritedAttrsFromASameNamedQualifiedBase_AreNotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "import textwrap\n" +
                "class TextWrapper(textwrap.TextWrapper):\n" +
                "    def go(self) -> None:\n" +
                "        x = self.width\n" +
                "        y = self.initial_indent\n" +
                "        self.drop_whitespace = True\n");
            Assert.IsFalse(NoAttr(r, "width"), Dump(r));
            Assert.IsFalse(NoAttr(r, "initial_indent"), Dump(r));
            Assert.IsFalse(NoAttr(r, "drop_whitespace"), Dump(r));
        }

        /// <summary>
        /// Constructor arity is likewise suppressed when the (qualified, external) base could alter
        /// construction — no phantom "N given, M required" against the wrong __init__.
        /// </summary>
        [TestMethod]
        public void ConstructingASubclassOfAQualifiedExternalBase_IsNotArityChecked()
        {
            var r = VirtualCompiler.CompileSource(
                "import textwrap\n" +
                "class TextWrapper(textwrap.TextWrapper):\n" +
                "    pass\n" +
                "w = TextWrapper(width=80, initial_indent='  ')\n");
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgCountMismatch),
                Dump(r));
        }

        /// <summary>A genuinely local, fully-resolved hierarchy still reports a truly absent member.</summary>
        [TestMethod]
        public void AResolvedWorkspaceHierarchy_StillReportsAbsence()
        {
            var r = VirtualCompiler.CompileSource(
                "class Base:\n" +
                "    def known(self) -> None: ...\n" +
                "class Derived(Base):\n" +
                "    def go(self) -> None:\n" +
                "        self.missing_method()\n");
            Assert.IsTrue(NoAttr(r, "missing_method"), Dump(r));
        }
    }
}
