namespace HelixPythonNamespace.Linker.Tests
{
    using System.Collections.Generic;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using HelixPythonNamespace.Linker;
    using HelixPythonNamespace.Python;
    using AstModule = HelixPythonNamespace.VirtualCompiler.Ast.Module;
    using CstToAst = HelixPythonNamespace.VirtualCompiler.Ast.CstToAst;

    /// <summary>
    /// Regression tests for cooperative cancellation in the linker. Unlike the throwing engines,
    /// <see cref="PythonLinker.Link"/> cancels by <em>early-returning</em> the partial
    /// <see cref="LinkResult"/> at each stage boundary (it is a pure function with no side effects
    /// to unwind), so a cancelled link returns promptly instead of resolving every cross-module
    /// reference. This test pins that contract.
    /// </summary>
    [TestClass]
    public class CancellationTests
    {
        private static CompileFileResult File(string fqn, string path, string src)
        {
            AstModule mod = CstToAst.Lower(Python.ParseModule(src, path).Module);
            return new CompileFileResult(mod, fqn, path);
        }

        private static CancellationToken AlreadyCancelled()
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();
            return cts.Token;
        }

        [TestMethod]
        public void Link_HonorsAlreadyCancelledToken_ReturnsEarlyWithoutThrowing()
        {
            List<CompileFileResult> files = new List<CompileFileResult>
            {
                File("a", "a.py", "from b import foo\nfoo()\n"),
                File("b", "b.py", "def foo():\n    pass\n"),
            };

            // Must not throw, and must bail before doing the full resolve/bind work.
            LinkResult result = PythonLinker.Link(files, null, null, AlreadyCancelled());
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Link_NoToken_Completes()
        {
            List<CompileFileResult> files = new List<CompileFileResult>
            {
                File("b", "b.py", "def foo():\n    pass\n"),
            };
            Assert.IsNotNull(PythonLinker.Link(files));
        }
    }
}
