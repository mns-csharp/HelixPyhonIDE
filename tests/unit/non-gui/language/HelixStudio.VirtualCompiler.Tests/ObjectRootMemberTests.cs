using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 430 (pydantic stress-test): every class derives from object, so its universal members
    /// (__class__, __dict__, __doc__, __module__, __hash__, ...) are always present — even on a stub
    /// class whose only declared base is object or that lists no base. `Exception.__class__`,
    /// `Iterable.__class__`, `type.__module__` false-flagged PY6001. A genuinely absent member is still
    /// reported.
    /// </summary>
    [TestClass]
    public class ObjectRootMemberTests
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
        public void UniversalDunders_OnBuiltinClasses_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(e: Exception, t: type) -> None:\n" +
                "    _ = e.__class__\n" +
                "    _ = e.__dict__\n" +
                "    _ = e.__doc__\n" +
                "    _ = t.__module__\n");
            Assert.IsFalse(HasAttrNotFound(r, "__class__"), Dump(r));
            Assert.IsFalse(HasAttrNotFound(r, "__dict__"), Dump(r));
            Assert.IsFalse(HasAttrNotFound(r, "__doc__"), Dump(r));
            Assert.IsFalse(HasAttrNotFound(r, "__module__"), Dump(r));
        }

        [TestMethod]
        public void UniversalDunders_OnWorkspaceClass_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Widget: ...\n" +
                "def f(w: Widget) -> None:\n" +
                "    _ = w.__class__\n" +
                "    _ = w.__module__\n");
            Assert.IsFalse(HasAttrNotFound(r, "__class__"), Dump(r));
            Assert.IsFalse(HasAttrNotFound(r, "__module__"), Dump(r));
        }

        [TestMethod]
        public void GenuinelyMissingAttribute_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Widget:\n" +
                "    def __init__(self) -> None:\n" +
                "        self.x = 1\n" +
                "def f(w: Widget) -> None:\n" +
                "    _ = w.no_such_attr\n");
            Assert.IsTrue(HasAttrNotFound(r, "no_such_attr"), Dump(r));
        }
    }
}
