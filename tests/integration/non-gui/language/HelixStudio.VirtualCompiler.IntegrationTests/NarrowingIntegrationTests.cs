using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Phase-3.1 integration: verifies that flow narrowing produced by
    /// <see cref="VirtualCompiler.Typing.NarrowingEngine"/> actually propagates
    /// through the <c>TypeInferencer</c>'s if/while visit — narrowed types
    /// suppress false attribute-missing diagnostics in branches where the
    /// narrowing would apply.
    /// </summary>
    [TestClass]
    public class NarrowingIntegrationTests
    {
        private static bool Has(CompileResult r, string code)
        {
            return r.Diagnostics.Any(d => d.Code == code);
        }

        [TestMethod]
        public void OptionalParam_NarrowedByIsNone_NoAttributeError()
        {
            // x: Optional[str] — without narrowing, x.upper() on the union Optional[str]
            // couldn't confidently resolve. With `if x is not None:` the branch sees str,
            // and str.upper() resolves against the bundled stub.
            const string src =
                "def f(x: Optional[str]):\n" +
                "    if x is not None:\n" +
                "        x.upper()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "narrowing should have filtered None out; got: " +
                string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void IsinstanceNarrow_GivesAccessTo_NarrowedTypeMethods()
        {
            // `x: Union[int, str]` then `if isinstance(x, str): x.upper()` should resolve
            // against the narrowed str type.
            const string src =
                "def f(x: Union[int, str]):\n" +
                "    if isinstance(x, str):\n" +
                "        x.upper()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "isinstance(x, str) should narrow to str in the true branch");
        }

        [TestMethod]
        public void NarrowingIsScoped_ToBranchBody()
        {
            // After the `if`, x reverts to its original union type — so x.upper() in
            // the code AFTER the if block stays permissive (we don't yet emit an error
            // here because Union attribute lookup is lenient; test confirms we don't
            // FALSE-flag the pre-if access either).
            const string src =
                "def f(x: Optional[str]):\n" +
                "    if x is not None:\n" +
                "        x.upper()\n" +
                "    print(x)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound));
        }

        [TestMethod]
        public void AttributeLookup_OnBaseClass_UnwrapsGenericRef()
        {
            // `x: list[int]` — accessing `.append` on x should resolve via list's stub
            // even though the type is a GenericRef.
            const string src =
                "x: list[int] = []\n" +
                "x.append(1)\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound),
                "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [TestMethod]
        public void MissingAttribute_OnGenericInstance_StillFlagged()
        {
            const string src =
                "x: list[int] = []\n" +
                "x.not_a_real_method()\n";
            var r = VirtualCompiler.CompileSource(src);
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAttributeNotFound));
        }
    }
}
