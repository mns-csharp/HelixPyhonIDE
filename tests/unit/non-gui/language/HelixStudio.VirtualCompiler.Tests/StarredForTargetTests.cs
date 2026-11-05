using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 381 (rich stress-test): a starred name in a for / comprehension target list
    /// (`for a, *rest in xs`, `[e for a, *rest in xs]`) was mis-parsed — ParseTargetList parsed each
    /// element with ParseBitOr, which rejects a leading '*', so `rest` was never bound and its use
    /// false-flagged PY3006 (rich control.py:64 `for code, *parameters in control_codes`). Starred
    /// assignment targets (`a, *b = ...`) already worked; only the target-list path was broken.
    /// </summary>
    [TestClass]
    public class StarredForTargetTests
    {
        private static int Undefined(CompileResult r, string name)
        {
            return r.Diagnostics.Count(d => d.Code == VirtualCompilerDiagnosticCodes.SymUndefinedName
                                            && d.Message.Contains("'" + name + "'"));
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void StarredForLoopTarget_BindsTheName()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(rows):\n" +
                "    for k, *vals in rows:\n" +
                "        print(k, vals)\n");
            Assert.AreEqual(0, Undefined(r, "vals"), Dump(r));
        }

        [TestMethod]
        public void StarredComprehensionTarget_BindsTheName()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(rows):\n" +
                "    return [rest for x, *rest in rows]\n");
            Assert.AreEqual(0, Undefined(r, "rest"), Dump(r));
        }

        [TestMethod]
        public void StarredGenExprTarget_UsedInElement_Binds()
        {
            // The exact rich control.py shape: `_format_map[code](*parameters) for code, *parameters in ...`
            var r = VirtualCompiler.CompileSource(
                "def f(rows, fmt):\n" +
                "    return \"\".join(fmt[code](*parameters) for code, *parameters in rows)\n");
            Assert.AreEqual(0, Undefined(r, "parameters"), Dump(r));
        }

        [TestMethod]
        public void LeadingStarredTarget_Binds()
        {
            var r = VirtualCompiler.CompileSource(
                "def f(rows):\n" +
                "    for *init, last in rows:\n" +
                "        print(init, last)\n");
            Assert.AreEqual(0, Undefined(r, "init"), Dump(r));
        }

        [TestMethod]
        public void GenuinelyUndefinedName_StillReports()
        {
            // Sanity: the fix binds the starred target, it does not blanket-suppress the scope.
            var r = VirtualCompiler.CompileSource(
                "def f(rows):\n" +
                "    for k, *vals in rows:\n" +
                "        print(k, vals, missing_helper)\n");
            Assert.AreEqual(1, Undefined(r, "missing_helper"), Dump(r));
        }
    }
}
