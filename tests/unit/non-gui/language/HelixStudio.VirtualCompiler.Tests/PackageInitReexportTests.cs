using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 383 (click/requests stress-test): a module-level import in a package `__init__.py` is a
    /// re-export of the package public API by near-universal convention (PyCharm and Ruff both treat
    /// it as used even without `__all__`). click's entire __init__.py (64 imports) and requests'
    /// __init__.py were false-flagged PY5001. Only the MODULE scope of an __init__.py is exempt — a
    /// function-scope import there, and any import in a normal module, is still checked.
    /// </summary>
    [TestClass]
    public class PackageInitReexportTests
    {
        private static bool Unused(CompileResult r, string name)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.SymUnusedImport
                                          && d.Message.Contains("'" + name + "'"));
        }

        private static int UnusedCount(CompileResult r)
        {
            return r.Diagnostics.Count(d => d.Code == VirtualCompilerDiagnosticCodes.SymUnusedImport);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void ModuleLevelImportsInInit_AreReexports_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "from .core import Command, Group, Argument\n" +
                "from .types import ParamType\n",
                @"C:\proj\click\__init__.py");
            Assert.AreEqual(0, UnusedCount(r), Dump(r));
        }

        [TestMethod]
        public void FunctionScopeImportInInit_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "from .core import Command\n" +
                "def helper():\n" +
                "    import os\n" +
                "    return 1\n",
                @"C:\proj\click\__init__.py");
            Assert.IsTrue(Unused(r, "os"), Dump(r));
            Assert.IsFalse(Unused(r, "Command"), Dump(r));
        }

        [TestMethod]
        public void NormalModule_StillFlagsUnusedImport()
        {
            var r = VirtualCompiler.CompileSource(
                "import os\n" +
                "def f():\n" +
                "    return 1\n",
                @"C:\proj\click\core.py");
            Assert.IsTrue(Unused(r, "os"), Dump(r));
        }
    }
}
