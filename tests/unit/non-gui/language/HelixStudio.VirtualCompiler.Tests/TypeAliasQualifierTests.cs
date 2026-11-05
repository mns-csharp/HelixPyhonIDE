using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 436 (pydantic stress-test): `X: TypeAlias = ...` (PEP 613) marks the assignment as a
    /// type-alias definition — the RHS is a type expression or a forward-ref string, not a value of a
    /// particular type — so it must not be checked as e.g. str-vs-TypeAlias (pydantic, 20 sites). Real
    /// annotations still get checked.
    /// </summary>
    [TestClass]
    public class TypeAliasQualifierTests
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
        public void TypeAlias_ForwardRefString_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import TypeAlias\n" +
                "MyAlias: TypeAlias = 'SomeModel'\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }

        [TestMethod]
        public void TypeAlias_Qualified_NotFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "import typing as t\n" +
                "MyAlias: t.TypeAlias = 'SomeModel'\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }

        [TestMethod]
        public void RealAnnotation_StillChecked()
        {
            var r = VirtualCompiler.CompileSource(
                "x: int = 'not an int'\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeAnnAssignMismatch), Dump(r));
        }
    }
}
