using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 423 (black stress-test): a function annotated `-> TypeGuard[T]` / `-> TypeIs[T]` returns a
    /// plain bool at runtime — the annotation only adds call-site narrowing — so any bool return
    /// satisfies it. `return isinstance(...) and ...` under `-> TypeGuard[ast.Attribute]` false-flagged
    /// PY6005 (black handle_ipynb_magics.py:349). A non-bool return is still a genuine mismatch.
    /// </summary>
    [TestClass]
    public class TypePredicateReturnTests
    {
        private static bool HasReturnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void BoolReturn_SatisfiesTypeGuardAnnotation()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TypeGuard\n" +
                "class Attribute: ...\n" +
                "def is_attr(node: object) -> TypeGuard[Attribute]:\n" +
                "    return isinstance(node, Attribute)\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void BoolReturn_SatisfiesTypeIsAnnotation()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing_extensions import TypeIs\n" +
                "class IO: ...\n" +
                "def is_io(v: object) -> TypeIs[IO]:\n" +
                "    return hasattr(v, 'read') and hasattr(v, 'write')\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void NonBoolReturn_UnderTypeGuard_StillFlagged()
        {
            // Returning a str (not a bool) under a TypeGuard annotation is a genuine mismatch.
            var r = VirtualCompiler.CompileSource(
                "from typing import TypeGuard\n" +
                "class Attribute: ...\n" +
                "def is_attr(node: object) -> TypeGuard[Attribute]:\n" +
                "    return 'not a bool'\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
