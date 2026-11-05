using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 378 (click stress-test): the biggest single click PY6005 family. Returning a dict
    /// DISPLAY (`return {"k": v}`) against a TypedDict-derived annotation is idiomatic and accepted
    /// by type checkers (click types.py: every ParamType.to_info_dict), but the checker widened the
    /// literal to `builtins.dict` and rejected it as "not assignable to ParamTypeInfoDict". A dict
    /// display now satisfies any TypedDict target; a non-display value (str, or a dict in a variable)
    /// is unaffected, so genuine mismatches are still caught.
    /// </summary>
    [TestClass]
    public class TypedDictDisplayReturnTests
    {
        private static bool HasReturnOrAnnMismatch(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch
                                          || d.Code == VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        private const string Prelude =
            "import typing as t\n" +
            "class ParamTypeInfoDict(t.TypedDict):\n" +
            "    param_type: str\n" +
            "    name: str\n" +
            "class Sub(ParamTypeInfoDict):\n" +
            "    extra: int\n";

        [TestMethod]
        public void DictDisplay_ReturnedAgainstTypedDict_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "def make() -> ParamTypeInfoDict:\n" +
                "    return {\"param_type\": \"x\", \"name\": \"y\"}\n");
            Assert.IsFalse(HasReturnOrAnnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void DictDisplay_ReturnedAgainstNestedTypedDict_DoesNotFlag()
        {
            // Base chain must be walked transitively: Sub -> ParamTypeInfoDict -> t.TypedDict.
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "def make() -> Sub:\n" +
                "    return {\"param_type\": \"x\", \"name\": \"y\", \"extra\": 1}\n");
            Assert.IsFalse(HasReturnOrAnnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void DictDisplay_AssignedToTypedDictAnnotation_DoesNotFlag()
        {
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "def f() -> None:\n" +
                "    d: ParamTypeInfoDict = {\"param_type\": \"x\", \"name\": \"y\"}\n");
            Assert.IsFalse(HasReturnOrAnnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void NonDictValue_AgainstTypedDict_StillReports()
        {
            // The leniency is scoped to dict DISPLAYS — a str return is still a genuine mismatch.
            var r = VirtualCompiler.CompileSource(
                Prelude +
                "def make() -> ParamTypeInfoDict:\n" +
                "    return \"not a dict\"\n");
            Assert.IsTrue(HasReturnOrAnnMismatch(r), Dump(r));
        }
    }
}
