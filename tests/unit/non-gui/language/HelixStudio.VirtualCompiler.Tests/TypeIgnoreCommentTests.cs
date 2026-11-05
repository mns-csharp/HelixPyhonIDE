using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 380 (rich stress-test): the compiler now honors `# type: ignore` comments, matching
    /// mypy/pyright/PyCharm. rich console.py silences IPython's runtime-injected `get_ipython`
    /// with `# type: ignore[name-defined]`; that line must not surface PY3006. Suppression is
    /// per-line, scoped to real comment tokens (never text inside a string), and preserves
    /// diagnostics on other lines.
    /// </summary>
    [TestClass]
    public class TypeIgnoreCommentTests
    {
        private static int Undefined(CompileResult r)
        {
            return r.Diagnostics.Count(d => d.Code == VirtualCompilerDiagnosticCodes.SymUndefinedName);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + "@" + d.Span.Start.Line + " " + d.Message));
        }

        [TestMethod]
        public void BareTypeIgnore_SuppressesTheLine()
        {
            var r = VirtualCompiler.CompileSource(
                "def f():\n" +
                "    undefined_name  # type: ignore\n");
            Assert.AreEqual(0, Undefined(r), Dump(r));
        }

        [TestMethod]
        public void TypeIgnoreWithCode_SuppressesTheLine()
        {
            // The exact form rich uses for get_ipython.
            var r = VirtualCompiler.CompileSource(
                "def f():\n" +
                "    get_ipython  # type: ignore[name-defined]\n");
            Assert.AreEqual(0, Undefined(r), Dump(r));
        }

        [TestMethod]
        public void OnlyTheIgnoredLineIsSuppressed()
        {
            var r = VirtualCompiler.CompileSource(
                "def f():\n" +
                "    silenced      # type: ignore\n" +
                "    still_flagged\n");
            Assert.AreEqual(1, Undefined(r), Dump(r));
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.SymUndefinedName
                                                 && d.Span.Start.Line == 3), Dump(r));
        }

        [TestMethod]
        public void MarkerInsideStringDoesNotSuppress()
        {
            // "# type: ignore" as a string value is not a comment — the undefined name still reports.
            var r = VirtualCompiler.CompileSource(
                "def f():\n" +
                "    s = \"# type: ignore\"\n" +
                "    undefined_name\n");
            Assert.AreEqual(1, Undefined(r), Dump(r));
        }

        [TestMethod]
        public void NearMissComment_DoesNotSuppress()
        {
            // `# type: ignoreme` is not a valid marker.
            var r = VirtualCompiler.CompileSource(
                "def f():\n" +
                "    undefined_name  # type: ignoreme\n");
            Assert.AreEqual(1, Undefined(r), Dump(r));
        }
    }
}
