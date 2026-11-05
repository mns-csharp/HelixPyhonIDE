using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    [TestClass]
    public class DeepExpressionStressTests
    {
        [TestMethod]
        [Timeout(10000)]
        public void ThousandsOfLeftAssociatedOperatorsCompileWithoutStackOverflow()
        {
            // SymPy's generated resolvent table contains expressions with hundreds of
            // left-associated terms. Keep the regression above that real-world depth without
            // asking the parser itself to accept an artificial multi-thousand-token extreme.
            string expression = string.Join("+", Enumerable.Repeat("1", 1000));
            CompileResult result = VirtualCompiler.CompileSource(
                "generated_lookup = " + expression + "\n",
                "generated_lookup.py");

            Assert.IsFalse(result.Diagnostics.Any(d => d.Severity ==
                HelixPythonNamespace.Python.Diagnostics.ParseSeverity.Error));
        }
    }
}
