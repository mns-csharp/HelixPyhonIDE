using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 360 (requests stress-test): PEP 673 `Self` was left as an opaque class named "Self", so
    /// every method annotated `-> Self` that returned self (or an instance of the class) was a PY6005
    /// "return type 'Session' is not assignable to annotated 'Self'" false positive — __enter__, copy,
    /// and every fluent/builder chain. Self means "the enclosing class"; it is resolved to that class.
    /// </summary>
    [TestClass]
    public class SelfReturnTypeTests
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
        public void ReturningSelf_SatisfiesASelfReturn()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Self\n" +
                "class C:\n" +
                "    def __enter__(self) -> Self:\n" +
                "        return self\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void ReturningAnInstanceOfTheClass_SatisfiesASelfReturn()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Self\n" +
                "class C:\n" +
                "    def copy(self) -> Self:\n" +
                "        c = C()\n" +
                "        return c\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        /// <summary>Self is resolved, not ignored: returning an unrelated type still fails, and the
        /// message names the resolved class rather than the opaque 'Self'.</summary>
        [TestMethod]
        public void ReturningTheWrongType_StillFails_WithTheResolvedClassName()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Self\n" +
                "class C:\n" +
                "    def bad(self) -> Self:\n" +
                "        return 5\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
            Assert.IsTrue(r.Diagnostics.Any(d =>
                    d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch &&
                    d.Message.Contains("annotated 'C'")),
                "the message should name the enclosing class C, not 'Self': " + Dump(r));
        }

        [TestMethod]
        public void QualifiedTypingSelf_IsAlsoResolved()
        {
            var r = VirtualCompiler.CompileSource(
                "import typing\n" +
                "class C:\n" +
                "    def chain(self) -> typing.Self:\n" +
                "        return self\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }
    }
}
