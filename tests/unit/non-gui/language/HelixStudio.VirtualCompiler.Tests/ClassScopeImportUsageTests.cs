using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class ClassScopeImportUsageTests
    {
        [TestMethod]
        public void SameNamedClassMethodDoesNotHideModuleImportFromOtherMethods()
        {
            const string source =
                "from copy import copy\n" +
                "class Model:\n" +
                "    def copy(self):\n" +
                "        return self\n" +
                "    def clone(self):\n" +
                "        return copy(self.__dict__)\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "model.py");

            Assert.IsFalse(result.Diagnostics.Any(
                d => d.Code == "PY5001" && d.Message.Contains("'copy'")),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }

        [TestMethod]
        public void FunctionLocalBindingStillHidesOuterImport()
        {
            const string source =
                "from copy import copy\n" +
                "def outer():\n" +
                "    copy = lambda value: value\n" +
                "    def inner(value):\n" +
                "        return copy(value)\n" +
                "    return inner\n";

            CompileResult result = VirtualCompiler.CompileSource(source, "model.py");

            Assert.IsTrue(result.Diagnostics.Any(
                d => d.Code == "PY5001" && d.Message.Contains("'copy'")),
                string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        }
    }
}
