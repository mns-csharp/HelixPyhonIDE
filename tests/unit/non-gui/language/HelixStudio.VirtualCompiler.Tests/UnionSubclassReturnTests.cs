using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 419 (rich stress-test): a union whose members are all workspace subclasses of the
    /// annotated base is assignable to that base. `theme = ANSISyntaxTheme(...) /
    /// PygmentsSyntaxTheme(...)` joins to `ANSISyntaxTheme | PygmentsSyntaxTheme` and is returned as
    /// `SyntaxTheme` (rich syntax.py:277); every member subclasses SyntaxTheme, so it type-checks. A
    /// union with a non-subclass member is still a genuine mismatch.
    /// </summary>
    [TestClass]
    public class UnionSubclassReturnTests
    {
        private static bool HasReturnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void UnionOfSubclasses_SatisfiesBaseReturn()
        {
            var r = VirtualCompiler.CompileSource(
                "class SyntaxTheme: ...\n" +
                "class ANSISyntaxTheme(SyntaxTheme): ...\n" +
                "class PygmentsSyntaxTheme(SyntaxTheme): ...\n" +
                "def get(name: str) -> SyntaxTheme:\n" +
                "    theme: SyntaxTheme\n" +
                "    if name == 'ansi':\n" +
                "        theme = ANSISyntaxTheme()\n" +
                "    else:\n" +
                "        theme = PygmentsSyntaxTheme()\n" +
                "    return theme\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnionWithNonSubclassMember_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class SyntaxTheme: ...\n" +
                "class ANSISyntaxTheme(SyntaxTheme): ...\n" +
                "class Unrelated: ...\n" +
                "def get(flag: bool) -> SyntaxTheme:\n" +
                "    theme: object\n" +
                "    if flag:\n" +
                "        theme = ANSISyntaxTheme()\n" +
                "    else:\n" +
                "        theme = Unrelated()\n" +
                "    return theme\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
