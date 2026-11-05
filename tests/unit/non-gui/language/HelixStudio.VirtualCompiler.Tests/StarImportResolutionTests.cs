using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class StarImportResolutionTests
    {
        [TestMethod]
        public void StarImportMakesUnknownModuleNamesGradual()
        {
            CompileResult result = VirtualCompiler.CompileSource(
                "from provider import *\n" +
                "class Generated(Parser):\n" +
                "    def build(self):\n" +
                "        return TokenStream()\n",
                "generated_parser.py");

            Assert.IsFalse(result.Diagnostics.Any(d =>
                d.Code == VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }

        [TestMethod]
        public void ModuleWithoutStarImportStillReportsUndefinedName()
        {
            CompileResult result = VirtualCompiler.CompileSource(
                "value = definitely_missing\n", "ordinary.py");

            Assert.IsTrue(result.Diagnostics.Any(d =>
                d.Code == VirtualCompilerDiagnosticCodes.SymUndefinedName));
        }
    }
}
