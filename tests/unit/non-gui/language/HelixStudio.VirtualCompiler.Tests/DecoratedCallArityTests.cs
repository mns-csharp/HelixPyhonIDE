using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HelixPythonNamespace.VirtualCompiler;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    /// <summary>
    /// Iter 426 (black stress-test): a signature-transforming decorator (@click.command(),
    /// @app.route(), a custom wrapper) can replace a function's call signature wholesale, so a call to
    /// it must not be arity-checked. `@click.command()` turns `main(<30 options>)` into a 0-arg
    /// Command, so `main()` must not report "0 given, 30 required" (black __init__.py:1718). A call to
    /// an UNdecorated function (or one with only signature-preserving decorators) is still checked.
    /// </summary>
    [TestClass]
    public class DecoratedCallArityTests
    {
        private static bool HasArity(CompileResult r)
        {
            return r.Diagnostics.Any(d => d.Code == VirtualCompilerDiagnosticCodes.TypeArgCountMismatch);
        }

        private static string Dump(CompileResult r)
        {
            return "diagnostics: " + string.Join(", ", r.Diagnostics.Select(d => d.Code + " " + d.Message));
        }

        [TestMethod]
        public void CommandDecoratedFunction_CalledWithNoArgs_NotArityChecked()
        {
            var r = VirtualCompiler.CompileSource(
                "import click\n" +
                "@click.command()\n" +
                "@click.option('--count', default=1)\n" +
                "def main(count: int, verbose: bool, name: str) -> None:\n" +
                "    pass\n" +
                "def run() -> None:\n" +
                "    main()\n");
            Assert.IsFalse(HasArity(r), Dump(r));
        }

        [TestMethod]
        public void UndecoratedFunction_WrongArity_StillFlagged()
        {
            var r = VirtualCompiler.CompileSource(
                "def add(a: int, b: int) -> int:\n" +
                "    return a + b\n" +
                "def run() -> None:\n" +
                "    add()\n");
            Assert.IsTrue(HasArity(r), Dump(r));
        }

        [TestMethod]
        public void SignaturePreservingDecorator_StillArityChecked()
        {
            // lru_cache preserves the call signature, so the arity error is still reported.
            var r = VirtualCompiler.CompileSource(
                "from functools import lru_cache\n" +
                "@lru_cache\n" +
                "def fib(n: int) -> int:\n" +
                "    return n\n" +
                "def run() -> None:\n" +
                "    fib()\n");
            Assert.IsTrue(HasArity(r), Dump(r));
        }
    }
}
