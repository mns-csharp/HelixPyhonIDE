using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 474 (django stress-test): a class whose method calls `setattr(self, name, ...)` with a
    /// non-constant name populates instance attributes whose names aren't statically known — Django's
    /// Settings.__init__ does `setattr(self, setting, ...)` over the settings module. So a later
    /// self.TIME_ZONE is real, not "missing". A class that never sets attributes dynamically still flags
    /// a genuinely-absent attribute.
    /// </summary>
    [TestClass]
    public class DynamicSetattrAttrTests
    {
        private static bool HasAttrNotFound(CompileResult r, string attr)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAttributeNotFound
                                          && d.Message.Contains("'" + attr + "'"));
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void ClassThatSetattrsDynamically_DoesNotFlagUnknownSelfAttrs()
        {
            var r = VirtualCompiler.CompileSource(
                "class Settings:\n" +
                "    def __init__(self, module: object) -> None:\n" +
                "        for setting in dir(module):\n" +
                "            if setting.isupper():\n" +
                "                setattr(self, setting, getattr(module, setting))\n" +
                "    def tz(self) -> object:\n" +
                "        return self.TIME_ZONE\n");
            Assert.IsFalse(HasAttrNotFound(r, "TIME_ZONE"), Dump(r));
        }

        [TestMethod]
        public void ConstantNameSetattr_IsNotConsideredDynamic()
        {
            // setattr(self, "x", ...) sets exactly 'x' — accessing a DIFFERENT unknown attr still flags.
            var r = VirtualCompiler.CompileSource(
                "class C:\n" +
                "    def __init__(self) -> None:\n" +
                "        setattr(self, 'x', 1)\n" +
                "    def go(self) -> object:\n" +
                "        return self.not_set_anywhere\n");
            Assert.IsTrue(HasAttrNotFound(r, "not_set_anywhere"), Dump(r));
        }

        [TestMethod]
        public void PlainClass_StillFlagsAGenuinelyMissingAttr()
        {
            var r = VirtualCompiler.CompileSource(
                "class C:\n" +
                "    def go(self) -> object:\n" +
                "        return self.ghost\n");
            Assert.IsTrue(HasAttrNotFound(r, "ghost"), Dump(r));
        }
    }
}
