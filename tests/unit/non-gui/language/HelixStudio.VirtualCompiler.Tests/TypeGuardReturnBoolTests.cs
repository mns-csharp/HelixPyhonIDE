using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 456 (fastapi stress-test): a TypeGuard[...] / TypeIs[...] value IS a bool at runtime
    /// (PEP 647 / 742), so a `-> bool` function may `return is_something(x)` where is_something is
    /// declared to return a type predicate (fastapi _compat/shared.py: _annotation_is_sequence ->
    /// bool returns lenient_issubclass(...) : TypeGuard[type[_T]]). Returning a real non-bool still
    /// flags.
    /// </summary>
    [TestClass]
    public class TypeGuardReturnBoolTests
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
        public void ReturningATypeGuardValue_SatisfiesBool()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TypeGuard, Any\n" +
                "def is_seq(x: Any) -> TypeGuard[list]:\n" +
                "    return isinstance(x, list)\n" +
                "def wrapper(x: Any) -> bool:\n" +
                "    return is_seq(x)\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ReturningATypeIsValue_SatisfiesBool()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Any\n" +
                "from typing_extensions import TypeIs\n" +
                "def is_str(x: Any) -> TypeIs[str]:\n" +
                "    return isinstance(x, str)\n" +
                "def wrapper(x: Any) -> bool:\n" +
                "    return is_str(x)\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ReturningAGenuineNonBool_StillFlagged()
        {
            // Proves we only accepted the type-predicate source, not every generic, as bool.
            var r = VirtualCompiler.CompileSource(
                "def make() -> list:\n" +
                "    return []\n" +
                "def wrapper() -> bool:\n" +
                "    return make()\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
