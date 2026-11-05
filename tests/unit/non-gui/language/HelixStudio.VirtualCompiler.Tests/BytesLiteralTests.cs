using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 414 (click stress-test): a b"..." byte-string literal is `bytes`, not `str` — the lexer
    /// decoded the body to a C# string and dropped the bytes flag, so `data: bytes = b""` false-flagged
    /// PY6008 (click _termui_impl.py:728). CstToAst now recovers the bytes prefix and types the literal
    /// as bytes; the bytes stub carries its real methods (join, split, …) so `b"".join(...)` resolves.
    /// </summary>
    [TestClass]
    public class BytesLiteralTests
    {
        private static bool Has(CompileResult r, string code)
        {
            return r.Diagnostics.Any(d => d.Code == code);
        }

        private static bool HasAttrNotFound(CompileResult r, string member)
        {
            return r.Diagnostics.Any(d =>
                d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound &&
                d.Message.Contains("'" + member + "'"));
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void BytesLiteral_SatisfiesBytesAnnotation()
        {
            var r = VirtualCompiler.CompileSource(
                "def f() -> None:\n" +
                "    data: bytes = b\"\"\n" +
                "    other: bytes | bytearray = b\"abc\"\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }

        [TestMethod]
        public void RawBytesLiteral_IsBytes()
        {
            var r = VirtualCompiler.CompileSource(
                "def f() -> None:\n" +
                "    pattern: bytes = rb\"\\d+\"\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }

        [TestMethod]
        public void BytesJoin_Resolves()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(parts: list) -> bytes:\n" +
                "    return b\" \".join(parts)\n");
            Assert.IsFalse(HasAttrNotFound(r, "join"), Dump(r));
        }

        [TestMethod]
        public void StrLiteral_StillFlaggedAgainstBytes()
        {
            // The plain (non-bytes) literal is still `str`, so this genuine mismatch is reported.
            var r = VirtualCompiler.CompileSource(
                "def f() -> None:\n" +
                "    data: bytes = \"not bytes\"\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }

        [TestMethod]
        public void BytesLiteral_FlaggedAgainstStr()
        {
            // Reverse direction: a bytes literal is not a str.
            var r = VirtualCompiler.CompileSource(
                "def f() -> None:\n" +
                "    s: str = b\"bytes\"\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }
    }
}
