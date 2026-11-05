using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 358 (stress-test of the `requests` library): the type-checker treated the typing
    /// qualifiers Final / ClassVar / Annotated as opaque class names, so every constant declared
    /// `X: Final = ...` — the idiom for a module-level constant — became a PY6008 "value is not
    /// assignable to annotated 'Final'" false positive. Even `X: Final[int] = 5` was rejected
    /// (int is obviously assignable to int). requests alone had six such phantom errors.
    ///
    /// A qualifier is not a type: bare Final/ClassVar impose no constraint, and Final[T]/ClassVar[T]/
    /// Annotated[T, ...] reduce to T for assignment checking.
    /// </summary>
    [TestClass]
    public class FinalQualifierAnnotationTests
    {
        private static bool HasAnnAssignMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void BareFinal_AcceptsAnyValue()
        {
            var r = VirtualCompiler.CompileSource("NETRC: Final = (\".netrc\", \"_netrc\")\n");
            Assert.IsFalse(HasAnnAssignMismatch(r), Dump(r));
        }

        [TestMethod]
        public void BareClassVar_AcceptsAnyValue()
        {
            var r = VirtualCompiler.CompileSource("COUNT: ClassVar = 0\n");
            Assert.IsFalse(HasAnnAssignMismatch(r), Dump(r));
        }

        [TestMethod]
        public void FinalOfInt_AcceptsAnInt()
        {
            var r = VirtualCompiler.CompileSource("N: Final[int] = 5\n");
            Assert.IsFalse(HasAnnAssignMismatch(r), Dump(r));
        }

        [TestMethod]
        public void AnnotatedOfInt_AcceptsAnInt()
        {
            var r = VirtualCompiler.CompileSource("N: Annotated[int, \"meta\"] = 5\n");
            Assert.IsFalse(HasAnnAssignMismatch(r), Dump(r));
        }

        /// <summary>The qualifier is unwrapped, not ignored: a real inner-type mismatch still fires.</summary>
        [TestMethod]
        public void FinalOfInt_StillRejectsAString()
        {
            var r = VirtualCompiler.CompileSource("N: Final[int] = \"not an int\"\n");
            Assert.IsTrue(HasAnnAssignMismatch(r), Dump(r));
            // And the message names the unwrapped type, not the qualifier.
            Assert.IsTrue(r.Diagnostics.Any(d =>
                    d.Code == VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch &&
                    d.Message.Contains("annotated 'int'")),
                Dump(r));
        }

        [TestMethod]
        public void QualifiedFinal_FromTypingModule_IsAlsoUnwrapped()
        {
            var r = VirtualCompiler.CompileSource("import typing\nN: typing.Final[int] = 5\n");
            Assert.IsFalse(HasAnnAssignMismatch(r), Dump(r));
        }
    }
}
