using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 379 (click/rich stress-test): PY3007 "redefinition shadows the previous definition" was
    /// the biggest single warning family (18 across click+rich), almost all idiomatic FPs — property
    /// accessor pairs (@property + @name.setter, rich Console.file) and conditional redefinitions in
    /// if/elif/else or try/except branches (click's nargs `convert`, platform/TYPE_CHECKING import
    /// fallbacks). The lint now suppresses those while still catching a genuine unconditional shadow.
    /// </summary>
    [TestClass]
    public class DuplicateDefinitionLintTests
    {
        private static int Py3007Count(CompileResult r)
        {
            return r.Diagnostics.Count(d => d.Code == VirtualCompilerDiagnosticCodes.SymDuplicateDefinition);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void PropertyGetterSetter_DoesNotWarn()
        {
            var r = VirtualCompiler.CompileSource(
                "class C:\n" +
                "    @property\n" +
                "    def file(self) -> int:\n" +
                "        return self._f\n" +
                "    @file.setter\n" +
                "    def file(self, v: int) -> None:\n" +
                "        self._f = v\n" +
                "    @file.deleter\n" +
                "    def file(self) -> None:\n" +
                "        del self._f\n");
            Assert.AreEqual(0, Py3007Count(r), Dump(r));
        }

        [TestMethod]
        public void IfElifElseRedefinition_DoesNotWarn()
        {
            // click core.py: `convert` defined once per nargs branch.
            var r = VirtualCompiler.CompileSource(
                "def outer(n: int):\n" +
                "    if n == 1:\n" +
                "        def convert(v): return v\n" +
                "    elif n == -1:\n" +
                "        def convert(v): return tuple(v)\n" +
                "    else:\n" +
                "        def convert(v): return list(v)\n" +
                "    return convert\n");
            Assert.AreEqual(0, Py3007Count(r), Dump(r));
        }

        [TestMethod]
        public void TryExceptRedefinition_DoesNotWarn()
        {
            // Platform / import fallback pattern.
            var r = VirtualCompiler.CompileSource(
                "try:\n" +
                "    def _get_argv_encoding() -> str: return 'utf-8'\n" +
                "except Exception:\n" +
                "    def _get_argv_encoding() -> str: return 'ascii'\n");
            Assert.AreEqual(0, Py3007Count(r), Dump(r));
        }

        [TestMethod]
        public void UnconditionalRedefinition_StillWarns()
        {
            // A real copy-paste shadow at the same unconditional level must still be flagged.
            var r = VirtualCompiler.CompileSource(
                "def dup() -> int:\n" +
                "    return 1\n" +
                "def dup() -> int:\n" +
                "    return 2\n");
            Assert.AreEqual(1, Py3007Count(r), Dump(r));
        }

        [TestMethod]
        public void ThrowawayRegistrationFunctionsDoNotWarn()
        {
            var r = VirtualCompiler.CompileSource(
                "@registry.register(int)\n" +
                "def _(value): return 1\n" +
                "@registry.register(str)\n" +
                "def _(value): return 2\n");
            Assert.AreEqual(0, Py3007Count(r), Dump(r));
        }

        [TestMethod]
        public void GenuineShadowInsideConditionalFunction_StillWarns()
        {
            // The conditional-depth reset per scope: a real shadow INSIDE a function that itself
            // sits in an if-branch must still be caught (the outer if must not mask it).
            var r = VirtualCompiler.CompileSource(
                "if True:\n" +
                "    def outer():\n" +
                "        def inner(): return 1\n" +
                "        def inner(): return 2\n");
            Assert.AreEqual(1, Py3007Count(r), Dump(r));
        }
    }
}
