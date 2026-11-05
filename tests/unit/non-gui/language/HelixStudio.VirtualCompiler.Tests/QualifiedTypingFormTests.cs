using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 365 (click stress-test): click does `import typing as t`, so `-> t.Any` / `x: t.Any`
    /// reached the annotation converter as a dotted AttributeExpr and became ClassRef("t.Any")
    /// instead of the gradual Any. Every `return &lt;concrete&gt;` under a `-> t.Any` then false-flagged
    /// PY6005 "not assignable to annotated 't.Any'" (23 on click). The qualified spellings of the
    /// typing special-forms and generic aliases now resolve the same as the bare names.
    /// </summary>
    [TestClass]
    public class QualifiedTypingFormTests
    {
        private static bool HasTypeError(CompileResult r)
        {
            return r.Diagnostics.Any(d =>
                d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch ||
                d.Code == VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch ||
                d.Code == VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void QualifiedAny_ReturnAnnotation_AcceptsAnyValue()
        {
            var r = VirtualCompiler.CompileSource(
                "import typing as t\n" +
                "def g() -> t.Any:\n" +
                "    return 5\n");
            Assert.IsFalse(HasTypeError(r), Dump(r));
        }

        [TestMethod]
        public void QualifiedAny_ParamAnnotation_AcceptsAnything()
        {
            var r = VirtualCompiler.CompileSource(
                "import typing as t\n" +
                "def sink(x: t.Any) -> None: ...\n" +
                "def run() -> None:\n" +
                "    sink(1)\n" +
                "    sink('s')\n");
            Assert.IsFalse(HasTypeError(r), Dump(r));
        }

        [TestMethod]
        public void QualifiedOptional_IsUnionWithNone()
        {
            var r = VirtualCompiler.CompileSource(
                "import typing as t\n" +
                "def n() -> t.Optional[int]:\n" +
                "    return None\n");
            Assert.IsFalse(HasTypeError(r), Dump(r));
        }

        [TestMethod]
        public void FullyQualifiedTypingAny_AlsoResolves()
        {
            var r = VirtualCompiler.CompileSource(
                "import typing\n" +
                "def g() -> typing.Any:\n" +
                "    return object()\n");
            Assert.IsFalse(HasTypeError(r), Dump(r));
        }

        /// <summary>The resolution is real, not blanket leniency: a concrete qualified type still checks.</summary>
        [TestMethod]
        public void QualifiedConcreteType_StillChecksItsReturn()
        {
            var r = VirtualCompiler.CompileSource(
                "import builtins\n" +
                "def g() -> builtins.int:\n" +
                "    return 'not an int'\n");
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch),
                Dump(r));
        }
    }
}
