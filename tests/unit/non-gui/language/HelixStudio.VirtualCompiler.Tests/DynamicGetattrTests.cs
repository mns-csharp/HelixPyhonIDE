using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 406 (click stress-test): a class that defines <c>__getattr__</c> fabricates a value for
    /// any attribute normal lookup misses, so accessing an otherwise-absent member is not provably
    /// wrong. click's ConsoleStream (_winconsole.py) delegates every attribute to a wrapped stream via
    /// <c>__getattr__</c>, so <c>.flush</c> must not raise PY6001. A class WITHOUT <c>__getattr__</c>
    /// still reports genuinely-absent members. Also covers the <c>str.isascii</c> stub gap (black
    /// strings.py:376).
    /// </summary>
    [TestClass]
    public class DynamicGetattrTests
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
        public void GetattrClass_MissingAttribute_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Delegating:\n" +
                "    def __init__(self, inner):\n" +
                "        self._inner = inner\n" +
                "    def __getattr__(self, name):\n" +
                "        return getattr(self._inner, name)\n" +
                "def use(d: Delegating) -> None:\n" +
                "    d.flush()\n" +
                "    d.anything_at_all()\n");
            Assert.IsFalse(HasAttrNotFound(r, "flush"), Dump(r));
            Assert.IsFalse(HasAttrNotFound(r, "anything_at_all"), Dump(r));
        }

        [TestMethod]
        public void GetattrInheritedFromBase_MissingAttribute_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Proxy:\n" +
                "    def __getattr__(self, name):\n" +
                "        return None\n" +
                "class Wrapper(Proxy):\n" +
                "    pass\n" +
                "def use(w: Wrapper) -> None:\n" +
                "    w.whatever()\n");
            Assert.IsFalse(HasAttrNotFound(r, "whatever"), Dump(r));
        }

        [TestMethod]
        public void PlainClass_MissingAttribute_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Plain:\n" +
                "    def __init__(self):\n" +
                "        self.x = 1\n" +
                "def use(p: Plain) -> None:\n" +
                "    p.missing()\n");
            Assert.IsTrue(HasAttrNotFound(r, "missing"), Dump(r));
        }

        [TestMethod]
        public void StrIsascii_IsKnown()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(s: str) -> bool:\n" +
                "    return s.isascii()\n");
            Assert.IsFalse(HasAttrNotFound(r, "isascii"), Dump(r));
        }
    }
}
