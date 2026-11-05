using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 428 (requests stress-test): str.startswith / str.endswith accept a str OR a tuple of
    /// prefixes/suffixes, so `host.startswith(("*", "."))` type-checks (requests models.py:533). The
    /// stub previously declared only a str parameter. A non-str/tuple argument is still rejected.
    /// </summary>
    [TestClass]
    public class StartswithTupleTests
    {
        private static bool HasArgMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void Startswith_AcceptsTupleOfPrefixes()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(host: str) -> bool:\n" +
                "    return host.startswith(('*', '.'))\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void Endswith_AcceptsTupleOfSuffixes()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(name: str) -> bool:\n" +
                "    return name.endswith(('.py', '.pyi'))\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void Startswith_AcceptsPlainString()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(host: str) -> bool:\n" +
                "    return host.startswith('http')\n");
            Assert.IsFalse(HasArgMismatch(r), Dump(r));
        }

        [TestMethod]
        public void Startswith_RejectsInt()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(host: str) -> bool:\n" +
                "    return host.startswith(42)\n");
            Assert.IsTrue(HasArgMismatch(r), Dump(r));
        }
    }
}
