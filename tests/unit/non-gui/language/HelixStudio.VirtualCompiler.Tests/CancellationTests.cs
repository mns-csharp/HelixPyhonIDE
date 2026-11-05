namespace HelixPythonNamespace.VirtualCompiler.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// Regression tests for cooperative cancellation in the virtual compiler. A compile that has
    /// been cancelled must abort with <see cref="OperationCanceledException"/> at the next stage /
    /// file boundary rather than running the whole pipeline (or whole solution) to completion.
    /// </summary>
    [TestClass]
    public class CancellationTests
    {
        private static string BigSource(int lines)
        {
            StringBuilder sb = new StringBuilder(lines * 12);
            for (int i = 0; i < lines; i++) sb.Append("x").Append(i).Append(" = ").Append(i).Append("\n");
            return sb.ToString();
        }

        private static CancellationToken AlreadyCancelled()
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();
            return cts.Token;
        }

        [TestMethod]
        public void CompileSource_HonorsAlreadyCancelledToken()
        {
            Assert.ThrowsException<OperationCanceledException>(
                () => VirtualCompiler.CompileSource(BigSource(1000), "big.py", null, null, AlreadyCancelled()));
        }

        [TestMethod]
        public void CompileSolution_HonorsAlreadyCancelledToken()
        {
            // The per-file loop checks the token before it even reads a file, so a bad path is fine:
            // cancellation must fire before any I/O.
            List<WorkspaceFile> files = new List<WorkspaceFile>
            {
                new WorkspaceFile("does-not-matter.py", "m"),
            };
            Assert.ThrowsException<OperationCanceledException>(
                () => VirtualCompiler.CompileSolution(files, null, AlreadyCancelled()));
        }

        [TestMethod]
        public void CompileSource_NoToken_Completes()
        {
            CompileResult result = VirtualCompiler.CompileSource(BigSource(50), "ok.py");
            Assert.IsNotNull(result);
        }
    }
}
