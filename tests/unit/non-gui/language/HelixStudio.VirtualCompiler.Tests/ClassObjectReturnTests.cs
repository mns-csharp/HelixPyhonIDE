using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 448 (pydantic stress-test): returning a bare class name is returning a class object, so it
    /// satisfies a `type[Base]` / `Type[Base]` annotation when the class is Base or a subclass —
    /// `return PydanticPlugin` under `-> type[Plugin]` (pydantic mypy.py); `return BaseConfig` under
    /// `-> Type[BaseConfig]` (config.py). Returning an unrelated class is still flagged.
    /// </summary>
    [TestClass]
    public class ClassObjectReturnTests
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
        public void SubclassObject_ReturnedAsTypeOfBase_Accepted()
        {
            var r = VirtualCompiler.CompileSource(
                "class Plugin: ...\n" +
                "class PydanticPlugin(Plugin): ...\n" +
                "def get_plugin() -> type[Plugin]:\n" +
                "    return PydanticPlugin\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void SameClassObject_ReturnedAsTypeOfItself_Accepted()
        {
            var r = VirtualCompiler.CompileSource(
                "from typing import Type\n" +
                "class BaseConfig: ...\n" +
                "def get_config() -> Type[BaseConfig]:\n" +
                "    return BaseConfig\n");
            Assert.IsFalse(HasReturnMismatch(r), Dump(r));
        }

        [TestMethod]
        public void UnrelatedClassObject_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "class Plugin: ...\n" +
                "class Unrelated: ...\n" +
                "def get_plugin() -> type[Plugin]:\n" +
                "    return Unrelated\n");
            Assert.IsTrue(HasReturnMismatch(r), Dump(r));
        }
    }
}
