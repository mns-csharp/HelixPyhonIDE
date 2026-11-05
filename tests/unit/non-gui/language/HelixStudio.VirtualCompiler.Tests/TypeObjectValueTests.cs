using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 408 (click stress-test): a bare class name used as a VALUE is a class object of type
    /// `type[C]`, not an instance of C. So `context_class: type[Context] = Context` is valid (was
    /// PY6008, click core.py:1010), and attribute access on a `type[X]` value resolves against X's
    /// class-level members instead of the bare `type` metaclass (was PY6001 on `cls.name` with
    /// cls: type[_ShellCompleteT], click shell_completion.py:578). Assigning an unrelated class, or
    /// an actual mismatch, is still reported.
    /// </summary>
    [TestClass]
    public class TypeObjectValueTests
    {
        private static bool Has(CompileResult r, string code)
        {
            return r.Diagnostics.Any(d => d.Code == code);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void ClassObject_AssignedToTypeAnnotation_IsAccepted()
        {
            var r = VirtualCompiler.CompileSource(
                "class Context: ...\n" +
                "class Command:\n" +
                "    context_class: type[Context] = Context\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }

        [TestMethod]
        public void SubclassObject_AssignedToTypeAnnotation_IsAccepted()
        {
            var r = VirtualCompiler.CompileSource(
                "class Context: ...\n" +
                "class RichContext(Context): ...\n" +
                "class Command:\n" +
                "    context_class: type[Context] = RichContext\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }

        [TestMethod]
        public void AttributeOnTypeParam_IsNotFlagged()
        {
            // cls: type[X] — accessing a class-level attribute of X must resolve, not hit `type`.
            var r = VirtualCompiler.CompileSource(
                "from typing import ClassVar\n" +
                "class Shell:\n" +
                "    name: ClassVar[str]\n" +
                "def register(cls: type[Shell]) -> str:\n" +
                "    return cls.name\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound), Dump(r));
        }

        [TestMethod]
        public void AttributeOnTypeParam_Unresolvable_StaysSilent()
        {
            // A TypeVar arg (`type[T]`) can't be pinned to a class, so `.anything` must stay silent
            // rather than false-flag against the bare `type` metaclass.
            var r = VirtualCompiler.CompileSource(
                "from typing import TypeVar\n" +
                "T = TypeVar('T')\n" +
                "def f(cls: type[T]) -> None:\n" +
                "    cls.whatever_attr\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound), Dump(r));
        }

        [TestMethod]
        public void UnrelatedClassObject_ToTypeAnnotation_IsStillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Context: ...\n" +
                "class Unrelated: ...\n" +
                "class Command:\n" +
                "    context_class: type[Context] = Unrelated\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }
    }
}
