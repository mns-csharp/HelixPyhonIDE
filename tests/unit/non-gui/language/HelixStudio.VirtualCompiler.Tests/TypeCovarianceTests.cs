using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 440 (pydantic stress-test): type[] is covariant — type[Derived] / Type[Derived] is
    /// assignable to type[Base] / Type[Base] when Derived subclasses Base. Also builtins `type` and
    /// typing `Type` are the same metatype. Covers `Type[Model]` returned/passed where
    /// `Type[BaseModel]` is expected (pydantic v1). An unrelated type[] argument is still flagged.
    /// </summary>
    [TestClass]
    public class TypeCovarianceTests
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
        public void TypeOfSubclass_ReturnedAsTypeOfBase_Accepted()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Type\n" +
                "class BaseModel: ...\n" +
                "class Model(BaseModel): ...\n" +
                "def get(t: Type[Model]) -> Type[BaseModel]:\n" +
                "    return t\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch), Dump(r));
        }

        [TestMethod]
        public void TypeOfSubclass_PassedWhereTypeOfBaseExpected_Accepted()
        {
            var r = VirtualCompiler.CompileSource(
                "class BaseModel: ...\n" +
                "class Model(BaseModel): ...\n" +
                "def register(t: type[BaseModel]) -> None: ...\n" +
                "def f(m: type[Model]) -> None:\n" +
                "    register(m)\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeArgTypeMismatch), Dump(r));
        }

        [TestMethod]
        public void TypeVarArg_StaysLenient()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Type, TypeVar\n" +
                "class BaseModel: ...\n" +
                "T = TypeVar('T', bound=BaseModel)\n" +
                "def get(t: Type[T]) -> Type[BaseModel]:\n" +
                "    return t\n");
            Assert.IsFalse(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch), Dump(r));
        }

        [TestMethod]
        public void TypeOfUnrelated_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Type\n" +
                "class BaseModel: ...\n" +
                "class Unrelated: ...\n" +
                "def get(t: Type[Unrelated]) -> Type[BaseModel]:\n" +
                "    return t\n");
            Assert.IsTrue(Has(r, VirtualCompilerDiagnosticCodes.TypeReturnTypeMismatch), Dump(r));
        }
    }
}
