using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 432 (pydantic stress-test): a nested class is a class attribute of its enclosing class,
    /// reachable as `self.Nested` / `Outer.Nested` (Django Meta, pydantic Config,
    /// GenerateJsonSchema.ValidationsMapping). Accessing it must not raise PY6001. A genuinely absent
    /// attribute on the enclosing class is still reported.
    /// </summary>
    [TestClass]
    public class NestedClassMemberTests
    {
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
        public void NestedClass_AccessedViaSelf_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Generator:\n" +
                "    class ValidationsMapping:\n" +
                "        numeric = {}\n" +
                "    def run(self) -> None:\n" +
                "        _ = self.ValidationsMapping.numeric\n");
            Assert.IsFalse(HasAttrNotFound(r, "ValidationsMapping"), Dump(r));
        }

        [TestMethod]
        public void NestedClass_AccessedViaInstance_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Generator:\n" +
                "    class Config:\n" +
                "        strict = True\n" +
                "def use(g: Generator) -> None:\n" +
                "    _ = g.Config\n");
            Assert.IsFalse(HasAttrNotFound(r, "Config"), Dump(r));
        }

        [TestMethod]
        public void GenuinelyMissing_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Generator:\n" +
                "    class Config:\n" +
                "        strict = True\n" +
                "def use(g: Generator) -> None:\n" +
                "    _ = g.NoSuchNested\n");
            Assert.IsTrue(HasAttrNotFound(r, "NoSuchNested"), Dump(r));
        }
    }
}
