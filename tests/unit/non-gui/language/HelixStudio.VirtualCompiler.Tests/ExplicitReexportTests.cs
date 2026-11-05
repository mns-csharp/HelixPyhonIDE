using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class ExplicitReexportTests
    {
        private static bool HasUnused(string source)
        {
            CompileResult result = VirtualCompiler.CompileSource(source, "facade.py");
            return result.Diagnostics.Any(d =>
                d.Code == VirtualCompilerDiagnosticCodes.SymUnusedImport);
        }

        [TestMethod]
        public void SameNameAliasAtModuleScopeIsAnExplicitReexport()
        {
            Assert.IsFalse(HasUnused("from provider import PublicName as PublicName\n"));
        }

        [TestMethod]
        public void DifferentAliasStillWarnsWhenUnused()
        {
            Assert.IsTrue(HasUnused("from provider import PublicName as OtherName\n"));
        }

        [TestMethod]
        public void PlainImportStillWarnsWhenUnused()
        {
            Assert.IsTrue(HasUnused("from provider import PublicName\n"));
        }

        [TestMethod]
        public void NoqaSuppressesALintDiagnosticOnItsLine()
        {
            Assert.IsFalse(HasUnused("from provider import PublicName  # noqa: F401\n"));
        }

        [TestMethod]
        public void NoqaTextInsideAStringDoesNotSuppress()
        {
            Assert.IsTrue(HasUnused(
                "from provider import PublicName\n" +
                "marker = '# noqa: F401'\n"));
        }
    }
}
