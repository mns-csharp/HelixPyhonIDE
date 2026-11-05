using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 444 (pydantic stress-test): a value whose inferred type is `Self` — the result of a
    /// `-> Self` classmethod like `FieldInfo._construct(...)` — is gradual (the bound class is the
    /// receiver, not tracked through the call), so returning it where a concrete class is annotated
    /// type-checks (pydantic fields.py `return field_info`). A genuinely wrong return is still flagged.
    /// </summary>
    [TestClass]
    public class SelfReturnTests
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
        public void SelfTypedValue_ReturnedAsConcreteClass_Accepted()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Self\n" +
                "class FieldInfo:\n" +
                "    @classmethod\n" +
                "    def construct(cls) -> Self:\n" +
                "        return cls()\n" +
                "def build() -> FieldInfo:\n" +
                "    field_info = FieldInfo.construct()\n" +
                "    return field_info\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void GenuineMismatch_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class FieldInfo: ...\n" +
                "class Widget: ...\n" +
                "def build() -> FieldInfo:\n" +
                "    return Widget()\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
